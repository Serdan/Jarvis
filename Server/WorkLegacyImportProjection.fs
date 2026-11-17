namespace Jarvis.WorkQueues

open System
open System.Collections
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
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
    // C1's original SnapshotJson is private to its importer. Independently
    // re-encode the complete typed inspection (same F# record/DU/map/set
    // format) instead of trusting SQLite's SourceSha or same-name key sets.
    // This helper never promotes captured evidence: Verify must authenticate
    // the original Git/role/approval source independently.
    module private OriginalC1 =
        let private encodeText (s: string) = JsonValue.Create(s) :> JsonNode
        let rec private encodeType (shape: Type) (value: obj) : JsonNode =
            if shape = typeof<string> then encodeText (unbox value)
            elif shape = typeof<Guid> then encodeText ((unbox<Guid> value).ToString("D"))
            elif shape = typeof<int> then JsonValue.Create(unbox<int> value) :> JsonNode
            elif shape = typeof<int64> then JsonValue.Create(unbox<int64> value) :> JsonNode
            elif shape = typeof<bool> then JsonValue.Create(unbox<bool> value) :> JsonNode
            elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Map<_,_>> then
                let args = shape.GetGenericArguments()
                if args.[0] <> typeof<string> then invalidOp "Unsupported legacy map key."
                let o = JsonObject()
                for item in (value :?> IEnumerable) do
                    let itemType = item.GetType()
                    let key = itemType.GetProperty("Key").GetValue(item) :?> string
                    let body = itemType.GetProperty("Value").GetValue(item)
                    o.Add(key, encodeType args.[1] body)
                o :> JsonNode
            elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Set<_>> then
                let child = shape.GetGenericArguments().[0]
                let a = JsonArray()
                for item in value :?> IEnumerable do a.Add(encodeType child item)
                a :> JsonNode
            elif shape.IsArray then
                let a = JsonArray()
                for item in value :?> IEnumerable do a.Add(encodeType (shape.GetElementType()) item)
                a :> JsonNode
            elif FSharpType.IsRecord shape then
                let o = JsonObject()
                let fields = FSharpType.GetRecordFields shape
                let values = FSharpValue.GetRecordFields value
                for f, item in Array.zip fields values |> Array.sortBy (fun (f, _) -> f.Name) do
                    o.Add(f.Name, encodeType f.PropertyType item)
                o :> JsonNode
            elif FSharpType.IsUnion shape then
                let case, fields = FSharpValue.GetUnionFields(value, shape)
                let o = JsonObject()
                o.Add("case", encodeText case.Name)
                let a = JsonArray()
                for f, item in Array.zip (case.GetFields()) fields do
                    a.Add(encodeType f.PropertyType item)
                o.Add("fields", a)
                o :> JsonNode
            else invalidOp ("Unsupported legacy field type " + shape.FullName)

        let encode<'T> (value: 'T) =
            encodeType typeof<'T> (box value) |> fun node -> node.ToJsonString()

        let plain<'T> (value: 'T) =
            let container = JsonObject()
            container.Add("LegacyValue", encodeType typeof<'T> (box value))
            container.ToJsonString()

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
    let private result = ResultBuilder()

    let private incomplete detail = Error(WorkLegacyProjectionGap.Incomplete detail)
    let private ensure condition detail =
        if condition then Ok () else incomplete detail

    let private sha = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
    let private gitOid = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
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

    let private originalValue json graph stage claim stop role source review integration effect =
        { BodyJson = json
          GraphVersion = int64 graph
          StageGeneration = int64 stage
          ClaimGeneration = int64 claim
          StopGeneration = int64 stop
          RoleVersion = int64 role
          SourceSha = source
          ReviewSha = review
          IntegrationSha = integration
          ExternalEffect = effect }

    // Read the *whole* original C1 row. SourceSha and CAS row version are not
    // authenticated hashes of BodyJson; a modified body must fail before the
    // normalized authority is written.
    let private originalRow (store: WorkSqliteAuthority) kind project entity expected =
        result {
            let! row = read store kind project entity
            let! () = ensure (row.Version = 1L && row.Value = expected)
                        ("original imported row is not equal to verified source: " + project + "/" + entity)
            return row
        }

    let private originalOrProjected (row: WorkSqliteRecord)
                                    (original: WorkSqliteRecordValue)
                                    (normalized: WorkSqliteRecordValue) =
        (row.Version = 1L && row.Value = original)
        || (row.Version = 2L && row.Value = normalized)

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
            let! () = ensure (marker.Version = 1L
                 && markerBody.Count = 6
                 && marker.Value.GraphVersion = 0L
                 && marker.Value.StageGeneration = 0L
                 && marker.Value.ClaimGeneration = 0L
                 && marker.Value.StopGeneration = 0L
                 && marker.Value.RoleVersion = 0L
                 && marker.Value.ExternalEffect = WorkSqliteEffect.Prepared
                 && marker.Value.SourceSha = Some proof.AccountHead
                 && marker.Value.ReviewSha.IsNone
                 && marker.Value.IntegrationSha.IsNone
                 && str "Mode" markerBody = Some "offline-only"
                 && str "AccountHead" markerBody = Some proof.AccountHead
                 && int32 "SchemaVersion" markerBody = Some 1
                 && importedHeads markerBody =
                    Some(inspection.Projects
                         |> List.map (fun p -> Some(p.ProjectId.ToString("D")), Some p.Head.PinnedCommit)
                         |> List.sort)) "C1 offline fence is absent or changed"
            let! audit = read store WorkSqliteRecordKind.Project accountId "legacy-import-c2-authority-audit"
            let! auditBody = body audit
            let all = c2.OriginalWriterFence
            let expectedIds =
                Set.ofList(account.AccountId :: (inspection.Projects |> List.map (fun p -> p.ProjectId)))
            let sourceMatches (r: WorkLegacyGitFenceReceipt) =
                let b = r.Binding
                if b.Scope = WorkLegacyGitScope.Account then
                    b.EntityId = account.AccountId
                    && b.LedgerId = account.LedgerId
                    && b.LedgerEpoch = account.LedgerEpoch
                    && b.PinnedHead = account.Head.PinnedCommit
                    && b.SourceHighWater = account.Head.SourceHighWater
                else
                    inspection.Projects |> List.exists (fun p ->
                        b.Scope = WorkLegacyGitScope.Project
                        && b.EntityId = p.ProjectId
                        && b.LedgerId = p.LedgerId
                        && b.LedgerEpoch = p.LedgerEpoch
                        && b.PinnedHead = p.Head.PinnedCommit
                        && b.SourceHighWater = p.Head.SourceHighWater)
            let validReceipt (r: WorkLegacyGitFenceReceipt) =
                let b = r.Binding
                sourceMatches r
                && b.DeviceId = proof.DeviceId
                && not (String.IsNullOrWhiteSpace b.TrustedRemoteId)
                && r.DecisionId = proof.ApprovalId
                && r.DecisionVersion > 0L
                && r.FenceOperationId <> Guid.Empty
                && r.ExclusiveReceiptId <> Guid.Empty
                && r.OriginalHead = b.PinnedHead
                && r.FencedHead <> b.PinnedHead
                && not (isNull r.FencedHead) && gitOid.IsMatch r.FencedHead
                && r.OriginalHighWater = b.SourceHighWater
                && r.OriginalHighWater < Int64.MaxValue
                && r.FenceHighWater = r.OriginalHighWater + 1L
                && r.WriterGeneration > 0L
                && r.NoNewOriginalMutations
                && r.DurableUntilExplicitCutover
                && r.OriginalPendingIntentHighWater >= 0L
                && r.OriginalSourceEffectHighWater >= 0L
                && r.MinimumClientProtocolVersion > 0
                && r.AllOriginalClientsQuiesced
                && digest r.AllClientQuiescenceProofSha256
                && digest r.AuditSha256
            let! () = ensure (all.Length = expectedIds.Count
                 && (all |> List.map (fun r -> r.Binding.EntityId) |> Set.ofList) = expectedIds
                 && (all |> List.forall validReceipt)
                 && (all |> List.map (fun r -> r.DecisionVersion) |> Set.ofList).Count = 1
                 && (all |> List.map (fun r -> r.FenceOperationId) |> Set.ofList).Count = 1
                 && (all |> List.map (fun r -> r.ExclusiveReceiptId) |> Set.ofList).Count = 1) "original writer/client/high-water quiescence cannot be proved"
            let referenceReceipt = all.Head
            let! () = ensure (audit.Version = 1L
                 && auditBody.Count = 9
                 && audit.Value.GraphVersion = 0L
                 && audit.Value.StageGeneration = 0L
                 && audit.Value.ClaimGeneration = 0L
                 && audit.Value.StopGeneration = 0L
                 && audit.Value.RoleVersion = 0L
                 && audit.Value.ReviewSha.IsNone && audit.Value.IntegrationSha.IsNone
                 && int32 "SchemaVersion" auditBody = Some 1
                 && str "Mode" auditBody = Some "c2-disposable-offline-only"
                 && str "DecisionId" auditBody = Some(proof.ApprovalId.ToString("D"))
                 && int64Prop "DecisionVersion" auditBody = Some referenceReceipt.DecisionVersion
                 && str "DeviceId" auditBody = Some(proof.DeviceId.ToString("D"))
                 && str "AccountId" auditBody = Some accountId
                 && str "ImportOperationId" auditBody = Some(proof.ImportOperationId.ToString("D"))
                 && str "FenceOperationId" auditBody = Some(referenceReceipt.FenceOperationId.ToString("D"))
                 && str "FenceOperationId" markerBody = Some(referenceReceipt.FenceOperationId.ToString("D"))
                 && int64Prop "FenceVersion" markerBody = Some referenceReceipt.DecisionVersion
                 && audit.Value.SourceSha = Some proof.AccountHead
                 && audit.Value.ExternalEffect = WorkSqliteEffect.Prepared) "independently fenced C2 audit is absent or mismatched"
            let! () =
                try
                    let entries =
                        auditBody["OriginalGitFences"].AsArray()
                        |> Seq.map (fun n -> n.AsObject())
                        |> Seq.toList
                    let entryMatches (r: WorkLegacyGitFenceReceipt) (e: JsonObject) =
                        let b = r.Binding
                        e.Count = 19
                        && str "Scope" e = Some(sprintf "%A" b.Scope)
                        && str "EntityId" e = Some(b.EntityId.ToString("D"))
                        && str "LedgerId" e = Some(b.LedgerId.ToString("D"))
                        && int32 "LedgerEpoch" e = Some b.LedgerEpoch
                        && str "DeviceId" e = Some(b.DeviceId.ToString("D"))
                        && str "TrustedRemoteId" e = Some b.TrustedRemoteId
                        && str "OriginalHead" e = Some r.OriginalHead
                        && str "FencedHead" e = Some r.FencedHead
                        && int64Prop "FenceHighWater" e = Some r.FenceHighWater
                        && int64Prop "WriterGeneration" e = Some r.WriterGeneration
                        && str "ExclusiveReceiptId" e = Some(r.ExclusiveReceiptId.ToString("D"))
                        && str "AuditSha256" e = Some r.AuditSha256
                        && boolean "NoNewOriginalMutations" e = Some r.NoNewOriginalMutations
                        && boolean "DurableUntilExplicitCutover" e = Some r.DurableUntilExplicitCutover
                        && int64Prop "OriginalPendingIntentHighWater" e = Some r.OriginalPendingIntentHighWater
                        && int64Prop "OriginalSourceEffectHighWater" e = Some r.OriginalSourceEffectHighWater
                        && int32 "MinimumClientProtocolVersion" e = Some r.MinimumClientProtocolVersion
                        && boolean "AllOriginalClientsQuiesced" e = Some r.AllOriginalClientsQuiesced
                        && str "AllClientQuiescenceProofSha256" e = Some r.AllClientQuiescenceProofSha256
                    ensure (entries.Length = all.Length
                        && (entries |> List.choose (str "EntityId") |> Set.ofList).Count = all.Length
                        && (all |> List.forall (fun r -> entries |> List.exists (entryMatches r))))
                        "C2 receipt set differs from its transaction-bound SQLite audit"
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
            let sourceHead = Some proof.SourceHead
            let! legacyQueue =
                originalRow store WorkSqliteRecordKind.Project id "legacy-queue"
                    (originalValue (OriginalC1.encode project.Queue) project.Graph.Version
                        0 0 0 0 sourceHead None None WorkSqliteEffect.NoneRecorded)
            let! legacyGraph =
                originalRow store WorkSqliteRecordKind.Project id "legacy-composition"
                    (originalValue (OriginalC1.encode project.Graph) project.Graph.Version
                        0 0 0 0 sourceHead None None WorkSqliteEffect.NoneRecorded)
            let! () = ensure (
                (project.Origins |> List.map (fun origin -> origin.WorkId) |> Set.ofList) =
                    (stages |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                && project.Origins.Length = stages.Count) "imported source inventory does not cover exactly the stage set"
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
                        let effect =
                            match stage.Push with
                            | Some push ->
                                match push.Outcome with
                                | PushOutcome.Unknown -> WorkSqliteEffect.Unknown
                                | PushOutcome.Observed _ -> WorkSqliteEffect.Observed
                                | PushOutcome.DefinitiveNoEffect -> WorkSqliteEffect.DefinitiveNoEffect
                            | None -> WorkSqliteEffect.NoneRecorded
                        let reviewSha =
                            project.Reviews |> List.tryFind (fun r -> r.WorkId = workId)
                            |> Option.map (fun r -> r.FrozenCommit)
                        let integrationSha =
                            project.Integrations |> List.tryFind (fun r -> r.WorkId = workId)
                            |> Option.map (fun r -> r.IntegratedCommit)
                        let original =
                            originalValue (OriginalC1.encode stage) project.Graph.Version
                                stage.StageGeneration stage.ClaimGeneration 0 0
                                (Some source.SourceCommit) reviewSha integrationSha effect
                        let! raw = read store WorkSqliteRecordKind.Stage id workId
                        let! originRow =
                            originalRow store WorkSqliteRecordKind.Work id ("origin/" + workId)
                                (originalValue (OriginalC1.encode source) source.GraphVersion
                                    source.StageGeneration 0 0 source.RoleVersion
                                    (Some source.SourceCommit) None None WorkSqliteEffect.NoneRecorded)
                        let rawJson = JsonNode.Parse(original.BodyJson).AsObject()
                        rawJson["state"] <- JsonValue.Create(stageState stage.State)
                        rawJson["queueId"] <- JsonValue.Create(stage.QueueId)
                        rawJson["gate"] <- JsonValue.Create(gateState stage.Gate)
                        rawJson["availableSequence"] <- JsonValue.Create(stage.AvailableSequence)
                        rawJson["proposalState"] <- JsonValue.Create(decision.State)
                        rawJson["decisionReceiptId"] <- JsonValue.Create(decision.DecisionReceiptId.ToString("D"))
                        rawJson["dependencies"] <- array stage.Dependencies
                        rawJson["waivedDependencies"] <- array (stage.WaivedDependencies |> Map.toSeq |> Seq.map fst)
                        let value =
                            { original with
                                BodyJson = rawJson.ToJsonString()
                                StopGeneration = proof.StopGeneration }
                        let! () = ensure (effect <> WorkSqliteEffect.Unknown
                             && effect <> WorkSqliteEffect.Prepared
                             && originalOrProjected raw original value) ("full original C1 stage, authorization, waiver or source differs: " + workId)
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
                        let original =
                            originalValue (OriginalC1.plain queues) 0 0 0 0 0
                                None None None WorkSqliteEffect.NoneRecorded
                        let rawJson = JsonNode.Parse(original.BodyJson).AsObject()
                        rawJson["state"] <- JsonValue.Create("Active")
                        rawJson["instructionsDigest"] <- JsonValue.Create(proofRole.InstructionsDigest)
                        rawJson["queues"] <- array queues
                        rawJson["evidenceReceiptId"] <- JsonValue.Create(proofRole.EvidenceReceiptId.ToString("D"))
                        let value =
                            { original with
                                BodyJson = rawJson.ToJsonString()
                                StopGeneration = proof.StopGeneration
                                RoleVersion = int64 proofRole.Version }
                        let! () = ensure (originalOrProjected raw original value) ("full original C1 subscription/role record changed: " + roleId)
                        let normalized = { raw with Version = 2L; Value = value }
                        return
                            { Id = roleId
                              Version = proofRole.Version
                              Queues = queues
                              InstructionsDigest = proofRole.InstructionsDigest
                              Stored = normalized }
                    })
            // Preserve every original C1 review, integration and operation
            // receipt in the same immutable-source CAS as the stage/role graph.
            let extras =
                [ for review in project.Reviews do
                    yield WorkSqliteRecordKind.Review, review.WorkId,
                        originalValue (OriginalC1.encode review) project.Graph.Version
                            0 0 0 0 (Some review.FrozenCommit) (Some review.FrozenCommit)
                            None WorkSqliteEffect.NoneRecorded
                  for integration in project.Integrations do
                    yield WorkSqliteRecordKind.Integration, integration.WorkId,
                        originalValue (OriginalC1.encode integration) project.Graph.Version
                            0 0 0 0 (Some integration.SourceCommit) None
                            (Some integration.IntegratedCommit) WorkSqliteEffect.Observed
                  for terminal in project.Terminals do
                    yield WorkSqliteRecordKind.Work, "terminal/" + terminal.LaunchId,
                        originalValue (OriginalC1.plain terminal) 0 0 0 0 0
                            None None None WorkSqliteEffect.NoneRecorded
                  for validation in project.Validations do
                    yield WorkSqliteRecordKind.Work, "validation/" + validation.EvidenceRef,
                        originalValue (OriginalC1.plain validation) 0 0 0 0 0
                            None None None WorkSqliteEffect.NoneRecorded
                  for operation in project.Operations do
                    yield WorkSqliteRecordKind.Work, "receipt/" + operation.OperationId.ToString("D"),
                        originalValue (OriginalC1.plain operation) 0 0 0 0 0
                            None None None WorkSqliteEffect.NoneRecorded ]
            let! immutableExtras =
                extras |> traverse (fun (kind, entity, expected) ->
                    originalRow store kind id entity expected)
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
                  yield! immutableExtras
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
                        let accountKey = proof.AccountId.ToString("D")
                        let! accountSnapshot =
                            originalRow store WorkSqliteRecordKind.Project accountKey "legacy-account"
                                (originalValue (OriginalC1.encode inspection.Account) 0 0 0 0 0
                                    (Some proof.AccountHead) None None WorkSqliteEffect.NoneRecorded)
                        let! accountReceipts =
                            inspection.Account.Operations |> traverse (fun original ->
                                originalRow store WorkSqliteRecordKind.Work accountKey
                                    ("receipt/" + original.OperationId.ToString("D"))
                                    (originalValue (OriginalC1.plain original) 0 0 0 0 0
                                        None None None WorkSqliteEffect.NoneRecorded))
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
                                (accountSnapshot :: (accountReceipts @ immutableFence))
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
