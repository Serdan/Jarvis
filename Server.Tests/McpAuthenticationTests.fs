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
        Path.Combine(repositoryRoot (), "Server", "bin", configuration, "net10.0", "JarvisServer.dll")
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
            | Some id when methodInfo.Name <> "GetOperationStatus"
                           && methodInfo.Name <> "AcknowledgePendingOperation" ->
                Some (methodInfo.Name, id)
            | _ -> None)
    Assert.That(writeMethods.Length, Is.EqualTo(46), String.concat ", " (writeMethods |> Array.map fst))
    for name, id in writeMethods do
        Assert.That(id.IsOptional, Is.True, $"operationId is required on {name}")
        Assert.That(id.DefaultValue, Is.EqualTo(""), $"operationId default differs on {name}")

    for selector in [ "GetOperationStatus"; "AcknowledgePendingOperation" ] do
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
