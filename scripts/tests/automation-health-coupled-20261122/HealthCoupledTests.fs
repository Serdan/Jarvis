module AutomationHealthCoupledTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Claims
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Authentication.JwtBearer
open System.Threading
open System.Threading.Tasks
open Client
open Client.LedgerStore
open Client.NativeHealth
open Client.NativeHealth.NativeHealth
open Client.ReviewWorkflow
open Common
open Common.SignalR
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open Server.Services

// The only substituted boundary is the SignalR transport: the production authenticated
// HubService reads actual production OptionalFeedbackStore SQLite, and production
// NativeHealth executes real Git-backed recovery in TWO independent local clones.
type private CallerContext(connection: string, user: string, ?validatedPrincipal: ClaimsPrincipal) =
    inherit HubCallerContext()
    let items = Dictionary<obj,obj>() :> IDictionary<obj,obj>
    let features = FeatureCollection() :> IFeatureCollection
    override _.ConnectionId = connection
    override _.UserIdentifier =
        match validatedPrincipal with
        | Some principal -> Server.Auth.tryUserId principal |> Option.defaultValue ""
        | None -> user
    override _.User =
        defaultArg validatedPrincipal
            (ClaimsPrincipal(ClaimsIdentity([Claim("sub", user)], "isolated-test-issuer")))
    override _.Items = items
    override _.Features = features
    override _.ConnectionAborted = CancellationToken.None
    override _.Abort() = ()

let private git cwd (args: string list) =
    use child = new Process()
    child.StartInfo.FileName <- "git"
    child.StartInfo.WorkingDirectory <- cwd
    child.StartInfo.RedirectStandardOutput <- true
    child.StartInfo.RedirectStandardError <- true
    args |> List.iter child.StartInfo.ArgumentList.Add
    if not (child.Start()) then failwith "Cannot start fixture Git."
    let output = child.StandardOutput.ReadToEnd()
    let errors = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then failwithf "Git %A exit %d: %s" args child.ExitCode errors
    output.Trim()

let private success = function
    | Ok value -> value
    | Error issue -> failwithf "Canonical Git operation failed: %A" issue

let private fixture (run: LedgerBinding -> LedgerBinding -> string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-health-coupled-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let bare = Path.Combine(root, "remote.git")
    let one = Path.Combine(root, "one")
    let two = Path.Combine(root, "two")
    try
        git root ["init"; "--bare"; bare] |> ignore
        git root ["clone"; bare; one] |> ignore
        git one ["config"; "user.name"; "Health Coupled Fixture"] |> ignore
        git one ["config"; "user.email"; "health@example.test"] |> ignore
        File.WriteAllText(Path.Combine(one, "source.txt"), "sealed fixture")
        git one ["add"; "source.txt"] |> ignore
        git one ["commit"; "-m"; "original source"] |> ignore
        git one ["push"; "-u"; "origin"; "HEAD"] |> ignore
        git root ["clone"; bare; two] |> ignore
        git two ["config"; "user.name"; "Health Coupled Fixture"] |> ignore
        git two ["config"; "user.email"; "health@example.test"] |> ignore
        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let binding path : LedgerBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = bare; ApprovedPushUrl = bare
              ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
        let first, second = binding one, binding two
        LedgerStore.initialize first true |> success |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Owner" |> success |> ignore
        let taskId = "health-coupled"
        ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId
            "Original" "<h1>Immutable spec</h1>" |> success |> ignore
        let attemptId = Guid.NewGuid()
        ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner" |> success |> ignore
        ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Owner" 0 0 0 |> success |> ignore
        let _, _, started = ReviewWorkflow.getTask first taskId |> success
        let head = git one ["rev-parse"; "HEAD"]
        let sourceRef = "refs/heads/" + git one ["branch"; "--show-current"]
        ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Owner"
            started.Value.Version started.Value.AssignmentGeneration head sourceRef
        |> success |> ignore
        run first second taskId
    finally
        for path in [one; two] do
            let cfg = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot path
            let journal = Path.Combine(Path.GetDirectoryName cfg, "pending",
                                      Path.GetFileNameWithoutExtension cfg)
            if Directory.Exists journal then Directory.Delete(journal, true)
        if Directory.Exists root then Directory.Delete(root, true)

let private config (binding: LedgerBinding) =
    let path = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot binding.RepositoryPath
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let head = (LedgerStore.fetch binding |> success).Commit
    let approved : Client.OrchestrationApi.LedgerTrustBinding =
        { SchemaVersion = 1; Enabled = true; ProjectRoot = binding.RepositoryPath
          ProjectId = binding.ProjectId.ToString("D"); LedgerId = binding.LedgerId.ToString("D")
          Epoch = binding.Epoch; RemoteName = binding.RemoteName
          ApprovedFetchUrl = binding.ApprovedFetchUrl; ApprovedPushUrl = binding.ApprovedPushUrl
          HighestTrustedHead = head }
    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize approved)
    path

let private candidate binding project =
    let snapshot = LedgerStore.fetch binding |> success
    let agent = ReviewWorkflow.getAgentWorkingTurnAt binding snapshot "Owner" |> success
    let task, attempt = ReviewWorkflow.getTaskAt binding snapshot "health-coupled" |> success
    let attempt = attempt.Value
    { ProjectName = project; ProjectId = binding.ProjectId; LedgerId = binding.LedgerId
      Epoch = binding.Epoch; ApprovedFetchUrl = binding.ApprovedFetchUrl
      ApprovedPushUrl = binding.ApprovedPushUrl; GitHead = snapshot.Commit
      AgentId = "Owner"; AgentVersion = agent.Version; TaskId = task.TaskId
      TaskVersion = task.Version; AttemptId = attempt.AttemptId
      AttemptVersion = attempt.Version; Generation = attempt.AssignmentGeneration
      SpecBlobOid = attempt.SpecBlobOid; SourceRef = attempt.SourceRef }

let private registeredHub (users: UserService) (tracker: ClientResponseTracker)
                          (store: OptionalFeedbackStore) user device =
    let connection = user + "-" + device
    users.TransportConnected(user, connection)
    users.Register(user,
        { DeviceId = device; DeviceName = device; ProtocolVersion = AgentProtocol.version
          ClientVersion = "test"; SupportedCommands = ["RunCommand"; "ListJobs"] },
        connection) |> ignore
    let hub = new HubService(users, tracker, store, NullLogger<HubService>.Instance)
    hub.Context <- CallerContext(connection, user)
    hub

let private readActivity (hub: HubService) project agent = task {
    try
        let! observation = hub.GetAgentActivity(project, agent)
        return Ok observation
    with ex ->
        return Error(ex.GetType().Name + ": " + ex.Message)
}

let private verified (store: OptionalFeedbackStore) user project agent = task {
    let mutable result = None
    let clock = Stopwatch.StartNew()
    while result.IsNone && clock.ElapsedMilliseconds < 5000L do
        try
            let! seen, serverNow = store.GetVerifiedAgentActivity(user, project, agent)
            result <- Some(seen, serverNow)
        with :? InvalidOperationException ->
            do! Task.Delay(30)
    return result |> Option.defaultWith (fun () -> failwith "No verified in-process SQLite activity")
}

let private runCheck rt (hub: HubService) (c: Candidate) =
    let dependencies : Dependencies =
        { Enumerate = (fun () -> Ok [c])
          Current = NativeHealth.current rt
          Activity = readActivity hub
          Recover = NativeHealth.recover rt }
    NativeHealth.checkOnce {Enabled = true; InactivitySeconds = 600} dependencies

[<TestFixture>]
type CoupledHealthPropertyTests() =

    [<TestCase(599, false)>]
    [<TestCase(600, true)>]
    [<TestCase(701, true)>]
    member _.ServerSQLiteFeedsTwoNativeClientsAndGatesAt600Seconds(age: int, expectedStale: bool) =
        fixture (fun one two taskId ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                use store = new OptionalFeedbackStore(Path.Combine(root, "activity.sqlite"))
                let users = UserService()
                let tracker = ClientResponseTracker()
                let hub1 = registeredHub users tracker store "alice" "one"
                let hub2 = registeredHub users tracker store "alice" "two"
                let rt = Runtime(root)
                store.RecordAgentActivity("alice", "one", "Owner",
                                          DateTimeOffset.UtcNow.AddSeconds(-float age)).GetAwaiter().GetResult()
                store.RecordAgentActivity("alice", "two", "Owner",
                                          DateTimeOffset.UtcNow.AddSeconds(-float age)).GetAwaiter().GetResult()
                verified store "alice" "one" "Owner" |> fun t -> t.GetAwaiter().GetResult() |> ignore
                verified store "alice" "two" "Owner" |> fun t -> t.GetAwaiter().GetResult() |> ignore
                let before1, before2 = candidate one "one", candidate two "two"
                Assert.That(recoveryOperationId before1, Is.EqualTo(recoveryOperationId before2))
                let result1 = runCheck rt hub1 before1 |> fun t -> t.GetAwaiter().GetResult()
                let result2 = runCheck rt hub2 before2 |> fun t -> t.GetAwaiter().GetResult()
                if expectedStale then
                    Assert.That(result1.Recoveries + result2.Recoveries, Is.GreaterThanOrEqualTo(1))
                else
                    Assert.That(result1.Recoveries + result2.Recoveries, Is.Zero)
                let _, finalTask, finalAttempt = ReviewWorkflow.getTask two taskId |> success
                Assert.That(finalTask.TaskId, Is.EqualTo(taskId))
                Assert.That(finalAttempt.Value.SpecBlobOid, Is.EqualTo(before1.SpecBlobOid))
                Assert.That(finalAttempt.Value.SourceRef, Is.EqualTo(before1.SourceRef))
                Assert.That(finalAttempt.Value.AssignmentGeneration,
                            Is.EqualTo(before1.Generation + (if expectedStale then 1 else 0)))
            finally
                File.Delete(cfg1); File.Delete(cfg2))

    [<Test>]
    member _.MissingOrWrongUserOrUnregisteredActivityNeverRecovers() =
        fixture (fun one two _ ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                use store = new OptionalFeedbackStore(Path.Combine(root, "activity.sqlite"))
                let users = UserService()
                let tracker = ClientResponseTracker()
                let alice = registeredHub users tracker store "alice" "one"
                let bob = registeredHub users tracker store "bob" "two"
                let rt = Runtime(root)
                store.RecordAgentActivity("bob", "one", "Owner", DateTimeOffset.UtcNow.AddHours(-1.)).GetAwaiter().GetResult()
                verified store "bob" "one" "Owner" |> fun t -> t.GetAwaiter().GetResult() |> ignore
                let c = candidate one "one"
                let missing = runCheck rt alice c |> fun t -> t.GetAwaiter().GetResult()
                Assert.That(missing.Recoveries, Is.Zero)
                Assert.That(missing.Holds, Is.EqualTo(1))
                let unregistered = new HubService(users, tracker, store, NullLogger<HubService>.Instance)
                unregistered.Context <- CallerContext("alice-not-registered", "alice")
                let denied = readActivity unregistered "one" "Owner" |> fun t -> t.GetAwaiter().GetResult()
                Assert.That(denied.IsError, Is.True)
                let foreign = readActivity bob "one" "Owner" |> fun t -> t.GetAwaiter().GetResult()
                Assert.That(foreign.IsOk, Is.True)
                // The wrong user's stale activity does not authorize Alice's recovery.
                Assert.That((runCheck rt alice c).GetAwaiter().GetResult().Recoveries, Is.Zero)
                let current = ReviewWorkflow.getAgentWorkingTurn one "Owner" |> success
                Assert.That(current.State, Is.EqualTo("Working"))
            finally
                File.Delete(cfg1); File.Delete(cfg2))


    [<Test>]
    member _.ConcurrentClientsUseOneRecoveryIdentityAndOnlyOneGenerationTransition() =
        fixture (fun one two taskId ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                use store = new OptionalFeedbackStore(Path.Combine(root, "activity.sqlite"))
                let users = UserService()
                let tracker = ClientResponseTracker()
                let hub1 = registeredHub users tracker store "alice" "one"
                let hub2 = registeredHub users tracker store "alice" "two"
                for project in ["one"; "two"] do
                    store.RecordAgentActivity("alice", project, "Owner",
                        DateTimeOffset.UtcNow.AddSeconds(-701.)).GetAwaiter().GetResult()
                    verified store "alice" project "Owner"
                    |> fun t -> t.GetAwaiter().GetResult() |> ignore
                let oneBefore, twoBefore = candidate one "one", candidate two "two"
                let operationId = recoveryOperationId oneBefore
                Assert.That(recoveryOperationId twoBefore, Is.EqualTo(operationId))
                // Simulate two queued authenticated SignalR requests at the transport
                // boundary. Production HubService/SQLite still performs every read;
                // only transport overlap is serialized to isolate the Git CAS race.
                use activityGate = new SemaphoreSlim(1,1)
                let serializedActivity (hub: HubService) project agent = task {
                    do! activityGate.WaitAsync()
                    try return! readActivity hub project agent
                    finally activityGate.Release() |> ignore
                }
                use barrier = new Barrier(2)
                let runClient hub c =
                    let runtime = Runtime(root)
                    let dependencies : Dependencies =
                        { Enumerate = (fun () -> Ok [c])
                          Current = NativeHealth.current runtime
                          Activity = serializedActivity hub
                          Recover = (fun latest id ->
                              if not (barrier.SignalAndWait(TimeSpan.FromSeconds(30.))) then
                                  failwith "Both native clients must reach the recovery CAS barrier."
                              NativeHealth.recover runtime latest id) }
                    Task.Run(fun () ->
                        NativeHealth.checkOnce { Enabled = true; InactivitySeconds = 600 } dependencies
                        |> fun task -> task.GetAwaiter().GetResult())
                let first = runClient hub1 oneBefore
                let second = runClient hub2 twoBefore
                Task.WaitAll [| first :> Task; second :> Task |]
                let detail = sprintf "first=%A second=%A" first.Result second.Result
                Assert.That(first.Result.Recoveries + second.Result.Recoveries,
                            Is.GreaterThanOrEqualTo(1), detail)
                let _, receipt = LedgerStore.getOperationStatus two operationId |> success
                Assert.That(receipt.IsSome, Is.True)
                Assert.That(receipt.Value.Type, Is.EqualTo("RecoverAgentAssignment"))
                let _, finalTask, finalAttempt = ReviewWorkflow.getTask two taskId |> success
                Assert.That(finalTask.TaskId, Is.EqualTo(taskId))
                Assert.That(finalAttempt.Value.AssignmentGeneration, Is.EqualTo(oneBefore.Generation + 1))
                Assert.That(finalAttempt.Value.SpecBlobOid, Is.EqualTo(oneBefore.SpecBlobOid))
                Assert.That(finalAttempt.Value.AttemptId, Is.EqualTo(oneBefore.AttemptId))
                Assert.That(finalAttempt.Value.SourceRef, Is.EqualTo(oneBefore.SourceRef))
                // Reapplying the identical operation UUID must be idempotent, not a
                // second recovery with another generation or different owner.
                // Replay the EXACT native request, not a changed reason/actor
                // payload (which must be rejected as OperationIdReused).
                let replay = NativeHealth.recover (Runtime(root)) twoBefore operationId
                Assert.That(replay.IsOk, Is.True, sprintf "Original-ID replay: %A" replay)
                let _, _, afterReplay = ReviewWorkflow.getTask one taskId |> success
                Assert.That(afterReplay.Value.AssignmentGeneration, Is.EqualTo(oneBefore.Generation + 1))
            finally
                File.Delete(cfg1); File.Delete(cfg2))

    [<Test>]
    member _.DeterministicSeededNegativeTracesNeverRecoverWithoutTwoFreshAuthoritativeReads() =
        // Every seed gets an independent Git remote, TWO clones, an isolated server
        // SQLite, registered authenticated HubService, and real native decision/recovery.
        // Failures report the seed and minimal fault kind for reproducibility.
        let cases =
            [| "fresh-during-recheck"
               "partial-reconnect"
               "intervening-canonical-yield"
               "clock-rollback-at-transport"
               "future-date-at-transport"
               "unavailable-first-read" |]
        for seed in 0 .. 11 do
            let fault = cases[(Random(0x19A5 + seed).Next(cases.Length))]
            try
                fixture (fun one two taskId ->
                    let root = Path.GetDirectoryName one.RepositoryPath
                    let cfg1, cfg2 = config one, config two
                    try
                        use store = new OptionalFeedbackStore(Path.Combine(root, "activity.sqlite"))
                        let users = UserService()
                        let tracker = ClientResponseTracker()
                        let hub = registeredHub users tracker store "alice" "one"
                        let rt = Runtime(root)
                        if fault <> "unavailable-first-read" then
                            store.RecordAgentActivity("alice", "one", "Owner",
                                DateTimeOffset.UtcNow.AddSeconds(-701.)).GetAwaiter().GetResult()
                            verified store "alice" "one" "Owner"
                            |> fun t -> t.GetAwaiter().GetResult() |> ignore
                        let original = candidate one "one"
                        let mutable reads = 0
                        let mutable firstObservation : AgentActivitySnapshot option = None
                        let activity project agent = task {
                            reads <- reads + 1
                            if fault = "partial-reconnect" && reads = 2 then
                                return Error "Injected disconnected authenticated transport"
                            elif fault = "fresh-during-recheck" && reads = 2 then
                                do! store.RecordAgentActivity("alice", project, agent, DateTimeOffset.UtcNow)
                                let! now = readActivity hub project agent
                                return now
                            else
                                let! real = readActivity hub project agent
                                match real with
                                | Ok value when reads = 1 ->
                                    firstObservation <- Some value
                                    return real
                                | Ok value when reads = 2 && fault = "clock-rollback-at-transport" ->
                                    return Ok { value with ServerNowUtc = firstObservation.Value.ServerNowUtc.AddSeconds(-10.) }
                                | Ok value when reads = 2 && fault = "future-date-at-transport" ->
                                    return Ok { value with LastSeenAtUtc = Some(value.ServerNowUtc.AddSeconds(1.)) }
                                | _ -> return real
                        }
                        let mutable currentChecks = 0
                        let current c =
                            currentChecks <- currentChecks + 1
                            if fault = "intervening-canonical-yield" then
                                let fresh = candidate one "one"
                                ReviewWorkflow.yieldAgent one (Guid.NewGuid()) taskId "Owner"
                                    fresh.AttemptVersion fresh.AgentVersion fresh.Generation
                                    "Fixture intervening user action" "Preserve canonical owner"
                                |> success |> ignore
                            NativeHealth.current rt c
                        let dependencies : Dependencies =
                            { Enumerate = (fun () -> Ok [original])
                              Current = current
                              Activity = activity
                              Recover = NativeHealth.recover rt }
                        let summary =
                            NativeHealth.checkOnce { Enabled = true; InactivitySeconds = 600 } dependencies
                            |> fun t -> t.GetAwaiter().GetResult()
                        Assert.That(summary.Recoveries, Is.Zero, "seed=" + string seed + " fault=" + fault)
                        let _, _, attempt = ReviewWorkflow.getTask two taskId |> success
                        Assert.That(attempt.Value.AssignmentGeneration, Is.EqualTo(original.Generation))
                        Assert.That(attempt.Value.SpecBlobOid, Is.EqualTo(original.SpecBlobOid))
                        Assert.That((summary.Holds >= 1), Is.True)
                        Assert.That(reads, Is.GreaterThanOrEqualTo(1))
                    finally
                        File.Delete(cfg1); File.Delete(cfg2))
            with ex ->
                failwithf "Minimal failing reproduction: seed=%d fault=%s; %s" seed fault (ex.ToString())

    [<Test>]
    member _.IndependentReferenceOracleAndMinimalBoundaryMutationWitness() =
        // An independently specified reference model detects the deliberate
        // '> 600' mutant of the production inclusive 600-second comparator.
        let oracle now seen = (now - seen) >= TimeSpan.FromSeconds 600.
        let mutant now seen = (now - seen) > TimeSpan.FromSeconds 600.
        let reference = DateTimeOffset(2026,11,22,0,0,0,TimeSpan.Zero)
        let seed = 0x19A5
        let rng = Random(seed)
        let ages = [599;600;601] @ [ for _ in 0..63 -> rng.Next(0,1201) ]
        let witnesses =
            ages |> List.filter (fun age ->
                oracle reference (reference.AddSeconds(-float age))
                <> mutant reference (reference.AddSeconds(-float age)))
        Assert.That((witnesses = [600]), Is.True)
        // Integer-domain shrinking reduces any threshold counterexample to 600.
        let shrink age =
            [0 .. age]
            |> List.tryFind (fun smaller ->
                oracle reference (reference.AddSeconds(-float smaller))
                <> mutant reference (reference.AddSeconds(-float smaller)))
        Assert.That(shrink (List.max witnesses), Is.EqualTo(Some 600))


    [<Test>]
    member _.AuthenticatedProductionToolIngressRefreshesSQLiteAndPreventsBothClientRecoveries() =
        fixture (fun one two taskId ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                use store = new OptionalFeedbackStore(Path.Combine(root, "activity.sqlite"))
                let users = UserService()
                let tracker = ClientResponseTracker()
                let hub1 = registeredHub users tracker store "alice" "one"
                let hub2 = registeredHub users tracker store "alice" "two"
                let completed : AgentCommandResponse =
                    { Result = Some "ok"; Error = None; Instructions = []
                      AgentStateChanges = []; InboxActivation = None; PendingJob = None }
                let recipient connection =
                    { new IClientService with
                        member _.ReceiveMessage(_) = Task.CompletedTask
                        member _.ReceiveSourceCommand(_, _, _, _) = Task.CompletedTask
                        member _.ReceiveManualGrantedCommand(_, _, _) = Task.CompletedTask
                        member _.ReceiveManualGrantApproval(_, _, _) = Task.CompletedTask
                        member _.ReceiveCommand(correlationId, _, _) =
                            let session =
                                match users.GetSessionByConnection(connection) with
                                | ValueSome known -> known
                                | _ -> failwith "Expected authenticated registered client session"
                            tracker.Complete(correlationId, session.UserId, connection,
                                             session.Generation, completed) |> ignore
                            Task.CompletedTask
                        member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                        member _.InvalidateAllAgentStates() = Task.CompletedTask
                        member _.RecordAgentActivity(_, _, _) = Task.CompletedTask }
                let clients =
                    { new IHubClients<IClientService> with
                        member _.All = recipient "alice-one"
                        member _.AllExcept(_) = recipient "alice-one"
                        member _.Client(connection) = recipient connection
                        member _.Clients(_) = recipient "alice-one"
                        member _.Group(_) = recipient "alice-one"
                        member _.GroupExcept(_, _) = recipient "alice-one"
                        member _.Groups(_) = recipient "alice-one"
                        member _.User(_) = recipient "alice-one"
                        member _.Users(_) = recipient "alice-one" }
                let hubContext =
                    { new IHubContext<HubService, IClientService> with
                        member _.Clients = clients
                        member _.Groups = Unchecked.defaultof<_> }
                let accessor = Microsoft.AspNetCore.Http.HttpContextAccessor()
                accessor.HttpContext <- Microsoft.AspNetCore.Http.DefaultHttpContext()
                let service =
                    ClientService(hubContext, users, tracker, accessor,
                                  NullLogger<ClientService>.Instance, store)
                let activity project =
                    RunCommandCommand {
                        ProjectName = project; Executable = "dotnet"
                        Args = ["--version"]; Reason = None; WorkingDirectory = None
                        TimeoutSeconds = None; MaxOutputBytes = None }
                // The transport returns a synthetic successful device result; the
                // production authenticated server dispatch and SQLite audit are real.
                for project in ["one"; "two"] do
                    let result =
                        service.SendCommandToUser("alice", activity project,
                            deviceId = project, agentId = "Owner")
                        |> fun t -> t.GetAwaiter().GetResult()
                    Assert.That(result.Result, Is.EqualTo(Some "ok"))
                    let seen, serverNow =
                        verified store "alice" project "Owner"
                        |> fun t -> t.GetAwaiter().GetResult()
                    Assert.That(serverNow - seen, Is.LessThan(TimeSpan.FromSeconds 30.))
                let runtime = Runtime(root)
                let summary1 =
                    runCheck runtime hub1 (candidate one "one")
                    |> fun t -> t.GetAwaiter().GetResult()
                let summary2 =
                    runCheck runtime hub2 (candidate two "two")
                    |> fun t -> t.GetAwaiter().GetResult()
                Assert.That(summary1.Recoveries + summary2.Recoveries, Is.Zero,
                            "Successful authenticated tool ingress is active work.")
                let _, _, attempt = ReviewWorkflow.getTask two taskId |> success
                Assert.That(attempt.Value.AssignmentGeneration, Is.EqualTo(1))
            finally
                File.Delete(cfg1); File.Delete(cfg2))


    [<TestCase(599, false)>]
    [<TestCase(600, true)>]
    [<TestCase(601, true)>]
    member _.ImmutableBoundaryObservationRunsThroughRealNativeHealthAndGitCas(age: int, shouldRecover: bool) =
        // Unlike the SQLite integration test, these are exact immutable server
        // observations injected ONLY at the already-authenticated transport port.
        // Current and Recover are the real native Git-backed implementations.
        fixture (fun one two taskId ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                let initial = candidate one "one"
                let now = DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero)
                let observation : AgentActivitySnapshot =
                    { ServerNowUtc = now
                      LastSeenAtUtc = Some(now.AddSeconds(-float age)) }
                let mutable reads = 0
                let dependencies : Dependencies =
                    { Enumerate = (fun () -> Ok [initial])
                      Current = NativeHealth.current (Runtime(root))
                      Activity = (fun _ _ ->
                          reads <- reads + 1
                          Task.FromResult(Ok observation))
                      Recover = NativeHealth.recover (Runtime(root)) }
                let outcome = NativeHealth.checkOnce
                                  { Enabled = true; InactivitySeconds = 600 } dependencies
                              |> fun t -> t.GetAwaiter().GetResult()
                Assert.That(outcome.Recoveries, Is.EqualTo(if shouldRecover then 1 else 0),
                            sprintf "exact-age=%d NativeHealth SUT result=%A" age outcome)
                Assert.That(reads, Is.EqualTo(if shouldRecover then 2 else 1))
                let _, _, finalAttempt = ReviewWorkflow.getTask two taskId |> success
                Assert.That(finalAttempt.Value.AssignmentGeneration,
                            Is.EqualTo(initial.Generation + (if shouldRecover then 1 else 0)))
                Assert.That(finalAttempt.Value.SpecBlobOid, Is.EqualTo(initial.SpecBlobOid))
                Assert.That(finalAttempt.Value.SourceRef, Is.EqualTo(initial.SourceRef))
                let _, receipt = LedgerStore.getOperationStatus two (recoveryOperationId initial) |> success
                Assert.That(receipt.IsSome, Is.EqualTo(shouldRecover))
            finally
                File.Delete(cfg1); File.Delete(cfg2))

    [<Test>]
    member _.TransportPortNegativeCanaryFailsActualNativeHealthBoundaryAssertion() =
        // Deliberately mutate ONLY a disposable transport observation by +1s
        // in LastSeenAtUtc. 600s -> 599s must suppress the REAL SUT recovery,
        // and an assertion expecting recovery must RED. A local comparator does
        // not provide this evidence; the real Current/Recover/Git paths do.
        fixture (fun one two taskId ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                let original = candidate one "one"
                let now = DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero)
                let mutated : AgentActivitySnapshot =
                    { ServerNowUtc = now; LastSeenAtUtc = Some(now.AddSeconds(-599.)) }
                let dependencies : Dependencies =
                    { Enumerate = (fun () -> Ok [original])
                      Current = NativeHealth.current (Runtime(root))
                      Activity = (fun _ _ -> Task.FromResult(Ok mutated))
                      Recover = NativeHealth.recover (Runtime(root)) }
                let outcome = NativeHealth.checkOnce
                                  { Enabled = true; InactivitySeconds = 600 } dependencies
                              |> fun t -> t.GetAwaiter().GetResult()
                let rejectedGate = Assert.Throws<AssertionException>(
                    TestDelegate(fun () ->
                        Assert.That(outcome.Recoveries, Is.EqualTo(1),
                                    "600s exact-boundary gate must reject +1s last-seen mutant")))
                Assert.That(rejectedGate, Is.Not.Null)
                Assert.That(outcome.Recoveries, Is.Zero)
                let _, _, attempt = ReviewWorkflow.getTask two taskId |> success
                Assert.That(attempt.Value.AssignmentGeneration, Is.EqualTo(original.Generation))
                let _, receipt = LedgerStore.getOperationStatus two (recoveryOperationId original) |> success
                Assert.That(receipt.IsNone, Is.True)
            finally
                File.Delete(cfg1); File.Delete(cfg2))

    [<Test>]
    member _.OwnerTransferRevokesStaleNativeCandidateAndPreservesNewCanonicalOwner() =
        fixture (fun one two taskId ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                use store = new OptionalFeedbackStore(Path.Combine(root, "activity.sqlite"))
                let users = UserService()
                let tracker = ClientResponseTracker()
                let hub = registeredHub users tracker store "alice" "one"
                let old = candidate two "one"
                store.RecordAgentActivity("alice", "one", "Owner",
                    DateTimeOffset.UtcNow.AddSeconds(-701.)).GetAwaiter().GetResult()
                verified store "alice" "one" "Owner"
                |> fun t -> t.GetAwaiter().GetResult() |> ignore
                ReviewWorkflow.registerAgent one (Guid.NewGuid()) "Successor" |> success |> ignore
                let _, task, attempt = ReviewWorkflow.getTask one taskId |> success
                let owner = ReviewWorkflow.getAgentWorkingTurn one "Owner" |> success
                let successorAttempt = Guid.NewGuid()
                ReviewWorkflow.transferAttemptOwner one (Guid.NewGuid()) taskId
                    successorAttempt "Successor" task.Version attempt.Value.Version
                    owner.Version attempt.Value.AssignmentGeneration "Canonical handoff before stale health"
                |> success |> ignore
                let summary = runCheck (Runtime(root)) hub old
                              |> fun t -> t.GetAwaiter().GetResult()
                Assert.That(summary.Recoveries, Is.Zero)
                let _, finalTask, finalAttempt = ReviewWorkflow.getTask two taskId |> success
                Assert.That(finalTask.AttemptId, Is.EqualTo(successorAttempt.ToString("N")))
                Assert.That(finalAttempt.Value.OwnerAgentId, Is.EqualTo("Successor"))
                Assert.That(finalAttempt.Value.Phase, Is.EqualTo("Queued"))
                Assert.That(finalAttempt.Value.SpecBlobOid, Is.EqualTo(old.SpecBlobOid))
                Assert.That(finalAttempt.Value.SourceRef, Is.EqualTo(old.SourceRef))
                let oldRecovery = NativeHealth.recover (Runtime(root)) old (recoveryOperationId old)
                Assert.That(oldRecovery.IsError, Is.True,
                            "A pre-transfer old-owner candidate must not recover the successor")
                let _, after, live = ReviewWorkflow.getTask two taskId |> success
                Assert.That(after.AttemptId, Is.EqualTo(successorAttempt.ToString("N")))
                Assert.That(live.Value.OwnerAgentId, Is.EqualTo("Successor"))
                let _, receipt = LedgerStore.getOperationStatus two (recoveryOperationId old) |> success
                Assert.That(receipt.IsNone, Is.True)
            finally
                File.Delete(cfg1); File.Delete(cfg2))

    [<Test>]
    member _.CanonicalRoleAttachmentCannotForgeWorkingAssignmentOrRecoveryOwner() =
        fixture (fun one two taskId ->
            let root = Path.GetDirectoryName one.RepositoryPath
            let cfg1, cfg2 = config one, config two
            try
                let original = candidate two "one"
                ReviewWorkflow.registerAgent one (Guid.NewGuid()) "Successor" |> success |> ignore
                Client.RoleInstructions.RoleInstructions.createRole one
                    "FixtureAuditor" (Guid.NewGuid()) "Auditor" "Disposable role" "Never authorize native recovery"
                |> success |> ignore
                Client.RoleInstructions.RoleInstructions.attachRole one
                    "FixtureAuditor" (Guid.NewGuid()) "Successor" "Auditor" 0
                |> success |> ignore
                // A role attachment is not a transfer, nor a generation change.
                let stillOwner = NativeHealth.current (Runtime(root)) original |> success
                Assert.That(stillOwner.IsSome, Is.True)
                Assert.That(stillOwner.Value.AgentId, Is.EqualTo("Owner"))
                let forged = { original with AgentId = "Successor" }
                let forgedCurrent = NativeHealth.current (Runtime(root)) forged |> success
                Assert.That(forgedCurrent.IsNone, Is.True)
                let denied = NativeHealth.recover (Runtime(root)) forged (recoveryOperationId forged)
                Assert.That(denied.IsError, Is.True)
                let _, task, attempt = ReviewWorkflow.getTask two taskId |> success
                Assert.That(task.TaskId, Is.EqualTo(taskId))
                Assert.That(attempt.Value.OwnerAgentId, Is.EqualTo("Owner"))
                Assert.That(attempt.Value.AssignmentGeneration, Is.EqualTo(original.Generation))
                Assert.That(attempt.Value.SpecBlobOid, Is.EqualTo(original.SpecBlobOid))
                let _, receipt = LedgerStore.getOperationStatus two (recoveryOperationId forged) |> success
                Assert.That(receipt.IsNone, Is.True)
            finally
                File.Delete(cfg1); File.Delete(cfg2))


    [<Test>]
    member _.OfflineAspNetBearerIssuerSignatureScopeAndRealSQLiteHubBinding() =
        // Real ASP.NET JwtBearerHandler + IAuthorizationService policy on an
        // offline DefaultHttpContext. No fake ClaimsPrincipal is used for token
        // authentication. This is component-level, NOT a full live Program host.
        let issuerOptions : Server.JarvisOptions =
            { Auth0Domain = "issuer.fixture.example.test"
              Audience = "https://jarvis-api.fixture.example.test"
              OpenAIAppsChallenge = ""
              EnableOperatorConversations = false }
        let issuer = Server.Auth.issuer issuerOptions
        let audience = issuerOptions.Audience
        let key = Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("offline-fixture-hmac-key-0123456789-abcdefghijklmnopqrstuvwxyz"))
        let wrongKey = Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("offline-fixture-wrong-key-0123456789-abcdefghijklmnopqrstuvwxyz"))
        let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services)
        |> ignore
        let authentication = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        authentication.AddJwtBearer(fun options ->
                options.MapInboundClaims <- false
                options.TokenValidationParameters <-
                    Microsoft.IdentityModel.Tokens.TokenValidationParameters(
                        ValidateIssuer = true,
                        ValidIssuer = issuer,
                        ValidateAudience = true,
                        ValidAudiences = Server.Auth.validAudiences issuerOptions,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = key,
                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        ClockSkew = TimeSpan.Zero))
        |> ignore
        services.AddAuthorization(fun options ->
                options.AddPolicy(Server.Auth.ClientConnectPolicy, fun policy ->
                    policy.RequireAuthenticatedUser() |> ignore
                    policy.RequireAssertion(
                        Func<Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext,bool>(
                            fun context -> Server.Auth.hasScope Server.Auth.ClientConnect context.User))
                    |> ignore))
        |> ignore
        use provider = services.BuildServiceProvider()
        let issue issuer scope signingKey subject =
            let claims = [ Claim("sub", subject); Claim("scope", scope) ]
            let token = System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer = issuer, audience = audience, claims = claims,
                notBefore = DateTime.UtcNow.AddMinutes(-1.),
                expires = DateTime.UtcNow.AddMinutes(10.),
                signingCredentials = Microsoft.IdentityModel.Tokens.SigningCredentials(
                    signingKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256))
            System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token)
        let validate token =
            // HandlerProvider caches schemes within one DI scope; a fresh
            // ASP.NET request MUST get a fresh service scope, or the earlier
            // authenticated principal could be spuriously reused.
            use requestScope = provider.CreateScope()
            let context = Microsoft.AspNetCore.Http.DefaultHttpContext()
            context.RequestServices <- requestScope.ServiceProvider
            context.Request.Headers.Authorization <- Microsoft.Extensions.Primitives.StringValues("Bearer " + token)
            let auth =
                (Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(
                    context, JwtBearerDefaults.AuthenticationScheme)).GetAwaiter().GetResult()
            if not auth.Succeeded then None
            else
                context.User <- auth.Principal
                let authorization = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>()
                let result =
                    (authorization.AuthorizeAsync(context.User, null,
                                                  Server.Auth.ClientConnectPolicy)).GetAwaiter().GetResult()
                if result.Succeeded then Some context.User else None
        let valid = issue issuer Server.Auth.ClientConnect key "alice"
        let accepted = validate valid
        Assert.That(accepted.IsSome, Is.True, "Signed issuer/scope must pass actual ASP.NET bearer+policy")
        Assert.That(Server.Auth.tryUserId accepted.Value, Is.EqualTo(Some "alice"))
        let badCases =
            [ "wrong issuer", issue "https://attacker.fixture.example.test/" Server.Auth.ClientConnect key "alice"
              "wrong signature", issue issuer Server.Auth.ClientConnect wrongKey "alice"
              "missing scope", issue issuer Server.Auth.WorkspaceRead key "alice" ]
        for (label, token) in badCases do
            Assert.That((validate token).IsNone, Is.True, label)
        // ClientConnectPolicy checks auth + scope, NOT a subject claim. Do
        // not invent a production requirement; the registered Hub user gate
        // must instead block an absent/unregistered identity.
        let subjectless = validate (issue issuer Server.Auth.ClientConnect key "")
        Assert.That(subjectless.IsSome, Is.True)
        Assert.That(Server.Auth.tryUserId subjectless.Value, Is.EqualTo(None))
        // A verified principal is explicitly propagated to the existing real
        // SQLite-backed HubService after bearer/scope gates pass; a mismatched
        // session or unregistered identity still cannot read Alice's activity.
        fixture (fun one two _ ->
            let root = Path.GetDirectoryName one.RepositoryPath
            use store = new OptionalFeedbackStore(Path.Combine(root, "jwt-activity.sqlite"))
            let users = UserService()
            let tracker = ClientResponseTracker()
            store.RecordAgentActivity("alice", "one", "Owner", DateTimeOffset.UtcNow)
                .GetAwaiter().GetResult()
            verified store "alice" "one" "Owner" |> fun t -> t.GetAwaiter().GetResult() |> ignore
            users.TransportConnected("alice", "verified-token-connection")
            users.Register("alice",
                { DeviceId = "one"; DeviceName = "one"; ProtocolVersion = AgentProtocol.version
                  ClientVersion = "test"; SupportedCommands = ["RunCommand"; "ListJobs"] },
                "verified-token-connection") |> ignore
            let hub = new HubService(users, tracker, store, NullLogger<HubService>.Instance)
            hub.Context <- CallerContext("verified-token-connection", "alice", accepted.Value)
            let realRead = readActivity hub "one" "Owner" |> fun t -> t.GetAwaiter().GetResult()
            Assert.That(realRead.IsOk, Is.True)
            let unexpectedUser = issue issuer Server.Auth.ClientConnect key "mallory"
            let mallory = validate unexpectedUser
            Assert.That(mallory.IsSome, Is.True, "Valid token is not itself authorization to another session")
            hub.Context <- CallerContext("verified-token-connection", "alice", mallory.Value)
            let denied = readActivity hub "one" "Owner" |> fun t -> t.GetAwaiter().GetResult()
            Assert.That(denied.IsError, Is.True, "JWT subject cannot spoof an existing Alice device"))
