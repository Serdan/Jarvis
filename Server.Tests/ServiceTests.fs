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
          ClientVersion = "1.2.3" }

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
          ClientVersion = "1.0.0" }
    let secondRegistration = { firstRegistration with DeviceId = "device-2"; DeviceName = "Laptop" }

    users.TransportConnected(userId, "desktop-connection")
    users.Register(userId, firstRegistration, "desktop-connection") |> ignore
    users.TransportConnected(userId, "laptop-connection")
    let switched = users.Register(userId, secondRegistration, "laptop-connection")

    switched.Generation |> shouldEqual 2L
    switched.RegistrationReason.Contains("Device switch", StringComparison.Ordinal) |> shouldEqual true

    match users.GetSession(userId) with
    | ValueNone -> Assert.Fail("Expected active device.")
    | ValueSome session ->
        session.DeviceId |> shouldEqual "device-2"
        session.DeviceName |> shouldEqual "Laptop"
        session.ConnectionId |> shouldEqual (Some "laptop-connection")

[<Test>]
let user_service_retains_disconnected_session_and_failure_reason () =
    let users = UserService()
    let userId = "auth0|user"
    let registration =
        { DeviceId = "device-1"
          DeviceName = "Workstation"
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0.0" }

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
              ClientVersion = "1.0.0" },
            "connection"
        )

    result.RegistrationReason.Contains("protocol mismatch", StringComparison.Ordinal) |> shouldEqual true

[<Test>]
let ``response tracker completes registered response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register(TimeSpan.FromSeconds 5L)
        let expected =
            { Result = Some "ok"
              Error = None }

        tracker.Complete(correlationId, expected)
        let! actual = pending

        actual |> shouldEqual expected
    }

[<Test>]
let ``response tracker cancellation removes pending response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register(TimeSpan.FromSeconds 5L)

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
        let _, pending = tracker.Register(TimeSpan.FromMilliseconds 25L)

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
        set [ "GetProfile"; "Feedback"; "ListFeedback"; "GetFeedbackSummary" ]

    unionNames |> shouldEqual catalogNames
    Set.difference mcpNames serverLocalNames |> shouldEqual catalogNames
    Set.difference mcpNames catalogNames |> shouldEqual serverLocalNames

[<Test>]
let ``MCP bridge returns successful JSON as text without a mismatched output schema`` () =
    let response =
        { Result = Some """{"value":42,"items":[1,2]}"""
          Error = None }

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
          Error = Some(ConfirmationRequired confirmation) }

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
              Error = Some error }
            |> McpToolHelpers.toCallToolResult

        result.IsError |> shouldEqual (Nullable true)
        result.StructuredContent.HasValue |> shouldEqual false
        let text = result.Content[0] :?> TextContentBlock
        text.Text |> shouldEqual $"{expectedKind}: {expectedMessage}"


[<Test>]
let auth_issuer_normalizes_auth0_domain () =
    let options =
        { Auth0Domain = "dev-kn4j3jz3qv2cvw05.eu.auth0.com"
          Audience = "https://jarvis2.kehlet.dev"
          OpenAIAppsChallenge = "" }

    Auth.issuer options |> shouldEqual "https://dev-kn4j3jz3qv2cvw05.eu.auth0.com/"

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
        if definition.Permissions |> List.contains WorkspaceWrite then Auth.WorkspaceWrite
        elif definition.Permissions |> List.contains ProcessExecution then Auth.ProcessExecute
        elif definition.Permissions |> List.contains VersionControlWrite then Auth.GitWrite
        else Auth.WorkspaceRead

    let definitions =
        AgentProtocol.commandDefinitions
        |> List.map (fun definition -> definition.Name, definition)
        |> Map.ofList

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
            | None -> Auth.WorkspaceRead

        let readOnly =
            match definition with
            | Some definition -> definition.Permissions = [ ReadOnly ]
            | None ->
                match methodInfo.Name with
                | "Feedback" -> false
                | "ListFeedback" | "GetFeedbackSummary" -> true
                | name -> failwith $"Unexpected server-local MCP tool: {name}"

        let destructive =
            match methodInfo.Name with
            | "WriteFile" | "PatchFile" | "RunCommand" | "RunProjectTask" | "StartJob" | "CancelJob" -> true
            | _ -> false

        let openWorld =
            match methodInfo.Name with
            | "RunCommand" | "RunProjectTask" | "StartJob" -> true
            | _ -> false

        attribute.ReadOnly |> shouldEqual readOnly
        attribute.Destructive |> shouldEqual destructive
        attribute.OpenWorld |> shouldEqual openWorld

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
