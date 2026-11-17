module Client.WorkQueueClaimBoundDirtySourcePolicy

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Common
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueReservationPermissions
open Client.SourceEffectJournal
open Client.SourceEffectRouteBridge

// WQ11: diagnostic, default-off, post-effect verifier. It never executes a
// source mutation, lifts a WQ03 claim, or changes the WQ02 project Git CAS.
// In particular, this is NOT a public source permission adapter.
[<RequireQualifiedAccess>]
type ClaimedDirtySourceOperation =
    | CompletedAwaitedFileEdit of originalCommand: AgentCommand
    | TestCompletion
    | ReadOnlyCheckpoint

type ClaimedDirtySourceEvidence =
    { Operation: ClaimedDirtySourceOperation
      OriginalEffectId: Guid
      RequestSha256: string }

type ClaimedDirtySourceReceipt =
    { LaunchId: string
      WorkId: string
      ClaimGeneration: int
      GrantRevision: int
      SourceSha: string
      OriginalEffectId: Guid
      Proof: string }

[<RequireQualifiedAccess>]
type ClaimedDirtySourceDecision =
    | Disabled
    | Rejected of string
    | HostProofUnavailable of string
    | EvidenceVerifiedForFixture of ClaimedDirtySourceReceipt

module WorkQueueClaimBoundDirtySourcePolicy =
    let private denied reason = Error("ClaimBoundSourceRejected: " + reason)
    let private missing reason = Error("SourceHostProofUnavailable: " + reason)
    let private nonblank value = not (String.IsNullOrWhiteSpace value)
    let private pathComparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
        else StringComparison.Ordinal
    let private fullPath path =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
    let private samePath a b =
        String.Equals(fullPath a, fullPath b, pathComparison)
    let private hex64 (value: string) =
        nonblank value && Regex.IsMatch(value, "^[a-f0-9]{64}$")
    let private gitSha (value: string) =
        nonblank value && Regex.IsMatch(value, "^([a-f0-9]{40}|[a-f0-9]{64})$")
    let private token (value: string) =
        nonblank value && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$")

    // Avoid taking a path through a symlink (including a parent of the selected
    // checkout). Git's toplevel and name check alone would not detect it.
    let private symlinkInAncestors path =
        let rec scan (directory: DirectoryInfo) =
            if isNull directory then false
            elif not (isNull directory.LinkTarget) then true
            else scan directory.Parent
        scan (DirectoryInfo(fullPath path))

    let private checkedRelativeFile root path = result {
        if not (nonblank path)
           || Path.IsPathRooted path
           || path.Contains('\\')
           || path.Contains(':')
           || (path.Split('/') |> Array.exists (fun x -> x = "." || x = ".." || x = "")) then
            return! denied "Source edit path is not a strict checkout-relative file."
        let selected = fullPath(Path.Combine(root, path))
        if not (selected.StartsWith(fullPath root + string Path.DirectorySeparatorChar,
                                    pathComparison)) then
            return! denied "Source edit path escapes the registered checkout."
        let rec noLinks current =
            if samePath current root then true
            else
                let info = FileInfo(current)
                if not (isNull info.LinkTarget) then false
                else
                    let parent = Path.GetDirectoryName(current)
                    nonblank parent && noLinks parent
        if not (noLinks selected) then
            return! denied "A source edit path traverses a symlink."
        return ()
    }

    let private git (directory: string) (args: string list) = result {
        try
            use command = new Process()
            command.StartInfo.FileName <- "git"
            command.StartInfo.WorkingDirectory <- directory
            command.StartInfo.UseShellExecute <- false
            command.StartInfo.RedirectStandardOutput <- true
            command.StartInfo.RedirectStandardError <- true
            command.StartInfo.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
            for key in [ "GIT_DIR"; "GIT_COMMON_DIR"; "GIT_WORK_TREE";
                         "GIT_OBJECT_DIRECTORY"; "GIT_ALTERNATE_OBJECT_DIRECTORIES";
                         "GIT_INDEX_FILE" ] do
                command.StartInfo.Environment.Remove(key) |> ignore
            for arg in args do command.StartInfo.ArgumentList.Add arg
            if not (command.Start()) then return! missing "Cannot start physical Git verifier."
            let output = command.StandardOutput.ReadToEndAsync()
            let errors = command.StandardError.ReadToEndAsync()
            if not (command.WaitForExit(20000)) then
                command.Kill(true)
                command.WaitForExit()
                return! missing "Physical Git verification timed out."
            errors.GetAwaiter().GetResult() |> ignore
            if command.ExitCode <> 0 then
                return! missing "Physical Git verification failed."
            return output.GetAwaiter().GetResult().Trim()
        with _ ->
            return! missing "Physical Git verification unavailable."
    }

    // Reverify the original registered linked checkout WITHOUT repeating
    // WQ03's clean-status check. A dirty worktree is expected after editing,
    // but a moved HEAD, foreign tree or changed remote source is never valid.
    let private verifyPhysical (ledger: LedgerBinding) (source: LedgerBinding)
        (grant: ReservationScopeGrant) (witness: SourceCheckoutGitIdentityWitness)
        (lineage: SourceLineage) (work: WorkStage) (launch: Launch) = result {
        let! grantPath =
            match grant.SourceCheckoutPath, grant.SourceWorktree, grant.SourceSha,
                  grant.SourceRepositoryId, grant.SourceClaimGeneration with
            | Some path, Some tree, Some sha, Some repository, Some generation
                when token tree && gitSha sha
                     && repository = WorkQueueGitLedger.sourceRepositoryId ledger
                     && generation = work.ClaimGeneration
                     && tree = sprintf "wq03-%s-%s-g%d" work.Id launch.Id generation ->
                Ok path
            | _ -> denied "Missing original clean-at-claim generation-bound grant."
        let root = fullPath source.RepositoryPath
        if not (Path.IsPathFullyQualified source.RepositoryPath)
           || not (samePath root grantPath)
           || Path.GetFileName(root) <> grant.SourceWorktree.Value
           || not (Directory.Exists root)
           || symlinkInAncestors root
           || source.ProjectId <> ledger.ProjectId
           || source.LedgerId <> ledger.LedgerId
           || source.Epoch <> ledger.Epoch
           || source.RemoteName <> ledger.RemoteName
           || source.ApprovedFetchUrl <> ledger.ApprovedFetchUrl
           || source.ApprovedPushUrl <> ledger.ApprovedPushUrl then
            return! denied "Physical source checkout is not the approved claim-bound directory."
        let! identity = LedgerStore.sourceRepositoryIdentity source
                        |> Result.mapError (fun _ ->
                            "SourceHostProofUnavailable: source repository identity unavailable.")
        if identity.FetchUrl <> ledger.ApprovedFetchUrl
           || identity.PushUrl <> ledger.ApprovedPushUrl then
            return! denied "Registered checkout uses a different source remote."
        let! top = git root ["rev-parse"; "--show-toplevel"]
        let! actualHead = git root ["rev-parse"; "HEAD"]
        let! headName = git root ["rev-parse"; "--abbrev-ref"; "HEAD"]
        if headName <> "HEAD" then return! denied "Source checkout is no longer detached."
        if not (samePath top root) || actualHead <> grant.SourceSha.Value then
            return! denied "Physical source HEAD is not its original claim SHA."
        // A same-path checkout from a second clone of the same approved
        // remote has the same HEAD, basename and remote-derived repository ID.
        // It must not inherit the *removed* checkout's awaited edit receipts.
        let! currentGitIdentity =
            WorkQueueReservationPermissions.physicalSourceGitIdentity root
            |> Result.mapError (fun _ ->
                "SourceHostProofUnavailable: physical Git administration/object-store identity cannot be verified.")
        let expectedGitIdentity = witness.Identity
        if not (samePath expectedGitIdentity.GitAdministrationDirectory
                         currentGitIdentity.GitAdministrationDirectory)
           || not (samePath expectedGitIdentity.CommonGitDirectory
                            currentGitIdentity.CommonGitDirectory)
           || not (samePath expectedGitIdentity.GitObjectStoreDirectory
                            currentGitIdentity.GitObjectStoreDirectory) then
            return! denied "Original Git administration/object-store identity differs from the immutable claim-bound grant."
        // Recreating a linked checkout from its original clone can reuse all
        // three Git paths and HEAD. Its removed host marker cannot survive
        // worktree removal; never credit the old FileEdit journal to the new
        // physical checkout, even though its Git paths are equal.
        let! currentInstanceSha =
            WorkQueueReservationPermissions.sourceCheckoutInstanceDigest false currentGitIdentity
            |> Result.mapError (fun _ ->
                "SourceHostProofUnavailable: original host-owned checkout instance marker missing or invalid.")
        if not (hex64 witness.CheckoutInstanceSha256)
           || currentInstanceSha <> witness.CheckoutInstanceSha256 then
            return! denied "Host-owned checkout instance differs from original claim-bound grant."
        let! trees = git root ["worktree"; "list"; "--porcelain"]
        let registered =
            trees.Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun block ->
                let lines = block.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                lines |> Array.contains ("worktree " + root)
                && lines |> Array.contains ("HEAD " + actualHead)
                && lines |> Array.contains "detached"
                && not (lines |> Array.exists (fun line ->
                    line.StartsWith("prunable ", StringComparison.Ordinal))))
        if not registered then return! denied "Physical source checkout is not registered at this claim generation."

        // Approved source ref is not the integration target. Even if a caller
        // presents the exact grant, recheck current approved remote provenance.
        let sourceRef = lineage.ReachableRef |> Option.defaultValue lineage.TargetRef
        let expectedSourceSha =
            lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit
        if not (sourceRef.StartsWith("refs/heads/", StringComparison.Ordinal))
           || not (gitSha expectedSourceSha)
           || not (gitSha lineage.PinnedBaseCommit)
           || lineage.RepositoryId <> WorkQueueGitLedger.sourceRepositoryId source then
            return! denied "Original source lineage malformed or changed."
        let! remote = git root ["ls-remote"; "--refs"; ledger.ApprovedFetchUrl; sourceRef]
        let lines = remote.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
        let remoteTip =
            match lines with
            | [| line |] ->
                match line.Split('\t') with
                | [| sha; exactRef |] when exactRef = sourceRef && gitSha sha -> Some sha
                | _ -> None
            | _ -> None
        match remoteTip with
        | None -> return! missing "Approved remote source ref cannot be independently verified."
        | Some tip ->
            if lineage.ReachableRef.IsSome then
                if lineage.ContinuationSha <> Some tip then
                    return! denied "Approved source continuation ref has moved."
            else
                // The original base may be an ancestor of an advancing target.
                let! _ = git root ["fetch"; "--no-tags"; "--no-write-fetch-head";
                                   ledger.ApprovedFetchUrl; lineage.TargetRef]
                let! _ = git root ["merge-base"; "--is-ancestor"; lineage.PinnedBaseCommit; tip]
                ()
        return ()
    }

    let private validate (ledger: LedgerBinding) (source: LedgerBinding)
        snapshot (project: Project) (now: int64) bearer
        (request: ReservationPermissionRequest) = result {
        let! grant =
            WorkQueueReservationPermissions.readScopeAt ledger snapshot request.LaunchId
            |> Result.mapError (fun _ -> "SourceHostProofUnavailable: canonical grant cannot be read.")
            |> Result.bind (function
                | Some (value, _) when value.SchemaVersion = 2 -> Ok value
                | _ -> denied "No original claim-bound grant.")
        let! originalGrant =
            LedgerStore.readAt ledger snapshot
                (WorkQueueReservationPermissions.revisionPath grant.LaunchId grant.Revision)
            |> Result.mapError (fun _ -> "SourceHostProofUnavailable: immutable grant history unreadable.")
        match originalGrant with
        | Some raw, Some _ when raw = CanonicalCodec.encode grant -> ()
        | _ -> return! denied "Current grant has no matching original immutable revision."
        let! originalGitIdentity =
            WorkQueueReservationPermissions.readSourceIdentityAt ledger snapshot
                grant.LaunchId grant.Revision
            |> Result.mapError (fun _ ->
                "SourceHostProofUnavailable: original physical Git identity revision cannot be read.")
            |> Result.bind (function
                | Some (witness, _) when witness.SchemaVersion = 2
                                          && witness.LaunchId = grant.LaunchId
                                          && witness.Revision = grant.Revision
                                          && witness.GrantSha256 =
                                              CanonicalCodec.digest(CanonicalCodec.encode grant) ->
                    Ok witness
                | _ -> missing "No immutable original Git administration/object-store identity matching the grant.")
        // The original clean claim has one immutable checkout instance.
        // A later grant revision cannot establish a different instance
        // under the same claim generation and reuse an older edit receipt.
        let! _claimInstance =
            match grant.SourceClaimGeneration with
            | Some generation when generation > 0 ->
                WorkQueueReservationPermissions.readSourceClaimInstanceAt ledger snapshot
                    grant.LaunchId generation
                |> Result.mapError (fun _ ->
                    "SourceHostProofUnavailable: original checkout claim instance anchor unreadable.")
                |> Result.bind (function
                    | Some (claim, _) when claim.SchemaVersion = 1
                                            && claim.ProjectId = ledger.ProjectId.ToString("D")
                                            && claim.WorkId = grant.WorkId
                                            && claim.LaunchId = grant.LaunchId
                                            && claim.ClaimGeneration = generation
                                            && Some claim.CheckoutPath = grant.SourceCheckoutPath
                                            && claim.Identity = originalGitIdentity.Identity
                                            && claim.CheckoutInstanceSha256 =
                                                originalGitIdentity.CheckoutInstanceSha256 -> Ok claim
                    | _ -> missing "No immutable original checkout claim instance matching this grant.")
            | _ -> denied "Original source claim generation missing."
        let! credential =
            WorkQueueGitLedger.readCredentialAt ledger snapshot request.LaunchId
            |> Result.mapError (fun _ -> "SourceHostProofUnavailable: original credential unreadable.")
            |> Result.bind (function
                | Some (value, _) when value.SchemaVersion = 1 -> Ok value
                | _ -> denied "Original credential not issued.")
        let! admission =
            WorkQueueGitLedger.readAdmissionAt ledger snapshot request.LaunchId
            |> Result.mapError (fun _ -> "SourceHostProofUnavailable: original admission unreadable.")
            |> Result.bind (function
                | Some (value, _) when value.SchemaVersion = 1
                                       && value.Kind = "AdmissionWon"
                                       && value.AbortOperationId = "" -> Ok value
                | _ -> denied "Original credentialed admission missing or aborted.")
        let! launch =
            match project.Launches.TryFind request.LaunchId with
            | Some l -> Ok l
            | None -> denied "Original launch is missing."
        let! work =
            match project.Works.TryFind grant.WorkId with
            | Some w -> Ok w
            | None -> denied "Original claimed Work is missing."
        let! lineage =
            match work.SourceLineage with
            | Some l -> Ok l
            | None -> denied "Original Work source lineage missing."
        let! lease =
            match launch.State, launch.Lease with
            | LaunchState.Registered(id, marker), Some lease
                when id = launch.Id
                     && marker = "work-credential-v1:" + launch.Id
                     && not lease.StopRequested -> Ok lease
            | _ -> denied "Launch is stopped, unregistered or has no live lease."
        // WQ02 issues CSPRNG UUIDv4 bearers while preserving original
        // Base64URL credentials by their immutable SHA-256 verifier. Reuse
        // its canonical format gate so accepted launches stay compatible.
        if not (WorkCredentialFormat.isSupportedBearer bearer)
           || not (hex64 credential.SecretSha256) then
            return! denied "Credential is invalid."
        let presented = SHA256.HashData(Encoding.UTF8.GetBytes bearer)
        if not (CryptographicOperations.FixedTimeEquals(
                    presented, Convert.FromHexString credential.SecretSha256)) then
            return! denied "Credential does not belong to the original launch."
        let projectId = ledger.ProjectId.ToString("D")
        if project.Migration <> MigrationEpoch.Frozen
           || grant.ProjectId <> projectId
           || request.TargetProjectId <> projectId
           || request.TargetWorkId <> grant.WorkId
           || request.LaunchId <> grant.LaunchId
           || grant.AccountTokenId <> launch.AccountTokenId
           || credential.ProjectId <> grant.ProjectId
           || credential.LaunchId <> launch.Id
           || credential.AccountTokenId <> launch.AccountTokenId
           || admission.ProjectId <> projectId
           || admission.LaunchId <> launch.Id
           || admission.AccountTokenId <> launch.AccountTokenId
           || launch.ProjectId <> projectId
           || launch.PrimaryWorkId <> Some work.Id
           || not launch.EverClaimed
           || work.ProjectId <> projectId
           || work.State <> WorkState.Claimed launch.Id
           || work.Kind <> WorkKind.Implementation
           || work.StageGeneration <> grant.WorkStageGeneration
           || work.ClaimGeneration <= 0
           || work.Domain <> DomainState.Undecided
           || work.Gate <> GateState.Clear
           || work.Push.IsSome
           || work.IntegrationAuthorized
           || Some lineage.RepositoryId <> grant.SourceRepositoryId
           || Some lineage.Generation <> grant.SourceLineageGeneration
           || lineage.ReachableRef <> grant.SourceRef
           || Some(lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit) <> grant.SourceSha
           || Some lineage.TargetRef <> grant.TargetRef
           || grant.TargetRef = grant.SourceRef
           || grant.SourceClaimGeneration <> Some work.ClaimGeneration
           || grant.LaunchVersionAtApproval > launch.Version
           || request.ExpectedLaunchVersion <> launch.Version
           || request.ExpectedLeaseGeneration <> lease.Generation
           || request.ExpectedRevision <> grant.Revision
           || request.Family <> ReservationOperationFamily.Implementation
           || not (grant.Families.Contains ReservationOperationFamily.Implementation)
           || not (grant.Families.Contains ReservationOperationFamily.ContextRead)
           || not (grant.ReadProjectIds.Contains projectId)
           || grant.Revoked
           || now < grant.ApprovedAtUnixMs || now >= grant.ExpiresAtUnixMs
           || request.SourceRef <> grant.SourceRef
           || request.SourceSha <> grant.SourceSha
           || request.TargetRef <> grant.TargetRef
           || request.SourceRepositoryId <> grant.SourceRepositoryId
           || request.SourceClaimGeneration <> grant.SourceClaimGeneration
           || request.SourceWorktree <> grant.SourceWorktree then
            return! denied "Original claim, scoped grant, generation or source ancestry differs."
        do! verifyPhysical ledger source grant originalGitIdentity lineage work launch
        return grant, work, launch, lineage
    }

    // Positive result is ONLY a verified, historic file-edit observation.
    // Tests/managed jobs cannot be made terminal by SourceEffectJournal today.
    // No return value here permits invoking a source tool or publishing a WQ02
    // checkpoint: those are separate, unavailable host/winning-CAS boundaries.
    let internal inspectDisposable enabled (ledger: LedgerBinding)
        (source: LedgerBinding) (trustedProjectName: string)
        (store: SourceEffectStore option) (now: int64) bearer
        (permission: ReservationPermissionRequest)
        (evidence: ClaimedDirtySourceEvidence) =
        if not enabled then ClaimedDirtySourceDecision.Disabled
        elif evidence.Operation = ClaimedDirtySourceOperation.TestCompletion then
            ClaimedDirtySourceDecision.HostProofUnavailable
                "No authenticated process-tree/test terminal attestation; returned command output is not a receipt."
        elif evidence.OriginalEffectId = Guid.Empty || not (hex64 evidence.RequestSha256)
             || not (token trustedProjectName) then
            ClaimedDirtySourceDecision.Rejected "Original effect identity, request digest or trusted project is invalid."
        else
            let inspection = result {
                let! snapshot, project =
                    WorkQueueGitLedger.load ledger
                    |> Result.mapError (fun _ -> "SourceHostProofUnavailable: project Git CAS not verified.")
                let! grant, work, launch, lineage =
                    validate ledger source snapshot project now bearer permission
                match evidence.Operation with
                | ClaimedDirtySourceOperation.CompletedAwaitedFileEdit originalCommand ->
                    let! filePath =
                        match SourceEffectRouteBridge.classify originalCommand,
                              AgentCommandInfo.projectName originalCommand
                              |> Option.bind ProjectSelector.parse with
                        | JournalCandidate(_, FileEdit, true), Some (projectName, Some checkout)
                            when projectName = trustedProjectName
                                 && Some checkout = grant.SourceWorktree ->
                            match originalCommand with
                            | WriteFileCommand payload -> Ok payload.FilePath
                            | PatchFileCommand payload -> Ok payload.FilePath
                            | _ -> denied "Original awaited file-edit route is not bound."
                        | _ -> denied "Original file edit route and registered checkout differ."
                    // Check the physical destination before consulting the
                    // historical effect. The effect's request hash MUST commit
                    // to this exact path and the full original source command.
                    do! checkedRelativeFile source.RepositoryPath filePath
                    if SourceEffectRouteBridge.requestSha256 originalCommand <>
                       evidence.RequestSha256 then
                        return! denied "Original source command/path digest differs from the journaled request."
                | ClaimedDirtySourceOperation.ReadOnlyCheckpoint -> ()
                | ClaimedDirtySourceOperation.TestCompletion ->
                    return! missing "Test completion requires a genuine host terminal attestation."
                let! journal =
                    match store with
                    | Some trusted -> Ok trusted
                    | None -> missing "Host-owned, continuity-anchored effect journal is missing."
                let! launchId =
                    match Guid.TryParse launch.Id with
                    | true, id when id <> Guid.Empty -> Ok id
                    | _ -> denied "Original launch UUID is malformed."
                // The journal scope uses the original WQ03 target-route binding;
                // the *source* continuation stays independently in the grant.
                let scope =
                    SourceEffectJournal.fromTrustedLedgerBinding ledger trustedProjectName
                        lineage.RepositoryId lineage.TargetRef grant.SourceWorktree.Value
                        work.Id launchId work.ClaimGeneration
                let report =
                    SourceEffectJournal.inspectForHandoff journal scope [evidence.OriginalEffectId]
                if report.MissingOriginalOperations <> []
                   || report.Records.Length <> 1 then
                    return! missing "Original host effect missing, unbound or anchor continuity failed."
                let receipt = report.Records.Head
                let! proof =
                    match receipt.Kind, receipt.State with
                    | FileEdit, Terminal proof
                        when proof = "host-awaited-file-edit-returned-v1"
                             && receipt.OriginalOperationId = evidence.OriginalEffectId
                             && receipt.RequestSha256 = evidence.RequestSha256 ->
                        Ok proof
                    | _ -> missing "Awaited host file edit is absent, uncertain or lacks exact request proof."
                return
                    { LaunchId = launch.Id; WorkId = work.Id
                      ClaimGeneration = work.ClaimGeneration; GrantRevision = grant.Revision
                      SourceSha = grant.SourceSha.Value
                      OriginalEffectId = evidence.OriginalEffectId; Proof = proof }
            }
            match inspection with
            | Ok receipt -> ClaimedDirtySourceDecision.EvidenceVerifiedForFixture receipt
            | Error reason when reason.StartsWith("SourceHostProofUnavailable:", StringComparison.Ordinal) ->
                ClaimedDirtySourceDecision.HostProofUnavailable reason
            | Error reason -> ClaimedDirtySourceDecision.Rejected reason
