module WorkSchedulerSqliteTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open NUnit.Framework
open Server
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts

let private ok = function
    | Ok result -> result
    | Error failure -> Assert.Fail(sprintf "Unexpected failure: %A" failure); Unchecked.defaultof<_>

let private withStore f =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-sqlite-scheduler-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    let path = Path.Combine(folder, "work.sqlite")
    try
        let store = WorkSqliteAuthority(path)
        store.Initialize() |> ok |> ignore
        f store path
    finally
        Directory.Delete(folder, true)

let private key kind project id =
    { ProjectId = project; Kind = kind; EntityId = id }

let private arr (values: seq<string>) =
    let result = JsonArray()
    for value in values do result.Add(JsonValue.Create(value))
    result

let private setString (body: JsonObject) (name: string) (value: string) =
    body[name] <- JsonValue.Create(value)

let private setInt (body: JsonObject) (name: string) (value: int) =
    body[name] <- JsonValue.Create(value)

let private setBool (body: JsonObject) (name: string) (value: bool) =
    body[name] <- JsonValue.Create(value)

let private record project kind id (body: JsonObject) graph stage claim stop role =
    { Key = key kind project id
      Version = 1L
      Value =
        { BodyJson = body.ToJsonString()
          GraphVersion = int64 graph
          StageGeneration = int64 stage
          ClaimGeneration = int64 claim
          StopGeneration = int64 stop
          RoleVersion = int64 role
          SourceSha = None
          ReviewSha = None
          IntegrationSha = None
          ExternalEffect = WorkSqliteEffect.NoneRecorded } }

let private root id =
    let spec =
        { Title = "root-" + id
          Goal = "Verified project"
          Scope = "Synthetic isolated test"
          AcceptanceCriteria = Set.singleton "Independent proof"
          Constraints = Set.empty
          LinkedTaskId = None }
    WorkComposition.apply WorkComposition.empty.Version
        (WorkCompositionMutation.Create("root-" + id, WorkUnitKind.Project, None, spec))
        WorkComposition.empty |> ok

let private stage project id queue sequence state dependencies : WorkStage =
    { Id = id
      ProjectId = project
      SubjectId = "task-" + id
      Kind = if queue = "review" then WorkKind.FrozenReview else WorkKind.Implementation
      QueueId = queue
      StageGeneration = 1
      State = state
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = dependencies
      WaivedDependencies = Map.empty
      AvailableAt = sequence
      AvailableSequence = sequence
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private launch project id role state credit : Launch =
    { Id = id
      ProjectId = project
      RoleId = role
      RoleVersion = 1
      AccountTokenId = "token:" + id
      State = state
      Lease =
        match state with
        | LaunchState.Registered _ ->
            Some { LastSeenAt = 1L; Generation = 1; StopRequested = false }
        | _ -> None
      Version = 1
      Credit = credit
      PrimaryWorkId = None
      EverClaimed = false
      AncillaryMessages = Set.empty
      DispatchCount = 0 }

let private stateText = function
    | WorkState.Available -> "Available"
    | WorkState.Blocked -> "Blocked"
    | WorkState.Claimed _ -> "Claimed"
    | WorkState.Completed -> "Completed"
    | WorkState.Cancelled -> "Cancelled"
    | WorkState.GateException -> "GateException"

let private launchStateText = function
    | LaunchState.Registered _ -> "Registered"
    | LaunchState.ReservedUnregistered -> "ReservedUnregistered"
    | LaunchState.SubmittedUnregistered _ -> "SubmittedUnregistered"
    | LaunchState.TerminalPendingRelease _ -> "TerminalPendingRelease"
    | LaunchState.DemandInvalidatedPendingTerminal _ -> "DemandInvalidatedPendingTerminal"

let private makeProject id (graph: WorkCompositionGraph) (stages: WorkStage list)
                        (roles: (string * Set<string>) list) (launches: Launch list)
                        (priorities: (string * int) list) : SqliteSchedulerProject =
    let subscriptions = roles |> Map.ofList
    let priorityReceipts =
        priorities
        |> List.mapi (fun i (work, priority) ->
            "decision-" + string i,
            ProjectOperationPayload.AssignPriority
                { WorkId = work
                  Priority = priority
                  Reason = "Verified Lead test priority"
                  ProjectVersion = 0 })
        |> Map.ofList
    let queue =
        { Contracts.emptyProject subscriptions with
            GraphVersion = graph.Version
            Works = stages |> List.map (fun s -> s.Id, s) |> Map.ofList
            Launches = launches |> List.map (fun l -> l.Id, l) |> Map.ofList
            Receipts = priorityReceipts
            NextAvailability = stages |> List.map (fun s -> s.AvailableSequence) |> List.max }
    let projectBody = JsonObject()
    setInt projectBody "queueVersion" queue.Version
    setString projectBody "coverage" "Verified"
    projectBody["stageIds"] <- arr (queue.Works |> Map.toSeq |> Seq.map fst)
    projectBody["roleIds"] <- arr (queue.Subscriptions |> Map.toSeq |> Seq.map fst)
    projectBody["launchIds"] <- arr (queue.Launches |> Map.toSeq |> Seq.map fst)
    let projectRecord = record id WorkSqliteRecordKind.Project "authority" projectBody graph.Version 0 0 0 0
    let stages =
        stages
        |> List.map (fun s ->
            let body = JsonObject()
            setString body "state" (stateText s.State)
            setString body "gate" "Clear"
            setString body "queueId" s.QueueId
            setString body "proposalState" "Open"
            body["availableSequence"] <- JsonValue.Create(s.AvailableSequence)
            body["dependencies"] <- arr s.Dependencies
            body["waivedDependencies"] <- arr (s.WaivedDependencies |> Map.toSeq |> Seq.map fst)
            s.Id, record id WorkSqliteRecordKind.Stage s.Id body graph.Version s.StageGeneration s.ClaimGeneration 0 0)
        |> Map.ofList
    let roles =
        roles
        |> List.map (fun (role, queues) ->
            let body = JsonObject()
            setString body "state" "Active"
            setString body "instructionsDigest" (role + "-digest")
            body["queues"] <- arr queues
            { Id = role
              Version = 1
              Queues = queues
              InstructionsDigest = role + "-digest"
              Stored = record id WorkSqliteRecordKind.Role role body graph.Version 0 0 0 1 })
    let launches =
        launches
        |> List.map (fun l ->
            let body = JsonObject()
            setString body "state" (launchStateText l.State)
            setString body "roleId" l.RoleId
            setBool body "everClaimed" l.EverClaimed
            setBool body "stopRequested" (l.Lease |> Option.exists (fun lease -> lease.StopRequested))
            body["credit"] <- (l.Credit |> Option.map (fun s -> JsonValue.Create(s) :> JsonNode) |> Option.defaultValue null)
            body["primaryWorkId"] <- null
            l.Id, record id WorkSqliteRecordKind.Launch l.Id body graph.Version 1 0 0 l.RoleVersion)
        |> Map.ofList
    { Id = id
      Queue = queue
      Graph = graph
      OptionalUnitIds = Set.empty
      Stored = projectRecord
      StageRecords = stages
      RoleRecords = roles
      LaunchRecords = launches }

let private seed (store: WorkSqliteAuthority) maximum reserved (projects: SqliteSchedulerProject list) =
    let records =
        [ for p in projects do
            yield p.Stored
            for s in p.StageRecords |> Map.toSeq |> Seq.map snd do yield s
            for role in p.RoleRecords do yield role.Stored
            for l in p.LaunchRecords |> Map.toSeq |> Seq.map snd do yield l ]
    let mutation record =
        { Key = record.Key; ExpectedVersion = None; Value = record.Value }
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records = records |> List.map mutation
          Capacities =
            [ { AccountId = "shared"
                ExpectedVersion = None
                Maximum = Some maximum
                ReservedDelta = reserved } ] } |> ok |> ignore
    { Account = { AccountId = "shared"; Version = 1L; Maximum = maximum; Reserved = reserved }
      Projects = projects
      CompleteProjectSet = true }

[<Test>]
let two_projects_compete_for_one_global_sqlite_slot () =
    withStore (fun store path ->
        let make id =
            let g = root id
            makeProject id g [ stage id ("work-" + id) "implementation" 1L WorkState.Available Set.empty ]
                [ "worker", Set.singleton "implementation" ] [] []
        let snapshot = seed store 1 0 [ make "a"; make "b" ]
        use gate = new ManualResetEventSlim(false)
        let contender id =
            Task.Run(fun () ->
                let client = WorkSqliteAuthority(path)
                gate.Wait()
                WorkSchedulerSqlite.reserve client snapshot (Guid.NewGuid()) id)
        let a = contender "launch-a"
        let b = contender "launch-b"
        gate.Set()
        let results = Task.WhenAll([| a; b |]).GetAwaiter().GetResult()
        Assert.That(results |> Array.filter Result.isOk |> Array.length, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isError |> Array.length, Is.EqualTo 1)
        match results |> Array.find Result.isError with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Expected SQLite CAS loser: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
    )

[<Test>]
let existing_specialist_credit_survives_higher_ranked_review_demand () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let review = stage id "review" "review" 1L WorkState.Available Set.empty
        let implement = stage id "implement" "implementation" 2L WorkState.Available Set.empty
        let prior = launch id "existing-reviewer" "specialist" LaunchState.ReservedUnregistered (Some "review")
        let p =
            makeProject id g [ review; implement ]
                [ "specialist", Set.singleton "review"
                  "generalist", Set.ofList [ "review"; "implementation" ] ]
                [ prior ] [ "review", 9; "implement", 1 ]
        let snapshot = seed store 2 1 [ p ]
        let result = WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "new-generalist" |> ok
        Assert.That(result.RoleId, Is.EqualTo "generalist")
        Assert.That(result.WorkId, Is.EqualTo "implement")
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 2)
        let old = store.ReadRecord p.LaunchRecords.[prior.Id].Key |> ok |> Option.get
        Assert.That(old.Value.BodyJson, Does.Contain "\"credit\":\"review\"")
    )

[<Test>]
let blockers_win_over_unrelated_lead_priority_and_unmet_dependents_stay_ineligible () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let prerequisite = stage id "prerequisite" "implementation" 20L WorkState.Available Set.empty
        let dependent = stage id "dependent" "implementation" 5L WorkState.Blocked (Set.singleton "prerequisite")
        let ordinary = stage id "ordinary" "implementation" 1L WorkState.Available Set.empty
        let p =
            makeProject id g [ prerequisite; dependent; ordinary ]
                [ "engineer", Set.singleton "implementation" ] []
                [ "prerequisite", 1; "ordinary", 1000 ]
        let snapshot = seed store 1 0 [ p ]
        let result = WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "launch" |> ok
        Assert.That(result.WorkId, Is.EqualTo "prerequisite")
    )

[<Test>]
let registered_launch_claims_exactly_once_and_original_operation_replays () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let work = stage id "work" "implementation" 1L WorkState.Available Set.empty
        let registered = launch id "launch" "engineer" (LaunchState.Registered("uuid","session")) (Some "work")
        let p = makeProject id g [ work ] [ "engineer", Set.singleton "implementation" ] [ registered ] []
        seed store 1 1 [ p ] |> ignore
        let operation = Guid.NewGuid()
        let first = WorkSchedulerSqlite.claim store p operation registered.Id |> ok
        Assert.That(first.WorkId, Is.EqualTo "work")
        Assert.That(first.Receipt.Replayed, Is.False)
        match WorkSchedulerSqlite.claim store p operation registered.Id with
        | Error SqliteSchedulerError.HistoricalReceiptNotAdmission -> ()
        | other -> Assert.Fail(sprintf "Historical claim must not grant renewed authority: %A" other)
        let historical = store.ReadReceipt operation |> ok |> Option.get
        Assert.That(historical.Replayed, Is.True)
        Assert.That(historical.Sequence, Is.EqualTo first.Receipt.Sequence)
        match WorkSchedulerSqlite.claim store p (Guid.NewGuid()) registered.Id with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Expected stale claim fence: %A" other)
        let claimed = store.ReadRecord p.StageRecords.["work"].Key |> ok |> Option.get
        Assert.That(claimed.Value.ClaimGeneration, Is.EqualTo 1L)
        Assert.That(claimed.Value.BodyJson, Does.Contain "\"Claimed\"")
        let launched = store.ReadRecord p.LaunchRecords.[registered.Id].Key |> ok |> Option.get
        Assert.That(launched.Value.BodyJson, Does.Contain "\"everClaimed\":true")
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
    )

[<Test>]
let stop_or_reincarnation_changed_in_sqlite_fences_snapshot_and_preserves_capacity () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let p = makeProject id g [ stage id "work" "implementation" 1L WorkState.Available Set.empty ]
                    [ "engineer", Set.singleton "implementation" ] [] []
        let snapshot = seed store 1 0 [ p ]
        store.Commit
            { OperationId = Guid.NewGuid()
              Guards = []
              Records = [ { Key = p.Stored.Key; ExpectedVersion = Some 1L
                            Value = { p.Stored.Value with StopGeneration = 1L } } ]
              Capacities = [] } |> ok |> ignore
        match WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "fenced" with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Expected stale STOP/authority CAS: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 0)
        Assert.That(store.ReadRecord (key WorkSqliteRecordKind.Launch id "fenced") |> ok |> Option.isNone, Is.True)
    )

[<Test>]
let unresolved_external_effect_cannot_be_admitted_or_replayed_as_a_new_launch () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let original =
            makeProject id g [ stage id "work" "implementation" 1L WorkState.Available Set.empty ]
                [ "engineer", Set.singleton "implementation" ] [] []
        let stageRow = original.StageRecords.["work"]
        let unknown = { stageRow with Value = { stageRow.Value with ExternalEffect = WorkSqliteEffect.Unknown } }
        let rows = original.StageRecords.Add("work", unknown)
        let p = { original with StageRecords = rows }
        let snapshot = seed store 1 0 [ p ]
        match WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "unsafe" with
        | Error SqliteSchedulerError.NoEligibleWork -> ()
        | other -> Assert.Fail(sprintf "Expected unresolved effect fence: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 0)
    )


[<Test>]
let fresh_fifo_position_after_yield_is_observed_in_sqlite_admission () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        // The yielded work has a later AvailabilitySequence. Equal Lead priority
        // must choose the older currently available work, not its original ID.
        let yielded = stage id "a-yielded" "implementation" 30L WorkState.Available Set.empty
        let untouched = stage id "z-untouched" "implementation" 2L WorkState.Available Set.empty
        let p =
            makeProject id g [ yielded; untouched ]
                [ "engineer", Set.singleton "implementation" ] [] []
        let snapshot = seed store 1 0 [ p ]
        let selected = WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "launch" |> ok
        Assert.That(selected.WorkId, Is.EqualTo "z-untouched")
    )

[<Test>]
let reservation_replays_identically_without_second_capacity_lease () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let p =
            makeProject id g [ stage id "work" "implementation" 1L WorkState.Available Set.empty ]
                [ "engineer", Set.singleton "implementation" ] [] []
        let snapshot = seed store 1 0 [ p ]
        let op = Guid.NewGuid()
        let first = WorkSchedulerSqlite.reserve store snapshot op "same" |> ok
        match WorkSchedulerSqlite.reserve store snapshot op "same" with
        | Error SqliteSchedulerError.HistoricalReceiptNotAdmission -> ()
        | other -> Assert.Fail(sprintf "Historical reservation must not be renewed: %A" other)
        let historical = store.ReadReceipt op |> ok |> Option.get
        Assert.That(historical.Replayed, Is.True)
        Assert.That(historical.Sequence, Is.EqualTo first.Receipt.Sequence)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
        match WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "another" with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Expected stale original snapshot: %A" other)
    )

[<Test>]
let draft_or_missing_approved_provenance_fences_available_stage () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let p = makeProject id g
                    [ stage id "work" "implementation" 1L WorkState.Available Set.empty ]
                    [ "engineer", Set.singleton "implementation" ] [] []
        let original = p.StageRecords.["work"]
        let modified = JsonNode.Parse(original.Value.BodyJson).AsObject()
        modified["proposalState"] <- JsonValue.Create("Draft")
        let bad = { original with Value = { original.Value with BodyJson = modified.ToJsonString() } }
        let invalid = { p with StageRecords = p.StageRecords.Add("work", bad) }
        let snapshot = seed store 1 0 [ invalid ]
        match WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "unapproved" with
        | Error(SqliteSchedulerError.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Expected fail-closed unapproved candidate: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 0)
    )

[<Test>]
let duplicate_cross_project_launch_uuid_is_rejected_before_reservation () =
    withStore (fun store _ ->
        let existing id =
            launch id "same-launch" "engineer" LaunchState.ReservedUnregistered (Some("work-" + id))
        let make id =
            makeProject id (root id)
                [ stage id ("work-" + id) "implementation" 1L WorkState.Available Set.empty ]
                [ "engineer", Set.singleton "implementation" ] [ existing id ] []
        let snapshot = seed store 3 2 [ make "a"; make "b" ]
        match WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "new" with
        | Error(SqliteSchedulerError.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Expected cross-project launch uniqueness fence: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 2)
    )


[<Test>]
let role_message_requires_future_durable_handler_cas_and_is_not_ghost_claimed () =
    withStore (fun store _ ->
        let id = "p"
        let g = root id
        let messageWork =
            { stage id "role-stage" "role:worker" 1L WorkState.Available Set.empty with
                Kind = WorkKind.RoleMessage "message-1"
                SubjectId = "worker" }
        let initial =
            makeProject id g [ messageWork ] [ "worker", Set.singleton "role:worker" ] [] []
        let message =
            { Id = "message-1"
              Subject = Subject.Role "worker"
              Body = "Need an acknowledged handler"
              State = MessageState.Pending
              HandlerGeneration = 0
              WorkId = Some "role-stage" }
        let p = { initial with Queue = { initial.Queue with Messages = Map.ofList [ message.Id, message ] } }
        let snapshot = seed store 1 0 [ p ]
        match WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "ghost" with
        | Error SqliteSchedulerError.NoEligibleWork -> ()
        | other -> Assert.Fail(sprintf "Role message cannot be reserved without durable handler row: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 0)
        Assert.That(store.ReadRecord (key WorkSqliteRecordKind.Launch id "ghost") |> ok |> Option.isNone, Is.True)
    )


let private reloadProject (store: WorkSqliteAuthority) (p: SqliteSchedulerProject) queue =
    let read key = store.ReadRecord key |> ok |> Option.get
    { p with
        Queue = queue
        Stored = read p.Stored.Key
        StageRecords = p.StageRecords |> Map.map (fun _ item -> read item.Key)
        RoleRecords = p.RoleRecords |> List.map (fun role -> { role with Stored = read role.Stored.Key })
        LaunchRecords =
            queue.Launches
            |> Map.map (fun launchId _ -> read (key WorkSqliteRecordKind.Launch p.Id launchId)) }

[<Test>]
let review_only_unsettled_credit_cannot_justify_another_global_reservation () =
    for effect in [ WorkSqliteEffect.Unknown; WorkSqliteEffect.Prepared ] do
        withStore (fun store _ ->
            let id = "p"
            let review = stage id "review" "review" 1L WorkState.Available Set.empty
            let implement = stage id "implement" "implementation" 2L WorkState.Available Set.empty
            let existing =
                launch id "specialist-reserved" "reviewer" LaunchState.ReservedUnregistered (Some "review")
            let original =
                makeProject id (root id) [ review; implement ]
                    [ "reviewer", Set.singleton "review"
                      "generalist", Set.singleton "implementation" ]
                    [ existing ] [ "implement", 10 ]
            let old = original.StageRecords.["review"]
            let project =
                { original with
                    StageRecords =
                        original.StageRecords.Add(
                            "review", { old with Value = { old.Value with ExternalEffect = effect } }) }
            let snapshot = seed store 2 1 [ project ]
            match WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "new-generalist" with
            | Error SqliteSchedulerError.NoEligibleWork -> ()
            | result -> Assert.Fail(sprintf "Unsettled %A was counted as specialist credit: %A" effect result)
            Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
            Assert.That(store.ReadRecord(key WorkSqliteRecordKind.Launch id "new-generalist") |> ok |> Option.isNone, Is.True)
        )

[<Test>]
let claim_reranking_persists_actual_work_source_and_generation () =
    withStore (fun store _ ->
        let id = "p"
        let shaA = String.replicate 40 "a"
        let shaB = String.replicate 40 "b"
        let a = stage id "a" "implementation" 1L WorkState.Available Set.empty
        let b = { stage id "b" "implementation" 2L WorkState.Available Set.empty with StageGeneration = 2 }
        let registered =
            launch id "launch" "engineer" (LaunchState.Registered("uuid", "session")) (Some "a")
        let original = makeProject id (root id) [ a; b ]
                            [ "engineer", Set.singleton "implementation" ]
                            [ registered ] [ "a", 1; "b", 20 ]
        let withSource workId sha =
            let row = original.StageRecords.[workId]
            { row with Value = { row.Value with SourceSha = Some sha } }
        let launchRow = original.LaunchRecords.[registered.Id]
        let p =
            { original with
                StageRecords =
                    original.StageRecords.Add("a", withSource "a" shaA)
                        .Add("b", withSource "b" shaB)
                LaunchRecords =
                    original.LaunchRecords.Add(
                        registered.Id,
                        { launchRow with Value = { launchRow.Value with SourceSha = Some shaA } }) }
        seed store 1 1 [ p ] |> ignore
        let claimed = WorkSchedulerSqlite.claim store p (Guid.NewGuid()) registered.Id |> ok
        Assert.That(claimed.WorkId, Is.EqualTo "b")
        Assert.That(claimed.WorkGeneration, Is.EqualTo 2)
        let persisted = store.ReadRecord p.LaunchRecords.[registered.Id].Key |> ok |> Option.get
        Assert.That(persisted.Value.SourceSha, Is.EqualTo(Some shaB))
        Assert.That(persisted.Value.StageGeneration, Is.EqualTo 2L)
        Assert.That(persisted.Value.ClaimGeneration, Is.EqualTo 1L)
        let body = JsonNode.Parse(persisted.Value.BodyJson).AsObject()
        let primary = body["primaryWorkId"]
        Assert.That(primary.ToString(), Is.EqualTo "b")
    )

[<Test>]
let cap_one_reservation_replays_from_fresh_reopened_sqlite_state () =
    withStore (fun store path ->
        let id = "p"
        let p = makeProject id (root id)
                    [ stage id "work" "implementation" 1L WorkState.Available Set.empty ]
                    [ "engineer", Set.singleton "implementation" ] [] []
        let initial = seed store 1 0 [ p ]
        let operation = Guid.NewGuid()
        let first = WorkSchedulerSqlite.reserve store initial operation "reserved" |> ok
        let reserved = launch id "reserved" first.RoleId LaunchState.ReservedUnregistered (Some first.WorkId)
        let queue =
            { p.Queue with
                Version = p.Queue.Version + 1
                Launches = p.Queue.Launches.Add(reserved.Id, reserved) }
        let fresh =
            { initial with
                Account = store.ReadCapacity "shared" |> ok |> Option.get
                Projects = [ reloadProject store p queue ] }
        let reopened = WorkSqliteAuthority(path)
        match WorkSchedulerSqlite.reserve reopened fresh operation "reserved" with
        | Error SqliteSchedulerError.HistoricalReceiptNotAdmission -> ()
        | other -> Assert.Fail(sprintf "Reopened historic reservation granted admission: %A" other)
        let historical = reopened.ReadReceipt operation |> ok |> Option.get
        Assert.That(historical.Replayed, Is.True)
        Assert.That(historical.Sequence, Is.EqualTo first.Receipt.Sequence)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
        match WorkSchedulerSqlite.reserve reopened fresh operation "different-launch" with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.OperationIdReused _)) -> ()
        | result -> Assert.Fail(sprintf "Reused receipt with changed launch identity: %A" result)
        match WorkSchedulerSqlite.reserve reopened fresh (Guid.NewGuid()) "new" with
        | Error SqliteSchedulerError.NoCapacity -> ()
        | result -> Assert.Fail(sprintf "Fresh operation unexpectedly bypassed cap-one: %A" result)
    )

[<Test>]
let already_claimed_launch_replays_from_fresh_reopened_sqlite_state () =
    withStore (fun store path ->
        let id = "p"
        let originalStage = stage id "work" "implementation" 1L WorkState.Available Set.empty
        let registered =
            launch id "launch" "engineer" (LaunchState.Registered("uuid", "session")) (Some "work")
        let p = makeProject id (root id) [ originalStage ]
                    [ "engineer", Set.singleton "implementation" ] [ registered ] []
        seed store 1 1 [ p ] |> ignore
        let operation = Guid.NewGuid()
        let first = WorkSchedulerSqlite.claim store p operation registered.Id |> ok
        let claimedStage =
            { originalStage with State = WorkState.Claimed registered.Id; ClaimGeneration = 1 }
        let claimedLaunch =
            { registered with Credit = None; PrimaryWorkId = Some originalStage.Id; EverClaimed = true }
        let queue =
            { p.Queue with
                Version = p.Queue.Version + 1
                Works = p.Queue.Works.Add(originalStage.Id, claimedStage)
                Launches = p.Queue.Launches.Add(registered.Id, claimedLaunch) }
        let fresh = reloadProject store p queue
        let reopened = WorkSqliteAuthority(path)
        match WorkSchedulerSqlite.claim reopened fresh operation registered.Id with
        | Error SqliteSchedulerError.HistoricalReceiptNotAdmission -> ()
        | other -> Assert.Fail(sprintf "Reopened historic claim granted admission: %A" other)
        let historical = reopened.ReadReceipt operation |> ok |> Option.get
        Assert.That(historical.Replayed, Is.True)
        Assert.That(historical.Sequence, Is.EqualTo first.Receipt.Sequence)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
        match WorkSchedulerSqlite.claim reopened fresh operation "different-launch" with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.OperationIdReused _)) -> ()
        | result -> Assert.Fail(sprintf "Reused claim operation with changed identity: %A" result)
        match WorkSchedulerSqlite.claim reopened fresh (Guid.NewGuid()) registered.Id with
        | Error(SqliteSchedulerError.Incomplete _) -> ()
        | result -> Assert.Fail(sprintf "Fresh claim succeeded on previously claimed launch: %A" result)
    )


// E1a tests use disposable two-project SQLite state and exact C1-style origin
// records. They deliberately do not construct a production old-writer fence.
let private importJournalEvidence (store: WorkSqliteAuthority)
                                  (projects: (SqliteSchedulerProject * string) list) =
    let accountHead = String.replicate 40 "c"
    let projectHead = String.replicate 40 "d"
    let sourceHead = String.replicate 40 "e"
    let fenceBody = JsonObject()
    setString fenceBody "Mode" "offline-only"
    fenceBody["FenceVersion"] <- JsonValue.Create(1L)
    setString fenceBody "FenceOperationId" (Guid.NewGuid().ToString("D"))
    setString fenceBody "AccountHead" accountHead
    let heads = JsonArray()
    for p, _ in projects do
        let head = JsonObject()
        setString head "ProjectId" p.Id
        setString head "Head" projectHead
        heads.Add(head)
    fenceBody["ProjectHeads"] <- heads
    let fence =
        record "shared" WorkSqliteRecordKind.Project "legacy-import-fence"
               fenceBody 0 0 0 0 0
    let fenceValue =
        { fence.Value with
            SourceSha = Some accountHead
            ExternalEffect = WorkSqliteEffect.Prepared }
    let fence = { fence with Value = fenceValue }
    let sourceRows =
        [ for p, sha in projects do
            let legacyBody = JsonObject()
            setString legacyBody "kind" "verified-offline-import"
            let legacy = record p.Id WorkSqliteRecordKind.Project
                                "legacy-queue" legacyBody p.Graph.Version 0 0 0 0
            yield { legacy with Value = { legacy.Value with SourceSha = Some projectHead } }
            for KeyValue(workId, work) in p.Queue.Works do
                let origin = JsonObject()
                setString origin "WorkId" workId
                setString origin "RepositoryId" ("repo-" + p.Id)
                setString origin "SourceRef" ("refs/heads/feature-" + p.Id)
                setString origin "SourceCommit" sha
                setString origin "SourceHead" sourceHead
                setString origin "RoleId" "worker"
                setInt origin "RoleVersion" 1
                setInt origin "StageGeneration" work.StageGeneration
                setInt origin "GraphVersion" p.Graph.Version
                setBool origin "SourceAncestryVerified" true
                let row = record p.Id WorkSqliteRecordKind.Work
                                 ("origin/" + workId) origin p.Graph.Version
                                 work.StageGeneration 0 0 1
                yield { row with Value = { row.Value with SourceSha = Some sha } } ]
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records = (fence :: sourceRows)
                    |> List.map (fun row ->
                        { Key = row.Key; ExpectedVersion = None; Value = row.Value })
          Capacities = [] } |> ok |> ignore

let private dispatchProject id sha =
    let p =
        makeProject id (root id)
            [ stage id ("work-" + id) "implementation" 1L WorkState.Available Set.empty ]
            [ "worker", Set.singleton "implementation" ] [] []
    let records =
        p.StageRecords
        |> Map.map (fun _ row ->
            let value = { row.Value with SourceSha = Some sha }
            { row with Value = value })
    { p with StageRecords = records }

[<Test>]
let e1a_two_project_prepared_unknown_crash_replay_keeps_account_credit_and_refuses_transport () =
    withStore (fun store path ->
        let a = dispatchProject "alpha" (String.replicate 40 "a")
        let b = dispatchProject "beta" (String.replicate 40 "b")
        let snapshot = seed store 1 0 [ a; b ]
        importJournalEvidence store [ a, String.replicate 40 "a"; b, String.replicate 40 "b" ]
        let reservationId = Guid.NewGuid()
        let reserved = WorkSchedulerSqlite.reserve store snapshot reservationId "launch-alpha" |> ok
        Assert.That(reserved.ProjectId, Is.EqualTo "alpha")
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        let prepareId = Guid.NewGuid()
        let first =
            WorkSchedulerSqliteDispatch.prepare store "shared" "alpha" "launch-alpha" prepareId |> ok
        Assert.That(first.Effect, Is.EqualTo WorkSqliteEffect.Prepared)
        Assert.That(first.Receipt.Value.Replayed, Is.False)
        Assert.That(first.SourceRef, Is.EqualTo "refs/heads/feature-alpha")
        Assert.That(first.SourceSha, Is.EqualTo(String.replicate 40 "a"))
        Assert.That(first.ReservationOperationId, Is.EqualTo reservationId)
        Assert.That(first.RoleId, Is.EqualTo "worker")
        Assert.That(first.StageGeneration, Is.EqualTo 1L)
        match WorkSchedulerSqliteDispatch.refuseExternalDispatch first with
        | Error SqliteSchedulerDispatchError.UnverifiedWriterFence -> ()
        | other -> Assert.Fail(sprintf "E1a must never authorize provider dispatch: %A" other)
        let reopened = WorkSqliteAuthority(path)
        let replay =
            WorkSchedulerSqliteDispatch.prepare reopened "shared" "alpha" "launch-alpha" prepareId |> ok
        Assert.That(replay.Receipt.Value.Replayed, Is.True)
        Assert.That(replay.Receipt.Value.Sequence, Is.EqualTo first.Receipt.Value.Sequence)
        Assert.That(replay.Effect, Is.EqualTo WorkSqliteEffect.Prepared)
        let uncertaintyId = Guid.NewGuid()
        let unknown =
            WorkSchedulerSqliteDispatch.markUnknown reopened "shared" "alpha"
                "launch-alpha" uncertaintyId |> ok
        Assert.That(unknown.Effect, Is.EqualTo WorkSqliteEffect.Unknown)
        Assert.That(unknown.Receipt.Value.Replayed, Is.False)
        let unknownReplay =
            WorkSchedulerSqliteDispatch.markUnknown store "shared" "alpha"
                "launch-alpha" uncertaintyId |> ok
        Assert.That(unknownReplay.Receipt.Value.Replayed, Is.True)
        Assert.That(unknownReplay.Receipt.Value.Sequence, Is.EqualTo unknown.Receipt.Value.Sequence)
        let persisted =
            WorkSchedulerSqliteDispatch.readIntent store "shared" "alpha" "launch-alpha" |> ok
        Assert.That(persisted.Effect, Is.EqualTo WorkSqliteEffect.Unknown)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 5L)
        match WorkSchedulerSqliteDispatch.markUnknown store "shared" "alpha"
                "launch-alpha" (Guid.NewGuid()) with
        | Error(SqliteSchedulerDispatchError.MissingOrInvalidEvidence _) -> ()
        | other -> Assert.Fail(sprintf "Unknown dispatch was reissued: %A" other)
        match WorkSchedulerSqliteDispatch.prepare reopened "shared" "beta"
                "launch-alpha" prepareId with
        | Error(SqliteSchedulerDispatchError.MissingOrInvalidEvidence _) -> ()
        | other -> Assert.Fail(sprintf "Cross-project receipt was laundered: %A" other)
        Assert.That(store.ReadRecord(key WorkSqliteRecordKind.ExternalEffect "beta" "launch-alpha")
                    |> ok |> Option.isNone, Is.True)
    )

[<Test>]
let e1a_stale_original_source_and_stop_are_refused_without_new_effect_record () =
    for breach in [ "source"; "stop" ] do
        withStore (fun store _ ->
            let a = dispatchProject "alpha" (String.replicate 40 "a")
            let snapshot = seed store 1 0 [ a ]
            importJournalEvidence store [ a, String.replicate 40 "a" ]
            WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "launch" |> ok |> ignore
            let bad =
                if breach = "source" then
                    let row = store.ReadRecord(key WorkSqliteRecordKind.Work "alpha" "origin/work-alpha")
                              |> ok |> Option.get
                    { row with Value = { row.Value with SourceSha = Some(String.replicate 40 "b") } }
                else
                    let row = store.ReadRecord(key WorkSqliteRecordKind.Project "alpha" "authority")
                              |> ok |> Option.get
                    { row with Value = { row.Value with StopGeneration = row.Value.StopGeneration + 1L } }
            store.Commit
                { OperationId = Guid.NewGuid()
                  Guards = []
                  Records = [ { Key = bad.Key; ExpectedVersion = Some bad.Version; Value = bad.Value } ]
                  Capacities = [] } |> ok |> ignore
            match WorkSchedulerSqliteDispatch.prepare store "shared" "alpha" "launch" (Guid.NewGuid()) with
            | Error(SqliteSchedulerDispatchError.MissingOrInvalidEvidence _) -> ()
            | other -> Assert.Fail(sprintf "Stale %s was not fenced: %A" breach other)
            Assert.That(store.ReadRecord(key WorkSqliteRecordKind.ExternalEffect "alpha" "launch")
                        |> ok |> Option.isNone, Is.True)
            Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        )

[<Test>]
let e1a_competing_prepares_serialize_without_duplicate_journal_or_credit () =
    withStore (fun store path ->
        let a = dispatchProject "alpha" (String.replicate 40 "a")
        let snapshot = seed store 1 0 [ a ]
        importJournalEvidence store [ a, String.replicate 40 "a" ]
        WorkSchedulerSqlite.reserve store snapshot (Guid.NewGuid()) "launch" |> ok |> ignore
        use gate = new ManualResetEventSlim(false)
        let contender () =
            Task.Run(fun () ->
                let client = WorkSqliteAuthority(path)
                gate.Wait()
                WorkSchedulerSqliteDispatch.prepare client "shared" "alpha" "launch" (Guid.NewGuid()))
        let one, two = contender(), contender()
        gate.Set()
        let results = Task.WhenAll([| one; two |]).GetAwaiter().GetResult()
        Assert.That(results |> Array.filter Result.isOk |> Array.length, Is.EqualTo 1)
        Assert.That(results |> Array.filter Result.isError |> Array.length, Is.EqualTo 1)
        let stored = WorkSchedulerSqliteDispatch.readIntent store "shared" "alpha" "launch" |> ok
        Assert.That(stored.Effect, Is.EqualTo WorkSqliteEffect.Prepared)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 4L)
    )


[<Test>]
let e1a_account_binding_rejects_cross_account_reservation_replay_and_preparation () =
    withStore (fun store _ ->
        let p = dispatchProject "alpha" (String.replicate 40 "a")
        let snapshot = seed store 1 0 [ p ]
        importJournalEvidence store [ p, String.replicate 40 "a" ]
        let reserveId = Guid.NewGuid()
        WorkSchedulerSqlite.reserve store snapshot reserveId "launch" |> ok |> ignore

        // A different account has its own slot and a cloned offline marker.
        // Neither proves it owns alpha's original reservation credit.
        let oldFence =
            store.ReadRecord (key WorkSqliteRecordKind.Project "shared" "legacy-import-fence")
            |> ok |> Option.get
        let copiedFence =
            { Key = key WorkSqliteRecordKind.Project "other" "legacy-import-fence"
              ExpectedVersion = None
              Value = oldFence.Value }
        store.Commit
            { OperationId = Guid.NewGuid()
              Guards = []
              Records = [ copiedFence ]
              Capacities =
                [ { AccountId = "other"; ExpectedVersion = None
                    Maximum = Some 1; ReservedDelta = 1 } ] }
        |> ok |> ignore
        let wrongSnapshot =
            { snapshot with Account = { snapshot.Account with AccountId = "other" } }
        match WorkSchedulerSqlite.reserve store wrongSnapshot reserveId "launch" with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.OperationIdReused _)) -> ()
        | other -> Assert.Fail(sprintf "Cross-account reservation receipt was replayed: %A" other)
        match WorkSchedulerSqliteDispatch.prepare store "other" "alpha" "launch"
                (Guid.NewGuid()) with
        | Error(SqliteSchedulerDispatchError.MissingOrInvalidEvidence _) -> ()
        | other -> Assert.Fail(sprintf "Foreign account prepared another account's launch: %A" other)
        Assert.That(store.ReadRecord(key WorkSqliteRecordKind.ExternalEffect "alpha" "launch")
                    |> ok |> Option.isNone, Is.True)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That((store.ReadCapacity "other" |> ok |> Option.get).Reserved, Is.EqualTo 1)
    )


let private installDurableRecoveryCoverage (store: WorkSqliteAuthority) projectId =
    // The G1a Recovery lane owns its implementation. Reproduce the durable
    // authority boundary here without taking a dependency on unreviewed G1a.
    let authority = store.ReadRecord(key WorkSqliteRecordKind.Project projectId "authority")
                    |> ok |> Option.get
    let body = JsonNode.Parse(authority.Value.BodyJson).AsObject()
    setString body "coverage" "Recovery"
    let capacity = store.ReadCapacity "shared" |> ok |> Option.get
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records =
            [ { Key = authority.Key
                ExpectedVersion = Some authority.Version
                Value = { authority.Value with BodyJson = body.ToJsonString() } } ]
          Capacities =
            [ { AccountId = "shared"
                ExpectedVersion = Some capacity.Version
                Maximum = None
                ReservedDelta = 0 } ] }
    |> ok |> ignore

[<Test>]
let post_recovery_same_uuid_two_project_reservation_never_renews_admission () =
    withStore (fun store path ->
        let a = dispatchProject "alpha" (String.replicate 40 "a")
        let b = dispatchProject "beta" (String.replicate 40 "b")
        let snapshot = seed store 2 0 [ a; b ]
        let operationId = Guid.NewGuid()
        let first = WorkSchedulerSqlite.reserve store snapshot operationId "launch-alpha" |> ok
        Assert.That(first.ProjectId, Is.EqualTo "alpha")
        installDurableRecoveryCoverage store "alpha"
        let fencedSequence = store.CurrentSequence() |> ok
        let client = WorkSqliteAuthority(path)
        match WorkSchedulerSqlite.reserve client snapshot operationId "launch-alpha" with
        | Error SqliteSchedulerError.HistoricalReceiptNotAdmission -> ()
        | other -> Assert.Fail(sprintf "Post-Recovery receipt granted fresh reservation: %A" other)
        let historical = client.ReadReceipt operationId |> ok |> Option.get
        Assert.That(historical.Sequence, Is.EqualTo first.Receipt.Sequence)
        Assert.That(historical.Replayed, Is.True)
        let oldLaunch = store.ReadRecord(key WorkSqliteRecordKind.Launch "alpha" "launch-alpha")
                        |> ok |> Option.get
        Assert.That(oldLaunch.Value.BodyJson, Does.Contain(operationId.ToString("D")))
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo fencedSequence)
        Assert.That(store.ReadRecord(key WorkSqliteRecordKind.Launch "beta" "launch-alpha")
                    |> ok |> Option.isNone, Is.True)
    )

[<Test>]
let post_recovery_same_uuid_two_project_claim_never_renews_admission () =
    withStore (fun store path ->
        let a = dispatchProject "alpha" (String.replicate 40 "a")
        let b = dispatchProject "beta" (String.replicate 40 "b")
        let registered = launch "alpha" "launch-alpha" "worker"
                            (LaunchState.Registered("uuid", "session")) (Some "work-alpha")
        let a = makeProject "alpha" a.Graph [ a.Queue.Works.["work-alpha"] ]
                    [ "worker", Set.singleton "implementation" ] [ registered ] []
        seed store 2 1 [ a; b ] |> ignore
        let operationId = Guid.NewGuid()
        let first = WorkSchedulerSqlite.claim store a operationId registered.Id |> ok
        Assert.That(first.WorkId, Is.EqualTo "work-alpha")
        installDurableRecoveryCoverage store "alpha"
        let fencedSequence = store.CurrentSequence() |> ok
        let client = WorkSqliteAuthority(path)
        match WorkSchedulerSqlite.claim client a operationId registered.Id with
        | Error SqliteSchedulerError.HistoricalReceiptNotAdmission -> ()
        | other -> Assert.Fail(sprintf "Post-Recovery receipt granted fresh claim: %A" other)
        let historical = client.ReadReceipt operationId |> ok |> Option.get
        Assert.That(historical.Sequence, Is.EqualTo first.Receipt.Sequence)
        Assert.That(historical.Replayed, Is.True)
        let oldLaunch = store.ReadRecord(key WorkSqliteRecordKind.Launch "alpha" registered.Id)
                        |> ok |> Option.get
        Assert.That(oldLaunch.Value.BodyJson, Does.Contain(operationId.ToString("D")))
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo fencedSequence)
        Assert.That(store.ReadRecord(key WorkSqliteRecordKind.Launch "beta" registered.Id)
                    |> ok |> Option.isNone, Is.True)
    )
