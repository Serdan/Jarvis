module Client.Tests.WorkQueueClaimBoundDirtySourcePolicyTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Common
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueReservationPermissions
open Client.SourceEffectJournal
open Client.SourceEffectRouteBridge
open Client.WorkQueueClaimBoundDirtySourcePolicy

let private ok = function
    | Ok value -> value
    | Error e -> failwithf "Claim-bound dirty source fixture: %A" e

let private git cwd args =
    use gitProcess = new Process()
    gitProcess.StartInfo.FileName <- "git"
    gitProcess.StartInfo.WorkingDirectory <- cwd
    gitProcess.StartInfo.UseShellExecute <- false
    gitProcess.StartInfo.RedirectStandardOutput <- true
    gitProcess.StartInfo.RedirectStandardError <- true
    for arg in args do gitProcess.StartInfo.ArgumentList.Add arg
    Assert.That(gitProcess.Start(), Is.True)
    let result = gitProcess.StandardOutput.ReadToEnd()
    let errors = gitProcess.StandardError.ReadToEnd()
    Assert.That(gitProcess.WaitForExit(30000), Is.True)
    Assert.That(gitProcess.ExitCode, Is.EqualTo(0), errors)
    result.Trim()

type private Fixture =
    { Root: string
      A: LedgerBinding
      B: LedgerBinding
      Source: LedgerBinding
      WorkId: string
      LaunchId: string
      TreeName: string
      Bearer: string
      GrantRequest: ReservationPermissionRequest
      HostJournal: SourceEffectStore
      Intent: SourceEffectIntent
      Effect: ClaimedDirtySourceEvidence }

let private mutate binding now mutation =
    let _, before = WorkQueueGitLedger.load binding |> ok
    WorkQueueGitLedger.executeInertFixtureAt binding "fixture" (Guid.NewGuid())
        before.Version now mutation |> ok |> ignore

let private plan project workId (lineage: SourceLineage) : WorkStage =
    { Id = workId; ProjectId = project; SubjectId = lineage.TaskId
      Kind = WorkKind.Implementation; QueueId = "implementation"
      StageGeneration = 1; State = WorkState.Available
      Domain = DomainState.Undecided; Gate = GateState.Clear
      ClaimGeneration = 0; Dependencies = Set.empty; WaivedDependencies = Map.empty
      AvailableAt = 0L; AvailableSequence = 0L; SourceLineage = Some lineage
      Push = None; IntegrationAuthorized = false }

let private reserved project launchId : Launch =
    { Id = launchId; ProjectId = project; RoleId = "Worker"; RoleVersion = 1
      AccountTokenId = Guid.NewGuid().ToString("D")
      State = LaunchState.ReservedUnregistered; Lease = None
      Version = 0; Credit = None; PrimaryWorkId = None
      EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }

let private withFixture action =
    let root = Path.Combine(Path.GetTempPath(),
                            "jarvis-claimed-dirty-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let bare = Path.Combine(root, "remote.git")
        let aPath = Path.Combine(root, "a")
        let bPath = Path.Combine(root, "b")
        git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
        git root ["clone"; bare; aPath] |> ignore
        File.WriteAllText(Path.Combine(aPath, "source.txt"), "claim fixture")
        git aPath ["add"; "source.txt"] |> ignore
        git aPath ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
                   "commit"; "-m"; "base"] |> ignore
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
            (Map.ofList ["Worker", set ["planning"; "implementation"]]) |> ok |> ignore
        let workId, launchId = "claimed-dirty", Guid.NewGuid().ToString("D")
        let project = projectId.ToString("D")
        let baseSha = git aPath ["rev-parse"; "HEAD"]
        let lineage: SourceLineage =
            { RepositoryId = WorkQueueGitLedger.sourceRepositoryId a
              TaskId = "task-claimed-dirty"; TargetRef = "refs/heads/main"
              LineageId = "lineage-claimed-dirty"; PinnedBaseCommit = baseSha
              ReachableRef = None; ContinuationSha = None; Generation = 1 }
        let issued = IssuedWorkCredential.Issue launchId
        mutate a 100L (PublishStage(plan project workId lineage))
        mutate b 101L (AdmitCredentialProject(reserved project launchId,
                                             issued.VerifierSha256))
        mutate a 102L (DispatchOnce(launchId, DispatchState.Submitted))
        let treeName = sprintf "wq03-%s-%s-g1" workId launchId
        let treePath = Path.Combine(root, treeName)
        git aPath ["worktree"; "add"; "--detach"; treePath; baseSha] |> ignore
        let source = binding treePath
        let scope =
            { LaunchId = launchId; ExpectedRevision = 0; ExpectedWorkId = workId
              ReadProjectIds = set [project]
              Families = set [ReservationOperationFamily.ContextRead;
                              ReservationOperationFamily.Implementation]
              SourceWorktree = Some treeName; ExpiresAtUnixMs = 5000L
              Revoked = false; OperatorApprovalId = "trusted-operator-fixture" }
        let operator = "operator:" + String.replicate 64 "a"
        WorkQueueReservationPermissions.reviseWithSourceAt a (Some source) operator
            (Guid.NewGuid()) 110L scope |> ok |> ignore
        let bearer = issued.RevealForPrivateDelivery()
        let _, beforeRegister = WorkQueueGitLedger.load b |> ok
        WorkQueueGitLedger.executeCredentialFixtureAt b "credential-fixture" (Guid.NewGuid())
            beforeRegister.Version 120L launchId bearer CredentialWorkAction.FirstConnect
            |> ok |> ignore
        let makePermission () =
            let _, current = WorkQueueGitLedger.load b |> ok
            let launch = current.Launches.[launchId]
            { LaunchId = launchId; TargetProjectId = project
              TargetWorkId = workId; Family = ReservationOperationFamily.Implementation
              SourceRef = None; SourceSha = Some baseSha
              TargetRef = Some lineage.TargetRef; SourceRepositoryId = Some lineage.RepositoryId
              SourceClaimGeneration = Some 1; SourceWorktree = Some treeName
              ExpectedRevision = 1; ExpectedLaunchVersion = launch.Version
              ExpectedLeaseGeneration = launch.Lease.Value.Generation }
        let initial = makePermission()
        WorkQueueReservationPermissions.checkSourceAt b source 125L bearer initial
            |> ok |> ignore
        let _, unclaimed = WorkQueueGitLedger.load a |> ok
        WorkQueueReservationPermissions.claimSourceFixtureAt source "credential-fixture"
            (Guid.NewGuid()) unclaimed.Version 130L bearer initial 1 1 |> ok |> ignore
        let afterClaim = makePermission()
        WorkQueueReservationPermissions.checkSourceAt a source 131L bearer afterClaim
            |> ok |> ignore

        let journalRoot = Path.Combine(root, "host-journal")
        Directory.CreateDirectory journalRoot |> ignore
        let anchor = journalRoot + ".continuity.anchor"
        provisionTrustedHostRoot journalRoot anchor |> ok |> ignore
        let journal = openExistingAtHostRoot journalRoot anchor |> ok
        let original = Guid.NewGuid()
        let editCommand =
            WriteFileCommand
                { ProjectName = "Fixture::" + treeName
                  FilePath = "source.txt"; Content = "intentional dirty WIP"
                  FileWriteMode = FileWriteMode.Write
                  ExpectedHash = None; CreateParents = Some false }
        let requestSha = requestSha256 editCommand
        let effectScope =
            fromTrustedLedgerBinding a "Fixture" lineage.RepositoryId lineage.TargetRef
                treeName workId (Guid.Parse launchId) 1
        let intent =
            { OriginalOperationId = original; Scope = effectScope
              Kind = FileEdit; RequestSha256 = requestSha }
        let evidence =
            { Operation = ClaimedDirtySourceOperation.CompletedAwaitedFileEdit editCommand
              OriginalEffectId = original; RequestSha256 = requestSha }
        let f =
            { Root = root; A = a; B = b; Source = source; WorkId = workId
              LaunchId = launchId; TreeName = treeName; Bearer = bearer
              GrantRequest = afterClaim; HostJournal = journal
              Intent = intent; Effect = evidence }
        action f
    finally
        try Directory.Delete(root, true) with _ -> ()

let private awaitedEdit f =
    runAwaitedHostFileEdit f.HostJournal f.Intent (fun () -> task {
        File.WriteAllText(Path.Combine(f.Source.RepositoryPath, "source.txt"), "intentional dirty WIP")
        return (Ok() : Result<unit,string>)
    })
    |> fun job -> job.GetAwaiter().GetResult()
    |> ok |> ok

let private inspect f source bearer request effect journal =
    WorkQueueClaimBoundDirtySourcePolicy.inspectDisposable true
        f.A source "Fixture" journal 150L bearer request effect

let private effectWithFilePath (evidence: ClaimedDirtySourceEvidence) filePath =
    let command =
        match evidence.Operation with
        | ClaimedDirtySourceOperation.CompletedAwaitedFileEdit
              (WriteFileCommand original) ->
            WriteFileCommand { original with FilePath = filePath }
        | ClaimedDirtySourceOperation.CompletedAwaitedFileEdit
              (PatchFileCommand original) ->
            PatchFileCommand { original with FilePath = filePath }
        | _ -> failwith "Expected original file-edit request"
    { evidence with Operation = ClaimedDirtySourceOperation.CompletedAwaitedFileEdit command }

let private verified = function
    | ClaimedDirtySourceDecision.EvidenceVerifiedForFixture receipt -> receipt
    | other -> failwithf "Expected verified post-effect evidence, got %A" other

let private blocked = function
    | ClaimedDirtySourceDecision.Rejected _
    | ClaimedDirtySourceDecision.HostProofUnavailable _ -> ()
    | other -> failwithf "Unsafe source operation survived WQ11 gate: %A" other

[<Test>]
let clean_claim_then_host_awaited_edit_dirty_checkout_two_clone_evidence_and_checkpoint () =
    withFixture (fun f ->
        // The normal WQ02 issuer now supplies a 36-character CSPRNG UUIDv4.
        Assert.That(WorkCredentialFormat.isCanonicalUuidV4 f.Bearer, Is.True)
        awaitedEdit f
        // Original claim authority stays clean-only; never relax this invariant.
        WorkQueueReservationPermissions.checkSourceAt f.B f.Source 140L f.Bearer
            f.GrantRequest
        |> function
            | Error _ -> ()
            | Ok _ -> Assert.Fail "WQ03 clean claim check was weakened."
        let a = inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal)
                |> verified
        let b = WorkQueueClaimBoundDirtySourcePolicy.inspectDisposable true f.B f.Source
                    "Fixture" (Some f.HostJournal) 151L f.Bearer f.GrantRequest f.Effect
                |> verified
        Assert.That(a, Is.EqualTo b)
        Assert.That(a.ClaimGeneration, Is.EqualTo 1)
        Assert.That(a.GrantRevision, Is.EqualTo 1)
        Assert.That(a.Proof, Is.EqualTo "host-awaited-file-edit-returned-v1")
        let checkpoint =
            { f.Effect with Operation = ClaimedDirtySourceOperation.ReadOnlyCheckpoint }
        let result = inspect f f.Source f.Bearer f.GrantRequest checkpoint
                        (Some f.HostJournal) |> verified
        Assert.That(result.OriginalEffectId, Is.EqualTo f.Intent.OriginalOperationId)
        // A source test has no authenticated process-tree terminal evidence.
        let testing =
            { f.Effect with Operation = ClaimedDirtySourceOperation.TestCompletion }
        match inspect f f.Source f.Bearer f.GrantRequest testing (Some f.HostJournal) with
        | ClaimedDirtySourceDecision.HostProofUnavailable _ -> ()
        | other -> Assert.Fail(sprintf "Unproved test completion accepted: %A" other))

[<Test>]
let missing_uncertain_wrong_uuid_request_and_journal_do_not_prove_dirty_effect () =
    withFixture (fun f ->
        // Before the host executes the awaited edit, even a correctly scoped
        // original UUID cannot be used as terminal evidence.
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal) |> blocked
        beginBeforeDispatch f.HostJournal f.Intent |> ok |> ignore
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal) |> blocked
        recordUncertain f.HostJournal f.Intent "Lost original edit acknowledgment"
            |> ok |> ignore
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal) |> blocked
        inspect f f.Source f.Bearer f.GrantRequest
            { f.Effect with OriginalEffectId = Guid.NewGuid() } (Some f.HostJournal) |> blocked
        inspect f f.Source f.Bearer f.GrantRequest
            { f.Effect with RequestSha256 = String.replicate 64 "f" } (Some f.HostJournal) |> blocked
        inspect f f.Source f.Bearer f.GrantRequest f.Effect None |> blocked)

[<Test>]
let foreign_valid_same_name_wrong_path_wrong_credential_and_symlink_are_rejected () =
    withFixture (fun f ->
        awaitedEdit f
        let sameName = Path.Combine(f.Root, "other-parent", f.TreeName)
        Directory.CreateDirectory(Path.GetDirectoryName sameName) |> ignore
        git f.B.RepositoryPath ["worktree"; "add"; "--detach"; sameName;
                                git f.B.RepositoryPath ["rev-parse"; "HEAD"] ] |> ignore
        let foreign = { f.B with RepositoryPath = sameName }
        inspect f foreign f.Bearer f.GrantRequest f.Effect (Some f.HostJournal) |> blocked
        inspect f f.Source (IssuedWorkCredential.Issue(f.LaunchId).RevealForPrivateDelivery())
            f.GrantRequest f.Effect (Some f.HostJournal) |> blocked
        inspect f f.Source f.Bearer
            { f.GrantRequest with SourceClaimGeneration = Some 2 }
            f.Effect (Some f.HostJournal) |> blocked
        inspect f f.Source f.Bearer
            { f.GrantRequest with TargetRef = Some "refs/heads/foreign" }
            f.Effect (Some f.HostJournal) |> blocked
        let outside = Path.Combine(f.Root, "outside.txt")
        File.WriteAllText(outside, "outside")
        let link = Path.Combine(f.Source.RepositoryPath, "out.txt")
        File.CreateSymbolicLink(link, outside) |> ignore
        inspect f f.Source f.Bearer f.GrantRequest
            (effectWithFilePath f.Effect "out.txt")
            (Some f.HostJournal)
        |> function
            | ClaimedDirtySourceDecision.Rejected reason
                when reason.Contains("symlink", StringComparison.OrdinalIgnoreCase) -> ()
            | other -> Assert.Fail(sprintf "Symbolic-link guard not proven: %A" other)
        inspect f f.Source f.Bearer f.GrantRequest
            (effectWithFilePath f.Effect "../outside.txt")
            (Some f.HostJournal)
        |> function
            | ClaimedDirtySourceDecision.Rejected reason
                when reason.Contains("relative", StringComparison.OrdinalIgnoreCase) -> ()
            | other -> Assert.Fail(sprintf "Traversal guard not proven: %A" other))

[<Test>]
let same_path_replacement_from_second_clone_cannot_reuse_original_host_edit () =
    withFixture (fun f ->
        awaitedEdit f
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal)
        |> verified |> ignore
        let originalGitAdmin = git f.Source.RepositoryPath ["rev-parse"; "--absolute-git-dir"]
        let originalObjects = git f.Source.RepositoryPath ["rev-parse"; "--git-path"; "objects"]
        let originalSha = git f.Source.RepositoryPath ["rev-parse"; "HEAD"]
        // The claimed dirty checkout disappears; the exact same pathname and
        // generation-qualified basename now point to clone B's linked tree.
        // The trusted host journal is intentionally left unchanged.
        git f.A.RepositoryPath ["worktree"; "remove"; "--force";
                                f.Source.RepositoryPath] |> ignore
        git f.B.RepositoryPath ["worktree"; "add"; "--detach";
                                f.Source.RepositoryPath; originalSha] |> ignore
        Assert.That(git f.Source.RepositoryPath ["rev-parse"; "HEAD"],
                    Is.EqualTo originalSha)
        Assert.That(git f.Source.RepositoryPath ["rev-parse"; "--absolute-git-dir"],
                    Is.Not.EqualTo originalGitAdmin)
        Assert.That(git f.Source.RepositoryPath ["rev-parse"; "--git-path"; "objects"],
                    Is.Not.EqualTo originalObjects)
        // An unmodified grant, valid same-remote source ref, exact credential,
        // physical pathname and historical terminal UUID are NOT sufficient.
        match inspect f f.Source f.Bearer f.GrantRequest f.Effect
                      (Some f.HostJournal) with
        | ClaimedDirtySourceDecision.Rejected reason
            when reason.Contains("Git administration", StringComparison.OrdinalIgnoreCase)
                 || reason.Contains("object-store", StringComparison.OrdinalIgnoreCase) -> ()
        | other -> Assert.Fail(sprintf "Same-path clone substitution survived identity binding: %A" other))

[<Test>]
let same_clone_same_path_checkout_reincarnation_cannot_reuse_original_host_edit () =
    withFixture (fun f ->
        awaitedEdit f
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal)
        |> verified |> ignore
        let path = f.Source.RepositoryPath
        let originalSha = git path ["rev-parse"; "HEAD"]
        let admin = git path ["rev-parse"; "--absolute-git-dir"]
        let common = git path ["rev-parse"; "--git-common-dir"]
        let objects = git path ["rev-parse"; "--git-path"; "objects"]
        let marker = Path.Combine(admin, "jarvis-wq11-checkout-instance.v1")
        Assert.That(File.Exists marker, Is.True, "Clean WQ03 claim must have host instance proof.")
        // Remove the *same clone's* original linked checkout after its edit.
        // Git can recreate exactly the original admin/common/objects paths.
        // Keep the successful original journal, grant and credential untouched.
        git f.A.RepositoryPath ["worktree"; "remove"; "--force"; path] |> ignore
        git f.A.RepositoryPath ["worktree"; "add"; "--detach"; path; originalSha] |> ignore
        Assert.That(git path ["rev-parse"; "HEAD"], Is.EqualTo originalSha)
        Assert.That(git path ["rev-parse"; "--absolute-git-dir"], Is.EqualTo admin)
        Assert.That(git path ["rev-parse"; "--git-common-dir"], Is.EqualTo common)
        Assert.That(git path ["rev-parse"; "--git-path"; "objects"], Is.EqualTo objects)
        Assert.That(File.Exists marker, Is.False,
                    "Git removal must destroy the original host-owned checkout instance marker.")
        // Even a new operator scope revision cannot silently mint a replacement
        // witness for the *old* claim generation and authorize its old journal.
        let replacementScope: ReservationScopeRevision =
            { LaunchId = f.LaunchId; ExpectedRevision = 1; ExpectedWorkId = f.WorkId
              ReadProjectIds = set [f.A.ProjectId.ToString("D")]
              Families = set [ReservationOperationFamily.ContextRead;
                              ReservationOperationFamily.Implementation]
              SourceWorktree = Some f.TreeName; ExpiresAtUnixMs = 5000L
              Revoked = false; OperatorApprovalId = "reincarnation-rebind-denial" }
        match WorkQueueReservationPermissions.reviseWithSourceAt f.A (Some f.Source)
                  ("operator:" + String.replicate 64 "a") (Guid.NewGuid()) 200L
                  replacementScope with
        | Error (InvalidBinding reason)
            when reason.Contains("checkout instance proof", StringComparison.OrdinalIgnoreCase) -> ()
        | other ->
            Assert.Fail(sprintf "Same-claim regrant did not fail on lost checkout instance: %A" other)
        let verifiedSnapshot, _ = WorkQueueGitLedger.load f.A |> ok
        let currentScope =
            WorkQueueReservationPermissions.readScopeAt f.A verifiedSnapshot f.LaunchId |> ok
        Assert.That((currentScope |> Option.get |> fst).Revision, Is.EqualTo 1)
        match inspect f f.Source f.Bearer f.GrantRequest f.Effect
                      (Some f.HostJournal) with
        | ClaimedDirtySourceDecision.HostProofUnavailable reason
            when reason.Contains("checkout instance", StringComparison.OrdinalIgnoreCase) -> ()
        | ClaimedDirtySourceDecision.Rejected reason
            when reason.Contains("checkout instance", StringComparison.OrdinalIgnoreCase) -> ()
        | other ->
            Assert.Fail(sprintf
                "Reincarnated same-clone checkout wrongly inherited original FileEdit receipt: %A" other))

[<Test>]
let unchanged_checkout_renewal_preserves_immutable_claim_instance_and_edit_evidence () =
    withFixture (fun f ->
        let beforeSnapshot, _ = WorkQueueGitLedger.load f.A |> ok
        let before =
            WorkQueueReservationPermissions.readSourceClaimInstanceAt
                f.A beforeSnapshot f.LaunchId 1 |> ok |> Option.get |> fst
        let renewal: ReservationScopeRevision =
            { LaunchId = f.LaunchId; ExpectedRevision = 1; ExpectedWorkId = f.WorkId
              ReadProjectIds = set [f.A.ProjectId.ToString("D")]
              Families = set [ReservationOperationFamily.ContextRead;
                              ReservationOperationFamily.Implementation]
              SourceWorktree = Some f.TreeName; ExpiresAtUnixMs = 5000L
              Revoked = false; OperatorApprovalId = "same-instance-renewal" }
        WorkQueueReservationPermissions.reviseWithSourceAt f.A (Some f.Source)
            ("operator:" + String.replicate 64 "a") (Guid.NewGuid()) 140L renewal
        |> ok |> ignore
        let afterSnapshot, _ = WorkQueueGitLedger.load f.B |> ok
        let after =
            WorkQueueReservationPermissions.readSourceClaimInstanceAt
                f.B afterSnapshot f.LaunchId 1 |> ok |> Option.get |> fst
        Assert.That(after, Is.EqualTo before)
        awaitedEdit f
        inspect f f.Source f.Bearer
            { f.GrantRequest with ExpectedRevision = 2 } f.Effect (Some f.HostJournal)
        |> verified |> ignore)

[<Test>]
let modified_head_revoked_grant_stale_claim_and_disabled_route_remain_fail_closed () =
    withFixture (fun f ->
        awaitedEdit f
        Assert.That(WorkQueueClaimBoundDirtySourcePolicy.inspectDisposable false
                        f.A f.Source "Fixture" (Some f.HostJournal) 150L
                        f.Bearer f.GrantRequest f.Effect,
                    Is.EqualTo ClaimedDirtySourceDecision.Disabled)
        // Original grant can be revoked without reapproving a dirty checkout.
        let revoked =
            { LaunchId = f.LaunchId; ExpectedRevision = 1; ExpectedWorkId = f.WorkId
              ReadProjectIds = set [f.A.ProjectId.ToString("D")]
              Families = Set.empty; SourceWorktree = None; ExpiresAtUnixMs = 5000L
              Revoked = true; OperatorApprovalId = "operator-revoked" }
        WorkQueueReservationPermissions.reviseWithSourceAt f.B None
            ("operator:" + String.replicate 64 "a") (Guid.NewGuid()) 153L revoked
            |> ok |> ignore
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal) |> blocked)

[<Test>]
let changed_head_and_different_generation_are_not_licensed_by_old_terminal_file_edit () =
    withFixture (fun f ->
        awaitedEdit f
        let path = f.Source.RepositoryPath
        git path ["add"; "source.txt"] |> ignore
        git path ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
                  "commit"; "-m"; "intentionally moved HEAD"] |> ignore
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal) |> blocked)

[<Test>]
let stopped_claim_cannot_reuse_previous_terminal_host_effect () =
    withFixture (fun f ->
        awaitedEdit f
        // A terminal file receipt is not a source execution lease. STOP wins
        // on the original project Git CAS and invalidates subsequent previews.
        mutate f.B 2000L (StopStalled(f.LaunchId, 1, 500L))
        inspect f f.Source f.Bearer f.GrantRequest f.Effect (Some f.HostJournal)
        |> blocked)

[<Test>]
let mismatched_original_file_command_and_checkout_never_reuse_terminal_receipt () =
    withFixture (fun f ->
        awaitedEdit f
        inspect f f.Source f.Bearer f.GrantRequest
            (effectWithFilePath f.Effect "different.txt") (Some f.HostJournal) |> blocked
        let differentCheckout =
            match f.Effect.Operation with
            | ClaimedDirtySourceOperation.CompletedAwaitedFileEdit
                  (WriteFileCommand original) ->
                { f.Effect with
                    Operation = ClaimedDirtySourceOperation.CompletedAwaitedFileEdit
                        (WriteFileCommand { original with ProjectName = "Fixture::different-checkout" }) }
            | _ -> failwith "Expected original host-owned WriteFile"
        inspect f f.Source f.Bearer f.GrantRequest differentCheckout
            (Some f.HostJournal) |> blocked)

[<Test>]
let independent_remote_source_handoff_preserves_clean_successor_claim_and_rejects_old_dirty_authority () =
    withFixture (fun f ->
        awaitedEdit f
        let original = inspect f f.Source f.Bearer f.GrantRequest f.Effect
                           (Some f.HostJournal) |> verified
        let before = WorkQueueGitLedger.load f.A |> ok |> snd
        let stage = before.Works.[f.WorkId]
        let lineage = stage.SourceLineage.Value
        git f.Source.RepositoryPath ["add"; "source.txt"] |> ignore
        git f.Source.RepositoryPath
            ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
             "commit"; "-m"; "wq11 deliberate WIP"] |> ignore
        let continuationSha = git f.Source.RepositoryPath ["rev-parse"; "HEAD"]
        let continuationRef =
            sprintf "refs/heads/jarvis-wip/%s/%s-g1" f.WorkId f.LaunchId
        git f.Source.RepositoryPath ["push"; "origin"; "HEAD:" + continuationRef] |> ignore
        let handoff: SourceCheckpoint =
            { WorkId = f.WorkId; LaunchId = f.LaunchId; AgentUuid = f.LaunchId
              RepositoryId = lineage.RepositoryId; TaskId = lineage.TaskId
              TargetRef = lineage.TargetRef; LineageId = lineage.LineageId
              PinnedBaseCommit = lineage.PinnedBaseCommit; StageGeneration = 1
              ClaimGeneration = 1; LineageGeneration = 1
              ContinuationRef = continuationRef; ContinuationSha = continuationSha
              PushOperationId = Guid.NewGuid().ToString("D")
              PushCommand = "git push origin HEAD:" + continuationRef
              PushOutcome = PushOutcome.Observed continuationSha
              ObservedRemoteSha = continuationSha
              RecoveryNote = "Original push observed independently without blind retry"
              Summary = "Cleanly committed original dirty generation for successor"
              NextAction = "Use generation-qualified detached successor checkout"
              TestEvidence = "Disposable two-clone WQ11 Release lineage fixture" }
        mutate f.Source 180L (YieldSource handoff)
        // The old source credential and original file receipt no longer apply.
        inspect f f.Source f.Bearer f.GrantRequest f.Effect
            (Some f.HostJournal) |> blocked
        let _, afterHandoff = WorkQueueGitLedger.load f.A |> ok
        let successorLineage = afterHandoff.Works.[f.WorkId].SourceLineage.Value
        Assert.That(successorLineage.ReachableRef, Is.EqualTo(Some continuationRef))
        Assert.That(successorLineage.ContinuationSha, Is.EqualTo(Some continuationSha))
        let nextLaunch = Guid.NewGuid().ToString("D")
        let nextCredential = IssuedWorkCredential.Issue nextLaunch
        mutate f.A 181L (AdmitCredentialProject(
            reserved (f.A.ProjectId.ToString("D")) nextLaunch,
            nextCredential.VerifierSha256))
        mutate f.B 182L (DispatchOnce(nextLaunch, DispatchState.Submitted))
        let nextGeneration = afterHandoff.Works.[f.WorkId].ClaimGeneration + 1
        let nextTree = sprintf "wq03-%s-%s-g%d" f.WorkId nextLaunch nextGeneration
        git f.B.RepositoryPath ["fetch"; "--no-tags"; "origin"; continuationRef] |> ignore
        let nextPath = Path.Combine(f.Root, "successor", nextTree)
        Directory.CreateDirectory(Path.GetDirectoryName nextPath) |> ignore
        git f.B.RepositoryPath ["worktree"; "add"; "--detach"; nextPath; continuationSha] |> ignore
        let nextSource = { f.B with RepositoryPath = nextPath }
        let nextScope =
            { LaunchId = nextLaunch; ExpectedRevision = 0; ExpectedWorkId = f.WorkId
              ReadProjectIds = set [f.A.ProjectId.ToString("D")]
              Families = set [ReservationOperationFamily.ContextRead;
                              ReservationOperationFamily.Implementation]
              SourceWorktree = Some nextTree; ExpiresAtUnixMs = 5000L
              Revoked = false; OperatorApprovalId = "approved-successor" }
        WorkQueueReservationPermissions.reviseWithSourceAt f.B (Some nextSource)
            ("operator:" + String.replicate 64 "a") (Guid.NewGuid()) 190L nextScope
            |> ok |> ignore
        let nextBearer = nextCredential.RevealForPrivateDelivery()
        let _, unregistered = WorkQueueGitLedger.load f.A |> ok
        WorkQueueGitLedger.executeCredentialFixtureAt f.A "credential-fixture" (Guid.NewGuid())
            unregistered.Version 191L nextLaunch nextBearer CredentialWorkAction.FirstConnect
            |> ok |> ignore
        let _, ready = WorkQueueGitLedger.load f.B |> ok
        let nextOwner = ready.Launches.[nextLaunch]
        let nextRequest =
            { LaunchId = nextLaunch; TargetProjectId = f.A.ProjectId.ToString("D")
              TargetWorkId = f.WorkId; Family = ReservationOperationFamily.Implementation
              SourceRef = successorLineage.ReachableRef
              SourceSha = successorLineage.ContinuationSha
              TargetRef = Some successorLineage.TargetRef
              SourceRepositoryId = Some successorLineage.RepositoryId
              SourceClaimGeneration = Some nextGeneration; SourceWorktree = Some nextTree
              ExpectedRevision = 1; ExpectedLaunchVersion = nextOwner.Version
              ExpectedLeaseGeneration = nextOwner.Lease.Value.Generation }
        WorkQueueReservationPermissions.checkSourceAt f.A nextSource 192L nextBearer
            nextRequest |> ok |> ignore
        WorkQueueReservationPermissions.claimSourceFixtureAt nextSource
            "credential-fixture" (Guid.NewGuid()) ready.Version 193L
            nextBearer nextRequest 1 nextGeneration |> ok |> ignore
        let _, claimed = WorkQueueGitLedger.load f.A |> ok
        Assert.That(claimed.Works.[f.WorkId].State, Is.EqualTo(WorkState.Claimed nextLaunch))
        Assert.That(original.ClaimGeneration, Is.EqualTo 1))
