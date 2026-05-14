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
      mutable Status: JobStatus
      mutable CompletedAt: DateTimeOffset option }

module private Core =
    let private defaultMaxOutputBytes = 64 * 1024
    let private jobs = ConcurrentDictionary<string, JobRecord>()

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

    let private truncate maxBytes (value: string) =
        if String.IsNullOrEmpty value then
            value, false
        else
            let bytes = Encoding.UTF8.GetByteCount value
            if bytes <= maxBytes then value, false
            else value.Substring(0, min value.Length maxBytes), true

    let private sliceFrom offset maxBytes (builder: StringBuilder) =
        let text = lock builder (fun () -> builder.ToString())
        let offset = defaultArg offset 0 |> max 0 |> min text.Length
        let value = text.Substring offset
        let value, truncated = truncate maxBytes value
        value, offset + value.Length, truncated

    let private appendLine (builder: StringBuilder) (data: string) =
        if not (isNull data) then
            lock builder (fun () -> builder.AppendLine data |> ignore)

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
                      Status = Running
                      CompletedAt = None }

                proc.OutputDataReceived.Add(fun event -> appendLine job.StdOut event.Data)
                proc.ErrorDataReceived.Add(fun event -> appendLine job.StdErr event.Data)
                proc.Exited.Add(fun _ ->
                    if job.Status <> Canceled then
                        try job.Status <- Completed proc.ExitCode
                        with _ -> job.Status <- FailedToStart "Process exited before an exit code was available."
                    job.CompletedAt <- Some DateTimeOffset.UtcNow)

                if not (jobs.TryAdd(jobId, job)) then
                    proc.Dispose()
                    Error(Client.ContextError "Failed to register job.")
                elif not (proc.Start()) then
                    let mutable removed = Unchecked.defaultof<JobRecord>
                    jobs.TryRemove(jobId, &removed) |> ignore
                    proc.Dispose()
                    Error(Client.ContextError "Failed to start job.")
                else
                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()
                    Ok { JobId = jobId; StartedAt = job.StartedAt }
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
                      Truncated = stdoutTruncated || stderrTruncated }

    let cancelJob (cmd: CancelJobCommand) =
        fun _ ->
            match jobs.TryGetValue cmd.JobId with
            | false, _ -> Error(NotFoundError $"Unknown job id: {cmd.JobId}")
            | true, job ->
                if job.Status = Running then
                    job.Status <- Canceled
                    job.CompletedAt <- Some DateTimeOffset.UtcNow
                    try
                        if not job.Process.HasExited then job.Process.Kill(entireProcessTree = true)
                        Ok()
                    with ex -> Error(ExceptionError ex)
                else
                    Ok()

let startJob cmd = Core.startJob cmd
let listJobs cmd = Core.listJobs cmd
let getJobResult cmd = Core.getJobResult cmd
let cancelJob cmd = Core.cancelJob cmd
