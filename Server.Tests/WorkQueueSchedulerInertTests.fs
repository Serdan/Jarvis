module ServerWorkQueueSchedulerInertTests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Jarvis.WorkQueues.InertScheduler

let private ok = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected WQ08 error: %A" error

let private mustReject (expected: Rejection) (result: Result<'T, Rejection>) =
    match result with
    | Error error -> Assert.That((error = expected), Is.True)
    | Ok _ -> Assert.Fail(sprintf "Expected %A, received success" expected)

let private worker =
    { Id = "Worker"
      Version = 7
      Queues = Set.singleton "implementation"
      InstructionsDigest = "sha256:verified-worker-config" }

let private reviewer =
    { Id = "Reviewer"
      Version = 4
      Queues = Set.singleton "review"
      InstructionsDigest = "sha256:verified-review-config" }

let private stage project id kind queue at deps state : WorkStage =
    { Id = id
      ProjectId = project
      SubjectId = "task:" + id
      Kind = kind
      QueueId = queue
      StageGeneration = 2
      State = state
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.ofList deps
      WaivedDependencies = Map.empty
      AvailableAt = at
      AvailableSequence = at
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private work project id at =
    stage project id WorkKind.Implementation "implementation" at [] WorkState.Available

let private project id roles works =
    let subscriptions = roles |> List.map (fun (role: InertRoleSnapshot) -> role.Id, role.Queues) |> Map.ofList
    let state =
        { emptyProject subscriptions with
            Works = works |> List.map (fun (work: WorkStage) -> work.Id, work) |> Map.ofList }
    { Id = id; State = state; Roles = roles }

let private snapshot account projects =
    { Account = account
      Projects = projects
      CompleteProjectSet = true
      CompleteRoleVersions = true }

let private single () =
    project "alpha" [worker] [work "alpha" "W" 1L]

let private planned id input =
    // Explicitly hypothetical fixture-only planning; normal plan is fail-closed.
    match planHypotheticalFixtureOnly input id |> ok with
    | Some value -> value
    | None -> failwith "Expected eligible WQ08 launch plan"

let private admission (plan: InertLaunchPlan) (account: Account) (project: Project) =
    let reserved = reserveAccount plan account |> ok
    let admitted = admitAfterReserve plan reserved project |> ok
    reserved, admitted

let private submitted (plan: InertLaunchPlan) account project =
    let reserved, admitted = admission plan account project
    reserved, recordDispatchOutcome plan DispatchState.Submitted admitted |> ok

[<Test>]
let cap_one_worker_directly_without_manager_or_any_eager_uuid () =
    let entry = single ()
    let original = snapshot (emptyAccount 1) [entry]
    let first = planned "stable-launch-1" original
    let repeated = planned "stable-launch-1" original
    Assert.That(first, Is.EqualTo(repeated))
    Assert.That(first.Launch.RoleId, Is.EqualTo("Worker"))
    Assert.That(first.Launch.RoleVersion, Is.EqualTo(worker.Version))
    Assert.That(first.Launch.State, Is.EqualTo(LaunchState.ReservedUnregistered))
    Assert.That(first.Launch.Lease, Is.EqualTo(None))
    Assert.That(first.Launch.Credit, Is.EqualTo(None))
    Assert.That(first.RequestFingerprint.Length, Is.EqualTo(64))
    Assert.That(original.Account.Tokens.IsEmpty, Is.True)
    Assert.That(entry.State.Launches.IsEmpty, Is.True)
    let reserved, admitted = admission first original.Account entry.State
    Assert.That(reserved.Tokens.[first.Launch.Id], Is.False)
    Assert.That(admitted.Launches.[first.Launch.Id].Credit, Is.EqualTo(Some "W"))
    Assert.That(planHypotheticalFixtureOnly (snapshot reserved [ { entry with State = admitted } ]) "another-launch" |> ok,
                Is.EqualTo(None))

[<Test>]
let runtime_planner_never_promotes_hypothetical_wq02_flags_into_verified_source_authority () =
    let entry = single ()
    let baseCase = snapshot (emptyAccount 1) [entry]
    let review = stage "beta" "forged-review-identity" WorkKind.FrozenReview "review" 1L [] WorkState.Available
    let roleCase = project "beta" [reviewer] [review]
    // All these fixtures say migration/index/roles are complete. None has an
    // authentic pinned WQ04 producer/high-water/role proof; no source ID or
    // cap variation may turn the normal runtime planner into a positive path.
    for scenario in
        [ baseCase
          { baseCase with Account = emptyAccount 0 }
          snapshot (emptyAccount 1) [roleCase]
          snapshot (emptyAccount 2) [entry; roleCase] ] do
        Assert.That(canDispatch scenario.Account (scenario.Projects |> List.map (fun p -> p.State)),
                    Is.False)
        mustReject (Rejection.IncompleteCoverage "migration/high-water prerequisites")
            (plan scenario "blocked-launch")

[<Test>]
let blocking_first_global_fifo_and_role_pinning_are_deterministic () =
    let alpha = project "alpha" [worker] [work "alpha" "older" 1L]
    let reviewWork = stage "beta" "review" WorkKind.FrozenReview "review" 50L [] WorkState.Available
    let dependent = stage "beta" "depends" WorkKind.Implementation "implementation" 60L ["review"] WorkState.Blocked
    let beta = project "beta" [reviewer] [reviewWork; dependent]
    let input = snapshot (emptyAccount 1) [alpha; beta]
    let choice = planned "stable-review" input
    Assert.That(choice.WorkId, Is.EqualTo("review"))
    Assert.That(choice.Launch.ProjectId, Is.EqualTo("beta"))
    Assert.That(choice.Launch.RoleId, Is.EqualTo("Reviewer"))
    let changedRole = { reviewer with Version = reviewer.Version + 1 }
    let changed = snapshot (emptyAccount 1) [alpha; { beta with Roles = [changedRole] }]
    let changedChoice = planned "stable-review" changed
    Assert.That(changedChoice.RequestFingerprint, Is.Not.EqualTo(choice.RequestFingerprint))

[<Test>]
let incomplete_account_scope_role_or_producer_projection_fails_closed () =
    let entry = single ()
    let original = snapshot (emptyAccount 1) [entry]
    mustReject (Rejection.IncompleteCoverage "account project set or canonical role versions")
        (planHypotheticalFixtureOnly { original with CompleteProjectSet = false } "L")
    mustReject (Rejection.IncompleteCoverage "account project set or canonical role versions")
        (planHypotheticalFixtureOnly { original with CompleteRoleVersions = false } "L")
    mustReject (Rejection.IncompleteCoverage "migration/high-water prerequisites")
        (planHypotheticalFixtureOnly { original with Projects = [{ entry with State = { entry.State with QueueCovered = false } }] } "L")
    mustReject (Rejection.IncompleteCoverage "migration/high-water prerequisites")
        (planHypotheticalFixtureOnly { original with Account = { original.Account with Migration = MigrationEpoch.Preparing } } "L")
    mustReject (Rejection.IncompleteCoverage "role subscription, version or stage provenance")
        (planHypotheticalFixtureOnly { original with Projects = [{ entry with Roles = [{ worker with Queues = Set.singleton "review" }] }] } "L")
    mustReject (Rejection.IncompleteCoverage "missing or duplicate project authority")
        (planHypotheticalFixtureOnly { original with Projects = [entry; entry] } "L")
    mustReject (Rejection.InvalidState "launchId must be supplied once by the external journal")
        (planHypotheticalFixtureOnly original "")

[<Test>]
let role_unmatched_or_covered_demand_does_not_allocate_a_second_token () =
    let emptyRole = project "alpha" [reviewer] [work "alpha" "W" 1L]
    Assert.That(planHypotheticalFixtureOnly (snapshot (emptyAccount 1) [emptyRole]) "unused" |> ok, Is.EqualTo(None))
    let entry = single ()
    let first = planned "L1" (snapshot (emptyAccount 2) [entry])
    let reserved, admitted = admission first (emptyAccount 2) entry.State
    Assert.That(planHypotheticalFixtureOnly (snapshot reserved [{entry with State = admitted}]) "L2" |> ok,
                Is.EqualTo(None))

[<Test>]
let global_first_requires_original_token_and_snapshot_bound_project_cas () =
    let entry = single ()
    let account = emptyAccount 2
    let candidate = planned "L" (snapshot account [entry])
    mustReject Rejection.MissingProof (admitAfterReserve candidate account entry.State)
    let reserved = reserveAccount candidate account |> ok
    let replay = reserveAccount candidate reserved |> ok
    Assert.That(replay, Is.EqualTo(reserved))
    let concurrent = { entry.State with Version = entry.State.Version + 1 }
    mustReject Rejection.CasConflict (admitAfterReserve candidate reserved concurrent)
    let admitted = admitAfterReserve candidate reserved entry.State |> ok
    Assert.That(admitted.Launches.["L"].Credit, Is.EqualTo(Some "W"))
    mustReject (Rejection.InvalidState "project admission exists")
        (abortUnboundC01 candidate admitted)
    mustReject Rejection.MissingProof (releaseAfterTerminal candidate admitted reserved)

[<Test>]
let c01_abort_tombstone_releases_only_after_project_terminal_receipt () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let reserved = reserveAccount candidate (emptyAccount 1) |> ok
    let abort = abortUnboundC01 candidate entry.State |> ok
    let tombstone = abort.Launches.["L"]
    Assert.That(tombstone.State, Is.EqualTo(LaunchState.TerminalPendingRelease("abort:" + candidate.AbortOperationId)))
    mustReject Rejection.CasConflict (admitAfterReserve candidate reserved abort)
    let released = releaseAfterTerminal candidate abort reserved |> ok
    Assert.That(released.Tokens.["L"], Is.True)
    let replay = releaseAfterTerminal candidate abort released |> ok
    Assert.That(replay, Is.EqualTo(released))
    let foreign = { abort with Launches = abort.Launches.Add("L", { tombstone with ProjectId = "wrong-project" }) }
    mustReject Rejection.MissingProof (releaseAfterTerminal candidate foreign reserved)

[<Test>]
let first_connect_creates_one_uuid_and_returns_original_receipt_on_lost_reply () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let _, ready = submitted candidate (emptyAccount 1) entry.State
    let uuid = Guid.NewGuid().ToString("D")
    let mutable allocations = 0
    let allocate () = allocations <- allocations + 1; uuid
    let registered, response =
        initializeFirstConnect ready.Version "init:one" "L" "session:first" 17L allocate worker ready |> ok
    Assert.That(allocations, Is.EqualTo(1))
    Assert.That(response.AgentUuid, Is.EqualTo(uuid))
    Assert.That(response.RoleVersion, Is.EqualTo(worker.Version))
    Assert.That(response.Queues = worker.Queues, Is.True)
    Assert.That(response.LeaseGeneration, Is.EqualTo(1))
    let replay, recovered =
        initializeFirstConnect ready.Version "init:one" "L" "session:first" 99L allocate worker registered |> ok
    Assert.That(replay, Is.EqualTo(registered))
    Assert.That(recovered, Is.EqualTo(response))
    let differentOpSameSession, responseAgain =
        initializeFirstConnect ready.Version "init:retry" "L" "session:first" 99L allocate worker registered |> ok
    Assert.That(differentOpSameSession, Is.EqualTo(registered))
    Assert.That(responseAgain, Is.EqualTo(response))
    Assert.That(allocations, Is.EqualTo(1))
    mustReject Rejection.AlreadyRegistered
        (initializeFirstConnect registered.Version "init:other" "L" "session:second" 20L allocate worker registered)
    mustReject Rejection.OperationIdReused
        (initializeFirstConnect registered.Version "init:one" "L" "session:second" 20L allocate worker registered)
    Assert.That(allocations, Is.EqualTo(1))

[<Test>]
let stale_and_terminal_connects_never_allocate_uuid () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let _, ready = submitted candidate (emptyAccount 1) entry.State
    let mutable allocations = 0
    let allocate () = allocations <- allocations + 1; Guid.NewGuid().ToString("D")
    mustReject Rejection.CasConflict
        (initializeFirstConnect (ready.Version - 1) "init:stale" "L" "S1" 1L allocate worker ready)
    let terminal = finishUnregistered ready.Version "terminal" "L" true true ready |> ok
    mustReject Rejection.Terminal
        (initializeFirstConnect terminal.Version "init:late" "L" "S1" 1L allocate worker terminal)
    Assert.That(allocations, Is.Zero)
    mustReject (Rejection.IncompleteCoverage "pinned role and instructions are unverified")
        (initializeFirstConnect ready.Version "init:drift" "L" "S1" 1L allocate { worker with Version = 99 } ready)

[<Test>]
let original_browser_unknown_never_becomes_a_second_submit_intent () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let _, admitted = admission candidate (emptyAccount 1) entry.State
    Assert.That(dispatchAdvice candidate admitted,
                Is.EqualTo(InertDispatchAdvice.SubmitOriginal candidate.DispatchOperationId))
    let uncertain = recordDispatchOutcome candidate DispatchState.OutcomeUnknown admitted |> ok
    Assert.That(dispatchAdvice candidate uncertain,
                Is.EqualTo(InertDispatchAdvice.ReconcileOriginal candidate.DispatchOperationId))
    let replay = recordDispatchOutcome candidate DispatchState.OutcomeUnknown uncertain |> ok
    Assert.That(replay, Is.EqualTo(uncertain))
    Assert.That(replay.Launches.["L"].DispatchCount, Is.EqualTo(1))
    mustReject (Rejection.InvalidState "dispatch is one-shot and requires reserved launch")
        (dispatchOnce uncertain.Version "second-browser-send" "L" DispatchState.Submitted uncertain)
    let _, firstConnect =
        initializeFirstConnect uncertain.Version "init" "L" "S1" 6L
            (fun () -> Guid.NewGuid().ToString("D")) worker uncertain |> ok
    Assert.That(firstConnect.RoleId, Is.EqualTo("Worker"))

[<Test>]
let unregistered_deadline_fences_late_connect_before_account_release () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let reserved, admitted = admission candidate (emptyAccount 1) entry.State
    let submitted = recordDispatchOutcome candidate DispatchState.OutcomeUnknown admitted |> ok
    let deadline =
        { LaunchId = "L"
          DeadlineAt = 100L
          OriginalDispatchOperationId = candidate.DispatchOperationId
          OriginalJournalSettled = true }
    mustReject Rejection.UnsafeExternalEffect
        (expireUnregistered submitted.Version "expire:early" 99L deadline submitted)
    mustReject Rejection.UnsafeExternalEffect
        (expireUnregistered submitted.Version "expire:unknown" 100L
            { deadline with OriginalJournalSettled = false } submitted)
    let terminal = expireUnregistered submitted.Version "expire:original" 100L deadline submitted |> ok
    mustReject Rejection.Terminal
        (initializeFirstConnect terminal.Version "init:late" "L" "new-session" 200L
            (fun () -> Guid.NewGuid().ToString("D")) worker terminal)
    let released = releaseAfterTerminal candidate terminal reserved |> ok
    Assert.That(released.Tokens.["L"], Is.True)
    Assert.That(dispatchAdvice candidate terminal, Is.EqualTo(InertDispatchAdvice.DoNotSubmit))

[<Test>]
let session_key_is_a_provider_execution_property_not_a_launch_property () =
    let emptyKeys : SessionKeys = Map.empty
    let keys, first = sessionKeyForExecution "execution-a" "high-entropy-a" emptyKeys |> ok
    let keys, replay = sessionKeyForExecution "execution-a" "ignored-new" keys |> ok
    Assert.That(replay, Is.EqualTo(first))
    let keys, second = sessionKeyForExecution "execution-b" "high-entropy-b" keys |> ok
    Assert.That(second, Is.Not.EqualTo(first))
    mustReject (Rejection.InvalidState "copied provider-session key")
        (sessionKeyForExecution "execution-c" first keys)

[<Test>]
let coalesced_wakeups_and_bounded_fallback_need_no_model_token () =
    let start : WakeProjection = { Pending = false; NextFallbackAt = 100L }
    let first = coalesceWake 20L 30L true start |> ok
    let repeated = coalesceWake 21L 30L true first |> ok
    Assert.That(first.Pending, Is.True)
    Assert.That(repeated.NextFallbackAt, Is.EqualTo(100L))
    let fallback = coalesceWake 101L 30L false { start with Pending = false } |> ok
    Assert.That(fallback.Pending, Is.True)
    Assert.That(fallback.NextFallbackAt, Is.EqualTo(131L))
    mustReject (Rejection.InvalidState "fallback interval must be positive")
        (coalesceWake 20L 0L true start)

let private git directory arguments =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- directory
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in arguments do start.ArgumentList.Add(argument)
    use gitProcess = Process.Start(start)
    let output = gitProcess.StandardOutput.ReadToEnd() + gitProcess.StandardError.ReadToEnd()
    if not (gitProcess.WaitForExit(15000)) then
        gitProcess.Kill(true)
        failwithf "Disposable Git process timeout %A" arguments
    gitProcess.ExitCode, output

let private gitOk directory arguments =
    let code, output = git directory arguments
    if code <> 0 then failwithf "Disposable git %A: %s" arguments output
    output

// Only temporary local Git repositories are used. No remote project branches,
// GitHub Actions, live Scheduler, browser sends or account ledgers are touched.
let private twoCloneFixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-wq08-inert-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        gitOk root ["init"; "--bare"; "--initial-branch=ledger"; "ledger.git"] |> ignore
        gitOk root ["clone"; "ledger.git"; "a"] |> ignore
        let a = Path.Combine(root, "a")
        for config in ["a"; "b"] do
            if config = "a" then
                gitOk a ["config"; "user.email"; "fixture@example.invalid"] |> ignore
                gitOk a ["config"; "user.name"; "WQ08 Fixture"] |> ignore
        File.WriteAllText(Path.Combine(a, "state.txt"), "head=0\n")
        gitOk a ["add"; "state.txt"] |> ignore
        gitOk a ["commit"; "-m"; "seed"] |> ignore
        gitOk a ["push"; "origin"; "HEAD:ledger"] |> ignore
        gitOk root ["clone"; "--branch"; "ledger"; "ledger.git"; "b"] |> ignore
        let b = Path.Combine(root, "b")
        gitOk b ["config"; "user.email"; "fixture@example.invalid"] |> ignore
        gitOk b ["config"; "user.name"; "WQ08 Fixture"] |> ignore
        action a b
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

let private stageCommit directory (body: string) message =
    File.WriteAllText(Path.Combine(directory, "state.txt"), body)
    gitOk directory ["add"; "state.txt"] |> ignore
    gitOk directory ["commit"; "-m"; message] |> ignore

[<Test>]
let disposable_two_clone_last_slot_cas_rejects_loser_and_recovers_original_reserve () =
    let entry = single ()
    let account = emptyAccount 1
    let aPlan = planned "A" (snapshot account [entry])
    let bPlan = planned "B" (snapshot account [entry])
    let afterA = reserveAccount aPlan account |> ok
    let afterB = reserveAccount bPlan account |> ok
    Assert.That(afterA.Tokens.["A"], Is.False)
    Assert.That(afterB.Tokens.["B"], Is.False)
    twoCloneFixture (fun a b ->
        stageCommit a ("head=1\nlaunch=A\nop=" + aPlan.ReserveOperationId + "\n") "reserve A"
        stageCommit b ("head=1\nlaunch=B\nop=" + bPlan.ReserveOperationId + "\n") "competing reserve B"
        gitOk a ["push"; "origin"; "HEAD:ledger"] |> ignore
        let rejectedPush, _ = git b ["push"; "origin"; "HEAD:ledger"]
        Assert.That(rejectedPush, Is.Not.EqualTo(0))
        gitOk b ["fetch"; "origin"; "ledger"] |> ignore
        let observed = gitOk b ["show"; "origin/ledger:state.txt"]
        Assert.That(observed, Does.Contain("launch=A"))
        Assert.That(observed, Does.Not.Contain("launch=B"))
        mustReject Rejection.CasConflict (reserveAccount bPlan afterA)
        let duplicate = reserveAccount aPlan afterA |> ok
        Assert.That(duplicate, Is.EqualTo(afterA))
        Assert.That(planHypotheticalFixtureOnly (snapshot afterA [entry]) "C" |> ok, Is.EqualTo(None)))

[<Test>]
let disposable_two_clone_project_c01_abort_fences_late_admission () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let reserved = reserveAccount candidate (emptyAccount 1) |> ok
    let couldAdmit = admitAfterReserve candidate reserved entry.State |> ok
    let abort = abortUnboundC01 candidate entry.State |> ok
    twoCloneFixture (fun a b ->
        stageCommit a ("head=1\nlaunch=L\nstate=abort\nop=" + candidate.AbortOperationId + "\n") "C01 terminal tombstone"
        stageCommit b ("head=1\nlaunch=L\nstate=admitted\nop=" + candidate.AdmitOperationId + "\n") "racing project admission"
        gitOk a ["push"; "origin"; "HEAD:ledger"] |> ignore
        let rejectedPush, _ = git b ["push"; "origin"; "HEAD:ledger"]
        Assert.That(rejectedPush, Is.Not.EqualTo(0))
        gitOk b ["fetch"; "origin"; "ledger"] |> ignore
        Assert.That(gitOk b ["show"; "origin/ledger:state.txt"], Does.Contain("state=abort"))
        mustReject Rejection.CasConflict (admitAfterReserve candidate reserved abort)
        mustReject (Rejection.InvalidState "project admission exists")
            (abortUnboundC01 candidate couldAdmit)
        let released = releaseAfterTerminal candidate abort reserved |> ok
        Assert.That(released.Tokens.["L"], Is.True))

[<Test>]
let disposable_two_clone_first_connect_one_uuid_wins_different_sessions_rejected () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let _, before = submitted candidate (emptyAccount 1) entry.State
    let uuidA = Guid.NewGuid().ToString("D")
    let uuidB = Guid.NewGuid().ToString("D")
    let winner, receiptA =
        initializeFirstConnect before.Version "init:A" "L" "session:A" 0L (fun () -> uuidA) worker before |> ok
    let _, receiptB =
        initializeFirstConnect before.Version "init:B" "L" "session:B" 0L (fun () -> uuidB) worker before |> ok
    Assert.That(receiptA.AgentUuid, Is.Not.EqualTo(receiptB.AgentUuid))
    twoCloneFixture (fun a b ->
        stageCommit a ("head=1\nlaunch=L\nsession=session:A\nuuid=" + uuidA + "\n") "initialize A"
        stageCommit b ("head=1\nlaunch=L\nsession=session:B\nuuid=" + uuidB + "\n") "competing initialize B"
        gitOk a ["push"; "origin"; "HEAD:ledger"] |> ignore
        let rejectedPush, _ = git b ["push"; "origin"; "HEAD:ledger"]
        Assert.That(rejectedPush, Is.Not.EqualTo(0))
        gitOk b ["fetch"; "origin"; "ledger"] |> ignore
        let observed = gitOk b ["show"; "origin/ledger:state.txt"]
        Assert.That(observed, Does.Contain(uuidA))
        Assert.That(observed, Does.Not.Contain(uuidB))
        mustReject Rejection.AlreadyRegistered
            (initializeFirstConnect before.Version "init:B" "L" "session:B" 0L
                (fun () -> Guid.NewGuid().ToString("D")) worker winner)
        let _, recovered =
            initializeFirstConnect before.Version "init:A" "L" "session:A" 0L
                (fun () -> failwith "replay unexpectedly allocated UUID") worker winner |> ok
        Assert.That(recovered, Is.EqualTo(receiptA)))

[<Test>]
let duplicate_registered_uuid_and_reused_session_key_are_rejected_across_launches () =
    let entry = project "alpha" [worker] [work "alpha" "W" 1L; work "alpha" "V" 2L]
    let account0 = emptyAccount 2
    let firstPlan = planned "L1" (snapshot account0 [entry])
    let account1, firstReserved = admission firstPlan account0 entry.State
    let firstSubmitted = recordDispatchOutcome firstPlan DispatchState.Submitted firstReserved |> ok
    let firstUuid = Guid.NewGuid().ToString("D")
    let firstRegistered, _ =
        initializeFirstConnect firstSubmitted.Version "init:L1" "L1" "provider-session-1" 1L
            (fun () -> firstUuid) worker firstSubmitted |> ok
    let secondPlan = planned "L2" (snapshot account1 [{entry with State = firstRegistered}])
    let account2, secondReserved = admission secondPlan account1 firstRegistered
    let secondSubmitted = recordDispatchOutcome secondPlan DispatchState.Submitted secondReserved |> ok
    Assert.That(account2.Tokens.Count, Is.EqualTo(2))
    mustReject (Rejection.InvalidState "provider session key already bound to another launch")
        (initializeFirstConnect secondSubmitted.Version "init:copy-key" "L2" "provider-session-1" 2L
            (fun () -> Guid.NewGuid().ToString("D")) worker secondSubmitted)
    mustReject (Rejection.InvalidState "UUID collision with registered agent")
        (initializeFirstConnect secondSubmitted.Version "init:duplicate-uuid" "L2" "provider-session-2" 2L
            (fun () -> firstUuid.ToUpperInvariant()) worker secondSubmitted)

[<Test>]
let expired_unregistered_rejects_fabricated_or_cross_launch_dispatch_evidence () =
    let entry = single ()
    let candidate = planned "L" (snapshot (emptyAccount 1) [entry])
    let _, admitted = admission candidate (emptyAccount 1) entry.State
    let submitted = recordDispatchOutcome candidate DispatchState.Submitted admitted |> ok
    let deadline =
        { LaunchId = "L"
          DeadlineAt = 10L
          OriginalDispatchOperationId = "never-journaled"
          OriginalJournalSettled = true }
    mustReject Rejection.UnsafeExternalEffect
        (expireUnregistered submitted.Version "expire" 100L deadline submitted)
    mustReject Rejection.UnsafeExternalEffect
        (expireUnregistered submitted.Version "expire" 100L
            { deadline with OriginalDispatchOperationId = candidate.AbortOperationId } submitted)
    let fenced =
        expireUnregistered submitted.Version "expire" 100L
            { deadline with OriginalDispatchOperationId = candidate.DispatchOperationId } submitted |> ok
    Assert.That(fenced.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "unregistered:expire"))

[<Test>]
let disposable_two_clone_same_launch_reuses_original_reservation_receipt () =
    let entry = single ()
    let initial = snapshot (emptyAccount 1) [entry]
    let aPlan = planned "same-launch" initial
    let bPlan = planned "same-launch" initial
    Assert.That(aPlan.RequestFingerprint, Is.EqualTo(bPlan.RequestFingerprint))
    let winnerAccount = reserveAccount aPlan initial.Account |> ok
    twoCloneFixture (fun a b ->
        let original = "head=1\nlaunch=same-launch\nop=" + aPlan.ReserveOperationId + "\n"
        stageCommit a original "client A original same-launch reserve"
        stageCommit b original "client B duplicate same-launch reserve"
        gitOk a ["push"; "origin"; "HEAD:ledger"] |> ignore
        let rejectedPush, _ = git b ["push"; "origin"; "HEAD:ledger"]
        Assert.That(rejectedPush, Is.Not.EqualTo(0))
        gitOk b ["fetch"; "origin"; "ledger"] |> ignore
        Assert.That(gitOk b ["show"; "origin/ledger:state.txt"], Is.EqualTo(original))
        let recovered = reserveAccount bPlan winnerAccount |> ok
        Assert.That(recovered, Is.EqualTo(winnerAccount))
        Assert.That(recovered.Tokens.Count, Is.EqualTo(1)))

[<Test>]
let forged_pre_registered_plan_cannot_claim_or_dispatch_with_a_borrowed_launch_identity () =
    let entry = single ()
    let account = emptyAccount 1
    let canonical = planned "L" (snapshot account [entry])
    let forged =
        { canonical with
            Launch =
                { canonical.Launch with
                    State = LaunchState.Registered(Guid.NewGuid().ToString("D"), "forged-session")
                    Lease = Some { LastSeenAt = 0L; Generation = 1; StopRequested = false } } }
    mustReject (Rejection.InvalidState "malformed or pre-registered launch plan")
        (reserveAccount forged account)
    let reserved = reserveAccount canonical account |> ok
    mustReject (Rejection.InvalidState "malformed or pre-registered launch plan")
        (admitAfterReserve forged reserved entry.State)
    mustReject (Rejection.InvalidState "malformed or pre-registered launch plan")
        (abortUnboundC01 forged entry.State)
    Assert.That(dispatchAdvice forged entry.State, Is.EqualTo(InertDispatchAdvice.DoNotSubmit))
    mustReject (Rejection.InvalidState "malformed or pre-registered launch plan")
        (recordDispatchOutcome forged DispatchState.Submitted entry.State)
    let terminal = abortUnboundC01 canonical entry.State |> ok
    mustReject (Rejection.InvalidState "malformed or pre-registered launch plan")
        (releaseAfterTerminal forged terminal reserved)
    let mismatchedOp = { canonical with DispatchOperationId = "new-unsafe-send-id" }
    Assert.That(dispatchAdvice mismatchedOp entry.State, Is.EqualTo(InertDispatchAdvice.DoNotSubmit))
