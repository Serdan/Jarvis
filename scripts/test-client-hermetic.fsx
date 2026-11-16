#r "System.IO.FileSystem.AccessControl"
#r "System.Security.AccessControl"
#r "System.Security.Principal.Windows"
#load "../Client.Tests/HermeticWindowsGuard.fs"
// Run with: dotnet fsi scripts/test-client-hermetic.fsx -- [dotnet test options]
// Each invocation receives a disposable HOME, XDG, local app data and temp tree.
// The test process is a child: the invoking Jarvis/.NET process keeps its normal environment.
open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open System.Text.RegularExpressions
open System.Text
open Jarvis.Testing

let args =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.toList
    |> function | "--" :: tail -> tail | rest -> rest

// Native Git-backed Windows fixtures run substantially slower than Linux.
// Allow an explicitly requested bounded deadline for full Windows acceptance;
// keep the short default and the non-Windows ceiling unchanged.
let maxTimeoutSeconds = if OperatingSystem.IsWindows() then 21600 else 1800

let rec parse timeout scratchParent forwarded = function
    | "--timeout-seconds" :: value :: tail ->
        match Int32.TryParse value with
        | true, seconds when seconds >= 10 && seconds <= maxTimeoutSeconds -> parse seconds scratchParent forwarded tail
        | _ -> failwith $"Timeout must be between 10 and {maxTimeoutSeconds} seconds."
    | "--scratch-parent" :: value :: tail -> parse timeout (Some value) forwarded tail
    | head :: tail -> parse timeout scratchParent (head :: forwarded) tail
    | [] -> timeout, scratchParent, List.rev forwarded

let timeout, scratchParent, suppliedArgs = parse 240 None [] args
let injectSetupFailure = suppliedArgs |> List.contains "--self-test-failed-setup"
let injectGitIdentitySentinel = suppliedArgs |> List.contains "--self-test-git-sentinel"
let injectRootCreationFailure = suppliedArgs |> List.contains "--self-test-after-root-creation"
let injectCleanupLock = suppliedArgs |> List.contains "--self-test-locked-cleanup"
let forwarded =
    suppliedArgs
    |> List.filter (fun arg ->
        arg <> "--self-test-failed-setup" && arg <> "--self-test-git-sentinel"
        && arg <> "--self-test-after-root-creation" && arg <> "--self-test-locked-cleanup")

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
    // Token-derived native profile and GUID-volume physical path exclusions
    // must succeed in THIS parent process, before scratch allocation or writes.
    if OperatingSystem.IsWindows() then
        WindowsDisposableGuard.attestScratchParent full
    else full

let scratchLocation = scratchParent |> Option.map validateScratchParent
let mutable scratchLease: FileStream option = None
let captureTail (reader: StreamReader) =
    task {
        // Never retain the unbounded output of a failing or chatty test process.
        let tail = StringBuilder()
        let currentLine = StringBuilder()
        let mutable completed = 0
        let progressClock = Stopwatch.StartNew()
        let buffer = Array.zeroCreate<char> 4096
        let mutable finished = false
        while not finished do
            let! count = reader.ReadAsync(buffer, 0, buffer.Length)
            if count = 0 then finished <- true
            else
                tail.Append(buffer, 0, count) |> ignore
                if tail.Length > 24000 then
                    tail.Remove(0, tail.Length - 24000) |> ignore
                // Surface bounded completion progress while the child runs.
                for i in 0 .. count - 1 do
                    let ch = buffer[i]
                    if ch = '\n' then
                        let line = currentLine.ToString().Trim()
                        currentLine.Clear() |> ignore
                        let failed = line.StartsWith("Failed ", StringComparison.OrdinalIgnoreCase)
                        if failed || line.StartsWith("Passed ", StringComparison.OrdinalIgnoreCase)
                                  || line.StartsWith("Skipped ", StringComparison.OrdinalIgnoreCase) then
                            completed <- completed + 1
                            // Preserve every per-case result for the current suite.
                            // Sample larger runs to avoid unbounded progress logs.
                            if failed || completed <= 1000 || completed % 10 = 0 then
                                let excerpt = if line.Length > 180 then line.Substring(0, 180) else line
                                printfn "Hermetic Client.Tests: progress completed %d (local stream); +%.1fs %s" completed progressClock.Elapsed.TotalSeconds excerpt
                    elif ch <> '\r' && currentLine.Length < 512 then
                        currentLine.Append(ch) |> ignore
        return tail.ToString()
    }

let sourceRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let tempDirectory =
    match scratchLocation with
    | Some parent ->
        // Allocate a random child under the pre-attested disposable parent.
        // Its exclusive, owner-only sidecar lease is held BEFORE root creation.
        let path = Path.Combine(parent, $"jarvis-client-tests-{Guid.NewGuid():N}")
        if not (WindowsDisposableGuard.provenAbsent path) then
            failwith "Refusing an already occupied disposable Client.Tests scratch name."
        if OperatingSystem.IsWindows() then
            scratchLease <- Some (WindowsDisposableGuard.createScratchLease path)
        try
            let created =
                if OperatingSystem.IsWindows() then
                    WindowsDisposableGuard.acquirePrivateRoot path injectRootCreationFailure
                else Directory.CreateDirectory(path)
            if injectRootCreationFailure && not (OperatingSystem.IsWindows()) then
                failwith "Hermetic root-creation failure injected after allocation."
            if created.Attributes.HasFlag(FileAttributes.ReparsePoint) then
                failwith "Refusing a linked disposable Client.Tests scratch root."
            created.FullName
        with original ->
            // Allocation precedes the main try/finally. This root has no
            // children yet; remove it non-recursively only after security
            // attestation, and preserve any failed cleanup for investigation.
            try
                if not (WindowsDisposableGuard.provenAbsent path) then
                    if OperatingSystem.IsWindows() then
                        WindowsDisposableGuard.assertNoLinkedAncestors path
                        WindowsDisposableGuard.assertOwnerOnly path true true
                    Directory.Delete(path)
                if not (WindowsDisposableGuard.provenAbsent path) then
                    failwith "Failed acquisition left an unverified scratch root."
                match scratchLease with
                | Some lease -> WindowsDisposableGuard.finishScratchLease path lease
                | None -> ()
                scratchLease <- None
                printfn "Hermetic Client.Tests: failed root acquisition %s; scratch cleanup verified = true" path
            with cleanupError ->
                scratchLease |> Option.iter (fun lease -> lease.Dispose())
                raise (AggregateException(
                    "Hermetic root allocation failed and safe scratch cleanup could not be verified.",
                    original, cleanupError))
            reraise()
    | None -> Directory.CreateTempSubdirectory("jarvis-client-tests-").FullName
// Direct/external NUnit fixtures attest that the root and every layout
// directory are owner-only. Set a known private mode before child startup.
let makeDirectory child =
    let path = Path.Combine(tempDirectory, child)
    Directory.CreateDirectory(path) |> ignore
    if OperatingSystem.IsWindows() then
        WindowsDisposableGuard.assertOwnerOnly path true false
    path

// Windows Git object files can be ReadOnly. Recursive Directory.Delete leaves
// them behind and incorrectly masks the original test outcome. Remove only
// this generated scratch tree, clearing ReadOnly on ordinary descendants;
// never recurse through a symlink/junction/reparse point.
let rec deleteDisposableTree (path: string) =
    let attributes = File.GetAttributes path
    if attributes.HasFlag(FileAttributes.ReparsePoint) then
        if attributes.HasFlag(FileAttributes.Directory) then Directory.Delete(path)
        else File.Delete(path)
    elif attributes.HasFlag(FileAttributes.Directory) then
        for child in Directory.EnumerateFileSystemEntries(path) |> Seq.toArray do
            deleteDisposableTree child
        if attributes.HasFlag(FileAttributes.ReadOnly) then
            File.SetAttributes(path, attributes &&& ~~~FileAttributes.ReadOnly)
        Directory.Delete(path)
    else
        if attributes.HasFlag(FileAttributes.ReadOnly) then
            File.SetAttributes(path, attributes &&& ~~~FileAttributes.ReadOnly)
        File.Delete(path)

// A killed Windows testhost may leave a descendant's ledger lock open
// briefly after the direct child exits. Retry only transient I/O locks, with
// an independent root identity and owner/ACL check before every traversal.
let cleanDisposableTree (path: string) =
    let elapsed = Stopwatch.StartNew()
    let mutable retries = 0
    let rec remove () =
        if not (WindowsDisposableGuard.provenAbsent path) then
            if OperatingSystem.IsWindows() then
                WindowsDisposableGuard.assertNoLinkedAncestors path
                WindowsDisposableGuard.assertOwnerOnly path true true
            try
                deleteDisposableTree path
            with
            | :? IOException when OperatingSystem.IsWindows()
                                  && elapsed.Elapsed < TimeSpan.FromSeconds 20. ->
                retries <- retries + 1
                System.Threading.Thread.Sleep(200)
                remove ()
    remove ()
    if retries > 0 then
        printfn "Hermetic Client.Tests: delayed Windows lock release required %d cleanup retries." retries
    if not (WindowsDisposableGuard.provenAbsent path) then
        failwith "Hermetic Client.Tests: scratch cleanup did not remove the private root."


try
    if injectCleanupLock then
        if not (OperatingSystem.IsWindows()) then
            failwith "Locked cleanup regression is Windows-only."
        let held = new FileStream(Path.Combine(tempDirectory, "delayed-release.lock"),
                                  FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        Task.Run(fun () ->
            System.Threading.Thread.Sleep(1200)
            held.Dispose()) |> ignore
        failwith "Intentional transient Windows lock before hermetic cleanup."
    if OperatingSystem.IsLinux() then
        File.SetUnixFileMode(tempDirectory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    printfn "Hermetic Client.Tests: scratch id %s" (Path.GetFileName tempDirectory)
    printfn "Hermetic Client.Tests: scratch path %s" tempDirectory
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
    // Git for Windows otherwise rejects long synthetic canonical ledger paths
    // in these disposable fixtures. Do not change the operator's Git settings.
    let gitConfigContents =
        "[user]\n\tname = Jarvis Test\n\temail = jarvis-test@example.invalid\n"
        + (if OperatingSystem.IsWindows() then "[core]\n\tlongpaths = true\n" else "")
    File.WriteAllText(gitConfig, gitConfigContents)
    if OperatingSystem.IsWindows() then
        WindowsDisposableGuard.assertOwnerOnly gitConfig false false
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
        // Bound an individual stuck fixture; collect test sequence, not memory.
        // The host is terminated and FSI still owns final private cleanup.
        start.ArgumentList.Add("/Blame:CollectHangDump;TestTimeout=180s;HangDumpType=none")
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
          "JARVIS_CLIENT_TEST_DISPOSABLE_PARENT", Path.GetDirectoryName(tempDirectory)
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
        printfn "Hermetic Client.Tests: child exit result %d; executed tests %d; cleanup pending." exitCode testCount
        Environment.ExitCode <- exitCode
    else
        Environment.ExitCode <- 124
finally
    // A failed setup, test failure, cancellation or timeout must not retain
    // activity, bindings, test output or private temporary credentials.
    // The exclusive lease persists until independent root-absence proof.
    try
        cleanDisposableTree tempDirectory
        match scratchLease with
        | Some lease -> WindowsDisposableGuard.finishScratchLease tempDirectory lease
        | None -> ()
        scratchLease <- None
        printfn "Hermetic Client.Tests: scratch cleanup verified = true"
    with cleanupFailure ->
        // If the root survived, leave its sidecar provenance on disk so a
        // future explicit recovery may claim it after this process exits.
        scratchLease |> Option.iter (fun lease -> lease.Dispose())
        eprintfn "Hermetic Client.Tests: scratch cleanup verified = false (%s)" cleanupFailure.Message
        reraise()
    if injectSetupFailure then
        eprintfn "Hermetic Client.Tests: scratch cleanup verified = %b"
            (WindowsDisposableGuard.provenAbsent tempDirectory)

// F# Interactive resets ExitCode when the script returns normally. The child
// outcome must be the host process outcome, and cleanup must run first.
Environment.Exit(Environment.ExitCode)
