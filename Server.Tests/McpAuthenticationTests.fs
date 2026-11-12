module ServerMcpAuthenticationTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open NUnit.Framework

let private repositoryRoot () =
    let rec find (directory: DirectoryInfo) =
        if isNull directory then failwith "Could not find Jarvis.slnx for the HTTP auth test."
        elif File.Exists(Path.Combine(directory.FullName, "Jarvis.slnx")) then directory.FullName
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))

let private unusedLoopbackPort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

[<Test>]
let unauthenticated_mcp_requests_get_http_oauth_challenge_but_resource_metadata_remains_public () =
    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let serverDll =
        Path.Combine(repositoryRoot (), "Server", "bin", configuration, "net11.0", "JarvisServer.dll")
    Assert.That(File.Exists(serverDll), Is.True, $"Missing server executable: {serverDll}")

    let tempDirectory = Path.Combine(Path.GetTempPath(), "jarvis-mcp-auth-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDirectory) |> ignore
    let port = unusedLoopbackPort ()
    let endpoint = $"http://127.0.0.1:{port}"

    let start = ProcessStartInfo("dotnet")
    start.ArgumentList.Add(serverDll)
    start.UseShellExecute <- false
    start.CreateNoWindow <- true
    start.Environment["ASPNETCORE_URLS"] <- endpoint
    start.Environment["ASPNETCORE_ENVIRONMENT"] <- "Development"
    start.Environment["JarvisAuth0Domain"] <- "dev-kn4j3jz3qv2cvw05.eu.auth0.com"
    start.Environment["JarvisAudience"] <- "https://jarvis2.kehlet.dev"
    start.Environment["JarvisDatabasePath"] <- Path.Combine(tempDirectory, "feedback.db")

    use server = Process.Start(start)
    try
        use http = new HttpClient()
        http.Timeout <- TimeSpan.FromSeconds(2.0)
        // The spawned server starts asynchronously; wait for the public metadata
        // endpoint instead of assuming a fixed startup delay.
        let mutable ready = false
        let mutable attempts = 0
        while not ready && attempts < 100 && not server.HasExited do
            attempts <- attempts + 1
            try
                use response = http.GetAsync(endpoint + "/.well-known/oauth-protected-resource").GetAwaiter().GetResult()
                ready <- response.StatusCode = HttpStatusCode.OK
            with
            | :? HttpRequestException
            | :? TaskCanceledException -> ()
            if not ready then Thread.Sleep(100)

        Assert.That(ready, Is.True, "Jarvis test server did not start or metadata is no longer public.")

        // MCP initialization and discovery both have to return an *HTTP* 401.
        // An MCP tool-level error in an HTTP 200 response is not an OAuth trigger
        // for clients such as LM Studio.
        let requests =
            [ """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"auth-probe","version":"1"}}}"""
              """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""" ]

        for payload in requests do
            use request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/mcp")
            request.Content <- new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
            use response = http.SendAsync(request).GetAwaiter().GetResult()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
            let challenge =
                response.Headers.WwwAuthenticate
                |> Seq.map string
                |> String.concat ","
            Assert.That(challenge, Does.Contain("Bearer"))
            Assert.That(challenge, Does.Contain("resource_metadata=\"https://jarvis2.kehlet.dev/.well-known/oauth-protected-resource\""))
            Assert.That(challenge, Does.Not.Contain("scope=\"client:connect\""))
        // Verify the updater's SignalR readiness contract against the real router.
        use negotiate = new HttpRequestMessage(HttpMethod.Post, endpoint + "/client/negotiate?negotiateVersion=1")
        use negotiateResponse = http.SendAsync(negotiate).GetAwaiter().GetResult()
        Assert.That(negotiateResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        let negotiateChallenge =
            negotiateResponse.Headers.WwwAuthenticate
            |> Seq.map string
            |> String.concat ","
        Assert.That(negotiateChallenge, Does.Contain("Bearer"))
        Assert.That(negotiateChallenge, Does.Contain("client:connect"))
    finally
        if not server.HasExited then
            server.Kill(true)
            server.WaitForExit(3000) |> ignore
        try Directory.Delete(tempDirectory, true) with _ -> ()


[<Test>]
let server_does_not_serve_readiness_routes_when_feedback_database_cannot_initialize () =
    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let serverDll =
        Path.Combine(repositoryRoot (), "Server", "bin", configuration, "net11.0", "JarvisServer.dll")
    Assert.That(File.Exists(serverDll), Is.True)
    let tempDirectory = Path.Combine(Path.GetTempPath(), "jarvis-server-readiness-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDirectory) |> ignore
    // Passing a directory as the database filename must fail SQLite startup.
    let invalidDatabasePath = Path.Combine(tempDirectory, "is-a-directory")
    Directory.CreateDirectory(invalidDatabasePath) |> ignore
    let start = ProcessStartInfo("dotnet")
    start.ArgumentList.Add(serverDll)
    start.UseShellExecute <- false
    start.CreateNoWindow <- true
    start.RedirectStandardError <- true
    start.Environment["ASPNETCORE_URLS"] <- $"http://127.0.0.1:{unusedLoopbackPort ()}"
    start.Environment["ASPNETCORE_ENVIRONMENT"] <- "Development"
    start.Environment["JarvisAuth0Domain"] <- "dev-kn4j3jz3qv2cvw05.eu.auth0.com"
    start.Environment["JarvisAudience"] <- "https://jarvis2.kehlet.dev"
    start.Environment["JarvisDatabasePath"] <- invalidDatabasePath
    use server = Process.Start(start)
    try
        let exited = server.WaitForExit(10000)
        Assert.That(exited, Is.True, "Server must not begin accepting traffic with an unusable database.")
        Assert.That(server.ExitCode, Is.Not.EqualTo(0))
        let diagnostic = server.StandardError.ReadToEnd()
        Assert.That(diagnostic, Does.Contain("SQLite"))
    finally
        if not server.HasExited then
            server.Kill(true)
            server.WaitForExit(3000) |> ignore
        try Directory.Delete(tempDirectory, true) with _ -> ()

[<Test>]
let write_tools_expose_optional_operation_ids () =
    let methods = typeof<Server.JarvisMcpTools>.GetMethods()
    let writeMethods =
        methods
        |> Array.filter (fun methodInfo -> methodInfo.Name <> "Feedback")
        |> Array.choose (fun methodInfo ->
            match methodInfo.GetParameters() |> Array.tryFind (fun p -> p.Name = "operationId") with
            | Some id when not (List.contains methodInfo.Name
                            [ "GetOperationStatus"; "AcknowledgePendingOperation";
                              "InspectPendingOperation"; "RetryPendingIntent"; "SupersedePendingIntent"; "AbandonPendingIntent" ]) ->
                Some (methodInfo.Name, id)
            | _ -> None)
    Assert.That(writeMethods.Length, Is.EqualTo(35), String.concat ", " (writeMethods |> Array.map fst))
    for name, id in writeMethods do
        Assert.That(id.IsOptional, Is.True, $"operationId is required on {name}")
        Assert.That(id.DefaultValue, Is.EqualTo(""), $"operationId default differs on {name}")

    for selector in [ "GetOperationStatus"; "AcknowledgePendingOperation";
                      "InspectPendingOperation"; "RetryPendingIntent"; "SupersedePendingIntent"; "AbandonPendingIntent" ] do
        let id = typeof<Server.JarvisMcpTools>.GetMethod(selector).GetParameters()
                 |> Array.find (fun parameter -> parameter.Name = "operationId")
        Assert.That(id.IsOptional, Is.False, $"Selector ID must remain required: {selector}")

[<Test>]
let omitted_operation_id_is_generated_and_explicit_retry_id_is_preserved () =
    let first, generated = Server.McpToolHelpers.resolveOperationId ""
    let second, generatedAgain = Server.McpToolHelpers.resolveOperationId " "
    let valid, parsed = Guid.TryParse first
    Assert.That(generated && generatedAgain && valid && parsed <> Guid.Empty, Is.True)
    Assert.That(second, Is.Not.EqualTo(first))
    let explicitId = Guid.NewGuid().ToString("D")
    let retained, didGenerate = Server.McpToolHelpers.resolveOperationId explicitId
    Assert.That(retained, Is.EqualTo(explicitId))
    Assert.That(didGenerate, Is.False)


[<Test>]
let generated_operation_id_is_in_agent_visible_result_and_metadata () =
    let id = Guid.NewGuid().ToString("D")
    let result = ModelContextProtocol.Protocol.CallToolResult()
    result.Content <- ResizeArray<ModelContextProtocol.Protocol.ContentBlock>()
    let updated = Server.McpToolHelpers.attachGeneratedOperationId (Some id) result
    Assert.That(updated.Meta["jarvis/generated_operation_id"].GetValue<string>(), Is.EqualTo(id))
    let text =
        updated.Content
        |> Seq.choose (function
            | :? ModelContextProtocol.Protocol.TextContentBlock as block -> Some block.Text
            | _ -> None)
        |> String.concat "\n"
    Assert.That(text, Does.Contain(id))
    Assert.That(text, Does.Contain("retries"))
    let unchanged = Server.McpToolHelpers.attachGeneratedOperationId None result
    Assert.That(Object.ReferenceEquals(updated, unchanged), Is.True)

[<Test>]
let cross_project_notification_parameter_is_optional_and_backwards_compatible () =
    let methodInfo = typeof<Server.JarvisMcpTools>.GetMethod("NotifyAgent")
    let parameters = methodInfo.GetParameters()
    let destination = parameters |> Array.find (fun p -> p.Name = "toProjectName")
    Assert.That(destination.IsOptional, Is.True)
    Assert.That(destination.DefaultValue, Is.EqualTo(""))
    for name in [ "projectName"; "fromAgentId"; "toAgentId"; "message" ] do
        let parameter = parameters |> Array.find (fun p -> p.Name = name)
        Assert.That(parameter.IsOptional, Is.False, $"{name} must remain required")

[<Test>]
let public_agent_lifecycle_has_implicit_start_and_explicit_administrative_state () =
    let tools = typeof<Server.JarvisMcpTools>
    Assert.That(tools.GetMethod("BeginAttempt"), Is.Null)
    Assert.That(tools.GetMethod("ResumeAgent"), Is.Null)
    Assert.That(tools.GetMethod("ParkAgent"), Is.Null)
    let admin = tools.GetMethod("SetAgentState")
    Assert.That(admin, Is.Not.Null)
    let state = admin.GetParameters() |> Array.find (fun p -> p.Name = "state")
    Assert.That(state.ParameterType, Is.EqualTo(typeof<Server.AdministrativeAgentState>))
    Assert.That((Enum.GetNames(state.ParameterType) = [| "NeedsWork"; "Parked" |]), Is.True)
    for name in [ "agentId"; "targetAgentId"; "reason"; "expectedAgentVersion" ] do
        Assert.That(admin.GetParameters() |> Array.exists (fun p -> p.Name = name), Is.True)

[<Test>]
let core_task_tools_have_one_public_operation_per_intention () =
    let tool name = typeof<Server.JarvisMcpTools>.GetMethod(name)
    for retired in [ "AcknowledgeAssignment"; "CancelOpenTask"; "ReviseTaskSpecPinned" ] do
        Assert.That(tool retired, Is.Null, $"Retired MCP tool {retired} must not be exposed.")
    let revise = tool "ReviseTaskSpec"
    Assert.That(revise, Is.Not.Null)
    let expectedSpec = revise.GetParameters() |> Array.find (fun p -> p.Name = "expectedSpecBlobOid")
    Assert.That(expectedSpec.IsOptional, Is.True)
    Assert.That(expectedSpec.DefaultValue, Is.EqualTo(""))
    let cancel = tool "CancelTask"
    Assert.That(cancel, Is.Not.Null)
    for name in [ "expectedAttemptVersion"; "expectedAgentVersion"; "assignmentGeneration" ] do
        let parameter = cancel.GetParameters() |> Array.find (fun p -> p.Name = name)
        Assert.That(parameter.IsOptional, Is.True, name)
        Assert.That(parameter.DefaultValue, Is.EqualTo(0), name)

[<Test>]
let queued_assignment_update_exposes_both_reissue_and_spec_adoption () =
    let methods = typeof<Server.JarvisMcpTools>
    Assert.That(methods.GetMethod("AdoptTaskSpec"), Is.Null)
    let reissue = methods.GetMethod("ReissueAssignment")
    Assert.That(reissue, Is.Not.Null)
    for name in [ "newSpecHtml"; "expectedCurrentSpecBlobOid"; "expectedTaskVersion" ] do
        let parameter = reissue.GetParameters() |> Array.find (fun p -> p.Name = name)
        Assert.That(parameter.IsOptional, Is.True, name)
        Assert.That(parameter.DefaultValue,
                    Is.EqualTo(if name = "expectedTaskVersion" then box 0 else box ""), name)

[<Test>]
let attempt_deferral_has_single_public_tool_with_disposition () =
    let methods = typeof<Server.JarvisMcpTools>
    Assert.That(methods.GetMethod("ParkAttempt"), Is.Null)
    Assert.That(methods.GetMethod("BlockAttempt"), Is.Null)
    let deferAttempt = methods.GetMethod("DeferAttempt")
    Assert.That(deferAttempt, Is.Not.Null)
    let disposition =
        deferAttempt.GetParameters() |> Array.find (fun p -> p.Name = "disposition")
    Assert.That(disposition.IsOptional, Is.True)
    Assert.That(disposition.DefaultValue, Is.EqualTo("Deferred"))

[<Test>]
let source_context_has_one_public_tool_with_optional_initial_baseline () =
    let methods = typeof<Server.JarvisMcpTools>
    for retired in [ "BindReviewBase"; "BindAttemptSource"; "RebindAttemptSource" ] do
        Assert.That(methods.GetMethod(retired), Is.Null, retired)
    let setSource = methods.GetMethod("SetAttemptSource")
    Assert.That(setSource, Is.Not.Null)
    for name in [ "baseOid"; "targetRef"; "allowDetached"; "allowUncommitted" ] do
        let parameter = setSource.GetParameters() |> Array.find (fun p -> p.Name = name)
        Assert.That(parameter.IsOptional, Is.True, name)

[<Test>]
let review_publication_is_one_tool_with_optional_replacement_version () =
    let methods = typeof<Server.JarvisMcpTools>
    Assert.That(methods.GetMethod("ReplaceReview"), Is.Null)
    let publish = methods.GetMethod("SubmitReview")
    Assert.That(publish, Is.Not.Null)
    let expected = publish.GetParameters() |> Array.find (fun p -> p.Name = "expectedRequestVersion")
    Assert.That(expected.IsOptional, Is.True)
    Assert.That(expected.DefaultValue, Is.EqualTo(0))

[<Test>]
let routine_agent_lifecycle_versions_are_optional_preconditions () =
    for toolName in [ "YieldAgent"; "CheckpointAttempt"; "CompleteAgentWork" ] do
        let methodInfo = typeof<Server.JarvisMcpTools>.GetMethod(toolName)
        Assert.That(methodInfo, Is.Not.Null)
        for name in [ "expectedAttemptVersion"; "expectedAgentVersion"; "assignmentGeneration" ] do
            let parameter = methodInfo.GetParameters() |> Array.find (fun p -> p.Name = name)
            Assert.That(parameter.IsOptional, Is.True, $"{toolName}: {name}")
            Assert.That(parameter.DefaultValue, Is.EqualTo(0), $"{toolName}: {name}")


[<Test>]
let create_task_accepts_optional_task_id_and_requires_content () =
    let parameters = typeof<Server.JarvisMcpTools>.GetMethod("CreateTask").GetParameters()
    for name in [ "projectName"; "title"; "html" ] do
        let param = parameters |> Array.find (fun p -> p.Name = name)
        Assert.That(param.IsOptional, Is.False, name)
    let id = parameters |> Array.find (fun p -> p.Name = "taskId")
    Assert.That(id.IsOptional, Is.True)
    Assert.That(id.DefaultValue, Is.EqualTo(""))

[<Test>]
let generated_task_id_is_unique_per_operation_and_stable_for_retries () =
    let op = Guid.NewGuid()
    let id, generated = Server.McpToolHelpers.resolveTaskId "" (op.ToString("D"))
    let replay, repeated = Server.McpToolHelpers.resolveTaskId null (op.ToString("N").ToUpperInvariant())
    let other, created = Server.McpToolHelpers.resolveTaskId " " (Guid.NewGuid().ToString("D"))
    Assert.That(generated && repeated && created, Is.True)
    Assert.That(id, Is.EqualTo("task-" + op.ToString("N")))
    Assert.That(replay, Is.EqualTo(id))
    Assert.That(other, Is.Not.EqualTo(id))
    let explicitId, explicitGenerated = Server.McpToolHelpers.resolveTaskId "named-task-42" (op.ToString("D"))
    Assert.That(explicitId, Is.EqualTo("named-task-42"))
    Assert.That(explicitGenerated, Is.False)
    let invalid, wasGenerated = Server.McpToolHelpers.resolveTaskId "" "invalid-operation"
    Assert.That(invalid, Is.Empty)
    Assert.That(wasGenerated, Is.False)

[<Test>]
let generated_task_id_is_visible_in_mcp_response_metadata_and_text () =
    let taskId = "task-" + Guid.NewGuid().ToString("N")
    let response = ModelContextProtocol.Protocol.CallToolResult()
    let updated = Server.McpToolHelpers.attachGeneratedTaskId (Some taskId) response
    Assert.That(updated.Meta["jarvis/generated_task_id"].GetValue<string>(), Is.EqualTo(taskId))
    let text =
        updated.Content
        |> Seq.choose (function
            | :? ModelContextProtocol.Protocol.TextContentBlock as block -> Some block.Text
            | _ -> None)
        |> String.concat "\n"
    Assert.That(text, Does.Contain("Jarvis taskId: " + taskId))
    Assert.That(Object.ReferenceEquals(Server.McpToolHelpers.attachGeneratedTaskId None updated, updated), Is.True)
