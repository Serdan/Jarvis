

// Appended by scripts/tests/scheduler-property-account-cas-20261122/run.sh
// to the frozen, source-pinned disposable Git fixture in this same compilation unit.
// This model intentionally does not call the production CAS implementation.
module private SchedulerAccountCasReference =
    type Stage = Reserved | Dispatching | Uncertain | Unknown | Released
    type Claim =
        { Key: int; Dispatch: int; Effect: int; Operation: int
          Pool: int; Project: int }
    type Entry = { Claim: Claim; Stage: Stage; Generation: int; Terminal: int option }
    type State =
        { Entries: Entry list; Receipts: Map<int, Claim>
          Head: int; Sequence: int }
    type Step =
        | Reserve of Claim
        | Move of int * Stage
        | Terminal of int * int * bool
        | BadCoverage of Claim
        | PoisonDuplicate of Claim
    type Outcome = Committed | Replayed | Rejected of string
    let empty () = { Entries = []; Receipts = Map.empty; Head = 1; Sequence = 0 }
    let occupied e = e.Stage <> Released
    let quota pool project =
        if pool = 0 then (2, if project = 0 then 1 else 2)
        else (2, if project = 0 then 2 else 1)
    let next state entries receipts =
        { Entries = entries; Receipts = receipts
          Head = state.Head + 1; Sequence = state.Sequence + 1 }
    let allowed a b =
        match a,b with
        | Reserved,Dispatching | Dispatching,Uncertain
        | Dispatching,Unknown | Uncertain,Unknown
        | Unknown,Dispatching -> true
        | _ -> false
    let apply state step =
        match step with
        | BadCoverage _ -> state, Rejected "PolicyNotAuthoritative"
        | PoisonDuplicate claim ->
            next state ({ Claim = claim; Stage = Reserved; Generation = 1; Terminal = None } :: state.Entries) state.Receipts, Committed
        | Reserve claim ->
            match Map.tryFind claim.Operation state.Receipts with
            | Some c when c = claim -> state, Replayed
            | Some _ -> state, Rejected "OperationIdReused"
            | None ->
                if state.Entries |> List.exists (fun e -> e.Claim.Key = claim.Key) then
                    state, Rejected "CanonicalDuplicate"
                elif state.Entries |> List.exists (fun e ->
                    e.Claim.Dispatch = claim.Dispatch || e.Claim.Effect = claim.Effect) then
                    state, Rejected "EffectIdentityDuplicate"
                else
                    let account, project = quota claim.Pool claim.Project
                    let active = state.Entries |> List.filter (fun e ->
                        occupied e && e.Claim.Pool = claim.Pool)
                    let local = active |> List.filter (fun e -> e.Claim.Project = claim.Project)
                    if active.Length >= account || local.Length >= project then
                        state, Rejected "CapacityFull"
                    else
                        let entry = { Claim = claim; Stage = Reserved; Generation = 1; Terminal = None }
                        next state (entry :: state.Entries) (state.Receipts.Add(claim.Operation,claim)), Committed
        | Move (dispatch, stage) ->
            match state.Entries |> List.tryFind (fun e -> e.Claim.Dispatch = dispatch) with
            | None -> state, Rejected "Missing"
            | Some e when not (allowed e.Stage stage) -> state, Rejected "InvalidTransition"
            | Some _ ->
                let entries = state.Entries |> List.map (fun e ->
                    if e.Claim.Dispatch = dispatch then
                        { e with Stage = stage; Generation = e.Generation + 1 }
                    else e)
                next state entries state.Receipts, Committed
        | Terminal (dispatch, terminal, verified) ->
            match state.Entries |> List.tryFind (fun e -> e.Claim.Dispatch = dispatch) with
            | None -> state, Rejected "ForeignTerminal"
            | Some _ when not verified || terminal = 0 -> state, Rejected "UnverifiedTerminal"
            | Some e when e.Stage = Released -> state, Rejected "AlreadyReleased"
            | Some _ when state.Entries |> List.exists (fun e -> e.Terminal = Some terminal) ->
                state, Rejected "TerminalReused"
            | Some _ ->
                let entries = state.Entries |> List.map (fun e ->
                    if e.Claim.Dispatch = dispatch then
                        { e with Stage = Released; Terminal = Some terminal; Generation = e.Generation + 1 }
                    else e)
                next state entries state.Receipts, Committed

    let invariant state =
        let unique projection =
            let all = state.Entries |> List.map projection
            all.Length = (all |> List.distinct |> List.length)
        let capacityHolds =
            [ 0; 1 ] |> List.forall (fun pool ->
                let active = state.Entries |> List.filter (fun e -> occupied e && e.Claim.Pool = pool)
                let cap, _ = quota pool 0
                active.Length <= cap &&
                ([0;1] |> List.forall (fun project ->
                    let _, limit = quota pool project
                    (active |> List.filter (fun e -> e.Claim.Project = project) |> List.length) <= limit)))
        unique (fun e -> e.Claim.Key)
        && unique (fun e -> e.Claim.Dispatch)
        && unique (fun e -> e.Claim.Effect)
        && (state.Entries |> List.filter (fun e -> e.Terminal.IsSome)
                          |> List.map (fun e -> e.Terminal.Value)
                          |> fun terminals -> terminals.Length = (List.distinct terminals).Length)
        && capacityHolds
        && state.Head = state.Sequence + 1
        && (state.Entries |> List.forall (fun e -> e.Generation >= 1))

    let candidate seed i pool project =
        { Key = i % 11; Dispatch = seed * 1000 + i
          Effect = seed * 1000 + i + 500
          Operation = seed * 1000 + i + 1000
          Pool = pool; Project = project }

    let generate seed count =
        let rng = Random(seed)
        let pool = ResizeArray<Claim>()
        [ for i in 1..count ->
            let op = rng.Next(9)
            let newCandidate () =
                let c = candidate seed i (rng.Next(2)) (rng.Next(2))
                pool.Add c
                c
            if op < 4 || pool.Count = 0 then Reserve (newCandidate())
            else
                let selected = pool[rng.Next(pool.Count)]
                match op with
                | 4 -> Reserve selected
                | 5 -> Reserve { selected with Operation = seed * 1000 + i + 2000 }
                | 6 -> Move(selected.Dispatch, [|Dispatching;Uncertain;Unknown|][rng.Next(3)])
                | 7 -> Terminal(selected.Dispatch, seed * 1000 + i + 3000, rng.Next(2) = 0)
                | _ -> BadCoverage (newCandidate()) ]

    let run trace =
        let mutable state = empty ()
        let mutable firstFailure = None
        for index, action in trace |> List.indexed do
            let before = state
            let updated, outcome = apply state action
            if firstFailure.IsNone && not (invariant updated) then
                firstFailure <- Some(index, action)
            if firstFailure.IsNone && (match outcome with Rejected _ | Replayed -> true | _ -> false)
               && (before.Head <> updated.Head || before.Sequence <> updated.Sequence
                   || before.Entries <> updated.Entries) then
                firstFailure <- Some(index, action)
            state <- updated
        state, firstFailure

    // Replay-based delta debugger: remove chunks while the predicate still fails.
    let shrink fails (trace: Step list) =
        let rec loop size (current: Step list) =
            if size <= 0 then current
            else
                let mutable reduced = None
                let mutable offset = 0
                while offset < current.Length && reduced.IsNone do
                    let candidate =
                        current |> List.mapi (fun i x -> i,x)
                                |> List.choose (fun (i,x) ->
                                    if i >= offset && i < offset + size then None else Some x)
                    if fails candidate then reduced <- Some candidate
                    offset <- offset + size
                match reduced with
                | Some next -> loop (min size (next.Length / 2)) next
                | None -> loop (size / 2) current
        loop (trace.Length / 2) trace

[<TestFixture>]
type SchedulerAccountCasProperty() =
    let requirePolicy label result =
        match result with
        | Error(AccountPolicyNotAuthoritative _) -> ()
        | other -> Assert.Fail(sprintf "%s: expected AccountPolicyNotAuthoritative, actual=%A" label other)
    let requireFull label expected used result =
        match result with
        | Error(AccountCapacityFull(a,b)) when a = used && b = expected -> ()
        | other -> Assert.Fail(sprintf "%s: expected AccountCapacityFull(%d,%d), actual=%A" label used expected other)

    [<Test>]
    member _.IndependentModel384Seed24StepReplayShrinkAndMutationControls() =
        // 9,216 reproducible operations; no claim of 9,216 real Git operations.
        let mutable accepted = 0
        let mutable rejected = 0
        let mutable replayed = 0
        for seed in 1..384 do
            let trace = SchedulerAccountCasReference.generate seed 24
            let mutable current = SchedulerAccountCasReference.empty ()
            for index, action in trace |> List.indexed do
                let before = current
                let next, result = SchedulerAccountCasReference.apply current action
                Assert.That(SchedulerAccountCasReference.invariant next, Is.True,
                    sprintf "seed=%d step=%d %A" seed index action)
                match result with
                | SchedulerAccountCasReference.Committed -> accepted <- accepted + 1
                | SchedulerAccountCasReference.Replayed ->
                    replayed <- replayed + 1
                    Assert.That(next, Is.EqualTo(before))
                | SchedulerAccountCasReference.Rejected _ ->
                    rejected <- rejected + 1
                    Assert.That(next, Is.EqualTo(before))
                current <- next
            let _, failure = SchedulerAccountCasReference.run trace
            Assert.That(failure.IsNone, Is.True, sprintf "replay seed %d" seed)
        Assert.That(accepted, Is.GreaterThan(0))
        Assert.That(rejected, Is.GreaterThan(0))
        Assert.That(replayed, Is.GreaterThan(0))
        let poison = SchedulerAccountCasReference.PoisonDuplicate
        let c = SchedulerAccountCasReference.candidate 999 1 0 0
        let trace = [SchedulerAccountCasReference.Reserve c;
                     SchedulerAccountCasReference.BadCoverage c;
                     poison { c with Operation = 99999; Dispatch = 99998; Effect = 99997 };
                     SchedulerAccountCasReference.Move(c.Dispatch, SchedulerAccountCasReference.Unknown)]
        let bad actions = SchedulerAccountCasReference.run actions |> snd |> Option.isSome
        Assert.That(bad trace, Is.True, "mutated duplicate-key state must be detected")
        let minimal = SchedulerAccountCasReference.shrink bad trace
        Assert.That(bad minimal, Is.True, sprintf "shrinker lost failure: %A" minimal)
        Assert.That(minimal.Length, Is.LessThan(trace.Length))
        // It cannot shrink to a trace without a real invalid state.
        Assert.That((minimal |> List.exists (function
            | SchedulerAccountCasReference.PoisonDuplicate _ -> true | _ -> false)), Is.True)
        TestContext.Progress.WriteLine(
            sprintf "MODEL: seeds=384 steps=9216 accepted=%d rejected=%d replayed=%d shrink=%d->%d"
                accepted rejected replayed trace.Length minimal.Length)

    [<Test>]
    member _.RealGitTwoCloneMultiStepAccountWorkerReviewerSourceTrace() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 1)
                [|NativeReservationFixture.limits 1 1; NativeReservationFixture.limits 1 1|]
            let worker1 = NativeReservationFixture.worker f 0
            let worker2 = NativeReservationFixture.worker f 1
            let reviewer1 = NativeReservationFixture.reviewer f 0
            let reviewer2 = NativeReservationFixture.reviewer f 1
            let account = f.AccountOne
            let fresh () = NativeReservationFixture.coverage f account f.ProjectsOne
            let operation = Guid.NewGuid()
            let observed = fresh()
            let first = NativeReservationFixture.reserve f account f.ProjectsOne observed operation worker1
                        |> NativeReservationFixture.success
            Assert.That(first.AlreadyApplied, Is.False)
            let firstHead = NativeFixture.head account
            let replay =
                NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo observed operation worker1
                |> NativeReservationFixture.success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Receipt.ResultSha256, Is.EqualTo(first.Receipt.ResultSha256))
            Assert.That(NativeFixture.head account, Is.EqualTo(firstHead))
            match NativeReservationFixture.reserve f account f.ProjectsOne observed operation
                    { worker1 with OwnerAgentId = "forged actor" } with
            | Error AccountOperationIdReused -> ()
            | other -> Assert.Fail(sprintf "reused UUID changed payload: %A" other)
            Assert.That(NativeFixture.head account, Is.EqualTo(firstHead))
            requirePolicy "duplicate canonical Worker"
                (NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) (Guid.NewGuid())
                    { worker1 with DispatchId = Guid.NewGuid().ToString("D")
                                   EffectOperationId = Guid.NewGuid().ToString("D") })
            Assert.That(NativeFixture.head account, Is.EqualTo(firstHead))
            requireFull "Worker full" 1 1
                (NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo (fresh()) (Guid.NewGuid()) worker2)
            let reviewOperation = Guid.NewGuid()
            let reviewed =
                NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) reviewOperation reviewer1
                |> NativeReservationFixture.success
            Assert.That(reviewed.AlreadyApplied, Is.False)
            let beforeBadReview = NativeFixture.head account
            requirePolicy "full-width reviewer alias 2+2^32"
                (NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) (Guid.NewGuid())
                    { reviewer1 with ReviewVersion = 4294967298L })
            requirePolicy "stale reviewer version"
                (NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) (Guid.NewGuid())
                    { reviewer1 with ReviewVersion = 1L })
            requirePolicy "forged review-ref"
                (NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) (Guid.NewGuid())
                    { reviewer1 with ReviewRef = "refs/heads/jarvis-review/fixture/forged/r1" })
            requirePolicy "invalid zero dispatch"
                (NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) (Guid.NewGuid())
                    { reviewer1 with DispatchId = Guid.Empty.ToString("D") })
            Assert.That(NativeFixture.head account, Is.EqualTo(beforeBadReview))
            requireFull "Reviewer full" 1 1
                (NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) (Guid.NewGuid()) reviewer2)
            let fence =
                (LedgerStore.fetch f.ProjectsOne[0] |> NativeReservationFixture.success).Commit
            let advance expected status session =
                NativeSchedulerAccountReservation.transitionVerifiedDisposable
                    f.Root account f.PublicKey f.Subject f.ProjectsOne (fresh())
                    (Guid.NewGuid()) "Property fixture" (Guid.Parse worker1.DispatchId)
                    (Guid.Parse worker1.EffectOperationId) expected status fence session
                |> NativeReservationFixture.success |> ignore
            advance 1L "Dispatching" ""
            advance 2L "Uncertain" ""
            advance 3L "Unknown" "provider-property-session"
            let beforeUnknownDenied = NativeFixture.head account
            requireFull "Unknown remains occupied" 1 1
                (NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo (fresh()) (Guid.NewGuid()) worker2)
            Assert.That(NativeFixture.head account, Is.EqualTo(beforeUnknownDenied))
            let entry = (NativeReservationFixture.snapshot f account f.ProjectsOne).Records
                        |> Array.find (fun r -> r.Candidate.DispatchId = worker1.DispatchId)
            Assert.That(entry.Status, Is.EqualTo("Unknown"))
            let locator =
                { DispatchId = Guid.Parse worker1.DispatchId
                  OriginalReservationId = operation
                  EffectOperationId = Guid.Parse worker1.EffectOperationId
                  TerminalOperationId = Guid.NewGuid() }
            let proof = NativeReservationFixture.terminalProof f worker1 entry locator
                            fence "provider-property-session"
            let verifier =
                { new INativeDisposableTerminalVerifier with
                    member _.VerifyCurrentTerminal _ = Ok proof }
            let release l =
                NativeSchedulerAccountReservation.releaseVerifiedDisposable
                    f.Root account f.PublicKey f.Subject f.ProjectsOne (fresh())
                    verifier (Guid.NewGuid()) "Property terminal" l
            let beforeForeign = NativeFixture.head account
            requirePolicy "foreign reservation terminal"
                (release {locator with OriginalReservationId = Guid.NewGuid()})
            requirePolicy "invalid empty terminal"
                (release {locator with TerminalOperationId = Guid.Empty})
            Assert.That(NativeFixture.head account, Is.EqualTo(beforeForeign))
            release locator |> NativeReservationFixture.success |> ignore
            let released = (NativeReservationFixture.snapshot f account f.ProjectsOne).Records
                           |> Array.find (fun r -> r.Candidate.DispatchId = worker1.DispatchId)
            Assert.That(released.Status, Is.EqualTo("Released"))
            Assert.That(released.TerminalOperationId, Is.EqualTo(locator.TerminalOperationId.ToString("D")))
            let afterRelease = NativeFixture.head account
            requirePolicy "released canonical key cannot rearm"
                (NativeReservationFixture.reserve f account f.ProjectsOne (fresh()) (Guid.NewGuid())
                    { worker1 with DispatchId = Guid.NewGuid().ToString("D")
                                   EffectOperationId = Guid.NewGuid().ToString("D") })
            Assert.That(NativeFixture.head account, Is.EqualTo(afterRelease))
            NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo (fresh()) (Guid.NewGuid()) worker2
            |> NativeReservationFixture.success |> ignore
            let final = NativeReservationFixture.snapshot f account f.ProjectsOne
            Assert.That(final.Records.Length, Is.EqualTo(3))
            Assert.That(final.Records |> Array.filter (fun r ->
                r.Status <> "Released" && r.Candidate.Kind = "Worker") |> Array.length, Is.EqualTo(1))
            Assert.That(final.Records |> Array.map _.CanonicalKey |> Array.distinct |> Array.length, Is.EqualTo(3))
            TestContext.Progress.WriteLine("REALGIT: 2 account clones, 2 project remotes, Worker+Reviewer, replay/alias/unknown/terminal/release/unique HEAD"))

    [<Test>]
    member _.RealGitDeterministicTwoClientWorkerAndReviewerBarriers() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 1 1)
                [|NativeReservationFixture.limits 1 1; NativeReservationFixture.limits 1 1|]
            let workers = [|NativeReservationFixture.worker f 0; NativeReservationFixture.worker f 1|]
            let reviews = [|NativeReservationFixture.reviewer f 0; NativeReservationFixture.reviewer f 1|]
            let contest (candidates: NativeDispatchCandidate array) =
                let one = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
                let two = NativeReservationFixture.coverage f f.AccountTwo f.ProjectsTwo
                use gate = new ManualResetEventSlim(false)
                let tasks =
                    [|Task.Run(fun () ->
                        gate.Wait()
                        NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne one (Guid.NewGuid()) candidates[0])
                      Task.Run(fun () ->
                        gate.Wait()
                        NativeReservationFixture.reserve f f.AccountTwo f.ProjectsTwo two (Guid.NewGuid()) candidates[1])|]
                gate.Set()
                Task.WaitAll(tasks |> Array.map (fun t -> t :> Task))
                let winners = tasks |> Array.filter (fun t -> Result.isOk t.Result)
                Assert.That(winners.Length, Is.EqualTo(1), sprintf "winners for %s" candidates[0].Kind)
            contest workers
            contest reviews
            let state = NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne
            Assert.That(state.Records.Length, Is.EqualTo(2))
            Assert.That(state.Records |> Array.map _.CanonicalKey |> Array.distinct |> Array.length, Is.EqualTo(2))
            Assert.That(state.Records |> Array.map _.OriginalOperationId |> Array.distinct |> Array.length, Is.EqualTo(2))
            Assert.That(state.Records |> Array.filter (fun r -> r.Candidate.Kind = "Worker")
                                      |> Array.length, Is.EqualTo(1))
            Assert.That(state.Records |> Array.filter (fun r -> r.Candidate.Kind = "Reviewer")
                                      |> Array.length, Is.EqualTo(1))
            TestContext.Progress.WriteLine("REALGIT: 2 two-client barriers; exactly one Worker and one Reviewer account winner"))


    [<Test>]
    member _.RealGitZeroQuotaStaleClaimsAndUncertainFetchFailClosed() =
        NativeFixture.withGit (fun f ->
            NativeReservationFixture.establish f
                (NativeReservationFixture.limits 0 0)
                [|NativeReservationFixture.limits 0 0; NativeReservationFixture.limits 0 0|]
            let worker = NativeReservationFixture.worker f 0
            let reviewer = NativeReservationFixture.reviewer f 1
            let fresh () = NativeReservationFixture.coverage f f.AccountOne f.ProjectsOne
            let attempt candidate coverage =
                NativeReservationFixture.reserve f f.AccountOne f.ProjectsOne
                    coverage (Guid.NewGuid()) candidate
            let initialHead = NativeFixture.head f.AccountOne
            let rejectPolicy label candidate =
                requirePolicy label (attempt candidate (fresh()))
                Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(initialHead), label)
            requireFull "account Worker quota zero" 0 0 (attempt worker (fresh()))
            requireFull "account Reviewer quota zero" 0 0 (attempt reviewer (fresh()))
            rejectPolicy "stale Worker generation" { worker with AssignmentGeneration = worker.AssignmentGeneration + 1L }
            rejectPolicy "foreign Worker actor" { worker with OwnerAgentId = "Other Worker" }
            rejectPolicy "forged Worker source ref" { worker with SourceRef = "refs/heads/forged-lease" }
            rejectPolicy "forged Worker source lease digest"
                { worker with SourceLeaseSha256 = String.replicate 64 "b" }
            rejectPolicy "foreign project identity" { worker with ProjectId = Guid.NewGuid().ToString("D") }
            rejectPolicy "foreign project ledger" { worker with ProjectLedgerId = Guid.NewGuid().ToString("D") }
            rejectPolicy "stale project epoch" { worker with ProjectEpoch = worker.ProjectEpoch + 1 }
            rejectPolicy "Reviewer wrong actor" { reviewer with OwnerAgentId = "Other Reviewer" }
            rejectPolicy "Reviewer wrong claim" { reviewer with ClaimOperationId = Guid.NewGuid().ToString("D") }
            rejectPolicy "Reviewer wrong version" { reviewer with ReviewVersion = 1L }
            rejectPolicy "Reviewer version overflow" { reviewer with ReviewVersion = 4294967298L }
            let validCoverage = fresh()
            requirePolicy "incomplete account/project coverage"
                (attempt worker {validCoverage with Complete = false})
            requirePolicy "forged account head"
                (attempt worker {validCoverage with AccountHead = f.InitialSourceHead})
            requirePolicy "stale global coverage"
                (attempt worker {validCoverage with ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(-1.0)})
            let alteredAccount = {f.AccountOne with AccountId = Guid.NewGuid()}
            match NativeReservationFixture.reserve f alteredAccount f.ProjectsOne (fresh())
                    (Guid.NewGuid()) worker with
            | Error _ -> ()
            | success -> Assert.Fail(sprintf "forged account identity admitted: %A" success)
            Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(initialHead))
            let hooks =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = function
                    | "fetch" :: _ -> AccountGitTestFault.FailBeforeExecution
                    | _ -> AccountGitTestFault.PassThrough }
            let preFaultCoverage = fresh()
            let uncertain =
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    attempt worker preFaultCoverage)
            match uncertain with
            | Error _ -> ()
            | ok -> Assert.Fail(sprintf "unknown/failed account fetch admitted: %A" ok)
            Assert.That(NativeFixture.head f.AccountOne, Is.EqualTo(initialHead))
            Assert.That((NativeReservationFixture.snapshot f f.AccountOne f.ProjectsOne).Records.Length,
                        Is.EqualTo(0))
            TestContext.Progress.WriteLine("REALGIT NEGATIVES: zero quotas, stale source/actor/project, account and coverage forgery, fetch fail-closed; no account HEAD change"))
