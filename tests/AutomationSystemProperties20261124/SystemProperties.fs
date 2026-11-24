module AutomationSystemProperties20261124

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.NativeHealth
open Client.NativeHealth.NativeHealth
open Client.AutomationChallengeWakeAdapter
open Common
open Common.SignalR
open Client.WorkQueueSchedulerHostLoop
open Client.WorkQueueReactiveWake
open Client.NativeSchedulerAccountReservation
open AutomationHealthHostedFixture

// Independent abstract ownership model. The state is driven by commands and
// preconditions, never by copying the observed canonical attempt phase.
type Phase = Working | Waiting | CancelledWaiting | Ready
type Model = { Phase: Phase; Generation: int; Age: int option }
type private ReservationExpectation =
    { Original: NativeDispatchCandidate
      OriginalOperation: string
      Status: string
      Generation: int64
      Fence: string
      Session: string
      Terminal: string }

type Command =
    | Advance of int
    | Activity
    | ResetActivity
    | CheckHealth
    | CheckStale
    | Block
    | Resolve
    | Resume
    | Checkpoint
    | ReplayResolve
    | Cancel

let next command state =
    match command, state.Phase with
    | Advance seconds, _ -> { state with Age = Option.map ((+) seconds) state.Age }
    | Activity, _ -> { state with Age = Some 0 }
    | ResetActivity, _ -> { state with Age = None }
    | CheckHealth, Working when state.Age |> Option.exists (fun n -> n >= 600) ->
        { state with Phase = Ready; Generation = state.Generation + 1 }
    | Block, Working -> { state with Phase = Waiting }
    | Resolve, Waiting -> { state with Phase = Ready; Generation = state.Generation + 1 }
    | Cancel, Waiting -> { state with Phase = CancelledWaiting }
    | Resume, Ready -> { state with Phase = Working }
    | _ -> state

exception PropertyViolation of label: string * detail: string

let private equal (label: string) (expected: 'a) (actual: 'a) =
    if actual <> expected then
        raise (PropertyViolation(label, sprintf "expected %A, actual %A" expected actual))

type private Fixture = {
    First: LedgerBinding
    Second: LedgerBinding
    Runtimes: Client.Runtime array
    Original: Candidate
    mutable State: Model
    mutable Previous: Candidate option
    mutable Gate: ChallengeCreated option
    mutable Resolved: (ChallengeCreated * Guid * ChallengeWakeCandidate) option
}

let private snapshot binding f =
    let s = LedgerStore.fetch binding |> success
    let t, a = ReviewWorkflow.getTaskAt binding s f.Original.TaskId |> success
    t, a.Value

let private observe f =
    for binding in [f.First; f.Second] do
        let snap = LedgerStore.fetch binding |> success
        let t, attempt = ReviewWorkflow.getTaskAt binding snap f.Original.TaskId |> success
        let a = attempt.Value
        let inbox = ReviewWorkflow.getAgentInboxAt binding snap f.Original.AgentId |> success
        let phase =
            match f.State.Phase with
            | Working -> "Started"
            | Waiting | CancelledWaiting -> "Blocked"
            | Ready -> "Queued"
        equal "attempt phase" phase a.Phase
        equal "assignment generation" f.State.Generation a.AssignmentGeneration
        equal "immutable task id" f.Original.TaskId t.TaskId
        equal "immutable attempt" f.Original.AttemptId a.AttemptId
        equal "owner" f.Original.AgentId a.OwnerAgentId
        equal "spec" f.Original.SpecBlobOid a.SpecBlobOid
        equal "source ref" f.Original.SourceRef a.SourceRef
        let assignment = inbox.Agent.CurrentAssignment
        match f.State.Phase with
        | Working | Ready ->
            equal "active focus exists" false (isNull (box assignment))
            equal "active focus exact task" f.Original.TaskId assignment.TaskId
            equal "active focus exact attempt" f.Original.AttemptId assignment.AttemptId
            equal "no active challenge wait" 0 inbox.BlockedChallengeWaits.Length
            equal "no blocked attempt" 0 inbox.Blocked.Length
            if f.State.Phase = Working then
                equal "working ownership" "Working" inbox.Agent.Execution
                equal "no additional runnable ownership" 0 inbox.RunnableQueue.Length
                equal "working exact attempt" (Some f.Original.AttemptId) (inbox.Working |> Option.map (fun item -> item.Attempt.AttemptId))
            else
                equal "ready ownership" "Ready" inbox.Agent.State
                equal "ready stopped execution" "Stopped" inbox.Agent.Execution
                equal "one runnable ownership" [f.Original.AttemptId] (inbox.RunnableQueue |> List.map (fun item -> item.Attempt.AttemptId))
                equal "not simultaneously Working" true inbox.Working.IsNone
        | Waiting | CancelledWaiting ->
            equal "blocked no active focus" true (isNull (box assignment))
            equal "blocked no runnable ownership" 0 inbox.RunnableQueue.Length
            equal "blocked not Working" false (inbox.Agent.Execution = "Working")
            equal "one blocked ownership" [f.Original.AttemptId] (inbox.Blocked |> List.map (fun item -> item.Attempt.AttemptId))
            let expectedWait = [f.Original.TaskId, f.Original.AttemptId, f.Original.AgentId, [f.Gate.Value.ChallengeId]]
            equal "exact canonical indexed wait" expectedWait
                (inbox.BlockedChallengeWaits |> List.map (fun w -> w.TaskId, w.AttemptId, w.OwnerAgentId, w.ChallengeIds))

let private current (f: Fixture) client =
    NativeHealth.current f.Runtimes[client] f.Original |> success

let private health f client candidate =
    let now = DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)
    let observation : AgentActivitySnapshot =
        { ServerNowUtc = now; LastSeenAtUtc = f.State.Age |> Option.map (fun age -> now.AddSeconds(-float age)) }
    let dependencies : Dependencies =
        { Enumerate = fun () -> Ok [candidate]
          Current = NativeHealth.current f.Runtimes[client]
          Activity = fun _ _ -> Task.FromResult(Ok observation)
          Recover = NativeHealth.recover f.Runtimes[client] }
    NativeHealth.checkOnce { Enabled = true; InactivitySeconds = 600 } dependencies
    |> fun t -> t.GetAwaiter().GetResult()

let private step f client command =
    let binding = if client = 0 then f.First else f.Second
    let reader = if client = 0 then f.Second else f.First
    let before = f.State
    let expected = next command before
    let taskId = f.Original.TaskId
    let t, a = snapshot binding f
    let owner = (ReviewWorkflow.getAgentInbox binding f.Original.AgentId |> success).Agent
    let head = (LedgerStore.fetch binding |> success).Commit
    match command with
    | CheckHealth ->
        match current f client with
        | Some c ->
            f.Previous <- Some c
            let result = health f client c
            equal "Health recoveries" (if expected.Generation <> before.Generation then 1 else 0) result.Recoveries
        | None ->
            equal "Health cannot find non-working attempt" true (before.Phase <> Working)
    | CheckStale ->
        match f.Previous with
        | Some c ->
            let actual = current f client
            // Only test an old candidate after its exact CAS lineage changes.
            if actual |> Option.forall (fun live -> {live with GitHead = c.GitHead} <> c) then
                let subject =
                    if Environment.GetEnvironmentVariable("JARVIS_SYSTEM_STALE_MUTANT") = "1" then
                        actual |> Option.defaultValue c
                    else c
                let result = health f client subject
                equal "stale recovery denied" 0 result.Recoveries
                equal "stale recovery did not write" head (LedgerStore.fetch reader |> success).Commit
        | None -> ()
    | Block when before.Phase = Working ->
        f.Previous <- current f client
        let gate =
            ChallengeWorkflow.create binding (Guid.NewGuid()) f.Original.AgentId "system property" taskId
                [{ OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "verified dependency" }]
            |> success
        ChallengeWorkflow.blockAttempt reader (Guid.NewGuid()) f.Original.AgentId taskId a.AttemptId
            t.Version a.Version owner.Version before.Generation "waiting" [gate.ChallengeId]
        |> success |> ignore
        f.Gate <- Some gate
        f.Resolved <- None
    | Resolve when before.Phase = Waiting ->
        let gate = f.Gate.Value
        let op = Guid.NewGuid()
        let result = ChallengeWorkflow.resolve binding op "Lead" gate.ChallengeId gate.ConditionIds.Head 1 "evidence" "" |> success
        let locator : ChallengeWakeCandidate =
            { ProjectId = binding.ProjectId; LedgerId = binding.LedgerId; Epoch = binding.Epoch
              Commit = result.CurrentHead; OperationId = op; ChallengeId = gate.ChallengeId
              ConditionId = gate.ConditionIds.Head; TaskId = taskId; AttemptId = a.AttemptId
              OwnerAgentId = f.Original.AgentId; BlockedGeneration = before.Generation }
        equal "fresh committed wake" (WakeDecision.Ready(result.CurrentHead, expected.Generation)) (inspect true reader locator)
        match inspect true reader {locator with BlockedGeneration = before.Generation + 1} with
        | WakeDecision.Hold _ -> ()
        | other -> raise (PropertyViolation("forged wake lineage", sprintf "%A" other))
        f.Resolved <- Some(gate, op, locator)
    | Cancel when before.Phase = Waiting ->
        let gate = f.Gate.Value
        let op = Guid.NewGuid()
        ChallengeWorkflow.cancel binding op f.Original.AgentId gate.ChallengeId 1 "cancelled dependency"
        |> success |> ignore
        let cancelledHead = (LedgerStore.fetch reader |> success).Commit
        let replay = ChallengeWorkflow.cancel reader op f.Original.AgentId gate.ChallengeId 1 "cancelled dependency" |> success
        equal "cancel replay" true replay.AlreadyApplied
        equal "cancel replay does not write" cancelledHead (LedgerStore.fetch binding |> success).Commit
    | Resolve when before.Phase = CancelledWaiting ->
        let gate = f.Gate.Value
        let result = ChallengeWorkflow.resolve reader (Guid.NewGuid()) "Lead" gate.ChallengeId gate.ConditionIds.Head 2 "late evidence" ""
        equal "cancelled dependency cannot satisfy" true result.IsError
        equal "cancelled dependency cannot wake" head (LedgerStore.fetch binding |> success).Commit
    | ReplayResolve ->
        match f.Resolved with
        | Some(gate, op, _) ->
            let replay = ChallengeWorkflow.resolve binding op "Lead" gate.ChallengeId gate.ConditionIds.Head 1 "evidence" "" |> success
            equal "resolution replay" true replay.AlreadyApplied
            equal "replay cannot reissue ownership" head (LedgerStore.fetch reader |> success).Commit
        | None -> ()
    | Resume when before.Phase = Ready ->
        ReviewWorkflow.resumeAgent binding (Guid.NewGuid()) taskId f.Original.AgentId a.Version owner.Version before.Generation
        |> success |> ignore
    | Checkpoint when before.Phase = Working ->
        f.Previous <- current f client
        let receipt = ReviewWorkflow.checkpointAttempt binding (Guid.NewGuid()) taskId f.Original.AgentId a.Version owner.Version before.Generation "progress" "continue" "" "" |> success
        let _, cp = ReviewWorkflow.getCheckpoint reader taskId receipt.Operation.Sequence |> success
        equal "checkpoint generation" before.Generation cp.AssignmentGeneration
    | _ -> ()
    f.State <- expected
    observe f

// The real authenticated post-journal Challenge callback queues a real
// scheduler host. Native dispatch is still disabled on this source; the
// wake port checks canonical automatic eligibility and returns a HOLD.
let private resolveThroughHost (account: SystemPropertyAccountFixture.AccountFixture)
                               (f: Fixture) client project =
    let gate = f.Gate.Value
    let op = Guid.NewGuid()
    let before = f.State
    equal "host resolution must begin blocked" Waiting before.Phase
    let binding = if client = 0 then f.First else f.Second
    let reader = if client = 0 then f.Second else f.First
    let runtime = f.Runtimes[client]
    let mutable wakes = 0
    let mutable callbacks = 0
    let mutable reads = 0
    let cmd : ChallengeWriteCommand =
        { ProjectName = f.Original.ProjectName; ActorId = "Lead"; OperationId = string op
          Action = "Resolve"; ChallengeId = gate.ChallengeId; ConditionId = gate.ConditionIds.Head
          ExpectedVersion = 1; Title = ""; TaskId = ""; AttemptId = ""; DependencyChallengeIds = []
          ExpectedTaskVersion = 0; ExpectedAttemptVersion = 0; ExpectedAgentVersion = 0
          AssignmentGeneration = 0; Conditions = []; Evidence = "evidence"; Reason = ""
          Outcome = "Satisfied"; NewOwnerAgentId = ""; Subscribe = false }
    let ports : SchedulerHostPorts =
        { ReadVerifiedHeads = fun () ->
            reads <- reads + 1
            let proof = SystemPropertyAccountFixture.coverage account client
            let head = proof.ProjectHeads |> Array.find (fun p -> p.ProjectId = string binding.ProjectId)
            Ok { AccountHead = proof.AccountHead; ProjectHead = head.VerifiedHead }
          Wake = fun triggers ->
            wakes <- wakes + 1
            equal "only committed dependency wake" [WorkWakeTrigger.DependencyCommitted] triggers
            equal "real canonical automatic eligibility" (ActivationEligible f.Original.TaskId)
                (ReviewWorkflow.inspectAutomaticActivation reader f.Original.AgentId |> success)
            WorkWakeResult.Disabled
          Report = ignore
          Delay = fun _ cancellation -> Task.Delay(System.Threading.Timeout.Infinite, cancellation) }
    let registeredHost, committed =
        registerHost true ports (fun name -> Client.OrchestrationApi.LedgerBindingConfig.load name runtime) ignore
    use host = registeredHost
    let callback : Client.SignalR.Client.ChallengeWakePostCommit =
        fun original receipt ->
            callbacks <- callbacks + 1
            equal "exact original Challenge UUID" (string op) receipt.operation.OperationId
            equal "callback observes durable canonical head" receipt.head (LedgerStore.fetch reader |> success).Commit
            if Environment.GetEnvironmentVariable("JARVIS_SYSTEM_DROP_WAKE") <> "1" then
                committed original receipt
    let execute () =
        let wire = System.Text.Json.JsonSerializer.Serialize<AgentCommand>(ChallengeWriteCommand cmd)
        Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake runtime wire "Lead" None false callback
        |> fun t -> t.GetAwaiter().GetResult()
    let result = execute ()
    equal "authenticated resolve committed" true result.Error.IsNone
    equal "one callback" 1 callbacks
    equal "no synchronous host execution" 0 wakes
    equal "committed wake reaches real host" (SchedulerHostOutcome.Reconciled WorkWakeResult.Disabled) (host.RunOnce [])
    equal "one verified host read" 1 reads
    equal "one dependency wake" 1 wakes
    equal "replay succeeds" true (execute().Error.IsNone)
    equal "no replayed callback" 1 callbacks
    equal "no second host wake" SchedulerHostOutcome.Dormant (host.RunOnce [])
    host.StopAsync().GetAwaiter().GetResult()
    equal "STOP refuses new hint" false (host.Notify WorkWakeTrigger.DependencyCommitted)
    equal "STOP refuses manual reconciliation" SchedulerHostOutcome.Stopped (host.RunOnce [WorkWakeTrigger.ManualReconcile])
    equal "STOP performed no additional reads" 1 reads
    f.State <- next Resolve before
    observe f

let private recoverWithRealActivity (activity: SystemPropertyActivityFixture.ActivityFixture)
                                    (f: Fixture) client =
    let c = current f client |> Option.get
    let before = f.State
    let old = DateTimeOffset.UtcNow.AddSeconds(-701.)
    let check reader =
        let deps : Dependencies =
            { Enumerate = fun () -> Ok [c]
              Current = NativeHealth.current f.Runtimes[client]
              Activity = reader
              Recover = NativeHealth.recover f.Runtimes[client] }
        NativeHealth.checkOnce {Enabled = true; InactivitySeconds = 600} deps
        |> fun t -> t.GetAwaiter().GetResult()
    let head = (LedgerStore.fetch f.First |> success).Commit
    let fresh = activity.Record(c.ProjectName, c.AgentId, DateTimeOffset.UtcNow)
    equal "fresh real SQLite activity suppresses recovery" 0 (check (fun p a -> activity.Read(client,p,a))).Recoveries
    let reordered = activity.Record(c.ProjectName, c.AgentId, old)
    equal "old observation cannot overwrite high water" fresh.LastSeenAtUtc reordered.LastSeenAtUtc
    equal "foreign registered client cannot recover" 0 (check (fun p a -> activity.Read(2,p,a))).Recoveries
    activity.Restart()
    equal "persisted old row alone cannot authorize recovery" 0 (check (fun p a -> activity.Read(client,p,a))).Recoveries
    equal "all denied activity histories leave Git unchanged" head (LedgerStore.fetch f.Second |> success).Commit
    // New process requalification cannot overwrite persisted fresh high water.
    // A separate fresh disposable SQLite history models an old qualifying
    // event with a wide timing margin, not an exact 600s hosted boundary.
    f.Previous <- Some c
    equal "model requires stale age" true (before.Age |> Option.exists (fun a -> a >= 600))
    observe f

let private recoverWithStaleSqlite (activity: SystemPropertyActivityFixture.ActivityFixture)
                                     (f: Fixture) client =
    let c = current f client |> Option.get
    let stale = activity.Record(c.ProjectName, c.AgentId, DateTimeOffset.UtcNow.AddSeconds(-701.))
    let afterReorder = activity.Record(c.ProjectName, c.AgentId, DateTimeOffset.UtcNow.AddSeconds(-800.))
    equal "SQLite monotonic high-water" stale.LastSeenAtUtc afterReorder.LastSeenAtUtc
    let deps : Dependencies =
        { Enumerate = fun () -> Ok [c]
          Current = NativeHealth.current f.Runtimes[client]
          Activity = fun p a -> activity.Read(client,p,a)
          Recover = NativeHealth.recover f.Runtimes[client] }
    let result = NativeHealth.checkOnce {Enabled = true; InactivitySeconds = 600} deps
                 |> fun t -> t.GetAwaiter().GetResult()
    let nextState = next CheckHealth f.State
    equal "real server SQLite qualified Health recovery" 1 result.Recoveries
    f.State <- nextState
    f.Previous <- Some c
    observe f

let private newFixture root =
    Directory.CreateDirectory(root) |> ignore
    let a,b,rtA,rtB,initial = create root
    ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Lead" |> success |> ignore
    let branch = "refs/heads/" + git a.RepositoryPath ["branch"; "--show-current"]
    ReviewWorkflow.rebindAttemptSource a (Guid.NewGuid()) initial.TaskId "Worker" initial.AttemptVersion initial.Generation branch |> success |> ignore
    let original = NativeHealth.current rtA initial |> success |> Option.get
    { First = a; Second = b; Runtimes = [|rtA;rtB|]; Original = original
      State = {Phase = Working; Generation = original.Generation; Age = Some 0}
      Previous = None; Gate = None; Resolved = None }

// A second independently assigned agent shares the same canonical project
// and clients. Its source branch and attempt are distinct.
let private secondAgent (f: Fixture) =
    let a = f.First
    let agentId, taskId = "Worker Two", "second-system-task"
    ReviewWorkflow.registerAgent a (Guid.NewGuid()) agentId |> success |> ignore
    ReviewWorkflow.createTaskWithSpec a (Guid.NewGuid()) taskId "Independent task" "<h1>Independent source</h1>"
    |> success |> ignore
    ReviewWorkflow.assignTask a (Guid.NewGuid()) taskId (Guid.NewGuid()) agentId |> success |> ignore
    ReviewWorkflow.resumeAgent a (Guid.NewGuid()) taskId agentId 0 0 0 |> success |> ignore
    let baseOid = git a.RepositoryPath ["rev-parse"; "HEAD"]
    let targetRef = "refs/heads/" + git a.RepositoryPath ["branch"; "--show-current"]
    let sourceRef = "refs/heads/worker-two"
    git a.RepositoryPath ["branch"; "worker-two"; baseOid] |> ignore
    git a.RepositoryPath ["push"; "origin"; sourceRef] |> ignore
    let _, _, attempt = ReviewWorkflow.getTask a taskId |> success
    ReviewWorkflow.bindReviewBase a (Guid.NewGuid()) taskId agentId
        attempt.Value.Version attempt.Value.AssignmentGeneration baseOid targetRef |> success |> ignore
    let _, _, bound = ReviewWorkflow.getTask a taskId |> success
    ReviewWorkflow.rebindAttemptSource a (Guid.NewGuid()) taskId agentId
        bound.Value.Version bound.Value.AssignmentGeneration sourceRef |> success |> ignore
    let snap = LedgerStore.fetch a |> success
    let agent = ReviewWorkflow.getAgentWorkingTurnAt a snap agentId |> success
    let task, active = ReviewWorkflow.getTaskAt a snap taskId |> success
    let active = active.Value
    let candidate =
        { f.Original with AgentId = agentId; TaskId = taskId; AgentVersion = agent.Version
                          GitHead = snap.Commit; TaskVersion = task.Version; AttemptId = active.AttemptId
                          AttemptVersion = active.Version; Generation = active.AssignmentGeneration
                          SpecBlobOid = active.SpecBlobOid; SourceRef = active.SourceRef }
    { f with Original = candidate; State = { Phase = Working; Generation = candidate.Generation; Age = Some 0 }
             Previous = None; Gate = None; Resolved = None }

type TraceStep = int * int * Command

let private runTrace (commands: TraceStep list) =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-system-property-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
    try
        let fs = [| newFixture (Path.Combine(root,"project-a")); newFixture (Path.Combine(root,"project-b")) |]
        let rec run prefix remaining =
            match remaining with
            | [] -> Ok ()
            | ((project,client,command) as event)::rest ->
                let executed = event::prefix
                try
                    step fs[project] client command
                    observe fs[1-project]
                    run executed rest
                with
                | PropertyViolation(label,detail) -> Error(label,detail,List.rev executed)
                | ex -> failwithf "Infrastructure/production operation failure at %A: %O" (List.rev executed) ex
        run [] commands
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior)
        Directory.Delete(root, true)

// Bounded deterministic delete-chunk shrinking. A fresh production-backed
// replay must reproduce the SAME named invariant, not just any exception.
let private shrink budget reproduces (trace: TraceStep list) =
    let mutable best = trace
    let mutable size = max 1 (trace.Length / 2)
    let mutable offset = 0
    let mutable attempts = 0
    while size > 0 && attempts < budget do
        if offset >= best.Length then
            size <- size / 2
            offset <- 0
        else
            let trial = best |> List.indexed |> List.choose (fun (i,event) ->
                if i >= offset && i < offset+size then None else Some event)
            attempts <- attempts + 1
            if not trial.IsEmpty && reproduces trial then
                best <- trial
                offset <- 0
            else offset <- offset + size
    best

let private configuredBudget variable fallback maximum =
    match Int32.TryParse(Environment.GetEnvironmentVariable variable) with
    | true, n when n >= 0 && n <= maximum -> n
    | _ -> fallback

[<TestFixture; NonParallelizable>]
type GeneratedComposition() =
    [<TestCase(101)>]
    [<TestCase(102)>]
    [<TestCase(103)>]
    member _.MixedHealthChallengeWake(seed: int) =
        let random = Random(seed)
        let prefix = [Block; Advance 601; CheckStale; CheckHealth; Resolve; ReplayResolve; Resume; CheckStale; CheckHealth; Resume; Checkpoint; CheckStale; ResetActivity; CheckHealth]
        let choices = [|Advance 599; Advance 1; Advance 601; Activity; ResetActivity; CheckHealth; CheckStale; Block; Resolve; Resume; Checkpoint; ReplayResolve; Cancel|]
        let commands =
            [ for command in prefix do
                for project in 0..1 do yield project, random.Next(2), command
              for _ in 1..configuredBudget "JARVIS_SYSTEM_EXTRA_STEPS" 16 256 do
                  yield random.Next(2), random.Next(2), choices[random.Next(choices.Length)]
              // Ensure at least one cancellation path is reached without
              // sacrificing exploration of recoverable states earlier.
              for project in 0..1 do
                  for command in [Resume; Block; Cancel; Resolve; Advance 701; CheckHealth; Resume] do
                      yield project, random.Next(2), command ]
        match runTrace commands with
        | Ok () -> ()
        | Error(label,detail,prefix) ->
            let minimal =
                shrink (configuredBudget "JARVIS_SYSTEM_SHRINK_BUDGET" 6 32)
                    (fun trial ->
                        match runTrace trial with
                        | Error(other,_,_) -> other = label
                        | Ok () -> false)
                    prefix
            Assert.Fail(sprintf "seed=%d invariant=%s %s originalPrefix=%A shrunk=%A" seed label detail prefix minimal)

    [<Test>]
    member _.DeleteShrinkerKeepsNamedWitness() =
        let trace = [0,0,Activity; 1,0,Block; 0,0,Advance 601; 0,1,CheckStale; 1,1,Resume]
        let witness t = List.contains (0,0,Advance 601) t && List.contains (0,1,CheckStale) t
        equal "delete shrinker preserves exact witness" [0,0,Advance 601; 0,1,CheckStale] (shrink 16 witness trace)

    // This is deliberately a safety projection of the currently compiled
    // default-off native reservation/claim path. It does not fabricate a
    // provider terminal or convert a synthetic coverage witness into production authority.
    [<TestCase(211)>]
    [<TestCase(212)>]
    member _.SharedAccountReservationAcrossHealthChallenge(seed: int) =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-system-account-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let random = Random(seed)
            let fs = [| newFixture (Path.Combine(root,"project-a")); newFixture (Path.Combine(root,"project-b")) |]
            use account = SystemPropertyAccountFixture.create root
                            [| fs |> Array.map _.First; fs |> Array.map _.Second |]
            let binding client project = if client = 0 then fs[project].First else fs[project].Second
            // The oracle retains the original command's full candidate and
            // UUID, plus separately expected lifecycle/effect state.
            let mutable expected : Map<string, ReservationExpectation> = Map.empty
            let mutable limit = 1
            let expectReserved (op: Guid) (candidate: NativeDispatchCandidate) =
                let reservation = { Original = candidate; OriginalOperation = string op; Status = "Reserved"
                                    Generation = 1L; Fence = ""; Session = ""; Terminal = "" }
                expected <- expected.Add(candidate.DispatchId, reservation)
            let expectMove (candidate: NativeDispatchCandidate) status generation fence session =
                let original = expected[candidate.DispatchId]
                expected <- expected.Add(candidate.DispatchId,
                    { original with Status = status; Generation = generation; Fence = fence; Session = session })
            let verify () =
                for c in 0..1 do
                    let state = SystemPropertyAccountFixture.read account c
                    equal "account identity" (string account.Accounts[c].AccountId) state.AccountId
                    equal "account ledger identity" (string account.Accounts[c].LedgerId) state.LedgerId
                    equal "account epoch" account.Accounts[c].Epoch state.Epoch
                    let records = state.Records
                    let observed =
                        records
                        |> Array.map (fun r ->
                            r.Candidate.DispatchId,
                            { Original = r.Candidate; OriginalOperation = r.OriginalOperationId
                              Status = r.Status; Generation = r.Generation; Fence = r.ProjectFenceOid
                              Session = r.ProviderSessionId; Terminal = r.TerminalOperationId })
                        |> Map.ofArray
                    equal "exact account reservation projection" expected observed
                    equal "unique reservations" records.Length observed.Count
                    equal "unique original operations" records.Length (records |> Array.map _.OriginalOperationId |> Array.distinct |> Array.length)
                    equal "unique effect operations" records.Length (records |> Array.map (fun r -> r.Candidate.EffectOperationId) |> Array.distinct |> Array.length)
                    equal "unique canonical keys" records.Length (records |> Array.map _.CanonicalKey |> Array.distinct |> Array.length)
                    let occupied = records |> Array.filter (fun r -> r.Status <> "Released")
                    equal "capacity conserved" true (occupied.Length <= limit)
                    for _, local in occupied |> Array.groupBy (fun r -> r.Candidate.ProjectId) do
                        equal "per-project quota conserved" true (local.Length <= 1)
                fs |> Array.iter observe
            let run p cmd =
                step fs[p] (random.Next 2) cmd
                verify ()
            let fresh p c = SystemPropertyAccountFixture.candidate (binding c p) fs[p].Original.TaskId
            let reserve p client =
                let c = fresh p client
                let op = Guid.NewGuid()
                let result = SystemPropertyAccountFixture.reserve account client op c
                op, c, result
            let blocked = random.Next 2
            let other = 1 - blocked
            SystemPropertyAccountFixture.approve account 0 0L limit 1 |> success |> ignore
            verify ()
            run blocked Block
            let _, _, denied = reserve blocked 1
            equal "blocked cannot reserve" true denied.IsError
            verify ()
            run blocked (Advance 601)
            run blocked CheckStale
            run blocked Resolve
            run blocked ReplayResolve
            run blocked Resume
            // Independent clients use a shared captured account version.
            // Either contender may win; at most one reservation may commit.
            let candidates = [|fresh 0 0; fresh 1 1|]
            let ids = [|Guid.NewGuid(); Guid.NewGuid()|]
            let covers = [| SystemPropertyAccountFixture.coverage account 0; SystemPropertyAccountFixture.coverage account 1 |]
            let operations =
                [| for p in 0..1 ->
                    Task.Run(fun () ->
                        SystemPropertyAccountFixture.reserveWithCoverage account p covers[p] ids[p] candidates[p]) |]
            Task.WhenAll(operations).GetAwaiter().GetResult() |> ignore
            let results = operations |> Array.map (fun t -> t.Result)
            let winners = [| for p in 0..1 do if results[p].IsOk then yield p |]
            equal "one account capacity winner" 1 winners.Length
            let winner = winners[0]
            let loser = 1 - winner
            let original = candidates[winner]
            expectReserved ids[winner] original
            verify ()
            let replay = SystemPropertyAccountFixture.reserveWithCoverage account loser covers[winner] ids[winner] original
            equal "exact original reservation replay" true replay.IsOk
            verify ()
            let _, _, duplicate = reserve winner loser
            equal "duplicate candidate blocked" true duplicate.IsError
            verify ()
            // A later Health recovery cannot erase an occupied original or
            // let its stale source issue a new project-side fence.
            run winner (Advance 701)
            use freshStore = new SystemPropertyActivityFixture.ActivityFixture(Path.Combine(root,"activity-fresh.sqlite"))
            recoverWithRealActivity freshStore fs[winner] winner
            verify ()
            use staleStore = new SystemPropertyActivityFixture.ActivityFixture(Path.Combine(root,"activity-stale.sqlite"))
            recoverWithStaleSqlite staleStore fs[winner] loser
            verify ()
            let _, stale = SystemPropertyAccountFixture.claim account loser winner ids[winner] original
            equal "stale project claim after recovery" true stale.IsError
            verify ()
            run winner Resume
            let _, _, deniedSuccessor = reserve winner winner
            equal "successor cannot consume occupied capacity" true deniedSuccessor.IsError
            verify ()
            // Roster replacement permits the other project without releasing
            // the old reservation. A fresh canonical claim uses real Git.
            limit <- 2
            SystemPropertyAccountFixture.approve account loser 1L limit 1 |> success |> ignore
            verify ()
            run loser Block
            let _, _, deniedAgain = reserve loser winner
            equal "blocked with available capacity still denied" true deniedAgain.IsError
            verify ()
            resolveThroughHost account fs[loser] winner loser
            verify ()
            run loser Resume
            let op, c, admitted = reserve loser loser
            admitted |> success |> ignore
            expectReserved op c
            verify ()
            let _, claim = SystemPropertyAccountFixture.claim account winner loser op c
            let fence = claim |> success
            let _, duplicateClaim = SystemPropertyAccountFixture.claim account loser loser op c
            equal "one project claim per original" true duplicateClaim.IsError
            verify ()
            let dispatch = Guid.Parse c.DispatchId
            let effect = Guid.Parse c.EffectOperationId
            let move version status session =
                SystemPropertyAccountFixture.transition account winner dispatch effect version status fence.CurrentHead session
            move 1L "Dispatching" "" |> success |> ignore
            expectMove c "Dispatching" 2L fence.CurrentHead ""
            verify ()
            let staleTransition = move 1L "Uncertain" ""
            equal "stale account transition rejected" true staleTransition.IsError
            verify ()
            let wrongEffect =
                SystemPropertyAccountFixture.transition account loser dispatch (Guid.NewGuid()) 2L "Uncertain" fence.CurrentHead ""
            equal "wrong original effect rejected" true wrongEffect.IsError
            verify ()
            let wrongFence =
                SystemPropertyAccountFixture.transition account loser dispatch effect 2L "Uncertain" (String.replicate 40 "a") ""
            equal "replacement project fence rejected" true wrongFence.IsError
            verify ()
            move 2L "Uncertain" "" |> success |> ignore
            expectMove c "Uncertain" 3L fence.CurrentHead ""
            verify ()
            // No live provider or synthetic terminal is invoked. Uncertainty
            // must remain occupied despite duplicate/no-terminal observations.
            let release = SystemPropertyAccountFixture.denyRelease account loser op c
            equal "unverified terminal cannot release" true release.IsError
            verify ()
            move 3L "Unknown" "disposable-unconfirmed-session" |> success |> ignore
            expectMove c "Unknown" 4L fence.CurrentHead "disposable-unconfirmed-session"
            verify ()
            let forged = SystemPropertyAccountFixture.forgeTerminal account winner loser op c
            equal "positive verifier without immutable receipt cannot release" true forged.IsError
            verify ()
            let wrongSession = move 4L "Active" "another-session"
            equal "replacement provider session rejected" true wrongSession.IsError
            verify ()
            let wrongOriginal = SystemPropertyAccountFixture.forgeTerminal account winner loser (Guid.NewGuid()) c
            equal "wrong original reservation cannot release" true wrongOriginal.IsError
            verify ()
            let _, _, full = reserve winner winner
            equal "uncertain reservation remains occupied" true full.IsError
            verify ()
            // Seeded post-uncertainty interleavings: ownership can change,
            // but neither missing activity nor resolved dependencies release
            // original account occupancy or authorize a second claim.
            let tail = [| Block; Resolve; Resume; Activity; Advance 601; CheckHealth; Checkpoint; CheckStale; ResetActivity |]
            for _ in 1..8 do
                let p = random.Next 2
                run p tail[random.Next tail.Length]
                let _, _, extra = reserve p (random.Next 2)
                equal "capacity stays occupied under generated ownership changes" true extra.IsError
                verify ()
            equal "current production provider dispatch disabled" true (Client.AutomationNativeProviderSessionHost.authorizeProviderDispatch().IsError)
            equal "current production release disabled" true (Client.AutomationNativeProviderSessionHost.releaseFromProviderTerminal().IsError)
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior)
            Directory.Delete(root, true)

    [<TestCase(301)>]
    [<TestCase(302)>]
    member _.IndependentOwnersShareCanonicalProject(seed: int) =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-system-owners-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let first = newFixture root
            let second = secondAgent first
            let fs = [|first; second|]
            let random = Random(seed)
            let run owner cmd =
                step fs[owner] (random.Next 2) cmd
                observe fs[1-owner]
            // Ensure a stalled sibling can recover without unblocking a
            // challenged owner or changing its attempt/source lineage.
            run 0 Block
            run 1 (Advance 701)
            run 1 CheckHealth
            // The other owner's exact fresh task/attempt generation does
            // not authorize claiming the recovered owner's slot.
            let _, ready = snapshot first.First second
            let wrongOwner = (ReviewWorkflow.getAgentInbox first.First first.Original.AgentId |> success).Agent
            let unchanged = (LedgerStore.fetch first.First |> success).Commit
            let wrong = ReviewWorkflow.resumeAgent first.Second (Guid.NewGuid()) second.Original.TaskId
                            first.Original.AgentId ready.Version wrongOwner.Version second.State.Generation
            equal "wrong owner cannot resume sibling" true wrong.IsError
            equal "wrong owner leaves canonical head unchanged" unchanged (LedgerStore.fetch first.First |> success).Commit
            fs |> Array.iter observe
            run 0 CheckHealth
            run 0 Resolve
            run 0 ReplayResolve
            run 1 Resume
            run 0 Resume
            run 1 CheckStale
            run 0 CheckStale
            let choices = [|Block; Resolve; Resume; Checkpoint; Advance 601; CheckHealth; Activity; CheckStale|]
            for _ in 1..8 do
                run (random.Next 2) choices[random.Next choices.Length]
            // Both original ownership identities remain distinct despite
            // all canonical mutations sharing one ledger head.
            equal "distinct agent identities" true (first.Original.AgentId <> second.Original.AgentId)
            equal "distinct task identities" true (first.Original.TaskId <> second.Original.TaskId)
            equal "distinct source identities" true (first.Original.SourceRef <> second.Original.SourceRef)
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior)
            Directory.Delete(root, true)

    // Explicit schedule at the final activity/recovery boundary. Both
    // contenders have captured the same Working generation; the first
    // durable transition must exclude the stale second command.
    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.ChallengeAndHealthCompeteForCapturedGeneration(blockFirst: bool) =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-system-race-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let f = newFixture root
            let taskId = f.Original.TaskId
            let gate = ChallengeWorkflow.create f.Second (Guid.NewGuid()) "Worker" "race" taskId
                           [{ OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "race dependency" }]
                       |> success
            let t, a = snapshot f.Second f
            let owner = (ReviewWorkflow.getAgentInbox f.Second "Worker" |> success).Agent
            let candidate = current f 0 |> Option.get
            let before = {f.State with Age = Some 701}
            f.State <- before
            let now = DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)
            let observation : AgentActivitySnapshot = { ServerNowUtc = now; LastSeenAtUtc = Some(now.AddSeconds(-701.)) }
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<Result<AgentActivitySnapshot,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable reads = 0
            let deps : Dependencies =
                { Enumerate = fun () -> Ok [candidate]
                  Current = NativeHealth.current f.Runtimes[0]
                  Activity = fun _ _ ->
                      reads <- reads + 1
                      if reads = 1 then Task.FromResult(Ok observation)
                      else
                          entered.TrySetResult(()) |> ignore
                          release.Task
                  Recover = NativeHealth.recover f.Runtimes[0] }
            let check = NativeHealth.checkOnce { Enabled = true; InactivitySeconds = 600 } deps
            let block () =
                ChallengeWorkflow.blockAttempt f.Second (Guid.NewGuid()) "Worker" taskId a.AttemptId
                    t.Version a.Version owner.Version before.Generation "race" [gate.ChallengeId]
            try
                equal "Health reaches final activity barrier" true (entered.Task.Wait(TimeSpan.FromSeconds 30.))
                if blockFirst then
                    block() |> success |> ignore
                    f.Gate <- Some gate
                    f.State <- next Block before
                    observe f
                    let committed = (LedgerStore.fetch f.Second |> success).Commit
                    release.SetResult(Ok observation)
                    equal "stale recovery loses to durable block" 0 (check.GetAwaiter().GetResult()).Recoveries
                    equal "losing recovery does not write" committed (LedgerStore.fetch f.Second |> success).Commit
                    observe f
                    step f 1 Resolve
                else
                    release.SetResult(Ok observation)
                    equal "fresh recovery wins" 1 (check.GetAwaiter().GetResult()).Recoveries
                    f.State <- next CheckHealth before
                    observe f
                    let committed = (LedgerStore.fetch f.First |> success).Commit
                    equal "stale block loses to recovery" true (block().IsError)
                    equal "losing block does not write" committed (LedgerStore.fetch f.First |> success).Commit
                    observe f
                    step f 1 Resume
                    step f 0 Block
                    step f 1 Resolve
            finally
                release.TrySetResult(Ok observation) |> ignore
                check.GetAwaiter().GetResult() |> ignore
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior)
            Directory.Delete(root, true)
