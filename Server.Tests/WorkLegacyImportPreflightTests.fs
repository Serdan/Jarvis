module ServerWorkLegacyImportPreflightTests

open System
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Server

let private shaA = String.replicate 40 "a"
let private shaB = String.replicate 40 "b"
let private shaC = String.replicate 40 "c"
let private digest = String.replicate 64 "e"
let private projectOne = Guid.Parse "11111111-1111-4111-8111-111111111111"
let private projectTwo = Guid.Parse "22222222-2222-4222-8222-222222222222"
let private ledgerOne = Guid.Parse "33333333-3333-4333-8333-333333333333"
let private ledgerTwo = Guid.Parse "44444444-4444-4444-8444-444444444444"
let private accountId = Guid.Parse "55555555-5555-4555-8555-555555555555"
let private accountLedgerId = Guid.Parse "66666666-6666-4666-8666-666666666666"

let private operation =
    { OperationId = Guid.Parse "77777777-7777-4777-8777-777777777777"
      Sequence = 1L
      RequestSha256 = digest
      ResultSha256 = digest
      Kind = "verified-ledger-operation" }

let private head =
    { PinnedCommit = shaA
      ObservedCommit = shaA
      AncestryVerified = true
      HistoryComplete = true
      SourceHighWater = 1L
      ObservedHighWater = 1L }

let private stage (projectId: Guid) id subject kind queue state domain =
    { Id = id
      ProjectId = projectId.ToString("D")
      SubjectId = subject
      Kind = kind
      QueueId = queue
      StageGeneration = 1
      State = state
      Domain = domain
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.empty
      WaivedDependencies = Map.empty
      AvailableAt = 1L
      AvailableSequence = 1L
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private subscriptions =
    Map.ofList
        [ "Worker", Set.singleton "implementation"
          "Reviewer", Set.singleton "review"
          "Integrator", Set.singleton "integration"
          "Lead", Set.singleton "planning" ]

let private origin (work: WorkStage) role commit =
    { WorkId = work.Id
      StageGeneration = work.StageGeneration
      SubjectId = work.SubjectId
      SourceHead = shaA
      SourceHighWater = 1L
      SourceAncestryVerified = true
      RepositoryId = "repository-identity"
      SourceRef = "refs/heads/verified-source"
      SourceCommit = commit
      RoleId = role
      RoleVersion = 1
      GraphVersion = 0
      ContextDigest = digest }

let private project projectId ledgerId workId =
    let work = stage projectId workId ("task-" + workId)
                     WorkKind.Implementation "implementation" WorkState.Available DomainState.Undecided
    let queue = { emptyProject subscriptions with Works = Map.ofList [ work.Id, work ] }
    { ProjectId = projectId
      LedgerId = ledgerId
      LedgerEpoch = 1
      Head = head
      Queue = queue
      Graph = WorkComposition.empty
      Origins = [ origin work "Worker" shaB ]
      Reviews = []
      Integrations = []
      Terminals = []
      Validations = []
      Operations = [ operation ] }

let private twoProjects () =
    { Account =
        { AccountId = accountId
          LedgerId = accountLedgerId
          LedgerEpoch = 1
          Head = head
          MaxConcurrent = 2
          Tokens = []
          Operations = [ operation ] }
      Projects =
        [ project projectOne ledgerOne "I1"
          project projectTwo ledgerTwo "I2" ] }

let private modifyOne f (input: WorkLegacyImportInspection) =
    { input with
        Projects =
            input.Projects
            |> List.map (fun p -> if p.ProjectId = projectOne then f p else p) }

let private findOne (input: WorkLegacyImportInspection) =
    input.Projects |> List.find (fun p -> p.ProjectId = projectOne)

let private expectsGap predicate result =
    match result with
    | Error gaps ->
        Assert.That(gaps |> List.exists predicate, Is.True, sprintf "Expected gap not found in %A" gaps)
    | Ok index -> Assert.Fail(sprintf "Unsafe preflight unexpectedly succeeded: %A" index)

let private reviewAndIntegration (input: WorkLegacyImportInspection) =
    modifyOne (fun p ->
        let review = stage p.ProjectId "R" "task-I1"
                         WorkKind.FrozenReview "review" WorkState.Blocked
                         (DomainState.FinalizedPendingTerminal "accepted-review")
        let integration = stage p.ProjectId "T" "task-I1"
                              WorkKind.Integration "integration" WorkState.Completed
                              (DomainState.Integrated "integrated-receipt")
        { p with
            Queue =
                { p.Queue with
                    Works = p.Queue.Works
                            |> Map.add review.Id review
                            |> Map.add integration.Id { integration with IntegrationAuthorized = true } }
            Origins = p.Origins @ [ origin review "Reviewer" shaB; origin integration "Integrator" shaB ]
            Reviews =
                [ { WorkId = "R"
                    SubjectId = "task-I1"
                    ReceiptId = "accepted-review"
                    FrozenCommit = shaB
                    DecisionSequence = 1L
                    Accepted = true } ]
            Integrations =
                [ { WorkId = "T"
                    ReviewWorkId = "R"
                    ReceiptId = "integrated-receipt"
                    SourceCommit = shaB
                    TargetRef = "refs/heads/main"
                    IntegratedCommit = shaC
                    SourceAncestryVerified = true } ] }) input

[<Test>]
let c0_two_project_account_fixture_is_read_only_and_stably_indexed () =
    let inspected = twoProjects () |> WorkLegacyImportPreflight.inspect
    match inspected with
    | Error gaps -> Assert.Fail(sprintf "Unexpected preflight gaps: %A" gaps)
    | Ok index ->
        Assert.That(index.Projects.Length, Is.EqualTo 2)
        Assert.That(index.Stages.Length, Is.EqualTo 2)
        Assert.That(index.OperationIds.Length, Is.EqualTo 3)
        Assert.That(index.ReservedCapacity, Is.Zero)
        Assert.That(index.AccountHead, Is.EqualTo shaA)

[<Test>]
let c0_preflight_preserves_exact_accepted_review_and_integration_provenance () =
    match twoProjects () |> reviewAndIntegration |> WorkLegacyImportPreflight.inspect with
    | Error gaps -> Assert.Fail(sprintf "Unexpected review/integration gaps: %A" gaps)
    | Ok index ->
        Assert.That(index.FrozenReviews, Has.Length.EqualTo 1)
        Assert.That(index.Integrations, Has.Length.EqualTo 1)
        Assert.That(index.Integrations.Head, Is.EqualTo((projectOne, "T", shaB, shaC)))

[<Test>]
let c0_ambiguous_canonical_project_or_duplicate_ledger_is_rejected () =
    let fixture = twoProjects ()
    let duplicate = fixture.Projects.Head
    let invalid = { fixture with Projects = duplicate :: fixture.Projects }
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.InvalidIdentity _ -> true | _ -> false)

[<Test>]
let c0_stale_git_head_and_incomplete_or_unverified_history_fail_closed () =
    let stale = twoProjects () |> modifyOne (fun p ->
        { p with Head = { p.Head with ObservedCommit = shaC } })
    stale |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.HeadMismatch _ -> true | _ -> false)
    let incomplete = twoProjects () |> modifyOne (fun p ->
        { p with Head = { p.Head with HistoryComplete = false; ObservedHighWater = 0L } })
    incomplete |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.IncompleteHistory _ -> true | _ -> false)

[<Test>]
let c0_missing_queue_coverage_and_source_origin_never_become_approval () =
    let coverage = twoProjects () |> modifyOne (fun p ->
        { p with Queue = { p.Queue with SourceCovered = false } })
    coverage |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.IncompleteHistory _ -> true | _ -> false)
    let absent = twoProjects () |> modifyOne (fun p -> { p with Origins = [] })
    absent |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.MissingOrigin _ -> true | _ -> false)

[<Test>]
let c0_role_or_graph_binding_mismatch_refuses_original_source_authority () =
    let invalid = twoProjects () |> modifyOne (fun p ->
        let origins =
            p.Origins
            |> List.map (fun o -> { o with RoleId = "Reviewer"; GraphVersion = 8 })
        { p with Origins = origins })
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.MissingOrigin _ -> true | _ -> false)

[<Test>]
let c0_duplicate_or_replayed_operation_receipt_rejected_even_if_payload_identical () =
    let invalid = twoProjects () |> modifyOne (fun p ->
        { p with
            Operations = [ operation; { operation with Sequence = 2L } ]
            Head = { p.Head with SourceHighWater = 2L; ObservedHighWater = 2L } })
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.DuplicateOperation _ -> true | _ -> false)

[<Test>]
let c0_missing_frozen_review_and_integration_receipts_are_not_inferred_from_stage () =
    let fixture = twoProjects () |> reviewAndIntegration
    let noReview = fixture |> modifyOne (fun p -> { p with Reviews = [] })
    noReview |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.MissingReviewDecision _ -> true | _ -> false)
    let noIntegration = fixture |> modifyOne (fun p -> { p with Integrations = [] })
    noIntegration |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.MissingIntegration _ -> true | _ -> false)

[<Test>]
let c0_unaccepted_review_never_authorizes_integration () =
    let invalid = twoProjects () |> reviewAndIntegration
                  |> modifyOne (fun p ->
                      { p with Reviews = p.Reviews |> List.map (fun r -> { r with Accepted = false }) })
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.MissingReviewDecision _ -> true | _ -> false)

[<Test>]
let c0_unmatched_claim_generation_is_rejected () =
    let invalid = twoProjects () |> modifyOne (fun p ->
        let works = p.Queue.Works |> Map.map (fun _ w ->
            { w with State = WorkState.Claimed "missing-launch"; ClaimGeneration = 3 })
        { p with Queue = { p.Queue with Works = works } })
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.InvalidStage _ -> true | _ -> false)

let private withStoppedClaim (input: WorkLegacyImportInspection) =
    let launchId = "one-running-launch"
    let tokenId = "one-account-token"
    let changed =
        input |> modifyOne (fun p ->
        let primary = p.Queue.Works.["I1"]
        let claimed = { primary with State = WorkState.Claimed launchId; ClaimGeneration = 2 }
        let launch =
            { Id = launchId
              ProjectId = p.ProjectId.ToString("D")
              RoleId = "Worker"
              RoleVersion = 1
              AccountTokenId = tokenId
              State = LaunchState.Registered("uuid", "session")
              Lease = Some { LastSeenAt = 9L; Generation = 4; StopRequested = true }
              Version = 2
              Credit = Some "I1"
              PrimaryWorkId = Some "I1"
              EverClaimed = true
              AncillaryMessages = Set.empty
              DispatchCount = 1 }
        { p with
            Queue =
                { p.Queue with
                    Works = p.Queue.Works.Add("I1", claimed)
                    Launches = Map.ofList [ launchId, launch ] } })
    { changed with
        Account =
            { changed.Account with
                Tokens =
                    [ { LaunchId = launchId
                        ProjectId = projectOne
                        AccountTokenId = tokenId
                        Released = false
                        TerminalReceipt = None } ] } }

[<Test>]
let c0_stop_and_claim_generation_are_retained_and_account_capacity_is_global () =
    match twoProjects () |> withStoppedClaim |> WorkLegacyImportPreflight.inspect with
    | Error gaps -> Assert.Fail(sprintf "Unexpected claim/STOP gaps: %A" gaps)
    | Ok index ->
        Assert.That(index.StoppedLaunches, Is.EqualTo(box [ projectOne, "one-running-launch", 4 ]))
        Assert.That(index.ReservedCapacity, Is.EqualTo 1)
    let invalid = twoProjects () |> withStoppedClaim
    let invalid = { invalid with Account = { invalid.Account with MaxConcurrent = 0 } }
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.AccountCapacityConflict _ -> true | _ -> false)

[<Test>]
let c0_missing_account_token_or_unproved_capacity_release_is_rejected () =
    let fixture = twoProjects () |> withStoppedClaim
    { fixture with Account = { fixture.Account with Tokens = [] } }
    |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.AccountCapacityConflict _ -> true | _ -> false)
    let invalid =
        { fixture with
            Account =
                { fixture.Account with
                    Tokens =
                        fixture.Account.Tokens
                        |> List.map (fun token ->
                            { token with Released = true; TerminalReceipt = None }) } }
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.AccountCapacityConflict _ -> true | _ -> false)

[<Test>]
let c0_unknown_original_push_is_never_reinterpreted_as_terminal () =
    let invalid = twoProjects () |> modifyOne (fun p ->
        let works =
            p.Queue.Works |> Map.map (fun _ w ->
                let push =
                    { OperationId = "push-unknown"
                      AcceptedSourceSha = shaB
                      TargetRef = "refs/heads/main"
                      Outcome = PushOutcome.Unknown }
                { w with Push = Some push })
        { p with Queue = { p.Queue with Works = works } })
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.UnresolvedExternalEffect _ -> true | _ -> false)

[<Test>]
let c0_frozen_review_sha_must_match_original_stage_source () =
    let invalid =
        twoProjects ()
        |> reviewAndIntegration
        |> modifyOne (fun p ->
            let origins =
                p.Origins |> List.map (fun o ->
                    if o.WorkId = "R" then { o with SourceCommit = shaC } else o)
            { p with Origins = origins })
    invalid |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.MissingReviewDecision _ -> true | _ -> false)

[<Test>]
let c0_original_account_release_needs_independently_verified_project_terminal () =
    let fixture = twoProjects () |> withStoppedClaim
    let fixture =
        { fixture with
            Account =
                { fixture.Account with
                    Tokens =
                        fixture.Account.Tokens |> List.map (fun token ->
                            { token with Released = true; TerminalReceipt = Some "terminal-receipt" }) } }
    fixture |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.AccountCapacityConflict _ -> true | _ -> false)

[<Test>]
let c0_project_terminal_provenance_preserves_capacity_release_without_active_claim () =
    let fixture =
        twoProjects ()
        |> withStoppedClaim
        |> modifyOne (fun p ->
            let work = { p.Queue.Works.["I1"] with State = WorkState.Completed }
            let launch = { p.Queue.Launches.["one-running-launch"] with
                               State = LaunchState.TerminalPendingRelease "terminal-receipt" }
            { p with
                Queue =
                    { p.Queue with
                        Works = p.Queue.Works.Add("I1", work)
                        Launches = p.Queue.Launches.Add("one-running-launch", launch) }
                Terminals =
                    [ { LaunchId = launch.Id
                        AccountTokenId = launch.AccountTokenId
                        ReceiptId = "terminal-receipt"
                        OperationId = operation.OperationId
                        TerminalHead = shaA
                        TerminalBlobOid = shaB
                        AncestryVerified = true
                        LaunchGeneration = 4L } ] })
    let fixture =
        { fixture with
            Account =
                { fixture.Account with
                    Tokens = fixture.Account.Tokens |> List.map (fun token ->
                        { token with Released = true; TerminalReceipt = Some "terminal-receipt" }) } }
    match fixture |> WorkLegacyImportPreflight.inspect with
    | Error gaps -> Assert.Fail(sprintf "Verified terminal incorrectly rejected: %A" gaps)
    | Ok index ->
        Assert.That(index.ReservedCapacity, Is.Zero)
        Assert.That(index.StoppedLaunches, Has.Length.EqualTo 1)

[<Test>]
let c0_tampered_terminal_blob_or_generation_blocks_release () =
    let fixture =
        twoProjects ()
        |> withStoppedClaim
        |> modifyOne (fun p ->
            let terminal =
                { LaunchId = "one-running-launch"
                  AccountTokenId = "one-account-token"
                  ReceiptId = "terminal-receipt"
                  OperationId = operation.OperationId
                  TerminalHead = shaA
                  TerminalBlobOid = "not-a-git-object"
                  AncestryVerified = true
                  LaunchGeneration = 99L }
            { p with Terminals = [ terminal ] })
    let fixture =
        { fixture with
            Account =
                { fixture.Account with
                    Tokens = fixture.Account.Tokens |> List.map (fun token ->
                        { token with Released = true; TerminalReceipt = Some "terminal-receipt" }) } }
    fixture |> WorkLegacyImportPreflight.inspect
    |> expectsGap (function WorkLegacyImportGap.AccountCapacityConflict _ -> true | _ -> false)

let private withOfflineFrozenProjects (input: WorkLegacyImportInspection) =
    { input with
        Projects =
            input.Projects |> List.map (fun p ->
                { p with Queue = { p.Queue with Migration = MigrationEpoch.Frozen } }) }

let private offlineFence (input: WorkLegacyImportInspection) =
    { AccountHead = input.Account.Head.PinnedCommit
      ProjectHeads = input.Projects |> List.map (fun p -> p.ProjectId, p.Head.PinnedCommit)
      FenceVersion = 2L
      FenceOperationId = Guid.Parse "88888888-8888-4888-8888-888888888888" }

let private withDisposableSqlite (act: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-work-import-c1-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    File.WriteAllText(Path.Combine(root, WorkLegacyImportSqlite.DisposableMarker),
                      WorkLegacyImportSqlite.DisposableMarkerContent)
    let path = Path.Combine(root, "work.sqlite")
    try act path
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private offlineImportOperation = Guid.Parse "99999999-9999-4999-8999-999999999999"

let private imported input fence path operationId =
    match WorkLegacyImportSqlite.commitOfflineFixture path operationId input fence with
    | Ok result -> result
    | Error gap -> failwithf "Offline import unexpectedly failed: %A" gap

[<Test>]
let c1_offline_sqlite_atomic_two_project_import_restart_and_idempotent_replay () =
    withDisposableSqlite (fun path ->
        let input = twoProjects () |> withOfflineFrozenProjects
        let fence = offlineFence input
        let receipt = imported input fence path offlineImportOperation
        Assert.That(receipt.Replayed, Is.False)
        Assert.That(receipt.SqliteSequence, Is.EqualTo 1L)
        let db = WorkSqliteAuthority(path)
        match db.ReadCapacity(input.Account.AccountId.ToString("D")) with
        | Ok(Some capacity) ->
            Assert.That(capacity.Maximum, Is.EqualTo 2)
            Assert.That(capacity.Reserved, Is.Zero)
        | result -> Assert.Fail(sprintf "Missing global account capacity: %A" result)
        for project in input.Projects do
            let key =
                { ProjectId = project.ProjectId.ToString("D")
                  Kind = WorkSqliteRecordKind.Project
                  EntityId = "legacy-queue" }
            match db.ReadRecord key with
            | Ok(Some record) ->
                Assert.That(record.Version, Is.EqualTo 1L)
                Assert.That(record.Value.BodyJson, Does.Contain("Migration"))
                Assert.That(record.Value.SourceSha, Is.EqualTo(Some shaA))
            | result -> Assert.Fail(sprintf "Missing project snapshot: %A" result)
        let restarted = WorkSqliteAuthority(path)
        Assert.That(restarted.CurrentSequence() |> Result.defaultValue -1L, Is.EqualTo 1L)
        let replay = imported input fence path offlineImportOperation
        Assert.That(replay.Replayed, Is.True)
        Assert.That(replay.SqliteSequence, Is.EqualTo 1L)
        Assert.That(restarted.CurrentSequence() |> Result.defaultValue -1L, Is.EqualTo 1L)
        let changedOperation = Guid.Parse "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        match WorkLegacyImportSqlite.commitOfflineFixture path changedOperation input fence with
        | Error(WorkLegacyImportSqliteGap.StorageFailure(WorkSqliteFailure.EntityAlreadyExists _)) -> ()
        | result -> Assert.Fail(sprintf "Non-idempotent second import was not rejected: %A" result)
        Assert.That(restarted.CurrentSequence() |> Result.defaultValue -1L, Is.EqualTo 1L))

[<Test>]
let c1_offline_preflight_and_fence_gate_refuse_unverified_inputs_before_db_creation () =
    withDisposableSqlite (fun path ->
        let input = twoProjects () |> withOfflineFrozenProjects
        let fence = offlineFence input
        let stale = { fence with ProjectHeads = [] }
        match WorkLegacyImportSqlite.commitOfflineFixture path offlineImportOperation input stale with
        | Error(WorkLegacyImportSqliteGap.UnverifiedWriterFence _) -> ()
        | result -> Assert.Fail(sprintf "Unmatched fence accepted: %A" result)
        Assert.That(File.Exists path, Is.False)
        let invalid = modifyOne (fun p -> { p with Origins = [] }) input
        match WorkLegacyImportSqlite.commitOfflineFixture path offlineImportOperation invalid fence with
        | Error(WorkLegacyImportSqliteGap.PreflightRejected _) -> ()
        | result -> Assert.Fail(sprintf "Source provenance missing but import succeeded: %A" result)
        Assert.That(File.Exists path, Is.False))

[<Test>]
let c1_sqlite_transaction_rollback_on_late_second_project_conflict () =
    withDisposableSqlite (fun path ->
        let input = twoProjects () |> withOfflineFrozenProjects
        let fence = offlineFence input
        let db = WorkSqliteAuthority(path)
        Assert.That(db.Initialize() |> Result.defaultValue -1, Is.EqualTo 1)
        let occupiedKey =
            { ProjectId = projectTwo.ToString("D")
              Kind = WorkSqliteRecordKind.Stage
              EntityId = "I2" }
        let seedValue =
            { BodyJson = "{}"
              GraphVersion = 0L
              StageGeneration = 1L
              ClaimGeneration = 0L
              StopGeneration = 0L
              RoleVersion = 0L
              SourceSha = None
              ReviewSha = None
              IntegrationSha = None
              ExternalEffect = WorkSqliteEffect.NoneRecorded }
        let seeded = db.Commit { OperationId = Guid.Parse "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
                                 Guards = []
                                 Records = [ { Key = occupiedKey; ExpectedVersion = None; Value = seedValue } ]
                                 Capacities = [] }
        Assert.That(seeded |> Result.isOk, Is.True)
        match WorkLegacyImportSqlite.commitOfflineFixture path offlineImportOperation input fence with
        | Error(WorkLegacyImportSqliteGap.StorageFailure(WorkSqliteFailure.EntityAlreadyExists _)) -> ()
        | result -> Assert.Fail(sprintf "Late write conflict incorrectly imported: %A" result)
        let firstProjectKey =
            { ProjectId = projectOne.ToString("D")
              Kind = WorkSqliteRecordKind.Project
              EntityId = "legacy-queue" }
        match db.ReadRecord firstProjectKey with
        | Ok None -> ()
        | result -> Assert.Fail(sprintf "Rolled back first project not absent: %A" result)
        match db.ReadCapacity(accountId.ToString("D")) with
        | Ok None -> ()
        | result -> Assert.Fail(sprintf "Rolled back account capacity not absent: %A" result)
        Assert.That(db.CurrentSequence() |> Result.defaultValue -1L, Is.EqualTo 1L))

[<Test>]
let c1_offline_fixture_import_does_not_open_unmarked_or_arbitrary_destinations () =
    let path = Path.Combine(Path.GetTempPath(), "jarvis-not-marked-" + Guid.NewGuid().ToString("N"), "work.sqlite")
    let input = twoProjects () |> withOfflineFrozenProjects
    match WorkLegacyImportSqlite.commitOfflineFixture path offlineImportOperation input (offlineFence input) with
    | Error(WorkLegacyImportSqliteGap.UnsafeDestination _) -> ()
    | result -> Assert.Fail(sprintf "Unmarked DB destination was not rejected: %A" result)
    Assert.That(File.Exists path, Is.False)

[<Test>]
let c1_symlink_database_is_rejected_even_if_dangling () =
    if OperatingSystem.IsLinux() then
        withDisposableSqlite (fun path ->
            let target = Path.Combine(Path.GetTempPath(), "not-a-fixture-" + Guid.NewGuid().ToString("N"), "outside.sqlite")
            File.CreateSymbolicLink(path, target) |> ignore
            let input = twoProjects () |> withOfflineFrozenProjects
            match WorkLegacyImportSqlite.commitOfflineFixture path offlineImportOperation input (offlineFence input) with
            | Error(WorkLegacyImportSqliteGap.UnsafeDestination _) -> ()
            | result -> Assert.Fail(sprintf "Dangling symlink DB path was not rejected: %A" result))
