module RegisteredReviewerQueueProjection20261127

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
let actual_registered_reviewer_inbox_preserves_ledger_and_legacy_queue () = task {
    Assert.That(OperatingSystem.IsLinux(), Is.True)
    let root = Path.Combine(Path.GetTempPath(), "jarvis-reviewer-queue-registration-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let inheritedGit =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<DictionaryEntry>
        |> Seq.map (fun entry -> string entry.Key)
        |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.Ordinal))
        |> Seq.toList
    let originalEnvironment =
        (["XDG_CONFIG_HOME"; "GIT_CONFIG_NOSYSTEM"; "GIT_CONFIG_GLOBAL"; "JARVIS_TOOL_JOB_THRESHOLD_MS"] @ inheritedGit)
        |> List.distinct |> List.map (fun key -> key, Environment.GetEnvironmentVariable(key))
    try
        for key in inheritedGit do Environment.SetEnvironmentVariable(key, null)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1")
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig"))
        Environment.SetEnvironmentVariable("JARVIS_TOOL_JOB_THRESHOLD_MS", "250")
        let first, second, _, _, _ = create root
        let reviewer = "Reviewer fixture"
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) reviewer |> success |> ignore
        Client.RoleInstructions.RoleInstructions.createRole first "fixture" (Guid.NewGuid()) "Reviewer" "Independent" "Inspect frozen source"
        |> success |> ignore
        Client.RoleInstructions.RoleInstructions.attachRole first "fixture" (Guid.NewGuid()) reviewer "Reviewer" 0 |> success |> ignore
        let source = git first.RepositoryPath ["rev-parse"; "HEAD"]
        let submit owner =
            let id = Guid.NewGuid()
            ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) "registered-review" owner id "" source "Exact evidence" "Independent" ""
            |> success |> ignore
            id
        let own = submit reviewer
        let eligible = submit "Worker"
        let claimed = submit "Worker"
        ReviewWorkflow.claimBranchReview first (Guid.NewGuid()) "registered-review" claimed reviewer 1 |> success |> ignore
        let terminalReview = submit "Worker"
        ReviewWorkflow.withdrawBranchReview first (Guid.NewGuid()) "registered-review" terminalReview "Worker" 1 |> success |> ignore
        // Synthetic scale records use the real schema; evidence for the
        // four actual publications above is separately authenticated.
        let template = (ReviewWorkflow.getBranchReview first "registered-review" eligible |> success).Review
        let synthetic =
            [ for n in 1 .. 300 ->
                let id = (Guid(n, 0s, 0s, Array.zeroCreate<byte> 8)).ToString("N")
                let task = sprintf "registered-bulk-%02d" (n % 11)
                let owner, status, claim =
                    match n % 6 with
                    | 0 | 1 -> reviewer, "Unclaimed", null
                    | 2 | 3 -> "Worker", "Unclaimed", null
                    | 4 -> "Worker", "Claimed", reviewer
                    | _ -> "Worker", "Withdrawn", null
                { template with ReviewId = id; TaskId = task; OwnerAgentId = owner
                                ReviewRef = $"refs/heads/jarvis-review/{task}/{id}/r1"
                                Status = status; ReviewerId = claim
                                Version = if status = "Unclaimed" then 1 else 2
                                CreatedSequence = 100L + int64 (n / 5) } ]
        let intent : LedgerIntent =
            { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.RegisteredQueue"; RequestJson = "{}" }
        let changes =
            synthetic |> List.map (fun row ->
                let raw = JsonSerializer.Serialize(row, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
                { Path = $"tasks/{row.TaskId}/reviews/{row.ReviewId}/review.json"
                  ExpectedBlobOid = None; Content = "{\"schemaVersion\":3," + raw.Substring(1) })
        LedgerStore.transact first intent (fun _ -> Ok changes)
        |> success |> ignore
        let before = (LedgerStore.fetch second |> success).Commit
        let issuer, audience = "urn:jarvis:offline:reviewer-queue", "urn:jarvis:offline"
        let key = SymmetricSecurityKey(Encoding.UTF8.GetBytes("disposable-reviewer-queue-registration-signing-key"))
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
            // The inspected main revision does not expose registerClientRuntime.
            // Recreate only its bounded ReceiveCommand callback with the actual
            // dispatch queue and production receive/reply core; no fake payload.
            let dispatch = Client.SignalR.CommandDispatchQueue(8, 128, ignore)
            use callback = hub.On<string, string, string>("ReceiveCommand",
                Func<string, string, string, Task>(fun correlation json actor ->
                    let submitted = dispatch.TrySubmit(correlation,
                        (fun () -> Client.SignalR.Client.receiveCommandAndReply hub rt correlation json actor),
                        (fun _ -> (task {
                            let response = Client.SignalR.CommandDispatchRecovery.unexpectedResponse json
                            do! hub.SendCoreAsync("SendClientResponse", [| box correlation; box response |])
                        } :> Task)))
                    if not submitted then failwith "Disposable dispatch queue unexpectedly full"
                    Task.CompletedTask))
            try
                do! hub.StartAsync()
                let registration : Common.SignalR.ClientRegistration =
                    { DeviceId = "reviewer-fixture"; DeviceName = "reviewer-fixture"
                      ProtocolVersion = Common.AgentProtocol.version; ClientVersion = "fixture"
                      SupportedCommands = Common.AgentProtocol.commandDefinitions |> List.map (fun d -> d.Name) }
                let! accepted = hub.InvokeAsync<Common.SignalR.ClientRegistrationResult>("Connect", registration)
                Assert.That(accepted.EnabledCommands.Length, Is.GreaterThan(0))
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
                let queueArgs = box {| projectName = "Fixture"; agentId = reviewer; deviceId = "reviewer-fixture" |}
                let! denied, _, _ = call "client:connect" "reviewer_inbox" queueArgs
                Assert.That(denied, Is.True, "workspace:read must be required")
                let terminal () = task {
                    let! failed, message, outer = call "workspace:read" "reviewer_inbox" queueArgs
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
                                    (box {| projectName = "Fixture"; agentId = reviewer; deviceId = "reviewer-fixture"; jobId = jobId; afterSequence = Nullable<int64>() |})
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
                        | None -> return failwith "ReviewerInbox promoted job did not reach terminal result"
                }
                let! raw, actualOuter, route = terminal ()
                use doc = JsonDocument.Parse raw
                let rows = prop "reviews" doc.RootElement
                let ids = rows.EnumerateArray() |> Seq.map (fun row -> (prop "ReviewId" row).GetString()) |> Seq.toList
                let expected =
                    ([own; eligible; claimed] |> List.map (fun id -> id.ToString("N")))
                    @ (synthetic |> List.filter (fun r -> r.Status <> "Withdrawn") |> List.sortBy (fun r -> r.CreatedSequence, r.TaskId, r.ReviewId) |> List.map (fun r -> r.ReviewId))
                Assert.That(ids, Is.EqualTo(box expected))
                Assert.That(ids, Does.Not.Contain(terminalReview.ToString("N")))
                Assert.That((prop "snapshotCommit" doc.RootElement).GetString(), Is.EqualTo(before))
                TestContext.Progress.WriteLine($"REGISTERED reviewer_inbox route={route} reviews={ids.Length} raw={bytes raw} actualHttpBody={actualOuter}")
                Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(before))
            finally
                dispatch.Complete().GetAwaiter().GetResult()
                hub.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            app.StopAsync().GetAwaiter().GetResult()
    finally
        for key, value in originalEnvironment do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)
}
