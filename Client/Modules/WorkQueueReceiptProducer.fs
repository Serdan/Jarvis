module Client.WorkQueueReceiptProducer

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueCanonicalSourceEvidence
open Client.WorkQueueCanonicalCasBinder
open Client.WorkQueueRoleAuthority
open Client.WorkCompositionGitLedger
open Client.OrchestrationApi

/// Explicit local operator/agent tooling only: not called by the Scheduler,
/// its launch adapter or the normal MCP command dispatch.
type ReceiptProducerPreview =
    { Status: string
      VerifiedGitHead: string
      SourceOperationId: string
      SourceOperationSequence: int64
      SourceOperationKind: string
      SourceRequestHash: string
      SourceEntityPath: string
      SubjectId: string
      SourceStatus: string
      StageId: string
      StageQueue: string
      PriorReviewStageId: string
      ProjectVersion: int
      SourceHighWater: int64
      Blockers: string list
      AccountCredit: string
      Recovery: string }

type private PreparedReceipt =
    { Request: CanonicalBindRequest
      Snapshot: LedgerSnapshot
      Project: Project
      SourceOperation: LedgerOperation
      SourceEntityPath: string
      SubjectId: string
      SourceStatus: string
      Event: QueueProducerEvent
      PriorReviewStageId: string }

module WorkQueueReceiptProducer =
    let private options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    let private invalid reason = InvalidBinding ("WQ04 receipt producer: " + reason)

    let private onePath (pattern: string) (operation: LedgerOperation) =
        let matches =
            operation.Paths
            |> Array.choose (fun path ->
                let m = Regex.Match(path, pattern, RegexOptions.CultureInvariant)
                if m.Success then Some(path, m) else None)
        match matches with
        | [| item |] -> Ok item
        | _ -> Error(invalid ("Expected exactly one original " + operation.Type + " entity path."))

    let private entity<'a> binding snapshot path : Result<'a, LedgerFailure> =
        LedgerStore.readAt binding snapshot path
        |> Result.bind (function
            | Some raw, Some _ ->
                try
                    let item = JsonSerializer.Deserialize<'a>(raw, options)
                    if isNull (box item) then Error(invalid ("Null canonical entity: " + path))
                    else Ok item
                with ex ->
                    Error(LedgerIntegrityError ("Unreadable canonical entity " + path + ": " + ex.Message))
            | _ -> Error(LedgerIntegrityError ("Missing canonical entity: " + path)))

    let private produceEvent projectId sourceId generation origin prerequisites =
        { ProjectId = projectId
          SourceId = sourceId
          StageGeneration = generation
          Origin = origin
          HardPrerequisites = prerequisites }

    let private prepare (binding: LedgerBinding) (originalOperationId: Guid) (selectedRole: string option) = result {
        if originalOperationId = Guid.Empty then
            return! Error(invalid "Nonempty original source-operation UUID required.")
        let! snapshot = LedgerStore.fetch binding
        let! maybeOperation = LedgerStore.getOperationAt binding snapshot originalOperationId
        let! operation =
            match maybeOperation with
            | Some source -> Ok source
            | None -> Error(invalid ("Original operation not found at verified head: " +
                                     originalOperationId.ToString("D")))
        let! project = WorkQueueGitLedger.loadAt binding snapshot
        let projectId = binding.ProjectId.ToString("D")
        let taskPattern = @"^tasks/(?<task>[A-Za-z0-9_-]{1,80})/task[.]json$"
        let reviewPattern = @"^tasks/(?<task>[A-Za-z0-9_-]{1,80})/reviews/(?<id>[0-9a-f]{32})/review[.]json$"
        let integrationPattern = @"^tasks/(?<task>[A-Za-z0-9_-]{1,80})/integrations/(?<id>[0-9a-f]{32})[.]json$"
        let! (taskId, sourceStatus, path, selector, event, previousReviewId) =
            match operation.Type with
            | "CreateTask" ->
                result {
                    let! (path, matched) = onePath taskPattern operation
                    let! task = entity<ReviewTask> binding snapshot path
                    if task.TaskId <> matched.Groups.["task"].Value then
                        return! Error(invalid "Created task disagrees with original receipt path.")
                    let event =
                        produceEvent projectId task.TaskId 1
                            (QueueProducerOrigin.TaskRequested task.TaskId) Set.empty
                    return task.TaskId, task.Lifecycle, path, TaskCreated task.TaskId, event, ""
                }
            | "SubmitBranchReview" ->
                result {
                    let! (path, matched) = onePath reviewPattern operation
                    let taskId = matched.Groups.["task"].Value
                    let reviewId = Guid.ParseExact(matched.Groups.["id"].Value, "N")
                    let! review = ReviewWorkflow.getBranchReviewAt binding snapshot taskId reviewId
                    if review.ReviewId <> reviewId.ToString("N") then
                        return! Error(invalid "Review identity disagrees with its original receipt.")
                    let selector =
                        ReviewSubmitted(taskId, reviewId, review.ReviewRef,
                                        review.SourceVersion, review.ReviewedHeadOid, review.TargetRef)
                    let event =
                        produceEvent projectId review.ReviewId review.SourceVersion
                            (QueueProducerOrigin.FrozenReviewRequested
                                (taskId, review.ReviewId, review.ReviewedHeadOid, review.ReviewRef))
                            Set.empty
                    return taskId, review.Status, path, selector, event, ""
                }
            | "SubmitIntegration" ->
                result {
                    let! (path, matched) = onePath integrationPattern operation
                    let taskId = matched.Groups.["task"].Value
                    let submissionId = Guid.ParseExact(matched.Groups.["id"].Value, "N")
                    let! submission = entity<IntegrationSubmission> binding snapshot path
                    if submission.TaskId <> taskId || submission.SubmissionId <> submissionId.ToString("N") then
                        return! Error(invalid "Integration identity disagrees with its original receipt.")
                    let! reviewId =
                        match Guid.TryParse submission.ReviewId with
                        | true, id when id <> Guid.Empty -> Ok id
                        | _ -> Error(invalid "Integration has no canonical reviewed UUID.")
                    let reviewEvent =
                        produceEvent projectId submission.ReviewId submission.ReviewVersion
                            (QueueProducerOrigin.FrozenReviewRequested
                                (taskId, submission.ReviewId, submission.ReviewedHeadOid, submission.ReviewRef))
                            Set.empty
                    let reviewStageId = WorkQueueProducers.workId reviewEvent
                    let selector =
                        IntegrationState(taskId, submissionId, reviewId, submission.SubmittedCommit,
                                         submission.TargetRef, "Pending")
                    let event =
                        produceEvent projectId submission.SubmissionId 1
                            (QueueProducerOrigin.AcceptedIntegration
                                (taskId, submission.ReviewId, submission.SubmittedCommit, submission.TargetRef))
                            (Set.singleton reviewStageId)
                    return taskId, submission.Status, path, selector, event, reviewStageId
                }
            | other ->
                Error(invalid ("Original operation " + other +
                                " is not CreateTask, SubmitBranchReview or SubmitIntegration."))
        // The original role demand is explicit or unambiguous from the
        // authenticated, versioned role catalog. Legacy WQ02 subscriptions
        // are not authority, nor may a missing role silently become runnable.
        let stageQueue =
            match event.Origin with
            | QueueProducerOrigin.TaskRequested _ -> "planning"
            | QueueProducerOrigin.FrozenReviewRequested _ -> "review"
            | QueueProducerOrigin.AcceptedIntegration _ -> "integration"
            | _ -> ""
        let! roles = WorkQueueRoleAuthority.loadAt binding snapshot
        let eligible =
            roles.Catalog.Current
            |> Map.toList
            |> List.choose (fun (id, version) ->
                roles.Catalog.Versions.TryFind id
                |> Option.bind (fun history -> history.TryFind version)
                |> Option.filter (fun role ->
                    role.Enabled && role.QueueSubscriptions.Contains stageQueue)
                |> Option.map (fun _ -> id, version))
        let! roleId, roleVersion =
            match selectedRole with
            | Some id ->
                match eligible |> List.tryFind (fun (role, _) -> role = id) with
                | Some role -> Ok role
                | None -> Error(invalid ("Role " + id + " has no current enabled original " + stageQueue + " demand."))
            | None ->
                match eligible with
                | [role] -> Ok role
                | [] -> Error(invalid ("No original enabled versioned role subscribes to " + stageQueue + ". Configure WQ04 role first."))
                | _ -> Error(invalid ("Multiple original roles subscribe to " + stageQueue + "; explicitly select the role for this producer receipt."))
        let request =
            { SourceAnchor =
                { SnapshotHead = snapshot.Commit
                  SnapshotHighWater = snapshot.Header.NextSequence - 1L
                  OriginalOperationId = originalOperationId
                  OriginalRequestHash = operation.RequestHash }
              SourceEvent = selector
              ExpectedProjectVersion = project.Version
              RoleScope = NewRoleDemand(roleId, roleVersion) }
        return
            { Request = request; Snapshot = snapshot; Project = project
              SourceOperation = operation; SourceEntityPath = path
              SubjectId = taskId; SourceStatus = sourceStatus
              Event = event; PriorReviewStageId = previousReviewId }
    }

    /// Inspect the original receipt and the work that would be written. No
    /// permissions, token credits, agents or queue stages are changed.
    let private previewSelected (binding: LedgerBinding) (originalOperationId: Guid)
                                (selectedRole: string option) = result {
        let! prepared = prepare binding originalOperationId selectedRole
        let stageId = WorkQueueProducers.workId prepared.Event
        let! originalStage = WorkQueueGitLedger.readOriginAt binding prepared.Snapshot stageId
        let alreadyPublished = prepared.Project.Works.ContainsKey stageId
        let! compositionLink = WorkCompositionGitLedger.readStageLinkAt binding prepared.Snapshot stageId
        let sameOrigin =
            match originalStage with
            | Some (origin, _) ->
                origin.SourceOperationId = originalOperationId.ToString("D")
                && origin.WorkId = stageId
                && origin.SourceOperationKind = prepared.SourceOperation.Type
                && origin.SourceOperationSequence = prepared.SourceOperation.Sequence
                && origin.SourceRequestHash = prepared.SourceOperation.RequestHash
            | None -> false
        let originalAuthority =
            match originalStage with
            | Some(origin, _) -> not (String.IsNullOrWhiteSpace origin.RoleId) && origin.RoleVersion > 0 && compositionLink.IsSome
            | None -> false
        let validation =
            if alreadyPublished && sameOrigin then
                // The source review/task may have legitimately advanced after
                // the stage was produced. Its historical producer receipt,
                // not the current unclaimed state, governs replay.
                []
            else
                match WorkQueueCanonicalSourceEvidence.read binding
                        prepared.Request.SourceAnchor prepared.Request.SourceEvent with
                | Ok _ -> []
                | Error error -> ["Original receipt/source needs attention: " + sprintf "%A" error]
        let projection =
            match WorkQueueProducers.projectEvent prepared.Project prepared.Event with
            | Ok (ProducedProjection.Work stage) when not alreadyPublished ->
                match WorkCompositionGitLedger.planOriginalStage binding prepared.Snapshot stage with
                | Ok _ -> []
                | Error error -> ["Original composition needs attention: " + sprintf "%A" error]
            | Ok (ProducedProjection.Work _) -> []
            | Ok _ -> ["Original event does not produce a project work stage."]
            | Error rejected -> ["Work projection needs attention: " + sprintf "%A" rejected]
        let dependency =
            if String.IsNullOrWhiteSpace prepared.PriorReviewStageId then []
            else
                match WorkQueueGitLedger.readOriginAt binding prepared.Snapshot prepared.PriorReviewStageId with
                | Error error -> ["Review-stage provenance unreadable: " + sprintf "%A" error]
                | Ok None -> ["Produce/reconcile the original review-request stage before integration; review stage " +
                               prepared.PriorReviewStageId + " is missing."]
                | Ok (Some (origin, _)) when origin.SourceOperationKind <> "SubmitBranchReview" ->
                    ["Integration prerequisite has no original SubmitBranchReview provenance."]
                | _ -> []
        let collision =
            if not alreadyPublished && originalStage.IsNone then []
            elif alreadyPublished && sameOrigin && originalAuthority then []
            elif alreadyPublished && sameOrigin then
                ["Existing original WQ04 stage has no pinned role/composition authority; reconcile legacy receipt with operator."]
            else ["Stage/origin mismatch: inspect the pinned Git ledger and repair with a backup SHA and diff."]
        let status =
            if collision <> [] || dependency <> [] then "NeedsAttention"
            elif alreadyPublished && sameOrigin then "AlreadyProduced"
            elif validation <> [] || projection <> [] then "NeedsAttention"
            else "ReadyToProduce"
        let queue =
            match prepared.Event.Origin with
            | QueueProducerOrigin.TaskRequested _ -> "planning"
            | QueueProducerOrigin.FrozenReviewRequested _ -> "review"
            | QueueProducerOrigin.AcceptedIntegration _ -> "integration"
            | _ -> "unsupported"
        return
            { Status = status
              VerifiedGitHead = prepared.Snapshot.Commit
              SourceOperationId = originalOperationId.ToString("D")
              SourceOperationSequence = prepared.SourceOperation.Sequence
              SourceOperationKind = prepared.SourceOperation.Type
              SourceRequestHash = prepared.SourceOperation.RequestHash
              SourceEntityPath = prepared.SourceEntityPath
              SubjectId = prepared.SubjectId
              SourceStatus = prepared.SourceStatus
              StageId = stageId
              StageQueue = queue
              PriorReviewStageId = prepared.PriorReviewStageId
              ProjectVersion = prepared.Project.Version
              SourceHighWater = prepared.Snapshot.Header.NextSequence - 1L
              Blockers = collision @ validation @ projection @ dependency
              AccountCredit = "NotReserved"
              Recovery = "Configure a current enabled versioned role and linked Work feature before the original producer CAS. TaskCreated uses its authenticated task receipt as lineage, not an invented branch. For legacy/incomplete stages preserve a backup SHA and reconcile with the operator; Unknown requires inspecting the exact UUID. WQ07 remains independent; this preview cannot launch Scheduler." }
    }

    let preview binding originalOperationId =
        previewSelected binding originalOperationId None

    let previewForRole binding originalOperationId roleId =
        previewSelected binding originalOperationId (Some roleId)

    let private appliedProducer (binding: LedgerBinding) (actor: string)
                                (operationId: Guid) (originalOperationId: Guid)
                                (selectedRole: string option) = result {
        let! snapshot = LedgerStore.fetch binding
        let! existing = LedgerStore.getOperationAt binding snapshot operationId
        match existing with
        | None -> return None
        | Some operation ->
            if operation.Type <> "WQ04.PracticalProducer" || operation.Actor <> actor then
                return! Error OperationIdReused
            let sourceId, workId, requestHash =
                try
                    use envelope = JsonDocument.Parse operation.CanonicalRequestJson
                    use request = JsonDocument.Parse(envelope.RootElement.GetProperty("request").GetString())
                    let origin = request.RootElement.GetProperty("origin")
                    origin.GetProperty("SourceOperationId").GetString(),
                    origin.GetProperty("WorkId").GetString(),
                    origin.GetProperty("SourceRequestHash").GetString()
                with _ -> "", "", ""
            if sourceId <> originalOperationId.ToString("D") then
                return! Error OperationIdReused
            if String.IsNullOrWhiteSpace workId
               || not (operation.Paths |> Array.contains (WorkQueueGitLedger.originPath workId)) then
                return! Error(LedgerIntegrityError
                    "Applied WQ04 producer receipt is missing its immutable origin path; reconcile Git ledger.")
            let! priorOrigin = WorkQueueGitLedger.readOriginAt binding snapshot workId
            let! project = WorkQueueGitLedger.loadAt binding snapshot
            let! link = WorkCompositionGitLedger.readStageLinkAt binding snapshot workId
            match priorOrigin with
            | Some (origin, _) when String.IsNullOrWhiteSpace origin.RoleId
                                    || origin.RoleVersion < 1 || link.IsNone ->
                return! Error(LedgerIntegrityError
                    "Original WQ04 receipt is missing role or graph authority; operator reconciliation of the historical record is required.")
            | Some (origin, _) when selectedRole.IsSome && selectedRole <> Some origin.RoleId ->
                return! Error OperationIdReused
            | Some (origin, _) when origin.WorkId = workId
                                    && origin.SourceOperationId = sourceId
                                    && origin.SourceRequestHash = requestHash
                                    && (operation.Paths |> Array.contains (WorkCompositionGitLedger.stageLinkPath workId))
                                    && project.Works.ContainsKey workId -> ()
            | _ ->
                return! Error(LedgerIntegrityError
                    "Applied WQ04 producer receipt has missing or divergent stage/provenance; reconcile Git ledger.")
            return Some
                { Operation = operation; CurrentHead = snapshot.Commit; AlreadyApplied = true }
    }

    /// The caller explicitly opts in with a new UUID and known actor. A
    /// previous durable receipt is recognized before refreshing the source,
    /// so an uncertain successful push is not misclassified as UUID reuse.
    let private produceSelected (binding: LedgerBinding) (actor: string)
                                (operationId: Guid) (originalOperationId: Guid)
                                (selectedRole: string option) = result {
        if String.IsNullOrWhiteSpace actor || operationId = Guid.Empty
           || originalOperationId = Guid.Empty || operationId = originalOperationId then
            return! Error(invalid "Actor and distinct nonempty original/new operation UUIDs required.")
        let! applied = appliedProducer binding actor operationId originalOperationId selectedRole
        match applied with
        | Some original -> return original
        | None ->
            let! prepared = prepare binding originalOperationId selectedRole
            return! WorkQueueCanonicalCasBinder.publishPractical
                        binding actor operationId prepared.Request
    }

    let produce binding actor operationId originalOperationId =
        produceSelected binding actor operationId originalOperationId None

    let produceForRole binding actor operationId originalOperationId roleId =
        produceSelected binding actor operationId originalOperationId (Some roleId)

    let private pretty value =
        JsonSerializer.Serialize(value, JsonSerializerOptions(WriteIndented = true))

    let private help () =
        eprintfn "Usage: JarvisClient queue-producer ROOT preview ORIGINAL_OP_UUID [ROLE_ID]"
        eprintfn "       JarvisClient queue-producer ROOT produce ACTOR NEW_OP_UUID ORIGINAL_OP_UUID [ROLE_ID]"
        eprintfn "       JarvisClient queue-producer ROOT inspect OP_UUID"
        eprintfn "       JarvisClient queue-producer ROOT origin WORK_ID"
        eprintfn "       JarvisClient queue-producer ROOT demand ROLE_ID"
        eprintfn "ROOT must already have an approved Git ledger trust binding."
        eprintfn "Only 'produce' writes; no Scheduler, agent dispatch or account tokens are activated."
        2

    let private parseUuid (value: string) =
        match Guid.TryParse value with
        | true, id when id <> Guid.Empty -> Some id
        | _ -> None

    let private printFailure operationId = function
        | OutcomeUnknown message ->
            eprintfn "%s" (pretty
                {| status = "Unknown"; operationId = operationId; detail = message
                   next = "Inspect this exact UUID at a verified Git head; do not invent a fresh operation ID to retry an uncertain push." |})
            4
        | StaleEntityVersion message ->
            eprintfn "%s" (pretty
                {| status = "Conflict"; operationId = operationId; detail = message
                   next = "Refresh preview and inspect the original Git receipt/stage before a new deliberate attempt." |})
            3
        | other ->
            eprintfn "%s" (pretty
                {| status = "NeedsAttention"; operationId = operationId; detail = sprintf "%A" other
                   next = "Inspect the original receipt and project Git state; repair with saved blob SHA, diff and reason if needed." |})
            2

    let private runAt (binding: LedgerBinding) command =
        match command with
        | "preview" :: source :: remaining when List.length remaining <= 1 ->
            match parseUuid source with
            | Some id ->
                match previewSelected binding id (List.tryHead remaining) with
                | Ok details ->
                    printfn "%s" (pretty details)
                    if details.Status = "NeedsAttention" then 3 else 0
                | Error error -> printFailure source error
            | None -> help ()
        | "produce" :: actor :: newId :: source :: remaining when List.length remaining <= 1 ->
            match parseUuid newId, parseUuid source with
            | Some operationId, Some sourceId ->
                match produceSelected binding actor operationId sourceId (List.tryHead remaining) with
                | Ok receipt ->
                    printfn "%s" (pretty
                        {| status = "Applied"; producerOperationId = receipt.Operation.OperationId
                           sourceOperationId = source; verifiedGitHead = receipt.CurrentHead
                           sequence = receipt.Operation.Sequence; alreadyApplied = receipt.AlreadyApplied
                           next = "Inspect stage/origin and role demand independently; no launch or account credit was authorized." |})
                    0
                | Error error -> printFailure newId error
            | _ -> help ()
        | [ "inspect"; operation ] ->
            match parseUuid operation with
            | None -> help ()
            | Some id ->
                match LedgerStore.fetch binding with
                | Error error -> printFailure operation error
                | Ok snapshot ->
                    match LedgerStore.getOperationAt binding snapshot id with
                    | Error error -> printFailure operation error
                    | Ok (Some original) ->
                        printfn "%s" (pretty
                            {| status = "Applied"; verifiedGitHead = snapshot.Commit
                               originalOperationId = original.OperationId
                               action = original.Type; actor = original.Actor
                               sequence = original.Sequence; requestHash = original.RequestHash
                               resultHash = original.OriginalResultHash; paths = original.Paths |})
                        0
                    | Ok None ->
                        printfn "%s" (pretty
                            {| status = "NotFoundAtVerifiedHead"; verifiedGitHead = snapshot.Commit
                               operationId = operation
                               warning = "Absence at this head does not prove an uncertain Git push cannot arrive." |})
                        0
        | [ "origin"; workId ] ->
            match LedgerStore.fetch binding with
            | Error error -> printFailure "" error
            | Ok snapshot ->
                match WorkQueueGitLedger.loadAt binding snapshot,
                      WorkQueueGitLedger.readOriginAt binding snapshot workId with
                | Ok queue, Ok stored ->
                    printfn "%s" (pretty
                        {| verifiedGitHead = snapshot.Commit; workId = workId
                           work = queue.Works.TryFind workId
                           provenance = stored |> Option.map fst
                           provenanceBlobOid = stored |> Option.map snd
                           recovery = "Git origin and work must agree. Save current blob SHA, inspect diff and record repair reason before direct Git CAS reconciliation." |})
                    0
                | Error error, _ | _, Error error -> printFailure "" error
        | [ "demand"; roleId ] ->
            match WorkQueueRoleAuthority.selectPracticalDemand binding roleId with
            | Ok selection ->
                printfn "%s" (pretty
                    {| status = "ReadOnly"; roleId = roleId; selection = selection
                       warning = "Role ranking is not an admission, launch or Scheduler permission." |})
                0
            | Error error -> printFailure "" error
        | _ -> help ()

    /// A separate, explicit invocation surface: not enabled by ordinary
    /// JarvisClient startup, task transitions or review/integration writes.
    let run args =
        match args with
        | "queue-producer" :: root :: command ->
            try
                let projectRoot = Path.GetFullPath root
                match LedgerBindingConfig.loadAtRootWithSnapshotUsing
                          LedgerStore.fetch projectRoot with
                | Ok (binding, _) -> runAt binding command
                | Error error ->
                    eprintfn "Approved ledger binding unavailable: %A" error
                    2
            with ex ->
                eprintfn "WQ04 receipt producer failed before publication: %s" ex.Message
                2
        | _ -> help ()
