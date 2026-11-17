module Client.WorkQueueNativeLifecycleCas

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueCrossProjectPlanner
open Client.AccountAdmissionAdapter
open Client.AccountCapacityLedger
open Client.OriginalBrowserJournal
open Client.WorkQueueToolFacade
open FsToolkit.ErrorHandling

// Disposable, explicitly injected WQ02 claim/checkpoint/terminal port. This
// is NOT registered with the authenticated MCP tool route, scheduler, or a
// production provider. Session identity and terminal evidence must come from a
// trusted host-side reader, never a tool argument or persistent Agent label.
type NativeAuthenticatedSession =
    { AgentUuid: string
      SessionKey: string
      LeaseGeneration: int
      OriginalOperationId: string }

type INativeWorkSessionJournal =
    abstract member ReadAuthenticatedSession: launchId: string -> Result<NativeAuthenticatedSession, string>
    abstract member VerifyTerminalEvidence:
        launchId: string * authenticatedSession: NativeAuthenticatedSession * evidenceSha256: string
            -> Result<unit, string>

type NativeWorkAction =
    | Claim of resultingClaimGeneration: int
    | Checkpoint of claimGeneration: int * summarySha256: string
    | Complete of claimGeneration: int * terminalEvidenceSha256: string

// This nominal scope cannot be constructed or altered by a public Work call.
// Its test-only issuer verifies fresh scratch remotes before either ledger exists.
[<Sealed; AllowNullLiteral>]
type NativeDisposableAuthority private (project: LedgerBinding, account: AccountBinding, nonce: string) =
    member internal _.Matches(candidateProject: LedgerBinding, candidateAccount: AccountBinding) =
        let same (a: string) (b: string) =
            String.Equals(a, b, StringComparison.Ordinal)
        let matchesProject =
            project.ProjectId = candidateProject.ProjectId &&
            project.LedgerId = candidateProject.LedgerId &&
            project.Epoch = candidateProject.Epoch &&
            same project.RemoteName candidateProject.RemoteName &&
            same project.ApprovedFetchUrl candidateProject.ApprovedFetchUrl &&
            same project.ApprovedPushUrl candidateProject.ApprovedPushUrl
        let matchesAccount =
            account.AccountId = candidateAccount.AccountId &&
            account.LedgerId = candidateAccount.LedgerId &&
            account.Epoch = candidateAccount.Epoch &&
            same account.RemoteName candidateAccount.RemoteName &&
            same account.ApprovedFetchUrl candidateAccount.ApprovedFetchUrl &&
            same account.ApprovedPushUrl candidateAccount.ApprovedPushUrl
        let marked (remote: string) =
            try
                isNull (DirectoryInfo(remote).LinkTarget) &&
                same (File.ReadAllText(Path.Combine(remote, "info", "jarvis-native-disposable-authority"))) nonce
            with _ -> false
        matchesProject && matchesAccount &&
        marked project.ApprovedPushUrl && marked account.ApprovedPushUrl

    // The guarded issuer is part of the sealed type, not a free-standing
    // constructor that another production call site could invoke directly.
    static member internal AuthorizeFreshScratch(project: LedgerBinding, account: AccountBinding)
            : Result<NativeDisposableAuthority, LedgerFailure> =
        let reject text = InvalidBinding ("WQ-native disposable CAS: " + text)
        let noLedgerRef (bare: string) =
            use gitProbe = new Process()
            gitProbe.StartInfo.FileName <- "git"
            gitProbe.StartInfo.UseShellExecute <- false
            gitProbe.StartInfo.CreateNoWindow <- true
            for arg in ["--git-dir"; bare; "show-ref"; "--verify"; "--quiet";
                        "refs/heads/jarvis-orchestration"] do
                gitProbe.StartInfo.ArgumentList.Add arg
            if not (gitProbe.Start()) then false
            elif not (gitProbe.WaitForExit(10000)) then
                gitProbe.Kill()
                false
            else gitProbe.ExitCode = 1
        try
            let pBare = Path.GetFullPath project.ApprovedPushUrl
            let aBare = Path.GetFullPath account.ApprovedPushUrl
            let root = Path.GetDirectoryName pBare
            let temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            let name = Path.GetFileName root
            let suffix =
                if name.StartsWith("work-native-cas-", StringComparison.Ordinal) then
                    name.Substring("work-native-cas-".Length)
                else ""
            let validNonceFolder =
                match Guid.TryParseExact(suffix, "N") with
                | true, id when id <> Guid.Empty -> true
                | _ -> false
            let valid =
                validNonceFolder &&
                String.Equals(Path.GetDirectoryName root, temp, StringComparison.Ordinal) &&
                String.Equals(pBare, Path.Combine(root, "project.git"), StringComparison.Ordinal) &&
                String.Equals(aBare, Path.Combine(root, "account.git"), StringComparison.Ordinal) &&
                project.ApprovedFetchUrl = project.ApprovedPushUrl &&
                account.ApprovedFetchUrl = account.ApprovedPushUrl &&
                project.RemoteName = "origin" && account.RemoteName = "origin" &&
                project.ProjectId <> Guid.Empty && project.LedgerId <> Guid.Empty &&
                account.AccountId <> Guid.Empty && account.LedgerId <> Guid.Empty &&
                project.Epoch > 0 && account.Epoch > 0 &&
                isNull (DirectoryInfo(root).LinkTarget) &&
                isNull (DirectoryInfo(pBare).LinkTarget) &&
                isNull (DirectoryInfo(aBare).LinkTarget) &&
                Directory.Exists(Path.Combine(pBare, "objects")) &&
                Directory.Exists(Path.Combine(aBare, "objects"))
            if not valid || not (noLedgerRef pBare && noLedgerRef aBare) then
                Error(reject "Scratch authority requires fresh local test remotes; initialized or production ledgers are forbidden.")
            else
                let nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                // CreateNew prevents scope reissuance after fixture setup.
                for remote in [pBare; aBare] do
                    use file = new FileStream(
                        Path.Combine(remote, "info", "jarvis-native-disposable-authority"),
                        FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    use writer = new StreamWriter(file)
                    writer.Write(nonce)
                Ok(NativeDisposableAuthority(project, account, nonce))
        with ex -> Error(reject ("Cannot issue verified scratch authority: " + ex.Message))

type NativeLifecycleBinding =
    { Project: LedgerBinding
      Account: AccountBinding
      DisposableAuthority: NativeDisposableAuthority
      OriginalJournalRoot: string
      Selected: PlannerCandidate
      Admission: AccountAdmissionRequest
      DispatchOperationId: Guid
      FirstConnectOperationId: Guid
      SessionJournal: INativeWorkSessionJournal }

module WorkQueueNativeLifecycleCas =
    let private reject text = InvalidBinding ("WQ-native disposable CAS: " + text)
    let private required test text = if test then Ok () else Error (reject text)

    // Internal fixture entrypoint; only the guarded sealed-type issuer can
    // construct an authority. No production tool route calls this function.
    let internal authorizeFreshScratch project account =
        NativeDisposableAuthority.AuthorizeFreshScratch(project, account)

    let private validSha (value: string) =
        not (isNull value) && value.Length = 64 &&
        (value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
    let private validUuid (value: string) =
        match Guid.TryParse value with
        | true, value when value <> Guid.Empty -> true
        | _ -> false
    let private optional value =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private verifyAt (binding: NativeLifecycleBinding) (actor: string)
                         (operationId: Guid) now (action: NativeWorkAction)
                         (snapshot: LedgerSnapshot) (before: Project) (after: Project) = result {
        let project = binding.Project
        let selected = binding.Selected
        let admission = binding.Admission
        let launchId = selected.LaunchId
        let workId = selected.WorkId

        // WQ02 is Frozen only for this disposable port. A production project
        // remains unclaimable; no caller-selected Work is ever authoritative.
        do! required (before.Migration = MigrationEpoch.Frozen &&
                      before.QueueCovered && before.GraphCovered &&
                      before.MessageCovered && before.SourceCovered)
             "Frozen disposable project must have complete queue/graph/message/source coverage."
        let! roleView = WorkQueueRoleAuthority.loadAt project snapshot
        do! required (roleView.Project = before && roleView.Proof.Head = snapshot.Commit)
             "WQ04 project and role history are not the same verified Git snapshot."
        let! owner =
            match before.Launches.TryFind launchId with
            | Some launch -> Ok launch
            | None -> Error(reject "Original WQ02 admitted launch is absent.")
        let! stage =
            match before.Works.TryFind workId with
            | Some stage -> Ok stage
            | None -> Error(reject "Original WQ04 source Work generation is absent.")
        do! required (stage.Id = workId &&
                      stage.StageGeneration = selected.WorkGeneration &&
                      stage.ProjectId = project.ProjectId.ToString("D") &&
                      stage.Gate = GateState.Clear &&
                      (stage.Dependencies |> Set.forall (fun id ->
                          stage.WaivedDependencies.ContainsKey id || Contracts.isSatisfied before id)) &&
                      owner.Id = launchId &&
                      owner.ProjectId = project.ProjectId.ToString("D") &&
                      owner.RoleId = selected.RoleId &&
                      owner.RoleVersion = selected.RoleVersion &&
                      owner.RoleId = admission.RoleId &&
                      owner.RoleVersion = admission.RoleVersion &&
                      owner.AccountTokenId = admission.AdmissionOperationId.ToString("D") &&
                      owner.EverClaimed = (match action with Claim _ -> false | _ -> true))
             "Wrong stage/claim generation, gated dependencies or foreign launch/role/credit."
        do! required (stage.Kind = WorkKind.Planning)
             "Only Planning Work can use this disposable port; review/integration/source completion needs independent domain evidence."

        // The original WQ04 role/catalog and immutable source must exist at
        // this head. Current subscriptions are not authority by themselves.
        let! original = WorkQueueGitLedger.readOriginAt project snapshot workId
        let! origin =
            match original with
            | Some(origin, _) -> Ok origin
            | _ -> Error(reject "WQ04 original source provenance is missing.")
        do! required (origin.SchemaVersion = 1 &&
                      origin.ProjectId = selected.ProjectId &&
                      origin.WorkId = workId &&
                      origin.SourceHead = selected.Source.Head &&
                      origin.SourceOperationId = selected.Source.OperationId &&
                      origin.SourceRequestHash = selected.Source.RequestHash &&
                      optional origin.SourceRef = selected.Source.SourceRef &&
                      optional origin.SourceCommit = selected.Source.SourceCommit &&
                      validUuid origin.SourceOperationId)
             "WQ04 source operation/ref/commit differs from original immutable Work."
        let! sourceOp = LedgerStore.getOperationAt project snapshot (Guid.Parse origin.SourceOperationId)
        do! required (sourceOp |> Option.exists (fun op ->
                          op.OperationId = origin.SourceOperationId &&
                          op.RequestHash = origin.SourceRequestHash &&
                          op.Actor = origin.SourceOperationActor &&
                          op.Type = origin.SourceOperationKind))
             "WQ04 original operation receipt or source request hash cannot be verified."

        // The accepted project admission selected exactly this Work and role
        // at its historical issuing commit, even after credit was consumed.
        let! admissionRecord = WorkQueueGitLedger.readAdmissionAt project snapshot launchId
        do! required (admissionRecord |> Option.exists (fun (value, _) ->
                          value.Kind = "AdmissionWon" &&
                          value.ProjectId = selected.ProjectId &&
                          value.AdmissionOperationId = admission.AdmissionOperationId.ToString("D") &&
                          value.AccountTokenId = owner.AccountTokenId &&
                          value.LaunchId = launchId))
             "WQ02 immutable admission witness is missing or foreign."
        let! admissionOp =
            LedgerStore.getOperationAt project snapshot admission.AdmissionOperationId
        let! admitted =
            match admissionOp with
            | Some op when op.Type = "WQ02.Mutate" && op.Actor = admission.Actor &&
                           op.Paths |> Array.contains (WorkQueueRoleAuthority.roleAdmissionPath launchId) ->
                Ok op
            | _ -> Error(reject "WQ04 original role admission operation/paths mismatch.")
        let! admittedHead = LedgerStore.getOperationOriginAt project snapshot admitted
        let! historical = LedgerStore.verifiedHistoricalSnapshot project admittedHead
        let! historicalProject = WorkQueueGitLedger.loadAt project historical
        do! required (historicalProject.Launches.TryFind launchId |> Option.exists (fun item ->
                          item.Credit = Some workId &&
                          item.AccountTokenId = owner.AccountTokenId &&
                          item.RoleId = owner.RoleId &&
                          item.RoleVersion = owner.RoleVersion))
             "Original WQ02 CAS credited another Work or a different versioned role."

        // Account admission is an independent durable Git authority. Never
        // mistake a previous release/unknown token for free capacity.
        let! capacity =
            AccountCapacityLedger.fetch binding.Account
            |> Result.mapError (fun error -> reject ("WQ07 head unavailable: " + string error))
        let accountAuthority =
            AccountAdmissionAdapter.projectAuthority project
            |> AccountCapacityLedger.projectAuthoritySha256
        let held =
            capacity.State.Tokens |> Array.filter (fun token ->
                token.Status = "Held" &&
                token.LaunchId = launchId &&
                token.ProjectId = project.ProjectId.ToString("D") &&
                token.ProjectLedgerId = project.LedgerId.ToString("D") &&
                token.ProjectLedgerEpoch = project.Epoch &&
                token.ProjectAuthoritySha256 = accountAuthority &&
                token.RoleId = owner.RoleId &&
                token.RoleConfigVersion = int64 owner.RoleVersion &&
                token.AdmissionOperationId = owner.AccountTokenId)
        do! required (held.Length = 1)
             "WQ07 original account credit is missing, released, ambiguous or foreign."

        // A client-local WQ03 journal alone is not an original-send permit.
        // Its host identity is anchored by an immutable Git original claim.
        let! localJournal =
            OriginalBrowserJournal.openExisting binding.OriginalJournalRoot
            |> Result.mapError (fun error -> reject ("WQ03 host journal: " + error))
        let! entry =
            OriginalBrowserJournal.findByLaunch localJournal launchId
            |> Result.mapError (fun error -> reject ("WQ03 original record: " + error))
        do! required (entry |> Option.exists (fun e ->
                          e.Intent.OperationId = selected.OriginalBrowserOperationId &&
                          e.Intent.LaunchId = launchId &&
                          e.Intent.ProjectId = selected.ProjectId &&
                          e.Intent.WorkId = workId &&
                          e.Intent.WorkGeneration = stage.StageGeneration &&
                          e.Intent.RoleId = owner.RoleId &&
                          e.Intent.RoleVersion = owner.RoleVersion &&
                          e.Intent.RequestFingerprint = origin.SourceRequestHash &&
                          e.Intent.Provenance.SourceRef = optional origin.SourceRef &&
                          e.Intent.Provenance.SourceCommit = optional origin.SourceCommit &&
                          (match e.Outcome with Some(OriginalBrowserOutcome.Accepted _) -> true | _ -> false)))
             "WQ03 original effect is absent, unknown, or owned by another launch/source."
        let! trustedHostId =
            OriginalBrowserJournal.trustedHostId localJournal
            |> Result.mapError (fun message -> reject ("WQ03 host identity: " + message))
        // Match the existing original-send claim's exact schema and immutable
        // creating PutRecord, but read them AT this transaction's pinned head.
        // A second trusted local journal cannot authorize another host.
        let hostClaimPath =
            "records/wq11-original-hosts/" + admission.LaunchId.ToString("N") + ".json"
        let expectedHostClaim =
            JsonSerializer.Serialize(
                {| schemaVersion = 1
                   launchId = selected.LaunchId
                   projectId = selected.ProjectId
                   workId = selected.WorkId
                   workGeneration = selected.WorkGeneration
                   roleId = selected.RoleId
                   roleVersion = selected.RoleVersion
                   admissionOperationId = admission.AdmissionOperationId.ToString("D")
                   originalOperationId = selected.OriginalBrowserOperationId
                   sourceRequestHash = selected.Source.RequestHash
                   journalHostId = trustedHostId |})
        let! hostContent, hostOid = LedgerStore.readAt project snapshot hostClaimPath
        let! hostClaimOperation =
            LedgerStore.getOperationAt project snapshot
                (Guid.Parse selected.OriginalBrowserOperationId)
        do! required (hostContent = Some expectedHostClaim &&
                      hostOid.IsSome &&
                      hostClaimOperation |> Option.exists (fun receipt ->
                          receipt.Type = "PutRecord" &&
                          receipt.Path = hostClaimPath &&
                          receipt.BlobOid = hostOid.Value &&
                          receipt.Paths = [|hostClaimPath|]))
             "WQ03 original-send host claim lacks its exact immutable Git creating operation."

        let! session =
            try binding.SessionJournal.ReadAuthenticatedSession launchId
                |> Result.mapError (fun message -> reject ("Authenticated session: " + message))
            with ex -> Error(reject ("Authenticated session read failed: " + ex.Message))
        let! lease =
            match owner.State, owner.Lease with
            | LaunchState.Registered (uuid, key), Some lease when
                not lease.StopRequested &&
                uuid = session.AgentUuid &&
                key = session.SessionKey &&
                lease.Generation = session.LeaseGeneration &&
                session.OriginalOperationId = selected.OriginalBrowserOperationId &&
                lease.LastSeenAt > 0L &&
                now >= lease.LastSeenAt && now - lease.LastSeenAt <= 600000L ->
                Ok lease
            | _ -> Error(reject "Ephemeral session UUID/key, stop flag or fresh lease generation cannot be verified.")
        let _ = lease
        let! storedCredential =
            WorkQueueGitLedger.readCredentialAt project snapshot launchId
        let originalFirstConnect =
            before.Receipts.TryFind(binding.FirstConnectOperationId.ToString("D"))
        // The Work-native WQ02 credential route intentionally has no Agent
        // UUID/session secret. Its first connect uses launch identity and an
        // immutable verifier stored in the same four-path admission Git CAS.
        // Never mistake that for the older legacy Agent initialize receipt.
        let credentialFirstConnect =
            match storedCredential with
            | Some (verifier, _) when
                verifier.SchemaVersion = 1 &&
                verifier.ProjectId = project.ProjectId.ToString("D") &&
                verifier.LaunchId = launchId &&
                verifier.AccountTokenId = admission.AdmissionOperationId.ToString("D") &&
                session.AgentUuid = launchId &&
                session.SessionKey = "work-credential-v1:" + launchId ->
                originalFirstConnect =
                    Some(ProjectOperationPayload.OtherMutation ("initialize-work|" + launchId))
            | _ -> false
        let legacyFirstConnect =
            storedCredential.IsNone &&
            originalFirstConnect =
                Some(ProjectOperationPayload.OtherMutation ("initialize|" + launchId + "|" + session.SessionKey))
        do! required (before.Receipts.TryFind(binding.DispatchOperationId.ToString("D")) =
                          Some(ProjectOperationPayload.OtherMutation ("dispatch|" + launchId + "|Submitted")) &&
                      (credentialFirstConnect || legacyFirstConnect))
             "WQ02 original dispatch/first-connect operation receipts are missing."

        match action with
        | Claim nextGeneration ->
            let! _ = WorkQueueRoleAuthority.inspect roleView (ClaimWork(launchId, workId))
            do! required (stage.State = WorkState.Available &&
                          nextGeneration = stage.ClaimGeneration + 1 &&
                          owner.Credit = Some workId &&
                          not owner.EverClaimed &&
                          after.Launches.TryFind launchId |> Option.exists (fun l ->
                              l.PrimaryWorkId = Some workId && l.EverClaimed && l.Credit.IsNone) &&
                          after.Works.TryFind workId |> Option.exists (fun w ->
                              w.State = WorkState.Claimed launchId &&
                              w.ClaimGeneration = nextGeneration &&
                              w.StageGeneration = stage.StageGeneration))
                 "WQ02 CAS chose a different Work/generation/credit; do not claim."
        | Checkpoint (generation, sha) ->
            do! required (validSha sha && stage.State = WorkState.Claimed launchId &&
                          stage.ClaimGeneration = generation &&
                          after.Works.TryFind workId = Some stage &&
                          after.Launches.TryFind launchId = Some owner)
                 "Checkpoint cannot change ownership or use stale Work/evidence."
        | Complete (generation, sha) ->
            do! required (validSha sha && stage.State = WorkState.Claimed launchId &&
                          stage.ClaimGeneration = generation &&
                          after.Works.TryFind workId |> Option.exists (fun w ->
                              w.State = WorkState.Completed &&
                              w.StageGeneration = stage.StageGeneration &&
                              w.ClaimGeneration = generation) &&
                          after.Launches.TryFind launchId |> Option.exists (fun l ->
                              l.State = LaunchState.TerminalPendingRelease
                                  ("terminal:" + operationId.ToString("D"))))
                 "Completion is not the same claimed Work/generation or terminal witness."
            do!
                try binding.SessionJournal.VerifyTerminalEvidence(launchId, session, sha)
                    |> Result.mapError (fun message -> reject ("WQ03 terminal evidence: " + message))
                with ex -> Error(reject ("WQ03 terminal evidence read failed: " + ex.Message))
        // No WQ07 release, accepted review, integration or domain finalization
        // is implied by the Work completion transition itself.
        return ()
    }

    /// Explicit opt-in disposable adapter. Callers must inject a trusted,
    /// independently authenticated original session/terminal journal; this
    /// API is deliberately absent from the production MCP registration.
    let transitionDisposable (enabled: bool) (binding: NativeLifecycleBinding)
                             (actor: string) (operationId: Guid) expectedProjectVersion
                             (action: NativeWorkAction) : Result<WorkToolTransition, LedgerFailure> = result {
        do! required enabled "No production Work mutation route is installed; disposable opt-in is disabled."
        do! required (not (isNull (box binding)) &&
                      not (isNull binding.DisposableAuthority) &&
                      binding.DisposableAuthority.Matches(binding.Project, binding.Account))
             "No verified fresh scratch authority for this project and account binding; Frozen production Work is immutable."
        do! required (not (isNull (box binding)) &&
                      not (isNull (box binding.SessionJournal)) &&
                      not (String.IsNullOrWhiteSpace binding.OriginalJournalRoot) &&
                      not (isNull (box binding.Selected)) &&
                      not (isNull (box binding.Admission)) &&
                      validUuid binding.Selected.LaunchId &&
                      validUuid binding.Selected.OriginalBrowserOperationId &&
                      binding.Selected.LaunchId = binding.Admission.LaunchId.ToString("D") &&
                      operationId <> Guid.Empty && expectedProjectVersion >= 0 &&
                      not (String.IsNullOrWhiteSpace actor) &&
                      actor.StartsWith("operator:", StringComparison.Ordinal) &&
                      actor.Length = 73 &&
                      validSha (actor.Substring(9)) &&
                      binding.DispatchOperationId <> Guid.Empty &&
                      binding.FirstConnectOperationId <> Guid.Empty &&
                      binding.DispatchOperationId <> binding.FirstConnectOperationId &&
                      operationId <> binding.DispatchOperationId &&
                      operationId <> binding.FirstConnectOperationId)
             "Missing authenticated operator, approved source/launch or unique stable operation identities."
        let selected = binding.Selected
        let mutation =
            match action with
            | Claim nextGeneration ->
                ClaimVerifiedWork(selected.LaunchId, selected.WorkId,
                                  selected.WorkGeneration, nextGeneration)
            | Checkpoint (generation, sha) ->
                CheckpointReadOnly(selected.LaunchId, selected.WorkId, generation, sha)
            | Complete (generation, sha) ->
                CompleteVerifiedReadOnly(selected.LaunchId, selected.WorkId, generation, sha)
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let! written =
            WorkQueueGitLedger.executePinnedDisposableLifecycle binding.Project actor
                operationId expectedProjectVersion now mutation
                (verifyAt binding actor operationId now action)
        let! head, state = WorkQueueGitLedger.load binding.Project
        return { Status = if written.AlreadyApplied then "IdempotentReplay" else "Applied"
                 Applied = true; OperationId = operationId.ToString("D")
                 VerifiedGitHead = head.Commit
                 ProjectVersion = state.Version
                 LaunchId = selected.LaunchId
                 WorkId = selected.WorkId; MissingBinding = "" }
    }
