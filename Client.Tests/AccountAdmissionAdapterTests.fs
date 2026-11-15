module Client.Tests.AccountAdmissionAdapterTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter

type private AdmissionFixture =
    { AccountA: AccountBinding
      AccountB: AccountBinding
      ProjectA: LedgerBinding
      ProjectASecond: LedgerBinding
      ProjectB: LedgerBinding
      ProjectBSecond: LedgerBinding
      SameGuidForeign: LedgerBinding
      SameGuidForeignSecond: LedgerBinding }

module private AdmissionFixtures =
    let git dir arguments =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- dir
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        arguments |> List.iter runner.StartInfo.ArgumentList.Add
        Assert.That(runner.Start(), Is.True)
        let output = runner.StandardOutput.ReadToEnd()
        let errors = runner.StandardError.ReadToEnd()
        runner.WaitForExit()
        Assert.That(runner.ExitCode, Is.EqualTo(0), errors)
        output.Trim()

    let success result =
        match result with
        | Ok value -> value
        | Error e -> failwithf "Expected verified Git CAS success, got %A" e

    let repository root name =
        let remote = Path.Combine(root, name + ".git")
        let one = Path.Combine(root, name + "-one")
        let two = Path.Combine(root, name + "-two")
        git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
        git root [ "clone"; remote; one ] |> ignore
        git one [ "config"; "user.name"; "Fixture" ] |> ignore
        git one [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        File.WriteAllText(Path.Combine(one, "source.txt"), "no live source changes")
        git one [ "add"; "source.txt" ] |> ignore
        git one [ "commit"; "-m"; "source fixture" ] |> ignore
        git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; remote; two ] |> ignore
        remote, one, two

    let withFixture cap action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-wq07-adapter-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let ar, a1, a2 = repository root "account"
            let pr1, p1, p2 = repository root "project-one"
            let pr2, q1, q2 = repository root "project-two"
            let foreignRemote, foreign1, foreign2 = repository root "same-guid-foreign-ledger"
            let accountId, accountLedger = Guid.NewGuid(), Guid.NewGuid()
            let projectId1, projectLedger1 = Guid.NewGuid(), Guid.NewGuid()
            let projectId2, projectLedger2 = Guid.NewGuid(), Guid.NewGuid()
            let foreignLedger = Guid.NewGuid()
            let account dir =
                { RepositoryPath = dir; RemoteName = "origin"
                  ApprovedFetchUrl = ar; ApprovedPushUrl = ar
                  AccountId = accountId; LedgerId = accountLedger; Epoch = 1 }
            let project dir remote projectId ledgerId =
                { RepositoryPath = dir; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let fixture =
                { AccountA = account a1; AccountB = account a2
                  ProjectA = project p1 pr1 projectId1 projectLedger1
                  ProjectASecond = project p2 pr1 projectId1 projectLedger1
                  ProjectB = project q1 pr2 projectId2 projectLedger2
                  ProjectBSecond = project q2 pr2 projectId2 projectLedger2
                  SameGuidForeign = project foreign1 foreignRemote projectId1 foreignLedger
                  SameGuidForeignSecond = project foreign2 foreignRemote projectId1 foreignLedger }
            AccountCapacityLedger.initialize fixture.AccountA cap |> success |> ignore
            for p in [ fixture.ProjectA; fixture.ProjectB; fixture.SameGuidForeign ] do
                LedgerStore.initialize p true |> success |> ignore
                WorkQueueGitLedger.initialize p "fixture" (Guid.NewGuid())
                    (Map.ofList [ "Lead", set [ "planning" ] ]) |> success |> ignore
            action fixture
            for dir in [ a1; a2; p1; p2; q1; q2; foreign1; foreign2 ] do
                Assert.That(git dir [ "status"; "--porcelain" ], Is.Empty)
        finally
            try Directory.Delete(root, true) with _ -> ()

    let stage (p: LedgerBinding) id =
        { Id = id; ProjectId = p.ProjectId.ToString("D")
          SubjectId = "task-" + id; Kind = WorkKind.Planning; QueueId = "planning"
          StageGeneration = 1; State = WorkState.Available; Domain = DomainState.Undecided
          Gate = GateState.Clear; ClaimGeneration = 0; Dependencies = Set.empty
          WaivedDependencies = Map.empty; AvailableAt = 0L; AvailableSequence = 0L
          SourceLineage = None; Push = None; IntegrationAuthorized = false }

    let mutate (p: LedgerBinding) op mutation =
        let _, state = WorkQueueGitLedger.load p |> success
        WorkQueueGitLedger.execute p "fixture" op state.Version mutation |> success |> ignore

    let available p id = mutate p (Guid.NewGuid()) (PublishStage(stage p id))

    let request () =
        { Actor = "fixture"
          LaunchId = Guid.NewGuid()
          AdmissionOperationId = Guid.NewGuid()
          AbortOperationId = Guid.NewGuid()
          ReleaseOperationId = Guid.NewGuid()
          RoleId = "Lead"; RoleVersion = 1 }

    let occupied (f: AdmissionFixture) =
        let state = (AccountCapacityLedger.fetch f.AccountA |> success).State
        state.Tokens |> Array.filter (fun token -> token.Status = "Held") |> Array.length

[<TestFixture>]
type AccountAdmissionAdapterTests() =
    let success = AdmissionFixtures.success
    let withFixture = AdmissionFixtures.withFixture
    let available = AdmissionFixtures.available
    let mutate = AdmissionFixtures.mutate
    let request = AdmissionFixtures.request

    [<Test>]
    member _.GlobalThenProjectThenClaimedTerminalRequiresImmutableProof() =
        withFixture 1 (fun f ->
            available f.ProjectA "work-a"
            let r = request()
            match AccountAdmissionAdapter.admit f.AccountA f.ProjectA r with
            | ProjectAdmitted _ -> ()
            | other -> failwithf "Expected project admission: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            // Delayed same-UUID replay from another Git client must verify
            // the original admission and not reserve or write a second launch.
            match AccountAdmissionAdapter.admit f.AccountB f.ProjectASecond r with
            | ProjectAdmitted _ -> ()
            | other -> failwithf "Exact project admission replay not verified: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            let launchId = r.LaunchId.ToString("D")
            let fake =
                { ProjectId = f.ProjectA.ProjectId; LaunchId = r.LaunchId
                  AdmissionOperationId = r.AdmissionOperationId
                  TerminalOperationId = Guid.NewGuid() }
            match AccountAdmissionAdapter.releaseTerminal f.AccountA f.ProjectA
                    "fixture" (Guid.NewGuid()) fake with
            | Error(AccountTerminalProofRejected _) -> ()
            | other -> failwithf "Forged/missing terminal was accepted: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            mutate f.ProjectA (Guid.NewGuid()) (DispatchOnce(launchId, DispatchState.Submitted))
            mutate f.ProjectA (Guid.NewGuid()) (InitializeAgent(launchId, "session-unique"))
            mutate f.ProjectA (Guid.NewGuid()) (ClaimNext launchId)
            let terminalId = Guid.NewGuid()
            mutate f.ProjectA terminalId (CompleteOrdinary(launchId, "work-a"))
            let locator = { fake with TerminalOperationId = terminalId }
            let released =
                AccountAdmissionAdapter.releaseTerminal f.AccountA f.ProjectA
                    "fixture" r.ReleaseOperationId locator |> success
            Assert.That(released.AlreadyApplied, Is.False)
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(0))
            let replay =
                AccountAdmissionAdapter.releaseTerminal f.AccountB f.ProjectASecond
                    "fixture" r.ReleaseOperationId locator |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(0))
            match AccountAdmissionAdapter.admit f.AccountB f.ProjectASecond r with
            | TokenRetained _ -> ()
            | other -> failwithf "Released global token authorized project admission replay: %A" other
            match AccountAdmissionAdapter.releaseTerminal f.AccountA f.ProjectA
                    "fixture" (Guid.NewGuid()) locator with
            | Error AccountLaunchAlreadyReleased -> ()
            | other -> failwithf "Distinct release double-freed the token: %A" other)

    [<Test>]
    member _.DefinitiveNoDemandAbortsByC01AndReturnsCapacityOnlyAfterProof() =
        withFixture 1 (fun f ->
            let r = request()
            match AccountAdmissionAdapter.admit f.AccountA f.ProjectA r with
            | UnboundAbortedAndReleased _ -> ()
            | other -> failwithf "Expected fenced C01 abort and release: %A" other
            let _, state = WorkQueueGitLedger.load f.ProjectA |> success
            let launch = state.Launches.[r.LaunchId.ToString("D")]
            Assert.That(launch.State,
                Is.EqualTo(LaunchState.TerminalPendingRelease("abort:" + r.AbortOperationId.ToString("D"))))
            Assert.That(state.Receipts.ContainsKey(r.AdmissionOperationId.ToString("D")), Is.False)
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(0))
            let token = (AccountCapacityLedger.fetch f.AccountA |> success).State.Tokens[0]
            Assert.That(token.ReleaseProof.TerminalKind, Is.EqualTo("AbortOnlyUnbound"))
            Assert.That(token.ReleaseProof.ProjectLedgerId, Is.EqualTo(f.ProjectA.LedgerId.ToString("D")))
            // Exact C01/release replay is idempotent and requires current proof.
            match AccountAdmissionAdapter.abortUnbound f.AccountB f.ProjectASecond r with
            | UnboundAbortedAndReleased _ -> ()
            | other -> failwithf "Exact C01 replay was not idempotent: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(0))
            match AccountAdmissionAdapter.admit f.AccountB f.ProjectASecond r with
            | TokenRetained _ -> ()
            | other -> failwithf "Released C01 token re-admitted project: %A" other
            // The tombstone fences late original admission forever.
            available f.ProjectA "late-work"
            let _, after = WorkQueueGitLedger.load f.ProjectA |> success
            let originalLaunch =
                { Id = r.LaunchId.ToString("D"); ProjectId = f.ProjectA.ProjectId.ToString("D")
                  RoleId = r.RoleId; RoleVersion = r.RoleVersion
                  AccountTokenId = r.AdmissionOperationId.ToString("D")
                  State = LaunchState.ReservedUnregistered; Lease = None
                  Version = 0; Credit = None; PrimaryWorkId = None
                  EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }
            match WorkQueueGitLedger.execute f.ProjectA "fixture" r.AdmissionOperationId
                    after.Version (AdmitProject originalLaunch) with
            | Error _ -> ()
            | Ok _ -> failwith "Late project admission bypassed C01 tombstone")

    [<Test>]
    member _.ProjectRemoteUnverifiableKeepsGlobalSlotUntilFreshC01Proof() =
        withFixture 1 (fun f ->
            let r = request()
            let remote = f.ProjectA.ApprovedFetchUrl
            let offline = remote + ".temporarily-offline"
            Directory.Move(remote, offline)
            try
                match AccountAdmissionAdapter.admit f.AccountA f.ProjectA r with
                | TokenRetained _ -> ()
                | other -> failwithf "Expected held token on unverified project remote: %A" other
                Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            finally
                Directory.Move(offline, remote)
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            Assert.That((WorkQueueGitLedger.load f.ProjectA |> success |> snd).Launches.IsEmpty, Is.True)
            match AccountAdmissionAdapter.abortUnbound f.AccountB f.ProjectASecond r with
            | UnboundAbortedAndReleased _ -> ()
            | other -> failwithf "C01 recovery did not release only after proof: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(0)))

    [<Test>]
    member _.DistinctProjectsRaceForOneAccountSlotAndCannotOvercommit() =
        withFixture 1 (fun f ->
            available f.ProjectA "work-one"
            available f.ProjectB "work-two"
            let a, b = request(), request()
            let one = Task.Run(fun () -> AccountAdmissionAdapter.admit f.AccountA f.ProjectA a)
            let two = Task.Run(fun () -> AccountAdmissionAdapter.admit f.AccountB f.ProjectBSecond b)
            Task.WaitAll([| one :> Task; two :> Task |])
            let admitted =
                [ one.Result; two.Result ]
                |> List.filter (function ProjectAdmitted _ -> true | _ -> false)
            Assert.That(admitted.Length, Is.EqualTo(1), sprintf "A=%A B=%A" one.Result two.Result)
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            let state = (AccountCapacityLedger.fetch f.AccountA |> success).State
            Assert.That(state.Tokens.Length, Is.EqualTo(1))
            Assert.That(state.MaxConcurrentAgents, Is.EqualTo(1)))

    [<Test>]
    member _.OriginalAdmissionWinsProjectRaceOrC01WinsButNeverBoth() =
        withFixture 1 (fun f ->
            available f.ProjectA "work-race"
            let r = request()
            AccountCapacityLedger.reserve f.AccountA r.AdmissionOperationId "fixture"
                r.LaunchId (AccountAdmissionAdapter.projectAuthority f.ProjectA) "Lead" 1L |> success |> ignore
            let launch =
                { Id = r.LaunchId.ToString("D"); ProjectId = f.ProjectA.ProjectId.ToString("D")
                  RoleId = "Lead"; RoleVersion = 1
                  AccountTokenId = r.AdmissionOperationId.ToString("D")
                  State = LaunchState.ReservedUnregistered
                  Lease = None; Version = 0; Credit = None; PrimaryWorkId = None
                  EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }
            let _, before = WorkQueueGitLedger.load f.ProjectA |> success
            let first = Task.Run(fun () ->
                WorkQueueGitLedger.execute f.ProjectA "fixture" r.AdmissionOperationId
                    before.Version (AdmitProject launch))
            let second = Task.Run(fun () ->
                WorkQueueGitLedger.execute f.ProjectASecond "fixture" r.AbortOperationId
                    before.Version (AbortUnboundProjectAdmission(
                        launch, r.AdmissionOperationId.ToString("D"))))
            Task.WaitAll([| first :> Task; second :> Task |])
            let _, settled = WorkQueueGitLedger.load f.ProjectA |> success
            let final = settled.Launches.[launch.Id]
            let decision =
                WorkQueueGitLedger.readAdmissionAt f.ProjectA
                    (WorkQueueGitLedger.load f.ProjectA |> success |> fst) launch.Id |> success
            match final.State, decision with
            | LaunchState.ReservedUnregistered, Some(doc, _) ->
                Assert.That(doc.Kind, Is.EqualTo("AdmissionWon"))
                Assert.That(settled.Receipts.ContainsKey(r.AdmissionOperationId.ToString("D")), Is.True)
                Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            | LaunchState.TerminalPendingRelease marker, Some(doc, _)
                when marker = "abort:" + r.AbortOperationId.ToString("D") ->
                Assert.That(doc.Kind, Is.EqualTo("AbortOnlyUnbound"))
                Assert.That(settled.Receipts.ContainsKey(r.AdmissionOperationId.ToString("D")), Is.False)
                match AccountAdmissionAdapter.abortUnbound f.AccountB f.ProjectASecond r with
                | UnboundAbortedAndReleased _ -> ()
                | other -> failwithf "Abort winner had no trusted proof: %A" other
                Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(0))
            | _ -> failwithf "Inconsistent winning launch CAS: %A %A" final decision)

    [<Test>]
    member _.UnprovenZeroClaimAndUnregisteredTerminalCannotReleaseGlobalToken() =
        withFixture 1 (fun f ->
            available f.ProjectA "unclaimed-work"
            let r = request()
            match AccountAdmissionAdapter.admit f.AccountA f.ProjectA r with
            | ProjectAdmitted _ -> ()
            | other -> failwithf "Expected held unregistered admission: %A" other
            let _, before = WorkQueueGitLedger.load f.ProjectA |> success
            let launchId = r.LaunchId.ToString("D")
            let fakeTerminal = Guid.NewGuid()
            match WorkQueueGitLedger.execute f.ProjectA "fixture" fakeTerminal
                    before.Version (TerminalizeNoClaim launchId) with
            | Error _ -> ()
            | Ok _ -> failwith "Unregistered zero-claim launch terminalized without evidence."
            let locator =
                { ProjectId = f.ProjectA.ProjectId; LaunchId = r.LaunchId
                  AdmissionOperationId = r.AdmissionOperationId
                  TerminalOperationId = fakeTerminal }
            match AccountAdmissionAdapter.releaseTerminal f.AccountA f.ProjectA
                    "fixture" r.ReleaseOperationId locator with
            | Error(AccountTerminalProofRejected _) -> ()
            | other -> failwithf "Unproven zero-claim released account token: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            let _, after = WorkQueueGitLedger.load f.ProjectA |> success
            Assert.That(after.Launches.[launchId].State, Is.EqualTo(LaunchState.ReservedUnregistered))
            Assert.That(after.Version, Is.EqualTo(before.Version)))

    [<Test>]
    member _.VersionedRoleMismatchCannotReleaseEvenWithAuthenticProjectTerminal() =
        withFixture 1 (fun f ->
            available f.ProjectA "role-work"
            let r = request()
            // Direct WQ-02 admission is intentionally inert. Even if a
            // project has an authentic terminal receipt, a token reserved
            // for a different role/configuration cannot be released by it.
            AccountCapacityLedger.reserve f.AccountA r.AdmissionOperationId "fixture"
                r.LaunchId (AccountAdmissionAdapter.projectAuthority f.ProjectA) "OtherRole" 2L |> success |> ignore
            let launchId = r.LaunchId.ToString("D")
            let initial =
                { Id = launchId; ProjectId = f.ProjectA.ProjectId.ToString("D")
                  RoleId = "Lead"; RoleVersion = 1
                  AccountTokenId = r.AdmissionOperationId.ToString("D")
                  State = LaunchState.ReservedUnregistered
                  Lease = None; Version = 0; Credit = None; PrimaryWorkId = None
                  EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }
            mutate f.ProjectA r.AdmissionOperationId (AdmitProject initial)
            mutate f.ProjectA (Guid.NewGuid()) (DispatchOnce(launchId, DispatchState.Submitted))
            mutate f.ProjectA (Guid.NewGuid()) (InitializeAgent(launchId, "role-session"))
            mutate f.ProjectA (Guid.NewGuid()) (ClaimNext launchId)
            let terminalId = Guid.NewGuid()
            mutate f.ProjectA terminalId (CompleteOrdinary(launchId, "role-work"))
            let locator =
                { ProjectId = f.ProjectA.ProjectId; LaunchId = r.LaunchId
                  AdmissionOperationId = r.AdmissionOperationId
                  TerminalOperationId = terminalId }
            match AccountAdmissionAdapter.releaseTerminal f.AccountA f.ProjectA
                    "fixture" r.ReleaseOperationId locator with
            | Error(AccountTerminalProofRejected reason)
                when reason.Contains("Versioned project role", StringComparison.Ordinal) -> ()
            | other -> failwithf "Mismatched versioned role released global token: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1)))

    [<Test>]
    member _.ImmutableDecisionRewriteCannotAuthorizeAccountRelease() =
        withFixture 1 (fun f ->
            let r = request()
            match AccountAdmissionAdapter.admit f.AccountA f.ProjectA r with
            | UnboundAbortedAndReleased _ -> ()
            | other -> failwithf "Expected abort fixture: %A" other
            // Rewrite only the decision blob at a later trusted head: even a
            // syntactically valid unchanged terminal state must fail proof.
            let snap, _ = WorkQueueGitLedger.load f.ProjectA |> success
            let path = WorkQueueGitLedger.admissionPath (r.LaunchId.ToString("D"))
            let raw, oid = LedgerStore.readAt f.ProjectA snap path |> success
            LedgerStore.putRecord f.ProjectA
                { OperationId = Guid.NewGuid(); Actor = "fixture"
                  Path = path; ExpectedBlobOid = oid
                  Content = raw.Value + " " } |> success |> ignore
            let locator =
                { ProjectId = f.ProjectA.ProjectId; LaunchId = r.LaunchId
                  AdmissionOperationId = r.AdmissionOperationId
                  TerminalOperationId = r.AbortOperationId }
            let verifier =
                new AccountAdmissionAdapter.VerifiedProjectTerminalReceipts(f.ProjectA)
                :> IProjectTerminalReceiptVerifier
            match verifier.VerifyCurrentTerminal locator with
            | Error _ -> ()
            | Ok proof -> failwithf "Rewritten decision accepted: %A" proof)


    [<Test>]
    member _.SameGuidForeignTerminalCannotReleaseOriginalLedgerToken() =
        withFixture 1 (fun f ->
            available f.ProjectA "authoritative-work"
            let r = request()
            match AccountAdmissionAdapter.admit f.AccountA f.ProjectA r with
            | ProjectAdmitted _ -> ()
            | other -> failwithf "Original project not admitted: %A" other

            // A second REAL Git remote is internally self-consistent and carries
            // the same project GUID, but not the ledger identity/trust anchor.
            let foreign = f.SameGuidForeign
            Assert.That(foreign.ProjectId, Is.EqualTo(f.ProjectA.ProjectId))
            Assert.That(foreign.LedgerId, Is.Not.EqualTo(f.ProjectA.LedgerId))
            Assert.That(foreign.ApprovedFetchUrl, Is.Not.EqualTo(f.ProjectA.ApprovedFetchUrl))
            let fakeLaunch =
                { Id = r.LaunchId.ToString("D"); ProjectId = foreign.ProjectId.ToString("D")
                  RoleId = r.RoleId; RoleVersion = r.RoleVersion
                  AccountTokenId = r.AdmissionOperationId.ToString("D")
                  State = LaunchState.ReservedUnregistered; Lease = None
                  Version = 0; Credit = None; PrimaryWorkId = None
                  EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }
            let _, fakeBefore = WorkQueueGitLedger.load foreign |> success
            WorkQueueGitLedger.execute foreign "fixture" r.AbortOperationId fakeBefore.Version
                (AbortUnboundProjectAdmission(fakeLaunch, r.AdmissionOperationId.ToString("D")))
            |> success |> ignore
            let locator =
                { ProjectId = foreign.ProjectId; LaunchId = r.LaunchId
                  AdmissionOperationId = r.AdmissionOperationId
                  TerminalOperationId = r.AbortOperationId }
            let foreignVerifier =
                new AccountAdmissionAdapter.VerifiedProjectTerminalReceipts(f.SameGuidForeignSecond)
                :> IProjectTerminalReceiptVerifier
            match foreignVerifier.VerifyCurrentTerminal locator with
            | Ok proof ->
                Assert.That(proof.ProjectLedgerId, Is.EqualTo(string foreign.LedgerId))
                Assert.That(proof.TerminalKind, Is.EqualTo("AbortOnlyUnbound"))
            | Error reason -> failwithf "Foreign self-consistent C01 did not verify: %s" reason

            match AccountAdmissionAdapter.releaseTerminal f.AccountB f.SameGuidForeignSecond
                    "fixture" r.ReleaseOperationId locator with
            | Error(AccountTerminalProofRejected _) -> ()
            | other -> failwithf "Foreign real C01 released original ledger: %A" other
            match AccountAdmissionAdapter.abortUnbound f.AccountB f.SameGuidForeignSecond r with
            | TokenRetained _ -> ()
            | other -> failwithf "Foreign abort bypassed original authority: %A" other
            match AccountAdmissionAdapter.admit f.AccountB f.SameGuidForeignSecond r with
            | GlobalReservationRejected AccountOperationIdReused -> ()
            | other -> failwithf "Same-UUID foreign ledger replay was accepted: %A" other
            // Even spoofing original ledger ID cannot change the immutable
            // approved endpoint in an original account operation.
            let swappedEndpoint =
                { f.ProjectASecond with ApprovedPushUrl = foreign.ApprovedPushUrl }
            match AccountAdmissionAdapter.admit f.AccountB swappedEndpoint r with
            | GlobalReservationRejected AccountOperationIdReused -> ()
            | other -> failwithf "Same-UUID changed remote identity replay accepted: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            match AccountAdmissionAdapter.admit f.AccountB f.ProjectBSecond (request()) with
            | GlobalReservationRejected(AccountCapacityFull(1, 1)) -> ()
            | other -> failwithf "Foreign C01 enabled global cap overcommit: %A" other
            let _, real = WorkQueueGitLedger.load f.ProjectA |> success
            Assert.That(real.Launches.[r.LaunchId.ToString("D")].State,
                        Is.EqualTo(LaunchState.ReservedUnregistered))
            let token = (AccountCapacityLedger.fetch f.AccountB |> success).State.Tokens[0]
            Assert.That(token.Status, Is.EqualTo("Held"))
            Assert.That(token.ProjectLedgerId, Is.EqualTo(string f.ProjectA.LedgerId))
            Assert.That(token.ProjectAuthoritySha256,
                        Is.EqualTo(AccountCapacityLedger.projectAuthoritySha256
                            (AccountAdmissionAdapter.projectAuthority f.ProjectA)))
            Assert.That(token.ProjectAuthoritySha256, Is.Not.EqualTo(f.ProjectA.ApprovedFetchUrl)))

    [<Test>]
    member _.PublishedProjectAdmissionLostAckKeepsOneTokenAndReplaysOriginalWinner() =
        withFixture 1 (fun f ->
            available f.ProjectA "lost-admission-reply"
            let r = request()
            let mutable dropped = false
            let first =
                WorkQueueGitLedger.withTestPublishedAckLoss
                    (fun op ->
                        if op = r.AdmissionOperationId && not dropped then
                            dropped <- true
                            true
                        else false)
                    (fun () -> AccountAdmissionAdapter.admit f.AccountA f.ProjectA r)
            Assert.That(dropped, Is.True)
            match first with
            | TokenRetained _ -> ()
            | other -> failwithf "Lost committed admission reply prematurely resolved: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            let snapshot, project = WorkQueueGitLedger.load f.ProjectASecond |> success
            Assert.That(project.Launches.Count, Is.EqualTo(1))
            Assert.That(project.Receipts.ContainsKey(r.AdmissionOperationId.ToString("D")), Is.True)
            let witness =
                WorkQueueGitLedger.readAdmissionAt f.ProjectASecond snapshot (r.LaunchId.ToString("D"))
                |> success
            Assert.That(witness.IsSome, Is.True)
            Assert.That((fst witness.Value).Kind, Is.EqualTo("AdmissionWon"))
            match AccountAdmissionAdapter.admit f.AccountB f.ProjectASecond r with
            | ProjectAdmitted _ -> ()
            | other -> failwithf "Original project admission UUID did not reconcile: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            Assert.That((AccountCapacityLedger.fetch f.AccountB |> success).State.Tokens.Length, Is.EqualTo(1))
            match AccountAdmissionAdapter.admit f.AccountB f.ProjectBSecond (request()) with
            | GlobalReservationRejected(AccountCapacityFull(1, 1)) -> ()
            | other -> failwithf "Lost-ack admission overcommitted account: %A" other)

    [<Test>]
    member _.PublishedC01LostAckRetainsTokenUntilOriginalAbortProofThenReleasesOnce() =
        withFixture 1 (fun f ->
            let r = request()
            let mutable dropped = false
            let first =
                WorkQueueGitLedger.withTestPublishedAckLoss
                    (fun op ->
                        if op = r.AbortOperationId && not dropped then
                            dropped <- true
                            true
                        else false)
                    (fun () -> AccountAdmissionAdapter.admit f.AccountA f.ProjectA r)
            Assert.That(dropped, Is.True)
            match first with
            | TokenRetained _ -> ()
            | other -> failwithf "Unknown C01 reply released account: %A" other
            Assert.That(AdmissionFixtures.occupied f, Is.EqualTo(1))
            let snap, state = WorkQueueGitLedger.load f.ProjectASecond |> success
            let decision = WorkQueueGitLedger.readAdmissionAt f.ProjectASecond snap (r.LaunchId.ToString("D"))
                           |> success
            Assert.That(decision.IsSome, Is.True)
            Assert.That((fst decision.Value).Kind, Is.EqualTo("AbortOnlyUnbound"))
            Assert.That(state.Receipts.ContainsKey(r.AdmissionOperationId.ToString("D")), Is.False)
            match AccountAdmissionAdapter.abortUnbound f.AccountB f.ProjectASecond r with
            | UnboundAbortedAndReleased _ -> ()
            | other -> failwithf "Exact original C01 winner could not be reconciled: %A" other
            let accountSnap, releaseReceipt =
                AccountCapacityLedger.readOperation f.AccountA r.ReleaseOperationId |> success
            Assert.That(releaseReceipt.IsSome, Is.True)
            Assert.That(AccountCapacityLedger.occupied accountSnap.State, Is.Zero)
            match AccountAdmissionAdapter.abortUnbound f.AccountA f.ProjectA r with
            | UnboundAbortedAndReleased _ -> ()
            | other -> failwithf "Exact C01/release replay not idempotent: %A" other
            let replaySnap, replayReceipt =
                AccountCapacityLedger.readOperation f.AccountB r.ReleaseOperationId |> success
            Assert.That(replayReceipt, Is.EqualTo(releaseReceipt))
            Assert.That(replaySnap.State.Tokens.Length, Is.EqualTo(1))
            Assert.That(AccountCapacityLedger.occupied replaySnap.State, Is.Zero))

    [<Test>]
    member _.TerminalAccountReleaseLostAckReplaysImmutableReceiptWithoutDoubleFree() =
        withFixture 1 (fun f ->
            available f.ProjectA "terminal-lost-reply"
            let r = request()
            match AccountAdmissionAdapter.admit f.AccountA f.ProjectA r with
            | ProjectAdmitted _ -> ()
            | other -> failwithf "Expected original admission: %A" other
            let launch = r.LaunchId.ToString("D")
            mutate f.ProjectA (Guid.NewGuid()) (DispatchOnce(launch, DispatchState.Submitted))
            mutate f.ProjectA (Guid.NewGuid()) (InitializeAgent(launch, "terminal-recovery"))
            mutate f.ProjectA (Guid.NewGuid()) (ClaimNext launch)
            let terminal = Guid.NewGuid()
            mutate f.ProjectA terminal (CompleteOrdinary(launch, "terminal-lost-reply"))
            let locator =
                { ProjectId = f.ProjectA.ProjectId; LaunchId = r.LaunchId
                  AdmissionOperationId = r.AdmissionOperationId
                  TerminalOperationId = terminal }
            let mutable pushedWithoutAck = false
            let mutable postPushFetchFailed = false
            let hooks =
                { BeforeLeasePush = fun _ _ -> ()
                  GitCommand = fun args ->
                    match args with
                    | "push" :: _ when not pushedWithoutAck ->
                        pushedWithoutAck <- true
                        AccountGitTestFault.ExecuteThenLoseAcknowledgment
                    | "fetch" :: _ when pushedWithoutAck && not postPushFetchFailed ->
                        postPushFetchFailed <- true
                        AccountGitTestFault.FailBeforeExecution
                    | _ -> AccountGitTestFault.PassThrough }
            let unknown =
                AccountCapacityLedger.withTestInterposition hooks (fun () ->
                    AccountAdmissionAdapter.releaseTerminal f.AccountA f.ProjectA
                        "fixture" r.ReleaseOperationId locator)
            Assert.That(pushedWithoutAck, Is.True)
            Assert.That(postPushFetchFailed, Is.True)
            match unknown with
            | Error(AccountOutcomeUnknown op) when op = r.ReleaseOperationId -> ()
            | other -> failwithf "Lost release ack did not preserve exact uncertainty: %A" other
            let snapshot, original = AccountCapacityLedger.readOperation f.AccountB r.ReleaseOperationId
                                     |> success
            Assert.That(original.IsSome, Is.True)
            Assert.That(AccountCapacityLedger.occupied snapshot.State, Is.Zero)
            let replay =
                AccountAdmissionAdapter.releaseTerminal f.AccountB f.ProjectASecond
                    "fixture" r.ReleaseOperationId locator |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Receipt, Is.EqualTo(original.Value))
            Assert.That((AccountCapacityLedger.fetch f.AccountA |> success).State.Tokens.Length, Is.EqualTo(1))
            match AccountAdmissionAdapter.releaseTerminal f.AccountA f.ProjectA
                    "fixture" (Guid.NewGuid()) locator with
            | Error AccountLaunchAlreadyReleased -> ()
            | other -> failwithf "Distinct release double-freed capacity: %A" other)
