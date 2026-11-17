namespace Jarvis.WorkQueues

open System
open System.Collections
open System.IO
open System.Text.Json.Nodes
open Microsoft.FSharp.Reflection
open Server

// C1 offline-only fixture adapter. This is deliberately not a production
// migration/cutover entrypoint. A real old-writer CAS fence and independently
// authenticated trust anchors must be implemented before any live import.
type WorkLegacyImportOfflineFence =
    { AccountHead: string
      ProjectHeads: (Guid * string) list
      FenceVersion: int64
      FenceOperationId: Guid }

[<RequireQualifiedAccess>]
type WorkLegacyImportSqliteGap =
    | PreflightRejected of WorkLegacyImportGap list
    | UnverifiedWriterFence of string
    | UnsafeDestination of string
    | StorageFailure of WorkSqliteFailure

type WorkLegacyImportOfflineReceipt =
    { ImportOperationId: Guid
      AccountId: Guid
      SourceAccountHead: string
      ProjectHeads: (Guid * string) list
      SqliteSequence: int64
      Replayed: bool }

module internal WorkLegacyImportSqlite =
    [<Literal>]
    let DisposableMarker = ".work-legacy-import-disposable"

    [<Literal>]
    let DisposableMarkerContent = "JARVIS_WORK_LEGACY_IMPORT_DISPOSABLE_V1"

    // This compiler-local codec preserves the *full* legacy typed snapshot,
    // including F# unions, maps, sets and option cases. Never map an absent
    // decision to an approved state when serializing.
    module private SnapshotJson =
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

    let private verifiedFixturePath (databasePath: string) =
        try
            if String.IsNullOrWhiteSpace databasePath
               || not (Path.IsPathFullyQualified databasePath) then
                false
            else
                let absolute = Path.GetFullPath databasePath
                let folder = Path.GetDirectoryName absolute
                let marker = Path.Combine(folder, DisposableMarker)
                let temporary = Path.GetFullPath(Path.GetTempPath())
                let prefix = temporary.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
                let isWithinTemporary =
                    absolute.StartsWith(prefix, StringComparison.Ordinal)
                let rec noLinkedComponents (path: string) =
                    if String.IsNullOrEmpty path then true
                    else
                        // LinkTarget also detects dangling symlinks that neither
                        // File.Exists nor Directory.Exists would report.
                        let fileLink = FileInfo(path).LinkTarget
                        let directoryLink = DirectoryInfo(path).LinkTarget
                        isNull fileLink && isNull directoryLink
                        && (if File.Exists path || Directory.Exists path then
                                not ((File.GetAttributes path).HasFlag FileAttributes.ReparsePoint)
                            else true)
                        && noLinkedComponents (Path.GetDirectoryName path)
                isWithinTemporary
                && not (Directory.Exists absolute)
                && Directory.Exists folder
                && File.Exists marker
                && File.ReadAllText marker = DisposableMarkerContent
                && noLinkedComponents folder
                && noLinkedComponents marker
                && noLinkedComponents absolute
        with _ -> false

    let private verifyFence (input: WorkLegacyImportInspection)
                            (index: WorkLegacyImportEvidenceIndex)
                            (fence: WorkLegacyImportOfflineFence) =
        let pinned = index.Projects |> List.map (fun (p, _, head, _, _) -> p, head) |> List.sort
        if fence.FenceOperationId = Guid.Empty || fence.FenceVersion <= 0L
           || fence.AccountHead <> index.AccountHead
           || fence.ProjectHeads |> List.sort <> pinned then
            Error(WorkLegacyImportSqliteGap.UnverifiedWriterFence
                "Fenced original ledger identities, heads and version must match the immutable preflight.")
        elif input.Projects |> List.exists (fun p ->
            p.Queue.Migration <> MigrationEpoch.Frozen) then
            Error(WorkLegacyImportSqliteGap.UnverifiedWriterFence
                "Legacy project is not in its pre-cutover Frozen phase.")
        else Ok ()

    let private defaultValue json graph stage claim stop role source review integration effect : WorkSqliteRecordValue =
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

    let private makeRecord projectId kind entityId value =
        { Key = { ProjectId = projectId; Kind = kind; EntityId = entityId }
          ExpectedVersion = None
          Value = value }

    let private buildTransaction importOperationId
                                 (inspection: WorkLegacyImportInspection)
                                 (index: WorkLegacyImportEvidenceIndex)
                                 (fence: WorkLegacyImportOfflineFence) =
        let accountId = inspection.Account.AccountId.ToString("D")
        let plain (json: string) =
            let container = JsonObject()
            container.Add("LegacyValue", JsonNode.Parse json)
            defaultValue (container.ToJsonString()) 0 0 0 0 0 None None None WorkSqliteEffect.NoneRecorded
        let one (project: Guid) kind entity value = makeRecord (project.ToString("D")) kind entity value
        let accountSnapshot =
            one inspection.Account.AccountId WorkSqliteRecordKind.Project "legacy-account"
                (defaultValue (SnapshotJson.encode inspection.Account) 0 0 0 0 0
                              (Some inspection.Account.Head.PinnedCommit) None None WorkSqliteEffect.NoneRecorded)
        let fenceJson =
            JsonObject()
        fenceJson.Add("Mode", JsonValue.Create("offline-only"))
        fenceJson.Add("SchemaVersion", JsonValue.Create(1))
        fenceJson.Add("FenceVersion", JsonValue.Create(fence.FenceVersion))
        fenceJson.Add("FenceOperationId", JsonValue.Create(fence.FenceOperationId.ToString("D")))
        fenceJson.Add("AccountHead", JsonValue.Create(fence.AccountHead))
        let projects = JsonArray()
        for projectId, head in fence.ProjectHeads |> List.sort do
            let source = JsonObject()
            source.Add("ProjectId", JsonValue.Create(projectId.ToString("D")))
            source.Add("Head", JsonValue.Create(head))
            projects.Add(source)
        fenceJson.Add("ProjectHeads", projects)
        let frozenMarker =
            one inspection.Account.AccountId WorkSqliteRecordKind.Project "legacy-import-fence"
                (defaultValue (fenceJson.ToJsonString()) 0 0 0 0 0
                              (Some inspection.Account.Head.PinnedCommit) None None WorkSqliteEffect.Prepared)
        let projectRecords =
            [ for project in inspection.Projects do
                let projectId = project.ProjectId
                let graphVersion = project.Graph.Version
                yield one projectId WorkSqliteRecordKind.Project "legacy-queue"
                    (defaultValue (SnapshotJson.encode project.Queue)
                        graphVersion 0 0 0 0 (Some project.Head.PinnedCommit) None None WorkSqliteEffect.NoneRecorded)
                yield one projectId WorkSqliteRecordKind.Project "legacy-composition"
                    (defaultValue (SnapshotJson.encode project.Graph)
                        graphVersion 0 0 0 0 (Some project.Head.PinnedCommit) None None WorkSqliteEffect.NoneRecorded)
                for KeyValue(workId, stage) in project.Queue.Works do
                    let origin = project.Origins |> List.tryFind (fun o -> o.WorkId = workId)
                    let sourceSha = origin |> Option.map (fun o -> o.SourceCommit)
                    let reviewSha = project.Reviews |> List.tryFind (fun r -> r.WorkId = workId)
                                             |> Option.map (fun r -> r.FrozenCommit)
                    let integrationSha = project.Integrations |> List.tryFind (fun i -> i.WorkId = workId)
                                             |> Option.map (fun i -> i.IntegratedCommit)
                    let effect =
                        match stage.Push with
                        | Some push ->
                            match push.Outcome with
                            | PushOutcome.Unknown -> WorkSqliteEffect.Unknown
                            | PushOutcome.Observed _ -> WorkSqliteEffect.Observed
                            | PushOutcome.DefinitiveNoEffect -> WorkSqliteEffect.DefinitiveNoEffect
                        | None -> WorkSqliteEffect.NoneRecorded
                    yield one projectId WorkSqliteRecordKind.Stage workId
                        (defaultValue (SnapshotJson.encode stage) graphVersion
                            stage.StageGeneration stage.ClaimGeneration 0 0
                            sourceSha reviewSha integrationSha effect)
                for KeyValue(launchId, launch) in project.Queue.Launches do
                    let stop = launch.Lease |> Option.map (fun lease -> lease.Generation) |> Option.defaultValue 0
                    yield one projectId WorkSqliteRecordKind.Launch launchId
                        (defaultValue (SnapshotJson.encode launch) graphVersion
                            0 0 stop launch.RoleVersion None None None WorkSqliteEffect.NoneRecorded)
                for KeyValue(roleId, subscriptions) in project.Queue.Subscriptions do
                    yield one projectId WorkSqliteRecordKind.Role roleId
                        (plain (SnapshotJson.encode subscriptions))
                for review in project.Reviews do
                    yield one projectId WorkSqliteRecordKind.Review review.WorkId
                        (defaultValue (SnapshotJson.encode review)
                            graphVersion 0 0 0 0 (Some review.FrozenCommit) (Some review.FrozenCommit)
                            None WorkSqliteEffect.NoneRecorded)
                for integration in project.Integrations do
                    yield one projectId WorkSqliteRecordKind.Integration integration.WorkId
                        (defaultValue (SnapshotJson.encode integration)
                            graphVersion 0 0 0 0 (Some integration.SourceCommit) None
                            (Some integration.IntegratedCommit) WorkSqliteEffect.Observed)
                for origin in project.Origins do
                    yield one projectId WorkSqliteRecordKind.Work ("origin/" + origin.WorkId)
                        (defaultValue (SnapshotJson.encode origin)
                            origin.GraphVersion origin.StageGeneration 0 0 origin.RoleVersion
                            (Some origin.SourceCommit) None None WorkSqliteEffect.NoneRecorded)
                for terminal in project.Terminals do
                    yield one projectId WorkSqliteRecordKind.Work ("terminal/" + terminal.LaunchId)
                        (plain (SnapshotJson.encode terminal))
                for validation in project.Validations do
                    yield one projectId WorkSqliteRecordKind.Work ("validation/" + validation.EvidenceRef)
                        (plain (SnapshotJson.encode validation))
                for operation in project.Operations do
                    yield one projectId WorkSqliteRecordKind.Work ("receipt/" + operation.OperationId.ToString("D"))
                        (plain (SnapshotJson.encode operation)) ]
        let accountOperations =
            [ for operation in inspection.Account.Operations do
                yield one inspection.Account.AccountId WorkSqliteRecordKind.Work
                          ("receipt/" + operation.OperationId.ToString("D"))
                          (plain (SnapshotJson.encode operation)) ]
        { OperationId = importOperationId
          Guards = []
          Records = accountSnapshot :: frozenMarker :: (accountOperations @ projectRecords)
          Capacities = [ { AccountId = accountId; ExpectedVersion = None
                           Maximum = Some inspection.Account.MaxConcurrent
                           ReservedDelta = index.ReservedCapacity } ] }

    // Hermetic-only conversion. A snapshot with the right strings but no
    // externally enforced old-writer fence is NOT production migration proof.
    // No production route calls this and the disposable marker is mandatory.
    let commitOfflineFixture (databasePath: string) (importOperationId: Guid)
                             (inspection: WorkLegacyImportInspection)
                             (fence: WorkLegacyImportOfflineFence)
                             : Result<WorkLegacyImportOfflineReceipt, WorkLegacyImportSqliteGap> =
        if not (verifiedFixturePath databasePath) then
            Error(WorkLegacyImportSqliteGap.UnsafeDestination databasePath)
        else
            match WorkLegacyImportPreflight.inspect inspection with
            | Error gaps -> Error(WorkLegacyImportSqliteGap.PreflightRejected gaps)
            | Ok index ->
                match verifyFence inspection index fence with
                | Error gap -> Error gap
                | Ok () ->
                    let authority = WorkSqliteAuthority databasePath
                    authority.Initialize()
                    |> Result.mapError WorkLegacyImportSqliteGap.StorageFailure
                    |> Result.bind (fun _ ->
                        buildTransaction importOperationId inspection index fence
                        |> authority.Commit
                        |> Result.mapError WorkLegacyImportSqliteGap.StorageFailure)
                    |> Result.map (fun receipt ->
                        { ImportOperationId = receipt.OperationId
                          AccountId = index.AccountId
                          SourceAccountHead = index.AccountHead
                          ProjectHeads = index.Projects |> List.map (fun (p, _, h, _, _) -> p, h)
                          SqliteSequence = receipt.Sequence
                          Replayed = receipt.Replayed })
