module ServerServiceTests

open System
open System.Text.Json
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
let ``user service exposes transport connection before registration`` () =
    let users = UserService()
    users.TransportConnected("connection")

    match users.GetTransportConnection("connection") with
    | ValueNone -> Assert.Fail("Expected transport connection.")
    | ValueSome connection ->
        connection.ConnectionId |> shouldEqual "connection"
        connection.RegisteredKey |> shouldEqual None

[<Test>]
let ``user service increments generation and preserves replacement on stale disconnect`` () =
    let users = UserService()
    let registration =
        { Key = "session"
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.2.3" }

    users.TransportConnected("old-connection")
    let first = users.Register(registration, "old-connection")
    users.TransportConnected("new-connection")
    let second = users.Register(registration, "new-connection")

    first.Generation |> shouldEqual 1L
    second.Generation |> shouldEqual 2L
    second.RegistrationReason.Contains("Reconnect", StringComparison.Ordinal) |> shouldEqual true

    users.Disconnect("old-connection", Some "stale disconnect")

    match users.GetSession("session") with
    | ValueNone -> Assert.Fail("Expected retained session.")
    | ValueSome session ->
        session.State |> shouldEqual Registered
        session.ConnectionId |> shouldEqual (Some "new-connection")
        session.Generation |> shouldEqual 2L
        session.ProtocolVersion |> shouldEqual AgentProtocol.version
        session.ClientVersion |> shouldEqual "1.2.3"
        users.GetConnectionId("session") |> shouldEqual (ValueSome "new-connection")

[<Test>]
let ``user service retains disconnected session and failure reason`` () =
    let users = UserService()
    let registration =
        { Key = "session"
          ProtocolVersion = AgentProtocol.version
          ClientVersion = "1.0.0" }

    users.TransportConnected("connection")
    users.Register(registration, "connection") |> ignore
    users.RecordDispatchFailure("session", "connection", "dispatch failed")

    match users.GetSession("session") with
    | ValueNone -> Assert.Fail("Expected session.")
    | ValueSome session ->
        session.LastFailure |> shouldEqual (Some "dispatch failed")

    users.Disconnect("connection", Some "network lost")

    match users.GetSession("session") with
    | ValueNone -> Assert.Fail("Expected retained disconnected session.")
    | ValueSome session ->
        session.State |> shouldEqual Disconnected
        session.ConnectionId |> shouldEqual None
        session.DisconnectedAt.IsSome |> shouldEqual true
        session.LastFailure |> shouldEqual (Some "network lost")
        users.GetConnectionId("session") |> shouldEqual ValueNone

[<Test>]
let ``user service records protocol mismatch in registration reason`` () =
    let users = UserService()
    users.TransportConnected("connection")

    let result =
        users.Register(
            { Key = "session"
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
let ``all Jarvis MCP tools return structured JSON content`` () =
    let methods =
        typeof<JarvisMcpTools>.GetMethods()
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetCustomAttributes(typeof<McpServerToolAttribute>, false) with
            | [| :? McpServerToolAttribute as attribute |] -> Some(methodInfo, attribute)
            | _ -> None)

    methods.Length > 0 |> shouldEqual true

    for methodInfo, attribute in methods do
        attribute.UseStructuredContent |> shouldEqual true
        methodInfo.ReturnType.IsGenericType |> shouldEqual true
        methodInfo.ReturnType.GetGenericTypeDefinition() |> shouldEqual typedefof<Task<_>>
        methodInfo.ReturnType.GetGenericArguments()[0] |> shouldEqual typeof<CallToolResult>
        attribute.OutputSchemaType |> shouldEqual typeof<JsonElement>

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

    unionNames |> shouldEqual catalogNames
    mcpNames |> shouldEqual catalogNames

[<Test>]
let ``actions schema matches command catalog operations and routes`` () =
    let schemaPath =
        IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "actions-schema"))

    let lines = IO.File.ReadAllLines schemaPath

    let operationIds =
        lines
        |> Array.choose (fun line ->
            let trimmed = line.Trim()
            let prefix = "operationId: "
            if trimmed.StartsWith(prefix, StringComparison.Ordinal) then
                Some(trimmed.Substring(prefix.Length))
            else
                None)
        |> Set.ofArray

    let routes =
        lines
        |> Array.choose (fun line ->
            if line.StartsWith("  /agent/", StringComparison.Ordinal) && line.EndsWith(":", StringComparison.Ordinal) then
                Some(line.Trim().TrimEnd(':'))
            else
                None)
        |> Set.ofArray

    let expectedOperationIds =
        AgentProtocol.commandDefinitions
        |> List.map (fun definition -> definition.OperationId)
        |> Set.ofList

    let expectedRoutes =
        AgentProtocol.commandDefinitions
        |> List.map (fun definition -> "/agent" + AgentProtocol.legacyRoute definition.Name)
        |> Set.ofList

    operationIds |> shouldEqual expectedOperationIds
    routes |> shouldEqual expectedRoutes

[<Test>]
let ``MCP bridge preserves successful structured content`` () =
    let response =
        { Result = Some """{"value":42,"items":[1,2]}"""
          Error = None }

    let result = McpToolHelpers.toCallToolResult response

    result.IsError |> shouldEqual (Nullable false)
    result.StructuredContent.HasValue |> shouldEqual true
    let structured = result.StructuredContent.Value
    structured.GetProperty("value").GetInt32() |> shouldEqual 42
    structured.GetProperty("items").GetArrayLength() |> shouldEqual 2

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
    result.StructuredContent.HasValue |> shouldEqual true
    let structured = result.StructuredContent.Value
    structured.GetProperty("kind").GetString() |> shouldEqual "ConfirmationRequired"
    structured.GetProperty("message").GetString() |> shouldEqual confirmation.Summary

    let request = structured.GetProperty("confirmationRequest")
    request.GetProperty("CommandName").GetString() |> shouldEqual confirmation.CommandName
    request.GetProperty("Summary").GetString() |> shouldEqual confirmation.Summary

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
        result.StructuredContent.HasValue |> shouldEqual true
        let structured = result.StructuredContent.Value
        structured.GetProperty("kind").GetString() |> shouldEqual expectedKind
        structured.GetProperty("message").GetString() |> shouldEqual expectedMessage


[<Test>]
let auth_issuer_normalizes_auth0_domain () =
    let options =
        { Auth0Domain = "dev-kn4j3jz3qv2cvw05.eu.auth0.com"
          Audience = "https://jarvis2.kehlet.dev" }

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
