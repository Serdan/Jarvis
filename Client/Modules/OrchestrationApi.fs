module Client.OrchestrationApi

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Common
open Client
open Client.IO
open Client.Effect

[<CLIMutable>]
type LedgerTrustBinding =
    { SchemaVersion: int
      Enabled: bool
      ProjectRoot: string
      ProjectId: string
      LedgerId: string
      Epoch: int
      RemoteName: string
      ApprovedFetchUrl: string
      ApprovedPushUrl: string
      HighestTrustedHead: string }

module LedgerErrors =
    let toEffect (failure: LedgerStore.LedgerFailure) =
        let code =
            match failure with
            | LedgerStore.InvalidBinding _ -> "InvalidBinding"
            | LedgerStore.RemoteUnavailable _ -> "RemoteUnavailable"
            | LedgerStore.LedgerIntegrityError _ -> "LedgerIntegrityError"
            | LedgerStore.LedgerHistoryUnavailable _ -> "LedgerHistoryUnavailable"
            | LedgerStore.StaleEntityVersion _ -> "StaleEntityVersion"
            | LedgerStore.OperationIdReused -> "OperationIdReused"
            | LedgerStore.OutcomeUnknown _ -> "OutcomeUnknown"
            | LedgerStore.LedgerContended -> "LedgerContended"
            | LedgerStore.TrustBindingContended _ -> "TrustBindingContended"
            | LedgerStore.GitFailure _ -> "GitFailure"
            | LedgerStore.SourceUnavailable _ -> "SourceUnavailable"
            | LedgerStore.AgentBusy _ -> "AgentBusy"
            | LedgerStore.ReviewAccessDenied _ -> "ReviewAccessDenied"
            | LedgerStore.ReviewAlreadyClaimed _ -> "ReviewAlreadyClaimed"
            | LedgerStore.ReviewVersionConflict _ -> "ReviewVersionConflict"
            | LedgerStore.SourceReviewRefUnavailable _ -> "SourceReviewRefUnavailable"
        Client.LedgerFailureError
            { Code = code
              Message = string failure
              OperationId = None
              VerifiedLedgerHead = None }

module LedgerBindingConfig =
    let pathForRoot (root: string) =
        let baseDirectory = Path.GetDirectoryName(ClientConfig.configPath())
        let hash =
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)))
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()
        Path.Combine(baseDirectory, "ledger-v2", hash + ".json")

    // The trust anchor is persisted independently of Git's replaceable cache.
    // Serialize checkout-local updates across threads AND client processes.
    // Sharing violations are recoverable contention, not malformed trust data.
    // Never retry an IOException thrown by the operation under the lock.
    let internal isTransientSharingViolation (error: IOException) =
        let hresult = error.HResult
        // FileStream maps errno through FACILITY_WIN32 on .NET Unix; Windows
        // sharing/lock violations use the same HRESULT facility. Do not
        // classify an arbitrary HRESULT with matching low bits as contention.
        let encodedErrno = (hresult &&& int 0xFFFF0000u) = int 0x80070000u
        let rawErrno = hresult >= 0 && hresult <= 255
        if not (encodedErrno || rawErrno) then false
        else
            match hresult &&& 0xFFFF with
            | 11 | 32 | 33 | 35 -> true // Linux EAGAIN, Windows sharing/lock, macOS EAGAIN
            | _ -> false

    let private withConfigLock (path: string) operation =
        try
            let dir = Path.GetDirectoryName path
            Directory.CreateDirectory(dir) |> ignore
            if DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                Error(Client.ValidationError " binding directory must not be a symlink.")
            else
                let lockPath = path + ".lock"
                if File.Exists(lockPath) && File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint) then
                    Error(Client.ValidationError " binding lock must not be a symlink.")
                else
                    // One nonblocking attempt. The caller's next dashboard refresh
                    // retries transient contention; remote fetches never run here.
                    let acquired =
                        try
                            Ok (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                               FileShare.None, 4096, FileOptions.None))
                        with
                        | :? IOException as ex when isTransientSharingViolation ex ->
                            Error(LedgerErrors.toEffect (LedgerStore.TrustBindingContended ex.Message))
                        | :? UnauthorizedAccessException as ex ->
                            Error(Client.PermissionDenied $"Cannot access trust-binding lock: {ex.Message}")
                        | :? IOException as ex ->
                            Error(Client.ValidationError $"Cannot open trust-binding lock: {ex.Message}")
                    match acquired with
                    | Error error -> Error error
                    | Ok stream ->
                        use held = stream
                        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                            File.SetUnixFileMode(lockPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                        operation ()
        with
        | :? UnauthorizedAccessException as ex ->
            Error(Client.PermissionDenied $"Cannot access trust binding: {ex.Message}")
        | ex ->
            Error(Client.ValidationError $"Cannot access trust binding: {ex.Message}")

    let private readStored (path: string) =
        try
            if not (File.Exists path) then
                Error(Client.ValidationError " ledger has no approved local binding.")
            elif File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                 || FileInfo(path).Length > 4096L then
                Error(Client.ValidationError " binding must be a regular, bounded file.")
            else
                let parsed = JsonSerializer.Deserialize<LedgerTrustBinding>(
                    File.ReadAllText(path),
                    JsonSerializerOptions(PropertyNameCaseInsensitive = true))
                if isNull (box parsed) then
                    Error(Client.ValidationError "Missing  binding record.")
                else Ok parsed
        with
        | :? UnauthorizedAccessException as ex ->
            Error(Client.PermissionDenied $"Cannot read trust binding: {ex.Message}")
        | ex -> Error(Client.ValidationError $"Cannot read trust binding: {ex.Message}")

    let private writeStored (path: string) (stored: LedgerTrustBinding) overwrite =
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            try
                File.WriteAllText(temp, JsonSerializer.Serialize(stored))
                if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                File.Move(temp, path, overwrite)
                Ok ()
            with
            | :? UnauthorizedAccessException as ex ->
                Error(Client.PermissionDenied $"Cannot persist trust binding: {ex.Message}")
            | ex ->
                Error(Client.ValidationError $"Cannot persist trust binding: {ex.Message}")
        finally
            if File.Exists(temp) then File.Delete(temp)

    let private validStored root (stored: LedgerTrustBinding) : Result<LedgerStore.LedgerBinding, EffectError> =
        let projectOk, projectId = Guid.TryParse(stored.ProjectId)
        let ledgerOk, ledgerId = Guid.TryParse(stored.LedgerId)
        if stored.SchemaVersion <> 1 || not stored.Enabled
           || not (String.Equals(stored.ProjectRoot, Path.GetFullPath(root), StringComparison.Ordinal))
           || not projectOk || not ledgerOk || projectId = Guid.Empty || ledgerId = Guid.Empty
           || stored.Epoch < 1
           || String.IsNullOrWhiteSpace stored.RemoteName
           || String.IsNullOrWhiteSpace stored.ApprovedFetchUrl
           || String.IsNullOrWhiteSpace stored.ApprovedPushUrl
           || String.IsNullOrWhiteSpace stored.HighestTrustedHead then
            Error(Client.ValidationError " binding is not explicitly enabled with a pinned root and identity.")
        else
            Ok
                { RepositoryPath = root; RemoteName = stored.RemoteName
                  ApprovedFetchUrl = stored.ApprovedFetchUrl
                  ApprovedPushUrl = stored.ApprovedPushUrl
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = stored.Epoch }

    // Local binding is intentionally opt-in, separately from the normal Jarvis config.
    // A successful load never lowers its persisted trust anchor, even if another
    // client process observes a later head during our network operation.
    // Remote Git I/O and ancestry verification MUST run without the process-shared
    // binding lock. The second short lock is a compare-and-swap against the
    // exact stored trust anchor read before the fetch.
    let internal loadAtRootWithSnapshotUsing
        (fetch: LedgerStore.LedgerBinding -> Result<LedgerStore.LedgerSnapshot, LedgerStore.LedgerFailure>) root =
        let path = pathForRoot root
        if not (File.Exists path) then
            Error(Client.ValidationError $" ledger disabled for this checkout. Approved binding required at {path}. No V1 fallback.")
        else
            let rec load remaining =
                let initial =
                    withConfigLock path (fun () ->
                        readStored path
                        |> Result.bind (fun stored ->
                            validStored root stored
                            |> Result.map (fun binding -> stored, binding)))
                match initial with
                | Error error -> Error error
                | Ok (stored, binding) ->
                    match fetch binding |> Result.mapError LedgerErrors.toEffect with
                    | Error error -> Error error
                    | Ok snapshot ->
                        match LedgerStore.LedgerStore.verifyTrustedHead binding
                                  stored.HighestTrustedHead snapshot.Commit
                              |> Result.mapError LedgerErrors.toEffect with
                        | Error error -> Error error
                        | Ok () ->
                            let committed =
                                withConfigLock path (fun () ->
                                    readStored path
                                    |> Result.bind (fun current ->
                                        validStored root current
                                        |> Result.bind (fun currentBinding ->
                                            if currentBinding <> binding
                                               || { current with HighestTrustedHead = stored.HighestTrustedHead } <> stored then
                                                Error(Client.ValidationError "Trust binding changed during fetch; refusing update.")
                                            elif current.HighestTrustedHead = snapshot.Commit then
                                                Ok (Some (binding, snapshot))
                                            elif current.HighestTrustedHead <> stored.HighestTrustedHead then
                                                // Another client advanced the trust anchor. Re-read,
                                                // fetch and verify against its newer anchor.
                                                Ok None
                                            else
                                                writeStored path
                                                    { current with HighestTrustedHead = snapshot.Commit } true
                                                |> Result.map (fun () -> Some (binding, snapshot)))))
                            match committed with
                            | Error error -> Error error
                            | Ok (Some verified) -> Ok verified
                            | Ok None when remaining > 0 -> load (remaining - 1)
                            | Ok None ->
                                Error(Client.ValidationError "Trusted ledger head advanced concurrently; retry the request.")
            load 2

    let private loadAtRootWithSnapshot root =
        loadAtRootWithSnapshotUsing LedgerStore.LedgerStore.fetch root

    let private loadAtRoot root =
        loadAtRootWithSnapshot root |> Result.map fst

    // Reuse the single verified snapshot from binding resolution for advisory
    // agent-state reads, rather than fetching the same remote ledger twice.
    let loadWithSnapshot projectName rt =
        ProjectPaths.resolveProjectRoot projectName rt
        |> Result.bind (fun (FolderPath root) -> loadAtRootWithSnapshot root)

    // A source operation already resolved its checkout when loading AGENTS.md.
    // Reuse that physical root, but perform exactly the same pinned trust-anchor
    // verification and ledger fetch as ordinary binding loads.
    let loadForContext (context: ProjectPaths.ResolvedProject) =
        let (FolderPath root) = ProjectPaths.ResolvedProject.sourceRoot context
        loadAtRoot root

    let load projectName rt =
        ProjectPaths.resolveProjectRoot projectName rt
        |> Result.bind (fun (FolderPath root) -> loadAtRoot root)

    // Inspect locally approved, validated binding identity before network I/O.
    // Dashboard refreshes use it to group aliases of one canonical ledger;
    // this read neither fetches a head nor updates the trust anchor.
    let inspectLocal projectName rt =
        ProjectPaths.resolveProjectRoot projectName rt
        |> Result.bind (fun (FolderPath root) ->
            let path = pathForRoot root
            withConfigLock path (fun () ->
                readStored path
                |> Result.bind (fun stored ->
                    validStored root stored
                    |> Result.map (fun binding -> binding, stored.HighestTrustedHead))))

    // Setup is always an explicit approved action, never a side effect of a read.
    // The expected head is a separately supplied trust anchor, not inferred
    // from matching task IDs or the newest local branch.
    let bindExisting projectName rt projectId ledgerId epoch remoteName
                     approvedFetchUrl approvedPushUrl expectedTrustedHead
                     acknowledgeVisibility =
        if not acknowledgeVisibility then
            Error(Client.ValidationError
                "Explicit acknowledgment of public/unknown remote visibility is required.")
        elif projectId = Guid.Empty || ledgerId = Guid.Empty || epoch < 1 then
            Error(Client.ValidationError "A pinned project/ledger UUID and positive epoch are required.")
        else
            match ProjectPaths.resolveProjectRoot projectName rt with
            | Error error -> Error error
            | Ok(FolderPath root) ->
                let path = pathForRoot root
                if File.Exists path then
                    Error(Client.ValidationError " ledger is already bound. Use explicit rebind.")
                else
                    let binding : LedgerStore.LedgerBinding =
                        { RepositoryPath = root; RemoteName = remoteName
                          ApprovedFetchUrl = approvedFetchUrl; ApprovedPushUrl = approvedPushUrl
                          ProjectId = projectId; LedgerId = ledgerId; Epoch = epoch }
                    match LedgerStore.LedgerStore.fetch binding with
                    | Error failure ->
                        Error(LedgerErrors.toEffect failure)
                    | Ok snapshot ->
                        match LedgerStore.LedgerStore.verifyTrustedHead binding expectedTrustedHead snapshot.Commit with
                        | Error failure ->
                            Error(LedgerErrors.toEffect failure)
                        | Ok () ->
                            let record =
                                { SchemaVersion = 1; Enabled = true
                                  ProjectRoot = Path.GetFullPath root
                                  ProjectId = projectId.ToString("D")
                                  LedgerId = ledgerId.ToString("D"); Epoch = epoch
                                  RemoteName = remoteName; ApprovedFetchUrl = approvedFetchUrl
                                  ApprovedPushUrl = approvedPushUrl
                                  HighestTrustedHead = snapshot.Commit }
                            withConfigLock path (fun () ->
                                if File.Exists path then
                                    Error(Client.ValidationError " binding was concurrently created; refusing overwrite.")
                                else
                                    writeStored path record false |> Result.map (fun () -> record))

    let initializeNew projectName rt projectId ledgerId epoch remoteName
                      approvedFetchUrl approvedPushUrl acknowledgeVisibility =
        if not acknowledgeVisibility then
            Error(Client.ValidationError
                "Explicit acknowledgment of public/unknown remote visibility is required.")
        elif projectId = Guid.Empty || ledgerId = Guid.Empty || epoch < 1 then
            Error(Client.ValidationError "Valid new ledger identity is required.")
        else
            match ProjectPaths.resolveProjectRoot projectName rt with
            | Error error -> Error error
            | Ok(FolderPath root) ->
                if File.Exists(pathForRoot root) then
                    Error(Client.ValidationError " ledger is already bound; refusing reinitialization.")
                else
                    let binding : LedgerStore.LedgerBinding =
                        { RepositoryPath = root; RemoteName = remoteName
                          ApprovedFetchUrl = approvedFetchUrl; ApprovedPushUrl = approvedPushUrl
                          ProjectId = projectId; LedgerId = ledgerId; Epoch = epoch }
                    match LedgerStore.LedgerStore.initialize binding true with
                    | Error failure ->
                        Error(LedgerErrors.toEffect failure)
                    | Ok snapshot ->
                        bindExisting projectName rt projectId ledgerId epoch remoteName
                            approvedFetchUrl approvedPushUrl snapshot.Commit true

    let health projectName rt =
        load projectName rt
        |> Result.bind (fun binding ->
            LedgerStore.LedgerStore.fetch binding
            |> Result.mapError LedgerErrors.toEffect
            |> Result.bind (fun snapshot ->
                LedgerStore.LedgerStore.inspectRemotePolicy binding
                |> Result.mapError LedgerErrors.toEffect
                |> Result.map (fun protection ->
                    {| projectId = binding.ProjectId.ToString("D");
                       ledgerId = binding.LedgerId.ToString("D");
                       epoch = binding.Epoch;
                       remoteName = binding.RemoteName;
                       approvedFetchUrl = binding.ApprovedFetchUrl;
                       approvedPushUrl = binding.ApprovedPushUrl;
                       trustedLedgerHead = snapshot.Commit;
                       ledgerSequence = snapshot.Header.NextSequence;
                       remoteProtection = protection |})))

    // Rebind only to the same logical ledger/epoch. Deliberate recovery with a
    // new epoch is a separate backed-up restore operation.
    let internal rebindRemoteUsing
        (fetch: LedgerStore.LedgerBinding -> Result<LedgerStore.LedgerSnapshot, LedgerStore.LedgerFailure>)
        projectName rt remoteName approvedFetchUrl approvedPushUrl
        expectedTrustedHead acknowledgeVisibility =
        if not acknowledgeVisibility then
            Error(Client.ValidationError "Explicit rebind and remote visibility approval required.")
        else
            match load projectName rt with
            | Error error -> Error error
            | Ok old ->
                match ProjectPaths.resolveProjectRoot projectName rt with
                | Error error -> Error error
                | Ok(FolderPath root) ->
                    let path = pathForRoot root
                    let initial =
                        withConfigLock path (fun () ->
                            readStored path
                            |> Result.bind (fun stored ->
                                validStored root stored
                                |> Result.bind (fun current ->
                                    if current <> old then
                                        Error(Client.ValidationError
                                            " binding changed while rebinding; reload and retry.")
                                    else Ok stored)))
                    initial
                    |> Result.bind (fun stored ->
                        let newBinding =
                            { old with RemoteName = remoteName
                                       ApprovedFetchUrl = approvedFetchUrl
                                       ApprovedPushUrl = approvedPushUrl }
                        // No network or Git operations may run while holding the
                        // cross-process trust-binding lock.
                        fetch newBinding
                        |> Result.mapError LedgerErrors.toEffect
                        |> Result.bind (fun snapshot ->
                            LedgerStore.LedgerStore.verifyTrustedHead newBinding
                                stored.HighestTrustedHead snapshot.Commit
                            |> Result.mapError LedgerErrors.toEffect
                            |> Result.bind (fun () ->
                                LedgerStore.LedgerStore.verifyTrustedHead newBinding
                                    expectedTrustedHead snapshot.Commit
                                |> Result.mapError LedgerErrors.toEffect
                                |> Result.bind (fun () ->
                                    withConfigLock path (fun () ->
                                        readStored path
                                        |> Result.bind (fun current ->
                                            validStored root current
                                            |> Result.bind (fun currentBinding ->
                                                if current <> stored || currentBinding <> old then
                                                    Error(Client.ValidationError
                                                        "Trusted binding changed while rebinding; reload and retry.")
                                                else
                                                    let replacement =
                                                        { current with RemoteName = remoteName
                                                                       ApprovedFetchUrl = approvedFetchUrl
                                                                       ApprovedPushUrl = approvedPushUrl
                                                                       HighestTrustedHead = snapshot.Commit }
                                                    let backup =
                                                        path + ".previous-" + Guid.NewGuid().ToString("N")
                                                    try
                                                        File.WriteAllText(backup, JsonSerializer.Serialize(current))
                                                        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                                                            File.SetUnixFileMode(backup,
                                                                UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                                                        writeStored path replacement true
                                                        |> Result.map (fun () -> replacement)
                                                    with ex ->
                                                        Error(Client.ValidationError
                                                            $" rebind backup failed: {ex.Message}"))))))))

    let rebindRemote projectName rt remoteName approvedFetchUrl approvedPushUrl
                     expectedTrustedHead acknowledgeVisibility =
        rebindRemoteUsing LedgerStore.LedgerStore.fetch projectName rt remoteName
            approvedFetchUrl approvedPushUrl expectedTrustedHead acknowledgeVisibility


[<CLIMutable>]
type PendingIntent =
    { SchemaVersion: int
      OperationId: string
      ProjectId: string
      LedgerId: string
      Epoch: int
      CommandType: string
      CommandJson: string
      CommandSha256: string
      CreatedUtc: string }

// Strictly typed, snapshot-pinned proof that a saved command is obsolete.
// These fields are stored in the immutable local tombstone, never on Git.
type PendingTaskObsolescence =
    | TaskAlreadyExists = 1
    | TaskVersionAdvanced = 2
    | AssignmentAdvanced = 3
    | TaskClosed = 4

type PendingReviewObsolescence =
    | ReviewAlreadyExists = 1
    | ReviewVersionAdvanced = 2
    | ReviewTerminal = 3

type PendingObsoleteTaskProof =
    { Rule: PendingTaskObsolescence
      TaskId: string
      OriginalAction: string
      OriginalExpectedVersion: int
      OriginalAssignmentGeneration: int
      ObservedVersion: int
      ObservedLifecycle: string
      ObservedAttemptId: string
      ObservedOwnerId: string
      ObservedAssignmentGeneration: int }

type PendingObsoleteReviewProof =
    { Rule: PendingReviewObsolescence
      TaskId: string
      ReviewId: string
      OriginalAction: string
      OriginalExpectedVersion: int
      ObservedVersion: int
      ObservedStatus: string
      ObservedOwnerId: string
      ObservedReviewerId: string }

// This records an explicit operator disposition of an uncertain local intent.
// It does not assert that the original remote push failed or create a receipt.
type SupersededPendingIntent =
    { SchemaVersion: int
      Original: PendingIntent
      OperatorId: string
      Reason: string
      RelatedOperationId: string
      RelatedRequestHash: string
      RelatedOperationKind: string
      RelatedSequence: int64
      RelatedOriginCommit: string
      // Schema 1 uses a distinct Applied receipt; schema 2 uses exactly one
      // current canonical entity proof. Null fields are not interchangeable.
      ObsoleteTask: PendingObsoleteTaskProof
      ObsoleteReview: PendingObsoleteReviewProof
      VerifiedLedgerHead: string
      RecordedUtc: string }

[<RequireQualifiedAccess>]
type PendingDisposition =
    | Applied
    | IdentityMismatch
    | NotFoundAtVerifiedHead
    | BindingMismatch
    | MalformedLocalRecord
    | MalformedCommand
    | Superseded
    | AppliedAfterSupersession
    | IdentityMismatchAfterSupersession
    | Abandoned
    | AppliedAfterAbandonment
    | IdentityMismatchAfterAbandonment
    | ArchiveBindingMismatch
    | ArchivedMalformed
    | ReplayRejectedStale

type PendingIntentCount =
    { Name: string
      Count: int }

type PendingIntentSummary =
    { VerifiedLedgerHead: string
      Total: int
      MalformedLocalRecords: int
      ArchivedTotal: int
      ArchivedMalformed: int
      ArchivedLateApplied: int
      ArchivedIdentityMismatch: int
      ByDisposition: PendingIntentCount list
      ByAction: PendingIntentCount list
      ByActor: PendingIntentCount list
      ByAge: PendingIntentCount list }

// Returned by the compact page API; never contains the stored command JSON,
// and in particular never exposes historic SessionId tokens.
type PendingIntentBrief =
    { LocalFileKey: string
      OperationId: string
      Action: string
      Actor: string
      TaskId: string option
      CreatedUtc: string
      CommandSha256: string
      Disposition: string }

type PendingIntentPage =
    { VerifiedLedgerHead: string
      Records: PendingIntentBrief list
      NextCursor: string option }

type PendingIntentInspection =
    { OperationId: string
      PresentLocally: bool
      VerifiedLedgerHead: string
      Disposition: string option
      Record: PendingIntent option
      Archive: SupersededPendingIntent option }

// Local crash recovery is an intent journal, not a second ledger writer.
// The authoritative operation outcome remains on the remote orphan ref.
module PendingIntentJournal =
    // Old clients may have unfinished intents carrying the prior wire command names.
    // Normalize for reconciliation without altering persisted records or their hashes.
    let normalizeCommandType value =
        match value with
        | "V2TaskWrite" -> "TaskWrite"
        | "V2ReviewWrite" -> "ReviewWrite"
        | "V2LedgerWrite" -> "LedgerWrite"
        | other -> other

    let private sha (content: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(content))
        |> Convert.ToHexString

    // A flushed file plus an atomic rename is not by itself a durable
    // archive tombstone after sudden power loss: the directory entry must
    // also reach stable storage BEFORE deleting the original pending intent.
    [<System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openDirectoryForSync(string path, int flags)

    [<System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int private syncDirectoryFd(int fd)

    [<System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int private closeDirectoryFd(int fd)

    [<System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "MoveFileExW",
                                                 CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                                                 SetLastError = true)>]
    extern bool private moveArchiveWriteThrough(string source, string destination, uint32 flags)

    let private syncArchiveDirectory folder =
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            // O_RDONLY = 0 on POSIX. Opening an existing directory with this
            // flag is supported on Linux and macOS; no write handle needed.
            let fd = openDirectoryForSync(folder, 0)
            if fd < 0 then
                let err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError()
                raise (IOException($"Cannot open archived-intent directory for durable sync (errno {err})."))
            try
                if syncDirectoryFd(fd) <> 0 then
                    let err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError()
                    raise (IOException($"Cannot fsync archived-intent directory (errno {err})."))
            finally
                closeDirectoryFd(fd) |> ignore

    let private durableArchiveMove temporary destination folder =
        if OperatingSystem.IsWindows() then
            // MOVEFILE_WRITE_THROUGH = 0x00000008. No
            // MOVEFILE_REPLACE_EXISTING: an audit UUID is never overwritten.
            if not (moveArchiveWriteThrough(temporary, destination, 0x00000008u)) then
                let err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError()
                raise (IOException($"Cannot durably publish archive tombstone (Win32 error {err})."))
        else
            File.Move(temporary, destination, false)
            syncArchiveDirectory folder

    let private journalDirectory root =
        let bindingPath = LedgerBindingConfig.pathForRoot root
        Path.Combine(Path.GetDirectoryName bindingPath, "pending",
                     Path.GetFileNameWithoutExtension bindingPath)

    let private filePath (binding: LedgerStore.LedgerBinding) (operationId: Guid) =
        Path.Combine(journalDirectory binding.RepositoryPath, operationId.ToString("N") + ".json")

    // At most 4096 persistent lock stripes, regardless of journal history.
    // FileShare.None serializes cooperating clients across processes.
    // Lock files are never unlinked (unlinking a held inode is unsafe on Unix).
    let private withIntentLock (binding: LedgerStore.LedgerBinding) (operationId: Guid)
                               (operation: unit -> Result<'a, EffectError>) =
        try
            if operationId = Guid.Empty then
                Error(Client.ValidationError "Valid pending operation UUID required.")
            else
                let journal = journalDirectory binding.RepositoryPath
                Directory.CreateDirectory(journal) |> ignore
                if DirectoryInfo(journal).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                    Error(Client.ValidationError "Pending journal directory must not be a symbolic link.")
                else
                    let folder = Path.Combine(journal, ".locks")
                    Directory.CreateDirectory(folder) |> ignore
                    if DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                        Error(Client.ValidationError "Pending lock directory must not be a symbolic link.")
                    else
                        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                            File.SetUnixFileMode(folder, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                        let slot = operationId.ToString("N").Substring(0, 3)
                        let lockFile = Path.Combine(folder, slot + ".lock")
                        if File.Exists(lockFile) && File.GetAttributes(lockFile).HasFlag(FileAttributes.ReparsePoint) then
                            Error(Client.ValidationError "Pending intent lock must not be a symbolic link.")
                        else
                            let rec acquire attempt =
                                try
                                    new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                                   FileShare.None, 4096, FileOptions.None)
                                with :? IOException when attempt < 1500 ->
                                    System.Threading.Thread.Sleep(40)
                                    acquire (attempt + 1)
                            use held = acquire 0
                            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                                File.SetUnixFileMode(lockFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                            // Never retry operation() itself on IOException:
                            // an uncertain remote write must not be repeated.
                            operation ()
        with ex ->
            Error(Client.ValidationError $"Unable to exclusively lock pending intent: {ex.Message}")

    let private readFile (file: string) =
        try
            if File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)
               || FileInfo(file).Length > 1024L * 1024L then
                Error(Client.ValidationError "Unsafe or oversized  pending intent.")
            else
                let record = JsonSerializer.Deserialize<PendingIntent>(File.ReadAllText(file))
                if isNull (box record) || record.SchemaVersion <> 1
                   || String.IsNullOrWhiteSpace record.OperationId
                   || String.IsNullOrWhiteSpace record.CommandType
                   || String.IsNullOrWhiteSpace record.CommandJson
                   || not (String.Equals(record.CommandSha256, sha record.CommandJson, StringComparison.Ordinal)) then
                    Error(Client.ValidationError "Invalid local pending  operation journal.")
                else Ok record
        with ex ->
            Error(Client.ValidationError $"Cannot read local pending operation: {ex.Message}")

    let private readCheckedFile file =
        readFile file
        |> Result.bind (fun record ->
            match Guid.TryParse record.OperationId with
            | true, id when Path.GetFileName(file) = id.ToString("N") + ".json" ->
                Ok record
            | _ -> Error(Client.ValidationError "Unexpected pending journal filename or operation UUID."))

    let private archiveDirectory root =
        Path.Combine(journalDirectory root, "archive")

    let private archivePath (binding: LedgerStore.LedgerBinding) (operationId: Guid) =
        Path.Combine(archiveDirectory binding.RepositoryPath,
                     operationId.ToString("N") + ".json")

    let private readArchiveFile (path: string) =
        try
            if File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
               || FileInfo(path).Length > 2L * 1024L * 1024L then
                Error(Client.ValidationError "Unsafe or oversized archived pending intent.")
            else
                let record = JsonSerializer.Deserialize<SupersededPendingIntent>(File.ReadAllText(path))
                let validEvidence =
                    if isNull (box record) then false
                    elif record.SchemaVersion = 1 then
                        isNull (box record.ObsoleteTask)
                        && isNull (box record.ObsoleteReview)
                        && not (String.IsNullOrWhiteSpace record.RelatedRequestHash)
                        && not (String.IsNullOrWhiteSpace record.RelatedOperationId)
                    elif record.SchemaVersion = 2 then
                        let task = not (isNull (box record.ObsoleteTask))
                        let review = not (isNull (box record.ObsoleteReview))
                        task <> review
                        && String.IsNullOrEmpty record.RelatedOperationId
                        && String.IsNullOrEmpty record.RelatedRequestHash
                        && (if task then
                                let proof = record.ObsoleteTask
                                not (String.IsNullOrWhiteSpace proof.TaskId)
                                && proof.ObservedVersion > 0
                                && Enum.IsDefined(proof.Rule)
                            else
                                let proof = record.ObsoleteReview
                                not (String.IsNullOrWhiteSpace proof.TaskId)
                                && not (String.IsNullOrWhiteSpace proof.ReviewId)
                                && proof.ObservedVersion > 0
                                && Enum.IsDefined(proof.Rule))
                    else false
                if not validEvidence
                   || isNull (box record.Original)
                   || record.Original.SchemaVersion <> 1
                   || String.IsNullOrWhiteSpace record.OperatorId
                   || String.IsNullOrWhiteSpace record.Reason
                   || not (String.Equals(record.Original.CommandSha256,
                                         sha record.Original.CommandJson, StringComparison.Ordinal)) then
                    Error(Client.ValidationError "Invalid archived pending intent audit record.")
                else
                    match Guid.TryParse record.Original.OperationId with
                    | true, id when Path.GetFileName(path) = id.ToString("N") + ".json" -> Ok record
                    | _ -> Error(Client.ValidationError "Invalid archived pending intent operation UUID.")
        with ex ->
            Error(Client.ValidationError $"Cannot read archived pending intent: {ex.Message}")

    let private findArchive (binding: LedgerStore.LedgerBinding) operationId =
        let path = archivePath binding operationId
        if File.Exists path then readArchiveFile path |> Result.map Some
        else Ok None

    let private archiveFiles root =
        try
            let folder = archiveDirectory root
            if not (Directory.Exists folder) then Ok [||]
            elif DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                Error(Client.ValidationError "Pending intent archive directory must not be a symbolic link.")
            else Ok (Directory.GetFiles(folder, "*.json") |> Array.sort)
        with ex ->
            Error(Client.ValidationError $"Cannot list pending intent archives: {ex.Message}")

    // The audit entry is durably created BEFORE removing the mutable pending
    // record. A crash between those steps leaves both files, which is safe:
    // the immutable archive is the tombstone; an operator can finish cleanup.
    let private createArchive (binding: LedgerStore.LedgerBinding) operationId audit =
        try
            let folder = archiveDirectory binding.RepositoryPath
            Directory.CreateDirectory(folder) |> ignore
            if DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                Error(Client.ValidationError "Pending archive directory must not be a symbolic link.")
            else
                if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                    File.SetUnixFileMode(folder, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                let destination = archivePath binding operationId
                if File.Exists destination then
                    Error(Client.ValidationError "Pending intent archive already exists.")
                else
                    let temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp"
                    try
                        do
                            use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                                        FileShare.None, 4096, FileOptions.WriteThrough)
                            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                                File.SetUnixFileMode(temporary, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                            let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(audit))
                            stream.Write(bytes, 0, bytes.Length)
                            stream.Flush(true)
                        // Close before rename: Windows FileShare.None prevents
                        // moving an open stream. Never overwrite a prior audit.
                        // Only return success after directory metadata is durable.
                        durableArchiveMove temporary destination folder
                        Ok ()
                    finally
                        if File.Exists temporary then File.Delete temporary
        with ex ->
            Error(Client.ValidationError $"Cannot archive pending intent safely: {ex.Message}")

    let private persistUnsafe (binding: LedgerStore.LedgerBinding) operationId commandType commandJson =
        let file = filePath binding operationId
        let newRecord =
            { SchemaVersion = 1
              OperationId = operationId.ToString("D")
              ProjectId = binding.ProjectId.ToString("D")
              LedgerId = binding.LedgerId.ToString("D")
              Epoch = binding.Epoch
              CommandType = commandType
              CommandJson = commandJson
              CommandSha256 = sha commandJson
              CreatedUtc = DateTimeOffset.UtcNow.ToString("O") }
        let matches (existing: PendingIntent) =
            existing.OperationId = newRecord.OperationId
            && existing.ProjectId = newRecord.ProjectId
            && existing.LedgerId = newRecord.LedgerId
            && existing.Epoch = newRecord.Epoch
            && normalizeCommandType existing.CommandType = normalizeCommandType newRecord.CommandType
            && existing.CommandSha256 = newRecord.CommandSha256
            && existing.CommandJson = newRecord.CommandJson
        try
            if File.Exists(archivePath binding operationId) then
                Error(Client.ValidationError "Pending intent was explicitly superseded; archived UUID cannot be reused.")
            elif File.Exists file then
                readFile file |> Result.bind (fun existing ->
                    if matches existing then Ok ()
                    else Error(Client.ValidationError "Pending  operation UUID reused with changed input."))
            else
                let directory = Path.GetDirectoryName file
                Directory.CreateDirectory(directory) |> ignore
                if DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                    Error(Client.ValidationError "Pending  journal directory must not be a symbolic link.")
                else
                    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                        File.SetUnixFileMode(directory,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                    let temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp"
                    try
                        File.WriteAllText(temp, JsonSerializer.Serialize(newRecord), UTF8Encoding(false))
                        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                            File.SetUnixFileMode(temp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                        try File.Move(temp, file, false)
                        with :? IOException when File.Exists file -> ()
                        readFile file |> Result.bind (fun existing ->
                            if matches existing then Ok ()
                            else Error(Client.ValidationError "Pending  operation UUID reused with changed input."))
                    finally
                        if File.Exists temp then File.Delete temp
        with ex ->
            Error(Client.ValidationError $"Cannot persist local pending  operation: {ex.Message}")

    let persist binding operationId commandType commandJson =
        withIntentLock binding operationId (fun () ->
            persistUnsafe binding operationId commandType commandJson)

    let private journalFiles root =
        try
            let directory = journalDirectory root
            if not (Directory.Exists directory) then Ok [||]
            elif DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                Error(Client.ValidationError "Pending  journal directory must not be a symbolic link.")
            else
                let files = Directory.GetFiles(directory, "*.json")
                Array.sortInPlace files
                Ok files
        with ex ->
            Error(Client.ValidationError $"Cannot enumerate pending  operations: {ex.Message}")

    let listAtRoot root =
        journalFiles root
        |> Result.bind (fun files ->
            try
                if files.Length > 1000 then
                    Error(Client.ValidationError "Too many pending operations; manual recovery required.")
                else
                    files
                    |> Array.fold (fun state file ->
                        state |> Result.bind (fun items ->
                            readCheckedFile file |> Result.map (fun record -> record :: items)))
                        (Ok [])
                    |> Result.map List.rev
            with ex ->
                Error(Client.ValidationError $"Cannot read pending  operations: {ex.Message}"))

    // Exact UUID lookup stays usable even when the legacy full-list endpoint
    // encounters more than 1,000 records. No command or session metadata is
    // inferred from the current caller.
    let findAtRoot root (operationId: Guid) =
        let path = Path.Combine(journalDirectory root, operationId.ToString("N") + ".json")
        if not (File.Exists path) then Ok None
        else
            readFile path
            |> Result.bind (fun record ->
                if record.OperationId = operationId.ToString("D") then Ok(Some record)
                else Error(Client.ValidationError "Local intent filename and operation UUID disagree."))

    // Opaque filename cursor. A page reads at most 100 full commands and reports malformed entries.
    // records, and an individual intent remains inspectable by UUID.
    let pageAtRoot root (cursor: string) (pageSize: int) =
        if pageSize < 1 || pageSize > 100 then
            Error(Client.ValidationError "Pending intent pageSize must be between 1 and 100.")
        elif not (String.IsNullOrWhiteSpace cursor)
             && (cursor.Length > 255 || cursor.Contains('/') || cursor.Contains('\\')
                 || cursor.Contains(char 0)) then
            Error(Client.ValidationError "Invalid pending journal filename cursor.")
        else
            journalFiles root
            |> Result.bind (fun files ->
                let selected =
                    files
                    |> Array.filter (fun path ->
                        String.IsNullOrWhiteSpace cursor
                        || String.CompareOrdinal(Path.GetFileNameWithoutExtension(path), cursor) > 0)
                    |> Array.truncate (pageSize + 1)
                let entries =
                    selected
                    |> Array.truncate pageSize
                    |> Array.map (fun file -> Path.GetFileNameWithoutExtension(file), readCheckedFile file)
                    |> Array.toList
                let nextCursor =
                    if selected.Length > pageSize then entries |> List.tryLast |> Option.map fst
                    else None
                Ok (entries, nextCursor))

    let listForProject projectName rt =
        ProjectPaths.resolveProjectRoot projectName rt
        |> Result.bind (fun (FolderPath root) -> listAtRoot root)

    // Check a pending command against the exact accepted operation kind and its
    // primary identities. A matching UUID by itself is never proof of the
    // requested operation. More precise caller review is required before
    // deleting local evidence.
    let private commandProperty (root: JsonElement) (name: string) =
        root.EnumerateObject()
        |> Seq.tryFind (fun prop -> String.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun prop -> prop.Value.ToString())

    let private operationKind (record: PendingIntent) =
        use doc = JsonDocument.Parse(record.CommandJson)
        let root = doc.RootElement
        let action = commandProperty root "Action"
        match normalizeCommandType record.CommandType, action with
        | "TaskWrite", Some "AcknowledgeFeedback" -> Some "AcknowledgeBranchReview"
        | "TaskWrite", Some action -> Some action
        | "ReviewWrite", Some "PutDocument" -> Some "PutReviewDocument"
        | "ReviewWrite", Some "Submit" -> Some "SubmitBranchReview"
        | "ReviewWrite", Some "Update" -> Some "ReplaceBranchReview"
        | "ReviewWrite", Some "Claim" -> Some "ClaimBranchReview"
        | "ReviewWrite", Some "Decide" -> Some "DecideBranchReview"
        | "ReviewWrite", Some "Withdraw" -> Some "WithdrawBranchReview"
        | "ReviewWrite", Some "Release" -> Some "ReleaseBranchReview"

        | "ReviewWrite", Some "AcknowledgeFeedback" -> Some "AcknowledgeBranchReview"
        | _ -> None

    let private identityMatches (record: PendingIntent) (operation: LedgerStore.LedgerOperation) =
        try
            use local = JsonDocument.Parse(record.CommandJson)
            use full = JsonDocument.Parse(operation.CanonicalRequestJson)
            let requestText = full.RootElement.GetProperty("request").GetString()
            use request = JsonDocument.Parse(requestText)
            let own = local.RootElement
            let remote = request.RootElement

            let pairs : (string * string) list =
                let common = [ "TaskId", "taskId" ]
                let owner = common @ [ "ActorId", "ownerId" ]
                let attempt = owner @ [ "ExpectedVersion", "expectedAttemptVersion"
                                        "ExpectedAgentVersion", "expectedAgentVersion"
                                        "AssignmentGeneration", "generation" ]
                let review = common @ [ "RequestId", "requestId" ]
                let reviewer = review @ [ "ActorId", "reviewerId"
                                          "ExpectedVersion", "expectedVersion" ]

                match normalizeCommandType record.CommandType, operation.Type with
                | "TaskWrite", "CreateTask" ->
                    common @ [ "Title", "title"; "Html", "specSha256" ]
                | "TaskWrite", "ReviseTaskSpec" ->
                    common @ [ "Title", "title"; "Html", "specSha256"
                               "ExpectedVersion", "expectedTaskVersion" ]
                | "TaskWrite", "ReviseTaskSpecPinned" ->
                    common @ [ "Title", "title"; "Html", "specSha256"
                               "ExpectedVersion", "expectedTaskVersion"; "BaseOid", "expectedSpecBlobOid" ]
                | "TaskWrite", "ImportTaskSpec" ->
                    common @ [ "ExpectedVersion", "expectedTaskVersion"; "BaseOid", "expectedSpecBlobOid"
                               "TargetRef", "expectedSourceBlobOidInput"; "SourceRef", "sourceRelativePath"
                               "Title", "title" ]
                | "TaskWrite", "AdoptTaskSpec" ->
                    attempt @ [ "ExpectedTaskVersion", "expectedTaskVersion"
                                "BaseOid", "expectedCurrentSpecOid"; "Summary", "instructions" ]
                | "TaskWrite", "RegisterAgent" -> [ "ActorId", "agentId" ]
                | "TaskWrite", (("StartTasklessWork" | "YieldTasklessWork") as action) ->
                    [ "ActorId", "agentId"; "ExpectedAgentVersion", "expectedAgentVersion"
                      "Summary", (if action = "StartTasklessWork" then "activity" else "summary") ]
                | "TaskWrite", "AssignTask" ->
                    common @ [ "AttemptId", "attemptId"; "OwnerId", "ownerId" ]
                | "TaskWrite", "AcknowledgeAssignment" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"; "AssignmentGeneration", "generation" ]
                | "TaskWrite", "ReissueAssignment" ->
                    owner @ [ "ExpectedVersion", "expectedAttemptVersion"
                              "ExpectedAgentVersion", "expectedAgentVersion"
                              "AssignmentGeneration", "generation"
                              "Summary", "instructions"; "Html", "specSha256" ]
                | "TaskWrite", "BeginAttempt" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation" ]
                | "TaskWrite", "BindReviewBase" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation"
                              "BaseOid", "baseOid"; "TargetRef", "targetRef" ]
                | "TaskWrite", "BindAttemptSource" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation"
                              "BaseOid", "baseOid"; "TargetRef", "targetRef"
                              "SourceRef", "sourceRef"; "AllowDetached", "allowDetached"
                              "AllowUncommitted", "allowUncommitted" ]
                | "TaskWrite", "RebindAttemptSource" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation"
                              "SourceRef", "sourceRef" ]
                | "TaskWrite", "CheckpointAttempt" ->
                    attempt @ [ "Summary", "summary"; "NextAction", "nextAction"
                                "BaseOid", "sourceCommit"; "Evidence", "evidence" ]
                | "TaskWrite", "YieldAgent" ->
                    attempt @ [ "Summary", "summary"; "NextAction", "nextAction" ]
                | "TaskWrite", "ResumeAgent" -> attempt
                | "TaskWrite", "CompleteAgentWork" ->
                    attempt @ [ "Summary", "summary" ]
                | "TaskWrite", "SetAgentState" ->
                    [ "ActorId", "targetAgentId"; "Summary", "state"
                      "ExpectedAgentVersion", "expectedAgentVersion"; "Reason", "reason" ]
                | "TaskWrite", "RecoverTasklessWork" ->
                    [ "ActorId", "agentId"; "ExpectedAgentVersion", "expectedAgentVersion"; "Reason", "reason" ]
                | "TaskWrite", "ReissueQueuedAttempt"
                | "TaskWrite", "RecoverAgentAssignment" ->
                    attempt @ [ "Reason", "reason" ]
                | "TaskWrite", ("ParkAttempt" | "BlockAttempt" | "ResumeAttempt"
                                   | "AbandonAttempt" | "CancelTask") ->
                    attempt @ [ "ExpectedTaskVersion", "expectedTaskVersion";
                                "Reason", "reason" ]
                | "TaskWrite", "CancelOpenTask" ->
                    common @ [ "ExpectedTaskVersion", "expectedTaskVersion"; "Reason", "reason" ]
                | "TaskWrite", "TransferOwner" ->
                    attempt @ [ "ExpectedTaskVersion", "expectedTaskVersion";
                                "AttemptId", "newAttemptId"; "OwnerId", "newOwner"; "Reason", "reason" ]
                | "TaskWrite", "CloseTask" ->
                    common @ [ "ExpectedTaskVersion", "expectedTaskVersion" ]
                | "TaskWrite", "ReopenTask" ->
                    common @ [ "ExpectedTaskVersion", "expectedTaskVersion"; "Reason", "reason" ]
                | "TaskWrite", "AcknowledgeReviewFeedback" ->
                    owner @ [ "DecisionSequence", "decisionSequence" ]
                | "ReviewWrite", "PutReviewDocument" ->
                    common @ [ "RequestId", "requestId"; "ActorId", "actorId"
                               "DocumentPath", "documentPath"; "Content", "contentSha256"
                               "ExpectedBlobOid", "expectedBlobOid" ]
                | "ReviewWrite", "SubmitBranchReview" ->
                    common @ [ "RequestId", "reviewId"; "ActorId", "ownerId"
                               "SourceRef", "sourceRef"; "HeadOid", "sourceHead"
                               "TargetRef", "targetRef"; "Summary", "summary"; "Kind", "kind" ]
                | "ReviewWrite", "ReplaceBranchReview" ->
                    common @ [ "RequestId", "reviewId"; "ActorId", "requesterId"
                               "ExpectedVersion", "expectedVersion"; "SourceRef", "sourceRef"
                               "HeadOid", "sourceHead"; "Summary", "summary" ]
                | "ReviewWrite", "ClaimBranchReview" ->
                    common @ [ "RequestId", "reviewId"; "ActorId", "reviewerId"
                               "ExpectedVersion", "expectedVersion" ]
                | "ReviewWrite", "DecideBranchReview" ->
                    common @ [ "RequestId", "reviewId"; "ActorId", "reviewerId"
                               "ExpectedVersion", "expectedVersion"; "Verdict", "status" ]
                | "ReviewWrite", ("WithdrawBranchReview" | "ReleaseBranchReview") ->
                    common @ [ "RequestId", "reviewId"; "ActorId", "actorId"
                               "ExpectedVersion", "expectedVersion" ]
                | _ -> []

            let equalField (source, dest) =
                match commandProperty own source, commandProperty remote dest with
                | Some localValue, Some remoteValue ->
                    let localValue =
                        if source = "Verdict" then
                            match localValue with
                            | "Accept" -> "Accepted"
                            | "RequestChanges" -> "ChangesRequested"
                            | other -> other
                        elif (source = "Html" && dest = "specSha256") || (source = "Content" && dest = "contentSha256") then
                            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(if isNull localValue then "" else localValue))
                            |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
                        else localValue
                    match Guid.TryParse localValue, Guid.TryParse remoteValue with
                    | (true, a), (true, b) -> a = b
                    | _ -> localValue = remoteValue
                | localValue, remoteValue ->
                    // Older pending commands predate explicit evidence fields.
                    // Missing evidence is equivalent only to a blank/false
                    // declaration, never to a newly reported test or inspection.
                    let optionalEmpty value =
                        match value with
                        | None -> true
                        | Some raw -> String.IsNullOrWhiteSpace raw
                                      || String.Equals(raw, "False", StringComparison.OrdinalIgnoreCase)
                    ((List.contains source [ "TestEvidence"; "Inspected"; "Tested"; "TestDetails" ]
                      && optionalEmpty localValue && optionalEmpty remoteValue)
                     || (source = "Feedback" && dest = "reason"
                         && operation.Type = "ReassignReviewer"
                         && optionalEmpty localValue && optionalEmpty remoteValue))

            operationKind record = Some operation.Type
            && not (List.isEmpty pairs)
            && List.forall equalField pairs
        with _ -> false

    let private checkBinding (binding: LedgerStore.LedgerBinding) (record: PendingIntent) =
        record.ProjectId = binding.ProjectId.ToString("D")
        && record.LedgerId = binding.LedgerId.ToString("D")
        && record.Epoch = binding.Epoch

    // Remote absence remains uncertain. A matching operation receipt is
    // authoritative only if the exact action and required identities match.
    let private classifyAt binding snapshot (knownIds: Set<string>) (record: PendingIntent) =
        if not (checkBinding binding record) then Ok PendingDisposition.BindingMismatch
        else
            match Guid.TryParse record.OperationId with
            | false, _ -> Error(Client.ValidationError "Invalid pending operation UUID.")
            | true, id when not (knownIds.Contains(id.ToString("N"))) ->
                Ok PendingDisposition.NotFoundAtVerifiedHead
            | true, id ->
                LedgerStore.LedgerStore.getOperationAt binding snapshot id
                |> Result.mapError LedgerErrors.toEffect
                |> Result.map (function
                    | Some op when identityMatches record op -> PendingDisposition.Applied
                    | Some _ -> PendingDisposition.IdentityMismatch
                    | None -> PendingDisposition.NotFoundAtVerifiedHead)

    // Parse only the bounded metadata needed for triage; old SessionId values
    // remain in the exact command JSON, exposed only via targeted inspection.
    let private metadata (record: PendingIntent) =
        try
            use doc = JsonDocument.Parse(record.CommandJson)
            let root = doc.RootElement
            if root.ValueKind <> JsonValueKind.Object then None
            else
                let field key = commandProperty root key |> Option.defaultValue ""
                let action = field "Action"
                if String.IsNullOrWhiteSpace action then None
                else
                    let actor =
                        [ "ActorId"; "ReviewerId"; "OwnerId" ]
                        |> List.map field
                        |> List.tryFind (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultValue "(unspecified)"
                    let task = field "TaskId"
                    Some (action, actor, if String.IsNullOrWhiteSpace task then None else Some task)
        with _ -> None

    let private increment (values: System.Collections.Generic.Dictionary<string, int>) key =
        match values.TryGetValue key with
        | true, count -> values[key] <- count + 1
        | false, _ -> values[key] <- 1

    let private toCounts (values: System.Collections.Generic.Dictionary<string, int>) =
        values
        |> Seq.map (fun (KeyValue(name, count)) -> { Name = name; Count = count })
        |> Seq.sortBy (fun row -> -row.Count, row.Name)
        |> Seq.toList

    let private ageBucket (created: string) (now: DateTimeOffset) =
        match DateTimeOffset.TryParse created with
        | true, time when now < time -> "FutureTimestamp"
        | true, time when now - time < TimeSpan.FromDays 1. -> "Under24Hours"
        | true, time when now - time < TimeSpan.FromDays 7. -> "OneToSevenDays"
        | true, time when now - time < TimeSpan.FromDays 30. -> "SevenToThirtyDays"
        | true, _ -> "OlderThan30Days"
        | _ -> "InvalidTimestamp"

    let private relatedProofMatches (original: PendingIntent)
                                    (related: LedgerStore.LedgerOperation) =
        try
            use saved = JsonDocument.Parse(original.CommandJson)
            use accepted = JsonDocument.Parse(related.CanonicalRequestJson)
            let requestJson = accepted.RootElement.GetProperty("request").GetString()
            use request = JsonDocument.Parse(requestJson)
            let own = saved.RootElement
            let remote = request.RootElement
            let ownField name = commandProperty own name |> Option.defaultValue ""
            let remoteField name = commandProperty remote name |> Option.defaultValue ""
            let taskId = ownField "TaskId"
            let actor = ownField "ActorId"
            let action = ownField "Action"
            // A related operation on a different task is never proof.
            let sameTask = not (String.IsNullOrWhiteSpace taskId)
                           && String.Equals(taskId, remoteField "taskId", StringComparison.Ordinal)
            let sameActor = not (String.IsNullOrWhiteSpace actor)
                            && String.Equals(actor, related.Actor, StringComparison.Ordinal)
                            && String.Equals(actor, remoteField "ownerId", StringComparison.Ordinal)
            let sameAssignmentGeneration =
                match Int32.TryParse(ownField "AssignmentGeneration"),
                      Int32.TryParse(remoteField "generation") with
                | (true, original), (true, successor) -> original > 0 && original = successor
                | _ -> false
            let equivalentCreate =
                normalizeCommandType original.CommandType = "TaskWrite"
                && action = "CreateTask" && related.Type = "CreateTask"
                && String.Equals(ownField "Title", remoteField "title", StringComparison.Ordinal)
                && String.Equals(sha (ownField "Html"), remoteField "specSha256",
                                 StringComparison.OrdinalIgnoreCase)
            let supersededLifecycle =
                normalizeCommandType original.CommandType = "TaskWrite"
                && action = "ResumeAgent" && sameActor && sameAssignmentGeneration
                && List.contains related.Type
                    [ "BeginAttempt"; "CheckpointAttempt"; "YieldAgent"; "CompleteAgentWork" ]
            sameTask && (equivalentCreate || supersededLifecycle)
        with _ -> false

    // Operator-controlled, durable local supersession. This does not mutate
    // the remote ledger. The related UUID is required to be a verified Applied
    // receipt with an exact supplied hash and subject-specific equivalence.
    // It is deliberately narrower than a generic 'discard stale item' tool.
    let supersede (binding: LedgerStore.LedgerBinding) operationId
                  (expectedCommandHash: string) (expectedHead: string)
                  (operatorId: string) (reason: string)
                  (relatedId: Guid) (relatedRequestHash: string) =
        withIntentLock binding operationId (fun () ->
            if String.IsNullOrWhiteSpace operatorId || operatorId.Length > 128
               || String.IsNullOrWhiteSpace reason || reason.Trim().Length < 10
               || reason.Length > 2048
               || operationId = relatedId
               || String.IsNullOrWhiteSpace expectedHead
               || String.IsNullOrWhiteSpace expectedCommandHash
               || String.IsNullOrWhiteSpace relatedRequestHash then
                Error(Client.ValidationError "Provide operator, a 10-2048 character reason, original hash, pinned head, and a distinct related operation with verified hash.")
            else
                findArchive binding operationId
                |> Result.bind (fun prior ->
                    let verifyOriginal original =
                        if not (checkBinding binding original)
                           || not (String.Equals(original.CommandSha256, expectedCommandHash, StringComparison.OrdinalIgnoreCase)) then
                            Error(Client.ValidationError "Pending supersession identity or original command hash mismatch.")
                        else Ok ()
                    match prior with
                    | Some archived ->
                        verifyOriginal archived.Original
                        |> Result.bind (fun () ->
                            if archived.OperatorId <> operatorId
                               || archived.Reason <> reason
                               || archived.VerifiedLedgerHead <> expectedHead
                               || archived.RelatedOperationId <> relatedId.ToString("D")
                               || archived.RelatedRequestHash <> relatedRequestHash then
                                Error(Client.ValidationError "Archived supersession is immutable; retry must use identical evidence.")
                            else
                                // An interrupted archive-first transition may
                                // have left the pending file. Recover atomically.
                                findAtRoot binding.RepositoryPath operationId
                                |> Result.bind (function
                                    | Some current when current <> archived.Original ->
                                        Error(Client.ValidationError "Pending intent differs from archived audit; refuse cleanup.")
                                    | active ->
                                        try
                                            if active.IsSome then
                                                // An earlier rename may have succeeded but
                                                // failed its directory fsync before cleanup.
                                                syncArchiveDirectory (archiveDirectory binding.RepositoryPath)
                                                File.Delete(filePath binding operationId)
                                            Ok {| operationId = operationId.ToString("D")
                                                  alreadySuperseded = true
                                                  verifiedLedgerHead = archived.VerifiedLedgerHead
                                                  archive = archived |}
                                        with ex ->
                                            Error(Client.ValidationError $"Cannot complete pending supersession cleanup: {ex.Message}")))
                    | None ->
                        findAtRoot binding.RepositoryPath operationId
                        |> Result.bind (function
                            | None -> Error(Client.ValidationError "No local pending intent exists for this operation.")
                            | Some original ->
                                verifyOriginal original
                                |> Result.bind (fun () ->
                                    LedgerStore.LedgerStore.fetch binding
                                    |> Result.mapError LedgerErrors.toEffect
                                    |> Result.bind (fun snapshot ->
                                        if snapshot.Commit <> expectedHead then
                                            Error(Client.ValidationError "Verified ledger head changed since inspection; inspect intent again.")
                                        else
                                            LedgerStore.LedgerStore.getOperationAt binding snapshot operationId
                                            |> Result.mapError LedgerErrors.toEffect
                                            |> Result.bind (function
                                                | Some _ ->
                                                    Error(Client.ValidationError "Original operation UUID is present at verified head; inspect/acknowledge it, do not supersede.")
                                                | None ->
                                                    LedgerStore.LedgerStore.getOperationAt binding snapshot relatedId
                                                    |> Result.mapError LedgerErrors.toEffect
                                                    |> Result.bind (function
                                                        | Some related when
                                                            String.Equals(related.RequestHash, relatedRequestHash, StringComparison.OrdinalIgnoreCase)
                                                            && relatedProofMatches original related ->
                                                            LedgerStore.LedgerStore.getOperationOriginAt binding snapshot related
                                                            |> Result.mapError LedgerErrors.toEffect
                                                            |> Result.bind (fun origin ->
                                                                let audit =
                                                                    { SchemaVersion = 1; Original = original
                                                                      OperatorId = operatorId; Reason = reason
                                                                      RelatedOperationId = relatedId.ToString("D")
                                                                      RelatedRequestHash = related.RequestHash
                                                                      RelatedOperationKind = related.Type
                                                                      RelatedSequence = related.Sequence
                                                                      RelatedOriginCommit = origin
                                                                      ObsoleteTask = Unchecked.defaultof<_>
                                                                      ObsoleteReview = Unchecked.defaultof<_>
                                                                      VerifiedLedgerHead = snapshot.Commit
                                                                      RecordedUtc = DateTimeOffset.UtcNow.ToString("O") }
                                                                createArchive binding operationId audit
                                                                |> Result.bind (fun () ->
                                                                    try
                                                                        File.Delete(filePath binding operationId)
                                                                        Ok {| operationId = operationId.ToString("D")
                                                                              alreadySuperseded = false
                                                                              verifiedLedgerHead = snapshot.Commit
                                                                              archive = audit |}
                                                                    with ex ->
                                                                        Error(Client.ValidationError $"Archive is durable; pending cleanup incomplete: {ex.Message}")))
                                                        | _ ->
                                                            Error(Client.ValidationError "Related operation is not a matching verified Applied receipt for the same task and lifecycle action."))))))))


    // Only snapshot-observable state which makes the ORIGINAL saved command
    // inapplicable is a valid abandonment proof. Missing remote UUID is
    // uncertainty, never proof. Parse the narrow historical fields required
    // for each action rather than reconstituting removed public APIs.
    let private obsoleteStateProof binding (snapshot: LedgerStore.LedgerSnapshot) (original: PendingIntent) =
        try
            use doc = JsonDocument.Parse(original.CommandJson)
            let root = doc.RootElement
            let field name = commandProperty root name |> Option.defaultValue ""
            let positive name =
                match Int32.TryParse(field name) with
                | true, value when value > 0 -> Some value
                | _ -> None
            let taskId = field "TaskId"
            let action = field "Action"
            if String.IsNullOrWhiteSpace taskId
               || String.IsNullOrWhiteSpace action then
                Error(Client.ValidationError "Saved command does not identify a task and operation; cannot establish obsolescence.")
            else
                match normalizeCommandType original.CommandType with
                | "TaskWrite" ->
                    ReviewWorkflow.ReviewWorkflow.getTaskAt binding snapshot taskId
                    |> Result.mapError LedgerErrors.toEffect
                    |> Result.bind (fun (task, attempt) ->
                        if task.TaskId <> taskId then
                            Error(Client.ValidationError "Canonical task snapshot changed or task identity mismatched; inspect again.")
                        else
                            let generation = positive "AssignmentGeneration"
                            let originalVersion =
                                if action = "ReviseTaskSpec" then positive "ExpectedVersion"
                                else positive "ExpectedTaskVersion"
                            let advanced =
                                match originalVersion with
                                | Some before when task.Version > before && List.contains action
                                    [ "ReviseTaskSpec"; "CloseTask"; "CancelOpenTask"; "ReopenTask" ] ->
                                    Some PendingTaskObsolescence.TaskVersionAdvanced
                                | _ -> None
                            let assignmentAdvanced =
                                match generation, attempt with
                                | Some before, Some current
                                    when action = "ResumeAgent" && current.AssignmentGeneration > before ->
                                    Some PendingTaskObsolescence.AssignmentAdvanced
                                | _ -> None
                            let taskClosed =
                                if action = "ResumeAgent" && generation.IsSome
                                   && List.contains task.Lifecycle [ "Closed"; "Cancelled" ] then
                                    Some PendingTaskObsolescence.TaskClosed
                                else None
                            let rule =
                                if action = "CreateTask" && task.Version >= 1 then
                                    Some PendingTaskObsolescence.TaskAlreadyExists
                                else
                                    advanced |> Option.orElse assignmentAdvanced |> Option.orElse taskClosed
                            match rule with
                            | None -> Error(Client.ValidationError
                                        "No verified task state makes this exact saved action obsolete; preserve the pending intent.")
                            | Some rule ->
                                let observedAttemptId, observedOwner, observedGeneration =
                                    match attempt with
                                    | Some current ->
                                        current.AttemptId, current.OwnerAgentId, current.AssignmentGeneration
                                    | None -> "", "", 0
                                let proof: PendingObsoleteTaskProof =
                                    { Rule = rule
                                      TaskId = taskId; OriginalAction = action
                                      OriginalExpectedVersion = originalVersion |> Option.defaultValue 0
                                      OriginalAssignmentGeneration = generation |> Option.defaultValue 0
                                      ObservedVersion = task.Version
                                      ObservedLifecycle = task.Lifecycle
                                      ObservedAttemptId = observedAttemptId
                                      ObservedOwnerId = observedOwner
                                      ObservedAssignmentGeneration = observedGeneration }
                                Ok (Choice1Of2 proof))
                | "ReviewWrite" ->
                    match Guid.TryParse(field "RequestId") with
                    | true, reviewId when reviewId <> Guid.Empty ->
                        ReviewWorkflow.ReviewWorkflow.getBranchReviewAt binding snapshot taskId reviewId
                        |> Result.mapError LedgerErrors.toEffect
                        |> Result.bind (fun review ->
                            if review.TaskId <> taskId
                               || not (String.Equals(review.ReviewId, reviewId.ToString("N"),
                                                     StringComparison.OrdinalIgnoreCase)) then
                                Error(Client.ValidationError "Canonical review snapshot or identifier changed; inspect again.")
                            else
                                let originalVersion = positive "ExpectedVersion"
                                let versionAdvanced =
                                    match originalVersion with
                                    | Some before when review.Version > before && List.contains action
                                        [ "Update"; "Claim"; "Decide"; "Release"; "Withdraw" ] ->
                                        Some PendingReviewObsolescence.ReviewVersionAdvanced
                                    | _ -> None
                                let terminal =
                                    if originalVersion.IsSome
                                       && List.contains action [ "Update"; "Claim"; "Decide"; "Release"; "Withdraw" ]
                                       && List.contains review.Status [ "Withdrawn"; "Accepted"; "ChangesRequested" ] then
                                        Some PendingReviewObsolescence.ReviewTerminal
                                    else None
                                let rule =
                                    if action = "Submit" && review.Version >= 1 then
                                        Some PendingReviewObsolescence.ReviewAlreadyExists
                                    else versionAdvanced |> Option.orElse terminal
                                match rule with
                                | None -> Error(Client.ValidationError
                                            "No verified review state makes this saved action obsolete; preserve the pending intent.")
                                | Some rule ->
                                    let proof: PendingObsoleteReviewProof =
                                        { Rule = rule
                                          TaskId = taskId
                                          ReviewId = reviewId.ToString("N")
                                          OriginalAction = action
                                          OriginalExpectedVersion = originalVersion |> Option.defaultValue 0
                                          ObservedVersion = review.Version
                                          ObservedStatus = review.Status
                                          ObservedOwnerId = review.OwnerAgentId
                                          ObservedReviewerId = review.ReviewerId }
                                    Ok (Choice2Of2 proof))
                    | _ ->
                        Error(Client.ValidationError "Saved review command has no valid review UUID.")
                | _ ->
                    Error(Client.ValidationError "Only saved TaskWrite and ReviewWrite commands can be abandoned by verified state.")
        with ex ->
            Error(Client.ValidationError $"Cannot establish typed saved-command obsolescence: {ex.Message}")

    // Operator-controlled abandonment is DISTINCT from supersession by an
    // Applied receipt. Exact original UUID is absent from a pinned canonical
    // head, and a current entity must positively show the old action cannot
    // run. The archive is not an assertion that the old push failed.
    let abandon (binding: LedgerStore.LedgerBinding) operationId
                (expectedCommandHash: string) (expectedHead: string)
                (operatorId: string) (reason: string) =
        withIntentLock binding operationId (fun () ->
            if String.IsNullOrWhiteSpace operatorId || operatorId.Length > 128
               || String.IsNullOrWhiteSpace reason || reason.Trim().Length < 10
               || reason.Length > 2048
               || String.IsNullOrWhiteSpace expectedHead
               || String.IsNullOrWhiteSpace expectedCommandHash
               || expectedCommandHash.Length <> 64 then
                Error(Client.ValidationError "Explicit abandonment requires operator, reason, original 64-character SHA256 and pinned verified head.")
            else
                findArchive binding operationId
                |> Result.bind (fun prior ->
                    let validate original =
                        if not (checkBinding binding original)
                           || not (String.Equals(original.CommandSha256, expectedCommandHash,
                                                 StringComparison.OrdinalIgnoreCase)) then
                            Error(Client.ValidationError "Abandonment binding or original command hash does not match.")
                        else Ok ()
                    let finish (audit: SupersededPendingIntent) repeat =
                        try
                            // The durable archive is the local UUID tombstone:
                            // retrying an interrupted transition may only
                            // clean the exact same recorded original.
                            findAtRoot binding.RepositoryPath operationId
                            |> Result.bind (fun current ->
                                if current.IsSome && current.Value <> audit.Original then
                                    Error(Client.ValidationError "Pending record changed after immutable archive; do not remove.")
                                else
                                    if current.IsSome then
                                        syncArchiveDirectory (archiveDirectory binding.RepositoryPath)
                                        File.Delete(filePath binding operationId)
                                    Ok {| operationId = operationId.ToString("D")
                                          alreadyAbandoned = repeat
                                          verifiedLedgerHead = audit.VerifiedLedgerHead
                                          archive = audit |})
                        with ex ->
                            Error(Client.ValidationError $"Archive exists but local abandonment cleanup failed: {ex.Message}")
                    match prior with
                    | Some audit ->
                        validate audit.Original
                        |> Result.bind (fun () ->
                            if audit.SchemaVersion <> 2
                               || audit.OperatorId <> operatorId
                               || audit.Reason <> reason
                               || audit.VerifiedLedgerHead <> expectedHead then
                                Error(Client.ValidationError "Archived UUID is immutable and cannot be abandoned with changed proof.")
                            else finish audit true)
                    | None ->
                        findAtRoot binding.RepositoryPath operationId
                        |> Result.bind (function
                            | None -> Error(Client.ValidationError "No saved pending intent to abandon.")
                            | Some original ->
                                validate original
                                |> Result.bind (fun () ->
                                    LedgerStore.LedgerStore.fetch binding
                                    |> Result.mapError LedgerErrors.toEffect
                                    |> Result.bind (fun snapshot ->
                                        if snapshot.Commit <> expectedHead then
                                            Error(Client.ValidationError "Verified head changed since inspection; inspect again.")
                                        else
                                            LedgerStore.LedgerStore.getOperationAt binding snapshot operationId
                                            |> Result.mapError LedgerErrors.toEffect
                                            |> Result.bind (function
                                                | Some _ ->
                                                    Error(Client.ValidationError "Original UUID is present at verified head; do not abandon.")
                                                | None ->
                                                    obsoleteStateProof binding snapshot original
                                                    |> Result.bind (fun proof ->
                                                        let taskProof, reviewProof =
                                                            match proof with
                                                            | Choice1Of2 task -> task, Unchecked.defaultof<_>
                                                            | Choice2Of2 review -> Unchecked.defaultof<_>, review
                                                        let audit =
                                                            { SchemaVersion = 2; Original = original
                                                              OperatorId = operatorId; Reason = reason
                                                              RelatedOperationId = ""; RelatedRequestHash = ""
                                                              RelatedOperationKind = ""; RelatedSequence = 0L
                                                              RelatedOriginCommit = ""
                                                              ObsoleteTask = taskProof; ObsoleteReview = reviewProof
                                                              VerifiedLedgerHead = snapshot.Commit
                                                              RecordedUtc = DateTimeOffset.UtcNow.ToString("O") }
                                                        createArchive binding operationId audit
                                                        |> Result.bind (fun () -> finish audit false))))))))

    // Read-only preflight for exact persisted-command retry. A successful
    // receipt returns its canonical operation; absence stays uncertain.
    // The caller subsequently re-enters the CURRENT typed Task/Review
    // writer, whose CAS/version rules and per-intent lock remain decisive.
    let prepareRetry (binding: LedgerStore.LedgerBinding) operationId
                     expectedCommandHash expectedHead =
        if String.IsNullOrWhiteSpace expectedHead
           || String.IsNullOrWhiteSpace expectedCommandHash
           || expectedCommandHash.Length <> 64 then
            Error(Client.ValidationError "Exact command SHA256 and previously inspected verified ledger head required.")
        else
            findArchive binding operationId
            |> Result.bind (function
                | Some _ -> Error(Client.ValidationError "Intent was superseded: archived UUID may never be retried.")
                | None ->
                    findAtRoot binding.RepositoryPath operationId
                    |> Result.bind (function
                        | None -> Error(Client.ValidationError "No saved pending intent exists for retry.")
                        | Some record ->
                            if not (checkBinding binding record) then
                                Error(Client.ValidationError "Saved pending intent belongs to a different ledger binding.")
                            elif not (String.Equals(record.CommandSha256, expectedCommandHash,
                                                   StringComparison.OrdinalIgnoreCase)) then
                                Error(Client.ValidationError "Original saved command hash changed; inspect again.")
                            else
                                LedgerStore.LedgerStore.fetch binding
                                |> Result.mapError LedgerErrors.toEffect
                                |> Result.bind (fun snapshot ->
                                    if snapshot.Commit <> expectedHead then
                                        Error(Client.ValidationError "Verified ledger head changed; inspect and authorize against the new head.")
                                    else
                                        LedgerStore.LedgerStore.getOperationAt binding snapshot operationId
                                        |> Result.mapError LedgerErrors.toEffect
                                        |> Result.bind (function
                                            | Some remote when identityMatches record remote ->
                                                Ok (record, Some remote)
                                            | Some _ ->
                                                Error(Client.ValidationError "IdentityMismatch: verified UUID belongs to a different command.")
                                            | None ->
                                                Ok (record, None)))))

    let private archivedDisposition binding snapshot (knownIds: Set<string>)
                                    (archived: SupersededPendingIntent) =
        let original = archived.Original
        if not (checkBinding binding original) then Ok PendingDisposition.ArchiveBindingMismatch
        else
            match Guid.TryParse original.OperationId with
            | false, _ -> Ok PendingDisposition.ArchivedMalformed
            | true, id when not (knownIds.Contains(id.ToString("N"))) ->
                Ok (if archived.SchemaVersion = 2 then PendingDisposition.Abandoned else PendingDisposition.Superseded)
            | true, id ->
                LedgerStore.LedgerStore.getOperationAt binding snapshot id
                |> Result.mapError LedgerErrors.toEffect
                |> Result.map (function
                    | Some op when identityMatches original op ->
                        if archived.SchemaVersion = 2 then PendingDisposition.AppliedAfterAbandonment
                        else PendingDisposition.AppliedAfterSupersession
                    | Some _ ->
                        if archived.SchemaVersion = 2 then PendingDisposition.IdentityMismatchAfterAbandonment
                        else PendingDisposition.IdentityMismatchAfterSupersession
                    | None ->
                        if archived.SchemaVersion = 2 then PendingDisposition.Abandoned
                        else PendingDisposition.Superseded)

    // One verified Git tree index and remote operation reads only for UUIDs
    // present at that head. Streaming local scan retains aggregates, not the
    // command JSON for every historical intent.
    let summarize (binding: LedgerStore.LedgerBinding) =
        journalFiles binding.RepositoryPath
        |> Result.bind (fun files ->
            LedgerStore.LedgerStore.fetch binding
            |> Result.mapError LedgerErrors.toEffect
            |> Result.bind (fun snapshot ->
                LedgerStore.LedgerStore.getOperationIdsAt binding snapshot
                |> Result.mapError LedgerErrors.toEffect
                |> Result.bind (fun knownIds ->
                    let dispositions = System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
                    let actions = System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
                    let actors = System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
                    let ages = System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
                    let now = DateTimeOffset.UtcNow
                    let mutable malformed = 0
                    let scan =
                        files |> Array.fold (fun state path ->
                            state |> Result.bind (fun () ->
                                match readCheckedFile path with
                                | Error _ ->
                                    malformed <- malformed + 1
                                    increment dispositions (string PendingDisposition.MalformedLocalRecord)
                                    Ok ()
                                | Ok record ->
                                    let disposition =
                                        match metadata record with
                                        | None ->
                                            malformed <- malformed + 1
                                            Ok PendingDisposition.MalformedCommand
                                        | Some (action, actor, _) ->
                                            increment actions action
                                            increment actors actor
                                            classifyAt binding snapshot knownIds record
                                    disposition |> Result.map (fun status ->
                                        increment dispositions (string status)
                                        increment ages (ageBucket record.CreatedUtc now))))
                            (Ok ())
                    scan
                    |> Result.bind (fun () ->
                        archiveFiles binding.RepositoryPath
                        |> Result.bind (fun archivedFiles ->
                            let mutable archivedMalformed = 0
                            let mutable lateApplied = 0
                            let mutable archivedConflict = 0
                            let archiveScan =
                                archivedFiles |> Array.fold (fun state path ->
                                    state |> Result.bind (fun () ->
                                        match readArchiveFile path with
                                        | Error _ ->
                                            archivedMalformed <- archivedMalformed + 1
                                            increment dispositions (string PendingDisposition.ArchivedMalformed)
                                            Ok ()
                                        | Ok archived ->
                                            archivedDisposition binding snapshot knownIds archived
                                            |> Result.map (fun status ->
                                                increment dispositions (string status)
                                                if status = PendingDisposition.AppliedAfterSupersession || status = PendingDisposition.AppliedAfterAbandonment then
                                                    lateApplied <- lateApplied + 1
                                                elif status = PendingDisposition.IdentityMismatchAfterSupersession || status = PendingDisposition.IdentityMismatchAfterAbandonment then
                                                    archivedConflict <- archivedConflict + 1)))
                                    (Ok ())
                            archiveScan |> Result.map (fun () ->
                                { VerifiedLedgerHead = snapshot.Commit
                                  Total = files.Length
                                  MalformedLocalRecords = malformed
                                  ArchivedTotal = archivedFiles.Length
                                  ArchivedMalformed = archivedMalformed
                                  ArchivedLateApplied = lateApplied
                                  ArchivedIdentityMismatch = archivedConflict
                                  ByDisposition = toCounts dispositions
                                  ByAction = toCounts actions
                                  ByActor = toCounts actors
                                  ByAge = toCounts ages }))))))

    let inspect (binding: LedgerStore.LedgerBinding) (operationId: Guid) =
        findAtRoot binding.RepositoryPath operationId
        |> Result.bind (fun stored ->
            findArchive binding operationId
            |> Result.bind (fun archived ->
                LedgerStore.LedgerStore.fetch binding
                |> Result.mapError LedgerErrors.toEffect
                |> Result.bind (fun snapshot ->
                    LedgerStore.LedgerStore.getOperationIdsAt binding snapshot
                    |> Result.mapError LedgerErrors.toEffect
                    |> Result.bind (fun knownIds ->
                        match stored, archived with
                        | Some record, Some audit when record <> audit.Original ->
                            Error(Client.ValidationError "Pending record differs from archived supersession; inspect local journal corruption.")
                        | _, Some audit ->
                            archivedDisposition binding snapshot knownIds audit
                            |> Result.map (fun status ->
                                { OperationId = operationId.ToString("D")
                                  PresentLocally = stored.IsSome
                                  VerifiedLedgerHead = snapshot.Commit
                                  Disposition = Some(string status)
                                  Record = Some audit.Original
                                  Archive = Some audit })
                        | None, None ->
                            Ok { OperationId = operationId.ToString("D")
                                 PresentLocally = false; VerifiedLedgerHead = snapshot.Commit
                                 Disposition = None; Record = None; Archive = None }
                        | Some record, None ->
                            classifyAt binding snapshot knownIds record
                            |> Result.map (fun disposition ->
                                { OperationId = record.OperationId; PresentLocally = true
                                  VerifiedLedgerHead = snapshot.Commit; Disposition = Some(string disposition)
                                  Record = Some record; Archive = None })))))

    let page (binding: LedgerStore.LedgerBinding) cursor pageSize =
        pageAtRoot binding.RepositoryPath cursor pageSize
        |> Result.bind (fun (records, nextCursor) ->
            LedgerStore.LedgerStore.fetch binding
            |> Result.mapError LedgerErrors.toEffect
            |> Result.bind (fun snapshot ->
                LedgerStore.LedgerStore.getOperationIdsAt binding snapshot
                |> Result.mapError LedgerErrors.toEffect
                |> Result.bind (fun knownIds ->
                    records |> List.fold (fun state (fileKey, record) ->
                        state |> Result.bind (fun items ->
                            match record with
                            | Error _ ->
                                Ok ({ LocalFileKey = fileKey; OperationId = ""
                                      Action = "(quarantined)"; Actor = "(unknown)"
                                      TaskId = None; CreatedUtc = ""; CommandSha256 = ""
                                      Disposition = string PendingDisposition.MalformedLocalRecord } :: items)
                            | Ok record ->
                                classifyAt binding snapshot knownIds record
                                |> Result.map (fun status ->
                                    let action, actor, task =
                                        metadata record
                                        |> Option.defaultValue ("(malformed)", "(unspecified)", None)
                                    { LocalFileKey = fileKey; OperationId = record.OperationId
                                      Action = action; Actor = actor; TaskId = task
                                      CreatedUtc = record.CreatedUtc
                                      CommandSha256 = record.CommandSha256
                                      Disposition =
                                        if metadata record |> Option.isNone then string PendingDisposition.MalformedCommand
                                        else string status } :: items)))
                        (Ok [])
                    |> Result.map (fun reversed ->
                        { VerifiedLedgerHead = snapshot.Commit
                          Records = List.rev reversed
                          NextCursor = nextCursor }))))

    let reconcile (binding: LedgerStore.LedgerBinding) =
        listAtRoot binding.RepositoryPath
        |> Result.bind (fun records ->
            LedgerStore.LedgerStore.fetch binding
            |> Result.mapError LedgerErrors.toEffect
            |> Result.bind (fun snapshot ->
                let inspection record =
                    if not (checkBinding binding record) then
                        Ok {| operationId = record.OperationId
                              disposition = "BindingMismatch"
                              snapshotCommit = snapshot.Commit
                              localCommandHash = record.CommandSha256
                              operation = None |}
                    else
                        match Guid.TryParse record.OperationId with
                        | true, id ->
                            LedgerStore.LedgerStore.getOperationAt binding snapshot id
                            |> Result.mapError LedgerErrors.toEffect
                            |> Result.map (fun operation ->
                                {| operationId = record.OperationId
                                   disposition =
                                       match operation with
                                       | None -> "NotFoundAtVerifiedHead"
                                       | Some value when identityMatches record value -> "Applied"
                                       | Some _ -> "IdentityMismatch"
                                   snapshotCommit = snapshot.Commit
                                   localCommandHash = record.CommandSha256
                                   operation = operation |})
                        | _ -> Error(Client.ValidationError "Invalid pending operation UUID.")
                records
                |> List.fold (fun state record ->
                    state |> Result.bind (fun items ->
                        inspection record |> Result.map (fun item -> item :: items)))
                    (Ok [])
                |> Result.map List.rev))

    // Explicitly retire a journal record only after the operator has inspected
    // matching remote proof and supplied its exact request hash. This does
    // not mutate the authoritative Git ledger.
    let private acknowledgeUnsafe (binding: LedgerStore.LedgerBinding) (operationId: Guid) (expectedRequestHash: string) =
        if String.IsNullOrWhiteSpace expectedRequestHash
           || expectedRequestHash.Length <> 64 then
            Error(Client.ValidationError "Expected exact 64-character remote request hash.")
        else
            findAtRoot binding.RepositoryPath operationId
            |> Result.bind (fun current ->
                match current with
                | None ->
                    // Explicit acknowledgments may be retried after deletion.
                    // Require the same exact remote operation proof again.
                    LedgerStore.LedgerStore.getOperationStatus binding operationId
                    |> Result.mapError LedgerErrors.toEffect
                    |> Result.bind (fun (snapshot, remote) ->
                        match remote with
                        | Some applied when applied.RequestHash = expectedRequestHash ->
                            Ok {| operationId = operationId.ToString("D")
                                  verifiedLedgerHead = snapshot.Commit
                                  originalResult = applied.OriginalResultJson
                                  acknowledged = true
                                  alreadyAcknowledged = true |}
                        | _ ->
                            Error(Client.ValidationError
                                "No matching confirmed remote operation; cannot acknowledge a missing journal."))
                | Some entry when not (checkBinding binding entry) ->
                    Error(Client.ValidationError "Pending intent belongs to another ledger identity.")
                | Some entry ->
                    LedgerStore.LedgerStore.getOperationStatus binding operationId
                    |> Result.mapError LedgerErrors.toEffect
                    |> Result.bind (fun (snapshot, remote) ->
                        match remote with
                        | Some applied when applied.RequestHash = expectedRequestHash
                                            && identityMatches entry applied ->
                            try
                                // Fail closed if the file has changed since inspection.
                                let path = filePath binding operationId
                                match readFile path with
                                | Error error -> Error error
                                | Ok current when current <> entry ->
                                    Error(Client.ValidationError "Pending intent changed during acknowledgment.")
                                | Ok _ ->
                                    File.Delete path
                                    Ok {| operationId = operationId.ToString("D")
                                          verifiedLedgerHead = snapshot.Commit
                                          originalResult = applied.OriginalResultJson
                                          acknowledged = true
                                          alreadyAcknowledged = false |}
                            with ex ->
                                Error(Client.ValidationError
                                    $"Unable to acknowledge verified pending operation: {ex.Message}")
                        | Some _ -> Error(Client.ValidationError
                            "The observed operation hash or command identity differs from the pending intent.")
                        | None -> Error(Client.ValidationError
                            "Operation is not present at the verified canonical ledger head; preserve pending journal.")))

    let acknowledge binding operationId expectedRequestHash =
        withIntentLock binding operationId (fun () ->
            findArchive binding operationId
            |> Result.bind (function
                | Some _ ->
                    Error(Client.ValidationError
                        "This UUID was explicitly superseded. Inspect its immutable archive and any late Applied receipt; acknowledgment cannot rewrite that disposition.")
                | None ->
                    acknowledgeUnsafe binding operationId expectedRequestHash))

    // A known domain/CAS refusal before any remote publication is not an
    // uncertain Git outcome. Transport, integrity and UUID-identity errors
    // deliberately do NOT qualify, even when they occur before a push.
    let private definitivelyRejected = function
        | Client.ValidationError _ -> true
        | Client.LedgerFailureError failure ->
            match failure.Code with
            | "InvalidBinding" | "StaleEntityVersion" | "AgentBusy"
            | "ReviewAccessDenied" | "ReviewAlreadyClaimed" | "ReviewVersionConflict"
            | "SourceUnavailable" | "SourceReviewRefUnavailable" -> true
            | _ -> false
        | _ -> false

    // Cleanup is best-effort because a remote Applied write remains successful
    // even if local storage becomes read-only. Never hide cleanup failure:
    // the remaining file is visible to reconciliation and can be acknowledged
    // against its exact remote receipt.
    let private retireAfterOutcome binding operationId reason =
        try
            File.Delete(filePath binding operationId)
        with ex ->
            // Avoid exposing command JSON, session tokens or local file paths.
            try
                eprintfn "Jarvis pending intent cleanup failed after %s for %O (%s); inspect the local journal."
                    reason operationId (ex.GetType().Name)
            with _ -> ()

    let private withOperationId (operationId: Guid) = function
        | Client.LedgerFailureError failure ->
            Client.LedgerFailureError
                { failure with OperationId = Some(operationId.ToString("D")) }
        | error -> error

    let private executeUnsafe binding operationId commandType command (apply: unit -> Result<'t, EffectError>) =
        let commandJson = JsonSerializer.Serialize(command)
        // A previous invocation may have attempted a push whose acceptance
        // is still delayed. Never let a fresh preflight rejection erase that
        // earlier evidence, even when today's caller supplies the same UUID.
        let previouslyPending = File.Exists(filePath binding operationId)
        persistUnsafe binding operationId commandType commandJson
        |> Result.bind (fun () ->
            let outcome, publication = LedgerStore.LedgerStore.observePublicationAttempts apply
            match outcome with
            | Error error
                when not previouslyPending
                     && publication = LedgerStore.LedgerStore.PublicationExposure.NotAttempted
                     && definitivelyRejected error ->
                // No remote publication was attempted during this invocation
                // and this UUID had no prior local uncertain attempt. The
                // authoritative domain error must still reach the caller.
                retireAfterOutcome binding operationId "definitive pre-publication rejection"
                Error(withOperationId operationId error)
            | Error(Client.LedgerFailureError failure) ->
                // The caller-owned UUID remains recoverable even after a harness reset.
                Error(withOperationId operationId (Client.LedgerFailureError failure))
            | Error _ -> outcome // Preserve exact intent for recovery after lost response.
            | Ok _ ->
                retireAfterOutcome binding operationId "confirmed success"
                outcome
        )

    let execute binding operationId commandType command apply =
        withIntentLock binding operationId (fun () ->
            executeUnsafe binding operationId commandType command apply)

    // An uncertain push can have been accepted even when its response was
    // lost. Re-read the authoritative UUID index before reporting ambiguity.
    // Only a fully matching canonical request is eligible for recovery.
    let executeLedgerWrite (binding: LedgerStore.LedgerBinding) (operationId: Guid)
                           (commandType: string) command
                           (apply: unit -> Result<LedgerStore.LedgerWrite, EffectError>) =
        execute binding operationId commandType command (fun () ->
            match apply () with
            | Error(Client.LedgerFailureError issue) when issue.Code = "OutcomeUnknown" ->
                match LedgerStore.LedgerStore.getOperationStatus binding operationId with
                | Ok(snapshot, Some applied) ->
                    let originalJson = JsonSerializer.Serialize(command)
                    let local =
                        { SchemaVersion = 1
                          OperationId = operationId.ToString("D")
                          ProjectId = binding.ProjectId.ToString("D")
                          LedgerId = binding.LedgerId.ToString("D")
                          Epoch = binding.Epoch
                          CommandType = commandType
                          CommandJson = originalJson
                          CommandSha256 = sha originalJson
                          CreatedUtc = "" }
                    if identityMatches local applied then
                        let verified : LedgerStore.LedgerWrite =
                            { Operation = applied; CurrentHead = snapshot.Commit; AlreadyApplied = true }
                        Ok verified
                    else
                        Error(Client.LedgerFailureError
                            { issue with VerifiedLedgerHead = Some snapshot.Commit })
                | Ok(snapshot, None) ->
                    Error(Client.LedgerFailureError
                        { issue with VerifiedLedgerHead = Some snapshot.Commit })
                | Error _ ->
                    Error(Client.LedgerFailureError issue)
            | other -> other)

module OperationResponse =
    // Keep canonical agent state effects available in the client transport
    // without extracting them from presentation JSON or exposing extra MCP fields.
    type Presented =
        { operation: LedgerStore.LedgerOperation
          head: string
          alreadyApplied: bool
          originalResult: JsonElement
          notificationDelivery: Client.ReviewWorkflow.NotificationDelivery option
          [<System.Text.Json.Serialization.JsonIgnore>]
          changedAgentIds: string list }

    let presentWithNotification (notification: Client.ReviewWorkflow.NotificationDelivery option) (write: LedgerStore.LedgerWrite) =
        use document = JsonDocument.Parse(write.Operation.OriginalResultJson)
        box
            { operation = write.Operation
              head = write.CurrentHead
              alreadyApplied = write.AlreadyApplied
              originalResult = document.RootElement.Clone()
              notificationDelivery = notification
              changedAgentIds = LedgerStore.LedgerStore.changedAgentIds write }

    let present write = presentWithNotification None write

module LedgerApi =
    let private id label (raw: string) =
        match Guid.TryParse raw with
        | true, parsed when parsed <> Guid.Empty -> Ok parsed
        | _ -> Error(Client.ValidationError $"Valid {label} UUID required.")

    let read (cmd: LedgerReadCommand) rt =
        match cmd.Action with
        | "Health" -> LedgerBindingConfig.health cmd.ProjectName rt |> Result.map box
        | "PendingOperationSummary" ->
            LedgerBindingConfig.load cmd.ProjectName rt
            |> Result.bind PendingIntentJournal.summarize
            |> Result.map box
        | "PendingOperationsPage" ->
            LedgerBindingConfig.load cmd.ProjectName rt
            |> Result.bind (fun binding ->
                PendingIntentJournal.page binding cmd.Cursor cmd.PageSize)
            |> Result.map box
        | "InspectPendingOperation" ->
            id "operation" cmd.OperationId
            |> Result.bind (fun operationId ->
                LedgerBindingConfig.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    PendingIntentJournal.inspect binding operationId))
            |> Result.map box
        | "PendingOperations" ->
            PendingIntentJournal.listForProject cmd.ProjectName rt
            |> Result.map (fun records ->
                records
                |> List.map (fun record ->
                    {| operationId = record.OperationId;
                       projectId = record.ProjectId;
                       ledgerId = record.LedgerId;
                       epoch = record.Epoch;
                       commandType = record.CommandType;
                       commandSha256 = record.CommandSha256;
                       originalCommandJson = record.CommandJson;
                       createdUtc = record.CreatedUtc |})
                |> box)
        | "ReconcilePendingOperations" ->
            LedgerBindingConfig.load cmd.ProjectName rt
            |> Result.bind PendingIntentJournal.reconcile
            |> Result.map box
        | "GetOperationStatus" ->
            id "operation" cmd.OperationId
            |> Result.bind (fun operationId ->
                LedgerBindingConfig.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    LedgerStore.LedgerStore.getOperationStatus binding operationId
                    |> Result.mapError LedgerErrors.toEffect
                    |> Result.bind (fun (snapshot, operation) ->
                        match operation with
                        | None ->
                            Ok(box {| snapshotCommit = snapshot.Commit
                                      operationId = operationId.ToString("D")
                                      disposition = "NotFoundAtVerifiedHead"
                                      originatingCommit = None
                                      originalResult = None
                                      operation = operation |})
                        | Some applied ->
                            LedgerStore.LedgerStore.getOperationOriginAt binding snapshot applied
                            |> Result.mapError LedgerErrors.toEffect
                            |> Result.map (fun origin ->
                                use result = JsonDocument.Parse(applied.OriginalResultJson)
                                box {| snapshotCommit = snapshot.Commit
                                       operationId = operationId.ToString("D")
                                       disposition = "Applied"
                                       originatingCommit = Some origin
                                       originalResult = Some(result.RootElement.Clone())
                                       operation = operation |}))))
        | _ -> Error(Client.ValidationError "Unsupported  ledger read action.")

    let write (cmd: LedgerWriteCommand) rt =
        id "operation" cmd.OperationId
        |> Result.bind (fun operationId ->
            if cmd.Action = "AcknowledgePendingOperation" then
                LedgerBindingConfig.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    PendingIntentJournal.acknowledge binding operationId
                        cmd.ExpectedRequestHash |> Result.map box)
            else
                let bindInputs () =
                    id "project" cmd.ProjectId
                    |> Result.bind (fun projectId ->
                        id "ledger" cmd.LedgerId
                        |> Result.map (fun ledgerId -> projectId, ledgerId))
                let result =
                    match cmd.Action with
                    | "BindExistingLedger" ->
                        bindInputs ()
                        |> Result.bind (fun (projectId, ledgerId) ->
                            LedgerBindingConfig.bindExisting cmd.ProjectName rt projectId ledgerId
                                cmd.Epoch cmd.RemoteName cmd.ApprovedFetchUrl cmd.ApprovedPushUrl
                                cmd.ExpectedTrustedHead cmd.AcknowledgeVisibility)
                    | "InitializeLedger" ->
                        bindInputs ()
                        |> Result.bind (fun (projectId, ledgerId) ->
                            LedgerBindingConfig.initializeNew cmd.ProjectName rt projectId ledgerId
                                cmd.Epoch cmd.RemoteName cmd.ApprovedFetchUrl cmd.ApprovedPushUrl
                                cmd.AcknowledgeVisibility)
                    | "RebindLedgerRemote" ->
                        LedgerBindingConfig.rebindRemote cmd.ProjectName rt
                            cmd.RemoteName cmd.ApprovedFetchUrl cmd.ApprovedPushUrl
                            cmd.ExpectedTrustedHead cmd.AcknowledgeVisibility
                    | _ -> Error(Client.ValidationError "Unsupported  ledger setup action.")
                result |> Result.map (fun binding ->
                    box {| projectId = binding.ProjectId; ledgerId = binding.LedgerId
                           epoch = binding.Epoch; remoteName = binding.RemoteName
                           trustedLedgerHead = binding.HighestTrustedHead |}))

module ReviewApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError LedgerErrors.toEffect
    let private parseId label (raw: string) =
        match Guid.TryParse raw with
        | true, value when value <> Guid.Empty -> Ok value
        | _ -> Error(Client.ValidationError $"Invalid {label}: caller-stable UUID required.")

    let read (cmd: ReviewReadCommand) rt =
        LedgerBindingConfig.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            match cmd.Action with
            | "ListProjectTasks" ->
                ReviewWorkflow.ReviewWorkflow.listProjectTasks binding
                |> mapError |> Result.map box
            | "GetTask" ->
                ReviewWorkflow.ReviewWorkflow.getTaskWithSpec binding cmd.TaskId
                |> mapError
                |> Result.map (fun (snapshot, task, attempt, html, specOid) ->
                    box {| snapshotCommit = snapshot.Commit; task = task; attempt = attempt
                           specHtml = html; specBlobOid = specOid |})
            | "GetReview" ->
                parseId "requestId" cmd.RequestId
                |> Result.bind (fun id ->
                    ReviewWorkflow.ReviewWorkflow.getBranchReview binding cmd.TaskId id
                    |> mapError |> Result.map box)
            | "ListDocuments" ->
                parseId "requestId" cmd.RequestId
                |> Result.bind (fun reviewId ->
                    ReviewWorkflow.ReviewWorkflow.listReviewDocuments binding cmd.TaskId reviewId
                    |> mapError
                    |> Result.map (fun (head, documents) ->
                        box {| snapshotCommit = head; documents = documents |}))
            | "ReadDocument" ->
                parseId "requestId" cmd.RequestId
                |> Result.bind (fun reviewId ->
                    ReviewWorkflow.ReviewWorkflow.getReviewDocument binding cmd.TaskId reviewId cmd.DocumentPath
                    |> mapError
                    |> Result.map (fun (head, document) ->
                        box {| snapshotCommit = head; document = document |}))
            | "ReviewerQueue" ->
                ReviewWorkflow.ReviewWorkflow.branchReviewerQueue binding cmd.ReviewerId
                |> mapError
                |> Result.map (fun (snapshot, queue) ->
                    box {| snapshotCommit = snapshot; reviews = queue |})
            | _ -> Error(Client.ValidationError "Unsupported  read operation."))

    let write (cmd: ReviewWriteCommand) rt =
        LedgerBindingConfig.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            parseId "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                let id () = parseId "requestId" cmd.RequestId
                let result =
                    PendingIntentJournal.executeLedgerWrite binding operationId "ReviewWrite" cmd (fun () ->
                        match cmd.Action with
                        | "PutDocument" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.putReviewDocument binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.DocumentPath
                                    cmd.ExpectedBlobOid cmd.Content
                                |> mapError)
                        | "Submit" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.submitBranchReview binding operationId
                                    cmd.TaskId cmd.ActorId reviewId
                                    cmd.SourceRef cmd.HeadOid cmd.Summary cmd.Kind cmd.TargetRef |> mapError)
                        | "Update" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.replaceBranchReview binding operationId
                                    cmd.TaskId cmd.ActorId reviewId cmd.ExpectedVersion
                                    cmd.SourceRef cmd.HeadOid cmd.Summary |> mapError)
                        | "Claim" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.claimBranchReview binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion |> mapError)
                        | "Decide" ->
                            id () |> Result.bind (fun reviewId ->
                                match cmd.Verdict with
                                | "Accept" | "RequestChanges" as verdict ->
                                    let decision =
                                        if verdict = "Accept" then ReviewWorkflow.Accept
                                        else ReviewWorkflow.RequestChanges
                                    ReviewWorkflow.ReviewWorkflow.decideBranchReview binding operationId
                                        cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion decision |> mapError
                                | _ -> Error(Client.ValidationError "Invalid  review verdict."))
                        | "Release" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.releaseBranchReview binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion |> mapError)
                        | "Withdraw" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.withdrawBranchReview binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion |> mapError)
                        | "AcknowledgeFeedback" ->
                            ReviewWorkflow.ReviewWorkflow.acknowledgeBranchReview binding operationId
                                cmd.TaskId cmd.ActorId (int64 cmd.ExpectedVersion) |> mapError
                        | _ -> Error(Client.ValidationError "Unsupported  write operation.")
                    )
                result |> Result.map OperationResponse.present))


// Separate typed setup/assignment contract. The review request API never has
// to reinterpret a request ID as a task, attempt, or agent identifier.
module AgentObservationApi =
    // This explicitly observational operation is not a substitute for the
    // agent's own inbox. Its observer label must match the separate
    // SignalR actor label before any dispatch reaches this function.
    // Only summarized canonical metadata is returned; notification bodies,
    // evidence, source documents and session IDs are never exposed here.
    let observe (cmd: ObserveAgentRequest) (getLocalLastSeen: string -> string -> DateTimeOffset option) rt =
        if String.IsNullOrWhiteSpace cmd.ObserverAgentId
           || String.IsNullOrWhiteSpace cmd.TargetAgentId then
            Error(Client.ValidationError "Observer and target agent identities are required.")
        else
            LedgerBindingConfig.loadWithSnapshot cmd.ProjectName rt
            |> Result.bind (fun (binding, snapshot) ->
                ReviewWorkflow.ReviewWorkflow.getObservedAgentInboxAt
                    binding snapshot cmd.ObserverAgentId cmd.TargetAgentId
                |> Result.mapError LedgerErrors.toEffect
                |> Result.map (fun inbox ->
                        let agent = inbox.Agent
                        let focus = inbox.Focused |> Option.map (fun item ->
                            {| taskId = item.Task.TaskId
                               title = item.Task.Title
                               lifecycle = item.Task.Lifecycle
                               attemptId = item.Attempt.AttemptId
                               attemptVersion = item.Attempt.Version
                               attemptPhase = item.Attempt.Phase
                               assignmentGeneration = item.Attempt.AssignmentGeneration |})
                        let lastSeen = getLocalLastSeen cmd.ProjectName cmd.TargetAgentId
                        box
                            {| projectName = cmd.ProjectName
                               observerAgentId = cmd.ObserverAgentId
                               targetAgentId = agent.AgentId
                               snapshotCommit = inbox.SnapshotCommit
                               canonicalStateSource = "VerifiedGitLedger"
                               canonicalStateFreshness = "FetchedForThisObservation"
                               observedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                               agentVersion = agent.Version
                               stateSequence = agent.StateSequence
                               state = agent.State
                               execution = agent.Execution
                               unassignedDisposition = Option.ofObj agent.UnassignedDisposition
                               stateReason = Option.ofObj agent.StateReason
                               stateEnteredAtUtc = Option.ofObj agent.StateEnteredAtUtc
                               workingTurnStartedAtUtc = Option.ofObj agent.WorkingTurnStartedAtUtc
                               focused = focus
                               operatorActionRequired = inbox.OperatorActionRequired
                               needsOrchestratorAssignment = inbox.NeedsOrchestratorAssignment
                               slotAvailable = inbox.SlotAvailable
                               pendingNotificationCount = inbox.PendingNotifications.Length
                               pendingReviewCount = inbox.PendingReviews.Length
                               availableReviewCount = inbox.AvailableReviews.Length
                               unacknowledgedFeedbackCount = inbox.UnacknowledgedFeedback.Length
                               blockedAttemptCount = inbox.Blocked.Length
                               blockedTaskIds = inbox.Blocked |> List.map (fun item -> item.Task.TaskId)
                               lastSeenAtUtc = lastSeen |> Option.map (fun seen -> seen.ToString("O"))
                               lastSeenProvenance = if lastSeen.IsSome then "LocalClientActivityCache" else "Unknown"
                               lastSeenIsCanonical = false |}))

module TaskApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError LedgerErrors.toEffect

    // The transport stores reviewId in Reason, not TargetRef.
    // Keep the positional submitIntegration call aligned with its signature.
    let integrationSubmissionReferences (cmd: TaskWriteCommand) =
        cmd.Reason, cmd.TargetRef

    let private guid label (text: string) =
        match Guid.TryParse text with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(Client.ValidationError $"Invalid {label}: a caller-stable UUID is required.")

    let read (cmd: TaskReadCommand) rt =
        LedgerBindingConfig.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            match cmd.Action with
            | "GetRole" ->
                RoleInstructions.RoleInstructions.getRole binding cmd.TaskId
                |> mapError |> Result.map box
            | "ListRoles" ->
                RoleInstructions.RoleInstructions.listRoles binding
                |> mapError |> Result.map box
            | "GetAgentInstructionConfig" ->
                RoleInstructions.RoleInstructions.getAgentConfig binding cmd.AgentId
                |> mapError |> Result.map box
            | "GetNotificationPreferences" ->
                ReviewWorkflow.ReviewWorkflow.getNotificationPreferences binding cmd.AgentId
                |> mapError |> Result.map box
            | "ResolveAgentInstructions" ->
                RoleInstructions.RoleInstructions.resolve binding cmd.AgentId
                |> mapError |> Result.map box
            | "GetSpecPage" ->
                ReviewWorkflow.ReviewWorkflow.getTaskSpecPage binding cmd.TaskId cmd.Cursor cmd.PageSize
                |> mapError |> Result.map box
            | "DiffTaskSpec" ->
                ReviewWorkflow.ReviewWorkflow.diffTaskSpec binding cmd.TaskId cmd.BeforeOid cmd.AfterOid
                |> mapError |> Result.map box
            | "GetIntegrationSubmissions" ->
                ReviewWorkflow.ReviewWorkflow.getIntegrationSubmissions binding cmd.TaskId
                |> mapError |> Result.map box
            | "AgentInbox" ->
                LedgerStore.LedgerStore.fetch binding
                |> Result.bind (fun snapshot ->
                    ReviewWorkflow.ReviewWorkflow.getAgentInboxAt binding snapshot cmd.AgentId
                    |> Result.bind (fun inbox ->
                        ChallengeWorkflow.ChallengeWorkflow.enrichInboxAt binding snapshot cmd.AgentId inbox))
                |> mapError |> Result.map box
            | "GetAssignment" ->
                ReviewWorkflow.ReviewWorkflow.getAssignment binding cmd.TaskId cmd.AssignmentGeneration
                |> mapError |> Result.map box
            | "GetCheckpoint" ->
                ReviewWorkflow.ReviewWorkflow.getCheckpoint binding cmd.TaskId cmd.CheckpointSequence
                |> mapError |> Result.map box
            | "LifecycleHistory" ->
                ReviewWorkflow.ReviewWorkflow.getTaskLifecycleHistory binding cmd.TaskId
                |> mapError |> Result.map box
            | "InspectAttemptSource" ->
                ReviewWorkflow.ReviewWorkflow.inspectAttemptSource binding cmd.TaskId
                |> mapError |> Result.map box
            | _ -> Error(Client.ValidationError "Unsupported  task read operation."))

    // Administrative operation provenance is user-scoped, not inferred from
    // provider-specific conversation metadata. Historical receipts preserve
    // their original actor on idempotent replay.
    let internal administrativeActor binding operationId _sessionId =
        match LedgerStore.LedgerStore.getOperationStatus binding operationId with
        | Error error -> Error(LedgerErrors.toEffect error)
        | Ok(_, Some applied) -> Ok applied.Actor
        | Ok(_, None) -> Ok "AuthenticatedUser"

    let private writeUsingContext (context: ProjectPaths.ResolvedProject option) (cmd: TaskWriteCommand) rt =
        let binding =
            match context with
            | Some resolved -> LedgerBindingConfig.loadForContext resolved
            | None -> LedgerBindingConfig.load cmd.ProjectName rt
        binding
        |> Result.bind (fun binding ->
            guid "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                if cmd.Action = "ExportTaskSpec" then
                    // Filesystem mutation must use the write permission route,
                    // not the TaskRead protocol or its read-only capability.
                    ReviewWorkflow.ReviewWorkflow.exportTaskSpecWithId binding operationId cmd.TaskId
                        cmd.SourceRef cmd.BaseOid |> mapError |> Result.map box
                else
                    // Preserve historical session metadata only in lifecycle
                    // journals; it is not used to establish agent identity.
                    let journalCommand =
                        if List.contains cmd.Action [ "BeginAttempt"; "ResumeAgent"; "YieldAgent"; "CheckpointAttempt"; "CompleteAgentWork" ] then cmd
                        else { cmd with SessionId = "" }
                    let result =
                        PendingIntentJournal.executeLedgerWrite binding operationId "TaskWrite" journalCommand (fun () ->
                            let withAdministrativeActor action =
                                administrativeActor binding operationId cmd.SessionId |> Result.bind action
                            match cmd.Action with
                            | "SubmitIntegration" ->
                                guid "submissionId" cmd.AttemptId
                                |> Result.bind (fun submissionId ->
                                    let reviewId, targetRef = integrationSubmissionReferences cmd
                                    ReviewWorkflow.ReviewWorkflow.submitIntegration binding operationId
                                        cmd.TaskId cmd.ActorId submissionId cmd.SourceRef cmd.BaseOid
                                        reviewId targetRef |> mapError)
                            | "CompleteIntegration" ->
                                guid "submissionId" cmd.AttemptId
                                |> Result.bind (fun submissionId ->
                                    ReviewWorkflow.ReviewWorkflow.completeIntegration binding operationId
                                        cmd.TaskId cmd.ActorId submissionId cmd.BaseOid |> mapError)
                            | "NotifyAgent" ->
                                ReviewWorkflow.ReviewWorkflow.notifyAgent binding operationId
                                    cmd.ActorId cmd.SourceRef cmd.OwnerId cmd.TaskId cmd.Summary |> mapError
                            | "SetNotificationSenderBlock" ->
                                match cmd.Reason with
                                | "Block" | "Unblock" ->
                                    ReviewWorkflow.ReviewWorkflow.setNotificationSenderBlock
                                        binding operationId cmd.ActorId cmd.ActorId cmd.ProjectName
                                        cmd.SourceRef cmd.OwnerId (cmd.Reason = "Block") cmd.Summary
                                    |> mapError
                                | _ -> Error(Client.ValidationError "Expected Block or Unblock.")
                            | "SetNotificationMute" ->
                                match cmd.Reason with
                                | "Mute" | "Unmute" ->
                                    ReviewWorkflow.ReviewWorkflow.setNotificationMute
                                        binding operationId cmd.ActorId cmd.ActorId
                                        (cmd.Reason = "Mute") cmd.Summary
                                    |> mapError
                                | _ -> Error(Client.ValidationError "Expected Mute or Unmute.")
                            | "AcknowledgeNotification" ->
                                guid "notificationId" cmd.AttemptId
                                |> Result.bind (fun notificationId ->
                                    ReviewWorkflow.ReviewWorkflow.acknowledgeNotification
                                        binding operationId cmd.ActorId notificationId |> mapError)
                            | "CreateTask" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.createTaskWithSpecAs binding actor operationId cmd.TaskId cmd.Title cmd.Html
                                    |> mapError)
                            | "ReviseTaskSpec" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.reviseTaskSpecPinnedAs binding actor operationId cmd.TaskId
                                        cmd.ExpectedVersion null cmd.Title cmd.Html |> mapError)
                            | "ReviseTaskSpecPinned" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.reviseTaskSpecPinnedAs binding actor operationId cmd.TaskId
                                        cmd.ExpectedVersion cmd.BaseOid cmd.Title cmd.Html |> mapError)
                            | "ImportTaskSpec" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.importTaskSpecAs binding actor operationId cmd.TaskId
                                        cmd.ExpectedVersion cmd.BaseOid cmd.TargetRef cmd.Title cmd.SourceRef |> mapError)
                            | "AdoptTaskSpec" ->
                                if not (String.IsNullOrWhiteSpace cmd.Html) then
                                    Error(Client.ValidationError "Provide either newSpecHtml or expectedCurrentSpecBlobOid, not both.")
                                else
                                    withAdministrativeActor (fun actor ->
                                        ReviewWorkflow.ReviewWorkflow.adoptTaskSpecAs binding actor operationId cmd.TaskId
                                            cmd.ActorId cmd.ExpectedTaskVersion cmd.ExpectedVersion
                                            cmd.ExpectedAgentVersion cmd.AssignmentGeneration cmd.BaseOid cmd.Summary |> mapError)
                            | "RegisterAgent" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.registerAgentAs binding actor operationId cmd.ActorId
                                    |> mapError)
                            | "StartTasklessWork" ->
                                ReviewWorkflow.ReviewWorkflow.startTasklessWork binding operationId
                                    cmd.ActorId cmd.ExpectedAgentVersion cmd.Summary |> mapError
                            | "YieldTasklessWork" ->
                                ReviewWorkflow.ReviewWorkflow.yieldTasklessWork binding operationId
                                    cmd.ActorId cmd.ExpectedAgentVersion cmd.Summary |> mapError
                            | "CreateRole" ->
                                withAdministrativeActor (fun actor ->
                                    RoleInstructions.RoleInstructions.createRole binding actor operationId
                                        cmd.TaskId cmd.Title cmd.Html |> mapError)
                            | "UpdateRole" ->
                                withAdministrativeActor (fun actor ->
                                    RoleInstructions.RoleInstructions.updateRole binding actor operationId
                                        cmd.TaskId cmd.ExpectedVersion cmd.Title cmd.Html |> mapError)
                            | "AttachRole" ->
                                withAdministrativeActor (fun actor ->
                                    RoleInstructions.RoleInstructions.attachRole binding actor operationId
                                        cmd.OwnerId cmd.TaskId cmd.ExpectedVersion |> mapError)
                            | "DetachRole" ->
                                withAdministrativeActor (fun actor ->
                                    RoleInstructions.RoleInstructions.detachRole binding actor operationId
                                        cmd.OwnerId cmd.TaskId cmd.ExpectedVersion |> mapError)
                            | "SetAgentInstructions" ->
                                withAdministrativeActor (fun actor ->
                                    RoleInstructions.RoleInstructions.setAgentInstructions binding actor operationId
                                        cmd.OwnerId cmd.ExpectedVersion cmd.Html |> mapError)
                            | "AssignTask" ->
                                guid "attemptId" cmd.AttemptId
                                |> Result.bind (fun attemptId ->
                                    withAdministrativeActor (fun actor ->
                                        ReviewWorkflow.ReviewWorkflow.assignTaskAs binding actor operationId cmd.TaskId
                                            attemptId cmd.OwnerId
                                        |> mapError))
                            | "ReissueAssignment" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.reissueAssignmentAs binding actor operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Summary cmd.Html
                                    |> mapError)
                            | "AcknowledgeAssignment" ->
                                ReviewWorkflow.ReviewWorkflow.acknowledgeAssignment binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                                |> mapError
                            | "BeginAttempt" ->
                                ReviewWorkflow.ReviewWorkflow.beginAttemptWithSession binding operationId cmd.TaskId
                                    cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration cmd.SessionId
                                |> mapError
                            | "BindReviewBase" ->
                                ReviewWorkflow.ReviewWorkflow.bindReviewBase binding operationId cmd.TaskId
                                    cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                                    cmd.BaseOid cmd.TargetRef
                                |> mapError
                            | "BindAttemptSource" ->
                                ReviewWorkflow.ReviewWorkflow.bindAttemptSource binding operationId cmd.TaskId
                                    cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                                    cmd.SourceRef cmd.BaseOid cmd.TargetRef
                                    cmd.AllowDetached cmd.AllowUncommitted
                                |> mapError
                            | "RebindAttemptSource" ->
                                ReviewWorkflow.ReviewWorkflow.rebindAttemptSource binding operationId cmd.TaskId
                                    cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                                    cmd.SourceRef
                                |> mapError
                            | "CheckpointAttempt" ->
                                ReviewWorkflow.ReviewWorkflow.checkpointAttemptWithSession binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                    cmd.AssignmentGeneration cmd.Summary cmd.NextAction cmd.BaseOid cmd.Evidence cmd.SessionId
                                |> mapError
                            | "YieldAgent" ->
                                ReviewWorkflow.ReviewWorkflow.yieldAgentWithSession binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                    cmd.AssignmentGeneration cmd.Summary cmd.NextAction cmd.SessionId |> mapError
                            // Backward compatibility for operations already persisted in
                            // protocol 3.57 pending journals; no public resume tool exists.
                            | "ResumeAgent" ->
                                ReviewWorkflow.ReviewWorkflow.resumeAgentWithSession binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                    cmd.AssignmentGeneration cmd.SessionId |> mapError
                            | "CompleteAgentWork" ->
                                ReviewWorkflow.ReviewWorkflow.completeAgentWorkWithSession binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                    cmd.AssignmentGeneration cmd.Summary cmd.SessionId |> mapError
                            | "ParkAttempt" | "BlockAttempt" | "ResumeAttempt"
                            | "AbandonAttempt" | "CancelTask" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.changeAttemptAdministrationAs binding actor operationId
                                        cmd.TaskId cmd.Action cmd.ExpectedTaskVersion cmd.ExpectedVersion
                                        cmd.ExpectedAgentVersion cmd.AssignmentGeneration cmd.Reason |> mapError)
                            | "CloseTask" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.closeTaskAs binding actor operationId
                                        cmd.TaskId cmd.ExpectedTaskVersion |> mapError)
                            | "CancelOpenTask" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.cancelOpenTaskAs binding actor operationId
                                        cmd.TaskId cmd.ExpectedTaskVersion cmd.Reason |> mapError)
                            | "TransferOwner" ->
                                guid "newAttemptId" cmd.AttemptId
                                |> Result.bind (fun newId ->
                                    withAdministrativeActor (fun actor ->
                                        ReviewWorkflow.ReviewWorkflow.transferAttemptOwnerAs binding actor operationId
                                            cmd.TaskId newId cmd.OwnerId
                                            cmd.ExpectedTaskVersion cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                            cmd.AssignmentGeneration cmd.Reason |> mapError))
                            | "ReopenTask" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.reopenTaskAs binding actor operationId
                                        cmd.TaskId cmd.ExpectedTaskVersion cmd.Reason |> mapError)
                            | "RecoverTasklessWork" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.recoverTasklessWorkAs binding actor operationId
                                        cmd.ActorId cmd.ExpectedAgentVersion cmd.Reason |> mapError)
                            | "SetAgentState" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.setAgentAdministrativeStateAs binding actor operationId
                                        cmd.ActorId cmd.Summary cmd.ExpectedAgentVersion cmd.Reason |> mapError)
                            | "ReissueQueuedAttempt" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.reissueQueuedAttemptAs binding actor operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Reason |> mapError)
                            | "RecoverAgentAssignment" ->
                                withAdministrativeActor (fun actor ->
                                    ReviewWorkflow.ReviewWorkflow.recoverAgentAssignmentAs binding actor operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Reason |> mapError)
                            | "AcknowledgeFeedback" ->
                                ReviewWorkflow.ReviewWorkflow.acknowledgeBranchReview binding operationId
                                    cmd.TaskId cmd.ActorId cmd.DecisionSequence
                                |> mapError
                            | _ -> Error(Client.ValidationError "Unsupported  task operation.")
                        )
                    result |> Result.bind (fun write ->
                        if cmd.Action = "NotifyAgent" then
                            ReviewWorkflow.ReviewWorkflow.notificationDelivery binding write
                            |> mapError
                            |> Result.map (fun delivery ->
                                OperationResponse.presentWithNotification (Some delivery) write)
                        else Ok(OperationResponse.present write))))


    let writeForContext (context: ProjectPaths.ResolvedProject) (cmd: TaskWriteCommand) rt =
        writeUsingContext (Some context) cmd rt

    let write (cmd: TaskWriteCommand) rt =
        writeUsingContext None cmd rt

module ChallengeApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) =
        result |> Result.mapError LedgerErrors.toEffect

    let private guid (raw: string) =
        match Guid.TryParse raw with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(Client.ValidationError "A stable nonempty operation UUID is required.")

    let read (cmd: ChallengeReadCommand) rt =
        LedgerBindingConfig.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            match cmd.Action with
            | "List" ->
                ChallengeWorkflow.ChallengeWorkflow.listPage binding cmd.ActorId
                    cmd.Limit cmd.AfterChallengeId cmd.ExpectedSnapshotCommit
                |> mapError |> Result.map box
            | "Get" ->
                ChallengeWorkflow.ChallengeWorkflow.getPage binding cmd.ActorId cmd.ChallengeId
                    cmd.Limit cmd.AfterEventSequence cmd.ExpectedSnapshotCommit
                |> mapError |> Result.map box
            | _ -> Error(Client.ValidationError "Unsupported challenge read action."))

    let write (cmd: ChallengeWriteCommand) rt =
        LedgerBindingConfig.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            guid cmd.OperationId
            |> Result.bind (fun operationId ->
                let result =
                    PendingIntentJournal.executeLedgerWrite binding operationId "ChallengeWrite" cmd
                        (fun () ->
                            let record =
                                match cmd.Action with
                                | "Create" ->
                                    if isNull (box cmd.Conditions) then
                                        Error(Client.ValidationError "Conditions are required.")
                                    else
                                        let definitions : ChallengeWorkflow.ChallengeConditionSpec list =
                                            cmd.Conditions |> List.map (fun definition ->
                                                { OwnerKind = definition.OwnerKind
                                                  OwnerAgentId = definition.OwnerAgentId
                                                  SuggestedAgentId = definition.SuggestedAgentId
                                                  Description = definition.Description })
                                        ChallengeWorkflow.ChallengeWorkflow.create
                                            binding operationId cmd.ActorId cmd.Title cmd.TaskId definitions
                                        |> mapError |> Result.map _.Receipt
                                | "Subscribe" ->
                                    ChallengeWorkflow.ChallengeWorkflow.setSubscription
                                        binding operationId cmd.ActorId cmd.Subscribe
                                    |> mapError
                                | "Split" ->
                                    if isNull (box cmd.Conditions) then
                                        Error(Client.ValidationError "Split parts are required.")
                                    else
                                        let parts : ChallengeWorkflow.ChallengeConditionSpec list =
                                            cmd.Conditions |> List.map (fun part ->
                                                { OwnerKind = part.OwnerKind
                                                  OwnerAgentId = part.OwnerAgentId
                                                  SuggestedAgentId = part.SuggestedAgentId
                                                  Description = part.Description })
                                        ChallengeWorkflow.ChallengeWorkflow.splitCondition
                                            binding operationId cmd.ActorId cmd.ChallengeId
                                            cmd.ConditionId cmd.ExpectedVersion parts
                                        |> mapError |> Result.map _.Receipt
                                | "Assign" ->
                                    ChallengeWorkflow.ChallengeWorkflow.assignCondition
                                        binding operationId cmd.ActorId cmd.ChallengeId
                                        cmd.ConditionId cmd.ExpectedVersion cmd.NewOwnerAgentId
                                    |> mapError
                                | "Resolve" ->
                                    if cmd.Outcome <> "Satisfied" && cmd.Outcome <> "Blocked" then
                                        Error(Client.ValidationError "Challenge condition outcome must be Satisfied or Blocked.")
                                    elif cmd.Outcome = "Satisfied" && not (String.IsNullOrWhiteSpace cmd.Reason) then
                                        Error(Client.ValidationError "Satisfied condition does not accept a blocked reason.")
                                    elif cmd.Outcome = "Blocked" && String.IsNullOrWhiteSpace cmd.Reason then
                                        Error(Client.ValidationError "Blocked condition requires a reason.")
                                    else
                                        ChallengeWorkflow.ChallengeWorkflow.resolve binding operationId cmd.ActorId
                                            cmd.ChallengeId cmd.ConditionId cmd.ExpectedVersion cmd.Evidence cmd.Reason
                                        |> mapError
                                | "Reopen" ->
                                    ChallengeWorkflow.ChallengeWorkflow.reopen binding operationId cmd.ActorId
                                        cmd.ChallengeId cmd.ConditionId cmd.ExpectedVersion cmd.Reason
                                    |> mapError
                                | "Cancel" ->
                                    ChallengeWorkflow.ChallengeWorkflow.cancel binding operationId cmd.ActorId
                                        cmd.ChallengeId cmd.ExpectedVersion cmd.Reason
                                    |> mapError
                                | "BlockAttempt" ->
                                    ChallengeWorkflow.ChallengeWorkflow.blockAttempt binding operationId cmd.ActorId
                                        cmd.TaskId cmd.AttemptId
                                        cmd.ExpectedTaskVersion cmd.ExpectedAttemptVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Reason cmd.DependencyChallengeIds
                                    |> mapError
                                | "RequeueAttempt" ->
                                    ChallengeWorkflow.ChallengeWorkflow.requeueAttempt binding operationId cmd.ActorId
                                        cmd.TaskId cmd.AttemptId
                                        cmd.ExpectedTaskVersion cmd.ExpectedAttemptVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Reason
                                    |> mapError
                                | _ -> Error(Client.ValidationError "Unsupported challenge mutation.")
                            record)
                result |> Result.map (fun receipt ->
                    if cmd.Action = "Create" || cmd.Action = "Split" then
                        let challengeId = operationId.ToString("N")
                        let challengeId =
                            if cmd.Action = "Split" then Guid.Parse(cmd.ChallengeId).ToString("N")
                            else challengeId
                        let conditionIds =
                            if cmd.Action = "Split" then
                                cmd.ConditionId ::
                                    (List.init (cmd.Conditions.Length - 1) (fun index ->
                                        let digest =
                                            SHA256.HashData(Encoding.UTF8.GetBytes(
                                                challengeId + ":split:" + operationId.ToString("N")
                                                + ":" + string (index + 1)))
                                        Guid(digest.AsSpan(0, 16)).ToString("N")))
                            else
                                cmd.Conditions |> List.mapi (fun index _ ->
                                    let digest = SHA256.HashData(Encoding.UTF8.GetBytes(challengeId + ":condition:" + string index))
                                    Guid(digest.AsSpan(0, 16)).ToString("N"))
                        box {| challengeId = challengeId
                               conditionIds = conditionIds
                               receipt = OperationResponse.present receipt |}
                    else OperationResponse.present receipt)))

// Operator-only recovery re-enters the current typed writers. Archived or
// retired lifecycle commands cannot be made executable by choosing a new
// session or constructing a new request with the original UUID.
module PendingRecoveryApi =
    let private requiredId label (value: string) =
        match Guid.TryParse value with
        | true, parsed when parsed <> Guid.Empty -> Ok parsed
        | _ -> Error(Client.ValidationError $"Valid {label} UUID required.")

    let private rehydrate<'a> (record: PendingIntent) =
        try
            let command = JsonSerializer.Deserialize<'a>(record.CommandJson)
            if isNull (box command)
               || not (String.Equals(JsonSerializer.Serialize(command), record.CommandJson, StringComparison.Ordinal)) then
                Error(Client.ValidationError "Saved command does not roundtrip byte-for-byte through the CURRENT typed writer; do not retry.")
            else Ok command
        with ex ->
            Error(Client.ValidationError $"Saved command cannot be safely decoded by the current protocol: {ex.Message}")

    let retry (cmd: RetryPendingIntentCommand) rt =
        if String.IsNullOrWhiteSpace cmd.OperatorId then
            Error(Client.ValidationError "An operator identity is required for explicit retry.")
        else
            requiredId "pending operation" cmd.OperationId
            |> Result.bind (fun operationId ->
                LedgerBindingConfig.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    PendingIntentJournal.prepareRetry binding operationId
                        cmd.ExpectedCommandSha256 cmd.ExpectedVerifiedHead
                    |> Result.bind (fun (stored, remote) ->
                        match remote with
                        | Some receipt ->
                            // Do NOT auto-delete a saved intent; exact canonical
                            // Applied proof is returned for explicit acknowledgment.
                            Ok (box {| operationId = stored.OperationId
                                       disposition = "Applied"
                                       alreadyApplied = true
                                       verifiedLedgerHead = cmd.ExpectedVerifiedHead
                                       requestHash = receipt.RequestHash |})
                        | None ->
                            match PendingIntentJournal.normalizeCommandType stored.CommandType with
                            | "TaskWrite" ->
                                rehydrate<TaskWriteCommand> stored
                                |> Result.bind (fun saved ->
                                    if saved.ProjectName <> cmd.ProjectName
                                       || not (match Guid.TryParse saved.OperationId with
                                               | true, id -> id = operationId | _ -> false) then
                                        Error(Client.ValidationError "Original project/operation identity changed during retry decode.")
                                    elif not (List.contains saved.Action
                                            [ "CreateTask"; "ReviseTaskSpec"; "CloseTask"; "ReopenTask";
                                              "CancelOpenTask" ]) then
                                        Error(Client.ValidationError
                                            "ReplayRejectedStale: this saved TaskWrite action is not safe to retry through the current protocol; use inspected supersession.")
                                    else
                                        TaskApi.write saved rt)
                            | "ReviewWrite" ->
                                rehydrate<ReviewWriteCommand> stored
                                |> Result.bind (fun saved ->
                                    if saved.ProjectName <> cmd.ProjectName
                                       || not (match Guid.TryParse saved.OperationId with
                                               | true, id -> id = operationId | _ -> false) then
                                        Error(Client.ValidationError "Original project/operation identity changed during retry decode.")
                                    elif not (List.contains saved.Action
                                            [ "PutDocument"; "Submit"; "Update"; "Claim"; "Decide";
                                              "Release"; "Withdraw" ]) then
                                        Error(Client.ValidationError
                                            "ReplayRejectedStale: saved review action cannot be retried by this operator tool.")
                                    else ReviewApi.write saved rt)
                            | _ ->
                                Error(Client.ValidationError "Saved ledger setup/lifecycle command is not on the operator retry allowlist."))))

    let supersede (cmd: SupersedePendingIntentCommand) rt =
        requiredId "pending operation" cmd.OperationId
        |> Result.bind (fun operationId ->
            requiredId "related applied operation" cmd.RelatedOperationId
            |> Result.bind (fun relatedId ->
                LedgerBindingConfig.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    PendingIntentJournal.supersede binding operationId
                        cmd.ExpectedCommandSha256 cmd.ExpectedVerifiedHead
                        cmd.OperatorId cmd.Reason relatedId cmd.RelatedRequestHash
                    |> Result.map (fun recorded ->
                        // No historical session or original command JSON
                        // in the write response; targeted read-only inspection
                        // is the explicit mechanism to retrieve those.
                        box {| operationId = recorded.operationId
                               disposition = "Superseded"
                               alreadySuperseded = recorded.alreadySuperseded
                               verifiedLedgerHead = recorded.verifiedLedgerHead
                               originalCommandSha256 = recorded.archive.Original.CommandSha256
                               operatorId = recorded.archive.OperatorId
                               reason = recorded.archive.Reason
                               relatedOperationId = recorded.archive.RelatedOperationId
                               relatedRequestHash = recorded.archive.RelatedRequestHash
                               relatedOriginCommit = recorded.archive.RelatedOriginCommit |}))))

    // The operator explicitly adopts a canonical-state proof: no remote write.
    let abandon (cmd: AbandonPendingIntentCommand) rt =
        requiredId "pending operation" cmd.OperationId
        |> Result.bind (fun operationId ->
            LedgerBindingConfig.load cmd.ProjectName rt
            |> Result.bind (fun binding ->
                PendingIntentJournal.abandon binding operationId
                    cmd.ExpectedCommandSha256 cmd.ExpectedVerifiedHead
                    cmd.OperatorId cmd.Reason
                |> Result.map (fun recorded ->
                    box {| operationId = recorded.operationId
                           disposition = "Abandoned"
                           alreadyAbandoned = recorded.alreadyAbandoned
                           verifiedLedgerHead = recorded.verifiedLedgerHead
                           originalCommandSha256 = recorded.archive.Original.CommandSha256
                           operatorId = recorded.archive.OperatorId
                           reason = recorded.archive.Reason
                           taskStateProof = recorded.archive.ObsoleteTask
                           reviewStateProof = recorded.archive.ObsoleteReview |})))

// Intentionally local-only: never route these through ChallengeWriteCommand
// or an agent-attributed MCP tool. The TUI supplies the authenticated OAuth
// operator principal through its Program-owned closure.
module OperatorChallengeApi =
    let private mapError result = Result.mapError LedgerErrors.toEffect result

    let list rt =
        ProjectBrowser.listGitProjects rt
        |> Result.map (fun projects ->
            let results =
                projects
                |> List.map (fun (ProjectName project) ->
                    match LedgerBindingConfig.load project rt with
                    | Error _ -> [], [] // Unbound projects cannot be read as trusted ledgers.
                    | Ok binding ->
                        match ChallengeWorkflow.ChallengeWorkflow.listOperator binding project with
                        | Ok items -> items, []
                        | Error error -> [], [ sprintf "%s: %A" project error ])
            { Items = results |> List.collect fst |> List.sortByDescending (fun item ->
                          OperatorChallengePresentation.isActionable item, item.CreatedAtUtc)
              Warnings = results |> List.collect snd })

    let details projectName challengeId rt =
        LedgerBindingConfig.load projectName rt
        |> Result.bind (fun binding ->
            ChallengeWorkflow.ChallengeWorkflow.getOperatorPage binding challengeId 16 0L ""
            |> mapError)

    let reply (authenticatedOperator: string) (request: OperatorChallengeReply) rt =
        if String.IsNullOrWhiteSpace authenticatedOperator
           || not (authenticatedOperator.StartsWith("operator:", StringComparison.Ordinal))
           || authenticatedOperator.Length > 120 then
            Error(Client.PermissionDenied "An authenticated operator identity is required.")
        else
            match Guid.TryParse request.OperationId with
            | false, _ -> Error(Client.ValidationError "An operator response requires a stable operation UUID.")
            | true, id when id = Guid.Empty ->
                Error(Client.ValidationError "An operator response requires a nonempty operation UUID.")
            | true, id ->
                LedgerBindingConfig.load request.ProjectName rt
                |> Result.bind (fun binding ->
                    PendingIntentJournal.executeLedgerWrite binding id "OperatorChallengeReply" request
                        (fun () ->
                            ChallengeWorkflow.ChallengeWorkflow.respondOperator binding id
                                authenticatedOperator request.ChallengeId request.ConditionId
                                request.ExpectedVersion request.Response
                            |> mapError))
