module FeedbackStoreTests

open System
open System.IO
open System.Security.Claims
open Microsoft.Data.Sqlite
open System.Text.Json.Nodes
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open NUnit.Framework
open FsUnitTyped
open Server
open Server.Services

let private withStore test =
    let directory =
        Path.Combine(Path.GetTempPath(), "jarvis-feedback-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(directory) |> ignore
    let store = FeedbackStore(Path.Combine(directory, "jarvis.db"))

    try
        test store
    finally
        Directory.Delete(directory, true)

let private feedbackInput project tool category severity summary details workaround =
    { ProjectName = project
      ToolName = tool
      Category = category
      Severity = severity
      Summary = summary
      Details = details
      Workaround = workaround
      OperationId = None }

let private authenticatedAccessor userId =
    let context = DefaultHttpContext()
    let identity =
        ClaimsIdentity(
            [ Claim("sub", userId)
              Claim("scope", Auth.WorkspaceRead) ],
            "test"
        )

    context.User <- ClaimsPrincipal(identity)
    let accessor = HttpContextAccessor()
    accessor.HttpContext <- context
    accessor

[<Test>]
let ``per-call device argument overrides conversation selection without changing it`` () =
    let users = UserService()
    let registration id : Common.SignalR.ClientRegistration =
        { DeviceId = id
          DeviceName = id
          ProtocolVersion = Common.AgentProtocol.version
          ClientVersion = "1.0"
          SupportedCommands = [ "ListProjects" ] }
    users.TransportConnected("alice", "home-connection")
    users.Register("alice", registration "home", "home-connection") |> ignore
    users.TransportConnected("alice", "work-connection")
    users.Register("alice", registration "work", "work-connection") |> ignore
    users.SelectConversationDevice("alice", "conversation-a", "home") |> shouldEqual true
    users.SelectConversationDevice("alice", "conversation-b", "work") |> shouldEqual true

    let request (sessionId: string) (overrideId: string option) =
        let meta = JsonObject()
        meta["openai/session"] <- JsonValue.Create(sessionId)
        let arguments = System.Collections.Generic.Dictionary<string, JsonElement>()
        match overrideId with
        | Some id -> arguments["deviceId"] <- JsonSerializer.SerializeToElement(id)
        | None -> ()
        ModelContextProtocol.Protocol.CallToolRequestParams(Name = "ListProjects", Meta = meta, Arguments = arguments)

    let effective request =
        RequestDeviceRouting.withMetadata request (fun () ->
            task {
                let selected = users.GetSelectedSession("alice", RequestDeviceRouting.session(), RequestDeviceRouting.deviceOverride())
                return
                    match selected with
                    | ValueSome session -> session.DeviceId
                    | ValueNone -> "missing"
            })
        |> Async.AwaitTask |> Async.RunSynchronously

    effective (request "conversation-a" None) |> shouldEqual "home"
    effective (request "conversation-b" None) |> shouldEqual "work"
    effective (request "conversation-a" (Some "work")) |> shouldEqual "work"
    effective (request "conversation-a" None) |> shouldEqual "home"
    users.GetSelectedDeviceId("alice", Some "conversation-a") |> shouldEqual (Some "home")
    users.GetSelectedDeviceId("alice", Some "conversation-b") |> shouldEqual (Some "work")
    users.GetSelectedDeviceId("bob", Some "conversation-a") |> shouldEqual None

[<Test>]
let ``request metadata does not leak between concurrent MCP calls`` () =
    task {
        let request (id: string) =
            let meta = JsonObject()
            meta["openai/session"] <- JsonValue.Create(id)
            ModelContextProtocol.Protocol.CallToolRequestParams(Name = "ListProjects", Meta = meta)
        let run (id: string) (delay: int) =
            RequestDeviceRouting.withMetadata (request id) (fun () ->
                task {
                    do! Task.Delay delay
                    return RequestDeviceRouting.session()
                })
        let a = run "conversation-a" 20
        let b = run "conversation-b" 5
        let! results = Task.WhenAll([| a; b |])
        results[0] |> shouldEqual (Some "conversation-a")
        results[1] |> shouldEqual (Some "conversation-b")
        RequestDeviceRouting.session() |> shouldEqual None
    }

[<Test>]
let ``missing conversation metadata never mutates global device selection`` () =
    let users = UserService()
    let registration : Common.SignalR.ClientRegistration =
        { DeviceId = "home"
          DeviceName = "Home"
          ProtocolVersion = Common.AgentProtocol.version
          ClientVersion = "1.0"
          SupportedCommands = [ "ListProjects" ] }
    users.TransportConnected("alice", "home-connection")
    users.Register("alice", registration, "home-connection") |> ignore
    let request = ModelContextProtocol.Protocol.CallToolRequestParams(Name = "SelectDevice")
    let attempted =
        RequestDeviceRouting.withMetadata request (fun () ->
            McpToolHelpers.selectDevice users (authenticatedAccessor "alice") "home")
        |> Async.AwaitTask |> Async.RunSynchronously
    attempted.IsError |> shouldEqual (Nullable true)
    users.GetDefaultDeviceId("alice") |> shouldEqual (Some "home")

[<Test>]
let ``device MCP tools preserve user isolation and explicit selection`` () =
    let users = UserService()
    let registration id : Common.SignalR.ClientRegistration =
        { DeviceId = id
          DeviceName = id
          ProtocolVersion = Common.AgentProtocol.version
          ClientVersion = "1.0.0"
          SupportedCommands = [ "ListProjects" ] }
    users.TransportConnected("alice", "home-connection")
    users.Register("alice", registration "home", "home-connection") |> ignore
    users.TransportConnected("alice", "work-connection")
    users.Register("alice", registration "work", "work-connection") |> ignore

    let bobResult = McpToolHelpers.selectDevice users (authenticatedAccessor "bob") "home"
                    |> Async.AwaitTask |> Async.RunSynchronously
    bobResult.IsError |> shouldEqual (Nullable true)
    let invalid = McpToolHelpers.selectDevice users (authenticatedAccessor "alice") "unknown"
                  |> Async.AwaitTask |> Async.RunSynchronously
    invalid.IsError |> shouldEqual (Nullable true)
    let selected = McpToolHelpers.selectDevice users (authenticatedAccessor "alice") "work"
                   |> Async.AwaitTask |> Async.RunSynchronously
    selected.IsError |> shouldEqual (Nullable true)
    let meta = JsonObject()
    meta["openai/session"] <- JsonValue.Create("conversation-1")
    let request = ModelContextProtocol.Protocol.CallToolRequestParams(Name = "SelectDevice", Meta = meta)
    let selected =
        RequestDeviceRouting.withMetadata request (fun () ->
            McpToolHelpers.selectDevice users (authenticatedAccessor "alice") "work")
        |> Async.AwaitTask |> Async.RunSynchronously
    selected.IsError |> shouldEqual (Nullable false)
    users.GetDefaultDeviceId("alice") |> shouldEqual (Some "home")
    users.GetSelectedDeviceId("alice", Some "conversation-1") |> shouldEqual (Some "work")
    users.GetSelectedDeviceId("alice", Some "conversation-2") |> shouldEqual (Some "home")
    let listed = McpToolHelpers.listDevices users (authenticatedAccessor "alice")
                 |> Async.AwaitTask |> Async.RunSynchronously
    listed.IsError |> shouldEqual (Nullable false)

let private beginWitness (store: FeedbackStore) user agent =
    match store.TryBeginAgentOperation(
              user, agent, "ReadFiles", Some "Jarvis",
              "server", Some "client", Some "3.80") with
    | Ok operation -> operation
    | Error error -> failwith error

let private approveTestEpoch (store: FeedbackStore) =
    match store.TryAuthorizeLegacyDrain(
              "test-operator", Guid.NewGuid().ToString("N"),
              "Verified old client processes and detached job trees are stopped.",
              fun () -> Ok ()) with
    | Ok () -> ()
    | Error error -> failwith error

[<Test>]
let ``freshly migrated witness table never proves an old protocol request inactive`` () =
    withStore (fun store ->
        // Simulate a 3.63 command dispatched by a pre-witness server. It
        // contributes to the legacy telemetry table, not the new witness.
        let prior = store.BeginOperation(
            "alice", "RunCommand", Some "Jarvis", "server-v3.63",
            Some "old-client", Some "3.63")
        store.CountUnresolvedAgentExecutions("alice", "Worker 3") |> shouldEqual 0
        store.GetAgentExecutionReadiness() |> shouldEqual (Ok LegacyDrainUnverified)
        store.TryAcquireAgentCorrection("alice", "Worker 3", "operator-pre-drain").IsError
        |> shouldEqual true
        // Even a timeout tagged completed in server telemetry cannot prove
        // the original client process or detached child work has terminated.
        store.CompleteOperation(prior.Id, false, Some "Timeout", 600000L)
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.GetAgentExecutionReadiness() |> shouldEqual (Ok LegacyDrainUnverified)
        restarted.TryAcquireAgentCorrection("alice", "Worker 3", "operator-pre-drain").IsError
        |> shouldEqual true
        restarted.TryAuthorizeLegacyDrain(
            "operator", "drain-id", "Confirmed no pre-witness jobs remain.",
            fun () -> Error "Prior-version command may still be executing").IsError
        |> shouldEqual true
        restarted.GetAgentExecutionReadiness() |> shouldEqual (Ok LegacyDrainUnverified)
        restarted.TryAuthorizeLegacyDrain(
            "operator", "drain-id", "Confirmed no pre-witness jobs remain.",
            fun () -> Ok ()) |> shouldEqual (Ok ())
        restarted.TryAuthorizeLegacyDrain(
            "operator", "drain-id", "Confirmed no pre-witness jobs remain.",
            fun () -> Ok ()) |> shouldEqual (Ok ())
        let ready = restarted.GetAgentExecutionReadiness()
        match ready with
        | Ok (LegacyDrainVerified("operator", evidence, _)) ->
            Assert.That(evidence, Does.Contain("pre-witness jobs"))
        | other -> Assert.Fail(sprintf "Expected audited drain: %A" other)
        restarted.TryAuthorizeLegacyDrain(
            "operator-2", "different-drain-id", "Different evidence for same protocol epoch.",
            fun () -> Ok ()).IsError |> shouldEqual true
        use twice = new FeedbackStore(store.DatabasePath)
        match twice.GetAgentExecutionReadiness() with
        | Ok (LegacyDrainVerified("operator", _, _)) -> ()
        | other -> Assert.Fail(sprintf "Readiness not durable: %A" other)
        let permit = twice.TryAcquireAgentCorrection("alice", "Worker 3", "operator-after-drain")
        Assert.That(permit.IsOk, Is.True, sprintf "Unexpected correction veto: %A" permit))

[<Test>]
let ``durable agent admission serializes with operator correction and records no false terminal`` () =
    withStore (fun store ->
        approveTestEpoch store
        let call = beginWitness store "alice" "Worker 3"
        store.CountUnresolvedAgentExecutions("alice", "WORKER 3")
        |> shouldEqual 1
        // Server request timeout or HTTP cancellation is not a terminal
        // execution witness even when telemetry marks it completed.
        store.CompleteOperation(call.Id, false, Some "Timeout", 600000L)
        store.CountUnresolvedAgentExecutions("alice", "Worker 3")
        |> shouldEqual 1
        store.TryAcquireAgentCorrection("alice", "Worker 3", "operator-1").IsError
        |> shouldEqual true
        store.TryMarkAgentNotDispatched("alice", call.Id).IsOk
        |> shouldEqual true
        let correction =
            match store.TryAcquireAgentCorrection("alice", "worker 3", "operator-1") with
            | Ok permit -> permit
            | Error error -> failwith error
        store.TryBeginAgentOperation(
            "alice", "WORKER 3", "ReadFiles", None, "server", None, None).IsError
        |> shouldEqual true
        store.TryAcquireAgentCorrection("alice", "Worker 3", "operator-1")
        |> shouldEqual (Ok correction)
        store.TryReleaseAgentCorrection("alice", "Worker 3", correction, false).IsError
        |> shouldEqual true
        store.TryBeginAgentOperation(
            "alice", "Worker 3", "ReadFiles", None, "server", None, None).IsError
        |> shouldEqual true
        store.TryReleaseAgentCorrection("alice", "Worker 3", correction, true).IsOk
        |> shouldEqual true
        beginWitness store "alice" "Worker 3" |> ignore)

[<Test>]
let ``late exact client receipt survives telemetry timeout and SQLite restart`` () =
    withStore (fun store ->
        approveTestEpoch store
        let call = beginWitness store "alice" "Worker 3"
        store.TryMarkAgentDispatched(
            "alice", call.Id, "desktop", "conn-42", 3L, "corr-42").IsOk
        |> shouldEqual true
        store.TryMarkAgentNotDispatched("alice", call.Id).IsError
        |> shouldEqual true
        store.CompleteOperation(call.Id, false, Some "Timeout", 600000L)
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.CountUnresolvedAgentExecutions("alice", "Worker 3")
        |> shouldEqual 1
        restarted.TryAcquireAgentCorrection("alice", "Worker 3", "operator-2").IsError
        |> shouldEqual true
        for user, conn, gen, correlation in
            [ "bob", "conn-42", 3L, "corr-42"
              "alice", "conn-old", 3L, "corr-42"
              "alice", "conn-42", 4L, "corr-42"
              "alice", "conn-42", 3L, "corr-forged" ] do
            restarted.TryRecordAgentClientTerminal(user, conn, gen, correlation)
            |> shouldEqual (Ok None)
        restarted.CountUnresolvedAgentExecutions("alice", "Worker 3")
        |> shouldEqual 1
        restarted.TryRecordAgentClientTerminal("alice", "conn-42", 3L, "corr-42")
        |> shouldEqual (Ok(Some call.Id))
        // Duplicate late terminal receipt is idempotent.
        restarted.TryRecordAgentClientTerminal("alice", "conn-42", 3L, "corr-42")
        |> shouldEqual (Ok(Some call.Id))
        restarted.CountUnresolvedAgentExecutions("alice", "Worker 3")
        |> shouldEqual 0
        let correction =
            restarted.TryAcquireAgentCorrection("alice", "Worker 3", "operator-2")
            |> function Ok permit -> permit | Error error -> failwith error
        use nextRestart = new FeedbackStore(store.DatabasePath)
        nextRestart.TryBeginAgentOperation(
            "alice", "Worker 3", "ReadFiles", None, "server", None, None).IsError
        |> shouldEqual true
        nextRestart.TryAcquireAgentCorrection("alice", "Worker 3", "operator-2")
        |> shouldEqual (Ok correction)
        nextRestart.TryReleaseAgentCorrection("alice", "Worker 3", correction, true).IsOk
        |> shouldEqual true)

[<Test>]
let ``telemetry pruning cannot remove unresolved execution witnesses`` () =
    withStore (fun store ->
        approveTestEpoch store
        let active = beginWitness store "alice" "Worker 3"
        store.PruneTelemetry(DateTimeOffset.UtcNow.AddDays(31.))
        store.ResolveOperation("alice", Some active.Id, None, None).IsError
        |> shouldEqual true
        store.CountUnresolvedAgentExecutions("alice", "Worker 3")
        |> shouldEqual 1
        store.TryAcquireAgentCorrection("alice", "Worker 3", "operator-3").IsError
        |> shouldEqual true
        store.TryMarkAgentNotDispatched("alice", active.Id).IsOk
        |> shouldEqual true)

[<Test>]
let ``cross-project notification requires witnessed source completion before separate destination admission`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent = "alice", "Sender"
        let parent = beginWitness store user agent
        let beginChild() = store.TryBeginAgentContinuation(user, parent.Id, "Destination")
        Assert.That(beginChild().IsError, Is.True,
                    "An admitted source health probe is not source terminal proof.")
        store.TryMarkAgentDispatched(user, parent.Id, "source-device", "source-conn", 1L, "source-corr")
        |> shouldEqual (Ok ())
        Assert.That(beginChild().IsError, Is.True,
                    "A running source inbox must veto destination dispatch.")
        store.CompleteOperation(parent.Id, false, Some "Timeout", 700000L)
        Assert.That(beginChild().IsError, Is.True,
                    "Server telemetry completion is not a client receipt.")
        let spoofed =
            store.TryRecordAgentClientTerminal(user, "other-conn", 1L, "source-corr")
        spoofed |> shouldEqual (Ok None)
        Assert.That(beginChild().IsError, Is.True)
        store.TryRecordAgentClientTerminal(user, "source-conn", 1L, "source-corr")
        |> shouldEqual (Ok(Some parent.Id))
        let child =
            beginChild() |> function Ok id -> id | Error error -> failwith error
        Assert.That(child, Is.Not.EqualTo(parent.Id))
        Assert.That(beginChild().IsError, Is.True,
                    "The same source terminal can have at most one destination write.")
        store.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 1
        Assert.That(
            store.TryAcquireAgentCorrection(user, agent, "operator-during-destination").IsError,
            Is.True, "A pending destination child blocks correction even after source terminal.")
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 1
        restarted.TryMarkAgentDispatched(
            user, child, "destination-device", "destination-conn", 4L, "destination-corr")
        |> shouldEqual (Ok ())
        restarted.GetAgentExecutionResolution(user, child)
        |> shouldEqual (Ok ExecutionUnresolved)
        restarted.TryRecordAgentClientTerminal(user, "destination-conn", 4L, "destination-corr")
        |> shouldEqual (Ok(Some child))
        restarted.GetAgentExecutionResolution(user, child)
        |> shouldEqual (Ok AuthenticatedClientFinished)
        restarted.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 0
        let permit = restarted.TryAcquireAgentCorrection(user, agent, "operator-after-two-stage")
        Assert.That(permit.IsOk, Is.True)
        match permit with
        | Ok id ->
            restarted.TryReleaseAgentCorrection(user, agent, id, true)
            |> shouldEqual (Ok ())
        | Error _ -> ())

[<Test>]
let ``correction excludes notification continuation between source and destination`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent = "alice", "Sender"
        let parent = beginWitness store user agent
        store.TryMarkAgentDispatched(user, parent.Id, "source", "conn", 1L, "corr")
        |> shouldEqual (Ok ())
        store.TryRecordAgentClientTerminal(user, "conn", 1L, "corr")
        |> shouldEqual (Ok(Some parent.Id))
        let permit =
            store.TryAcquireAgentCorrection(user, agent, "operator-between-steps")
            |> function Ok id -> id | Error error -> failwith error
        store.TryBeginAgentContinuation(user, parent.Id, "Destination").IsError
        |> shouldEqual true
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.TryBeginAgentContinuation(user, parent.Id, "Destination").IsError
        |> shouldEqual true
        restarted.TryReleaseAgentCorrection(user, agent, permit, false).IsError
        |> shouldEqual true
        restarted.TryReleaseAgentCorrection(user, agent, permit, true)
        |> shouldEqual (Ok ())
        let child =
            restarted.TryBeginAgentContinuation(user, parent.Id, "Destination")
            |> function Ok id -> id | Error error -> failwith error
        restarted.TryMarkAgentNotDispatched(user, child) |> shouldEqual (Ok ())
        restarted.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 0)

[<Test>]
let ``correction permit lifecycle requires durable terminal or proven non dispatch`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, actor, target = "alice", "Reviewer 1", "Worker 3"
        let initial = beginWitness store user actor
        store.GetAgentExecutionResolution(user, initial.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.GetAgentExecutionResolution("bob", initial.Id).IsError
        |> shouldEqual true
        store.TryMarkAgentNotDispatched(user, initial.Id)
        |> shouldEqual (Ok ())
        store.GetAgentExecutionResolution(user, initial.Id)
        |> shouldEqual (Ok NeverDispatched)
        let permit =
            match store.TryAcquireAgentCorrection(user, target, "operator-correction-1") with
            | Ok value -> value
            | Error error -> failwith error
        store.TryBeginAgentOperation(
            user, target, "AgentInbox", Some "Jarvis",
            "server", None, None).IsError
        |> shouldEqual true
        store.TryReleaseAgentCorrection(user, target, permit, false).IsError
        |> shouldEqual true
        store.TryReleaseAgentCorrection(user, target, permit, true)
        |> shouldEqual (Ok ())
        let owned = beginWitness store user target
        store.GetAgentExecutionResolution(user, owned.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.TryAcquireAgentCorrection(user, target, "operator-correction-2").IsError
        |> shouldEqual true
        store.TryMarkAgentDispatched(user, owned.Id, "device", "conn",
                                      2L, "correlation")
        |> shouldEqual (Ok ())
        store.GetAgentExecutionResolution(user, owned.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.CompleteOperation(owned.Id, false, Some "Timeout", 600000L)
        store.GetAgentExecutionResolution(user, owned.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.TryRecordAgentClientTerminal(user, "conn", 2L, "correlation")
        |> shouldEqual (Ok(Some owned.Id))
        store.GetAgentExecutionResolution(user, owned.Id)
        |> shouldEqual (Ok AuthenticatedClientFinished)
        let corrected =
            match store.TryAcquireAgentCorrection(user, target, "operator-correction-2") with
            | Ok value -> value
            | Error error -> failwith error
        store.TryReleaseAgentCorrection(user, target, corrected, true)
        |> shouldEqual (Ok ())
        store.GetAgentExecutionResolution(user, "missing-id").IsError
        |> shouldEqual true)

[<TestFixture>]
type CorrectionAdmissionConcurrentTests() =
    [<Test>]
    member _.TwoDevicesCannotEnterTargetDuringAdministrativePermit() =
        withStore (fun store ->
            approveTestEpoch store
            let user, agent = "alice", "Worker 3"
            let permit =
                store.TryAcquireAgentCorrection(user, agent, "operator-id")
                |> function Ok id -> id | Error error -> failwith error
            let attempts =
                [| for device in 1 .. 8 ->
                    Task.Run(fun () ->
                        store.TryBeginAgentOperation(
                            user, agent, "ReadFiles", Some(sprintf "Device-%d" device),
                            "server", None, None)) |]
            Task.WaitAll(attempts |> Array.map (fun t -> t :> Task))
            Assert.That(attempts |> Array.forall (fun t -> t.Result.IsError), Is.True)
            use restarted = new FeedbackStore(store.DatabasePath)
            Assert.That(
                restarted.TryBeginAgentOperation(user, agent, "ReadFiles", None, "server", None, None).IsError,
                Is.True, "A restart must not erase the correction gate.")
            restarted.TryReleaseAgentCorrection(user, agent, permit, true)
            |> shouldEqual (Ok ())
            Assert.That(store.TryBeginAgentOperation(user, agent, "ReadFiles", None, "server", None, None).IsOk,
                        Is.True))

[<Test>]
let ``authenticated cross-project MCP notification independently witnesses sender and destination`` () =
    withStore (fun store ->
        approveTestEpoch store
        let userId = "alice"
        let users = new UserService()
        let connectionId = "cross-project-conn"
        users.TransportConnected(userId, connectionId)
        let registered =
            users.Register(
                userId,
                { DeviceId = "client-1"
                  DeviceName = "Test workstation"
                  ProtocolVersion = Common.AgentProtocol.version
                  ClientVersion = "test"
                  SupportedCommands = [ "LedgerRead"; "TaskRead"; "TaskWrite" ] },
                connectionId)
        let tracker = new ClientResponseTracker()
        let observedActors = ResizeArray<string>()
        let recipient =
            { new Common.SignalR.IClientService with
                member _.ReceiveMessage(_) = Task.CompletedTask
                member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                member _.ReceiveCommand(correlationId, _, invokingAgentId) =
                    observedActors.Add(invokingAgentId)
                    let witness =
                        store.TryRecordAgentClientTerminal(
                            userId, connectionId, registered.Generation, correlationId)
                    Assert.That(witness.IsOk, Is.True, sprintf "Client witness persistence failed: %A" witness)
                    let response: Common.AgentCommandResponse =
                        { Result = Some "{}"
                          Error = None
                          Instructions = []
                          AgentStateChanges = []
                          InboxActivation = None }
                    Assert.That(
                        tracker.Complete(
                            correlationId, userId, connectionId, registered.Generation, response),
                        Is.True)
                    Task.CompletedTask }
        let clients =
            { new Microsoft.AspNetCore.SignalR.IHubClients<Common.SignalR.IClientService> with
                member _.All = recipient
                member _.AllExcept(_) = recipient
                member _.Client(_) = recipient
                member _.Clients(_) = recipient
                member _.Group(_) = recipient
                member _.GroupExcept(_, _) = recipient
                member _.Groups(_) = recipient
                member _.User(_) = recipient
                member _.Users(_) = recipient }
        let hub =
            { new Microsoft.AspNetCore.SignalR.IHubContext<HubService, Common.SignalR.IClientService> with
                member _.Clients = clients
                member _.Groups = Unchecked.defaultof<_> }
        let services =
            Microsoft.Extensions.DependencyInjection.ServiceCollection()
            :> Microsoft.Extensions.DependencyInjection.IServiceCollection
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<FeedbackStore>(
            services, store) |> ignore
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<UserService>(
            services, users) |> ignore
        use provider =
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services)
        let context = DefaultHttpContext()
        context.User <-
            ClaimsPrincipal(
                ClaimsIdentity(
                    [ Claim("sub", userId)
                      Claim("scope", Auth.WorkspaceRead + " " + Auth.GitWrite) ], "test"))
        context.RequestServices <- provider
        let http = HttpContextAccessor()
        http.HttpContext <- context
        let service = new ClientService(hub, users, tracker, http, store)
        let result =
            JarvisMcpTools.NotifyAgent(
                "Source", "Sender", "Recipient", "Please inspect the destination",
                "notification-two-stage", "", "", "Destination", service, http)
            |> Async.AwaitTask
            |> Async.RunSynchronously
        Assert.That(result.IsError, Is.EqualTo(Nullable false))
        observedActors |> Seq.toList
        |> shouldEqual [ ""; "Sender"; ""; "Sender" ]
        store.CountUnresolvedAgentExecutions(userId, "Sender") |> shouldEqual 0
        store.CountUnresolvedAgentExecutions(userId, "Recipient") |> shouldEqual 0
        use database = new SqliteConnection($"Data Source={store.DatabasePath}")
        database.Open()
        use query = database.CreateCommand()
        query.CommandText <-
            """SELECT request_id, parent_request_id, project_name, state
               FROM agent_execution_witness WHERE user_id = $user;"""
        query.Parameters.AddWithValue("$user", userId) |> ignore
        use reader = query.ExecuteReader()
        let mutable rows = []
        while reader.Read() do
            rows <-
                (reader.GetString 0,
                 (if reader.IsDBNull 1 then None else Some(reader.GetString 1)),
                 reader.GetString 2,
                 reader.GetString 3) :: rows
        Assert.That(rows.Length, Is.EqualTo(2))
        let root = rows |> List.find (fun (_, parent, _, _) -> parent.IsNone)
        let child = rows |> List.find (fun (_, parent, _, _) -> parent.IsSome)
        let rootId, _, rootProject, rootState = root
        let _, childParent, childProject, childState = child
        Assert.That(rootProject, Is.EqualTo("Source"))
        Assert.That(childProject, Is.EqualTo("Destination"))
        Assert.That(childParent, Is.EqualTo(Some rootId))
        Assert.That(rootState, Is.EqualTo("Terminal"))
        Assert.That(childState, Is.EqualTo("Terminal")))

[<Test>]
let ``authenticated MCP correction releases exclusive permit after proven no route`` () =
    withStore (fun store ->
        approveTestEpoch store
        let userId = "alice"
        let services =
            Microsoft.Extensions.DependencyInjection.ServiceCollection()
            :> Microsoft.Extensions.DependencyInjection.IServiceCollection
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<FeedbackStore>(
            services, store) |> ignore
        let users = new UserService()
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<UserService>(
            services, users) |> ignore
        use provider =
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services)
        let context = DefaultHttpContext()
        context.User <-
            ClaimsPrincipal(
                ClaimsIdentity(
                    [ Claim("sub", userId)
                      Claim("scope", Auth.WorkspaceRead + " " + Auth.GitWrite) ],
                    "test"))
        context.RequestServices <- provider
        let http = HttpContextAccessor()
        http.HttpContext <- context
        let client =
            new ClientService(Unchecked.defaultof<_>, users, new ClientResponseTracker(), http, store)
        let result =
            JarvisMcpTools.CorrectAgentWorkingState(
                "Jarvis", "Admin 1", "Worker 3", "task-1", "attempt-1",
                1, 1, 1, 1, "Operator proved prior session idle",
                "operator-correction-no-device", "", client, http)
            |> Async.AwaitTask
            |> Async.RunSynchronously
        Assert.That(result.IsError, Is.EqualTo(Nullable true))
        // No route existed. The initiating Admin witness is provably
        // NotDispatched and the target's exclusive permit was released.
        let permit =
            store.TryAcquireAgentCorrection(userId, "Worker 3", "operator-2")
        Assert.That(permit.IsOk, Is.True,
                    sprintf "No-device request leaked exclusive correction permit: %A" permit)
        Assert.That(store.CountUnresolvedAgentExecutions(userId, "Admin 1"), Is.Zero)
        match permit with
        | Ok id ->
            store.TryReleaseAgentCorrection(userId, "Worker 3", id, true)
            |> shouldEqual (Ok ())
        | Error _ -> ()
        let activeOwner = beginWitness store userId "Worker 3"
        let blocked =
            JarvisMcpTools.CorrectAgentWorkingState(
                "Jarvis", "Admin 1", "Worker 3", "task-1", "attempt-1",
                1, 1, 1, 1, "Operator correction must not race owner activity",
                "operator-correction-busy", "", client, http)
            |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(blocked.IsError, Is.EqualTo(Nullable true))
        Assert.That(store.CountUnresolvedAgentExecutions(userId, "Worker 3"), Is.EqualTo(1),
                    "The owner's admitted request must veto correction independently of device routing.")
        Assert.That(store.CountUnresolvedAgentExecutions(userId, "Admin 1"), Is.Zero,
                    "A correction refused before dispatch must not leak its initiating witness.")
        store.TryMarkAgentNotDispatched(userId, activeOwner.Id) |> shouldEqual (Ok ())
        let resumed =
            store.TryAcquireAgentCorrection(userId, "Worker 3", "operator-after-idle")
        Assert.That(resumed.IsOk, Is.True,
                    sprintf "Denied correction left an unexpected exclusive permit: %A" resumed)
        match resumed with
        | Ok id ->
            store.TryReleaseAgentCorrection(userId, "Worker 3", id, true)
            |> shouldEqual (Ok ())
        | Error _ -> ())

[<Test>]
let ``concurrent SQLite correction and admission cannot both pass`` () =
    withStore (fun store ->
        approveTestEpoch store
        for i in 1 .. 16 do
            let agent = sprintf "Worker-%d" i
            let t1 =
                Task.Run(fun () ->
                    store.TryBeginAgentOperation(
                        "alice", agent, "SearchFiles", Some "Jarvis", "server", None, None))
            let t2 =
                Task.Run(fun () ->
                    store.TryAcquireAgentCorrection("alice", agent, sprintf "operator-%d" i))
            Task.WaitAll([| t1 :> Task; t2 :> Task |])
            Assert.That(t1.Result.IsOk && t2.Result.IsOk, Is.False,
                        "Admission and correction cannot win the same agent gate.")
            Assert.That(t1.Result.IsOk || t2.Result.IsOk, Is.True,
                        "At least one contender must acquire the uncontended SQLite writer gate."))

[<Test; Explicit("Manual 100-call SQLite execution-witness performance measurement")>]
let ``benchmark hundred durable SQLite admissions and authenticated terminal receipts`` () =
    withStore (fun store ->
        let stopwatch = System.Diagnostics.Stopwatch.StartNew()
        for i in 1 .. 100 do
            let req = beginWitness store "alice" "Worker 3"
            let correlation = sprintf "corr-%d" i
            match store.TryMarkAgentDispatched(
                "alice", req.Id, "desktop", "connection-1", 1L, correlation) with
            | Ok () -> ()
            | Error error -> Assert.Fail(error)
            store.CompleteOperation(req.Id, true, None, 0L)
            match store.TryRecordAgentClientTerminal(
                "alice", "connection-1", 1L, correlation) with
            | Ok receipt -> Assert.That(receipt, Is.EqualTo(Some req.Id))
            | Error error -> Assert.Fail(error)
        stopwatch.Stop()
        Assert.That(store.CountUnresolvedAgentExecutions("alice", "Worker 3"), Is.Zero)
        TestContext.Out.WriteLine(
            sprintf "100 durable SQLite admissions/routes/terminals: %.1f ms total, %.2f ms/call"
                stopwatch.Elapsed.TotalMilliseconds
                (stopwatch.Elapsed.TotalMilliseconds / 100.)))

[<Test>]
let ``existing feedback database migrates without losing records`` () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-feedback-legacy-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    let databasePath = Path.Combine(directory, "legacy.db")
    try
        use connection = new SqliteConnection($"Data Source={databasePath}")
        connection.Open()
        use setup = connection.CreateCommand()
        setup.CommandText <-
            """CREATE TABLE feedback (
                 id TEXT PRIMARY KEY, created_at TEXT NOT NULL, user_id TEXT NOT NULL,
                 project_name TEXT NULL, tool_name TEXT NULL, category TEXT NOT NULL,
                 severity TEXT NOT NULL, summary TEXT NOT NULL, details TEXT NULL,
                 workaround TEXT NULL, operation_id TEXT NULL);
               INSERT INTO feedback (id, created_at, user_id, category, severity, summary)
               VALUES ('old-1', '2026-01-01T00:00:00.0000000+00:00', 'alice', 'Other', 'Info', 'Legacy');"""
        setup.ExecuteNonQuery() |> ignore
        connection.Close()
        use store = new FeedbackStore(databasePath)
        let migrated = store.GetFeedback("alice", "old-1").Value
        migrated.Summary |> shouldEqual "Legacy"
        migrated.Status |> shouldEqual "Open"
        migrated.ResolvedAt.HasValue |> shouldEqual false
        store.UpdateFeedback("alice", "old-1", "WontFix", None, Some "Not planned").Value.Status
        |> shouldEqual "WontFix"
    finally
        Directory.Delete(directory, true)

[<Test>]
let ``feedback resolution updates persist and remain user scoped`` () =
    withStore (fun store ->
        let created =
            store.AddFeedback("alice", feedbackInput None None "ToolFailure" "Friction" "Problem" None None, None)
        let original = store.GetFeedback("alice", created.Id).Value
        original.Status |> shouldEqual "Open"
        original.ResolvedAt.HasValue |> shouldEqual false
        store.GetFeedback("bob", created.Id) |> shouldEqual None
        store.UpdateFeedback("bob", created.Id, "Resolved", Some "abc123", Some "fixed") |> shouldEqual None

        let resolved = store.UpdateFeedback("alice", created.Id, "Resolved", Some "abc123", Some "fixed").Value
        resolved.Status |> shouldEqual "Resolved"
        resolved.ResolvedAt.HasValue |> shouldEqual true
        resolved.ResolutionCommit |> shouldEqual "abc123"
        resolved.ResolutionNotes |> shouldEqual "fixed"
        let persisted = store.GetFeedback("alice", created.Id).Value
        persisted.ResolutionCommit |> shouldEqual "abc123"
        let listed = store.ListFeedback("alice", None, None, None, None, 10)
        listed[0].Status |> shouldEqual "Resolved"

        let reopened = store.UpdateFeedback("alice", created.Id, "Open", None, None).Value
        reopened.ResolvedAt.HasValue |> shouldEqual false
        reopened.ResolutionCommit |> shouldEqual ""
        reopened.ResolutionNotes |> shouldEqual ""
    )

[<Test>]
let ``feedback update helper validates state and enforces user identity`` () =
    withStore (fun store ->
        let created = store.AddFeedback("alice", feedbackInput None None "Other" "Info" "Issue" None None, None)
        let alice = authenticatedAccessor "alice"
        let invalid = McpToolHelpers.updateFeedback store alice created.Id "Invalid" "" "" |> Async.AwaitTask |> Async.RunSynchronously
        invalid.IsError |> shouldEqual (Nullable true)
        let bob = authenticatedAccessor "bob"
        let denied = McpToolHelpers.updateFeedback store bob created.Id "Resolved" "abc" "" |> Async.AwaitTask |> Async.RunSynchronously
        denied.IsError |> shouldEqual (Nullable true)
        let alice = authenticatedAccessor "alice"
        let updated = McpToolHelpers.updateFeedback store alice created.Id "resolved" "abc" "fixed" |> Async.AwaitTask |> Async.RunSynchronously
        updated.IsError |> shouldEqual (Nullable false)
        store.GetFeedback("alice", created.Id).Value.Status |> shouldEqual "Resolved"
    )

[<Test>]
let ``telemetry expires while feedback survives and loses pruned operation link`` () =
    withStore (fun store ->
        let operation = store.BeginOperation("user-1", "RunCommand", None, "server", None, None)
        store.CompleteOperation(operation.Id, false, Some "Failure", 1L)
        store.AddFeedback(
            "user-1",
            feedbackInput None None "ToolFailure" "Info" "Keep this report" None None,
            Some operation.Id
        ) |> ignore
        store.AddConnectionEvent(
            "user-1", "TransportConnected", "connection-1",
            None, None, None, None, None, None, None, None
        )

        store.PruneTelemetry(DateTimeOffset.UtcNow.AddDays(31.0))
        store.ListConnectionEvents("user-1", 10).Length |> shouldEqual 0
        store.ResolveOperation("user-1", Some operation.Id, None, None)
        |> Result.isError |> shouldEqual true
        let feedback = store.ListFeedback("user-1", None, None, None, None, 10)
        feedback.Length |> shouldEqual 1
        feedback[0].Summary |> shouldEqual "Keep this report"
        feedback[0].OperationId |> shouldEqual ""
    )

[<Test>]
let ``feedback store links recent operation and aggregates it`` () =
    withStore (fun store ->
        let operation =
            store.BeginOperation(
                "user-1",
                "PatchFile",
                Some "Project1",
                "server-1",
                Some "client-1",
                Some "3.0"
            )

        store.CompleteOperation(operation.Id, false, Some "ValidationFailed", 42L)

        let linked =
            store.ResolveOperation("user-1", None, Some "PatchFile", Some "Project1")

        linked |> shouldEqual (Ok(Some operation.Id))

        let created =
            store.AddFeedback(
                "user-1",
                feedbackInput
                    (Some "Project1")
                    (Some "PatchFile")
                    "ToolFailure"
                    "Friction"
                    "Patch failed on synchronized edit"
                    None
                    (Some "Used a Python script"),
                Some operation.Id
            )

        created.OperationId |> shouldEqual operation.Id

        let entries =
            store.ListFeedback(
                "user-1",
                Some "Project1",
                Some "PatchFile",
                Some "ToolFailure",
                Some "Friction",
                20
            )

        entries.Length |> shouldEqual 1
        entries[0].OperationId |> shouldEqual operation.Id
        entries[0].Summary |> shouldEqual "Patch failed on synchronized edit"

        let summary = store.GetSummary("user-1", Some "Project1", None)
        summary.Total |> shouldEqual 1
        summary.WithWorkaround |> shouldEqual 1
        summary.LinkedToFailedOperation |> shouldEqual 1
        summary.ByTool |> Array.exactlyOne |> fun item ->
            item.Name |> shouldEqual "PatchFile"
            item.Count |> shouldEqual 1
    )

[<Test>]
let ``feedback cannot link another users operation`` () =
    withStore (fun store ->
        let operation =
            store.BeginOperation("user-1", "RunCommand", None, "server-1", None, None)

        store.CompleteOperation(operation.Id, true, None, 5L)

        store.ResolveOperation("user-2", Some operation.Id, None, None)
        |> shouldEqual (Error "operationId does not identify an operation belonging to the authenticated user.")
    )

[<Test>]
let ``feedback validation canonicalizes category and severity`` () =
    FeedbackValidation.category "ergonomics" |> shouldEqual (Ok "Ergonomics")
    FeedbackValidation.severity "friction" |> shouldEqual (Ok "Friction")
    FeedbackValidation.category "nonsense" |> Result.isError |> shouldEqual true

[<Test>]
let ``feedback MCP helper stores without a registered local client`` () =
    withStore (fun store ->
        let accessor = authenticatedAccessor "user-feedback"

        let result =
            McpToolHelpers.submitFeedback
                store
                accessor
                "Project1"
                "PatchFile"
                "Ergonomics"
                "Friction"
                "Multi-file synchronized edit was awkward"
                ""
                "Used a Python script"
                ""
            |> Async.AwaitTask
            |> Async.RunSynchronously

        result.IsError |> shouldEqual (Nullable false)

        let entries =
            store.ListFeedback("user-feedback", Some "Project1", Some "PatchFile", None, None, 10)

        entries.Length |> shouldEqual 1
        entries[0].Workaround |> shouldEqual "Used a Python script"
    )


[<Test>]
let ``feedback MCP helper aggregates independent validation errors`` () =
    withStore (fun store ->
        let accessor = authenticatedAccessor "user-validation"

        let result =
            McpToolHelpers.submitFeedback
                store
                accessor
                ""
                "Feedback"
                "ErrorHandling"
                "Medium"
                "Test"
                ""
                ""
                ""
            |> Async.AwaitTask
            |> Async.RunSynchronously

        result.IsError |> shouldEqual (Nullable true)
        let text = result.Content[0] :?> ModelContextProtocol.Protocol.TextContentBlock
        text.Text.Contains("category must be one of", StringComparison.Ordinal) |> shouldEqual true
        text.Text.Contains("severity must be one of", StringComparison.Ordinal) |> shouldEqual true)

[<Test>]
let ``feedback list enforces user isolation`` () =
    withStore (fun store ->
        store.AddFeedback(
            "user-1",
            feedbackInput None None "Positive" "Info" "Useful behavior" None None,
            None
        )
        |> ignore

        store.ListFeedback("user-2", None, None, None, None, 10)
        |> Array.length
        |> shouldEqual 0
    )

[<Test>]
let ``connection diagnostics persist disconnect metadata and isolate users`` () =
    withStore (fun store ->
        store.AddConnectionEvent(
            "user-1",
            "TransportConnected",
            "connection-1",
            None, None, None, None, None, None, None, None
        )

        store.AddConnectionEvent(
            "user-1",
            "TransportDisconnected",
            "connection-1",
            Some "device-1",
            Some "Serdan-Home",
            Some 9L,
            Some "1.2.3",
            Some "3.0",
            Some 60000L,
            Some "System.TimeoutException",
            Some "Client hasn't sent a message/ping within the configured ClientTimeoutInterval."
        )

        let events = store.ListConnectionEvents("user-1", 10)
        events.Length |> shouldEqual 2
        events[0].EventType |> shouldEqual "TransportDisconnected"
        events[0].DeviceName |> shouldEqual "Serdan-Home"
        events[0].Generation.Value |> shouldEqual 9L
        events[0].DurationMs.Value |> shouldEqual 60000L
        events[0].ErrorType |> shouldEqual "System.TimeoutException"
        events[0].ErrorMessage.Contains("ClientTimeoutInterval", StringComparison.Ordinal)
        |> shouldEqual true

        store.ListConnectionEvents("user-2", 10)
        |> Array.length
        |> shouldEqual 0
    )


[<Test>]
let ``explicit agent identity uses tool arguments without provider metadata`` () =
    let request (name: string) (agent: string) =
        let args = System.Collections.Generic.Dictionary<string, JsonElement>()
        args[name] <- JsonSerializer.SerializeToElement(agent)
        ModelContextProtocol.Protocol.CallToolRequestParams(Name = "RunCommand", Arguments = args)
    let resolve req =
        RequestDeviceRouting.withMetadata req (fun () ->
            task {
                return RequestDeviceRouting.agentId(), RequestDeviceRouting.session()
            }) |> Async.AwaitTask |> Async.RunSynchronously
    resolve (request "agentId" "GUI Agent") |> shouldEqual (Some "GUI Agent", None)
    resolve (request "fromAgentId" "Lead") |> shouldEqual (Some "Lead", None)
    resolve (request "agentId" "") |> shouldEqual (None, None)
    let invalid =
        RequestDeviceRouting.withMetadata (request "agentId" "") (fun () ->
            task { return RequestDeviceRouting.invalidAgentId() })
        |> Async.AwaitTask |> Async.RunSynchronously
    invalid |> shouldEqual true
    RequestDeviceRouting.agentId() |> shouldEqual None
    RequestDeviceRouting.invalidAgentId() |> shouldEqual false

[<Test>]
let ``explicit agent metadata is isolated between concurrent calls`` () =
    task {
        let request id =
            let args = System.Collections.Generic.Dictionary<string, JsonElement>()
            args["agentId"] <- JsonSerializer.SerializeToElement(id)
            ModelContextProtocol.Protocol.CallToolRequestParams(Name = "RunCommand", Arguments = args)
        let run id (delay: int) =
            RequestDeviceRouting.withMetadata (request id) (fun () ->
                task {
                    do! Task.Delay delay
                    return RequestDeviceRouting.agentId()
                })
        let a = run "Agent A" 30
        let b = run "Agent B" 10
        let! values = Task.WhenAll([| a; b |])
        values[0] |> shouldEqual (Some "Agent A")
        values[1] |> shouldEqual (Some "Agent B")
        RequestDeviceRouting.agentId() |> shouldEqual None
    }
