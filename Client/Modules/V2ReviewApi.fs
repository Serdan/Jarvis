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

    // Local binding is intentionally opt-in, separately from the normal Jarvis config.
    // The read-only V2 tools also refuse to initialize or adopt a remote implicitly.
    let load projectName rt =
        match ProjectPaths.resolveProjectRoot projectName rt with
        | Error error -> Error error
        | Ok(FolderPath root) ->
            let path = pathForRoot root
            if not (File.Exists path) then
                Error(Client.ValidationError $"V2 ledger disabled for this checkout. Approved binding required at {path}. No V1 fallback.")
            else
                try
                    let attrs = File.GetAttributes path
                    if attrs.HasFlag(FileAttributes.ReparsePoint) || FileInfo(path).Length > 4096L then
                        Error(Client.ValidationError "V2 binding config must be a regular, bounded file.")
                    else
                        let stored = JsonSerializer.Deserialize<ReviewV2OptIn>(
                            File.ReadAllText(path),
                            JsonSerializerOptions(PropertyNameCaseInsensitive = true))
                        let projectOk, projectId = Guid.TryParse(stored.ProjectId)
                        let ledgerOk, ledgerId = Guid.TryParse(stored.LedgerId)
                        if isNull (box stored)
                           || stored.SchemaVersion <> 1 || not stored.Enabled
                           || not (String.Equals(stored.ProjectRoot, Path.GetFullPath(root), StringComparison.Ordinal))
                           || not projectOk || not ledgerOk || projectId = Guid.Empty || ledgerId = Guid.Empty
                           || stored.Epoch < 1
                           || String.IsNullOrWhiteSpace stored.RemoteName
                           || String.IsNullOrWhiteSpace stored.ApprovedFetchUrl
                           || String.IsNullOrWhiteSpace stored.ApprovedPushUrl
                           || String.IsNullOrWhiteSpace stored.HighestTrustedHead then
                            Error(Client.ValidationError "V2 ledger binding is not explicitly enabled with a pinned root and identity.")
                        else
                            let binding : LedgerStore.LedgerBinding =
                                { RepositoryPath = root
                                  RemoteName = stored.RemoteName
                                  ApprovedFetchUrl = stored.ApprovedFetchUrl
                                  ApprovedPushUrl = stored.ApprovedPushUrl
                                  ProjectId = projectId
                                  LedgerId = ledgerId
                                  Epoch = stored.Epoch }
                            match LedgerStore.LedgerStore.fetch binding with
                            | Error error ->
                                Error(V2LedgerErrors.toEffect error)
                            | Ok snapshot ->
                                match LedgerStore.LedgerStore.verifyTrustedHead binding stored.HighestTrustedHead snapshot.Commit with
                                | Error error ->
                                    Error(V2LedgerErrors.toEffect error)
                                | Ok () ->
                                    if stored.HighestTrustedHead <> snapshot.Commit then
                                        // Persist the new verified head independently from Git's local cache ref.
                                        let next = { stored with HighestTrustedHead = snapshot.Commit }
                                        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
                                        try
                                            File.WriteAllText(temp, JsonSerializer.Serialize(next))
                                            File.Move(temp, path, true)
                                            Ok binding
                                        finally
                                            if File.Exists(temp) then File.Delete(temp)
                                    else Ok binding
                with ex ->
                    Error(Client.ValidationError $"Cannot load V2 binding: {ex.Message}")


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
                            try
                                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                                let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
                                try
                                    File.WriteAllText(temp, JsonSerializer.Serialize(record))
                                    File.Move(temp, path, false)
                                    Ok record
                                finally
                                    if File.Exists temp then File.Delete temp
                            with ex ->
                                Error(Client.ValidationError $"Cannot persist verified V2 binding: {ex.Message}")

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
            |> Result.map (fun snapshot ->
                {| projectId = binding.ProjectId.ToString("D")
                   ledgerId = binding.LedgerId.ToString("D")
                   epoch = binding.Epoch
                   remoteName = binding.RemoteName
                   approvedFetchUrl = binding.ApprovedFetchUrl
                   approvedPushUrl = binding.ApprovedPushUrl
                   trustedLedgerHead = snapshot.Commit
                   ledgerSequence = snapshot.Header.NextSequence |}))

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
                let newBinding =
                    { old with RemoteName = remoteName
                               ApprovedFetchUrl = approvedFetchUrl
                               ApprovedPushUrl = approvedPushUrl }
                match LedgerStore.LedgerStore.fetch newBinding with
                | Error failure ->
                    Error(V2LedgerErrors.toEffect failure)
                | Ok snapshot ->
                    match LedgerStore.LedgerStore.verifyTrustedHead newBinding expectedTrustedHead snapshot.Commit with
                    | Error failure ->
                        Error(V2LedgerErrors.toEffect failure)
                    | Ok () ->
                        match ProjectPaths.resolveProjectRoot projectName rt with
                        | Error error -> Error error
                        | Ok(FolderPath root) ->
                            let path = pathForRoot root
                            try
                                let previous = File.ReadAllText path
                                let record = JsonSerializer.Deserialize<ReviewV2OptIn>(previous)
                                if not (String.Equals(record.ProjectId, old.ProjectId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                                   || not (String.Equals(record.LedgerId, old.LedgerId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                                   || record.Epoch <> old.Epoch then
                                    Error(Client.ValidationError "V2 binding changed during rebind.")
                                else
                                    let replacement =
                                        { record with RemoteName = remoteName
                                                      ApprovedFetchUrl = approvedFetchUrl
                                                      ApprovedPushUrl = approvedPushUrl
                                                      HighestTrustedHead = snapshot.Commit }
                                    let backup = path + ".previous-" + Guid.NewGuid().ToString("N")
                                    File.WriteAllText(backup, previous)
                                    let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
                                    try
                                        File.WriteAllText(temp, JsonSerializer.Serialize(replacement))
                                        File.Move(temp, path, true)
                                        Ok replacement
                                    finally
                                        if File.Exists temp then File.Delete temp
                            with ex ->
                                Error(Client.ValidationError $"V2 rebind failed: {ex.Message}")



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
        | "GetOperationStatus" ->
            id "operation" cmd.OperationId
            |> Result.bind (fun operationId ->
                ReviewV2Binding.load cmd.ProjectName rt
                |> Result.bind (fun binding ->
                    LedgerStore.LedgerStore.getOperationStatus binding operationId
                    |> Result.mapError V2LedgerErrors.toEffect
                    |> Result.map (fun (snapshot, operation) ->
                        box {| snapshotCommit = snapshot.Commit
                               operationId = operationId.ToString("D")
                               disposition = if operation.IsSome then "Applied" else "NotFoundAtVerifiedHead"
                               operation = operation |})))
        | _ -> Error(Client.ValidationError "Unsupported V2 ledger read action.")

    let write (cmd: V2LedgerWriteCommand) rt =
        id "operation" cmd.OperationId
        |> Result.bind (fun _ ->
            let bindInputs () =
                id "project" cmd.ProjectId
                |> Result.bind (fun projectId ->
                    id "ledger" cmd.LedgerId
                    |> Result.map (fun ledgerId -> projectId, ledgerId))
            let result =
                match cmd.Action with
                | "BindExistingLedger" ->
                    bindInputs ()
                    |> Result.bind (fun (projectId,ledgerId) ->
                        ReviewV2Binding.bindExisting cmd.ProjectName rt projectId ledgerId
                            cmd.Epoch cmd.RemoteName cmd.ApprovedFetchUrl cmd.ApprovedPushUrl
                            cmd.ExpectedTrustedHead cmd.AcknowledgeVisibility)
                | "InitializeLedger" ->
                    bindInputs ()
                    |> Result.bind (fun (projectId,ledgerId) ->
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
            | "ReviewerQueue" ->
                ReviewWorkflow.ReviewWorkflow.reviewerQueue binding cmd.ReviewerId
                |> mapError
                |> Result.map (fun (snapshot, queue) ->
                    box {| snapshotCommit = snapshot.Commit; reviews = queue |})
            | _ -> Error(Client.ValidationError "Unsupported V2 read operation."))

    let write (cmd: V2ReviewWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            parseId "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                let id () = parseId "requestId" cmd.RequestId
                let result =
                    V2PendingIntentJournal.execute binding operationId "V2ReviewWrite" cmd (fun () ->
                        match cmd.Action with
                        | "Submit" ->
                            id ()
                            |> Result.mapError (fun e -> e)
                            |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.submitReview binding operationId
                                    cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.Generation reviewId
                                    cmd.SourceRef cmd.HeadOid cmd.Summary
                                |> mapError)
                        | "Update" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.updateReview binding operationId
                                    cmd.TaskId cmd.ActorId cmd.Generation reviewId cmd.ExpectedVersion
                                    cmd.SourceRef cmd.HeadOid cmd.Summary
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
                                    ReviewWorkflow.ReviewWorkflow.decide binding operationId
                                        cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion decision cmd.Feedback
                                    |> mapError
                                | _ -> Error(Client.ValidationError "Invalid V2 review verdict."))
                        | "Withdraw" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.withdrawReview binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion cmd.Feedback
                                |> mapError)
                        | "Reassign" ->
                            id () |> Result.bind (fun reviewId ->
                                ReviewWorkflow.ReviewWorkflow.reassignReviewer binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ReviewerId cmd.ExpectedVersion
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
                result |> Result.map (fun write ->
                    let dto = {| operation = write.Operation
                                 head = write.CurrentHead
                                 alreadyApplied = write.AlreadyApplied |}
                    box dto)))


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
            ReviewWorkflow.ReviewWorkflow.getAgentInbox binding cmd.AgentId
            |> mapError
            |> Result.map box)

    let write (cmd: V2TaskWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            guid "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                let result =
                    V2PendingIntentJournal.execute binding operationId "V2TaskWrite" cmd (fun () ->
                        match cmd.Action with
                        | "CreateTask" ->
                            ReviewWorkflow.ReviewWorkflow.createTaskWithSpec binding operationId cmd.TaskId cmd.Title cmd.Html
                            |> mapError
                        | "ReviseTaskSpec" ->
                            ReviewWorkflow.ReviewWorkflow.reviseTaskSpec binding operationId cmd.TaskId
                                cmd.ExpectedVersion cmd.Title cmd.Html |> mapError
                        | "RegisterAgent" ->
                            ReviewWorkflow.ReviewWorkflow.registerAgent binding operationId cmd.ActorId
                            |> mapError
                        | "AssignTask" ->
                            guid "attemptId" cmd.AttemptId
                            |> Result.bind (fun attemptId ->
                                ReviewWorkflow.ReviewWorkflow.assignTask binding operationId cmd.TaskId
                                    attemptId cmd.OwnerId cmd.ReviewerId
                                |> mapError)
                        | "BeginAttempt" ->
                            ReviewWorkflow.ReviewWorkflow.beginAttempt binding operationId cmd.TaskId
                                cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                            |> mapError
                        | "BindReviewBase" ->
                            ReviewWorkflow.ReviewWorkflow.bindReviewBase binding operationId cmd.TaskId
                                cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                                cmd.BaseOid cmd.TargetRef
                            |> mapError
                        | "YieldAgent" ->
                            ReviewWorkflow.ReviewWorkflow.yieldAgent binding operationId
                                cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                cmd.AssignmentGeneration cmd.Summary cmd.NextAction |> mapError
                        | "ResumeAgent" ->
                            ReviewWorkflow.ReviewWorkflow.resumeAgent binding operationId
                                cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                cmd.AssignmentGeneration |> mapError
                        | "CompleteAgentWork" ->
                            ReviewWorkflow.ReviewWorkflow.completeAgentWork binding operationId
                                cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                                cmd.AssignmentGeneration cmd.Summary |> mapError
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
                result |> Result.map (fun write ->
                    box {| operation = write.Operation
                           head = write.CurrentHead
                           alreadyApplied = write.AlreadyApplied |})))


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
                        V2PendingIntentJournal.execute binding operationId "V2IntegrationWrite" cmd (fun () ->
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
                    result |> Result.map (fun write ->
                        box {| operation = write.Operation; head = write.CurrentHead
                               alreadyApplied = write.AlreadyApplied |}))))
