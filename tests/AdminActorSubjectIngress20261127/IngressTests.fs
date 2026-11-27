module AdminActorSubjectIngress20261127

open System
open System.Collections
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Claims
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
open AutomationHealthHostedFixture

let private subject = "SyntheticSubject"
let private initiator = "SyntheticInitiator"
let private unrelated = "SyntheticUnrelated"
let private device = "admin-regression-fixture"
let private project = "Fixture"
let private taskId = "admin-regression-task"
let private blankCommand action operation (actor: string) : TaskWriteCommand =
    { ProjectName = project; Action = action; OperationId = operation
      TaskId = taskId; AttemptId = ""; ActorId = actor; OwnerId = ""
      Title = ""; Html = ""; ExpectedVersion = 0; ExpectedAgentVersion = 0
      ExpectedTaskVersion = 0; AssignmentGeneration = 0; Summary = ""; NextAction = ""
      Reason = ""; BaseOid = ""; TargetRef = ""; SourceRef = ""
      AllowDetached = false; AllowUncommitted = false; DecisionSequence = 0L
      Evidence = ""; SessionId = "" }

type private Scenario =
    { Route: string
      Args: (string * obj) list
      Expected: TaskWriteCommand
      ShouldApply: bool }

let private prepare binding action operation =
    for agent in [initiator; unrelated; "PreviousOwner"] do
        ReviewWorkflow.registerAgent binding (Guid.NewGuid()) agent |> success |> ignore
    if action <> "RegisterAgent" then
        ReviewWorkflow.registerAgent binding (Guid.NewGuid()) subject |> success |> ignore
        ReviewWorkflow.queueResponseAlert binding (Guid.NewGuid()) initiator subject "Synthetic target alert must remain pending" |> success |> ignore
    ReviewWorkflow.createTaskWithSpec binding (Guid.NewGuid()) taskId "Synthetic admin regression" "<p>Original</p>" |> success |> ignore
    if action <> "RegisterAgent" && action <> "AssignTask" then
        let owner = if action = "TransferOwner" then "PreviousOwner" else subject
        ReviewWorkflow.assignTask binding (Guid.NewGuid()) taskId (Guid.NewGuid()) owner |> success |> ignore
    if action = "RecoverAgentAssignment" then
        ReviewWorkflow.resumeAgent binding (Guid.NewGuid()) taskId subject 0 0 0 |> success |> ignore
    let snapshot = LedgerStore.fetch binding |> success
    let task, attempt = ReviewWorkflow.getTaskAt binding snapshot taskId |> success
    let versions owner =
        let agent = ReviewWorkflow.getAgentWorkingTurnAt binding snapshot owner |> success
        let a = attempt.Value
        [ "expectedAttemptVersion", box a.Version; "expectedAgentVersion", box agent.Version
          "assignmentGeneration", box a.AssignmentGeneration ], a.Version, agent.Version, a.AssignmentGeneration
    let nextAttempt = Guid.NewGuid().ToString("D")
    let baseArgs = ["taskId", box taskId]
    let reason = "Synthetic fixture reason"
    let instructions = "Synthetic fixture instructions"
    let canonicalActor = if action = "AssignTask" || action = "TransferOwner" then "Orchestrator" else subject
    let cmd = blankCommand action operation canonicalActor
    match action with
    | "RegisterAgent" ->
        { Route = "register_agent"; Args = []; Expected = { cmd with TaskId = "" }; ShouldApply = true }
    | "AssignTask" ->
        { Route = "assign_task"; Args = baseArgs @ ["attemptId", box nextAttempt]
          Expected = { cmd with AttemptId = nextAttempt; OwnerId = subject }; ShouldApply = true }
    | "ReissueAssignment" | "AdoptTaskSpec" ->
        let fields, av, gv, gen = versions subject
        let adoption = action = "AdoptTaskSpec"
        let args =
            if adoption then
                ["expectedCurrentSpecBlobOid", box attempt.Value.SpecBlobOid; "expectedTaskVersion", box task.Version]
            else []
        { Route = "reissue_assignment"; Args = baseArgs @ fields @ ["instructions", box instructions] @ args
          Expected =
            { cmd with ExpectedVersion = av; ExpectedAgentVersion = gv; AssignmentGeneration = gen
                       Summary = instructions; BaseOid = (if adoption then attempt.Value.SpecBlobOid else "")
                       ExpectedTaskVersion = (if adoption then task.Version else 0) }
          // Adoption requires a separately revised spec, not the current pinned OID.
          // Its wire/authorization negative is deliberate rather than a claimed successful adoption.
          ShouldApply = not adoption }
    | "ReissueQueuedAttempt" | "RecoverAgentAssignment" ->
        let fields, av, gv, gen = versions subject
        { Route = (if action = "ReissueQueuedAttempt" then "reissue_queued_attempt" else "recover_agent_assignment")
          Args = baseArgs @ fields @ ["reason", box reason]
          Expected = { cmd with ExpectedVersion = av; ExpectedAgentVersion = gv
                                AssignmentGeneration = gen; Reason = reason }
          // Reissue queued requires an unoccupied owner; this fixture intentionally has a Ready owner.
          ShouldApply = action = "RecoverAgentAssignment" }
    | "TransferOwner" ->
        let fields, av, gv, gen = versions "PreviousOwner"
        { Route = "transfer_owner"; Args = baseArgs @ fields @ ["expectedTaskVersion", box task.Version
                                                              "newAttemptId", box nextAttempt; "reason", box reason]
          Expected = { cmd with AttemptId = nextAttempt; OwnerId = subject; ExpectedVersion = av
                                ExpectedAgentVersion = gv; ExpectedTaskVersion = task.Version
                                AssignmentGeneration = gen; Reason = reason }
          ShouldApply = true }
    | _ -> failwithf "Unknown test action: %s" action

let private parseFirstLine<'a> (raw: string) =
    JsonSerializer.Deserialize<'a>(raw.Split('\n').[0])

// Actual registered HTTP MCP route and authenticated SignalR transport. Only the
// client receive boundary is captured to insert a deterministic gate before
// the real authenticated dispatcher. No synthetic AgentCommandResponse is used.
let private runCase action mode = task {
    Assert.That(OperatingSystem.IsLinux(), Is.True, "Linux disposable configuration required.")
    let root = Path.Combine(Path.GetTempPath(), "jarvis-admin-actor-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let controlled = ["XDG_CONFIG_HOME"; "GIT_CONFIG_NOSYSTEM"; "GIT_CONFIG_GLOBAL"]
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
    let issuer = "urn:jarvis:offline:admin-actor"
    let audience = "urn:jarvis:offline"
    let key = SymmetricSecurityKey(Encoding.UTF8.GetBytes("admin-actor-local-test-only-signing-key-20261127"))
    let token (user: string) (scopes: string) =
        let claims = [Claim("sub", user); Claim("scope", scopes)]
        JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow.AddMinutes(-1.),
                         DateTime.UtcNow.AddMinutes(15.), SigningCredentials(key, SecurityAlgorithms.HmacSha256))
        |> JwtSecurityTokenHandler().WriteToken
    try
        let first, second, _, _, _ = create root
        let tui = ConsoleTui.ConsoleTui()
        let rt = Runtime(Path.GetDirectoryName(first.RepositoryPath), tui, FullTrust)
        let operation = Guid.NewGuid().ToString("D")
        let scenario = prepare first action operation
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let finished = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        let captures = ConcurrentQueue<string * string>()
        let mutable executions = 0
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
        use http = new HttpClient(Timeout = TimeSpan.FromSeconds(35.))
        let hub =
            HubConnectionBuilder()
                .WithUrl(address + "/client", Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>(fun options ->
                    options.AccessTokenProvider <- Func<Task<string>>(fun () -> Task.FromResult(token "alice" "client:connect"))))
                .Build()
        let run json actor =
            task {
                let cmd = JsonSerializer.Deserialize<AgentCommand>(json)
                let isOriginal =
                    match cmd with
                    | TaskWriteCommand c -> c.OperationId = operation
                    | _ -> false
                if isOriginal then
                    Interlocked.Increment(&executions) |> ignore
                    do! gate.Task
                let! response =
                    Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake rt json actor None false (fun _ _ -> ())
                if isOriginal then finished.TrySetResult(response) |> ignore
                return response
            }
        use handler =
            hub.On<string,string,string>("ReceiveCommand", Func<string,string,string,Task>(fun correlation json actor ->
                task {
                    captures.Enqueue((json, actor))
                    let! response = Client.SignalR.Client.executeOrPromoteWith rt json actor (TimeSpan.FromMilliseconds(50.)) run
                    do! hub.SendCoreAsync("SendClientResponse", [|box correlation; box response|], CancellationToken.None)
                } :> Task))
        let mutable requestId = 0
        let call user scopes name (fields: (string * obj) list) = task {
            let id = Interlocked.Increment(&requestId)
            let args = dict fields
            let raw = JsonSerializer.Serialize({| jsonrpc="2.0"; id=id; ``method``="tools/call"; ``params``={| name=name; arguments=args |} |})
            use request = new HttpRequestMessage(HttpMethod.Post, address + "/mcp")
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token user scopes)
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
            match envelope.RootElement.TryGetProperty("result") with
            | false, _ -> return true, payload
            | true, result ->
                let isError = match result.TryGetProperty("isError") with true, v -> v.GetBoolean() | _ -> false
                return isError, result.GetProperty("content").[0].GetProperty("text").GetString()
        }
        let scopes = "workspace:read git:write"
        let activity who =
            hub.InvokeCoreAsync<AgentActivitySnapshot>("GetAgentActivity", [|box project; box who|], CancellationToken.None)
        let originalArgs =
            let labels =
                if mode = "explicit" then ["agentId", box initiator; "targetAgentId", box subject]
                else ["agentId", box subject]
            ["projectName", box project; "deviceId", box device; "operationId", box operation] @ labels @ scenario.Args
        let poll user who proj id =
            call user scopes "get_job_result"
                ["projectName", box proj; "agentId", box who; "deviceId", box device
                 "jobId", box id; "afterSequence", null]
        let rec terminal who jobId deadline = task {
            let! failed, raw = poll "alice" who project jobId
            Assert.That(failed, Is.False, raw)
            let job = parseFirstLine<JobResult> raw
            match job.Status with
            | ToolCompleted -> return job
            | Running when DateTimeOffset.UtcNow < deadline ->
                do! Task.Delay(50)
                return! terminal who jobId deadline
            | _ -> return failwithf "Unexpected or expired disposable job: %A" job.Status
        }
        try
            do! hub.StartAsync()
            let registration : ClientRegistration =
                { DeviceId = device; DeviceName = device; ClientVersion = "fixture"
                  ProtocolVersion = AgentProtocol.version
                  SupportedCommands = AgentProtocol.commandDefinitions |> List.map (fun x -> x.Name) }
            let! accepted = hub.InvokeCoreAsync<ClientRegistrationResult>("Connect", [|box registration|], CancellationToken.None)
            Assert.That(accepted.EnabledCommands, Does.Contain("TaskWrite"))
            let! before = activity subject
            Assert.That(before.LastSeenAtUtc, Is.EqualTo(None))
            let! denied, deniedRaw = call "alice" "workspace:read" scenario.Route originalArgs
            Assert.That(denied, Is.True, deniedRaw)
            Assert.That(captures.IsEmpty, Is.True, "Missing write scope must not reach SignalR.")
            let! failed, raw = call "alice" scopes scenario.Route originalArgs
            if mode = "explicit" && failed then
                Assert.Fail("Explicit initiator/subject shape rejected by registered MCP route: " + raw)
            Assert.That(failed, Is.False, raw)
            let job = parseFirstLine<ToolJobHandle> raw
            let json, transportedActor = captures.ToArray() |> Array.exactlyOne
            let canonical = JsonSerializer.Serialize<AgentCommand>(TaskWriteCommand scenario.Expected)
            TestContext.WriteLine(sprintf "%s: job actor=%s transport actor=%s canonical=%s" action job.AgentId transportedActor json)
            let! after = activity subject
            let! otherDenied, otherRaw = poll "alice" unrelated project job.JobId
            Assert.That(otherDenied, Is.True, otherRaw)
            let! wrongProjectDenied, wrongProjectRaw = poll "alice" transportedActor "UnknownFixtureProject" job.JobId
            Assert.That(wrongProjectDenied, Is.True, wrongProjectRaw)
            let! wrongPrincipalDenied, wrongPrincipalRaw = poll "bob" transportedActor project job.JobId
            Assert.That(wrongPrincipalDenied, Is.True, wrongPrincipalRaw)
            if mode = "control" then
                Assert.That(json, Is.EqualTo(canonical), "All canonical wire fields are independently pinned.")
                // These are synthetic fixtures; replay exactly one fresh in-memory
                // request while its original operation is still pending.
                let! duplicateFailed, duplicateRaw = call "alice" scopes scenario.Route originalArgs
                Assert.That(duplicateFailed, Is.False, duplicateRaw)
                let duplicate = parseFirstLine<ToolJobHandle> duplicateRaw
                Assert.That(duplicate.JobId, Is.EqualTo(job.JobId))
                // The operation registry key includes the transport actor.
                // Keep that actor fixed when checking changed canonical bytes.
                // Registration has no mutable field other than its subject,
                // so its changed-request control is covered by the other routes.
                if action <> "RegisterAgent" then
                    let changedArgs = originalArgs |> List.map (fun (k,v) -> if k = "taskId" then k, box "different-fixture-task" else k,v)
                    let! changedFailed, changedRaw = call "alice" scopes scenario.Route changedArgs
                    Assert.That(changedFailed, Is.True, changedRaw)
                Assert.That(Volatile.Read(&executions), Is.EqualTo(1))
            gate.TrySetResult(()) |> ignore
            let! response = finished.Task.WaitAsync(TimeSpan.FromSeconds(35.))
            let _, receipt = LedgerStore.getOperationStatus second (Guid.Parse operation) |> success
            TestContext.WriteLine(sprintf "terminal error=%A, receipt=%A, target activity=%A" response.Error (receipt |> Option.map (fun r -> r.Type)) after.LastSeenAtUtc)
            if mode = "control" then
                // No claim that every route succeeds in every ledger state:
                // the passing cases prove terminal evidence; deliberate invalid
                // state cases prove only actual routing, bytes and privacy.
                let! result = terminal transportedActor job.JobId (DateTimeOffset.UtcNow.AddSeconds(15.))
                Assert.That(result.ToolResponse.IsSome, Is.True)
                Assert.That(result.ToolResponse.Value.Error, Is.EqualTo(response.Error))
                if scenario.ShouldApply then
                    Assert.That(response.Error.IsNone, Is.True, sprintf "%A" response.Error)
                    Assert.That(receipt.IsSome, Is.True, "Applied route must have canonical receipt.")
                    Assert.That(receipt.Value.Type, Is.EqualTo(action))
                    if action = "AssignTask" then
                        let _, _, attempt = ReviewWorkflow.getTask second taskId |> success
                        Assert.That(attempt.Value.OwnerAgentId, Is.EqualTo(subject))
                        let snapshot = LedgerStore.fetch second |> success
                        let state = ReviewWorkflow.getAgentWorkingTurnAt second snapshot subject |> success
                        Assert.That(state.State, Is.EqualTo("Ready"))
            elif mode = "legacy" then
                // Desired legacy invariant: subject-only input does not establish
                // an agent owner or activity. This is intentionally RED on baseline.
                Assert.That(transportedActor, Is.Not.EqualTo(subject), "Legacy administrative subject became initiating transport actor.")
                Assert.That(job.AgentId, Is.Not.EqualTo(subject), "Subject must never own caller's promoted job.")
                Assert.That(after.LastSeenAtUtc, Is.EqualTo(None), "Administrative subject is not caller activity.")
            else
                Assert.That(transportedActor, Is.EqualTo(initiator))
                Assert.That(job.AgentId, Is.EqualTo(initiator))
                Assert.That(json, Is.EqualTo(canonical), "Explicit actor must not change canonical subject or request bytes.")
                Assert.That(after.LastSeenAtUtc, Is.EqualTo(None))
                let! result = terminal initiator job.JobId (DateTimeOffset.UtcNow.AddSeconds(15.))
                Assert.That(result.ToolResponse.IsSome, Is.True)
                if action <> "RegisterAgent" then
                    let alerts = ReviewWorkflow.getResponseAlerts second subject |> success
                    Assert.That(alerts.Pending |> List.length, Is.EqualTo(1), "Subject alert was consumed by caller.")
        finally
            // Release the synthetic gate even if a RED assertion fires before completion.
            gate.TrySetResult(()) |> ignore
            if Volatile.Read(&executions) > 0 then
                try finished.Task.Wait(TimeSpan.FromSeconds(35.)) |> ignore with _ -> ()
            hub.DisposeAsync().AsTask().GetAwaiter().GetResult()
            app.StopAsync().GetAwaiter().GetResult()
    finally
        for key, value in originalEnvironment do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)
}

[<TestCase("RegisterAgent"); TestCase("AssignTask"); TestCase("ReissueAssignment"); TestCase("AdoptTaskSpec"); TestCase("ReissueQueuedAttempt"); TestCase("RecoverAgentAssignment"); TestCase("TransferOwner"); NonParallelizable; Category("Control")>]
let registered_legacy_route_wire_and_privacy_controls action = runCase action "control"

[<TestCase("RegisterAgent"); TestCase("AssignTask"); TestCase("ReissueAssignment"); TestCase("AdoptTaskSpec"); TestCase("ReissueQueuedAttempt"); TestCase("RecoverAgentAssignment"); TestCase("TransferOwner"); NonParallelizable; Category("KnownDefect")>]
let legacy_subject_must_not_own_caller_job_or_activity action = runCase action "legacy"

[<TestCase("RegisterAgent"); TestCase("AssignTask"); TestCase("ReissueAssignment"); TestCase("AdoptTaskSpec"); TestCase("ReissueQueuedAttempt"); TestCase("RecoverAgentAssignment"); TestCase("TransferOwner"); NonParallelizable; Category("KnownDefect")>]
let explicit_initiator_and_subject_must_preserve_wire_and_job_privacy action = runCase action "explicit"
