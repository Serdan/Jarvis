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
                MaxImportBytes = Some 0L }
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
            AllowedEnvironmentVariables = Some [ "CUSTOM_VAR" ] }
    match ClientConfig.resolve stored [ "--machine-name"; "Worker"; "--trust"; "partial"; "--max-import-bytes"; "64" ] with
    | Error e -> failwith e
    | Ok effective ->
        effective.MachineName |> shouldEqual "Worker"
        effective.WorkspacePath |> shouldEqual "/tmp"
        effective.TrustLevel |> shouldEqual "partial"
        effective.MaxImportBytes |> shouldEqual 64L
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
