module Client.WorkQueueReactiveWake

open System
open Client.WorkQueueSchedulerHostPreview

// A client-side, opt-in reconciliation seam, not a scheduler service.
// Triggers are hints only. Each decision comes from a fresh pinned Git read;
// launch and browser operation IDs must already exist in the durable planner
// intent. The WorkExecutor owns all admission and side effects.
[<RequireQualifiedAccess>]
type WorkWakeTrigger =
    | WorkCreated
    | WorkTransitionCommitted
    | DependencyCommitted
    | ReviewAccepted
    | IntegrationCommitted
    | CapacityReleased
    | ManualReconcile

type WorkExecutorIntent =
    { WorkId: string
      WorkGeneration: int
      RoleId: string
      RoleVersion: int
      ProjectHead: string
      AccountHead: string
      SourceOperationId: string
      SourceRequestHash: string
      LaunchId: string
      OriginalBrowserOperationId: string }

[<RequireQualifiedAccess>]
type WorkExecutorResult =
    | Applied
    | AlreadyApplied
    | OutcomeUnknown of string
    | Rejected of string

[<RequireQualifiedAccess>]
type WorkWakeResult =
    | Disabled
    | Dormant
    | NoCapacity
    | Reconciled of WorkExecutorIntent
    | Coalesced of WorkExecutorIntent
    | Unknown of code: string * nextAction: string
    | NeedsAttention of code: string * nextAction: string

type WorkWakePorts =
    { // Read must fetch verified account/project heads afresh, not serve a cache.
      ReadVerified: unit -> SchedulerHostPreviewReport
      // Planner can only return a previously recorded, stable launch/one-shot intent.
      // It must not mint identifiers, reserve capacity, or dispatch anything.
      Plan: SchedulerHostPreviewReport -> Result<WorkExecutorIntent option, string>
      Execute: WorkExecutorIntent -> WorkExecutorResult }

// This adapter explicitly reuses the existing bound account/project Git readers.
// Browser evidence remains default-off unless the operator opens its original
// journal separately; no active subscriptions or production executor are installed.
module WorkQueueReactiveWake =
    let boundRead account project roleId () =
        WorkQueueSchedulerHostPreview.preview account project roleId

    let private validUuid (value: string) =
        match Guid.TryParse value with
        | true, id -> id <> Guid.Empty
        | _ -> false

    let private validIntent (report: SchedulerHostPreviewReport)
                            (candidate: PreviewCandidate) (intent: WorkExecutorIntent) =
        intent.WorkId = candidate.WorkId
        && intent.WorkGeneration = candidate.WorkGeneration
        && intent.RoleId = candidate.RoleId
        && intent.RoleVersion = candidate.RoleVersion
        && Some intent.ProjectHead = report.ProjectHead
        && Some intent.AccountHead = report.AccountHead
        && Some intent.SourceOperationId = candidate.SourceOperationId
        && Some intent.SourceRequestHash = candidate.SourceRequestHash
        && candidate.SourceHead.IsSome
        && candidate.OriginBlobOid.IsSome
        // WQ02 task-created planning demand has a pinned canonical origin
        // but no external source commit; a SHA is not a universal prerequisite.
        && validUuid intent.LaunchId
        && validUuid intent.OriginalBrowserOperationId

    let private blockingIssues (report: SchedulerHostPreviewReport) =
        // The bound preview has no browser journal by default. Canonical
        // project/account authority can nevertheless prove a historical launch
        // terminal with its identity-matched account token released.
        // Unsettled launches and other evidence gaps remain blocking.
        let settledHistory =
            report.ExistingLaunches
            |> List.filter (fun launch ->
                launch.State = "TerminalPendingRelease"
                && launch.AccountTokenStatus = "Released")
            |> List.map (fun launch -> launch.LaunchId)
            |> Set.ofList
        report.Issues
        |> List.filter (fun issue ->
            issue.Code <> "DispatchDisabled"
            && issue.Code <> "CapacityFull"
            && not ((issue.Code = "BrowserJournalUnknown" || issue.Code = "BrowserJournalMissing")
                    && settledHistory.Contains issue.Record))

    /// Holds only a bounded, optional local duplicate hint. Safe after restart:
    /// all durable identity and replay/outcome recovery belong to the injected
    /// planner/executor, never to this process or the server.
    type Coordinator(ports: WorkWakePorts, ?enabled: bool, ?maxReadRetries: int) =
        let enabled = defaultArg enabled false
        let maxReadRetries = max 1 (min 5 (defaultArg maxReadRetries 3))
        let sync = obj ()
        let recent = ResizeArray<Guid>()
        let unknownOriginals = ResizeArray<Guid>()
        let inFlight = System.Collections.Generic.HashSet<Guid>()
        let recentLimit = 128

        let remember (history: ResizeArray<Guid>) operationId =
            if not (history.Contains operationId) then
                if history.Count = recentLimit then history.RemoveAt 0
                history.Add operationId

        let read () =
            try Ok(ports.ReadVerified())
            with ex -> Error ex.Message

        let plan report =
            try ports.Plan report
            with ex -> Error ex.Message

        let execute intent =
            try ports.Execute intent
            with ex -> WorkExecutorResult.OutcomeUnknown ex.Message

        // A request collapses all input signals to one Git reconciliation.
        // Signal text is never used as a proof of committed dependency changes.
        member _.Wake(triggers: WorkWakeTrigger list) =
            lock sync (fun () ->
                if not enabled then WorkWakeResult.Disabled
                // All equivalent event deliveries in this invocation collapse
                // into a set. No signal means no work; explicit ManualReconcile
                // is the deterministic restart/resubscribe path.
                elif Set.ofList triggers |> Set.isEmpty then WorkWakeResult.Dormant
                else
                    let rec reconcile remaining =
                        match read () with
                        | Error error ->
                            WorkWakeResult.Unknown(
                                "CanonicalReadFailed",
                                "Inspect original approved Git account/project bindings and retry with the same operations: " + error)
                        | Ok first ->
                            match first.AccountHead, first.ProjectHead with
                            | Some accountHead, Some projectHead
                                when not (String.IsNullOrWhiteSpace accountHead)
                                     && not (String.IsNullOrWhiteSpace projectHead) ->
                                let issues = blockingIssues first
                                match issues with
                                | firstIssue :: _ ->
                                    WorkWakeResult.NeedsAttention(firstIssue.Code, firstIssue.NextAction)
                                | [] when first.AccountMigration <> Some "Activated" ->
                                    WorkWakeResult.NeedsAttention(
                                        "AccountNotActivated",
                                        "Use the approved account migration/cutover process; the coordinator does not activate it.")
                                | [] when first.AvailableSlots.IsNone ->
                                    WorkWakeResult.Unknown(
                                        "CapacityProofUnavailable",
                                        "Re-read verified account capacity and held-token authority before admission.")
                                | [] when first.AvailableSlots.Value <= 0 ->
                                    WorkWakeResult.NoCapacity
                                | [] ->
                                    match first.WouldLaunch with
                                    | None -> WorkWakeResult.Dormant
                                    | Some candidate ->
                                        if candidate.SourceOperationId.IsNone
                                           || candidate.SourceRequestHash.IsNone
                                           || candidate.SourceHead.IsNone
                                           || candidate.OriginBlobOid.IsNone then
                                            WorkWakeResult.Unknown(
                                                "SourceProofMissing",
                                                "Recover the original canonical source operation, request hash, pinned head and origin OID; do not fabricate a launch ID.")
                                        else
                                            match plan first with
                                            | Error error ->
                                                WorkWakeResult.NeedsAttention(
                                                    "PlannerUnresolved",
                                                    "Inspect durable planner intents before retrying: " + error)
                                            | Ok None -> WorkWakeResult.Dormant
                                            | Ok(Some intent) when not (validIntent first candidate intent) ->
                                                WorkWakeResult.NeedsAttention(
                                                    "PlannerIntentMismatch",
                                                    "Restore pinned Work/generation/role/head/source and original launch/browser UUIDs.")
                                            | Ok(Some intent) ->
                                                // A second independently fetched snapshot fences stale
                                                // plans. A changed head retries, bounded by the caller.
                                                match read () with
                                                | Error error ->
                                                    WorkWakeResult.Unknown(
                                                        "CanonicalRecheckFailed",
                                                        "Recover the original operation before deciding whether to replay: " + error)
                                                | Ok current when current.AccountHead <> first.AccountHead
                                                                  || current.ProjectHead <> first.ProjectHead ->
                                                    if remaining > 1 then reconcile (remaining - 1)
                                                    else WorkWakeResult.NeedsAttention(
                                                        "ConcurrentGitChanges",
                                                        "Git heads changed during bounded reconciliation; retry from latest verified heads, preserving original operation IDs.")
                                                | Ok current when current.WouldLaunch <> first.WouldLaunch
                                                                  || current.AvailableSlots <> first.AvailableSlots
                                                                  || current.AccountMigration <> first.AccountMigration
                                                                  || not (List.isEmpty (blockingIssues current)) ->
                                                    WorkWakeResult.Unknown(
                                                        "InconsistentRead",
                                                        "Same Git heads yielded different readiness. Audit bound read ports and original evidence.")
                                                | Ok current ->
                                                    // A previous launch requires reconciliation of its
                                                    // *original* attempt; never blindly submit another.
                                                    if current.ExistingLaunches |> List.exists (fun x -> x.LaunchId = intent.LaunchId) then
                                                        WorkWakeResult.NeedsAttention(
                                                            "OriginalLaunchExists",
                                                            "Inspect the original project admission, browser journal and account token; never re-mint or re-submit.")
                                                    else
                                                        let original = Guid.Parse intent.OriginalBrowserOperationId
                                                        if unknownOriginals.Contains original then
                                                            WorkWakeResult.NeedsAttention(
                                                                "OriginalOperationUnknown",
                                                                "Reconcile original browser operation " + intent.OriginalBrowserOperationId +
                                                                " from its durable journal before any further execution.")
                                                        elif recent.Contains original || inFlight.Contains original then
                                                            WorkWakeResult.Coalesced intent
                                                        else
                                                            // External executor callbacks can synchronously
                                                            // re-enter this monitor on the same thread.
                                                            inFlight.Add original |> ignore
                                                            let outcome =
                                                                try execute intent
                                                                finally inFlight.Remove original |> ignore
                                                            match outcome with
                                                            | WorkExecutorResult.Applied
                                                            | WorkExecutorResult.AlreadyApplied ->
                                                                remember recent original
                                                                WorkWakeResult.Reconciled intent
                                                            | WorkExecutorResult.OutcomeUnknown reason ->
                                                                remember unknownOriginals original
                                                                WorkWakeResult.NeedsAttention(
                                                                    "OriginalOperationUnknown",
                                                                    "Reconcile original browser operation " + intent.OriginalBrowserOperationId +
                                                                    " with its durable journal/receipt; do not submit a new ID. " + reason)
                                                            | WorkExecutorResult.Rejected reason ->
                                                                WorkWakeResult.NeedsAttention(
                                                                    "ExecutorRejected",
                                                                    "Inspect pinned admission and original operation before retrying: " + reason)
                            | _ ->
                                WorkWakeResult.Unknown(
                                    "CanonicalHeadsUnavailable",
                                    "Fetch complete approved account and project Git heads before considering Work.")
                    reconcile maxReadRetries)
