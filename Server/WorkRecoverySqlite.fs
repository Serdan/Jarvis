namespace Jarvis.WorkRecovery

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Server
open Jarvis.WorkQueues

// G1a is a default-off, disposable-fixture adapter. In particular, callers must
// not equate these caller-provided snapshots with an authenticated F1 backup
// witness. This file exposes no live host route, restore, launch or grant.
type SqliteRecoveryProjectRows =
    { ProjectId: string
      Project: WorkSqliteRecord
      Stages: Map<string, WorkSqliteRecord>
      Roles: WorkSqliteRecord list
      Launches: WorkSqliteRecord list }

type SqliteRecoverySource =
    { Account: WorkSqliteCapacity
      Projects: SqliteRecoveryProjectRows list
      CompleteProjectSet: bool }

[<RequireQualifiedAccess>]
type SqliteRecoveryFailure =
    | Incomplete of string
    | Storage of WorkSqliteFailure

type SqliteRecoverySeedResult =
    { Receipt: WorkSqliteReceipt
      ProjectIds: string list
      Plan: RecoveryPlan }

[<RequireQualifiedAccess>]
module internal WorkRecoverySqlite =
    let fromScheduler (snapshot: SqliteSchedulerSnapshot) : SqliteRecoverySource =
        { Account = snapshot.Account
          CompleteProjectSet = snapshot.CompleteProjectSet
          Projects =
            snapshot.Projects
            |> List.map (fun p ->
                { ProjectId = p.Id
                  Project = p.Stored
                  Stages = p.StageRecords
                  Roles = p.RoleRecords |> List.map (fun r -> r.Stored)
                  Launches = p.LaunchRecords |> Map.toList |> List.map snd }) }

    let private key project =
        { ProjectId = project
          Kind = WorkSqliteRecordKind.ExternalEffect
          EntityId = "recovery:authority" }

    let private field (name: string) (value: WorkSqliteRecord) =
        try
            match JsonNode.Parse(value.Value.BodyJson).AsObject()[name] with
            | null -> None
            | node -> Some(node.GetValue<string>())
        with _ -> None

    let private fail message = Error(SqliteRecoveryFailure.Incomplete message)

    let private guard (record: WorkSqliteRecord) =
        { Key = record.Key
          ExpectedVersion = record.Version
          GraphVersion = Some record.Value.GraphVersion
          StageGeneration = Some record.Value.StageGeneration
          ClaimGeneration = Some record.Value.ClaimGeneration
          StopGeneration = Some record.Value.StopGeneration
          RoleVersion = Some record.Value.RoleVersion
          SourceSha = record.Value.SourceSha }

    let private changeString (name: string) (value: string) (json: string) =
        let body = JsonNode.Parse(json).AsObject()
        body[name] <- JsonValue.Create(value)
        body.ToJsonString()

    let private update record body =
        { Key = record.Key
          ExpectedVersion = Some record.Version
          Value = { record.Value with BodyJson = body } }

    // A digest of the whole *captured* pre-fence input, not just receipt IDs.
    // It binds replay to the original account/project/Work/role/launch/source
    // versions and all negative effect evidence. It is not a proof attestation.
    let private fingerprint (source: SqliteRecoverySource) (input: RecoveryInput) =
        let canonical =
            sprintf "%A\n%A"
                input
                { source with Projects = source.Projects |> List.sortBy (fun p -> p.ProjectId) }
        SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private validate (source: SqliteRecoverySource) (input: RecoveryInput) =
        let sourceProjects = source.Projects |> List.map (fun p -> p.ProjectId)
        let inputProjects = input.Projects |> List.map (fun p -> p.ProjectId)
        if not source.CompleteProjectSet || sourceProjects.IsEmpty
           || sourceProjects.Length <> (sourceProjects |> Set.ofList |> Set.count)
           || inputProjects.Length <> (inputProjects |> Set.ofList |> Set.count)
           || Set.ofList sourceProjects <> Set.ofList inputProjects then
            fail "unverified or unequal complete account project set"
        elif source.Account.Version <= 0L || source.Account.Maximum < 0
             || source.Account.Reserved < 0 || source.Account.Reserved > source.Account.Maximum
             || String.IsNullOrWhiteSpace source.Account.AccountId
             || input.SchemaVersion <> 1 || input.SnapshotDatabaseSequence < 0L then
            fail "invalid account or backup generation"
        else
            let bad =
                source.Projects |> List.tryPick (fun p ->
                    let state = input.Projects |> List.find (fun x -> x.ProjectId = p.ProjectId)
                    let ids = state.Works |> List.map (fun w -> w.WorkId)
                    let rows = p.Stages |> Map.toList |> List.map fst
                    if p.Project.Key <>
                        { ProjectId = p.ProjectId; Kind = WorkSqliteRecordKind.Project; EntityId = "authority" }
                       || p.Project.Value.GraphVersion <> int64 state.GraphVersion
                       || field "coverage" p.Project <> Some "Verified"
                       || p.Project.Version < 1L
                       || ids.Length <> (ids |> Set.ofList |> Set.count)
                       || Set.ofList ids <> Set.ofList rows then
                        Some("project/stage authority mismatch: " + p.ProjectId)
                    // Prepared is legitimate in-flight evidence, not corrupt
                    // authority. The CAS must durably fence it and siblings
                    // without converting the original effect to terminal.
                    elif state.Works |> List.exists (fun w ->
                        let row = p.Stages.[w.WorkId]
                        row.Key <> { ProjectId = p.ProjectId; Kind = WorkSqliteRecordKind.Stage; EntityId = w.WorkId }
                        || row.Version < 1L
                        || row.Value.GraphVersion <> int64 state.GraphVersion
                        || row.Value.StageGeneration <> int64 w.Generation
                        || row.Value.StopGeneration <> p.Project.Value.StopGeneration
                        || (field "state" row |> Option.isNone)
                        || (field "gate" row |> Option.isNone)) then
                        Some("stage, STOP, generation or effect mismatch: " + p.ProjectId)
                    elif (p.Roles @ p.Launches) |> List.exists (fun r ->
                        r.Key.ProjectId <> p.ProjectId || r.Version < 1L
                        || r.Value.StopGeneration <> p.Project.Value.StopGeneration) then
                        Some("role/launch STOP mismatch: " + p.ProjectId)
                    else None)
            match bad with
            | Some message -> fail message
            | None -> Ok ()

    let private rowGuarded records = records |> List.map guard

    let private projectRows (p: SqliteRecoveryProjectRows) =
        [ p.Project ] @ (p.Stages |> Map.toList |> List.map snd) @ p.Roles @ p.Launches

    let private fenceBody (operationId: Guid) (digest: string) (input: RecoveryInput) (plan: RecoveryPlan)
                          (project: SqliteRecoveryProjectRows) =
        let json = JsonObject()
        json["state"] <- JsonValue.Create("Recovery")
        json["operationId"] <- JsonValue.Create(operationId.ToString("D"))
        json["requestDigest"] <- JsonValue.Create(digest)
        json["snapshotDatabaseSequence"] <- JsonValue.Create(input.SnapshotDatabaseSequence)
        json["originalProjectVersion"] <- JsonValue.Create(project.Project.Version)
        let queue = JsonArray()
        for item in plan.RecoveryQueue do
            if item.ProjectId = project.ProjectId || item.ProjectId = "(global)" then
                let entry = JsonObject()
                entry["id"] <- JsonValue.Create(item.Id)
                entry["projectId"] <- JsonValue.Create(item.ProjectId)
                entry["workId"] <- (item.WorkId |> Option.map (fun x -> JsonValue.Create(x) :> JsonNode) |> Option.defaultValue null)
                entry["queue"] <- JsonValue.Create("Recovery")
                entry["requirements"] <- JsonValue.Create(sprintf "%A" item.Requirements)
                queue.Add(entry)
        json["recoveryQueue"] <- queue
        // The exact original stage bodies are retained for a future verified
        // G1b release; no release is authorized merely by this sealed copy.
        let originals = JsonObject()
        for KeyValue(id, row) in project.Stages do
            let original = JsonObject()
            original["version"] <- JsonValue.Create(row.Version)
            original["bodyJson"] <- JsonValue.Create(row.Value.BodyJson)
            original["externalEffect"] <- JsonValue.Create(sprintf "%A" row.Value.ExternalEffect)
            originals[id] <- original
        json["originalStages"] <- originals
        // Preserve the original captured launch identities separately from
        // journal observations. A newer unrelated terminal launch cannot
        // reconcile an in-flight identity lost by the restored snapshot.
        let captured = JsonArray()
        for work in (input.Projects |> List.find (fun p -> p.ProjectId = project.ProjectId)).Works do
            for launch in work.CapturedLaunches do
                let entry = JsonObject()
                entry["projectId"] <- JsonValue.Create(launch.ProjectId)
                entry["workId"] <- JsonValue.Create(launch.WorkId)
                entry["generation"] <- JsonValue.Create(launch.Generation)
                entry["operationId"] <- JsonValue.Create(launch.OperationId)
                captured.Add(entry)
        json["originalCapturedLaunches"] <- captured
        json.ToJsonString()

    // One atomic SQLite CAS invalidates every pre-restore Scheduler projection
    // and all future admissions in these projects. The "coverage" downgrade
    // makes even a *fresh* direct E0 Scheduler snapshot fail closed. Every stage
    // also changes to Blocked/SourceRecoveryBlocked; uncertainty is not erased.
    // A future verified release must restore coverage and stage gates in ONE
    // guarded transaction, never by trusting G0's caller-supplied bool flags.
    let seed (store: WorkSqliteAuthority) (source: SqliteRecoverySource)
             (input: RecoveryInput) (operationId: Guid)
             : Result<SqliteRecoverySeedResult, SqliteRecoveryFailure> =
        if operationId = Guid.Empty then
            fail "missing original fencing operation identity"
        else
            match validate source input with
            | Error reason -> Error reason
            | Ok () ->
                let plan = WorkRecovery.plan input
                let digest = fingerprint source input
                match store.ReadReceipt operationId with
                | Error err -> Error(SqliteRecoveryFailure.Storage err)
                | Ok(Some receipt) ->
                    let already =
                        source.Projects |> List.forall (fun p ->
                            match store.ReadRecord (key p.ProjectId) with
                            | Ok(Some row) ->
                                field "operationId" row = Some(operationId.ToString("D"))
                                && field "requestDigest" row = Some digest
                            | _ -> false)
                    if not already then
                        Error(SqliteRecoveryFailure.Storage(WorkSqliteFailure.OperationIdReused operationId))
                    else
                        Ok { Receipt = receipt
                             ProjectIds = source.Projects |> List.map (fun p -> p.ProjectId) |> List.sort
                             Plan = plan }
                | Ok None ->
                    let mutations =
                        [ for p in source.Projects do
                            let body =
                                p.Project.Value.BodyJson
                                |> changeString "coverage" "Recovery"
                                |> changeString "recoveryOperationId" (operationId.ToString("D"))
                                |> changeString "recoveryDigest" digest
                            yield update p.Project body
                            for KeyValue(_, stage) in p.Stages do
                                let locked =
                                    stage.Value.BodyJson
                                    |> changeString "state" "Blocked"
                                    |> changeString "gate" "SourceRecoveryBlocked"
                                yield update stage locked
                            yield
                                { Key = key p.ProjectId
                                  ExpectedVersion = None
                                  Value =
                                    { BodyJson = fenceBody operationId digest input plan p
                                      GraphVersion = p.Project.Value.GraphVersion
                                      StageGeneration = 0L
                                      ClaimGeneration = 0L
                                      StopGeneration = p.Project.Value.StopGeneration
                                      RoleVersion = 0L
                                      SourceSha = None
                                      ReviewSha = None
                                      IntegrationSha = None
                                      ExternalEffect = WorkSqliteEffect.Unknown } }]
                    let command =
                        { OperationId = operationId
                          Guards = source.Projects |> List.collect (projectRows >> rowGuarded)
                          Records = mutations
                          // The zero-delta capacity CAS fences concurrent E0
                          // reserve; a stale capacity snapshot cannot reserve.
                          Capacities =
                            [ { AccountId = source.Account.AccountId
                                ExpectedVersion = Some source.Account.Version
                                Maximum = None
                                ReservedDelta = 0 } ] }
                    match store.Commit command with
                    | Error failure -> Error(SqliteRecoveryFailure.Storage failure)
                    | Ok receipt ->
                        Ok { Receipt = receipt
                             ProjectIds = source.Projects |> List.map (fun p -> p.ProjectId) |> List.sort
                             Plan = plan }

    let readFence (store: WorkSqliteAuthority) projectId =
        store.ReadRecord(key projectId)
