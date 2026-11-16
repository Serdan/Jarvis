module Client.WorkQueueProjectMigrationPrepare

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkCompositionGitLedger
open Client.AccountCapacityLedger

// WQ10 project-side conversion is deliberately separate from WQ07 account Preparing.
// This assembly-internal positive writer can run ONLY in a private disposable fixture.
// It is not a production authentication, old-writer-fence or migration entrypoint.
type ProjectPreparedTaskLink =
    { LegacyTaskId: string
      TaskBlobOid: string
      UnitId: string
      WorkId: string
      HistoricalPathsSha256: string }

type ProjectPreparedReceipt =
    { SchemaVersion: int
      Status: string
      OriginalOperationId: string
      ProjectId: string
      ProjectLedgerId: string
      ProjectEpoch: int
      FrozenBaseHead: string
      AccountId: string
      AccountLedgerId: string
      AccountEpoch: int
      AccountPreparingHead: string
      AccountStateVersion: int64
      ReviewedReportSha256: string
      OriginalJournalId: string
      OriginalJournalHighWater: int64
      OriginalSourceEffectHighWater: int64
      OriginalJournalWitnessSha256: string
      OriginalOperationRequestHashes: Map<string, string>
      LegacyPaths: Map<string, string>
      LegacyCoverageSha256: string
      Conversion: Map<string, ProjectPreparedTaskLink>
      GraphVersion: int
      QueueVersion: int
      RuntimeAuthorized: bool }

type DisposableOriginalJournal =
    { SchemaVersion: int
      JournalId: string
      OriginalHighWater: int64
      OriginalSourceEffectHighWater: int64
      // Fixture-only proof: each UUID/hash must match the original immutable
      // LedgerStore operation receipt at the exact Frozen project head.
      OriginalRequestHashes: Map<string, string> }

type ProjectPrepareCommand =
    { OriginalOperationId: Guid
      Actor: string
      Project: LedgerBinding
      ExpectedProjectHead: string
      Account: AccountBinding
      ExpectedAccountHead: string
      ExpectedAccountStateVersion: int64
      ReviewedReportSha256: string
      OriginalJournalId: Guid
      DisposableRoot: string }

[<RequireQualifiedAccess>]
type ProjectPreparedInspection =
    | NotPrepared
    | ProjectOnly of ProjectPreparedReceipt
    | AccountHeadDiverged of ProjectPreparedReceipt * observed: string
    | RecoveryBlocked of string

module WorkQueueProjectMigrationPrepare =
    [<Literal>]
    let ReceiptPath = "records/work-queue/project-prepared.json"
    [<Literal>]
    let FixtureJournalPath = "records/wq10-fixture/original-journal.json"

    let private fail reason : LedgerFailure =
        InvalidBinding ("WQ10 project Prepared: " + reason)

    let private sha256 = Regex("^[a-f0-9]{64}$", RegexOptions.Compiled)
    let private gitOid = Regex("^[a-f0-9]{40}([a-f0-9]{24})?$", RegexOptions.Compiled)

    let private validHead head = not (isNull head) && gitOid.IsMatch head

    // The marker is only a *test sink restriction*, never an authorization
    // claim. A production caller cannot reach the writer through the public API.
    let private approvedDisposable (command: ProjectPrepareCommand) =
        try
            let absolute (raw: string) =
                not (String.IsNullOrWhiteSpace raw) &&
                Path.IsPathFullyQualified raw &&
                (raw.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.forall ((<>) ".."))
            let rec noLinkedAncestors (path: string) =
                let attributes = File.GetAttributes path
                not (attributes.HasFlag FileAttributes.ReparsePoint)
                && (let parent = Path.GetDirectoryName path
                    isNull parent || parent = path || noLinkedAncestors parent)
            let rec noMetadataLinks path =
                Directory.Exists path && noLinkedAncestors path
                && (Directory.EnumerateFileSystemEntries path
                    |> Seq.forall (fun item ->
                        let attrs = File.GetAttributes item
                        not (attrs.HasFlag FileAttributes.ReparsePoint)
                        && (not (attrs.HasFlag FileAttributes.Directory) || noMetadataLinks item)))
            let root = command.DisposableRoot
            let tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            let contained path =
                absolute path && Directory.Exists path
                && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                    + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && noLinkedAncestors path
            let accounts = [ command.Account.RepositoryPath
                             command.Account.ApprovedFetchUrl
                             command.Account.ApprovedPushUrl ]
            let projects = [ command.Project.RepositoryPath
                             command.Project.ApprovedFetchUrl
                             command.Project.ApprovedPushUrl ]
            let rootValid =
                absolute root && Directory.Exists root
                && Path.GetFullPath(root).StartsWith(tmp + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && noLinkedAncestors root
                && File.Exists(Path.Combine(root, ".wq10-disposable-fixture"))
                && noLinkedAncestors (Path.Combine(root, ".wq10-disposable-fixture"))
                && File.ReadAllText(Path.Combine(root, ".wq10-disposable-fixture")) = "WQ10_DISPOSABLE_FIXTURE_V1"
            let privateRoot =
                if OperatingSystem.IsWindows() then true
                else
                    let mode = File.GetUnixFileMode root
                    (mode &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)) = enum<UnixFileMode> 0
            if not (rootValid && privateRoot && List.forall contained (accounts @ projects)) then false
            elif command.Account.ApprovedFetchUrl <> command.Account.ApprovedPushUrl
                 || command.Project.ApprovedFetchUrl <> command.Project.ApprovedPushUrl
                 || command.Account.ApprovedFetchUrl = command.Project.ApprovedFetchUrl then false
            else
                let clone path =
                    let metadata = Path.Combine(path, ".git")
                    noMetadataLinks metadata
                    && not (File.Exists(Path.Combine(metadata, "commondir")))
                    && not (File.Exists(Path.Combine(metadata, "objects", "info", "alternates")))
                let bare path =
                    noMetadataLinks path
                    && File.Exists(Path.Combine(path, "HEAD"))
                    && File.Exists(Path.Combine(path, "config"))
                    && not (File.Exists(Path.Combine(path, "objects", "info", "alternates")))
                clone command.Project.RepositoryPath
                && clone command.Account.RepositoryPath
                && bare command.Project.ApprovedFetchUrl
                && bare command.Account.ApprovedFetchUrl
        with _ -> false

    let private canonical<'a> (raw: string) =
        try
            let decoded = CanonicalCodec.decode<'a> raw
            if CanonicalCodec.encode decoded = raw then Ok decoded
            else Error(LedgerIntegrityError "Noncanonical WQ10 Prepared record.")
        with ex ->
            Error(LedgerIntegrityError ("Malformed WQ10 Prepared record: " + ex.Message))

    let private taskId path =
        let matchResult = Regex.Match(path, "^tasks/([A-Za-z0-9_-]+)/task[.]json$")
        if matchResult.Success then Some matchResult.Groups.[1].Value else None

    let private historyAt binding snapshot = result {
        let! tasks = LedgerStore.listAt binding snapshot "tasks"
        let! agents = LedgerStore.listAt binding snapshot "agents"
        let! roles = LedgerStore.listAt binding snapshot "records/roles"
        let paths = (tasks @ agents @ roles) |> List.distinct |> List.sort
        if paths.Length > 250 then
            return! Error(fail "Fixture contains too many legacy evidence paths for one bounded receipt.")
        let! history =
            (Ok Map.empty, paths)
            ||> List.fold (fun result path ->
                result |> Result.bind (fun history ->
                    LedgerStore.readAt binding snapshot path
                    |> Result.bind (function
                        | Some _, Some blob -> Ok(history.Add(path, blob))
                        | _ -> Error(LedgerIntegrityError ("Missing historical legacy Git blob: " + path)))))
        let canonicalTasks =
            tasks |> List.choose taskId |> List.distinct |> List.sort
        if canonicalTasks.IsEmpty then
            return! Error(fail "No original canonical task history exists to convert.")
        return history, canonicalTasks
    }

    let private originalJournalAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (expectedId: Guid) = result {
        let! raw, blob = LedgerStore.readAt binding snapshot FixtureJournalPath
        let! journal =
            match raw, blob with
            | Some content, Some _ -> canonical<DisposableOriginalJournal> content
            | _ -> Error(fail "Missing independent disposable original-journal fixture record.")
        if journal.SchemaVersion <> 1 || journal.JournalId <> expectedId.ToString("D")
           || journal.OriginalHighWater <= 0L
           || journal.OriginalSourceEffectHighWater < 0L
           || journal.OriginalRequestHashes.IsEmpty then
            return! Error(fail "Invalid fixture source-journal identity/high-water.")
        let! () =
            (Ok (), journal.OriginalRequestHashes |> Map.toList)
            ||> List.fold (fun state (uuid, expectedHash) ->
                state |> Result.bind (fun () ->
                    match Guid.TryParse uuid with
                    | true, id when id <> Guid.Empty && sha256.IsMatch expectedHash ->
                        LedgerStore.getOperationAt binding snapshot id
                        |> Result.bind (function
                            | Some op when op.OperationId = uuid
                                           && op.RequestHash = expectedHash
                                           && op.Sequence <= journal.OriginalHighWater -> Ok()
                            | _ -> Error(fail "Original operation UUID, hash or high-water did not verify."))
                    | _ -> Error(fail "Malformed original operation witness.")))
        return journal, CanonicalCodec.digest (CanonicalCodec.encode journal)
    }

    let private spec title goal linked =
        { Title = title; Goal = goal; Scope = "Inert historical WQ10 conversion"
          AcceptanceCriteria = Set.singleton "Requires independent post-migration validation"
          Constraints = Set.ofList [ "Fixture only"; "Never dispatch from Prepared" ]
          LinkedTaskId = linked }

    let private addGraphUnits (binding: LedgerBinding) (graph: WorkCompositionGraph) taskIds = result {
        let roots =
            graph.Units |> Map.toList |> List.map snd
            |> List.filter (fun item -> item.Kind = WorkUnitKind.Project)
        let rootId = match roots with
                     | [ root ] -> root.Id
                     | _ -> "wq10-project-" + binding.ProjectId.ToString("N")
        let! initial =
            if roots.IsEmpty then
                WorkComposition.apply graph.Version
                    (WorkCompositionMutation.Create(rootId, WorkUnitKind.Project, None,
                        spec "Legacy migration project" "Retain historical provenance" None)) graph
                |> Result.mapError (fun e -> fail (string e))
            elif roots.Length = 1 then Ok graph
            else Error(fail "Ambiguous composition project roots.")
        let! graphAndLinks =
            (Ok (initial, Map.empty), taskIds)
            ||> List.fold (fun current task ->
                current |> Result.bind (fun (currentGraph, links) ->
                    let matches =
                        currentGraph.Units |> Map.toList |> List.map snd
                        |> List.filter (fun unit -> unit.Spec.LinkedTaskId = Some task)
                    match matches with
                    | [ existing ] when existing.CancellationReason.IsNone ->
                        Ok(currentGraph, links.Add(task, existing.Id))
                    | [] ->
                        let id = "wq10-unit-" + CanonicalCodec.digest task
                        WorkComposition.apply currentGraph.Version
                            (WorkCompositionMutation.Create(id, WorkUnitKind.Feature, Some rootId,
                                spec ("Legacy " + task) ("Preserve " + task) (Some task)))
                            currentGraph
                        |> Result.mapError (fun e -> fail (string e))
                        |> Result.map (fun updated -> updated, links.Add(task, id))
                    | _ -> Error(fail ("Duplicate/cancelled original composition link: " + task))))
        return graphAndLinks
    }

    let private stage (binding: LedgerBinding) (originalUuid: string) (graphVersion: int) (task: string) =
        let id = "wq10-stage-" + CanonicalCodec.digest task
        { Id = id
          ProjectId = binding.ProjectId.ToString("D")
          SubjectId = task
          Kind = WorkKind.Planning
          QueueId = "wq10:inert-conversion"
          StageGeneration = 1
          State = WorkState.Blocked
          Domain = DomainState.Undecided
          // Gate != Clear prevents the normal WQ02 dependency wake code from
          // creating an Available stage on a later dependency transition.
          Gate = GateState.IntegrationGateHold(graphVersion, originalUuid)
          ClaimGeneration = 0
          Dependencies = Set.empty
          WaivedDependencies = Map.empty
          AvailableAt = 0L
          AvailableSequence = 0L
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }

    let private convertQueue (binding: LedgerBinding) (uuid: string) (before: Project) (taskIds: string list) = result {
        if before.Migration <> MigrationEpoch.Frozen
           || not (before.QueueCovered && before.GraphCovered
                   && before.MessageCovered && before.SourceCovered)
           || not before.Launches.IsEmpty
           || before.Works |> Map.exists (fun _ item ->
                item.State = WorkState.Available
                || (item.State = WorkState.Blocked && item.Gate = GateState.Clear)
                || (match item.State with WorkState.Claimed _ -> true | _ -> false)) then
            return! Error(fail "Frozen and wholly non-runnable WQ02 preimage required.")
        let gateVersion = before.GraphVersion + 1
        let! queueAndLinks =
            (Ok (before.Works, Map.empty), taskIds)
            ||> List.fold (fun current task ->
                current |> Result.bind (fun (stages, links) ->
                    let matched =
                        stages |> Map.toList |> List.map snd
                        |> List.filter (fun item -> item.SubjectId = task)
                    match matched with
                    | [] ->
                        let newStage = stage binding uuid gateVersion task
                        if stages.ContainsKey newStage.Id then
                            Error(fail ("Inert queue stage identity collision: " + task))
                        else Ok(stages.Add(newStage.Id, newStage), links.Add(task, newStage.Id))
                    | [ existing ] when existing.State = WorkState.Blocked
                                        && existing.Gate <> GateState.Clear ->
                        Ok(stages, links.Add(task, existing.Id))
                    | _ -> Error(fail ("Ambiguous or active original Work stage for " + task))))
        let stages, links = queueAndLinks
        let next =
            { before with
                Works = stages
                Version = before.Version + 1
                GraphVersion = gateVersion
                Migration = MigrationEpoch.Preparing
                Receipts = before.Receipts.Add(uuid, ProjectOperationPayload.OtherMutation "WQ10.Prepared:inert-project-only") }
        return next, links
    }

    let private preparePlan (command: ProjectPrepareCommand) snapshot = result {
        let binding = command.Project
        if snapshot.Commit <> command.ExpectedProjectHead then
            return! Error(StaleEntityVersion "Original Frozen project head moved; inspect and re-plan, never overwrite.")
        let! account = AccountCapacityLedger.fetch command.Account
                       |> Result.mapError (fun e -> fail ("Account read failed: " + string e))
        if account.Head <> command.ExpectedAccountHead
           || account.State.StateVersion <> command.ExpectedAccountStateVersion then
            return! Error(StaleEntityVersion "Account authority changed; cross-ledger state needs reconciliation.")
        if (account.State.MigrationPhase <> "Frozen" && account.State.MigrationPhase <> "Preparing")
           || AccountCapacityLedger.occupied account.State <> 0 then
            return! Error(fail "Account has not fenced its disposable migration epoch or still holds launches.")
        let! before = WorkQueueGitLedger.loadAt binding snapshot
        let! rawQueue, queueOid = LedgerStore.readAt binding snapshot WorkQueueGitLedger.RecordPath
        let! graph, graphOid = WorkCompositionGitLedger.loadAt binding snapshot
        let! already, _ = LedgerStore.readAt binding snapshot ReceiptPath
        if already.IsSome then
            return! Error(fail "Project already has a Prepared receipt; only original UUID replay is permitted.")
        let! history, taskIds = historyAt binding snapshot
        let! journal, witness = originalJournalAt binding snapshot command.OriginalJournalId
        let! nextGraph, units = addGraphUnits binding graph taskIds
        let op = command.OriginalOperationId.ToString("D")
        let! nextQueue, stages = convertQueue binding op before taskIds
        let links =
            taskIds
            |> List.map (fun task ->
                let prefix = "tasks/" + task + "/"
                let members = history |> Map.filter (fun p _ -> p.StartsWith(prefix, StringComparison.Ordinal))
                let item =
                    { LegacyTaskId = task; TaskBlobOid = history.["tasks/" + task + "/task.json"]
                      UnitId = units.[task]; WorkId = stages.[task]
                      HistoricalPathsSha256 = CanonicalCodec.digest (CanonicalCodec.encode members) }
                task, item)
            |> Map.ofList
        let receipt =
            { SchemaVersion = 1
              Status = "PreparedInertProjectOnly"
              OriginalOperationId = op
              ProjectId = binding.ProjectId.ToString("D")
              ProjectLedgerId = binding.LedgerId.ToString("D")
              ProjectEpoch = binding.Epoch
              FrozenBaseHead = snapshot.Commit
              AccountId = command.Account.AccountId.ToString("D")
              AccountLedgerId = command.Account.LedgerId.ToString("D")
              AccountEpoch = command.Account.Epoch
              AccountPreparingHead = account.Head
              AccountStateVersion = account.State.StateVersion
              ReviewedReportSha256 = command.ReviewedReportSha256
              OriginalJournalId = journal.JournalId
              OriginalJournalHighWater = journal.OriginalHighWater
              OriginalSourceEffectHighWater = journal.OriginalSourceEffectHighWater
              OriginalJournalWitnessSha256 = witness
              OriginalOperationRequestHashes = journal.OriginalRequestHashes
              LegacyPaths = history
              LegacyCoverageSha256 = CanonicalCodec.digest (CanonicalCodec.encode history)
              Conversion = links
              GraphVersion = nextGraph.Version
              QueueVersion = nextQueue.Version
              RuntimeAuthorized = false }
        let graphJson = CanonicalCodec.encode
                            { SchemaVersion = 1
                              ProjectId = binding.ProjectId.ToString("D")
                              StateHash = CanonicalCodec.digest (CanonicalCodec.encode nextGraph)
                              Graph = nextGraph }
        let queueJson = CanonicalCodec.encode
                            { SchemaVersion = 1
                              ProjectId = binding.ProjectId.ToString("D")
                              StateHash = CanonicalCodec.digest (CanonicalCodec.encode nextQueue)
                              State = nextQueue }
        // All three records, plus LedgerStore's original UUID receipt, are
        // committed in ONE optimistic project Git tree. Zero source/legacy writes.
        return
            [ { Path = WorkCompositionGitLedger.RecordPath
                ExpectedBlobOid = graphOid; Content = graphJson }
              { Path = WorkQueueGitLedger.RecordPath
                ExpectedBlobOid = queueOid; Content = queueJson }
              { Path = ReceiptPath
                ExpectedBlobOid = None; Content = CanonicalCodec.encode receipt } ]
    }

    // Default-off operational port. No command, signer, role or caller-supplied
    // boolean can authorize a production project conversion through it.
    let attempt (_: ProjectPrepareCommand) : Result<LedgerWrite, LedgerFailure> =
        Error(fail "Public project Prepared conversion is disabled: independent host trust, verified original journal high-water and old-writer fence are missing.")

    let internal attemptOnDisposableFixture command : Result<LedgerWrite, LedgerFailure> =
        if command.OriginalOperationId = Guid.Empty
           || command.OriginalJournalId = Guid.Empty
           || String.IsNullOrWhiteSpace command.Actor
           || not (validHead command.ExpectedProjectHead)
           || not (validHead command.ExpectedAccountHead)
           || String.IsNullOrWhiteSpace command.ReviewedReportSha256
           || not (sha256.IsMatch command.ReviewedReportSha256)
           || command.Project.Epoch <= 0 || command.Account.Epoch <= 0
           || not (approvedDisposable command) then
            Error(fail "No approved private disposable fixture authority for project Prepared.")
        else
            let intent =
                { OperationId = command.OriginalOperationId
                  Actor = command.Actor
                  Kind = "WQ10.PrepareProjectEpoch.FixtureOnly"
                  // Bind the original request independently of clone-local paths.
                  RequestJson =
                    JsonSerializer.Serialize(
                        {| schemaVersion = 1
                           action = "PrepareProjectEpoch"
                           projectId = command.Project.ProjectId.ToString("D")
                           projectLedgerId = command.Project.LedgerId.ToString("D")
                           projectEpoch = command.Project.Epoch
                           expectedProjectHead = command.ExpectedProjectHead
                           accountId = command.Account.AccountId.ToString("D")
                           accountLedgerId = command.Account.LedgerId.ToString("D")
                           accountEpoch = command.Account.Epoch
                           expectedAccountHead = command.ExpectedAccountHead
                           expectedAccountVersion = command.ExpectedAccountStateVersion
                           originalJournalId = command.OriginalJournalId.ToString("D")
                           reviewedReportSha256 = command.ReviewedReportSha256 |}) }
            LedgerStore.transact command.Project intent (preparePlan command)

    // Read-only recovery. "ProjectOnly" is not permission to activate any
    // account/Scheduler, even if an account head later advances.
    let inspect (project: LedgerBinding) (account: AccountBinding) : Result<ProjectPreparedInspection, LedgerFailure> = result {
        let! snapshot = LedgerStore.fetch project
        let! raw, _ = LedgerStore.readAt project snapshot ReceiptPath
        match raw with
        | None -> return ProjectPreparedInspection.NotPrepared
        | Some json ->
            let! receipt = canonical<ProjectPreparedReceipt> json
            if receipt.SchemaVersion <> 1
               || receipt.Status <> "PreparedInertProjectOnly"
               || receipt.RuntimeAuthorized
               || receipt.ProjectId <> project.ProjectId.ToString("D")
               || receipt.ProjectLedgerId <> project.LedgerId.ToString("D")
               || receipt.ProjectEpoch <> project.Epoch
               || receipt.AccountId <> account.AccountId.ToString("D")
               || receipt.AccountLedgerId <> account.LedgerId.ToString("D")
               || receipt.AccountEpoch <> account.Epoch
               || receipt.LegacyCoverageSha256 <> CanonicalCodec.digest (CanonicalCodec.encode receipt.LegacyPaths) then
                return ProjectPreparedInspection.RecoveryBlocked "Prepared identity or coverage digest failed."
            else
                let! queue = WorkQueueGitLedger.loadAt project snapshot
                let! graph, _ = WorkCompositionGitLedger.loadAt project snapshot
                let consistent =
                    queue.Migration = MigrationEpoch.Preparing
                    && receipt.Conversion |> Map.forall (fun task link ->
                        link.LegacyTaskId = task
                        && graph.Units.TryFind link.UnitId
                           |> Option.exists (fun unit -> unit.Spec.LinkedTaskId = Some task)
                        && queue.Works.TryFind link.WorkId
                           |> Option.exists (fun stage ->
                               stage.State = WorkState.Blocked
                               && stage.Gate <> GateState.Clear
                               && stage.SubjectId = task))
                if not consistent then
                    return ProjectPreparedInspection.RecoveryBlocked "Project conversion graph/queue is no longer inert and consistent."
                else
                    let! observed = AccountCapacityLedger.fetch account
                                    |> Result.mapError (fun e -> fail ("Account recovery read failed: " + string e))
                    if observed.Head <> receipt.AccountPreparingHead then
                        return ProjectPreparedInspection.AccountHeadDiverged(receipt, observed.Head)
                    else
                        return ProjectPreparedInspection.ProjectOnly receipt
    }
