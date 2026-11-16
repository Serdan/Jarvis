module Client.WorkQueueOperatorConversations

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Client.LedgerStore

// Explicit, manually driven Task/Role conversations on the approved Git ledger.
// This is NOT WQ-04 Scheduler demand, a work claim, or source-effect authority.
// Operations need a stable UUID and expected per-thread version. Lost acks can
// be resolved from immutable LedgerStore receipts without trusting local state.

[<CLIMutable>]
type OperatorMessage =
    { Id: string
      Author: string
      Body: string
      Status: string
      Handler: string
      HandlerGeneration: int
      ReplyId: string
      ReplyBody: string
      LastOperationId: string }

[<CLIMutable>]
type OperatorMessageAudit =
    { OperationId: string
      MessageId: string
      Action: string
      Actor: string
      OldStatus: string
      NewStatus: string
      Handler: string
      HandlerGeneration: int
      PriorGitHead: string
      Reason: string }

[<CLIMutable>]
type OperatorThread =
    { SchemaVersion: int
      ProjectId: string
      SubjectKind: string
      SubjectId: string
      Version: int
      Messages: OperatorMessage array
      Audit: OperatorMessageAudit array }

type OperatorThreadSnapshot =
    { Thread: OperatorThread
      GitHead: string
      BlobOid: string option }

type OperatorReceipt =
    { OperationId: string
      ObservedGitHead: string
      AlreadyApplied: bool
      LedgerSequence: int64 }

type OperatorOperationObservation =
    { VerifiedGitHead: string
      Operation: LedgerOperation option }

module WorkQueueOperatorConversations =
    let private invalid reason = InvalidBinding ("WQ-06 operator conversations: " + reason)
    let private integrity reason = LedgerIntegrityError ("WQ-06 operator conversations: " + reason)
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)
    let private digest (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

    let validSubject kind id =
        (kind = "Task" || kind = "Role")
        && nonBlank id && Encoding.UTF8.GetByteCount(id) <= 256

    let threadPath kind id =
        "records/work-queue/operator-conversations/threads/" +
        digest (kind + ":" + id) + ".json"

    let private fresh (binding: LedgerBinding) kind id =
        { SchemaVersion = 1
          ProjectId = binding.ProjectId.ToString("D")
          SubjectKind = kind
          SubjectId = id
          Version = 0
          Messages = [||]
          Audit = [||] }

    let private validMessage (message: OperatorMessage) =
        nonBlank message.Id && nonBlank message.Author && nonBlank message.Body
        && Encoding.UTF8.GetByteCount(message.Body) <= 32768
        && message.HandlerGeneration >= 0
        && nonBlank message.LastOperationId
        && (match message.Status with
            | "Pending" -> message.Handler = "" && message.ReplyId = "" && message.ReplyBody = ""
            | "Claimed" ->
                nonBlank message.Handler && message.HandlerGeneration > 0
                && message.ReplyId = "" && message.ReplyBody = ""
            | "Handled" ->
                nonBlank message.Handler && message.HandlerGeneration > 0
                && nonBlank message.ReplyId && not (isNull message.ReplyBody)
            | _ -> false)

    let private validate (binding: LedgerBinding) kind id (thread: OperatorThread) =
        if isNull (box thread) || thread.SchemaVersion <> 1
           || thread.ProjectId <> binding.ProjectId.ToString("D")
           || thread.SubjectKind <> kind || thread.SubjectId <> id
           || thread.Version < 0 || isNull thread.Messages || isNull thread.Audit
           || thread.Audit.Length <> thread.Version
           || (thread.Messages |> Array.exists (validMessage >> not))
           || (thread.Messages |> Array.map (fun m -> m.Id) |> Array.distinct |> Array.length)
              <> thread.Messages.Length then
            Error(integrity "thread identity, version or message state is inconsistent")
        else Ok thread

    let readAt (binding: LedgerBinding) snapshot kind id =
        if not (validSubject kind id) then Error(invalid "expected Task or Role and a subject ID")
        else
            LedgerStore.readAt binding snapshot (threadPath kind id)
            |> Result.bind (function
                | None, None -> Ok { Thread = fresh binding kind id; GitHead = snapshot.Commit; BlobOid = None }
                | Some raw, Some oid ->
                    try
                        let thread = JsonSerializer.Deserialize<OperatorThread>(raw)
                        validate binding kind id thread
                        |> Result.map (fun value ->
                            { Thread = value; GitHead = snapshot.Commit; BlobOid = Some oid })
                    with ex ->
                        Error(integrity ("malformed thread: " + ex.Message))
                | _ -> Error(integrity "thread Git object missing its matching blob identity"))

    let read binding kind id =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot -> readAt binding snapshot kind id)

    let inspectOperation binding operationId =
        if operationId = Guid.Empty then Error(invalid "operation UUID is required")
        else
            LedgerStore.fetch binding
            |> Result.bind (fun snapshot ->
                LedgerStore.getOperationAt binding snapshot operationId
                |> Result.map (fun operation ->
                    { VerifiedGitHead = snapshot.Commit
                      Operation = operation }))

    type private Change =
        { Messages: OperatorMessage array
          MessageId: string
          Previous: string
          Next: string
          Handler: string
          Generation: int
          Reason: string }

    let private apply binding actor operationId expectedVersion kind id action requestJson transform =
        if operationId = Guid.Empty || not (nonBlank actor)
           || not (validSubject kind id) || expectedVersion < 0 then
            Error(invalid "operation UUID, actor, subject or version invalid")
        else
            let intent =
                { OperationId = operationId
                  Actor = actor
                  Kind = "WQ06.Operator." + action
                  RequestJson = requestJson }
            LedgerStore.transact binding intent (fun snapshot ->
                readAt binding snapshot kind id
                |> Result.bind (fun current ->
                    let thread = current.Thread
                    if thread.Version <> expectedVersion then
                        Error(StaleEntityVersion
                            (sprintf "WQ-06 thread changed: expected %d, observed %d at %s"
                                expectedVersion thread.Version snapshot.Commit))
                    else
                        transform thread
                        |> Result.map (fun changed ->
                            let audit =
                                { OperationId = operationId.ToString("D")
                                  MessageId = changed.MessageId
                                  Action = action
                                  Actor = actor
                                  OldStatus = changed.Previous
                                  NewStatus = changed.Next
                                  Handler = changed.Handler
                                  HandlerGeneration = changed.Generation
                                  PriorGitHead = snapshot.Commit
                                  Reason = changed.Reason }
                            let next =
                                { thread with
                                    Version = thread.Version + 1
                                    Messages = changed.Messages
                                    Audit = Array.append thread.Audit [| audit |] }
                            [ { Path = threadPath kind id
                                ExpectedBlobOid = current.BlobOid
                                Content = JsonSerializer.Serialize(next) } ])))
            |> Result.map (fun written ->
                { OperationId = operationId.ToString("D")
                  ObservedGitHead = written.CurrentHead
                  AlreadyApplied = written.AlreadyApplied
                  LedgerSequence = written.Operation.Sequence })

    let post binding actor operationId expectedVersion kind id messageId body =
        if not (nonBlank messageId) || not (nonBlank body)
           || Encoding.UTF8.GetByteCount(body) > 32768 then
            Error(invalid "stable message ID and a body of at most 32 KiB required")
        else
            let request = JsonSerializer.Serialize(
                {| subjectKind = kind; subjectId = id; expectedVersion = expectedVersion
                   messageId = messageId; body = body |})
            apply binding actor operationId expectedVersion kind id "Post" request (fun thread ->
                if thread.Messages |> Array.exists (fun m -> m.Id = messageId) then
                    Error(invalid "message ID already exists in this thread")
                else
                    let message =
                        { Id = messageId; Author = actor; Body = body
                          Status = "Pending"; Handler = ""; HandlerGeneration = 0
                          ReplyId = ""; ReplyBody = ""
                          LastOperationId = operationId.ToString("D") }
                    Ok { Messages = Array.append thread.Messages [| message |]
                         MessageId = messageId
                         Previous = ""; Next = "Pending"
                         Handler = ""; Generation = 0; Reason = "" })

    let private changeMessage messageId (thread: OperatorThread) predicate update makeChange =
        match thread.Messages |> Array.tryFindIndex (fun m -> m.Id = messageId) with
        | None -> Error(invalid "message ID does not exist in this thread")
        | Some index ->
            let message = thread.Messages.[index]
            if not (predicate message) then
                Error(StaleEntityVersion
                    (sprintf "message %s is %s at handler generation %d (%s)"
                        messageId message.Status message.HandlerGeneration message.Handler))
            else
                let after = update message
                let messages = Array.copy thread.Messages
                messages.[index] <- after
                Ok(makeChange message after messages)

    let claim binding actor operationId expectedVersion kind id messageId =
        let request = JsonSerializer.Serialize(
            {| subjectKind = kind; subjectId = id; expectedVersion = expectedVersion
               messageId = messageId |})
        apply binding actor operationId expectedVersion kind id "Claim" request (fun thread ->
            changeMessage messageId thread (fun m -> m.Status = "Pending")
                (fun m ->
                    { m with
                        Status = "Claimed"; Handler = actor
                        HandlerGeneration = m.HandlerGeneration + 1
                        LastOperationId = operationId.ToString("D") })
                (fun before after messages ->
                    { Messages = messages; MessageId = messageId
                      Previous = before.Status; Next = after.Status
                      Handler = actor; Generation = after.HandlerGeneration; Reason = "" }))

    let handle binding actor operationId expectedVersion kind id messageId
               handlerGeneration replyId (replyBody: string) =
        if handlerGeneration < 1 || not (nonBlank replyId)
           || isNull replyBody || Encoding.UTF8.GetByteCount(replyBody) > 32768 then
            Error(invalid "claim generation, stable reply ID and reply body required")
        else
            let request = JsonSerializer.Serialize(
                {| subjectKind = kind; subjectId = id; expectedVersion = expectedVersion
                   messageId = messageId; handlerGeneration = handlerGeneration
                   replyId = replyId; replyBody = replyBody |})
            apply binding actor operationId expectedVersion kind id "Handle" request (fun thread ->
                changeMessage messageId thread
                    (fun m ->
                        m.Status = "Claimed" && m.Handler = actor
                        && m.HandlerGeneration = handlerGeneration)
                    (fun m ->
                        { m with Status = "Handled"; ReplyId = replyId
                                 ReplyBody = replyBody
                                 LastOperationId = operationId.ToString("D") })
                    (fun before after messages ->
                        { Messages = messages; MessageId = messageId
                          Previous = before.Status; Next = after.Status
                          Handler = actor; Generation = after.HandlerGeneration; Reason = "" }))

    // Explicit operator/handler recovery: do not silently time out a claim.
    // The audit retains old handler, generation, reason and backup Git SHA.
    let release binding actor operationId expectedVersion kind id messageId
                handlerGeneration reason =
        if handlerGeneration < 1 || not (nonBlank reason) then
            Error(invalid "current claim generation and audit reason required")
        else
            let request = JsonSerializer.Serialize(
                {| subjectKind = kind; subjectId = id; expectedVersion = expectedVersion
                   messageId = messageId; handlerGeneration = handlerGeneration
                   reason = reason |})
            apply binding actor operationId expectedVersion kind id "Release" request (fun thread ->
                changeMessage messageId thread
                    (fun m -> m.Status = "Claimed"
                              && m.HandlerGeneration = handlerGeneration)
                    (fun m ->
                        { m with Status = "Pending"; Handler = ""
                                 LastOperationId = operationId.ToString("D") })
                    (fun before after messages ->
                        { Messages = messages; MessageId = messageId
                          Previous = before.Status; Next = after.Status
                          Handler = before.Handler
                          Generation = before.HandlerGeneration
                          Reason = reason }))

    // Privileged, inspectable recovery for anomalous records. It deliberately
    // accepts a replacement JSON document without demanding a new application
    // schema or a code change. The old blob remains reachable through the
    // recorded backup Git head and its blob OID; the repair note is immutable.
    // This is not an automatic retry of an uncertain operation.
    let repairPath (operationId: Guid) =
        "records/work-queue/operator-conversations/repairs/" +
        operationId.ToString("D") + ".json"

    [<CLIMutable>]
    type RepairAudit =
        { SchemaVersion: int
          ProjectId: string
          SubjectKind: string
          SubjectId: string
          OperationId: string
          Actor: string
          Reason: string
          PreviousGitHead: string
          PreviousBlobOid: string
          ReplacementSha256: string }

    let inspectRaw binding kind id =
        if not (validSubject kind id) then Error(invalid "invalid repair subject")
        else
            LedgerStore.fetch binding
            |> Result.bind (fun snapshot ->
                LedgerStore.readAt binding snapshot (threadPath kind id)
                |> Result.map (fun (raw, blob) ->
                    snapshot.Commit, blob, raw))

    let repair binding actor operationId kind id expectedBlobOid reason (replacement: string) =
        if operationId = Guid.Empty || not (validSubject kind id)
           || not (nonBlank actor) || not (nonBlank reason)
           || isNull replacement || Encoding.UTF8.GetByteCount(replacement) > 65536 then
            Error(invalid "repair requires operator, operation UUID, subject, reason and bounded JSON")
        else
            try
                use parsed = JsonDocument.Parse(replacement)
                if parsed.RootElement.ValueKind <> JsonValueKind.Object then
                    Error(invalid "repair content must be a JSON object")
                else
                    let replacementHash = digest replacement
                    let requestJson = JsonSerializer.Serialize(
                        {| subjectKind = kind; subjectId = id
                           expectedBlobOid = expectedBlobOid
                           replacementSha256 = replacementHash
                           reason = reason |})
                    let intent =
                        { OperationId = operationId; Actor = actor
                          Kind = "WQ06.Operator.Repair"
                          RequestJson = requestJson }
                    LedgerStore.transact binding intent (fun snapshot ->
                        LedgerStore.readAt binding snapshot (threadPath kind id)
                        |> Result.bind (fun (_, blob) ->
                            if (blob |> Option.defaultValue "none") <> expectedBlobOid then
                                Error(StaleEntityVersion
                                    ("WQ-06 repair backup SHA changed; re-inspect before proceeding at " +
                                      snapshot.Commit))
                            else
                                let audit =
                                    { SchemaVersion = 1
                                      ProjectId = binding.ProjectId.ToString("D")
                                      SubjectKind = kind
                                      SubjectId = id
                                      OperationId = operationId.ToString("D")
                                      Actor = actor
                                      Reason = reason
                                      PreviousGitHead = snapshot.Commit
                                      PreviousBlobOid = expectedBlobOid
                                      ReplacementSha256 = replacementHash }
                                Ok [ { Path = threadPath kind id
                                       ExpectedBlobOid = blob
                                       Content = replacement }
                                     { Path = repairPath operationId
                                       ExpectedBlobOid = None
                                       Content = JsonSerializer.Serialize(audit) } ]))
                    |> Result.map (fun written ->
                        { OperationId = operationId.ToString("D")
                          ObservedGitHead = written.CurrentHead
                          AlreadyApplied = written.AlreadyApplied
                          LedgerSequence = written.Operation.Sequence })
            with :? JsonException as ex ->
                Error(invalid ("repair JSON malformed: " + ex.Message))
