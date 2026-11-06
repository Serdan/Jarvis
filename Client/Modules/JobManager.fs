module Client.JobManager

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
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
                          Truncated = job.OutputTruncated })

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

let listJobsForProject (canonical: ProjectPaths.ProjectId option) (cmd: ListJobsCommand) = Core.listJobsForProject canonical cmd

let listJobs (cmd: ListJobsCommand) =
    fun rt ->
        let project =
            match cmd.ProjectName with
            | None -> Ok None
            | Some name -> projectId name |> Result.map Some
        match project with
        | Error error -> Error error
        | Ok canonical -> Core.listJobsForProject canonical cmd rt

let getJobResultForProject (canonical: ProjectPaths.ProjectId option) (cmd: GetJobResultCommand) = Core.getJobResultForProject canonical cmd

let getJobResult (cmd: GetJobResultCommand) =
    fun rt ->
        match optionalProjectId cmd.ProjectName with
        | Error error -> Error error
        | Ok canonical -> Core.getJobResultForProject canonical cmd rt

let cancelJobForProject (canonical: ProjectPaths.ProjectId option) (cmd: CancelJobCommand) = Core.cancelJobForProject canonical cmd

let cancelJob (cmd: CancelJobCommand) =
    fun rt ->
        match optionalProjectId cmd.ProjectName with
        | Error error -> Error error
        | Ok canonical -> Core.cancelJobForProject canonical cmd rt
