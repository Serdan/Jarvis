module AutomationHealthHosted20261123
open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Claims
open System.IdentityModel.Tokens.Jwt
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Microsoft.IdentityModel.Tokens
open Microsoft.IdentityModel.Protocols
open Microsoft.IdentityModel.Protocols.OpenIdConnect
open Microsoft.AspNetCore.SignalR
open Common
open Common.SignalR
open NUnit.Framework
open Microsoft.Data.Sqlite
open global.Client
open global.Client.LedgerStore
open global.Client.NativeHealth
open global.Client.NativeHealth.NativeHealth
open global.Client.ReviewWorkflow
open AutomationHealthHostedFixture

[<Test>]
let registered_loopback_clients_and_sqlite_ingress_must_be_composed () =
    task {
        let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-health-hosted-" + Guid.NewGuid().ToString("N"))
        System.IO.Directory.CreateDirectory(root) |> ignore
        let issuer = "urn:jarvis:offline:health"
        let audience = "urn:jarvis:offline"
        let key = SymmetricSecurityKey(Encoding.UTF8.GetBytes("offline-health-hosted-test-signing-key-256-bits-long-20261123"))
        let token scopes sub =
            let claims = [ Claim("sub", sub); Claim("scope", scopes) ]
            let jwt = JwtSecurityToken(issuer, audience, claims,
                                       DateTime.UtcNow.AddMinutes(-1.),
                                       DateTime.UtcNow.AddMinutes(15.),
                                       SigningCredentials(key, SecurityAlgorithms.HmacSha256))
            JwtSecurityTokenHandler().WriteToken(jwt)
        try
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
            builder.Configuration.AddInMemoryCollection(
                [ KeyValuePair("Auth0Domain", issuer)
                  KeyValuePair("Audience", audience)
                  KeyValuePair("DatabasePath", System.IO.Path.Combine(root, "telemetry.sqlite")) ]) |> ignore
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
            use http = new HttpClient()
            let! unauthorized = http.PostAsync(address + "/client/negotiate?negotiateVersion=1", new StringContent(""))
            Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
            use wrongScope = new HttpRequestMessage(HttpMethod.Post, address + "/client/negotiate?negotiateVersion=1")
            wrongScope.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token "workspace:read" "alice")
            use! wrongScopeResult = http.SendAsync(wrongScope)
            Assert.That(wrongScopeResult.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
                        "Authenticated JWT without client:connect must not negotiate SignalR.")
            let connect user device scopes =
                let jwt = token scopes user
                let hub =
                    HubConnectionBuilder()
                        .WithUrl(address + "/client", Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>(fun o ->
                            o.AccessTokenProvider <- Func<Task<string>>(fun () -> Task.FromResult jwt)))
                        .Build()
                hub, device
            let one, _ = connect "alice" "a" "client:connect"
            let two, _ = connect "alice" "b" "client:connect"
            try
                do! one.StartAsync()
                do! two.StartAsync()
                let registration device =
                    { DeviceId=device; DeviceName=device; ProtocolVersion=AgentProtocol.version
                      ClientVersion="offline-hosted-fixture"; SupportedCommands=[] }
                let! a = one.InvokeAsync<ClientRegistrationResult>("Connect", registration "a")
                let! b = two.InvokeAsync<ClientRegistrationResult>("Connect", registration "b")
                Assert.That(a.Generation, Is.Not.EqualTo(b.Generation))
                let unavailable (hub: HubConnection) (project: string) (agent: string) (label: string) =
                    task {
                        let mutable rejected = false
                        try
                            let! _ = hub.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", project, agent)
                            ()
                        with :? HubException -> rejected <- true
                        Assert.That(rejected, Is.True, label)
                    }
                let unregistered, _ = connect "alice" "not-registered" "client:connect"
                try
                    do! unregistered.StartAsync()
                    do! unavailable unregistered "Fixture" "Worker" "Unregistered connection must be denied"
                finally
                    unregistered.DisposeAsync().AsTask().GetAwaiter().GetResult()
                let otherUser, _ = connect "bob" "bob-a" "client:connect"
                try
                    do! otherUser.StartAsync()
                    let! _ = otherUser.InvokeAsync<ClientRegistrationResult>("Connect", registration "bob-a")
                    do! unavailable otherUser "Fixture" "Worker" "Different user cannot read Alice's activity"
                finally
                    otherUser.DisposeAsync().AsTask().GetAwaiter().GetResult()
                // Genuine authenticated MCP agent tool ingress. A missing
                // test-only device hits the legitimate failed-routing branch,
                // where the qualifying call still counts as agent activity.
                let command = """{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"agent_inbox","arguments":{"projectName":"Fixture","agentId":"Worker","deviceId":"test-missing-device"}}}"""
                use request = new HttpRequestMessage(HttpMethod.Post, address + "/mcp")
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token "workspace:read" "alice")
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
                request.Content <- new StringContent(command, Encoding.UTF8, "application/json")
                use! response = http.SendAsync(request)
                let! body = response.Content.ReadAsStringAsync()
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
                Assert.That(body.Contains("device", StringComparison.OrdinalIgnoreCase),
                            Is.True, "MCP tool must reach production device routing: " + body)
                let mutable observed : AgentActivitySnapshot option = None
                let mutable attempts = 0
                while observed.IsNone && attempts < 40 do
                    attempts <- attempts + 1
                    try
                        let! value = one.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", "Fixture", "Worker")
                        observed <- Some value
                    with :? HubException ->
                        do! Task.Delay(50)
                Assert.That(observed.IsSome, Is.True, "No SQLite activity proof from registered MCP ingress.")
                let mutant =
                    String.Equals(Environment.GetEnvironmentVariable("JARVIS_HEALTH_HOSTED_NEGATIVE_MUTANT"),
                                  "1", StringComparison.Ordinal)
                let secondProject = if mutant then "Foreign" else "Fixture"
                let! other = two.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", secondProject, "Worker")
                Assert.That(observed.Value.LastSeenAtUtc.IsSome, Is.True)
                Assert.That(other.LastSeenAtUtc, Is.EqualTo(observed.Value.LastSeenAtUtc))
                do! unavailable one "Foreign" "Worker" "Foreign project must fail closed"
                do! unavailable two "Fixture" "Foreign" "Foreign agent must fail closed"

                // Genuine MCP observer must never refresh the same server coverage.
                let observerCommand = """{"jsonrpc":"2.0","id":43,"method":"tools/call","params":{"name":"list_jobs","arguments":{"projectName":"Fixture","agentId":"Worker","includeCompleted":false,"deviceId":"test-missing-device"}}}"""
                use observerRequest = new HttpRequestMessage(HttpMethod.Post, address + "/mcp")
                observerRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token "workspace:read" "alice")
                observerRequest.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
                observerRequest.Content <- new StringContent(observerCommand, Encoding.UTF8, "application/json")
                use! observerResponse = http.SendAsync(observerRequest)
                Assert.That(observerResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! observerBody = observerResponse.Content.ReadAsStringAsync()
                Assert.That(observerBody.Contains("device", StringComparison.OrdinalIgnoreCase), Is.True,
                            "Observer must reach authenticated device routing: " + observerBody)
                let! afterObserver = one.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", "Fixture", "Worker")
                Assert.That(afterObserver.LastSeenAtUtc, Is.EqualTo(observed.Value.LastSeenAtUtc),
                            "Observer-only authenticated MCP must not refresh activity.")
                Assert.That(other.ServerNowUtc, Is.GreaterThanOrEqualTo(other.LastSeenAtUtc.Value))

                // Two truly independent local runtime instances and Git clones
                // sharing one disposable canonical remote. All Activity callbacks
                // call registered server Hub endpoints, never synthesized snapshots.
                let first, second, firstRt, secondRt, candidate = create root
                let makeDeps (hub: HubConnection) (rt: Runtime) : Dependencies =
                    { Enumerate = (fun () -> Ok [candidate])
                      Current = NativeHealth.current rt
                      Activity = (fun project actor ->
                          task {
                              try
                                  let! live = hub.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", project, actor)
                                  return Ok live
                              with ex -> return Error ex.Message
                          })
                      Recover = NativeHealth.recover rt }
                let originalId = NativeHealth.recoveryOperationId candidate
                let strict = { Enabled = true; InactivitySeconds = 600 }
                let! notIdle = NativeHealth.checkOnce strict (makeDeps one firstRt)
                Assert.That(notIdle.Recoveries, Is.Zero, "Real server activity within 600s must hold.")
                let before = LedgerStore.fetch second |> success
                Assert.That(before.Commit, Is.EqualTo(candidate.GitHead))

                // One second is an explicitly test-only configured timeout,
                // NOT a claim that 600 real seconds elapsed on the host.
                do! Task.Delay(1250)
                let accelerated = { Enabled = true; InactivitySeconds = 1 }
                let firstCheck = NativeHealth.checkOnce accelerated (makeDeps one firstRt)
                let secondCheck = NativeHealth.checkOnce accelerated (makeDeps two secondRt)
                let! checks = Task.WhenAll([| firstCheck; secondCheck |])
                let _, receipt = LedgerStore.getOperationStatus second originalId |> success
                Assert.That(receipt.IsSome, Is.True, sprintf "Native recovery missing. Checks: %A" checks)
                Assert.That(receipt.Value.Type, Is.EqualTo("RecoverAgentAssignment"))
                let _, afterTask, afterAttempt = ReviewWorkflow.getTask second candidate.TaskId |> success
                Assert.That(afterAttempt.Value.AssignmentGeneration, Is.EqualTo(candidate.Generation + 1))
                Assert.That(afterAttempt.Value.AttemptId, Is.EqualTo(candidate.AttemptId))
                Assert.That(afterAttempt.Value.SpecBlobOid, Is.EqualTo(candidate.SpecBlobOid))
                Assert.That(afterAttempt.Value.SourceRef, Is.EqualTo(candidate.SourceRef))
                Assert.That(afterTask.TaskId, Is.EqualTo(candidate.TaskId))
                match NativeHealth.current secondRt candidate with
                | Ok None -> ()
                | other -> Assert.Fail(sprintf "Git fresh-read must reject recovered assignment: %A" other)
                let negativeId = Guid.NewGuid()
                match ReviewWorkflow.recoverAgentAssignmentWithTaskVersionAs second
                        "AuthenticatedUser" negativeId candidate.TaskId candidate.AgentId
                        candidate.TaskVersion candidate.AttemptVersion candidate.AgentVersion
                        candidate.Generation "Stale second writer" with
                | Error(StaleEntityVersion _) -> ()
                | outcome -> Assert.Fail(sprintf "Second independent stale Git CAS must lose: %A" outcome)

                // Intentional destructive control is test-owned SQLite ONLY,
                // after positive ingress and CAS. Never use direct SQLite to
                // manufacture a positive activity snapshot.
                use sqlite = new SqliteConnection("Data Source=" + System.IO.Path.Combine(root, "telemetry.sqlite"))
                sqlite.Open()
                use future = sqlite.CreateCommand()
                future.CommandText <- "UPDATE attributed_agent_activity SET last_seen_at='2099-01-01T00:00:00.0000000+00:00' WHERE user_id='alice' AND project_name='Fixture' AND agent_id='Worker';"
                Assert.That(future.ExecuteNonQuery(), Is.EqualTo(1))
                do! unavailable one "Fixture" "Worker" "Future SQL date must not certify Health inactivity"
                do! unavailable two "Fixture" "Worker" "Future date rejected by both real clients"
                use delete = sqlite.CreateCommand()
                delete.CommandText <- "DELETE FROM attributed_agent_activity WHERE user_id='alice' AND project_name='Fixture' AND agent_id='Worker';"
                Assert.That(delete.ExecuteNonQuery(), Is.EqualTo(1))
                do! unavailable one "Fixture" "Worker" "SQLite row deletion must fail closed"
                do! unavailable two "Fixture" "Worker" "Both clients must fail closed after SQLite row deletion"
            finally
                one.DisposeAsync().AsTask().GetAwaiter().GetResult()
                two.DisposeAsync().AsTask().GetAwaiter().GetResult()
                app.StopAsync().GetAwaiter().GetResult()
        finally
            try System.IO.Directory.Delete(root, true) with _ -> ()
    }