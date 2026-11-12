module ServerMcpCapacityTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Security.Claims
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ModelContextProtocol.Server
open NUnit.Framework
open global.Server

// Real MCP HTTP dispatch, but only deliberately tiny in-memory test tools.
// No external client, Git ledger, auth provider, or production droplet is involved.
module private ProbeState =
    let mutable gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let entered = ConcurrentQueue<int>()
    let reset () =
        gate <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        entered.Clear()

[<McpServerToolType>]
type ConcurrencyProbeTools =
    [<McpServerTool>]
    static member Hold(callId: int) =
        task {
            ProbeState.entered.Enqueue(callId)
            do! ProbeState.gate.Task
            return callId
        }

let private unusedLoopbackPort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

let private startServer () =
    task {
        let endpoint = sprintf "http://127.0.0.1:%d" (unusedLoopbackPort ())
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls(endpoint) |> ignore
        builder.Services.AddAuthorization() |> ignore
        builder.Services.AddRateLimiter(HttpRateLimitProtection.configure) |> ignore
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(fun options -> options.Stateless <- true)
            .WithRequestFilters(fun filters ->
                filters.AddCallToolFilter(McpEndpoint.deviceRoutingFilter()) |> ignore)
            .WithTools<ConcurrencyProbeTools>()
        |> ignore

        let app = builder.Build()
        app.UseRouting() |> ignore
        // Trusted synthetic test principal. Production JWT and OAuth behavior
        // is independently covered by McpAuthenticationTests.
        app.Use(
            Func<RequestDelegate, RequestDelegate>(fun next ->
                RequestDelegate(fun context ->
                    context.User <-
                        ClaimsPrincipal(ClaimsIdentity([ Claim("sub", "one-operator") ], "test-auth"))
                    next.Invoke(context)))
        ) |> ignore
        app.UseAuthorization() |> ignore
        app.UseRateLimiter() |> ignore

        // Exercise the same map and rate policy modules as production.
        McpEndpoint.map app
        app.MapGet("/client", Func<string>(fun () -> "client"))
            .RequireRateLimiting(HttpRateLimitProtection.Policy) |> ignore
        app.MapGet("/exports/test", Func<string>(fun () -> "export"))
            .RequireRateLimiting(HttpRateLimitProtection.Policy) |> ignore

        do! app.StartAsync()
        return app, endpoint
    }

let private postMcp (http: HttpClient) (endpoint: string) (payload: string) =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/mcp")
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
        request.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
        return! http.SendAsync(request)
    }

let private initialize id =
    sprintf """{"jsonrpc":"2.0","id":%d,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"quota-probe","version":"1"}}}""" id

let private responseJson (body: string) =
    let trimmed = body.TrimStart()
    let json =
        if trimmed.StartsWith("{", StringComparison.Ordinal) then trimmed
        else
            body.Split('\n')
            |> Array.map (fun line -> line.Trim())
            |> Array.find (fun line -> line.StartsWith("data: {", StringComparison.Ordinal))
            |> fun line -> line.Substring(5).Trim()
    JsonDocument.Parse(json)

[<Test>]
let authenticated_mcp_http_accepts_more_than_240_rapid_requests_without_synthetic_429 () =
    task {
        let! instance, endpoint = startServer ()
        use app = instance
        use http = new HttpClient()
        http.Timeout <- TimeSpan.FromSeconds(15.0)

        // Eight controlled batches prevent unbounded test connections, yet
        // exceed the retired 240/10s subject window on loopback.
        let startedAt = System.Diagnostics.Stopwatch.StartNew()
        for batch in 0 .. 7 do
            let requests =
                [| for offset in 1 .. 32 ->
                    postMcp http endpoint (initialize (batch * 32 + offset)) |]
            let! responses = Task.WhenAll(requests)
            for response in responses do
                use response = response
                let! body = response.Content.ReadAsStringAsync()
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
                Assert.That(body, Does.Not.Contain("capacity is busy"))
        startedAt.Stop()
        Assert.That(startedAt.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10.0)),
                    "The burst must fit within the retired 10-second subject window to be meaningful.")

        // /client and /exports still share the original fixed-window IP
        // protection; no MCP request consumed an HTTP limiter permit.
        use! clientResponse = http.GetAsync(endpoint + "/client")
        Assert.That(clientResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        for _ in 1 .. 29 do
            use! allowed = http.GetAsync(endpoint + "/exports/test")
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        // The live policy has a queue of five: a lone excess request
        // waits for replenishment rather than receiving 429. Overflow it
        // using eight bounded requests and cancel any queued survivors.
        use overflowCancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2.0))
        let overflow =
            [| for _ in 1 .. 8 ->
                task {
                    try
                        use! response = http.GetAsync(endpoint + "/exports/test", overflowCancellation.Token)
                        return response.StatusCode, response.Headers.RetryAfter <> null
                    with :? OperationCanceledException ->
                        return HttpStatusCode.RequestTimeout, false
                } |]
        let! outcomes = Task.WhenAll(overflow)
        Assert.That(outcomes |> Array.exists (fun (status, retry) ->
            status = HttpStatusCode.TooManyRequests && retry), Is.True,
            "Unrelated /client and /exports protection must still return 429 with Retry-After.")
    }

[<Test>]
let more_than_40_simultaneous_authenticated_mcp_tool_calls_enter_normal_handlers () =
    task {
        ProbeState.reset()
        let! instance, endpoint = startServer ()
        use app = instance
        use handler = new SocketsHttpHandler(MaxConnectionsPerServer = 60)
        use http = new HttpClient(handler)
        http.Timeout <- TimeSpan.FromSeconds(20.0)

        // Discover the real SDK-generated name instead of duplicating the
        // tool naming convention in the test.
        use! listing = postMcp http endpoint """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"""
        let! listedBody = listing.Content.ReadAsStringAsync()
        Assert.That(listing.StatusCode, Is.EqualTo(HttpStatusCode.OK), listedBody)
        use document = responseJson listedBody
        let name =
            document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("name").GetString())
            |> Seq.find (fun value -> value.Contains("hold", StringComparison.OrdinalIgnoreCase))

        let count = 48
        let responses =
            [| for id in 1 .. count ->
                let payload =
                    sprintf """{"jsonrpc":"2.0","id":%d,"method":"tools/call","params":{"name":"%s","arguments":{"callId":%d}}}""" id name id
                postMcp http endpoint payload |]
        try
            // Without the old 24 active / 16 queued cap, every handler can
            // enter while the same gate holds all 48 calls in flight.
            let mutable attempts = 0
            while ProbeState.entered.Count < count && attempts < 300 do
                attempts <- attempts + 1
                do! Task.Delay(25)
            Assert.That(ProbeState.entered.Count, Is.EqualTo(count),
                        "All tool handlers should begin before any held call is released.")
        finally
            ProbeState.gate.TrySetResult(()) |> ignore

        let! finished = Task.WhenAll(responses)
        for response in finished do
            use response = response
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(body, Does.Not.Contain("capacity is busy"))
            use result = responseJson body
            Assert.That(result.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Object))
        Assert.That(ProbeState.entered.Count, Is.EqualTo(count))
    }
