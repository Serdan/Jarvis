module ServerDeviceRouteIdentityTests

open System
open NUnit.Framework
open Common
open Common.SignalR
open Server.Services

let private route machine installation =
    DeviceRouteIdentity.create machine installation (Guid.NewGuid().ToString("D"))

let private registration routeId =
    { DeviceId = routeId
      DeviceName = "Shared workstation"
      ProtocolVersion = AgentProtocol.version
      ClientVersion = "test"
      SupportedCommands = [ "ListProjects" ] }

[<Test>]
let concurrent_instances_route_and_disconnect_independently () =
    let users = UserService()
    let machine = Guid.NewGuid().ToString("D")
    let installation = Guid.NewGuid().ToString("D")
    let first = route machine installation
    let second = route machine installation
    users.TransportConnected("alice", "first-connection")
    users.Register("alice", registration first, "first-connection") |> ignore
    users.TransportConnected("alice", "second-connection")
    users.Register("alice", registration second, "second-connection") |> ignore
    Assert.That(users.ListDevices("alice").Length, Is.EqualTo(2))
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some first))
    Assert.That(users.SetDefaultDevice("alice", second), Is.True)
    match users.GetSelectedSession("alice", None, None) with
    | ValueSome session -> Assert.That(session.ConnectionId, Is.EqualTo(Some "second-connection"))
    | _ -> Assert.Fail("Selected route absent")
    users.Disconnect("first-connection", None)
    match users.GetSessionByDevice("alice", second) with
    | ValueSome session ->
        Assert.That(session.State, Is.EqualTo(Registered))
        Assert.That(session.ConnectionId, Is.EqualTo(Some "second-connection"))
    | _ -> Assert.Fail("One process disconnect must not remove another process")

[<Test>]
let copied_machine_id_from_different_installation_is_rejected_without_eviction () =
    let users = UserService()
    let machine = Guid.NewGuid().ToString("D")
    let original = route machine (Guid.NewGuid().ToString("D"))
    let copy = route machine (Guid.NewGuid().ToString("D"))
    users.TransportConnected("alice", "first-connection")
    users.Register("alice", registration original, "first-connection") |> ignore
    users.TransportConnected("alice", "clone-connection")
    let failure =
        Assert.Throws<InvalidOperationException>(
            TestDelegate(fun () -> users.Register("alice", registration copy, "clone-connection") |> ignore))
    Assert.That(failure.Message, Does.Contain("Machine identity collision"))
    Assert.That(users.ListDevices("alice").Length, Is.EqualTo(1))
    match users.GetSessionByDevice("alice", original) with
    | ValueSome session -> Assert.That(session.ConnectionId, Is.EqualTo(Some "first-connection"))
    | _ -> Assert.Fail("Rejected clone must not evict existing route")

[<Test>]
let malformed_versioned_route_fails_closed_and_legacy_registration_is_preserved () =
    let users = UserService()
    users.TransportConnected("alice", "bad-connection")
    Assert.Throws<ArgumentException>(
        TestDelegate(fun () ->
            users.Register("alice", registration "jarvis2:bad", "bad-connection") |> ignore))
    |> ignore
    Assert.That(users.ListDevices("alice"), Is.Empty)
    users.Register("alice", registration "legacy-device", "bad-connection") |> ignore
    Assert.That(users.ListDevices("alice").Length, Is.EqualTo(1))

[<Test>]
let registration_registry_reconstructs_after_empty_server_restart () =
    let machine = Guid.NewGuid().ToString("D")
    let installation = Guid.NewGuid().ToString("D")
    let first = route machine installation
    let second = route machine installation
    let before = UserService()
    before.TransportConnected("alice", "connection-a")
    before.Register("alice", registration first, "connection-a") |> ignore
    before.TransportConnected("alice", "connection-b")
    before.Register("alice", registration second, "connection-b") |> ignore
    let after = UserService()
    Assert.That(after.ListDevices("alice"), Is.Empty)
    after.TransportConnected("alice", "connection-a2")
    after.Register("alice", registration first, "connection-a2") |> ignore
    after.TransportConnected("alice", "connection-b2")
    after.Register("alice", registration second, "connection-b2") |> ignore
    Assert.That(after.ListDevices("alice").Length, Is.EqualTo(2))

[<Test>]
let legacy_machine_selection_migrates_only_after_legacy_disconnect () =
    let users = UserService()
    let machine = Guid.NewGuid().ToString("D")
    let newRoute = route machine (Guid.NewGuid().ToString("D"))
    users.TransportConnected("alice", "legacy-connection")
    users.Register("alice", registration machine, "legacy-connection") |> ignore
    Assert.That(users.SelectConversationDevice("alice", "conversation", machine), Is.True)
    users.TransportConnected("alice", "new-connection")
    users.Register("alice", registration newRoute, "new-connection") |> ignore
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some machine))
    Assert.That(users.GetSelectedDeviceId("alice", Some "conversation"), Is.EqualTo(Some machine))
    users.Disconnect("legacy-connection", None)
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some newRoute))
    Assert.That(users.GetSelectedDeviceId("alice", Some "conversation"), Is.EqualTo(Some newRoute))
    match users.GetSelectedSession("alice", Some "conversation", Some machine) with
    | ValueSome session -> Assert.That(session.State, Is.EqualTo(Disconnected))
    | _ -> Assert.Fail("Explicit old route must not be silently reinterpreted")
