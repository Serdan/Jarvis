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
                    let rec obtain attempts =
                        try
                            use held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                                      FileShare.None, 4096, FileOptions.None)
                            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                            operation ()
                        with :? IOException when attempts < 50 ->
                            System.Threading.Thread.Sleep(40)
                            obtain (attempts + 1)
                    obtain 0
        with ex ->
            Error(Client.ValidationError $"Cannot lock  trust binding: {ex.Message}")

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
        with ex -> Error(Client.ValidationError $"Cannot read  binding: {ex.Message}")

    let private writeStored (path: string) (stored: LedgerTrustBinding) overwrite =
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            try
                File.WriteAllText(temp, JsonSerializer.Serialize(stored))
                if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                File.Move(temp, path, overwrite)
                Ok ()
            with ex ->
                Error(Client.ValidationError $"Cannot persist  binding: {ex.Message}")
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
    let load projectName rt =
        match ProjectPaths.resolveProjectRoot projectName rt with
        | Error error -> Error error
        | Ok(FolderPath root) ->
            let path = pathForRoot root
            if not (File.Exists path) then
                Error(Client.ValidationError $" ledger disabled for this checkout. Approved binding required at {path}. No V1 fallback.")
            else
                withConfigLock path (fun () ->
                    readStored path
                    |> Result.bind (fun stored ->
                        validStored root stored
                        |> Result.bind (fun binding ->
                            LedgerStore.LedgerStore.fetch binding
                            |> Result.mapError LedgerErrors.toEffect
                            |> Result.bind (fun snapshot ->
                                LedgerStore.LedgerStore.verifyTrustedHead binding
                                    stored.HighestTrustedHead snapshot.Commit
                                |> Result.mapError LedgerErrors.toEffect
                                |> Result.bind (fun () ->
                                    if stored.HighestTrustedHead = snapshot.Commit then Ok binding
                                    else
                                        writeStored path
                                            { stored with HighestTrustedHead = snapshot.Commit } true
                                        |> Result.map (fun () -> binding))))))



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
    let rebindRemote projectName rt remoteName approvedFetchUrl approvedPushUrl
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
                    withConfigLock path (fun () ->
                        readStored path
                        |> Result.bind (fun stored ->
                            validStored root stored
                            |> Result.bind (fun current ->
                                if current <> old then
                                    Error(Client.ValidationError
                                        " binding changed while rebinding; reload and retry.")
                                else
                                    let newBinding =
                                        { old with RemoteName = remoteName
                                                   ApprovedFetchUrl = approvedFetchUrl
                                                   ApprovedPushUrl = approvedPushUrl }
                                    LedgerStore.LedgerStore.fetch newBinding
                                    |> Result.mapError LedgerErrors.toEffect
                                    |> Result.bind (fun snapshot ->
                                        // A caller-supplied anchor cannot replace what this
                                        // checkout already trusted. Both must be ancestors.
                                        LedgerStore.LedgerStore.verifyTrustedHead newBinding
                                            stored.HighestTrustedHead snapshot.Commit
                                        |> Result.mapError LedgerErrors.toEffect
                                        |> Result.bind (fun () ->
                                            LedgerStore.LedgerStore.verifyTrustedHead newBinding
                                                expectedTrustedHead snapshot.Commit
                                            |> Result.mapError LedgerErrors.toEffect
                                            |> Result.bind (fun () ->
                                                let replacement =
                                                    { stored with RemoteName = remoteName
                                                                  ApprovedFetchUrl = approvedFetchUrl
                                                                  ApprovedPushUrl = approvedPushUrl
                                                                  HighestTrustedHead = snapshot.Commit }
                                                let backup =
                                                    path + ".previous-" + Guid.NewGuid().ToString("N")
                                                try
                                                    File.WriteAllText(backup, JsonSerializer.Serialize(stored))
                                                    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                                                        File.SetUnixFileMode(backup,
                                                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                                                    writeStored path replacement true
                                                    |> Result.map (fun () -> replacement)
                                                with ex ->
                                                    Error(Client.ValidationError
                                                        $" rebind backup failed: {ex.Message}")))))))


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

    let private journalDirectory root =
        let bindingPath = LedgerBindingConfig.pathForRoot root
        Path.Combine(Path.GetDirectoryName bindingPath, "pending",
                     Path.GetFileNameWithoutExtension bindingPath)

    let private filePath (binding: LedgerStore.LedgerBinding) (operationId: Guid) =
        Path.Combine(journalDirectory binding.RepositoryPath, operationId.ToString("N") + ".json")

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

    let persist (binding: LedgerStore.LedgerBinding) operationId commandType commandJson =
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
            if File.Exists file then
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

    let listAtRoot root =
        try
            let directory = journalDirectory root
            if not (Directory.Exists directory) then Ok []
            elif DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                Error(Client.ValidationError "Pending  journal directory must not be a symbolic link.")
            else
                let files = Directory.GetFiles(directory, "*.json")
                if files.Length > 1000 then
                    Error(Client.ValidationError "Too many pending operations; manual recovery required.")
                else
                    files
                    |> Array.sort
                    |> Array.fold (fun state file ->
                        state |> Result.bind (fun items ->
                            readFile file |> Result.bind (fun record ->
                                match Guid.TryParse record.OperationId with
                                | true, id when Path.GetFileName(file) = id.ToString("N") + ".json" ->
                                    Ok(record :: items)
                                | _ ->
                                    Error(Client.ValidationError "Unexpected pending operation journal filename."))))
                        (Ok [])
                    |> Result.map List.rev
        with ex ->
            Error(Client.ValidationError $"Cannot enumerate pending  operations: {ex.Message}")

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
                | "TaskWrite", "MigrateOrthogonalAgentState" ->
                    common @ [ "BaseOid", "expectedLedgerHead" ]
                | "TaskWrite", "RegisterAgent" -> [ "ActorId", "agentId" ]
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
                | "TaskWrite", "ParkAgent" ->
                    [ "ActorId", "agentId"; "ExpectedAgentVersion", "expectedAgentVersion"
                      "Reason", "reason" ]
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
    let acknowledge (binding: LedgerStore.LedgerBinding) (operationId: Guid) (expectedRequestHash: string) =
        if String.IsNullOrWhiteSpace expectedRequestHash
           || expectedRequestHash.Length <> 64 then
            Error(Client.ValidationError "Expected exact 64-character remote request hash.")
        else
            listAtRoot binding.RepositoryPath
            |> Result.bind (fun records ->
                match records |> List.tryFind (fun record ->
                    record.OperationId = operationId.ToString("D")) with
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

    let execute binding operationId commandType command (apply: unit -> Result<'t, EffectError>) =
        let commandJson = JsonSerializer.Serialize(command)
        persist binding operationId commandType commandJson
        |> Result.bind (fun () ->
            let outcome = apply ()
            match outcome with
            | Error(Client.LedgerFailureError failure) ->
                // The caller-owned UUID remains recoverable even after a harness reset.
                Error(Client.LedgerFailureError
                    { failure with OperationId = Some(operationId.ToString("D")) })
            | Error _ -> outcome // Preserve exact intent for recovery after lost response.
            | Ok _ ->
                try
                    let path = filePath binding operationId
                    if File.Exists path then File.Delete path
                with _ -> () // A stale entry is safe: remote UUID remains authoritative.
                outcome
        )

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
    let present (write: LedgerStore.LedgerWrite) =
        use document = JsonDocument.Parse(write.Operation.OriginalResultJson)
        box {| operation = write.Operation
               head = write.CurrentHead
               alreadyApplied = write.AlreadyApplied
               originalResult = document.RootElement.Clone() |}

module LedgerApi =
    let private id label (raw: string) =
        match Guid.TryParse raw with
        | true, parsed when parsed <> Guid.Empty -> Ok parsed
        | _ -> Error(Client.ValidationError $"Valid {label} UUID required.")

    let read (cmd: LedgerReadCommand) rt =
        match cmd.Action with
        | "Health" -> LedgerBindingConfig.health cmd.ProjectName rt |> Result.map box
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
module TaskApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError LedgerErrors.toEffect

    let private guid label (text: string) =
        match Guid.TryParse text with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(Client.ValidationError $"Invalid {label}: a caller-stable UUID is required.")

    let read (cmd: TaskReadCommand) rt =
        LedgerBindingConfig.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            match cmd.Action with
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
                ReviewWorkflow.ReviewWorkflow.getBranchAgentInbox binding cmd.AgentId
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

    let write (cmd: TaskWriteCommand) rt =
        LedgerBindingConfig.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            guid "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                if cmd.Action = "ExportTaskSpec" then
                    // Filesystem mutation must use the write permission route,
                    // not the TaskRead protocol or its read-only capability.
                    ReviewWorkflow.ReviewWorkflow.exportTaskSpecWithId binding operationId cmd.TaskId
                        cmd.SourceRef cmd.BaseOid |> mapError |> Result.map box
                else
                    // Session routing metadata does not change ordinary task-write
                    // identity. Begin/Resume deliberately associate an execution.
                    let journalCommand =
                        if cmd.Action = "BeginAttempt" || cmd.Action = "ResumeAgent" then cmd
                        else { cmd with SessionId = "" }
                    // An operation already recorded in Git is a retry, even when
                    // the session is now active. Route it through the normal
                    // receipt/journal path so its original result is returned
                    // and a matching pending local intent is cleared.
                    let existingResumeOperation =
                        if cmd.Action <> "ResumeAgent" then Ok false
                        else
                            LedgerStore.LedgerStore.getOperationStatus binding operationId
                            |> Result.mapError LedgerErrors.toEffect
                            |> Result.map (fun (_, operation) -> operation.IsSome)
                    let sessionStatus =
                        existingResumeOperation
                        |> Result.bind (fun alreadyRecorded ->
                            if cmd.Action <> "ResumeAgent" || alreadyRecorded then Ok None
                            else
                                ReviewWorkflow.ReviewWorkflow.inspectWorkingResumeSession binding
                                    cmd.TaskId cmd.ActorId cmd.AssignmentGeneration cmd.SessionId
                                |> mapError)
                    match sessionStatus with
                    | Error error -> Error error
                    | Ok(Some status) -> Ok(box status)
                    | Ok None ->
                        let result =
                            PendingIntentJournal.executeLedgerWrite binding operationId "TaskWrite" journalCommand (fun () ->
                                match cmd.Action with
                                | "SubmitIntegration" ->
                                    guid "submissionId" cmd.AttemptId
                                    |> Result.bind (fun submissionId ->
                                        ReviewWorkflow.ReviewWorkflow.submitIntegration binding operationId
                                            cmd.TaskId cmd.ActorId submissionId cmd.SourceRef cmd.BaseOid
                                            cmd.TargetRef cmd.Reason |> mapError)
                                | "CompleteIntegration" ->
                                    guid "submissionId" cmd.AttemptId
                                    |> Result.bind (fun submissionId ->
                                        ReviewWorkflow.ReviewWorkflow.completeIntegration binding operationId
                                            cmd.TaskId cmd.ActorId submissionId cmd.BaseOid |> mapError)
                                | "NotifyAgent" ->
                                    ReviewWorkflow.ReviewWorkflow.notifyAgent binding operationId
                                        cmd.ActorId cmd.SourceRef cmd.OwnerId cmd.TaskId cmd.Summary |> mapError
                                | "AcknowledgeNotification" ->
                                    guid "notificationId" cmd.AttemptId
                                    |> Result.bind (fun notificationId ->
                                        ReviewWorkflow.ReviewWorkflow.acknowledgeNotification
                                            binding operationId cmd.ActorId notificationId |> mapError)
                                | "CreateTask" ->
                                    ReviewWorkflow.ReviewWorkflow.createTaskWithSpec binding operationId cmd.TaskId cmd.Title cmd.Html
                                    |> mapError
                                | "ReviseTaskSpec" ->
                                    ReviewWorkflow.ReviewWorkflow.reviseTaskSpec binding operationId cmd.TaskId
                                        cmd.ExpectedVersion cmd.Title cmd.Html |> mapError
                                | "ReviseTaskSpecPinned" ->
                                    ReviewWorkflow.ReviewWorkflow.reviseTaskSpecPinned binding operationId cmd.TaskId
                                        cmd.ExpectedVersion cmd.BaseOid cmd.Title cmd.Html |> mapError
                                | "ImportTaskSpec" ->
                                    ReviewWorkflow.ReviewWorkflow.importTaskSpec binding operationId cmd.TaskId
                                        cmd.ExpectedVersion cmd.BaseOid cmd.TargetRef cmd.Title cmd.SourceRef |> mapError
                                | "AdoptTaskSpec" ->
                                    ReviewWorkflow.ReviewWorkflow.adoptTaskSpec binding operationId cmd.TaskId
                                        cmd.ActorId cmd.ExpectedTaskVersion cmd.ExpectedVersion
                                        cmd.ExpectedAgentVersion cmd.AssignmentGeneration cmd.BaseOid cmd.Summary |> mapError
                                | "MigrateOrthogonalAgentState" ->
                                    ReviewWorkflow.ReviewWorkflow.migrateOrthogonalAgentState binding operationId cmd.BaseOid
                                    |> mapError
                                | "RegisterAgent" ->
                                    ReviewWorkflow.ReviewWorkflow.registerAgent binding operationId cmd.ActorId
                                    |> mapError
                                | "AssignTask" ->
                                    guid "attemptId" cmd.AttemptId
                                    |> Result.bind (fun attemptId ->
                                        ReviewWorkflow.ReviewWorkflow.assignTask binding operationId cmd.TaskId
                                            attemptId cmd.OwnerId
                                        |> mapError)
                                | "ReissueAssignment" ->
                                    ReviewWorkflow.ReviewWorkflow.reissueAssignment binding operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Summary cmd.Html
                                    |> mapError
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
                                    ReviewWorkflow.ReviewWorkflow.checkpointAttempt binding operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Summary cmd.NextAction cmd.BaseOid cmd.Evidence
                                    |> mapError
                                | "YieldAgent" ->
                                    ReviewWorkflow.ReviewWorkflow.yieldAgent binding operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Summary cmd.NextAction |> mapError
                                | "ResumeAgent" ->
                                    ReviewWorkflow.ReviewWorkflow.resumeAgentWithSession binding operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.SessionId |> mapError
                                | "CompleteAgentWork" ->
                                    ReviewWorkflow.ReviewWorkflow.completeAgentWork binding operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Summary |> mapError
                                | "ParkAttempt" | "BlockAttempt" | "ResumeAttempt"
                                | "AbandonAttempt" | "CancelTask" ->
                                    ReviewWorkflow.ReviewWorkflow.changeAttemptAdministration binding operationId
                                        cmd.TaskId cmd.Action cmd.ExpectedTaskVersion cmd.ExpectedVersion
                                        cmd.ExpectedAgentVersion cmd.AssignmentGeneration cmd.Reason |> mapError
                                | "CloseTask" ->
                                    ReviewWorkflow.ReviewWorkflow.closeTask binding operationId
                                        cmd.TaskId cmd.ExpectedTaskVersion |> mapError
                                | "CancelOpenTask" ->
                                    ReviewWorkflow.ReviewWorkflow.cancelOpenTask binding operationId
                                        cmd.TaskId cmd.ExpectedTaskVersion cmd.Reason |> mapError
                                | "TransferOwner" ->
                                    guid "newAttemptId" cmd.AttemptId
                                    |> Result.bind (fun newId ->
                                        ReviewWorkflow.ReviewWorkflow.transferAttemptOwner binding operationId
                                            cmd.TaskId newId cmd.OwnerId
                                            cmd.ExpectedTaskVersion cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                            cmd.AssignmentGeneration cmd.Reason |> mapError)
                                | "ReopenTask" ->
                                    ReviewWorkflow.ReviewWorkflow.reopenTask binding operationId
                                        cmd.TaskId cmd.ExpectedTaskVersion cmd.Reason |> mapError
                                | "ParkAgent" ->
                                    ReviewWorkflow.ReviewWorkflow.parkAgent binding operationId
                                        cmd.ActorId cmd.ExpectedAgentVersion cmd.Reason |> mapError
                                | "ReissueQueuedAttempt" ->
                                    ReviewWorkflow.ReviewWorkflow.reissueQueuedAttempt binding operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Reason |> mapError
                                | "RecoverAgentAssignment" ->
                                    ReviewWorkflow.ReviewWorkflow.recoverAgentAssignment binding operationId
                                        cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                        cmd.AssignmentGeneration cmd.Reason |> mapError
                                | "AcknowledgeFeedback" ->
                                    ReviewWorkflow.ReviewWorkflow.acknowledgeBranchReview binding operationId
                                        cmd.TaskId cmd.ActorId cmd.DecisionSequence
                                    |> mapError
                                | _ -> Error(Client.ValidationError "Unsupported  task operation.")
                            )
                        result |> Result.map OperationResponse.present))
