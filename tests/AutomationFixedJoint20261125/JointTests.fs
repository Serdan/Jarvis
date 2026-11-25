module AutomationFixedJointTests
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

open System.IO
open Client.ChallengeWorkflow
open Client.RoleInstructions
open AutomationFixedJointBrowser

[<Test; NonParallelizable>]
let authenticatedHealthAndChallengeWakeReachRecurringScheduler () =
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
        let priorConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let first, second, firstRt, secondRt, _ = create root
            RoleInstructions.setLaunchTemplate first "fixture" (Guid.NewGuid()) 0
                "Agent {{agentId}}, project {{projectName}}. Call inbox." |> success |> ignore
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

                let! other = two.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", "Fixture", "Worker")
                Assert.That(observed.Value.LastSeenAtUtc.IsSome, Is.True)
                Assert.That(other.LastSeenAtUtc, Is.EqualTo(observed.Value.LastSeenAtUtc))
                do! unavailable one "Foreign" "Worker" "Foreign project cannot authorize recovery"

                // Production roster, native recheck and mutation, authenticated activity.
                let makeDeps (hub: HubConnection) (rt: Runtime) : Dependencies =
                    { Enumerate = fun () -> NativeHealth.enumerate rt
                      Current = NativeHealth.current rt
                      Activity = fun project agent -> task {
                          try
                              let! live = hub.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", project, agent)
                              return Ok live
                          with ex -> return Error ex.Message
                      }
                      Recover = NativeHealth.recover rt }
                let candidates = NativeHealth.enumerate firstRt |> success
                Assert.That(candidates.Length, Is.EqualTo(1))
                let candidate = candidates.Head
                Assert.That(candidate.AgentId, Is.EqualTo("Worker"))
                let originalId = NativeHealth.recoveryOperationId candidate
                let strict = { Enabled = true; InactivitySeconds = 600 }
                let! held = NativeHealth.checkOnce strict (makeDeps one firstRt)
                Assert.That(held.Recoveries, Is.Zero)
                Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(candidate.GitHead))
                // Hosted time is not injectable; use an explicitly accelerated
                // timeout here, and separate deterministic decision cases below.
                do! Task.Delay(1250)
                let accelerated = { Enabled = true; InactivitySeconds = 1 }
                let! checks = Task.WhenAll([| NativeHealth.checkOnce accelerated (makeDeps one firstRt)
                                              NativeHealth.checkOnce accelerated (makeDeps two secondRt) |])
                let _, receipt = LedgerStore.getOperationStatus second originalId |> success
                Assert.That(receipt.IsSome, Is.True, sprintf "Recovery missing: %A" checks)
                Assert.That(receipt.Value.Type, Is.EqualTo("RecoverAgentAssignment"))
                let _, _, recovered = ReviewWorkflow.getTask second candidate.TaskId |> success
                Assert.That(recovered.Value.AssignmentGeneration, Is.EqualTo(candidate.Generation + 1))
                Assert.That(recovered.Value.AttemptId, Is.EqualTo(candidate.AttemptId))
                Assert.That(recovered.Value.SpecBlobOid, Is.EqualTo(candidate.SpecBlobOid))
                Assert.That(recovered.Value.SourceRef, Is.EqualTo(candidate.SourceRef))
                match ReviewWorkflow.recoverAgentAssignmentWithTaskVersionAs second "AuthenticatedUser"
                        (Guid.NewGuid()) candidate.TaskId candidate.AgentId candidate.TaskVersion
                        candidate.AttemptVersion candidate.AgentVersion candidate.Generation "Stale contender" with
                | Error(StaleEntityVersion _) -> ()
                | result -> Assert.Fail(sprintf "Stale contender must lose: %A" result)
                let agent id = (ReviewWorkflow.getAgentInbox second id |> success).Agent
                Assert.That((agent "Worker").State, Is.EqualTo("Ready"))
                let launchRt = Runtime(Path.GetDirectoryName(first.RepositoryPath), ConsoleTui.ConsoleTui(), Client.FullTrust)
                let taskId = candidate.TaskId
                let register id = ReviewWorkflow.registerAgent second (Guid.NewGuid()) id |> success |> ignore
                for id in ["Manual"; "Lead"] do register id
                let startManual () =
                    ReviewWorkflow.startTasklessWork second (Guid.NewGuid()) "Manual" (agent "Manual").Version "Manual occupancy"
                    |> success |> ignore
                let stopManual () =
                    ReviewWorkflow.yieldTasklessWork second (Guid.NewGuid()) "Manual" (agent "Manual").Version "Naturally drained"
                    |> success |> ignore
                let changePolicy version total roles =
                    ProjectCapacityPolicy.updateOptional second "fixture" (Guid.NewGuid()) version total roles |> success |> ignore
                RoleInstructions.createRole second "fixture" (Guid.NewGuid()) "Worker" "Implementation" "Implement"
                |> success |> ignore
                for id in ["Manual"; "Worker"] do
                    RoleInstructions.attachRole second "fixture" (Guid.NewGuid()) id "Worker" 0 |> success |> ignore

                do! withScheduler root launchRt "Fixture" first.RepositoryPath (fun noEffect send refuseManual -> task {
                    noEffect() |> ignore
                    startManual()
                    do! refuseManual "Manual" "Worker"
                    stopManual()
                    ProjectCapacityPolicy.updateAutomation second "fixture" (Guid.NewGuid()) 0 true None Map.empty |> success |> ignore
                    // Absent caps allow an actual recurring scheduled resume of
                    // precisely the recovered episode.
                    let readyBefore () =
                        let snap = LedgerStore.fetch second |> success
                        let prepared = ScheduledReadyResume.prepareAt ScheduledReadyResume.readCurrentLimits second snap "Fixture" |> success
                        prepared |> Option.defaultWith (fun () -> failwith "Expected exact eligible Ready episode")
                    let verifyIntent (prepared: ScheduledReadyResume.Prepared) =
                        let snapshot = LedgerStore.fetch second |> success
                        let op = LedgerStore.getOperationAt second snapshot prepared.OperationId |> success
                        Assert.That(op.IsSome, Is.True, "Original Ready intent not durably recorded.")
                        Assert.That(op.Value.Type, Is.EqualTo("ScheduledReadyResumeIntent"))
                        let path = sprintf "records/scheduled-ready-resume/%s/intent.json" (prepared.OperationId.ToString("N"))
                        let content, _ = LedgerStore.readAt second snapshot path |> success
                        use json = System.Text.Json.JsonDocument.Parse(content.Value)
                        let ready = json.RootElement.GetProperty("ready")
                        Assert.That(ready.GetProperty("AgentId").GetString(), Is.EqualTo(prepared.Ready.AgentId))
                        Assert.That(ready.GetProperty("Generation").GetInt32(), Is.EqualTo(prepared.Ready.Generation))
                        Assert.That(ready.GetProperty("AttemptId").GetString(), Is.EqualTo(prepared.Ready.AttemptId))
                        Assert.That(ready.GetProperty("StateSequence").GetInt64(), Is.EqualTo(prepared.Ready.StateSequence))
                        Assert.That(json.RootElement.GetProperty("promptSha256").GetString(), Is.EqualTo(prepared.Rendered.PromptSha256))
                    let healthReady = readyBefore()
                    Assert.That(healthReady.Ready.Generation, Is.EqualTo(recovered.Value.AssignmentGeneration))
                    let! submittedError = send "Agent Worker, project Fixture. Call inbox." "health-tab" "submitted"
                    Assert.That(submittedError, Is.EqualTo("completed: "))
                    verifyIntent healthReady
                    Assert.That((agent "Worker").State, Is.EqualTo("Ready"), "Browser effect alone does not activate the assignment.")
                    noEffect() |> ignore
                    ReviewWorkflow.activateReadyAgent second (Guid.NewGuid()) taskId "Worker" |> success |> ignore

                    let _, task, attempt = ReviewWorkflow.getTask second taskId |> success
                    let challenge =
                        ChallengeWorkflow.create second (Guid.NewGuid()) "Worker" "Wait for gate" taskId
                            [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Gate" } ]
                        |> success
                    ChallengeWorkflow.blockAttempt second (Guid.NewGuid()) "Worker" taskId attempt.Value.AttemptId
                        task.Version attempt.Value.Version (agent "Worker").Version attempt.Value.AssignmentGeneration
                        "Waiting" [challenge.ChallengeId] |> success |> ignore
                    noEffect() |> ignore
                    ChallengeWorkflow.resolve second (Guid.NewGuid()) "Lead" challenge.ChallengeId
                        challenge.ConditionIds.Head 1 "Passed" "" |> success |> ignore
                    let _, _, requeued = ReviewWorkflow.getTask second taskId |> success
                    Assert.That(requeued.Value.AssignmentGeneration, Is.EqualTo(recovered.Value.AssignmentGeneration + 1))
                    Assert.That((agent "Worker").State, Is.EqualTo("Ready"))

                    startManual()
                    changePolicy 1 (Some 1) Map.empty
                    noEffect() |> ignore
                    // Real manual command bypasses the cap; fake extension
                    // refuses the open, so no live effect or Ready consumption.
                    do! refuseManual "Manual" "Worker"
                    Assert.That((agent "Manual").Execution, Is.EqualTo("Working"))
                    changePolicy 2 None (Map.ofList ["Worker", 1])
                    noEffect() |> ignore
                    changePolicy 3 (Some 2) (Map.ofList ["Worker", 1])
                    noEffect() |> ignore
                    stopManual()

                    ReviewWorkflow.queueResponseAlert second (Guid.NewGuid()) "fixture" "Worker" "STOP this session"
                    |> success |> ignore
                    noEffect() |> ignore
                    let alert = (ReviewWorkflow.getResponseAlerts second "Worker" |> success).Pending |> List.exactlyOne
                    ReviewWorkflow.clearResponseAlert second (Guid.NewGuid()) "fixture" "Worker" (Guid.Parse alert.AlertId)
                    |> success |> ignore
                    let challengeReady = readyBefore()
                    Assert.That(challengeReady.Ready.Generation, Is.EqualTo(requeued.Value.AssignmentGeneration))
                    Assert.That(challengeReady.OperationId, Is.Not.EqualTo(healthReady.OperationId))
                    let! challengeResult = send "Agent Worker, project Fixture. Call inbox." "challenge-tab" "submitted"
                    verifyIntent challengeReady
                    Assert.That(challengeResult, Is.EqualTo("completed: "))
                    ReviewWorkflow.activateReadyAgent second (Guid.NewGuid()) taskId "Worker" |> success |> ignore
                    // Independent ordinary Ready work, with explicitly disabled caps.
                    changePolicy 4 None Map.empty
                    register "Ordinary"
                    ReviewWorkflow.createTask second (Guid.NewGuid()) "ordinary-task" "Ordinary" |> success |> ignore
                    ReviewWorkflow.assignTask second (Guid.NewGuid()) "ordinary-task" (Guid.NewGuid()) "Ordinary" |> success |> ignore
                    let ordinary = readyBefore()
                    Assert.That(ordinary.Ready.AgentId, Is.EqualTo("Ordinary"))
                    let! error = send "Agent Ordinary, project Fixture. Call inbox." "ordinary-tab" "outcomeUnknown"
                    verifyIntent ordinary
                    Assert.That(error, Does.Contain("OutcomeUnknown"))
                    let final = LedgerStore.fetch second |> success
                    let paths = git second.RepositoryPath ["ls-tree"; "-r"; "--name-only"; final.Commit; "--"; "records/scheduled-ready-resume"]
                    Assert.That(paths.Split('\n') |> Array.filter (fun path -> path.EndsWith("/intent.json")) |> Array.length, Is.EqualTo(3))
                    noEffect() |> ignore
                    let after = LedgerStore.fetch second |> success
                    Assert.That(after.Commit, Is.EqualTo(final.Commit), "Unknown occurrence must not replay or republish.")
                })
            finally
                one.DisposeAsync().AsTask().GetAwaiter().GetResult()
                two.DisposeAsync().AsTask().GetAwaiter().GetResult()
                app.StopAsync().GetAwaiter().GetResult()
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", priorConfig)
            Directory.Delete(root, true)
    }

// Separate deterministic boundary test at the actual NativeHealth decision
// seam. The observation clock is controlled here, not in the hosted endpoint.
[<TestCase(599, false)>]
[<TestCase(600, true)>]
[<TestCase(601, true)>]
let exactBoundaryOnDisposableCanonicalLedger age shouldRecover = task {
    let root = Path.Combine(Path.GetTempPath(), "jarvis-fixed-boundary-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let first, second, rt, _, _ = create root
        let candidate = NativeHealth.enumerate rt |> success |> List.exactlyOne
        let last = DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)
        let observation = { LastSeenAtUtc = Some last; ServerNowUtc = last.AddSeconds(float age) }
        let deps : Dependencies =
            { Enumerate = fun () -> NativeHealth.enumerate rt
              Current = NativeHealth.current rt
              Activity = fun _ _ -> Task.FromResult(Ok observation)
              Recover = NativeHealth.recover rt }
        let! result = NativeHealth.checkOnce { Enabled = true; InactivitySeconds = 600 } deps
        Assert.That(result.Recoveries, Is.EqualTo(if shouldRecover then 1 else 0))
        let _, _, after = ReviewWorkflow.getTask second candidate.TaskId |> success
        Assert.That(after.Value.AssignmentGeneration, Is.EqualTo(candidate.Generation + if shouldRecover then 1 else 0))
        let _, receipt = LedgerStore.getOperationStatus first (NativeHealth.recoveryOperationId candidate) |> success
        Assert.That(receipt.IsSome, Is.EqualTo(shouldRecover))
    finally
        Directory.Delete(root, true)
}
