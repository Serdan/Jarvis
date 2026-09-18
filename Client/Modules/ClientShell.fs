module Client.ClientShell

open System
open System.Diagnostics
open System.IO
open Common
open Client.Effect
open Client.IO
open FsToolkit.ErrorHandling
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

module private Core =
    let private defaultTimeoutSeconds = 60
    let private defaultMaxOutputBytes = 64 * 1024

    let private resolveWorkingDirectory projectName workingDirectory =
        match workingDirectory with
        | None -> ProjectPaths.resolveProjectRoot projectName
        | Some relativePath -> ProjectPaths.resolveProjectFolder projectName relativePath

    let private validateRelativePath = ProjectPaths.validateProjectRelativePath

    let private deniedExecutables =
        set [ "bash"; "sh"; "zsh"; "fish"; "cmd"; "cmd.exe"; "powershell"; "powershell.exe"; "pwsh"; "pwsh.exe" ]

    let private validateExecutable executable =
        if String.IsNullOrWhiteSpace executable then
            Error(Client.ValidationError "Executable cannot be empty.")
        elif deniedExecutables.Contains(executable.Trim().ToLowerInvariant()) then
            Error(Client.PermissionDenied $"Shell executable is denied by default: {executable}")
        else
            Ok executable

    let private takeUtf8Prefix maxBytes (value: string) =
        let output = Text.StringBuilder()
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

        output.ToString()

    let private readBounded maxBytes (reader: StreamReader) =
        task {
            let buffer = Array.zeroCreate<char> 4096
            let output = Text.StringBuilder()
            let mutable bytes = 0
            let mutable truncated = false
            let mutable reading = true

            while reading do
                let! count = reader.ReadAsync(buffer, 0, buffer.Length)

                if count = 0 then
                    reading <- false
                else
                    let chunk = String(buffer, 0, count)
                    let chunkBytes = Text.Encoding.UTF8.GetByteCount chunk
                    let remaining = max 0 (maxBytes - bytes)

                    if remaining > 0 then
                        let fragment = takeUtf8Prefix remaining chunk
                        let addedBytes = Text.Encoding.UTF8.GetByteCount fragment
                        output.Append(fragment) |> ignore
                        bytes <- bytes + addedBytes
                        truncated <- truncated || addedBytes < chunkBytes
                    else
                        truncated <- true

            return output.ToString(), truncated
        }

    let private runProcess workingDirectory executable args timeoutSeconds maxOutputBytes : Client.Result<RunCommandResult> =
        try
            match validateExecutable executable with
            | Error error -> Error error
            | Ok executable ->
                use proc = new Process()

                proc.StartInfo.FileName <- executable
                proc.StartInfo.WorkingDirectory <- workingDirectory
                proc.StartInfo.RedirectStandardOutput <- true
                proc.StartInfo.RedirectStandardError <- true
                proc.StartInfo.UseShellExecute <- false
                proc.StartInfo.CreateNoWindow <- true

                args |> List.iter proc.StartInfo.ArgumentList.Add

                if not (proc.Start()) then
                    Error(Client.ContextError "Failed to start process.")
                else
                    let stdoutTask = readBounded maxOutputBytes proc.StandardOutput
                    let stderrTask = readBounded maxOutputBytes proc.StandardError
                    let timeoutMs = timeoutSeconds * 1000
                    let exited = proc.WaitForExit timeoutMs

                    if not exited then
                        try
                            proc.Kill(entireProcessTree = true)
                            proc.WaitForExit()
                        with _ -> ()

                    let stdout, stdoutTruncated = stdoutTask.Result
                    let stderr, stderrTruncated = stderrTask.Result

                    Ok
                        { ExitCode = if exited then proc.ExitCode else -1
                          TimedOut = not exited
                          StdOut = stdout
                          StdErr = stderr
                          Truncated = stdoutTruncated || stderrTruncated }
        with ex ->
            Error(ExceptionError ex)

    let private run projectName workingDirectory executable args timeoutSeconds maxOutputBytes =
        effect {
            let timeoutSeconds = timeoutSeconds |> Option.defaultValue defaultTimeoutSeconds
            let maxOutputBytes = maxOutputBytes |> Option.defaultValue defaultMaxOutputBytes

            if timeoutSeconds <= 0 then
                return! Client.ValidationError "TimeoutSeconds must be greater than zero." |> Effect.ofError
            elif maxOutputBytes <= 0 then
                return! Client.ValidationError "MaxOutputBytes must be greater than zero." |> Effect.ofError
            else
                let! (FolderPath workingDirectory) = resolveWorkingDirectory projectName workingDirectory
                let! result = fun _ -> runProcess workingDirectory executable args timeoutSeconds maxOutputBytes

                return
                    { ExitCode = result.ExitCode
                      TimedOut = result.TimedOut
                      StdOut = result.StdOut
                      StdErr = result.StdErr
                      Truncated = result.Truncated }
        }

    let runCommand (cmd: RunCommandCommand) =
        run cmd.ProjectName cmd.WorkingDirectory cmd.Executable cmd.Args cmd.TimeoutSeconds cmd.MaxOutputBytes

    let private git projectName args maxOutputBytes =
        run projectName None "git" args (Some defaultTimeoutSeconds) maxOutputBytes

    let getGitStatus (cmd: GitStatusCommand) =
        git cmd.ProjectName [ "status"; "--short"; "--branch" ] None

    let getGitDiff (cmd: GitDiffCommand) =
        match cmd.Path with
        | None -> git cmd.ProjectName [ "diff"; "--" ] cmd.MaxOutputBytes
        | Some path ->
            effect {
                let! safePath = fun _ -> validateRelativePath path
                return! git cmd.ProjectName [ "diff"; "--"; safePath ] cmd.MaxOutputBytes
            }

    let gitCommit (cmd: GitCommitCommand) =
        effect {
            if String.IsNullOrWhiteSpace cmd.Message then
                return! Client.ValidationError "Commit message cannot be empty." |> Effect.ofError
            elif cmd.Paths.IsEmpty && not cmd.AllowEmpty then
                return! Client.ValidationError "GitCommit requires at least one path unless AllowEmpty = true." |> Effect.ofError
            else
                let! safePaths =
                    fun _ ->
                        cmd.Paths
                        |> List.map validateRelativePath
                        |> List.fold
                            (fun state item ->
                                match state, item with
                                | Ok values, Ok value -> Ok(value :: values)
                                | Error error, _ -> Error error
                                | _, Error error -> Error error)
                            (Ok [])
                        |> Result.map List.rev

                let! addResult =
                    if safePaths.IsEmpty then
                        fun _ -> Ok None
                    else
                        git cmd.ProjectName ([ "add"; "--" ] @ safePaths) None |>> Some

                let! stagedResult =
                    if safePaths.IsEmpty then
                        git cmd.ProjectName [ "diff"; "--cached"; "--quiet"; "--" ] None |>> Some
                    else
                        fun _ -> Ok None

                do!
                    match stagedResult with
                    | Some result when result.ExitCode = 1 ->
                        Client.ValidationError "Cannot create an empty commit while staged changes already exist." |> Effect.ofError
                    | Some result when result.ExitCode <> 0 ->
                        Client.GenericError result.StdErr |> Effect.ofError
                    | _ -> fun _ -> Ok()

                match addResult with
                | Some result when result.ExitCode <> 0 ->
                    return! Client.GenericError result.StdErr |> Effect.ofError
                | _ ->
                    let commitArgs =
                        [ yield "commit"
                          if cmd.AllowEmpty then yield "--allow-empty"
                          yield "-m"
                          yield cmd.Message
                          match cmd.Body with
                          | Some body when not (String.IsNullOrWhiteSpace body) ->
                              yield "-m"
                              yield body
                          | _ -> ()
                          if not safePaths.IsEmpty then
                              yield "--only"
                              yield "--"
                              yield! safePaths ]

                    let! commitResult = git cmd.ProjectName commitArgs None

                    if commitResult.ExitCode <> 0 then
                        return! Client.GenericError commitResult.StdErr |> Effect.ofError
                    else
                        let! revParse = git cmd.ProjectName [ "rev-parse"; "HEAD" ] None
                        let hash = revParse.StdOut.Trim()

                        return
                            { CommitHash = hash
                              Summary = commitResult.StdOut.Trim()
                              StdOut = commitResult.StdOut
                              StdErr = commitResult.StdErr }
        }

let runCommand cmd = Core.runCommand cmd
let getGitStatus cmd = Core.getGitStatus cmd
let getGitDiff cmd = Core.getGitDiff cmd
let gitCommit cmd = Core.gitCommit cmd
