module RegisteredTaskMetadataProjection20261127

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
open NUnit.Framework
open Client
open Client.LedgerStore
open Client.ReviewWorkflow
open AutomationHealthHostedFixture

let bytes (value: string) = Encoding.UTF8.GetByteCount value
let prop name (value: JsonElement) = value.GetProperty(name: string)

[<Test; NonParallelizable>]
let actual_registered_get_task_reads_legacy_body_and_preserves_ledger () = task {
    Assert.That(OperatingSystem.IsLinux(), Is.True)
    let root = Path.Combine(Path.GetTempPath(), "jarvis-task-metadata-registration-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let inheritedGit =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<DictionaryEntry>
        |> Seq.map (fun entry -> string entry.Key)
        |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.Ordinal))
        |> Seq.toList
    let originalEnvironment =
        (["XDG_CONFIG_HOME"; "GIT_CONFIG_NOSYSTEM"; "GIT_CONFIG_GLOBAL"] @ inheritedGit)
        |> List.distinct |> List.map (fun key -> key, Environment.GetEnvironmentVariable(key))
    try
        for key in inheritedGit do Environment.SetEnvironmentVariable(key, null)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1")
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig"))
        let first, second, _, _, _ = create root
        let makeBody size = "<article><p>" + String.replicate (size - bytes "<article><p></p></article>") "x" + "</p></article>"
        let cases = ["lead-size", makeBody 8192; "skew-size", makeBody (256 * 1024)]
        for id, body in cases do
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) id "Registered task" body |> success |> ignore
        let before = (LedgerStore.fetch second |> success).Commit
        let issuer, audience = "urn:jarvis:offline:task-metadata", "urn:jarvis:offline"
        let key = SymmetricSecurityKey(Encoding.UTF8.GetBytes("disposable-task-metadata-registration-signing-key"))
        let token scopes =
            let claims = [Claim("sub", "alice"); Claim("scope", scopes)]
            JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow.AddMinutes(-1.),
                             DateTime.UtcNow.AddMinutes(15.), SigningCredentials(key, SecurityAlgorithms.HmacSha256))
            |> JwtSecurityTokenHandler().WriteToken
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
        try
            let address = app.Urls |> Seq.exactlyOne
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds(30.))
            let hub =
                HubConnectionBuilder()
                    .WithUrl(address + "/client", Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>(fun options ->
                        options.AccessTokenProvider <- Func<Task<string>>(fun () -> Task.FromResult(token "client:connect"))))
                    .Build()
            let tui = ConsoleTui.ConsoleTui()
            let rt = Runtime(Path.GetDirectoryName(first.RepositoryPath), tui, FullTrust)
            let actor =
                SHA256.HashData(Encoding.UTF8.GetBytes("alice"))
                |> Convert.ToHexString |> fun s -> "operator:" + s.ToLowerInvariant()
            // Production composition; no scheduler start, browser or synthetic
            // hub.On handler, and no authority outside this disposable fixture.
            use registration =
                Program.registerClientRuntime rt tui None (Path.Combine(root, "schedules.json"))
                    (fun () -> Ok ClientConfig.Disabled)
                    (fun _ -> failwith "Disabled native authority must not be opened")
                    (fun () -> failwith "Disabled native job root must not be opened")
                    (fun () -> hub, Some actor)
            try
                do! hub.StartAsync()
                let! accepted = registration.RegisterAsync "metadata-fixture" (fun () -> "metadata-fixture")
                Assert.That(accepted, Is.True)
                let mutable nextId = 0
                let call auth name (args: obj) = task {
                    nextId <- nextId + 1
                    let id = nextId
                    let json = JsonSerializer.Serialize({| jsonrpc = "2.0"; id = id; ``method`` = "tools/call"; ``params`` = {| name = name; arguments = args |} |})
                    use request = new HttpRequestMessage(HttpMethod.Post, address + "/mcp")
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token auth)
                    request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
                    request.Content <- new StringContent(json, Encoding.UTF8, "application/json")
                    use! response = http.SendAsync(request)
                    let! body = response.Content.ReadAsStringAsync()
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    let payload =
                        if response.Content.Headers.ContentType.MediaType = "text/event-stream" then
                            body.Split('\n') |> Array.find (fun line -> line.StartsWith("data: ", StringComparison.Ordinal)) |> fun line -> line.Substring(6)
                        else body
                    use envelope = JsonDocument.Parse(payload)
                    Assert.That((prop "id" envelope.RootElement).GetInt32(), Is.EqualTo(id))
                    let result = prop "result" envelope.RootElement
                    let failed = match result.TryGetProperty("isError") with true, v -> v.GetBoolean() | _ -> false
                    let message = (prop "text" ((prop "content" result).[0])).GetString()
                    return failed, message, bytes body
                }
                let taskArgs id = box {| projectName = "Fixture"; agentId = "Worker"; taskId = id; deviceId = "metadata-fixture" |}
                let! denied, _, _ = call "client:connect" "get_task" (taskArgs "lead-size")
                Assert.That(denied, Is.True, "workspace:read must be required")
                // Promotion is real. Only poll the same returned handle, never
                // repeat a GetTask to obtain its terminal result.
                let terminal id = task {
                    let! failed, message, outer = call "workspace:read" "get_task" (taskArgs id)
                    Assert.That(failed, Is.False, message)
                    use initial = JsonDocument.Parse message
                    match initial.RootElement.TryGetProperty("JobId") with
                    | false, _ -> return message, outer, "direct"
                    | true, handle ->
                        let jobId = handle.GetString()
                        let mutable result : (string * int) option = None
                        let deadline = DateTime.UtcNow.AddSeconds(25.)
                        while result.IsNone && DateTime.UtcNow < deadline do
                            let! failed, pollText, pollOuter =
                                call "workspace:read" "get_job_result"
                                    (box {| projectName = "Fixture"; agentId = "Worker"; deviceId = "metadata-fixture"; jobId = jobId; afterSequence = Nullable<int64>() |})
                            Assert.That(failed, Is.False, pollText)
                            use poll = JsonDocument.Parse pollText
                            let status = (prop "case" (prop "Status" poll.RootElement)).GetString()
                            if status = "ToolCompleted" then
                                let response = prop "ToolResponse" poll.RootElement
                                Assert.That((prop "Error" response).ValueKind, Is.EqualTo(JsonValueKind.Null))
                                result <- Some((prop "Result" response).GetString(), pollOuter)
                            else
                                Assert.That(status, Is.EqualTo("Running"), pollText)
                                do! Task.Delay(100)
                        match result with
                        | Some (raw, actualOuter) -> return raw, actualOuter, "promoted"
                        | None -> return failwith "GetTask promoted job did not reach terminal result"
                }
                for id, expected in cases do
                    let! raw, actualOuter, route = terminal id
                    use doc = JsonDocument.Parse raw
                    Assert.That((prop "specHtml" doc.RootElement).GetString(), Is.EqualTo(expected))
                    Assert.That((prop "snapshotCommit" doc.RootElement).GetString(), Is.EqualTo(before))
                    Assert.That((prop "attempt" doc.RootElement).ValueKind, Is.EqualTo(JsonValueKind.Null))
                    TestContext.Progress.WriteLine($"REGISTERED task={id} route={route} html={bytes expected} raw={bytes raw} actualHttpBody={actualOuter}")
                Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(before))
            finally
                registration.StopAsync().GetAwaiter().GetResult()
                hub.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            app.StopAsync().GetAwaiter().GetResult()
    finally
        for key, value in originalEnvironment do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)
}
