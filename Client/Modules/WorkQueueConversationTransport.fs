module Client.WorkQueueConversationTransport

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger

// WQ-06 incremental bridge: the WQ-02 Project is the *only* handler/queue authority.
// Replies are immutable, precommitted Git objects. The thread exposes a reply
// only after the canonical project CAS references its id as Handled.
// No production endpoints or Scheduler are registered by this module.

[<CLIMutable>]
type PreparedReply =
    { SchemaVersion: int
      ProjectId: string
      SubjectKey: string
      MessageId: string
      ReplyId: string
      LaunchId: string
      HandlerGeneration: int
      HandlerKind: string
      Body: string }

type ConversationEntry =
    { Message: SubjectMessage
      Reply: PreparedReply option }

type ConversationThread =
    { Subject: Subject
      ProjectVersion: int
      SnapshotCommit: string
      Entries: ConversationEntry list
      PendingCount: int }

// The caller must obtain the WorkStage for Role messages from WQ-04's
// authoritative producer. WQ-06 deliberately does not create role demand.
type MessagePublication =
    { Message: SubjectMessage
      RoleWorkFromProducer: WorkStage option }

type HandlerClaim =
    { MessageId: string
      LaunchId: string
      HandlerGeneration: int
      HandlerKind: string
      WorkId: string option }

module WorkQueueConversationTransport =
    let private invalid reason = InvalidBinding ("WQ-06 conversation: " + reason)
    let private integrity reason = LedgerIntegrityError ("WQ-06 conversation: " + reason)

    let private nonBlank (value: string) =
        not (String.IsNullOrWhiteSpace value)

    let private subjectKey = function
        | Subject.Task task -> "task:" + task
        | Subject.Role role -> "role:" + role

    let private digest (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(value))
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

    let replyPath messageId replyId =
        "records/work-queue/conversations/replies/" +
        digest messageId + "/" + digest replyId + ".json"

    let private json (record: PreparedReply) = JsonSerializer.Serialize(record)

    let private completeCoverage (project: Project) =
        if not project.QueueCovered then Error(invalid "queue high-water not covered")
        elif not project.GraphCovered then Error(invalid "hard graph high-water not covered")
        elif not project.MessageCovered then Error(invalid "message/handler high-water not covered")
        elif not project.SourceCovered then Error(invalid "source/lineage high-water not covered")
        elif project.Works |> Map.exists (fun _ w ->
            w.Dependencies |> Set.exists (fun id -> not (project.Works.ContainsKey id))) then
            Error(invalid "hard dependency generation missing")
        else Ok ()

    let private validReply (binding: LedgerBinding) (message: SubjectMessage)
                           (reply: PreparedReply) =
        reply.SchemaVersion = 1
        && reply.ProjectId = binding.ProjectId.ToString("D")
        && reply.SubjectKey = subjectKey message.Subject
        && reply.MessageId = message.Id
        && reply.HandlerGeneration > 0
        && nonBlank reply.LaunchId
        && nonBlank reply.ReplyId
        && (reply.HandlerKind = "Primary" || reply.HandlerKind = "Ancillary")
        && not (isNull reply.Body)
        && Encoding.UTF8.GetByteCount(reply.Body) <= 32768

    let private readReplyAt binding snapshot (message: SubjectMessage) replyId =
        LedgerStore.readAt binding snapshot (replyPath message.Id replyId)
        |> Result.bind (function
            | Some raw, Some _ ->
                try
                    let record = JsonSerializer.Deserialize<PreparedReply>(raw)
                    if isNull (box record) || not (validReply binding message record)
                       || record.ReplyId <> replyId || json record <> raw then
                        Error(integrity "prepared reply identity, schema or canonical content mismatch")
                    else Ok record
                with ex ->
                    Error(integrity ("unreadable reply receipt: " + ex.Message))
            | None, None ->
                Error(integrity "handled message has no durable reply content")
            | _ -> Error(integrity "incomplete immutable reply object"))

    // A pinned read never consumes a message or issues a handler credit.
    // Deterministic message-ID order is used until WQ-04 supplies its
    // authoritative per-subject event/FIFO cursor; no timestamp is invented.
    let readThreadAt binding snapshot subject =
        WorkQueueGitLedger.loadAt binding snapshot
        |> Result.bind (fun project ->
            completeCoverage project
            |> Result.bind (fun () ->
                let messages : SubjectMessage list =
                    project.Messages
                    |> Map.toList
                    |> List.map snd
                    |> List.filter (fun m -> m.Subject = subject)
                let rec collect (entries: ConversationEntry list) (remaining: SubjectMessage list) =
                    match remaining with
                    | [] -> Ok(List.rev entries)
                    | message :: rest ->
                        match message.State with
                        | MessageState.Handled replyId ->
                            readReplyAt binding snapshot message replyId
                            |> Result.bind (fun receipt ->
                                if receipt.HandlerGeneration <> message.HandlerGeneration then
                                    Error(integrity "handled reply does not match canonical handler generation")
                                else
                                    collect ({ Message = message; Reply = Some receipt } :: entries) rest)
                        | _ -> collect ({ Message = message; Reply = None } :: entries) rest
                collect [] messages
                |> Result.map (fun entries ->
                    { Subject = subject
                      ProjectVersion = project.Version
                      SnapshotCommit = snapshot.Commit
                      Entries = entries
                      PendingCount =
                        entries |> List.filter (fun item ->
                            item.Message.State = MessageState.Pending) |> List.length })))

    let readThread binding subject =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot -> readThreadAt binding snapshot subject)

    let post binding actor operationId expectedVersion publication =
        let message = publication.Message
        if operationId = Guid.Empty || not (nonBlank message.Id)
           || not (nonBlank message.Body)
           || Encoding.UTF8.GetByteCount(message.Body) > 32768
           || (match message.Subject with
               | Subject.Task id | Subject.Role id -> not (nonBlank id)) then
            Error(invalid "invalid immutable message identity, subject or body")
        else
            // Project.PostMessage atomically creates passive Task attention
            // or its exactly-one Role work; it owns uniqueness and receipt replay.
            WorkQueueGitLedger.execute binding actor operationId expectedVersion
                (PostMessage(message, publication.RoleWorkFromProducer))

    let claimAncillary binding actor operationId expectedVersion launchId messageId =
        WorkQueueGitLedger.execute binding actor operationId expectedVersion
            (ClaimAncillary(launchId, messageId))
        |> Result.bind (fun committed ->
            // Never derive a handler generation from a later moving HEAD:
            // the *same launch* could have released and reclaimed the same
            // message while the original acknowledgement was lost. Recover
            // the original operation's verified immutable Git ancestry.
            LedgerStore.fetch binding
            |> Result.bind (fun latest ->
                LedgerStore.getOperationAt binding latest operationId
                |> Result.bind (function
                    | Some receipt when receipt.OperationId = operationId.ToString("D")
                                        && receipt.RequestHash = committed.Operation.RequestHash
                                        && receipt.Type = "WQ02.Mutate" ->
                        LedgerStore.getOperationOriginAt binding latest receipt
                        |> Result.bind (fun origin ->
                            LedgerStore.verifiedHistoricalSnapshot binding origin
                            |> Result.bind (fun pinned ->
                                WorkQueueGitLedger.loadAt binding pinned
                                |> Result.bind (fun project ->
                                    let originalPayload =
                                        ProjectOperationPayload.OtherMutation(
                                            "ancillary-claim|" + launchId + "|" + messageId)
                                    if project.Version <> expectedVersion + 1
                                       || project.Receipts.TryFind(operationId.ToString("D"))
                                          <> Some originalPayload then
                                        Error(integrity "claim origin lacks exact CAS receipt/version")
                                    else
                                        match project.Messages.TryFind messageId with
                                        | Some m when m.State = MessageState.ClaimedAncillary launchId ->
                                            Ok(committed,
                                               { MessageId = m.Id
                                                 LaunchId = launchId
                                                 HandlerGeneration = m.HandlerGeneration
                                                 HandlerKind = "Ancillary"
                                                 WorkId = m.WorkId })
                                        | _ -> Error(integrity "original claim did not own handler lease"))))
                    | _ -> Error(integrity "original ancillary claim operation is missing or mismatched"))))

    // No new primary claim is created here. A dedicated Role-message launch
    // must use the existing ClaimNext work/credit CAS and its generation.
    let primaryMessageClaim (project: Project) launchId =
        match project.Launches.TryFind launchId with
        | Some launch ->
            match launch.PrimaryWorkId with
            | Some workId ->
                match project.Works.TryFind workId with
                | Some work when work.State = WorkState.Claimed launchId ->
                    match work.Kind with
                    | WorkKind.RoleMessage messageId ->
                        match project.Messages.TryFind messageId with
                        | Some message
                            when message.State = MessageState.ClaimedPrimary launchId
                                 && message.WorkId = Some workId
                                 && message.HandlerGeneration = work.ClaimGeneration ->
                            Some { MessageId = messageId
                                   LaunchId = launchId
                                   HandlerGeneration = message.HandlerGeneration
                                   HandlerKind = "Primary"
                                   WorkId = Some workId }
                        | _ -> None
                    | _ -> None
                | _ -> None
            | None -> None
        | None -> None

    let private activeHandler (project: Project) (message: SubjectMessage)
                              (lease: HandlerClaim) =
        let stageDependenciesSatisfied (stage: WorkStage) =
            stage.Dependencies
            |> Set.forall (fun id ->
                stage.WaivedDependencies.ContainsKey id
                || Contracts.isSatisfied project id)
        let claimIsSafe =
            match project.Launches.TryFind lease.LaunchId with
            | Some launch ->
                match launch.PrimaryWorkId with
                | Some primaryId ->
                    match project.Works.TryFind primaryId with
                    | Some primary when primary.State = WorkState.Claimed lease.LaunchId
                                        && primary.Gate = GateState.Clear
                                        && stageDependenciesSatisfied primary ->
                        if lease.HandlerKind = "Ancillary" then
                            primary.Kind <> WorkKind.RoleMessage lease.MessageId
                            && primary.Domain = DomainState.Undecided
                            && (match primary.Kind with WorkKind.RoleMessage _ -> false | _ -> true)
                            && (match message.WorkId with
                                | None -> true
                                | Some workId ->
                                    project.Works.TryFind workId
                                    |> Option.exists (fun work ->
                                        work.State = WorkState.Claimed lease.LaunchId
                                        && work.ClaimGeneration = lease.HandlerGeneration
                                        && work.Kind = WorkKind.RoleMessage message.Id
                                        && work.Gate = GateState.Clear
                                        && stageDependenciesSatisfied work))
                        else true
                    | _ -> false
                | _ -> false
            | _ -> false
        let registered =
            project.Launches.TryFind lease.LaunchId
            |> Option.exists (fun launch ->
                match launch.State, launch.Lease with
                | LaunchState.Registered _, Some live ->
                    not live.StopRequested && launch.EverClaimed
                | _ -> false)
        let claimMatches =
            message.Id = lease.MessageId
            && message.HandlerGeneration = lease.HandlerGeneration
            && message.WorkId = lease.WorkId
            && match lease.HandlerKind, message.State with
               | "Primary", MessageState.ClaimedPrimary launch ->
                   launch = lease.LaunchId
                   && primaryMessageClaim project launch = Some lease
               | "Ancillary", MessageState.ClaimedAncillary launch ->
                   launch = lease.LaunchId
                   && (project.Launches.TryFind launch
                       |> Option.exists (fun l -> l.AncillaryMessages.Contains message.Id))
               | _ -> false
        registered && claimMatches && claimIsSafe

    let private fromClaim (binding: LedgerBinding) (message: SubjectMessage) (lease: HandlerClaim) (replyId: string) (body: string) =
        { SchemaVersion = 1
          ProjectId = binding.ProjectId.ToString("D")
          SubjectKey = subjectKey message.Subject
          MessageId = message.Id
          ReplyId = replyId
          LaunchId = lease.LaunchId
          HandlerGeneration = lease.HandlerGeneration
          HandlerKind = lease.HandlerKind
          Body = body }

    // Prepare is an append-only immutable Git object. It does NOT handle the
    // message, release a launch, or publish an answer to the visible transcript.
    // Unknown outcomes must recover the *original* prepare operation id.
    let prepareReply binding actor operationId (lease: HandlerClaim) (replyId: string) (body: string) =
        if operationId = Guid.Empty || not (nonBlank replyId)
           || isNull body || Encoding.UTF8.GetByteCount(body) > 32768 then
            Error(invalid "invalid immutable reply operation, id or content")
        else
            let intentBody =
                JsonSerializer.Serialize(
                    {| schemaVersion = 1
                       messageId = lease.MessageId
                       replyId = replyId
                       launchId = lease.LaunchId
                       handlerGeneration = lease.HandlerGeneration
                       handlerKind = lease.HandlerKind
                       body = body |})
            let intent =
                { OperationId = operationId; Actor = actor
                  Kind = "WQ06.PrepareConversationReply"
                  RequestJson = intentBody }
            LedgerStore.transact binding intent (fun snapshot ->
                WorkQueueGitLedger.loadAt binding snapshot
                |> Result.bind (fun project ->
                    completeCoverage project
                    |> Result.bind (fun () ->
                        match project.Messages.TryFind lease.MessageId with
                        | Some message when activeHandler project message lease ->
                            let reply = fromClaim binding message lease replyId body
                            LedgerStore.readAt binding snapshot (replyPath message.Id replyId)
                            |> Result.bind (function
                                | None, None ->
                                    Ok [ { Path = replyPath message.Id replyId
                                           ExpectedBlobOid = None
                                           Content = json reply } ]
                                | _ -> Error(invalid "reply id already prepared by another operation"))
                        | _ -> Error(invalid "stale, STOP-requested or ineligible handler lease"))))

    // The completion's *one* project CAS is authoritative for reply visibility,
    // W completion, terminal primary launch, ancillary release and downstream wake.
    // This wrapper checks content availability but never fabricates those effects.
    let completePrepared binding actor operationId expectedVersion
                         (lease: HandlerClaim) replyId =
        if operationId = Guid.Empty || not (nonBlank replyId) then
            Error(invalid "invalid completion identity")
        else
            WorkQueueGitLedger.load binding
            |> Result.bind (fun (snapshot, project) ->
                completeCoverage project
                |> Result.bind (fun () ->
                    match project.Messages.TryFind lease.MessageId with
                    | Some message when
                        activeHandler project message lease
                        || (message.HandlerGeneration = lease.HandlerGeneration
                            && match message.State with
                               | MessageState.Handled handled -> handled = replyId
                               | _ -> false) ->
                        readReplyAt binding snapshot message replyId
                        |> Result.bind (fun receipt ->
                            if receipt.LaunchId <> lease.LaunchId
                               || receipt.HandlerGeneration <> lease.HandlerGeneration
                               || receipt.HandlerKind <> lease.HandlerKind then
                                Error(invalid "reply prepared for a different handler generation")
                            else
                                let mutation =
                                    match lease.HandlerKind, lease.WorkId with
                                    | "Primary", Some workId ->
                                        Some(CompleteMessage(lease.LaunchId, workId,
                                                             lease.MessageId, lease.HandlerGeneration, replyId))
                                    | "Ancillary", _ ->
                                        Some(CompleteAncillary(lease.LaunchId, lease.MessageId,
                                                               lease.HandlerGeneration, replyId))
                                    | _ -> None
                                match mutation with
                                | None -> Error(invalid "invalid handler kind/work identity")
                                | Some mutation ->
                                    // Handled is admitted only for an exact original receipt
                                    // replay. A distinct operation must fail in WQ-02's CAS.
                                    WorkQueueGitLedger.execute binding actor operationId
                                        expectedVersion mutation)
                    | _ -> Error(invalid "handler lease not live at completion")))

    // Independent gated ancillary requeue is delegated to WQ-01's graph CAS;
    // the conversation and prepared-but-unhandled reply remain durable.
    let resolveGatedAncillary binding actor operationId expectedVersion
                              launchId messageId workId launchVersion
                              claimGeneration graphVersion =
        WorkQueueGitLedger.execute binding actor operationId expectedVersion
            (ResolveAncillaryGate(launchId, messageId, workId, launchVersion,
                                  claimGeneration, graphVersion))
