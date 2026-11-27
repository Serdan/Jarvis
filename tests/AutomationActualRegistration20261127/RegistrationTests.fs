module AutomationActualRegistration20261127

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Claims
open System.Security.Cryptography
open System.IdentityModel.Tokens.Jwt
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.IdentityModel.Tokens
open Microsoft.IdentityModel.Protocols
open Microsoft.IdentityModel.Protocols.OpenIdConnect
open Common
open Common.SignalR
open NUnit.Framework
open Client
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open AutomationHealthHostedFixture
open ActualRegistrationBrowser

[<Test; NonParallelizable>]
let actual_registered_routes_preserve_legacy_and_compiled_off_native_authority () = task {
    // The existing disposable Git fixture uses XDG_CONFIG_HOME for trust anchors.
    // Fail before touching anything on a host where this isolation is unavailable.
    Assert.That(OperatingSystem.IsLinux(), Is.True, "Linux disposable configuration required.")
    let root = Path.Combine(Path.GetTempPath(), "jarvis-actual-registration-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let controlled = [ "XDG_CONFIG_HOME"; "GIT_CONFIG_NOSYSTEM"; "GIT_CONFIG_GLOBAL" ]
    let inheritedGit =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<DictionaryEntry>
        |> Seq.map (fun entry -> string entry.Key)
        |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.Ordinal))
        |> Seq.toList
    let originalEnvironment =
        (controlled @ inheritedGit) |> List.distinct
        |> List.map (fun key -> key, Environment.GetEnvironmentVariable(key))
    for key in inheritedGit do Environment.SetEnvironmentVariable(key, null)
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
    Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1")
    Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig"))
    let issuer = "urn:jarvis:offline:registration"
    let audience = "urn:jarvis:offline"
    let key = SymmetricSecurityKey(Encoding.UTF8.GetBytes("actual-registration-local-test-only-signing-key-20261127"))
    let token (scopes: string) =
        let claims = [ Claim("sub", "alice"); Claim("scope", scopes) ]
        JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow.AddMinutes(-1.),
                         DateTime.UtcNow.AddMinutes(15.), SigningCredentials(key, SecurityAlgorithms.HmacSha256))
        |> JwtSecurityTokenHandler().WriteToken
    try
        let first, second, _, _, _ = create root
        let tui = ConsoleTui.ConsoleTui()
        let rt = Runtime(Path.GetDirectoryName(first.RepositoryPath), tui, FullTrust)
        let script = "async function execute(args) { return args; }"
        File.WriteAllText(Path.Combine(first.RepositoryPath, "fixed.js"), script)
        let launcher = "browser-scripts/chatgpt/launchAgent.js"
        let launcherPath = Path.Combine(first.RepositoryPath, launcher)
        Directory.CreateDirectory(Path.GetDirectoryName launcherPath) |> ignore
        File.Copy(Path.Combine(AppContext.BaseDirectory, "launchAgent.js"), launcherPath)
        RoleInstructions.setLaunchTemplate first "fixture" (Guid.NewGuid()) 0
            "Agent {{agentId}}, project {{projectName}}. Call inbox." |> success |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Ready" |> success |> ignore
        ReviewWorkflow.createTask first (Guid.NewGuid()) "ready-task" "Ready fixture task" |> success |> ignore
        ReviewWorkflow.assignTask first (Guid.NewGuid()) "ready-task" (Guid.NewGuid()) "Ready" |> success |> ignore
        ProjectCapacityPolicy.updateAutomation first "fixture" (Guid.NewGuid()) 0 true None Map.empty |> success |> ignore

        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Configuration.AddInMemoryCollection(
            [ KeyValuePair("Auth0Domain", issuer)
              KeyValuePair("Audience", audience)
              KeyValuePair("DatabasePath", Path.Combine(root, "telemetry.sqlite")) ]) |> ignore
        Program.configureServices builder.Services builder.Configuration
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, fun options ->
            let metadata = OpenIdConnectConfiguration()
            metadata.Issuer <- issuer
            metadata.SigningKeys.Add(key)
            options.ConfigurationManager <- StaticConfigurationManager<OpenIdConnectConfiguration>(metadata)
            options.TokenValidationParameters.ValidIssuer <- issuer
            options.TokenValidationParameters.ValidateIssuer <- true
            options.TokenValidationParameters.ValidAudience <- audience
            options.TokenValidationParameters.ValidateAudience <- true
            options.TokenValidationParameters.IssuerSigningKey <- key
            options.TokenValidationParameters.ValidateIssuerSigningKey <- true
            options.TokenValidationParameters.ValidateLifetime <- true) |> ignore
        use app = builder.Build()
        Program.configureApp app |> ignore
        do! app.StartAsync()
        let address = app.Urls |> Seq.exactlyOne
        use http = new HttpClient(Timeout = TimeSpan.FromSeconds(25.))
        try
            use! unauthorized = http.PostAsync(address + "/client/negotiate?negotiateVersion=1", new StringContent(""))
            Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
            use wrong = new HttpRequestMessage(HttpMethod.Post, address + "/client/negotiate?negotiateVersion=1")
            wrong.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token "workspace:read")
            use! denied = http.SendAsync(wrong)
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let hub =
                HubConnectionBuilder()
                    .WithUrl(address + "/client", Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>(fun options ->
                        options.AccessTokenProvider <- Func<Task<string>>(fun () -> Task.FromResult(token "client:connect"))))
                    .Build()
            let scopes = "workspace:read process:execute git:write"
            let mutable requestId = 0
            let call (auth: string) (name: string) args = task {
                requestId <- requestId + 1
                let id = requestId
                let raw = JsonSerializer.Serialize({| jsonrpc="2.0"; id=id; ``method``="tools/call"; ``params``={| name=name; arguments=args |} |})
                use request = new HttpRequestMessage(HttpMethod.Post, address + "/mcp")
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token auth)
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
                request.Content <- new StringContent(raw, Encoding.UTF8, "application/json")
                use! response = http.SendAsync(request)
                let! body = response.Content.ReadAsStringAsync()
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
                let payload =
                    if response.Content.Headers.ContentType.MediaType = "text/event-stream" then
                        body.Split('\n') |> Array.find (fun line -> line.StartsWith("data: ", StringComparison.Ordinal)) |> fun line -> line.Substring(6)
                    else body
                use envelope = JsonDocument.Parse(payload)
                Assert.That(envelope.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(id))
                let result = envelope.RootElement.GetProperty("result")
                let isError = match result.TryGetProperty("isError") with true, v -> v.GetBoolean() | _ -> false
                return isError, result.GetProperty("content").[0].GetProperty("text").GetString()
            }
            let expectOk (pending: Task<bool * string>) = task {
                let! failed, message = pending
                Assert.That(failed, Is.False, message)
                return message
            }
            let mutable rootReads = 0
            let mutable bindingReads = 0
            let readRoot () =
                Interlocked.Increment(&rootReads) |> ignore
                Path.Combine(root, "native-wake-root")
            let readBinding _ =
                Interlocked.Increment(&bindingReads) |> ignore
                Ok first
            let actor = SHA256.HashData(Encoding.UTF8.GetBytes("alice")) |> Convert.ToHexString |> fun s -> "operator:" + s.ToLowerInvariant()
            do! withBrowser rt (fun bridge noFrame respond -> task {
                // This is the production composition, not a local set of hub.On handlers.
                use registration =
                    Program.registerClientRuntime rt tui (Some bridge) (Path.Combine(root, "schedules.json"))
                        (fun () -> Ok ClientConfig.Disabled) readBinding readRoot (fun () -> hub, Some actor)
                try
                    do! hub.StartAsync()
                    let! accepted = registration.RegisterAsync "local-fixture" (fun () -> "local-fixture")
                    Assert.That(accepted, Is.True, "Real production registration must negotiate the original commands.")
                    registration.StartScheduler()
                    let fixedArgs = {| projectName="Fixture"; agentId="Worker"; deviceId="local-fixture"; windowRegistration="agents"
                                       url="https://example.test/"; scriptPath="fixed.js"; argsJson="""{"proof":"fixed"}"""; closeTabAfterExecution=true; timeoutMs=5000 |}
                    let! forbidden, _ = call "workspace:read" "browser_run_in_new_tab" fixedArgs
                    Assert.That(forbidden, Is.True, "MCP must deny missing process:execute scope.")
                    Assert.That(noFrame(), Is.True)
                    let fixedCall = call scopes "browser_run_in_new_tab" fixedArgs
                    do! respond "fixed-tab" "submitted" (fun frame ->
                        Assert.That(frame.GetProperty("source").GetString(), Is.EqualTo(script))
                        Assert.That(frame.GetProperty("args").GetProperty("proof").GetString(), Is.EqualTo("fixed")))
                    let! _ = expectOk fixedCall

                    // Trigger actual authenticated Challenge commit and invalidation
                    // routes while the native host is compiled OFF. The Git receipt
                    // demonstrates this was not an unexecuted or mocked callback.
                    let operation = Guid.NewGuid()
                    let! _ = call scopes "create_challenge"
                                {| projectName="Fixture"; agentId="Worker"; deviceId="local-fixture"; title="Native OFF witness"
                                   operationId=operation.ToString("D"); conditions=[| {| ownerKind="Agent"; ownerAgentId="Ready"; suggestedAgentId=""; description="Local fixture gate" |} |] |}
                             |> expectOk
                    let _, receipt = LedgerStore.getOperationStatus second operation |> success
                    Assert.That(receipt.IsSome, Is.True, "Challenge commit must reach disposable Git through real registration.")
                    Assert.That(receipt.Value.Type, Is.EqualTo("CreateChallenge"))
                    use scope = app.Services.CreateScope()
                    let service = scope.ServiceProvider.GetRequiredService<Server.Services.ClientService>()
                    do! service.InvalidateAgentStatesForUser("alice", "Fixture", [|"Worker"|])
                    do! service.InvalidateAllAgentStatesForUser("alice")
                    do! Task.Delay(150)
                    Assert.That(Volatile.Read(&rootReads), Is.Zero, "Native OFF must never open the durable job root.")
                    Assert.That(Volatile.Read(&bindingReads), Is.Zero, "Native OFF must never read its authority binding.")
                    Assert.That(noFrame(), Is.True, "Native OFF must not dispatch the paired extension.")

                    let schedule id =
                        call scopes "schedule_browser_execution"
                            {| scheduleId=id; runAfterSeconds=5; url="https://chatgpt.com/g/g-p-fixture/project"; projectName="Fixture"
                               agentId="Worker"; deviceId="local-fixture"; scriptPath=launcher; argsJson="""{"resumeReady":true}"""
                               alias="agents"; windowRegistration="agents"; closeTabAfterExecution=true; repeatEverySeconds=60 |}
                        |> expectOk
                    let scheduler = rt.BrowserScheduler.Value
                    let state id =
                        use doc = JsonDocument.Parse(scheduler.Get(id) |> success)
                        let p key = doc.RootElement.GetProperty(key: string).GetString()
                        p "status", p "attemptId", p "error"
                    let terminal id =
                        waitUntil ("No terminal original schedule occurrence: " + id) (fun () ->
                            let status, attempt, _ = state id
                            status = "pending" && not (String.IsNullOrWhiteSpace attempt))
                    let! _ = call scopes "schedule_browser_execution"
                                {| scheduleId="fixed-once"; runAfterSeconds=5; url="https://example.test/"; projectName="Fixture"
                                   agentId="Worker"; deviceId="local-fixture"; scriptPath="fixed.js"; argsJson="""{"proof":"scheduled"}"""
                                   alias="agents"; windowRegistration="agents"; closeTabAfterExecution=true; repeatEverySeconds=0 |}
                             |> expectOk
                    do! respond "scheduled-tab" "submitted" (fun frame ->
                        Assert.That(frame.GetProperty("source").GetString(), Is.EqualTo(script))
                        Assert.That(frame.GetProperty("args").GetProperty("proof").GetString(), Is.EqualTo("scheduled")))
                    do! waitUntil "Legacy one-shot schedule did not complete" (fun () ->
                        let status, _, _ = state "fixed-once"
                        status = "completed")
                    ReviewWorkflow.queueResponseAlert first (Guid.NewGuid()) "fixture" "Ready" "STOP this session" |> success |> ignore
                    let! _ = schedule "stop"
                    do! terminal "stop"
                    Assert.That(noFrame(), Is.True, "Pending STOP must suppress the real scheduled callback.")
                    let! _ = call scopes "cancel_scheduled_browser_execution" {| scheduleId="stop"; agentId="Worker"; deviceId="local-fixture" |} |> expectOk
                    let alert = (ReviewWorkflow.getResponseAlerts first "Ready" |> success).Pending |> List.exactlyOne
                    ReviewWorkflow.clearResponseAlert first (Guid.NewGuid()) "fixture" "Ready" (Guid.Parse alert.AlertId) |> success |> ignore
                    let! _ = schedule "unknown"
                    do! respond "unknown-tab" "outcomeUnknown" (fun frame ->
                        Assert.That(frame.GetProperty("source").GetString(), Is.EqualTo(File.ReadAllText(launcherPath)))
                        let args = frame.GetProperty("args")
                        Assert.That(args.GetProperty("prompt").GetString(), Does.StartWith("Agent Ready, project Fixture. Call inbox."))
                        Assert.That(args.GetProperty("prompt").GetString(), Does.Contain("launchId"))
                        Assert.That(args.GetProperty("startUrl").GetString(), Is.EqualTo("https://chatgpt.com/g/g-p-fixture/project"))
                        Assert.That(args.EnumerateObject() |> Seq.length, Is.EqualTo(2)))
                    do! terminal "unknown"
                    let _, _, error = state "unknown"
                    Assert.That(error, Does.Contain("OutcomeUnknown"))
                    let after = LedgerStore.fetch second |> success
                    let intents = git second.RepositoryPath ["ls-tree"; "-r"; "--name-only"; after.Commit; "--"; "records/scheduled-ready-resume"]
                    Assert.That(intents.Split('\n') |> Array.filter (fun path -> path.EndsWith("/intent.json")) |> Array.length, Is.EqualTo(1))
                    Assert.That((ReviewWorkflow.getAgentInbox second "Ready" |> success).Agent.State, Is.EqualTo("Ready"))
                    // Force a new independent occurrence to encounter the original
                    // Unknown intent. The legacy route must not republish or resend.
                    let! _ = schedule "retry-held"
                    do! terminal "retry-held"
                    Assert.That(noFrame(), Is.True, "Unknown must not be replayed by another scheduled occurrence.")
                    Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(after.Commit))
                    Assert.That(Volatile.Read(&rootReads), Is.Zero)
                    Assert.That(Volatile.Read(&bindingReads), Is.Zero)
                finally
                    registration.StopAsync().GetAwaiter().GetResult()
                    hub.DisposeAsync().AsTask().GetAwaiter().GetResult()
            })
        finally
            app.StopAsync().GetAwaiter().GetResult()
    finally
        for key, value in originalEnvironment do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)
}
