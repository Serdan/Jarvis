module Client.V2ReviewApi

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
type ReviewV2OptIn =
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

module V2LedgerErrors =
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
        Client.V2LedgerFailureError
            { Code = code
              Message = string failure
              OperationId = None
              VerifiedLedgerHead = None }

module ReviewV2Binding =
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
                Error(Client.ValidationError "V2 binding directory must not be a symlink.")
            else
                let lockPath = path + ".lock"
                if File.Exists(lockPath) && File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint) then
                    Error(Client.ValidationError "V2 binding lock must not be a symlink.")
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
            Error(Client.ValidationError $"Cannot lock V2 trust binding: {ex.Message}")

    let private readStored (path: string) =
        try
            if not (File.Exists path) then
                Error(Client.ValidationError "V2 ledger has no approved local binding.")
            elif File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                 || FileInfo(path).Length > 4096L then
                Error(Client.ValidationError "V2 binding must be a regular, bounded file.")
            else
                let parsed = JsonSerializer.Deserialize<ReviewV2OptIn>(
                    File.ReadAllText(path),
                    JsonSerializerOptions(PropertyNameCaseInsensitive = true))
                if isNull (box parsed) then
                    Error(Client.ValidationError "Missing V2 binding record.")
                else Ok parsed
        with ex -> Error(Client.ValidationError $"Cannot read V2 binding: {ex.Message}")

    let private writeStored (path: string) (stored: ReviewV2OptIn) overwrite =
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            try
                File.WriteAllText(temp, JsonSerializer.Serialize(stored))
                if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                File.Move(temp, path, overwrite)
                Ok ()
            with ex ->
                Error(Client.ValidationError $"Cannot persist V2 binding: {ex.Message}")
        finally
            if File.Exists(temp) then File.Delete(temp)

    let private validStored root (stored: ReviewV2OptIn) : Result<LedgerStore.LedgerBinding, EffectError> =
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
            Error(Client.ValidationError "V2 binding is not explicitly enabled with a pinned root and identity.")
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
                Error(Client.ValidationError $"V2 ledger disabled for this checkout. Approved binding required at {path}. No V1 fallback.")
            else
                withConfigLock path (fun () ->
                    readStored path
                    |> Result.bind (fun stored ->
                        validStored root stored
                        |> Result.bind (fun binding ->
                            LedgerStore.LedgerStore.fetch binding
                            |> Result.mapError V2LedgerErrors.toEffect
                            |> Result.bind (fun snapshot ->
                                LedgerStore.LedgerStore.verifyTrustedHead binding
                                    stored.HighestTrustedHead snapshot.Commit
                                |> Result.mapError V2LedgerErrors.toEffect
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
                    Error(Client.ValidationError "V2 ledger is already bound. Use explicit rebind.")
                else
                    let binding : LedgerStore.LedgerBinding =
                        { RepositoryPath = root; RemoteName = remoteName
                          ApprovedFetchUrl = approvedFetchUrl; ApprovedPushUrl = approvedPushUrl
                          ProjectId = projectId; LedgerId = ledgerId; Epoch = epoch }
                    match LedgerStore.LedgerStore.fetch binding with
                    | Error failure ->
                        Error(V2LedgerErrors.toEffect failure)
                    | Ok snapshot ->
                        match LedgerStore.LedgerStore.verifyTrustedHead binding expectedTrustedHead snapshot.Commit with
                        | Error failure ->
                            Error(V2LedgerErrors.toEffect failure)
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
                                    Error(Client.ValidationError "V2 binding was concurrently created; refusing overwrite.")
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
                    Error(Client.ValidationError "V2 ledger is already bound; refusing reinitialization.")
                else
                    let binding : LedgerStore.LedgerBinding =
                        { RepositoryPath = root; RemoteName = remoteName
                          ApprovedFetchUrl = approvedFetchUrl; ApprovedPushUrl = approvedPushUrl
                          ProjectId = projectId; LedgerId = ledgerId; Epoch = epoch }
                    match LedgerStore.LedgerStore.initialize binding true with
                    | Error failure ->
                        Error(V2LedgerErrors.toEffect failure)
                    | Ok snapshot ->
                        bindExisting projectName rt projectId ledgerId epoch remoteName
                            approvedFetchUrl approvedPushUrl snapshot.Commit true

    let health projectName rt =
        load projectName rt
        |> Result.bind (fun binding ->
            LedgerStore.LedgerStore.fetch binding
            |> Result.mapError V2LedgerErrors.toEffect
            |> Result.bind (fun snapshot ->
                LedgerStore.LedgerStore.inspectRemotePolicy binding
                |> Result.mapError V2LedgerErrors.toEffect
                |> Result.map (fun protection ->
                    {| projectId = binding.ProjectId.ToString("D")
                       ledgerId = binding.LedgerId.ToString("D")
                       epoch = binding.Epoch
                       remoteName = binding.RemoteName
                       approvedFetchUrl = binding.ApprovedFetchUrl
                       approvedPushUrl = binding.ApprovedPushUrl
                       trustedLedgerHead = snapshot.Commit
                       ledgerSequence = snapshot.Header.NextSequence
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
                                        "V2 binding changed while rebinding; reload and retry.")
                                else
                                    let newBinding =
                                        { old with RemoteName = remoteName
                                                   ApprovedFetchUrl = approvedFetchUrl
                                                   ApprovedPushUrl = approvedPushUrl }
                                    LedgerStore.LedgerStore.fetch newBinding
                                    |> Result.mapError V2LedgerErrors.toEffect
                                    |> Result.bind (fun snapshot ->
                                        // A caller-supplied anchor cannot replace what this
                                        // checkout already trusted. Both must be ancestors.
                                        LedgerStore.LedgerStore.verifyTrustedHead newBinding
                                            stored.HighestTrustedHead snapshot.Commit
                                        |> Result.mapError V2LedgerErrors.toEffect
                                        |> Result.bind (fun () ->
                                            LedgerStore.LedgerStore.verifyTrustedHead newBinding
                                                expectedTrustedHead snapshot.Commit
                                            |> Result.mapError V2LedgerErrors.toEffect
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
                                                        $"V2 rebind backup failed: {ex.Message}")))))))


[<CLIMutable>]
type V2PendingIntent =
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
module V2PendingIntentJournal =
    let private sha (content: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(content))
        |> Convert.ToHexString

    let private journalDirectory root =
        let bindingPath = ReviewV2Binding.pathForRoot root
        Path.Combine(Path.GetDirectoryName bindingPath, "pending",
                     Path.GetFileNameWithoutExtension bindingPath)

    let private filePath (binding: LedgerStore.LedgerBinding) (operationId: Guid) =
        Path.Combine(journalDirectory binding.RepositoryPath, operationId.ToString("N") + ".json")

    let private readFile (file: string) =
        try
            if File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)
               || FileInfo(file).Length > 1024L * 1024L then
                Error(Client.ValidationError "Unsafe or oversized V2 pending intent.")
            else
                let record = JsonSerializer.Deserialize<V2PendingIntent>(File.ReadAllText(file))
                if isNull (box record) || record.SchemaVersion <> 1
                   || String.IsNullOrWhiteSpace record.OperationId
                   || String.IsNullOrWhiteSpace record.CommandType
                   || String.IsNullOrWhiteSpace record.CommandJson
                   || not (String.Equals(record.CommandSha256, sha record.CommandJson, StringComparison.Ordinal)) then
                    Error(Client.ValidationError "Invalid local pending V2 operation journal.")
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
        let matches (existing: V2PendingIntent) =
            existing.OperationId = newRecord.OperationId
            && existing.ProjectId = newRecord.ProjectId
            && existing.LedgerId = newRecord.LedgerId
            && existing.Epoch = newRecord.Epoch
            && existing.CommandType = newRecord.CommandType
            && existing.CommandSha256 = newRecord.CommandSha256
            && existing.CommandJson = newRecord.CommandJson
        try
            if File.Exists file then
                readFile file |> Result.bind (fun existing ->
                    if matches existing then Ok ()
                    else Error(Client.ValidationError "Pending V2 operation UUID reused with changed input."))
            else
                let directory = Path.GetDirectoryName file
                Directory.CreateDirectory(directory) |> ignore
                if DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                    Error(Client.ValidationError "Pending V2 journal directory must not be a symbolic link.")
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
                            else Error(Client.ValidationError "Pending V2 operation UUID reused with changed input."))
                    finally
                        if File.Exists temp then File.Delete temp
        with ex ->
            Error(Client.ValidationError $"Cannot persist local pending V2 operation: {ex.Message}")

    let listAtRoot root =
        try
            let directory = journalDirectory root
            if not (Directory.Exists directory) then Ok []
            elif DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint) then
                Error(Client.ValidationError "Pending V2 journal directory must not be a symbolic link.")
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
            Error(Client.ValidationError $"Cannot enumerate pending V2 operations: {ex.Message}")

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

    let private operationKind (record: V2PendingIntent) =
        use doc = JsonDocument.Parse(record.CommandJson)
        let root = doc.RootElement
        let action = commandProperty root "Action"
        match record.CommandType, action with
        | "V2TaskWrite", Some "AcknowledgeFeedback" -> Some "AcknowledgeReviewFeedback"
        | "V2TaskWrite", Some action -> Some action
        | "V2ReviewWrite", Some "PutDocument" -> Some "PutReviewDocument"
        | "V2ReviewWrite", Some "Submit" -> Some "SubmitForReview"
        | "V2ReviewWrite", Some "Update" -> Some "UpdateReviewRequest"
        | "V2ReviewWrite", Some "Claim" -> Some "ClaimReview"
        | "V2ReviewWrite", Some "Decide" -> Some "ReviewDecision"
        | "V2ReviewWrite", Some "Withdraw" -> Some "WithdrawReview"
        | "V2ReviewWrite", Some "Reassign" -> Some "ReassignReviewer"
        | "V2ReviewWrite", Some "ReleaseClaim" -> Some "ReleaseReviewClaim"
        | "V2ReviewWrite", Some "ReserveOrphan" -> Some "ReserveOrphanReviewRef"
        | "V2ReviewWrite", Some "Finalize" -> Some "FinalizeImplementation"
        | "V2ReviewWrite", Some "AcknowledgeFeedback" -> Some "AcknowledgeReviewFeedback"
        | "V2IntegrationWrite", Some "PlanIntegration" -> Some "PlanIntegration"
        | "V2IntegrationWrite", Some "PendingPush" -> Some "MarkIntegrationPendingPush"
        | "V2IntegrationWrite", Some "RecordIntegration" -> Some "RecordIntegration"
        | "V2IntegrationWrite", Some "CloseIntegratedTask" -> Some "CloseIntegratedTask"
        | _ -> None

    let private identityMatches (record: V2PendingIntent) (operation: LedgerStore.LedgerOperation) =
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
                let integration = common @ [ "ActionId", "actionId" ]
                match record.CommandType, operation.Type with
                | "V2TaskWrite", "CreateTask" ->
                    common @ [ "Title", "title"; "Html", "specSha256" ]
                | "V2TaskWrite", "ReviseTaskSpec" ->
                    common @ [ "Title", "title"; "Html", "specSha256"
                               "ExpectedVersion", "expectedTaskVersion" ]
                | "V2TaskWrite", "ReviseTaskSpecPinned" ->
                    common @ [ "Title", "title"; "Html", "specSha256"
                               "ExpectedVersion", "expectedTaskVersion"; "BaseOid", "expectedSpecBlobOid" ]
                | "V2TaskWrite", "ImportTaskSpec" ->
                    common @ [ "ExpectedVersion", "expectedTaskVersion"; "BaseOid", "expectedSpecBlobOid"
                               "TargetRef", "expectedSourceBlobOidInput"; "SourceRef", "sourceRelativePath"
                               "Title", "title" ]
                | "V2TaskWrite", "AdoptTaskSpec" ->
                    attempt @ [ "ExpectedTaskVersion", "expectedTaskVersion"
                                "BaseOid", "expectedCurrentSpecOid"; "Summary", "instructions" ]
                | "V2TaskWrite", "RegisterAgent" -> [ "ActorId", "agentId" ]
                | "V2TaskWrite", "AssignTask" ->
                    common @ [ "AttemptId", "attemptId"; "OwnerId", "ownerId"; "ReviewerId", "reviewerId" ]
                | "V2TaskWrite", "AcknowledgeAssignment" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"; "AssignmentGeneration", "generation" ]
                | "V2TaskWrite", "ReissueAssignment" ->
                    owner @ [ "ExpectedVersion", "expectedAttemptVersion"
                              "ExpectedAgentVersion", "expectedAgentVersion"
                              "AssignmentGeneration", "generation"
                              "Summary", "instructions"; "Html", "specSha256" ]
                | "V2TaskWrite", "BeginAttempt" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation" ]
                | "V2TaskWrite", "BindReviewBase" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation"
                              "BaseOid", "baseOid"; "TargetRef", "targetRef" ]
                | "V2TaskWrite", "BindAttemptSource" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation"
                              "BaseOid", "baseOid"; "TargetRef", "targetRef"
                              "SourceRef", "sourceRef"; "AllowDetached", "allowDetached"
                              "AllowUncommitted", "allowUncommitted" ]
                | "V2TaskWrite", "RebindAttemptSource" ->
                    owner @ [ "ExpectedVersion", "expectedVersion"
                              "AssignmentGeneration", "generation"
                              "SourceRef", "sourceRef" ]
                | "V2TaskWrite", "CheckpointAttempt" ->
                    attempt @ [ "Summary", "summary"; "NextAction", "nextAction"
                                "BaseOid", "sourceCommit"; "Evidence", "evidence" ]
                | "V2TaskWrite", "YieldAgent" ->
                    attempt @ [ "Summary", "summary"; "NextAction", "nextAction" ]
                | "V2TaskWrite", "ResumeAgent" -> attempt
                | "V2TaskWrite", "CompleteAgentWork" ->
                    attempt @ [ "Summary", "summary" ]
                | "V2TaskWrite", "ParkAgent" ->
                    [ "ActorId", "agentId"; "ExpectedAgentVersion", "expectedAgentVersion"
                      "Reason", "reason" ]
                | "V2TaskWrite", "ReissueQueuedAttempt"
                | "V2TaskWrite", "RecoverAgentAssignment" ->
                    attempt @ [ "Reason", "reason" ]
                | "V2TaskWrite", ("ParkAttempt" | "BlockAttempt" | "ResumeAttempt"
                                   | "AbandonAttempt" | "CancelTask") ->
                    attempt @ [ "ExpectedTaskVersion", "expectedTaskVersion";
                                "Reason", "reason" ]
                | "V2TaskWrite", "CancelOpenTask" ->
                    common @ [ "ExpectedTaskVersion", "expectedTaskVersion"; "Reason", "reason" ]
                | "V2TaskWrite", "TransferOwner" ->
                    attempt @ [ "ExpectedTaskVersion", "expectedTaskVersion";
                                "AttemptId", "newAttemptId"; "OwnerId", "newOwner";
                                "ReviewerId", "reviewer"; "Reason", "reason" ]
                | "V2TaskWrite", "CloseNonSourceTask" ->
                    common @ [ "ActorId", "approver"; "ExpectedTaskVersion", "expectedTaskVersion";
                                "ExpectedVersion", "expectedAttemptVersion";
                                "Summary", "criteria"; "Evidence", "evidence" ]
                | "V2TaskWrite", "ReopenTask" ->
                    common @ [ "ExpectedTaskVersion", "expectedTaskVersion"; "Reason", "reason" ]
                | "V2TaskWrite", "AcknowledgeReviewFeedback" ->
                    owner @ [ "DecisionSequence", "decisionSequence" ]
                | "V2ReviewWrite", "PutReviewDocument" ->
                    review @ [ "ActorId", "actorId"; "DocumentPath", "documentPath"
                               "Content", "content"; "ExpectedBlobOid", "expectedBlobOid" ]
                | "V2ReviewWrite", "SubmitForReview" ->
                    owner @ [ "RequestId", "requestId"; "ExpectedVersion", "expectedVersion"
                              "Generation", "generation"; "SourceRef", "sourceRef"
                              "HeadOid", "headOid"; "Summary", "summary"; "TestEvidence", "testEvidence" ]
                | "V2ReviewWrite", "UpdateReviewRequest" ->
                    owner @ [ "RequestId", "requestId"; "ExpectedVersion", "expectedRequestVersion"
                              "Generation", "generation"; "SourceRef", "sourceRef"
                              "HeadOid", "headOid"; "Summary", "summary"; "TestEvidence", "testEvidence" ]
                | "V2ReviewWrite", "ClaimReview" ->
                    reviewer @ [ "Generation", "expectedRevision" ]
                | "V2ReviewWrite", "ReviewDecision" ->
                    reviewer @ [ "Verdict", "status"; "Feedback", "feedback"
                                 "Inspected", "inspected"; "Tested", "tested"
                                 "TestDetails", "testDetails" ]
                | "V2ReviewWrite", "WithdrawReview" ->
                    review @ [ "ActorId", "actor"; "ExpectedVersion", "expectedVersion"
                               "Feedback", "reason" ]
                | "V2ReviewWrite", "ReassignReviewer" ->
                    review @ [ "ActorId", "actor"; "ExpectedVersion", "expectedVersion"
                               "ReviewerId", "nextReviewer"; "Feedback", "reason" ]
                | "V2ReviewWrite", "ReleaseReviewClaim" ->
                    review @ [ "ActorId", "actor"; "ExpectedVersion", "expectedVersion"
                               "Feedback", "reason" ]
                | "V2ReviewWrite", "ReserveOrphanReviewRef" ->
                    review @ [ "ActorId", "actor"; "Generation", "revision"
                               "HeadOid", "expectedHead"; "Feedback", "reason" ]
                | "V2ReviewWrite", "FinalizeImplementation" ->
                    review @ [ "ActorId", "ownerId"; "ExpectedVersion", "expectedAttemptVersion" ]
                | "V2ReviewWrite", "AcknowledgeReviewFeedback" ->
                    owner @ [ "ExpectedVersion", "decisionSequence" ]
                | "V2IntegrationWrite", "PlanIntegration" ->
                    integration @ [ "RequestId", "requestId"; "ExpectedVersion", "expectedAttemptVersion"
                                    "TargetRef", "targetRef"; "ExpectedTargetHead", "expectedTargetHead" ]
                | "V2IntegrationWrite", "MarkIntegrationPendingPush"
                | "V2IntegrationWrite", "RecordIntegration" ->
                    integration @ [ "ExpectedVersion", "expectedVersion" ]
                | "V2IntegrationWrite", "CloseIntegratedTask" ->
                    integration @ [ "ExpectedVersion", "expectedTaskVersion" ]
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
                        elif source = "Html" && dest = "specSha256" then
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

    let private checkBinding (binding: LedgerStore.LedgerBinding) (record: V2PendingIntent) =
        record.ProjectId = binding.ProjectId.ToString("D")
        && record.LedgerId = binding.LedgerId.ToString("D")
        && record.Epoch = binding.Epoch

    let reconcile (binding: LedgerStore.LedgerBinding) =
        listAtRoot binding.RepositoryPath
        |> Result.bind (fun records ->
            LedgerStore.LedgerStore.fetch binding
            |> Result.mapError V2LedgerErrors.toEffect
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
                            |> Result.mapError V2LedgerErrors.toEffect
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
                    |> Result.mapError V2LedgerErrors.toEffect
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
                    |> Result.mapError V2LedgerErrors.toEffect
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
            | Error(Client.V2LedgerFailureError failure) ->
                // The caller-owned UUID remains recoverable even after a harness reset.
                Error(Client.V2LedgerFailureError
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
            | Error(Client.V2LedgerFailureError issue) when issue.Code = "OutcomeUnknown" ->
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
                        Error(Client.V2LedgerFailureError
                            { issue with VerifiedLedgerHead = Some snapshot.Commit })
                | Ok(snapshot, None) ->
                    Error(Client.V2LedgerFailureError
                        { issue with VerifiedLedgerHead = Some snapshot.Commit })
                | Error _ ->
                    Error(Client.V2LedgerFailureError issue)
            | other -> other)

module V2OperationResponse =
    let present (write: LedgerStore.LedgerWrite) =
        use document = JsonDocument.Parse(write.Operation.OriginalResultJson)
        box {| operation = write.Operation
               head = write.CurrentHead
               alreadyApplied = write.AlreadyApplied
               originalResult = document.RootElement.Clone() |}

module V2LedgerApi =
    let private id label (raw: string) =
        match Guid.TryParse raw with
        | true, parsed when parsed <> Guid.Empty -> Ok parsed
        | _ -> Error(Client.ValidationError $"Valid {label} UUID required.")

    let read (cmd: V2LedgerReadCommand) rt =
        match cmd.Action with
        | "Health" -> ReviewV2Binding.health cmd.ProjectName rt |> Result.map box
        | "PendingOperations" ->
            V2PendingIntentJournal.listForProject cmd.ProjectName rt
            |> Result.map (fun records ->
                records
                |> List.map (fun record ->
                    {| operationId = record.OperationId
                       projectId = record.ProjectId
                       ledgerId = record.LedgerId
                       epoch = record.Epoch
                       commandType = record.CommandType
                       commandSha256 = record.CommandSha256
                       originalCommandJson = record.CommandJson
                       createdUtc = record.CreatedUtc |})
                |> box)
        | "ReconcilePendingOperations" ->
            ReviewV2Binding.load cmd.ProjectName rt
            |> Result.bind V2PendingIntentJournal.reconcile
            |> Result.map box
        | "GetOperationStatus" ->
            id "operation" cmd.OperationId
            |> Result.bind (fun operationId ->
                ReviewV2Binding.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    LedgerStore.LedgerStore.getOperationStatus binding operationId
                    |> Result.mapError V2LedgerErrors.toEffect
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
                            |> Result.mapError V2LedgerErrors.toEffect
                            |> Result.map (fun origin ->
                                use result = JsonDocument.Parse(applied.OriginalResultJson)
                                box {| snapshotCommit = snapshot.Commit
                                       operationId = operationId.ToString("D")
                                       disposition = "Applied"
                                       originatingCommit = Some origin
                                       originalResult = Some(result.RootElement.Clone())
                                       operation = operation |}))))
        | _ -> Error(Client.ValidationError "Unsupported V2 ledger read action.")

    let write (cmd: V2LedgerWriteCommand) rt =
        id "operation" cmd.OperationId
        |> Result.bind (fun operationId ->
            if cmd.Action = "AcknowledgePendingOperation" then
                ReviewV2Binding.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    V2PendingIntentJournal.acknowledge binding operationId
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
                            ReviewV2Binding.bindExisting cmd.ProjectName rt projectId ledgerId
                                cmd.Epoch cmd.RemoteName cmd.ApprovedFetchUrl cmd.ApprovedPushUrl
                                cmd.ExpectedTrustedHead cmd.AcknowledgeVisibility)
                    | "InitializeLedger" ->
                        bindInputs ()
                        |> Result.bind (fun (projectId, ledgerId) ->
                            ReviewV2Binding.initializeNew cmd.ProjectName rt projectId ledgerId
                                cmd.Epoch cmd.RemoteName cmd.ApprovedFetchUrl cmd.ApprovedPushUrl
                                cmd.AcknowledgeVisibility)
                    | "RebindLedgerRemote" ->
                        ReviewV2Binding.rebindRemote cmd.ProjectName rt
                            cmd.RemoteName cmd.ApprovedFetchUrl cmd.ApprovedPushUrl
                            cmd.ExpectedTrustedHead cmd.AcknowledgeVisibility
                    | _ -> Error(Client.ValidationError "Unsupported V2 ledger setup action.")
                result |> Result.map (fun binding ->
                    box {| projectId = binding.ProjectId; ledgerId = binding.LedgerId
                           epoch = binding.Epoch; remoteName = binding.RemoteName
                           trustedLedgerHead = binding.HighestTrustedHead |}))

module V2ReviewApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError V2LedgerErrors.toEffect
    let private parseId label (raw: string) =
        match Guid.TryParse raw with
        | true, value when value <> Guid.Empty -> Ok value
        | _ -> Error(Client.ValidationError $"Invalid {label}: caller-stable UUID required.")

    let read (cmd: V2ReviewReadCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            match cmd.Action with
            | "GetTask" ->
                ReviewWorkflow.ReviewWorkflow.getTaskWithSpec binding cmd.TaskId
                |> mapError
                |> Result.map (fun (snapshot, task, attempt, html, specOid) ->
                    box {| snapshotCommit = snapshot.Commit; task = task; attempt = attempt
                           specHtml = html; specBlobOid = specOid |})
            | "GetReview" ->
                parseId "requestId" cmd.RequestId
                |> Result.bind (fun id ->
                    ReviewWorkflow.ReviewWorkflow.getReview binding cmd.TaskId id
                    |> mapError
                    |> Result.map (fun (snapshot, request, revision) ->
                        box {| snapshotCommit = snapshot.Commit; request = request; revision = revision |}))
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
            | "DecisionEvidence" ->
                parseId "requestId" cmd.RequestId
                |> Result.bind (fun requestId ->
                    ReviewWorkflow.ReviewWorkflow.getDecisionEvidence binding cmd.TaskId requestId
                    |> mapError
                    |> Result.map box)
            | "OrphanRefs" ->
                ReviewWorkflow.ReviewWorkflow.inspectOrphanReviewRefs binding cmd.TaskId
                |> mapError |> Result.map box
            | "ClaimHistory" ->
                parseId "requestId" cmd.RequestId
                |> Result.bind (fun requestId ->
                    ReviewWorkflow.ReviewWorkflow.getReviewClaimEvents binding cmd.TaskId requestId
                    |> mapError |> Result.map box)
            | "AwaitingReassignment" ->
                ReviewWorkflow.ReviewWorkflow.awaitingReviewReassignment binding
                |> mapError |> Result.map box
            | "ReviewerQueue" ->
                ReviewWorkflow.ReviewWorkflow.reviewerQueue binding cmd.ReviewerId
                |> mapError
                |> Result.map (fun (snapshot, queue) ->
                    box {| snapshotCommit = snapshot.Commit; reviews = queue |})
            | _ -> Error(Client.ValidationError "Unsupported V2 read operation."))

    // Source cleanup executes only after an immutable reservation in the Git
    // ledger. It is separately idempotent and never claims a ledger write
    // succeeded merely because the source ref is absent.
    let deleteReservedOrphan (cmd: V2ReviewWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            parseId "requestId" cmd.RequestId
            |> Result.bind (fun id ->
                ReviewWorkflow.ReviewWorkflow.deleteReservedOrphanReviewRef binding
                    cmd.TaskId id cmd.Generation cmd.HeadOid
                |> mapError |> Result.map box))

    let write (cmd: V2ReviewWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            parseId "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                let id () = parseId "requestId" cmd.RequestId
                let result =
                    V2PendingIntentJournal.executeLedgerWrite binding operationId "V2ReviewWrite" cmd (fun () ->
                        match cmd.Action with
                        | "PutDocument" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.putReviewDocument binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.DocumentPath
                                    cmd.ExpectedBlobOid cmd.Content
                                |> mapError)
                        | "Submit" ->
                            id ()
                            |> Result.mapError (fun e -> e)
                            |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.submitReviewWithEvidence binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.Generation reviewId
                                    cmd.SourceRef cmd.HeadOid cmd.Summary cmd.TestEvidence
                                |> mapError)
                        | "Update" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.updateReviewWithEvidence binding operationId
                                    cmd.TaskId cmd.ActorId cmd.Generation reviewId cmd.ExpectedVersion
                                    cmd.SourceRef cmd.HeadOid cmd.Summary cmd.TestEvidence
                                |> mapError)
                        | "Claim" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.claimReview binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion cmd.Generation
                                |> mapError)
                        | "Decide" ->
                            id () |> Result.bind (fun reviewId ->
                                match cmd.Verdict with
                                | "Accept" | "RequestChanges" as verdict ->
                                    let decision =
                                        if verdict = "Accept" then ReviewWorkflow.Accept
                                        else ReviewWorkflow.RequestChanges
                                    ReviewWorkflow.ReviewWorkflow.decideWithEvidence binding operationId
                                        cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion decision
                                        cmd.Feedback cmd.Inspected cmd.Tested cmd.TestDetails
                                    |> mapError
                                | _ -> Error(Client.ValidationError "Invalid V2 review verdict."))
                        | "Withdraw" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.withdrawReview binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion cmd.Feedback
                                |> mapError)
                        | "ReserveOrphan" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.reserveOrphanReviewRef binding operationId
                                    cmd.TaskId reviewId cmd.Generation cmd.HeadOid cmd.ActorId cmd.Feedback
                                |> mapError)
                        | "ReleaseClaim" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.releaseReviewClaim binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion cmd.Feedback
                                |> mapError)
                        | "Reassign" ->
                            id () |> Result.bind (fun reviewId ->
                                (if String.IsNullOrWhiteSpace cmd.Feedback then
                                     ReviewWorkflow.ReviewWorkflow.reassignReviewer binding operationId
                                         cmd.TaskId reviewId cmd.ActorId cmd.ReviewerId cmd.ExpectedVersion
                                 else
                                     ReviewWorkflow.ReviewWorkflow.reassignReviewerWithReason binding operationId
                                         cmd.TaskId reviewId cmd.ActorId cmd.ReviewerId cmd.ExpectedVersion cmd.Feedback)
                                |> mapError)
                        | "Finalize" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.finalizeImplementation binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion reviewId
                                |> mapError)
                        | "AcknowledgeFeedback" ->
                            ReviewWorkflow.ReviewWorkflow.acknowledgeReviewFeedback binding operationId
                                cmd.TaskId cmd.ActorId (int64 cmd.ExpectedVersion)
                            |> mapError
                        | _ -> Error(Client.ValidationError "Unsupported V2 write operation.")
                    )
                result |> Result.map V2OperationResponse.present))


// Separate typed setup/assignment contract. The review request API never has
// to reinterpret a request ID as a task, attempt, or agent identifier.
module V2TaskApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError V2LedgerErrors.toEffect

    let private guid label (text: string) =
        match Guid.TryParse text with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(Client.ValidationError $"Invalid {label}: a caller-stable UUID is required.")

    let read (cmd: V2TaskReadCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            match cmd.Action with
            | "GetSpecPage" ->
                ReviewWorkflow.ReviewWorkflow.getTaskSpecPage binding cmd.TaskId cmd.Cursor cmd.PageSize
                |> mapError |> Result.map box
            | "DiffTaskSpec" ->
                ReviewWorkflow.ReviewWorkflow.diffTaskSpec binding cmd.TaskId cmd.BeforeOid cmd.AfterOid
                |> mapError |> Result.map box
            | "AgentInbox" ->
                ReviewWorkflow.ReviewWorkflow.getAgentInbox binding cmd.AgentId
                |> mapError |> Result.map box
            | "GetAssignment" ->
                ReviewWorkflow.ReviewWorkflow.getAssignment binding cmd.TaskId cmd.AssignmentGeneration
                |> mapError |> Result.map box
            | "GetCheckpoint" ->
                ReviewWorkflow.ReviewWorkflow.getCheckpoint binding cmd.TaskId cmd.CheckpointSequence
                |> mapError |> Result.map box
            | "NonSourceApproval" ->
                ReviewWorkflow.ReviewWorkflow.getNonSourceClosureApproval binding cmd.TaskId cmd.CheckpointSequence
                |> mapError |> Result.map box
            | "LifecycleHistory" ->
                ReviewWorkflow.ReviewWorkflow.getTaskLifecycleHistory binding cmd.TaskId
                |> mapError |> Result.map box
            | "InspectAttemptSource" ->
                ReviewWorkflow.ReviewWorkflow.inspectAttemptSource binding cmd.TaskId
                |> mapError |> Result.map box
            | _ -> Error(Client.ValidationError "Unsupported V2 task read operation."))

    let write (cmd: V2TaskWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            guid "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                if cmd.Action = "ExportTaskSpec" then
                    // Filesystem mutation must use the write permission route,
                    // not the V2TaskRead protocol or its read-only capability.
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
                            |> Result.mapError V2LedgerErrors.toEffect
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
                            V2PendingIntentJournal.executeLedgerWrite binding operationId "V2TaskWrite" journalCommand (fun () ->
                                match cmd.Action with
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
                                | "RegisterAgent" ->
                                    ReviewWorkflow.ReviewWorkflow.registerAgent binding operationId cmd.ActorId
                                    |> mapError
                                | "AssignTask" ->
                                    guid "attemptId" cmd.AttemptId
                                    |> Result.bind (fun attemptId ->
                                        ReviewWorkflow.ReviewWorkflow.assignTask binding operationId cmd.TaskId
                                            attemptId cmd.OwnerId cmd.ReviewerId
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
                                | "CancelOpenTask" ->
                                    ReviewWorkflow.ReviewWorkflow.cancelOpenTask binding operationId
                                        cmd.TaskId cmd.ExpectedTaskVersion cmd.Reason |> mapError
                                | "TransferOwner" ->
                                    guid "newAttemptId" cmd.AttemptId
                                    |> Result.bind (fun newId ->
                                        ReviewWorkflow.ReviewWorkflow.transferAttemptOwner binding operationId
                                            cmd.TaskId newId cmd.OwnerId cmd.ReviewerId
                                            cmd.ExpectedTaskVersion cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                            cmd.AssignmentGeneration cmd.Reason |> mapError)
                                | "CloseNonSourceTask" ->
                                    ReviewWorkflow.ReviewWorkflow.closeNonSourceTask binding operationId cmd.TaskId
                                        cmd.ActorId cmd.ExpectedTaskVersion cmd.ExpectedVersion
                                        cmd.Summary cmd.Evidence |> mapError
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
                                    ReviewWorkflow.ReviewWorkflow.acknowledgeReviewFeedback binding operationId
                                        cmd.TaskId cmd.ActorId cmd.DecisionSequence
                                    |> mapError
                                | _ -> Error(Client.ValidationError "Unsupported V2 task operation.")
                            )
                        result |> Result.map V2OperationResponse.present))


module V2IntegrationApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError V2LedgerErrors.toEffect

    let private guid label (raw: string) =
        match Guid.TryParse raw with
        | true, parsed when parsed <> Guid.Empty -> Ok parsed
        | _ -> Error(Client.ValidationError $"Invalid {label}: caller-stable UUID required.")

    let read (cmd: V2IntegrationReadCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            guid "integration action ID" cmd.ActionId
            |> Result.bind (fun actionId ->
                ReviewWorkflow.ReviewWorkflow.inspectIntegration binding cmd.TaskId actionId
                |> mapError
                |> Result.map box))

    let write (cmd: V2IntegrationWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            guid "operation ID" cmd.OperationId
            |> Result.bind (fun operationId ->
                guid "integration action ID" cmd.ActionId
                |> Result.bind (fun actionId ->
                    let result =
                        V2PendingIntentJournal.executeLedgerWrite binding operationId "V2IntegrationWrite" cmd (fun () ->
                            match cmd.Action with
                            | "PlanIntegration" ->
                                guid "accepted review ID" cmd.RequestId
                                |> Result.bind (fun reviewId ->
                                    ReviewWorkflow.ReviewWorkflow.planIntegration binding operationId
                                        cmd.TaskId actionId reviewId cmd.ExpectedVersion
                                        cmd.TargetRef cmd.ExpectedTargetHead
                                    |> mapError)
                            | "PendingPush" ->
                                ReviewWorkflow.ReviewWorkflow.markIntegrationPendingPush binding
                                    operationId cmd.TaskId actionId cmd.ExpectedVersion
                                |> mapError
                            | "RecordIntegration" ->
                                ReviewWorkflow.ReviewWorkflow.recordIntegration binding operationId
                                    cmd.TaskId actionId cmd.ExpectedVersion
                                |> mapError
                            | "CloseIntegratedTask" ->
                                ReviewWorkflow.ReviewWorkflow.closeIntegratedTask binding
                                    operationId cmd.TaskId actionId cmd.ExpectedVersion
                                |> mapError
                            | _ -> Error(Client.ValidationError "Unsupported V2 integration operation.")
                        )
                    result |> Result.map V2OperationResponse.present)))
