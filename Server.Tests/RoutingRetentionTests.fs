module ServerRoutingRetentionTests

open System
open Common
open Common.SignalR
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open Server.Services

let private origin = DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero)

let private bounded =
    { UserRetentionPolicy.defaults with
        DisconnectedTimeToLive = TimeSpan.FromMinutes 10.
        ConversationIdleTimeToLive = TimeSpan.FromMinutes 5.
        SweepInterval = TimeSpan.FromSeconds 1.
        MaxDisconnectedPerUser = 2
        MaxDisconnectedTotal = 3
        MaxConversationsPerUser = 3
        MaxConversationsTotal = 5 }

let private register (users: UserService) user device connection =
    users.TransportConnected(user, connection)
    users.Register(user,
        { DeviceId = device
          DeviceName = device
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "test"
          SupportedCommands = [ "ListProjects" ] }, connection)

let private selected (users: UserService) user conversation =
    users.GetSelectedDeviceId(user, Some conversation)

[<Test>]
let user_service_can_be_constructed_by_production_dependency_injection () =
    let services = ServiceCollection()
    services.AddSingleton<UserService>() |> ignore
    use provider = services.BuildServiceProvider()
    let resolved = provider.GetRequiredService<UserService>()
    Assert.That(resolved, Is.Not.Null)
    Assert.That(resolved.RetentionCounts.DeviceSessions, Is.Zero)
    Assert.That(typeof<UserService>.GetConstructor(Type.EmptyTypes), Is.Not.Null)

[<Test>]
let expired_disconnected_default_and_conversation_fall_back_to_an_active_device () =
    let mutable now = origin
    let users = UserService(clock = (fun () -> now), retention = bounded)
    let first = register users "alice" "home" "home-1"
    register users "alice" "work" "work-1" |> ignore
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some "home"))
    Assert.That(users.SelectConversationDevice("alice", "conversation", "home"), Is.True)
    users.Disconnect("home-1", Some "network lost")
    now <- now.AddMinutes 4.
    match users.GetSelectedSession("alice", Some "conversation", None) with
    | ValueSome session ->
        Assert.That(session.DeviceId, Is.EqualTo("home"))
        Assert.That(session.State, Is.EqualTo(Disconnected))
    | ValueNone -> Assert.Fail("Disconnected device remains routable during retention window.")

    now <- now.AddMinutes 7.
    // Automatic resolution falls back only after the default is evicted.
    Assert.That(selected users "alice" "conversation", Is.EqualTo(Some "work"))
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some "work"))
    Assert.That(users.GetSessionByDevice("alice", "home").IsNone, Is.True)
    Assert.That(users.GetSelectedSession("alice", None, Some "home").IsNone, Is.True,
                "Explicit device override must not route to a different device.")
    let reconnected = register users "alice" "home" "home-2"
    Assert.That(reconnected.Generation, Is.GreaterThan(first.Generation))
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some "work"))
    Assert.That(selected users "alice" "conversation", Is.EqualTo(Some "work"),
                "Evicted selection must not silently reattach when the old device returns.")

[<Test>]
let active_device_and_transport_never_expire_even_after_many_ttls () =
    let mutable now = origin
    let users = UserService(clock = (fun () -> now), retention = bounded)
    let registered = register users "alice" "always-on" "connection"
    now <- now.AddDays 365.
    users.PruneExpired()
    Assert.That(users.RetentionCounts.ConnectedTransports, Is.EqualTo(1))
    Assert.That(users.RetentionCounts.DeviceSessions, Is.EqualTo(1))
    match users.GetSessionByDevice("alice", "always-on") with
    | ValueSome session ->
        Assert.That(session.State, Is.EqualTo(Registered))
        Assert.That(session.Generation, Is.EqualTo(registered.Generation))
    | ValueNone -> Assert.Fail("A registered device must never be expired by idle age.")

[<Test>]
let conversation_expiry_is_idle_based_and_reads_extend_the_lifetime () =
    let mutable now = origin
    let users = UserService(clock = (fun () -> now), retention = bounded)
    register users "alice" "home" "home-connection" |> ignore
    register users "alice" "work" "work-connection" |> ignore
    Assert.That(users.SelectConversationDevice("alice", "active-conversation", "work"), Is.True)
    now <- now.AddMinutes 4.
    Assert.That(selected users "alice" "active-conversation", Is.EqualTo(Some "work"))
    now <- now.AddMinutes 4.
    Assert.That(selected users "alice" "active-conversation", Is.EqualTo(Some "work"))
    now <- now.AddMinutes 6.
    Assert.That(selected users "alice" "active-conversation", Is.EqualTo(Some "home"))
    Assert.That(users.RetentionCounts.ConversationSelections, Is.Zero)
    // A new selection is required to route to work again.
    Assert.That(users.SelectConversationDevice("alice", "active-conversation", "work"), Is.True)
    Assert.That(selected users "alice" "active-conversation", Is.EqualTo(Some "work"))

[<Test>]
let per_user_and_global_conversation_caps_retain_most_recent_choices () =
    let mutable now = origin
    let policy = { bounded with MaxConversationsPerUser = 2; MaxConversationsTotal = 3 }
    let users = UserService(clock = (fun () -> now), retention = policy)
    register users "alice" "home" "a-home" |> ignore
    register users "alice" "work" "a-work" |> ignore
    register users "bob" "desktop" "b-desktop" |> ignore
    for conversation in [ "c1"; "c2"; "c3" ] do
        Assert.That(users.SelectConversationDevice("alice", conversation, "work"), Is.True)
        now <- now.AddSeconds 1.
    Assert.That(users.RetentionCounts.ConversationSelections, Is.EqualTo(2))
    Assert.That(selected users "alice" "c1", Is.EqualTo(Some "home"))
    for conversation in [ "b1"; "b2" ] do
        Assert.That(users.SelectConversationDevice("bob", conversation, "desktop"), Is.True)
        now <- now.AddSeconds 1.
    Assert.That(users.RetentionCounts.ConversationSelections, Is.EqualTo(3))
    Assert.That(selected users "alice" "c2", Is.EqualTo(Some "home"))
    Assert.That(selected users "alice" "c3", Is.EqualTo(Some "work"))
    Assert.That(selected users "bob" "b2", Is.EqualTo(Some "desktop"))

[<Test>]
let thousands_of_synthetic_conversations_never_exceed_caps () =
    let mutable now = origin
    let policy = { bounded with
                    MaxConversationsPerUser = 10
                    MaxConversationsTotal = 75
                    ConversationIdleTimeToLive = TimeSpan.FromDays 365. }
    let users = UserService(clock = (fun () -> now), retention = policy)
    for userIndex in 1 .. 15 do
        let user = $"user-{userIndex}"
        register users user "device" $"connection-{userIndex}" |> ignore
        for conversation in 1 .. 120 do
            Assert.That(users.SelectConversationDevice(user, $"conversation-{conversation}", "device"), Is.True)
            Assert.That(users.RetentionCounts.ConversationSelections, Is.LessThanOrEqualTo(75))
            now <- now.AddMilliseconds 1.
    Assert.That(users.RetentionCounts.ConversationSelections, Is.EqualTo(75))
    Assert.That(users.RetentionCounts.DeviceSessions, Is.EqualTo(15))
    Assert.That(users.RetentionCounts.DefaultSelections, Is.EqualTo(15))

[<Test>]
let disconnected_device_caps_evict_oldest_but_never_an_active_device () =
    let mutable now = origin
    let users = UserService(clock = (fun () -> now), retention = bounded)
    register users "alice" "active" "a-active" |> ignore
    for index in 1 .. 10 do
        let device = $"offline-{index}"
        let connection = $"a-{index}"
        register users "alice" device connection |> ignore
        users.Disconnect(connection, None)
        Assert.That(users.RetentionCounts.DisconnectedSessions, Is.LessThanOrEqualTo(2))
        now <- now.AddSeconds 1.
    Assert.That(users.GetSessionByDevice("alice", "offline-1").IsNone, Is.True)
    Assert.That(users.GetSessionByDevice("alice", "offline-10").IsSome, Is.True)
    register users "bob" "active" "b-active" |> ignore
    for index in 1 .. 5 do
        let connection = $"b-{index}"
        register users "bob" $"offline-{index}" connection |> ignore
        users.Disconnect(connection, None)
        now <- now.AddSeconds 1.
        Assert.That(users.RetentionCounts.DisconnectedSessions, Is.LessThanOrEqualTo(3))
    Assert.That(users.GetSessionByDevice("alice", "active").IsSome, Is.True)
    Assert.That(users.GetSessionByDevice("bob", "active").IsSome, Is.True)
    Assert.That(users.RetentionCounts.ConnectedTransports, Is.EqualTo(2))

[<Test>]
let expired_device_re_registration_cannot_reuse_stale_generation_even_on_reused_connection_id () =
    let mutable now = origin
    let users = UserService(clock = (fun () -> now), retention = bounded)
    let first = register users "alice" "device" "same-connection"
    let tracker = ClientResponseTracker()
    let correlationId, _ =
        tracker.Register("alice", "same-connection", first.Generation, TimeSpan.FromMinutes 5.)
    users.Disconnect("same-connection", None)
    now <- now.AddMinutes 11.
    users.PruneExpired()
    Assert.That(users.GetSessionByDevice("alice", "device").IsNone, Is.True)
    let newRegistration = register users "alice" "device" "same-connection"
    Assert.That(newRegistration.Generation, Is.GreaterThan(first.Generation))
    let result = { Result = Some "stale"; Error = None; Instructions = []; AgentStateChanges = [] }
    Assert.That(tracker.Complete(correlationId, "alice", "same-connection",
                                 newRegistration.Generation, result), Is.False)
    tracker.Cancel(correlationId)
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some "device"))

[<Test>]
let conversation_and_device_retention_are_scoped_to_owner_and_are_ephemeral_across_restart () =
    let mutable now = origin
    let users = UserService(clock = (fun () -> now), retention = bounded)
    register users "alice" "shared" "a-connection" |> ignore
    register users "bob" "shared" "b-connection" |> ignore
    Assert.That(users.SelectConversationDevice("alice", "shared-conversation", "shared"), Is.True)
    Assert.That(users.GetSelectedSession("charlie", Some "shared-conversation", Some "shared").IsNone, Is.True)
    users.Disconnect("a-connection", None)
    now <- now.AddMinutes 11.
    users.PruneExpired()
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(None))
    Assert.That(users.GetDefaultDeviceId("bob"), Is.EqualTo(Some "shared"))
    Assert.That(users.GetSessionByDevice("bob", "shared").IsSome, Is.True)
    let restarted = UserService(clock = (fun () -> now), retention = bounded)
    Assert.That(restarted.RetentionCounts.DeviceSessions, Is.Zero)
    Assert.That(restarted.RetentionCounts.ConversationSelections, Is.Zero)
    Assert.That(restarted.GetDefaultDeviceId("bob"), Is.EqualTo(None))

[<Test>]
let retention_policy_rejects_invalid_intervals_and_caps () =
    let invalid = [
        { bounded with DisconnectedTimeToLive = TimeSpan.Zero }
        { bounded with ConversationIdleTimeToLive = TimeSpan.Zero }
        { bounded with SweepInterval = TimeSpan.Zero }
        { bounded with MaxDisconnectedPerUser = 0 }
        { bounded with MaxDisconnectedTotal = 0 }
        { bounded with MaxConversationsPerUser = 0 }
        { bounded with MaxConversationsTotal = 0 } ]
    for policy in invalid do
        Assert.Throws<ArgumentException>(TestDelegate(fun () ->
            UserService(clock=(fun () -> origin), retention=policy) |> ignore)) |> ignore

[<Test>]
let transport_identity_cannot_be_overwritten_or_used_for_another_users_device () =
    let users = UserService()
    let first = register users "alice" "alice-device" "shared-connection"
    Assert.Throws<InvalidOperationException>(TestDelegate(fun () ->
        users.TransportConnected("bob", "shared-connection"))) |> ignore
    Assert.Throws<InvalidOperationException>(TestDelegate(fun () ->
        users.Register("bob",
            { DeviceId = "bob-device"; DeviceName = "bob-device"
              ProtocolVersion = AgentProtocol.version; ClientVersion = "test"
              SupportedCommands = [ "ListProjects" ] }, "shared-connection") |> ignore)) |> ignore
    Assert.That(users.GetSessionByDevice("bob", "bob-device").IsNone, Is.True)
    match users.GetSessionByConnection("shared-connection") with
    | ValueSome session ->
        Assert.That(session.UserId, Is.EqualTo("alice"))
        Assert.That(session.DeviceId, Is.EqualTo("alice-device"))
        Assert.That(session.Generation, Is.EqualTo(first.Generation))
    | ValueNone -> Assert.Fail("The original user's transport and device must survive rejected reuse.")

[<Test>]
let device_reconnect_before_expiry_preserves_default_and_live_conversation_selection () =
    let mutable now = origin
    let users = UserService(clock = (fun () -> now), retention = bounded)
    let first = register users "alice" "home" "first-connection"
    register users "alice" "work" "work-connection" |> ignore
    Assert.That(users.SelectConversationDevice("alice", "conversation", "home"), Is.True)
    users.Disconnect("first-connection", None)
    now <- now.AddMinutes 3.
    let reconnected = register users "alice" "home" "second-connection"
    Assert.That(reconnected.Generation, Is.GreaterThan(first.Generation))
    Assert.That(users.GetDefaultDeviceId("alice"), Is.EqualTo(Some "home"))
    match users.GetSelectedSession("alice", Some "conversation", None) with
    | ValueSome session ->
        Assert.That(session.State, Is.EqualTo(Registered))
        Assert.That(session.ConnectionId, Is.EqualTo(Some "second-connection"))
    | ValueNone -> Assert.Fail("Unexpired conversation should follow reconnect of the same device ID.")
    users.Disconnect("first-connection", Some "late stale disconnect")
    Assert.That(users.GetConnectionId("alice"), Is.EqualTo(ValueSome "second-connection"))

[<Test>]
let recent_conversation_read_prevents_lru_eviction_when_a_new_selection_arrives () =
    let mutable now = origin
    let policy = { bounded with MaxConversationsPerUser = 2; MaxConversationsTotal = 4 }
    let users = UserService(clock = (fun () -> now), retention = policy)
    register users "alice" "home" "h" |> ignore
    register users "alice" "work" "w" |> ignore
    Assert.That(users.SelectConversationDevice("alice", "first", "work"), Is.True)
    now <- now.AddSeconds 1.
    Assert.That(users.SelectConversationDevice("alice", "second", "work"), Is.True)
    now <- now.AddSeconds 1.
    Assert.That(selected users "alice" "first", Is.EqualTo(Some "work"))
    now <- now.AddSeconds 1.
    Assert.That(users.SelectConversationDevice("alice", "third", "work"), Is.True)
    Assert.That(users.RetentionCounts.ConversationSelections, Is.EqualTo(2))
    Assert.That(selected users "alice" "second", Is.EqualTo(Some "home"))
    Assert.That(selected users "alice" "first", Is.EqualTo(Some "work"))
    Assert.That(selected users "alice" "third", Is.EqualTo(Some "work"))

[<Test>]
let repeated_registration_on_one_live_transport_invalidates_old_reply_generation () =
    let users = UserService()
    let first = register users "alice" "device" "connection"
    let tracker = ClientResponseTracker()
    let correlationId, _ = tracker.Register(
        "alice", "connection", first.Generation, TimeSpan.FromMinutes 2.)
    let second =
        users.Register("alice",
            { DeviceId = "device"; DeviceName = "device"
              ProtocolVersion = AgentProtocol.version; ClientVersion = "test"
              SupportedCommands = [ "ListProjects" ] }, "connection")
    Assert.That(second.Generation, Is.GreaterThan(first.Generation))
    let stale = { Result = Some "stale"; Error = None; Instructions = []; AgentStateChanges = [] }
    Assert.That(tracker.Complete(correlationId, "alice", "connection", second.Generation, stale), Is.False)
    tracker.Cancel(correlationId)
    Assert.That(users.RetentionCounts.DeviceSessions, Is.EqualTo(1))
    Assert.That(users.RetentionCounts.ConnectedTransports, Is.EqualTo(1))
