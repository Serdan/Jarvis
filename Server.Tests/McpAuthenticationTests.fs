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
