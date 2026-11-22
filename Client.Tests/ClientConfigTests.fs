module ClientConfigTests

open System
open System.IO
open Client
open NUnit.Framework
open FsUnitTyped

[<Test>]
let ``config location is per user and honors linux XDG`` () =
    let xdgRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "jarvis-xdg-test"))
    ClientConfig.directoryFor true false "/home/example" "/ignored" xdgRoot
    |> shouldEqual (Path.Combine(xdgRoot, "Jarvis"))
    ClientConfig.directoryFor true false "/home/example" "/ignored" "relative"
    |> shouldEqual (Path.Combine("/home/example", ".config", "Jarvis"))
    ClientConfig.directoryFor false true "/Users/example" "/ignored" ""
    |> shouldEqual (Path.Combine("/Users/example", "Library", "Application Support", "Jarvis"))
    (ClientConfig.directoryFor false false "/irrelevant" "/Users/test/AppData/Roaming" "")
    |> shouldEqual (Path.Combine("/Users/test/AppData/Roaming", "Jarvis"))

[<Test>]
let ``saved configuration persists independently of executable and preserves unknown keys`` () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-config-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let path = Path.Combine(root, "config.json")
    try
        File.WriteAllText(path, """{"schemaVersion":1,"futureField":"kept"}""")
        let snapshot =
            match ClientConfig.loadAt path with
            | Ok x -> x
            | Error e -> failwith e
        let changed =
            { snapshot.Stored with
                MachineName = Some "Bench 01"
                WorkspacePath = Some root
                TrustLevel = Some "none"
                AllowedEnvironmentVariables = Some [ "TEST_KEY" ]
                MaxImportBytes = Some 0L; BrowserPort = Some 43822 }
        let saved =
            match ClientConfig.save snapshot changed with
            | Ok x -> x
            | Error e -> failwith e
        saved.Stored |> shouldEqual changed
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let bits = File.GetUnixFileMode(path)
            bits.HasFlag(UnixFileMode.GroupRead) |> shouldEqual false
            bits.HasFlag(UnixFileMode.OtherRead) |> shouldEqual false
        File.ReadAllText(path).Contains("\"futureField\"") |> shouldEqual true
        let reloaded =
            match ClientConfig.loadAt path with
            | Ok x -> x
            | Error e -> failwith e
        reloaded.Stored |> shouldEqual changed
        // Stale state must not silently clobber newer changes.
        match ClientConfig.save snapshot { changed with MachineName = Some "stale" } with
        | Error message -> message.Contains("changed") |> shouldEqual true
        | Ok _ -> Assert.Fail("Expected an edit conflict")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``invalid configuration is rejected without changing its bytes`` () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-bad-config-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let path = Path.Combine(root, "config.json")
    try
        for invalid in [ ""; "{"; """{"schemaVersion":2}"""; """{"schemaVersion":1,"trustLevel":"super"}"""; """{"schemaVersion":1,"maxImportBytes":-1}""" ] do
            File.WriteAllText(path, invalid)
            match ClientConfig.loadAt path with
            | Ok _ -> Assert.Fail("Invalid config was accepted")
            | Error _ -> ()
            File.ReadAllText(path) |> shouldEqual invalid
    finally
        Directory.Delete(root, true)

[<Test>]
let ``configured defaults and startup flags use deterministic precedence`` () =
    let stored =
        { ClientConfig.empty with
            MachineName = Some "Home"
            WorkspacePath = Some "/tmp"
            TrustLevel = Some "none"
            MaxImportBytes = Some 10L
            BrowserPort = None
            AllowedEnvironmentVariables = Some [ "CUSTOM_VAR" ] }
    match ClientConfig.resolve stored [ "--machine-name"; "Worker"; "--trust"; "partial"; "--max-import-bytes"; "64"; "--browser-port"; "43822" ] with
    | Error e -> failwith e
    | Ok effective ->
        effective.MachineName |> shouldEqual "Worker"
        effective.WorkspacePath |> shouldEqual "/tmp"
        effective.TrustLevel |> shouldEqual "partial"
        effective.MaxImportBytes |> shouldEqual 64L
        effective.BrowserPort |> shouldEqual (Some 43822)
        effective.Overrides.Contains("browserPort") |> shouldEqual true
        effective.AllowedEnvironmentVariables |> List.contains "CUSTOM_VAR" |> shouldEqual true
        effective.Overrides.Contains("machineName") |> shouldEqual true
        effective.Overrides.Contains("workspacePath") |> shouldEqual false

[<Test>]
let ``config field validation prevents invalid machine names trust and variables`` () =
    match ClientConfig.updateKey "machine-name" "bad\nname" ClientConfig.empty with
    | Ok _ -> Assert.Fail("Accepted control characters")
    | Error _ -> ()
    match ClientConfig.updateKey "trust-level" "super" ClientConfig.empty with
    | Ok _ -> Assert.Fail("Accepted invalid trust")
    | Error _ -> ()
    match ClientConfig.updateKey "allowed-environment-variables" "VALID,INVALID-KEY" ClientConfig.empty with
    | Ok _ -> Assert.Fail("Accepted invalid variable")
    | Error _ -> ()
    ClientConfig.updateKey "machine-name" "Home Server" ClientConfig.empty
    |> Result.map (fun stored -> stored.MachineName)
    |> shouldEqual (Ok (Some "Home Server"))

[<Test>]
let ``CLI override does not mutate persisted configuration`` () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-config-override-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let path = Path.Combine(root, "config.json")
    try
        let initial = ClientConfig.loadAt path |> Result.defaultWith failwith
        let stored = { ClientConfig.empty with MachineName = Some "Configured"; WorkspacePath = Some root }
        let saved = ClientConfig.save initial stored |> Result.defaultWith failwith
        let original = File.ReadAllText(path)
        let runtime = ClientConfig.resolve saved.Stored [ "--machine-name"; "Temporary" ] |> Result.defaultWith failwith
        runtime.MachineName |> shouldEqual "Temporary"
        let afterwards = File.ReadAllText(path)
        afterwards |> shouldEqual original
        (ClientConfig.loadAt path |> Result.defaultWith failwith).Stored.MachineName |> shouldEqual (Some "Configured")
    finally
        Directory.Delete(root, true)


[<Test>]
let browserPortValidation () =
    let blank = ClientConfig.resolve ClientConfig.empty []
    match blank with
    | Ok effective -> effective.BrowserPort |> shouldEqual None
    | Error e -> Assert.Fail(e)
    match ClientConfig.updateKey "browser-port" "43822" ClientConfig.empty with
    | Error e -> Assert.Fail(e)
    | Ok stored -> stored.BrowserPort |> shouldEqual (Some 43822)
    for value in [ "0"; "1023"; "65536"; "nope" ] do
        match ClientConfig.updateKey "browser-port" value ClientConfig.empty with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Accepted invalid port: " + value)

[<Test>]
let nativeHealthDefaultsDisabledAndUsesPositive600SecondTimeout () =
    let config = ClientConfig.resolve ClientConfig.empty [] |> Result.defaultWith failwith
    config.NativeHealthEnabled |> shouldEqual false
    config.NativeHealthTimeoutSeconds |> shouldEqual 600

[<Test>]
let nativeHealthConfigSetUnsetAndValidation () =
    let enabled =
        ClientConfig.updateKey "native-health-enabled" "true" ClientConfig.empty
        |> Result.defaultWith failwith
    enabled.NativeHealthEnabled |> shouldEqual (Some true)
    let configured =
        ClientConfig.updateKey "native-health-timeout-seconds" "900" enabled
        |> Result.defaultWith failwith
    let effective = ClientConfig.resolve configured [] |> Result.defaultWith failwith
    effective.NativeHealthEnabled |> shouldEqual true
    effective.NativeHealthTimeoutSeconds |> shouldEqual 900
    let reverted =
        configured
        |> ClientConfig.unsetKey "native-health-enabled"
        |> Result.defaultWith failwith
    let defaults = ClientConfig.resolve reverted [] |> Result.defaultWith failwith
    defaults.NativeHealthEnabled |> shouldEqual false
    for invalid in [ "0"; "-1"; "604801"; "abc" ] do
        match ClientConfig.updateKey "native-health-timeout-seconds" invalid configured with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Accepted invalid Native Health timeout: " + invalid)
    for invalid in [ "1"; "yes"; "on"; "" ] do
        match ClientConfig.updateKey "native-health-enabled" invalid configured with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Accepted non-Boolean Native Health setting: " + invalid)

[<Test>]
let ``workspace setting rejects nonexistent root instead of deferring to interactive startup prompt`` () =
    let missing = Path.Combine(Path.GetTempPath(), "jarvis-settings-missing-" + Guid.NewGuid().ToString("N"))
    match ClientConfig.updateKey "workspace-path" missing ClientConfig.empty with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Nonexistent root was accepted, but startup getDir will prompt instead of using it")

[<Test>]
let ``workspace setting accepts an existing root with spaces`` () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis settings root " + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    try
        let changed = ClientConfig.updateKey "workspace-path" directory ClientConfig.empty |> Result.defaultWith failwith
        changed.WorkspacePath |> shouldEqual (Some directory)
    finally
        Directory.Delete(directory, true)


[<Test>]
let persistedWorkspaceRootDrivesRealProjectEnumerationAfterFreshRuntime () =
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-settings-runtime-" + Guid.NewGuid().ToString("N"))
    let rootA = Path.Combine(sandbox, "root A")
    let rootB = Path.Combine(sandbox, "root B")
    let file = Path.Combine(sandbox, "xdg", "Jarvis", "config.json")
    Directory.CreateDirectory(Path.Combine(rootA, "A1")) |> ignore
    Directory.CreateDirectory(Path.Combine(rootB, "B1")) |> ignore
    try
        let a = ClientConfig.applyAt file None "workspace-path" (Some rootA) |> Result.defaultWith failwith
        let previous = File.ReadAllText(file)
        let b = ClientConfig.applyAt file (Some a.Revision) "workspace-path" (Some rootB) |> Result.defaultWith failwith
        Assert.That(File.ReadAllText(file), Is.Not.EqualTo(previous))
        let fresh = ClientConfig.loadAt file |> Result.defaultWith failwith
        fresh.Stored.WorkspacePath |> shouldEqual (Some rootB)
        let effective = ClientConfig.resolve fresh.Stored [] |> Result.defaultWith failwith
        effective.WorkspacePath |> shouldEqual rootB
        let rt = new Runtime(effective.WorkspacePath)
        match Client.ProjectBrowser.listProjects rt with
        | Error issue -> Assert.Fail(sprintf "Runtime list_projects failed: %A" issue)
        | Ok projects ->
            let names = projects |> Seq.map (fun (ProjectName name) -> name) |> Set.ofSeq
            Assert.That(names.Contains "B1", Is.True)
            Assert.That(names.Contains "A1", Is.False)
            Assert.That(names.Contains "root B", Is.False)
        let overridden =
            ClientConfig.resolve fresh.Stored ["--path"; rootA] |> Result.defaultWith failwith
        overridden.WorkspacePath |> shouldEqual rootA
        Assert.That(overridden.Overrides.Contains("workspacePath"), Is.True)
        (ClientConfig.loadAt file |> Result.defaultWith failwith).Stored.WorkspacePath |> shouldEqual (Some rootB)
        let stale = ClientConfig.applyAt file (Some a.Revision) "machine-name" (Some "wrong")
        Assert.That(stale.IsError, Is.True)
        (ClientConfig.loadAt file |> Result.defaultWith failwith).Stored.MachineName |> shouldEqual None
        Assert.That((ClientConfig.loadAt file |> Result.defaultWith failwith).Revision, Is.EqualTo(b.Revision))
    finally
        Directory.Delete(sandbox, true)

[<Test>]
let allEightSettingsRoundtripThroughCommonBoundaryAndUnset () =
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-settings-matrix-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(sandbox) |> ignore
    let file = Path.Combine(sandbox, "config", "config.json")
    let edits =
        [ "machine-name", "Disposable W24"
          "workspace-path", sandbox
          "trust-level", "none"
          "allowed-environment-variables", "SAFE_A,SAFE_B"
          "max-import-bytes", "2048"
          "browser-port", "43822"
          "native-health-enabled", "true"
          "native-health-timeout-seconds", "601" ]
    try
        let mutable revision = None
        for key, value in edits do
            let updated = ClientConfig.applyAt file revision key (Some value) |> Result.defaultWith failwith
            revision <- Some updated.Revision
            let reloaded = ClientConfig.loadAt file |> Result.defaultWith failwith
            reloaded |> shouldEqual updated
        let values = (ClientConfig.loadAt file |> Result.defaultWith failwith).Stored
        values.MachineName |> shouldEqual (Some "Disposable W24")
        values.WorkspacePath |> shouldEqual (Some sandbox)
        values.TrustLevel |> shouldEqual (Some "none")
        values.AllowedEnvironmentVariables |> shouldEqual (Some ["SAFE_A"; "SAFE_B"])
        values.MaxImportBytes |> shouldEqual (Some 2048L)
        values.BrowserPort |> shouldEqual (Some 43822)
        values.NativeHealthEnabled |> shouldEqual (Some true)
        values.NativeHealthTimeoutSeconds |> shouldEqual (Some 601)
        for key, _ in edits do
            let updated = ClientConfig.applyAt file revision key None |> Result.defaultWith failwith
            revision <- Some updated.Revision
        (ClientConfig.loadAt file |> Result.defaultWith failwith).Stored |> shouldEqual ClientConfig.empty
        let effective = ClientConfig.resolve ClientConfig.empty [] |> Result.defaultWith failwith
        Assert.That(effective.NativeHealthEnabled, Is.False)
        Assert.That(effective.NativeHealthTimeoutSeconds, Is.EqualTo(600))
    finally
        Directory.Delete(sandbox, true)

[<Test>]
let simultaneousSnapshotsNeverBothCommitAndOverwriteEachOther () =
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-settings-cas-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(sandbox) |> ignore
    let file = Path.Combine(sandbox, "config.json")
    try
        for round = 1 to 25 do
            let snapshot = ClientConfig.loadAt file |> Result.defaultWith failwith
            let a = System.Threading.Tasks.Task.Run(fun () ->
                ClientConfig.save snapshot {snapshot.Stored with MachineName = Some ("A" + string round)})
            let b = System.Threading.Tasks.Task.Run(fun () ->
                ClientConfig.save snapshot {snapshot.Stored with MachineName = Some ("B" + string round)})
            System.Threading.Tasks.Task.WaitAll([| a :> System.Threading.Tasks.Task; b :> System.Threading.Tasks.Task |])
            let committed = [a.Result; b.Result] |> List.choose (function Ok value -> Some value | _ -> None)
            Assert.That(committed.Length, Is.EqualTo(1), "Exactly one writer may win each revision CAS")
            let persisted = ClientConfig.loadAt file |> Result.defaultWith failwith
            persisted |> shouldEqual committed.Head
    finally
        Directory.Delete(sandbox, true)


[<Test>]
let remoteToolSettingsBoundaryRefusesPrivilegeEscalationWithoutLocalConfirmation () =
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-settings-remote-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(sandbox) |> ignore
    let file = Path.Combine(sandbox, "config.json")
    try
        let baseline = ClientConfig.applyAt file None "trust-level" (Some "none") |> Result.defaultWith failwith
        for key, value in
            [ "trust-level", Some "partial"
              "trust-level", Some "full"
              "trust-level", None
              "allowed-environment-variables", Some "SECRET" ] do
            let outcome = ClientConfig.applyRemoteAt file (Some baseline.Revision) key value
            Assert.That(outcome.IsError, Is.True, "Unconfirmed remote privilege expansion must fail")
            Assert.That((ClientConfig.loadAt file |> Result.defaultWith failwith).Revision, Is.EqualTo(baseline.Revision))
        let reduced = ClientConfig.applyRemoteAt file (Some baseline.Revision) "trust-level" (Some "none")
        Assert.That(reduced.IsOk, Is.True)
    finally
        Directory.Delete(sandbox, true)

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.DependencyInjection

type SettingsRegistrationProbe() =
    let observed = System.Collections.Concurrent.ConcurrentQueue<string>()
    member _.Record(name: string) = observed.Enqueue(name)
    member _.Names = observed.ToArray()

type SettingsRegistrationHub(probe: SettingsRegistrationProbe) =
    inherit Hub()
    member _.Connect(registration: Common.SignalR.ClientRegistration) =
        probe.Record(registration.DeviceName)
        System.Threading.Tasks.Task.FromResult<Common.SignalR.ClientRegistrationResult>(
            { Generation = 1L
              RegisteredAt = DateTimeOffset.UtcNow
              RegistrationReason = "isolated settings test"
              ServerProtocolVersion = "test"
              EnabledCommands = [] })

[<Test>]
let configuredMachineNameReRegistersThroughRealDisposableSignalRHub () = task {
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-settings-registration-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(sandbox) |> ignore
    let file = Path.Combine(sandbox, "xdg", "Jarvis", "config.json")
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    builder.Services.AddSingleton<SettingsRegistrationProbe>() |> ignore
    builder.Services.AddSignalR() |> ignore
    use server = builder.Build()
    server.MapHub<SettingsRegistrationHub>("/settings") |> ignore
    do! server.StartAsync()
    let endpoint = server.Urls |> Seq.head
    let connection = HubConnectionBuilder().WithUrl(endpoint + "/settings").Build()
    try
        let first = ClientConfig.applyAt file None "machine-name" (Some "W24-First") |> Result.defaultWith failwith
        let loadMachineName () =
            let stored = (ClientConfig.loadAt file |> Result.defaultWith failwith).Stored
            (ClientConfig.resolve stored [] |> Result.defaultWith failwith).MachineName
        do! connection.StartAsync()
        let tui = Client.ConsoleTui.ConsoleTui()
        let! initialRegistration = Program.register tui connection "disposable-device-w24" loadMachineName
        Assert.That(initialRegistration, Is.True)
        let second = ClientConfig.applyAt file (Some first.Revision) "machine-name" (Some "W24-Second")
                     |> Result.defaultWith failwith
        let! subsequentRegistration = Program.register tui connection "disposable-device-w24" loadMachineName
        Assert.That(subsequentRegistration, Is.True)
        let probe = server.Services.GetRequiredService<SettingsRegistrationProbe>()
        probe.Names |> shouldEqual [| "W24-First"; "W24-Second" |]
        let cliOverride = ClientConfig.resolve second.Stored ["--machine-name"; "W24-CLI"]
                          |> Result.defaultWith failwith
        Assert.That(cliOverride.MachineName, Is.EqualTo("W24-CLI"))
        Assert.That(cliOverride.Overrides.Contains("machineName"), Is.True)
        (ClientConfig.loadAt file |> Result.defaultWith failwith).Stored.MachineName |> shouldEqual (Some "W24-Second")
    finally
        connection.DisposeAsync().AsTask().GetAwaiter().GetResult()
        server.StopAsync().GetAwaiter().GetResult()
        Directory.Delete(sandbox, true)
}


[<Test>]
let realCliProcessesSaveReloadAndUnsetDisposableWorkspaceAndMachineName () =
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-settings-cli-" + Guid.NewGuid().ToString("N"))
    let configRoot = Path.Combine(sandbox, "xdg")
    let home = Path.Combine(sandbox, "home")
    let rootA = Path.Combine(sandbox, "projects A")
    let rootB = Path.Combine(sandbox, "projects B")
    for path in [home; configRoot; rootA; rootB] do Directory.CreateDirectory(path) |> ignore
    let dll = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Client", "bin", "Release", "net11.0", "JarvisClient.dll"))
    let expectedPath = Path.Combine(configRoot, "Jarvis", "config.json")
    let run arguments =
        let psi = System.Diagnostics.ProcessStartInfo("dotnet")
        psi.ArgumentList.Add(dll)
        for arg in arguments do psi.ArgumentList.Add(arg)
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.Environment["XDG_CONFIG_HOME"] <- configRoot
        psi.Environment["HOME"] <- home
        psi.Environment["DOTNET_CLI_HOME"] <- home
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
        use proc = System.Diagnostics.Process.Start(psi)
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        if not (proc.WaitForExit(10000)) then
            proc.Kill(true)
            Assert.Fail("Disposable configuration CLI process exceeded 10 seconds")
        (proc.ExitCode, stdout, stderr)
    try
        Assert.That(File.Exists(dll), Is.True, "The source-bound Release client executable must exist")
        let exitPath, printedPath, _ = run ["config"; "path"]
        Assert.That(exitPath, Is.Zero)
        Assert.That(printedPath.Trim(), Is.EqualTo(expectedPath))
        let exitA, _, stderrA = run ["config"; "set"; "workspace-path"; rootA]
        Assert.That(exitA, Is.Zero, stderrA)
        let exitMachine, _, stderrMachine = run ["config"; "set"; "machine-name"; "W24 cli persisted"]
        Assert.That(exitMachine, Is.Zero, stderrMachine)
        let exitB, _, stderrB = run ["config"; "set"; "workspace-path"; rootB]
        Assert.That(exitB, Is.Zero, stderrB)
        let exitShow, output, showError = run ["config"; "show"]
        Assert.That(exitShow, Is.Zero, showError)
        Assert.That(output.Contains(rootB), Is.True)
        Assert.That(output.Contains("W24 cli persisted"), Is.True)
        let readback = ClientConfig.loadAt expectedPath |> Result.defaultWith failwith
        Assert.That(readback.Stored.MachineName, Is.EqualTo(Some "W24 cli persisted"))
        Assert.That(readback.Stored.WorkspacePath, Is.EqualTo(Some rootB))
        let exitUnset, _, stderrUnset = run ["config"; "unset"; "machine-name"]
        Assert.That(exitUnset, Is.Zero, stderrUnset)
        Assert.That((ClientConfig.loadAt expectedPath |> Result.defaultWith failwith).Stored.MachineName, Is.EqualTo(None))
        let exitBad, _, _ = run ["config"; "set"; "workspace-path"; Path.Combine(sandbox, "missing")]
        Assert.That(exitBad, Is.EqualTo(2))
        Assert.That((ClientConfig.loadAt expectedPath |> Result.defaultWith failwith).Stored.WorkspacePath, Is.EqualTo(Some rootB))
    finally
        Directory.Delete(sandbox, true)


[<Test>]
let malformedOrSymlinkedConfigFailsWithoutMutatingDestination () =
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-settings-negative-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(sandbox) |> ignore
    let file = Path.Combine(sandbox, "config.json")
    let target = Path.Combine(sandbox, "target.json")
    try
        let malformed = """{"schemaVersion":1,"machineName":"""
        File.WriteAllText(file, malformed)
        let refused = ClientConfig.applyAt file None "machine-name" (Some "ShouldNotWrite")
        Assert.That(refused.IsError, Is.True)
        Assert.That(File.ReadAllText(file), Is.EqualTo(malformed))
        File.Delete(file)
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let targetBytes = """{"schemaVersion":1,"machineName":"Keep"}"""
            File.WriteAllText(target, targetBytes)
            File.CreateSymbolicLink(file, target) |> ignore
            let linked = ClientConfig.applyAt file None "machine-name" (Some "OverwriteDenied")
            Assert.That(linked.IsError, Is.True, "Symlinked config must fail closed")
            Assert.That(File.ReadAllText(target), Is.EqualTo(targetBytes))
    finally
        Directory.Delete(sandbox, true)


let private driveOfflineSettingsTui root (keys: string list) =
    if not (OperatingSystem.IsLinux()) || not (File.Exists("/usr/bin/script")) then
        Assert.Ignore("Linux util-linux script PTY is required for actual keyboard settings exercise")
    let harness = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "SettingsHarness", "bin", "Release", "net11.0", "SettingsHarness.dll"))
    if not (File.Exists harness) then
        Assert.Ignore("The separately built Release offline TUI harness is unavailable")
    let home = Path.Combine(root, "home")
    let xdg = Path.Combine(root, "xdg")
    Directory.CreateDirectory(home) |> ignore
    Directory.CreateDirectory(xdg) |> ignore
    let psi = System.Diagnostics.ProcessStartInfo("/usr/bin/script")
    for arg in ["-q"; "-e"; "-c"; "stty rows 40 cols 100; exec dotnet " + harness; "/dev/null"] do
        psi.ArgumentList.Add(arg)
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.Environment["HOME"] <- home
    psi.Environment["XDG_CONFIG_HOME"] <- xdg
    psi.Environment["DOTNET_CLI_HOME"] <- home
    psi.Environment["JARVIS_W24_ISOLATED_TUI"] <- "yes"
    psi.Environment["TERM"] <- "xterm"
    psi.Environment["COLUMNS"] <- "100"
    psi.Environment["LINES"] <- "40"
    use child = System.Diagnostics.Process.Start(psi)
    let outputTask = child.StandardOutput.ReadToEndAsync()
    let errorTask = child.StandardError.ReadToEndAsync()
    System.Threading.Thread.Sleep(1100)
    for key in keys do
        child.StandardInput.Write(key)
        child.StandardInput.Flush()
        System.Threading.Thread.Sleep(180)
    System.Threading.Thread.Sleep(650)
    child.StandardInput.Write("q")
    child.StandardInput.Flush()
    if not (child.WaitForExit(5500)) then
        child.Kill(true)
        child.WaitForExit()
        Assert.Fail("Isolated offline TUI did not terminate after q")
    let output = outputTask.GetAwaiter().GetResult()
    let errors = errorTask.GetAwaiter().GetResult()
    Assert.That(child.ExitCode, Is.Zero, $"Isolated TUI returned {child.ExitCode}: {errors}; output suffix={output.Substring(max 0 (output.Length - 500))}")
    let escaped = output.Replace("\u001b", "<ESC>")
    Assert.That(output.Contains("Settings"), Is.True, sprintf "TUI settings view must render. Captured %d chars: stdout=%s stderr=%s" output.Length (escaped.Substring(0, min 1200 escaped.Length)) errors)
    Path.Combine(xdg, "Jarvis", "config.json"), output

[<Test>]
let actualOfflineTuiKeyboardMachineNameSaveAndReopen () =
    let sandbox = Path.Combine(Path.GetTempPath(), "jarvis-tui-machine-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(sandbox) |> ignore
    try
        let path, _ = driveOfflineSettingsTui sandbox ["g"; "\r"; "W24-TUI-Machine"; "\r"; "s"]
        let persisted = ClientConfig.loadAt path |> Result.defaultWith failwith
        Assert.That(persisted.Stored.MachineName, Is.EqualTo(Some "W24-TUI-Machine"))
        let _, reopened = driveOfflineSettingsTui sandbox ["g"]
        Assert.That(reopened.Contains("W24-TUI-Machine"), Is.True, "New TUI process must load the persisted value")
    finally
        Directory.Delete(sandbox, true)

[<Test>]
let actualOfflineTuiKeyboardWorkspacePathSaveAndReopen () =
    let sandbox = Path.Combine(Path.GetTempPath(), "w24-root-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    let workspace = Path.Combine(sandbox, "workspace B with spaces")
    Directory.CreateDirectory(workspace) |> ignore
    try
        let path, _ = driveOfflineSettingsTui sandbox ["g"; "\u001b[B"; "\r"; workspace; "\r"; "s"]
        let persisted = ClientConfig.loadAt path |> Result.defaultWith failwith
        Assert.That(persisted.Stored.WorkspacePath, Is.EqualTo(Some workspace))
        let _, reopened = driveOfflineSettingsTui sandbox ["g"]
        Assert.That(reopened.Contains("Settings"), Is.True, "Fresh TUI must reopen settings")
        (ClientConfig.loadAt path |> Result.defaultWith failwith).Stored.WorkspacePath |> shouldEqual (Some workspace)
    finally
        Directory.Delete(sandbox, true)


[<Test>]
let actualOfflineTuiKeyboardAllEightFieldsSaveThenRestoreDefaults () =
    let sandbox = Path.Combine(Path.GetTempPath(), "w24-all-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    let workspace = Path.Combine(sandbox, "projects B")
    Directory.CreateDirectory(Path.Combine(workspace, "B1")) |> ignore
    let values =
        [ "W24 All Settings"
          workspace
          "none"
          "SAFE_A,SAFE_B"
          "8192"
          "43822"
          "true"
          "901" ]
    let changeKeys =
        ["g"]
        @ (values
           |> List.mapi (fun index value ->
               (if index = 0 then [] else ["\u001b[B"]) @ ["\r"; value; "\r"])
           |> List.concat)
        @ ["s"; "y"] // env allowlist expansion must require explicit local approval
    try
        let path, _ = driveOfflineSettingsTui sandbox changeKeys
        let persisted = ClientConfig.loadAt path |> Result.defaultWith failwith
        Assert.That(persisted.Stored.MachineName, Is.EqualTo(Some "W24 All Settings"))
        Assert.That(persisted.Stored.WorkspacePath, Is.EqualTo(Some workspace))
        Assert.That(persisted.Stored.TrustLevel, Is.EqualTo(Some "none"))
        persisted.Stored.AllowedEnvironmentVariables |> shouldEqual (Some ["SAFE_A"; "SAFE_B"])
        persisted.Stored.MaxImportBytes |> shouldEqual (Some 8192L)
        persisted.Stored.BrowserPort |> shouldEqual (Some 43822)
        persisted.Stored.NativeHealthEnabled |> shouldEqual (Some true)
        persisted.Stored.NativeHealthTimeoutSeconds |> shouldEqual (Some 901)
        let fresh = ClientConfig.resolve persisted.Stored [] |> Result.defaultWith failwith
        let rt = new Runtime(fresh.WorkspacePath)
        match Client.ProjectBrowser.listProjects rt with
        | Error issue -> Assert.Fail(sprintf "Reopened runtime failed: %A" issue)
        | Ok names ->
            Assert.That(names |> Seq.exists (fun (ProjectName name) -> name = "B1"), Is.True)
        let resetKeys =
            ["g"]
            @ ([0..7] |> List.collect (fun index -> (if index = 0 then [] else ["\u001b[B"]) @ ["u"]))
            @ ["s"]
        let _, reopened = driveOfflineSettingsTui sandbox resetKeys
        Assert.That(reopened.Contains("Settings"), Is.True)
        (ClientConfig.loadAt path |> Result.defaultWith failwith).Stored |> shouldEqual ClientConfig.empty
    finally
        Directory.Delete(sandbox, true)


[<Test>]
let saveRejectsNewMissingWorkspaceAndExternalEmptyFileWithoutMutation () =
    let sandbox = Path.Combine(Path.GetTempPath(), "w24-save-invalid-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(sandbox) |> ignore
    let file = Path.Combine(sandbox, "config.json")
    try
        let before = ClientConfig.loadAt file |> Result.defaultWith failwith
        let missing = Path.Combine(sandbox, "missing root")
        let badRoot = { ClientConfig.empty with MachineName = Some "should-not-commit"; WorkspacePath = Some missing }
        Assert.That((ClientConfig.save before badRoot).IsError, Is.True)
        Assert.That(File.Exists(file), Is.False)
        File.WriteAllText(file, "")
        let replaced = ClientConfig.save before { ClientConfig.empty with MachineName = Some "should-not-overwrite-corrupt" }
        Assert.That(replaced.IsError, Is.True)
        Assert.That(File.ReadAllText(file), Is.EqualTo(""))
    finally
        Directory.Delete(sandbox, true)


[<Test>]
let persistedSettingsReachRealRuntimePermissionImportEnvironmentHealthAndBrowserConsumers () =
    let sandbox = Path.Combine(Path.GetTempPath(), "w24-runtime-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    let root = Path.Combine(sandbox, "workspace")
    Directory.CreateDirectory(root) |> ignore
    let file = Path.Combine(sandbox, "xdg", "Jarvis", "config.json")
    use listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
    listener.Stop()
    let edits =
        [ "workspace-path", root
          "machine-name", "W24 Runtime Device"
          "trust-level", "none"
          "allowed-environment-variables", "W24_TOKEN"
          "max-import-bytes", "8192"
          "browser-port", string port
          "native-health-enabled", "true"
          "native-health-timeout-seconds", "601" ]
    try
        let mutable revision = None
        for key, value in edits do
            let saved = ClientConfig.applyAt file revision key (Some value) |> Result.defaultWith failwith
            revision <- Some saved.Revision
        let stored = (ClientConfig.loadAt file |> Result.defaultWith failwith).Stored
        let effective = ClientConfig.resolve stored [] |> Result.defaultWith failwith
        let trust = Client.TrustLevel.parse effective.TrustLevel |> Result.defaultWith failwith
        let runtime = new Runtime(effective.WorkspacePath, new Client.ConsoleTui.ConsoleTui(), trust, effective.MaxImportBytes)
        Assert.That((runtime :> PermissionIO).TrustLevel, Is.EqualTo(NoTrust))
        Assert.That(runtime.MaxImportBytes, Is.EqualTo(8192L))
        Assert.That(effective.MachineName, Is.EqualTo("W24 Runtime Device"))
        let startInfo = System.Diagnostics.ProcessStartInfo("/usr/bin/env")
        startInfo.Environment["W24_TOKEN"] <- "token-w24-only"
        startInfo.Environment["W24_PASSWORD"] <- "must-not-leak"
        Client.ProcessEnvironment.applyWithAllowed effective.AllowedEnvironmentVariables startInfo
        Assert.That(startInfo.Environment.ContainsKey("W24_TOKEN"), Is.True)
        Assert.That(startInfo.Environment.ContainsKey("W24_PASSWORD"), Is.False)
        let health = Client.NativeHealth.NativeHealth.settingsFromStored stored
        Assert.That(health.Enabled, Is.True)
        Assert.That(health.InactivitySeconds, Is.EqualTo(601))
        use bridge = new Client.BrowserBridge.Service(effective.BrowserPort.Value, "isolated-test-secret")
        bridge.Start()
        use http = new System.Net.Http.HttpClient()
        http.Timeout <- TimeSpan.FromSeconds(3.0)
        let response = http.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult()
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden),
            "Actual loopback browser listener must reject unauthenticated HTTP, not merely expose a port number")
    finally
        Directory.Delete(sandbox, true)
