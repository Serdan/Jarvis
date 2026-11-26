module Client.JobManager

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text
open Common
open Client.Effect
open Client.IO
open FsToolkit.ErrorHandling
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

module private Core =
    module Durable = BoundedProcess.DurableJobs

    let private defaultMaxOutputBytes = 64 * 1024
    let private maxCompletedJobs = 100
    let private maxActiveJobs = 64
    let private retention = TimeSpan.FromDays 7.
    let private startupGrace = TimeSpan.FromMinutes 1.
    let private startupWait = TimeSpan.FromSeconds 15.
    let private deniedExecutables =
        set [ "bash"; "sh"; "zsh"; "fish"; "cmd"; "cmd.exe"; "powershell"; "powershell.exe"; "pwsh"; "pwsh.exe" ]

    let private validateExecutable executable =
        if String.IsNullOrWhiteSpace executable then
            Error(Client.ValidationError "Executable cannot be empty.")
        elif deniedExecutables.Contains(executable.Trim().ToLowerInvariant()) then
            Error(Client.PermissionDenied $"Shell executable is denied by default: {executable}")
        else
            Ok executable

    let private resolveWorkingDirectory (context: ProjectPaths.ResolvedProject) workingDirectory =
        match workingDirectory with
        | None -> fun _ -> Ok(ProjectPaths.ResolvedProject.sourceRoot context)
        | Some relativePath ->
            ProjectPaths.resolveSourcePath context relativePath >>= FileIO.parseFolder

    let private rootForRuntime (rt: #ProjectIO) =
        let (ProjectDirectory workspace) = rt.Project.Root
        Durable.root workspace

    let private unknownMessage =
        "Engineering job outcome is unknown: no authoritative terminal receipt and no verified live supervisor. Do not rerun to reconstruct evidence."

    let private statusAndReceipt = function
        | Durable.Live receipt | Durable.Terminal receipt -> receipt.Status, Some receipt
        | Durable.Unknown receipt -> JobStatus.RecoveryUnknown unknownMessage, receipt

    let private authorized (canonical: ProjectPaths.ProjectId option) (actor: string option) (request: Durable.Request) =
        if canonical |> Option.exists (fun id -> ProjectPaths.ProjectId.value id <> request.Project) then
            Error(Client.ValidationError "jobId does not belong to the requested project.")
        elif actor.IsSome && (canonical.IsNone || actor <> request.Actor) then
            Error(Client.ValidationError "Engineering job belongs to another initiating agent or project.")
        else Ok()

    let private pending now (request: Durable.Request) = now - request.StartedAt < startupGrace

    let private prune root =
        // Admission is serialized across client hosts; the supervisor has its
        // own lifetime claim. A concurrent observation cannot create a new job.
        let now = DateTimeOffset.UtcNow
        let entries =
            Durable.entries root
            |> Array.map (fun (directory, request) -> directory, request, Durable.observe directory request)
        let active =
            entries
            |> Array.sumBy (fun (directory, request, observation) ->
                match observation with
                | Durable.Live _ -> 1
                | Durable.Unknown _ when pending now request || Durable.hasClaimOwner directory -> 1
                | _ -> 0)
        entries
        |> Array.choose (fun (directory, request, observation) ->
            match observation with
            | Durable.Live _ -> None
            | Durable.Unknown _ when pending now request || Durable.hasClaimOwner directory -> None
            | Durable.Terminal receipt ->
                Some(directory, receipt.CompletedAt |> Option.defaultValue request.StartedAt)
            | Durable.Unknown _ -> Some(directory, request.StartedAt))
        |> Array.sortByDescending snd
        |> Array.iteri (fun index (directory, completed) ->
            if index >= maxCompletedJobs || now - completed >= retention then
                Durable.tryPrune directory |> ignore)
        active

    let private startProcess root projectName actor workingDirectory executable args reason maxOutputBytes : Client.Result<StartJobResult> =
        try
            match validateExecutable executable with
            | Error error -> Error error
            | Ok executable ->
                let request: Durable.Request =
                    { Schema = 1
                      JobId = Guid.NewGuid().ToString("N")
                      Project = ProjectPaths.ProjectId.value projectName
                      Actor = actor
                      Executable = executable
                      Args = args
                      Reason = reason
                      WorkingDirectory = workingDirectory
                      StartedAt = DateTimeOffset.UtcNow
                      MaxOutputBytes = maxOutputBytes }
                let launch =
                    Durable.withAdmission root (fun () ->
                        if prune root >= maxActiveJobs then
                            Error(Client.ContextError "Engineering job capacity reached (64 active supervisors).")
                        else
                            let directory = Durable.create root request
                            try
                                let host = Environment.ProcessPath
                                if String.IsNullOrWhiteSpace host then invalidOp "Cannot locate the Jarvis process host."
                                let assembly = typeof<Durable.Request>.Assembly.Location
                                let exe, hostArgs =
                                    BoundedProcess.helperHostInvocation host assembly
                                        [ BoundedProcess.groupHostFlag; Durable.mode; directory ]
                                let info = ProcessStartInfo(exe)
                                info.WorkingDirectory <- workingDirectory
                                info.UseShellExecute <- false
                                info.CreateNoWindow <- true
                                ProcessEnvironment.apply info
                                hostArgs |> List.iter info.ArgumentList.Add
                                use supervisor = new Process(StartInfo = info)
                                if not (supervisor.Start()) then invalidOp "Could not start engineering job supervisor."
                                Ok directory
                            with ex ->
                                // No returned supervisor identity: do not
                                // authorize a second spawn from this request.
                                // Retain the original registration on ambiguity.
                                Error(Client.ContextError $"Unable to launch engineering job supervisor (job {request.JobId}): {ex.Message}"))
                match launch with
                | Error error -> Error error
                | Ok directory ->
                    let deadline = Stopwatch.StartNew()
                    let rec awaitStart () =
                        match Durable.observe directory request with
                        | Durable.Terminal receipt ->
                            match receipt.Status with
                            | FailedToStart message ->
                                // Preserve existing no-ghost behavior for a
                                // known failed start. No command was executed.
                                Durable.withAdmission root (fun () ->
                                    let cleanupDeadline = Stopwatch.StartNew()
                                    let mutable removed = Durable.tryPrune directory
                                    while not removed && cleanupDeadline.Elapsed < TimeSpan.FromSeconds 2. do
                                        Thread.Sleep 25
                                        removed <- Durable.tryPrune directory)
                                Error(Client.ContextError message)
                            | _ -> Ok { JobId = request.JobId; StartedAt = request.StartedAt }
                        | Durable.Live receipt when receipt.Started ->
                            Ok { JobId = request.JobId; StartedAt = request.StartedAt }
                        | _ when deadline.Elapsed >= startupWait ->
                            // A lost startup receipt is not proof of non-start.
                            // Return the stable handle instead of encouraging replay.
                            Ok { JobId = request.JobId; StartedAt = request.StartedAt }
                        | _ ->
                            Thread.Sleep 25
                            awaitStart ()
                    awaitStart ()
        with ex -> Error(ExceptionError ex)

    let startJobForContext actor (context: ProjectPaths.ResolvedProject) (cmd: StartJobCommand) =
        effect {
            let maxOutputBytes = cmd.MaxOutputBytes |> Option.defaultValue defaultMaxOutputBytes
            if maxOutputBytes <= 0 || maxOutputBytes > Durable.maxOutputBytes then
                return! Client.ValidationError $"MaxOutputBytes must be between 1 and {Durable.maxOutputBytes}." |> Effect.ofError
            else
                let canonical =
                    context |> ProjectPaths.ResolvedProject.target |> ProjectPaths.ProjectTarget.projectId
                let! (FolderPath workingDirectory) = resolveWorkingDirectory context cmd.WorkingDirectory
                let! result = fun rt ->
                    try startProcess (rootForRuntime rt) canonical actor workingDirectory cmd.Executable cmd.Args cmd.Reason maxOutputBytes
                    with ex -> Error(ExceptionError ex)
                return result
        }

    let startJobForTarget actor (target: ProjectPaths.ProjectTarget) (cmd: StartJobCommand) =
        effect {
            let! context = ProjectPaths.resolveProjectContextForTarget target
            return! startJobForContext actor context cmd
        }

    let private toSummary (request: Durable.Request) observation : JobSummary =
        let status, receipt = statusAndReceipt observation
        { JobId = request.JobId
          ProjectName = request.Project
          Executable = request.Executable
          Args = request.Args
          Reason = request.Reason
          WorkingDirectory = Some request.WorkingDirectory
          Kind = JobKind.Process
          CommandName = None
          OperationId = None
          AgentId = request.Actor
          Status = status
          StartedAt = request.StartedAt
          CompletedAt =
            match observation with
            | Durable.Terminal value -> value.CompletedAt
            | _ -> None }

    let listJobsForProject canonical actor (cmd: ListJobsCommand) =
        fun rt ->
            try
                let root = rootForRuntime rt
                Durable.withAdmission root (fun () -> prune root |> ignore)
                Durable.entries root
                |> Array.choose (fun (directory, request) ->
                    let projectMatches = canonical |> Option.forall (fun id -> ProjectPaths.ProjectId.value id = request.Project)
                    let actorMatches = actor |> Option.forall (fun id -> request.Actor = Some id)
                    if not (projectMatches && actorMatches) then None
                    else
                        let summary = toSummary request (Durable.observe directory request)
                        if cmd.IncludeCompleted || summary.Status = Running then Some summary else None)
                |> Array.sortBy (fun job -> job.StartedAt)
                |> Array.toList
                |> fun items -> Ok { Jobs = items }
            with ex -> Error(ExceptionError ex)

    let private withJob rt canonical actor id action =
        try
            let root = rootForRuntime rt
            if not (Durable.validId id) then Error(NotFoundError $"Unknown job id: {id}")
            else
                let directory = Durable.jobDirectory root id
                if not (Directory.Exists directory) then Error(NotFoundError $"Unknown job id: {id}")
                else
                    let request = Durable.readRequest directory
                    match authorized canonical actor request with
                    | Error error -> Error error
                    | Ok () -> action directory request
        with ex -> Error(ExceptionError ex)

    let getJobResultForProject canonical actor (cmd: GetJobResultCommand) =
        fun rt ->
            withJob rt canonical actor cmd.JobId (fun directory request ->
                let observation = Durable.observe directory request
                let status, receipt = statusAndReceipt observation
                let afterSequence = defaultArg cmd.AfterSequence 0L |> max 0L
                let events =
                    receipt |> Option.map (fun value -> value.Events |> List.filter (fun event -> event.Sequence > afterSequence))
                    |> Option.defaultValue []
                Ok
                    { JobId = request.JobId
                      Status = status
                      Events = events
                      NextSequence = receipt |> Option.map (fun value -> value.NextSequence) |> Option.defaultValue 0L
                      Truncated =
                        match observation with
                        | Durable.Unknown _ -> true
                        | _ -> receipt |> Option.exists (fun value -> value.Truncated)
                      ToolResponse = None
                      OperationId = None })

    let cancelJobForProject canonical actor (cmd: CancelJobCommand) =
        fun rt ->
            withJob rt canonical actor cmd.JobId (fun directory request ->
                match Durable.observe directory request with
                | Durable.Terminal _ -> Ok()
                | Durable.Unknown _ -> Error(Client.ContextError unknownMessage)
                | Durable.Live _ ->
                    // The supervisor owns the original child handle. Never
                    // signal a recovered bare PID from the client process.
                    Durable.requestCancel directory
                    let deadline = Stopwatch.StartNew()
                    let rec awaitCancel () =
                        match Durable.observe directory request with
                        | Durable.Terminal _ -> Ok()
                        | Durable.Unknown _ -> Error(Client.ContextError unknownMessage)
                        | Durable.Live receipt when receipt.CancelError.IsSome ->
                            Error(Client.ContextError (Option.get receipt.CancelError))
                        | Durable.Live _ when deadline.Elapsed >= startupWait ->
                            Error(Client.ContextError "Cancellation requested; terminal receipt not yet available. Poll the original job.")
                        | Durable.Live _ ->
                            Thread.Sleep 50
                            awaitCancel ()
                    awaitCancel ())

// Tool operations are intentionally separate from OS processes. Admission is
// bounded before scheduling; the original task owns the one canonical result.
type private ToolOperationRecord =
    { Handle: ToolJobHandle
      CanonicalProject: ProjectPaths.ProjectId option
      CommandHash: string
      OperationKey: string option
      Completion: TaskCompletionSource<AgentCommandResponse>
      PreserveEvidence: AgentCommandResponse -> AgentCommandResponse
      mutable Status: JobStatus
      mutable StartedExecution: bool
      mutable Counted: bool
      mutable Exposed: bool
      mutable CompletedAt: DateTimeOffset option }

module private ToolOperations =
    let private gate = obj()
    let private records = ConcurrentDictionary<string, ToolOperationRecord>(StringComparer.Ordinal)
    let private expired = ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal)
    // Stable original operation IDs prevent duplicate execution on retry or
    // after losing the first response. Keep a bounded tombstone after eviction.
    let private byOperation =
        System.Collections.Generic.Dictionary<string, struct (string * string * DateTimeOffset)>(StringComparer.Ordinal)
    let private workerSlots = new SemaphoreSlim(8, 8)
    let private maxInFlight = 32
    let private maxRetained = 100
    let private terminalAge = TimeSpan.FromMinutes(30.)
    let mutable private inFlight = 0

    let private prune now =
        // Caller holds gate. Running operations are never evicted.
        let completed =
            records.Values
            |> Seq.filter (fun entry -> entry.Status <> Running)
            |> Seq.sortByDescending (fun entry -> entry.CompletedAt |> Option.defaultValue entry.Handle.StartedAt)
            |> Seq.toArray
        completed
        |> Array.iteri (fun index entry ->
            if index >= maxRetained
               || (entry.CompletedAt |> Option.exists (fun time -> now - time >= terminalAge)) then
                match records.TryRemove entry.Handle.JobId with
                | true, _ -> expired[entry.Handle.JobId] <- now
                | _ -> ())
        for KeyValue(id, whenExpired) in expired do
            if now - whenExpired > terminalAge then
                let mutable value = Unchecked.defaultof<DateTimeOffset>
                expired.TryRemove(id, &value) |> ignore
        // Preserve deduplication for retained jobs and a short bounded
        // reconciliation window after result eviction.
        for KeyValue(key, struct (jobId, _, admitted)) in byOperation |> Seq.toArray do
            if not (records.ContainsKey jobId)
               && (now - admitted >= terminalAge || byOperation.Count > 512) then
                byOperation.Remove(key) |> ignore
        // Expiry markers are diagnostic, never a second unbounded registry.
        if expired.Count > 200 then
            expired |> Seq.sortBy (fun entry -> entry.Value)
            |> Seq.truncate (expired.Count - 200)
            |> Seq.iter (fun entry ->
                let mutable ignored = Unchecked.defaultof<DateTimeOffset>
                expired.TryRemove(entry.Key, &ignored) |> ignore)

    let private failedResponse (message: string) : AgentCommandResponse =
        { Result = None
          Error = Some(ExecutionFailed message)
          Instructions = []
          StateChangeEvidence = StateChangeEvidence.Confirmed []
          InboxActivation = None
          PendingJob = None }

    // Once execution begins an exception cannot prove that a mutation did not
    // commit. Preserve the stable operation ID for reconciliation and retain
    // any independently confirmed source-side evidence as a known subset.
    let private uncertainResponse (entry: ToolOperationRecord) (message: string) : AgentCommandResponse =
        { Result = None
          Error = Some(UncertainOutcome
              { Message = message + " The outcome may have committed; reconcile the original operation ID before retrying."
                OperationId = entry.Handle.OperationId })
          Instructions = []
          // Completeness is independently unknown, never a confirmed zero.
          StateChangeEvidence = StateChangeEvidence.Unknown []
          InboxActivation = None
          PendingJob = None }

    let private complete (entry: ToolOperationRecord) (response: AgentCommandResponse) =
        lock gate (fun () ->
            // The only other terminal path is cancellation BEFORE execution.
            if entry.Status = Running then
                entry.Status <-
                    match response.Error with
                    | Some error -> ToolFailed(sprintf "%A" error)
                    | None -> ToolCompleted
                entry.CompletedAt <- Some DateTimeOffset.UtcNow
                entry.Completion.TrySetResult(response) |> ignore
            // Cancellation of a queued job may already have released its slot.
            if entry.Counted then
                entry.Counted <- false
                inFlight <- inFlight - 1
            prune DateTimeOffset.UtcNow)

    let private schedule (entry: ToolOperationRecord) (execute: unit -> Task<AgentCommandResponse>) =
        // At most 32 accepted work items, of which at most 8 can execute.
        // Task.Run is bounded by admission and keeps synchronous F# work out
        // of SignalR ingress; all faults are observed and become typed results.
        Task.Run(Func<Task>(fun () ->
            task {
                try
                    do! workerSlots.WaitAsync()
                    try
                        let shouldExecute =
                            lock gate (fun () ->
                                if entry.Status = Running then
                                    entry.StartedExecution <- true
                                    true
                                else false)
                        if shouldExecute then
                            let! response =
                                task {
                                    try return! execute ()
                                    with ex ->
                                        return entry.PreserveEvidence (
                                            uncertainResponse entry ("Tool operation failed after execution began: " + ex.Message))
                                }
                            complete entry response
                        else
                            // Queue cancellation released admission immediately.
                            lock gate (fun () -> prune DateTimeOffset.UtcNow)
                    finally
                        workerSlots.Release() |> ignore
                with ex ->
                    let started = lock gate (fun () -> entry.StartedExecution)
                    let response =
                        if started then
                            uncertainResponse entry ("Tool operation dispatch failed after execution began: " + ex.Message)
                        else
                            // Admission or semaphore failure before execute()
                            // cannot have performed this job's command.
                            failedResponse ("Tool operation dispatch failed before execution: " + ex.Message)
                    complete entry (entry.PreserveEvidence response)
            } :> Task))
        |> ignore

    let start canonical projectName actor commandName operationId commandHash execute preserveEvidence =
        lock gate (fun () ->
            prune DateTimeOffset.UtcNow
            let identity = canonical |> Option.map ProjectPaths.ProjectId.value |> Option.defaultValue "@unscoped"
            let key =
                operationId |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map (fun op -> identity + "\u001f" + actor + "\u001f" + op)
            let duplicate =
                match key with
                | None -> None
                | Some value ->
                    match byOperation.TryGetValue value with
                    | false, _ -> None
                    | true, struct (jobId, priorHash, _) when priorHash <> commandHash ->
                        Some(Error(Client.ValidationError
                            "An existing operationId was reused for different tool arguments."))
                    | true, struct (jobId, _, _) ->
                        match records.TryGetValue jobId with
                        | true, existing ->
                            Some(Ok(existing.Handle, existing.Completion.Task))
                        | false, _ ->
                            Some(Error(Client.NotFoundError
                                "The original tool job has expired. Reconcile its persisted operationId rather than rerunning it."))
            match duplicate with
            | Some result -> result
            | None when inFlight >= maxInFlight ->
                Error(Client.ContextError
                    "The client has reached its bounded tool-operation capacity (32 running/queued). Retry after an existing job finishes.")
            | None ->
                let id = Guid.NewGuid().ToString("N")
                let handle =
                    { JobId = id
                      Kind = JobKind.ToolOperation
                      Status = "Running"
                      ProjectName = projectName
                      DeviceId = None
                      AgentId = actor
                      OperationId = operationId
                      CommandName = commandName
                      StartedAt = DateTimeOffset.UtcNow }
                let entry =
                    { Handle = handle
                      CanonicalProject = canonical
                      CommandHash = commandHash
                      OperationKey = key
                      Completion = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
                      PreserveEvidence = preserveEvidence
                      Status = Running
                      StartedExecution = false
                      Counted = true
                      Exposed = false
                      CompletedAt = None }
                if not (records.TryAdd(id, entry)) then
                    Error(Client.ContextError "Unable to register client-owned tool operation.")
                else
                    inFlight <- inFlight + 1
                    key |> Option.iter (fun value ->
                        byOperation[value] <- struct (id, commandHash, handle.StartedAt))
                    schedule entry execute
                    Ok(handle, entry.Completion.Task))

    let promote jobId =
        lock gate (fun () ->
            match records.TryGetValue jobId with
            | true, entry -> entry.Exposed <- true
            | _ -> ())

    let forgetQuick jobId =
        lock gate (fun () ->
            match records.TryGetValue jobId with
            | true, entry when not entry.Exposed && entry.Status <> Running
                                && entry.OperationKey.IsNone ->
                let mutable ignored = Unchecked.defaultof<ToolOperationRecord>
                records.TryRemove(jobId, &ignored) |> ignore
            | _ -> ())

    let contains jobId = records.ContainsKey jobId

    let private authorize (entry: ToolOperationRecord) canonical agent =
        match agent with
        | Some actor when not (String.Equals(entry.Handle.AgentId, actor, StringComparison.Ordinal)) ->
            Error(Client.ValidationError "Tool job belongs to another initiating agent.")
        | _ ->
            // Local TUI access has no actor but remains confined to this client.
            if canonical <> entry.CanonicalProject && agent.IsSome then
                Error(Client.ValidationError "Tool job belongs to a different project; use its project selector.")
            else Ok ()

    let private toSummary (entry: ToolOperationRecord) =
        { JobId = entry.Handle.JobId
          ProjectName = entry.Handle.ProjectName |> Option.defaultValue ""
          Executable = ""
          Args = []
          Reason = None
          WorkingDirectory = None
          Kind = JobKind.ToolOperation
          CommandName = Some entry.Handle.CommandName
          OperationId = entry.Handle.OperationId
          AgentId = Some entry.Handle.AgentId
          Status = entry.Status
          StartedAt = entry.Handle.StartedAt
          CompletedAt = entry.CompletedAt }

    let list canonical actor includeCompleted =
        lock gate (fun () ->
            prune DateTimeOffset.UtcNow
            records.Values
            |> Seq.filter (fun entry ->
                entry.Exposed
                && (canonical |> Option.forall (fun requested -> Some requested = entry.CanonicalProject))
                && (actor |> Option.forall (fun id -> id = entry.Handle.AgentId))
                && (includeCompleted || entry.Status = Running))
            |> Seq.map toSummary
            |> Seq.toList)

    let get canonical actor jobId : Client.Result<JobResult> option =
        lock gate (fun () ->
            prune DateTimeOffset.UtcNow
            match records.TryGetValue jobId with
            | true, entry ->
                let result =
                    match authorize entry canonical actor with
                    | Error error -> Error error
                    | Ok () ->
                        let response =
                            if entry.Completion.Task.IsCompletedSuccessfully then
                                // The stored completion is immutable and can
                                // be polled repeatedly. Never expose one-shot
                                // alerts captured by an older background path
                                // as recurring job-result instructions.
                                let original = entry.Completion.Task.Result
                                Some { original with Instructions = OperationalControl.withoutOneShot original.OperationalControls }
                            else None
                        Ok
                            { JobId = jobId
                              Status = entry.Status
                              Events = []
                              NextSequence = 0L
                              Truncated = false
                              ToolResponse = response
                              OperationId = entry.Handle.OperationId }
                Some result
            | _ when expired.ContainsKey jobId ->
                Some(Error(Client.NotFoundError
                    "Tool job result expired or was evicted from client memory. Reconcile any committed write by its stable operationId; do not blindly rerun it."))
            | _ -> None)

    let cancel canonical actor jobId : Client.Result<unit> option =
        lock gate (fun () ->
            match records.TryGetValue jobId with
            | false, _ -> None
            | true, entry ->
                let response =
                    match authorize entry canonical actor with
                    | Error error -> Error error
                    | Ok () when entry.Status <> Running -> Ok ()
                    | Ok () when entry.StartedExecution ->
                        Error(Client.ValidationError
                            "Cannot cancel an executing tool operation: a ledger write may already have committed. Stop waiting instead.")
                    | Ok () ->
                        entry.Status <- Canceled
                        entry.CompletedAt <- Some DateTimeOffset.UtcNow
                        if entry.Counted then
                            entry.Counted <- false
                            inFlight <- inFlight - 1
                        entry.Completion.TrySetResult(entry.PreserveEvidence (failedResponse "Tool operation canceled before execution.")) |> ignore
                        prune DateTimeOffset.UtcNow
                        Ok ()
                Some response)

let startToolOperation canonical projectName actor commandName operationId commandHash execute =
    ToolOperations.start canonical projectName actor commandName operationId commandHash execute id

let startToolOperationWithCancelEvidence canonical projectName actor commandName operationId commandHash execute preserveEvidence =
    ToolOperations.start canonical projectName actor commandName operationId commandHash execute preserveEvidence

let promoteToolOperation jobId = ToolOperations.promote jobId
let forgetQuickToolOperation jobId = ToolOperations.forgetQuick jobId
let isToolOperationJob jobId = ToolOperations.contains jobId

let awaitToolOperation (handle: ToolJobHandle, completion: Task<AgentCommandResponse>) (threshold: TimeSpan) =
    task {
        let! winner = Task.WhenAny(completion :> Task, Task.Delay(threshold))
        if Object.ReferenceEquals(winner, completion :> Task) then
            let! response = completion
            forgetQuickToolOperation handle.JobId
            return Choice1Of2 response
        else
            promoteToolOperation handle.JobId
            return Choice2Of2 handle
    }


let private projectId raw =
    ProjectPaths.ProjectTarget.parse raw
    |> Result.map ProjectPaths.ProjectTarget.projectId

let private optionalProjectId raw =
    if String.IsNullOrWhiteSpace raw then Ok None
    else projectId raw |> Result.map Some

let startJobForTargetAsAgent (target: ProjectPaths.ProjectTarget) actor (cmd: StartJobCommand) =
    Core.startJobForTarget actor target cmd
let startJobInAsAgent (context: ProjectPaths.ResolvedProject) actor (cmd: StartJobCommand) =
    Core.startJobForContext actor context cmd
let startJobForTarget (target: ProjectPaths.ProjectTarget) (cmd: StartJobCommand) =
    startJobForTargetAsAgent target None cmd
let startJobIn (context: ProjectPaths.ResolvedProject) (cmd: StartJobCommand) =
    startJobInAsAgent context None cmd

let startJob (cmd: StartJobCommand) =
    effect {
        let! target = ProjectPaths.ProjectTarget.parse cmd.ProjectName |> Effect.ofResult
        return! Core.startJobForTarget None target cmd
    }

let listJobsForProjectAsAgent (canonical: ProjectPaths.ProjectId option) actor (cmd: ListJobsCommand) =
    fun rt ->
        match Core.listJobsForProject canonical actor cmd rt with
        | Error error -> Error error
        | Ok processJobs ->
            let toolJobs = ToolOperations.list canonical actor cmd.IncludeCompleted
            Ok { Jobs = processJobs.Jobs @ toolJobs }

let listJobsForProject (canonical: ProjectPaths.ProjectId option) (cmd: ListJobsCommand) =
    listJobsForProjectAsAgent canonical None cmd

let listJobs (cmd: ListJobsCommand) =
    fun rt ->
        let project =
            match cmd.ProjectName with
            | None -> Ok None
            | Some name -> projectId name |> Result.map Some
        match project with
        | Error error -> Error error
        | Ok canonical -> listJobsForProject canonical cmd rt

let getJobResultForProjectAsAgent (canonical: ProjectPaths.ProjectId option) actor (cmd: GetJobResultCommand) =
    fun rt ->
        match ToolOperations.get canonical actor cmd.JobId with
        | Some result -> result
        | None ->
            match Core.getJobResultForProject canonical actor cmd rt with
            | Error(NotFoundError _) ->
                Error(NotFoundError
                    $"Unknown job id: {cmd.JobId}. If this was a client-owned tool operation, the client may have restarted or evicted the job; reconcile a persisted write using its original operationId rather than blindly repeating it.")
            | result -> result

let getJobResultForProject (canonical: ProjectPaths.ProjectId option) (cmd: GetJobResultCommand) =
    getJobResultForProjectAsAgent canonical None cmd

let getJobResult (cmd: GetJobResultCommand) =
    fun rt ->
        match optionalProjectId cmd.ProjectName with
        | Error error -> Error error
        | Ok canonical -> getJobResultForProject canonical cmd rt

let cancelJobForProjectAsAgent (canonical: ProjectPaths.ProjectId option) actor (cmd: CancelJobCommand) =
    fun rt ->
        match ToolOperations.cancel canonical actor cmd.JobId with
        | Some result -> result
        | None -> Core.cancelJobForProject canonical actor cmd rt

let cancelJobForProject (canonical: ProjectPaths.ProjectId option) (cmd: CancelJobCommand) =
    cancelJobForProjectAsAgent canonical None cmd

let cancelJob (cmd: CancelJobCommand) =
    fun rt ->
        match optionalProjectId cmd.ProjectName with
        | Error error -> Error error
        | Ok canonical -> cancelJobForProject canonical cmd rt
