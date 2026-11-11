module ServerServiceTests

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Common
open Common.SignalR
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open NUnit.Framework
open FsUnitTyped
open Server
open Server.Services

[<Test>]
let invalidation_routes_only_to_connected_devices_of_authenticated_user () =
    let users = UserService()
    let register userId device connection =
        let registration =
            { DeviceId = device; DeviceName = device; ProtocolVersion = AgentProtocol.version
              ClientVersion = "test"; SupportedCommands = ["TaskWrite"; "TaskRead"] }
        users.TransportConnected(userId, connection)
        users.Register(userId, registration, connection) |> ignore
    register "alice" "desktop" "a-desktop"
    register "alice" "laptop" "a-laptop"
    register "bob" "desktop" "b-desktop"
    let initial = AgentStateInvalidationRouting.recipients (users.ListDevices("alice"))
    Assert.That(initial |> Set.ofArray,
                Is.EqualTo(Set.ofList [ "a-desktop"; "a-laptop" ] :> obj))
    users.Disconnect("a-laptop", Some "offline")
    let remaining = AgentStateInvalidationRouting.recipients (users.ListDevices("alice"))
    Assert.That(remaining, Is.EqualTo([| "a-desktop" |] :> obj))
    Assert.That(AgentStateInvalidationRouting.recipients (users.ListDevices("bob")),
                Is.EqualTo([| "b-desktop" |] :> obj))

[<Test>]
let invalidation_routes_only_exact_changed_agents_even_on_command_failure () =
    // The server never guesses state changes from the command type or success.
    let failedRead : AgentCommandResponse =
        { Result = None
          Error = Some(NotFound "A source file was missing after activation")
          Instructions = []
          AgentStateChanges =
              [ { ProjectName = "Wayfold"; AgentId = "Owner" }
                { ProjectName = "Wayfold"; AgentId = "Owner" }
                { ProjectName = "Jarvis"; AgentId = "Reviewer" } ]
          InboxActivation = None; PendingJob = None }
    let groups = AgentStateInvalidationRouting.affectedByProject failedRead.AgentStateChanges
    Assert.That(groups.Length, Is.EqualTo(2))
    Assert.That(groups |> List.find (fun (project, _) -> project = "Wayfold") |> snd,
                Is.EqualTo([| "Owner" |] :> obj))
    Assert.That(groups |> List.find (fun (project, _) -> project = "Jarvis") |> snd,
                Is.EqualTo([| "Reviewer" |] :> obj))
    let checkpoint : AgentCommandResponse =
        { failedRead with Result = Some "{}"; Error = None; AgentStateChanges = [] }
    Assert.That(AgentStateInvalidationRouting.affectedByProject checkpoint.AgentStateChanges, Is.Empty)

[<Test>]
let user_service_exposes_authenticated_transport_before_registration () =
    let users = UserService()
    users.TransportConnected("auth0|user", "connection")

    match users.GetTransportConnection("connection") with
    | ValueNone -> Assert.Fail("Expected transport connection.")
    | ValueSome connection ->
        connection.ConnectionId |> shouldEqual "connection"
        connection.UserId |> shouldEqual "auth0|user"
        connection.RegisteredDeviceId |> shouldEqual None

[<Test>]
let user_service_increments_generation_and_preserves_replacement_on_stale_disconnect () =
    let users = UserService()
    let userId = "auth0|user"
    let registration =
        { DeviceId = "device-1"
          DeviceName = "Workstation"
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.2.3"
          SupportedCommands = AgentProtocol.commandDefinitions |> List.map _.Name }

    users.TransportConnected(userId, "old-connection")
    let first = users.Register(userId, registration, "old-connection")
    users.TransportConnected(userId, "new-connection")
    let second = users.Register(userId, registration, "new-connection")

    first.Generation |> shouldEqual 1L
    second.Generation |> shouldEqual 2L
    second.RegistrationReason.Contains("Reconnect", StringComparison.Ordinal) |> shouldEqual true

    users.Disconnect("old-connection", Some "stale disconnect")

    match users.GetSession(userId) with
    | ValueNone -> Assert.Fail("Expected retained session.")
    | ValueSome session ->
        session.State |> shouldEqual Registered
        session.ConnectionId |> shouldEqual (Some "new-connection")
        session.Generation |> shouldEqual 2L
        session.UserId |> shouldEqual userId
        session.DeviceId |> shouldEqual registration.DeviceId
        session.DeviceName |> shouldEqual registration.DeviceName
        session.ProtocolVersion |> shouldEqual AgentProtocol.version
        session.ClientVersion |> shouldEqual "1.2.3"
        users.GetConnectionId(userId) |> shouldEqual (ValueSome "new-connection")

[<Test>]
let user_service_switches_active_device_for_same_user () =
    let users = UserService()
    let userId = "auth0|user"
    let firstRegistration =
        { DeviceId = "device-1"
          DeviceName = "Desktop"
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0.0"
          SupportedCommands = AgentProtocol.commandDefinitions |> List.map _.Name }
    let secondRegistration = { firstRegistration with DeviceId = "device-2"; DeviceName = "Laptop" }

    users.TransportConnected(userId, "desktop-connection")
    let firstGeneration = users.Register(userId, firstRegistration, "desktop-connection").Generation
    users.TransportConnected(userId, "laptop-connection")
    let switched = users.Register(userId, secondRegistration, "laptop-connection")

    (switched.Generation > firstGeneration) |> shouldEqual true
    switched.RegistrationReason.Contains("Initial registration", StringComparison.Ordinal) |> shouldEqual true

    match users.GetSession(userId) with
    | ValueNone -> Assert.Fail("Expected active device.")
    | ValueSome session ->
        session.DeviceId |> shouldEqual "device-1"
        session.DeviceName |> shouldEqual "Desktop"
        session.ConnectionId |> shouldEqual (Some "desktop-connection")

    users.ListDevices(userId).Length |> shouldEqual 2
    users.SetDefaultDevice(userId, "device-2") |> shouldEqual true
    match users.GetSession(userId) with
    | ValueSome session -> session.DeviceId |> shouldEqual "device-2"
    | ValueNone -> Assert.Fail("Selected default device was not retained.")

[<Test>]
let ``multiple device sessions remain independent across reconnect and disconnect`` () =
    let users = UserService()
    let userId = "alice"
    let makeRegistration id name =
        { DeviceId = id
          DeviceName = name
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0.0"
          SupportedCommands = AgentProtocol.commandDefinitions |> List.map _.Name }

    users.TransportConnected(userId, "home-1")
    let firstHome = users.Register(userId, makeRegistration "home" "Home", "home-1").Generation
    users.TransportConnected(userId, "work-1")
    users.Register(userId, makeRegistration "work" "Work", "work-1") |> ignore

    users.GetDefaultDeviceId(userId) |> shouldEqual (Some "home")
    users.GetSessionByConnection("home-1") |> function
        | ValueSome session -> session.DeviceId |> shouldEqual "home"
        | _ -> Assert.Fail("Home connection not registered")
    users.GetSessionByConnection("work-1") |> function
        | ValueSome session -> session.DeviceId |> shouldEqual "work"
        | _ -> Assert.Fail("Work connection not registered")

    users.SetDefaultDevice(userId, "work") |> shouldEqual true
    users.TransportConnected(userId, "home-2")
    let reconnected = users.Register(userId, makeRegistration "home" "Home", "home-2")
    (reconnected.Generation > firstHome) |> shouldEqual true
    users.GetDefaultDeviceId(userId) |> shouldEqual (Some "work")
    users.GetSessionByConnection("home-1") |> shouldEqual ValueNone
    users.Disconnect("home-1", None)
    users.GetSessionByConnection("home-2") |> function
        | ValueSome session -> session.State |> shouldEqual Registered
        | _ -> Assert.Fail("New home connection was lost after stale disconnect")

    users.Disconnect("work-1", Some "network lost")
    match users.GetSession(userId) with
    | ValueSome session ->
        session.DeviceId |> shouldEqual "work"
        session.State |> shouldEqual Disconnected
    | ValueNone -> Assert.Fail("Default device disappeared")
    users.GetConnectionId(userId) |> shouldEqual ValueNone
    users.SetDefaultDevice(userId, "home") |> shouldEqual true
    users.GetConnectionId(userId) |> shouldEqual (ValueSome "home-2")

[<Test>]
let ``re-registering a different device on one transport disconnects previous device`` () =
    let users = UserService()
    let registration device =
        { DeviceId = device
          DeviceName = device
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0"
          SupportedCommands = [ "ListProjects" ] }
    users.TransportConnected("alice", "connection")
    users.Register("alice", registration "old", "connection") |> ignore
    users.Register("alice", registration "new", "connection") |> ignore
    match users.GetSessionByDevice("alice", "old") with
    | ValueSome session ->
        session.State |> shouldEqual Disconnected
        session.ConnectionId |> shouldEqual None
    | ValueNone -> Assert.Fail("Previous registration missing")
    match users.GetSessionByConnection("connection") with
    | ValueSome session -> session.DeviceId |> shouldEqual "new"
    | ValueNone -> Assert.Fail("New device not registered")

[<Test>]
let ``device selection is scoped to owning user`` () =
    let users = UserService()
    let registration =
        { DeviceId = "shared-device-id"
          DeviceName = "Desktop"
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0.0"
          SupportedCommands = [ "ListProjects" ] }
    users.TransportConnected("alice", "alice-connection")
    users.Register("alice", registration, "alice-connection") |> ignore
    users.ListDevices("bob").Length |> shouldEqual 0
    users.SetDefaultDevice("bob", "shared-device-id") |> shouldEqual false
    users.GetSessionByDevice("bob", "shared-device-id") |> shouldEqual ValueNone

[<Test>]
let user_service_retains_disconnected_session_and_failure_reason () =
    let users = UserService()
    let userId = "auth0|user"
    let registration =
        { DeviceId = "device-1"
          DeviceName = "Workstation"
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0.0"
          SupportedCommands = AgentProtocol.commandDefinitions |> List.map _.Name }

    users.TransportConnected(userId, "connection")
    users.Register(userId, registration, "connection") |> ignore
    users.RecordDispatchFailure(userId, "connection", "dispatch failed")

    match users.GetSession(userId) with
    | ValueNone -> Assert.Fail("Expected session.")
    | ValueSome session -> session.LastFailure |> shouldEqual (Some "dispatch failed")

    users.Disconnect("connection", Some "network lost")

    match users.GetSession(userId) with
    | ValueNone -> Assert.Fail("Expected retained disconnected session.")
    | ValueSome session ->
        session.State |> shouldEqual Disconnected
        session.ConnectionId |> shouldEqual None
        session.DisconnectedAt.IsSome |> shouldEqual true
        session.LastFailure |> shouldEqual (Some "network lost")
        users.GetConnectionId(userId) |> shouldEqual ValueNone

[<Test>]
let user_service_records_protocol_mismatch_in_registration_reason () =
    let users = UserService()
    let userId = "auth0|user"
    users.TransportConnected(userId, "connection")

    let result =
        users.Register(
            userId,
            { DeviceId = "device-1"
              DeviceName = "Workstation"
              ProtocolVersion = "older"
              ClientVersion = "1.0.0"
              SupportedCommands = [ "ListProjects" ] },
            "connection"
        )

    result.RegistrationReason.Contains("protocol mismatch", StringComparison.Ordinal) |> shouldEqual true

[<Test>]
let ``registration negotiates only commands known to both client and server`` () =
    let users = UserService()
    let userId = "auth0|user"
    users.TransportConnected(userId, "connection")

    let result =
        users.Register(
            userId,
            { DeviceId = "device-1"
              DeviceName = "Workstation"
              ProtocolVersion = AgentProtocol.version
              ClientVersion = "1.0.0"
              SupportedCommands = [ "WriteFile"; "FutureCommand"; "ListProjects" ] },
            "connection"
        )

    result.ServerProtocolVersion |> shouldEqual AgentProtocol.version
    result.EnabledCommands |> shouldEqual [ "ListProjects"; "WriteFile" ]

    match users.GetSession(userId) with
    | ValueNone -> Assert.Fail("Expected registered session.")
    | ValueSome session ->
        session.SupportedCommands |> shouldEqual result.EnabledCommands
        session.RegistrationReason.Contains("negotiated 2/", StringComparison.Ordinal) |> shouldEqual true

[<Test>]
let ``client service rejects commands not negotiated by the registered client`` () =
    task {
        let users = UserService()
        let userId = "auth0|user"
        users.TransportConnected(userId, "connection")
        users.Register(
            userId,
            { DeviceId = "device-1"
              DeviceName = "Workstation"
              ProtocolVersion = "3.9"
              ClientVersion = "1.0.0"
              SupportedCommands = [ "ListProjects" ] },
            "connection"
        )
        |> ignore

        let client = ClientService(Unchecked.defaultof<_>, users, ClientResponseTracker(),
                                   Microsoft.AspNetCore.Http.HttpContextAccessor())
        let command =
            WriteFileCommand
                { ProjectName = "Jarvis"
                  FilePath = "notes.txt"
                  Content = "content"
                  FileWriteMode = Write
                  ExpectedHash = None
                  CreateParents = None }

        let! response = client.SendCommandToUser(userId, command)

        match response.Error with
        | Some(ExecutionFailed message) ->
            message.Contains("WriteFile", StringComparison.Ordinal) |> shouldEqual true
            message.Contains("client 1.0.0", StringComparison.OrdinalIgnoreCase) |> shouldEqual true
            message.Contains("protocol 3.9", StringComparison.Ordinal) |> shouldEqual true
            message.Contains($"server uses protocol {AgentProtocol.version}", StringComparison.Ordinal) |> shouldEqual true
            message.Contains("reconnect", StringComparison.OrdinalIgnoreCase) |> shouldEqual true
        | other -> Assert.Fail($"Expected unsupported-command diagnostic, got {other}")
    }

[<Test>]
let ``registration without capability metadata negotiates no commands`` () =
    let users = UserService()
    users.TransportConnected("auth0|user", "connection")

    let result =
        users.Register(
            "auth0|user",
            { DeviceId = "legacy-device"
              DeviceName = "Legacy"
              ProtocolVersion = "3.10"
              ClientVersion = "0.9.0"
              SupportedCommands = Unchecked.defaultof<string list> },
            "connection"
        )

    result.EnabledCommands |> shouldEqual []

[<Test>]
let ``response tracker completes registered response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register("alice", "conn-1", 1L, TimeSpan.FromSeconds 5L)
        let expected =
            { Result = Some "ok"
              Error = None
              Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }

        tracker.Complete(correlationId, "alice", "conn-1", 1L, expected) |> shouldEqual true
        let! actual = pending

        actual |> shouldEqual expected
    }

[<Test>]
let ``response tracker rejects other users connections and generations`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register("alice", "connection-a", 3L, TimeSpan.FromSeconds 5L)
        let response = { Result = Some "accepted"; Error = None; Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }

        tracker.Complete(correlationId, "bob", "connection-a", 3L, response) |> shouldEqual false
        tracker.Complete(correlationId, "alice", "connection-b", 3L, response) |> shouldEqual false
        tracker.Complete(correlationId, "alice", "connection-a", 2L, response) |> shouldEqual false
        pending.IsCompleted |> shouldEqual false

        tracker.Complete(correlationId, "alice", "connection-a", 3L, response) |> shouldEqual true
        let! actual = pending
        actual |> shouldEqual response
        tracker.Complete(correlationId, "alice", "connection-a", 3L, response) |> shouldEqual false
    }

[<Test>]
let ``response tracker cancellation removes pending response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register("alice", "conn-1", 1L, TimeSpan.FromSeconds 5L)

        tracker.Cancel(correlationId)

        try
            let! _ = pending
            Assert.Fail("Expected pending response to be canceled.")
        with :? TaskCanceledException ->
            ()
    }

[<Test>]
let ``response tracker honors configured timeout`` () =
    task {
        let tracker = ClientResponseTracker()
        let _, pending = tracker.Register("alice", "conn-1", 1L, TimeSpan.FromMilliseconds 25L)

        try
            let! _ = pending
            Assert.Fail("Expected pending response to time out.")
        with :? TaskCanceledException ->
            ()
    }

[<Test>]
let ``all Jarvis MCP tools expose ChatGPT-compatible discovery metadata`` () =
    let methods =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute as attribute |] -> Some(methodInfo, attribute)
            | _ -> None)

    methods.Length > 0 |> shouldEqual true

    for methodInfo, attribute in methods do
        String.IsNullOrWhiteSpace(attribute.Title) |> shouldEqual false
        methodInfo.ReturnType.IsGenericType |> shouldEqual true
        methodInfo.ReturnType.GetGenericTypeDefinition() |> shouldEqual typedefof<Task<_>>
        methodInfo.ReturnType.GetGenericArguments()[0] |> shouldEqual typeof<CallToolResult>
        if methodInfo.Name = "GetProfile" then
            attribute.UseStructuredContent |> shouldEqual true
            attribute.OutputSchemaType |> shouldEqual typeof<ProfileResult>
        else
            attribute.UseStructuredContent |> shouldEqual false
            isNull attribute.OutputSchemaType |> shouldEqual true

[<Test>]
let ``command catalog matches AgentCommand union and MCP tools`` () =
    let catalogNames =
        AgentProtocol.commandDefinitions
        |> List.map (fun definition -> definition.Name)
        |> Set.ofList

    let unionNames =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<AgentCommand>)
        |> Array.map (fun unionCase ->
            if unionCase.Name.EndsWith("Command", StringComparison.Ordinal) then
                unionCase.Name.Substring(0, unionCase.Name.Length - "Command".Length)
            else
                unionCase.Name)
        |> Set.ofArray

    let mcpNames =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute |] -> Some methodInfo.Name
            | _ -> None)
        |> Set.ofArray

    let serverLocalNames =
        set [ "GetProfile"; "Feedback"; "ListFeedback"; "GetFeedback"; "UpdateFeedback"; "GetFeedbackSummary"; "GetConnectionDiagnostics"; "ListDevices"; "SelectDevice"; "UpdateServer"; "GetServerUpdateStatus" ]

    // Retired task commands must not be exposed by either the wire protocol or MCP.
    // The orphan Git branch is the sole supported orchestration API.
    let retired =
        set [ "AttachWorkTask"; "GetWorkTask"; "ListWorkTasks"; "CheckpointWorkTask"; "CompleteWorkTask"
              "ListTasks"; "UpdateTask"; "ClaimTask"; "CheckpointTask"
              "CompleteTask"; "GetTaskHistory"; "CompareWorktreeTasks"; "GetAgentAssignment"
              "AcknowledgeTask"; "GetOrchestrationSnapshot"; "RetireWorktree"; "BeginTask" ]
    // Orphan-ledger MCP tools route through eight versioned internal wire commands.
    let ledgerWire = set [ "TaskRead"; "TaskWrite"; "ReviewRead"; "ReviewWrite"; "LedgerRead"; "LedgerWrite" ]
    let orchestrationTools =
        set [ "GetIntegrationSubmissions"; "SubmitIntegration"; "CompleteIntegration"; "NotifyAgent"; "AcknowledgeNotification"; "AgentInbox"; "GetAssignment"; "GetCheckpoint"; "InspectAttemptSource"; "TaskLifecycleHistory"; "GetTaskSpecPage"; "DiffTaskSpec"; "CreateTask"; "ReviseTaskSpec"; "RegisterAgent"; "AssignTask"; "ReissueAssignment"; "CheckpointAttempt"
              "SetAttemptSource"; "YieldAgent"; "CompleteAgentWork"
              "SetAgentState"; "ReissueQueuedAttempt"; "RecoverAgentAssignment"; "AcknowledgeReviewFeedback"; "DeferAttempt"; "ResumeAttempt"; "AbandonAttempt"; "CancelTask"; "TransferOwner"; "CloseTask"; "ReopenTask"; "ImportTaskSpec"; "ExportTaskSpec"
              "GetTask"; "TaskOverview"; "ReviewDetails"; "ReviewerInbox"; "ListReviewDocuments"; "ReadReviewDocument"
              "SubmitReview"; "ClaimReview"; "DecideReview"
              "ReleaseReview"; "WithdrawReview"; "PutReviewDocument"
              "LedgerHealth"; "GetOperationStatus"; "PendingOperations"; "ReconcilePendingOperations"
              "PendingOperationSummary"; "PendingOperationsPage"; "InspectPendingOperation"
              "AcknowledgePendingOperation"; "InitializeLedger"; "BindExistingLedger"; "RebindLedgerRemote"
              ]
    // Retired wire cases are deleted; public CreateTask/AssignTask are canonical ledger tools.
    unionNames |> shouldEqual catalogNames
    Set.intersect mcpNames retired |> shouldEqual Set.empty
    Assert.That(mcpNames, Does.Contain("GetTask"))
    Assert.That(mcpNames, Does.Contain("ListConfiguredTasks"))
    Assert.That(mcpNames, Does.Not.Contain("ReviewTask"))
    Assert.That(mcpNames, Does.Not.Contain("ListProjectTasks"))
    Assert.That(mcpNames, Does.Not.Contain("MigrateOrthogonalAgentState"))
    Set.intersect catalogNames retired |> shouldEqual Set.empty
    Set.intersect mcpNames orchestrationTools |> shouldEqual orchestrationTools
    Set.difference (Set.difference mcpNames serverLocalNames) orchestrationTools
    |> shouldEqual (Set.difference catalogNames ledgerWire)
    Set.difference (Set.difference mcpNames catalogNames) orchestrationTools
    |> shouldEqual serverLocalNames


[<Test>]
let ``public orchestration tool metadata describes canonical branch reviews`` () =
    let reviewMethods =
        set [ "GetTask"; "ReviewDetails"; "ReviewerInbox"; "SubmitReview"
              "ClaimReview"; "DecideReview"; "ReleaseReview"; "WithdrawReview"
              "TaskOverview"; "ListReviewDocuments"; "ReadReviewDocument"
              "PutReviewDocument" ]
    for methodInfo in typeof<JarvisMcpTools>.GetMethods() do
        let toolAttributes =
            methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false)
        if toolAttributes.Length = 1 then
            let tool = toolAttributes[0] :?> McpServerToolAttribute
            let descriptions =
                methodInfo.GetCustomAttributes(typeof<System.ComponentModel.DescriptionAttribute>, false)
            Assert.That(descriptions.Length, Is.EqualTo(1), methodInfo.Name)
            let description =
                (descriptions[0] :?> System.ComponentModel.DescriptionAttribute).Description
            Assert.That(tool.Title, Is.Not.Empty, methodInfo.Name)
            Assert.That(description, Is.Not.Empty, methodInfo.Name)
            if reviewMethods.Contains methodInfo.Name then
                Assert.That(description, Does.Not.Contain("review revision"), methodInfo.Name)
                Assert.That(description, Does.Not.Contain("client opt-in"), methodInfo.Name)
    let reviewDescription methodName =
        let methodInfo = typeof<JarvisMcpTools>.GetMethod(methodName)
        (methodInfo.GetCustomAttributes(typeof<System.ComponentModel.DescriptionAttribute>, false)[0]
         :?> System.ComponentModel.DescriptionAttribute).Description
    Assert.That(reviewDescription "ReviewDetails", Does.Contain("independent"))
    Assert.That(reviewDescription "ClaimReview", Does.Contain("Git branch"))
    Assert.That(reviewDescription "DecideReview", Does.Contain("Markdown"))
    Assert.That(reviewDescription "SubmitReview", Does.Contain("No assignment"))

[<Test>]
let ``public SubmitReview accepts arbitrary commit and optional context without worker phase`` () =
    let parameters =
        typeof<JarvisMcpTools>.GetMethod("SubmitReview").GetParameters()
    let names = parameters |> Array.map _.Name
    for oldField in [ "expectedAttemptVersion"; "assignmentGeneration"; "reviewKind"; "ownerId" ] do
        Assert.That(names, Does.Not.Contain(oldField))
    for contextField in [ "taskId"; "sourceRef"; "targetRef"; "purpose"; "summary" ] do
        let field = parameters |> Array.find (fun p -> p.Name = contextField)
        Assert.That(field.IsOptional, Is.True, contextField)
    let close = typeof<JarvisMcpTools>.GetMethod("CloseTask")
    Assert.That(close, Is.Not.Null)
    Assert.That(typeof<JarvisMcpTools>.GetMethod("CloseNonSourceTask"), Is.Null)
    Assert.That(typeof<JarvisMcpTools>.GetMethod("ReleaseReview"), Is.Not.Null)

[<Test>]
let ``task assignment and owner transfer do not accept reviewer IDs`` () =
    for methodName in [ "AssignTask"; "TransferOwner" ] do
        let methodInfo = typeof<JarvisMcpTools>.GetMethod(methodName)
        Assert.That(methodInfo, Is.Not.Null, methodName)
        let parameterNames = methodInfo.GetParameters() |> Array.map _.Name
        Assert.That(parameterNames, Does.Contain("operationId"), methodName)
        Assert.That(parameterNames, Does.Not.Contain("reviewerId"), methodName)
    let claim = typeof<JarvisMcpTools>.GetMethod("ClaimReview")
    Assert.That(claim.GetParameters() |> Array.map _.Name, Does.Contain("agentId"))

[<Test>]
let ``client-backed MCP tools expose an optional per-command deviceId`` () =
    let methods = typeof<JarvisMcpTools>.GetMethods()
    let clientMethods =
        methods
        |> Array.filter (fun methodInfo ->
            methodInfo.GetParameters()
            |> Array.exists (fun parameter -> parameter.ParameterType = typeof<ClientService>))
    // Three additional read-only pending recovery views are public aliases
    // of LedgerRead; retry/supersede have distinct wire definitions.
    clientMethods.Length |> shouldEqual (AgentProtocol.commandDefinitions.Length - 1 + 51)
    for methodInfo in clientMethods do
        let deviceParam = methodInfo.GetParameters() |> Array.tryFind (fun parameter -> parameter.Name = "deviceId")
        match deviceParam with
        | None -> Assert.Fail($"{methodInfo.Name} is missing the deviceId override.")
        | Some parameter ->
            parameter.ParameterType |> shouldEqual typeof<string>
            parameter.IsOptional |> shouldEqual true

[<Test>]
let ``development MCP tools accept an optional registered worktreeName`` () =
    let scopedTools =
        [ "GetProjectDetails"; "ListDirectory"; "SearchFiles"; "SearchText"
          "ReadFile"; "ReadFiles"; "ReadImage"; "ExportFile"; "ImportFile"
          "WriteFile"; "PatchFile"; "RunCommand"; "ListConfiguredTasks"
          "RunProjectTask"; "GetGitStatus"; "GetGitDiff"; "GitCommit"
          "StartJob"; "ListJobs" ]
    for name in scopedTools do
        let methodInfo = typeof<JarvisMcpTools>.GetMethod(name)
        Assert.That(methodInfo, Is.Not.Null, name)
        let worktreeParam =
            methodInfo.GetParameters() |> Array.find (fun p -> p.Name = "worktreeName")
        Assert.That(worktreeParam.ParameterType, Is.EqualTo(typeof<string>), name)
        Assert.That(worktreeParam.IsOptional, Is.True, name)
        Assert.That(worktreeParam.DefaultValue, Is.EqualTo(""), name)
    for logicalProjectTool in [ "AgentInbox"; "TaskOverview"; "SubmitReview"; "NotifyAgent" ] do
        let names =
            typeof<JarvisMcpTools>.GetMethod(logicalProjectTool).GetParameters()
            |> Array.map _.Name
        Assert.That(names, Does.Not.Contain("worktreeName"), logicalProjectTool)

[<Test>]
let ``worktreeName must be a registered-worktree selector name rather than path`` () =
    McpToolHelpers.projectWithWorktreeName "Wayfold" "" |> shouldEqual "Wayfold"
    McpToolHelpers.projectWithWorktreeName "Wayfold" "agent6-example"
    |> shouldEqual "Wayfold::agent6-example"
    // Existing callers using the selector form continue to work when the new field is omitted.
    McpToolHelpers.projectWithWorktreeName "Wayfold::agent6-example" ""
    |> shouldEqual "Wayfold::agent6-example"
    for projectName, worktreeName in
        [ "Wayfold::already-selected", "another"; "Wayfold", "../escape"
          "Wayfold", "linked/path"; "Wayfold", ".."; "Wayfold", "invalid" + string (char 92) + "name"; "Wayfold", "with\nnewline"; "Wayfold", " " ] do
        try
            McpToolHelpers.projectWithWorktreeName projectName worktreeName |> ignore
            Assert.Fail("Expected invalid worktreeName to be rejected")
        with :? ArgumentException -> ()

[<Test>]
let ``WriteFile rejects invalid file write mode with actionable validation error`` () =
    let result =
        JarvisMcpTools.WriteFile(
            "Project1",
            "Test Agent",
            "",
            "test.txt",
            "content",
            "Create",
            "",
            Nullable(),
            "",
            Unchecked.defaultof<ClientService>,
            Unchecked.defaultof<Microsoft.AspNetCore.Http.IHttpContextAccessor>)
        |> Async.AwaitTask
        |> Async.RunSynchronously

    result.IsError |> shouldEqual (Nullable true)
    let text = result.Content[0] :?> TextContentBlock
    text.Text |> shouldEqual "ValidationFailed: fileWriteMode must be one of: Append, Write. Received: 'Create'."

[<Test>]
let ``ImportFile advertises the ChatGPT file parameter`` () =
    let methodInfo =
        typeof<JarvisMcpTools>.GetMethod("ImportFile")

    isNull methodInfo |> shouldEqual false

    let metadata =
        methodInfo.GetCustomAttributes(typeof<McpMetaAttribute>, false)
        |> Array.choose (function
            | :? McpMetaAttribute as value -> Some value
            | _ -> None)
        |> Array.find (fun value -> value.Name = "openai/fileParams")

    use document = JsonDocument.Parse(metadata.JsonValue)
    document.RootElement.GetArrayLength() |> shouldEqual 1
    document.RootElement[0].GetString() |> shouldEqual "file"

    let fileParameter =
        methodInfo.GetParameters()
        |> Array.find (fun parameter -> parameter.Name = "file")

    fileParameter.ParameterType |> shouldEqual typeof<OpenAIFileParam>

[<Test>]
let ``server ListCommands catalog matches public MCP tools`` () =
    let mcpNames =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute |] -> Some methodInfo.Name
            | _ -> None)
        |> Set.ofArray

    let listedNames =
        McpToolHelpers.mcpListCommandsResult.Commands
        |> List.map (fun capability -> capability.Name)
        |> Set.ofList

    listedNames |> shouldEqual mcpNames

[<Test>]
let ``MCP bridge returns successful JSON as text without a mismatched output schema`` () =
    let response =
        { Result = Some """{"value":42,"items":[1,2]}"""
          Error = None
          Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }

    let result = McpToolHelpers.toCallToolResult response

    result.IsError |> shouldEqual (Nullable false)
    result.StructuredContent.HasValue |> shouldEqual false

    let text = result.Content[0] :?> TextContentBlock
    text.Text |> shouldEqual """{"value":42,"items":[1,2]}"""

[<Test>]
let mcp_self_inbox_exposes_committed_activation_on_success_and_read_error () =
    let receipt : AgentInboxActivation =
        { ProjectName = "Jarvis"; AgentId = "Owner"
          ActivatedByThisCall = true
          BeforeState = Some "Ready"; AfterState = Some "Working"
          CommittedHead = Some(String('a', 40))
          OperationId = Some(Guid.NewGuid().ToString("D")) }
    for readFailed in [ false; true ] do
        let response : AgentCommandResponse =
            { Result = if readFailed then None else Some """{"Agent":{"State":"Working"}}"""
              Error = if readFailed then Some(NotFound "Inbox read failed after activation") else None
              Instructions = []
              AgentStateChanges = [ { ProjectName = "Jarvis"; AgentId = "Owner" } ]
              InboxActivation = Some receipt; PendingJob = None }
        let roundtrip =
            JsonSerializer.Deserialize<AgentCommandResponse>(JsonSerializer.Serialize(response))
        Assert.That(roundtrip.InboxActivation.IsSome, Is.True)
        Assert.That(roundtrip.InboxActivation.Value.CommittedHead,
                    Is.EqualTo(receipt.CommittedHead))
        let tool =
            response |> McpToolHelpers.toCallToolResult
            |> McpToolHelpers.attachStateEffects response
        Assert.That(tool.IsError, Is.EqualTo(Nullable readFailed))
        Assert.That(tool.Content.Count, Is.EqualTo(2))
        let exposed = (tool.Content[1] :?> TextContentBlock).Text
        use document = JsonDocument.Parse(exposed)
        let changes = document.RootElement.GetProperty("agentStateChanges").EnumerateArray() |> Seq.toList
        Assert.That(changes.Length, Is.EqualTo(1))
        Assert.That(changes.Head.GetProperty("AgentId").GetString(), Is.EqualTo("Owner"))
        Assert.That(tool.Meta["jarvis/agent_state_changes"].ToJsonString(),
                    Does.Contain("Owner"))
        let activation = document.RootElement.GetProperty("agentInboxActivation")
        Assert.That(activation.GetProperty("ActivatedByThisCall").GetBoolean(), Is.True)
        Assert.That(activation.GetProperty("BeforeState").GetString(), Is.EqualTo("Ready"))
        Assert.That(activation.GetProperty("AfterState").GetString(), Is.EqualTo("Working"))
        Assert.That(activation.GetProperty("CommittedHead").GetString(),
                    Is.EqualTo(String('a', 40)))
        Assert.That(tool.Meta["jarvis/inbox_activation"].ToJsonString(),
                    Does.Contain("ActivatedByThisCall"))
        if readFailed then
            Assert.That((tool.Content[0] :?> TextContentBlock).Text,
                        Does.Contain("Inbox read failed after activation"))
    let noChange : AgentInboxActivation =
        { ProjectName = receipt.ProjectName
          AgentId = receipt.AgentId
          ActivatedByThisCall = false
          BeforeState = None; AfterState = None
          CommittedHead = None; OperationId = None }
    let repeated =
        { Result = Some """{"Agent":{"State":"Working"}}"""
          Error = None; Instructions = []; AgentStateChanges = []
          InboxActivation = Some noChange; PendingJob = None }
    let repeated =
        repeated |> McpToolHelpers.toCallToolResult
        |> McpToolHelpers.attachStateEffects repeated
    use repeatDoc = JsonDocument.Parse((repeated.Content[1] :?> TextContentBlock).Text)
    let repeatActivation = repeatDoc.RootElement.GetProperty("agentInboxActivation")
    Assert.That(repeatActivation.GetProperty("ActivatedByThisCall").GetBoolean(), Is.False)
    Assert.That(repeatActivation.GetProperty("CommittedHead").ValueKind,
                Is.EqualTo(JsonValueKind.Null))
    Assert.That(repeatDoc.RootElement.GetProperty("agentStateChanges").GetArrayLength(), Is.EqualTo(0))
    // Older 3.62 clients omit InboxActivation. A newer server continues to
    // accept those wire envelopes, without claiming an activation occurred.
    let legacy = JsonSerializer.Deserialize<AgentCommandResponse>(
        """{"Result":"{}","Error":null,"Instructions":[],"AgentStateChanges":[]}""")
    Assert.That(legacy.InboxActivation.IsNone, Is.True)

[<Test>]
let mcp_project_tool_responses_always_expose_canonical_state_change_evidence () =
    for failed in [ false; true ] do
        let response : AgentCommandResponse =
            { Result = if failed then None else Some """{"result":"ok"}"""
              Error = if failed then Some(NotFound "Source file absent") else None
              Instructions = []
              AgentStateChanges = []
              InboxActivation = None; PendingJob = None }
        let tool =
            response |> McpToolHelpers.toCallToolResult
            |> McpToolHelpers.attachStateEffects response
        Assert.That(tool.IsError, Is.EqualTo(Nullable failed))
        Assert.That(tool.Content.Count, Is.EqualTo(2))
        use doc = JsonDocument.Parse((tool.Content[1] :?> TextContentBlock).Text)
        Assert.That(doc.RootElement.GetProperty("agentStateChanges").GetArrayLength(),
                    Is.EqualTo(0))
        let mutable ignored = Unchecked.defaultof<JsonElement>
        Assert.That(doc.RootElement.TryGetProperty("agentInboxActivation", &ignored), Is.False)
        Assert.That(tool.Meta["jarvis/agent_state_changes"].ToJsonString(), Is.EqualTo("[]"))

    // A command can affect a different agent, and an earlier committed write
    // must remain visible even if its subsequent operation fails.
    let affected : AgentCommandResponse =
        { Result = None
          Error = Some(NotFound "Read failed after prior mutation")
          Instructions = []
          AgentStateChanges = [ { ProjectName = "Wayfold"; AgentId = "Worker 1" } ]
          InboxActivation = None; PendingJob = None }
    let result =
        affected |> McpToolHelpers.toCallToolResult
        |> McpToolHelpers.attachStateEffects affected
    Assert.That(result.IsError, Is.EqualTo(Nullable true))
    use effects = JsonDocument.Parse((result.Content[1] :?> TextContentBlock).Text)
    let changes = effects.RootElement.GetProperty("agentStateChanges")
    Assert.That(changes.GetArrayLength(), Is.EqualTo(1))
    Assert.That(changes[0].GetProperty("ProjectName").GetString(), Is.EqualTo("Wayfold"))
    Assert.That(changes[0].GetProperty("AgentId").GetString(), Is.EqualTo("Worker 1"))
    let mutable absent = Unchecked.defaultof<JsonElement>
    Assert.That(effects.RootElement.TryGetProperty("agentInboxActivation", &absent), Is.False)

[<Test>]
let cross_project_notify_merges_preflight_and_destination_state_changes () =
    let sourceActivation : AgentInboxActivation =
        { ProjectName = "Wayfold"; AgentId = "Worker 1"
          ActivatedByThisCall = true
          BeforeState = Some "Ready"; AfterState = Some "Working"
          CommittedHead = Some(String('a', 40)); OperationId = Some(Guid.NewGuid().ToString("D")) }
    let preflight : AgentCommandResponse =
        { Result = Some "{}"; Error = None; Instructions = []
          AgentStateChanges = [ { ProjectName = "Wayfold"; AgentId = "Worker 1" } ]
          InboxActivation = Some sourceActivation; PendingJob = None }
    let destination : AgentCommandResponse =
        { Result = None; Error = Some(NotFound "Destination write failed")
          Instructions = []
          AgentStateChanges =
            [ { ProjectName = "Jarvis"; AgentId = "Reviewer 1" }
              { ProjectName = "Wayfold"; AgentId = "Worker 1" } ]
          InboxActivation = None; PendingJob = None }
    let merged = McpToolHelpers.mergeStateEffects preflight destination
    Assert.That(merged.AgentStateChanges.Length, Is.EqualTo(2))
    Assert.That(merged.InboxActivation.Value.ActivatedByThisCall, Is.True)
    Assert.That(merged.Error.IsSome, Is.True)
    let tool =
        merged |> McpToolHelpers.toCallToolResult
        |> McpToolHelpers.attachStateEffects merged
    use json = JsonDocument.Parse((tool.Content[1] :?> TextContentBlock).Text)
    Assert.That(json.RootElement.GetProperty("agentStateChanges").GetArrayLength(), Is.EqualTo(2))
    Assert.That(json.RootElement.GetProperty("agentInboxActivation").GetProperty("ActivatedByThisCall").GetBoolean(), Is.True)

[<Test>]
let ``MCP bridge preserves typed confirmation error details`` () =
    let confirmation =
        { CommandName = "RunCommand"
          ProjectName = Some "Project1"
          Permissions = [ ProcessExecution ]
          Summary = "Run dotnet"
          Paths = []
          Executable = Some "dotnet"
          Args = [ "test" ]
          EstimatedImpact = "Run dotnet"
          SupportsDryRun = false }

    let response =
        { Result = None
          Error = Some(ConfirmationRequired confirmation)
          Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }

    let result = McpToolHelpers.toCallToolResult response

    result.IsError |> shouldEqual (Nullable true)
    result.StructuredContent.HasValue |> shouldEqual false

    let text = result.Content[0] :?> TextContentBlock
    text.Text.Contains("ConfirmationRequired", StringComparison.Ordinal) |> shouldEqual true

[<Test>]
let ``MCP bridge preserves stable AgentError kinds`` () =
    let cases =
        [ NotFound "missing", "NotFound", "missing"
          PermissionDenied "denied", "PermissionDenied", "denied"
          ValidationFailed "invalid", "ValidationFailed", "invalid"
          Conflict "conflict", "Conflict", "conflict"
          ExecutionFailed "failed", "ExecutionFailed", "failed"
          OutputTruncated "truncated", "OutputTruncated", "truncated" ]

    for error, expectedKind, expectedMessage in cases do
        let result =
            { Result = None
              Error = Some error
              Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
            |> McpToolHelpers.toCallToolResult

        result.IsError |> shouldEqual (Nullable true)
        result.StructuredContent.HasValue |> shouldEqual false
        let text = result.Content[0] :?> TextContentBlock
        text.Text |> shouldEqual $"{expectedKind}: {expectedMessage}"

[<Test>]
let mcp_image_content_serializes_data_as_base64 () =
    let bytes = [| 0uy; 1uy; 2uy; 127uy; 128uy; 255uy |]
    let result = CallToolResult()
    result.Content <-
        ResizeArray<ContentBlock>(
            [ ImageContentBlock.FromBytes(bytes, "image/png") :> ContentBlock ]
        )

    let json = JsonSerializer.Serialize(result, global.ModelContextProtocol.McpJsonUtilities.DefaultOptions)
    use document = JsonDocument.Parse(json)
    let image = document.RootElement.GetProperty("content")[0]

    image.GetProperty("type").GetString() |> shouldEqual "image"
    image.GetProperty("mimeType").GetString() |> shouldEqual "image/png"
    image.GetProperty("data").GetString() |> shouldEqual (Convert.ToBase64String bytes)


[<Test>]
let auth_issuer_normalizes_auth0_domain () =
    let options =
        { Auth0Domain = "dev-kn4j3jz3qv2cvw05.eu.auth0.com"
          Audience = "https://jarvis2.kehlet.dev"
          OpenAIAppsChallenge = "" }

    Auth.issuer options |> shouldEqual "https://dev-kn4j3jz3qv2cvw05.eu.auth0.com/"

[<Test>]
let auth_accepts_exactly_the_legacy_and_trailing_slash_api_audiences () =
    let options =
        { Auth0Domain = "example.eu.auth0.com"
          Audience = "https://jarvis2.kehlet.dev"
          OpenAIAppsChallenge = "" }
    Auth.validAudiences options
    |> shouldEqual [| "https://jarvis2.kehlet.dev"; "https://jarvis2.kehlet.dev/" |]

    Auth.validAudiences { options with Audience = "https://jarvis2.kehlet.dev/" }
    |> shouldEqual (Auth.validAudiences options)

[<Test>]
let auth_scope_lookup_handles_space_separated_scope_claim () =
    let identity =
        System.Security.Claims.ClaimsIdentity(
            [ System.Security.Claims.Claim("sub", "auth0|user")
              System.Security.Claims.Claim("scope", "workspace:read process:execute") ],
            "test"
        )

    let principal = System.Security.Claims.ClaimsPrincipal(identity)

    Auth.tryUserId principal |> shouldEqual (Some "auth0|user")
    Auth.hasScope Auth.WorkspaceRead principal |> shouldEqual true
    Auth.hasScope Auth.ProcessExecute principal |> shouldEqual true
    Auth.hasScope Auth.GitWrite principal |> shouldEqual false


[<Test>]
let mcp_tools_never_expose_session_key_parameters () =
    let methods =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute |] -> Some methodInfo
            | _ -> None)

    for methodInfo in methods do
        methodInfo.GetParameters()
        |> Array.exists (fun parameter -> String.Equals(parameter.Name, "key", StringComparison.OrdinalIgnoreCase))
        |> shouldEqual false


[<Test>]
let mcp_tool_oauth_metadata_matches_command_permissions () =
    let expectedScope (definition: CommandDefinition) =
        if definition.Permissions |> List.contains VersionControlWrite then Auth.GitWrite
        elif definition.Permissions |> List.contains WorkspaceWrite then Auth.WorkspaceWrite
        elif definition.Permissions |> List.contains ProcessExecution then Auth.ProcessExecute
        else Auth.WorkspaceRead

    let wireCommandNames = AgentProtocol.commandDefinitions |> List.map _.Name |> Set.ofList

    let definitions =
        let original =
            AgentProtocol.commandDefinitions
            |> List.map (fun definition -> definition.Name, definition)
            |> Map.ofList
        let ledgerRead = original["LedgerRead"]
        let ledgerWrite = original["LedgerWrite"]
        let taskRead = original["TaskRead"]
        let taskWrite = original["TaskWrite"]
        let ledgerRead = original["ReviewRead"]
        let ledgerWrite = original["ReviewWrite"]
        let aliases =
            [ for name in [ "LedgerHealth"; "GetOperationStatus"; "PendingOperations"; "ReconcilePendingOperations";
                            "PendingOperationSummary"; "PendingOperationsPage"; "InspectPendingOperation" ] -> name, ledgerRead
              for name in [ "InitializeLedger"; "BindExistingLedger"; "RebindLedgerRemote"; "AcknowledgePendingOperation" ] -> name, ledgerWrite
              for name in [ "GetIntegrationSubmissions"; "AgentInbox"; "GetAssignment"; "GetCheckpoint"; "InspectAttemptSource"; "TaskLifecycleHistory"; "GetTaskSpecPage"; "DiffTaskSpec" ] ->
                  name, (if name = "AgentInbox" then
                             { taskRead with MutatesState = true }
                         else taskRead)
              for name in [ "SubmitIntegration"; "CompleteIntegration"; "NotifyAgent"; "AcknowledgeNotification"; "CreateTask"; "ReviseTaskSpec"; "RegisterAgent"; "AssignTask"; "ReissueAssignment"; "CheckpointAttempt"
                            "SetAttemptSource"; "YieldAgent"; "CompleteAgentWork";
                            "SetAgentState"; "ReissueQueuedAttempt"; "RecoverAgentAssignment"; "AcknowledgeReviewFeedback"; "DeferAttempt"; "ResumeAttempt"; "AbandonAttempt"; "CancelTask"; "TransferOwner"; "CloseTask"; "ReopenTask"; "ImportTaskSpec"; "ExportTaskSpec" ] -> name, taskWrite
              for name in [ "GetTask"; "TaskOverview"; "ReviewDetails"; "ReviewerInbox"; "ListReviewDocuments"; "ReadReviewDocument" ] ->
                  name, ledgerRead
              for name in [ "SubmitReview"; "ClaimReview"; "DecideReview"
                            "ReleaseReview"; "WithdrawReview"; "PutReviewDocument" ] ->
                  name, ledgerWrite ]
        aliases |> List.fold (fun map (key, value) -> Map.add key value map) original

    let methods =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute as attribute |] -> Some(methodInfo, attribute)
            | _ -> None)

    for methodInfo, attribute in methods |> Array.filter (fun (methodInfo, _) -> methodInfo.Name <> "GetProfile") do
        let definition = definitions |> Map.tryFind methodInfo.Name
        let scope =
            match definition with
            | Some definition -> expectedScope definition
            | None when methodInfo.Name = "UpdateServer" -> Auth.ProcessExecute
            | None -> Auth.WorkspaceRead

        let readOnly =
            match definition with
            | Some definition -> not definition.MutatesState
            | None ->
                match methodInfo.Name with
                | "Feedback" | "UpdateFeedback" | "SelectDevice" | "UpdateServer" -> false
                | "ListFeedback" | "GetFeedback" | "GetFeedbackSummary" | "GetConnectionDiagnostics" | "ListDevices" | "GetServerUpdateStatus" -> true
                | name -> failwith $"Unexpected server-local MCP tool: {name}"

        attribute.ReadOnly |> shouldEqual readOnly
        attribute.Destructive |> shouldEqual false
        attribute.OpenWorld |> shouldEqual false

        let securityMeta =
            methodInfo.GetCustomAttributes(typeof<McpMetaAttribute>, false)
            |> Array.choose (function
                | :? McpMetaAttribute as metadata -> Some metadata
                | _ -> None)
            |> Array.find (fun metadata -> metadata.Name = "securitySchemes")

        use document = JsonDocument.Parse(securityMeta.JsonValue)
        let scheme = document.RootElement[0]
        scheme.GetProperty("type").GetString() |> shouldEqual "oauth2"
        let scopes = scheme.GetProperty("scopes")
        scopes[0].GetString() |> shouldEqual scope


[<Test>]
let observe_agent_is_explicitly_read_only_while_self_inbox_is_activity () =
    let byName name =
        McpToolHelpers.mcpListCommandsResult.Commands
        |> List.find (fun item -> item.Name = name)
    let observation = byName "ObserveAgent"
    let inbox = byName "AgentInbox"
    Assert.That(observation.MutatesState, Is.False)
    Assert.That(inbox.MutatesState, Is.True)
    Assert.That(observation.Permissions, Does.Contain(ReadOnly))
    let tool name =
        let methodInfo = typeof<JarvisMcpTools>.GetMethod(name)
        let attributes =
            methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false)
        attributes[0] :?> McpServerToolAttribute
    Assert.That((tool "ObserveAgent").ReadOnly, Is.True)
    Assert.That((tool "AgentInbox").ReadOnly, Is.False)
    let parameters = typeof<JarvisMcpTools>.GetMethod("ObserveAgent").GetParameters()
    Assert.That(parameters |> Array.map _.Name |> Array.take 3,
                Is.EqualTo([| "projectName"; "agentId"; "targetAgentId" |] :> obj))

[<Test>]
let mcp_profile_tool_uses_authenticated_subject () =
    let context = Microsoft.AspNetCore.Http.DefaultHttpContext()
    let identity =
        System.Security.Claims.ClaimsIdentity(
            [ System.Security.Claims.Claim("sub", "auth0|profile-user") ],
            "test"
        )
    context.User <- System.Security.Claims.ClaimsPrincipal(identity)

    let accessor = Microsoft.AspNetCore.Http.HttpContextAccessor()
    accessor.HttpContext <- context

    let result =
        McpToolHelpers.getProfile accessor
        |> Async.AwaitTask
        |> Async.RunSynchronously

    result.IsError |> shouldEqual (Nullable false)
    result.StructuredContent.HasValue |> shouldEqual true
    result.StructuredContent.Value.GetProperty("id").GetString()
    |> shouldEqual (Auth.profileId "auth0|profile-user")

    let methodInfo = typeof<JarvisMcpTools>.GetMethod("GetProfile")
    let attribute =
        methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false)
        |> Array.choose (function
            | :? McpServerToolAttribute as item -> Some item
            | _ -> None)
        |> Array.exactlyOne

    attribute.ReadOnly |> shouldEqual true
    attribute.Destructive |> shouldEqual false
    attribute.OpenWorld |> shouldEqual false
    attribute.OutputSchemaType |> shouldEqual typeof<ProfileResult>

    let metadata =
        methodInfo.GetCustomAttributes(typeof<McpMetaAttribute>, false)
        |> Array.choose (function
            | :? McpMetaAttribute as item -> Some(item.Name, item.JsonValue)
            | _ -> None)
        |> Map.ofArray

    use profileMarker = JsonDocument.Parse(metadata["openai/profile"])
    profileMarker.RootElement.GetBoolean() |> shouldEqual true

    use security = JsonDocument.Parse(metadata["securitySchemes"])
    security.RootElement[0].GetProperty("type").GetString() |> shouldEqual "oauth2"
    security.RootElement[0].GetProperty("scopes").GetArrayLength() |> shouldEqual 0


[<Test>]
let mcp_profile_tool_returns_chatgpt_compatible_authentication_challenge () =
    let services =
        Microsoft.Extensions.DependencyInjection.ServiceCollection()
        :> Microsoft.Extensions.DependencyInjection.IServiceCollection

    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.Extensions.Options.IOptions<JarvisOptions>>(
        services,
        Microsoft.Extensions.Options.Options.Create(
            { Auth0Domain = "dev-kn4j3jz3qv2cvw05.eu.auth0.com"
              Audience = "https://jarvis2.kehlet.dev"
              OpenAIAppsChallenge = "" }
        )
    )
    |> ignore

    use provider =
        Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services)
    let context = Microsoft.AspNetCore.Http.DefaultHttpContext()
    context.RequestServices <- provider

    let accessor = Microsoft.AspNetCore.Http.HttpContextAccessor()
    accessor.HttpContext <- context

    let result =
        McpToolHelpers.getProfile accessor
        |> Async.AwaitTask
        |> Async.RunSynchronously

    result.IsError |> shouldEqual (Nullable true)

    let challenges = result.Meta["mcp/www_authenticate"] :?> JsonArray
    challenges.Count |> shouldEqual 1

    let challenge = challenges[0].GetValue<string>()
    challenge.Contains("resource_metadata=\"https://jarvis2.kehlet.dev/.well-known/oauth-protected-resource\"")
    |> shouldEqual true
    challenge.Contains("error=\"invalid_token\"") |> shouldEqual true
    challenge.Contains("error_description=\"Authentication required.\"") |> shouldEqual true

[<Test>]
let checkpoint_attempt_source_and_evidence_are_optional_in_public_mcp_signature () =
    let methodInfo = typeof<JarvisMcpTools>.GetMethod("CheckpointAttempt")
    Assert.That(methodInfo, Is.Not.Null)
    let parameters = methodInfo.GetParameters()
    for name in [ "sourceCommit"; "evidence" ] do
        let parameter =
            parameters |> Array.find (fun parameter -> parameter.Name = name)
        Assert.That(parameter.IsOptional, Is.True, name)
        Assert.That(parameter.HasDefaultValue, Is.True, name)
        Assert.That(parameter.DefaultValue, Is.EqualTo(""), name)
    for name in [ "projectName"; "taskId"; "agentId"; "summary"; "nextAction" ] do
        let parameter =
            parameters |> Array.find (fun parameter -> parameter.Name = name)
        Assert.That(parameter.IsOptional, Is.False, name)

[<Test>]
let run_command_reason_parameter_is_optional_for_mcp_compatibility () =
    let methodInfo = typeof<JarvisMcpTools>.GetMethod("RunCommand")
    let reason =
        methodInfo.GetParameters()
        |> Array.find (fun parameter -> parameter.Name = "reason")

    reason.IsOptional |> shouldEqual true
    reason.HasDefaultValue |> shouldEqual true
    reason.DefaultValue |> shouldEqual ""

[<Test>]
let mcp_guidance_blocks_precede_result_without_changing_success () =
    let instructions =
        [ { Scope = "workspace"; Path = "AGENTS.md"; Sha256 = "sha256:" + String('b', 64)
            Content = "Workspace guidance" }
          { Scope = "project"; Path = "Demo/AGENTS.md"; Sha256 = "sha256:" + String('c', 64)
            Content = "Project guidance" }
          { Scope = "project"; Path = "Demo/src/AGENTS.md"; Sha256 = "sha256:" + String('a', 64)
            Content = "Source guidance" } ]
    let response =
        { Result = Some """{"result":"ok"}"""
          Error = None
          Instructions = instructions; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
    let result = response |> McpToolHelpers.toCallToolResult |> McpToolHelpers.attachInstructions instructions
    Assert.That(result.IsError, Is.EqualTo(Nullable false))
    Assert.That(result.Content.Count, Is.EqualTo(2))
    let guidance = result.Content[0] :?> TextContentBlock
    use json = JsonDocument.Parse(guidance.Text)
    let map = json.RootElement.GetProperty("AGENTS.md")
    Assert.That(json.RootElement.EnumerateObject() |> Seq.length, Is.EqualTo(1))
    Assert.That(map.GetProperty(".").GetString(), Is.EqualTo("Workspace guidance"))
    Assert.That(map.GetProperty("Demo").GetString(), Is.EqualTo("Project guidance"))
    Assert.That(map.GetProperty("Demo/src").GetString(), Is.EqualTo("Source guidance"))
    Assert.That(guidance.Text, Does.Not.Contain("This is workspace content"))
    let operation = result.Content[1] :?> TextContentBlock
    Assert.That(operation.Text, Is.EqualTo("""{"result":"ok"}"""))

[<Test>]
let mcp_agency_write_warning_precedes_success_without_marking_error () =
    let notes =
        [ { Scope = "write-warning"; Path = "Demo/AGENTS.md"; Sha256 = ""
            Content = "WARNING: Demo/AGENTS.md is unusually large. The write succeeded." } ]
    let response = { Result = Some """{"ok":true}"""; Error = None; Instructions = notes; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
    let shown = response |> McpToolHelpers.toCallToolResult |> McpToolHelpers.attachInstructions notes
    Assert.That(shown.IsError, Is.EqualTo(Nullable false))
    Assert.That(shown.Content.Count, Is.EqualTo(2))
    Assert.That((shown.Content[0] :?> TextContentBlock).Text, Does.StartWith("Jarvis instruction notice:"))
    Assert.That((shown.Content[1] :?> TextContentBlock).Text, Is.EqualTo("""{"ok":true}"""))

[<Test>]
let mcp_guidance_preserves_errors_and_non_text_blocks () =
    let instructions =
        [ { Scope = "workspace"
            Path = "AGENTS.md"
            Sha256 = "sha256:" + String('b', 64)
            Content = "Workspace guidance" } ]
    let failure =
        { Result = None
          Error = Some(ValidationFailed "Unrelated error")
          Instructions = instructions; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
    let error = failure |> McpToolHelpers.toCallToolResult |> McpToolHelpers.attachInstructions instructions
    Assert.That(error.IsError, Is.EqualTo(Nullable true))
    Assert.That((error.Content[1] :?> TextContentBlock).Text, Is.EqualTo("ValidationFailed: Unrelated error"))
    let image = CallToolResult()
    image.Content <- ResizeArray<ContentBlock>([ ImageContentBlock.FromBytes([| 137uy; 80uy; 78uy; 71uy |], "image/png") :> ContentBlock ])
    let augmented = McpToolHelpers.attachInstructions instructions image
    Assert.That(augmented.Content.Count, Is.EqualTo(2))
    Assert.That(augmented.Content[1], Is.InstanceOf<ImageContentBlock>())
    Assert.That(augmented.Content[0], Is.InstanceOf<TextContentBlock>())

[<Test>]
let mcp_server_instructions_are_static_and_concise () =
    Assert.That(InstructionGuidance.Text, Does.Contain("AGENTS.md"))
    Assert.That(InstructionGuidance.Text.Length, Is.LessThan(512))

[<Test>]
let response_json_carries_typed_hierarchical_instructions () =
    let response =
        { Result = Some """{"ok":true}"""
          Error = None
          Instructions = [ { Scope = "project"; Path = "Demo/AGENTS.md"; Sha256 = "sha256:" + String('f', 64); Content = "# Guidance" } ]
          AgentStateChanges = [ { ProjectName = "Demo"; AgentId = "Owner" } ]
          InboxActivation = None; PendingJob = None }
    let json = JsonSerializer.Serialize(response)
    use document = JsonDocument.Parse(json)
    let root = document.RootElement
    Assert.That(root.GetProperty("Result").GetString(), Is.EqualTo("""{"ok":true}"""))
    let instruction = root.GetProperty("Instructions").EnumerateArray() |> Seq.head
    Assert.That(instruction.GetProperty("Path").GetString(), Is.EqualTo("Demo/AGENTS.md"))
    Assert.That(instruction.GetProperty("Scope").GetString(), Is.EqualTo("project"))
    Assert.That(instruction.GetProperty("Content").GetString(), Is.EqualTo("# Guidance"))
    let mutation = root.GetProperty("AgentStateChanges").EnumerateArray() |> Seq.exactlyOne
    Assert.That(mutation.GetProperty("ProjectName").GetString(), Is.EqualTo("Demo"))
    Assert.That(mutation.GetProperty("AgentId").GetString(), Is.EqualTo("Owner"))
    let restored = JsonSerializer.Deserialize<AgentCommandResponse>(json)
    Assert.That(restored.AgentStateChanges, Is.EqualTo(response.AgentStateChanges :> obj))


[<Test>]
let ``MCP bridge preserves structured  ledger failure category`` () =
    let problem : Common.LedgerFailureInfo =
        { Code = "OutcomeUnknown"
          Message = "Remote push response lost"
          OperationId = Some(Guid.NewGuid().ToString("D"))
          VerifiedLedgerHead = None }
    let response =
        { Result = None
          Error = Some(Common.AgentError.LedgerFailure problem)
          Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
    let tool = McpToolHelpers.toCallToolResult response
    tool.IsError |> shouldEqual (Nullable true)
    let content = tool.Content[0] :?> TextContentBlock
    content.Text.Contains("LedgerFailure(OutcomeUnknown)", StringComparison.Ordinal)
    |> shouldEqual true

[<Test>]
let ``typed  ledger failures survive response JSON roundtrip`` () =
    let operationId = Guid.NewGuid().ToString("D")
    let source : Common.AgentCommandResponse =
        { Result = None
          Error =
              Some(Common.AgentError.LedgerFailure
                  { Code = "OutcomeUnknown"; Message = "Unconfirmed push"
                    OperationId = Some operationId; VerifiedLedgerHead = None })
          Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
    let payload = JsonSerializer.Serialize(source)
    let destination = JsonSerializer.Deserialize<Common.AgentCommandResponse>(payload)
    match destination.Error with
    | Some(Common.AgentError.LedgerFailure issue) ->
        Assert.That(issue.Code, Is.EqualTo("OutcomeUnknown"))
        Assert.That(issue.OperationId, Is.EqualTo(Some operationId))
    | other -> Assert.Fail(sprintf "Lost typed  failure during transport: %A" other)


[<Test>]
let cross_project_routing_prefers_selected_client_then_eligible_alternatives () =
    let users = UserService()
    let user = "agent-owner"
    let registration id commands =
        { DeviceId = id
          DeviceName = id
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0"
          SupportedCommands = commands }
    let all = [ "LedgerRead"; "TaskRead"; "TaskWrite" ]
    users.TransportConnected(user, "sender-connection")
    users.Register(user, registration "sender" all, "sender-connection") |> ignore
    users.TransportConnected(user, "destination-connection")
    users.Register(user, registration "destination" all, "destination-connection") |> ignore
    let selected = users.GetSessionByDevice(user, "sender")
    let candidates = ProjectNotificationRouting.candidates selected
                         (users.ListDevices(user) |> Array.toList) false "TaskWrite" false
    (candidates |> List.map (fun device -> device.DeviceId))
    |> shouldEqual [ "sender"; "destination" ]
    let pinned = ProjectNotificationRouting.candidates selected
                     (users.ListDevices(user) |> Array.toList) true "TaskWrite" false
    (pinned |> List.map (fun device -> device.DeviceId)) |> shouldEqual [ "sender" ]
    users.Disconnect("sender-connection", Some "Disconnected")
    let fallback = ProjectNotificationRouting.candidates (users.GetSessionByDevice(user, "sender"))
                       (users.ListDevices(user) |> Array.toList) false "TaskWrite" false
    (fallback |> List.map (fun device -> device.DeviceId)) |> shouldEqual [ "destination" ]
    let oldVersion = { (users.GetSessionByDevice(user, "destination") |> function ValueSome session -> session | _ -> failwith "Missing device")
                       with ProtocolVersion = "3.47" }
    let crossEligible = ProjectNotificationRouting.candidates ValueNone
                            [ oldVersion ] false "TaskWrite" true
    crossEligible |> shouldEqual []
    let ordinaryEligible = ProjectNotificationRouting.candidates ValueNone
                               [ oldVersion ] false "TaskWrite" false
    ordinaryEligible.Length |> shouldEqual 1
    let unrelated = ProjectNotificationRouting.candidates ValueNone
                        (users.ListDevices("different-user") |> Array.toList) false "TaskWrite" false
    unrelated |> shouldEqual []

[<Test>]
let cross_project_failure_reports_real_device_incompatibility () =
    let users = UserService()
    let user = "routing-diagnostics"
    let register name protocol commands =
        users.TransportConnected(user, name + "-connection")
        users.Register(user,
            { DeviceId = name; DeviceName = name; ProtocolVersion = protocol
              ClientVersion = "1"; SupportedCommands = commands },
            name + "-connection") |> ignore
    register "old" "3.48" [ "LedgerRead"; "TaskWrite" ]
    register "no-write" AgentProtocol.version [ "LedgerRead"; "TaskRead" ]
    register "no-ledger" AgentProtocol.version [ "TaskWrite" ]
    register "healthy" AgentProtocol.version [ "LedgerRead"; "TaskWrite" ]
    let selected = users.GetSessionByDevice(user, "old")
    let ordered = ProjectNotificationRouting.ordered selected
                      (users.ListDevices(user) |> Array.toList) false
    let eligible = ProjectNotificationRouting.candidates selected
                       (users.ListDevices(user) |> Array.toList) false "TaskWrite" true
    (eligible |> List.map (fun item -> item.DeviceName)) |> shouldEqual [ "healthy" ]
    let text = ProjectNotificationRouting.failureMessage "Wayfold" "TaskWrite" true false ordered
                   [ "healthy", "destination project or ledger binding not found" ]
    Assert.That(text, Does.Contain("old: protocol 3.48"))
    Assert.That(text, Does.Contain("no-write: missing TaskWrite support"))
    Assert.That(text, Does.Contain("no-ledger: missing LedgerRead support"))
    Assert.That(text, Does.Contain("healthy: destination project or ledger binding not found"))
    Assert.That(text, Does.Not.Contain("No compatible connected"))

[<Test>]
let cross_project_probe_errors_are_classified_without_leaking_client_error_text () =
    let error = "private/absolute/path/with-sensitive-details"
    let failures : (AgentError * string) list =
        [ NotFound error, "destination project or ledger binding not found"
          ValidationFailed error, "ledger binding or project validation failed"
          PermissionDenied error, "permission denied"
          ExecutionFailed error, "ledger health execution failed or timed out" ]
    for issue, expected in failures do
        let reply : AgentCommandResponse =
            { Result = None; Error = Some issue; Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let classification = ProjectNotificationRouting.probeFailure reply
        Assert.That(classification, Is.EqualTo(expected))
        Assert.That(classification, Does.Not.Contain(error))

[<Test>]
let cross_project_routing_error_distinguishes_absent_device_from_protocol_mismatch () =
    let empty = ProjectNotificationRouting.failureMessage "Wayfold" "TaskWrite" true false [] []
    Assert.That(empty, Does.Contain("No registered client"))
    Assert.That(empty, Does.Not.Contain("requires protocol"))
    let explicit = ProjectNotificationRouting.failureMessage "Wayfold" "TaskWrite" true true [] []
    Assert.That(explicit, Does.Contain("No selected/explicit device"))


[<Test>]
let ``all Jarvis MCP tools declare non-destructive closed-world metadata`` () =
    let tools =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute as attribute |] -> Some(methodInfo.Name, attribute)
            | _ -> None)
    Assert.That(tools.Length, Is.GreaterThan(0))
    for name, attribute in tools do
        Assert.That(attribute.Destructive, Is.False, name)
        Assert.That(attribute.OpenWorld, Is.False, name)


[<Test>]
let cross_project_notification_health_probe_uses_client_supported_action () =
    match ProjectNotificationRouting.healthProbe "Wayfold" with
    | LedgerReadCommand command ->
        Assert.That(command.ProjectName, Is.EqualTo("Wayfold"))
        Assert.That(command.Action, Is.EqualTo("Health"))
        Assert.That(command.OperationId, Is.Empty)
    | other -> Assert.Fail(sprintf "Expected LedgerReadCommand; got %A" other)


[<Test>]
let ``project scoped MCP tools standardize explicit agentId arguments`` () =
    let excluded = set [ "RegisterAgent"; "InitializeLedger"; "BindExistingLedger"; "RebindLedgerRemote" ]
    let tools = typeof<JarvisMcpTools>.GetMethods(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Static)
    for tool in tools do
        let parameters = tool.GetParameters() |> Array.map (fun p -> p.Name) |> Set.ofArray
        for deprecated in [ "ownerId"; "newOwnerId"; "actorId"; "reviewerId"; "requesterId" ] do
            Assert.That(parameters.Contains(deprecated), Is.False, tool.Name + " exposes " + deprecated)
        if parameters.Contains("projectName") && not (excluded.Contains tool.Name) then
            let isGlobalFilter =
                tool.Name = "Feedback" || tool.Name = "ListFeedback"
                || tool.Name = "GetFeedbackSummary" || tool.Name = "GetConnectionDiagnostics" || tool.Name = "GetClientActivity"
            if not isGlobalFilter then
                Assert.That(parameters.Contains("agentId") || (tool.Name = "NotifyAgent" && parameters.Contains("fromAgentId")),
                            Is.True, tool.Name + " lacks explicit agent attribution")


[<Test>]
let client_request_abort_cancels_outstanding_device_correlation_without_faulting_device () =
    task {
        let users = UserService()
        let user = "auth0|caller"
        let connection = "conn-1"
        users.TransportConnected(user, connection)
        let registered =
            users.Register(user,
                { DeviceId = "device-1"
                  DeviceName = "Workstation"
                  ProtocolVersion = AgentProtocol.version
                  ClientVersion = "test"
                  SupportedCommands = [ "ListProjects" ] }, connection)
        let tracker = ClientResponseTracker()
        let dispatched =
            TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        let recipient =
            { new Common.SignalR.IClientService with
                member _.ReceiveMessage(_) = Task.CompletedTask
                member _.ReceiveCommand(correlationId, _, _) =
                    dispatched.TrySetResult(correlationId) |> ignore
                    Task.CompletedTask
                member _.InvalidateAgentStates(_, _) = Task.CompletedTask }
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
        use abort = new System.Threading.CancellationTokenSource()
        let context = Microsoft.AspNetCore.Http.DefaultHttpContext()
        context.RequestAborted <- abort.Token
        let http = Microsoft.AspNetCore.Http.HttpContextAccessor()
        http.HttpContext <- context
        let service = ClientService(hub, users, tracker, http)
        let pending = service.SendCommandToUser(user, ListProjectsCommand)
        let! correlationId = dispatched.Task.WaitAsync(TimeSpan.FromSeconds(4.))
        abort.Cancel()
        let! canceled =
            task {
                try
                    let! _ = pending
                    return false
                with :? OperationCanceledException -> return true
            }
        Assert.That(canceled, Is.True)
        let response = { Result = Some "unexpected"; Error = None; Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        Assert.That(tracker.Complete(correlationId, user, connection, registered.Generation, response), Is.False)
        match users.GetSession(user) with
        | ValueSome session ->
            Assert.That(session.State, Is.EqualTo(Registered))
            Assert.That(session.LastFailure.IsNone, Is.True)
        | ValueNone -> Assert.Fail("Original session should remain registered.")
    }


[<Test>]
let polledToolJobsRestoreExactInstructionsAndMutationEvidence () =
    let guidance =
        [ { Scope = "project"; Path = "Jarvis/AGENTS.md"
            Sha256 = "sha256:" + String('c', 64)
            Content = "Canonical instructions from the original command." } ]
    let receipt : AgentInboxActivation =
        { ProjectName = "Jarvis"; AgentId = "Worker 1"
          ActivatedByThisCall = true; BeforeState = Some "Ready"
          AfterState = Some "Working"; CommittedHead = Some(String('a', 40))
          OperationId = Some "activation-id" }
    for failed in [false; true] do
        let original : AgentCommandResponse =
            { Result = if failed then None else Some """{"value":"terminal"}"""
              Error = if failed then Some(ValidationFailed "Original validation failure.") else None
              Instructions = guidance
              AgentStateChanges = [{ ProjectName = "Jarvis"; AgentId = "Worker 1" }]
              InboxActivation = Some receipt; PendingJob = None }
        let job : JobResult =
            { JobId = "tool-job-1"; Status = if failed then ToolFailed "failed" else ToolCompleted
              Events = []; NextSequence = 0L; Truncated = false
              ToolResponse = Some original; OperationId = Some "stable-operation-id" }
        let poll : AgentCommandResponse =
            { Result = Some(JsonSerializer.Serialize job)
              Error = None; Instructions = []
              AgentStateChanges = []; InboxActivation = None; PendingJob = None }
        let command = GetJobResultCommand { ProjectName = "Jarvis"; JobId = job.JobId; AfterSequence = None }
        let effective = McpToolHelpers.completedToolEvidence command poll
        Assert.That(effective, Is.EqualTo(original))
        let displayed =
            poll |> McpToolHelpers.toCallToolResult
            |> McpToolHelpers.attachStateEffects effective
            |> McpToolHelpers.attachInstructions effective.Instructions
        Assert.That(displayed.IsError, Is.EqualTo(Nullable false))
        Assert.That(displayed.Meta["jarvis/agent_state_changes"].ToJsonString(), Does.Contain("Worker 1"))
        Assert.That(displayed.Meta["jarvis/inbox_activation"].ToJsonString(), Does.Contain("activation-id"))
        Assert.That((displayed.Content[0] :?> TextContentBlock).Text,
                    Does.Contain("Canonical instructions from the original command"))
        use terminalJson = JsonDocument.Parse((displayed.Content[1] :?> TextContentBlock).Text)
        let terminal = terminalJson.RootElement.GetProperty("ToolResponse")
        Assert.That(terminal.GetProperty("AgentStateChanges").GetArrayLength(), Is.EqualTo(1))
        Assert.That(terminal.GetProperty("Instructions").GetArrayLength(), Is.EqualTo(1))
        Assert.That(terminal.GetProperty("InboxActivation").GetProperty("ActivatedByThisCall").GetBoolean(), Is.True)

[<Test>]
let pendingToolJobNeverAdvertisesAFalseEmptyMutationList () =
    let handle : ToolJobHandle =
        { JobId = "pending-job"; Kind = JobKind.ToolOperation; Status = "Running"
          ProjectName = Some "Jarvis"; DeviceId = None; AgentId = "Worker 1"
          OperationId = Some "original-id"; CommandName = "ReadFile"
          StartedAt = DateTimeOffset.UtcNow }
    let pending : AgentCommandResponse =
        { Result = Some(JsonSerializer.Serialize handle); Error = None
          Instructions = []; AgentStateChanges = Unchecked.defaultof<_>
          InboxActivation = None; PendingJob = Some handle }
    use wire = JsonDocument.Parse(JsonSerializer.Serialize pending)
    Assert.That(wire.RootElement.GetProperty("AgentStateChanges").ValueKind,
                Is.EqualTo(JsonValueKind.Null))
    let shown =
        pending |> McpToolHelpers.toCallToolResult
        |> McpToolHelpers.attachStateEffects pending
    Assert.That(shown.Meta, Is.Null)
    Assert.That(shown.Content.Count, Is.EqualTo(1),
                "A pending response must not add an authoritative agentStateChanges: [] annotation.")


// Exercise real ClientService routing through two registered SignalR clients
// and its real response tracker, without starting a production transport.
let private routedNotificationFixture (reply: string -> AgentCommand -> string -> AgentCommandResponse) =
    task {
        let users = UserService()
        let userId = "route-authenticated-user"
        let actor = "Worker 1"
        let tracker = ClientResponseTracker()
        let calls = System.Collections.Concurrent.ConcurrentQueue<string * AgentCommand * string>()
        let register device =
            let connection = device + "-connection"
            users.TransportConnected(userId, connection)
            users.Register(userId,
                { DeviceId = device; DeviceName = device
                  ProtocolVersion = AgentProtocol.version; ClientVersion = "test"
                  SupportedCommands = [ "LedgerRead"; "GetJobResult"; "TaskWrite" ] },
                connection) |> ignore
        register "first"
        register "second"
        Assert.That(users.SelectConversationDevice(userId, "route-test", "first"), Is.True)
        let recipient (connectionId: string) =
            { new Common.SignalR.IClientService with
                member _.ReceiveMessage(_) = Task.CompletedTask
                member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                member _.ReceiveCommand(correlationId, commandJson, senderActor) =
                    let device = connectionId.Replace("-connection", "", StringComparison.Ordinal)
                    let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
                    calls.Enqueue((device, command, senderActor))
                    let outcome = reply device command senderActor
                    match users.GetSessionByDevice(userId, device) with
                    | ValueSome session ->
                        tracker.Complete(correlationId, userId, connectionId, session.Generation, outcome) |> ignore
                    | ValueNone -> Assert.Fail("Lost routed test session.")
                    Task.CompletedTask }
        let clients =
            { new Microsoft.AspNetCore.SignalR.IHubClients<Common.SignalR.IClientService> with
                member _.All = recipient "first-connection"
                member _.AllExcept(_) = recipient "first-connection"
                member _.Client(connection) = recipient connection
                member _.Clients(_) = recipient "first-connection"
                member _.Group(_) = recipient "first-connection"
                member _.GroupExcept(_, _) = recipient "first-connection"
                member _.Groups(_) = recipient "first-connection"
                member _.User(_) = recipient "first-connection"
                member _.Users(_) = recipient "first-connection" }
        let hub =
            { new Microsoft.AspNetCore.SignalR.IHubContext<HubService, Common.SignalR.IClientService> with
                member _.Clients = clients
                member _.Groups = Unchecked.defaultof<_> }
        let http = Microsoft.AspNetCore.Http.HttpContextAccessor()
        http.HttpContext <- Microsoft.AspNetCore.Http.DefaultHttpContext()
        let service = ClientService(hub, users, tracker, http)
        let notification : TaskWriteCommand =
            { ProjectName = "Wayfold"; Action = "NotifyAgent"; OperationId = Guid.NewGuid().ToString("D")
              TaskId = "task-1"; AttemptId = ""; ActorId = actor; OwnerId = ""; Title = ""; Html = ""
              ExpectedVersion = 0; ExpectedAgentVersion = 0; ExpectedTaskVersion = 0
              AssignmentGeneration = 0; Summary = "test"; NextAction = ""; Reason = ""
              BaseOid = ""; TargetRef = ""; SourceRef = "Jarvis"; AllowDetached = false
              AllowUncommitted = false; DecisionSequence = 0L; Evidence = ""; SessionId = "" }
        let cmd =
            CrossProjectNotificationCommand
                { Notification = notification
                  SourceStateChanges = [ { ProjectName = "Jarvis"; AgentId = actor } ]
                  SourceInboxActivation = None }
        let! response =
            service.SendCommandToProject(userId, "Wayfold", cmd,
                                         conversationId = "route-test", agentId = actor)
        return response, calls.ToArray()
    }

let private routeTerminalHealth : AgentCommandResponse =
    { Result = Some """{"VerifiedLedger":true}"""; Error = None
      Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }

let private routePendingHealth jobId actor : AgentCommandResponse =
    let handle : ToolJobHandle =
        { JobId = jobId; Kind = JobKind.ToolOperation; Status = "Running"
          ProjectName = Some "Wayfold"; DeviceId = None; AgentId = actor
          OperationId = None; CommandName = "LedgerRead"
          StartedAt = DateTimeOffset.UtcNow }
    { Result = Some(JsonSerializer.Serialize handle); Error = None
      Instructions = []; AgentStateChanges = Unchecked.defaultof<_>
      InboxActivation = None; PendingJob = Some handle }

let private routeJobResult jobId status (terminal: AgentCommandResponse option) : AgentCommandResponse =
    let result : JobResult =
        { JobId = jobId; Status = status; Events = []; NextSequence = 0L
          Truncated = false; ToolResponse = terminal; OperationId = None }
    { Result = Some(JsonSerializer.Serialize result); Error = None
      Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }

[<Test>]
let pendingHealthFailureRejectsFirstDeviceBeforeRoutedCrossProjectMutation () =
    task {
        let jobId = Guid.NewGuid().ToString("N")
        let firstHealth = routePendingHealth jobId "Worker 1"
        let unhealthy = { routeTerminalHealth with Result = None; Error = Some(ValidationFailed "unverified ledger") }
        let respond (device: string) (command: AgentCommand) (actor: string) =
            match device, command with
            | "first", LedgerReadCommand _ -> firstHealth
            | "first", GetJobResultCommand request ->
                Assert.That(request.JobId, Is.EqualTo(jobId))
                Assert.That(request.ProjectName, Is.EqualTo("Wayfold"))
                Assert.That(actor, Is.EqualTo("Worker 1"))
                routeJobResult jobId (ToolFailed "health") (Some unhealthy)
            | "second", LedgerReadCommand _ -> routeTerminalHealth
            | "second", CrossProjectNotificationCommand _ ->
                { routeTerminalHealth with Result = Some """{"notified":true}""" }
            | _ -> failwith $"Unexpected routing: {device}: {AgentCommandInfo.name command}"
        let! outcome, calls = routedNotificationFixture respond
        Assert.That(outcome.Error.IsNone, Is.True)
        let writes =
            calls |> Array.choose (fun (device, command, _) ->
                match command with
                | CrossProjectNotificationCommand _ -> Some device
                | _ -> None)
        Assert.That(writes, Is.EqualTo([|"second"|] :> obj),
                    "A pending unhealthy preferred ledger must never receive a destination write.")
        Assert.That(calls |> Array.exists (fun (device, command, _) ->
            device = "first" && (match command with GetJobResultCommand _ -> true | _ -> false)), Is.True)
    }

[<Test>]
let pendingHealthCompletionUsesExactlyTheOriginalCandidateDeviceAndActor () =
    task {
        let jobId = Guid.NewGuid().ToString("N")
        let firstHealth = routePendingHealth jobId "Worker 1"
        let mutable polls = 0
        let respond (device: string) (command: AgentCommand) (actor: string) =
            match device, command with
            | "first", LedgerReadCommand _ -> firstHealth
            | "first", GetJobResultCommand request ->
                polls <- polls + 1
                Assert.That(request.JobId, Is.EqualTo(jobId))
                Assert.That(actor, Is.EqualTo("Worker 1"))
                if polls = 1 then routeJobResult jobId Running None
                else routeJobResult jobId ToolCompleted (Some routeTerminalHealth)
            | "first", CrossProjectNotificationCommand envelope ->
                Assert.That(envelope.SourceStateChanges.Length, Is.EqualTo(1))
                Assert.That(envelope.SourceStateChanges.Head.ProjectName, Is.EqualTo("Jarvis"))
                { routeTerminalHealth with Result = Some """{"notified":true}""" }
            | _ -> failwith $"Incorrect fallback/actor: {device}: {AgentCommandInfo.name command}"
        let! outcome, calls = routedNotificationFixture respond
        Assert.That(outcome.Error.IsNone, Is.True)
        Assert.That(polls, Is.EqualTo(2))
        Assert.That(calls |> Array.forall (fun (device, _, _) -> device = "first"), Is.True)
        Assert.That(calls |> Array.filter (fun (_, cmd, _) ->
            match cmd with CrossProjectNotificationCommand _ -> true | _ -> false) |> Array.length,
            Is.EqualTo(1))
    }


[<Test>]
let routedPendingDestinationKeepsOriginalDeviceAndSourceEnvelopeWithoutReexecution () =
    task {
        let jobId = Guid.NewGuid().ToString("N")
        let mutable writes = 0
        let respond (device: string) (command: AgentCommand) (actor: string) =
            match device, command with
            | "first", LedgerReadCommand _ -> routeTerminalHealth
            | "first", CrossProjectNotificationCommand envelope ->
                writes <- writes + 1
                Assert.That(actor, Is.EqualTo("Worker 1"))
                Assert.That(envelope.Notification.Action, Is.EqualTo("NotifyAgent"))
                Assert.That(envelope.SourceStateChanges.Length, Is.EqualTo(1))
                Assert.That(envelope.SourceStateChanges[0].ProjectName, Is.EqualTo("Jarvis"))
                let pending = routePendingHealth jobId actor
                let routed = { pending.PendingJob.Value with CommandName = "TaskWrite" }
                { pending with
                    PendingJob = Some routed
                    Result = Some(JsonSerializer.Serialize routed) }
            | _ -> failwith $"Cross-device mutation or repeated execution: {device}/{AgentCommandInfo.name command}"
        let! reply, calls = routedNotificationFixture respond
        Assert.That(reply.Error.IsNone, Is.True)
        Assert.That(reply.PendingJob.IsSome, Is.True)
        let handle = reply.PendingJob.Value
        Assert.That(handle.JobId, Is.EqualTo(jobId))
        Assert.That(handle.DeviceId, Is.EqualTo(Some "first"))
        Assert.That(handle.ProjectName, Is.EqualTo(Some "Wayfold"))
        Assert.That(handle.AgentId, Is.EqualTo("Worker 1"))
        Assert.That(isNull (box reply.AgentStateChanges), Is.True,
                    "Running destination has unknown effects, not an authoritative empty list.")
        let fromPayload = JsonSerializer.Deserialize<ToolJobHandle>(reply.Result.Value)
        Assert.That(fromPayload, Is.EqualTo(handle))
        Assert.That(writes, Is.EqualTo(1))
        Assert.That(calls |> Array.filter (fun (_, command, _) ->
            match command with CrossProjectNotificationCommand _ -> true | _ -> false) |> Array.length,
            Is.EqualTo(1))
    }

[<Test>]
let mergingSourceWithPendingDestinationPreservesUnknownChangesAndOriginalJobHandle () =
    for sourceChanged in [false; true] do
        let source : AgentCommandResponse =
            { Result = Some "{}"; Error = None; Instructions = []
              AgentStateChanges =
                if sourceChanged then [ { ProjectName = "Jarvis"; AgentId = "Worker 1" } ] else []
              InboxActivation =
                if sourceChanged then
                    Some { ProjectName = "Jarvis"; AgentId = "Worker 1"
                           ActivatedByThisCall = true; BeforeState = Some "Ready"
                           AfterState = Some "Working"; CommittedHead = Some(String('a', 40))
                           OperationId = Some "source-activation" }
                else None
              PendingJob = None }
        let pending = routePendingHealth (Guid.NewGuid().ToString("N")) "Worker 1"
        let merged = McpToolHelpers.mergeStateEffects source pending
        Assert.That(merged.PendingJob, Is.EqualTo(pending.PendingJob))
        Assert.That(merged.Result, Is.EqualTo(pending.Result))
        Assert.That(isNull (box merged.AgentStateChanges), Is.True)
        Assert.That(merged.InboxActivation.IsNone, Is.True)
        let result =
            merged |> McpToolHelpers.toCallToolResult
            |> McpToolHelpers.attachStateEffects merged
        Assert.That(result.Meta, Is.Null, "Unverified effects must not appear as committed on a pending reply.")
