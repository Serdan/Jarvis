module Client.Tests.AccountCapacityLedgerTests

open System
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
            let projectId, projectLedgerId = Guid.NewGuid(), Guid.NewGuid()
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
    let admissionPath (launch: Guid) = $"records/admission-{launch:N}.json"

    let publishProjectAdmission (p: LedgerBinding) (launch: Guid) (accountAdmission: Guid) =
        LedgerStore.putRecord p
            { OperationId = Guid.NewGuid(); Actor = "project fixture"
              Path = admissionPath launch; ExpectedBlobOid = None
              Content = JsonSerializer.Serialize(
                  {| launchId = string launch; projectId = string p.ProjectId
                     accountAdmissionOperationId = string accountAdmission |}) }
        |> success |> ignore

    let publishTerminal (p: LedgerBinding) (launch: Guid) (accountAdmission: Guid) kind =
        let operationId = Guid.NewGuid()
        let document =
            { LaunchId = string launch; ProjectId = string p.ProjectId
              AccountAdmissionOperationId = string accountAdmission
              TerminalKind = kind; LaunchGeneration = 1L }
        LedgerStore.putRecord p
            { OperationId = operationId; Actor = "project terminal fixture"
              Path = terminalPath launch; ExpectedBlobOid = None
              Content = JsonSerializer.Serialize(document) }
        |> success |> ignore
        { ProjectId = p.ProjectId; LaunchId = launch
          AdmissionOperationId = accountAdmission
          TerminalOperationId = operationId }

    // Fixture adapter reads the project authority on a freshly fetched verified
    // head, proves the original terminal operation introduced the immutable
    // terminal blob, and checks that admitted vs abort-only histories are
    // consistent. WQ-02 replaces this adapter with its canonical launch CAS.
    let verifier (p: LedgerBinding) : IProjectTerminalReceiptVerifier =
        { new IProjectTerminalReceiptVerifier with
            member _.VerifyCurrentTerminal locator =
                match LedgerStore.fetch p with
                | Error error -> Error(sprintf "Project head unverified: %A" error)
                | Ok snapshot ->
                    let path = terminalPath locator.LaunchId
                    match LedgerStore.readAt p snapshot path,
                          LedgerStore.getOperationAt p snapshot locator.TerminalOperationId with
                    | Ok(Some raw, Some oid), Ok(Some operation)
                        when operation.Paths |> Array.contains path ->
                        match LedgerStore.getOperationOriginAt p snapshot operation with
                        | Error error -> Error(sprintf "No immutable terminal origin: %A" error)
                        | Ok origin ->
                            match LedgerStore.readAt p { snapshot with Commit = origin } path with
                            | Ok(Some original, Some originalOid)
                                when original = raw && originalOid = oid ->
                                try
                                    let document = JsonSerializer.Deserialize<ProjectTerminalDocument>(raw)
                                    let matches =
                                        not (isNull (box document))
                                        && document.LaunchId = string locator.LaunchId
                                        && document.ProjectId = string locator.ProjectId
                                        && document.AccountAdmissionOperationId =
                                            string locator.AdmissionOperationId
                                    if not matches then Error "Project terminal identity mismatch"
                                    else
                                        match LedgerStore.readAt p snapshot (admissionPath locator.LaunchId) with
                                        | Error error -> Error(sprintf "Admission history unverified: %A" error)
                                        | Ok(admission, _) when document.TerminalKind = "LaunchTerminal"
                                                                && admission.IsSome ->
                                            Ok { ProjectId = document.ProjectId
                                                 LaunchId = document.LaunchId
                                                 AdmissionOperationId = document.AccountAdmissionOperationId
                                                 ProjectLedgerId = string p.LedgerId
                                                 ProjectHead = snapshot.Commit
                                                 TerminalOperationId = string locator.TerminalOperationId
                                                 TerminalBlobOid = oid
                                                 TerminalKind = document.TerminalKind
                                                 LaunchGeneration = document.LaunchGeneration }
                                        | Ok(None, _) when document.TerminalKind = "AbortOnlyUnbound" ->
                                            Ok { ProjectId = document.ProjectId
                                                 LaunchId = document.LaunchId
                                                 AdmissionOperationId = document.AccountAdmissionOperationId
                                                 ProjectLedgerId = string p.LedgerId
                                                 ProjectHead = snapshot.Commit
                                                 TerminalOperationId = string locator.TerminalOperationId
                                                 TerminalBlobOid = oid
                                                 TerminalKind = document.TerminalKind
                                                 LaunchGeneration = document.LaunchGeneration }
                                        | _ -> Error "Project admission/abort-only history contradicts terminal kind"
                                with ex -> Error(sprintf "Malformed project terminal: %s" ex.Message)
                            | _ -> Error "Terminal path overwritten or no longer matches original operation"
                    | _ -> Error "No independently committed current project terminal operation" }

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
                AccountCapacityLedger.reserve f.AccountOne op1 "Worker" l1 p1 "Worker" 1L)
            let second = Task.Run(fun () ->
                gate.Wait()
                AccountCapacityLedger.reserve f.AccountTwo op2 "Reviewer" l2 p2 "Reviewer" 1L)
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
    member _.LostAcknowledgementReplaysStableReceiptsWithFullSemanticEquality() =
        AccountFixture.withGit 2 (fun f ->
            let launch, project, op = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            let first =
                AccountCapacityLedger.reserve f.AccountOne op "Scheduler" launch project "Worker" 3L
                |> success
            let replay =
                AccountCapacityLedger.reserve f.AccountTwo op "Scheduler" launch project "Worker" 3L
                |> success
            Assert.That(first.AlreadyApplied, Is.False)
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(first.Receipt.RequestSha256, Is.EqualTo(replay.Receipt.RequestSha256))
            Assert.That(first.Receipt.OriginalResultJson, Is.EqualTo(replay.Receipt.OriginalResultJson))
            match AccountCapacityLedger.reserve f.AccountTwo op "Scheduler" launch project "Reviewer" 3L with
            | Error AccountOperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Changed role reusing op ID must fail: %A" other)
            match AccountCapacityLedger.reserve f.AccountTwo op "Scheduler" launch (Guid.NewGuid()) "Worker" 3L with
            | Error AccountOperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Changed project reusing op ID must fail: %A" other)
            match AccountCapacityLedger.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler" launch project "Worker" 3L with
            | Error AccountLaunchExists -> ()
            | other -> Assert.Fail(sprintf "Second op may not mint another token: %A" other)
            let _, receipt = AccountCapacityLedger.readOperation f.AccountTwo op |> success
            Assert.That(receipt.IsSome, Is.True)
            Assert.That(receipt.Value.OriginalResultJson, Is.EqualTo(first.Receipt.OriginalResultJson)))

    [<Test>]
    member _.CapacityShrinkRacesReservationInSingleGitCasDomain() =
        AccountFixture.withGit 2 (fun f ->
            AccountCapacityLedger.reserve f.AccountOne (Guid.NewGuid()) "Scheduler"
                (Guid.NewGuid()) (Guid.NewGuid()) "Lead" 1L |> success |> ignore
            use gate = new ManualResetEventSlim(false)
            let second = Task.Run(fun () ->
                gate.Wait()
                AccountCapacityLedger.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                    (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1L)
            let shrink = Task.Run(fun () ->
                gate.Wait()
                AccountCapacityLedger.setCapacity f.AccountOne (Guid.NewGuid()) "Operator" 1L 1)
            gate.Set()
            Task.WaitAll([| second :> Task; shrink :> Task |])
            let state = (AccountCapacityLedger.fetch f.AccountTwo |> success).State
            Assert.That(AccountCapacityLedger.occupied state, Is.LessThanOrEqualTo(state.MaxConcurrentAgents))
            Assert.That(Result.isOk second.Result, Is.Not.EqualTo(Result.isOk shrink.Result))
            match second.Result, shrink.Result with
            | Ok _, Error(AccountResizeBelowOccupancy 2) -> ()
            | Error(AccountCapacityFull(1, 1)), Ok _ -> ()
            | other -> Assert.Fail(sprintf "Unexpected reserve/resize result: %A" other)
            match AccountCapacityLedger.setCapacity f.AccountTwo (Guid.NewGuid()) "Operator"
                state.ConfigVersion 0 with
            | Error(InvalidAccountBinding _) -> ()
            | other -> Assert.Fail(sprintf "Zero capacity accepted: %A" other))

    [<Test>]
    member _.ReleaseRequiresCurrentVerifiedProjectTerminalAndNeverDoubleFrees() =
        AccountFixture.withGit 1 (fun f ->
            let launch, project, admission = Guid.NewGuid(), f.ProjectOne.ProjectId, Guid.NewGuid()
            AccountCapacityLedger.reserve f.AccountOne admission "Scheduler" launch project "Worker" 1L
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
            AccountCapacityLedger.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                successor (Guid.NewGuid()) "Reviewer" 1L |> success |> ignore
            match AccountCapacityLedger.release f.AccountOne verifier
                (Guid.NewGuid()) "Reconciler" terminal with
            | Error AccountLaunchAlreadyReleased -> ()
            | other -> Assert.Fail(sprintf "Second release must not free successor: %A" other)
            Assert.That((AccountCapacityLedger.fetch f.AccountOne |> success).State
                        |> AccountCapacityLedger.occupied, Is.EqualTo(1))
            match AccountCapacityLedger.reserve f.AccountTwo (Guid.NewGuid()) "Scheduler"
                launch project "Worker" 1L with
            | Error AccountLaunchExists -> ()
            | other -> Assert.Fail(sprintf "Released launch ID must never be reused: %A" other))

    [<Test>]
    member _.AbortOnlyRequiresProofAdmissionNeverWonAndRejectsLaterConflicts() =
        AccountFixture.withGit 1 (fun f ->
            let launch, admission = Guid.NewGuid(), Guid.NewGuid()
            AccountCapacityLedger.reserve f.AccountOne admission "Scheduler" launch
                f.ProjectOne.ProjectId "Worker" 1L |> success |> ignore
            let aborted = AccountFixture.publishTerminal f.ProjectOne launch admission "AbortOnlyUnbound"
            let verifier = AccountFixture.verifier f.ProjectTwo
            // Simulate the original admission unexpectedly winning after an invalid
            // abort-only fixture: the verifier must not certify a contradictory head.
            AccountFixture.publishProjectAdmission f.ProjectOne launch admission
            match AccountCapacityLedger.release f.AccountTwo verifier
                (Guid.NewGuid()) "Reconciler" aborted with
            | Error(AccountTerminalProofRejected _) -> ()
            | other -> Assert.Fail(sprintf "Conflicting admission/tombstone must hold token: %A" other)
            Assert.That((AccountCapacityLedger.fetch f.AccountOne |> success).State
                        |> AccountCapacityLedger.occupied, Is.EqualTo(1)))

    [<Test>]
    member _.CleanAbortOnlyTombstonePermitsReleaseWithoutProjectLaunch() =
        AccountFixture.withGit 1 (fun f ->
            let launch, admission = Guid.NewGuid(), Guid.NewGuid()
            AccountCapacityLedger.reserve f.AccountOne admission "Scheduler" launch
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
            AccountCapacityLedger.reserve f.AccountOne (Guid.NewGuid()) "Scheduler"
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
            AccountCapacityLedger.reserve f.AccountOne op "Scheduler" launch project "Worker" 1L
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
