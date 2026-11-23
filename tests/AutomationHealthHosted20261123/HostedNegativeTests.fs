module AutomationHealthHostedNegativeTests

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
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.IdentityModel.Tokens
open Microsoft.IdentityModel.Protocols
open Microsoft.IdentityModel.Protocols.OpenIdConnect
open Microsoft.Data.Sqlite
open NUnit.Framework
open Common
open Common.SignalR
open global.Client
open global.Client.LedgerStore
open global.Client.NativeHealth
open global.Client.NativeHealth.NativeHealth
open global.Client.ReviewWorkflow
open AutomationHealthHostedFixture

type private McpAuditStore(path: string) =
    let store = new Server.Services.OptionalFeedbackStore(path)
    member _.Store = store
    interface IDisposable with
        member _.Dispose() = (store :> IDisposable).Dispose()

[<Test>]
let hosted_negative_matrix_requires_real_coverage () =
    task {
        let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-health-matrix-" + Guid.NewGuid().ToString("N"))
        System.IO.Directory.CreateDirectory(root) |> ignore
        let databasePath = System.IO.Path.Combine(root, "telemetry.sqlite")
        let issuer = "urn:jarvis:offline:health-matrix"
        let audience = "urn:jarvis:offline"
        let key = SymmetricSecurityKey(Encoding.UTF8.GetBytes("offline-health-matrix-test-signing-key-256-bits-long-20261123"))
        let token scopes sub =
            let claims = [ Claim("sub", sub); Claim("scope", scopes) ]
            JwtSecurityToken(
                issuer, audience, claims, DateTime.UtcNow.AddMinutes(-1.),
                DateTime.UtcNow.AddMinutes(15.),
                SigningCredentials(key, SecurityAlgorithms.HmacSha256))
            |> JwtSecurityTokenHandler().WriteToken

        // Always use the genuine production registrations and mapped endpoints.
        // Recreating this DI container is the offline hosted-process restart seam:
        // it retains disk SQLite but cannot retain OptionalFeedbackStore's proof cache.
        let newHost () =
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
            builder.Configuration.AddInMemoryCollection(
                [ KeyValuePair("Auth0Domain", issuer)
                  KeyValuePair("Audience", audience)
                  KeyValuePair("DatabasePath", databasePath) ]) |> ignore
            Program.configureServices builder.Services builder.Configuration
            builder.Services.AddSingleton<McpAuditStore>(fun _ ->
                new McpAuditStore(System.IO.Path.Combine(root, "mcp-audit.sqlite"))) |> ignore
            builder.Services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                fun options ->
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
            let app = builder.Build()
            app.Use(Func<HttpContext, Func<Task>, Task>(fun context next ->
                task {
                    if context.Request.Path = PathString("/mcp") then
                        let services = context.RequestServices
                        // MCP operation diagnostics race the qualifying activity write
                        // for the same single-flight store. Route only the request's
                        // direct diagnostics lookup to a second disposable store.
                        // ClientService and HubService still resolve and share the
                        // Program-registered activity store through ordinary DI.
                        let audit = services.GetRequiredService<McpAuditStore>()
                        context.RequestServices <-
                            { new IServiceProvider with
                                member _.GetService(serviceType) =
                                    if serviceType = typeof<Server.Services.OptionalFeedbackStore> then box audit.Store
                                    else services.GetService(serviceType) }
                        try do! next.Invoke()
                        finally context.RequestServices <- services
                    else
                        do! next.Invoke()
                } :> Task)) |> ignore
            Program.configureApp app |> ignore
            app

        let connect address user =
            let jwt = token "client:connect" user
            HubConnectionBuilder()
                .WithUrl(address + "/client", Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>(fun o ->
                    o.AccessTokenProvider <- Func<Task<string>>(fun () -> Task.FromResult jwt)))
                .Build()
        let registration device =
            { DeviceId = device; DeviceName = device; ProtocolVersion = AgentProtocol.version
              ClientVersion = "offline-hosted-matrix"; SupportedCommands = [] }
        let register (hub: HubConnection) device =
            task {
                do! hub.StartAsync()
                let! result = hub.InvokeAsync<ClientRegistrationResult>("Connect", registration device)
                Assert.That(result.Generation, Is.GreaterThan(0), "Hub must return a registered session generation.")
            }
        let unavailable (hub: HubConnection) (label: string) =
            task {
                let mutable rejected = false
                try
                    let! _ = hub.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", "Fixture", "Worker")
                    ()
                with :? HubException -> rejected <- true
                Assert.That(rejected, Is.True, label)
            }
        use http = new HttpClient()
        let qualifyingIngress address =
            task {
                let command =
                    """{"jsonrpc":"2.0","id":55,"method":"tools/call","params":{"name":"agent_inbox","arguments":{"projectName":"Fixture","agentId":"Worker","deviceId":"test-missing-device"}}}"""
                use request = new HttpRequestMessage(HttpMethod.Post, address + "/mcp")
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token "workspace:read" "alice")
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
                request.Content <- new StringContent(command, Encoding.UTF8, "application/json")
                use! response = http.SendAsync(request)
                let! body = response.Content.ReadAsStringAsync()
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
                let payload =
                    if response.Content.Headers.ContentType.MediaType = "text/event-stream" then
                        body.Split('\n')
                        |> Array.find (fun line -> line.StartsWith("data: ", StringComparison.Ordinal))
                        |> fun line -> line.Substring(6)
                    else body
                use envelope = System.Text.Json.JsonDocument.Parse(payload)
                let result = envelope.RootElement.GetProperty("result")
                Assert.That(envelope.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(55))
                Assert.That(result.GetProperty("isError").GetBoolean(), Is.True)
                let message = result.GetProperty("content").[0].GetProperty("text").GetString()
                Assert.That(message, Is.EqualTo("NotFound: Jarvis device 'test-missing-device' is not registered for the authenticated user. Use ListDevices to find available devices."))
            }

        // The only positive timestamps originate in actual registered hub reads.
        // Optional clock projection below is an explicit test-only negative/edge
        // control: it preserves the server-authoritative SQLite LastSeenAtUtc,
        // but does NOT claim the projected ServerNowUtc came from the live host.
        let hubActivity (hub: HubConnection) (projectedAge: TimeSpan option) (project: string) (agent: string) =
            task {
                try
                    let! real = hub.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", project, agent)
                    match projectedAge, real.LastSeenAtUtc with
                    | Some age, Some seen -> return Ok { real with ServerNowUtc = seen.Add(age) }
                    | Some _, None -> return Error "No authenticated LastSeen to project."
                    | None, _ -> return Ok real
                with ex -> return Error ex.Message
            }
        let fixture name =
            let directory = System.IO.Path.Combine(root, name)
            System.IO.Directory.CreateDirectory(directory) |> ignore
            create directory
        let dependencies candidate (rt: Runtime) activity : Dependencies =
            { Enumerate = (fun () -> Ok [candidate])
              Current = NativeHealth.current rt
              Activity = activity
              Recover = NativeHealth.recover rt }
        let settings = { Enabled = true; InactivitySeconds = 600 }

        try
            let firstHost = newHost()
            try
                do! firstHost.StartAsync()
                let address = firstHost.Urls |> Seq.exactlyOne
                let alice = connect address "alice"
                let bob = connect address "bob"
                try
                    do! register alice "alice-new-negative"
                    // The isolation assertion deliberately follows the genuine
                    // qualifying ingress and presence of Alice's persisted row.
                    let store = firstHost.Services.GetRequiredService<Server.Services.OptionalFeedbackStore>()
                    // Hub Connect posts a diagnostic write to the same single-flight
                    // store. Wait for that flight to drain before the one MCP call.
                    use proofDeadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds 2.)
                    let mutable ready = false
                    while not ready && not proofDeadline.IsCancellationRequested do
                        let! status = store.TryExecute(fun _ -> ())
                        match status with
                        | Ok () -> ready <- true
                        | Error _ -> do! Task.Delay(25)
                    Assert.That(ready, Is.True, "SQLite did not finish the registered connection diagnostics.")
                    do! qualifyingIngress address
                    let mutable confirmed : AgentActivitySnapshot option = None
                    let mutable lastProofError = ""
                    let mutable attempts = 0
                    while confirmed.IsNone && attempts < 40 do
                        attempts <- attempts + 1
                        try
                            let! actual = alice.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", "Fixture", "Worker")
                            confirmed <- Some actual
                        with :? HubException as ex ->
                            lastProofError <- ex.Message
                            do! Task.Delay(50)
                    Assert.That(confirmed.IsSome, Is.True, "Qualified MCP ingress must reach verified hosted SQLite: " + lastProofError)
                    Assert.That(confirmed.Value.LastSeenAtUtc.IsSome, Is.True)
                    do! register bob "bob-after-alice-ingress"
                    do! unavailable bob "Bob must not observe Alice's persisted row after qualifying ingress."

                    let first, second, rt, _, candidate = fixture "boundary"
                    let baseline = LedgerStore.fetch first |> success
                    let! recent = NativeHealth.checkOnce settings (dependencies candidate rt (hubActivity alice None))
                    Assert.That(recent.Recoveries, Is.Zero, "Actual recent hosted activity must HOLD at 600s.")
                    let! below = NativeHealth.checkOnce settings (dependencies candidate rt (hubActivity alice (Some (TimeSpan.FromMilliseconds 599999.))))
                    Assert.That(below.Recoveries, Is.Zero, "599.999s must remain below the 600s threshold.")
                    Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(baseline.Commit))
                    let! onEdge = NativeHealth.checkOnce settings (dependencies candidate rt (hubActivity alice (Some (TimeSpan.FromSeconds 600.))))
                    Assert.That(onEdge.Recoveries, Is.EqualTo(1),
                                "At exactly 600s, the true Git CAS path must become eligible.")
                    let _, receipt = LedgerStore.getOperationStatus second (NativeHealth.recoveryOperationId candidate) |> success
                    Assert.That(receipt.IsSome, Is.True, "Exact edge must produce an authentic Git operation receipt.")
                    Assert.That(receipt.Value.Type, Is.EqualTo("RecoverAgentAssignment"))
                    let _, _, afterAttempt = ReviewWorkflow.getTask second candidate.TaskId |> success
                    Assert.That(afterAttempt.Value.AssignmentGeneration, Is.EqualTo(candidate.Generation + 1))
                    Assert.That(afterAttempt.Value.SourceRef, Is.EqualTo(candidate.SourceRef))

                    // Two real registered Hub reads, but a deliberately reversed
                    // test-only server clock on the second. No fake Git recovery.
                    let reverseBinding, _, reverseRt, _, reverseCandidate = fixture "reversed-clock"
                    let reverseHead = (LedgerStore.fetch reverseBinding |> success).Commit
                    let mutable readCount = 0
                    let mutable recoveredOperation = None
                    let reversedActivity (project: string) (agent: string) =
                        task {
                            let! real = alice.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", project, agent)
                            Assert.That(real.LastSeenAtUtc, Is.EqualTo(confirmed.Value.LastSeenAtUtc))
                            readCount <- readCount + 1
                            let age = if readCount = 1 then TimeSpan.FromSeconds 601. else TimeSpan.FromSeconds 600.
                            return Ok { real with ServerNowUtc = real.LastSeenAtUtc.Value.Add(age) }
                        }
                    let reversedDeps =
                        { dependencies reverseCandidate reverseRt reversedActivity with
                            Recover = (fun _ operationId -> recoveredOperation <- Some operationId; Ok ()) }
                    let! reversed = NativeHealth.checkOnce settings reversedDeps
                    Assert.That(readCount, Is.EqualTo(2), "Both observations must traverse the authenticated Hub.")
                    Assert.That(reversed.Errors, Is.Empty)
                    Assert.That(recoveredOperation, Is.EqualTo(None), "Reversed time cannot invoke recovery.")
                    Assert.That(reversed.Holds, Is.EqualTo(1))
                    let monotonicDeps = { reversedDeps with Activity = hubActivity alice (Some(TimeSpan.FromSeconds 601.)) }
                    let! monotonic = NativeHealth.checkOnce settings monotonicDeps
                    Assert.That(monotonic.Errors, Is.Empty)
                    Assert.That(monotonic.Recoveries, Is.EqualTo(1))
                    Assert.That(recoveredOperation, Is.EqualTo(Some (NativeHealth.recoveryOperationId reverseCandidate)))
                    Assert.That((LedgerStore.fetch reverseBinding |> success).Commit, Is.EqualTo(reverseHead))

                    // Source and attempt-version drift is a genuine canonical Git
                    // mutation inside Current, between Enumerate and final read.
                    let sourceBinding, _, sourceRt, _, sourceCandidate = fixture "source-rebind"
                    let shiftedRef = "refs/heads/health-matrix-alternate"
                    git sourceBinding.RepositoryPath [ "branch"; "health-matrix-alternate" ] |> ignore
                    let mutable sourceChanged, sourceRecovery = false, 0
                    let sourceDeps =
                        { dependencies sourceCandidate sourceRt (hubActivity alice (Some(TimeSpan.FromSeconds 600.))) with
                            Current = (fun candidateBefore ->
                                ReviewWorkflow.rebindAttemptSource sourceBinding (Guid.NewGuid())
                                    candidateBefore.TaskId candidateBefore.AgentId
                                    candidateBefore.AttemptVersion candidateBefore.Generation shiftedRef
                                |> success |> ignore
                                sourceChanged <- true
                                NativeHealth.current sourceRt candidateBefore)
                            Recover = (fun _ _ -> sourceRecovery <- sourceRecovery + 1; Ok ()) }
                    let! sourceHold = NativeHealth.checkOnce settings sourceDeps
                    Assert.That(sourceChanged, Is.True)
                    Assert.That(sourceHold.Holds, Is.EqualTo(1))
                    Assert.That(sourceRecovery, Is.Zero, "Changed canonical source/attempt version must HOLD.")
                    let sourceFresh = NativeHealth.current sourceRt sourceCandidate |> success
                    Assert.That(sourceFresh.IsSome, Is.True)
                    Assert.That(sourceFresh.Value.SourceRef, Is.EqualTo(shiftedRef))
                    Assert.That(sourceFresh.Value.AttemptVersion, Is.GreaterThan(sourceCandidate.AttemptVersion))

                    // Assignment-generation drift is a second actual canonical Git
                    // CAS between enumeration and Current; stale worker cannot act.
                    let genBinding, _, genRt, _, genCandidate = fixture "generation-shift"
                    let mutable generationChanged, genRecoverCalls = false, 0
                    let genDeps =
                        { dependencies genCandidate genRt (hubActivity alice (Some(TimeSpan.FromSeconds 600.))) with
                            Current = (fun candidateBefore ->
                                ReviewWorkflow.recoverAgentAssignmentWithTaskVersionAs genBinding
                                    "AuthenticatedUser" (Guid.NewGuid()) candidateBefore.TaskId
                                    candidateBefore.AgentId candidateBefore.TaskVersion
                                    candidateBefore.AttemptVersion candidateBefore.AgentVersion
                                    candidateBefore.Generation "Offline prior generation change"
                                |> success |> ignore
                                generationChanged <- true
                                NativeHealth.current genRt candidateBefore)
                            Recover = (fun _ _ -> genRecoverCalls <- genRecoverCalls + 1; Ok ()) }
                    let! genHold = NativeHealth.checkOnce settings genDeps
                    Assert.That(generationChanged, Is.True)
                    Assert.That(genHold.Holds, Is.EqualTo(1))
                    Assert.That(genRecoverCalls, Is.Zero, "Changed actual assignment generation must HOLD.")
                    let _, _, changedAttempt = ReviewWorkflow.getTask genBinding genCandidate.TaskId |> success
                    Assert.That(changedAttempt.Value.AssignmentGeneration, Is.EqualTo(genCandidate.Generation + 1))
                finally
                    alice.DisposeAsync().AsTask().GetAwaiter().GetResult()
                    bob.DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally
                firstHost.StopAsync().GetAwaiter().GetResult()
                firstHost.DisposeAsync().AsTask().GetAwaiter().GetResult()

            // New real host/service provider, unchanged disk SQLite. Persisted
            // rows cannot resurrect a process-local proof; only NEW ingress can.
            use sql = new SqliteConnection("Data Source=" + databasePath)
            sql.Open()
            use count = sql.CreateCommand()
            count.CommandText <- "SELECT COUNT(*) FROM attributed_agent_activity WHERE user_id='alice' AND project_name='Fixture' AND agent_id='Worker'"
            Assert.That(Convert.ToInt32(count.ExecuteScalar()), Is.EqualTo(1),
                        "SQLite observation must survive the hosted service-provider restart.")
            let restarted = newHost()
            try
                do! restarted.StartAsync()
                let address = restarted.Urls |> Seq.exactlyOne
                let aliceAgain = connect address "alice"
                try
                    do! register aliceAgain "alice-after-host-restart"
                    do! unavailable aliceAgain "Persisted SQLite row alone cannot verify post-restart Health inactivity."
                finally
                    aliceAgain.DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally
                restarted.StopAsync().GetAwaiter().GetResult()
                restarted.DisposeAsync().AsTask().GetAwaiter().GetResult()
        finally
            try System.IO.Directory.Delete(root, true) with _ -> ()
    }
