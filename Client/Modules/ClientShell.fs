module Client.ClientShell

open System.Diagnostics
open Client.Effect

type ProcessError =
    | StartProcessError of string
    | ExecutionError of string
    | UnhandledExceptionError of exn

let runProcess file args =
    let processStartInfo =
        ProcessStartInfo(
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        )

    try
        let proc = Process.Start(processStartInfo)

        if proc = null then
            Error(StartProcessError "Failed to start process.")
        else
            let output = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit()

            if proc.ExitCode = 0 then
                Ok output
            else
                Error(ExecutionError $"Exit code: {proc.ExitCode}\nOutput: {output}")
    with ex ->
        Error(UnhandledExceptionError ex)

let errorMap msg =
    Result.mapError
    <| function
        | StartProcessError s -> ContextError s
        | ExecutionError s -> GenericError $"{msg}. {s}"
        | UnhandledExceptionError exc -> ExceptionError exc

/// Runs unit tests for a given project.
/// Builds the specified project.
let buildProject (projectPath: string) : IO<'rt, string> =
    fun _ -> runProcess "dotnet" $"build {projectPath}" |> errorMap "Build failed"

/// Lints the specified project using dotnet format.
let lintProject (projectPath: string) : IO<'rt, string> =
    fun _ -> runProcess "dotnet" $"format {projectPath}" |> errorMap "Linting failed"

let runUnitTests (projectPath: string) : IO<'rt, string> =
    fun _ -> runProcess "dotnet" $"test {projectPath}" |> errorMap "Test execution failed"
