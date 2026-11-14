module Client.JobManager

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.IO.Pipes
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
      TerminateGroup: unit -> unit
      NativeScoped: bool
      LaunchGate: obj
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
        |> Seq.filter (fun job ->
            match job.Status with
            | Running | CleanupUnverified _ -> false
            | _ -> true)
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

    // A group supervisor is itself always an available executable. Resolve
    // the original program before returning a StartJob handle so an unknown
    // executable still fails synchronously rather than as a ghost job.
    let private executableAvailable (workingDirectory: string) (executable: string) =
        let env name fallback =
            match Environment.GetEnvironmentVariable name with
            | null -> fallback
            | value -> value
        let suffixes =
            if OperatingSystem.IsWindows() && String.IsNullOrEmpty(Path.GetExtension executable) then
                let extensions =
                    (env "PATHEXT" ".COM;.EXE;.BAT;.CMD").Split(
                        ';', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList
                "" :: extensions
            else [ "" ]
        let rooted = Path.IsPathRooted executable
        let hasDirectory =
            executable.IndexOfAny([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]) >= 0
        let searchRoots =
            if rooted then [ "" ]
            elif hasDirectory then [ workingDirectory ]
            else
                (env "PATH" "").Split(Path.PathSeparator, StringSplitOptions.None)
                |> Array.map (fun entry ->
                    if String.IsNullOrWhiteSpace entry then workingDirectory
                    elif Path.IsPathRooted entry then entry
                    else Path.Combine(workingDirectory, entry))
                |> Array.toList
        searchRoots
        |> List.exists (fun folder ->
            suffixes |> List.exists (fun suffix ->
                File.Exists(Path.Combine(folder, executable + suffix))))

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
            | Ok executable when not (executableAvailable workingDirectory executable) ->
                Error(Client.NotFoundError $"Executable not found: {executable}")
            | Ok executable ->
                let jobId = Guid.NewGuid().ToString("N")
                let proc = new Process()
                // StartJob must not launch into the Jarvis client's process
                // group: its leader may exit while descendants continue.
                // Reuse the same cross-platform containment used by bounded
                // commands. The Windows helper cannot execute before its
                // assignment to a kill-on-close Job Object is acknowledged.
                let windows = OperatingSystem.IsWindows()
                let supervisorGate = obj()
                let mutable windowsJob = 0n
                let mutable readiness: EventWaitHandle option = None
                let mutable nativeReadyPipe: NamedPipeServerStream option = None
                let mutable nativeScope: LinuxJobScope.Identity option = None
                let releaseSupervisor () =
                    lock supervisorGate (fun () ->
                        readiness |> Option.iter (fun ready ->
                            try ready.Dispose() with _ -> ())
                        readiness <- None
                        nativeReadyPipe |> Option.iter (fun ready ->
                            try ready.Dispose() with _ -> ())
                        nativeReadyPipe <- None
                        if windowsJob <> 0n then
                            BoundedProcess.Native.CloseHandle(windowsJob) |> ignore
                            windowsJob <- 0n)
                let terminateGroup () =
                    lock supervisorGate (fun () ->
                        if windows then
                            if windowsJob <> 0n then
                                BoundedProcess.Native.TerminateJobObject(windowsJob, 1u) |> ignore
                        elif OperatingSystem.IsLinux() && nativeScope.IsSome then
                            // cgroup.kill addresses the exact kernel scope rather
                            // than a potentially reused process-group number.
                            // This is cleanup, NOT an execution terminal receipt.
                            match LinuxJobScope.tryKill (Option.get nativeScope) with
                            | Ok () -> ()
                            | Error failure ->
                                // Do not fall back to killpg(proc.Id): the helper
                                // may already have exited, and its process-group
                                // number could have been reused by an unrelated
                                // program. Propagate the failure to CancelJob
                                // instead of returning a false cancellation ack.
                                raise (IOException(
                                    "Native StartJob scope termination is unverified: " + failure))
                        elif OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                            BoundedProcess.Native.killUnixGroup proc.Id |> ignore)
                // The launcher owns these handles until a successful StartJob
                // transfers them to the process-exit callback. Setup errors
                // must always close the Windows kill-on-close job, even before
                // Process.Start and before registration in the jobs dictionary.
                let mutable transferred = false
                use launchGuard =
                    { new IDisposable with
                        member _.Dispose() =
                            if not transferred then
                                releaseSupervisor()
                                try proc.Dispose() with _ -> () }
                // This first append-only record is durably flushed BEFORE
                // Process.Start. A crash during spawn leaves an unresolved
                // intent rather than an invisible native process. The second
                // record pins PID, boot and birth evidence before the
                // StartJob handle can be returned. Neither is terminal proof.
                let ownershipDirectory = ProcessOwnershipJournal.defaultDirectory()
                let launchIntent =
                    ProcessOwnershipJournal.beginLaunchAt ownershipDirectory jobId
                        (ProjectPaths.ProjectId.value projectName)
                        (DeviceIdentity.getOrCreate())
                nativeScope <- LinuxJobScope.create jobId
                nativeScope |> Option.iter (fun scope ->
                    ProcessOwnershipJournal.recordNativeScopeAt
                        ownershipDirectory launchIntent scope |> ignore)
                let readyName =
                    if windows then
                        Some($"Local\\Jarvis.StartJob.{Guid.NewGuid():N}")
                    elif nativeScope.IsSome then
                        Some($"jarvis-startjob-cgroup-{jobId}")
                    else None
                let hostedExecutable, hostedArgs =
                    try
                        if windows then
                            windowsJob <- BoundedProcess.Native.newWindowsJob()
                            readiness <- Some(new EventWaitHandle(
                                false, EventResetMode.ManualReset, Option.get readyName))
                        elif nativeScope.IsSome then
                            nativeReadyPipe <- Some(new NamedPipeServerStream(
                                Option.get readyName, PipeDirection.Out, 1,
                                PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                        BoundedProcess.groupProcessInvocation executable args readyName
                    with _ ->
                        releaseSupervisor()
                        proc.Dispose()
                        reraise()

                proc.StartInfo.FileName <- hostedExecutable
                proc.StartInfo.WorkingDirectory <- workingDirectory
                proc.StartInfo.RedirectStandardOutput <- true
                proc.StartInfo.RedirectStandardError <- true
                proc.StartInfo.UseShellExecute <- false
                proc.StartInfo.CreateNoWindow <- true
                ProcessEnvironment.apply proc.StartInfo
                proc.EnableRaisingEvents <- true
                hostedArgs |> List.iter proc.StartInfo.ArgumentList.Add

                let job =
                    { JobId = jobId
                      ProjectName = projectName
                      Executable = executable
                      Args = args
                      Reason = reason
                      WorkingDirectory = Some workingDirectory
                      StartedAt = DateTimeOffset.UtcNow
                      Process = proc
                      TerminateGroup = terminateGroup
                      NativeScoped = nativeScope.IsSome
                      LaunchGate = obj()
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

                let mutable completedHandled = false
                let completeObservedExit () =
                    if transferred && not completedHandled then
                        completedHandled <- true
                        // A native scope cleanup failure is a retryable,
                        // visible state, NOT Completed/Canceled. An escaped or
                        // unreaped descendant may still hold stdout/stderr
                        // open, so never call unbounded WaitForExit while
                        // holding LaunchGate for an opted-in native scope.
                        let cleanupError =
                            if job.NativeScoped then
                                try
                                    terminateGroup()
                                    None
                                with ex ->
                                    Some($"Native scope termination unverified: {ex.Message}")
                            else
                                try terminateGroup() with _ -> ()
                                try proc.WaitForExit() with _ -> ()
                                None
                        lock job.SyncRoot (fun () ->
                            match cleanupError with
                            | Some issue -> job.Status <- CleanupUnverified issue
                            | None ->
                                if job.CancellationRequested then
                                    job.Status <- Canceled
                                else
                                    try job.Status <- Completed proc.ExitCode
                                    with _ ->
                                        job.Status <- FailedToStart
                                            "Process exited before an exit code was available."
                            job.CompletedAt <- Some DateTimeOffset.UtcNow)
                        releaseSupervisor()
                        pruneCompletedJobs()

                proc.Exited.Add(fun _ ->
                    // Exited can be raised before launch handoff completes.
                    // The launcher also checks HasExited after transferring
                    // ownership, so an early one-shot event is not lost.
                    lock job.LaunchGate completeObservedExit)

                if not (jobs.TryAdd(jobId, job)) then
                    releaseSupervisor()
                    proc.Dispose()
                    Error(Client.ContextError "Failed to register job.")
                else
                    // A job is inserted before Start to receive asynchronous
                    // output. Hold its launch gate through OS assignment,
                    // stream setup and ownership transfer. Both cancellation
                    // and Exited wait for this handoff instead of racing it.
                    lock job.LaunchGate (fun () ->
                        try
                            if not (proc.Start()) then
                                let mutable removed = Unchecked.defaultof<JobRecord>
                                jobs.TryRemove(jobId, &removed) |> ignore
                                Error(Client.ContextError "Failed to start job.")
                            else
                                if windows then
                                    if not (BoundedProcess.Native.AssignProcessToJobObject(
                                        windowsJob, proc.Handle)) then
                                        raise (System.ComponentModel.Win32Exception(
                                            System.Runtime.InteropServices.Marshal.GetLastWin32Error()))
                                nativeScope |> Option.iter (fun scope ->
                                    // An arbitrary executable cannot run before the
                                    // managed Linux helper has entered this cgroup.
                                    let gate = Option.get nativeReadyPipe
                                    let connected =
                                        gate.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(20.))
                                    if not connected then
                                        raise (TimeoutException(
                                            "Linux StartJob native-scope helper did not connect."))
                                    LinuxJobScope.attachBeforeExec scope proc.Id)
                                ProcessOwnershipJournal.recordLaunchedAt
                                    ownershipDirectory launchIntent proc |> ignore
                                if windows then
                                    // All descendants inherit the assigned job.
                                    readiness |> Option.iter (fun ready -> ready.Set() |> ignore)
                                elif nativeScope.IsSome then
                                    // The immutable launch stage and kernel attach
                                    // must BOTH exist before allowing exec.
                                    let gate = Option.get nativeReadyPipe
                                    gate.WriteByte(1uy)
                                    gate.Flush()
                                proc.BeginOutputReadLine()
                                proc.BeginErrorReadLine()
                                transferred <- true
                                // If Exited fired during initialization, its
                                // callback could not finalize yet. Detect an
                                // already-dead leader and finalize exactly once.
                                if proc.HasExited then completeObservedExit()
                                Ok { JobId = jobId; StartedAt = job.StartedAt }
                        with ex ->
                            try
                                try terminateGroup() with _ -> ()
                                if not proc.HasExited then proc.Kill(entireProcessTree = true)
                            with _ -> ()
                            // Process.WaitForExit and Dispose can both enter
                            // the Exited callback while the Process monitor
                            // is held. Never wait/dispose while holding
                            // LaunchGate; launchGuard closes native resources
                            // AFTER this critical section has been exited.
                            let mutable removed = Unchecked.defaultof<JobRecord>
                            jobs.TryRemove(jobId, &removed) |> ignore
                            Error(ExceptionError ex))
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
              Kind = JobKind.Process
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
            |> Seq.filter (fun job ->
                cmd.IncludeCompleted ||
                match job.Status with
                | Running | CleanupUnverified _ -> true
                | _ -> false)
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
                    // Requesting cancellation is NOT a terminal process
                    // observation. Decide whether to cancel WHILE holding
                    // the launch/exit gate: the Exited callback must not
                    // complete a job and release its process-group identity
                    // between our status check and kill request.
                    lock job.LaunchGate (fun () ->
                        // Process.HasExited may synchronously invoke Exited,
                        // whose WaitForExit drains async output callbacks that
                        // require SyncRoot. Never query it under SyncRoot.
                        let alreadyExited = job.Process.HasExited
                        let shouldCancel, retryCleanup =
                            lock job.SyncRoot (fun () ->
                                match job.Status with
                                | Running when not alreadyExited ->
                                    job.CancellationRequested <- true
                                    true, false
                                | CleanupUnverified _ when job.NativeScoped ->
                                    // Retry the immutable native cgroup scope,
                                    // never the potentially reused leader PID.
                                    true, true
                                | _ -> false, false)
                        if not shouldCancel then Ok()
                        else
                            try
                                job.TerminateGroup()
                                if not job.NativeScoped && not alreadyExited
                                   && not job.Process.HasExited then
                                    // Native scopes already issued exact cgroup.kill.
                                    // Do not race a redundant PID/descendant-tree kill
                                    // against an asynchronously exited helper.
                                    job.Process.Kill(entireProcessTree = true)
                                if retryCleanup then
                                    lock job.SyncRoot (fun () ->
                                        if alreadyExited || job.Process.HasExited then
                                            if job.CancellationRequested then
                                                job.Status <- Canceled
                                            else
                                                try job.Status <- Completed job.Process.ExitCode
                                                with _ ->
                                                    job.Status <- FailedToStart
                                                        "Process exited without an available exit code."
                                            job.CompletedAt <- Some DateTimeOffset.UtcNow
                                        else
                                            // The Exited callback will finish
                                            // the parent; a kernel kill request
                                            // alone cannot prove its exit.
                                            job.Status <- Running)
                                Ok()
                            with ex ->
                                // Retain a queryable, retryable cleanup failure.
                                // A failed kernel kill is not a successful
                                // cancellation and must never fall back to
                                // signaling a historical process-group ID.
                                if job.NativeScoped then
                                    lock job.SyncRoot (fun () ->
                                        job.Status <- CleanupUnverified
                                            $"Native scope termination unverified: {ex.Message}"
                                        if alreadyExited then
                                            job.CompletedAt <- Some DateTimeOffset.UtcNow)
                                Error(ExceptionError ex))

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
          AgentStateChanges = []
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
          // The typed UncertainOutcome marks completeness UNKNOWN, never zero.
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
        | None ->
            match Core.getJobResultForProject canonical cmd rt with
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

/// Read-only diagnostic for a server-pinned StartJob UUID. The caller must
/// supply the logical project, and the record must belong to this device.
/// No PID observation authorizes termination or correction.
let getProcessOwnershipEvidenceForProject
    (canonical: ProjectPaths.ProjectId option) (cmd: GetProcessOwnershipEvidenceCommand) =
    fun _ ->
        match canonical, projectId cmd.ProjectName with
        | None, _ -> Error(ValidationError "Process ownership evidence requires an explicit project.")
        | _, Error error -> Error error
        | Some expected, Ok actual when actual <> expected ->
            Error(ValidationError "Process evidence project does not match the authenticated route.")
        | Some expected, Ok _ ->
            try
                match ProcessOwnershipJournal.inspectAt
                          (ProcessOwnershipJournal.defaultDirectory()) cmd.JobId with
                | Error failure -> Error(ContextError $"Process ownership remains unverified: {failure}")
                | Ok evidence ->
                    if evidence.Intent.ProjectId <> ProjectPaths.ProjectId.value expected then
                        Error(ContextError "Process ownership project mismatch; evidence is unverified.")
                    elif DeviceIdentity.tryReadExisting() <> Some evidence.Intent.DeviceId then
                        Error(ContextError "Process ownership device mismatch or missing persisted identity; evidence is unverified.")
                    else
                        let launch = evidence.Launch
                        let optionalText project =
                            launch
                            |> Option.map project
                            |> Option.bind (fun value ->
                                if String.IsNullOrWhiteSpace value then None else Some value)
                        Ok
                            { JobId = evidence.Intent.JobId
                              ProjectId = evidence.Intent.ProjectId
                              DeviceId = evidence.Intent.DeviceId
                              ClientIncarnationId = evidence.Intent.ClientIncarnationId
                              IntentCreatedUtc = evidence.Intent.CreatedUtc
                              Stage = if launch.IsSome then "Launched" else "IntentOnly"
                              ProcessId = launch |> Option.map (fun item -> item.ProcessId)
                              ProcessStartUtc = optionalText (fun item -> item.ProcessStartUtc)
                              LinuxBootId = optionalText (fun item -> item.LinuxBootId)
                              LinuxStartTicks = optionalText (fun item -> item.LinuxStartTicks)
                              IntentSha256 = optionalText (fun item -> item.IntentSha256)
                              LaunchRecordedUtc = optionalText (fun item -> item.RecordedUtc)
                              ProcessTreeStatus = "Unverified" }
            with ex ->
                Error(ContextError $"Process ownership remains unverified: {ex.GetType().Name}")

let getProcessOwnershipEvidence (cmd: GetProcessOwnershipEvidenceCommand) =
    fun rt ->
        match projectId cmd.ProjectName with
        | Error error -> Error error
        | Ok expected -> getProcessOwnershipEvidenceForProject (Some expected) cmd rt

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
