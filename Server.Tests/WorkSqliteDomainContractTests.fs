module WorkSqliteDomainContractTests

open System
open System.IO
open System.Text.Json.Nodes
open NUnit.Framework
open Server

// Fixture-only B1 structural projections. They are NOT original authenticated
// Git witnesses, F-restorable data or authorization to activate the Scheduler.
let private account = "11111111-1111-4111-8111-111111111111"
let private projectA = "22222222-2222-4222-8222-222222222222"
let private projectB = "33333333-3333-4333-8333-333333333333"
let private ledger = "44444444-4444-4444-8444-444444444444"
let private evidence = "55555555-5555-4555-8555-555555555555"
let private op = "66666666-6666-4666-8666-666666666666"
let private prepare = "77777777-7777-4777-8777-777777777777"
let private receipt = "88888888-8888-4888-8888-888888888888"
let private repository = String.replicate 64 "a"
let private sourceSha = String.replicate 40 "b"
let private reviewSha = String.replicate 40 "c"
let private integrationSha = String.replicate 40 "d"
let private digest = String.replicate 64 "e"
let private reference = "refs/heads/main"

let private text (value: string) : JsonNode = JsonValue.Create(value)
let private n64 (value: int64) : JsonNode = JsonValue.Create(value)
let private flag (value: bool) : JsonNode = JsonValue.Create(value)
let private nil : JsonNode = null
let private strings values : JsonNode =
    let result = JsonArray()
    for value in values do result.Add(text value)
    result

let private set (name: string) (value: JsonNode) (body: JsonObject) =
    body[name] <- value
    body

let private start project =
    JsonObject()
    |> set "schema" (text WorkSqliteDomain.Schema)
    |> set "accountId" (text account)
    |> set "projectId" (text project)
    |> set "ledgerId" (text ledger)
    |> set "sourceSequence" (n64 1L)

let private body kind project id =
    let root = start project
    match kind with
    | WorkSqliteRecordKind.Project ->
        root |> set "coverage" (text "StructuralOnly")
             |> set "queueVersion" (n64 2L)
             |> set "stageIds" (strings [ "work-1" ])
             |> set "roleIds" (strings [ "worker" ])
             |> set "launchIds" (strings [])
             |> set "originalProjectHead" (text sourceSha)
    | WorkSqliteRecordKind.Work ->
        root |> set "workId" (text id) |> set "queueId" (text "implementation")
             |> set "proposalState" (text "Open") |> set "roleId" (text "worker")
             |> set "leadPriority" (n64 1L)
    | WorkSqliteRecordKind.Stage ->
        root |> set "state" (text "Available") |> set "queueId" (text "implementation")
             |> set "gate" (text "Clear") |> set "proposalState" (text "Open")
             |> set "decisionReceiptId" (text receipt) |> set "availableSequence" (n64 5L)
             |> set "dependencies" (strings [ "prerequisite" ])
             |> set "waivedDependencies" (strings []) |> set "claimLaunch" nil
    | WorkSqliteRecordKind.Launch ->
        root |> set "state" (text "ReservedUnregistered") |> set "roleId" (text "worker")
             |> set "credit" (text "work-1") |> set "primaryWorkId" nil
             |> set "everClaimed" (flag false) |> set "stopRequested" (flag false)
             |> set "reservationOperationId" (text op)
             |> set "reservationAccountId" (text account)
             |> set "reservationWorkId" (text "work-1")
             |> set "reservationWorkGeneration" (n64 1L)
             |> set "claimOperationId" nil |> set "claimWorkGeneration" (n64 0L)
    | WorkSqliteRecordKind.Role ->
        root |> set "state" (text "Active") |> set "queues" (strings [ "implementation" ])
             |> set "instructionsDigest" (text digest)
             |> set "evidenceReceiptId" (text evidence)
    | WorkSqliteRecordKind.Review ->
        root |> set "workId" (text "work-1") |> set "frozenCommit" (text reviewSha)
             |> set "decision" (text "Pending") |> set "decisionSequence" (n64 0L)
             |> set "decisionReceiptId" nil
    | WorkSqliteRecordKind.Integration ->
        root |> set "workId" (text "work-1") |> set "sourceCommit" (text sourceSha)
             |> set "integratedCommit" nil |> set "status" (text "Pending")
             |> set "integrationReceiptId" nil
    | WorkSqliteRecordKind.ExternalEffect ->
        root |> set "effectKind" (text "Launch") |> set "effectState" (text "Prepared")
             |> set "operationId" (text op) |> set "prepareOperationId" (text prepare)
             |> set "uncertaintyOperationId" nil |> set "observedReceiptId" nil
             |> set "launchId" (text "launch-1") |> set "workId" (text "work-1")
             |> set "roleId" (text "worker") |> set "roleVersion" (n64 1L)
             |> set "stageGeneration" (n64 1L) |> set "stopGeneration" (n64 1L)
             |> set "repositoryId" (text repository) |> set "sourceRef" (text reference)
             |> set "sourceSha" (text sourceSha) |> set "sourceHead" (text sourceSha)
             |> set "projectHead" (text sourceSha)
             |> set "reservationOperationId" (text receipt)

let private expectation project : WorkSqliteDomain.Expectation =
    { AccountId = account
      ProjectId = project
      OriginalLedgerId = ledger
      RepositoryId = repository
      SourceRef = reference }

let private fixture kind project id (data: JsonObject) : WorkSqliteProvenancedMutation =
    let value : WorkSqliteRecordValue =
        { BodyJson = data.ToJsonString()
          GraphVersion = 2L; StageGeneration = 1L; ClaimGeneration = 1L
          StopGeneration = 1L; RoleVersion = 1L
          SourceSha = Some sourceSha
          ReviewSha = if kind = WorkSqliteRecordKind.Review then Some reviewSha else None
          IntegrationSha = None
          ExternalEffect =
              if kind = WorkSqliteRecordKind.ExternalEffect then WorkSqliteEffect.Prepared
              else WorkSqliteEffect.NoneRecorded }
    { Mutation =
        { Key = { ProjectId = project; Kind = kind; EntityId = id }
          ExpectedVersion = None; Value = value }
      Source =
        Some { RepositoryId = repository; SourceRef = reference
               SourceCommit = sourceSha
               VerificationReceiptId = Guid.Parse evidence
               EvidenceSha256 = digest } }

let private make kind project id = fixture kind project id (body kind project id)

let private accepted context row =
    match WorkSqliteDomain.normalizeStructure context row with
    | Ok result -> result
    | Error error ->
        Assert.Fail(sprintf "Unexpected B1 structural rejection: %A" error)
        Unchecked.defaultof<_>

let private rejected context row =
    match WorkSqliteDomain.normalizeStructure context row with
    | Error(WorkSqliteFailure.InvalidCommand _) -> ()
    | other -> Assert.Fail(sprintf "Unsafe B1 body unexpectedly passed: %A" other)

let private candidates =
    [ WorkSqliteRecordKind.Project, "authority"
      WorkSqliteRecordKind.Work, "work-1"
      WorkSqliteRecordKind.Stage, "work-1"
      WorkSqliteRecordKind.Launch, "launch-1"
      WorkSqliteRecordKind.Role, "worker"
      WorkSqliteRecordKind.Review, "review-1"
      WorkSqliteRecordKind.Integration, "integration-1"
      WorkSqliteRecordKind.ExternalEffect, "launch-1" ]

[<Test>]
let closed_eight_kind_body_projection_is_deterministic_but_never_a_source_attestation () =
    for kind, id in candidates do
        let context = expectation projectA
        let original = make kind projectA id
        let normalized = accepted context original
        Assert.That(normalized, Does.Contain("\"schema\":\"WorkSqliteDomain/B1\""))
        let normalizedInput =
            { original with Mutation = { original.Mutation with
                                           Value = { original.Mutation.Value with BodyJson = normalized } } }
        Assert.That(accepted context normalizedInput, Is.EqualTo normalized)
        let shuffled = JsonObject()
        for item in (body kind projectA id |> Seq.toList |> List.rev) do
            shuffled[item.Key] <- if isNull item.Value then null else item.Value.DeepClone()
        let reordered = { original with Mutation = { original.Mutation with
                                                       Value = { original.Mutation.Value with
                                                                   BodyJson = shuffled.ToJsonString() } } }
        Assert.That(accepted context reordered, Is.EqualTo normalized)

[<Test>]
let every_kind_fails_closed_on_future_schema_extra_keys_nested_bearer_and_duplicate_json () =
    for kind, id in candidates do
        let context = expectation projectA
        let future = body kind projectA id |> set "schema" (text "WorkSqliteDomain/B2")
        rejected context (fixture kind projectA id future)
        let unknown = body kind projectA id |> set "future" (text "unexpected")
        rejected context (fixture kind projectA id unknown)
        let bearer = body kind projectA id |> set "manualGrant" (text "Bearer fixture-secret")
        rejected context (fixture kind projectA id bearer)
        let nested = body kind projectA id |> set "secret" (JsonNode.Parse("""{"nested":{"accessToken":"fixture"}}"""))
        rejected context (fixture kind projectA id nested)
        let original = make kind projectA id
        let duplicate =
            original.Mutation.Value.BodyJson.Replace(
                "\"schema\":", "\"schema\":\"WorkSqliteDomain/B1\",\"schema\":", StringComparison.Ordinal)
        rejected context
            { original with Mutation = { original.Mutation with
                                           Value = { original.Mutation.Value with BodyJson = duplicate } } }

[<Test>]
let original_source_scope_and_missing_receipt_never_promote_caller_strings () =
    let context = expectation projectA
    let good = make WorkSqliteRecordKind.Work projectA "work-1"
    rejected context { good with Mutation = { good.Mutation with
                                               Key = { good.Mutation.Key with ProjectId = projectB } } }
    rejected context (fixture WorkSqliteRecordKind.Work projectA "work-1"
                           (body WorkSqliteRecordKind.Work projectA "work-1" |> set "accountId" (text projectB)))
    rejected context (fixture WorkSqliteRecordKind.Work projectA "work-1"
                           (body WorkSqliteRecordKind.Work projectA "work-1" |> set "projectId" (text projectB)))
    rejected context { good with Source = None }
    rejected context { good with Source = good.Source |> Option.map (fun s -> { s with VerificationReceiptId = Guid.Empty }) }
    rejected context { good with Source = good.Source |> Option.map (fun s -> { s with RepositoryId = digest }) }
    rejected context { good with Source = good.Source |> Option.map (fun s -> { s with SourceRef = "refs/heads/forged" }) }
    rejected context { good with Source = good.Source |> Option.map (fun s -> { s with SourceCommit = reviewSha }) }
    rejected context { good with Mutation = { good.Mutation with
                                               Value = { good.Mutation.Value with SourceSha = None } } }

[<Test>]
let original_ledger_identity_is_bound_for_all_eight_structural_kinds () =
    for kind, id in candidates do
        let context = expectation projectA
        accepted context (make kind projectA id) |> ignore
        let foreign = body kind projectA id
                      |> set "ledgerId" (text "99999999-9999-4999-8999-999999999999")
        rejected context (fixture kind projectA id foreign)

[<Test>]
let pending_and_final_review_decisions_have_disjoint_sequence_receipt_states () =
    let context = expectation projectA
    let pending () = body WorkSqliteRecordKind.Review projectA "review-1"
    accepted context (fixture WorkSqliteRecordKind.Review projectA "review-1" (pending ())) |> ignore
    let pendingWithSequence = pending () |> set "decisionSequence" (n64 17L)
    rejected context (fixture WorkSqliteRecordKind.Review projectA "review-1" pendingWithSequence)
    let pendingWithReceipt = pending () |> set "decisionReceiptId" (text receipt)
    rejected context (fixture WorkSqliteRecordKind.Review projectA "review-1" pendingWithReceipt)
    let pendingWithBoth = pending () |> set "decisionSequence" (n64 17L)
                                 |> set "decisionReceiptId" (text receipt)
    rejected context (fixture WorkSqliteRecordKind.Review projectA "review-1" pendingWithBoth)
    for decision in [ "Accepted"; "ChangesRequested" ] do
        let final = pending () |> set "decision" (text decision)
        rejected context (fixture WorkSqliteRecordKind.Review projectA "review-1" final)
        let withReceipt = pending () |> set "decision" (text decision)
                                     |> set "decisionReceiptId" (text receipt)
        rejected context (fixture WorkSqliteRecordKind.Review projectA "review-1" withReceipt)
        let withSequence = pending () |> set "decision" (text decision)
                                      |> set "decisionSequence" (n64 17L)
        rejected context (fixture WorkSqliteRecordKind.Review projectA "review-1" withSequence)
        let complete = pending () |> set "decision" (text decision)
                                  |> set "decisionSequence" (n64 17L)
                                  |> set "decisionReceiptId" (text receipt)
        accepted context (fixture WorkSqliteRecordKind.Review projectA "review-1" complete) |> ignore

[<Test>]
let review_and_integration_require_frozen_sha_and_current_decision_receipts () =
    let context = expectation projectA
    let review = make WorkSqliteRecordKind.Review projectA "review-1"
    rejected context { review with Mutation = { review.Mutation with
                                                  Value = { review.Mutation.Value with ReviewSha = Some integrationSha } } }
    let verdict = body WorkSqliteRecordKind.Review projectA "review-1"
                  |> set "decision" (text "Accepted")
                  |> set "decisionSequence" (n64 3L)
    rejected context (fixture WorkSqliteRecordKind.Review projectA "review-1" verdict)
    accepted context (fixture WorkSqliteRecordKind.Review projectA "review-1"
                        (verdict |> set "decisionReceiptId" (text receipt))) |> ignore
    let integration = make WorkSqliteRecordKind.Integration projectA "integration-1"
    rejected context { integration with Mutation = { integration.Mutation with
                                                       Value = { integration.Mutation.Value with SourceSha = Some reviewSha } } }
    let integrated = body WorkSqliteRecordKind.Integration projectA "integration-1"
                     |> set "status" (text "Integrated")
                     |> set "integratedCommit" (text integrationSha)
                     |> set "integrationReceiptId" (text receipt)
    rejected context (fixture WorkSqliteRecordKind.Integration projectA "integration-1" integrated)
    let complete = fixture WorkSqliteRecordKind.Integration projectA "integration-1" integrated
    accepted context { complete with Mutation = { complete.Mutation with
                                                    Value = { complete.Mutation.Value with
                                                                IntegrationSha = Some integrationSha } } } |> ignore

[<Test>]
let prepared_unknown_and_terminal_effect_identity_remain_distinct () =
    let context = expectation projectA
    let effect = make WorkSqliteRecordKind.ExternalEffect projectA "launch-1"
    let unknown = body WorkSqliteRecordKind.ExternalEffect projectA "launch-1"
                  |> set "effectState" (text "Unknown")
    rejected context (fixture WorkSqliteRecordKind.ExternalEffect projectA "launch-1" unknown)
    let withId = unknown |> set "uncertaintyOperationId" (text op)
    let repeatedId = fixture WorkSqliteRecordKind.ExternalEffect projectA "launch-1" withId
    let repeatedId = { repeatedId with Mutation = { repeatedId.Mutation with
                                                     Value = { repeatedId.Mutation.Value with
                                                                 ExternalEffect = WorkSqliteEffect.Unknown } } }
    rejected context repeatedId
    withId["uncertaintyOperationId"] <- text receipt
    let candidate = fixture WorkSqliteRecordKind.ExternalEffect projectA "launch-1" withId
    let candidate = { candidate with Mutation = { candidate.Mutation with
                                                   Value = { candidate.Mutation.Value with ExternalEffect = WorkSqliteEffect.Unknown } } }
    accepted context candidate |> ignore
    let terminal = withId |> set "effectState" (text "Observed")
    rejected context { candidate with Mutation = { candidate.Mutation with
                                                     Value = { candidate.Mutation.Value with
                                                                 ExternalEffect = WorkSqliteEffect.Observed;
                                                                 BodyJson = terminal.ToJsonString() } } }

[<Test>]
let claim_stage_role_and_restoration_source_values_are_not_optional () =
    let context = expectation projectA
    let stage = body WorkSqliteRecordKind.Stage projectA "work-1"
                |> set "state" (text "Claimed")
    rejected context (fixture WorkSqliteRecordKind.Stage projectA "work-1" stage)
    let invalidWaiver = body WorkSqliteRecordKind.Stage projectA "work-1"
                        |> set "waivedDependencies" (strings [ "unlisted" ])
    rejected context (fixture WorkSqliteRecordKind.Stage projectA "work-1" invalidWaiver)
    let role = make WorkSqliteRecordKind.Role projectA "worker"
    rejected context { role with Mutation = { role.Mutation with
                                                Value = { role.Mutation.Value with RoleVersion = 0L } } }
    let launch = body WorkSqliteRecordKind.Launch projectA "launch-1"
                 |> set "everClaimed" (flag true)
    rejected context (fixture WorkSqliteRecordKind.Launch projectA "launch-1" launch)
    let missing = body WorkSqliteRecordKind.Project projectA "authority"
    missing.Remove("ledgerId") |> ignore
    rejected context (fixture WorkSqliteRecordKind.Project projectA "authority" missing)
    let absentSequence = body WorkSqliteRecordKind.Project projectA "authority"
    absentSequence.Remove("sourceSequence") |> ignore
    rejected context (fixture WorkSqliteRecordKind.Project projectA "authority" absentSequence)

[<Test>]
let same_work_id_in_two_projects_preserves_one_account_global_capacity () =
    let folder = Path.Combine(Path.GetTempPath(), "work-b1-domain-fixture-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(folder) |> ignore
    try
        let store = WorkSqliteAuthority(Path.Combine(folder, "B.sqlite"))
        match store.Initialize(), store.InitializeProvenanceV2() with
        | Ok 1, Ok 2 -> ()
        | other -> Assert.Fail(sprintf "Cannot initialize disposable B v2 fixture: %A" other)
        let left = make WorkSqliteRecordKind.Work projectA "work-1"
        let right = make WorkSqliteRecordKind.Work projectB "work-1"
        accepted (expectation projectA) left |> ignore
        accepted (expectation projectB) right |> ignore
        let accountCapacity : WorkSqliteCapacityMutation =
            { AccountId = account; ExpectedVersion = None
              Maximum = Some 1; ReservedDelta = 1 }
        match store.CommitProvenanced(Guid.NewGuid(), [], [ left; right ], [ accountCapacity ]) with
        | Error error -> Assert.Fail(sprintf "Disposable B v2 fixture CAS rejected: %A" error)
        | Ok receipt ->
            Assert.That(receipt.Sequence, Is.EqualTo 1L)
            for project in [ projectA; projectB ] do
                let key = { ProjectId = project; Kind = WorkSqliteRecordKind.Work; EntityId = "work-1" }
                match store.ReadRecord key with
                | Ok(Some row) -> Assert.That(row.Version, Is.EqualTo 1L)
                | other -> Assert.Fail(sprintf "Missing scoped Work fixture: %A" other)
            match store.ReadCapacity account with
            | Ok(Some capacity) ->
                Assert.That(capacity.Maximum, Is.EqualTo 1)
                Assert.That(capacity.Reserved, Is.EqualTo 1)
            | other -> Assert.Fail(sprintf "Missing shared account capacity: %A" other)
    finally
        Directory.Delete(folder, true)
