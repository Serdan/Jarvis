module Server.Tests.TaskOverviewRouteTests

open System.Collections.Concurrent
open System.Security.Claims
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open NUnit.Framework
open Common
open Common.SignalR
open Server
open Server.Services

[<TestFixture>]
type TaskOverviewRouteTests() =
    [<Test>]
    member _.bounded_task_overview_route_preserves_filters_and_authorization() =
        task {
            let userId = "task-overview-operator"
            let users = UserService()
            let tracker = ClientResponseTracker()
            let captured = ConcurrentQueue<AgentCommand>()
            let dispatchedConnections = ConcurrentQueue<string>()
            let response =
                { Result = Some """{"ok":true}"""
                  Error = None
                  Instructions = []
                  StateChangeEvidence = StateChangeEvidence.Confirmed []
                  InboxActivation = None
                  PendingJob = None }

            let register deviceId connectionId clientVersion supportedCommands =
                users.TransportConnected(userId, connectionId)
                users.Register(
                    userId,
                    { DeviceId = deviceId
                      DeviceName = deviceId
                      ProtocolVersion = AgentProtocol.version
                      ClientVersion = clientVersion
                      SupportedCommands = supportedCommands },
                    connectionId)

            let supportedCommands =
                AgentProtocol.commandDefinitions
                |> List.map (fun definition -> definition.Name)

            let registration = register "matched" "matched-connection" "matched-client" supportedCommands
            Assert.That(registration.EnabledCommands, Does.Contain("ReviewRead"))

            let recipient connectionId =
                { new IClientService with
                    member _.ReceiveMessage(_) = Task.CompletedTask
                    member _.ReceiveCommand(correlationId, commandJson, _) =
                        let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
                        captured.Enqueue(command)
                        dispatchedConnections.Enqueue(connectionId)
                        match users.GetSessionByConnection(connectionId) with
                        | ValueSome session ->
                            tracker.Complete(
                                correlationId,
                                userId,
                                connectionId,
                                session.Generation,
                                response)
                            |> ignore
                        | ValueNone -> ()
                        Task.CompletedTask
                    member _.ReceiveSourceCommand(_, _, _, _) = Task.CompletedTask
                    member _.ReceiveManualGrantedCommand(_, _, _) = Task.CompletedTask
                    member _.ReceiveManualGrantApproval(_, _, _) = Task.CompletedTask
                    member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                    member _.InvalidateJobWake(_) = Task.CompletedTask
                    member _.InvalidateAllAgentStates() = Task.CompletedTask
                    member _.RecordAgentActivity(_, _, _) = Task.CompletedTask }

            let fallback = recipient ""
            let clients =
                { new IHubClients<IClientService> with
                    member _.All = fallback
                    member _.AllExcept(_) = fallback
                    member _.Client(connectionId) = recipient connectionId
                    member _.Clients(_) = fallback
                    member _.Group(_) = fallback
                    member _.GroupExcept(_, _) = fallback
                    member _.Groups(_) = fallback
                    member _.User(_) = fallback
                    member _.Users(_) = fallback }

            let hub =
                { new IHubContext<HubService, IClientService> with
                    member _.Clients = clients
                    member _.Groups = Unchecked.defaultof<_> }

            let feedbackStore = OptionalFeedbackStore("")
            let services = ServiceCollection()
            services.AddLogging() |> ignore
            services.AddSingleton<UserService>(users) |> ignore
            services.AddSingleton<OptionalFeedbackStore>(feedbackStore) |> ignore
            services.AddSingleton<IOptions<JarvisOptions>>(
                Options.Create(
                    { Auth0Domain = "auth.example.test"
                      Audience = "https://jarvis.example.test"
                      OpenAIAppsChallenge = ""
                      EnableOperatorConversations = false })) |> ignore
            use provider = services.BuildServiceProvider()

            let context = DefaultHttpContext()
            context.RequestServices <- provider
            context.User <-
                ClaimsPrincipal(
                    ClaimsIdentity(
                        [ Claim("sub", userId)
                          Claim("scope", "workspace:read") ],
                        "task-overview-test"))

            let http = HttpContextAccessor()
            http.HttpContext <- context

            let client =
                ClientService(
                    hub,
                    users,
                    tracker,
                    http,
                    NullLogger<ClientService>.Instance,
                    feedbackStore)

            let invoke scope lifecycle phase assignee reviewStatus sinceSequence pageSize cursor =
                JarvisMcpTools.TaskOverview(
                    "Jarvis",
                    "Worker 6",
                    scope,
                    lifecycle,
                    phase,
                    assignee,
                    reviewStatus,
                    sinceSequence,
                    pageSize,
                    cursor,
                    "",
                    client,
                    http)

            let isError (result: ModelContextProtocol.Protocol.CallToolResult) =
                result.IsError.GetValueOrDefault(false)

            let contentText (result: ModelContextProtocol.Protocol.CallToolResult) =
                result.Content
                |> Seq.choose (fun block ->
                    let property = block.GetType().GetProperty("Text")
                    if isNull property then None
                    else
                        match property.GetValue(block) with
                        | :? string as value -> Some value
                        | _ -> None)
                |> String.concat "\n"

            Assert.That(users.SetDefaultDevice(userId, "matched"), Is.True)
            let authorized = context.User
            context.User <- ClaimsPrincipal(ClaimsIdentity([ Claim("sub", userId) ], "task-overview-test"))
            let! denied = invoke "pertinent" "" "" "" "" -1L 0 ""
            Assert.That(isError denied, Is.True)
            Assert.That(contentText denied, Does.Contain("workspace:read"))
            Assert.That(captured.IsEmpty, Is.True)
            Assert.That(dispatchedConnections.IsEmpty, Is.True)
            context.User <- authorized

            let! defaultResult =
                invoke "pertinent" "" "" "" "" -1L 0 ""

            Assert.That(isError defaultResult, Is.False)

            match captured.TryDequeue() with
            | true, ReviewReadCommand command ->
                Assert.That(command.Action, Is.EqualTo("ListProjectTasks"))
                match command.TaskOverview with
                | Some query ->
                    Assert.That(query.Scope, Is.EqualTo("pertinent"))
                    Assert.That(query.PageSize, Is.EqualTo(0))
                    Assert.That(query.Cursor, Is.EqualTo(""))
                | None -> Assert.Fail("The bounded TaskOverview action omitted its request.")
            | _ -> Assert.Fail("The public TaskOverview tool did not send ReviewReadCommand.")

            match dispatchedConnections.TryDequeue() with
            | true, connectionId -> Assert.That(connectionId, Is.EqualTo("matched-connection"))
            | _ -> Assert.Fail("The selected matched device did not receive the bounded action.")

            let! historyResult =
                invoke "history" "Open" "Blocked" "Worker 6" "Pending" 42L 1 "cursor-token"

            Assert.That(isError historyResult, Is.False)

            match captured.TryDequeue() with
            | true, ReviewReadCommand command ->
                Assert.That(command.Action, Is.EqualTo("ListProjectTasks"))
                match command.TaskOverview with
                | Some query ->
                    Assert.That(query.Scope, Is.EqualTo("history"))
                    Assert.That(query.Lifecycle, Is.EqualTo("Open"))
                    Assert.That(query.Phase, Is.EqualTo("Blocked"))
                    Assert.That(query.Assignee, Is.EqualTo("Worker 6"))
                    Assert.That(query.ReviewStatus, Is.EqualTo("Pending"))
                    Assert.That(query.SinceSequence, Is.EqualTo(42L))
                    Assert.That(query.PageSize, Is.EqualTo(1))
                    Assert.That(query.Cursor, Is.EqualTo("cursor-token"))
                | None -> Assert.Fail("The history TaskOverview action omitted its request.")
            | _ -> Assert.Fail("The history TaskOverview call did not send ReviewReadCommand.")

            match dispatchedConnections.TryDequeue() with
            | true, connectionId -> Assert.That(connectionId, Is.EqualTo("matched-connection"))
            | _ -> Assert.Fail("The history request was not routed to the selected matched device.")

            // The matched-version bounded request stays under the normal
            // ReviewRead command and authorization boundary.
            Assert.That(AgentProtocol.commandDefinitions |> List.exists (fun d -> d.Name = "ReviewRead"), Is.True)
            Assert.That(captured.IsEmpty, Is.True)
            Assert.That(dispatchedConnections.IsEmpty, Is.True)
        }
