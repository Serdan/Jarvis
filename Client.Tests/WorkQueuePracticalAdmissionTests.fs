module Client.Tests.WorkQueuePracticalAdmissionTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueReceiptProducer
open Client.WorkQueuePracticalAdmission
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter

let private ok = function
    | Ok value -> value
    | Error error -> failwithf "WQ04 real admission fixture: %A" error

let private git directory arguments =
    use runner = new Process()
    runner.StartInfo.FileName <- "git"
    runner.StartInfo.WorkingDirectory <- directory
    runner.StartInfo.UseShellExecute <- false
    runner.StartInfo.RedirectStandardOutput <- true
    runner.StartInfo.RedirectStandardError <- true
    for argument in arguments do runner.StartInfo.ArgumentList.Add(argument)
    Assert.That(runner.Start(), Is.True)
    let output = runner.StandardOutput.ReadToEnd()
    let error = runner.StandardError.ReadToEnd()
    Assert.That(runner.WaitForExit(30000), Is.True, sprintf "git %A timed out" arguments)
    Assert.That(runner.ExitCode, Is.EqualTo(0), error)
    output.Trim()

let private withGitPair action =
    let directory =
        Path.Combine(Path.GetTempPath(), "wq04-real-admission-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    try
        let remote = Path.Combine(directory, "remote.git")
        let first = Path.Combine(directory, "first")
        let second = Path.Combine(directory, "second")
        git directory ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
        git directory ["clone"; remote; first] |> ignore
        File.WriteAllText(Path.Combine(first, "fixture.txt"), "WQ04 disposable Git proof")
        git first ["add"; "fixture.txt"] |> ignore
        git first ["-c"; "user.name=WQ04 Fixture"; "-c"; "user.email=wq04@example.test"
                   "commit"; "-m"; "fixture"] |> ignore
        git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
        git directory ["clone"; remote; second] |> ignore
        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let binding checkout : LedgerBinding =
            { RepositoryPath = checkout; RemoteName = "origin"
              ApprovedFetchUrl = remote; ApprovedPushUrl = remote
              ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
        let a, b = binding first, binding second
        LedgerStore.initialize a true |> ok |> ignore
        LedgerStore.fetch b |> ok |> ignore
        WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
            (Map.ofList ["Lead", set ["planning"; "role:Lead"]]) |> ok |> ignore
        let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
        let account (project: LedgerBinding) : AccountBinding =
            { RepositoryPath = project.RepositoryPath; RemoteName = project.RemoteName
              ApprovedFetchUrl = project.ApprovedFetchUrl
              ApprovedPushUrl = project.ApprovedPushUrl
              AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
        let accountA, accountB = account a, account b
        AccountCapacityLedger.initialize accountA 1 |> ok |> ignore
        ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Lead" |> ok |> ignore
        WorkQueueRoleAuthority.initializeCatalog a "Lead" (Guid.NewGuid()) |> ok |> ignore
        let role: RoleConfiguration =
            { RoleId = "Lead"; Version = 1; Enabled = true
              Instructions = "Complete disposable WQ04 planning"
              QueueSubscriptions = set ["planning"] }
        WorkQueueRoleAuthority.configure a "Lead" (Guid.NewGuid()) 0 0 role |> ok |> ignore
        action a b accountA accountB
        Assert.That(git first ["status"; "--porcelain"], Is.Empty)
        Assert.That(git second ["status"; "--porcelain"], Is.Empty)
    finally
        if Directory.Exists(directory) then Directory.Delete(directory, true)

let private producedTask source producer id =
    let sourceOp = Guid.NewGuid()
    let producerOp = Guid.NewGuid()
    ReviewWorkflow.createTask source sourceOp id ("WQ04 source " + id) |> ok |> ignore
    WQ04ProducerFixture.produce producer "Lead" producerOp sourceOp |> ok |> ignore
    let snapshot, state = WorkQueueGitLedger.load producer |> ok
    let work =
        state.Works |> Map.toList |> List.map snd
        |> List.filter (fun candidate -> candidate.SubjectId = id)
        |> List.exactlyOne
    let origin, originOid =
        WorkQueueGitLedger.readOriginAt producer snapshot work.Id
        |> ok |> Option.get
    Assert.That(origin.SourceOperationId, Is.EqualTo(sourceOp.ToString("D")))
    { WorkId = work.Id; OriginBlobOid = originOid }: PracticalAdmissionDemand

let private request role: AccountAdmissionRequest =
    { Actor = "WQ04 Disposable Admission"
      LaunchId = Guid.NewGuid()
      AdmissionOperationId = Guid.NewGuid()
      AbortOperationId = Guid.NewGuid()
      ReleaseOperationId = Guid.NewGuid()
      RoleId = role
      RoleVersion = 1 }

let private currentProjectMutation binding operation mutation =
    let _, before = WorkQueueGitLedger.load binding |> ok
    WorkQueueGitLedger.executeInertFixture binding "WQ04 Disposable" operation
        before.Version mutation |> ok

[<TestFixture>]
type WorkQueuePracticalAdmissionTests() =
    [<Test>]
    member _.GenuineDemandAdmitsOnceWithPinnedRoleAndOriginalSourceThenReleasesCapOne() =
        withGitPair (fun a b accountA accountB ->
            let selected = producedTask a b "wq04-real-role-first"
            let first = request "Lead"
            let held () = AccountCapacityLedger.occupied (AccountCapacityLedger.fetch accountB |> ok).State

            // No live default-on registration exists: explicitly disabled is
            // always no-effect, before even reaching account-global capacity.
            match WorkQueuePracticalAdmission.admitExplicit false accountA a first selected with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Default-off admission mutated account: %A" other)
            Assert.That(held(), Is.Zero)

            let missingRole = { first with RoleId = "Missing" }
            match WorkQueuePracticalAdmission.admitExplicit true accountA a missingRole selected with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Unknown role could reserve credit: %A" other)
            let alteredSource = { selected with OriginBlobOid = String.replicate 40 "0" }
            match WorkQueuePracticalAdmission.admitExplicit true accountA a first alteredSource with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Foreign WQ04 origin could reserve credit: %A" other)
            let foreign = { a with ProjectId = Guid.NewGuid() }
            match WorkQueuePracticalAdmission.admitExplicit true accountA foreign first selected with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Foreign project binding could reserve credit: %A" other)
            Assert.That(held(), Is.Zero)

            match WorkQueuePracticalAdmission.admitExplicit true accountA a first selected with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Real role/source admission did not succeed: %A" other)
            let view = WorkQueueRoleAuthority.load b |> ok
            let launchId = first.LaunchId.ToString("D")
            let admitted = view.Project.Launches.[launchId]
            Assert.That(admitted.Credit, Is.EqualTo(Some selected.WorkId))
            Assert.That(admitted.AccountTokenId,
                        Is.EqualTo(first.AdmissionOperationId.ToString("D")))
            Assert.That(admitted.RoleId, Is.EqualTo(first.RoleId))
            Assert.That(admitted.RoleVersion, Is.EqualTo(first.RoleVersion))
            let accountToken = (AccountCapacityLedger.fetch accountB |> ok).State.Tokens |> Array.exactlyOne
            Assert.That(accountToken.ProjectId, Is.EqualTo(a.ProjectId.ToString("D")))
            Assert.That(accountToken.Status, Is.EqualTo("Held"))
            Assert.That(held(), Is.EqualTo(1))

            // The same original UUID must recover from the independently
            // fetched clone without consuming another global slot/launch.
            match WorkQueuePracticalAdmission.admitExplicit true accountB b first selected with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Original admission replay failed: %A" other)
            Assert.That((AccountCapacityLedger.fetch accountA |> ok).State.Tokens.Length,
                        Is.EqualTo(1))

            let nextDemand = producedTask b a "wq04-real-role-second"
            let second = request "Lead"
            match WorkQueuePracticalAdmission.admitExplicit true accountB b second selected with
            | GlobalReservationRejected(AccountCapacityFull(1, 1)) -> ()
            | other -> Assert.Fail(sprintf "Cap-one did not reject simultaneous launch: %A" other)
            Assert.That(held(), Is.EqualTo(1))

            // WQ02's accepted inert state machine is used ONLY to complete
            // this disposable launch; no production agent/browser is invoked.
            currentProjectMutation a (Guid.NewGuid())
                (DispatchOnce(launchId, DispatchState.Submitted)) |> ignore
            currentProjectMutation b (Guid.NewGuid())
                (InitializeAgent(launchId, "wq04-disposable-session")) |> ignore
            currentProjectMutation a (Guid.NewGuid()) (ClaimNext launchId) |> ignore
            // A replay of the *original* admission after a later claim is
            // still idempotent; it does not require now-claimed work to be
            // newly Available or reserve a second account slot.
            match WorkQueuePracticalAdmission.admitExplicit true accountB b first selected with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Claimed-work admission replay failed: %A" other)
            let terminalOperation = Guid.NewGuid()
            currentProjectMutation b terminalOperation
                (CompleteOrdinary(launchId, selected.WorkId)) |> ignore
            let current = WorkQueueRoleAuthority.load a |> ok
            Assert.That(current.Project.Works.[selected.WorkId].State,
                        Is.EqualTo(WorkState.Completed))
            let locator: ProjectTerminalLocator =
                { ProjectId = a.ProjectId; LaunchId = first.LaunchId
                  AdmissionOperationId = first.AdmissionOperationId
                  TerminalOperationId = terminalOperation }
            let released =
                AccountAdmissionAdapter.releaseTerminal accountB b "WQ04 Disposable"
                    first.ReleaseOperationId locator |> ok
            Assert.That(released.AlreadyApplied, Is.False)
            let replay =
                AccountAdmissionAdapter.releaseTerminal accountA a "WQ04 Disposable"
                    first.ReleaseOperationId locator |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(held(), Is.Zero)
            Assert.That((WorkQueueRoleAuthority.load b |> ok).Project.Launches.Count,
                        Is.EqualTo(1))

            // Original release restores the single cap, which can now be
            // consumed by another independently authenticated source demand.
            match WorkQueuePracticalAdmission.admitExplicit true accountA a second nextDemand with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Released cap could not be reused: %A" other)
            Assert.That(held(), Is.EqualTo(1)))

    [<Test>]
    member _.LostProjectAcknowledgmentPreservesOriginalTokenAndAllowsExactReplay() =
        withGitPair (fun a b accountA accountB ->
            let selected = producedTask a b "wq04-unknown-ack"
            let first = request "Lead"
            let firstOutcome =
                WorkQueueGitLedger.withTestPublishedAckLoss
                    (fun op -> op = first.AdmissionOperationId)
                    (fun () ->
                        WorkQueuePracticalAdmission.admitExplicit true accountA a first selected)
            match firstOutcome with
            | TokenRetained reason ->
                Assert.That(reason, Does.Contain("OutcomeUnknown"))
            | other -> Assert.Fail(sprintf "Lost project acknowledgement was not visible: %A" other)
            let original = WorkQueueRoleAuthority.load b |> ok
            Assert.That(original.Project.Launches.Count, Is.EqualTo(1))
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch accountB |> ok).State,
                        Is.EqualTo(1))
            match WorkQueuePracticalAdmission.admitExplicit true accountB b first selected with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Exact original acknowledgment recovery failed: %A" other)
            Assert.That((AccountCapacityLedger.fetch accountA |> ok).State.Tokens.Length,
                        Is.EqualTo(1))
            Assert.That((WorkQueueRoleAuthority.load a |> ok).Project.Launches.Count,
                        Is.EqualTo(1)))

    [<Test>]
    member _.AuditedC01RepairReleasesHeldTokenWithoutInventingRoleAdmission() =
        withGitPair (fun a b accountA accountB ->
            // Simulate a crash after global-first reservation but before the
            // project CAS. The stable original identity remains reconcilable.
            let first = request "Lead"
            AccountCapacityLedger.reserve accountA first.AdmissionOperationId
                first.Actor first.LaunchId
                (AccountAdmissionAdapter.projectAuthority a)
                first.RoleId (int64 first.RoleVersion) |> ok |> ignore
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch accountB |> ok).State,
                        Is.EqualTo(1))
            match AccountAdmissionAdapter.abortUnbound accountB b first with
            | UnboundAbortedAndReleased _ -> ()
            | other -> Assert.Fail(sprintf "Audited direct C01 repair failed: %A" other)
            let repaired = WorkQueueRoleAuthority.load a |> ok
            let aborted = repaired.Project.Launches.[first.LaunchId.ToString("D")]
            Assert.That(aborted.State, Is.EqualTo(LaunchState.TerminalPendingRelease(
                "abort:" + first.AbortOperationId.ToString("D"))))
            Assert.That(aborted.Credit, Is.EqualTo(None))
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch accountA |> ok).State,
                        Is.Zero)
            // A C01 no-session tombstone must not poison future pinned-role
            // demand, nor be mistaken for a successful role admission.
            let demand = producedTask b a "wq04-repair-next-demand"
            let fresh = request "Lead"
            match WorkQueuePracticalAdmission.admitExplicit true accountA a fresh demand with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "WQ04 future admission after C01 repair failed: %A" other)
            Assert.That((WorkQueueRoleAuthority.load b |> ok).Project.Launches.Count,
                        Is.EqualTo(2)))

    [<Test>]
    member _.CredentialIsAtomicWithPinnedAccountRoleAndSourceAndCannotDowngrade() =
        withGitPair (fun a b accountA accountB ->
            let selected = producedTask a b "wq04-credential-admission"
            let first = request "Lead"
            let launchId = first.LaunchId.ToString("D")
            let issued = IssuedWorkCredential.Issue launchId
            let wrong = IssuedWorkCredential.Issue launchId
            match WorkQueuePracticalAdmission.admitExplicitWithCredential true accountA a first selected issued with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Credentialed WQ04 admission failed: %A" other)
            let head, state = WorkQueueGitLedger.load b |> ok
            let record, _ = WorkQueueGitLedger.readCredentialAt b head launchId |> ok |> Option.get
            Assert.That(record.SecretSha256, Is.EqualTo(issued.VerifierSha256))
            Assert.That(record.AccountTokenId, Is.EqualTo(first.AdmissionOperationId.ToString("D")))
            Assert.That(state.Launches.[launchId].Credit, Is.EqualTo(Some selected.WorkId))
            let receipt = LedgerStore.getOperationAt b head first.AdmissionOperationId |> ok |> Option.get
            let expectedPaths : string array =
                [| WorkQueueGitLedger.RecordPath
                   WorkQueueGitLedger.admissionPath launchId
                   WorkQueueGitLedger.credentialPath launchId
                   WorkQueueRoleAuthority.roleAdmissionPath launchId |]
            Assert.That(receipt.Paths = expectedPaths, Is.True)
            let credentialBody, _ = LedgerStore.readAt b head (WorkQueueGitLedger.credentialPath launchId) |> ok
            Assert.That(credentialBody.Value, Does.Not.Contain(issued.RevealForPrivateDelivery()))
            match WorkQueuePracticalAdmission.admitExplicitWithCredential true accountB b first selected issued with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Exact credential admission replay failed: %A" other)
            match WorkQueuePracticalAdmission.admitExplicit true accountA a first selected with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Legacy admission should not replay credentialed launch: %A" other)
            match WorkQueuePracticalAdmission.admitExplicitWithCredential true accountA a first selected wrong with
            | TokenRetained _ -> ()
            | other -> Assert.Fail(sprintf "Reissued credential should not replay original launch: %A" other)
            currentProjectMutation a (Guid.NewGuid()) (DispatchOnce(launchId, DispatchState.Submitted))
            |> ignore
            let _, dispatched = WorkQueueGitLedger.load b |> ok
            match WorkQueueGitLedger.executeInertFixtureAt b "legacy" (Guid.NewGuid())
                    dispatched.Version 110L (InitializeAgent(launchId, "stolen-legacy-session")) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Legacy registration bypassed Work credential: %A" other)

            // A credentialed original admission must also remain verifiable
            // by the account-release witness after the launch is terminal.
            // This follows the credential fixture path, not InitializeAgent.
            let bearer = issued.RevealForPrivateDelivery()
            WorkQueueGitLedger.executeCredentialFixtureAt a "WQ04 Disposable" (Guid.NewGuid())
                dispatched.Version 111L launchId bearer CredentialWorkAction.FirstConnect
            |> ok |> ignore
            let _, registered = WorkQueueGitLedger.load b |> ok
            WorkQueueGitLedger.executeCredentialFixtureAt b "WQ04 Disposable" (Guid.NewGuid())
                registered.Version 112L launchId bearer CredentialWorkAction.ClaimPrimary
            |> ok |> ignore
            let _, claimed = WorkQueueGitLedger.load a |> ok
            // Original four-path admission remains replayable after its
            // credit has been claimed; replay must use the issuing CAS.
            match WorkQueuePracticalAdmission.admitExplicitWithCredential true accountB b first selected issued with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Claimed credential admission replay failed: %A" other)
            let terminalOperation = Guid.NewGuid()
            WorkQueueGitLedger.executeCredentialFixtureAt a "WQ04 Disposable" terminalOperation
                claimed.Version 113L launchId bearer (CredentialWorkAction.FinishOrdinary selected.WorkId)
            |> ok |> ignore
            let locator: ProjectTerminalLocator =
                { ProjectId = a.ProjectId; LaunchId = first.LaunchId
                  AdmissionOperationId = first.AdmissionOperationId
                  TerminalOperationId = terminalOperation }
            let release = AccountAdmissionAdapter.releaseTerminal accountB b "WQ04 Disposable"
                              first.ReleaseOperationId locator |> ok
            Assert.That(release.AlreadyApplied, Is.False)
            Assert.That(AccountCapacityLedger.occupied (AccountCapacityLedger.fetch accountA |> ok).State,
                        Is.Zero))
