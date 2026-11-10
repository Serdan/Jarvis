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

type private JobRecord =
    { JobId: string
      ProjectName: ProjectPaths.ProjectId
      Executable: string
      Args: string list
      Reason: string option
      WorkingDirectory: string option
      StartedAt: DateTimeOffset
      Process: Process
      SyncRoot: obj
      Events: ResizeArray<JobOutputEvent>
      MaxOutputBytes: int
      mutable OutputBytes: int
      mutable OutputTruncated: bool
      mutable NextSequence: int64
      mutable CancellationRequested: bool
      mutable Status: JobStatus
      mutable CompletedAt: DateTimeOffset option }

module private Core =
    let private defaultMaxOutputBytes = 64 * 1024
    let private maxCompletedJobs = 100
    let private jobs = ConcurrentDictionary<string, JobRecord>()

    // A registered worktree selects where a job executes, not which logical
    // project owns its result. GetJobResult/CancelJob only receive projectName.
    let private validateJobProject (requested: ProjectPaths.ProjectId option) (job: JobRecord) =
        match requested with
        | None -> Ok ()
        | Some canonical when canonical = job.ProjectName -> Ok ()
        | Some _ -> Error(ValidationError "jobId does not belong to the requested project.")

    let private pruneCompletedJobs () =
        jobs.Values
        |> Seq.filter (fun job -> job.Status <> Running)
        |> Seq.sortByDescending (fun job -> job.StartedAt)
        |> Seq.indexed
        |> Seq.choose (fun (index, job) ->
            if index >= maxCompletedJobs then Some job else None)
        |> Seq.iter (fun job ->
            match jobs.TryRemove job.JobId with
            | true, removed ->
                try removed.Process.Dispose()
                with _ -> ()
            | false, _ -> ())

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

    let private takeUtf8Prefix maxBytes (value: string) =
        let output = StringBuilder()
        let mutable bytes = 0
        let mutable accepting = true

        for rune in value.EnumerateRunes() do
            if accepting then
                let runeBytes = rune.Utf8SequenceLength

                if bytes + runeBytes <= maxBytes then
                    output.Append(rune.ToString()) |> ignore
                    bytes <- bytes + runeBytes
                else
                    accepting <- false

        output.ToString(), bytes

    let private appendEvent (job: JobRecord) stream (data: string) =
        if not (isNull data) then
            lock job.SyncRoot (fun () ->
                job.NextSequence <- job.NextSequence + 1L
                let value = data + Environment.NewLine
                let remaining = max 0 (job.MaxOutputBytes - job.OutputBytes)
                let fragment, addedBytes = takeUtf8Prefix remaining value
                let valueBytes = Encoding.UTF8.GetByteCount value

                if addedBytes > 0 then
                    job.Events.Add
                        { Sequence = job.NextSequence
                          Stream = stream
                          Text = fragment }

                job.OutputBytes <- job.OutputBytes + addedBytes
                job.OutputTruncated <- job.OutputTruncated || addedBytes < valueBytes)

    let private startProcess projectName workingDirectory executable args reason maxOutputBytes : Client.Result<StartJobResult> =
        try
            match validateExecutable executable with
            | Error error -> Error error
            | Ok executable ->
                let jobId = Guid.NewGuid().ToString("N")
                let proc = new Process()

                proc.StartInfo.FileName <- executable
                proc.StartInfo.WorkingDirectory <- workingDirectory
                proc.StartInfo.RedirectStandardOutput <- true
                proc.StartInfo.RedirectStandardError <- true
                proc.StartInfo.UseShellExecute <- false
                proc.StartInfo.CreateNoWindow <- true
                ProcessEnvironment.apply proc.StartInfo
                proc.EnableRaisingEvents <- true
                args |> List.iter proc.StartInfo.ArgumentList.Add

                let job =
                    { JobId = jobId
                      ProjectName = projectName
                      Executable = executable
                      Args = args
                      Reason = reason
                      WorkingDirectory = Some workingDirectory
                      StartedAt = DateTimeOffset.UtcNow
                      Process = proc
                      SyncRoot = obj()
                      Events = ResizeArray()
                      MaxOutputBytes = maxOutputBytes
                      OutputBytes = 0
                      OutputTruncated = false
                      NextSequence = 0L
                      CancellationRequested = false
                      Status = Running
                      CompletedAt = None }

                proc.OutputDataReceived.Add(fun event ->
                    appendEvent job JobOutputStream.StdOut event.Data)

                proc.ErrorDataReceived.Add(fun event ->
                    appendEvent job JobOutputStream.StdErr event.Data)

                proc.Exited.Add(fun _ ->
                    try proc.WaitForExit()
                    with _ -> ()

                    lock job.SyncRoot (fun () ->
                        if job.CancellationRequested then
                            job.Status <- Canceled
                        else
                            try job.Status <- Completed proc.ExitCode
                            with _ -> job.Status <- FailedToStart "Process exited before an exit code was available."
                        job.CompletedAt <- Some DateTimeOffset.UtcNow)
                    pruneCompletedJobs())

                if not (jobs.TryAdd(jobId, job)) then
                    proc.Dispose()
                    Error(Client.ContextError "Failed to register job.")
                else
                    try
                        if not (proc.Start()) then
                            let mutable removed = Unchecked.defaultof<JobRecord>
                            jobs.TryRemove(jobId, &removed) |> ignore
                            proc.Dispose()
                            Error(Client.ContextError "Failed to start job.")
                        else
                            proc.BeginOutputReadLine()
                            proc.BeginErrorReadLine()
                            Ok { JobId = jobId; StartedAt = job.StartedAt }
                    with ex ->
                        let mutable removed = Unchecked.defaultof<JobRecord>
                        jobs.TryRemove(jobId, &removed) |> ignore
                        proc.Dispose()
                        Error(ExceptionError ex)
        with ex ->
            Error(ExceptionError ex)

    let startJobForContext (context: ProjectPaths.ResolvedProject) (cmd: StartJobCommand) =
        effect {
            let maxOutputBytes = cmd.MaxOutputBytes |> Option.defaultValue defaultMaxOutputBytes
            if maxOutputBytes <= 0 then
                return! Client.ValidationError "MaxOutputBytes must be greater than zero." |> Effect.ofError
            else
                let canonical =
                    context |> ProjectPaths.ResolvedProject.target |> ProjectPaths.ProjectTarget.projectId
                let! (FolderPath workingDirectory) = resolveWorkingDirectory context cmd.WorkingDirectory
                let! result = fun _ -> startProcess canonical workingDirectory cmd.Executable cmd.Args cmd.Reason maxOutputBytes
                return result
        }

    let startJobForTarget (target: ProjectPaths.ProjectTarget) (cmd: StartJobCommand) =
        effect {
            let! context = ProjectPaths.resolveProjectContextForTarget target
            return! startJobForContext context cmd
        }

    let private toSummary (job: JobRecord) =
        lock job.SyncRoot (fun () ->
            { JobId = job.JobId
              ProjectName = ProjectPaths.ProjectId.value job.ProjectName
              Executable = job.Executable
              Args = job.Args
              Reason = job.Reason
              WorkingDirectory = job.WorkingDirectory
              Kind = "Process"
              CommandName = None
              OperationId = None
              AgentId = None
              Status = job.Status
              StartedAt = job.StartedAt
              CompletedAt = job.CompletedAt })

    let listJobsForProject (canonical: ProjectPaths.ProjectId option) (cmd: ListJobsCommand) =
        fun _ ->
            jobs.Values
            |> Seq.filter (fun job -> canonical |> Option.forall ((=) job.ProjectName))
            |> Seq.filter (fun job -> cmd.IncludeCompleted || job.Status = Running)
            |> Seq.sortBy (fun job -> job.StartedAt)
            |> Seq.map toSummary
            |> Seq.toList
            |> fun items -> Ok { Jobs = items }

    let getJobResultForProject (canonical: ProjectPaths.ProjectId option) (cmd: GetJobResultCommand) =
        fun _ ->
            match jobs.TryGetValue cmd.JobId with
            | false, _ -> Error(NotFoundError $"Unknown job id: {cmd.JobId}")
            | true, job ->
                match validateJobProject canonical job with
                | Error error -> Error error
                | Ok () -> lock job.SyncRoot (fun () ->
                    let afterSequence = defaultArg cmd.AfterSequence 0L |> max 0L
                    let events =
                        job.Events
                        |> Seq.filter (fun event -> event.Sequence > afterSequence)
                        |> Seq.toList

                    Ok
                        { JobId = job.JobId
                          Status = job.Status
                          Events = events
                          NextSequence = job.NextSequence
                          Truncated = job.OutputTruncated
                          ToolResponse = None
                          OperationId = None })

    let cancelJobForProject (canonical: ProjectPaths.ProjectId option) (cmd: CancelJobCommand) =
        fun _ ->
            match jobs.TryGetValue cmd.JobId with
            | false, _ -> Error(NotFoundError $"Unknown job id: {cmd.JobId}")
            | true, job ->
                match validateJobProject canonical job with
                | Error error -> Error error
                | Ok () ->
                    if job.Status = Running then
                        job.CancellationRequested <- true
                        try
                            if not job.Process.HasExited then job.Process.Kill(entireProcessTree = true)
                            job.Status <- Canceled
                            job.CompletedAt <- Some DateTimeOffset.UtcNow
                            Ok()
                        with ex ->
                            job.CancellationRequested <- false
                            Error(ExceptionError ex)
                    else
                        Ok()

// Tool operations are intentionally separate from OS processes. Admission is
// bounded before scheduling; the original task owns the one canonical result.
type private ToolOperationRecord =
    { Handle: ToolJobHandle
      CanonicalProject: ProjectPaths.ProjectId option
      Completion: TaskCompletionSource<AgentCommandResponse>
      mutable Status: JobStatus
      mutable StartedExecution: bool
      mutable Exposed: bool
      mutable CompletedAt: DateTimeOffset option }

module private ToolOperations =
    let private gate = obj()
    let private records = ConcurrentDictionary<string, ToolOperationRecord>(StringComparer.Ordinal)
    let private expired = ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal)
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
          AgentStateChanges = []
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
                                    with ex -> return failedResponse ("Tool operation failed: " + ex.Message)
                                }
                            complete entry response
                        else
                            lock gate (fun () ->
                                inFlight <- inFlight - 1
                                prune DateTimeOffset.UtcNow)
                    finally
                        workerSlots.Release() |> ignore
                with ex ->
                    complete entry (failedResponse ("Tool operation dispatch failed: " + ex.Message))
            } :> Task))
        |> ignore

    let start canonical projectName actor commandName operationId execute =
        lock gate (fun () ->
            prune DateTimeOffset.UtcNow
            if inFlight >= maxInFlight then
                Error(Client.ContextError
                    "The client has reached its bounded tool-operation capacity (32 running/queued). Retry after an existing job finishes.")
            else
                let id = Guid.NewGuid().ToString("N")
                let handle =
                    { JobId = id
                      Kind = "ToolOperation"
                      Status = "Running"
                      ProjectName = projectName
                      AgentId = actor
                      OperationId = operationId
                      CommandName = commandName
                      StartedAt = DateTimeOffset.UtcNow }
                let entry =
                    { Handle = handle
                      CanonicalProject = canonical
                      Completion = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
                      Status = Running
                      StartedExecution = false
                      Exposed = false
                      CompletedAt = None }
                if not (records.TryAdd(id, entry)) then
                    Error(Client.ContextError "Unable to register client-owned tool operation.")
                else
                    inFlight <- inFlight + 1
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
            | true, entry when not entry.Exposed && entry.Status <> Running ->
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
          Kind = "ToolOperation"
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
                                Some entry.Completion.Task.Result
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
                        entry.Completion.TrySetResult(failedResponse "Tool operation canceled before execution.") |> ignore
                        Ok ()
                Some response)

let startToolOperation canonical projectName actor commandName operationId execute =
    ToolOperations.start canonical projectName actor commandName operationId execute

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

let startJobForTarget (target: ProjectPaths.ProjectTarget) (cmd: StartJobCommand) = Core.startJobForTarget target cmd
let startJobIn (context: ProjectPaths.ResolvedProject) (cmd: StartJobCommand) = Core.startJobForContext context cmd

let startJob (cmd: StartJobCommand) =
    effect {
        let! target = ProjectPaths.ProjectTarget.parse cmd.ProjectName |> Effect.ofResult
        return! Core.startJobForTarget target cmd
    }

let listJobsForProjectAsAgent (canonical: ProjectPaths.ProjectId option) actor (cmd: ListJobsCommand) =
    fun rt ->
        match Core.listJobsForProject canonical cmd rt with
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
        | None -> Core.getJobResultForProject canonical cmd rt

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
        | None -> Core.cancelJobForProject canonical cmd rt

let cancelJobForProject (canonical: ProjectPaths.ProjectId option) (cmd: CancelJobCommand) =
    cancelJobForProjectAsAgent canonical None cmd

let cancelJob (cmd: CancelJobCommand) =
    fun rt ->
        match optionalProjectId cmd.ProjectName with
        | Error error -> Error error
        | Ok canonical -> cancelJobForProject canonical cmd rt
