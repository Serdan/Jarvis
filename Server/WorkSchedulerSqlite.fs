namespace Jarvis.WorkQueues

open System
open System.Text.Json.Nodes
open Jarvis.WorkQueues.Contracts
open Server

// E0 only: a default-off SQLite scheduler domain adapter. The verified importer
// (Work/C) must supply complete, authenticated snapshots before this is wired
// to a host. This module never dispatches a browser, starts a model, performs a
// Git operation, or registers a live Scheduler.
type SqliteSchedulerRole =
    { Id: string
      Version: int
      Queues: Set<string>
      InstructionsDigest: string
      Stored: WorkSqliteRecord }

type SqliteSchedulerProject =
    { Id: string
      Queue: Project
      Graph: WorkCompositionGraph
      OptionalUnitIds: Set<string>
      Stored: WorkSqliteRecord
      StageRecords: Map<string, WorkSqliteRecord>
      RoleRecords: SqliteSchedulerRole list
      LaunchRecords: Map<string, WorkSqliteRecord> }

type SqliteSchedulerSnapshot =
    { Account: WorkSqliteCapacity
      Projects: SqliteSchedulerProject list
      // This is an importer attestation, NOT user/agent-supplied authority.
      // Until Work/C can prove it, no production host is permitted to call E0.
      CompleteProjectSet: bool }

type SqliteSchedulerOutcome =
    { Receipt: WorkSqliteReceipt
      ProjectId: string
      RoleId: string
      LaunchId: string
      WorkId: string
      WorkGeneration: int }

[<RequireQualifiedAccess>]
type SqliteSchedulerError =
    | Incomplete of string
    | NoCapacity
    | NoEligibleWork
    | BlockedByExternalEffect
    | Projection of WorkBlockingError
    | Storage of WorkSqliteFailure

[<RequireQualifiedAccess>]
module internal WorkSchedulerSqlite =
    let private fail reason = Error(SqliteSchedulerError.Incomplete reason)
    let private key kind project id =
        { ProjectId = project; Kind = kind; EntityId = id }

    let private stringProperty (name: string) (record: WorkSqliteRecord) =
        try
            let body = JsonNode.Parse(record.Value.BodyJson).AsObject()
            match body[name] with
            | null -> None
            | field -> Some(field.GetValue<string>())
        with _ -> None

    let private intProperty (name: string) (record: WorkSqliteRecord) =
        try
            let body = JsonNode.Parse(record.Value.BodyJson).AsObject()
            match body[name] with
            | null -> None
            | field -> Some(field.GetValue<int>())
        with _ -> None

    let private int64Property (name: string) (record: WorkSqliteRecord) =
        try
            let body = JsonNode.Parse(record.Value.BodyJson).AsObject()
            match body[name] with
            | null -> None
            | field -> Some(field.GetValue<int64>())
        with _ -> None

    let private boolProperty (name: string) (record: WorkSqliteRecord) =
        try
            let body = JsonNode.Parse(record.Value.BodyJson).AsObject()
            match body[name] with
            | null -> None
            | field -> Some(field.GetValue<bool>())
        with _ -> None

    let private stringSetProperty (name: string) (record: WorkSqliteRecord) =
        try
            let body = JsonNode.Parse(record.Value.BodyJson).AsObject()
            body[name].AsArray()
            |> Seq.map (fun x -> x.GetValue<string>()) |> Set.ofSeq |> Some
        with _ -> None

    let private changeString (name: string) (value: string option) (json: string) =
        let body = JsonNode.Parse(json).AsObject()
        body[name] <-
            match value with
            | Some text -> JsonValue.Create(text) :> JsonNode
            | None -> null
        body.ToJsonString()

    let private changeInt (name: string) (value: int) (json: string) =
        let body = JsonNode.Parse(json).AsObject()
        body[name] <- JsonValue.Create(value)
        body.ToJsonString()

    let private changeBool (name: string) (value: bool) (json: string) =
        let body = JsonNode.Parse(json).AsObject()
        body[name] <- JsonValue.Create(value)
        body.ToJsonString()

    let private changeSet (name: string) (value: Set<string>) (json: string) =
        let body = JsonNode.Parse(json).AsObject()
        let items = JsonArray()
        for item in value do items.Add(JsonValue.Create(item))
        body[name] <- items
        body.ToJsonString()

    let private stageState = function
        | WorkState.Available -> "Available"
        | WorkState.Blocked -> "Blocked"
        | WorkState.GateException -> "GateException"
        | WorkState.Claimed _ -> "Claimed"
        | WorkState.Completed -> "Completed"
        | WorkState.Cancelled -> "Cancelled"

    let private launchState = function
        | LaunchState.ReservedUnregistered -> "ReservedUnregistered"
        | LaunchState.SubmittedUnregistered _ -> "SubmittedUnregistered"
        | LaunchState.Registered _ -> "Registered"
        | LaunchState.DemandInvalidatedPendingTerminal _ -> "DemandInvalidatedPendingTerminal"
        | LaunchState.TerminalPendingRelease _ -> "TerminalPendingRelease"

    let private settled (record: WorkSqliteRecord) =
        record.Value.ExternalEffect <> WorkSqliteEffect.Unknown
        && record.Value.ExternalEffect <> WorkSqliteEffect.Prepared

    let private gateState = function
        | GateState.Clear -> "Clear"
        | GateState.CompositionHold _ -> "CompositionHold"
        | GateState.DependencyGatePending _ -> "DependencyGatePending"
        | GateState.IntegrationGateHold _ -> "IntegrationGateHold"
        | GateState.ExternalEffectObservedPendingGate _ -> "ExternalEffectObservedPendingGate"
        | GateState.SourceRecoveryBlocked _ -> "SourceRecoveryBlocked"

    let private guard (record: WorkSqliteRecord) =
        { Key = record.Key
          ExpectedVersion = record.Version
          GraphVersion = Some record.Value.GraphVersion
          StageGeneration = Some record.Value.StageGeneration
          ClaimGeneration = Some record.Value.ClaimGeneration
          StopGeneration = Some record.Value.StopGeneration
          RoleVersion = Some record.Value.RoleVersion
          SourceSha = record.Value.SourceSha }

    let private update (record: WorkSqliteRecord) (value: WorkSqliteRecordValue) =
        { Key = record.Key; ExpectedVersion = Some record.Version; Value = value }

    let private stageRecordValid (p: SqliteSchedulerProject) (stage: WorkStage) (record: WorkSqliteRecord) =
        record.Key = key WorkSqliteRecordKind.Stage p.Id stage.Id
        && record.Value.GraphVersion = int64 p.Graph.Version
        && record.Value.StageGeneration = int64 stage.StageGeneration
        && record.Value.ClaimGeneration = int64 stage.ClaimGeneration
        && record.Value.StopGeneration = p.Stored.Value.StopGeneration
        && stringProperty "state" record = Some(stageState stage.State)
        && stringProperty "queueId" record = Some stage.QueueId
        && stringProperty "gate" record = Some(gateState stage.Gate)
        && stringSetProperty "dependencies" record = Some stage.Dependencies
        && stringSetProperty "waivedDependencies" record =
            Some(stage.WaivedDependencies |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        && int64Property "availableSequence" record = Some stage.AvailableSequence
        && (match stage.SourceLineage with
            | None -> true
            | Some source ->
                record.Value.SourceSha =
                    Some(source.ContinuationSha |> Option.defaultValue source.PinnedBaseCommit))
        // Available is not permission to bypass Lead approval. The importer
        // must publish its verified Draft/Proposed/Open decision in the same
        // transaction as the stage; only Open can enter a runnable projection.
        && (stage.State <> WorkState.Available
            || stringProperty "proposalState" record = Some "Open")

    let private roleValid (p: SqliteSchedulerProject) (role: SqliteSchedulerRole) =
        role.Stored.Key = key WorkSqliteRecordKind.Role p.Id role.Id
        && role.Version > 0
        && not (String.IsNullOrWhiteSpace role.InstructionsDigest)
        && role.Stored.Value.RoleVersion = int64 role.Version
        && role.Stored.Value.StopGeneration = p.Stored.Value.StopGeneration
        && p.Queue.Subscriptions.TryFind role.Id = Some role.Queues
        && stringProperty "state" role.Stored = Some "Active"
        && stringProperty "instructionsDigest" role.Stored = Some role.InstructionsDigest
        && stringSetProperty "queues" role.Stored = Some role.Queues
        && settled role.Stored

    let private launchValid (p: SqliteSchedulerProject) (launch: Launch) (record: WorkSqliteRecord) =
        record.Key = key WorkSqliteRecordKind.Launch p.Id launch.Id
        && record.Value.StopGeneration = p.Stored.Value.StopGeneration
        && record.Value.RoleVersion = int64 launch.RoleVersion
        && stringProperty "state" record = Some(launchState launch.State)
        && stringProperty "roleId" record = Some launch.RoleId
        && boolProperty "everClaimed" record = Some launch.EverClaimed
        && boolProperty "stopRequested" record =
            Some(launch.Lease |> Option.exists (fun lease -> lease.StopRequested))
        && (match launch.Credit with
            | Some work -> stringProperty "credit" record = Some work
            | None -> stringProperty "credit" record = None)
        && (match launch.PrimaryWorkId with
            | Some work -> stringProperty "primaryWorkId" record = Some work
            | None -> stringProperty "primaryWorkId" record = None)
        // A credit may be reassigned at admission; a primary may be chosen at
        // claim. Either binding must carry that stage's actual provenance.
        && (match (if launch.EverClaimed then launch.PrimaryWorkId else launch.Credit) with
            | None -> true
            | Some workId ->
                match p.Queue.Works.TryFind workId, p.StageRecords.TryFind workId with
                | Some stage, Some stageRow ->
                    record.Value.StageGeneration = int64 stage.StageGeneration
                    && record.Value.SourceSha = stageRow.Value.SourceSha
                    && (not launch.EverClaimed
                        || record.Value.ClaimGeneration = int64 stage.ClaimGeneration)
                | _ -> false)
        && settled record

    let private verifyProject (p: SqliteSchedulerProject) =
        if String.IsNullOrWhiteSpace p.Id
           || p.Stored.Key <> key WorkSqliteRecordKind.Project p.Id "authority"
           || p.Stored.Value.GraphVersion <> int64 p.Graph.Version
           || p.Queue.GraphVersion <> p.Graph.Version
           || p.Stored.Value.StopGeneration < 0L
           || intProperty "queueVersion" p.Stored <> Some p.Queue.Version
           || stringProperty "coverage" p.Stored <> Some "Verified"
           || stringSetProperty "stageIds" p.Stored <> Some(p.Queue.Works |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
           || stringSetProperty "roleIds" p.Stored <> Some(p.Queue.Subscriptions |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
           || stringSetProperty "launchIds" p.Stored <> Some(p.Queue.Launches |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
           || not p.Queue.QueueCovered || not p.Queue.GraphCovered
           || not p.Queue.MessageCovered || not p.Queue.SourceCovered then
            fail ("unverified queue/graph authority: " + p.Id)
        elif p.StageRecords.Count <> p.Queue.Works.Count
             || p.LaunchRecords.Count <> p.Queue.Launches.Count
             || p.RoleRecords.Length <> p.Queue.Subscriptions.Count
             || (p.RoleRecords |> List.map (fun r -> r.Id) |> Set.ofList |> Set.count) <> p.RoleRecords.Length then
            fail ("incomplete stage, role or launch record set: " + p.Id)
        elif p.Queue.Works
             |> Map.exists (fun id stage ->
                 match p.StageRecords.TryFind id with
                 | None -> true
                 | Some record -> not (stageRecordValid p stage record)) then
            fail ("stage/source/proposal version mismatch: " + p.Id)
        elif p.Queue.Launches
             |> Map.exists (fun id launch ->
                 match p.LaunchRecords.TryFind id with
                 | None -> true
                 | Some record -> not (launchValid p launch record)) then
            fail ("unverified launch/STOP evidence: " + p.Id)
        elif p.RoleRecords |> List.exists (roleValid p >> not) then
            fail ("unverified role subscription/version: " + p.Id)
        else
            let scope =
                { ExpectedGraphVersion = p.Graph.Version
                  ExpectedProjectVersion = p.Queue.Version
                  OptionalUnitIds = p.OptionalUnitIds }
            WorkBlocking.project scope p.Graph p.Queue
            |> Result.mapError SqliteSchedulerError.Projection

    let private projectGuards (p: SqliteSchedulerProject) =
        [ yield guard p.Stored
          for record in p.StageRecords |> Map.toList |> List.map snd do
              yield guard record
          for role in p.RoleRecords do yield guard role.Stored
          for record in p.LaunchRecords |> Map.toList |> List.map snd do
              yield guard record ]

    let private runnableRole (p: SqliteSchedulerProject) role (stage: WorkStage) =
        role.Queues.Contains stage.QueueId
        && (match stage.Kind with
            // Until importer C provides a canonical message-handler SQLite row,
            // E0 cannot atomically mark ClaimedPrimary/handler generation alongside
            // the stage and launch. Refuse rather than ghost-claim Role messages.
            | WorkKind.RoleMessage _ -> false
            | _ -> true)

    let private liveUnclaimed (p: SqliteSchedulerProject) =
        p.Queue.Launches
        |> Map.toList
        |> List.map snd
        |> List.filter (fun launch ->
            not launch.EverClaimed
            && (match launch.State with
                | LaunchState.TerminalPendingRelease _
                | LaunchState.DemandInvalidatedPendingTerminal _ -> false
                | _ -> true))

    // WQ07 injective demand-credit matching, implemented entirely in memory.
    // The version-guarded SQLite commit is the *only* mutation authority.
    // A generalist cannot steal the last review demand from a review-only
    // reserved launch: every existing launch must remain matchable.
    let private matchExisting (p: SqliteSchedulerProject) (eligible: RankedWorkCandidate list)
                              (existing: Launch list) =
        let roles = p.RoleRecords |> List.map (fun role -> role.Id, role) |> Map.ofList
        let candidates (launch: Launch) =
            match roles.TryFind launch.RoleId with
            | Some role when role.Version = launch.RoleVersion ->
                eligible
                // Never count an unresolved one-shot effect as usable credit
                // when both admission and actual claim refuse that stage.
                |> List.filter (fun item ->
                    runnableRole p role item.Stage
                    && (p.StageRecords.TryFind item.Stage.Id |> Option.exists settled))
                |> List.map (fun item -> item.Stage.Id)
            | _ -> []
        let byId = existing |> List.map (fun l -> l.Id, l) |> Map.ofList
        let rec augment (launch: Launch) (seen: Set<string>) (owners: Map<string, string>) =
            candidates launch
            |> List.tryPick (fun workId ->
                if seen |> Set.contains workId then None
                else
                    let visited = Set.add workId seen
                    match owners.TryFind workId with
                    | None -> Some(owners.Add(workId, launch.Id))
                    | Some other ->
                        match augment byId.[other] visited owners with
                        | Some reassigned -> Some(reassigned.Add(workId, launch.Id))
                        | None -> None)
        let mutable owners = Map.empty
        for launch in existing |> List.sortBy (fun l -> l.Id) do
            match augment launch Set.empty owners with
            | Some matched -> owners <- matched
            | None -> ()
        let assignments =
            owners |> Map.toList |> List.map (fun (work, launch) -> launch, work) |> Map.ofList
        if assignments.Count = existing.Length then Some assignments else None

    let private rank (p: SqliteSchedulerProject, projection: WorkBlockingProjection) =
        projection.Eligible
        |> List.sortBy (fun item ->
            (if item.IsBlocking then 0 else 1),
            -(int64 item.LeadPriority),
            item.AvailableSequence, p.Id, item.Stage.Id)
        |> List.map (fun item -> p, item)

    let private withCredit (p: SqliteSchedulerProject) (record: WorkSqliteRecord) (credit: string) =
        let stage = p.StageRecords.[credit]
        { record.Value with
            BodyJson = changeString "credit" (Some credit) record.Value.BodyJson
            StageGeneration = stage.Value.StageGeneration
            SourceSha = stage.Value.SourceSha }

    let private accountMutation account delta =
        { AccountId = account.AccountId
          ExpectedVersion = Some account.Version
          Maximum = None
          ReservedDelta = delta }

    let private updateProject (p: SqliteSchedulerProject) (newLaunch: string option) =
        let nextBody =
            p.Stored.Value.BodyJson |> changeInt "queueVersion" (p.Queue.Version + 1)
        let nextBody =
            match newLaunch with
            | None -> nextBody
            | Some id ->
                p.Queue.Launches |> Map.toSeq |> Seq.map fst |> Set.ofSeq |> Set.add id
                |> fun ids -> changeSet "launchIds" ids nextBody
        update p.Stored
            { p.Stored.Value with
                BodyJson = nextBody }

    let private verifySnapshot (snapshot: SqliteSchedulerSnapshot) =
        if not snapshot.CompleteProjectSet || snapshot.Projects.IsEmpty
           || snapshot.Account.Maximum < 0 || snapshot.Account.Reserved < 0
           || snapshot.Account.Reserved > snapshot.Account.Maximum
           || (snapshot.Projects |> List.collect (fun p -> p.Queue.Launches |> Map.toList |> List.map fst)
               |> fun ids -> (ids |> Set.ofList |> Set.count) <> ids.Length)
           || (snapshot.Projects |> List.map (fun p -> p.Id) |> Set.ofList |> Set.count)
              <> snapshot.Projects.Length then
            fail "account project set or capacity snapshot is incomplete"
        else
            let results =
                snapshot.Projects |> List.map (fun p -> verifyProject p |> Result.map (fun v -> p, v))
            match results |> List.tryPick (function Error e -> Some e | _ -> None) with
            | Some e -> Error e
            | None -> Ok(results |> List.choose (function Ok x -> Some x | _ -> None))

    let private newLaunchBody (launch: Launch) (operationId: Guid) (workGeneration: int) (accountId: string) =
        let body = JsonObject()
        body["state"] <- JsonValue.Create("ReservedUnregistered")
        body["roleId"] <- JsonValue.Create(launch.RoleId)
        body["credit"] <- (launch.Credit |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode) |> Option.defaultValue null)
        body["primaryWorkId"] <- null
        body["everClaimed"] <- JsonValue.Create(false)
        body["stopRequested"] <- JsonValue.Create(false)
        // Bind the original reservation result, not a later re-ranked credit
        // or claim, to the immutable SQLite operation receipt.
        body["reservationOperationId"] <- JsonValue.Create(operationId.ToString("D"))
        body["reservationAccountId"] <- JsonValue.Create(accountId)
        body["reservationWorkId"] <- JsonValue.Create(launch.Credit.Value)
        body["reservationWorkGeneration"] <- JsonValue.Create(workGeneration)
        body.ToJsonString()

    /// E0 admission: rank globally by D, preserve existing specialist credits,
    /// then atomically reserve the account token and create the project launch.
    /// The immutable caller operationId is also the SQLite replay receipt.
    let private reserveFresh (store: WorkSqliteAuthority) (snapshot: SqliteSchedulerSnapshot)
                (operationId: Guid) launchId : Result<SqliteSchedulerOutcome, SqliteSchedulerError> =
        if operationId = Guid.Empty || String.IsNullOrWhiteSpace launchId then
            fail "original operation/launch identity is missing"
        else
            match verifySnapshot snapshot with
            | Error e -> Error e
            | Ok verified when verified |> List.exists (fun (p, _) -> p.Queue.Launches.ContainsKey launchId) ->
                fail "launch identity is already reserved in another project or generation"
            | Ok _ when snapshot.Account.Reserved >= snapshot.Account.Maximum ->
                Error SqliteSchedulerError.NoCapacity
            | Ok verified ->
                let all =
                    verified |> List.collect rank
                    |> List.sortBy (fun (p, item) ->
                        (if item.IsBlocking then 0 else 1),
                        -(int64 item.LeadPriority),
                        item.AvailableSequence, p.Id, item.Stage.Id)
                let selection =
                    all |> List.tryPick (fun (p, item) ->
                        match p.StageRecords.TryFind item.Stage.Id with
                        | None -> None
                        | Some stageRecord when not (settled stageRecord) -> None
                        | Some stageRecord ->
                            p.RoleRecords
                            |> List.sortBy (fun role -> role.Queues.Count, role.Id)
                            |> List.tryPick (fun role ->
                                if not (runnableRole p role item.Stage) then None
                                else
                                    let others = liveUnclaimed p
                                    let remaining =
                                        verified |> List.find (fun (q, _) -> q.Id = p.Id) |> snd
                                        |> fun projection ->
                                            projection.Eligible
                                            |> List.filter (fun other -> other.Stage.Id <> item.Stage.Id)
                                    match matchExisting p remaining others with
                                    | None -> None
                                    | Some credits ->
                                        Some(p, item, stageRecord, role, credits)))
                match selection with
                | None -> Error SqliteSchedulerError.NoEligibleWork
                | Some(p, item, stage, role, credits) ->
                    let launch =
                        { Id = launchId
                          ProjectId = p.Id
                          RoleId = role.Id
                          RoleVersion = role.Version
                          AccountTokenId = "token:" + launchId
                          State = LaunchState.ReservedUnregistered
                          Lease = None
                          Version = 0
                          Credit = Some item.Stage.Id
                          PrimaryWorkId = None
                          EverClaimed = false
                          AncillaryMessages = Set.empty
                          DispatchCount = 0 }
                    let launchRow =
                        { Key = key WorkSqliteRecordKind.Launch p.Id launchId
                          ExpectedVersion = None
                          Value =
                            { BodyJson = newLaunchBody launch operationId item.Stage.StageGeneration snapshot.Account.AccountId
                              GraphVersion = int64 p.Graph.Version
                              StageGeneration = int64 item.Stage.StageGeneration
                              ClaimGeneration = 0L
                              StopGeneration = p.Stored.Value.StopGeneration
                              RoleVersion = int64 role.Version
                              SourceSha = stage.Value.SourceSha
                              ReviewSha = None
                              IntegrationSha = None
                              ExternalEffect = WorkSqliteEffect.NoneRecorded } }
                    let recredits =
                        [ for other in liveUnclaimed p do
                            let next = credits.[other.Id]
                            if other.Credit <> Some next then
                                let current = p.LaunchRecords.[other.Id]
                                yield update current (withCredit p current next) ]
                    let command =
                        { OperationId = operationId
                          Guards = verified |> List.collect (fst >> projectGuards)
                          Records = updateProject p (Some launchId) :: launchRow :: recredits
                          Capacities = [ accountMutation snapshot.Account 1 ] }
                    match store.Commit command with
                    | Error error -> Error(SqliteSchedulerError.Storage error)
                    | Ok receipt ->
                        Ok { Receipt = receipt
                             ProjectId = p.Id; RoleId = role.Id; LaunchId = launchId
                             WorkId = item.Stage.Id
                             WorkGeneration = item.Stage.StageGeneration }

    /// A registered launch gets at most one primary claim. This does NOT
    /// release account capacity or execute external effects.
    let private claimFresh (store: WorkSqliteAuthority) (p: SqliteSchedulerProject)
              (operationId: Guid) launchId : Result<SqliteSchedulerOutcome, SqliteSchedulerError> =
        if operationId = Guid.Empty then fail "claim operation ID missing"
        else
            match verifyProject p with
            | Error e -> Error e
            | Ok projection ->
                match p.Queue.Launches.TryFind launchId,
                      p.LaunchRecords.TryFind launchId with
                | Some launch, Some launchRow when
                    not launch.EverClaimed && launch.PrimaryWorkId.IsNone &&
                    (match launch.State, launch.Lease with
                     | LaunchState.Registered _, Some lease -> not lease.StopRequested
                     | _ -> false) ->
                    match p.RoleRecords |> List.tryFind (fun r ->
                        r.Id = launch.RoleId && r.Version = launch.RoleVersion) with
                    | None -> fail "launch's pinned role has changed"
                    | Some role ->
                        let candidates =
                            projection.Eligible
                            |> List.filter (fun item ->
                                runnableRole p role item.Stage
                                && p.StageRecords.TryFind item.Stage.Id
                                   |> Option.exists settled)
                        let others = liveUnclaimed p |> List.filter (fun l -> l.Id <> launchId)
                        let choice =
                            candidates |> List.tryPick (fun item ->
                                let remaining =
                                    projection.Eligible
                                    |> List.filter (fun other -> other.Stage.Id <> item.Stage.Id)
                                match matchExisting p remaining others with
                                | Some credits -> Some(item, credits)
                                | None -> None)
                        match choice with
                        | None -> Error SqliteSchedulerError.NoEligibleWork
                        | Some(item, credits) ->
                            let stage = p.StageRecords.[item.Stage.Id]
                            let claimed =
                                { stage.Value with
                                    BodyJson =
                                        stage.Value.BodyJson
                                        |> changeString "state" (Some "Claimed")
                                        |> changeString "claimLaunch" (Some launchId)
                                    ClaimGeneration = stage.Value.ClaimGeneration + 1L }
                            let nextLaunch =
                                { launchRow.Value with
                                    BodyJson =
                                        launchRow.Value.BodyJson
                                        |> changeString "credit" None
                                        |> changeString "primaryWorkId" (Some item.Stage.Id)
                                        |> changeBool "everClaimed" true
                                        |> changeString "claimOperationId" (Some(operationId.ToString("D")))
                                        |> changeInt "claimWorkGeneration" item.Stage.StageGeneration
                                    ClaimGeneration = stage.Value.ClaimGeneration + 1L
                                    StageGeneration = stage.Value.StageGeneration
                                    SourceSha = stage.Value.SourceSha }
                            let recredits =
                                [ for other in others do
                                    let next = credits.[other.Id]
                                    if other.Credit <> Some next then
                                        let current = p.LaunchRecords.[other.Id]
                                        yield update current (withCredit p current next) ]
                            let command =
                                { OperationId = operationId
                                  Guards = projectGuards p
                                  Records =
                                    [ updateProject p None
                                      update stage claimed
                                      update launchRow nextLaunch ] @ recredits
                                  Capacities = [] }
                            match store.Commit command with
                            | Error error -> Error(SqliteSchedulerError.Storage error)
                            | Ok receipt ->
                                Ok { Receipt = receipt
                                     ProjectId = p.Id
                                     RoleId = role.Id
                                     LaunchId = launchId
                                     WorkId = item.Stage.Id
                                     WorkGeneration = item.Stage.StageGeneration }
                | _ -> fail "requires registered, unstopped, never-claimed launch"

    // A retry after store reopen must resolve the original committed outcome
    // before current eligibility, account capacity, or EverClaimed can reject it.
    // An unrelated receipt (including a changed launch identity) fails closed.
    let private replayOutcome (store: WorkSqliteAuthority) (operationId: Guid)
                              (launchId: string) (projectIds: string list)
                              (operationField: string) (workField: string)
                              (generationField: string) (requirePrimary: bool) (accountId: string option) =
        match store.ReadReceipt operationId with
        | Error failure -> Error(SqliteSchedulerError.Storage failure)
        | Ok None -> Ok None
        | Ok(Some receipt) ->
            let rec locate remaining found =
                match remaining with
                | [] -> Ok found
                | projectId :: rest ->
                    match store.ReadRecord (key WorkSqliteRecordKind.Launch projectId launchId) with
                    | Error failure -> Error(SqliteSchedulerError.Storage failure)
                    | Ok None -> locate rest found
                    | Ok(Some row) ->
                        match found with
                        | Some _ -> fail "duplicate launch identity across replay projects"
                        | None -> locate rest (Some(projectId, row))
            match locate (projectIds |> List.distinct) None with
            | Error failure -> Error failure
            | Ok(Some(projectId, row)) when
                stringProperty operationField row = Some(operationId.ToString("D"))
                && (accountId |> Option.forall (fun expected ->
                    stringProperty "reservationAccountId" row = Some expected)) ->
                match stringProperty "roleId" row,
                      stringProperty workField row,
                      intProperty generationField row with
                | Some roleId, Some workId, Some generation when
                    generation > 0
                    && (not requirePrimary
                        || (boolProperty "everClaimed" row = Some true
                            && stringProperty "primaryWorkId" row = Some workId)) ->
                    Ok(Some
                        { Receipt = receipt
                          ProjectId = projectId
                          RoleId = roleId
                          LaunchId = launchId
                          WorkId = workId
                          WorkGeneration = generation })
                | _ -> fail "applied receipt has missing or inconsistent launch result provenance"
            | _ -> Error(SqliteSchedulerError.Storage(WorkSqliteFailure.OperationIdReused operationId))

    let reserve (store: WorkSqliteAuthority) (snapshot: SqliteSchedulerSnapshot)
                (operationId: Guid) launchId : Result<SqliteSchedulerOutcome, SqliteSchedulerError> =
        if operationId = Guid.Empty || String.IsNullOrWhiteSpace launchId then
            fail "original operation/launch identity is missing"
        elif not snapshot.CompleteProjectSet then
            fail "account project set is incomplete"
        else
            match replayOutcome store operationId launchId
                    (snapshot.Projects |> List.map (fun p -> p.Id))
                    "reservationOperationId" "reservationWorkId" "reservationWorkGeneration"
                    false (Some snapshot.Account.AccountId) with
            | Error failure -> Error failure
            | Ok(Some original) -> Ok original
            | Ok None -> reserveFresh store snapshot operationId launchId

    let claim (store: WorkSqliteAuthority) (p: SqliteSchedulerProject)
              (operationId: Guid) launchId : Result<SqliteSchedulerOutcome, SqliteSchedulerError> =
        if operationId = Guid.Empty || String.IsNullOrWhiteSpace launchId then
            fail "claim operation/launch identity is missing"
        else
            match replayOutcome store operationId launchId [ p.Id ]
                    "claimOperationId" "primaryWorkId" "claimWorkGeneration" true None with
            | Error failure -> Error failure
            | Ok(Some original) -> Ok original
            | Ok None -> claimFresh store p operationId launchId
