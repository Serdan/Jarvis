module Client.Tests.AccountCapacityLedgerTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Client.AccountCapacityLedger
open Client.LedgerStore
open NUnit.Framework

type private GitFixture =
    { AccountOne: AccountBinding
      AccountTwo: AccountBinding
      ProjectOne: LedgerBinding
      ProjectTwo: LedgerBinding
      CloneOne: string
      CloneTwo: string
      SourceHead: string
      Remote: string }

[<CLIMutable>]
type ProjectTerminalDocument =
    { LaunchId: string
      ProjectId: string
      AccountAdmissionOperationId: string
      TerminalKind: string
      LaunchGeneration: int64 }

module private AccountFixture =
    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        Assert.That(proc.Start(), Is.True)
        let output = proc.StandardOutput.ReadToEnd()
        let errors = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        Assert.That(proc.ExitCode, Is.EqualTo(0), errors)
        output.Trim()

    let success result =
        match result with
        | Ok value -> value
        | Error e -> failwithf "Expected successful ledger operation, got %A" e

    // Keep the foundation tests scoped to their disposable, account-local project
    // authority. Production callers must pass a full independently trusted binding.
    let reserve (b: AccountBinding) op actor launch (projectId: Guid) role version =
        AccountCapacityLedger.reserve b op actor launch
            { ProjectId = projectId; LedgerId = projectId; Epoch = 1
              ApprovedFetchUrl = b.ApprovedFetchUrl; ApprovedPushUrl = b.ApprovedPushUrl }
            role version

    let withGit capacity action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-account-fixture-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let one, two = Path.Combine(root, "client-one"), Path.Combine(root, "client-two")
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; one ] |> ignore
            git one [ "config"; "user.name"; "Fixture" ] |> ignore
            git one [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "must never be modified by account admission")
            git one [ "add"; "source.txt" ] |> ignore
            git one [ "commit"; "-m"; "immutable source fixture" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; remote; two ] |> ignore
            let sourceHead = git one [ "rev-parse"; "HEAD" ]
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let projectId = Guid.NewGuid()
            let projectLedgerId = projectId
            let account path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            let project path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = projectLedgerId; Epoch = 1 }
            AccountCapacityLedger.initialize (account one) capacity |> success |> ignore
            LedgerStore.initialize (project one) true |> success |> ignore
            let fixture =
                { AccountOne = account one; AccountTwo = account two
                  ProjectOne = project one; ProjectTwo = project two
                  CloneOne = one; CloneTwo = two
                  SourceHead = sourceHead; Remote = remote }
            action fixture
            Assert.That(git one [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git two [ "rev-parse"; "HEAD" ], Is.EqualTo(sourceHead))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git two [ "status"; "--porcelain" ], Is.Empty)
        finally
            Directory.Delete(root, true)

    let terminalPath (launch: Guid) = $"records/terminal-{launch:N}.json"
    // One absence/version guard for BOTH possible project admission outcomes.
    // The admission write and abort-only tombstone race on this exact path.
    let decisionPath (launch: Guid) = $"records/launch-{launch:N}.json"

    let decision (p: LedgerBinding) launch accountAdmission kind =
        { LaunchId = string launch; ProjectId = string p.ProjectId
          AccountAdmissionOperationId = string accountAdmission
          TerminalKind = kind; LaunchGeneration = 1L }

    let tryPublishProjectAdmission (p: LedgerBinding) (launch: Guid) (accountAdmission: Guid) =
        LedgerStore.putRecord p
            { OperationId = accountAdmission; Actor = "project admission fixture"
              Path = decisionPath launch; ExpectedBlobOid = None
              Content = JsonSerializer.Serialize(decision p launch accountAdmission "AdmissionWon") }

    let publishProjectAdmission (p: LedgerBinding) (launch: Guid) (accountAdmission: Guid) =
        tryPublishProjectAdmission p launch accountAdmission |> success |> ignore

    let tryPublishTerminal (p: LedgerBinding) (launch: Guid) (accountAdmission: Guid)
                           kind operationId =
        let path =
            if kind = "AbortOnlyUnbound" then decisionPath launch
            else terminalPath launch
        LedgerStore.putRecord p
            { OperationId = operationId; Actor = "project terminal fixture"
              Path = path; ExpectedBlobOid = None
              Content = JsonSerializer.Serialize(decision p launch accountAdmission kind) }
        |> Result.map (fun outcome ->
            { ProjectId = p.ProjectId; LaunchId = launch
              AdmissionOperationId = accountAdmission
              TerminalOperationId = operationId }, outcome)

    let publishTerminal (p: LedgerBinding) (launch: Guid) (accountAdmission: Guid) kind =
        tryPublishTerminal p launch accountAdmission kind (Guid.NewGuid()) |> success |> fst

    // Disposable WQ-02 adapter: the single decision-path CAS fences the loser.
    // Resolve the exact immutable operation at a fresh canonical project head.
    // An absent admission op is NOT proof alone; only the original abort winner
    // recorded at that same immutable path can justify abort-only release.
    let verifier (p: LedgerBinding) : IProjectTerminalReceiptVerifier =
        { new IProjectTerminalReceiptVerifier with
            member _.VerifyCurrentTerminal locator =
                match LedgerStore.fetch p with
                | Error error -> Error(sprintf "Project head unverified: %A" error)
                | Ok snapshot ->
                    let guardPath = decisionPath locator.LaunchId
                    let terminalPath = terminalPath locator.LaunchId
                    let readDecision =
                        match LedgerStore.readAt p snapshot guardPath with
                        | Ok(Some raw, Some oid) ->
                            try
                                let document = JsonSerializer.Deserialize<ProjectTerminalDocument>(raw)
                                if isNull (box document)
                                   || document.LaunchId <> string locator.LaunchId
                                   || document.ProjectId <> string locator.ProjectId
                                   || document.AccountAdmissionOperationId <> string locator.AdmissionOperationId then
                                    Error "Per-launch decision identity mismatch"
                                else Ok(document, raw, oid)
                            with ex -> Error(sprintf "Invalid per-launch decision: %s" ex.Message)
                        | _ -> Error "No canonical per-launch admission/abort CAS decision"
                    match readDecision with
                    | Error error -> Error error
                    | Ok(decision, decisionRaw, decisionOid) ->
                        match LedgerStore.getOperationAt p snapshot locator.TerminalOperationId with
                        | Error error -> Error(sprintf "Terminal operation unverified: %A" error)
                        | Ok None -> Error "No independently committed project terminal operation"
                        | Ok(Some operation) ->
                            let path =
                                if decision.TerminalKind = "AbortOnlyUnbound" then guardPath
                                else terminalPath
                            if operation.Paths.Length <> 1 || operation.Paths[0] <> path then
                                Error "Terminal operation does not own the required launch path"
                            else
                                match LedgerStore.getOperationOriginAt p snapshot operation with
                                | Error error -> Error(sprintf "No immutable terminal origin: %A" error)
                                | Ok origin ->
                                    match LedgerStore.readAt p snapshot path,
                                          LedgerStore.readAt p { snapshot with Commit = origin } path with
                                    | Ok(Some raw, Some oid), Ok(Some original, Some originalOid)
                                        when raw = original && oid = originalOid ->
                                        try
                                            let document = JsonSerializer.Deserialize<ProjectTerminalDocument>(raw)
                                            if isNull (box document)
                                               || document.LaunchId <> string locator.LaunchId
                                               || document.ProjectId <> string locator.ProjectId
                                               || document.AccountAdmissionOperationId <> string locator.AdmissionOperationId then
                                                Error "Project terminal identity mismatch"
                                            else
                                                let decisionVerified =
                                                    match document.TerminalKind with
                                                    | "AbortOnlyUnbound" when path = guardPath
                                                         && decision.TerminalKind = "AbortOnlyUnbound"
                                                         && raw = decisionRaw && oid = decisionOid ->
                                                        match LedgerStore.getOperationAt p snapshot locator.AdmissionOperationId with
                                                        | Ok None -> Ok()
                                                        | _ -> Error "Original admission committed despite abort-only decision"
                                                    | "LaunchTerminal" when path = terminalPath
                                                         && decision.TerminalKind = "AdmissionWon" ->
                                                        match LedgerStore.getOperationAt p snapshot locator.AdmissionOperationId with
                                                        | Ok(Some admissionOperation)
                                                            when admissionOperation.Paths.Length = 1
                                                              && admissionOperation.Paths[0] = guardPath ->
                                                            match LedgerStore.getOperationOriginAt p snapshot admissionOperation with
                                                            | Error error -> Error(sprintf "Admission origin unverified: %A" error)
                                                            | Ok admissionOrigin ->
                                                                match LedgerStore.readAt p
                                                                        { snapshot with Commit = admissionOrigin } guardPath with
                                                                | Ok(Some admissionRaw, Some admissionOid)
                                                                    when admissionRaw = decisionRaw
                                                                      && admissionOid = decisionOid -> Ok()
                                                                | _ -> Error "Original admission did not win immutable launch CAS"
                                                        | _ -> Error "Original admission operation absent or incompatible"
                                                    | _ -> Error "Project terminal and original launch CAS are incompatible"
                                                match decisionVerified with
                                                | Error error -> Error error
                                                | Ok () ->
                                                    Ok { ProjectId = document.ProjectId
                                                         LaunchId = document.LaunchId
                                                         AdmissionOperationId = document.AccountAdmissionOperationId
                                                         ProjectLedgerId = string p.LedgerId
                                                         ProjectLedgerEpoch = p.Epoch
                                                         ProjectAuthoritySha256 =
                                                             AccountCapacityLedger.projectAuthoritySha256
                                                                 { ProjectId = p.ProjectId; LedgerId = p.LedgerId
                                                                   Epoch = p.Epoch
                                                                   ApprovedFetchUrl = p.ApprovedFetchUrl
                                                                   ApprovedPushUrl = p.ApprovedPushUrl }
                                                         ProjectHead = snapshot.Commit
                                                         TerminalOperationId = string locator.TerminalOperationId
                                                         TerminalBlobOid = oid
                                                         TerminalKind = document.TerminalKind
                                                         LaunchGeneration = document.LaunchGeneration }
                                        with ex -> Error(sprintf "Malformed project terminal: %s" ex.Message)
                                    | _ -> Error "Terminal path overwritten or lacks immutable origin" }

[<TestFixture>]
type AccountCapacityLedgerTests() =
    let success = AccountFixture.success

    [<Test>]
    member _.CapOneConcurrentCrossProjectReservesNeverOvercommit() =
        AccountFixture.withGit 1 (fun f ->
            let p1, p2 = Guid.NewGuid(), Guid.NewGuid()
            let l1, l2 = Guid.NewGuid(), Guid.NewGuid()
            let op1, op2 = Guid.NewGuid(), Guid.NewGuid()
            let initialProject = LedgerStore.fetch f.ProjectOne |> success
            use gate = new ManualResetEventSlim(false)
            let first = Task.Run(fun () ->
                gate.Wait()
                AccountFixture.reserve f.AccountOne op1 "Worker" l1 p1 "Worker" 1L)
            let second = Task.Run(fun () ->
                gate.Wait()
                AccountFixture.reserve f.AccountTwo op2 "Reviewer" l2 p2 "Reviewer" 1L)
            gate.Set()
            Task.WaitAll([| first :> Task; second :> Task |])
            let outcomes = [ first.Result; second.Result ]
            Assert.That(outcomes |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
            Assert.That(outcomes |> List.filter (function
                | Error(AccountCapacityFull(_, 1)) -> true | _ -> false) |> List.length, Is.EqualTo(1))
            let state = (AccountCapacityLedger.fetch f.AccountTwo |> success).State
            Assert.That(AccountCapacityLedger.occupied state, Is.EqualTo(1))
            Assert.That(state.Tokens.Length, Is.EqualTo(1))
            Assert.That((LedgerStore.fetch f.ProjectTwo |> success).Commit,
                        Is.EqualTo(initialProject.Commit)))

    [<Test>]
    member _.SuccessfulReceiptReplayUsesFullSemanticEquality() =
        AccountFixture.withGit 2 (fun f ->
            let launch, project, op = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            let first =
                AccountFixture.reserve f.AccountOne op "Scheduler" launch project "Worker" 3L
                |> success
            let replay =
                AccountFixture.reserve f.AccountTwo op "Scheduler" launch project "Worker" 3L
                |> success
            Assert.That(first.AlreadyApplied, Is.False)
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(first.Receipt.RequestSha256, Is.EqualTo(replay.Receipt.RequestSha256))
            Assert.That(first.Receipt.OriginalResultJson, Is.EqualTo(replay.Receipt.OriginalResultJson))
            match AccountFixture.reserve f.AccountTwo op "Scheduler" launch project "Reviewer" 3L with
            | Error AccountOperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Changed role reusing op ID must fail: %A" other)
            match AccountFixture.reserve f.AccountTwo op "Scheduler" launch (Guid.NewGuid()) "Worker" 3L with
            | Error AccountOperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Changed project reusing op ID must fail: %A" other)
            match AccountFixture.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler" launch project "Worker" 3L with
            | Error AccountLaunchExists -> ()
            | other -> Assert.Fail(sprintf "Second op may not mint another token: %A" other)
            let _, receipt = AccountCapacityLedger.readOperation f.AccountTwo op |> success
            Assert.That(receipt.IsSome, Is.True)
            Assert.That(receipt.Value.OriginalResultJson, Is.EqualTo(first.Receipt.OriginalResultJson)))

    [<Test>]
    member _.AcceptedGitPushWithLostAckAndFailedPostPushFetchReplaysOriginalReceipt() =
        AccountFixture.withGit 1 (fun f ->
            let launch, project, op = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            let mutable pushAcceptedWithoutAck = false
            let mutable postPushFetchFailed = false
            let hooks =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = fun args ->
                    match args with
                    | "push" :: _ when not pushAcceptedWithoutAck ->
                        pushAcceptedWithoutAck <- true
                        AccountGitTestFault.ExecuteThenLoseAcknowledgment
                    | "fetch" :: _ when pushAcceptedWithoutAck && not postPushFetchFailed ->
                        postPushFetchFailed <- true
                        AccountGitTestFault.FailBeforeExecution
                    | _ -> AccountGitTestFault.PassThrough }
            let unknown =
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    AccountFixture.reserve f.AccountOne op "Scheduler"
                        launch project "Worker" 1L)
            match unknown with
            | Error(AccountOutcomeUnknown actual) when actual = op -> ()
            | other -> Assert.Fail(sprintf "Post-push uncertainty must be explicit: %A" other)
            Assert.That(pushAcceptedWithoutAck, Is.True)
            Assert.That(postPushFetchFailed, Is.True)
            // The original push DID reach the bare remote. Reconciliation must
            // return the exact immutable receipt without a second mutation.
            let initial, original = AccountCapacityLedger.readOperation f.AccountTwo op |> success
            Assert.That(original.IsSome, Is.True)
            let replay =
                AccountFixture.reserve f.AccountTwo op "Scheduler"
                    launch project "Worker" 1L |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Receipt, Is.EqualTo(original.Value))
            Assert.That(replay.CurrentHead, Is.EqualTo(initial.Head))
            Assert.That(initial.State.Tokens.Length, Is.EqualTo(1))
            Assert.That(AccountCapacityLedger.occupied initial.State, Is.EqualTo(1)))

    [<Test>]
    member _.DeterministicallyConcurrentClonesUseSameParentAndLoserReplans() =
        AccountFixture.withGit 1 (fun f ->
            let oldHead = (AccountCapacityLedger.fetch f.AccountOne |> success).Head
            let seen = ConcurrentDictionary<Guid, bool>()
            let heads = ConcurrentBag<string>()
            use twoPlanned = new Barrier(2)
            let hooks =
                { BeforeLeasePush = fun op head ->
                    if seen.TryAdd(op, true) then
                        heads.Add(head)
                        Assert.That(twoPlanned.SignalAndWait(TimeSpan.FromSeconds(45.)), Is.True,
                                    "Both clones must plan before either push")
                  GitCommand = fun _ -> AccountGitTestFault.PassThrough }
            let run account =
                Task.Run(fun () ->
                    AccountCapacityLedger.withTestInterposition hooks (fun () ->
                        AccountFixture.reserve account (Guid.NewGuid())
                            "Scheduler" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1L))
            let first, second = run f.AccountOne, run f.AccountTwo
            Task.WaitAll([| first :> Task; second :> Task |])
            Assert.That(heads.Count, Is.EqualTo(2))
            Assert.That(heads |> Seq.forall ((=) oldHead), Is.True,
                        "Both writers must have computed from the same stale parent")
            let outcomes = [ first.Result; second.Result ]
            Assert.That(outcomes |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
            Assert.That(outcomes |> List.filter (function
                | Error(AccountCapacityFull(_, 1)) -> true | _ -> false) |> List.length,
                Is.EqualTo(1))
            let state = (AccountCapacityLedger.fetch f.AccountTwo |> success).State
            Assert.That(AccountCapacityLedger.occupied state, Is.EqualTo(1))
            Assert.That(state.Tokens.Length, Is.EqualTo(1)))

    [<Test>]
    member _.UnverifiableAccountReleaseTransportKeepsTokenHeldUntilExactReplay() =
        AccountFixture.withGit 1 (fun f ->
            let launch, admission = Guid.NewGuid(), Guid.NewGuid()
            AccountFixture.reserve f.AccountOne admission "Scheduler" launch
                f.ProjectOne.ProjectId "Worker" 1L |> success |> ignore
            AccountFixture.publishProjectAdmission f.ProjectOne launch admission
            let terminal =
                AccountFixture.publishTerminal f.ProjectOne launch admission "LaunchTerminal"
            let releaseId = Guid.NewGuid()
            let mutable attemptedPush = false
            let hooks =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = fun args ->
                    match args with
                    | "push" :: _ ->
                        attemptedPush <- true
                        AccountGitTestFault.FailBeforeExecution
                    | "fetch" :: _ when attemptedPush ->
                        AccountGitTestFault.FailBeforeExecution
                    | _ -> AccountGitTestFault.PassThrough }
            let result =
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    AccountCapacityLedger.release f.AccountOne
                        (AccountFixture.verifier f.ProjectTwo) releaseId "Reconciler" terminal)
            match result with
            | Error(AccountOutcomeUnknown id) when id = releaseId -> ()
            | other -> Assert.Fail(sprintf "Unverifiable release must be unknown: %A" other)
            Assert.That(attemptedPush, Is.True)
            let snapshot, receipt = AccountCapacityLedger.readOperation f.AccountTwo releaseId |> success
            Assert.That(receipt.IsNone, Is.True)
            Assert.That(AccountCapacityLedger.occupied snapshot.State, Is.EqualTo(1))
            match AccountFixture.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                    (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1L with
            | Error(AccountCapacityFull(1, 1)) -> ()
            | other -> Assert.Fail(sprintf "Outcome-unknown release improperly freed token: %A" other)
            AccountCapacityLedger.release f.AccountTwo (AccountFixture.verifier f.ProjectTwo)
                releaseId "Reconciler" terminal |> success |> ignore
            Assert.That((AccountCapacityLedger.fetch f.AccountTwo |> success).State
                        |> AccountCapacityLedger.occupied, Is.Zero))

    [<Test>]
    member _.CapacityShrinkRacesReservationFromSameStaleParent() =
        AccountFixture.withGit 2 (fun f ->
            AccountFixture.reserve f.AccountOne (Guid.NewGuid()) "Scheduler"
                (Guid.NewGuid()) (Guid.NewGuid()) "Lead" 1L |> success |> ignore
            let oldHead = (AccountCapacityLedger.fetch f.AccountOne |> success).Head
            let observed = ConcurrentBag<string>()
            let seen = ConcurrentDictionary<Guid, bool>()
            use planned = new Barrier(2)
            let hooks =
                { BeforeLeasePush = fun op head ->
                    if seen.TryAdd(op, true) then
                        observed.Add(head)
                        Assert.That(planned.SignalAndWait(TimeSpan.FromSeconds(45.)), Is.True,
                                    "Shrink and reserve must both plan at the old head")
                  GitCommand = fun _ -> AccountGitTestFault.PassThrough }
            let reservation = Task.Run(fun () ->
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    AccountFixture.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                        (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1L))
            let shrink = Task.Run(fun () ->
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    AccountCapacityLedger.setCapacity f.AccountOne (Guid.NewGuid()) "Operator" 1L 1))
            Task.WaitAll([| reservation :> Task; shrink :> Task |])
            Assert.That(observed.Count, Is.EqualTo(2))
            Assert.That(observed |> Seq.forall ((=) oldHead), Is.True)
            let state = (AccountCapacityLedger.fetch f.AccountTwo |> success).State
            Assert.That(AccountCapacityLedger.occupied state, Is.LessThanOrEqualTo(state.MaxConcurrentAgents))
            Assert.That(Result.isOk reservation.Result, Is.Not.EqualTo(Result.isOk shrink.Result))
            match reservation.Result, shrink.Result with
            | Ok _, Error(AccountResizeBelowOccupancy 2) -> ()
            | Error(AccountCapacityFull(1, 1)), Ok _ -> ()
            | other -> Assert.Fail(sprintf "Unexpected same-parent reserve/resize result: %A" other)
            match AccountCapacityLedger.setCapacity f.AccountTwo (Guid.NewGuid()) "Operator"
                state.ConfigVersion 0 with
            | Error(InvalidAccountBinding _) -> ()
            | other -> Assert.Fail(sprintf "Zero capacity accepted: %A" other))

    [<Test>]
    member _.ReleaseRequiresCurrentVerifiedProjectTerminalAndNeverDoubleFrees() =
        AccountFixture.withGit 1 (fun f ->
            let launch, project, admission = Guid.NewGuid(), f.ProjectOne.ProjectId, Guid.NewGuid()
            AccountFixture.reserve f.AccountOne admission "Scheduler" launch project "Worker" 1L
            |> success |> ignore
            let verifier = AccountFixture.verifier f.ProjectTwo
            let missing =
                { ProjectId = project; LaunchId = launch
                  AdmissionOperationId = admission; TerminalOperationId = Guid.NewGuid() }
            match AccountCapacityLedger.release f.AccountTwo verifier
                (Guid.NewGuid()) "Reconciler" missing with
            | Error(AccountTerminalProofRejected _) -> ()
            | other -> Assert.Fail(sprintf "Missing terminal must retain capacity: %A" other)
            Assert.That((AccountCapacityLedger.fetch f.AccountOne |> success).State
                        |> AccountCapacityLedger.occupied, Is.EqualTo(1))
            AccountFixture.publishProjectAdmission f.ProjectOne launch admission
            let terminal = AccountFixture.publishTerminal f.ProjectOne launch admission "LaunchTerminal"
            let releaseOperation = Guid.NewGuid()
            let first =
                AccountCapacityLedger.release f.AccountTwo verifier releaseOperation "Reconciler" terminal
                |> success
            let replay =
                AccountCapacityLedger.release f.AccountOne verifier releaseOperation "Reconciler" terminal
                |> success
            Assert.That(first.AlreadyApplied, Is.False)
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(first.Receipt.OriginalResultJson, Is.EqualTo(replay.Receipt.OriginalResultJson))
            let successor = Guid.NewGuid()
            AccountFixture.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                successor (Guid.NewGuid()) "Reviewer" 1L |> success |> ignore
            match AccountCapacityLedger.release f.AccountOne verifier
                (Guid.NewGuid()) "Reconciler" terminal with
            | Error AccountLaunchAlreadyReleased -> ()
            | other -> Assert.Fail(sprintf "Second release must not free successor: %A" other)
            Assert.That((AccountCapacityLedger.fetch f.AccountOne |> success).State
                        |> AccountCapacityLedger.occupied, Is.EqualTo(1))
            match AccountFixture.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                launch project "Worker" 1L with
            | Error AccountLaunchExists -> ()
            | other -> Assert.Fail(sprintf "Released launch ID must never be reused: %A" other))

    [<Test>]
    member _.AbortWinsPerLaunchCasAndFencesOriginalAdmissionAfterCapacityRelease() =
        AccountFixture.withGit 1 (fun f ->
            let launch, admission = Guid.NewGuid(), Guid.NewGuid()
            AccountFixture.reserve f.AccountOne admission "Scheduler" launch
                f.ProjectOne.ProjectId "Worker" 1L |> success |> ignore
            let abortOp = Guid.NewGuid()
            let aborted, first =
                AccountFixture.tryPublishTerminal f.ProjectOne launch admission
                    "AbortOnlyUnbound" abortOp |> success
            Assert.That(first.AlreadyApplied, Is.False)
            // Actual late original operation, issued from another Git clone,
            // must not commit even when the released account slot is reused.
            let verifier = AccountFixture.verifier f.ProjectTwo
            AccountCapacityLedger.release f.AccountTwo verifier
                (Guid.NewGuid()) "Reconciler" aborted |> success |> ignore
            AccountFixture.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1L |> success |> ignore
            for _ in 1..2 do
                match AccountFixture.tryPublishProjectAdmission f.ProjectTwo launch admission with
                | Error(StaleEntityVersion _) -> ()
                | other -> Assert.Fail(sprintf "Late admission escaped winning abort CAS: %A" other)
            let _, replay =
                AccountFixture.tryPublishTerminal f.ProjectTwo launch admission
                    "AbortOnlyUnbound" abortOp |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That((AccountCapacityLedger.fetch f.AccountOne |> success).State
                        |> AccountCapacityLedger.occupied, Is.EqualTo(1)))

    [<Test>]
    member _.OriginalAdmissionWinsCasAndLateAbortCannotReleaseAccountToken() =
        AccountFixture.withGit 1 (fun f ->
            let launch, admission = Guid.NewGuid(), Guid.NewGuid()
            AccountFixture.reserve f.AccountOne admission "Scheduler" launch
                f.ProjectOne.ProjectId "Worker" 1L |> success |> ignore
            AccountFixture.publishProjectAdmission f.ProjectOne launch admission
            let abortOp = Guid.NewGuid()
            match AccountFixture.tryPublishTerminal f.ProjectTwo launch admission
                    "AbortOnlyUnbound" abortOp with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Abort escaped winning admission CAS: %A" other)
            let locator = { ProjectId = f.ProjectOne.ProjectId; LaunchId = launch
                            AdmissionOperationId = admission; TerminalOperationId = abortOp }
            match AccountCapacityLedger.release f.AccountTwo (AccountFixture.verifier f.ProjectTwo)
                (Guid.NewGuid()) "Reconciler" locator with
            | Error(AccountTerminalProofRejected _) -> ()
            | other -> Assert.Fail(sprintf "Uncommitted abort freed account slot: %A" other)
            Assert.That((AccountCapacityLedger.fetch f.AccountTwo |> success).State
                        |> AccountCapacityLedger.occupied, Is.EqualTo(1)))

    [<Test>]
    member _.AmbiguousOriginalProjectAdmissionAcknowledgmentReplaysSameCasOutcome() =
        AccountFixture.withGit 1 (fun f ->
            let launch, admission = Guid.NewGuid(), Guid.NewGuid()
            AccountFixture.reserve f.AccountOne admission "Scheduler" launch
                f.ProjectOne.ProjectId "Worker" 1L |> success |> ignore
            // Pretend the caller lost the first successful admission response.
            // Reissue the exact original UUID from a different project clone.
            let first = AccountFixture.tryPublishProjectAdmission f.ProjectOne launch admission |> success
            let replay = AccountFixture.tryPublishProjectAdmission f.ProjectTwo launch admission |> success
            Assert.That(first.AlreadyApplied, Is.False)
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(first.Operation.OriginalResultJson,
                        Is.EqualTo(replay.Operation.OriginalResultJson))
            match AccountFixture.tryPublishTerminal f.ProjectTwo launch admission
                    "AbortOnlyUnbound" (Guid.NewGuid()) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Ambiguous admission allowed abort: %A" other)
            Assert.That((AccountCapacityLedger.fetch f.AccountTwo |> success).State
                        |> AccountCapacityLedger.occupied, Is.EqualTo(1))
            let terminal = AccountFixture.publishTerminal f.ProjectOne launch admission "LaunchTerminal"
            AccountCapacityLedger.release f.AccountTwo (AccountFixture.verifier f.ProjectTwo)
                (Guid.NewGuid()) "Reconciler" terminal |> success |> ignore
            Assert.That((AccountCapacityLedger.fetch f.AccountOne |> success).State
                        |> AccountCapacityLedger.occupied, Is.Zero))

    [<Test>]
    member _.CleanAbortOnlyTombstonePermitsReleaseWithoutProjectLaunch() =
        AccountFixture.withGit 1 (fun f ->
            let launch, admission = Guid.NewGuid(), Guid.NewGuid()
            AccountFixture.reserve f.AccountOne admission "Scheduler" launch
                f.ProjectOne.ProjectId "Worker" 1L |> success |> ignore
            let aborted = AccountFixture.publishTerminal f.ProjectOne launch admission "AbortOnlyUnbound"
            let outcome =
                AccountCapacityLedger.release f.AccountTwo (AccountFixture.verifier f.ProjectTwo)
                    (Guid.NewGuid()) "Reconciler" aborted |> success
            Assert.That(outcome.Receipt.Kind, Is.EqualTo("ReleaseGlobalAgentSlot"))
            Assert.That((AccountCapacityLedger.fetch f.AccountOne |> success).State
                        |> AccountCapacityLedger.occupied, Is.Zero))

    [<Test>]
    member _.ResizeReceiptReplayRejectsChangedPayloadAndStaleConfig() =
        AccountFixture.withGit 2 (fun f ->
            let launch, project = Guid.NewGuid(), Guid.NewGuid()
            AccountFixture.reserve f.AccountOne (Guid.NewGuid()) "Scheduler"
                launch project "Lead" 1L |> success |> ignore
            let op = Guid.NewGuid()
            let resized = AccountCapacityLedger.setCapacity f.AccountOne op "Operator" 1L 1 |> success
            let replay = AccountCapacityLedger.setCapacity f.AccountTwo op "Operator" 1L 1 |> success
            Assert.That(resized.AlreadyApplied, Is.False)
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Receipt.OriginalResultJson, Is.EqualTo(resized.Receipt.OriginalResultJson))
            match AccountCapacityLedger.setCapacity f.AccountTwo op "Operator" 1L 2 with
            | Error AccountOperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Reused config op with different limit: %A" other)
            match AccountCapacityLedger.setCapacity f.AccountOne (Guid.NewGuid()) "Operator" 1L 2 with
            | Error(AccountConfigVersionChanged 2L) -> ()
            | other -> Assert.Fail(sprintf "Old config version must be fenced: %A" other)
            let current = (AccountCapacityLedger.fetch f.AccountOne |> success).State
            Assert.That(current.MaxConcurrentAgents, Is.EqualTo(1))
            Assert.That(AccountCapacityLedger.occupied current, Is.EqualTo(1)))

    [<Test>]
    member _.DescendantMayNotRewriteAnImmutableAccountOperationReceipt() =
        AccountFixture.withGit 1 (fun f ->
            let launch, project, op = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            AccountFixture.reserve f.AccountOne op "Scheduler" launch project "Worker" 1L
            |> success |> ignore
            // A third writable clone creates a *descendant*, so ordinary
            // trusted-head ancestry alone would accept it; receipt origin must
            // still reject the changed bytes.
            let root = Path.GetDirectoryName(f.CloneOne)
            let tamper = Path.Combine(root, "receipt-tamper")
            AccountFixture.git root [ "clone"; f.Remote; tamper ] |> ignore
            AccountFixture.git tamper [ "config"; "user.name"; "Adversarial fixture" ] |> ignore
            AccountFixture.git tamper [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            AccountFixture.git tamper [ "checkout"; "-B"; "tamper"; "origin/jarvis-account-admission" ]
            |> ignore
            let relativePath = $"operations/{op:N}.json"
            let receiptFile = Path.Combine(tamper, "operations", $"{op:N}.json")
            let original = File.ReadAllText(receiptFile)
            let rewritten = original.Replace("\"actor\":\"Scheduler\"", "\"actor\":\"Forger\"")
            Assert.That(rewritten, Is.Not.EqualTo(original))
            File.WriteAllText(receiptFile, rewritten)
            AccountFixture.git tamper [ "add"; relativePath ] |> ignore
            AccountFixture.git tamper [ "commit"; "-m"; "rewrite existing account receipt" ] |> ignore
            AccountFixture.git tamper
                [ "push"; "origin"; $"HEAD:{AccountCapacityLedger.AccountRef}" ] |> ignore
            match AccountCapacityLedger.readOperation f.AccountTwo op with
            | Error(AccountIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Descendant receipt rewrite must be rejected: %A" other))

    [<Test>]
    member _.TrustEpochAndHistoryRewritesAreFenced() =
        AccountFixture.withGit 1 (fun f ->
            match AccountCapacityLedger.fetch { f.AccountTwo with Epoch = 2 } with
            | Error(AccountIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Cross-epoch read should fail: %A" other)
            let anotherHead = AccountFixture.git f.CloneOne [ "rev-parse"; "HEAD" ]
            AccountFixture.git f.CloneOne
                [ "push"; "--force"; "origin"; $"{anotherHead}:{AccountCapacityLedger.AccountRef}" ]
            |> ignore
            match AccountCapacityLedger.fetch f.AccountOne with
            | Error(AccountIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Non-descendant remote rewrite accepted: %A" other))
