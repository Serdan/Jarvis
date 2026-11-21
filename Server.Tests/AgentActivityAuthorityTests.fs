module AgentActivityAuthorityTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Claims
open System.Threading
open System.Threading.Tasks
open Common
open Common.SignalR
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.SignalR
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open Server.Services

let private scratch () =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-health-activity-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    directory, Path.Combine(directory, "activity.sqlite")

let private expectUnavailable (operation: Task<'T>) = task {
    try
        let! _ = operation
        Assert.Fail("Missing or incomplete SQLite evidence must never authorize idleness.")
    with
    | :? InvalidOperationException as ex ->
        Assert.That(ex.Message, Does.Contain("ActivityProofUnavailable"))
}

let private expectUnauthorized (operation: Task<'T>) = task {
    try
        let! _ = operation
        Assert.Fail("An unregistered client must not read activity.")
    with :? UnauthorizedAccessException -> ()
}

let private awaitVerified (store: OptionalFeedbackStore) user project agent = task {
    let watch = Stopwatch.StartNew()
    let mutable result : (DateTimeOffset * DateTimeOffset) option = None
    while result.IsNone && watch.Elapsed < TimeSpan.FromSeconds 3. do
        try
            let! seen = store.GetVerifiedAgentActivity(user, project, agent)
            result <- Some seen
        with :? InvalidOperationException ->
            do! Task.Delay(20)
    match result with
    | Some evidence -> return evidence
    | None -> return failwith "An admitted successful agent call did not produce verified SQLite activity."
}

type private CallerContext(connectionId: string, userId: string) =
    inherit HubCallerContext()
    let items = Dictionary<obj, obj>() :> IDictionary<obj, obj>
    let features = FeatureCollection() :> IFeatureCollection
    let principal =
        ClaimsPrincipal(ClaimsIdentity([ Claim("sub", userId) ], "test"))
    override _.ConnectionId = connectionId
    override _.UserIdentifier = userId
    override _.User = principal
    override _.Items = items
    override _.Features = features
    override _.ConnectionAborted = CancellationToken.None
    override _.Abort() = ()

let private registeredHub users tracker store user connection =
    let hub = new HubService(users, tracker, store, NullLogger<HubService>.Instance)
    hub.Context <- CallerContext(connection, user)
    hub

[<Test>]
let authenticated_client_ingress_and_registered_hub_aggregate_by_user_project_agent () = task {
    let directory, path = scratch ()
    try
        use store = new OptionalFeedbackStore(path, timeout = TimeSpan.FromSeconds 2.)
        let users = UserService()
        let tracker = ClientResponseTracker()
        let register user device connection =
            users.TransportConnected(user, connection)
            users.Register(user, {
                DeviceId = device; DeviceName = device
                ProtocolVersion = AgentProtocol.version; ClientVersion = "test"
                SupportedCommands = ["RunCommand"; "ListJobs"] }, connection) |> ignore
        register "alice" "desktop" "alice-desktop"
        register "alice" "laptop" "alice-laptop"
        register "bob" "desktop" "bob-desktop"

        let completed = { Result = Some "ok"; Error = None; Instructions = []
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
                        | _ -> failwith "Expected an authenticated registered session."
                    tracker.Complete(correlationId, session.UserId, connection, session.Generation, completed)
                    |> ignore
                    Task.CompletedTask
                member _.InvalidateAgentStates(_, _) = Task.CompletedTask
                member _.InvalidateAllAgentStates() = Task.CompletedTask
                member _.RecordAgentActivity(_, _, _) = Task.CompletedTask }
        let clients =
            { new IHubClients<IClientService> with
                member _.All = recipient "alice-desktop"
                member _.AllExcept(_) = recipient "alice-desktop"
                member _.Client(connection) = recipient connection
                member _.Clients(_) = recipient "alice-desktop"
                member _.Group(_) = recipient "alice-desktop"
                member _.GroupExcept(_, _) = recipient "alice-desktop"
                member _.Groups(_) = recipient "alice-desktop"
                member _.User(_) = recipient "alice-desktop"
                member _.Users(_) = recipient "alice-desktop" }
        let hubContext =
            { new IHubContext<HubService, IClientService> with
                member _.Clients = clients
                member _.Groups = Unchecked.defaultof<_> }
        let accessor = HttpContextAccessor()
        accessor.HttpContext <- DefaultHttpContext()
        let service = ClientService(hubContext, users, tracker, accessor,
                                    NullLogger<ClientService>.Instance, store)
        let activity project =
            RunCommandCommand {
                ProjectName = project; Executable = "dotnet"; Args = ["--version"]
                Reason = None; WorkingDirectory = None
                TimeoutSeconds = None; MaxOutputBytes = None }
        let! delivered = service.SendCommandToUser("alice", activity "Jarvis::worktree",
                                                   deviceId = "desktop", agentId = "Worker 7")
        Assert.That(delivered.Result, Is.EqualTo(Some "ok"))
        let! first, _ = awaitVerified store "alice" "Jarvis" "Worker 7"
        let aliceHub = registeredHub users tracker store "alice" "alice-laptop"
        let beforeRead = DateTimeOffset.UtcNow
        let! snapshot = aliceHub.GetAgentActivity("Jarvis", "Worker 7")
        let afterRead = DateTimeOffset.UtcNow
        Assert.That(snapshot.LastSeenAtUtc, Is.EqualTo(Some first))
        Assert.That(snapshot.ServerNowUtc, Is.InRange(beforeRead, afterRead))
        Assert.That(first, Is.LessThanOrEqualTo(snapshot.ServerNowUtc))

        let bobHub = registeredHub users tracker store "bob" "bob-desktop"
        do! expectUnavailable (bobHub.GetAgentActivity("Jarvis", "Worker 7"))
        do! expectUnavailable (aliceHub.GetAgentActivity("Jarvis", "Worker 8"))
        do! expectUnavailable (aliceHub.GetAgentActivity("Elsewhere", "Worker 7"))
        let unregistered = registeredHub users tracker store "alice" "unknown-connection"
        do! expectUnauthorized (unregistered.GetAgentActivity("Jarvis", "Worker 7"))

        let observer = ListJobsCommand { ProjectName = Some "Jarvis"; IncludeCompleted = true }
        let! observed = service.SendCommandToUser("alice", observer,
                                                  deviceId = "desktop", agentId = "Worker 7")
        Assert.That(observed.Result, Is.EqualTo(Some "ok"))
        let! afterObserver, _ = store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7")
        Assert.That(afterObserver, Is.EqualTo(first), "Observer-only polling must not refresh activity.")

        let! later = service.SendCommandToUser("alice", activity "Jarvis",
                                               deviceId = "laptop", agentId = "Worker 7")
        Assert.That(later.Result, Is.EqualTo(Some "ok"))
        let! second, _ = awaitVerified store "alice" "Jarvis" "Worker 7"
        Assert.That(second, Is.GreaterThanOrEqualTo(first), "Both devices share one monotonic clock.")

        // Server receipt is activity even if the selected device is gone.
        // The failed tool must not be interpreted as an idle agent.
        let! failedRoute = service.SendCommandToUser("alice", activity "Jarvis",
                                                     deviceId = "missing", agentId = "Worker 9")
        Assert.That(failedRoute.Error.IsSome, Is.True)
        let! _, _ = awaitVerified store "alice" "Jarvis" "Worker 9"

        // A canceled browser transport is not proof that the backend agent
        // was inactive: the authenticated call reached ingress.
        use canceled = new CancellationTokenSource()
        canceled.Cancel()
        accessor.HttpContext.RequestAborted <- canceled.Token
        try
            let! _ = service.SendCommandToUser("alice", activity "Jarvis",
                                               deviceId = "desktop", agentId = "Worker canceled")
            Assert.Fail("Expected canceled transport to stop before dispatch.")
        with :? OperationCanceledException -> ()
        accessor.HttpContext.RequestAborted <- CancellationToken.None
        let! _, _ = awaitVerified store "alice" "Jarvis" "Worker canceled"

        let! bobCall = service.SendCommandToUser("bob", activity "Jarvis",
                                                 deviceId = "desktop", agentId = "Worker 7")
        Assert.That(bobCall.Result, Is.EqualTo(Some "ok"))
        let! _, _ = awaitVerified store "bob" "Jarvis" "Worker 7"
        let! aliceAgain = aliceHub.GetAgentActivity("Jarvis", "Worker 7")
        Assert.That(aliceAgain.LastSeenAtUtc, Is.EqualTo(Some second),
                    "A different authenticated user must not refresh Alice's actor.")
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let sqlite_activity_is_monotonic_but_prior_process_rows_never_establish_coverage () = task {
    let directory, path = scratch ()
    try
        let first = DateTimeOffset.UtcNow.AddMinutes(-4.)
        do!
            task {
                use store = new OptionalFeedbackStore(path, timeout = TimeSpan.FromSeconds 2.)
                do! store.RecordAgentActivity("alice", "Jarvis::branch", "Manager", first)
                do! store.RecordAgentActivity("alice", "Jarvis", "Manager", first.AddMinutes(-2.))
                do! store.RecordAgentActivity("alice", "Jarvis", "Manager", first.AddMinutes(2.))
                let! recorded, now = store.GetVerifiedAgentActivity("alice", "Jarvis", "Manager")
                Assert.That(recorded, Is.EqualTo(first.AddMinutes(2.)))
                Assert.That(now, Is.GreaterThanOrEqualTo(recorded))
                do! expectUnavailable (store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 1"))
                do! expectUnavailable (store.GetVerifiedAgentActivity("alice", "Other", "Manager"))
            }
        // Another server process could have missed a write just before restart;
        // historical rows must not be treated as a complete inactivity history.
        use restarted = new OptionalFeedbackStore(path, timeout = TimeSpan.FromSeconds 2.)
        do! expectUnavailable (restarted.GetVerifiedAgentActivity("alice", "Jarvis", "Manager"))
        let next = DateTimeOffset.UtcNow
        do! restarted.RecordAgentActivity("alice", "Jarvis", "Manager", next)
        let! observed, _ = restarted.GetVerifiedAgentActivity("alice", "Jarvis", "Manager")
        Assert.That(observed, Is.EqualTo(next))
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let failed_busy_and_quota_activity_writes_invalidate_read_without_blocking () = task {
    let directory, path = scratch ()
    use gate = new ManualResetEventSlim(false)
    try
        use store = new OptionalFeedbackStore(path, timeout = TimeSpan.FromMilliseconds 150.)
        let first = DateTimeOffset.UtcNow.AddMinutes(-1.)
        do! store.RecordAgentActivity("alice", "Jarvis", "Worker 7", first)
        let! _, _ = store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7")
        let holding = store.TryExecute(fun _ -> gate.Wait(TimeSpan.FromSeconds 3.) |> ignore)
        let watch = Stopwatch.StartNew()
        do! store.RecordAgentActivity("alice", "Jarvis", "Worker 7", DateTimeOffset.UtcNow)
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.))
        do! expectUnavailable (store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7"))
        gate.Set()
        let! _ = holding
        do! Task.Delay(100)
        let fresh = DateTimeOffset.UtcNow
        do! store.RecordAgentActivity("alice", "Jarvis", "Worker 7", fresh)
        let! recovered, _ = store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7")
        Assert.That(recovered, Is.GreaterThanOrEqualTo(fresh))

        let quotaPath = Path.Combine(directory, "full.sqlite")
        File.WriteAllBytes(quotaPath, Array.zeroCreate<byte> (320 * 1024))
        use quota = new OptionalFeedbackStore(quotaPath, maxDiskBytes = 256L * 1024L)
        do! quota.RecordAgentActivity("alice", "Jarvis", "Worker 7", DateTimeOffset.UtcNow)
        do! expectUnavailable (quota.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7"))
    finally
        gate.Set()
        Directory.Delete(directory, true)
}

[<Test>]
let lost_sqlite_row_and_failed_storage_do_not_convert_old_activity_into_idle () = task {
    let directory, path = scratch ()
    try
        use store = new OptionalFeedbackStore(path, timeout = TimeSpan.FromSeconds 2.)
        do! store.RecordAgentActivity("alice", "Jarvis", "Worker 7", DateTimeOffset.UtcNow.AddMinutes(-2.))
        let! _, _ = store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7")
        let sql statement =
            use connection = new SqliteConnection("Data Source=" + path)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- statement
            command.ExecuteNonQuery() |> ignore
        sql "DELETE FROM attributed_agent_activity;"
        do! expectUnavailable (store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7"))
        sql "DROP TABLE attributed_agent_activity;"
        do! store.RecordAgentActivity("alice", "Jarvis", "Worker 7", DateTimeOffset.UtcNow)
        do! expectUnavailable (store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7"))
        sql """CREATE TABLE attributed_agent_activity (
            user_id TEXT NOT NULL, project_name TEXT NOT NULL,
            agent_id TEXT NOT NULL, last_seen_at TEXT NOT NULL,
            PRIMARY KEY(user_id, project_name, agent_id));"""
        let repaired = DateTimeOffset.UtcNow
        do! store.RecordAgentActivity("alice", "Jarvis", "Worker 7", repaired)
        let! observed, _ = store.GetVerifiedAgentActivity("alice", "Jarvis", "Worker 7")
        Assert.That(observed, Is.EqualTo(repaired))
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let actual_sqlite_writer_lock_poisoned_activity_requires_new_success () = task {
    let directory, path = scratch ()
    try
        use store = new OptionalFeedbackStore(path, timeout = TimeSpan.FromMilliseconds 150.)
        do! store.RecordAgentActivity("alice", "Jarvis", "Manager", DateTimeOffset.UtcNow.AddMinutes(-1.))
        let! _, _ = store.GetVerifiedAgentActivity("alice", "Jarvis", "Manager")
        do!
            task {
                use writer = new SqliteConnection("Data Source=" + path + ";Cache=Shared")
                writer.Open()
                use transaction = writer.BeginTransaction()
                use command = writer.CreateCommand()
                command.Transaction <- transaction
                command.CommandText <-
                    "UPDATE attributed_agent_activity SET last_seen_at = last_seen_at WHERE user_id = 'alice';"
                command.ExecuteNonQuery() |> ignore
                let started = Stopwatch.StartNew()
                do! store.RecordAgentActivity("alice", "Jarvis", "Manager", DateTimeOffset.UtcNow)
                Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.))
                do! expectUnavailable (store.GetVerifiedAgentActivity("alice", "Jarvis", "Manager"))
                transaction.Rollback()
            }
        // An uncertain timed-out worker may retain the single-flight slot
        // until the SQLite busy timeout expires. No requeue/replay occurs.
        do! Task.Delay(1300)
        let fresh = DateTimeOffset.UtcNow
        do! store.RecordAgentActivity("alice", "Jarvis", "Manager", fresh)
        let! recorded, _ = store.GetVerifiedAgentActivity("alice", "Jarvis", "Manager")
        Assert.That(recorded, Is.GreaterThanOrEqualTo(fresh))
    finally
        Directory.Delete(directory, true)
}
