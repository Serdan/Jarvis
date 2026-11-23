module SharedHealthContention

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
open System.Diagnostics
open System.Threading
open Server.Services

type private McpAuditStore(path: string) =
    let store = new Server.Services.OptionalFeedbackStore(path)
    member _.Store = store
    interface IDisposable with
        member _.Dispose() = (store :> IDisposable).Dispose()

[<Test>]
let shared_audit_activity_contention () =
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
        let mutable splitAudit = false
        let newHost (dbPath: string) =
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
            builder.Configuration.AddInMemoryCollection(
                [ KeyValuePair("Auth0Domain", issuer)
                  KeyValuePair("Audience", audience)
                  KeyValuePair("DatabasePath", dbPath) ]) |> ignore
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
                    if splitAudit && context.Request.Path = PathString("/mcp") then
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
        let qualifyingIngress address (actor: string) =
            task {
                let command =
                    """{"jsonrpc":"2.0","id":55,"method":"tools/call","params":{"name":"agent_inbox","arguments":{"projectName":"Fixture","agentId":"Worker","deviceId":"test-missing-device"}}}"""
                let command = command.Replace("\"Worker\"", "\"" + actor + "\"")
                let sw = Stopwatch.StartNew()
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
                return sw.Elapsed.TotalMilliseconds
            }

        let awaitDrain (store: OptionalFeedbackStore) =
            task {
                let until = Stopwatch.StartNew()
                let mutable ready = false
                while not ready && until.Elapsed < TimeSpan.FromSeconds 3. do
                    let! result = store.TryExecute(fun _ -> ())
                    match result with
                    | Ok () -> ready <- true
                    | Error _ -> do! Task.Delay(10)
                Assert.That(ready, Is.True, "Expected store to drain")
            }
        let read (hub: HubConnection) (actor: string) =
            task {
                try
                    let! snap = hub.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity", "Fixture", actor)
                    return snap.LastSeenAtUtc
                with :? HubException as ex ->
                    Assert.That(ex.Message, Does.Contain("ActivityProofUnavailable"))
                    return None
            }
        let checkUnavailable path label expected =
            task {
                splitAudit <- false
                use host = newHost path
                do! host.StartAsync()
                let address = host.Urls |> Seq.exactlyOne
                let hub = connect address "alice"
                try
                    let! elapsed = qualifyingIngress address "Unavailable"
                    do! register hub ("negative-" + label)
                    do! Task.Delay(300)
                    let! proof = read hub "Unavailable"
                    Assert.That(proof.IsNone, Is.True, label)
                    let store = host.Services.GetRequiredService<OptionalFeedbackStore>()
                    let! status = store.TryExecute(fun _ -> ())
                    match status with
                    | Error error -> Assert.That(error, Does.Contain(expected), label)
                    | Ok () -> Assert.Fail(label + ": unexpectedly admitted database")
                    TestContext.Out.WriteLine(sprintf "%s real MCP routed and Hub HOLD; ms=%.3f store=%A" label elapsed status)
                finally
                    hub.DisposeAsync().AsTask().GetAwaiter().GetResult()
                    host.StopAsync().GetAwaiter().GetResult()
            }
        let report label (values: (float * bool) array) =
            let times = values |> Array.map fst |> Array.sort
            TestContext.Out.WriteLine(sprintf "%s: verified=%d/%d unavailable=%d p50_ms=%.3f p95_ms=%.3f max_ms=%.3f" label (values |> Array.filter snd |> Array.length) values.Length (values |> Array.filter (snd >> not) |> Array.length) times[times.Length/2] times[int (ceil (0.95 * float times.Length))-1] times[times.Length-1])
        try
            use app = newHost databasePath
            do! app.StartAsync()
            let address = app.Urls |> Seq.exactlyOne
            let! coldMs = qualifyingIngress address "Cold"
            let hub = connect address "alice"
            try
                do! register hub "shared-contention"
                let store = app.Services.GetRequiredService<OptionalFeedbackStore>()
                // Connect may already have initialized the DB; cold classification
                // is first authenticated ingress after connect, not guaranteed cold SQLite.
                do! awaitDrain store
                do! Task.Delay(30)
                let! cold = read hub "Cold"
                TestContext.Out.WriteLine(sprintf "shared cold first ingress before Hub registration verified=%b ms=%.3f" cold.IsSome coldMs)
                let sequential = ResizeArray<float * bool>()
                for i in 1..40 do
                    let actor = "Sequential" + string i
                    let! ms = qualifyingIngress address actor
                    // Poll only a no-op gate after response; proof must survive the
                    // last ingress, not merely become available once the DB drains.
                    do! awaitDrain store
                    do! Task.Delay(20)
                    let! seen = read hub actor
                    sequential.Add((ms, seen.IsSome))
                report "shared sequential first+warm, fresh distinct keys" (sequential.ToArray())
                let concurrent = ResizeArray<float * bool>()
                for batch in 1..5 do
                    let actors = [| for j in 1..8 -> sprintf "Concurrent%d-%d" batch j |]
                    let! times = actors |> Array.map (qualifyingIngress address) |> Task.WhenAll
                    do! awaitDrain store
                    do! Task.Delay(20)
                    for i in 0..actors.Length-1 do
                        let! seen = read hub actors[i]
                        concurrent.Add((times[i], seen.IsSome))
                report "shared concurrent distinct keys, 5x8" (concurrent.ToArray())
                Assert.That(sequential |> Seq.forall snd, Is.True, "Every normal sequential latest ingress must establish proof after bounded drain")
                Assert.That(concurrent |> Seq.forall snd, Is.True, "Every normal concurrent latest ingress must establish proof after bounded drain")

                // Explicit comparison/control only: separate request's audit
                // lookup to seed real authenticated coverage for the same key.
                splitAudit <- true
                let seed actor =
                    task {
                        let mutable value = None
                        let mutable attempts = 0
                        while value.IsNone && attempts < 10 do
                            attempts <- attempts + 1
                            let! _ = qualifyingIngress address actor
                            do! awaitDrain store
                            let! seen = read hub actor
                            value <- seen
                        Assert.That(value.IsSome, Is.True, "Separated audit control must establish coverage")
                        return value.Value
                    }
                let! prior = seed "Worker"
                let! priorOther = seed "Other"
                do! Task.Delay(1100)
                splitAudit <- false
                // Controlled shared-gate audit operation, not a source edit.
                // Same BeginOperation method as actual MCP audit; barrier after
                // its write while inFlight remains owned. No external process.
                use entered = new ManualResetEventSlim(false)
                use release = new ManualResetEventSlim(false)
                let flight = store.TryExecute(fun db ->
                    db.BeginOperation("alice", "agent_inbox", Some "Fixture", "scratch", None, None) |> ignore
                    entered.Set()
                    if not (release.Wait(TimeSpan.FromSeconds 5.)) then failwith "Barrier timed out")
                Assert.That(entered.Wait(TimeSpan.FromSeconds 3.), Is.True, "Audit barrier must acquire gate")
                let! ms = (qualifyingIngress address "Worker").WaitAsync(TimeSpan.FromSeconds 2.)
                let! crossMs = (qualifyingIngress address "Cross").WaitAsync(TimeSpan.FromSeconds 2.)
                let! during = read hub "Worker"
                Assert.That(during.IsNone, Is.True)
                let! status = flight
                Assert.That(status, Is.EqualTo(Error "TelemetryUnavailable: SQLite timed out; diagnostic outcome unknown." : Result<unit,string>))
                let latestBefore = DateTimeOffset.UtcNow
                let! latestMs = (qualifyingIngress address "Worker").WaitAsync(TimeSpan.FromSeconds 2.)
                let! timedMs = (qualifyingIngress address "AfterTimeout").WaitAsync(TimeSpan.FromSeconds 2.)
                let! stillPending = read hub "Worker"
                Assert.That(stillPending.IsNone, Is.True, "Timed-out audit retains the physical slot; same-key supersession cannot certify before dispatch")
                let candidate : Candidate =
                    { ProjectName="Fixture"; ProjectId=Guid.NewGuid(); LedgerId=Guid.NewGuid(); Epoch=1; ApprovedFetchUrl="unused"; ApprovedPushUrl="unused"; GitHead="unused"; AgentId="Worker"; AgentVersion=1; TaskId="unused"; TaskVersion=1; AttemptId="unused"; AttemptVersion=1; Generation=1; SpecBlobOid="unused"; SourceRef="unused" }
                let mutable reachedCurrent = false
                let mutable reachedRecover = false
                let deps : Dependencies =
                    { Enumerate=fun () -> Ok [candidate]
                      Current=fun c -> reachedCurrent <- true; Ok(Some c)
                      Activity=fun p a -> task { try let! snap = hub.InvokeAsync<AgentActivitySnapshot>("GetAgentActivity",p,a) in return Ok snap with ex -> return Error ex.Message }
                      Recover=fun _ _ -> reachedRecover <- true; Ok () }
                let! check = NativeHealth.checkOnce { Enabled=true; InactivitySeconds=1 } deps
                Assert.That(check.Holds, Is.EqualTo(1))
                Assert.That(check.Recoveries, Is.Zero)
                Assert.That(reachedCurrent || reachedRecover, Is.False)
                TestContext.Out.WriteLine(sprintf "Native Health real Hub HOLD with prior row age>1s; timeout-gate ingress ms=%.3f" timedMs)

                release.Set()
                do! awaitDrain store
                let! after = read hub "Worker"
                let! cross = read hub "Cross"
                let! other = read hub "Other"
                Assert.That(after.IsSome && after.Value >= latestBefore, Is.True, "Latest same-key ingress must drain after actual audit release; an older pending generation cannot certify")
                Assert.That(cross.IsSome, Is.True, "Distinct queued key must drain after actual audit release")
                Assert.That(other, Is.EqualTo(Some priorOther), "Untouched key must remain independently certified")
                let! persisted = store.TryExecute(fun db -> db.GetAttributedAgentActivity("alice", "Fixture", "Worker"))
                Assert.That(persisted, Is.EqualTo(Ok after : Result<DateTimeOffset option,string>), "Proof must come from durable row")
                Assert.That(DateTimeOffset.UtcNow - prior, Is.GreaterThan(TimeSpan.FromSeconds 1.))
                let! timed = read hub "AfterTimeout"
                Assert.That(timed.IsSome, Is.True, "New genuine ingress while timed-out audit physically holds gate must drain only after release")
                TestContext.Out.WriteLine(sprintf "controlled audit held: same_ms=%.3f cross_ms=%.3f superseding_ms=%.3f after_timeout_ms=%.3f; verified latest durable proof after release" ms crossMs latestMs timedMs)
                // Observe ordinary shared attempts to requalify, then positive
                // explicit separate-store control. No direct activity injection.
                let attempts = ResizeArray<float * bool>()
                for i in 1..20 do
                    let! elapsed = qualifyingIngress address "Worker"
                    do! awaitDrain store
                    do! Task.Delay(20)
                    let! proof = read hub "Worker"
                    match proof with
                    | Some seen -> Assert.That(seen, Is.GreaterThan(prior))
                    | None -> ()
                    attempts.Add((elapsed, proof.IsSome))
                report "shared same-key fresh requalification" (attempts.ToArray())
                Assert.That(attempts |> Seq.forall snd, Is.True)
                splitAudit <- true
                let controls = ResizeArray<float * bool>()
                for i in 1..20 do
                    let! elapsed = qualifyingIngress address "Worker"
                    do! awaitDrain store
                    do! Task.Delay(20)
                    let! proof = read hub "Worker"
                    match proof with
                    | Some seen -> Assert.That(seen, Is.GreaterThan(prior))
                    | None -> ()
                    controls.Add((elapsed, proof.IsSome))
                report "separated audit comparison, same key" (controls.ToArray())
                Assert.That(controls |> Seq.exists snd, Is.True)
                // Real SQLite lock. Separate audit control admits the activity write
                // rather than allowing MCP audit itself to monopolize the gate.
                let! lockPrior = seed "Locked"
                do!
                    task {
                        use writer = new SqliteConnection("Data Source=" + databasePath + ";Cache=Shared")
                        writer.Open()
                        use txn = writer.BeginTransaction()
                        use sql = writer.CreateCommand()
                        sql.Transaction <- txn
                        sql.CommandText <- "UPDATE attributed_agent_activity SET last_seen_at=last_seen_at WHERE agent_id='Locked';"
                        Assert.That(sql.ExecuteNonQuery(), Is.EqualTo(1))
                        let! lockMs = qualifyingIngress address "Locked"
                        do! Task.Delay(300)
                        let! locked = read hub "Locked"
                        Assert.That(locked.IsNone, Is.True)
                        TestContext.Out.WriteLine(sprintf "actual SQLite writer-lock ingress latency_ms=%.3f; proof unavailable after 300ms" lockMs)
                        txn.Rollback()
                    }
                do! awaitDrain store
                let! late = read hub "Locked"
                Assert.That(late.IsNone, Is.True, "Uncertain timed-out activity write cannot certify coverage even after release")
                let! refreshed = seed "Locked"
                Assert.That(refreshed, Is.GreaterThan(lockPrior))
                TestContext.Out.WriteLine("actual SQLite lock release + genuine fresh ingress requalified")
                let separateConcurrent = ResizeArray<float * bool>()
                for batch in 1..5 do
                    let actors = [| for j in 1..8 -> sprintf "SeparateConcurrent%d-%d" batch j |]
                    let! times = actors |> Array.map (qualifyingIngress address) |> Task.WhenAll
                    do! awaitDrain store
                    do! Task.Delay(20)
                    for i in 0..actors.Length-1 do
                        let! seen = read hub actors[i]
                        separateConcurrent.Add((times[i],seen.IsSome))
                report "separated audit concurrent distinct keys, 5x8" (separateConcurrent.ToArray())
                do! awaitDrain store
                let audit = app.Services.GetRequiredService<McpAuditStore>().Store
                do! awaitDrain audit
                let auditCounts path =
                    use connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly")
                    connection.Open()
                    use sql = connection.CreateCommand()
                    sql.CommandText <- "SELECT count(*), COALESCE(sum(completed_at IS NOT NULL), 0) FROM operations;"
                    use reader = sql.ExecuteReader()
                    Assert.That(reader.Read(), Is.True)
                    reader.GetInt64(0), reader.GetInt64(1)
                let sharedAuditCounts = auditCounts databasePath
                let separateAuditCounts = auditCounts (System.IO.Path.Combine(root,"mcp-audit.sqlite"))
                Assert.That(fst sharedAuditCounts, Is.GreaterThan(0L))
                Assert.That(fst separateAuditCounts, Is.GreaterThan(0L))
                TestContext.Out.WriteLine(sprintf "diagnostic operations (started,completed) shared=%A split=%A; include seed/control/barrier, not exact matched workload" sharedAuditCounts separateAuditCounts)
            finally
                hub.DisposeAsync().AsTask().GetAwaiter().GetResult()
                app.StopAsync().GetAwaiter().GetResult()
            let quota = System.IO.Path.Combine(root,"quota.sqlite")
            do
                use file = System.IO.File.Create(quota)
                file.SetLength(16L * 1024L * 1024L)
            do! checkUnavailable quota "default-quota" "TelemetryQuotaExceeded"
            Assert.That(System.IO.FileInfo(quota).Length, Is.EqualTo(16L * 1024L * 1024L))
            do! checkUnavailable "" "disabled-database" "TelemetryUnavailable"
            let inaccessible = System.IO.Path.Combine(root,"directory-not-db")
            System.IO.Directory.CreateDirectory(inaccessible) |> ignore
            do! checkUnavailable inaccessible "unopenable-database" "TelemetryUnavailable"
        finally
            try System.IO.Directory.Delete(root, true) with _ -> ()
    }
