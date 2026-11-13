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

// Simulate the exact authenticated SignalR identity contract used by
// HubService.SendClientResponse rather than calling the SQLite store directly.
let private signalRCaller userId connectionId =
    let principal =
        ClaimsPrincipal(ClaimsIdentity([ Claim("sub", userId) ], "test"))
    let items =
        System.Collections.Generic.Dictionary<obj, obj>()
        :> System.Collections.Generic.IDictionary<obj, obj>
    let features =
        Microsoft.AspNetCore.Http.Features.FeatureCollection()
        :> Microsoft.AspNetCore.Http.Features.IFeatureCollection
    { new Microsoft.AspNetCore.SignalR.HubCallerContext() with
        member _.ConnectionId = connectionId
        member _.UserIdentifier = userId
        member _.User = principal
        member _.Items = items
        member _.Features = features
        member _.ConnectionAborted = System.Threading.CancellationToken.None
        member _.Abort() = () }

[<Test>]
let ``authenticated hub receipt survives SQLite write failure then late retry after tracker completes`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, connectionId = "alice", "Worker 3", "auth-signalr-conn"
        let users = UserService()
        users.TransportConnected(user, connectionId)
        let session =
            users.Register(
                user,
                { DeviceId = "device"
                  DeviceName = "Workstation"
                  ProtocolVersion = Common.AgentProtocol.version
                  ClientVersion = "test"
                  SupportedCommands = [ "TaskRead" ] },
                connectionId)
        let tracker = ClientResponseTracker()
        let correlationId, trackingTask =
            tracker.Register(user, connectionId, session.Generation,
                             TimeSpan.FromSeconds(5.))
        let request = beginWitness store user agent
        store.TryMarkAgentDispatched(
            user, request.Id, "device", connectionId, session.Generation, correlationId)
        |> shouldEqual (Ok ())
        let hub =
            new HubService(
                users, tracker, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HubService>.Instance)
        hub.Context <- signalRCaller user connectionId
        let response: Common.AgentCommandResponse =
            { Result = Some "{}"; Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use failUpdate = db.CreateCommand()
        failUpdate.CommandText <-
            """CREATE TRIGGER simulated_sqlite_terminal_failure
               BEFORE UPDATE OF state ON agent_execution_witness
               WHEN NEW.state = 'Terminal'
               BEGIN SELECT RAISE(FAIL, 'simulated terminal witness storage outage'); END;"""
        failUpdate.ExecuteNonQuery() |> ignore
        // The canonical result is already from the client. Losing a SQLite
        // receipt must NOT turn that result into a tool error or falsely
        // advance the witness to Terminal.
        hub.SendClientResponse(correlationId, response).GetAwaiter().GetResult()
        Assert.That(trackingTask.IsCompletedSuccessfully, Is.True)
        Assert.That(trackingTask.Result.Result, Is.EqualTo(Some "{}"))
        store.GetAgentExecutionResolution(user, request.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.TryAcquireAgentCorrection(user, agent, "operator-before-recovery").IsError
        |> shouldEqual true
        use recover = db.CreateCommand()
        recover.CommandText <- "DROP TRIGGER simulated_sqlite_terminal_failure;"
        recover.ExecuteNonQuery() |> ignore
        // The tracker has already evicted its correlation. Authenticated
        // duplicate terminal evidence must still complete the durable row.
        hub.SendClientResponse(correlationId, response).GetAwaiter().GetResult()
        store.GetAgentExecutionResolution(user, request.Id)
        |> shouldEqual (Ok AuthenticatedClientFinished)
        let permit = store.TryAcquireAgentCorrection(user, agent, "operator-after-recovery")
        Assert.That(permit.IsOk, Is.True)
        match permit with
        | Ok id ->
            store.TryReleaseAgentCorrection(user, agent, id, true)
            |> shouldEqual (Ok ())
        | Error _ -> ())

[<Test>]
let ``authenticated StartJob handle survives SQLite receipt failure and late recovery without releasing correction`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, connection = "alice", "Worker 3", "job-storage-connection"
        let users = UserService()
        users.TransportConnected(user, connection)
        let session =
            users.Register(
                user,
                { DeviceId = "workstation"; DeviceName = "Workstation"
                  ProtocolVersion = Common.AgentProtocol.version; ClientVersion = "test"
                  SupportedCommands = [ "StartJob" ] },
                connection)
        let tracker = ClientResponseTracker()
        let correlation, expected =
            tracker.Register(user, connection, session.Generation, TimeSpan.FromSeconds(5.))
        let request =
            store.TryBeginAgentOperation(
                user, agent, "StartJob", Some "Jarvis", "server", None,
                Some Common.AgentProtocol.version)
            |> function Ok admitted -> admitted | Error error -> failwith error
        store.TryMarkAgentDispatched(
            user, request.Id, "workstation", connection, session.Generation, correlation)
        |> shouldEqual (Ok ())
        let hub =
            new HubService(
                users, tracker, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HubService>.Instance)
        hub.Context <- signalRCaller user connection
        let payload : Common.StartJobResult =
            { JobId = Guid.NewGuid().ToString("N"); StartedAt = DateTimeOffset.UtcNow }
        let response : Common.AgentCommandResponse =
            { Result = Some(JsonSerializer.Serialize(payload))
              Error = None; Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use trigger = db.CreateCommand()
        trigger.CommandText <-
            """CREATE TRIGGER fail_job_identity_persistence
               BEFORE UPDATE OF job_id ON agent_execution_witness
               WHEN NEW.job_id IS NOT NULL
               BEGIN SELECT RAISE(ABORT, 'simulated job-handle SQLite failure'); END;"""
        trigger.ExecuteNonQuery() |> ignore
        // Client command has already executed. A failed *audit* write must
        // not turn its successful StartJob result into a failed tool call.
        hub.SendClientResponse(correlation, response).GetAwaiter().GetResult()
        Assert.That(expected.IsCompletedSuccessfully, Is.True)
        expected.Result.Result |> shouldEqual response.Result
        let inspect (current: FeedbackStore) =
            match current.ListUnresolvedAgentExecutions(user, agent, 10) with
            | Ok page -> page.Executions |> List.find (fun e -> e.RequestId = request.Id)
            | Error error -> failwith error
        (inspect store).JobId |> shouldEqual None
        (inspect store).State |> shouldEqual "Dispatched"
        store.TryAcquireAgentCorrection(user, agent, "operator-during-sqlite-failure").IsError
        |> shouldEqual true
        use recover = db.CreateCommand()
        recover.CommandText <- "DROP TRIGGER fail_job_identity_persistence;"
        recover.ExecuteNonQuery() |> ignore
        // The original server tracker has evicted its correlation. The same
        // authenticated client may retry the original response as evidence,
        // but no handle response is terminal process-tree evidence.
        hub.SendClientResponse(correlation, response).GetAwaiter().GetResult()
        (inspect store).JobId |> shouldEqual (Some payload.JobId)
        (inspect store).JobStartedAt |> shouldEqual (Some payload.StartedAt)
        Assert.That((inspect store).JobReceiptAt.IsSome, Is.True)
        use restarted = new FeedbackStore(store.DatabasePath)
        let afterRestart = inspect restarted
        afterRestart.JobId |> shouldEqual (Some payload.JobId)
        afterRestart.State |> shouldEqual "Dispatched"
        restarted.GetAgentExecutionResolution(user, request.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        restarted.TryAcquireAgentCorrection(user, agent, "operator-after-late-job-handle").IsError
        |> shouldEqual true)

[<Test>]
let ``late SignalR receipt accepts exact active generation but rejects replaced device`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, deviceId = "alice", "Worker 3", "device"
        let users = UserService()
        let registration: Common.SignalR.ClientRegistration =
            { DeviceId = deviceId; DeviceName = "Workstation"
              ProtocolVersion = Common.AgentProtocol.version
              ClientVersion = "test"; SupportedCommands = [ "TaskRead" ] }
        users.TransportConnected(user, "old-conn")
        let prior = users.Register(user, registration, "old-conn")
        let tracker = ClientResponseTracker()
        let correlationLate, timedOut =
            tracker.Register(user, "old-conn", prior.Generation, TimeSpan.FromMilliseconds(30.))
        let first = beginWitness store user agent
        store.TryMarkAgentDispatched(
            user, first.Id, deviceId, "old-conn", prior.Generation, correlationLate)
        |> shouldEqual (Ok ())
        Assert.That(
            System.Threading.SpinWait.SpinUntil(
                (fun () -> timedOut.IsCanceled), TimeSpan.FromSeconds(3.)),
            Is.True, "The server-side response correlation must time out first.")
        let response: Common.AgentCommandResponse =
            { Result = Some "{}"; Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let hubFrom connection =
            let result =
                new HubService(
                    users, tracker, store,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HubService>.Instance)
            result.Context <- signalRCaller user connection
            result
        let oldHub = hubFrom "old-conn"
        // The late client reply is authenticated by the still-current route,
        // even though ClientResponseTracker has evicted its correlation.
        oldHub.SendClientResponse(correlationLate, response).GetAwaiter().GetResult()
        store.GetAgentExecutionResolution(user, first.Id)
        |> shouldEqual (Ok AuthenticatedClientFinished)
        let correlationReplaced, _ =
            tracker.Register(user, "old-conn", prior.Generation, TimeSpan.FromSeconds(5.))
        let second = beginWitness store user agent
        store.TryMarkAgentDispatched(
            user, second.Id, deviceId, "old-conn", prior.Generation, correlationReplaced)
        |> shouldEqual (Ok ())
        users.TransportConnected(user, "new-conn")
        let nextGeneration = users.Register(user, registration, "new-conn")
        Assert.That(nextGeneration.Generation, Is.GreaterThan(prior.Generation))
        // A reply from either connection must NOT reuse the old exact route:
        // the old connection is no longer registered, while the new
        // generation lacks the old correlation/connection binding.
        oldHub.SendClientResponse(correlationReplaced, response).GetAwaiter().GetResult()
        let newHub = hubFrom "new-conn"
        newHub.SendClientResponse(correlationReplaced, response).GetAwaiter().GetResult()
        store.GetAgentExecutionResolution(user, second.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.TryAcquireAgentCorrection(user, agent, "operator-after-reconnect").IsError
        |> shouldEqual true)

[<Test>]
let ``witness-bound ClientService rejects all known non-dispatch routes without invoking client`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent = "alice", "Worker 3"
        let users = UserService()
        let tracker = ClientResponseTracker()
        let mutable sends = 0
        let recipient =
            { new Common.SignalR.IClientService with
                member _.ReceiveMessage(_) = Task.CompletedTask
                member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                member _.ReceiveCommand(_, _, _) =
                    sends <- sends + 1
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
        let accessor = HttpContextAccessor()
        accessor.HttpContext <- DefaultHttpContext()
        let service = ClientService(hub, users, tracker, accessor, store)
        let attempt requestId =
            service.SendCommandToUser(
                user, Common.ListProjectsCommand,
                agentId = agent, activityRequestId = requestId)
            |> fun command -> command.GetAwaiter().GetResult()
        // No selected device: the original request is admitted but cannot
        // be sent, so only explicit verified pre-send cleanup may close it.
        let missing = beginWitness store user agent
        Assert.That((attempt missing.Id).Error.IsSome, Is.True)
        store.GetAgentExecutionResolution(user, missing.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.TryMarkAgentNotDispatched(user, missing.Id) |> shouldEqual (Ok ())
        users.TransportConnected(user, "conn")
        let register protocol capabilities =
            users.Register(
                user,
                { DeviceId = "device"; DeviceName = "Workstation"
                  ProtocolVersion = protocol; ClientVersion = "test"
                  SupportedCommands = capabilities },
                "conn") |> ignore
        // Neither 3.63 nor deployed mainline 3.64 has the complete P1
        // authenticated witness and deferred ToolOperation terminal contract,
        // even when the advertised command capability itself matches.
        for legacyProtocol in [ "3.63"; "3.64" ] do
            register legacyProtocol [ "ListProjects" ]
            let legacy = beginWitness store user agent
            Assert.That((attempt legacy.Id).Error.IsSome, Is.True)
            store.GetAgentExecutionResolution(user, legacy.Id)
            |> shouldEqual (Ok ExecutionUnresolved)
            store.TryMarkAgentNotDispatched(user, legacy.Id) |> shouldEqual (Ok ())
        // Matching protocol without the exact advertised capability must
        // also be pre-send refused.
        register Common.AgentProtocol.version []
        let unsupported = beginWitness store user agent
        Assert.That((attempt unsupported.Id).Error.IsSome, Is.True)
        store.GetAgentExecutionResolution(user, unsupported.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        store.TryMarkAgentNotDispatched(user, unsupported.Id)
        |> shouldEqual (Ok ())
        // Even if a route is viable, a failed durable route bind prevents
        // dispatch, independently of SignalR transport readiness.
        register Common.AgentProtocol.version [ "ListProjects" ]
        let invalid = beginWitness store user agent
        store.TryMarkAgentNotDispatched(user, invalid.Id) |> shouldEqual (Ok ())
        Assert.That((attempt invalid.Id).Error.IsSome, Is.True)
        store.GetAgentExecutionResolution(user, invalid.Id)
        |> shouldEqual (Ok NeverDispatched)
        Assert.That(sends, Is.EqualTo(0), "No rejected route may reach ReceiveCommand."))

[<Test>]
let ``authenticated promoted ToolOperation receipt is never original terminal evidence`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, conn, device = "alice", "Worker 3", "tool-job-conn", "device-1"
        let users = UserService()
        users.TransportConnected(user, conn)
        let registered =
            users.Register(
                user,
                { DeviceId = device
                  DeviceName = "Test workstation"
                  ProtocolVersion = Common.AgentProtocol.version
                  ClientVersion = "test"
                  SupportedCommands = [ "RunCommand"; "GetJobResult" ] },
                conn)
        let tracker = ClientResponseTracker()
        let correlation, expected =
            tracker.Register(user, conn, registered.Generation, TimeSpan.FromSeconds(5.))
        let request =
            store.TryBeginAgentOperation(
                user, agent, "RunCommand", Some "Jarvis", "server",
                Some "test", Some Common.AgentProtocol.version)
            |> function Ok value -> value | Error failure -> failwith failure
        store.TryMarkAgentDispatched(
            user, request.Id, device, conn, registered.Generation, correlation)
        |> shouldEqual (Ok ())
        let hub =
            new HubService(
                users, tracker, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HubService>.Instance)
        hub.Context <- signalRCaller user conn
        let handle : Common.ToolJobHandle =
            { JobId = Guid.NewGuid().ToString("N")
              Kind = Common.JobKind.ToolOperation
              Status = "Running"
              ProjectName = Some "Jarvis"
              DeviceId = None
              AgentId = agent
              OperationId = Some(Guid.NewGuid().ToString("D"))
              CommandName = "RunCommand"
              StartedAt = DateTimeOffset.UtcNow }
        let reply : Common.AgentCommandResponse =
            { Result = Some(JsonSerializer.Serialize(handle))
              Error = None
              Instructions = []
              AgentStateChanges = []
              InboxActivation = None
              PendingJob = Some handle }
        hub.SendClientResponse(correlation, reply).GetAwaiter().GetResult()
        Assert.That(expected.IsCompletedSuccessfully, Is.True)
        Assert.That(expected.Result.PendingJob, Is.EqualTo(Some handle))
        let inspect (current: FeedbackStore) =
            match current.ListUnresolvedAgentExecutions(user, agent, 10) with
            | Ok page -> page.Executions |> List.find (fun e -> e.RequestId = request.Id)
            | Error failure -> failwith failure
        let witnessed = inspect store
        witnessed.DeferredToolJobId |> shouldEqual (Some handle.JobId)
        witnessed.DeferredToolJobOperationId |> shouldEqual handle.OperationId
        witnessed.DeferredToolJobStartedAt |> shouldEqual (Some handle.StartedAt)
        Assert.That(witnessed.DeferredToolJobReceiptAt.IsSome, Is.True)
        witnessed.State |> shouldEqual "Dispatched"
        store.GetAgentExecutionResolution(user, request.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        // Neither replay nor an accidental generic terminal receipt can
        // promote the original while its client-owned tool is still running.
        hub.SendClientResponse(correlation, reply).GetAwaiter().GetResult()
        (inspect store).DeferredToolJobReceiptAt
        |> shouldEqual witnessed.DeferredToolJobReceiptAt
        store.TryRecordAgentClientTerminal(
            user, conn, registered.Generation, correlation).IsError
        |> shouldEqual true
        use restarted = new FeedbackStore(store.DatabasePath)
        (inspect restarted).DeferredToolJobId |> shouldEqual (Some handle.JobId)
        restarted.TryAcquireAgentCorrection(user, agent, "operator-while-job-pending").IsError
        |> shouldEqual true)

[<Test>]
let ``authenticated promoted job poll proves terminal only on original route`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, actor, device, connection = "alice", "Worker 3", "device-a", "client-a"
        let users = UserService()
        users.TransportConnected(user, connection)
        let session = users.Register(user,
            { DeviceId = device; DeviceName = "Workstation"
              ProtocolVersion = Common.AgentProtocol.version; ClientVersion = "test"
              SupportedCommands = [ "ReadFiles"; "GetJobResult" ] }, connection)
        let tracker = ClientResponseTracker()
        let makeHub (source: FeedbackStore) =
            let hub = HubService(users, tracker, source,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HubService>.Instance)
            hub.Context <- signalRCaller user connection
            hub
        let correlation, initial =
            tracker.Register(user, connection, session.Generation, TimeSpan.FromSeconds(5.))
        let witness =
            store.TryBeginAgentOperation(
                user, actor, "ReadFiles", Some "Jarvis", "server", None,
                Some Common.AgentProtocol.version)
            |> function Ok row -> row | Error failure -> failwith failure
        store.TryMarkAgentDispatched(user, witness.Id, device,
            connection, session.Generation, correlation) |> shouldEqual (Ok ())
        let handle : Common.ToolJobHandle =
            { JobId = Guid.NewGuid().ToString("N")
              Kind = Common.JobKind.ToolOperation; Status = "Running"
              ProjectName = Some "Jarvis"; DeviceId = None; AgentId = actor
              OperationId = Some(Guid.NewGuid().ToString("D"))
              CommandName = "ReadFiles"; StartedAt = DateTimeOffset.UtcNow }
        let ordinary payload : Common.AgentCommandResponse =
            { Result = Some payload; Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let pending = { (ordinary(JsonSerializer.Serialize(handle))) with PendingJob = Some handle }
        (makeHub store).SendClientResponse(correlation, pending).GetAwaiter().GetResult()
        Assert.That(initial.IsCompletedSuccessfully, Is.True)
        let bindPoll route (source: FeedbackStore) =
            source.TryRecordAgentToolJobPollDispatch(user, device, connection,
                session.Generation, route, actor, "Jarvis", handle.JobId)
        let result status final op : Common.JobResult =
            { JobId = handle.JobId; Status = status; Events = []
              NextSequence = 0L; Truncated = false; ToolResponse = final; OperationId = op }
        let poll status final op =
            ordinary(JsonSerializer.Serialize(result status final op))
        let readStatus (source: FeedbackStore) =
            source.GetAgentExecutionResolution(user, witness.Id)
        let first, _ = tracker.Register(user, connection, session.Generation, TimeSpan.FromSeconds(5.))
        bindPoll first store |> shouldEqual (Ok(Some witness.Id))
        (makeHub store).SendClientResponse(
            first, poll Common.Running None handle.OperationId).GetAwaiter().GetResult()
        readStatus store |> shouldEqual (Ok ExecutionUnresolved)
        let wrong, _ = tracker.Register(user, connection, session.Generation, TimeSpan.FromSeconds(5.))
        bindPoll wrong store |> shouldEqual (Ok(Some witness.Id))
        (makeHub store).SendClientResponse(wrong,
            poll Common.ToolCompleted (Some(ordinary "{}"))
                (Some(Guid.NewGuid().ToString("D")))).GetAwaiter().GetResult()
        readStatus store |> shouldEqual (Ok ExecutionUnresolved)
        store.TryRecordAgentToolJobPollDispatch(user, "other-device", connection,
            session.Generation, "wrong-device-poll", actor, "Jarvis", handle.JobId)
        |> shouldEqual (Ok None)
        store.TryRecordAgentToolJobPollDispatch(user, device, connection,
            session.Generation, "wrong-project-poll", actor, "OtherProject", handle.JobId)
        |> shouldEqual (Ok None)
        store.TryRecordAgentToolJobPollDispatch(user, device, connection,
            session.Generation, "wrong-actor-poll", "Other Agent", "Jarvis", handle.JobId)
        |> shouldEqual (Ok None)
        let last, awaited = tracker.Register(user, connection, session.Generation, TimeSpan.FromSeconds(5.))
        bindPoll last store |> shouldEqual (Ok(Some witness.Id))
        use restarted = new FeedbackStore(store.DatabasePath)
        let hub = makeHub restarted
        let finished = poll Common.ToolCompleted (Some(ordinary "{}")) handle.OperationId
        // The caller gets the genuine result even if SQLite fails to persist
        // termination. The original must remain unresolved until an exact
        // authenticated late retry succeeds after the storage fault clears.
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use reject = db.CreateCommand()
        reject.CommandText <-
            $"CREATE TRIGGER reject_terminal_job BEFORE UPDATE OF state ON agent_execution_witness " +
            $"WHEN NEW.state = 'Terminal' AND NEW.request_id = '{witness.Id}' " +
            "BEGIN SELECT RAISE(ABORT, 'simulated terminal attestation failure'); END;"
        reject.ExecuteNonQuery() |> ignore
        hub.SendClientResponse(last, finished).GetAwaiter().GetResult()
        Assert.That(awaited.IsCompletedSuccessfully, Is.True)
        readStatus restarted |> shouldEqual (Ok ExecutionUnresolved)
        use recover = db.CreateCommand()
        recover.CommandText <- "DROP TRIGGER reject_terminal_job;"
        recover.ExecuteNonQuery() |> ignore
        hub.SendClientResponse(last, finished).GetAwaiter().GetResult()
        readStatus restarted |> shouldEqual (Ok AuthenticatedClientFinished)
        hub.SendClientResponse(last, finished).GetAwaiter().GetResult()
        readStatus restarted |> shouldEqual (Ok AuthenticatedClientFinished))

[<Test>]
let ``ClientService binds GetJobResult before dispatch and HubService attests original`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, actor, device, connection = "alice", "Worker 3", "device", "conn"
        let users = UserService()
        users.TransportConnected(user, connection)
        let session =
            users.Register(user,
                { DeviceId = device; DeviceName = "Workstation"
                  ProtocolVersion = Common.AgentProtocol.version
                  ClientVersion = "test"
                  SupportedCommands = [ "ReadFiles"; "GetJobResult" ] },
                connection)
        let tracker = ClientResponseTracker()
        let clientHub =
            HubService(users, tracker, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HubService>.Instance)
        clientHub.Context <- signalRCaller user connection
        let originalCorrelation, _ =
            tracker.Register(user, connection, session.Generation, TimeSpan.FromSeconds(5.))
        let original =
            store.TryBeginAgentOperation(
                user, actor, "ReadFiles", Some "Jarvis", "server", None,
                Some Common.AgentProtocol.version)
            |> function Ok row -> row | Error failure -> failwith failure
        store.TryMarkAgentDispatched(user, original.Id, device,
            connection, session.Generation, originalCorrelation)
        |> shouldEqual (Ok ())
        let handle : Common.ToolJobHandle =
            { JobId = Guid.NewGuid().ToString("N")
              Kind = Common.JobKind.ToolOperation; Status = "Running"
              ProjectName = Some "Jarvis"; DeviceId = None; AgentId = actor
              OperationId = Some(Guid.NewGuid().ToString("D"))
              CommandName = "ReadFiles"; StartedAt = DateTimeOffset.UtcNow }
        let terminal : Common.AgentCommandResponse =
            { Result = Some "{}"; Error = None
              Instructions = []; AgentStateChanges = []
              InboxActivation = None; PendingJob = None }
        let initial =
            { terminal with Result = Some(JsonSerializer.Serialize(handle))
                            PendingJob = Some handle }
        clientHub.SendClientResponse(originalCorrelation, initial).GetAwaiter().GetResult()
        let mutable dispatched = 0
        let recipient =
            { new Common.SignalR.IClientService with
                member _.ReceiveMessage(_) = Task.CompletedTask
                member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                member _.ReceiveCommand(correlationId, commandJson, routedActor) =
                    dispatched <- dispatched + 1
                    Assert.That(routedActor, Is.EqualTo(actor))
                    match JsonSerializer.Deserialize<Common.AgentCommand>(commandJson) with
                    | Common.GetJobResultCommand request ->
                        Assert.That(request.JobId, Is.EqualTo(handle.JobId))
                        let result : Common.JobResult =
                            { JobId = handle.JobId; Status = Common.ToolCompleted
                              Events = []; NextSequence = 0L; Truncated = false
                              ToolResponse = Some terminal; OperationId = handle.OperationId }
                        clientHub.SendClientResponse(
                            correlationId,
                            { terminal with Result = Some(JsonSerializer.Serialize(result)) }
                        ).GetAwaiter().GetResult()
                        Task.CompletedTask
                    | _ -> failwith "Unexpected client command." }
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
        let hubContext =
            { new Microsoft.AspNetCore.SignalR.IHubContext<HubService, Common.SignalR.IClientService> with
                member _.Clients = clients
                member _.Groups = Unchecked.defaultof<_> }
        let accessor = HttpContextAccessor()
        accessor.HttpContext <- DefaultHttpContext()
        let service = ClientService(hubContext, users, tracker, accessor, store)
        let response =
            service.SendCommandToUser(
                user, Common.GetJobResultCommand
                    { ProjectName = "Jarvis"; JobId = handle.JobId; AfterSequence = None },
                agentId = actor)
            |> fun task -> task.GetAwaiter().GetResult()
        Assert.That(response.Error.IsNone, Is.True)
        Assert.That(dispatched, Is.EqualTo(1))
        store.GetAgentExecutionResolution(user, original.Id)
        |> shouldEqual (Ok AuthenticatedClientFinished))

[<Test>]
let ``completed ToolOperation poll cannot clear process-running execution witness`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, device, connection, generation =
            "alice", "Worker 3", "device", "conn", 8L
        let request =
            store.TryBeginAgentOperation(
                user, agent, "RunCommand", Some "Jarvis", "server", None,
                Some Common.AgentProtocol.version)
            |> function Ok row -> row | Error failure -> failwith failure
        store.TryMarkAgentDispatched(
            user, request.Id, device, connection, generation, "process-correlation")
        |> shouldEqual (Ok ())
        let jobId, operationId = Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D")
        let startedAt = DateTimeOffset.UtcNow
        store.TryRecordAgentDeferredToolJob(
            user, connection, generation, "process-correlation",
            agent, "RunCommand", jobId, startedAt,
            operationId = operationId, projectName = "Jarvis")
        |> shouldEqual (Ok(Some request.Id))
        store.TryRecordAgentToolJobPollDispatch(
            user, device, connection, generation, "job-poll",
            agent, "Jarvis", jobId)
        |> shouldEqual (Ok(Some request.Id))
        let completed : Common.AgentCommandResponse =
            { Result = Some "{}"; Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let result : Common.JobResult =
            { JobId = jobId; Status = Common.ToolCompleted; Events = []
              NextSequence = 0L; Truncated = false
              ToolResponse = Some completed; OperationId = Some operationId }
        let poll = { completed with Result = Some(JsonSerializer.Serialize(result)) }
        store.TryRecordAgentToolJobPollTerminal(
            user, "different-connection", generation, "job-poll", poll)
        |> shouldEqual (Ok None)
        store.TryRecordAgentToolJobPollTerminal(
            user, connection, generation + 1L, "job-poll", poll)
        |> shouldEqual (Ok None)
        store.TryRecordAgentToolJobPollTerminal(
            user, connection, generation, "job-poll", poll).IsError
        |> shouldEqual true
        store.GetAgentExecutionResolution(user, request.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.TryAcquireAgentCorrection(
            user, agent, "operator-on-process-poll").IsError
        |> shouldEqual true)

[<Test>]
let ``authenticated StartJob client response does not prove child process exit`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, conn = "alice", "Worker 3", "start-job-conn"
        let users = UserService()
        users.TransportConnected(user, conn)
        let registered =
            users.Register(
                user,
                { DeviceId = "start-job-device"
                  DeviceName = "Workstation"
                  ProtocolVersion = Common.AgentProtocol.version
                  ClientVersion = "test"
                  SupportedCommands = [ "StartJob"; "GetJobResult" ] },
                conn)
        let tracker = ClientResponseTracker()
        let correlation, expected =
            tracker.Register(user, conn, registered.Generation, TimeSpan.FromSeconds(5.))
        let start =
            store.TryBeginAgentOperation(
                user, agent, "StartJob", Some "Jarvis", "server",
                Some "test", Some Common.AgentProtocol.version)
            |> function Ok operation -> operation | Error error -> failwith error
        store.TryMarkAgentDispatched(
            user, start.Id, "start-job-device", conn, registered.Generation, correlation)
        |> shouldEqual (Ok ())
        let hub =
            new HubService(
                users, tracker, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HubService>.Instance)
        hub.Context <- signalRCaller user conn
        let payload : Common.StartJobResult =
            { JobId = "0123456789abcdef0123456789abcdef"; StartedAt = DateTimeOffset.UtcNow }
        let response: Common.AgentCommandResponse =
            { Result = Some(System.Text.Json.JsonSerializer.Serialize(payload))
              Error = None; Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        // The tool successfully returns the handle, but that does NOT
        // establish whether its detached child process is still running.
        hub.SendClientResponse(correlation, response).GetAwaiter().GetResult()
        Assert.That(expected.IsCompletedSuccessfully, Is.True)
        Assert.That(expected.Result.Result, Is.EqualTo(response.Result))
        store.GetAgentExecutionResolution(user, start.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        let exact =
            match store.ListUnresolvedAgentExecutions(user, agent, 10) with
            | Ok page -> page.Executions |> List.find (fun item -> item.RequestId = start.Id)
            | Error error -> failwith error
        exact.JobId |> shouldEqual (Some payload.JobId)
        exact.JobStartedAt |> shouldEqual (Some payload.StartedAt)
        Assert.That(exact.JobReceiptAt.IsSome, Is.True)
        let firstReceiptAt = exact.JobReceiptAt
        store.TryAcquireAgentCorrection(user, agent, "operator-after-handle").IsError
        |> shouldEqual true
        hub.SendClientResponse(correlation, response).GetAwaiter().GetResult()
        let duplicate =
            match store.ListUnresolvedAgentExecutions(user, agent, 10) with
            | Ok page -> page.Executions |> List.find (fun item -> item.RequestId = start.Id)
            | Error error -> failwith error
        duplicate.JobId |> shouldEqual (Some payload.JobId)
        duplicate.JobReceiptAt |> shouldEqual firstReceiptAt
        store.GetAgentExecutionResolution(user, start.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        // A completed GetJobResult request is distinct from a confirmed
        // process-tree drain; it must not accidentally clear the StartJob.
        let poll =
            store.TryBeginAgentOperation(
                user, agent, "GetJobResult", Some "Jarvis", "server",
                Some "test", Some Common.AgentProtocol.version)
            |> function Ok operation -> operation | Error error -> failwith error
        store.TryMarkAgentDispatched(
            user, poll.Id, "start-job-device", conn,
            registered.Generation, "job-result-correlation")
        |> shouldEqual (Ok ())
        store.TryRecordAgentClientTerminal(
            user, conn, registered.Generation, "job-result-correlation")
        |> shouldEqual (Ok(Some poll.Id))
        store.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 1
        store.TryAcquireAgentCorrection(user, agent, "operator-after-poll").IsError
        |> shouldEqual true)

[<Test>]
let ``StartJob launch receipt cannot be replaced, reused or forged across device identity`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, connection, device, generation =
            "alice", "Worker 3", "device-session", "workstation", 4L
        let admit correlation =
            let operation =
                store.TryBeginAgentOperation(
                    user, agent, "StartJob", Some "Jarvis", "server",
                    Some "client", Some Common.AgentProtocol.version)
                |> function Ok value -> value | Error failure -> failwith failure
            store.TryMarkAgentDispatched(user, operation.Id, device, connection, generation, correlation)
            |> shouldEqual (Ok ())
            operation.Id
        let firstRequest = admit "first-launch-correlation"
        let startedAt = DateTimeOffset.UtcNow
        let jobId = Guid.NewGuid().ToString("N")
        let payload id start : Common.StartJobResult = { JobId = id; StartedAt = start }
        let response id start : Common.AgentCommandResponse =
            { Result = Some(JsonSerializer.Serialize(payload id start))
              Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let firstResponse = response jobId startedAt
        // Successful launch is a nonterminal result: the handle is durable.
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "first-launch-correlation",
            clientResponse = firstResponse).IsError
        |> shouldEqual true
        let read request =
            match store.ListUnresolvedAgentExecutions(user, agent, 100) with
            | Ok page -> page.Executions |> List.find (fun item -> item.RequestId = request)
            | Error failure -> failwith failure
        let first = read firstRequest
        first.JobId |> shouldEqual (Some jobId)
        first.JobStartedAt |> shouldEqual (Some startedAt)
        Assert.That(first.JobReceiptAt.IsSome, Is.True)
        let receiptTime = first.JobReceiptAt
        // Neither an unrelated authenticated connection nor a replacement
        // generation may register a handle for the original routed request.
        store.TryRecordAgentClientTerminal(
            user, "other-connection", generation, "first-launch-correlation",
            clientResponse = response (Guid.NewGuid().ToString("N")) startedAt)
        |> shouldEqual (Ok None)
        store.TryRecordAgentClientTerminal(
            user, connection, generation + 1L, "first-launch-correlation",
            clientResponse = response (Guid.NewGuid().ToString("N")) startedAt)
        |> shouldEqual (Ok None)
        store.TryRecordAgentClientTerminal(
            "bob", connection, generation, "first-launch-correlation",
            clientResponse = response (Guid.NewGuid().ToString("N")) startedAt)
        |> shouldEqual (Ok None)
        // Same routed identity but changed jobId or launch timestamp is also
        // rejected without replacing the first authenticated receipt.
        for bad in
            [ response (Guid.NewGuid().ToString("N")) startedAt
              response jobId (startedAt.AddSeconds(1.0)) ] do
            store.TryRecordAgentClientTerminal(
                user, connection, generation, "first-launch-correlation",
                clientResponse = bad).IsError |> shouldEqual true
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "first-launch-correlation",
            clientResponse = firstResponse).IsError |> shouldEqual true
        let retained = read firstRequest
        retained.JobId |> shouldEqual first.JobId
        retained.JobStartedAt |> shouldEqual first.JobStartedAt
        retained.JobReceiptAt |> shouldEqual receiptTime
        // Two concurrent starts on one physical device cannot legitimately
        // identify the same job. Reject second receipt rather than creating
        // ambiguous evidence that a later drain might incorrectly reuse.
        let secondRequest = admit "second-launch-correlation"
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "second-launch-correlation",
            clientResponse = firstResponse).IsError |> shouldEqual true
        (read secondRequest).JobId |> shouldEqual None
        // Classification and first handle persist after telemetry pruning
        // and full FeedbackStore reconstruction.
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use prune = db.CreateCommand()
        prune.CommandText <- "DELETE FROM operations WHERE id IN ($first, $second);"
        prune.Parameters.AddWithValue("$first", firstRequest) |> ignore
        prune.Parameters.AddWithValue("$second", secondRequest) |> ignore
        prune.ExecuteNonQuery() |> shouldEqual 2
        use restarted = new FeedbackStore(store.DatabasePath)
        let afterRestart =
            match restarted.ListUnresolvedAgentExecutions(user, agent, 100) with
            | Ok page -> page.Executions |> List.find (fun item -> item.RequestId = firstRequest)
            | Error failure -> failwith failure
        afterRestart.JobId |> shouldEqual (Some jobId)
        afterRestart.JobStartedAt |> shouldEqual (Some startedAt)
        afterRestart.JobReceiptAt |> shouldEqual receiptTime
        restarted.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 2
        restarted.TryAcquireAgentCorrection(user, agent, "operator-after-job-evidence").IsError
        |> shouldEqual true)

[<Test>]
let ``StartJob missing malformed and noncanonical receipts never certify liveness`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, device, connection, generation =
            "alice", "Worker 3", "workstation", "session", 2L
        let validId = Guid.NewGuid().ToString("N")
        let successful payload : Common.AgentCommandResponse =
            { Result = Some payload; Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let payload id timestamp =
            JsonSerializer.Serialize(
                { JobId = id; StartedAt = timestamp } : Common.StartJobResult)
        let responses : Common.AgentCommandResponse list =
            [ successful "{}"
              successful "not valid JSON"
              successful (payload "" DateTimeOffset.UtcNow)
              successful (payload "job-child-still-running" DateTimeOffset.UtcNow)
              successful (payload (validId.ToUpperInvariant()) DateTimeOffset.UtcNow)
              successful (payload (Guid.Empty.ToString("N")) DateTimeOffset.UtcNow)
              successful (payload validId DateTimeOffset.MinValue)
              { Result = None; Error = Some(Common.ExecutionFailed "Client denied execution")
                Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
              { Result = None; Error = None; Instructions = []
                AgentStateChanges = []; InboxActivation = None; PendingJob = None } ]
        let allRequests =
            responses
            |> List.mapi (fun i failed ->
                let req =
                    store.TryBeginAgentOperation(
                        user, agent, "StartJob", Some "Jarvis", "server", None, None)
                    |> function Ok value -> value | Error failure -> failwith failure
                let correlation = $"invalid-job-{i}"
                store.TryMarkAgentDispatched(
                    user, req.Id, device, connection, generation, correlation)
                |> shouldEqual (Ok ())
                store.TryRecordAgentClientTerminal(
                    user, connection, generation, correlation,
                    clientResponse = failed).IsError |> shouldEqual true
                req.Id)
        use restarted = new FeedbackStore(store.DatabasePath)
        let evidence =
            match restarted.ListUnresolvedAgentExecutions(user, agent, 100) with
            | Ok page -> page.Executions
            | Error failure -> failwith failure
        evidence.Length |> shouldEqual allRequests.Length
        for item in evidence do
            item.ToolName |> shouldEqual (Some "StartJob")
            item.JobId |> shouldEqual None
            item.JobStartedAt |> shouldEqual None
            item.JobReceiptAt |> shouldEqual None
            item.State |> shouldEqual "Dispatched"
        restarted.TryAcquireAgentCorrection(user, agent, "operator-after-invalid-launch").IsError
        |> shouldEqual true)

[<Test>]
let ``unresolved evidence pages preserve exact device routing across restart and telemetry loss`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent = "alice", "Worker 3"
        let job =
            store.TryBeginAgentOperation(
                user, agent, "StartJob", Some "Jarvis", "server", None, None)
            |> function Ok activity -> activity | Error failure -> failwith failure
        store.TryMarkAgentDispatched(
            user, job.Id, "workstation", "device-connection", 7L, "job-correlation")
        |> shouldEqual (Ok ())
        let pending = beginWitness store user agent
        let other = beginWitness store user "Other Agent"
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use prune = db.CreateCommand()
        prune.CommandText <- "DELETE FROM operations WHERE id IN ($first, $second);"
        prune.Parameters.AddWithValue("$first", job.Id) |> ignore
        prune.Parameters.AddWithValue("$second", pending.Id) |> ignore
        prune.ExecuteNonQuery() |> ignore
        use restarted = new FeedbackStore(store.DatabasePath)
        let page =
            restarted.ListUnresolvedAgentExecutions(user, "worker 3", 1)
            |> function Ok results -> results | Error failure -> failwith failure
        page.Executions.Length |> shouldEqual 1
        page.HasMore |> shouldEqual true
        let complete =
            restarted.ListUnresolvedAgentExecutions(user, agent, 10)
            |> function Ok results -> results | Error failure -> failwith failure
        complete.HasMore |> shouldEqual false
        complete.Executions.Length |> shouldEqual 2
        let detached =
            complete.Executions |> List.find (fun record -> record.RequestId = job.Id)
        detached.ToolName |> shouldEqual (Some "StartJob")
        detached.State |> shouldEqual "Dispatched"
        detached.ProjectName |> shouldEqual (Some "Jarvis")
        detached.DeviceId |> shouldEqual (Some "workstation")
        detached.ConnectionId |> shouldEqual (Some "device-connection")
        detached.DeviceGeneration |> shouldEqual (Some 7L)
        detached.CorrelationId |> shouldEqual (Some "job-correlation")
        detached.ParentRequestId |> shouldEqual None
        let admitted =
            complete.Executions |> List.find (fun record -> record.RequestId = pending.Id)
        admitted.State |> shouldEqual "Admitted"
        admitted.DeviceGeneration |> shouldEqual None
        let otherPage =
            restarted.ListUnresolvedAgentExecutions(user, "Other Agent", 10)
            |> function Ok results -> results | Error failure -> failwith failure
        Assert.That(otherPage.Executions |> List.exists (fun x -> x.RequestId = other.Id), Is.True)
        restarted.ListUnresolvedAgentExecutions("bob", agent, 10)
        |> shouldEqual (Ok { Executions = []; HasMore = false })
        restarted.ListUnresolvedAgentExecutions(user, agent, 0).IsError
        |> shouldEqual true
        restarted.ListUnresolvedAgentExecutions(user, agent, 501).IsError
        |> shouldEqual true
        // The audit page never constitutes permission to correct: both
        // unrelated pending admissions and detached child work still veto it.
        restarted.TryAcquireAgentCorrection(user, agent, "operator-with-audit-page").IsError
        |> shouldEqual true)

[<Test>]
let ``telemetry pruning never loses durable StartJob execution classification`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, connection, generation = "alice", "Worker 3", "client", 3L
        let start =
            store.TryBeginAgentOperation(
                user, agent, "StartJob", Some "Jarvis",
                "server", Some "client", Some Common.AgentProtocol.version)
            |> function Ok activity -> activity | Error failure -> failwith failure
        store.TryMarkAgentDispatched(
            user, start.Id, "desktop", connection, generation, "start-correlation")
        |> shouldEqual (Ok ())
        // Simulate the independent 30-day telemetry retention. StartJob
        // classification MUST remain on the durable witness, not the
        // disposable operations table.
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use prune = db.CreateCommand()
        prune.CommandText <- "DELETE FROM operations WHERE id = $id;"
        prune.Parameters.AddWithValue("$id", start.Id) |> ignore
        Assert.That(prune.ExecuteNonQuery(), Is.EqualTo(1))
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.TryRecordAgentClientTerminal(
            user, connection, generation, "start-correlation").IsError
        |> shouldEqual true
        restarted.GetAgentExecutionResolution(user, start.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        restarted.TryAcquireAgentCorrection(user, agent, "operator-after-telemetry-prune").IsError
        |> shouldEqual true
        // Ordinary non-detached operations retain their classification
        // after the same telemetry loss and can still accept exact replies.
        let ordinary =
            restarted.TryBeginAgentOperation(
                user, agent, "ReadFile", Some "Jarvis",
                "server", Some "client", Some Common.AgentProtocol.version)
            |> function Ok activity -> activity | Error failure -> failwith failure
        restarted.TryMarkAgentDispatched(
            user, ordinary.Id, "desktop", connection, generation, "read-correlation")
        |> shouldEqual (Ok ())
        prune.Parameters["$id"].Value <- ordinary.Id
        Assert.That(prune.ExecuteNonQuery(), Is.EqualTo(1))
        restarted.TryRecordAgentClientTerminal(
            user, connection, generation, "read-correlation")
        |> shouldEqual (Ok(Some ordinary.Id))
        restarted.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 1)

[<Test>]
let ``legacy witness migration backfills known command and fails closed when telemetry was lost`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent = "alice", "Worker 3"
        let known =
            store.TryBeginAgentOperation(
                user, agent, "ReadFile", Some "Jarvis",
                "server", None, None)
            |> function Ok activity -> activity | Error failure -> failwith failure
        let unknown =
            store.TryBeginAgentOperation(
                user, agent, "StartJob", Some "Jarvis",
                "server", None, None)
            |> function Ok activity -> activity | Error failure -> failwith failure
        store.TryMarkAgentDispatched(
            user, known.Id, "desktop", "conn", 2L, "known-receipt")
        |> shouldEqual (Ok ())
        store.TryMarkAgentDispatched(
            user, unknown.Id, "desktop", "conn", 2L, "unknown-receipt")
        |> shouldEqual (Ok ())
        // Emulate a pre-upgrade database lacking the new tool_name column.
        // Its historical StartJob telemetry has already expired.
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use removeHistory = db.CreateCommand()
        removeHistory.CommandText <- "DELETE FROM operations WHERE id = $id;"
        removeHistory.Parameters.AddWithValue("$id", unknown.Id) |> ignore
        Assert.That(removeHistory.ExecuteNonQuery(), Is.EqualTo(1))
        use oldSchema = db.CreateCommand()
        oldSchema.CommandText <-
            "ALTER TABLE agent_execution_witness DROP COLUMN tool_name;"
        oldSchema.ExecuteNonQuery() |> ignore
        use migrated = new FeedbackStore(store.DatabasePath)
        migrated.TryRecordAgentClientTerminal(
            user, "conn", 2L, "known-receipt")
        |> shouldEqual (Ok(Some known.Id))
        migrated.TryRecordAgentClientTerminal(
            user, "conn", 2L, "unknown-receipt").IsError
        |> shouldEqual true
        migrated.GetAgentExecutionResolution(user, unknown.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        migrated.TryAcquireAgentCorrection(user, agent, "operator-after-partial-migration").IsError
        |> shouldEqual true)

[<Test>]
let ``promoted tool job remains nonterminal despite its ordinary client reply and survives restart`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, conn, device, generation =
            "alice", "Worker 3", "workstation-conn", "workstation", 3L
        let admit tool correlation =
            let request =
                store.TryBeginAgentOperation(
                    user, agent, tool, Some "Jarvis", "server", None,
                    Some Common.AgentProtocol.version)
                |> function Ok row -> row | Error failure -> failwith failure
            store.TryMarkAgentDispatched(
                user, request.Id, device, conn, generation, correlation)
            |> shouldEqual (Ok ())
            request.Id
        let requestId = admit "RunCommand" "original-correlation"
        let startedAt = DateTimeOffset.UtcNow
        let jobId = Guid.NewGuid().ToString("N")
        let promote actorId commandName route generation jobId =
            store.TryRecordAgentDeferredToolJob(
                user, route, generation, "original-correlation",
                actorId, commandName, jobId, startedAt)
        use failureDb = new SqliteConnection($"Data Source={store.DatabasePath}")
        failureDb.Open()
        use blockReceipt = failureDb.CreateCommand()
        blockReceipt.CommandText <-
            """CREATE TRIGGER fail_promoted_job_receipt
               BEFORE UPDATE OF tool_job_id ON agent_execution_witness
               WHEN NEW.tool_job_id IS NOT NULL
               BEGIN SELECT RAISE(ABORT, 'simulated promoted-job storage failure'); END;"""
        blockReceipt.ExecuteNonQuery() |> ignore
        promote agent "RunCommand" conn generation jobId
        |> fun result -> result.IsError |> shouldEqual true
        store.GetAgentExecutionResolution(user, requestId)
        |> shouldEqual (Ok ExecutionUnresolved)
        use restore = failureDb.CreateCommand()
        restore.CommandText <- "DROP TRIGGER fail_promoted_job_receipt;"
        restore.ExecuteNonQuery() |> ignore
        promote agent "RunCommand" conn generation jobId
        |> shouldEqual (Ok (Some requestId))
        let inspect (source: FeedbackStore) request =
            match source.ListUnresolvedAgentExecutions(user, agent, 100) with
            | Ok page -> page.Executions |> List.find (fun e -> e.RequestId = request)
            | Error failure -> failwith failure
        let original = inspect store requestId
        original.DeferredToolJobId |> shouldEqual (Some jobId)
        original.DeferredToolJobStartedAt |> shouldEqual (Some startedAt)
        Assert.That(original.DeferredToolJobReceiptAt.IsSome, Is.True)
        original.State |> shouldEqual "Dispatched"
        // A reply with PendingJob is not a terminal command response. Even a
        // buggy later generic-terminal call cannot bypass this durable marker.
        store.TryRecordAgentClientTerminal(
            user, conn, generation, "original-correlation").IsError
        |> shouldEqual true
        store.GetAgentExecutionResolution(user, requestId)
        |> shouldEqual (Ok ExecutionUnresolved)
        // Identical retry is idempotent and preserves the first receipt.
        promote agent "RunCommand" conn generation jobId
        |> shouldEqual (Ok (Some requestId))
        (inspect store requestId).DeferredToolJobReceiptAt
        |> shouldEqual original.DeferredToolJobReceiptAt
        promote "Other Agent" "RunCommand" conn generation jobId
        |> fun r -> r.IsError |> shouldEqual true
        promote agent "ReadFiles" conn generation jobId
        |> fun r -> r.IsError |> shouldEqual true
        promote agent "RunCommand" "different-conn" generation jobId
        |> shouldEqual (Ok None)
        promote agent "RunCommand" conn (generation + 1L) jobId
        |> shouldEqual (Ok None)
        promote agent "RunCommand" conn generation (Guid.NewGuid().ToString("N"))
        |> fun r -> r.IsError |> shouldEqual true
        let second = admit "ReadFiles" "second-correlation"
        store.TryRecordAgentDeferredToolJob(
            user, conn, generation, "second-correlation",
            agent, "ReadFiles", jobId, startedAt).IsError
        |> shouldEqual true
        (inspect store second).DeferredToolJobId |> shouldEqual None
        // Explicit process jobs and promoted tool jobs use the same client
        // registry; their IDs must not become ambiguous on one device.
        let processRequest = admit "StartJob" "process-correlation"
        let startResult : Common.StartJobResult =
            { JobId = jobId; StartedAt = startedAt }
        let response : Common.AgentCommandResponse =
            { Result = Some(JsonSerializer.Serialize(startResult)); Error = None
              Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        store.TryRecordAgentClientTerminal(
            user, conn, generation, "process-correlation",
            clientResponse = response).IsError
        |> shouldEqual true
        (inspect store processRequest).JobId |> shouldEqual None
        // Promoted tool identity is also immutable across start-time changes
        // and invalid/corrupt handle encodings.
        store.TryRecordAgentDeferredToolJob(
            user, conn, generation, "original-correlation",
            agent, "RunCommand", jobId, startedAt.AddSeconds(1.)).IsError
        |> shouldEqual true
        promote agent "RunCommand" conn generation (jobId.ToUpperInvariant())
        |> fun r -> r.IsError |> shouldEqual true
        use db = new SqliteConnection($"Data Source={store.DatabasePath}")
        db.Open()
        use prune = db.CreateCommand()
        prune.CommandText <- "DELETE FROM operations WHERE id = $requestId;"
        prune.Parameters.AddWithValue("$requestId", requestId) |> ignore
        prune.ExecuteNonQuery() |> shouldEqual 1
        use restarted = new FeedbackStore(store.DatabasePath)
        let recovered = inspect restarted requestId
        recovered.DeferredToolJobId |> shouldEqual (Some jobId)
        recovered.DeferredToolJobStartedAt |> shouldEqual (Some startedAt)
        recovered.DeferredToolJobReceiptAt |> shouldEqual original.DeferredToolJobReceiptAt
        restarted.TryRecordAgentClientTerminal(
            user, conn, generation, "original-correlation").IsError
        |> shouldEqual true
        restarted.TryAcquireAgentCorrection(user, agent, "operator-pending-tool-job").IsError
        |> shouldEqual true)

[<Test>]
let ``promoted cross-project notification stages retain exact job operation and project provenance`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, connection, generation =
            "alice", "Sender", "notify-connection", 3L
        let beginSource correlation =
            let request =
                store.TryBeginAgentOperation(
                    user, agent, "NotifyAgent", Some "Source",
                    "server", None, Some Common.AgentProtocol.version)
                |> function Ok value -> value | Error failure -> failwith failure
            store.TryMarkAgentDispatched(
                user, request.Id, "device", connection, generation, correlation)
            |> shouldEqual (Ok ())
            request.Id
        let read requestId =
            match store.ListUnresolvedAgentExecutions(user, agent, 20) with
            | Ok page -> page.Executions |> List.find (fun item -> item.RequestId = requestId)
            | Error failure -> failwith failure
        let startedAt = DateTimeOffset.UtcNow
        // The cross-project source agent-inbox read uses the TaskRead wire
        // name, while the logical MCP action is witnessed as NotifyAgent.
        let source = beginSource "source-pending-correlation"
        let sourceJob = Guid.NewGuid().ToString("N")
        store.TryRecordAgentDeferredToolJob(
            user, connection, generation, "source-pending-correlation",
            agent, "TaskRead", sourceJob, startedAt,
            projectName = "Source")
        |> shouldEqual (Ok (Some source))
        (read source).DeferredToolJobId |> shouldEqual (Some sourceJob)
        (read source).DeferredToolJobOperationId |> shouldEqual None
        store.TryBeginAgentContinuation(user, source, "Destination").IsError
        |> shouldEqual true
        // A verified result of the exact source TaskRead job now permits the
        // NotifyAgent continuation, but the pending handle alone did not.
        store.TryRecordAgentToolJobPollDispatch(
            user, "device", connection, generation, "source-job-poll",
            agent, "Source", sourceJob)
        |> shouldEqual (Ok (Some source))
        let terminal : Common.AgentCommandResponse =
            { Result = Some "{}"; Error = None
              Instructions = []; AgentStateChanges = []
              InboxActivation = None; PendingJob = None }
        let sourceResult : Common.JobResult =
            { JobId = sourceJob; Status = Common.ToolCompleted
              Events = []; NextSequence = 0L; Truncated = false
              ToolResponse = Some terminal; OperationId = None }
        store.TryRecordAgentToolJobPollTerminal(
            user, connection, generation, "source-job-poll",
            { terminal with Result = Some(JsonSerializer.Serialize(sourceResult)) })
        |> shouldEqual (Ok (Some source))
        store.GetAgentExecutionResolution(user, source)
        |> shouldEqual (Ok AuthenticatedClientFinished)
        let _reconciledSourceChild =
            store.TryBeginAgentContinuation(user, source, "Destination")
            |> function Ok id -> id | Error failure -> failwith failure
        // A second, genuinely terminal source may create one separate
        // destination witness. The mainline envelope is a TaskWrite job.
        let finishedSource = beginSource "source-finished-correlation"
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "source-finished-correlation")
        |> shouldEqual (Ok (Some finishedSource))
        let child =
            store.TryBeginAgentContinuation(user, finishedSource, "Destination")
            |> function Ok id -> id | Error failure -> failwith failure
        store.TryMarkAgentDispatched(
            user, child, "device", connection, generation, "destination-correlation")
        |> shouldEqual (Ok ())
        let destinationJob = Guid.NewGuid().ToString("N")
        let operationId = Guid.NewGuid().ToString("D")
        let destination job op project =
            store.TryRecordAgentDeferredToolJob(
                user, connection, generation, "destination-correlation",
                agent, "TaskWrite", job, startedAt,
                operationId = op, projectName = project)
        destination destinationJob operationId "WrongProject"
        |> fun result -> result.IsError |> shouldEqual true
        destination destinationJob operationId "Destination"
        |> shouldEqual (Ok (Some child))
        let evidence = read child
        evidence.ParentRequestId |> shouldEqual (Some finishedSource)
        evidence.ProjectName |> shouldEqual (Some "Destination")
        evidence.DeferredToolJobId |> shouldEqual (Some destinationJob)
        evidence.DeferredToolJobOperationId |> shouldEqual (Some operationId)
        evidence.DeferredToolJobStartedAt |> shouldEqual (Some startedAt)
        Assert.That(evidence.DeferredToolJobReceiptAt.IsSome, Is.True)
        destination destinationJob operationId "Destination"
        |> shouldEqual (Ok (Some child))
        (read child).DeferredToolJobReceiptAt
        |> shouldEqual evidence.DeferredToolJobReceiptAt
        destination destinationJob (Guid.NewGuid().ToString("D")) "Destination"
        |> fun result -> result.IsError |> shouldEqual true
        destination (Guid.NewGuid().ToString("N")) operationId "Destination"
        |> fun result -> result.IsError |> shouldEqual true
        store.TryRecordAgentDeferredToolJob(
            user, connection, generation, "destination-correlation",
            agent, "TaskRead", destinationJob, startedAt,
            operationId = operationId, projectName = "Destination").IsError
        |> shouldEqual true
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "destination-correlation").IsError
        |> shouldEqual true
        use restarted = new FeedbackStore(store.DatabasePath)
        let recovered =
            match restarted.ListUnresolvedAgentExecutions(user, agent, 20) with
            | Ok page -> page.Executions |> List.find (fun item -> item.RequestId = child)
            | Error failure -> failwith failure
        recovered.DeferredToolJobOperationId |> shouldEqual (Some operationId)
        restarted.TryAcquireAgentCorrection(user, agent, "operator-with-pending-notification").IsError
        |> shouldEqual true)

[<Test>]
let ``process-running and scheduled commands require more than an ordinary client reply`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, connection, generation =
            "alice", "Worker 3", "client", 5L
        for tool in
            [ "RunCommand"; "RunProjectTask"; "UpdateClient"; "CancelJob"
              "BrowserRunInNewTab"; "ScheduleBrowserExecution"; "GitCommit" ] do
            let witness =
                store.TryBeginAgentOperation(
                    user, agent, tool, Some "Jarvis", "server", None, None)
                |> function Ok value -> value | Error failure -> failwith failure
            let correlation = "not-quiescent-" + tool
            store.TryMarkAgentDispatched(
                user, witness.Id, "device", connection, generation, correlation)
            |> shouldEqual (Ok ())
            // Parent-process exit, cancellation acknowledgement or successful
            // scheduling is NOT durable proof that descendants are quiescent.
            store.TryRecordAgentClientTerminal(
                user, connection, generation, correlation).IsError
            |> shouldEqual true
            store.GetAgentExecutionResolution(user, witness.Id)
            |> shouldEqual (Ok ExecutionUnresolved)
        let read =
            store.TryBeginAgentOperation(
                user, agent, "ReadFiles", Some "Jarvis", "server", None, None)
            |> function Ok value -> value | Error failure -> failwith failure
        store.TryMarkAgentDispatched(
            user, read.Id, "device", connection, generation, "bounded-read")
        |> shouldEqual (Ok ())
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "bounded-read")
        |> shouldEqual (Ok (Some read.Id))
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.CountUnresolvedAgentExecutions(user, agent) |> shouldEqual 7
        restarted.TryAcquireAgentCorrection(
            user, agent, "operator-after-process-reply").IsError
        |> shouldEqual true)

[<Test>]
let ``unknown future command type cannot certify client-terminal liveness`` () =
    withStore (fun store ->
        approveTestEpoch store
        let user, agent, connection, generation = "alice", "Worker 3", "client", 2L
        let beginAndDispatch tool correlation =
            let operation =
                store.TryBeginAgentOperation(
                    user, agent, tool, Some "Jarvis", "server", None, None)
                |> function Ok value -> value | Error error -> failwith error
            store.TryMarkAgentDispatched(
                user, operation.Id, "device", connection, generation, correlation)
            |> shouldEqual (Ok ())
            operation
        let unknown = beginAndDispatch "FutureLongRunningCommand" "future-correlation"
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "future-correlation").IsError
        |> shouldEqual true
        store.GetAgentExecutionResolution(user, unknown.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        // A known ordinary request remains independently terminal;
        // a future possibly-detached command cannot be inferred safe.
        let known = beginAndDispatch "ReadFiles" "known-correlation"
        store.TryRecordAgentClientTerminal(
            user, connection, generation, "known-correlation")
        |> shouldEqual (Ok(Some known.Id))
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.GetAgentExecutionResolution(user, unknown.Id)
        |> shouldEqual (Ok ExecutionUnresolved)
        restarted.TryAcquireAgentCorrection(
            user, agent, "operator-unknown-command").IsError
        |> shouldEqual true)

[<Test>]
let ``legacy drain approval cannot cross a protocol version boundary`` () =
    withStore (fun store ->
        approveTestEpoch store
        match store.GetAgentExecutionReadiness() with
        | Ok (LegacyDrainVerified _) -> ()
        | other -> Assert.Fail(sprintf "Expected current protocol approval: %A" other)
        use connection = new SqliteConnection($"Data Source={store.DatabasePath}")
        connection.Open()
        use downgrade = connection.CreateCommand()
        downgrade.CommandText <-
            "UPDATE agent_execution_epoch SET protocol_version = '3.64' WHERE epoch_id = 1;"
        downgrade.ExecuteNonQuery() |> shouldEqual 1
        use restarted = new FeedbackStore(store.DatabasePath)
        restarted.GetAgentExecutionReadiness()
        |> shouldEqual (Ok LegacyDrainUnverified)
        restarted.TryAcquireAgentCorrection("alice", "Worker 3", "old-epoch-correction").IsError
        |> shouldEqual true
        restarted.TryAuthorizeLegacyDrain(
            "operator", "new-drain-operation",
            "Old protocol approval requires independent drain evidence.",
            fun () -> Ok ()).IsError |> shouldEqual true
        // Older databases may contain Ready evidence predating the newly
        // introduced protocol-version field; absence is never auto-backfilled.
        use missing = connection.CreateCommand()
        missing.CommandText <-
            "UPDATE agent_execution_epoch SET protocol_version = NULL WHERE epoch_id = 1;"
        missing.ExecuteNonQuery() |> shouldEqual 1
        use again = new FeedbackStore(store.DatabasePath)
        again.GetAgentExecutionReadiness() |> shouldEqual (Ok LegacyDrainUnverified)
        again.TryAcquireAgentCorrection("alice", "Worker 3", "missing-version").IsError
        |> shouldEqual true)

[<Test>]
let ``freshly migrated witness table never proves an old protocol request inactive`` () =
    withStore (fun store ->
        // Simulate a deployed 3.64 command dispatched by a pre-witness
        // server. It contributes to the old telemetry, not the new witness.
        let prior = store.BeginOperation(
            "alice", "RunCommand", Some "Jarvis", "server-v3.64",
            Some "old-client", Some "3.64")
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
                          InboxActivation = None; PendingJob = None }
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
        Assert.That(result.IsError, Is.EqualTo(Nullable false),
                    sprintf "Unexpected cross-project result: %A" result.Content)
        // Mainline passes initiating-agent provenance to routing health probes,
        // but only the two actual work commands carry durable witness IDs.
        observedActors |> Seq.toList
        |> shouldEqual [ "Sender"; "Sender"; "Sender"; "Sender" ]
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
        let latencies = ResizeArray<float>()
        for i in 1 .. 100 do
            let started = System.Diagnostics.Stopwatch.GetTimestamp()
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
            latencies.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds)
        stopwatch.Stop()
        Assert.That(store.CountUnresolvedAgentExecutions("alice", "Worker 3"), Is.Zero)
        let ordered = latencies |> Seq.sort |> Seq.toArray
        let p95 = ordered[94]
        TestContext.Out.WriteLine(
            sprintf "100 durable SQLite admissions/routes/terminals: %.1f ms total, %.2f ms/call, p95 %.2f ms"
                stopwatch.Elapsed.TotalMilliseconds
                (stopwatch.Elapsed.TotalMilliseconds / 100.) p95))

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
