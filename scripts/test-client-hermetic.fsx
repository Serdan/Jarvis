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

let rec parse timeout scratchParent forwarded = function
    | "--timeout-seconds" :: value :: tail ->
        match Int32.TryParse value with
        | true, seconds when seconds >= 10 && seconds <= 900 -> parse seconds scratchParent forwarded tail
        | _ -> failwith "Timeout must be between 10 and 900 seconds."
    | "--scratch-parent" :: value :: tail -> parse timeout (Some value) forwarded tail
    | head :: tail -> parse timeout scratchParent (head :: forwarded) tail
    | [] -> timeout, scratchParent, List.rev forwarded

let timeout, scratchParent, suppliedArgs = parse 240 None [] args
let injectSetupFailure = suppliedArgs |> List.contains "--self-test-failed-setup"
let injectGitIdentitySentinel = suppliedArgs |> List.contains "--self-test-git-sentinel"
let forwarded =
    suppliedArgs
    |> List.filter (fun arg ->
        arg <> "--self-test-failed-setup" && arg <> "--self-test-git-sentinel")

// A Windows test runner must be given a pre-existing, disposable directory
// OUTSIDE the operator profile. Never create scratch under ambient Windows
// %TEMP% (normally inside the real user's LocalApplicationData).
if OperatingSystem.IsMacOS() then
    failwith "Hermetic Client.Tests macOS known-folder isolation is unverified."
if OperatingSystem.IsWindows() && scratchParent.IsNone then
    failwith "Windows requires --scratch-parent under an explicitly disposable directory outside the operator profile."

let privatePathComparison =
    if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
    else StringComparison.Ordinal

let sameOrUnder (root: string) (candidate: string) =
    if String.IsNullOrWhiteSpace root then false
    else
        let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
        let target = Path.TrimEndingDirectorySeparator(Path.GetFullPath candidate)
        String.Equals(root, target, privatePathComparison)
        || target.StartsWith(root + string Path.DirectorySeparatorChar, privatePathComparison)

let validateScratchParent (path: string) =
    if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
        failwith "Hermetic scratch parent must be an absolute path."
    let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
    if not (Directory.Exists full) then
        failwith "Hermetic scratch parent must already exist."
    // No path can be trusted merely because its spelling has a safe prefix.
    let mutable ancestor = full
    let mutable checking = true
    while checking do
        let attributes = File.GetAttributes ancestor
        if attributes.HasFlag(FileAttributes.ReparsePoint)
           || not (isNull (DirectoryInfo(ancestor).LinkTarget)) then
            failwith "Hermetic scratch parent has a linked/junction ancestor."
        let parent = Path.GetDirectoryName ancestor
        if String.IsNullOrWhiteSpace parent || parent = ancestor then
            checking <- false
        else ancestor <- parent
    if OperatingSystem.IsWindows() then
        for protectedPath in
            [ Environment.GetFolderPath Environment.SpecialFolder.UserProfile
              Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
              Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData ] do
            if sameOrUnder protectedPath full then
                failwith "Hermetic scratch parent must be outside the real Windows user profile."
    full

let scratchLocation = scratchParent |> Option.map validateScratchParent
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
let tempDirectory =
    match scratchLocation with
    | Some parent ->
        // Directory.CreateTempSubdirectory has no parent overload in .NET 11.
        // Allocate a unique child only under the pre-attested scratch parent.
        let path = Path.Combine(parent, $"jarvis-client-tests-{Guid.NewGuid():N}")
        if File.Exists path || Directory.Exists path then
            failwith "Refusing an already occupied disposable Client.Tests scratch name."
        let created = Directory.CreateDirectory(path)
        if created.Attributes.HasFlag(FileAttributes.ReparsePoint) then
            failwith "Refusing a linked disposable Client.Tests scratch root."
        created.FullName
    | None -> Directory.CreateTempSubdirectory("jarvis-client-tests-").FullName
// Direct/external NUnit fixtures attest that the root and every layout
// directory are owner-only. Set a known private mode before child startup.
if OperatingSystem.IsLinux() then
    File.SetUnixFileMode(tempDirectory,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
printfn "Hermetic Client.Tests: scratch id %s" (Path.GetFileName tempDirectory)
printfn "Hermetic Client.Tests: scratch path %s" tempDirectory
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

    // FSI can be launched through an SDK-specific executable on Windows;
    // Environment.ProcessPath may then be fsi.exe rather than dotnet.exe.
    // Explicitly run the dotnet CLI already required to launch this script.
    let dotnet = "dotnet"

    let start = ProcessStartInfo(dotnet)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.WorkingDirectory <- sourceRoot
    if OperatingSystem.IsWindows() then
        // On Windows, dotnet test can silently exit 0 without test discovery
        // after NuGet/MSBuild imports are isolated from ambient user caches.
        // Execute the compiled .NET 11 Release assembly through VSTest
        // directly, retaining a strict nonzero-executed-count assertion.
        let binaryDirectory = Path.Combine(sourceRoot, "Client.Tests", "bin", "Release", "net11.0")
        let assembly = Path.Combine(binaryDirectory, "Client.Tests.dll")
        if not (File.Exists assembly) then
            failwith "Windows hermetic VSTest requires a prebuilt .NET 11 Release Client.Tests.dll."
        start.ArgumentList.Add("vstest")
        start.ArgumentList.Add(assembly)
        start.ArgumentList.Add("/Framework:net11.0")
        start.ArgumentList.Add("/ResultsDirectory:" + results)
        start.ArgumentList.Add("/Logger:console;verbosity=normal")
        start.ArgumentList.Add("/TestAdapterPath:" + binaryDirectory)
        let rec forward = function
            | [] -> ()
            | "--no-build" :: tail
            | "--no-restore" :: tail -> forward tail
            | "--filter" :: expression :: tail ->
                start.ArgumentList.Add("/TestCaseFilter:" + expression)
                forward tail
            | "--list-tests" :: tail ->
                start.ArgumentList.Add("/ListTests")
                forward tail
            | other :: _ -> failwith $"Unsupported Windows VSTest passthrough option: {other}."
        forward forwarded
        printfn "Hermetic Client.Tests: child executable %s; Windows CLI command dotnet vstest Client.Tests.dll (Release net11.0)" dotnet
    else
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
        printfn "Hermetic Client.Tests: child executable %s; CLI command dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore" dotnet

    // Exercise inherited-Git-identity failure with synthetic sentinels only.
    if injectGitIdentitySentinel then
        for key, value in
            [ "GIT_AUTHOR_NAME", "LEAK_SENTINEL_AUTHOR"
              "GIT_AUTHOR_EMAIL", "sentinel-author@example.invalid"
              "GIT_COMMITTER_NAME", "LEAK_SENTINEL_COMMITTER"
              "GIT_COMMITTER_EMAIL", "sentinel-committer@example.invalid"
              "GIT_CONFIG_COUNT", "1"
              "GIT_CONFIG_KEY_0", "user.name"
              "GIT_CONFIG_VALUE_0", "LEAK_SENTINEL_CONFIG" ] do
            start.Environment[key] <- value

    // GIT_AUTHOR_*, GIT_COMMITTER_*, GIT_CONFIG_COUNT and repository/index
    // overrides all outrank or bypass a disposable GIT_CONFIG_GLOBAL.
    // Strip every inherited Git override, then install only test values.
    for key in start.Environment.Keys |> Seq.toArray do
        if key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) then
            start.Environment.Remove(key) |> ignore

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
          "GIT_CONFIG_NOSYSTEM", "1"
          "GIT_AUTHOR_NAME", "Jarvis Test"
          "GIT_AUTHOR_EMAIL", "jarvis-test@example.invalid"
          "GIT_COMMITTER_NAME", "Jarvis Test"
          "GIT_COMMITTER_EMAIL", "jarvis-test@example.invalid"
          "LOCALAPPDATA", data
          "JARVIS_CLIENT_TEST_HERMETIC_ROOT", tempDirectory
          "NUGET_PACKAGES", Path.Combine(cache, "nuget")
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
        printfn "Hermetic Client.Tests: child exit %d; drained %b; stdout chars %d; stderr chars %d" child.ExitCode drained
            (if drained then stdoutTask.Result.Length else -1)
            (if drained then stderrTask.Result.Length else -1)
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
    printfn "Hermetic Client.Tests: scratch cleanup verified = %b" (not (Directory.Exists tempDirectory))
    if injectSetupFailure then
        eprintfn "Hermetic Client.Tests: scratch cleanup verified = %b" (not (Directory.Exists tempDirectory))

// F# Interactive resets ExitCode when the script returns normally. The child
// outcome must be the host process outcome, and cleanup must run first.
Environment.Exit(Environment.ExitCode)
