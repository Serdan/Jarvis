module Client.Tests.WorkQueueCredentialTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger

// Two independent clients and an actual bare Git ledger. No browser, hosted CI,
// persistent relay identity database, production Scheduler or network API.
[<TestFixture>]
type WorkQueueCredentialTests() =
    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Work credential fixture failed: %A" error

    let reject = function
        | Error (InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected credential denial, received: %A" other)

    let git directory args =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- directory
        p.StartInfo.UseShellExecute <- false
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        for arg in args do p.StartInfo.ArgumentList.Add arg
        Assert.That(p.Start(), Is.True)
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        Assert.That(p.WaitForExit(30000), Is.True)
        Assert.That(p.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let withClones run =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-work-credentials-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let aPath, bPath = Path.Combine(root, "a"), Path.Combine(root, "b")
            git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
            git root ["clone"; bare; aPath] |> ignore
            File.WriteAllText(Path.Combine(aPath, "source.txt"), "credentials fixture")
            git aPath ["add"; "source.txt"] |> ignore
            git aPath ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
                       "commit"; "-m"; "fixture"] |> ignore
            git aPath ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; bare; bPath] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding aPath, binding bPath
            LedgerStore.initialize a true |> ok |> ignore
            LedgerStore.fetch b |> ok |> ignore
            WorkQueueGitLedger.initialize a "fixture" (Guid.NewGuid())
                (Map.ofList ["Worker", set ["planning"]]) |> ok |> ignore
            run a b
        finally
            try Directory.Delete(root, true) with _ -> ()

    let plan projectId workId =
        { Id = workId; ProjectId = projectId; SubjectId = "task-" + workId
          Kind = WorkKind.Planning; QueueId = "planning"
          StageGeneration = 1; State = WorkState.Available
          Domain = DomainState.Undecided; Gate = GateState.Clear
          ClaimGeneration = 0; Dependencies = Set.empty; WaivedDependencies = Map.empty
          AvailableAt = 0L; AvailableSequence = 0L; SourceLineage = None
          Push = None; IntegrationAuthorized = false }

    let reserved projectId id =
        { Id = id; ProjectId = projectId; RoleId = "Worker"; RoleVersion = 1
          AccountTokenId = Guid.NewGuid().ToString("D")
          State = LaunchState.ReservedUnregistered; Lease = None
          Version = 0; Credit = None; PrimaryWorkId = None
          EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }

    let mutate (binding: LedgerBinding) now mutation =
        let _, state = WorkQueueGitLedger.load binding |> ok
        WorkQueueGitLedger.executeInertFixtureAt binding "fixture" (Guid.NewGuid())
            state.Version now mutation |> ok |> ignore

    let setup (a: LedgerBinding) (b: LedgerBinding) id workId =
        let projectId = a.ProjectId.ToString("D")
        let credential = IssuedWorkCredential.Issue(id)
        mutate a 100L (PublishStage(plan projectId workId))
        let launch = reserved projectId id
        mutate b 101L (AdmitCredentialProject(launch, credential.VerifierSha256))
        mutate a 102L (DispatchOnce(id, DispatchState.Submitted))
        credential

    let auth (binding: LedgerBinding) op version now launchId bearer action =
        WorkQueueGitLedger.executeCredentialFixtureAt binding "credential-fixture" op version now
            launchId bearer action

    [<Test>]
    member _.TwoCloneRegistrationClaimCompletionAndNoGitPlaintext() =
        withClones (fun a b ->
            let id = Guid.NewGuid().ToString("D")
            let workId = "plan-1"
            let credential = setup a b id workId
            let bearer = credential.RevealForPrivateDelivery()
            let issuedAgain = IssuedWorkCredential.Issue id
            Assert.That(credential.ToString(), Does.Not.Contain(bearer))
            Assert.That(issuedAgain.VerifierSha256, Is.Not.EqualTo(credential.VerifierSha256))
            let beforeHead, before = WorkQueueGitLedger.load b |> ok
            let verifier, _ = WorkQueueGitLedger.readCredentialAt b beforeHead id |> ok |> Option.get
            Assert.That(verifier.SchemaVersion, Is.EqualTo(1))
            Assert.That(verifier.LaunchId, Is.EqualTo(id))
            Assert.That(verifier.ProjectId, Is.EqualTo(before.Launches.[id].ProjectId))
            Assert.That(verifier.AccountTokenId, Is.EqualTo(before.Launches.[id].AccountTokenId))
            Assert.That(verifier.SecretSha256, Is.EqualTo(credential.VerifierSha256))
            let text, _ = LedgerStore.readAt b beforeHead (WorkQueueGitLedger.credentialPath id) |> ok
            Assert.That(text.Value, Does.Not.Contain(bearer))
            let project, _ = LedgerStore.readAt b beforeHead WorkQueueGitLedger.RecordPath |> ok
            Assert.That(project.Value, Does.Not.Contain(bearer))
            reject (auth a (Guid.NewGuid()) before.Version 103L id
                         (issuedAgain.RevealForPrivateDelivery()) CredentialWorkAction.FirstConnect)
            reject (WorkQueueGitLedger.executeInertFixtureAt a "fixture" (Guid.NewGuid())
                        before.Version 104L (InitializeAgent(id, "legacy-can-not-bypass")))
            let registrationId = Guid.NewGuid()
            let first = auth a registrationId before.Version 105L id bearer CredentialWorkAction.FirstConnect |> ok
            Assert.That(first.AlreadyApplied, Is.False)
            let replay = auth b registrationId before.Version 105L id bearer CredentialWorkAction.FirstConnect |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            let _, after = WorkQueueGitLedger.load b |> ok
            match after.Launches.[id].State with
            | LaunchState.Registered (registeredId, marker) ->
                Assert.That(registeredId, Is.EqualTo(id))
                Assert.That(marker, Is.EqualTo("work-credential-v1:" + id))
            | other -> Assert.Fail(sprintf "Wrong registered identity %A" other)
            let claimOp = Guid.NewGuid()
            auth b claimOp after.Version 106L id bearer CredentialWorkAction.ClaimPrimary |> ok |> ignore
            let _, claimed = WorkQueueGitLedger.load a |> ok
            Assert.That(claimed.Works.[workId].State, Is.EqualTo(WorkState.Claimed id))
            let finishOp = Guid.NewGuid()
            let firstFinish =
                auth a finishOp claimed.Version 107L id bearer
                    (CredentialWorkAction.FinishOrdinary workId) |> ok
            let replayFinish =
                auth b finishOp claimed.Version 107L id bearer
                    (CredentialWorkAction.FinishOrdinary workId) |> ok
            Assert.That(replayFinish.AlreadyApplied, Is.True)
            Assert.That(replayFinish.Operation.OriginalResultHash,
                        Is.EqualTo(firstFinish.Operation.OriginalResultHash))
            let _, finished = WorkQueueGitLedger.load a |> ok
            Assert.That(finished.Works.[workId].State, Is.EqualTo(WorkState.Completed))
            reject (auth b (Guid.NewGuid()) finished.Version 108L id bearer
                         (CredentialWorkAction.HeartbeatLease 1))
            // Even after completion the old, exact operation receipt can be
            // replayed but a new operation may not use the terminal secret.
            Assert.That((WorkQueueGitLedger.load b |> ok |> snd).Version, Is.EqualTo(finished.Version)))

    [<Test>]
    member _.CrossLaunchAndForeignCredentialAreDeniedEvenWithMatchingPublicLabels() =
        withClones (fun a b ->
            let projectId = a.ProjectId.ToString("D")
            let idA, idB = Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")
            let credentialA = IssuedWorkCredential.Issue idA
            let credentialB = IssuedWorkCredential.Issue idB
            mutate a 100L (PublishStage(plan projectId "plan-A"))
            mutate b 101L (PublishStage(plan projectId "plan-B"))
            mutate a 102L (AdmitCredentialProject(reserved projectId idA, credentialA.VerifierSha256))
            mutate b 103L (AdmitCredentialProject(reserved projectId idB, credentialB.VerifierSha256))
            mutate a 104L (DispatchOnce(idA, DispatchState.Submitted))
            mutate b 105L (DispatchOnce(idB, DispatchState.Submitted))
            let _, version = WorkQueueGitLedger.load a |> ok
            let aBearer = credentialA.RevealForPrivateDelivery()
            let bBearer = credentialB.RevealForPrivateDelivery()
            reject (auth a (Guid.NewGuid()) version.Version 106L idB aBearer CredentialWorkAction.FirstConnect)
            reject (auth b (Guid.NewGuid()) version.Version 106L idA bBearer CredentialWorkAction.FirstConnect)
            reject (auth a (Guid.NewGuid()) version.Version 106L (Guid.NewGuid().ToString("D"))
                         aBearer CredentialWorkAction.FirstConnect)
            let first = auth a (Guid.NewGuid()) version.Version 106L idA aBearer CredentialWorkAction.FirstConnect |> ok
            Assert.That(first.AlreadyApplied, Is.False)
            let _, registered = WorkQueueGitLedger.load b |> ok
            reject (auth b (Guid.NewGuid()) registered.Version 107L idA bBearer
                         CredentialWorkAction.ClaimPrimary)
            let bConnected =
                auth b (Guid.NewGuid()) registered.Version 107L idB bBearer CredentialWorkAction.FirstConnect |> ok
            Assert.That(bConnected.AlreadyApplied, Is.False))

    [<Test>]
    member _.StoppedLeaseRevokesCredentialAndPreventsFreshRegistration() =
        withClones (fun a b ->
            let id = Guid.NewGuid().ToString("D")
            let credential = setup a b id "plan-stop"
            let bearer = credential.RevealForPrivateDelivery()
            let _, unregistered = WorkQueueGitLedger.load a |> ok
            let connectOp = Guid.NewGuid()
            auth b connectOp unregistered.Version 1000L id bearer CredentialWorkAction.FirstConnect |> ok |> ignore
            let _, connected = WorkQueueGitLedger.load a |> ok
            Assert.That(connected.Launches.[id].Lease.Value.Generation, Is.EqualTo(1))
            // Scheduler/admin STOP is separate from execution authentication.
            mutate b 2000L (StopStalled(id, 1, 500L))
            let _, stopped = WorkQueueGitLedger.load a |> ok
            Assert.That(stopped.Launches.[id].Lease.Value.StopRequested, Is.True)
            reject (auth a (Guid.NewGuid()) stopped.Version 2001L id bearer
                         (CredentialWorkAction.HeartbeatLease 2))
            reject (auth b (Guid.NewGuid()) stopped.Version 2002L id bearer CredentialWorkAction.ClaimPrimary)
            reject (auth a (Guid.NewGuid()) stopped.Version 2003L id bearer CredentialWorkAction.FirstConnect))
