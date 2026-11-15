// Run with: dotnet fsi scripts/test-client-hermetic.fsx -- [dotnet test options]
// Each invocation receives a disposable HOME, XDG, local app data and temp tree.
// The test process is a child: the invoking Jarvis/.NET process keeps its normal environment.
open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open System.Text.RegularExpressions
open System.Text

let args =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.toList
    |> function | "--" :: tail -> tail | rest -> rest

let rec parse timeout forwarded = function
    | "--timeout-seconds" :: value :: tail ->
        match Int32.TryParse value with
        | true, seconds when seconds >= 10 && seconds <= 900 -> parse seconds forwarded tail
        | _ -> failwith "Timeout must be between 10 and 900 seconds."
    | head :: tail -> parse timeout (head :: forwarded) tail
    | [] -> timeout, List.rev forwarded

let timeout, suppliedArgs = parse 240 [] args
let injectSetupFailure = suppliedArgs |> List.contains "--self-test-failed-setup"
let forwarded = suppliedArgs |> List.filter ((<>) "--self-test-failed-setup")
let captureTail (reader: StreamReader) =
    task {
        // Never retain the unbounded output of a failing or chatty test process.
        let tail = StringBuilder()
        let buffer = Array.zeroCreate<char> 4096
        let mutable finished = false
        while not finished do
            let! count = reader.ReadAsync(buffer, 0, buffer.Length)
            if count = 0 then finished <- true
            else
                tail.Append(buffer, 0, count) |> ignore
                if tail.Length > 24000 then
                    tail.Remove(0, tail.Length - 24000) |> ignore
        return tail.ToString()
    }

let sourceRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let tempDirectory = Directory.CreateTempSubdirectory("jarvis-client-tests-").FullName
printfn "Hermetic Client.Tests: scratch id %s" (Path.GetFileName tempDirectory)
let makeDirectory child =
    let path = Path.Combine(tempDirectory, child)
    Directory.CreateDirectory(path) |> ignore
    path

try
    // Avoid ever inheriting the operator's Jarvis config, activity, identity,
    // binding or local file-edit locks. Set environment BEFORE starting testhost.
    let home = makeDirectory "home"
    let config = makeDirectory "config"
    let data = makeDirectory "data"
    let state = makeDirectory "state"
    let cache = makeDirectory "cache"
    let runtime = makeDirectory "runtime"
    let temp = makeDirectory "tmp"
    let cli = makeDirectory "dotnet-cli"
    let results = makeDirectory "results"
    // Isolated Git plumbing uses a test-only identity for commit-tree in
    // temporary repositories. Never read or modify the operator's gitconfig.
    let gitConfig = Path.Combine(tempDirectory, "gitconfig")
    File.WriteAllText(gitConfig, "[user]\n\tname = Jarvis Test\n\temail = jarvis-test@example.invalid\n")
    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
        File.SetUnixFileMode(gitConfig, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
        for directory in [ home; config; data; state; cache; runtime; temp; cli; results ] do
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    if injectSetupFailure then
        eprintfn "Hermetic Client.Tests: intentional setup failure in %s" tempDirectory
        failwith "Harness setup failure injected after allocating disposable paths."

    let dotnet =
        match Environment.ProcessPath with
        | null | "" -> "dotnet"
        | executable -> executable

    let start = ProcessStartInfo(dotnet)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.WorkingDirectory <- sourceRoot
    start.ArgumentList.Add("test")
    start.ArgumentList.Add("Client.Tests/Client.Tests.fsproj")
    start.ArgumentList.Add("-c")
    start.ArgumentList.Add("Release")
    start.ArgumentList.Add("--no-restore")
    start.ArgumentList.Add("--results-directory")
    start.ArgumentList.Add(results)
    start.ArgumentList.Add("--logger")
    start.ArgumentList.Add("console;verbosity=normal")
    // A hanging individual test should not consume the whole suite timeout.
    start.ArgumentList.Add("--blame-hang-timeout")
    start.ArgumentList.Add("75s")
    for arg in forwarded do start.ArgumentList.Add(arg)

    for (key, value) in
        [ "HOME", home
          "USERPROFILE", home
          "XDG_CONFIG_HOME", config
          "XDG_DATA_HOME", data
          "XDG_STATE_HOME", state
          "XDG_CACHE_HOME", cache
          "XDG_RUNTIME_DIR", runtime
          "DOTNET_CLI_HOME", cli
          "TMPDIR", temp
          "TEMP", temp
          "TMP", temp
          "APPDATA", config
          "GIT_CONFIG_GLOBAL", gitConfig
          "LOCALAPPDATA", data
          "JARVIS_CLIENT_TEST_HERMETIC_ROOT", tempDirectory
          "DOTNET_CLI_TELEMETRY_OPTOUT", "1"
          "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"
          "DOTNET_NOLOGO", "1" ] do
        start.Environment[key] <- value

    // --no-restore consumes the already restored project assets. NuGet packages
    // may be read from the existing cache, but test code cannot write there.
    use child = new Process(StartInfo = start)
    if not (child.Start()) then
        failwith "Could not launch the isolated dotnet test process."
    // Read both streams concurrently so full-diagnostic NUnit output cannot
    // block a child on a filled stdout/stderr pipe.
    let stdoutTask = captureTail child.StandardOutput
    let stderrTask = captureTail child.StandardError
    let finished = child.WaitForExit(timeout * 1000)
    if not finished then
        eprintfn "Hermetic Client.Tests: exceeded %d seconds (FAIL, not PASS); killing test process tree." timeout
        try child.Kill(true) with _ -> ()
        child.WaitForExit(10000) |> ignore

    let drained =
        Task.WaitAll([| stdoutTask :> Task; stderrTask :> Task |], 10000)
    let report (label: string) (maxCharacters: int) (value: string) =
        if not (String.IsNullOrWhiteSpace value) then
            let visible =
                if value.Length > maxCharacters then
                    printfn "%s: omitted %d earlier characters." label (value.Length - maxCharacters)
                    value.Substring(value.Length - maxCharacters)
                else value
            printf "%s" visible
            if not (visible.EndsWith("\n")) then printfn ""
    if drained then
        report "test stdout" 10500 stdoutTask.Result
        report "test stderr" 3500 stderrTask.Result
    else
        eprintfn "Hermetic Client.Tests: could not drain all child output."

    if finished then
        let discoveryOnly = forwarded |> List.contains "--list-tests"
        let testCount =
            if drained then
                let matched = Regex.Match(stdoutTask.Result, @"Total tests:\s*(\d+)")
                if matched.Success then
                    match Int32.TryParse(matched.Groups[1].Value) with
                    | true, value -> value
                    | _ -> 0
                else 0
            else 0
        // .NET can return exit 0 after only a build when test discovery fails.
        // An empty or unreported run must never be presented as a test PASS.
        let exitCode =
            if child.ExitCode <> 0 then child.ExitCode
            elif discoveryOnly || testCount > 0 then 0
            else 2
        if exitCode = 2 then
            eprintfn "Hermetic Client.Tests: no executed test count was reported (FAIL, not PASS)."
        printfn "Hermetic Client.Tests: exit %d; executed tests %d; disposable environment removed." exitCode testCount
        Environment.ExitCode <- exitCode
    else
        Environment.ExitCode <- 124
finally
    // A failed setup, test failure, cancellation or timeout must not retain
    // activity, bindings, test output or private temporary credentials.
    if Directory.Exists(tempDirectory) then
        Directory.Delete(tempDirectory, true)
    if injectSetupFailure then
        eprintfn "Hermetic Client.Tests: scratch cleanup verified = %b" (not (Directory.Exists tempDirectory))

// F# Interactive resets ExitCode when the script returns normally. The child
// outcome must be the host process outcome, and cleanup must run first.
Environment.Exit(Environment.ExitCode)
