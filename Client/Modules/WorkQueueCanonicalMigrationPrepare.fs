module Client.WorkQueueCanonicalMigrationPrepare

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.AccountCapacityLedger

// WQ10 recovery-safe *binding assessment*, deliberately not a migration writer.
// No accepted WQ07 Preparing transaction or WQ02 Prepared conversion exists.
// In particular, a supplied approval, SHA or boolean cannot create either proof.
[<RequireQualifiedAccess>]
type CanonicalPrepareMode =
    | InspectOnly
    | AttemptOnDisposable

type CanonicalProjectTarget =
    { Name: string
      Binding: LedgerBinding
      ExpectedHead: string
      ExpectedHighWater: int64
      ExpectedVersion: int }

type CanonicalPrepareRequest =
    { OperationId: Guid
      Actor: string
      DisposableRoot: string
      Mode: CanonicalPrepareMode
      Account: AccountBinding
      ExpectedAccountHead: string
      Projects: CanonicalProjectTarget list
      ReviewedOfflineReportSha256: string }

// No Ready/Prepared case is representable until a reviewed atomic writer
// authenticates all these capabilities at the same respective Git CAS heads.
[<RequireQualifiedAccess>]
type CanonicalPrepareGap =
    | DisabledByDefault
    | InvalidRequest of string
    | UnapprovedDisposableAuthority of string
    | AccountUnreadable of string
    | ProjectUnreadable of string * string
    | AccountHeadChanged of expected: string * observed: string
    | ProjectHeadChanged of project: string * expected: string * observed: string
    | AccountNotFrozen of observed: string
    | ProjectNotFrozen of project: string * observed: MigrationEpoch
    | AccountCapacityOccupied of count: int
    | ProjectLaunchesOutstanding of project: string * count: int
    | ProjectCoverageIncomplete of project: string * coverages: string list
    | ProjectHighWaterChanged of project: string * expected: int64 * observed: int64
    | ProjectVersionChanged of project: string * expected: int * observed: int
    | AuthenticatedOfflineReviewMissing
    | AuthenticatedOldWriterFenceMissing
    | OriginalJournalsAndSourceHighWaterMissing
    | VerifiedRoleDependencyAndIndexConversionMissing
    | AccountPreparingCasUnavailable
    | ProjectPreparedCasUnavailable
    | CrossLedgerPartialReceiptRecoveryUnavailable

type CanonicalPrepareAssessment =
    { OperationId: Guid
      AccountHead: string option
      ProjectHeads: (string * string) list
      Missing: CanonicalPrepareGap list
      CanonicalPrepared: bool
      SchedulerEnabled: bool
      ProductionCutoverAuthorized: bool }

// An exact marker and physical boundary are necessary, never sufficient.
// All paths touched by ledger fetch must be inside a disposable root. Resolve
// each existing ancestor's reparse-point attributes rather than only checking
// the final component (including a symlinked repo, remote or marker).
module WorkQueueCanonicalMigrationPrepare =
    let private sha = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
    let private oid = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private marker = ".wq10-disposable-fixture"
    let private markerContent = "WQ10_DISPOSABLE_FIXTURE_V1"

    let private safeAbsolutePath (raw: string) =
        if String.IsNullOrWhiteSpace raw || not (Path.IsPathFullyQualified raw)
           || raw.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
              |> Array.exists ((=) "..") then
            None
        else
            try Some(Path.GetFullPath raw) with _ -> None

    let private noSymlinkAncestors (path: string) =
        try
            let rec check current =
                if String.IsNullOrEmpty current then true
                else
                    let attr = File.GetAttributes current
                    not (attr.HasFlag FileAttributes.ReparsePoint)
                    && (let parent = Path.GetDirectoryName current
                        isNull parent || parent = current || check parent)
            check path
        with _ -> false

    let private under root path =
        match safeAbsolutePath path with
        | Some absolute ->
            absolute.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Directory.Exists absolute && noSymlinkAncestors absolute
        | None -> false

    // A symlink anywhere under .git (objects, refs, logs, packs, or nested
    // descendants) may redirect fetch writes. Check the entire supplied
    // metadata tree, then use a newly created private clone for actual reads.
    let private noGitMetadataRedirects (root: string) =
        let rec check directory =
            try
                noSymlinkAncestors directory
                && (Directory.EnumerateFileSystemEntries directory
                    |> Seq.forall (fun path ->
                        let attrs = File.GetAttributes path
                        not (attrs.HasFlag FileAttributes.ReparsePoint)
                        && (not (attrs.HasFlag FileAttributes.Directory) || check path)))
            with _ -> false
        Directory.Exists root && check root

    let private safeClone root (raw: string) =
        if not (under root raw) then false
        else
            let metadata = Path.Combine(raw, ".git")
            let alternates = Path.Combine(metadata, "objects", "info", "alternates")
            noGitMetadataRedirects metadata
            && not (File.Exists alternates)
            && not (File.Exists(Path.Combine(metadata, "commondir")))

    let private safeBareAuthority root (raw: string) =
        if not (under root raw) then false
        else
            let head = Path.Combine(raw, "HEAD")
            let objects = Path.Combine(raw, "objects")
            let refs = Path.Combine(raw, "refs")
            let config = Path.Combine(raw, "config")
            let alternates = Path.Combine(objects, "info", "alternates")
            noGitMetadataRedirects raw
            && File.Exists head && noSymlinkAncestors head
            && Directory.Exists objects && noSymlinkAncestors objects
            && Directory.Exists refs && noSymlinkAncestors refs
            && File.Exists config && noSymlinkAncestors config
            && not (File.Exists alternates)

    let private approvedRoot (request: CanonicalPrepareRequest) =
        match safeAbsolutePath request.DisposableRoot with
        | None -> Error "Disposable root must be an explicit absolute nontraversing path."
        | Some root when not (
                root.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                    Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar, StringComparison.Ordinal)) ->
            Error "WQ10 disposable root must be nested beneath the OS temporary directory."
        | Some root when not (Directory.Exists root && noSymlinkAncestors root) ->
            Error "Disposable root is missing, symlinked or inaccessible."
        | Some root ->
            let markerPath = Path.Combine(root, marker)
            if not (File.Exists markerPath)
               || not (noSymlinkAncestors markerPath)
               || File.ReadAllText(markerPath) <> markerContent then
                Error "Exact WQ10 disposable-root marker is missing or untrusted."
            else
                let targets =
                    [ request.Account.RepositoryPath
                      request.Account.ApprovedFetchUrl
                      request.Account.ApprovedPushUrl ]
                    @ (request.Projects |> List.collect (fun p ->
                        [ p.Binding.RepositoryPath
                          p.Binding.ApprovedFetchUrl
                          p.Binding.ApprovedPushUrl ]))
                if targets |> List.exists (fun p -> not (under root p)) then
                    Error "Every local clone, fetch authority and push authority must be physically inside the disposable root."
                elif not (safeClone root request.Account.RepositoryPath)
                     || request.Projects |> List.exists (fun p -> not (safeClone root p.Binding.RepositoryPath)) then
                    Error "Clone Git metadata escapes disposable scratch or contains an alternate/worktree redirect."
                elif not (safeBareAuthority root request.Account.ApprovedFetchUrl)
                     || not (safeBareAuthority root request.Account.ApprovedPushUrl)
                     || request.Projects |> List.exists (fun p ->
                         not (safeBareAuthority root p.Binding.ApprovedFetchUrl)
                         || not (safeBareAuthority root p.Binding.ApprovedPushUrl)) then
                    Error "Approved Git authorities must be local bare repositories without alternate-object redirects."
                else
                    let authorities =
                        request.Account.ApprovedPushUrl
                        :: (request.Projects |> List.map (fun p -> p.Binding.ApprovedPushUrl))
                        |> List.choose safeAbsolutePath
                    if authorities.Length <> 3
                       || (authorities |> List.map (fun s -> s.ToUpperInvariant()) |> List.distinct |> List.length) <> 3 then
                        Error "One distinct account and exactly two distinct project Git authorities are required."
                    else Ok root

    // Existing WQ07/WQ02 fetch readers mutate local Git object/ref caches.
    // Caller-supplied clones cannot guarantee confinement: a nested symlink
    // may be swapped in after preflight. Build fresh private metadata instead.
    let private isolatedClone (root: string) template label remote (remoteName: string) =
        if not (Regex.IsMatch(remoteName, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")) then
            invalidOp "Disposable Git remote name is invalid."
        let destination = Path.Combine(root, label)
        use gitProcess = new Process()
        gitProcess.StartInfo.FileName <- "git"
        gitProcess.StartInfo.WorkingDirectory <- root
        gitProcess.StartInfo.UseShellExecute <- false
        gitProcess.StartInfo.CreateNoWindow <- true
        gitProcess.StartInfo.RedirectStandardOutput <- true
        gitProcess.StartInfo.RedirectStandardError <- true
        for arg in
            [ "clone"; "--quiet"; "--no-checkout"; "--no-local"
              "--template=" + template; "--origin"; remoteName
              "--"; remote; destination ] do
            gitProcess.StartInfo.ArgumentList.Add arg
        // A fresh metadata directory is not safe if Git inherits redirection
        // or tracing paths via the operator's environment.
        let gitOverrides =
            gitProcess.StartInfo.Environment.Keys
            |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            |> Seq.toList
        for key in gitOverrides do
            gitProcess.StartInfo.Environment.Remove key |> ignore
        gitProcess.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] <- "1"
        gitProcess.StartInfo.Environment["GIT_CONFIG_GLOBAL"] <- Path.Combine(root, "empty-global-config")
        gitProcess.StartInfo.Environment["GIT_TEMPLATE_DIR"] <- template
        gitProcess.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        if not (gitProcess.Start()) then
            invalidOp "Could not start disposable Git clone."
        let stderr = gitProcess.StandardError.ReadToEndAsync()
        let stdout = gitProcess.StandardOutput.ReadToEndAsync()
        if not (gitProcess.WaitForExit(30000)) then
            gitProcess.Kill(true)
            invalidOp "Disposable Git clone exceeded the bounded deadline."
        let errors = stderr.GetAwaiter().GetResult()
        stdout.GetAwaiter().GetResult() |> ignore
        if gitProcess.ExitCode <> 0 then
            invalidOp ("Disposable Git clone failed: " + errors)
        if not (safeClone root destination) then
            invalidOp "New private Git metadata failed the no-redirect check."
        destination

    let private withPrivateReadBindings (root: string) (request: CanonicalPrepareRequest) action =
        // Root must be non-world-writable; otherwise another user could race
        // setup before the fresh child directory is made private.
        if not (OperatingSystem.IsWindows()) then
            let rootMode = File.GetUnixFileMode root
            let writableByOthers = UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite
            if (rootMode &&& writableByOthers) <> UnixFileMode.None then
                invalidOp "Disposable root is not private enough for isolated Git reads."
        let scopePath = Path.Combine(root, "wq10-private-" + Guid.NewGuid().ToString("N"))
        let scope =
            if OperatingSystem.IsWindows() then Directory.CreateDirectory scopePath
            else
                Directory.CreateDirectory(
                    scopePath,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        try
            if not (noSymlinkAncestors scope.FullName) then
                invalidOp "Disposable isolated scratch redirected."
            if not (OperatingSystem.IsWindows()) then
                let mode = File.GetUnixFileMode scope.FullName
                let otherAccess =
                    UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute
                    ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute
                if (mode &&& otherAccess) <> UnixFileMode.None then
                    invalidOp "Isolated Git scratch must not be accessible to other users."
            let template = Path.Combine(scope.FullName, "empty-template")
            Directory.CreateDirectory template |> ignore
            let isolatedAccount =
                { request.Account with
                    RepositoryPath = isolatedClone scope.FullName template "account"
                                                    request.Account.ApprovedFetchUrl request.Account.RemoteName }
            let isolatedProjects =
                request.Projects
                |> List.mapi (fun i project ->
                    let repository =
                        isolatedClone scope.FullName template ("project-" + string i)
                                      project.Binding.ApprovedFetchUrl project.Binding.RemoteName
                    project, { project.Binding with RepositoryPath = repository })
            action isolatedAccount isolatedProjects
        finally
            Directory.Delete(scope.FullName, true)

    let private permanentMissing =
        [ CanonicalPrepareGap.AuthenticatedOfflineReviewMissing
          CanonicalPrepareGap.AuthenticatedOldWriterFenceMissing
          CanonicalPrepareGap.OriginalJournalsAndSourceHighWaterMissing
          CanonicalPrepareGap.VerifiedRoleDependencyAndIndexConversionMissing
          CanonicalPrepareGap.AccountPreparingCasUnavailable
          CanonicalPrepareGap.ProjectPreparedCasUnavailable
          CanonicalPrepareGap.CrossLedgerPartialReceiptRecoveryUnavailable ]

    let inspect (request: CanonicalPrepareRequest) : CanonicalPrepareAssessment =
        let gaps = ResizeArray<CanonicalPrepareGap>()
        let mutable accountHead = None
        let projectHeads = ResizeArray<string * string>()
        if request.Mode = CanonicalPrepareMode.InspectOnly then
            gaps.Add CanonicalPrepareGap.DisabledByDefault
        if request.OperationId = Guid.Empty || String.IsNullOrWhiteSpace request.Actor then
            gaps.Add(CanonicalPrepareGap.InvalidRequest "A stable nonzero operation UUID and actor are required.")
        if not (oid.IsMatch(defaultArg (Option.ofObj request.ExpectedAccountHead) "")) then
            gaps.Add(CanonicalPrepareGap.InvalidRequest "Pin one exact account Git head.")
        if not (sha.IsMatch(defaultArg (Option.ofObj request.ReviewedOfflineReportSha256) "")) then
            gaps.Add(CanonicalPrepareGap.InvalidRequest "Pin the SHA-256 of the unchanged Release inspector report.")
        if request.Projects.Length <> 2 then
            gaps.Add(CanonicalPrepareGap.InvalidRequest "Exactly two approved project authorities must be specified.")
        if request.Projects
           |> List.exists (fun p ->
               String.IsNullOrWhiteSpace p.Name || p.Name.Contains('/')
               || p.Name.Contains('\\') || p.Name = "." || p.Name = ".."
               || not (oid.IsMatch(defaultArg (Option.ofObj p.ExpectedHead) ""))
               || p.ExpectedHighWater < 0L || p.ExpectedVersion < 0) then
            gaps.Add(CanonicalPrepareGap.InvalidRequest "Project identity, pinned head, version or high-water is invalid.")
        let identities = request.Projects |> List.map (fun p -> p.Name.ToUpperInvariant(), p.Binding.ProjectId)
        if (identities |> List.map fst |> List.distinct |> List.length) <> identities.Length
           || (identities |> List.map snd |> List.distinct |> List.length) <> identities.Length then
            gaps.Add(CanonicalPrepareGap.InvalidRequest "Project names and authenticated project IDs must be distinct.")
        let valid = gaps.Count = 0
                    || (gaps.Count = 1 && request.Mode = CanonicalPrepareMode.InspectOnly)
        if valid then
            try
                match approvedRoot request with
                | Error reason -> gaps.Add(CanonicalPrepareGap.UnapprovedDisposableAuthority reason)
                | Ok root ->
                    withPrivateReadBindings root request (fun accountBinding projectBindings ->
                        match AccountCapacityLedger.fetch accountBinding with
                        | Error error -> gaps.Add(CanonicalPrepareGap.AccountUnreadable(string error))
                        | Ok account ->
                            accountHead <- Some account.Head
                            if account.Head <> request.ExpectedAccountHead then
                                gaps.Add(CanonicalPrepareGap.AccountHeadChanged(request.ExpectedAccountHead, account.Head))
                            if account.State.MigrationPhase <> "Frozen" then
                                gaps.Add(CanonicalPrepareGap.AccountNotFrozen(account.State.MigrationPhase))
                            let occupied = AccountCapacityLedger.occupied account.State
                            if occupied <> 0 then
                                gaps.Add(CanonicalPrepareGap.AccountCapacityOccupied occupied)
                        for p, binding in projectBindings do
                            match LedgerStore.fetch binding with
                            | Error error ->
                                gaps.Add(CanonicalPrepareGap.ProjectUnreadable(p.Name, string error))
                            | Ok snapshot ->
                                projectHeads.Add(p.Name, snapshot.Commit)
                                if snapshot.Commit <> p.ExpectedHead then
                                    gaps.Add(CanonicalPrepareGap.ProjectHeadChanged(p.Name, p.ExpectedHead, snapshot.Commit))
                                let highWater = snapshot.Header.NextSequence - 1L
                                if highWater <> p.ExpectedHighWater then
                                    gaps.Add(CanonicalPrepareGap.ProjectHighWaterChanged(p.Name, p.ExpectedHighWater, highWater))
                                match WorkQueueGitLedger.loadAt binding snapshot with
                                | Error error ->
                                    gaps.Add(CanonicalPrepareGap.ProjectUnreadable(p.Name, string error))
                                | Ok project ->
                                    if project.Version <> p.ExpectedVersion then
                                        gaps.Add(CanonicalPrepareGap.ProjectVersionChanged(p.Name, p.ExpectedVersion, project.Version))
                                    if project.Migration <> MigrationEpoch.Frozen then
                                        gaps.Add(CanonicalPrepareGap.ProjectNotFrozen(p.Name, project.Migration))
                                    if project.Launches.Count <> 0 then
                                        gaps.Add(CanonicalPrepareGap.ProjectLaunchesOutstanding(p.Name, project.Launches.Count))
                                    let coverage =
                                        [ if not project.QueueCovered then "queue"
                                          if not project.GraphCovered then "dependency-graph"
                                          if not project.MessageCovered then "messages"
                                          if not project.SourceCovered then "source" ]
                                    if not coverage.IsEmpty then
                                        gaps.Add(CanonicalPrepareGap.ProjectCoverageIncomplete(p.Name, coverage)))
            with ex ->
                gaps.Add(CanonicalPrepareGap.UnapprovedDisposableAuthority(
                    "Disposable inspection failed closed: " + ex.GetType().Name))
        // These are not user-supplied attestations. There is currently no trusted
        // producer for them, so a positive canonical Prepared result is impossible.
        gaps.AddRange permanentMissing
        { OperationId = request.OperationId
          AccountHead = accountHead
          ProjectHeads = List.ofSeq projectHeads
          Missing = List.ofSeq gaps
          CanonicalPrepared = false
          SchedulerEnabled = false
          ProductionCutoverAuthorized = false }

    // No CAS writer is present. Returning Error with *read-only* pinned evidence
    // is the only outcome; neither account nor project staging refs are created.
    let attemptCanonicalPrepare request : Result<unit, CanonicalPrepareAssessment> =
        Error(inspect request)

    // The shipping public API has no trust-key argument and no installed
    // host anchors. Only the assembly-internal hermetic fixture seam below
    // can exercise verified transaction mechanics. Never treat test signers
    // as operational trust anchors.
    type internal PinnedAccountPreparingKeys =
        { ClientProtocolPublicKeySpki: byte array
          OldWriterFencePublicKeySpki: byte array
          OperatorApprovalPublicKeySpki: byte array }

    type AccountPreparingCommand =
        { Target: CanonicalPrepareRequest
          ExpectedAccountStateVersion: int64
          MinConnectedClientProtocol: int
          ClientProtocolReportSha256: string
          OldWriterFenceReportSha256: string
          OperatorDecisionSha256: string
          OriginalSourceCheckpoints: AccountMigrationOriginalSourceCheckpoint array
          OriginalSourceCoverageWitness: AccountMigrationSignedWitness
          ClientProtocolWitness: AccountMigrationSignedWitness
          OldWriterFenceWitness: AccountMigrationSignedWitness
          OperatorApprovalWitness: AccountMigrationSignedWitness }

    [<RequireQualifiedAccess>]
    type AccountPreparingState =
        | AccountPreparing of AccountMigrationPreparingReceipt * AccountWrite
        | AccountRecoveryBlocked of string list

    [<RequireQualifiedAccess>]
    type ProjectPreparedState =
        | ProjectPreparedNotEstablished

    type AccountPreparingOutcome =
        { Account: AccountPreparingState
          Projects: ProjectPreparedState
          SchedulerEnabled: bool
          ProductionCutoverAuthorized: bool }

    let private sha256String (data: string) =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant()

    // Length-prefix every UTF-8 field rather than using ambiguous delimiters.
    // Roster order is canonical by immutable project identity.
    let private frame (fields: string list) =
        fields
        |> List.map (fun value -> string (Encoding.UTF8.GetByteCount value) + ":" + value)
        |> String.concat ""

    let private participants (target: CanonicalPrepareRequest) =
        target.Projects
        |> List.sortBy (fun p -> p.Binding.ProjectId)
        |> List.map (fun p ->
            let authority: ProjectLedgerAuthority =
                { ProjectId = p.Binding.ProjectId
                  LedgerId = p.Binding.LedgerId
                  Epoch = p.Binding.Epoch
                  ApprovedFetchUrl = p.Binding.ApprovedFetchUrl
                  ApprovedPushUrl = p.Binding.ApprovedPushUrl }
            { ProjectId = string p.Binding.ProjectId
              LedgerId = string p.Binding.LedgerId
              LedgerEpoch = p.Binding.Epoch
              AuthoritySha256 = AccountCapacityLedger.projectAuthoritySha256 authority
              FrozenHead = p.ExpectedHead
              ProjectVersion = p.ExpectedVersion
              SourceHighWater = p.ExpectedHighWater })
        |> List.toArray

    let private rosterDigest (roster: AccountMigrationParticipant array) =
        roster
        |> Array.toList
        |> List.collect (fun p ->
            [ p.ProjectId; p.LedgerId; string p.LedgerEpoch
              p.AuthoritySha256; p.FrozenHead
              string p.ProjectVersion; string p.SourceHighWater ])
        |> frame |> sha256String

    // Original journal identity and source-effect high-water are separate
    // from the NEW canonical project NextSequence. An independently trusted
    // legacy fence/coverage signer must attest them for both frozen heads.
    let accountPreparingCoverageSha256 (command: AccountPreparingCommand) =
        let checkpoints =
            if isNull command.OriginalSourceCheckpoints then [||]
            else command.OriginalSourceCheckpoints
        ( "jarvis-original-source-journal-coverage-v1"
          :: command.Target.ReviewedOfflineReportSha256
          :: (checkpoints
              |> Array.sortBy (fun c -> c.ProjectId)
              |> Array.toList
              |> List.collect (fun c ->
                  [ c.ProjectId; c.FrozenProjectHead; c.OriginalJournalId
                    string c.OriginalJournalHighWater
                    string c.OriginalSourceEffectHighWater ])))
        |> frame |> sha256String

    // Separate from signed proof documents; the canonical statement binds
    // the exact original legacy journals and source-effect high-water too.
    let private statementDigest (command: AccountPreparingCommand) rosterSha =
        let target = command.Target
        let b = target.Account
        frame
            [ "jarvis-account-preparing-v1"
              string target.OperationId; target.Actor
              string b.AccountId; string b.LedgerId; string b.Epoch
              sha256String(frame [ b.ApprovedFetchUrl; b.ApprovedPushUrl ])
              target.ExpectedAccountHead
              string command.ExpectedAccountStateVersion
              rosterSha; target.ReviewedOfflineReportSha256
              accountPreparingCoverageSha256 command
              string command.MinConnectedClientProtocol
              command.ClientProtocolReportSha256
              command.OldWriterFenceReportSha256 ]
        |> sha256String

    let accountPreparingStatementSha256 (command: AccountPreparingCommand) =
        statementDigest command (rosterDigest (participants command.Target))

    // Public signature grammar for independently provisioned producers. All
    // three signatures are domain-separated; signing the same generic source
    // string or reusing another role's signature does not confer authority.
    let accountPreparingWitnessMessage role statementSha evidenceSha =
        Encoding.UTF8.GetBytes(
            frame [ "jarvis-account-preparing-attestation-v1"; role; statementSha; evidenceSha ])

    let private verifyWitness (publicKey: byte array) role statementSha expectedEvidence
                              (witness: AccountMigrationSignedWitness) =
        if isNull publicKey || publicKey.Length = 0 || isNull (box witness)
           || witness.EvidenceSha256 <> expectedEvidence
           || witness.SignerPublicKeySha256 <>
               Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant()
           || not (sha.IsMatch(defaultArg (Option.ofObj witness.EvidenceSha256) "")) then false
        else
            try
                use verifier = ECDsa.Create()
                let mutable read = 0
                verifier.ImportSubjectPublicKeyInfo(publicKey.AsSpan(), &read)
                read = publicKey.Length
                && verifier.VerifyData(
                    accountPreparingWitnessMessage role statementSha expectedEvidence,
                    Convert.FromBase64String(witness.SignatureBase64),
                    HashAlgorithmName.SHA256)
            with _ -> false

    let private accountOnlyGap coverageVerified = function
        // These require later project conversion, but are not account-only
        // first-time prerequisites when independently signed evidence exists.
        | CanonicalPrepareGap.AuthenticatedOfflineReviewMissing
        | CanonicalPrepareGap.AuthenticatedOldWriterFenceMissing
        | CanonicalPrepareGap.VerifiedRoleDependencyAndIndexConversionMissing
        | CanonicalPrepareGap.AccountPreparingCasUnavailable
        | CanonicalPrepareGap.ProjectPreparedCasUnavailable
        | CanonicalPrepareGap.CrossLedgerPartialReceiptRecoveryUnavailable -> false
        // Never waive the ORIGINAL journals/high-water merely because the
        // new canonical ledger has a NextSequence or an arbitrary report hash.
        | CanonicalPrepareGap.OriginalJournalsAndSourceHighWaterMissing ->
            not coverageVerified
        | _ -> true

    let private blockedAccount reasons : AccountPreparingOutcome =
        { Account = AccountPreparingState.AccountRecoveryBlocked reasons
          Projects = ProjectPreparedState.ProjectPreparedNotEstablished
          SchedulerEnabled = false
          ProductionCutoverAuthorized = false }

    // The only PUBLIC entrypoint. There is presently no independent host or
    // operator trust-anchor provisioning, so caller-generated signatures can
    // never enable a CAS. Future host integration must resolve immutable
    // anchors and original-source attestation OUTSIDE the Preparing request.
    let attemptAccountPreparing (_command: AccountPreparingCommand) : AccountPreparingOutcome =
        blockedAccount
            [ "Account Preparing blocked: independent host-pinned protocol, legacy-journal/fence and operator approval trust anchors are not provisioned." ]

    // Assembly-INTERNAL fixture executor, used solely by Client.Tests to
    // verify hypothetical pinned-host receipt mechanics on marked private
    // disposable Git authorities. It is deliberately not a shipping API.
    let internal attemptAccountPreparingWithFixtureTrustAnchors
        (keys: PinnedAccountPreparingKeys)
        (command: AccountPreparingCommand) : AccountPreparingOutcome =
        try
            let target = command.Target
            let invalid =
                [ if target.Mode <> CanonicalPrepareMode.AttemptOnDisposable then
                      "Account Preparing remains disabled without an explicit disposable attempt."
                  if command.ExpectedAccountStateVersion < 1L then
                      "Pin the previous WQ07 account StateVersion."
                  if command.MinConnectedClientProtocol < 1 then
                      "A positive, signed client-protocol minimum is required."
                  for digest in
                      [ command.ClientProtocolReportSha256
                        command.OldWriterFenceReportSha256; command.OperatorDecisionSha256 ] do
                      if not (sha.IsMatch(defaultArg (Option.ofObj digest) "")) then
                          "All three source evidence SHA-256 digests must be fixed."
                  if isNull (box keys) || isNull keys.ClientProtocolPublicKeySpki
                     || isNull keys.OldWriterFencePublicKeySpki
                     || isNull keys.OperatorApprovalPublicKeySpki then
                      "Independent host-pinned verification keys were not provisioned." ]
            if not invalid.IsEmpty then blockedAccount invalid
            else
                let roster = participants target
                let rosterSha = rosterDigest roster
                let statementSha = statementDigest command rosterSha
                let coverageSha = accountPreparingCoverageSha256 command
                let checkpoints = command.OriginalSourceCheckpoints
                let expectedCheckpoints =
                    roster
                    |> Array.map (fun p -> p.ProjectId, p.FrozenHead)
                    |> Set.ofArray
                let validCoverage =
                    not (isNull checkpoints) && checkpoints.Length = 2
                    && (checkpoints
                        |> Array.map (fun c -> c.ProjectId, c.FrozenProjectHead)
                        |> Set.ofArray) = expectedCheckpoints
                    && (checkpoints |> Array.forall (fun c ->
                        let mutable parsed = Guid.Empty
                        not (isNull (box c)) && Guid.TryParse(c.OriginalJournalId, &parsed)
                        && parsed <> Guid.Empty
                        && c.OriginalJournalHighWater >= 0L
                        && c.OriginalSourceEffectHighWater >= 0L))
                let keyDigests =
                    [ keys.ClientProtocolPublicKeySpki; keys.OldWriterFencePublicKeySpki
                      keys.OperatorApprovalPublicKeySpki ]
                    |> List.map (fun key -> Convert.ToHexString(SHA256.HashData key))
                let evidenceValid =
                    keyDigests |> List.distinct |> List.length = 3
                    && verifyWitness keys.OldWriterFencePublicKeySpki
                           "original-journal-coverage" statementSha coverageSha
                           command.OriginalSourceCoverageWitness
                    && verifyWitness keys.ClientProtocolPublicKeySpki "client-protocol"
                           statementSha command.ClientProtocolReportSha256 command.ClientProtocolWitness
                    && verifyWitness keys.OldWriterFencePublicKeySpki "old-writer-fence"
                           statementSha command.OldWriterFenceReportSha256 command.OldWriterFenceWitness
                    && verifyWitness keys.OperatorApprovalPublicKeySpki "operator-approval"
                           statementSha command.OperatorDecisionSha256 command.OperatorApprovalWitness
                if not validCoverage then
                    blockedAccount
                        [ "OriginalJournalsAndSourceHighWaterMissing: independently attested original journal IDs, source-effect high-water and both frozen heads are required." ]
                elif not evidenceValid then
                    blockedAccount
                        [ "Missing, foreign, stale or mismatched independently signed protocol/fence/operator/original-source evidence." ]
                else
                    let b = target.Account
                    let receipt =
                        { SchemaVersion = 1
                          OriginalOperationId = string target.OperationId
                          Actor = target.Actor
                          AccountId = string b.AccountId
                          LedgerId = string b.LedgerId
                          Epoch = b.Epoch
                          PreviousAccountHead = target.ExpectedAccountHead
                          PreviousStateVersion = command.ExpectedAccountStateVersion
                          ParticipantRoster = roster
                          ParticipantRosterSha256 = rosterSha
                          ReviewedReportSha256 = target.ReviewedOfflineReportSha256
                          OriginalSourceCheckpoints = checkpoints
                          OriginalSourceCoverageSha256 = coverageSha
                          OriginalSourceCoverageWitness = command.OriginalSourceCoverageWitness
                          MinConnectedClientProtocol = command.MinConnectedClientProtocol
                          StatementSha256 = statementSha
                          ClientProtocolWitness = command.ClientProtocolWitness
                          OldWriterFenceWitness = command.OldWriterFenceWitness
                          OperatorApprovalWitness = command.OperatorApprovalWitness
                          AccountStatus = "AccountPreparing"
                          ProjectStatus = "ProjectPreparedNotEstablished" }
                    match approvedRoot target with
                    | Error reason ->
                        blockedAccount [ sprintf "%A" (CanonicalPrepareGap.UnapprovedDisposableAuthority reason) ]
                    | Ok root ->
                        let execute =
                            withPrivateReadBindings root target (fun account _ ->
                                // The lower-level account writer accepts ONLY an
                                // authenticated capability, never raw receipt shape.
                                match AccountCapacityLedger.authenticateDisposablePreparingReceipt
                                          keys.ClientProtocolPublicKeySpki
                                          keys.OldWriterFencePublicKeySpki
                                          keys.OperatorApprovalPublicKeySpki receipt with
                                | Error failure -> Error failure
                                | Ok authenticated ->
                                    // WQ07 receipt lookup must happen BEFORE any
                                    // mutable current-project Frozen/head checks.
                                    // A committed exact original can recover after
                                    // legitimate project progress or lost ack.
                                    match AccountCapacityLedger.readOperation account target.OperationId with
                                    | Error failure -> Error failure
                                    | Ok (_, Some _) ->
                                        AccountCapacityLedger.prepareAccountForVerifiedDisposable
                                            root account authenticated
                                    | Ok (_, None) ->
                                        let assessment = inspect target
                                        let violations =
                                            assessment.Missing
                                            |> List.filter (accountOnlyGap validCoverage)
                                            |> List.map (sprintf "%A")
                                        if not violations.IsEmpty then
                                            Error(InvalidAccountBinding(
                                                "First-time account Preparing preflight blocked: "
                                                + String.concat "; " violations))
                                        else
                                            AccountCapacityLedger.prepareAccountForVerifiedDisposable
                                                root account authenticated)
                        match execute with
                        | Ok write ->
                            { Account = AccountPreparingState.AccountPreparing(receipt, write)
                              Projects = ProjectPreparedState.ProjectPreparedNotEstablished
                              SchedulerEnabled = false
                              ProductionCutoverAuthorized = false }
                        | Error error ->
                            blockedAccount
                                [ "Account Preparing CAS blocked: " + string error
                                  "Recover uncertain outcomes with the original UUID; never mint a replacement blindly." ]
        with ex ->
            blockedAccount [ "Disposable account Preparing refused: " + ex.GetType().Name ]
