module WorkRecoverySqliteTests

open System
open System.IO
open System.Text.Json.Nodes
open NUnit.Framework
open Server
open Jarvis.WorkRecovery

let private ok = function
    | Ok value -> value
    | Error error -> Assert.Fail(sprintf "Unexpected failure %A" error); Unchecked.defaultof<_>

let private withStore f =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-recovery-g1a-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    try
        let store = WorkSqliteAuthority(Path.Combine(folder, "work.sqlite"))
        store.Initialize() |> ok |> ignore
        f store
    finally
        Directory.Delete(folder, true)

let private key project kind id =
    { ProjectId = project; Kind = kind; EntityId = id }

let private strProp (record: WorkSqliteRecord) (name: string) =
    let body = JsonNode.Parse(record.Value.BodyJson).AsObject()
    body[name].GetValue<string>()

let private row project kind id body stage effect =
    { Key = key project kind id
      Version = 1L
      Value =
        { BodyJson = body
          GraphVersion = 3L
          StageGeneration = stage
          ClaimGeneration = 0L
          StopGeneration = 0L
          RoleVersion = 0L
          SourceSha = None
          ReviewSha = None
          IntegrationSha = None
          ExternalEffect = effect } }

let private makeProject project effect =
    let work = "W-" + project
    let projectBody = JsonObject()
    projectBody["coverage"] <- JsonValue.Create("Verified")
    projectBody["queueVersion"] <- JsonValue.Create(1)
    let stageBody = JsonObject()
    stageBody["state"] <- JsonValue.Create("Available")
    stageBody["gate"] <- JsonValue.Create("Clear")
    stageBody["queueId"] <- JsonValue.Create("implementation")
    stageBody["proposalState"] <- JsonValue.Create("Open")
    let projectRow = row project WorkSqliteRecordKind.Project "authority" (projectBody.ToJsonString()) 0L WorkSqliteEffect.NoneRecorded
    let stageRow = row project WorkSqliteRecordKind.Stage work (stageBody.ToJsonString()) 2L effect
    { ProjectId = project
      Project = projectRow
      Stages = Map.ofList [ work, stageRow ]
      Roles = []
      Launches = [] }

let private work project =
    { WorkId = "W-" + project
      WorkVersion = 4
      Generation = 2
      RoleAuthoritySha = String.replicate 40 "c"
      Goal = ArtifactGoal.Unchanged
      CapturedInFlightLaunch = false
      CapturedLaunches = []
      Source = None
      Review = None
      Integration = None
      Effects = []
      Release = None }

let private recoveryProject project =
    { ProjectId = project
      GraphVersion = 3
      RestoredSequence = 1L
      KnownPreviousSequence = None
      Stop = StopFence.Unknown
      GapSeal = None
      Works = [ work project ] }

let private input projects =
    { SchemaVersion = 1
      SnapshotDatabaseSequence = 1L
      KnownPreviousDatabaseSequence = None
      CompleteProjectSetVerified = false
      AccountGapSeal = None
      GlobalStop = StopFence.Unknown
      Projects = projects |> List.map recoveryProject }

let private setup (store: WorkSqliteAuthority) effects =
    let projects = effects |> List.map (fun (project, effect) -> makeProject project effect)
    let insert (r: WorkSqliteRecord) = { Key = r.Key; ExpectedVersion = None; Value = r.Value }
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records =
            projects
            |> List.collect (fun p -> [ insert p.Project ] @ (p.Stages |> Map.toList |> List.map (snd >> insert)))
          Capacities =
            [ { AccountId = "shared"
                ExpectedVersion = None
                Maximum = Some 1
                ReservedDelta = 0 } ] } |> ok |> ignore
    { Account = { AccountId = "shared"; Version = 1L; Maximum = 1; Reserved = 0 }
      Projects = projects
      CompleteProjectSet = true }

let private record (store: WorkSqliteAuthority) project kind entity =
    store.ReadRecord(key project kind entity) |> ok |> Option.get

let private assertFenced store project =
    let authority = record store project WorkSqliteRecordKind.Project "authority"
    let stage = record store project WorkSqliteRecordKind.Stage ("W-" + project)
    Assert.That(strProp authority "coverage", Is.EqualTo "Recovery")
    Assert.That(strProp stage "state", Is.EqualTo "Blocked")
    Assert.That(strProp stage "gate", Is.EqualTo "SourceRecoveryBlocked")
    let fence = WorkRecoverySqlite.readFence store project |> ok |> Option.get
    Assert.That(fence.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Unknown)
    Assert.That(strProp fence "state", Is.EqualTo "Recovery")
    fence

[<Test>]
let seed_is_single_cas_fence_for_two_projects_and_account_capacity () =
    withStore (fun store ->
        let source = setup store [ "alpha", WorkSqliteEffect.NoneRecorded; "beta", WorkSqliteEffect.NoneRecorded ]
        let original = input [ "alpha"; "beta" ]
        let seeded = WorkRecoverySqlite.seed store source original (Guid.NewGuid()) |> ok
        Assert.That(seeded.ProjectIds, Is.EqualTo(box [ "alpha"; "beta" ]))
        Assert.That(seeded.Receipt.Replayed, Is.False)
        assertFenced store "alpha" |> ignore
        assertFenced store "beta" |> ignore
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Version, Is.EqualTo 2L)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
        Assert.That(seeded.Plan.RecoveryQueue |> List.exists (fun x -> x.Queue = "Recovery" && x.ProjectId = "(global)"), Is.True))

[<Test>]
let stale_scheduler_project_and_account_cannot_reserve_after_fence () =
    withStore (fun store ->
        let source = setup store [ "alpha", WorkSqliteEffect.NoneRecorded ]
        let old = source.Projects.Head.Project
        let oldStage = source.Projects.Head.Stages.["W-alpha"]
        WorkRecoverySqlite.seed store source (input [ "alpha" ]) (Guid.NewGuid()) |> ok |> ignore
        let command =
            { OperationId = Guid.NewGuid()
              Guards =
                [ { Key = old.Key; ExpectedVersion = old.Version
                    GraphVersion = Some old.Value.GraphVersion
                    StageGeneration = None; ClaimGeneration = None
                    StopGeneration = Some old.Value.StopGeneration
                    RoleVersion = None; SourceSha = None }
                  { Key = oldStage.Key; ExpectedVersion = oldStage.Version
                    GraphVersion = Some oldStage.Value.GraphVersion
                    StageGeneration = Some oldStage.Value.StageGeneration
                    ClaimGeneration = Some oldStage.Value.ClaimGeneration
                    StopGeneration = Some oldStage.Value.StopGeneration
                    RoleVersion = None; SourceSha = None } ]
              Records = []
              Capacities =
                [ { AccountId = "shared"; ExpectedVersion = Some source.Account.Version
                    Maximum = None; ReservedDelta = 1 } ] }
        match store.Commit command with
        | Error(WorkSqliteFailure.StaleVersion _) -> ()
        | other -> Assert.Fail(sprintf "Stale scheduler CAS should fail: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 0)
        assertFenced store "alpha" |> ignore)

[<Test>]
let unknown_effect_and_original_launch_identity_survive_fencing () =
    withStore (fun store ->
        let source = setup store [ "alpha", WorkSqliteEffect.Unknown ]
        let baseInput = input [ "alpha" ]
        let launch =
            { ProjectId = "alpha"; WorkId = "W-alpha"; Generation = 2; OperationId = "captured-original-launch" }
        let captured = { work "alpha" with CapturedInFlightLaunch = true; CapturedLaunches = [ launch ] }
        let original = { baseInput with Projects = [ { recoveryProject "alpha" with Works = [ captured ] } ] }
        let result = WorkRecoverySqlite.seed store source original (Guid.NewGuid()) |> ok
        let fence = assertFenced store "alpha"
        Assert.That((record store "alpha" WorkSqliteRecordKind.Stage "W-alpha").Value.ExternalEffect,
                    Is.EqualTo WorkSqliteEffect.Unknown)
        Assert.That(fence.Value.BodyJson.Contains("captured-original-launch"), Is.True)
        // The captured launch remains in the versioned, digest-bound pure
        // recovery input; no unrelated terminal observation can release it.
        Assert.That(result.Plan.Projects.Head.Works.Head.Requirements,
                    Does.Contain(Requirement.InFlightLaunchReconciliation)))

[<Test>]
let prepared_effect_is_fenced_for_entire_account_without_erasing_original_evidence () =
    withStore (fun store ->
        // An in-flight original effect is evidence requiring reconciliation,
        // not a reason to leave an otherwise Available sibling project live.
        let source = setup store [ "alpha", WorkSqliteEffect.Prepared; "beta", WorkSqliteEffect.NoneRecorded ]
        let originalStage = source.Projects.Head.Stages.["W-alpha"]
        let capturedLaunch =
            { ProjectId = "alpha"; WorkId = "W-alpha"; Generation = 2; OperationId = "original-prepared-launch" }
        let uncertainEffect =
            { OperationId = capturedLaunch.OperationId
              WorkId = "W-alpha"; Generation = 2; Kind = EffectKind.Launch
              SourceSha = None; Outcome = EffectOutcome.Unknown }
        let uncertainWork =
            { work "alpha" with
                CapturedInFlightLaunch = true
                CapturedLaunches = [ capturedLaunch ]
                Effects = [ uncertainEffect ] }
        let original =
            { input [ "alpha"; "beta" ] with
                Projects =
                    [ { recoveryProject "alpha" with Works = [ uncertainWork ] }
                      recoveryProject "beta" ] }
        let operation = Guid.NewGuid()
        let first = WorkRecoverySqlite.seed store source original operation |> ok
        Assert.That(first.Receipt.Replayed, Is.False)
        Assert.That(first.Plan.NormalDispatchAllowed, Is.False)
        let alphaDecision = first.Plan.Projects |> List.find (fun p -> p.ProjectId = "alpha")
        Assert.That(alphaDecision.Works.Head.Requirements,
                    Does.Contain(Requirement.UnknownExternalEffect "original-prepared-launch"))
        Assert.That(alphaDecision.Works.Head.Requirements,
                    Does.Contain(Requirement.InFlightLaunchReconciliation))
        for project in [ "alpha"; "beta" ] do assertFenced store project |> ignore
        let preserved = record store "alpha" WorkSqliteRecordKind.Stage "W-alpha"
        Assert.That(preserved.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Prepared)
        let fence = WorkRecoverySqlite.readFence store "alpha" |> ok |> Option.get
        let fenceJson = JsonNode.Parse(fence.Value.BodyJson).AsObject()
        let originalStages = fenceJson["originalStages"].AsObject()
        let originalRow = originalStages["W-alpha"].AsObject()
        Assert.That(originalRow["bodyJson"].GetValue<string>(), Is.EqualTo originalStage.Value.BodyJson)
        Assert.That(originalRow["externalEffect"].GetValue<string>(), Is.EqualTo "Prepared")
        Assert.That(fence.Value.BodyJson, Does.Contain "original-prepared-launch")
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Version, Is.EqualTo 2L)
        let replay = WorkRecoverySqlite.seed store source original operation |> ok
        Assert.That(replay.Receipt.Replayed, Is.True)
        Assert.That(replay.Receipt.Sequence, Is.EqualTo first.Receipt.Sequence)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
        Assert.That((record store "alpha" WorkSqliteRecordKind.Stage "W-alpha").Value.ExternalEffect,
                    Is.EqualTo WorkSqliteEffect.Prepared))

[<Test>]
let one_stale_stage_rejects_whole_batch_without_partial_project_fences () =
    withStore (fun store ->
        let source = setup store [ "alpha", WorkSqliteEffect.NoneRecorded; "beta", WorkSqliteEffect.NoneRecorded ]
        let original = source.Projects.Head.Stages.["W-alpha"]
        store.Commit
            { OperationId = Guid.NewGuid()
              Guards = []
              Records = [ { Key = original.Key; ExpectedVersion = Some original.Version; Value = original.Value } ]
              Capacities = [] } |> ok |> ignore
        match WorkRecoverySqlite.seed store source (input [ "alpha"; "beta" ]) (Guid.NewGuid()) with
        | Error(SqliteRecoveryFailure.Storage(WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Expected stale version: %A" other)
        for project in [ "alpha"; "beta" ] do
            Assert.That(strProp (record store project WorkSqliteRecordKind.Project "authority") "coverage",
                        Is.EqualTo "Verified")
            Assert.That((WorkRecoverySqlite.readFence store project |> ok).IsNone, Is.True)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Version, Is.EqualTo 1L))

[<Test>]
let replay_requires_original_operation_and_all_evidence_not_just_same_receipt_id () =
    withStore (fun store ->
        let source = setup store [ "alpha", WorkSqliteEffect.NoneRecorded ]
        let original = input [ "alpha" ]
        let operation = Guid.NewGuid()
        let first = WorkRecoverySqlite.seed store source original operation |> ok
        let second = WorkRecoverySqlite.seed store source original operation |> ok
        Assert.That(first.Receipt.Sequence, Is.EqualTo second.Receipt.Sequence)
        Assert.That(second.Receipt.Replayed, Is.True)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
        let changed =
            { original with
                Projects =
                    [ { recoveryProject "alpha" with
                          Works = [ { work "alpha" with Goal = ArtifactGoal.SourceProduced } ] } ] }
        match WorkRecoverySqlite.seed store source changed operation with
        | Error(SqliteRecoveryFailure.Storage(WorkSqliteFailure.OperationIdReused id)) ->
            Assert.That(id, Is.EqualTo operation)
        | other -> Assert.Fail(sprintf "Changed replay evidence was accepted: %A" other)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L))

[<Test>]
let incomplete_or_mismatched_project_set_is_rejected_without_writes () =
    withStore (fun store ->
        let source = setup store [ "alpha", WorkSqliteEffect.NoneRecorded; "beta", WorkSqliteEffect.NoneRecorded ]
        let original = input [ "alpha"; "beta" ]
        for badSource, badInput in
            [ { source with CompleteProjectSet = false }, original
              source, input [ "alpha" ] ] do
            match WorkRecoverySqlite.seed store badSource badInput (Guid.NewGuid()) with
            | Error(SqliteRecoveryFailure.Incomplete _) -> ()
            | other -> Assert.Fail(sprintf "Incomplete project authority accepted: %A" other)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 1L))

[<Test>]
let caller_supplied_pure_release_flags_do_not_unfence_sqlite () =
    withStore (fun store ->
        let source = setup store [ "alpha", WorkSqliteEffect.NoneRecorded ]
        let cleared = StopFence.Cleared("proof", 1, true)
        let original =
            { input [ "alpha" ] with
                CompleteProjectSetVerified = true
                KnownPreviousDatabaseSequence = Some 1L
                GlobalStop = cleared
                Projects =
                    [ { recoveryProject "alpha" with
                          KnownPreviousSequence = Some 1L
                          Stop = cleared } ] }
        let pre = (WorkRecovery.plan original).Projects.Head.Works.Head
        Assert.That(pre.Requirements, Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
        let authorization =
            { ProjectId = "alpha"; WorkId = "W-alpha"
              ReceiptId = "self-attested-release"
              AuthorizationRevision = 1L
              CanonicallyVerified = true
              SnapshotDatabaseSequence = 1L
              ReconciledThroughProjectSequence = 1L
              ProjectGraphVersion = 3
              WorkVersion = 4
              Generation = 2
              RoleAuthoritySha = String.replicate 40 "c"
              EvidenceReceipts = pre.EvidenceReceipts
              EvidenceDigest = pre.EvidenceDigest }
        let baseProject = original.Projects.Head
        let baseWork = baseProject.Works.Head
        let selfAttested =
            { original with
                Projects = [ { baseProject with
                                 Works = [ { baseWork with Release = Some authorization } ] } ] }
        Assert.That((WorkRecovery.plan selfAttested).NormalDispatchAllowed, Is.True)
        WorkRecoverySqlite.seed store source selfAttested (Guid.NewGuid()) |> ok |> ignore
        // G1a only SEEDS locks; G1b must verify the real backup sequence and
        // Git/side-effect transcript before any release mutation exists.
        assertFenced store "alpha" |> ignore)


// E0 adapter contract regression: exercise the real default-off Scheduler,
 // rather than assuming that a similar ad-hoc CAS would behave identically.
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts

let private e0Array (values: seq<string>) =
    let result = JsonArray()
    for value in values do result.Add(JsonValue.Create(value))
    result

let private e0Record project kind id body graph stage role =
    { Key = key project kind id
      Version = 1L
      Value =
        { BodyJson = body
          GraphVersion = int64 graph
          StageGeneration = int64 stage
          ClaimGeneration = 0L
          StopGeneration = 0L
          RoleVersion = int64 role
          SourceSha = None; ReviewSha = None; IntegrationSha = None
          ExternalEffect = WorkSqliteEffect.NoneRecorded } }

let private e0Fixture (store: WorkSqliteAuthority) =
    let id = "alpha"
    let spec =
        { Title = "root-alpha"; Goal = "Synthetic Recovery fixture"
          Scope = "two-phase default-off test"
          AcceptanceCriteria = Set.singleton "proof"
          Constraints = Set.empty; LinkedTaskId = None }
    let graph =
        WorkComposition.apply WorkComposition.empty.Version
            (WorkCompositionMutation.Create("root-alpha", WorkUnitKind.Project, None, spec))
            WorkComposition.empty |> ok
    let stage =
        { Id = "W-alpha"
          ProjectId = id
          SubjectId = "task-W-alpha"
          Kind = WorkKind.Implementation
          QueueId = "implementation"
          StageGeneration = 1
          State = WorkState.Available
          Domain = DomainState.Undecided
          Gate = GateState.Clear
          ClaimGeneration = 0
          Dependencies = Set.empty
          WaivedDependencies = Map.empty
          AvailableAt = 1L
          AvailableSequence = 1L
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }
    let subscriptions = Map.ofList [ "worker", Set.singleton "implementation" ]
    let queue =
        { Contracts.emptyProject subscriptions with
            GraphVersion = graph.Version
            Works = Map.ofList [ stage.Id, stage ]
            NextAvailability = 1L }
    let projectBody = JsonObject()
    projectBody["queueVersion"] <- JsonValue.Create(queue.Version)
    projectBody["coverage"] <- JsonValue.Create("Verified")
    projectBody["stageIds"] <- e0Array [ stage.Id ]
    projectBody["roleIds"] <- e0Array [ "worker" ]
    projectBody["launchIds"] <- e0Array []
    let project = e0Record id WorkSqliteRecordKind.Project "authority"
                            (projectBody.ToJsonString()) graph.Version 0 0
    let stageBody = JsonObject()
    stageBody["state"] <- JsonValue.Create("Available")
    stageBody["gate"] <- JsonValue.Create("Clear")
    stageBody["queueId"] <- JsonValue.Create(stage.QueueId)
    stageBody["proposalState"] <- JsonValue.Create("Open")
    stageBody["availableSequence"] <- JsonValue.Create(stage.AvailableSequence)
    stageBody["dependencies"] <- e0Array []
    stageBody["waivedDependencies"] <- e0Array []
    let stageRow = e0Record id WorkSqliteRecordKind.Stage stage.Id
                            (stageBody.ToJsonString()) graph.Version stage.StageGeneration 0
    let roleBody = JsonObject()
    roleBody["state"] <- JsonValue.Create("Active")
    roleBody["instructionsDigest"] <- JsonValue.Create("worker-digest")
    roleBody["queues"] <- e0Array [ "implementation" ]
    let roleStored = e0Record id WorkSqliteRecordKind.Role "worker"
                              (roleBody.ToJsonString()) graph.Version 0 1
    let role =
        { Id = "worker"; Version = 1
          Queues = Set.singleton "implementation"
          InstructionsDigest = "worker-digest"
          Stored = roleStored }
    let sourceProject =
        { Id = id
          Queue = queue
          Graph = graph
          OptionalUnitIds = Set.empty
          Stored = project
          StageRecords = Map.ofList [ stage.Id, stageRow ]
          RoleRecords = [ role ]
          LaunchRecords = Map.empty }
    let insert (r: WorkSqliteRecord) =
        { Key = r.Key; ExpectedVersion = None; Value = r.Value }
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records = [ insert project; insert stageRow; insert roleStored ]
          Capacities =
            [ { AccountId = "shared"; ExpectedVersion = None
                Maximum = Some 1; ReservedDelta = 0 } ] } |> ok |> ignore
    { Account = { AccountId = "shared"; Version = 1L; Maximum = 1; Reserved = 0 }
      Projects = [ sourceProject ]
      CompleteProjectSet = true }

// The Prepared alpha stage is not dispatchable, but beta remains an ordinary
// eligible stage until Recovery fences the *complete* account project set.
let private e0PreparedAndAvailablePair (store: WorkSqliteAuthority) =
    let initial = e0Fixture store
    let alpha = initial.Projects.Head
    let betaStage =
        { alpha.Queue.Works.["W-alpha"] with
            Id = "W-beta"; ProjectId = "beta"; SubjectId = "task-W-beta" }
    let clone (row: WorkSqliteRecord) kind id =
        { row with
            Key = key "beta" kind id
            Value = { row.Value with BodyJson = row.Value.BodyJson.Replace("W-alpha", "W-beta") } }
    let betaRole = alpha.RoleRecords.Head
    let betaStageRow = clone alpha.StageRecords.["W-alpha"] WorkSqliteRecordKind.Stage "W-beta"
    let beta =
        { alpha with
            Id = "beta"
            Queue = { alpha.Queue with Works = Map.ofList [ betaStage.Id, betaStage ] }
            Stored = clone alpha.Stored WorkSqliteRecordKind.Project "authority"
            StageRecords = Map.ofList [ "W-beta", betaStageRow ]
            RoleRecords = [ { betaRole with Stored = clone betaRole.Stored WorkSqliteRecordKind.Role "worker" } ] }
    let insert (row: WorkSqliteRecord) =
        { Key = row.Key; ExpectedVersion = None; Value = row.Value }
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records = [ insert beta.Stored; insert betaStageRow; insert beta.RoleRecords.Head.Stored ]
          Capacities = [] } |> ok |> ignore
    let originalAlpha = alpha.StageRecords.["W-alpha"]
    store.Commit
        { OperationId = Guid.NewGuid()
          Guards = []
          Records =
            [ { Key = originalAlpha.Key
                ExpectedVersion = Some originalAlpha.Version
                Value = { originalAlpha.Value with ExternalEffect = WorkSqliteEffect.Prepared } } ]
          Capacities = [] } |> ok |> ignore
    let preparedAlpha =
        { alpha with
            StageRecords = Map.ofList [ "W-alpha", record store "alpha" WorkSqliteRecordKind.Stage "W-alpha" ] }
    { initial with Projects = [ preparedAlpha; beta ] }

[<Test>]
let actual_e0_scheduler_reserve_fails_both_stale_and_fresh_after_recovery_seed () =
    withStore (fun store ->
        let verified = e0Fixture store
        match WorkSchedulerSqlite.reserve store verified (Guid.NewGuid()) "before-recovery" with
        | Ok _ -> ()
        | other -> Assert.Fail(sprintf "E0 fixture was not eligible before seeding: %A" other))
    withStore (fun store ->
        let verified = e0Fixture store
        let recoveredProject = recoveryProject "alpha"
        let original =
            { input [ "alpha" ] with
                Projects =
                    [ { recoveredProject with
                            GraphVersion = verified.Projects.Head.Graph.Version
                            Works = [ { work "alpha" with Generation = 1 } ] } ] }
        let source = WorkRecoverySqlite.fromScheduler verified
        WorkRecoverySqlite.seed store source original (Guid.NewGuid()) |> ok |> ignore
        // E0 verifies its old in-memory snapshot but loses the transaction
        // because the Recovery seeding changed the project + capacity versions.
        match WorkSchedulerSqlite.reserve store verified (Guid.NewGuid()) "stale-launch" with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Stale Scheduler bypassed Recovery CAS: %A" other)
        let p = verified.Projects.Head
        let freshProject =
            { p with
                Stored = record store "alpha" WorkSqliteRecordKind.Project "authority"
                StageRecords = Map.ofList [ "W-alpha", record store "alpha" WorkSqliteRecordKind.Stage "W-alpha" ] }
        let fresh =
            { verified with
                Account = store.ReadCapacity "shared" |> ok |> Option.get
                Projects = [ freshProject ] }
        // A new snapshot cannot bypass the coverage downgrade, regardless
        // of whether it reconstructs its Work projection as Available.
        match WorkSchedulerSqlite.reserve store fresh (Guid.NewGuid()) "fresh-launch" with
        | Error(SqliteSchedulerError.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Fresh Scheduler bypassed Recovery coverage: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 0))

[<Test>]
let actual_e0_two_project_prepared_effect_fences_otherwise_eligible_sibling () =
    // Before Recovery, beta is genuinely eligible: alpha's Prepared effect
    // cannot be treated as a reason that beta would already be rejected.
    withStore (fun store ->
        let verified = e0PreparedAndAvailablePair store
        match WorkSchedulerSqlite.reserve store verified (Guid.NewGuid()) "pre-fence-beta" with
        | Ok outcome -> Assert.That(outcome.ProjectId, Is.EqualTo "beta")
        | other -> Assert.Fail(sprintf "Settled sibling was not eligible before Recovery: %A" other))
    withStore (fun store ->
        let verified = e0PreparedAndAvailablePair store
        let original =
            { input [ "alpha"; "beta" ] with
                Projects =
                    verified.Projects
                    |> List.map (fun p ->
                        { recoveryProject p.Id with
                            GraphVersion = p.Graph.Version
                            Works = [ { work p.Id with Generation = 1 } ] }) }
        let source = WorkRecoverySqlite.fromScheduler verified
        let seedId = Guid.NewGuid()
        WorkRecoverySqlite.seed store source original seedId |> ok |> ignore
        let alphaStage = record store "alpha" WorkSqliteRecordKind.Stage "W-alpha"
        Assert.That(alphaStage.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Prepared)
        for project in [ "alpha"; "beta" ] do
            let projectRow = record store project WorkSqliteRecordKind.Project "authority"
            Assert.That(strProp projectRow "coverage", Is.EqualTo "Recovery")
            let stage = record store project WorkSqliteRecordKind.Stage ("W-" + project)
            Assert.That(strProp stage "state", Is.EqualTo "Blocked")
            Assert.That(strProp stage "gate", Is.EqualTo "SourceRecoveryBlocked")
        match WorkSchedulerSqlite.reserve store verified (Guid.NewGuid()) "stale-beta" with
        | Error(SqliteSchedulerError.Storage(WorkSqliteFailure.StaleVersion _)) -> ()
        | other -> Assert.Fail(sprintf "Stale E0 escaped project-wide fence: %A" other)
        let freshProjects =
            verified.Projects
            |> List.map (fun p ->
                { p with
                    Stored = record store p.Id WorkSqliteRecordKind.Project "authority"
                    StageRecords =
                        p.StageRecords
                        |> Map.map (fun id _ -> record store p.Id WorkSqliteRecordKind.Stage id) })
        let fresh =
            { verified with
                Account = store.ReadCapacity "shared" |> ok |> Option.get
                Projects = freshProjects }
        match WorkSchedulerSqlite.reserve store fresh (Guid.NewGuid()) "fresh-beta" with
        | Error(SqliteSchedulerError.Incomplete _) -> ()
        | other -> Assert.Fail(sprintf "Fresh E0 escaped project-wide Recovery coverage: %A" other)
        Assert.That((store.ReadCapacity "shared" |> ok |> Option.get).Reserved, Is.EqualTo 0)
        Assert.That((WorkRecoverySqlite.seed store source original seedId |> ok).Receipt.Replayed, Is.True))
