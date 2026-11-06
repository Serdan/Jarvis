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
    users.Register(userId, firstRegistration, "desktop-connection") |> ignore
    users.TransportConnected(userId, "laptop-connection")
    let switched = users.Register(userId, secondRegistration, "laptop-connection")

    switched.Generation |> shouldEqual 1L
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
    users.Register(userId, makeRegistration "home" "Home", "home-1") |> ignore
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
    reconnected.Generation |> shouldEqual 2L
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

        let client = ClientService(Unchecked.defaultof<_>, users, ClientResponseTracker())
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
              Instructions = [] }

        tracker.Complete(correlationId, "alice", "conn-1", 1L, expected) |> shouldEqual true
        let! actual = pending

        actual |> shouldEqual expected
    }

[<Test>]
let ``response tracker rejects other users connections and generations`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register("alice", "connection-a", 3L, TimeSpan.FromSeconds 5L)
        let response = { Result = Some "accepted"; Error = None; Instructions = [] }

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
              "LedgerHealth"; "GetOperationStatus"; "PendingOperations"; "ReconcilePendingOperations"; "AcknowledgePendingOperation"; "InitializeLedger"; "BindExistingLedger"; "RebindLedgerRemote"
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
    clientMethods.Length |> shouldEqual (AgentProtocol.commandDefinitions.Length - 1 + 49) // public orchestration aliases / 8 wire operations
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
          Instructions = [] }

    let result = McpToolHelpers.toCallToolResult response

    result.IsError |> shouldEqual (Nullable false)
    result.StructuredContent.HasValue |> shouldEqual false

    let text = result.Content[0] :?> TextContentBlock
    text.Text |> shouldEqual """{"value":42,"items":[1,2]}"""

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
          Instructions = [] }

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
              Instructions = [] }
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
            [ for name in [ "LedgerHealth"; "GetOperationStatus"; "PendingOperations"; "ReconcilePendingOperations" ] -> name, ledgerRead
              for name in [ "InitializeLedger"; "BindExistingLedger"; "RebindLedgerRemote"; "AcknowledgePendingOperation" ] -> name, ledgerWrite
              for name in [ "GetIntegrationSubmissions"; "AgentInbox"; "GetAssignment"; "GetCheckpoint"; "InspectAttemptSource"; "TaskLifecycleHistory"; "GetTaskSpecPage"; "DiffTaskSpec" ] -> name, taskRead
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
        [ { Scope = "project"
            Path = "Demo/src/AGENTS.md"
            Sha256 = "sha256:" + String('a', 64)
            Content = "Source guidance" } ]
    let response =
        { Result = Some """{"result":"ok"}"""
          Error = None
          Instructions = instructions }
    let result = response |> McpToolHelpers.toCallToolResult |> McpToolHelpers.attachInstructions instructions
    Assert.That(result.IsError, Is.EqualTo(Nullable false))
    Assert.That(result.Content.Count, Is.EqualTo(2))
    let guidance = result.Content[0] :?> TextContentBlock
    Assert.That(guidance.Text, Does.Contain("Demo/src/AGENTS.md"))
    Assert.That(guidance.Text, Does.Contain("Source guidance"))
    let operation = result.Content[1] :?> TextContentBlock
    Assert.That(operation.Text, Is.EqualTo("""{"result":"ok"}"""))

[<Test>]
let mcp_agency_write_warning_precedes_success_without_marking_error () =
    let notes =
        [ { Scope = "write-warning"; Path = "Demo/AGENTS.md"; Sha256 = ""
            Content = "WARNING: Demo/AGENTS.md is unusually large. The write succeeded." } ]
    let response = { Result = Some """{"ok":true}"""; Error = None; Instructions = notes }
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
          Instructions = instructions }
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
          Instructions = [ { Scope = "project"; Path = "Demo/AGENTS.md"; Sha256 = "sha256:" + String('f', 64); Content = "# Guidance" } ] }
    let json = JsonSerializer.Serialize(response)
    use document = JsonDocument.Parse(json)
    let root = document.RootElement
    Assert.That(root.GetProperty("Result").GetString(), Is.EqualTo("""{"ok":true}"""))
    let instruction = root.GetProperty("Instructions").EnumerateArray() |> Seq.head
    Assert.That(instruction.GetProperty("Path").GetString(), Is.EqualTo("Demo/AGENTS.md"))
    Assert.That(instruction.GetProperty("Scope").GetString(), Is.EqualTo("project"))
    Assert.That(instruction.GetProperty("Content").GetString(), Is.EqualTo("# Guidance"))


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
          Instructions = [] }
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
          Instructions = [] }
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
            { Result = None; Error = Some issue; Instructions = [] }
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
