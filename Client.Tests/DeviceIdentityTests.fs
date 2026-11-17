module ClientDeviceIdentityTests

open System
open System.IO
open NUnit.Framework
open Client
open Common.SignalR

[<Test>]
let concurrent_first_starts_share_durable_identifiers_but_never_process_route () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-device-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    try
        let machinePath = Path.Combine(directory, "roaming", "device-id")
        let installationPath = Path.Combine(directory, "local", "installation-id")
        let identities =
            [| 1 .. 64 |]
            |> Array.Parallel.map (fun _ -> DeviceIdentity.createInstanceAt machinePath installationPath)
        Assert.That(identities |> Array.map _.MachineId |> Array.distinct |> Array.length, Is.EqualTo(1))
        Assert.That(identities |> Array.map _.InstallationId |> Array.distinct |> Array.length, Is.EqualTo(1))
        Assert.That(identities |> Array.map _.InstanceId |> Array.distinct |> Array.length, Is.EqualTo(64))
        Assert.That(identities |> Array.map _.RouteId |> Array.distinct |> Array.length, Is.EqualTo(64))
        let restarted = DeviceIdentity.createInstanceAt machinePath installationPath
        Assert.That(restarted.MachineId, Is.EqualTo(identities[0].MachineId))
        Assert.That(restarted.InstallationId, Is.EqualTo(identities[0].InstallationId))
        Assert.That(restarted.InstanceId, Is.Not.EqualTo(identities[0].InstanceId))
        let parsed = DeviceRouteIdentity.tryParse restarted.RouteId |> Option.get
        Assert.That(parsed.MachineId, Is.EqualTo(restarted.MachineId))
        Assert.That(parsed.InstallationId, Is.EqualTo(restarted.InstallationId))
        Assert.That(parsed.InstanceId, Is.EqualTo(restarted.InstanceId))
    finally
        Directory.Delete(directory, true)

[<Test>]
let corrupt_identity_cannot_silently_rotate_registered_machine () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-device-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    try
        let path = Path.Combine(directory, "device-id")
        File.WriteAllText(path, "not-a-uuid")
        Assert.Throws<InvalidOperationException>(
            TestDelegate(fun () -> DeviceIdentity.getOrCreateAt path |> ignore))
        |> ignore
        Assert.That(File.ReadAllText(path), Is.EqualTo("not-a-uuid"))
    finally
        Directory.Delete(directory, true)

[<Test>]
let legacy_machine_uuid_is_accepted_while_malformed_routes_are_not () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-device-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    try
        let path = Path.Combine(directory, "device-id")
        let id = Guid.NewGuid()
        File.WriteAllText(path, id.ToString("B"))
        Assert.That(DeviceIdentity.getOrCreateAt path, Is.EqualTo(id.ToString("D")))
        Assert.That(DeviceRouteIdentity.tryParse("jarvis2:invalid"), Is.EqualTo(None))
        Assert.That(DeviceRouteIdentity.tryParse(id.ToString("D")), Is.EqualTo(None))
    finally
        Directory.Delete(directory, true)

[<Test>]
let concurrent_browser_bridge_pairing_key_creation_is_atomic () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-browser-secret-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    try
        let path = Path.Combine(directory, "browser-bridge.key")
        let keys =
            [| 1 .. 64 |]
            |> Array.Parallel.map (fun _ -> Client.BrowserBridge.getOrCreateSecretAt path)
        Assert.That(keys |> Array.distinct |> Array.length, Is.EqualTo(1))
        Assert.That(keys[0].Length, Is.EqualTo(64))
        Assert.That(File.ReadAllText(path).Trim(), Is.EqualTo(keys[0]))
    finally
        Directory.Delete(directory, true)

[<Test>]
let concurrent_stale_configuration_updates_never_both_commit () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-config-race-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    try
        let path = Path.Combine(directory, "config.json")
        let initial = ClientConfig.loadAt path |> Result.defaultWith failwith
        let firstSave = ClientConfig.save initial { initial.Stored with MachineName = Some "Initial" }
                        |> Result.defaultWith failwith
        let snapshot = ClientConfig.loadAt path |> Result.defaultWith failwith
        let results =
            [| 1 .. 24 |]
            |> Array.Parallel.map (fun index ->
                ClientConfig.save snapshot { snapshot.Stored with MachineName = Some ("Changed " + string index) })
        Assert.That(results |> Array.filter Result.isOk |> Array.length, Is.EqualTo(1))
        Assert.That(results |> Array.filter Result.isError |> Array.length, Is.EqualTo(23))
        let current = ClientConfig.loadAt path |> Result.defaultWith failwith
        Assert.That(current.Revision, Is.Not.EqualTo(firstSave.Revision))
        Assert.That(current.Stored.MachineName.Value, Does.StartWith("Changed "))
    finally
        Directory.Delete(directory, true)
