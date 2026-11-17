module Client.WorkQueueReservationPermissions

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueIntegrationSourceAuthority

// A narrow, opt-in Work launch authorization contract. This is NOT a second
// principal, generic RBAC system or a filesystem sandbox. No public MCP/Agent
// tool is redirected to these fixture-only entrypoints by this module.
[<RequireQualifiedAccess>]
type ReservationOperationFamily =
    | ContextRead
    | Implementation
    | Prerequisite
    | Review
    | Integration
    | SchedulingAdmin

// The immutable revisions live in Git alongside one replaceable, versioned
// current pointer. The credential preimage is deliberately absent.
// Immutable physical Git administration/object-store evidence lives alongside
// its grant revision. Separate storage retains canonical v2 grant compatibility.
type SourceCheckoutGitIdentity =
    { GitAdministrationDirectory: string
      CommonGitDirectory: string
      GitObjectStoreDirectory: string }

type SourceCheckoutGitIdentityWitness =
    { SchemaVersion: int
      LaunchId: string
      Revision: int
      GrantSha256: string
      Identity: SourceCheckoutGitIdentity
      CheckoutInstanceSha256: string }

// One immutable anchor per original Work/launch claim generation. A later
// scope revision cannot bless a replacement checkout under the same claim.
type SourceCheckoutClaimInstanceWitness =
    { SchemaVersion: int
      ProjectId: string
      WorkId: string
      LaunchId: string
      ClaimGeneration: int
      CheckoutPath: string
      Identity: SourceCheckoutGitIdentity
      CheckoutInstanceSha256: string }

type ReservationScopeGrant =
    { SchemaVersion: int
      Revision: int
      ProjectId: string
      LaunchId: string
      AccountTokenId: string
      WorkId: string
      WorkStageGeneration: int
      LaunchVersionAtApproval: int
      ReadProjectIds: Set<string>
      Families: Set<ReservationOperationFamily>
      // WQ03 source continuation is not the approved integration target.
      SourceRef: string option
      SourceSha: string option
      TargetRef: string option
      SourceRepositoryId: string option
      SourceLineageGeneration: int option
      SourceClaimGeneration: int option
      SourceWorktree: string option
      SourceCheckoutPath: string option
      ApprovedAtUnixMs: int64
      ExpiresAtUnixMs: int64
      Revoked: bool
      OperatorActor: string
      OperatorApprovalId: string
      RevisionOperationId: string }

// Only the trusted operator/host bridge may construct this request. In
// particular, no role label, public launch ID or bearer can create a grant.
type ReservationScopeRevision =
    { LaunchId: string
      ExpectedRevision: int
      ExpectedWorkId: string
      ReadProjectIds: Set<string>
      Families: Set<ReservationOperationFamily>
      SourceWorktree: string option
      ExpiresAtUnixMs: int64
      Revoked: bool
      OperatorApprovalId: string }

type ReservationPermissionRequest =
    { LaunchId: string
      TargetProjectId: string
      TargetWorkId: string
      Family: ReservationOperationFamily
      SourceRef: string option
      SourceSha: string option
      TargetRef: string option
      SourceRepositoryId: string option
      SourceClaimGeneration: int option
      SourceWorktree: string option
      ExpectedRevision: int
      ExpectedLaunchVersion: int
      ExpectedLeaseGeneration: int }

// A proposal is NOT a linked, runnable prerequisite. It must be admitted by
// the canonical WQ04 producer and linked by the positive graph/WQ02 writer.
// That writer must atomically invalidate any prior queue eligibility.
[<RequireQualifiedAccess>]
type ReservationBlockerKind =
    | Significant
    | OutsideDelegation

type ReservationPrerequisiteProposal =
    { SchemaVersion: int
      ProjectId: string
      OwningWorkId: string
      LaunchId: string
      GrantRevision: int
      ExpectedGraphVersion: int
      SuggestedWorkId: string
      Kind: ReservationBlockerKind
      EvidenceSha256: string
      Description: string
      RecordedAtUnixMs: int64
      OperationId: string
      Status: string }

module WorkQueueReservationPermissions =
    let private projectId (binding: LedgerBinding) = binding.ProjectId.ToString("D")
    let private hash = CanonicalCodec.digest
    let private hex64 (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[a-f0-9]{64}$")

    let private operatorActor (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^operator:[a-f0-9]{64}$")

    let scopePath launchId =
        "records/work-queue/reservation-scopes/" + hash launchId + ".json"

    let revisionPath launchId revision =
        sprintf "records/work-queue/reservation-scopes/history/%s/%08d.json" (hash launchId) revision

    let internal sourceIdentityPath launchId revision =
        sprintf "records/work-queue/reservation-scopes/source-identity/%s/%08d.json"
            (hash launchId) revision

    let internal sourceClaimInstancePath launchId generation =
        sprintf "records/work-queue/reservation-scopes/claim-instance/%s/%08d.json"
            (hash launchId) generation

    // Source grants are minted from an actual clean, registered WQ03 worktree.
    // Capture the original *physical* Git admin and object-store directories,
    // rather than a remote-derived repository ID or the user-visible pathname.
    // This read does not repeat the claim-time clean-status check and therefore
    // also works for a deliberately dirty Implementation continuation.
    let internal physicalSourceGitIdentity (checkoutPath: string) = result {
        let invalid reason =
            Error(InvalidBinding("WQ11 Git administration proof unavailable: " + reason))
        if String.IsNullOrWhiteSpace checkoutPath
           || not (Path.IsPathFullyQualified checkoutPath) then
            return! invalid "checkout is not a fully qualified host-selected path"
        let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutPath))
        if not (Directory.Exists root) then
            return! invalid "original checkout directory no longer exists"
        let git (args: string list) = result {
            try
                use runner = new Process()
                runner.StartInfo.FileName <- "git"
                runner.StartInfo.WorkingDirectory <- root
                runner.StartInfo.UseShellExecute <- false
                runner.StartInfo.RedirectStandardOutput <- true
                runner.StartInfo.RedirectStandardError <- true
                runner.StartInfo.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
                // Never let inherited Git environment redirects impersonate
                // the physical checkout resolved at this host path.
                for key in [ "GIT_DIR"; "GIT_COMMON_DIR"; "GIT_WORK_TREE";
                             "GIT_OBJECT_DIRECTORY"; "GIT_ALTERNATE_OBJECT_DIRECTORIES";
                             "GIT_INDEX_FILE" ] do
                    runner.StartInfo.Environment.Remove(key) |> ignore
                args |> List.iter (fun arg -> runner.StartInfo.ArgumentList.Add(arg))
                if not (runner.Start()) then
                    return! invalid "cannot launch local Git identity probe"
                let output = runner.StandardOutput.ReadToEndAsync()
                let errors = runner.StandardError.ReadToEndAsync()
                if not (runner.WaitForExit(20000)) then
                    runner.Kill(true)
                    runner.WaitForExit()
                    return! invalid "local Git identity probe timed out"
                errors.GetAwaiter().GetResult() |> ignore
                if runner.ExitCode <> 0 then
                    return! invalid "local Git identity probe failed"
                let value = output.GetAwaiter().GetResult().Trim()
                if String.IsNullOrWhiteSpace value then
                    return! invalid "Git returned an empty identity component"
                return value
            with _ ->
                return! invalid "local Git identity probe failed"
        }
        let! admin = git [ "rev-parse"; "--absolute-git-dir" ]
        let! common = git [ "rev-parse"; "--git-common-dir" ]
        let! objects = git [ "rev-parse"; "--git-path"; "objects" ]
        let absolute (value: string) =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                if Path.IsPathRooted value then value else Path.Combine(root, value)))
        let identity =
            { GitAdministrationDirectory = absolute admin
              CommonGitDirectory = absolute common
              GitObjectStoreDirectory = absolute objects }
        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
            else StringComparison.Ordinal
        let same x y = String.Equals(x, y, comparison)
        let rec hasLink (directory: DirectoryInfo) =
            if isNull directory then false
            elif not directory.Exists || not (isNull directory.LinkTarget) then true
            else hasLink directory.Parent
        let physical =
            [ identity.GitAdministrationDirectory; identity.CommonGitDirectory;
              identity.GitObjectStoreDirectory ]
        if same identity.GitAdministrationDirectory identity.CommonGitDirectory
           || (physical |> List.exists (fun path ->
               not (Directory.Exists path) || hasLink (DirectoryInfo path))) then
            return! invalid "not an isolated physical linked checkout or Git administration path is unavailable"
        return identity
    }

    // The linked worktree's Git administration directory is deleted by Git
    // when the checkout is removed. Git may reuse its *name and full path*
    // when recreating a linked tree from the same clone. A one-time CSPRNG
    // marker in that host-owned directory distinguishes these lifetimes.
    // It is never placed inside the source worktree or written by FileEdit.
    // The original grant CAS stores only its digest; inspections only read.
    let private checkoutInstanceMarker = "jarvis-wq11-checkout-instance.v1"

    let internal sourceCheckoutInstanceDigest create
            (identity: SourceCheckoutGitIdentity) = result {
        let invalid reason =
            Error(InvalidBinding("WQ11 checkout instance proof unavailable: " + reason))
        let admin = identity.GitAdministrationDirectory
        if String.IsNullOrWhiteSpace admin
           || not (Path.IsPathFullyQualified admin)
           || not (Directory.Exists admin) then
            return! invalid "original linked Git administration directory missing"
        let marker = Path.Combine(admin, checkoutInstanceMarker)
        try
            if not (File.Exists marker) then
                if not create then
                    return! invalid "original host-owned checkout instance marker missing"
                // CreateNew never adopts/replaces an existing marker. A
                // failed/racing creation denies this grant instead of silently
                // changing its original physical checkout evidence.
                let bytes = Encoding.ASCII.GetBytes(
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant())
                use output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write,
                                            FileShare.None, 4096, FileOptions.WriteThrough)
                output.Write(bytes, 0, bytes.Length)
                output.Flush(true)
                if not (OperatingSystem.IsWindows()) then
                    File.SetUnixFileMode(marker,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

            let file = FileInfo(marker)
            if not file.Exists || not (isNull file.LinkTarget) || file.Length <> 64L then
                return! invalid "host-owned checkout instance marker missing, linked or malformed"
            if not (OperatingSystem.IsWindows())
               && File.GetUnixFileMode(marker) <>
                    (UnixFileMode.UserRead ||| UnixFileMode.UserWrite) then
                return! invalid "host-owned checkout instance marker is not private"
            let nonce = File.ReadAllText(marker, Encoding.ASCII)
            if not (Regex.IsMatch(nonce, "^[a-f0-9]{64}$")) then
                return! invalid "host-owned checkout instance marker invalid"
            return hash nonce
        with _ ->
            return! invalid "host-owned checkout instance marker unreadable or cannot be provisioned"
    }

    let proposalPath (operationId: Guid) =
        "records/work-queue/prerequisite-proposals/" + hash (operationId.ToString("D")) + ".json"

    let private readCanonical<'T> (binding: LedgerBinding) snapshot path =
        LedgerStore.readAt binding snapshot path
        |> Result.bind (function
            | None, None -> Ok None
            | Some raw, Some blob ->
                try
                    let document = CanonicalCodec.decode<'T> raw
                    if CanonicalCodec.encode document <> raw then
                        Error(LedgerIntegrityError ("Noncanonical Work reservation record: " + path))
                    else Ok(Some(document, blob))
                with _ ->
                    Error(LedgerIntegrityError ("Malformed Work reservation record: " + path))
            | _ -> Error(LedgerIntegrityError ("Partial Work reservation record: " + path)))

    let internal readSourceIdentityAt binding snapshot launchId revision =
        readCanonical<SourceCheckoutGitIdentityWitness> binding snapshot
            (sourceIdentityPath launchId revision)

    let internal readSourceClaimInstanceAt binding snapshot launchId generation =
        readCanonical<SourceCheckoutClaimInstanceWitness> binding snapshot
            (sourceClaimInstancePath launchId generation)

    let readScopeAt binding snapshot launchId =
        readCanonical<ReservationScopeGrant> binding snapshot (scopePath launchId)

    let readProposalAt binding snapshot (operationId: Guid) =
        readCanonical<ReservationPrerequisiteProposal> binding snapshot (proposalPath operationId)

    let private supportedFamilies (stage: WorkStage) =
        let common = set [ReservationOperationFamily.ContextRead; ReservationOperationFamily.Prerequisite]
        match stage.Kind with
        | WorkKind.Planning | WorkKind.Implementation ->
            common.Add ReservationOperationFamily.Implementation
        | WorkKind.FrozenReview ->
            common.Add ReservationOperationFamily.Review
        // IntegrationAuthorized is a post-push flag, not a pre-claim permit.
        // The WQ04/WQ05 same-CAS source witness is checked before this call.
        | WorkKind.Integration when stage.SourceLineage.IsSome ->
            common.Add ReservationOperationFamily.Integration
        | _ -> common

    let private grantedWork (launch: Launch) =
        if launch.EverClaimed then launch.PrimaryWorkId else launch.Credit

    let private requireSome error = function
        | Some value -> Ok value
        | None -> Error error

    // The grant is minted from the canonical WQ02 stage and WQ03-verified local
    // source checkout, never a caller-selected role, worktree name or destination.
    type private PinnedSource =
        { SourceRef: string option
          SourceSha: string
          TargetRef: string
          RepositoryId: string
          LineageGeneration: int
          ClaimGeneration: int
          WorktreeName: string
          CheckoutPath: string
          GitIdentity: SourceCheckoutGitIdentity }

    let private generationForSource (launch: Launch) (stage: WorkStage) =
        match stage.State with
        | WorkState.Available -> Ok(stage.ClaimGeneration + 1)
        | WorkState.Claimed owner when owner = launch.Id && stage.ClaimGeneration > 0 ->
            Ok stage.ClaimGeneration
        | _ ->
            Error(InvalidBinding "Source scope requires available Work or this launch's current claim.")

    // The sourceBinding is a host-selected checkout handle, NOT a user claim.
    // WQ03 enforces the registered linked/detached worktree, exact HEAD, clean
    // status, source remote identity and generation-scoped name. The actual
    // worktree must be checked again by any eventual source-operation adapter.
    let private pinSource (ledger: LedgerBinding) (sourceBinding: LedgerBinding option)
            (launch: Launch) (stage: WorkStage) selectedName = result {
        let! lineage =
            stage.SourceLineage
            |> requireSome (InvalidBinding "Source stage lacks canonical WQ03 lineage.")
        let! claimGeneration = generationForSource launch stage
        let expectedName = sprintf "wq03-%s-%s-g%d" stage.Id launch.Id claimGeneration
        let! checkout =
            sourceBinding
            |> requireSome (InvalidBinding "Source grant needs a host-bound registered checkout.")
        let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkout.RepositoryPath))
        if selectedName <> Some expectedName
           || Path.GetFileName(root) <> expectedName
           || checkout.ProjectId <> ledger.ProjectId
           || checkout.LedgerId <> ledger.LedgerId
           || checkout.Epoch <> ledger.Epoch
           || checkout.ApprovedFetchUrl <> ledger.ApprovedFetchUrl
           || checkout.ApprovedPushUrl <> ledger.ApprovedPushUrl
           || checkout.RemoteName <> ledger.RemoteName
           || lineage.RepositoryId <> WorkQueueGitLedger.sourceRepositoryId ledger
           || lineage.RepositoryId <> WorkQueueGitLedger.sourceRepositoryId checkout
           || (stage.Kind = WorkKind.Integration
               && (lineage.ReachableRef.IsNone || lineage.ContinuationSha.IsNone)) then
            return! Error(InvalidBinding
                "Source grant does not match original Work/launch/claim-generation checkout or continuation.")
        do! WorkQueueGitLedger.verifySourceClaimCheckout checkout launch.Id
                { stage with ClaimGeneration = claimGeneration }
        let! identity = physicalSourceGitIdentity root
        return
            { SourceRef = lineage.ReachableRef
              SourceSha = lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit
              TargetRef = lineage.TargetRef
              RepositoryId = lineage.RepositoryId
              LineageGeneration = lineage.Generation
              ClaimGeneration = claimGeneration
              WorktreeName = expectedName
              CheckoutPath = root
              GitIdentity = identity }
    }

    // An operator-approved revision is independently auditable in the canonical
    // Git ledger. This method must ONLY be invoked by an OAuth-authenticated
    // operator adapter, never directly by an execution credential. The actor is
    // an adapter assertion, not proof by itself; no public route exposes it.
    let internal reviseWithSourceAt (binding: LedgerBinding) (sourceBinding: LedgerBinding option)
            (trustedActor: string) (operationId: Guid) (now: int64)
            (request: ReservationScopeRevision) =
        if not (operatorActor trustedActor)
           || operationId = Guid.Empty
           || String.IsNullOrWhiteSpace request.OperatorApprovalId
           || request.OperatorApprovalId.Length > 160
           || request.ExpectedRevision < 0
           || request.ExpiresAtUnixMs <= now
           || request.ExpiresAtUnixMs - now > 86400000L
           || request.ReadProjectIds.Count > 16
           || (request.ReadProjectIds |> Seq.exists String.IsNullOrWhiteSpace)
           // A canonical WQ04 hashed Work ID plus launch UUID and generation
           // yields a ~130-character WQ03 basename. The exact computed name
           // and physical registered checkout are still checked in pinSource.
           || (request.SourceWorktree |> Option.exists (fun name ->
               not (Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$")))) then
            Error(InvalidBinding "Invalid operator reservation scope revision; obtain a fresh explicit approval.")
        else
            let requestJson = JsonObject()
            requestJson.Add("schemaVersion", JsonValue.Create(1))
            requestJson.Add("approval", JsonValue.Create(request.OperatorApprovalId))
            requestJson.Add("requested", JsonNode.Parse(CanonicalCodec.encode request))
            let intent =
                { OperationId = operationId; Actor = trustedActor; Kind = "WQ11.ScopeRevision"
                  RequestJson = requestJson.ToJsonString() }
            LedgerStore.transact binding intent (fun snapshot -> result {
                let! project = WorkQueueGitLedger.loadAt binding snapshot
                let! credential = WorkQueueGitLedger.readCredentialAt binding snapshot request.LaunchId
                let! admission = WorkQueueGitLedger.readAdmissionAt binding snapshot request.LaunchId
                let! existing = readScopeAt binding snapshot request.LaunchId
                let! (launch: Launch) =
                    project.Launches.TryFind request.LaunchId
                    |> requireSome (InvalidBinding "Reservation scope requires an admitted canonical launch.")
                let! workId =
                    grantedWork launch
                    |> requireSome (InvalidBinding "Reservation scope requires the pinned credit or claim.")
                let! (stage: WorkStage) =
                    project.Works.TryFind workId
                    |> requireSome (InvalidBinding "Reservation scope requires pinned canonical Work.")
                let! verifier =
                    match credential, admission with
                    | Some (value, _), Some (original, _)
                        when value.SchemaVersion = 1
                             && original.SchemaVersion = 1
                             && original.Kind = "AdmissionWon"
                             && original.AbortOperationId = ""
                             && value.ProjectId = projectId binding
                             && original.ProjectId = value.ProjectId
                             && value.LaunchId = launch.Id
                             && original.LaunchId = launch.Id
                             && value.AccountTokenId = launch.AccountTokenId
                             && original.AccountTokenId = launch.AccountTokenId
                             && hex64 value.SecretSha256 -> Ok value
                    | _ -> Error(InvalidBinding "Grant requires the original credentialed reservation admission.")
                if stage.ProjectId <> projectId binding
                   || launch.ProjectId <> stage.ProjectId
                   || request.ExpectedWorkId <> workId
                   || verifier.ProjectId <> stage.ProjectId then
                    return! Error(InvalidBinding "Reservation grant cannot select another Work or project.")
                let existingVersion = existing |> Option.map (fun (record, _) -> record.Revision) |> Option.defaultValue 0
                if existingVersion <> request.ExpectedRevision then
                    return! Error(StaleEntityVersion "Reservation scope revision changed; refresh explicit approval.")
                let! authorizedStage =
                    if stage.Kind = WorkKind.Integration
                       && not request.Revoked
                       && request.Families.Contains ReservationOperationFamily.Integration then
                        WorkQueueIntegrationSourceAuthority.verifiedStageAt binding snapshot stage
                    else Ok stage
                let allowed = supportedFamilies authorizedStage
                if not request.Revoked
                   && (request.Families.IsEmpty || not (Set.isSubset request.Families allowed)
                       || not (request.Families.Contains ReservationOperationFamily.ContextRead)
                       || not (request.ReadProjectIds.Contains (projectId binding))) then
                    return! Error(InvalidBinding "Requested operation family exceeds pinned Work authority.")
                // Source authority is derived inside the winning original Git
                // CAS from the canonical Work and a real WQ03 checkout. The
                // approved target ref is NOT a continuation/source ref.
                let needsSource =
                    not request.Revoked
                    && ((stage.Kind = WorkKind.Implementation
                         && request.Families.Contains ReservationOperationFamily.Implementation)
                        || (stage.Kind = WorkKind.Integration
                            && request.Families.Contains ReservationOperationFamily.Integration))
                let! source =
                    if needsSource then
                        pinSource binding sourceBinding launch authorizedStage request.SourceWorktree
                        |> Result.map Some
                    else
                        if request.SourceWorktree.IsSome then
                            Error(InvalidBinding "Non-source or revoked grant cannot select a checkout.")
                        else Ok None
                let revision = existingVersion + 1
                let record =
                    { SchemaVersion = 2; Revision = revision
                      ProjectId = projectId binding; LaunchId = launch.Id
                      AccountTokenId = launch.AccountTokenId; WorkId = workId
                      WorkStageGeneration = stage.StageGeneration
                      LaunchVersionAtApproval = launch.Version
                      ReadProjectIds = request.ReadProjectIds
                      Families = (if request.Revoked then Set.empty else request.Families)
                      SourceRef = source |> Option.bind (fun x -> x.SourceRef)
                      SourceSha = source |> Option.map (fun x -> x.SourceSha)
                      TargetRef = source |> Option.map (fun x -> x.TargetRef)
                      SourceRepositoryId = source |> Option.map (fun x -> x.RepositoryId)
                      SourceLineageGeneration = source |> Option.map (fun x -> x.LineageGeneration)
                      SourceClaimGeneration = source |> Option.map (fun x -> x.ClaimGeneration)
                      SourceWorktree = source |> Option.map (fun x -> x.WorktreeName)
                      SourceCheckoutPath = source |> Option.map (fun x -> x.CheckoutPath)
                      ApprovedAtUnixMs = now; ExpiresAtUnixMs = request.ExpiresAtUnixMs
                      Revoked = request.Revoked; OperatorActor = trustedActor
                      OperatorApprovalId = request.OperatorApprovalId
                      RevisionOperationId = operationId.ToString("D") }
                // Exact grant bytes are immutable; the companion original
                // filesystem identity is created in the *same* Git CAS.
                // Existing v2 grants remain decodable, but WQ11 dirty-source
                // inspection rejects historical revisions without this witness.
                let! sourceWitness, claimInstanceChange =
                    match source with
                    | Some pinned ->
                      result {
                        let! original =
                            readSourceClaimInstanceAt binding snapshot launch.Id pinned.ClaimGeneration
                        // Never retrofit an original claim witness to a
                        // preexisting scope: that checkout could be a replacement.
                        if original.IsNone && existingVersion <> 0 then
                            return! Error(InvalidBinding
                                "Original checkout claim instance is unanchored; cannot rebind an existing reservation.")
                        let! instanceSha =
                            sourceCheckoutInstanceDigest original.IsNone pinned.GitIdentity
                        let claimWitness =
                            { SchemaVersion = 1; ProjectId = projectId binding
                              WorkId = stage.Id; LaunchId = launch.Id
                              ClaimGeneration = pinned.ClaimGeneration
                              CheckoutPath = pinned.CheckoutPath
                              Identity = pinned.GitIdentity
                              CheckoutInstanceSha256 = instanceSha }
                        let! claimChange =
                            match original with
                            | None ->
                                Ok [ { Path = sourceClaimInstancePath launch.Id pinned.ClaimGeneration
                                       ExpectedBlobOid = None
                                       Content = CanonicalCodec.encode claimWitness } ]
                            | Some (prior, _) when prior = claimWitness -> Ok []
                            | _ -> Error(LedgerIntegrityError
                                "Original checkout claim instance differs; re-claim with a new generation.")
                        return
                            Some { SchemaVersion = 2; LaunchId = launch.Id; Revision = revision
                                   GrantSha256 = CanonicalCodec.digest(CanonicalCodec.encode record)
                                   Identity = pinned.GitIdentity
                                   CheckoutInstanceSha256 = instanceSha }, claimChange
                      }
                    | None -> Ok(None, [])
                let! priorWitness =
                    readSourceIdentityAt binding snapshot launch.Id revision
                if priorWitness.IsSome then
                    return! Error(LedgerIntegrityError
                        "Immutable source Git administration identity already exists.")
                let! older =
                    match existing with
                    | Some (prior, oid) when prior.SchemaVersion = 2
                                              && prior.ProjectId = projectId binding
                                              && prior.LaunchId = launch.Id
                                              && prior.AccountTokenId = launch.AccountTokenId
                                              && prior.Revision = existingVersion -> Ok(Some oid)
                    | None -> Ok None
                    | _ -> Error(LedgerIntegrityError "Previous reservation grant identity changed.")
                let! priorHistory = readCanonical<ReservationScopeGrant> binding snapshot (revisionPath launch.Id revision)
                if priorHistory.IsSome then
                    return! Error(LedgerIntegrityError "Reservation scope revision history already exists.")
                let identityChange =
                    match sourceWitness with
                    | Some witness ->
                        [ { Path = sourceIdentityPath launch.Id revision
                            ExpectedBlobOid = None
                            Content = CanonicalCodec.encode witness } ]
                    | None -> []
                return
                    [ { Path = scopePath launch.Id; ExpectedBlobOid = older
                        Content = CanonicalCodec.encode record }
                      { Path = revisionPath launch.Id revision; ExpectedBlobOid = None
                        Content = CanonicalCodec.encode record } ]
                    @ identityChange @ claimInstanceChange
            })

    let internal reviseAt binding trustedActor operationId now request =
        reviseWithSourceAt binding None trustedActor operationId now request

    let private denied (request: ReservationPermissionRequest) why =
        Error(InvalidBinding (sprintf
            "Reservation %A denied for project %s Work %s: %s. Request an operator-approved scope revision or record a separate linked prerequisite."
            request.Family request.TargetProjectId request.TargetWorkId why))

    // Evaluate at the same VERIFIED Git snapshot used for the actual mutation.
    // Callers MUST derive Family and source targets from the invoked operation,
    // not from self-declared role or model-provided permission claims.
    let internal authorizeWithSourceAt (binding: LedgerBinding) (sourceBinding: LedgerBinding option)
            snapshot (project: Project) (now: int64) (bearer: string)
            (request: ReservationPermissionRequest) = result {
        let! current = readScopeAt binding snapshot request.LaunchId
        let! verifier = WorkQueueGitLedger.readCredentialAt binding snapshot request.LaunchId
        let! grant =
            match current with
            | Some (value, _) when value.SchemaVersion = 2 -> Ok value
            | _ -> denied request "No approved current grant"
        let! credential =
            match verifier with
            | Some (value, _) when value.SchemaVersion = 1 -> Ok value
            | _ -> denied request "Missing canonical launch credential"
        let! (launch: Launch) =
            project.Launches.TryFind request.LaunchId
            |> requireSome (InvalidBinding "Scoped launch is not in the canonical project.")
        let! (stage: WorkStage) =
            project.Works.TryFind grant.WorkId
            |> requireSome (InvalidBinding "Scoped Work is absent from the canonical project.")
        if not (WorkCredentialFormat.isSupportedBearer bearer)
           || not (hex64 credential.SecretSha256) then
            return! denied request "Launch credential invalid"
        let presented = SHA256.HashData(Encoding.UTF8.GetBytes bearer)
        let expected = Convert.FromHexString credential.SecretSha256
        if not (CryptographicOperations.FixedTimeEquals(presented, expected)) then
            return! denied request "Launch credential invalid"
        let! lease =
            match launch.State, launch.Lease with
            | LaunchState.Registered (id, marker), Some value
                when id = launch.Id
                     && marker = "work-credential-v1:" + launch.Id
                     && not value.StopRequested -> Ok value
            | _ -> denied request "Launch is stopped, not registered or terminal"
        if grant.ProjectId <> projectId binding
           || grant.LaunchId <> request.LaunchId
           || grant.AccountTokenId <> launch.AccountTokenId
           || credential.LaunchId <> grant.LaunchId
           || credential.ProjectId <> grant.ProjectId
           || credential.AccountTokenId <> grant.AccountTokenId
           || launch.ProjectId <> grant.ProjectId
           || stage.ProjectId <> grant.ProjectId
           || grant.WorkStageGeneration <> stage.StageGeneration
           || grant.WorkId <> (grantedWork launch |> Option.defaultValue "")
           || grant.LaunchVersionAtApproval > launch.Version then
            return! denied request "Original launch, Work or stage-generation binding changed"
        if grant.Revision <> request.ExpectedRevision
           || grant.Revoked
           || now < grant.ApprovedAtUnixMs
           || now >= grant.ExpiresAtUnixMs then
            return! denied request "Grant is missing, expired, revoked or stale"
        if request.ExpectedLaunchVersion <> launch.Version
           || request.ExpectedLeaseGeneration <> lease.Generation then
            return! denied request "Launch or lease generation changed"
        if not (grant.Families.Contains request.Family) then
            return! denied request "Operation family is not delegated"
        if request.Family = ReservationOperationFamily.ContextRead then
            if not (grant.ReadProjectIds.Contains request.TargetProjectId) then
                return! denied request "Context read project is not delegated"
        else
            if request.TargetProjectId <> grant.ProjectId
               || request.TargetWorkId <> grant.WorkId then
                return! denied request "Mutation is outside the assigned project or Work"
            let sourceOperation =
                (stage.Kind = WorkKind.Implementation
                 && request.Family = ReservationOperationFamily.Implementation)
                || (stage.Kind = WorkKind.Integration
                    && request.Family = ReservationOperationFamily.Integration)
            if sourceOperation then
                let! authorizedStage =
                    if stage.Kind = WorkKind.Integration then
                        WorkQueueIntegrationSourceAuthority.verifiedStageAt binding snapshot stage
                    else Ok stage
                // Recheck the original Work generation and physical WQ03 source
                // checkout at the operation boundary; comparing strings alone
                // would allow a different but valid reservation's worktree.
                let! pinned =
                    pinSource binding sourceBinding launch authorizedStage grant.SourceWorktree
                    |> Result.mapError (fun _ ->
                        InvalidBinding "Source checkout is not WQ03-pinned to this reservation.")
                if grant.SourceRef <> pinned.SourceRef
                   || grant.SourceSha <> Some pinned.SourceSha
                   || grant.TargetRef <> Some pinned.TargetRef
                   || grant.SourceRepositoryId <> Some pinned.RepositoryId
                   || grant.SourceLineageGeneration <> Some pinned.LineageGeneration
                   || grant.SourceClaimGeneration <> Some pinned.ClaimGeneration
                   || grant.SourceWorktree <> Some pinned.WorktreeName
                   || grant.SourceCheckoutPath <> Some pinned.CheckoutPath
                   || request.SourceRef <> pinned.SourceRef
                   || request.SourceSha <> Some pinned.SourceSha
                   || request.TargetRef <> Some pinned.TargetRef
                   || request.SourceRepositoryId <> Some pinned.RepositoryId
                   || request.SourceClaimGeneration <> Some pinned.ClaimGeneration
                   || request.SourceWorktree <> Some pinned.WorktreeName then
                    return! denied request "Source checkout/continuation differs from original WQ03 scope"
            else if request.SourceRef.IsSome || request.SourceSha.IsSome
                    || request.TargetRef.IsSome || request.SourceRepositoryId.IsSome
                    || request.SourceClaimGeneration.IsSome || request.SourceWorktree.IsSome then
                return! denied request "Non-source mutation cannot select source authority"
        return grant
    }

    // Without a host-selected WQ03 checkout, source operations fail closed.
    // Planning grants remain usable on the existing non-source fixture path.
    let authorizeAt binding snapshot project now bearer request =
        authorizeWithSourceAt binding None snapshot project now bearer request

    // Authenticated, bounded read permission can include other projects, but
    // a mutation always requires a credential bound to its owning project.
    let checkAt binding (now: int64) bearer request =
        WorkQueueGitLedger.load binding
        |> Result.bind (fun (snapshot, project) ->
            authorizeAt binding snapshot project now bearer request)

    let internal checkSourceAt binding sourceBinding now bearer request =
        WorkQueueGitLedger.load binding
        |> Result.bind (fun (snapshot, project) ->
            authorizeWithSourceAt binding (Some sourceBinding) snapshot project now bearer request)

    // Claim and read-only checkpoint are the two non-source fixture transitions
    // that already have an independent winning-CAS validation callback in WQ02.
    // No public Agent/Work MCP route can invoke these until live cutover approval.
    let internal claimWithSourceFixtureAt binding (sourceBinding: LedgerBinding option)
            actor (operationId: Guid) expectedVersion now bearer
            (request: ReservationPermissionRequest) expectedStageGeneration resultingClaimGeneration =
        let mutation =
            ClaimVerifiedWork(request.LaunchId, request.TargetWorkId,
                              expectedStageGeneration, resultingClaimGeneration)
        let verify snapshot (before: Project) (after: Project) = result {
            if request.Family <> ReservationOperationFamily.Implementation
               && request.Family <> ReservationOperationFamily.Review
               && request.Family <> ReservationOperationFamily.Integration then
                return! denied request "Claim family does not match delegated Work"
            let! grant = authorizeWithSourceAt binding sourceBinding snapshot before now bearer request
            let! (prior: WorkStage) =
                before.Works.TryFind grant.WorkId
                |> requireSome (InvalidBinding "Claimed Work missing at winning CAS.")
            if prior.StageGeneration <> expectedStageGeneration
               || prior.ClaimGeneration + 1 <> resultingClaimGeneration then
                return! denied request "Stale claimed Work generation"
            let correctFamily =
                match prior.Kind with
                | WorkKind.FrozenReview -> ReservationOperationFamily.Review
                | WorkKind.Integration -> ReservationOperationFamily.Integration
                | _ -> ReservationOperationFamily.Implementation
            if request.Family <> correctFamily then
                return! denied request "Claim operation does not match pinned Work kind"
            match after.Launches.TryFind grant.LaunchId, after.Works.TryFind grant.WorkId with
            | Some launched, Some work
                when launched.PrimaryWorkId = Some grant.WorkId
                     && work.State = WorkState.Claimed grant.LaunchId
                     && work.ClaimGeneration = resultingClaimGeneration -> return ()
            | _ -> return! denied request "Claim did not preserve assigned Work ownership"
        }
        WorkQueueGitLedger.executePinnedDisposableLifecycle binding actor operationId
            expectedVersion now mutation verify

    let internal claimFixtureAt binding actor operationId expectedVersion now
            bearer request expectedStageGeneration resultingClaimGeneration =
        claimWithSourceFixtureAt binding None actor operationId expectedVersion now
            bearer request expectedStageGeneration resultingClaimGeneration

    let internal claimSourceFixtureAt binding actor operationId expectedVersion now
            bearer request expectedStageGeneration resultingClaimGeneration =
        claimWithSourceFixtureAt binding (Some binding) actor operationId expectedVersion now
            bearer request expectedStageGeneration resultingClaimGeneration

    let internal checkpointFixtureAt binding actor (operationId: Guid) expectedVersion now
            bearer (request: ReservationPermissionRequest) expectedClaimGeneration evidenceSha =
        let mutation =
            CheckpointReadOnly(request.LaunchId, request.TargetWorkId,
                               expectedClaimGeneration, evidenceSha)
        let verify snapshot (before: Project) (after: Project) = result {
            let! grant = authorizeAt binding snapshot before now bearer request
            if request.Family <> ReservationOperationFamily.Implementation
               && request.Family <> ReservationOperationFamily.Review then
                return! denied request "Checkpoint family is not delegated"
            match before.Works.TryFind grant.WorkId, after.Works.TryFind grant.WorkId with
            | Some oldWork, Some currentWork
                when oldWork.State = WorkState.Claimed grant.LaunchId
                     && oldWork.ClaimGeneration = expectedClaimGeneration
                     && currentWork.State = oldWork.State -> return ()
            | _ -> return! denied request "Checkpoint is not for the live claimed Work generation"
        }
        WorkQueueGitLedger.executePinnedDisposableLifecycle binding actor operationId
            expectedVersion now mutation verify

    // Propose rather than silently execute a significant/outside-scope blocker.
    // The proposal and its originating launch/grant are independently visible
    // in Git; neither WQ02 graph nor available queue changes here.
    let internal proposePrerequisiteAt binding actor (operationId: Guid) now bearer
            (permission: ReservationPermissionRequest) expectedGraphVersion
            (kind: ReservationBlockerKind) suggestedWorkId evidenceSha description =
        if operationId = Guid.Empty
           || not (hex64 evidenceSha)
           || String.IsNullOrWhiteSpace suggestedWorkId || suggestedWorkId.Length > 160
           || String.IsNullOrWhiteSpace description || description.Length > 1000
           || suggestedWorkId = permission.TargetWorkId then
            Error(InvalidBinding "Prerequisite proposal needs a distinct Work ID and bounded SHA-256 evidence.")
        else
            let requestJson = JsonObject()
            requestJson.Add("schemaVersion", JsonValue.Create(1))
            requestJson.Add("launchId", JsonValue.Create(permission.LaunchId))
            requestJson.Add("workId", JsonValue.Create(permission.TargetWorkId))
            requestJson.Add("scopeRevision", JsonValue.Create(permission.ExpectedRevision))
            requestJson.Add("suggestedWorkId", JsonValue.Create(suggestedWorkId))
            requestJson.Add("kind", JsonValue.Create(sprintf "%A" kind))
            requestJson.Add("evidenceSha256", JsonValue.Create(evidenceSha))
            requestJson.Add("description", JsonValue.Create(description))
            let intent =
                { OperationId = operationId; Actor = actor; Kind = "WQ11.ProposePrerequisite"
                  RequestJson = requestJson.ToJsonString() }
            LedgerStore.transact binding intent (fun snapshot -> result {
                let! project = WorkQueueGitLedger.loadAt binding snapshot
                let! grant = authorizeAt binding snapshot project now bearer permission
                if permission.Family <> ReservationOperationFamily.Prerequisite then
                    return! denied permission "Prerequisite proposal family required"
                if expectedGraphVersion <> project.GraphVersion then
                    return! Error(StaleEntityVersion "Blocker graph version changed; recheck its dependency.")
                if project.Works.ContainsKey suggestedWorkId then
                    return! denied permission "Use canonical graph writer for an existing prerequisite; do not duplicate it"
                let! existing = readProposalAt binding snapshot operationId
                if existing.IsSome then
                    return! Error(LedgerIntegrityError "Prerequisite proposal already exists with this operation ID.")
                let proposal =
                    { SchemaVersion = 1; ProjectId = grant.ProjectId
                      OwningWorkId = grant.WorkId; LaunchId = grant.LaunchId
                      GrantRevision = grant.Revision; ExpectedGraphVersion = expectedGraphVersion
                      SuggestedWorkId = suggestedWorkId; Kind = kind
                      EvidenceSha256 = evidenceSha; Description = description
                      RecordedAtUnixMs = now; OperationId = operationId.ToString("D")
                      Status = "ProposedOnlyNotRunnable" }
                return [ { Path = proposalPath operationId
                           ExpectedBlobOid = None; Content = CanonicalCodec.encode proposal } ]
            })
