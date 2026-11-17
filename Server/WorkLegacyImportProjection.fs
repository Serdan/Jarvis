namespace Jarvis.WorkQueues

open System
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Server

// C1 -> E0 is NOT a field-name conversion. These proofs must be supplied by a
// separately reviewed, authenticated original-writer/role/approval capture.
// The interface is intentionally unregistered: a caller-provided object or a
// C1 offline fence cannot authorize live migration or dispatch.
type WorkLegacyProjectionDecision =
    { WorkId: string
      State: string // exactly Draft, Proposed, or Open
      DecisionReceiptId: Guid
      DecisionVersion: int64
      SourceHead: string }

type WorkLegacyProjectionRole =
    { RoleId: string
      Version: int
      InstructionsDigest: string
      Queues: Set<string>
      EvidenceReceiptId: Guid
      SourceHead: string }

type WorkLegacyProjectionProjectEvidence =
    { ProjectId: Guid
      LedgerId: Guid
      SourceHead: string
      StopGeneration: int64
      Decisions: WorkLegacyProjectionDecision list
      Roles: WorkLegacyProjectionRole list
      OptionalUnitIds: Set<string> }

type WorkLegacyProjectionEvidence =
    { AccountId: Guid
      AccountLedgerId: Guid
      AccountHead: string
      DeviceId: Guid
      ApprovalId: Guid
      ImportOperationId: Guid
      Maximum: int
      Reserved: int
      Projects: WorkLegacyProjectionProjectEvidence list }

type IWorkLegacyProjectionVerifier =
    // Implementations MUST independently reread original canonical Git heads,
    // receipts, source/ref lineage, approved proposals, role instruction digests,
    // all-client writer quiescence, STOP and account-reservation high water.
    // Unknown/unreachable outcomes are errors. There is no production binding.
    abstract member Verify:
        WorkLegacyImportInspection * WorkLegacyFencedOfflineReceipt
            -> Result<WorkLegacyProjectionEvidence, string>

[<RequireQualifiedAccess>]
type WorkLegacyProjectionGap =
    | Incomplete of string
    | Preflight of WorkLegacyImportGap list
    | Storage of WorkSqliteFailure
    | UnsafeDestination

type WorkLegacyProjectionOutcome =
    { Receipt: WorkSqliteReceipt
      Snapshot: SqliteSchedulerSnapshot }

[<RequireQualifiedAccess>]
module internal WorkLegacyImportProjection =
    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
    let private result = ResultBuilder()

    let private incomplete detail = Error(WorkLegacyProjectionGap.Incomplete detail)
    let private ensure condition detail =
        if condition then Ok () else incomplete detail

    let private sha = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
    let private digest (value: string) =
        not (isNull value) && sha.IsMatch value

    let private key kind project entity =
        { ProjectId = project; Kind = kind; EntityId = entity }

    let private read (store: WorkSqliteAuthority) kind project entity =
        store.ReadRecord(key kind project entity)
        |> Result.mapError WorkLegacyProjectionGap.Storage
        |> Result.bind (function
            | Some record -> Ok record
            | None -> incomplete ("missing imported evidence " + project + "/" + entity))

    let private body (row: WorkSqliteRecord) =
        try Ok(JsonNode.Parse(row.Value.BodyJson).AsObject())
        with _ -> incomplete ("malformed imported record " + row.Key.EntityId)

    let private str (name: string) (o: JsonObject) =
        try Option.ofObj o[name] |> Option.map (fun v -> v.GetValue<string>())
        with _ -> None

    let private int32 (name: string) (o: JsonObject) =
        try Option.ofObj o[name] |> Option.map (fun v -> v.GetValue<int>())
        with _ -> None

    let private int64Prop (name: string) (o: JsonObject) =
        try Option.ofObj o[name] |> Option.map (fun v -> v.GetValue<int64>())
        with _ -> None

    let private boolean (name: string) (o: JsonObject) =
        try Option.ofObj o[name] |> Option.map (fun v -> v.GetValue<bool>())
        with _ -> None

    let private nested (name: string) (o: JsonObject) =
        try Option.ofObj o[name] |> Option.map (fun v -> v.AsObject())
        with _ -> None

    let private keys (name: string) (o: JsonObject) : Set<string> option =
        try Option.ofObj o[name] |> Option.map (fun v ->
            v.AsObject() |> Seq.map (fun kv -> kv.Key) |> Set.ofSeq)
        with _ -> None

    let private strings (name: string) (o: JsonObject) : Set<string> option =
        try Option.ofObj o[name] |> Option.map (fun v ->
            v.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> Set.ofSeq)
        with _ -> None

    let private importedHeads (o: JsonObject) =
        try
            o["ProjectHeads"].AsArray()
            |> Seq.map (fun entry ->
                let value = entry.AsObject()
                str "ProjectId" value, str "Head" value)
            |> Seq.toList |> List.sort |> Some
        with _ -> None

    let private array (values: seq<string>) =
        let arr = JsonArray()
        for value in values do arr.Add(JsonValue.Create value)
        arr

    let private guard (row: WorkSqliteRecord) =
        { Key = row.Key
          ExpectedVersion = row.Version
          GraphVersion = Some row.Value.GraphVersion
          StageGeneration = Some row.Value.StageGeneration
          ClaimGeneration = Some row.Value.ClaimGeneration
          StopGeneration = Some row.Value.StopGeneration
          RoleVersion = Some row.Value.RoleVersion
          SourceSha = row.Value.SourceSha }

    let private rawSet (name: string) (o: JsonObject) = strings name o
    let private stageState = function
        | WorkState.Available -> "Available"
        | WorkState.Blocked -> "Blocked"
        | WorkState.Claimed _ -> "Claimed"
        | WorkState.GateException -> "GateException"
        | WorkState.Completed -> "Completed"
        | WorkState.Cancelled -> "Cancelled"

    let private gateState = function
        | GateState.Clear -> "Clear"
        | GateState.CompositionHold _ -> "CompositionHold"
        | GateState.DependencyGatePending _ -> "DependencyGatePending"
        | GateState.IntegrationGateHold _ -> "IntegrationGateHold"
        | GateState.ExternalEffectObservedPendingGate _ -> "ExternalEffectObservedPendingGate"
        | GateState.SourceRecoveryBlocked _ -> "SourceRecoveryBlocked"

    let private traverse f items =
        let rec loop acc = function
            | [] -> Ok(List.rev acc)
            | x :: xs ->
                match f x with
                | Error e -> Error e
                | Ok value -> loop (value :: acc) xs
        loop [] items

    let private verifiedFence (store: WorkSqliteAuthority)
                              (inspection: WorkLegacyImportInspection)
                              (c2: WorkLegacyFencedOfflineReceipt)
                              (proof: WorkLegacyProjectionEvidence) =
        result {
            let account = inspection.Account
            let accountId = account.AccountId.ToString("D")
            let! () = ensure (proof.AccountId = account.AccountId
                 && proof.AccountLedgerId = account.LedgerId
                 && proof.AccountHead = account.Head.PinnedCommit
                 && proof.DeviceId <> Guid.Empty
                 && proof.ApprovalId <> Guid.Empty
                 && proof.ApprovalId = c2.ApprovalId
                 && proof.ImportOperationId = c2.Import.ImportOperationId
                 && c2.Import.AccountId = account.AccountId
                 && c2.Import.SourceAccountHead = account.Head.PinnedCommit
                 && (c2.Import.ProjectHeads |> List.sort) =
                    (inspection.Projects |> List.map (fun p -> p.ProjectId, p.Head.PinnedCommit) |> List.sort)
                 && proof.Maximum = account.MaxConcurrent
                 && proof.Reserved >= 0 && proof.Reserved <= proof.Maximum) "account/device/operator/import proof is incomplete"
            let! importReceipt = store.ReadReceipt(c2.Import.ImportOperationId)
                                 |> Result.mapError WorkLegacyProjectionGap.Storage
            let! () = ensure (importReceipt |> Option.exists (fun r -> r.Sequence = c2.Import.SqliteSequence)) "the exact original C1 SQLite import receipt is absent"
            let! marker = read store WorkSqliteRecordKind.Project accountId "legacy-import-fence"
            let! markerBody = body marker
            let! () = ensure (str "Mode" markerBody = Some "offline-only"
                 && marker.Value.ExternalEffect = WorkSqliteEffect.Prepared
                 && marker.Value.SourceSha = Some proof.AccountHead
                 && str "AccountHead" markerBody = Some proof.AccountHead
                 && int32 "SchemaVersion" markerBody = Some 1
                 && importedHeads markerBody =
                    Some(inspection.Projects
                         |> List.map (fun p -> Some(p.ProjectId.ToString("D")), Some p.Head.PinnedCommit)
                         |> List.sort)) "C1 offline fence is absent or changed"
            let! audit = read store WorkSqliteRecordKind.Project accountId "legacy-import-c2-authority-audit"
            let! auditBody = body audit
            let! () = ensure (str "Mode" auditBody = Some "c2-disposable-offline-only"
                 && str "DecisionId" auditBody = Some(proof.ApprovalId.ToString("D"))
                 && str "DeviceId" auditBody = Some(proof.DeviceId.ToString("D"))
                 && str "AccountId" auditBody = Some accountId
                 && str "ImportOperationId" auditBody = Some(proof.ImportOperationId.ToString("D"))
                 && audit.Value.SourceSha = Some proof.AccountHead
                 && audit.Value.ExternalEffect = WorkSqliteEffect.Prepared) "independently fenced C2 audit is absent or mismatched"
            let all = c2.OriginalWriterFence
            let expectedIds =
                Set.ofList(account.AccountId :: (inspection.Projects |> List.map (fun p -> p.ProjectId)))
            let! () = ensure (all.Length = expectedIds.Count
                 && (all |> List.map (fun r -> r.Binding.EntityId) |> Set.ofList) = expectedIds
                 && (all |> List.forall (fun r ->
                     r.Binding.DeviceId = proof.DeviceId
                     && r.DecisionId = proof.ApprovalId
                     && r.NoNewOriginalMutations && r.DurableUntilExplicitCutover
                     && r.AllOriginalClientsQuiesced
                     && r.OriginalPendingIntentHighWater >= 0L
                     && r.OriginalSourceEffectHighWater >= 0L
                     && r.FenceHighWater = r.OriginalHighWater + 1L))) "original writer/client/high-water quiescence cannot be proved"
            let! () =
                try
                    let entries = auditBody["OriginalGitFences"].AsArray() |> Seq.map (fun n -> n.AsObject()) |> Seq.toList
                    ensure (entries.Length = all.Length &&
                        all |> List.forall (fun r ->
                            entries |> List.exists (fun e ->
                                str "EntityId" e = Some(r.Binding.EntityId.ToString("D"))
                                && str "LedgerId" e = Some(r.Binding.LedgerId.ToString("D"))
                                && str "FencedHead" e = Some r.FencedHead
                                && str "AuditSha256" e = Some r.AuditSha256
                                && int64Prop "WriterGeneration" e = Some r.WriterGeneration
                                && boolean "AllOriginalClientsQuiesced" e = Some true))) "C2 receipt set differs from its transaction-bound SQLite audit"
                with _ -> incomplete "malformed C2 original-writer receipt inventory"
            return [ marker; audit ]
        }

    let private buildProject (store: WorkSqliteAuthority)
                             (project: WorkLegacyImportProject)
                             (proof: WorkLegacyProjectionProjectEvidence) =
        result {
            let id = project.ProjectId.ToString("D")
            let stages = project.Queue.Works
            let roles = project.Queue.Subscriptions
            let! () = ensure (proof.ProjectId = project.ProjectId
                 && proof.LedgerId = project.LedgerId
                 && proof.SourceHead = project.Head.PinnedCommit
                 && proof.StopGeneration >= 0L
                 && project.Queue.Migration = MigrationEpoch.Frozen
                 && project.Queue.GraphVersion = project.Graph.Version
                 && project.Queue.Launches.IsEmpty
                 && proof.OptionalUnitIds.IsEmpty
                 && (proof.Decisions |> List.map (fun d -> d.WorkId) |> Set.ofList) =
                    (stages |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                 && proof.Decisions.Length = stages.Count
                 && (proof.Roles |> List.map (fun r -> r.RoleId) |> Set.ofList) =
                    (roles |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                 && proof.Roles.Length = roles.Count) ("legacy launch, graph, proposal or role coverage is incomplete for " + id)
            let! legacyQueue = read store WorkSqliteRecordKind.Project id "legacy-queue"
            let! queueJson = body legacyQueue
            let! legacyGraph = read store WorkSqliteRecordKind.Project id "legacy-composition"
            let! graphJson = body legacyGraph
            let! () = ensure (legacyQueue.Value.SourceSha = Some proof.SourceHead
                 && legacyQueue.Value.GraphVersion = int64 project.Graph.Version
                 && legacyGraph.Value.SourceSha = Some proof.SourceHead
                 && legacyGraph.Value.GraphVersion = int64 project.Graph.Version
                 && int32 "Version" queueJson = Some project.Queue.Version
                 && int32 "GraphVersion" queueJson = Some project.Queue.GraphVersion
                 && (nested "Migration" queueJson |> Option.bind (str "case")) = Some "Frozen"
                 && keys "Works" queueJson = Some(stages |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                 && keys "Subscriptions" queueJson = Some(roles |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                 && keys "Launches" queueJson = Some Set.empty
                 && int32 "Version" graphJson = Some project.Graph.Version) "imported queue/graph/source version or exact stage and role inventory changed"
            let decisions = proof.Decisions |> List.map (fun d -> d.WorkId, d) |> Map.ofList
            let roleProofs = proof.Roles |> List.map (fun r -> r.RoleId, r) |> Map.ofList
            let! projectedStages =
                stages |> Map.toList |> traverse (fun (workId, stage) ->
                    result {
                        let decision = decisions.[workId]
                        let origin = project.Origins |> List.tryFind (fun o -> o.WorkId = workId)
                        let! source = match origin with
                                      | Some o -> Ok o
                                      | None -> incomplete ("missing verified original source " + workId)
                        let! () = ensure (stage.ProjectId = id && stage.StageGeneration > 0
                             && decision.State = "Open" && decision.DecisionReceiptId <> Guid.Empty
                             && decision.DecisionVersion > 0L && decision.SourceHead = proof.SourceHead
                             && source.SourceCommit.Length >= 40
                             && source.SourceAncestryVerified
                             && source.SourceHead = proof.SourceHead
                             && source.SubjectId = stage.SubjectId
                             && source.StageGeneration = stage.StageGeneration
                             && source.GraphVersion <= project.Graph.Version
                             && source.RepositoryId <> "" && source.SourceRef.StartsWith("refs/")
                             && (roleProofs.TryFind source.RoleId |> Option.exists (fun role ->
                                 role.Version = source.RoleVersion))
                             && (stage.Push |> Option.exists (fun push -> push.Outcome = PushOutcome.Unknown) |> not)) ("original approval, source or external effect is not proved for " + workId)
                        let! raw = read store WorkSqliteRecordKind.Stage id workId
                        let! rawJson = body raw
                        let! originRow = read store WorkSqliteRecordKind.Work id ("origin/" + workId)
                        let! originJson = body originRow
                        let! () = ensure (raw.Version = 1L || raw.Version = 2L) ("stage has been modified by an unknown writer: " + workId)
                        let! () = ensure (raw.Value.GraphVersion = int64 project.Graph.Version
                             && raw.Value.StageGeneration = int64 stage.StageGeneration
                             && raw.Value.ClaimGeneration = int64 stage.ClaimGeneration
                             && raw.Value.ExternalEffect <> WorkSqliteEffect.Unknown
                             && raw.Value.ExternalEffect <> WorkSqliteEffect.Prepared
                             && (nested "State" rawJson |> Option.bind (str "case")) = Some(stageState stage.State)
                             && str "QueueId" rawJson = Some stage.QueueId
                             && str "Id" rawJson = Some workId
                             && str "ProjectId" rawJson = Some id
                             && int32 "StageGeneration" rawJson = Some stage.StageGeneration
                             && int32 "ClaimGeneration" rawJson = Some stage.ClaimGeneration
                             && (nested "Gate" rawJson |> Option.bind (str "case")) = Some(gateState stage.Gate)
                             && strings "Dependencies" rawJson = Some stage.Dependencies
                             && keys "WaivedDependencies" rawJson =
                                 Some(stage.WaivedDependencies |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                             && int64Prop "AvailableSequence" rawJson = Some stage.AvailableSequence
                             && int64Prop "AvailableAt" rawJson = Some stage.AvailableAt
                             && originRow.Value.SourceSha = Some source.SourceCommit
                             && raw.Value.ReviewSha =
                                 (project.Reviews |> List.tryFind (fun review -> review.WorkId = workId)
                                  |> Option.map (fun review -> review.FrozenCommit))
                             && raw.Value.IntegrationSha =
                                 (project.Integrations |> List.tryFind (fun integration -> integration.WorkId = workId)
                                  |> Option.map (fun integration -> integration.IntegratedCommit))
                             && str "SourceCommit" originJson = Some source.SourceCommit
                             && str "SourceRef" originJson = Some source.SourceRef
                             && str "RepositoryId" originJson = Some source.RepositoryId
                             && str "SourceHead" originJson = Some source.SourceHead
                             && raw.Value.SourceSha = Some source.SourceCommit) ("raw C1 stage/source mismatch: " + workId)
                        rawJson["state"] <- JsonValue.Create(stageState stage.State)
                        rawJson["queueId"] <- JsonValue.Create(stage.QueueId)
                        rawJson["gate"] <- JsonValue.Create(gateState stage.Gate)
                        rawJson["availableSequence"] <- JsonValue.Create(stage.AvailableSequence)
                        rawJson["proposalState"] <- JsonValue.Create(decision.State)
                        rawJson["decisionReceiptId"] <- JsonValue.Create(decision.DecisionReceiptId.ToString("D"))
                        rawJson["dependencies"] <- array stage.Dependencies
                        rawJson["waivedDependencies"] <- array (stage.WaivedDependencies |> Map.toSeq |> Seq.map fst)
                        let value =
                            { raw.Value with BodyJson = rawJson.ToJsonString()
                                             StopGeneration = proof.StopGeneration }
                        let normalized = { raw with Version = 2L; Value = value }
                        return (workId, normalized, originRow)
                    })
            let! projectedRoles =
                roles |> Map.toList |> traverse (fun (roleId, queues) ->
                    result {
                        let proofRole = roleProofs.[roleId]
                        let! () = ensure (proofRole.RoleId = roleId && proofRole.Version > 0
                             && proofRole.Queues = queues && digest proofRole.InstructionsDigest
                             && proofRole.EvidenceReceiptId <> Guid.Empty
                             && proofRole.SourceHead = proof.SourceHead) ("missing independently verified role instructions " + roleId)
                        let! raw = read store WorkSqliteRecordKind.Role id roleId
                        let! rawJson = body raw
                        let! () = ensure ((raw.Version = 1L || raw.Version = 2L)
                             && rawSet "LegacyValue" rawJson = Some queues) ("raw C1 role subscription differs from verified role " + roleId)
                        rawJson["state"] <- JsonValue.Create("Active")
                        rawJson["instructionsDigest"] <- JsonValue.Create(proofRole.InstructionsDigest)
                        rawJson["queues"] <- array queues
                        rawJson["evidenceReceiptId"] <- JsonValue.Create(proofRole.EvidenceReceiptId.ToString("D"))
                        let value =
                            { raw.Value with BodyJson = rawJson.ToJsonString()
                                             StopGeneration = proof.StopGeneration
                                             RoleVersion = int64 proofRole.Version }
                        let normalized = { raw with Version = 2L; Value = value }
                        return
                            { Id = roleId
                              Version = proofRole.Version
                              Queues = queues
                              InstructionsDigest = proofRole.InstructionsDigest
                              Stored = normalized }
                    })
            let pBody = JsonObject()
            pBody["coverage"] <- JsonValue.Create("Verified")
            pBody["queueVersion"] <- JsonValue.Create(project.Queue.Version)
            pBody["stageIds"] <- array (stages |> Map.toSeq |> Seq.map fst)
            pBody["roleIds"] <- array (roles |> Map.toSeq |> Seq.map fst)
            pBody["launchIds"] <- array Seq.empty
            pBody["originalProjectHead"] <- JsonValue.Create(proof.SourceHead)
            pBody["originalLedgerId"] <- JsonValue.Create(proof.LedgerId.ToString("D"))
            let authority =
                { Key = key WorkSqliteRecordKind.Project id "authority"
                  Version = 1L
                  Value =
                    { BodyJson = pBody.ToJsonString()
                      GraphVersion = int64 project.Graph.Version
                      StageGeneration = 0L
                      ClaimGeneration = 0L
                      StopGeneration = proof.StopGeneration
                      RoleVersion = 0L
                      SourceSha = Some proof.SourceHead
                      ReviewSha = None
                      IntegrationSha = None
                      ExternalEffect = WorkSqliteEffect.NoneRecorded } }
            let normalized =
                { Id = id
                  Queue = project.Queue
                  Graph = project.Graph
                  OptionalUnitIds = proof.OptionalUnitIds
                  Stored = authority
                  StageRecords = projectedStages |> List.map (fun (id, row, _) -> id, row) |> Map.ofList
                  RoleRecords = projectedRoles
                  LaunchRecords = Map.empty }
            let immutableEvidence =
                [ yield legacyQueue; yield legacyGraph
                  for (_, _, origin) in projectedStages do yield origin ]
            let newRecords =
                [ yield { Key = authority.Key; ExpectedVersion = None; Value = authority.Value }
                  for (_, row, _) in projectedStages do
                      yield { Key = row.Key; ExpectedVersion = Some 1L; Value = row.Value }
                  for role in projectedRoles do
                      yield { Key = role.Stored.Key; ExpectedVersion = Some 1L
                              Value = role.Stored.Value } ]
            return normalized, immutableEvidence, newRecords
        }

    /// Default-off hermetic projection. Preserves every imported legacy JSON
    /// field in place, adds normalized fields in one CAS transaction, and never
    /// calls the Scheduler or any external transport. The caller must use the
    /// returned snapshot with the separate default-off E0/E1a APIs.
    let commitOfflineFixture (verifier: IWorkLegacyProjectionVerifier)
                             (databasePath: string)
                             (inspection: WorkLegacyImportInspection)
                             (c2: WorkLegacyFencedOfflineReceipt)
                             (operationId: Guid)
                             : Result<WorkLegacyProjectionOutcome, WorkLegacyProjectionGap> =
        if operationId = Guid.Empty || not (WorkLegacyImportSqlite.isDisposableFixturePath databasePath) then
            Error WorkLegacyProjectionGap.UnsafeDestination
        else
            match WorkLegacyImportPreflight.inspect inspection with
            | Error gaps -> Error(WorkLegacyProjectionGap.Preflight gaps)
            | Ok index ->
                let evidence =
                    try verifier.Verify(inspection, c2)
                    with _ -> Error "original-authority verifier unavailable or threw"
                match evidence with
                | Error why -> incomplete ("unverified original capture: " + why)
                | Ok proof ->
                    let store = WorkSqliteAuthority databasePath
                    result {
                        let! immutableFence = verifiedFence store inspection c2 proof
                        let! () = ensure (proof.Reserved = index.ReservedCapacity
                             && proof.Projects.Length = inspection.Projects.Length
                             && (proof.Projects |> List.map (fun p -> p.ProjectId) |> Set.ofList) =
                                (inspection.Projects |> List.map (fun p -> p.ProjectId) |> Set.ofList)
                             && index.ReservedCapacity = 0) "unverified global capacity, incomplete project set or existing launch"
                        let! accountSnapshot =
                            read store WorkSqliteRecordKind.Project (proof.AccountId.ToString("D")) "legacy-account"
                        let! () = ensure (accountSnapshot.Value.SourceSha = Some proof.AccountHead) "imported original account snapshot changed"
                        let! projects =
                            inspection.Projects
                            |> traverse (fun p ->
                                let capture = proof.Projects |> List.find (fun x -> x.ProjectId = p.ProjectId)
                                buildProject store p capture)
                        let! capacity = store.ReadCapacity(proof.AccountId.ToString("D"))
                                        |> Result.mapError WorkLegacyProjectionGap.Storage
                        let! () = ensure (capacity |> Option.exists (fun value ->
                                value.Maximum = proof.Maximum
                                && value.Reserved = proof.Reserved)) "original account capacity does not match verified high-water"
                        let projectSnapshots = projects |> List.map (fun (p, _, _) -> p)
                        let command =
                            { OperationId = operationId
                              Guards =
                                (accountSnapshot :: immutableFence)
                                @ (projects |> List.collect (fun (_, immutable, _) -> immutable))
                                |> List.map guard
                              Records = projects |> List.collect (fun (_, _, mutations) -> mutations)
                              Capacities =
                                [ { AccountId = proof.AccountId.ToString("D")
                                    ExpectedVersion = Some 1L
                                    Maximum = None
                                    ReservedDelta = 0 } ] }
                        let! committed = store.Commit command
                                         |> Result.mapError WorkLegacyProjectionGap.Storage
                        let snapshot =
                            { Account =
                                { AccountId = proof.AccountId.ToString("D")
                                  Version = 2L
                                  Maximum = proof.Maximum
                                  Reserved = proof.Reserved }
                              Projects = projectSnapshots
                              CompleteProjectSet = true }
                        return { Receipt = committed; Snapshot = snapshot }
                    }
