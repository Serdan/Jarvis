module Client.WorkQueueCanonicalMigrationPrepare

open System
open System.Diagnostics
open System.IO
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
