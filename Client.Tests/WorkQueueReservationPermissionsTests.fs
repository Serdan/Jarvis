module Client.Tests.WorkQueueReservationPermissionsTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueReservationPermissions

[<TestFixture>]
type WorkQueueReservationPermissionsTests() =
    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Reservation fixture failed: %A" error

    let deny = function
        | Error _ -> ()
        | Ok _ -> Assert.Fail "Unauthorized reservation operation was accepted."

    let git cwd command =
        use gitProcess = new Process()
        gitProcess.StartInfo.FileName <- "git"
        gitProcess.StartInfo.WorkingDirectory <- cwd
        gitProcess.StartInfo.UseShellExecute <- false
        gitProcess.StartInfo.RedirectStandardOutput <- true
        gitProcess.StartInfo.RedirectStandardError <- true
        for arg in command do gitProcess.StartInfo.ArgumentList.Add arg
        Assert.That(gitProcess.Start(), Is.True)
        let output = gitProcess.StandardOutput.ReadToEnd()
        let error = gitProcess.StandardError.ReadToEnd()
        Assert.That(gitProcess.WaitForExit(30000), Is.True)
        Assert.That(gitProcess.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let withClones operation =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-reservation-grants-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let aPath, bPath = Path.Combine(root, "a"), Path.Combine(root, "b")
            git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
            git root ["clone"; bare; aPath] |> ignore
            File.WriteAllText(Path.Combine(aPath, "source.txt"), "reservation fixture")
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
                (Map.ofList ["Worker", set ["planning"; "implementation"; "integration"]]) |> ok |> ignore
            operation a b
        finally
            try Directory.Delete(root, true) with _ -> ()

    let plan project workId =
        { Id = workId; ProjectId = project; SubjectId = "task-" + workId
          Kind = WorkKind.Planning; QueueId = "planning"
          StageGeneration = 1; State = WorkState.Available
          Domain = DomainState.Undecided; Gate = GateState.Clear
          ClaimGeneration = 0; Dependencies = Set.empty; WaivedDependencies = Map.empty
          AvailableAt = 0L; AvailableSequence = 0L; SourceLineage = None
          Push = None; IntegrationAuthorized = false }

    let reserved project launchId =
        { Id = launchId; ProjectId = project; RoleId = "Worker"; RoleVersion = 1
          AccountTokenId = Guid.NewGuid().ToString("D")
          State = LaunchState.ReservedUnregistered; Lease = None
          Version = 0; Credit = None; PrimaryWorkId = None
          EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }

    let mutate binding now mutation =
        let _, project = WorkQueueGitLedger.load binding |> ok
        WorkQueueGitLedger.executeInertFixtureAt binding "fixture" (Guid.NewGuid())
            project.Version now mutation |> ok |> ignore

    let setup (a: LedgerBinding) (b: LedgerBinding) =
        let home = a.ProjectId.ToString("D")
        let launch = Guid.NewGuid().ToString("D")
        let work = "plan-reservation"
        let credential = IssuedWorkCredential.Issue launch
        mutate a 100L (PublishStage(plan home work))
        mutate b 101L (AdmitCredentialProject(reserved home launch, credential.VerifierSha256))
        mutate a 102L (DispatchOnce(launch, DispatchState.Submitted))
        home, work, launch, credential.RevealForPrivateDelivery()

    let operator = "operator:" + String.replicate 64 "a"
    let families =
        set [ ReservationOperationFamily.ContextRead
              ReservationOperationFamily.Implementation
              ReservationOperationFamily.Prerequisite ]

    let revision home launch work number allowed =
        { LaunchId = launch; ExpectedRevision = number; ExpectedWorkId = work
          ReadProjectIds = set [home; "public-project-context"]
          Families = allowed; SourceWorktree = None
          ExpiresAtUnixMs = 5000L; Revoked = false
          OperatorApprovalId = "signed-host-approval-id" }

    let approve (binding: LedgerBinding) now scope =
        WorkQueueReservationPermissions.reviseAt binding operator (Guid.NewGuid()) now scope
        |> ok |> ignore

    let register (binding: LedgerBinding) launch bearer =
        let _, project = WorkQueueGitLedger.load binding |> ok
        WorkQueueGitLedger.executeCredentialFixtureAt binding "credential-fixture" (Guid.NewGuid())
            project.Version 120L launch bearer CredentialWorkAction.FirstConnect
        |> ok |> ignore

    let permission (binding: LedgerBinding) launch work family rev =
        let _, project = WorkQueueGitLedger.load binding |> ok
        let owner = project.Launches.[launch]
        let lease = owner.Lease.Value
        { LaunchId = launch; TargetProjectId = binding.ProjectId.ToString("D")
          TargetWorkId = work; Family = family; SourceRef = None
          SourceSha = None; TargetRef = None; SourceRepositoryId = None
          SourceClaimGeneration = None; SourceWorktree = None
          ExpectedRevision = rev; ExpectedLaunchVersion = owner.Version
          ExpectedLeaseGeneration = lease.Generation }

    let sourceStage (a: LedgerBinding) (b: LedgerBinding) workId launchId =
        let home = a.ProjectId.ToString("D")
        let baseSha = git a.RepositoryPath ["rev-parse"; "HEAD"]
        let lineage : SourceLineage =
            { RepositoryId = WorkQueueGitLedger.sourceRepositoryId a
              TaskId = "task-" + workId; TargetRef = "refs/heads/main"
              LineageId = "lineage-" + workId; PinnedBaseCommit = baseSha
              ReachableRef = None; ContinuationSha = None; Generation = 1 }
        let implementation =
            { plan home workId with
                Kind = WorkKind.Implementation; QueueId = "implementation"
                SourceLineage = Some lineage }
        let credential = IssuedWorkCredential.Issue launchId
        mutate a 100L (PublishStage implementation)
        mutate b 101L (AdmitCredentialProject(reserved home launchId, credential.VerifierSha256))
        mutate a 102L (DispatchOnce(launchId, DispatchState.Submitted))
        let treeName = sprintf "wq03-%s-%s-g1" workId launchId
        let root = Path.GetDirectoryName(a.RepositoryPath)
        let path = Path.Combine(root, treeName)
        git a.RepositoryPath ["worktree"; "add"; "--detach"; path; baseSha] |> ignore
        home, workId, launchId, credential.RevealForPrivateDelivery(),
            lineage, treeName, ({ a with RepositoryPath = path }: LedgerBinding)

    let sourcePermission (binding: LedgerBinding) launch work family revisionNumber
            (lineage: SourceLineage) claimGeneration treeName =
        { permission binding launch work family revisionNumber with
            SourceRef = lineage.ReachableRef
            SourceSha = Some(lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit)
            TargetRef = Some lineage.TargetRef
            SourceRepositoryId = Some lineage.RepositoryId
            SourceClaimGeneration = Some claimGeneration
            SourceWorktree = Some treeName }


    [<Test>]
    member _.TwoCloneScopeIsCredentialBoundAndCrossProjectMutationDoesNotInheritReadAccess() =
        withClones (fun a b ->
            let home, work, launch, bearer = setup a b
            approve a 110L (revision home launch work 0 families)
            let _, head = WorkQueueGitLedger.load b |> ok
            register b launch bearer
            let read = permission a launch work ReservationOperationFamily.ContextRead 1
            let foreignContext = { read with TargetProjectId = "public-project-context" }
            WorkQueueReservationPermissions.checkAt a 130L bearer foreignContext |> ok |> ignore

            let edit = permission b launch work ReservationOperationFamily.Implementation 1
            WorkQueueReservationPermissions.checkAt b 131L bearer edit |> ok |> ignore
            let foreignEdit = { edit with TargetProjectId = "public-project-context" }
            WorkQueueReservationPermissions.checkAt a 132L bearer foreignEdit |> deny
            let wrongWork = { edit with TargetWorkId = "unrelated-backlog" }
            WorkQueueReservationPermissions.checkAt b 133L bearer wrongWork |> deny
            let wrongTree = { edit with SourceWorktree = Some "other-worker-tree" }
            WorkQueueReservationPermissions.checkAt a 134L bearer wrongTree |> deny
            let elevated = { edit with Family = ReservationOperationFamily.Integration }
            WorkQueueReservationPermissions.checkAt a 135L bearer elevated |> deny
            let scheduling = { edit with Family = ReservationOperationFamily.SchedulingAdmin }
            WorkQueueReservationPermissions.checkAt b 136L bearer scheduling |> deny
            WorkQueueReservationPermissions.checkAt a 137L (IssuedWorkCredential.Issue(launch).RevealForPrivateDelivery()) edit
            |> deny
            let snapshot, _ = WorkQueueGitLedger.load b |> ok
            let grant, _ = WorkQueueReservationPermissions.readScopeAt b snapshot launch |> ok |> Option.get
            Assert.That(grant.WorkId, Is.EqualTo(work))
            Assert.That(grant.Revision, Is.EqualTo(1))
            Assert.That(grant.RevisionOperationId, Is.Not.Empty)
            let raw, _ = LedgerStore.readAt b snapshot (WorkQueueReservationPermissions.scopePath launch) |> ok
            Assert.That(raw.Value.Contains(bearer), Is.False)
            let history, _ = LedgerStore.readAt b snapshot (WorkQueueReservationPermissions.revisionPath launch 1) |> ok
            Assert.That(history.Value, Is.EqualTo(raw.Value)))

    [<Test>]
    member _.CompetingGrantRevisionAndLeaseGenerationFailClosedOnBothClones() =
        withClones (fun a b ->
            let home, work, launch, bearer = setup a b
            let baseGrant = revision home launch work 0 families
            approve a 110L baseGrant
            WorkQueueReservationPermissions.reviseAt b operator (Guid.NewGuid()) 111L baseGrant
            |> deny
            register b launch bearer
            let old = permission a launch work ReservationOperationFamily.Implementation 1
            let next = { revision home launch work 1 (set [ReservationOperationFamily.ContextRead;
                                                           ReservationOperationFamily.Prerequisite])
                         with OperatorApprovalId = "operator-reduced-execution" }
            approve b 140L next
            WorkQueueReservationPermissions.checkAt a 141L bearer old |> deny
            let newPermission = { old with ExpectedRevision = 2 }
            WorkQueueReservationPermissions.checkAt b 142L bearer newPermission |> deny
            let prereq = { newPermission with Family = ReservationOperationFamily.Prerequisite }
            WorkQueueReservationPermissions.checkAt a 143L bearer prereq |> ok |> ignore
            let staleLease = { prereq with ExpectedLeaseGeneration = 0 }
            WorkQueueReservationPermissions.checkAt b 144L bearer staleLease |> deny
            let staleLaunch = { prereq with ExpectedLaunchVersion = 0 }
            WorkQueueReservationPermissions.checkAt a 145L bearer staleLaunch |> deny
            WorkQueueReservationPermissions.checkAt b 5000L bearer prereq |> deny
            approve a 200L { next with ExpectedRevision = 2; Revoked = true;
                                        OperatorApprovalId = "operator-revocation" }
            WorkQueueReservationPermissions.checkAt b 201L bearer
                { prereq with ExpectedRevision = 3 } |> deny)

    [<Test>]
    member _.SignificantBlockerIsEvidenceLinkedProposalWithoutQueueOrGraphMutation() =
        withClones (fun a b ->
            let home, work, launch, bearer = setup a b
            approve a 110L (revision home launch work 0 families)
            register b launch bearer
            let beforeHead, before = WorkQueueGitLedger.load a |> ok
            let request = permission b launch work ReservationOperationFamily.Prerequisite 1
            let operation = Guid.NewGuid()
            let evidence = String.replicate 64 "f"
            WorkQueueReservationPermissions.proposePrerequisiteAt a "credential-fixture" operation
                150L bearer request before.GraphVersion ReservationBlockerKind.Significant
                "fix-dependency" evidence "Significant dependency requires independent Work"
            |> ok |> ignore
            let afterHead, after = WorkQueueGitLedger.load b |> ok
            Assert.That(after.GraphVersion, Is.EqualTo(before.GraphVersion))
            Assert.That(after.Works.ContainsKey "fix-dependency", Is.False)
            let proposal, _ = WorkQueueReservationPermissions.readProposalAt b afterHead operation
                                |> ok |> Option.get
            Assert.That(proposal.LaunchId, Is.EqualTo(launch))
            Assert.That(proposal.OwningWorkId, Is.EqualTo(work))
            Assert.That(proposal.Status, Is.EqualTo("ProposedOnlyNotRunnable"))
            Assert.That(proposal.EvidenceSha256, Is.EqualTo(evidence))
            WorkQueueReservationPermissions.proposePrerequisiteAt b "credential-fixture" (Guid.NewGuid())
                151L bearer request (before.GraphVersion + 1) ReservationBlockerKind.OutsideDelegation
                "wrong-graph" evidence "Outside delegated engineering scope"
            |> deny
            let wrong = { request with TargetWorkId = "other-owner" }
            WorkQueueReservationPermissions.proposePrerequisiteAt b "credential-fixture" (Guid.NewGuid())
                152L bearer wrong before.GraphVersion ReservationBlockerKind.OutsideDelegation
                "different-work" evidence "Wrong work owner"
            |> deny)

    [<Test>]
    member _.ClaimAndCheckpointVerifyScopeInsideWinningGitCasAndRejectCrossLaunchBearers() =
        withClones (fun a b ->
            let home, work, launch, bearer = setup a b
            approve a 110L (revision home launch work 0 families)
            register b launch bearer
            let request = permission a launch work ReservationOperationFamily.Implementation 1
            let _, before = WorkQueueGitLedger.load b |> ok
            WorkQueueReservationPermissions.claimFixtureAt b "credential-fixture"
                (Guid.NewGuid()) before.Version 160L bearer request 1 1
            |> ok |> ignore
            let _, claimed = WorkQueueGitLedger.load a |> ok
            Assert.That(claimed.Works.[work].State, Is.EqualTo(WorkState.Claimed launch))
            let stale = permission b launch work ReservationOperationFamily.Implementation 1
            let digest = String.replicate 64 "c"
            WorkQueueReservationPermissions.checkpointFixtureAt a "credential-fixture"
                (Guid.NewGuid()) claimed.Version 170L bearer stale 1 digest
            |> ok |> ignore
            let _, after = WorkQueueGitLedger.load b |> ok
            let wrongToken = IssuedWorkCredential.Issue(launch).RevealForPrivateDelivery()
            WorkQueueReservationPermissions.checkpointFixtureAt b "credential-fixture"
                (Guid.NewGuid()) after.Version 171L wrongToken
                (permission b launch work ReservationOperationFamily.Implementation 1) 1 digest
            |> deny
            WorkQueueReservationPermissions.checkpointFixtureAt b "credential-fixture"
                (Guid.NewGuid()) after.Version 172L bearer
                { (permission b launch work ReservationOperationFamily.Implementation 1)
                  with ExpectedLeaseGeneration = 0 } 1 digest
            |> deny)


    [<Test>]
    member _.OriginalImplementationSourceGrantPinsRegisteredCheckoutAcrossClonesAndClaim() =
        withClones (fun a b ->
            let home, work, launch, bearer, lineage, tree, source = sourceStage a b "impl-source" (Guid.NewGuid().ToString("D"))
            let revision = { revision home launch work 0 families with SourceWorktree = Some tree }
            WorkQueueReservationPermissions.reviseWithSourceAt b (Some source) operator
                (Guid.NewGuid()) 110L revision |> ok |> ignore
            register a launch bearer
            let scoped = sourcePermission b launch work ReservationOperationFamily.Implementation 1 lineage 1 tree
            WorkQueueReservationPermissions.checkSourceAt a source 130L bearer scoped |> ok |> ignore
            WorkQueueReservationPermissions.checkSourceAt b source 131L bearer scoped |> ok |> ignore
            // A string-only check, even with the right credential and scope, is insufficient.
            WorkQueueReservationPermissions.checkAt a 132L bearer scoped |> deny
            let snapshot, _ = WorkQueueGitLedger.load b |> ok
            let grant, _ = WorkQueueReservationPermissions.readScopeAt b snapshot launch |> ok |> Option.get
            Assert.That(grant.SourceRef, Is.EqualTo(None))
            Assert.That(grant.SourceSha, Is.EqualTo(Some lineage.PinnedBaseCommit))
            Assert.That(grant.TargetRef, Is.EqualTo(Some lineage.TargetRef))
            Assert.That(grant.SourceRepositoryId, Is.EqualTo(Some lineage.RepositoryId))
            Assert.That(grant.SourceClaimGeneration, Is.EqualTo(Some 1))
            Assert.That(grant.SourceCheckoutPath, Is.EqualTo(Some source.RepositoryPath))
            let _, current = WorkQueueGitLedger.load a |> ok
            WorkQueueReservationPermissions.claimSourceFixtureAt source "credential-fixture"
                (Guid.NewGuid()) current.Version 150L bearer scoped 1 1 |> ok |> ignore
            let claimed = sourcePermission b launch work ReservationOperationFamily.Implementation 1 lineage 1 tree
            WorkQueueReservationPermissions.checkSourceAt b source 151L bearer claimed |> ok |> ignore
            WorkQueueReservationPermissions.checkSourceAt a source 152L bearer
                { claimed with SourceClaimGeneration = Some 2 } |> deny
            let _, after = WorkQueueGitLedger.load b |> ok
            Assert.That(after.Works.[work].ClaimGeneration, Is.EqualTo(1))
            Assert.That(after.Works.[work].State, Is.EqualTo(WorkState.Claimed launch)))

    [<Test>]
    member _.ForeignValidCheckoutAndContinuationCannotBecomeOriginalGrantOrPermission() =
        withClones (fun a b ->
            let home, work, launch, bearer, lineage, tree, source = sourceStage a b "source-one" (Guid.NewGuid().ToString("D"))
            let wanted = { revision home launch work 0 families with SourceWorktree = Some tree }
            let foreignName = sprintf "wq03-%s-%s-g1" "source-two" (Guid.NewGuid().ToString("D"))
            let foreignPath = Path.Combine(Path.GetDirectoryName(a.RepositoryPath), foreignName)
            git a.RepositoryPath ["worktree"; "add"; "--detach"; foreignPath; lineage.PinnedBaseCommit] |> ignore
            let foreign = { a with RepositoryPath = foreignPath }
            let wrongApproval = { wanted with SourceWorktree = Some foreignName }
            WorkQueueReservationPermissions.reviseWithSourceAt b (Some foreign) operator
                (Guid.NewGuid()) 110L wrongApproval |> deny
            WorkQueueReservationPermissions.reviseWithSourceAt a (Some foreign) operator
                (Guid.NewGuid()) 111L wanted |> deny
            WorkQueueReservationPermissions.reviseWithSourceAt a (Some source) operator
                (Guid.NewGuid()) 112L wanted |> ok |> ignore
            register b launch bearer
            let good = sourcePermission a launch work ReservationOperationFamily.Implementation 1 lineage 1 tree
            // A second, independently registered/detached/clean checkout with
            // the exact same name and SHA cannot substitute for the approved path.
            let otherParent = Path.Combine(Path.GetDirectoryName(b.RepositoryPath), "second-registered-source")
            Directory.CreateDirectory(otherParent) |> ignore
            let sameNameOtherPath = Path.Combine(otherParent, tree)
            git b.RepositoryPath ["worktree"; "add"; "--detach"; sameNameOtherPath; lineage.PinnedBaseCommit] |> ignore
            let sameNameOther = { b with RepositoryPath = sameNameOtherPath }
            WorkQueueGitLedger.verifySourceClaimCheckout sameNameOther launch
                { plan home work with Kind = WorkKind.Implementation
                                      SourceLineage = Some lineage; ClaimGeneration = 1 }
            |> ok |> ignore
            WorkQueueReservationPermissions.checkSourceAt b sameNameOther 140L bearer good |> deny
            WorkQueueReservationPermissions.checkSourceAt a foreign 141L bearer
                { good with SourceWorktree = Some foreignName } |> deny
            let foreignRef = "refs/heads/jarvis-wip/unrelated-valid-continuation"
            git a.RepositoryPath ["push"; "origin"; "HEAD:" + foreignRef] |> ignore
            WorkQueueReservationPermissions.checkSourceAt b source 142L bearer
                { good with SourceRef = Some foreignRef } |> deny
            WorkQueueReservationPermissions.checkSourceAt a source 143L bearer
                { good with SourceSha = Some(String.replicate 40 "a") } |> deny
            WorkQueueReservationPermissions.checkSourceAt b source 144L bearer
                { good with TargetRef = Some foreignRef } |> deny
            WorkQueueReservationPermissions.checkSourceAt b source 145L bearer
                { good with SourceClaimGeneration = Some 2 } |> deny
            WorkQueueReservationPermissions.checkSourceAt a source 146L
                (IssuedWorkCredential.Issue(launch).RevealForPrivateDelivery()) good |> deny
            let _, before = WorkQueueGitLedger.load b |> ok
            Assert.That(before.Works.[work].State, Is.EqualTo(WorkState.Available)))


    [<Test>]
    member _.SuccessorSourceGrantUsesOriginalReachableWipRefAndShaNotTarget() =
        withClones (fun a b ->
            let home, work, launch, bearer, lineage, tree, source =
                sourceStage a b "source-continuation" (Guid.NewGuid().ToString("D"))
            let firstRevision = { revision home launch work 0 families with SourceWorktree = Some tree }
            WorkQueueReservationPermissions.reviseWithSourceAt a (Some source) operator
                (Guid.NewGuid()) 110L firstRevision |> ok |> ignore
            register b launch bearer
            let first = sourcePermission a launch work ReservationOperationFamily.Implementation 1 lineage 1 tree
            let _, available = WorkQueueGitLedger.load b |> ok
            WorkQueueReservationPermissions.claimSourceFixtureAt source "credential-fixture"
                (Guid.NewGuid()) available.Version 160L bearer first 1 1 |> ok |> ignore
            let _, claimed = WorkQueueGitLedger.load a |> ok
            let sourceFile = Path.Combine(source.RepositoryPath, "source.txt")
            File.WriteAllText(sourceFile, "verified successor WIP")
            git source.RepositoryPath ["add"; "source.txt"] |> ignore
            git source.RepositoryPath
                ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
                 "commit"; "-m"; "successor WIP"] |> ignore
            let sha = git source.RepositoryPath ["rev-parse"; "HEAD"]
            let remoteRef = sprintf "refs/heads/jarvis-wip/%s/%s-g1" work launch
            git source.RepositoryPath ["push"; "origin"; "HEAD:" + remoteRef] |> ignore
            let handoff : SourceCheckpoint =
                { WorkId = work; LaunchId = launch; AgentUuid = launch
                  RepositoryId = lineage.RepositoryId; TaskId = lineage.TaskId
                  TargetRef = lineage.TargetRef; LineageId = lineage.LineageId
                  PinnedBaseCommit = lineage.PinnedBaseCommit; StageGeneration = 1
                  ClaimGeneration = 1; LineageGeneration = 1
                  ContinuationRef = remoteRef; ContinuationSha = sha
                  PushOperationId = Guid.NewGuid().ToString("D")
                  PushCommand = "git push origin HEAD:" + remoteRef
                  PushOutcome = PushOutcome.Observed sha; ObservedRemoteSha = sha
                  RecoveryNote = "Immutable remote inspected independently, no blind retry"
                  Summary = "A real clean committed WIP generation"
                  NextAction = "Continuation with a new credentialed source generation"
                  TestEvidence = "Two-clone release source-continuation fixture" }
            mutate source 170L (YieldSource handoff)
            let _, yielded = WorkQueueGitLedger.load a |> ok
            let resumedWork = yielded.Works.[work]
            Assert.That(resumedWork.SourceLineage.Value.ReachableRef, Is.EqualTo(Some remoteRef))
            Assert.That(resumedWork.SourceLineage.Value.ContinuationSha, Is.EqualTo(Some sha))
            let nextLaunch = Guid.NewGuid().ToString("D")
            let nextCredential = IssuedWorkCredential.Issue nextLaunch
            mutate a 171L (AdmitCredentialProject(reserved home nextLaunch, nextCredential.VerifierSha256))
            mutate b 172L (DispatchOnce(nextLaunch, DispatchState.Submitted))
            let nextClaimGeneration = resumedWork.ClaimGeneration + 1
            let nextName = sprintf "wq03-%s-%s-g%d" work nextLaunch nextClaimGeneration
            git b.RepositoryPath ["fetch"; "--no-tags"; "origin"; remoteRef] |> ignore
            let nextCheckoutPath = Path.Combine(Path.GetDirectoryName(b.RepositoryPath), nextName)
            git b.RepositoryPath ["worktree"; "add"; "--detach"; nextCheckoutPath; sha] |> ignore
            let successor = { b with RepositoryPath = nextCheckoutPath }
            let nextScope = { revision home nextLaunch work 0 families with SourceWorktree = Some nextName }
            WorkQueueReservationPermissions.reviseWithSourceAt b (Some successor) operator
                (Guid.NewGuid()) 180L nextScope |> ok |> ignore
            let nextBearer = nextCredential.RevealForPrivateDelivery()
            register a nextLaunch nextBearer
            let successorLineage = resumedWork.SourceLineage.Value
            let sourceRequest =
                sourcePermission b nextLaunch work ReservationOperationFamily.Implementation
                    1 successorLineage nextClaimGeneration nextName
            let sourceGrant = WorkQueueReservationPermissions.checkSourceAt a successor 190L
                                  nextBearer sourceRequest |> ok
            Assert.That(sourceGrant.SourceRef, Is.EqualTo(Some remoteRef))
            Assert.That(sourceGrant.SourceSha, Is.EqualTo(Some sha))
            Assert.That(sourceGrant.TargetRef, Is.EqualTo(Some lineage.TargetRef))
            Assert.That(sourceGrant.SourceClaimGeneration, Is.EqualTo(Some nextClaimGeneration))
            WorkQueueReservationPermissions.checkSourceAt b successor 191L nextBearer
                { sourceRequest with SourceRef = Some lineage.TargetRef } |> deny
            WorkQueueReservationPermissions.checkSourceAt a successor 192L nextBearer
                { sourceRequest with SourceSha = Some lineage.PinnedBaseCommit } |> deny
            WorkQueueReservationPermissions.checkSourceAt b successor 193L nextBearer
                { sourceRequest with SourceClaimGeneration = Some 1 } |> deny
            WorkQueueReservationPermissions.checkSourceAt a source 194L bearer first |> deny
            let _, beforeClaim = WorkQueueGitLedger.load a |> ok
            WorkQueueReservationPermissions.claimSourceFixtureAt successor "credential-fixture"
                (Guid.NewGuid()) beforeClaim.Version 195L nextBearer sourceRequest 1 nextClaimGeneration
            |> ok |> ignore
            let _, after = WorkQueueGitLedger.load b |> ok
            Assert.That(after.Works.[work].State, Is.EqualTo(WorkState.Claimed nextLaunch)))
