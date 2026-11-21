module ServerAgentActivityIngressTests

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Common
open Common.SignalR
open NUnit.Framework
open Server.Services

let private response : AgentCommandResponse =
    { Result = Some "accepted"; Error = None; Instructions = []
      AgentStateChanges = []; InboxActivation = None; PendingJob = None }

type private Received = { CorrelationId: string; Json: string; Actor: string }

let private fixture supportsActivity =
    let user = "auth0|activity-fixture"
    let connection = "server-connection-1"
    let device = "client-device-a"
    let users = UserService()
    users.TransportConnected(user, connection)
    let supported =
        [ "TaskRead"; "LedgerRead" ]
        @ (if supportsActivity then [ AgentActivityProtocol.capability ] else [])
    let session =
        users.Register(user,
            { DeviceId = device; DeviceName = "Fixture"
              ProtocolVersion = AgentProtocol.version; ClientVersion = "test"
              SupportedCommands = supported }, connection)
    let tracker = ClientResponseTracker()
    let received = ConcurrentQueue<Received>()
    let recipient =
        { new IClientService with
            member _.ReceiveMessage(_) = Task.CompletedTask
            member _.ReceiveSourceCommand(_, _, _, _) = Task.CompletedTask
            member _.ReceiveManualGrantedCommand(_, _, _) = Task.CompletedTask
            member _.ReceiveManualGrantApproval(_, _, _) = Task.CompletedTask
            member _.ReceiveCommand(correlation, json, actor) =
                received.Enqueue { CorrelationId = correlation; Json = json; Actor = actor }
                tracker.Complete(correlation, user, connection, session.Generation, response) |> ignore
                Task.CompletedTask
            member _.InvalidateAgentStates(_, _) = Task.CompletedTask
            member _.InvalidateAllAgentStates() = Task.CompletedTask
            member _.RecordAgentActivity(_, _, _) = Task.CompletedTask }
    let clients =
        { new Microsoft.AspNetCore.SignalR.IHubClients<IClientService> with
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
        { new Microsoft.AspNetCore.SignalR.IHubContext<HubService,IClientService> with
            member _.Clients = clients
            member _.Groups = Unchecked.defaultof<_> }
    let http = Microsoft.AspNetCore.Http.HttpContextAccessor()
    http.HttpContext <- Microsoft.AspNetCore.Http.DefaultHttpContext()
    let service =
        ClientService(hub, users, tracker, http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientService>.Instance)
    user, session, received, service

let private taskRead : AgentCommand =
    TaskReadCommand
        { ProjectName = "Jarvis"; AgentId = "Worker 8"; Action = "GetTask"
          TaskId = "fixture"; AssignmentGeneration = 0; CheckpointSequence = 0L
          Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null
          RelativePath = null }

[<Test>]
let unnegotiated_capability_denies_attributed_tool_before_dispatch () =
    task {
        let user, session, received, service = fixture false
        Assert.That(session.EnabledCommands |> List.contains AgentActivityProtocol.capability, Is.False)
        let! denied = service.SendCommandToUser(user, taskRead, agentId = "Worker 8")
        match denied.Error with
        | Some(ExecutionFailed reason) ->
            Assert.That(reason, Does.StartWith("ActivityProofUnavailable:"))
        | other -> Assert.Fail(sprintf "Expected definite activity capability refusal: %A" other)
        Assert.That(received.IsEmpty, Is.True, "No SignalR command may reach an old client.")
        let! operatorCall = service.SendCommandToUser(user, taskRead)
        Assert.That(operatorCall, Is.EqualTo(response),
                    "An unattributed operator request is not agent activity.")
        let mutable plain = Unchecked.defaultof<Received>
        Assert.That(received.TryDequeue(&plain), Is.True)
        Assert.That(JsonSerializer.Deserialize<AgentCommand>(plain.Json), Is.EqualTo(taskRead))
        Assert.That(received.IsEmpty, Is.True)
        let observer : AgentCommand =
            LedgerReadCommand { ProjectName = "Jarvis"; Action = "Health"; OperationId = ""
                                Cursor = ""; PageSize = 0 }
        let! diagnostic = service.SendCommandToUser(user, observer)
        Assert.That(diagnostic, Is.EqualTo(response))
        let mutable value = Unchecked.defaultof<Received>
        Assert.That(received.TryDequeue(&value), Is.True)
        Assert.That(JsonSerializer.Deserialize<AgentCommand>(value.Json), Is.EqualTo(observer),
                    "Observer commands remain unenveloped and do not publish activity.")
    }

[<Test>]
let server_builds_fresh_utc_device_bound_authenticated_event () =
    task {
        let user, session, received, service = fixture true
        Assert.That(session.EnabledCommands |> List.contains AgentActivityProtocol.capability, Is.True)
        let before = DateTimeOffset.UtcNow
        let! result = service.SendCommandToUser(user, taskRead, agentId = "Worker 8")
        let after = DateTimeOffset.UtcNow
        Assert.That(result, Is.EqualTo(response))
        let mutable value = Unchecked.defaultof<Received>
        Assert.That(received.TryDequeue(&value), Is.True)
        Assert.That(value.Actor, Is.EqualTo("Worker 8"))
        Assert.That(received.IsEmpty, Is.True)
        let envelope = JsonSerializer.Deserialize<AgentActivityCommandEnvelope>(value.Json)
        Assert.That(envelope.SchemaVersion, Is.EqualTo(AgentActivityProtocol.schemaVersion))
        Assert.That(JsonSerializer.Deserialize<AgentCommand>(envelope.CommandJson), Is.EqualTo(taskRead))
        let activity = envelope.Activity
        Assert.That(activity.AgentId, Is.EqualTo("Worker 8"))
        Assert.That(activity.ProjectName, Is.EqualTo("Jarvis"))
        Assert.That(activity.DeviceId, Is.EqualTo("client-device-a"))
        Assert.That(activity.ConnectionId, Is.EqualTo("server-connection-1"))
        Assert.That(activity.ConnectionGeneration, Is.EqualTo(session.Generation))
        Assert.That(activity.CorrelationId, Is.EqualTo(value.CorrelationId))
        Assert.That(activity.AccountScope,
                    Is.EqualTo(AgentActivityProtocol.accountScopeFromSubject user |> Option.get))
        Assert.That(activity.CommandName, Is.EqualTo("TaskRead"))
        Assert.That(activity.ObservedAtUtc.Offset, Is.EqualTo(TimeSpan.Zero))
        Assert.That(activity.ObservedAtUtc, Is.InRange(before, after))
        let mutable parsed = Guid.Empty
        Assert.That(Guid.TryParseExact(activity.EventId, "D", &parsed), Is.True)
        Assert.That(parsed, Is.Not.EqualTo(Guid.Empty))
    }
