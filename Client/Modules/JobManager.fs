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
      ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      StartedAt: DateTimeOffset
      Process: Process
      StdOut: StringBuilder
      StdErr: StringBuilder
      MaxOutputBytes: int
      mutable StdOutBytes: int
      mutable StdErrBytes: int
      mutable StdOutTruncated: bool
      mutable StdErrTruncated: bool
      mutable CancellationRequested: bool
      mutable Status: JobStatus
      mutable CompletedAt: DateTimeOffset option }

module private Core =
    let private defaultMaxOutputBytes = 64 * 1024
    let private maxCompletedJobs = 100
    let private jobs = ConcurrentDictionary<string, JobRecord>()

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

    let private resolveWorkingDirectory projectName workingDirectory =
        match workingDirectory with
        | None -> ProjectPaths.resolveProjectRoot projectName
        | Some relativePath -> ProjectPaths.resolveProjectFolder projectName relativePath

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

    let private truncate maxBytes (value: string) =
        if String.IsNullOrEmpty value then
            value, false
        else
            let bytes = Encoding.UTF8.GetByteCount value
            if bytes <= maxBytes then
                value, false
            else
                let prefix, _ = takeUtf8Prefix maxBytes value
                prefix, true

    let private sliceFrom offset maxBytes (builder: StringBuilder) =
        let text = lock builder (fun () -> builder.ToString())
        let offset = defaultArg offset 0 |> max 0 |> min text.Length
        let value = text.Substring offset
        let value, truncated = truncate maxBytes value
        value, offset + value.Length, truncated

    let private appendLine maxBytes currentBytes (builder: StringBuilder) (data: string) =
        if isNull data then
            currentBytes, false
        else
            lock builder (fun () ->
                let value = data + Environment.NewLine
                let remaining = max 0 (maxBytes - currentBytes)
                let fragment, addedBytes = takeUtf8Prefix remaining value
                builder.Append(fragment) |> ignore
                currentBytes + addedBytes, addedBytes < Encoding.UTF8.GetByteCount value)

    let private startProcess projectName workingDirectory executable args maxOutputBytes : Client.Result<StartJobResult> =
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
                proc.EnableRaisingEvents <- true
                args |> List.iter proc.StartInfo.ArgumentList.Add

                let job =
                    { JobId = jobId
                      ProjectName = projectName
                      Executable = executable
                      Args = args
                      WorkingDirectory = Some workingDirectory
                      StartedAt = DateTimeOffset.UtcNow
                      Process = proc
                      StdOut = StringBuilder()
                      StdErr = StringBuilder()
                      MaxOutputBytes = maxOutputBytes
                      StdOutBytes = 0
                      StdErrBytes = 0
                      StdOutTruncated = false
                      StdErrTruncated = false
                      CancellationRequested = false
                      Status = Running
                      CompletedAt = None }

                proc.OutputDataReceived.Add(fun event ->
                    let bytes, truncated = appendLine job.MaxOutputBytes job.StdOutBytes job.StdOut event.Data
                    job.StdOutBytes <- bytes
                    job.StdOutTruncated <- job.StdOutTruncated || truncated)

                proc.ErrorDataReceived.Add(fun event ->
                    let bytes, truncated = appendLine job.MaxOutputBytes job.StdErrBytes job.StdErr event.Data
                    job.StdErrBytes <- bytes
                    job.StdErrTruncated <- job.StdErrTruncated || truncated)

                proc.Exited.Add(fun _ ->
                    if job.CancellationRequested then
                        job.Status <- Canceled
                    else
                        try job.Status <- Completed proc.ExitCode
                        with _ -> job.Status <- FailedToStart "Process exited before an exit code was available."
                    job.CompletedAt <- Some DateTimeOffset.UtcNow
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

    let startJob (cmd: StartJobCommand) =
        effect {
            let maxOutputBytes = cmd.MaxOutputBytes |> Option.defaultValue defaultMaxOutputBytes
            if maxOutputBytes <= 0 then
                return! Client.ValidationError "MaxOutputBytes must be greater than zero." |> Effect.ofError
            else
                let! (FolderPath workingDirectory) = resolveWorkingDirectory cmd.ProjectName cmd.WorkingDirectory
                let! result = fun _ -> startProcess cmd.ProjectName workingDirectory cmd.Executable cmd.Args maxOutputBytes
                return result
        }

    let private toSummary (job: JobRecord) =
        { JobId = job.JobId
          ProjectName = job.ProjectName
          Executable = job.Executable
          Args = job.Args
          WorkingDirectory = job.WorkingDirectory
          Status = job.Status
          StartedAt = job.StartedAt
          CompletedAt = job.CompletedAt }

    let listJobs (cmd: ListJobsCommand) =
        fun _ ->
            jobs.Values
            |> Seq.filter (fun job ->
                match cmd.ProjectName with
                | Some projectName -> job.ProjectName = projectName
                | None -> true)
            |> Seq.filter (fun job -> cmd.IncludeCompleted || job.Status = Running)
            |> Seq.sortBy (fun job -> job.StartedAt)
            |> Seq.map toSummary
            |> Seq.toList
            |> fun items -> Ok { Jobs = items }

    let getJobResult (cmd: GetJobResultCommand) =
        fun _ ->
            match jobs.TryGetValue cmd.JobId with
            | false, _ -> Error(NotFoundError $"Unknown job id: {cmd.JobId}")
            | true, job ->
                let stdout, outputOffset, stdoutTruncated = sliceFrom cmd.FromOffset job.MaxOutputBytes job.StdOut
                let stderr, _, stderrTruncated = sliceFrom None job.MaxOutputBytes job.StdErr
                Ok
                    { JobId = job.JobId
                      Status = job.Status
                      StdOut = stdout
                      StdErr = stderr
                      OutputOffset = outputOffset
                      Truncated = job.StdOutTruncated || job.StdErrTruncated || stdoutTruncated || stderrTruncated }

    let cancelJob (cmd: CancelJobCommand) =
        fun _ ->
            match jobs.TryGetValue cmd.JobId with
            | false, _ -> Error(NotFoundError $"Unknown job id: {cmd.JobId}")
            | true, job ->
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

let startJob cmd = Core.startJob cmd
let listJobs cmd = Core.listJobs cmd
let getJobResult cmd = Core.getJobResult cmd
let cancelJob cmd = Core.cancelJob cmd
