namespace Jarvis.WorkQueues

open System
open System.Text.Json.Nodes
open Server

// E1a: a durable, default-off one-shot boundary. This is NOT the dispatch
// capability: the C2 old-writer fence and an operator cutover are still absent.
// All inputs here are selectors, never attestations or launch authorization.
type SqliteSchedulerDispatchIntent =
    { AccountId: string
      ProjectId: string
      LaunchId: string
      WorkId: string
      RoleId: string
      RoleVersion: int64
      StageGeneration: int64
      StopGeneration: int64
      RepositoryId: string
      SourceRef: string
      SourceSha: string
      SourceHead: string
      ProjectHead: string
      ReservationOperationId: Guid
      PrepareOperationId: Guid
      Effect: WorkSqliteEffect
      Receipt: WorkSqliteReceipt option }

[<RequireQualifiedAccess>]
type SqliteSchedulerDispatchError =
    | MissingOrInvalidEvidence of string
    | PendingExternalEffect
    | UnverifiedWriterFence
    | Storage of WorkSqliteFailure

module internal WorkSchedulerSqliteDispatch =
    type private ResultExpression() =
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Bind(value, f) = Result.bind f value

    let private result = ResultExpression()
    let private invalid text = Error(SqliteSchedulerDispatchError.MissingOrInvalidEvidence text)
    let private ensure condition text = if condition then Ok () else invalid text

    let private key kind project id =
        { ProjectId = project; Kind = kind; EntityId = id }

    let private read (store: WorkSqliteAuthority) kind project id =
        store.ReadRecord(key kind project id)
        |> Result.mapError SqliteSchedulerDispatchError.Storage
        |> Result.bind (function
            | Some row -> Ok row
            | None -> invalid ("missing durable " + string kind + " " + project + "/" + id))

    let private receipt (store: WorkSqliteAuthority) operationId =
        store.ReadReceipt operationId
        |> Result.mapError SqliteSchedulerDispatchError.Storage

    let private json (row: WorkSqliteRecord) =
        try Ok(JsonNode.Parse(row.Value.BodyJson).AsObject())
        with _ -> invalid ("invalid durable record " + row.Key.EntityId)

    let private optionalString (name: string) (body: JsonObject) =
        try
            match body[name] with
            | null -> None
            | value -> Some(value.GetValue<string>())
        with _ -> None

    let private requiredString (name: string) (body: JsonObject) =
        match optionalString name body with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> invalid ("missing durable " + name)

    let private requiredInt (name: string) (body: JsonObject) =
        try
            match body[name] with
            | null -> invalid ("missing durable " + name)
            | value -> Ok(value.GetValue<int>())
        with _ -> invalid ("invalid durable " + name)

    let private requiredInt64 (name: string) (body: JsonObject) =
        try
            match body[name] with
            | null -> invalid ("missing durable " + name)
            | value -> Ok(value.GetValue<int64>())
        with _ -> invalid ("invalid durable " + name)

    let private requiredBool (name: string) (body: JsonObject) =
        try
            match body[name] with
            | null -> invalid ("missing durable " + name)
            | value -> Ok(value.GetValue<bool>())
        with _ -> invalid ("invalid durable " + name)

    let private hasString name expected body = optionalString name body = Some expected
    let private identifier name value =
        ensure (not (String.IsNullOrWhiteSpace value) && value = value.Trim()
                && value.Length <= 256) ("invalid " + name)

    let private guard (row: WorkSqliteRecord) =
        { Key = row.Key
          ExpectedVersion = row.Version
          GraphVersion = Some row.Value.GraphVersion
          StageGeneration = Some row.Value.StageGeneration
          ClaimGeneration = Some row.Value.ClaimGeneration
          StopGeneration = Some row.Value.StopGeneration
          RoleVersion = Some row.Value.RoleVersion
          SourceSha = row.Value.SourceSha }

    let private changed (row: WorkSqliteRecord) value =
        { Key = row.Key; ExpectedVersion = Some row.Version; Value = value }

    let private addString (name: string) (value: string) (json: string) =
        let body = JsonNode.Parse(json).AsObject()
        body[name] <- JsonValue.Create(value)
        body.ToJsonString()

    let private sourceRecordBody
            (accountId: string) (projectId: string) (launchId: string)
            (workId: string) (roleId: string) (roleVersion: int64)
            (stageGeneration: int64) (stopGeneration: int64)
            (repositoryId: string) (sourceRef: string) (sourceSha: string)
            (sourceHead: string) (projectHead: string)
            (reservationId: Guid) (prepareId: Guid) =
        let body = JsonObject()
        let set (name: string) (value: string) = body[name] <- JsonValue.Create(value)
        set "schema" "WorkSchedulerSqliteDispatch/E1a/v1"
        set "accountId" accountId
        set "projectId" projectId
        set "launchId" launchId
        set "workId" workId
        set "roleId" roleId
        body["roleVersion"] <- JsonValue.Create(roleVersion)
        body["stageGeneration"] <- JsonValue.Create(stageGeneration)
        body["stopGeneration"] <- JsonValue.Create(stopGeneration)
        set "repositoryId" repositoryId
        set "sourceRef" sourceRef
        set "sourceSha" sourceSha
        set "sourceHead" sourceHead
        set "projectHead" projectHead
        set "reservationOperationId" (reservationId.ToString("D"))
        set "prepareOperationId" (prepareId.ToString("D"))
        body["uncertaintyOperationId"] <- null
        body.ToJsonString()

    // The importer leaves a Prepared, offline-only marker. The marker's Git
    // heads are evidence of the *old* snapshot, not evidence of quiescent Git
    // writers or permission to actually launch anything.
    let private importedHead accountId projectId
                             (fence: WorkSqliteRecord) (legacy: WorkSqliteRecord) =
        result {
            let! fenceJson = json fence
            let! mode = requiredString "Mode" fenceJson
            let! accountHead = requiredString "AccountHead" fenceJson
            let! version = requiredInt64 "FenceVersion" fenceJson
            let! operation = requiredString "FenceOperationId" fenceJson
            let validOperation, operationGuid = Guid.TryParse operation
            let! () = ensure
                            (mode = "offline-only" && fence.Value.ExternalEffect = WorkSqliteEffect.Prepared
                             && version > 0L && validOperation && operationGuid <> Guid.Empty
                             && fence.Value.SourceSha = Some accountHead)
                            "verified offline import marker required"
            let! projectHead =
                try
                    fenceJson["ProjectHeads"].AsArray()
                    |> Seq.choose (fun node ->
                        try
                            let item = node.AsObject()
                            match optionalString "ProjectId" item, optionalString "Head" item with
                            | Some id, Some head when id = projectId -> Some head
                            | _ -> None
                        with _ -> None)
                    |> Seq.toList
                    |> function
                        | [ head ] -> Ok head
                        | _ -> invalid "import project head is missing or ambiguous"
                with _ -> invalid "invalid import project-head inventory"
            let! () = ensure (legacy.Value.SourceSha = Some projectHead
                              && legacy.Key = key WorkSqliteRecordKind.Project projectId "legacy-queue")
                             "imported project source head does not match the frozen inventory"
            return projectHead
        }

    let private validateFresh (store: WorkSqliteAuthority) accountId projectId launchId prepareId =
        result {
            let! () = identifier "account" accountId
            let! () = identifier "project" projectId
            let! () = identifier "launch" launchId
            let! () = ensure (prepareId <> Guid.Empty) "nonempty prepare operation required"
            let! capacity =
                store.ReadCapacity accountId
                |> Result.mapError SqliteSchedulerDispatchError.Storage
                |> Result.bind (function Some value -> Ok value | None -> invalid "account capacity is missing")
            let! () = ensure (capacity.Reserved > 0 && capacity.Reserved <= capacity.Maximum)
                                "a durable outstanding account reservation is required"
            let! project = read store WorkSqliteRecordKind.Project projectId "authority"
            let! fence = read store WorkSqliteRecordKind.Project accountId "legacy-import-fence"
            let! legacy = read store WorkSqliteRecordKind.Project projectId "legacy-queue"
            let! projectHead = importedHead accountId projectId fence legacy
            let! launch = read store WorkSqliteRecordKind.Launch projectId launchId
            let! launchJson = json launch
            let! reservationAccountId = requiredString "reservationAccountId" launchJson
            let! () = ensure (reservationAccountId = accountId)
                                "reservation does not belong to the requested global account"
            let! reservationIdText = requiredString "reservationOperationId" launchJson
            let validReservation, reservationId = Guid.TryParse reservationIdText
            let! () = ensure (validReservation && reservationId <> Guid.Empty
                              && reservationId <> prepareId)
                             "verified, distinct original reservation receipt required"
            let! originalReceipt = receipt store reservationId
            let! () = ensure originalReceipt.IsSome "original reservation receipt is absent"
            let! workId = requiredString "reservationWorkId" launchJson
            let! generation = requiredInt "reservationWorkGeneration" launchJson
            let! roleId = requiredString "roleId" launchJson
            let! everClaimed = requiredBool "everClaimed" launchJson
            let! stopRequested = requiredBool "stopRequested" launchJson
            let! () = ensure
                            (hasString "state" "ReservedUnregistered" launchJson
                             && hasString "credit" workId launchJson
                             && optionalString "primaryWorkId" launchJson = None
                             && not everClaimed && not stopRequested
                             && launch.Value.ExternalEffect = WorkSqliteEffect.NoneRecorded)
                             "only an unclaimed, unsubmitted and unstopped launch is preparable"
            let! stage = read store WorkSqliteRecordKind.Stage projectId workId
            let! stageJson = json stage
            let! role = read store WorkSqliteRecordKind.Role projectId roleId
            let! roleJson = json role
            let! origin = read store WorkSqliteRecordKind.Work projectId ("origin/" + workId)
            let! originJson = json origin
            let! repositoryId = requiredString "RepositoryId" originJson
            let! sourceRef = requiredString "SourceRef" originJson
            let! sourceSha = requiredString "SourceCommit" originJson
            let! sourceHead = requiredString "SourceHead" originJson
            let! sourceWorkId = requiredString "WorkId" originJson
            let! sourceRoleId = requiredString "RoleId" originJson
            let! originGeneration = requiredInt "StageGeneration" originJson
            let! originRoleVersion = requiredInt "RoleVersion" originJson
            let! originGraphVersion = requiredInt "GraphVersion" originJson
            let! ancestry = requiredBool "SourceAncestryVerified" originJson
            let! () = ensure
                            (sourceWorkId = workId && sourceRoleId = roleId
                             && originGeneration = generation
                             && originRoleVersion = int launch.Value.RoleVersion
                             && originGraphVersion >= 0 && int64 originGraphVersion <= project.Value.GraphVersion
                             && ancestry
                             && origin.Value.SourceSha = Some sourceSha
                             && stage.Value.SourceSha = Some sourceSha
                             && launch.Value.SourceSha = Some sourceSha
                             && sourceRef.StartsWith("refs/", StringComparison.Ordinal)
                             && repositoryId.Length <= 256
                             && (sourceHead.Length = 40 || sourceHead.Length = 64))
                             "original Git source/ref/generation lacks authenticated import provenance"
            let! () = ensure
                            (hasString "coverage" "Verified" (JsonNode.Parse(project.Value.BodyJson).AsObject())
                             && project.Value.GraphVersion = stage.Value.GraphVersion
                             && stage.Value.StageGeneration = int64 generation
                             && launch.Value.StageGeneration = int64 generation
                             && project.Value.StopGeneration = stage.Value.StopGeneration
                             && stage.Value.StopGeneration = role.Value.StopGeneration
                             && role.Value.StopGeneration = launch.Value.StopGeneration
                             && project.Value.StopGeneration >= 0L
                             && launch.Value.RoleVersion > 0L
                             && role.Value.RoleVersion = launch.Value.RoleVersion
                             && role.Value.ExternalEffect = WorkSqliteEffect.NoneRecorded
                             && stage.Value.ExternalEffect <> WorkSqliteEffect.Unknown
                             && stage.Value.ExternalEffect <> WorkSqliteEffect.Prepared
                             && hasString "state" "Available" stageJson
                             && hasString "gate" "Clear" stageJson
                             && hasString "proposalState" "Open" stageJson
                             && hasString "state" "Active" roleJson)
                             "role, STOP, stage or approval authority changed since reservation"
            let! stageQueue = requiredString "queueId" stageJson
            let! () =
                try
                    roleJson["queues"].AsArray()
                    |> Seq.exists (fun queue -> queue.GetValue<string>() = stageQueue)
                    |> fun found -> ensure found "pinned role cannot execute the source queue"
                with _ -> invalid "missing verified role queue subscriptions"
            return capacity, project, fence, legacy, launch, stage, role, origin,
                   projectHead, workId, roleId, repositoryId, sourceRef,
                   sourceSha, sourceHead, reservationId
        }

    let private readEffect (store: WorkSqliteAuthority) accountId projectId launchId =
        result {
            let! effect = read store WorkSqliteRecordKind.ExternalEffect projectId launchId
            let! body = json effect
            let! () = ensure
                            (hasString "schema" "WorkSchedulerSqliteDispatch/E1a/v1" body
                             && hasString "accountId" accountId body
                             && hasString "projectId" projectId body
                             && hasString "launchId" launchId body)
                            "dispatch journal does not match its durable identity"
            let! workId = requiredString "workId" body
            let! roleId = requiredString "roleId" body
            let! repositoryId = requiredString "repositoryId" body
            let! sourceRef = requiredString "sourceRef" body
            let! sourceSha = requiredString "sourceSha" body
            let! sourceHead = requiredString "sourceHead" body
            let! projectHead = requiredString "projectHead" body
            let! roleVersion = requiredInt64 "roleVersion" body
            let! generation = requiredInt64 "stageGeneration" body
            let! stop = requiredInt64 "stopGeneration" body
            let! reservationText = requiredString "reservationOperationId" body
            let! prepareText = requiredString "prepareOperationId" body
            let goodReservation, reservationId = Guid.TryParse reservationText
            let goodPrepare, prepareId = Guid.TryParse prepareText
            let! () = ensure (goodReservation && goodPrepare
                              && reservationId <> Guid.Empty && prepareId <> Guid.Empty
                              && repositoryId.Length <= 256
                              && sourceRef.StartsWith("refs/", StringComparison.Ordinal)
                              && (sourceSha.Length = 40 || sourceSha.Length = 64)
                              && (sourceHead.Length = 40 || sourceHead.Length = 64)
                              && (projectHead.Length = 40 || projectHead.Length = 64)
                              && effect.Value.SourceSha = Some sourceSha
                              && effect.Value.StageGeneration = generation
                              && effect.Value.StopGeneration = stop
                              && effect.Value.RoleVersion = roleVersion
                              && (effect.Value.ExternalEffect = WorkSqliteEffect.Prepared
                                  || effect.Value.ExternalEffect = WorkSqliteEffect.Unknown))
                             "corrupted or unsupported external-effect journal"
            let! reservationReceipt = receipt store reservationId
            let! prepareReceipt = receipt store prepareId
            let! () = ensure (reservationReceipt.IsSome && prepareReceipt.IsSome)
                                "journal lacks authenticated original reservation/prepare receipts"
            let! launch = read store WorkSqliteRecordKind.Launch projectId launchId
            let! launchBody = json launch
            let! () = ensure
                            (hasString "reservationAccountId" accountId launchBody
                             && hasString "reservationOperationId" (reservationId.ToString("D")) launchBody
                             && hasString "reservationWorkId" workId launchBody
                             && launch.Value.ExternalEffect = effect.Value.ExternalEffect
                             && launch.Value.SourceSha = Some sourceSha
                             && launch.Value.StageGeneration = generation
                             && launch.Value.StopGeneration = stop
                             && launch.Value.RoleVersion = roleVersion
                             && hasString "dispatchPrepareOperationId" (prepareId.ToString("D")) launchBody)
                             "launch and external-effect journals disagree"
            let! originalGeneration = requiredInt "reservationWorkGeneration" launchBody
            let! () = ensure (int64 originalGeneration = generation
                              && hasString "roleId" roleId launchBody)
                                "journal launch role or original generation is inconsistent"
            let! unknownReceiptValid =
                if effect.Value.ExternalEffect = WorkSqliteEffect.Unknown then
                    match optionalString "uncertaintyOperationId" body with
                    | Some value ->
                        match Guid.TryParse value with
                        | true, id when id <> Guid.Empty ->
                            receipt store id |> Result.map Option.isSome
                        | _ -> Ok false
                    | None -> Ok false
                else Ok true
            let! () = ensure unknownReceiptValid
                                "unknown external effect lacks an authenticated receipt"
            return effect, launch,
                   { AccountId = accountId; ProjectId = projectId; LaunchId = launchId
                     WorkId = workId; RoleId = roleId; RoleVersion = roleVersion
                     StageGeneration = generation; StopGeneration = stop
                     RepositoryId = repositoryId; SourceRef = sourceRef
                     SourceSha = sourceSha; SourceHead = sourceHead; ProjectHead = projectHead
                     ReservationOperationId = reservationId; PrepareOperationId = prepareId
                     Effect = effect.Value.ExternalEffect; Receipt = None }
        }

    let readIntent (store: WorkSqliteAuthority) accountId projectId launchId =
        readEffect store accountId projectId launchId
        |> Result.map (fun (_, _, intent) -> intent)

    /// Durable prepare is a local database transaction, not an actual dispatch.
    /// A different prepare UUID can never reissue the same launch or take a new
    /// global credit. The original reservation receipt is always checked.
    let prepare (store: WorkSqliteAuthority) accountId projectId launchId prepareId =
        result {
            let! () = ensure (prepareId <> Guid.Empty) "nonempty prepare operation required"
            let! oldReceipt = receipt store prepareId
            match oldReceipt with
            | Some prior ->
                let! _, _, original = readEffect store accountId projectId launchId
                let! () = ensure (original.PrepareOperationId = prepareId)
                                    "operation receipt belongs to a different launch"
                return { original with Receipt = Some prior }
            | None ->
                let! (capacity, project, fence, legacy, launch, stage, role, origin,
                      projectHead, workId, roleId, repositoryId, sourceRef,
                      sourceSha, sourceHead, reservationId) =
                    validateFresh store accountId projectId launchId prepareId
                let body =
                    sourceRecordBody accountId projectId launchId workId roleId
                        launch.Value.RoleVersion launch.Value.StageGeneration
                        launch.Value.StopGeneration repositoryId sourceRef sourceSha
                        sourceHead projectHead reservationId prepareId
                let effect =
                    { Key = key WorkSqliteRecordKind.ExternalEffect projectId launchId
                      ExpectedVersion = None
                      Value =
                        { launch.Value with
                            BodyJson = body
                            ExternalEffect = WorkSqliteEffect.Prepared } }
                let launchBody =
                    launch.Value.BodyJson
                    |> addString "dispatchPrepareOperationId" (prepareId.ToString("D"))
                let pending =
                    { launch.Value with BodyJson = launchBody
                                        ExternalEffect = WorkSqliteEffect.Prepared }
                let transaction =
                    { OperationId = prepareId
                      Guards = [ project; fence; legacy; launch; stage; role; origin ]
                               |> List.map guard
                      Records = [ effect; changed launch pending ]
                      // Zero delta is still a versioned global-capacity CAS.
                      // No credit is released on preparing or on uncertainty.
                      Capacities =
                        [ { AccountId = accountId; ExpectedVersion = Some capacity.Version
                            Maximum = None; ReservedDelta = 0 } ] }
                let! recorded = store.Commit transaction
                                |> Result.mapError SqliteSchedulerDispatchError.Storage
                let! _, _, intent = readEffect store accountId projectId launchId
                return { intent with Receipt = Some recorded }
        }

    /// A durable conservative transition BEFORE any future external transport
    /// can be attempted. A crash after this write must not retry dispatch.
    /// No external transport is called here. Even a definite no-effect result
    /// will later require authenticated evidence from the E1b reconciler.
    let markUnknown (store: WorkSqliteAuthority) accountId projectId launchId operationId =
        result {
            let! () = ensure (operationId <> Guid.Empty) "nonempty uncertainty operation required"
            let! pastReceipt = receipt store operationId
            let! effect, launch, intent = readEffect store accountId projectId launchId
            let! () = ensure (operationId <> intent.PrepareOperationId)
                                "prepare and uncertainty operations must be distinct"
            let! effectBody = json effect
            match pastReceipt with
            | Some original ->
                let! () = ensure
                                (intent.Effect = WorkSqliteEffect.Unknown
                                 && hasString "uncertaintyOperationId" (operationId.ToString("D")) effectBody)
                                "uncertainty receipt does not belong to this launch"
                return { intent with Receipt = Some original }
            | None ->
                let! () = ensure (intent.Effect = WorkSqliteEffect.Prepared)
                                    "unknown effect cannot be reissued with a new operation"
                let unknownEffect =
                    { effect.Value with
                        BodyJson = addString "uncertaintyOperationId" (operationId.ToString("D")) effect.Value.BodyJson
                        ExternalEffect = WorkSqliteEffect.Unknown }
                let unknownLaunch =
                    { launch.Value with ExternalEffect = WorkSqliteEffect.Unknown }
                let! recorded =
                    store.Commit
                        { OperationId = operationId
                          Guards = [ guard effect; guard launch ]
                          Records = [ changed effect unknownEffect; changed launch unknownLaunch ]
                          Capacities = [] }
                    |> Result.mapError SqliteSchedulerDispatchError.Storage
                return { intent with Effect = WorkSqliteEffect.Unknown; Receipt = Some recorded }
        }

    /// C2's independently accepted, integrated writer-quiescence witness and
    /// the operator's cutover grant are not part of E1a. Intentionally no
    /// callback, browser, process or provider API is reachable from this code.
    let refuseExternalDispatch (_: SqliteSchedulerDispatchIntent) : Result<unit, SqliteSchedulerDispatchError> =
        Error SqliteSchedulerDispatchError.UnverifiedWriterFence
