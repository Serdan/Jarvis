module Client.SignalR.Client

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Client
open Client.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Client.Effect
open Common
open FsToolkit.ErrorHandling

let receiveMessage (rt: Runtime) message =
    rt.Tui.Log message
    Task.CompletedTask

// A reservation is a canonical *in-flight* veto, not an expiring activity
// lease. Keep one per process/assignment across a burst of calls and release
// only after the last call has finished and an idle period has elapsed.
// Neither the client cache nor a failed timer may clear the canonical record.
type private ReservationSlot =
    { Sync: obj
      mutable Assignment: (string * string) option
      mutable ReservationId: Guid option
      mutable BeginOperationId: Guid
      mutable ReleaseOperationId: Guid
      mutable Confirmed: bool
      mutable Release: (Guid -> Guid -> Result<unit, string>) option
      mutable ActiveCalls: int
      mutable LastFinishedAt: DateTimeOffset
      mutable TimerRunning: bool
      mutable ReleaseFailure: string option }

type internal CoalescedReservationManager(idleDelay: TimeSpan, report: string -> unit) =
    let slots = ConcurrentDictionary<string, ReservationSlot>(StringComparer.Ordinal)

    let releaseCanonical (slot: ReservationSlot) =
        match slot.ReservationId, slot.Release with
        | None, None -> Ok ()
        | Some reservationId, Some release when slot.Confirmed ->
            let outcome =
                try release reservationId slot.ReleaseOperationId
                with ex -> Error ex.Message
            match outcome with
            | Ok () ->
                slot.Assignment <- None
                slot.ReservationId <- None
                slot.Release <- None
                slot.Confirmed <- false
                slot.ReleaseFailure <- None
                Ok ()
            | Error failure ->
                slot.ReleaseFailure <- Some failure
                report (
                    $"In-flight reservation {reservationId:N} could not be released: {failure}. " +
                    "The canonical correction veto remains until recovery.")
                Error failure
        | _ -> Error "The canonical reservation admission outcome is uncertain."

    let scheduleIdleRelease (slot: ReservationSlot) =
        if not slot.TimerRunning then
            slot.TimerRunning <- true
            let rec finishWhenIdle () =
                task {
                    do! Task.Delay(idleDelay)
                    let finished =
                        lock slot.Sync (fun () ->
                            if slot.ActiveCalls > 0
                               || DateTimeOffset.UtcNow - slot.LastFinishedAt < idleDelay then
                                false
                            else
                                if slot.ReservationId.IsSome then
                                    releaseCanonical slot |> ignore
                                slot.TimerRunning <- false
                                true)
                    if not finished then return! finishWhenIdle ()
                }
            Task.Run(Func<Task>(fun () -> finishWhenIdle () :> Task)) |> ignore

    // The operation ID of an uncertain begin/release is reused on retries.
    // This permits idempotent recovery after a push succeeded but the response
    // was lost. Never create a second reservation while the first is uncertain.
    member _.Run<'T>(key: string, taskId: string, attemptId: string,
                      reserve: Guid -> Guid ->
                          Result<(Guid -> Guid -> Result<unit, string>), string>,
                      work: unit -> Task<'T>) : Task<Result<'T, string>> =
        task {
            let slot =
                slots.GetOrAdd(key, fun _ ->
                    { Sync = obj(); Assignment = None; ReservationId = None
                      BeginOperationId = Guid.Empty; ReleaseOperationId = Guid.Empty
                      Confirmed = false; Release = None; ActiveCalls = 0
                      LastFinishedAt = DateTimeOffset.MinValue
                      TimerRunning = false; ReleaseFailure = None })
            let acquireFresh () =
                let reservationId = Guid.NewGuid()
                let beginId = Guid.NewGuid()
                slot.Assignment <- Some(taskId, attemptId)
                slot.ReservationId <- Some reservationId
                slot.BeginOperationId <- beginId
                slot.ReleaseOperationId <- Guid.NewGuid()
                slot.Confirmed <- false
                let result =
                    try reserve reservationId beginId
                    with ex -> Error ex.Message
                match result with
                | Ok release ->
                    slot.Release <- Some release
                    slot.Confirmed <- true
                    slot.ReleaseFailure <- None
                    slot.ActiveCalls <- slot.ActiveCalls + 1
                    Ok ()
                | Error failure ->
                    Error($"Reservation {reservationId:N} admission uncertain: {failure}. " +
                          "Retry this operation after checking its canonical status.")
            let admission =
                lock slot.Sync (fun () ->
                    match slot.Assignment, slot.ReservationId with
                    | None, None -> acquireFresh ()
                    | Some (oldTask, oldAttempt), Some reservationId ->
                        if not slot.Confirmed then
                            if oldTask <> taskId || oldAttempt <> attemptId then
                                Error "An uncertain reservation belongs to a different assignment."
                            else
                                let result =
                                    try reserve reservationId slot.BeginOperationId
                                    with ex -> Error ex.Message
                                match result with
                                | Ok release ->
                                    slot.Release <- Some release
                                    slot.Confirmed <- true
                                    slot.ReleaseFailure <- None
                                    slot.ActiveCalls <- slot.ActiveCalls + 1
                                    Ok ()
                                | Error failure ->
                                    Error($"Reservation {reservationId:N} admission remains uncertain: {failure}")
                        elif slot.ActiveCalls > 0 && (oldTask <> taskId || oldAttempt <> attemptId) then
                            Error "Another assignment still has active calls in this client."
                        elif slot.ReleaseFailure.IsSome
                             || oldTask <> taskId || oldAttempt <> attemptId then
                            match releaseCanonical slot with
                            | Ok () -> acquireFresh ()
                            | Error failure ->
                                Error($"Cannot resolve prior in-flight reservation: {failure}")
                        else
                            slot.ActiveCalls <- slot.ActiveCalls + 1
                            Ok ()
                    | _ -> Error "Corrupt in-process reservation state.")
            match admission with
            | Error error -> return Error error
            | Ok () ->
                try
                    let! outcome = work()
                    return Ok outcome
                finally
                    lock slot.Sync (fun () ->
                        slot.ActiveCalls <- slot.ActiveCalls - 1
                        slot.LastFinishedAt <- DateTimeOffset.UtcNow
                        if slot.ActiveCalls = 0 then scheduleIdleRelease slot)
        }

    member _.Flush(key: string) =
        match slots.TryGetValue key with
        | false, _ -> Ok ()
        | true, slot ->
            lock slot.Sync (fun () ->
                if slot.ActiveCalls <> 0 then
                    Error "Cannot release a reservation while commands are in flight."
                else releaseCanonical slot)

// Explicit agent labels identify the current work slot. Cache canonical
// Working state for ordinary calls to avoid one remote git fetch per tool.
let private workingGuidanceCaches = ConditionalWeakTable<Runtime, WorkingTurnGuidance.AgentCache>()
let private inFlightManagers = ConditionalWeakTable<Runtime, CoalescedReservationManager>()
let private instructionDeliveryCaches = ConditionalWeakTable<Runtime, AgentInstructions.DeliveryCache>()

let internal flushInFlightActivity (rt: Runtime) projectId (agentId: string) =
    let manager =
        inFlightManagers.GetValue(rt, fun _ ->
            CoalescedReservationManager(TimeSpan.FromSeconds(30.), rt.Tui.Log))
    manager.Flush((ProjectPaths.ProjectId.value projectId)
                  + "::" + agentId.ToUpperInvariant())
let invalidateAgentStates (rt: Runtime) (projectName: string) (agentIds: string array) =
    // Server notifications only carry the logical project identity. Worktree
    // selectors share the canonical ledger/assignment cache by ProjectId.
    match ProjectPaths.ProjectTarget.parse projectName with
    | Ok target ->
        let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
            WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
        if not (isNull agentIds) then
            let projectId = ProjectPaths.ProjectTarget.projectId target
            for agentId in agentIds |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.distinct do
                cache.Invalidate(projectId, agentId)
    | Error _ -> ()

let invalidateAllAgentStates (rt: Runtime) =
    // Reconnection can follow missed updates from other devices.
    let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
        WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
    cache.InvalidateAll()
// Only commands addressing repository source are checkout selectors.
// Message/activity project fields are literal history labels, not paths.
type private CommandProject =
    | Unscoped
    | ActivityLabel of string option
    | Checkout of ProjectPaths.ProjectTarget

let private projectTarget = function
    | Checkout target -> Some target
    | Unscoped | ActivityLabel _ -> None

let private classifyProject command =
    match command with
    | MessageCommand cmd -> Ok(ActivityLabel cmd.ProjectName)
    | GetClientActivityCommand cmd -> Ok(ActivityLabel cmd.ProjectName)
    | ListSkillsCommand cmd when cmd.ProjectName = "@global" -> Ok Unscoped
    | GetSkillCommand cmd when cmd.ProjectName = "@global" -> Ok Unscoped
    | CreateSkillCommand cmd when cmd.ProjectName = "@global" -> Ok Unscoped
    | GetJobResultCommand cmd when String.IsNullOrWhiteSpace cmd.ProjectName -> Ok Unscoped
    | CancelJobCommand cmd when String.IsNullOrWhiteSpace cmd.ProjectName -> Ok Unscoped
    | _ ->
        match AgentCommandInfo.projectName command with
        | None -> Ok Unscoped
        | Some raw -> ProjectPaths.ProjectTarget.parse raw |> Result.map Checkout


// A local approved binding means this checkout participates in canonical
// agent-state correction. Failed Git/agent reads must not be interpreted as
// "no active assignment" and silently skip a required in-flight fence.
// Unbound source-only projects continue to work without ledger enrollment.
let private hasCanonicalLedgerBinding (rt: Runtime) (project: string) =
    match ProjectPaths.resolveProjectRoot project rt with
    | Error _ -> true // A failed project lookup cannot prove the absence of a ledger.
    | Ok (FolderPath root) ->
        let path = OrchestrationApi.LedgerBindingConfig.pathForRoot root
        try
            File.GetAttributes(path) |> ignore
            true
        with
        | :? FileNotFoundException | :? DirectoryNotFoundException -> false
        | _ -> true

let private loadAgentState (rt: Runtime) (project: string) (agentId: string) =
    match OrchestrationApi.LedgerBindingConfig.loadWithSnapshot project rt with
    | Error _ -> None
    | Ok (binding, snapshot) ->
        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurnAt binding snapshot agentId with
        | Ok agent -> Some agent
        | Error _ -> None

let private checkWorkingGuidance (rt: Runtime) agentId (target: ProjectPaths.ProjectTarget option) command =
    if String.IsNullOrWhiteSpace agentId then []
    else
        match target with
        | None -> []
        | Some target ->
            let projectId = ProjectPaths.ProjectTarget.projectId target
            let project = ProjectPaths.ProjectId.value projectId
            let now = DateTimeOffset.UtcNow
            let cache = workingGuidanceCaches.GetValue(rt, fun _ -> WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
            let agent =
                cache.Get(now, projectId, agentId, fun () ->
                    loadAgentState rt project agentId)
            match agent |> Option.bind (WorkingTurnGuidance.instructionAt now) with
            | Some content ->
                [ { Scope = "working-turn"; Path = "jarvis/working-turn"
                    Sha256 = ""; Content = content } ]
            | None -> []

// A cross-project notification is sent using the *source* agent identity,
// which is not an identity of an agent in the destination project. Routing
// health probes likewise do not represent agent activity. Keep both out of
// destination last-seen, turn-guidance and implicit-activation handling.
let private isLocalAgentActivity = function
    | TaskWriteCommand cmd when cmd.Action = "NotifyAgent"
                                && not (String.IsNullOrWhiteSpace cmd.SourceRef)
                                && cmd.SourceRef <> cmd.ProjectName -> false
    | LedgerReadCommand cmd when cmd.Action = "Health" -> false
    | ObserveAgentCommand _ -> false // Neither observer nor target is acting.
    | _ -> true

// An outstanding canonical reservation protects *every* admitted agent
// command, including a read that takes longer than its former lease deadline.
// Batched reservation scopes amortize the two Git writes across a burst, so
// classifying by guessed command duration is unnecessary.
let private requiresDurableActivityReservation agentId = function
    | TaskWriteCommand cmd -> cmd.ActorId = agentId
    | TaskReadCommand cmd -> String.IsNullOrWhiteSpace cmd.AgentId || cmd.AgentId = agentId
    | LedgerWriteCommand _ | ReviewWriteCommand _
    | RetryPendingIntentCommand _ | SupersedePendingIntentCommand _
    | AbandonPendingIntentCommand _ | ObserveAgentCommand _ -> false
    | LedgerReadCommand cmd when cmd.Action = "Health" -> false
    | _ -> true

// An agent's first ordinary tool call starts its Ready assignment.
// No new session identifier or explicit resume tool is needed. Never
// auto-activate a different agent merely because an administrative operation
// names that agent as its target.
let private activatesAgent (agentId: string) command =
    match command with
    | _ when not (isLocalAgentActivity command) -> false
    | TaskWriteCommand cmd ->
        cmd.ActorId = agentId
        && List.contains cmd.Action
            [ "CheckpointAttempt"; "YieldAgent"; "CompleteAgentWork"
              "AcknowledgeFeedback"; "AcknowledgeNotification"; "NotifyAgent"
              "BindAttemptSource"; "RebindAttemptSource"; "BindReviewBase"
              "SetAttemptSource"; "SubmitIntegration"; "CompleteIntegration" ]
    | TaskReadCommand cmd ->
        String.IsNullOrWhiteSpace cmd.AgentId || cmd.AgentId = agentId
    | LedgerWriteCommand _ | ReviewWriteCommand _
    | RetryPendingIntentCommand _ | SupersedePendingIntentCommand _
    | AbandonPendingIntentCommand _ -> false
    | _ -> true

let private activateReadyOnActivity (rt: Runtime) (agentId: string)
                                (target: ProjectPaths.ProjectTarget option) command =
    if String.IsNullOrWhiteSpace agentId || not (activatesAgent agentId command) then Ok ([], None)
    else
        match target with
        | None -> Ok ([], None)
        | Some target ->
            let projectId = ProjectPaths.ProjectTarget.projectId target
            let project = ProjectPaths.ProjectId.value projectId
            let cache = workingGuidanceCaches.GetValue(rt, fun _ -> WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
            // The server broadcasts invalidation after successful ledger writes
            // on any connected device; the inbox also forces authoritative refresh.
            // Keep the 2-minute fallback for changes outside Jarvis and missed
            // notifications. Do not fetch from GitHub for every ordinary source call.
            match command with
            | TaskReadCommand cmd when cmd.Action = "AgentInbox" -> cache.Invalidate(projectId, agentId)
            | _ -> ()
            let current =
                cache.Get(DateTimeOffset.UtcNow, projectId, agentId, fun () ->
                    loadAgentState rt project agentId)
            let renewWorkingLease (assignment: ReviewWorkflow.AgentAssignment) =
                match OrchestrationApi.LedgerBindingConfig.load project rt with
                | Error error -> Error(Client.ValidationError($"Cannot renew Working activity lease: {error}"))
                | Ok binding ->
                    match ReviewWorkflow.ReviewWorkflow.recordWorkingActivity binding
                              (Guid.NewGuid()) agentId assignment.TaskId assignment.AttemptId with
                    | Ok _ ->
                        cache.Invalidate(projectId, agentId)
                        Ok ()
                    | Error error ->
                        cache.Invalidate(projectId, agentId)
                        Error(Client.ValidationError($"Cannot renew Working activity lease: {error}. Reread your agent inbox."))
            match current with
            | Some agent when agent.State = "Ready"
                              && not (isNull (box agent.CurrentAssignment))
                              && not (String.IsNullOrWhiteSpace agent.CurrentAssignment.TaskId) ->
                match OrchestrationApi.LedgerBindingConfig.load project rt with
                | Error error -> Error(Client.ValidationError($"Cannot activate Ready agent: {error}"))
                | Ok binding ->
                    let result =
                        ReviewWorkflow.ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid())
                            agent.CurrentAssignment.TaskId agentId
                    cache.Invalidate(projectId, agentId)
                    match result with
                    | Ok write when write.AlreadyApplied -> Ok ([], None)
                    | Ok write ->
                        // Only a successfully committed Ready-to-Working write
                        // provides evidence of a transition performed here.
                        let committed : AgentInboxActivation =
                            { ProjectName = project
                              AgentId = agentId
                              ActivatedByThisCall = true
                              BeforeState = Some "Ready"
                              AfterState = Some "Working"
                              CommittedHead = Some write.CurrentHead
                              OperationId = Some write.Operation.OperationId }
                        if requiresDurableActivityReservation agentId command then
                            // The following reserved ingress establishes the
                            // in-flight fence without changing owner CAS tokens.
                            Ok (Client.LedgerStore.LedgerStore.changedAgentIds write, Some committed)
                        else
                            match renewWorkingLease agent.CurrentAssignment with
                            | Ok () -> Ok (Client.LedgerStore.LedgerStore.changedAgentIds write, Some committed)
                            | Error error -> Error error
                    | Error(Client.LedgerStore.StaleEntityVersion _) ->
                        // Another concurrent tool may already have started this
                        // assignment. Do not reset its Working clock.
                        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurn binding agentId with
                        | Ok latest when latest.State = "Working"
                                         && not (isNull (box latest.CurrentAssignment)) ->
                            if requiresDurableActivityReservation agentId command then
                                Ok ([], None)
                            else
                                match renewWorkingLease latest.CurrentAssignment with
                                | Ok () -> Ok ([], None)
                                | Error error -> Error error
                        | _ -> Error(Client.ValidationError "Agent assignment changed before automatic activation.")
                    | Error error -> Error(Client.ValidationError($"Automatic agent activation failed: {error}"))
            | Some agent when agent.State = "Working"
                              && not (requiresDurableActivityReservation agentId command) ->
                // A locally cached *canonical* lease guarantees that a
                // correction cannot win before its expiry. Renew through Git
                // only near expiry; never silently revive a corrected turn.
                if isNull (box agent.CurrentAssignment) then
                    Error(Client.ValidationError "Working activity has no assigned attempt.")
                elif ReviewWorkflow.ReviewWorkflow.workingActivityLeaseSufficient DateTimeOffset.UtcNow agent then
                    Ok ([], None)
                else
                    match renewWorkingLease agent.CurrentAssignment with
                    | Ok () -> Ok ([], None)
                    | Error error -> Error error
            | None when hasCanonicalLedgerBinding rt project ->
                Error(Client.ValidationError(
                    "Cannot resolve the canonical agent state for this bound project; refusing to run unfenced. Reread the agent inbox."))
            | _ -> Ok ([], None)

// Keep a canonical lease valid throughout long-running tools. An admitted
// command can outlive its initial lease, so ingress-only renewal is not enough.
// The interval is injectable for deterministic scheduling tests.
let internal withActivityLeaseHeartbeat (interval: TimeSpan)
                                        (renew: unit -> Result<unit, string>)
                                        (work: unit -> Task<'t>) =
    task {
        use stop = new System.Threading.CancellationTokenSource()
        let gate = obj()
        let mutable renewalFailure : string option = None
        let background =
            Task.Run(System.Func<Task>(fun () ->
                task {
                    try
                        while not stop.IsCancellationRequested do
                            do! Task.Delay(interval, stop.Token)
                            if not stop.IsCancellationRequested then
                                match renew() with
                                | Ok () -> ()
                                | Error reason ->
                                    lock gate (fun () -> renewalFailure <- Some reason)
                                    stop.Cancel()
                    with :? OperationCanceledException -> ()
                } :> Task))
        try
            let! result = work()
            stop.Cancel()
            do! background
            return result, lock gate (fun () -> renewalFailure)
        finally
            stop.Cancel()
    }

// Only the Working turn identifies a stable delivery scope. Ledger calls bootstrap
// root guidance, while normal source operations discover deeper instructions.
let private incrementalInstructions (rt: Runtime) (agentId: string)
                                    (target: ProjectPaths.ProjectTarget option)
                                    provenance documents =
    if String.IsNullOrWhiteSpace agentId then documents
    else
        match target with
        | None -> documents
        | Some target ->
            let projectId = ProjectPaths.ProjectTarget.projectId target
            let project = ProjectPaths.ProjectId.value projectId
            let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
                WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
            let now = DateTimeOffset.UtcNow
            let agent =
                cache.Get(now, projectId, agentId, fun () ->
                    loadAgentState rt project agentId)
            match agent with
            | Some active when active.State = "Working" ->
                let turn = string active.StateSequence + ":" + active.WorkingTurnStartedAtUtc
                let delivery = instructionDeliveryCaches.GetValue(rt, fun _ -> AgentInstructions.DeliveryCache())
                delivery.Filter(target, agentId, turn, provenance, documents, fun () ->
                    // Ledger status reads remain usable; failed instruction
                    // resolution is surfaced as a retryable warning.
                    AgentInstructions.rootsForTarget rt target)
            | _ -> documents

let private serialize<'a> (value: 'a) =
    try
        JsonSerializer.Serialize value |> Ok
    with e ->
        e |> ExceptionError |> Error

type private DispatchedResult =
    { Payload: string
      ChangedAgentIds: string list }

let private serialize'<'a> (result: Result<'a, Client.EffectError>) =
    result
    |> Result.bind (fun value ->
        let changedAgentIds =
            match box value with
            | :? OrchestrationApi.OperationResponse.Presented as operation ->
                operation.changedAgentIds
            | _ -> []
        serialize value
        |> Result.map (fun payload ->
            { Payload = payload; ChangedAgentIds = changedAgentIds }))
    |> ValueTask<_>

let private toAgentResponse command instructions stateChanges inboxActivation response =
    match response with
    | Error error ->
        { Result = None
          Error = Some(EffectError.toAgentError error)
          Instructions = instructions
          AgentStateChanges = stateChanges
          InboxActivation = inboxActivation }
    | Ok (dispatched: DispatchedResult) ->
        let payload = dispatched.Payload
        let bytes = Encoding.UTF8.GetByteCount payload +
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize instructions) +
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize stateChanges) +
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize inboxActivation)
        let maxBytes =
            match command with
            | ReadImageCommand _ -> (AgentProtocol.maxImageBytes * 4 / 3) + (64 * 1024)
            | ExportFileCommand _ -> (AgentProtocol.maxExportBytes * 4 / 3) + (64 * 1024)
            | _ -> AgentProtocol.maxResponseBytes

        if bytes <= maxBytes then
            { Result = Some payload
              Error = None
              Instructions = instructions
              AgentStateChanges = stateChanges
              InboxActivation = inboxActivation }
        else
            { Result = None
              Error = Some(OutputTruncated $"Command response was {bytes} bytes and exceeds the safe response size of {maxBytes} bytes.")
              Instructions = instructions
              AgentStateChanges = stateChanges
              InboxActivation = inboxActivation }

let private unwrapProjectName (ProjectName name) = name
let private unwrapContent (Content content) = content

let private readFileResultToDto path value =
    match value with
    | Content'.Text text ->
        {| filePath = path
           content = text
           error = null |}
    | Content'.Error error ->
        {| filePath = path
           content = null
           error = EffectError.toString error |}

let private auditDetails command =
    match command with
    | ImportFileCommand cmd -> Some("ImportFile", Some cmd.ProjectName, [ WorkspaceWrite; NetworkAccess ], [ cmd.FilePath ], None, [])
    | UpdateClientCommand -> Some("UpdateClient", None, [ ProcessExecution; NetworkAccess; Destructive ], [], None, [])
    | WriteFileCommand cmd -> Some("WriteFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | PatchFileCommand cmd -> Some("PatchFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | RunCommandCommand cmd -> Some("RunCommand", Some cmd.ProjectName, [ ProcessExecution ], [], Some cmd.Executable, cmd.Args)
    | RunProjectTaskCommand cmd -> Some("RunProjectTask", Some cmd.ProjectName, [ ProcessExecution ], [], None, [ cmd.TaskName ])
    | CreateSkillCommand cmd -> Some("CreateSkill", Some cmd.ProjectName, [ WorkspaceWrite ], [ $".agents/skills/{cmd.SkillName}/SKILL.md" ], None, [])
    | RemoveWorktreeCommand cmd -> Some("RemoveWorktree", Some cmd.ProjectName, [ VersionControlWrite; Destructive ], [], Some "git", [ cmd.WorktreePath ])
    | TaskWriteCommand cmd -> Some("Task" + cmd.Action, Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.TaskId; cmd.AttemptId; cmd.OperationId ])
    | LedgerWriteCommand cmd -> Some("Ledger" + cmd.Action, Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.RemoteName; cmd.ExpectedTrustedHead; cmd.OperationId ])
    | RetryPendingIntentCommand cmd ->
        Some("RetryPendingIntent", Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ],
             [], Some "git", [ cmd.OperationId; cmd.ExpectedCommandSha256; cmd.ExpectedVerifiedHead ])
    | SupersedePendingIntentCommand cmd ->
        Some("SupersedePendingIntent", Some cmd.ProjectName,
             [ WorkspaceWrite; VersionControlWrite; NetworkAccess ],
             [], Some "git", [ cmd.OperationId; cmd.RelatedOperationId; cmd.Reason ])
    | AbandonPendingIntentCommand cmd ->
        Some("AbandonPendingIntent", Some cmd.ProjectName,
             [ WorkspaceWrite; VersionControlWrite; NetworkAccess ],
             [], Some "git", [ cmd.OperationId; cmd.ExpectedCommandSha256; cmd.Reason ])
    | ReviewWriteCommand cmd -> Some("Review" + cmd.Action, Some cmd.ProjectName, [ WorkspaceWrite; VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.TaskId; cmd.RequestId; cmd.OperationId ])
    | CreateProjectCommand cmd ->
        Some("CreateProject", None, [ WorkspaceWrite ] @ (if cmd.InitializeGit then [ VersionControlWrite ] else []) @ (if cmd.InitializeLedger then [ NetworkAccess ] else []),
             [ cmd.Name ], (if cmd.InitializeGit then Some "git" else None), [ cmd.RemoteUrl ])
    | CreateWorktreeCommand cmd -> Some("CreateWorktree", Some cmd.ProjectName, [ VersionControlWrite ], [], Some "git", [ cmd.Branch; cmd.Name ])
    | GitCommitCommand cmd -> Some("GitCommit", Some cmd.ProjectName, [ VersionControlWrite ], cmd.Paths, Some "git", [ cmd.Message ])
    | StartJobCommand cmd -> Some("StartJob", Some cmd.ProjectName, [ ProcessExecution ], [], Some cmd.Executable, cmd.Args)
    | CancelJobCommand cmd -> Some("CancelJob", None, [ ProcessExecution ], [], None, [ cmd.JobId ])
    | _ -> None

let private audit command response =
    match auditDetails command with
    | None -> ()
    | Some(commandName, projectName, permissions, paths, executable, args) ->
        let summary =
            match response with
            | Ok _ -> "Ok"
            | Error error -> EffectError.toString error

        AuditLog.recordCommand commandName projectName permissions paths executable args summary

let private importFile (resolved: ProjectPaths.ResolvedProject option) (cmd: ImportFileCommand) (rt: Runtime) =
    match cmd.MaxBytes with
    | Some value when value <= 0L ->
        Error(Client.ValidationError "MaxBytes must be greater than zero when supplied.")
    | requestedLimit ->
        match ImportNetwork.parseUrl cmd.DownloadUrl with
        | Error failure -> Error failure
        | Ok _ ->
            let destination =
                match resolved with
                | Some project -> fun rt -> ProjectPaths.resolveSourcePath project cmd.FilePath rt |> Result.map FilePath
                | None -> ProjectPaths.resolveWritableProjectFile cmd.ProjectName cmd.FilePath
            match destination rt with
            | Error error -> Error error
            | Ok(FilePath destinationPath) ->
                let localLimit =
                    if rt.MaxImportBytes = 0L then Int64.MaxValue
                    else rt.MaxImportBytes

                let effectiveLimit =
                    requestedLimit
                    |> Option.map (fun requested -> min requested localLimit)
                    |> Option.defaultValue localLimit

                let parentPath = Path.GetDirectoryName destinationPath
                let tempPath =
                    Path.Combine(parentPath, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.jarvis-import")

                let result =
                    try
                        if File.Exists destinationPath && not cmd.Overwrite then
                            Error(Client.ValidationError $"Destination file already exists: {cmd.FilePath}")
                        else
                            if cmd.CreateParents then
                                Directory.CreateDirectory(parentPath) |> ignore
                            elif not (Directory.Exists parentPath) then
                                raise (DirectoryNotFoundException $"Parent directory does not exist: {Path.GetDirectoryName(cmd.FilePath)}")

                            // Automatic trust under PartialTrust is restricted to
                            // provider file hosts throughout the redirect chain.
                            // Generic HTTPS imports require explicit local approval,
                            // or the operator's existing FullTrust setting.
                            let permission = rt :> PermissionIO
                            let redirectTrust =
                                if permission.TrustLevel = PartialTrust
                                    && ImportNetwork.isKnownAttachment cmd.DownloadUrl then
                                    ImportNetwork.ProviderOriginsOnly
                                else ImportNetwork.AnyPublicHttps

                            let result =
                                ImportNetwork.downloadTo
                                    (ImportNetwork.sharedClient())
                                    redirectTrust cmd.DownloadUrl tempPath effectiveLimit
                                    Threading.CancellationToken.None
                                |> fun task -> task.GetAwaiter().GetResult()

                            match result with
                            | Error failure -> Error failure
                            | Ok receipt ->
                                // The checkout can change during a remote download.
                                // Revalidate source containment before publication.
                                match destination rt with
                                | Error error -> Error error
                                | Ok(FilePath revalidated) when revalidated <> destinationPath ->
                                    Error(Client.ValidationError "Import destination changed during download.")
                                | Ok _ ->
                                    File.Move(tempPath, destinationPath, cmd.Overwrite)
                                    Ok
                                        { FilePath = cmd.FilePath
                                          FileId = cmd.FileId
                                          BytesWritten = receipt.BytesWritten
                                          MimeType = cmd.MimeType
                                          Sha256 = receipt.Sha256 }
                    with
                    | :? IOException ->
                        Error(Client.ValidationError "Unable to publish imported file.")
                    | ex -> Error(Client.ExceptionError ex)
                if File.Exists tempPath then
                    try File.Delete tempPath with _ -> ()
                result

let private sourceOperation resolved withContext legacy =
    match resolved with
    | Some project -> withContext project
    | None -> legacy()

let private dispatch (rt: Runtime) (target: ProjectPaths.ProjectTarget option)
                     (resolved: ProjectPaths.ResolvedProject option) (command: AgentCommand) =
    match command with
    | ListCommandsCommand -> rt |> ProjectBrowser.listCommands |> serialize'
    | ListWorktreesCommand cmd -> rt |> Worktrees.listWorktrees cmd |> serialize'
    | GetWorktreeCommand cmd -> rt |> Worktrees.getWorktree cmd |> serialize'
    | CreateWorktreeCommand cmd -> rt |> Worktrees.createWorktree cmd |> serialize'
    | InspectWorktreeRemovalCommand cmd -> rt |> Worktrees.inspectRemoval cmd |> serialize'
    | RemoveWorktreeCommand cmd -> rt |> Worktrees.removeWorktree cmd |> serialize'
    | TaskReadCommand cmd -> rt |> OrchestrationApi.TaskApi.read cmd |> serialize'
    | ObserveAgentCommand cmd ->
        rt |> OrchestrationApi.AgentObservationApi.observe cmd
            (fun project agent -> rt.Tui.GetAgentLastSeen(project, agent)) |> serialize'
    | TaskWriteCommand cmd when cmd.Action = "ExportTaskSpec" || cmd.Action = "ImportTaskSpec" ->
        match resolved with
        | Some context -> rt |> OrchestrationApi.TaskApi.writeForContext context cmd |> serialize'
        | None -> Error(Client.ValidationError "Task-spec source checkout was not resolved.") |> serialize'
    | TaskWriteCommand cmd -> rt |> OrchestrationApi.TaskApi.write cmd |> serialize'
    | LedgerReadCommand cmd -> rt |> OrchestrationApi.LedgerApi.read cmd |> serialize'
    | LedgerWriteCommand cmd -> rt |> OrchestrationApi.LedgerApi.write cmd |> serialize'
    | ReviewReadCommand cmd -> rt |> OrchestrationApi.ReviewApi.read cmd |> serialize'
    | ReviewWriteCommand cmd -> rt |> OrchestrationApi.ReviewApi.write cmd |> serialize'
    | RetryPendingIntentCommand cmd -> rt |> OrchestrationApi.PendingRecoveryApi.retry cmd |> serialize'
    | SupersedePendingIntentCommand cmd -> rt |> OrchestrationApi.PendingRecoveryApi.supersede cmd |> serialize'
    | AbandonPendingIntentCommand cmd -> rt |> OrchestrationApi.PendingRecoveryApi.abandon cmd |> serialize'
    | CreateProjectCommand cmd -> rt |> ProjectCreation.create cmd |> serialize'
    | ListProjectsCommand ->
        rt
        |> ProjectBrowser.listProjects
        |> Result.map (Seq.map unwrapProjectName >> Seq.toList)
        |> serialize'
    | GetProjectDetailsCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.getProjectDetailsIn project cmd) (fun () -> ProjectBrowser.getProjectDetails cmd)
        |> Result.map (List.map (fun (name, content) -> {| fileName = name; content = unwrapContent content |}))
        |> serialize'
    | ListDirectoryCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.listDirectoryIn project cmd) (fun () -> ProjectBrowser.listDirectory cmd) |> serialize'
    | SearchFilesCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.searchFilesIn project cmd) (fun () -> ProjectBrowser.searchFiles cmd) |> serialize'
    | SearchTextCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.searchTextIn project cmd) (fun () -> ProjectBrowser.searchText cmd) |> serialize'
    | ReadFileCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.readFileIn project cmd) (fun () -> ProjectBrowser.readFile cmd)
        |> Result.map unwrapContent
        |> serialize'
    | ReadFilePageCommand cmd ->
        rt
        |> sourceOperation resolved
            (fun project -> ProjectBrowser.readFilePageIn project cmd)
            (fun () -> ProjectBrowser.readFilePage cmd)
        |> serialize'
    | ReadFilesCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.readFilesIn project cmd) (fun () -> ProjectBrowser.readFiles cmd)
        |> Result.map (Seq.zip cmd.FilePaths >> Seq.map (fun (path, result) -> readFileResultToDto path result) >> Seq.toList)
        |> serialize'
    | GetAgentInstructionsCommand cmd ->
        match target with
        | Some selected -> rt |> AgentInstructions.getExplicit selected cmd |> serialize'
        | None -> Error(Client.ValidationError "A validated project checkout is required.") |> serialize'
    | ReadImageCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.readImageIn project cmd) (fun () -> ProjectBrowser.readImage cmd)
        |> serialize'
    | ExportFileCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.exportFileIn project cmd) (fun () -> ProjectBrowser.exportFile cmd)
        |> serialize'
    | ImportFileCommand cmd -> rt |> importFile resolved cmd |> serialize'
    | UpdateClientCommand -> SelfUpdate.prepare () |> Async.AwaitTask |> Async.RunSynchronously |> serialize'
    | WriteFileCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.writeFileIn project cmd) (fun () -> ProjectBrowser.writeFile cmd) |> serialize'
    | PatchFileCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.patchFileIn project cmd) (fun () -> ProjectBrowser.patchFile cmd)
        |> serialize'
    | RunCommandCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.runCommandIn project cmd) (fun () -> ClientShell.runCommand cmd) |> serialize'
    | ListConfiguredTasksCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.listConfiguredTasksIn project cmd) (fun () -> ClientShell.listConfiguredTasks cmd) |> serialize'
    | RunProjectTaskCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ClientShell.resolveProjectTaskIn project cmd) (fun () -> ClientShell.resolveProjectTask cmd)
        |> Result.bind (fun task -> (sourceOperation resolved (fun project -> ClientShell.runProjectTaskIn project task) (fun () -> ClientShell.runProjectTask cmd.ProjectName task)) rt)
        |> serialize'
    | ListSkillsCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.listSkillsIn project cmd) (fun () -> ProjectBrowser.listSkills cmd) |> serialize'
    | GetSkillCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.getSkillIn project cmd) (fun () -> ProjectBrowser.getSkill cmd) |> serialize'
    | CreateSkillCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.createSkillIn project cmd) (fun () -> ProjectBrowser.createSkill cmd) |> serialize'
    | GetGitStatusCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.getGitStatusIn project cmd) (fun () -> ClientShell.getGitStatus cmd) |> serialize'
    | GetGitDiffCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.getGitDiffIn project cmd) (fun () -> ClientShell.getGitDiff cmd) |> serialize'
    | GitCommitCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.gitCommitIn project cmd) (fun () -> ClientShell.gitCommit cmd) |> serialize'
    | StartJobCommand cmd ->
        match resolved, target with
        | Some project, _ -> rt |> JobManager.startJobIn project cmd |> serialize'
        | None, Some validated -> rt |> JobManager.startJobForTarget validated cmd |> serialize'
        | None, None -> rt |> JobManager.startJob cmd |> serialize'
    | ListJobsCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.listJobsForProject (Some(ProjectPaths.ProjectTarget.projectId validated)) cmd |> serialize'
        | None -> rt |> JobManager.listJobs cmd |> serialize'
    | GetJobResultCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.getJobResultForProject (Some(ProjectPaths.ProjectTarget.projectId validated)) cmd |> serialize'
        | None -> rt |> JobManager.getJobResult cmd |> serialize'
    | CancelJobCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.cancelJobForProject (Some(ProjectPaths.ProjectTarget.projectId validated)) cmd |> serialize'
        | None -> rt |> JobManager.cancelJob cmd |> serialize'
    | MessageCommand cmd ->
        if String.IsNullOrWhiteSpace cmd.Message then
            Error(Client.ValidationError "Message cannot be empty.") |> serialize'
        elif cmd.Message.Length > 1000 then
            Error(Client.ValidationError "Message cannot exceed 1000 characters.") |> serialize'
        else
            rt.Tui.Message(cmd.ProjectName, cmd.Message)
            Ok() |> serialize'
    | GetClientActivityCommand cmd ->
        rt.Tui.GetActivitySnapshot(cmd.ProjectName, cmd.Limit, includeObservers = defaultArg cmd.IncludeObservers false)
        |> Ok
        |> serialize'

let private receiveCommandForTarget (rt: Runtime) (target: ProjectPaths.ProjectTarget option)
                                    (resolved: ProjectPaths.ResolvedProject option)
                                    (preparedTask: ProjectTaskDefinition option) (command: AgentCommand) =
    task {
        let activityId =
            match command with
            | GetClientActivityCommand _
            | MessageCommand _ -> None
            | _ -> Some(rt.Tui.StartActivity(command))
        let stopwatch = Stopwatch.StartNew()

        let permission = rt :> PermissionIO
        let resolution =
            match command with
            | RunProjectTaskCommand cmd ->
                (match preparedTask with
                 | Some task -> Ok task
                 | None ->
                     (sourceOperation resolved
                         (fun project -> ClientShell.resolveProjectTaskIn project cmd)
                         (fun () -> ClientShell.resolveProjectTask cmd)) rt)
                |> Result.map (fun task ->
                    let authorizationCommand =
                        ClientShell.projectTaskAsRunCommand cmd.ProjectName task
                        |> RunCommandCommand

                    authorizationCommand, Some(cmd, task))
            | _ ->
                Ok(command, None)

        let! response =
            task {
                match resolution with
                | Error error ->
                    return Error error
                | Ok(authorizationCommand, resolvedTask) ->
                    let promptWithActivityState promptCommand request =
                        task {
                            activityId
                            |> Option.iter rt.Tui.MarkActivityAwaitingPermission
                            let! approval = permission.PromptPermission promptCommand request
                            activityId
                            |> Option.iter rt.Tui.MarkActivityRunning
                            return approval
                        }

                    let! authorization =
                        PermissionPolicy.authorizeWithTrust permission.TrustLevel promptWithActivityState authorizationCommand

                    match authorization, resolvedTask with
                    | Error error, _ ->
                        return Error error
                    | Ok(), Some(cmd, task) ->
                        let! result = (sourceOperation resolved
                            (fun project -> ClientShell.runProjectTaskIn project task)
                            (fun () -> ClientShell.runProjectTask cmd.ProjectName task)) rt |> serialize'
                        return result
                    | Ok(), None ->
                        let! result = dispatch rt target resolved command
                        return result
            }

        stopwatch.Stop()

        match response with
        | Ok payload ->
            let outcome = ActivityPresentation.tryResultSummary command payload.Payload
            activityId
            |> Option.iter (fun id ->
                rt.Tui.CompleteActivity(id, stopwatch.ElapsedMilliseconds, outcome))
        | Error err ->
            activityId
            |> Option.iter (fun id ->
                rt.Tui.FailActivity(id, stopwatch.ElapsedMilliseconds, ActivityPresentation.shortError err, ActivityPresentation.fullError err))

        audit command (response |> Result.map (fun outcome -> outcome.Payload))

        return response
    }

let receiveCommand (rt: Runtime) (command: AgentCommand) =
    task {
        let! result = receiveCommandForTarget rt None None None command
        return result |> Result.map (fun outcome -> outcome.Payload)
    }

let private receiveCommandWithTypedInstructions (rt: Runtime)
                                                (target: ProjectPaths.ProjectTarget option) command =
    task {
        match AgentInstructions.prepareOperationWithTarget rt target command with
        | Error error ->
            return Error error, [], AgentInstructions.RootBootstrapRequired
        | Ok prepared ->
            let! response =
                receiveCommandForTarget rt target prepared.Source prepared.ProjectTask command
            let provenance =
                if prepared.Source.IsSome then AgentInstructions.HierarchyLoaded
                else AgentInstructions.RootBootstrapRequired
            return response,
                   prepared.Instructions
                   @ AgentInstructions.writeWarningsForContext rt prepared.Source command response,
                   provenance
    }

let receiveCommandWithInstructions (rt: Runtime) command =
    task {
        match classifyProject command with
        | Error error -> return Error error, []
        | Ok scope ->
            let! response, instructions, _ =
                receiveCommandWithTypedInstructions rt (projectTarget scope) command
            return response |> Result.map (fun outcome -> outcome.Payload), instructions
    }

// Shared by SignalR receive and tests. Identity is supplied by the MCP call,
// not inferred from provider conversation metadata.
let processIncomingCommand (rt: Runtime) (commandJson: string) (agentId: string) =
    task {
        let mutable parsedCommand = None
        let mutable instructions: InstructionDocument list = []
        let mutable implicitChanges: string list = []
        let mutable inboxActivation: AgentInboxActivation option = None
        let! response =
            task {
                try
                    let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
                    parsedCommand <- Some command
                    // A self-inbox response must explicitly distinguish a
                    // repeated read from a newly committed activation, even
                    // when the subsequent inbox read fails.
                    match command with
                    | TaskReadCommand inbox when inbox.Action = "AgentInbox" ->
                        inboxActivation <-
                            Some { ProjectName = inbox.ProjectName; AgentId = inbox.AgentId
                                   ActivatedByThisCall = false
                                   BeforeState = None; AfterState = None
                                   CommittedHead = None; OperationId = None }
                    | _ -> ()
                    // A target field is never proof of acting-agent identity.
                    // Reject forged observer identity before source resolution,
                    // any activity attribution, or the canonical read.
                    let project =
                        match command with
                        | ObserveAgentCommand observation
                            when String.IsNullOrWhiteSpace agentId
                                 || not (String.Equals(observation.ObserverAgentId, agentId, StringComparison.Ordinal)) ->
                            Error(Client.ValidationError "Observation actor label does not match the initiating tool-call actor.")
                        | _ -> classifyProject command
                    match project with
                    | Error error -> return Error error
                    | Ok scope ->
                        let target = projectTarget scope
                        // The caller's agentId belongs to the source project
                        // of a cross-project notification, not the destination.
                        let localAgentId =
                            if isLocalAgentActivity command then agentId else ""
                        if not (String.IsNullOrWhiteSpace localAgentId) then
                            target |> Option.iter (fun selected ->
                                rt.Tui.RecordAgentSeen(AgentView.activityProject selected, localAgentId))
                        match activateReadyOnActivity rt localAgentId target command with
                        | Error error -> return Error error
                        | Ok (changes, committedActivation) ->
                            implicitChanges <- changes
                            match command, committedActivation with
                            | TaskReadCommand inbox, Some receipt when inbox.Action = "AgentInbox" ->
                                inboxActivation <- Some receipt
                            | _ -> ()
                            let work () = receiveCommandWithTypedInstructions rt target command
                            // The lease is renewed during long-running commands.
                            // The canonical record (not local TUI last-seen)
                            // determines whether administrative correction may run.
                            let leaseContext, verifiedUnassigned =
                                match target with
                                | Some selected when not (String.IsNullOrWhiteSpace localAgentId)
                                                     && activatesAgent localAgentId command ->
                                    let projectId = ProjectPaths.ProjectTarget.projectId selected
                                    let project = ProjectPaths.ProjectId.value projectId
                                    let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
                                        WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
                                    match cache.Get(DateTimeOffset.UtcNow, projectId, localAgentId,
                                                    fun () -> loadAgentState rt project localAgentId) with
                                    | Some active when active.State = "Working"
                                                       && not (isNull (box active.CurrentAssignment)) ->
                                        Some (project, projectId, active.CurrentAssignment), false
                                    | Some inactive when isNull (box inactive.CurrentAssignment)
                                                         && List.contains inactive.State [ "Parked"; "NeedsWork" ] ->
                                        // A successfully decoded canonical unassigned agent
                                        // can read their inbox without inventing Working activity.
                                        None, true
                                    | _ -> None, false
                                | _ -> None, false
                            // A failed cache refresh must not silently
                            // turn a bound Working-agent call into an
                            // unreserved operation. Preserve source-only
                            // behavior for checkouts with no approved ledger.
                            let expectedFence =
                                match target with
                                | Some selected when not (String.IsNullOrWhiteSpace localAgentId)
                                                     && activatesAgent localAgentId command ->
                                    let project = selected |> ProjectPaths.ProjectTarget.projectId
                                                           |> ProjectPaths.ProjectId.value
                                    hasCanonicalLedgerBinding rt project
                                | _ -> false
                            let! (execution, renewalFailure) =
                                match leaseContext with
                                | None when expectedFence && not verifiedUnassigned ->
                                    task {
                                        return (Error(Client.ValidationError(
                                            "Canonical Working activity state unavailable; the call was not executed.")),
                                            [], AgentInstructions.RootBootstrapRequired), None
                                    }
                                | None ->
                                    task {
                                        let! outcome = work()
                                        return outcome, None
                                    }
                                | Some (project, projectId, assignment)
                                    when requiresDurableActivityReservation localAgentId command ->
                                    // One canonical admission is reused throughout a
                                    // continuous local burst. Concurrent calls share the
                                    // same reservation, but can never release it while
                                    // any source operation is still executing. The remote
                                    // correction veto is not time-limited.
                                    task {
                                        let manager =
                                            inFlightManagers.GetValue(rt, fun _ ->
                                                CoalescedReservationManager(
                                                    TimeSpan.FromSeconds(30.), rt.Tui.Log))
                                        let key =
                                            (ProjectPaths.ProjectId.value projectId)
                                            + "::" + localAgentId.ToUpperInvariant()
                                        let! admitted =
                                            manager.Run(key, assignment.TaskId, assignment.AttemptId,
                                                (fun reservationId beginOperationId ->
                                                    match OrchestrationApi.LedgerBindingConfig.load project rt with
                                                    | Error error -> Error(sprintf "Binding: %A" error)
                                                    | Ok binding ->
                                                        match ReviewWorkflow.ReviewWorkflow.beginInFlightAgentOperation
                                                                  binding beginOperationId localAgentId
                                                                  assignment.TaskId assignment.AttemptId reservationId with
                                                        | Error error -> Error(sprintf "Admission: %A" error)
                                                        | Ok _ ->
                                                            Ok (fun id releaseOperationId ->
                                                                match ReviewWorkflow.ReviewWorkflow.endInFlightAgentOperation
                                                                          binding releaseOperationId localAgentId id with
                                                                | Ok _ -> Ok ()
                                                                | Error error -> Error(sprintf "%A" error))),
                                                work)
                                        match admitted with
                                        | Ok outcome -> return outcome, None
                                        | Error failure ->
                                            return (Error(Client.ValidationError(
                                                $"In-flight Working admission refused: {failure}")), [],
                                                AgentInstructions.RootBootstrapRequired), None
                                    }
                                | Some (project, projectId, assignment) ->
                                    withActivityLeaseHeartbeat (TimeSpan.FromMinutes(2.))
                                        (fun () ->
                                            match OrchestrationApi.LedgerBindingConfig.load project rt with
                                            | Error error -> Error(sprintf "Lease binding unavailable: %A" error)
                                            | Ok binding ->
                                                match ReviewWorkflow.ReviewWorkflow.recordWorkingActivity
                                                          binding (Guid.NewGuid()) localAgentId
                                                          assignment.TaskId assignment.AttemptId with
                                                | Ok _ ->
                                                    let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
                                                        WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
                                                    cache.Invalidate(projectId, localAgentId)
                                                    Ok ()
                                                | Error error -> Error(sprintf "Lease renewal rejected: %A" error))
                                        work
                            let returned, applicable, provenance = execution
                            // Never replace a real committed command receipt with a
                            // fabricated generic error after activity bookkeeping
                            // fails. Surface liveness uncertainty separately.
                            let result = returned
                            // A write may alter another agent's state, or none
                            // at all. Invalidate precisely the affected local
                            // cache entries before preparing turn guidance.
                            match target, result with
                            | Some selected, Ok outcome when not outcome.ChangedAgentIds.IsEmpty ->
                                let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
                                    WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
                                let projectId = ProjectPaths.ProjectTarget.projectId selected
                                for changed in outcome.ChangedAgentIds do
                                    cache.Invalidate(projectId, changed)
                            | _ -> ()
                            instructions <-
                                match command with
                                | GetAgentInstructionsCommand _ ->
                                    // The explicit result includes the complete hierarchy.
                                    // Do not double-inject automatic AGENTS.md payloads.
                                    checkWorkingGuidance rt localAgentId target command
                                | _ ->
                                    incrementalInstructions rt localAgentId target provenance applicable
                                    @ checkWorkingGuidance rt localAgentId target command
                            match renewalFailure with
                            | Some warning ->
                                rt.Tui.Log warning
                                instructions <-
                                    instructions @
                                    [ { Scope = "activity-fence"; Path = "jarvis/activity-fence"
                                        Sha256 = ""; Content = warning } ]
                            | None -> ()
                            return result
                with ex ->
                    return Error(ExceptionError ex)
            }
        // The metadata is independent of the requested command outcome. A
        // Ready->Working transition stays committed if the following file read
        // fails, so other devices must still expire that agent's cache.
        let changes =
            match parsedCommand |> Option.bind AgentCommandInfo.projectName with
            | Some project when not (String.IsNullOrWhiteSpace project) ->
                let executed =
                    match response with
                    | Ok outcome -> outcome.ChangedAgentIds
                    | Error _ -> []
                implicitChanges @ executed
                |> List.distinct
                |> List.map (fun id -> { ProjectName = project; AgentId = id })
            | _ -> []
        let agentResponse =
            match parsedCommand with
            | Some command -> toAgentResponse command instructions changes inboxActivation response
            | None -> toAgentResponse ListCommandsCommand [] changes inboxActivation response
        return parsedCommand, (response |> Result.map (fun outcome -> outcome.Payload)), agentResponse
    }

let receiveCommandAndReply (connection: HubConnection) (rt: Runtime) (correlationId: string) (commandJson: string) (agentId: string) : Task =
    task {
        let! parsedCommand, response, agentResponse = processIncomingCommand rt commandJson agentId
        let! sendResult =
            ResponseDelivery.sendWithReconnectRetry
                (TimeSpan.FromMinutes 2.0)
                (TimeSpan.FromMilliseconds 250.0)
                (fun () -> connection.State)
                (fun () -> connection.invokeAsync("SendClientResponse", correlationId, agentResponse))

        match sendResult with
        | Ok() -> ()
        | Error ex ->
            rt.Tui.Log $"Failed to deliver command response after reconnect retries: {ex.Message}"

        match sendResult, parsedCommand, response with
        | Ok(), Some UpdateClientCommand, Ok _ ->
            rt.Tui.Log "Update verified and installed; restarting Jarvis in this terminal."
            match SelfUpdate.activatePending () with
            | Ok _ -> ()
            | Error error -> rt.Tui.Log $"Self-update activation failed: {EffectError.toString error}"
        | _ -> ()
    }
