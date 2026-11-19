module WorkStatefulRolloutAcceptanceContractTests

open System
open System.IO
open NUnit.Framework
open Server
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts

// I0 is a disposable/default-off acceptance fixture, not authenticated original-Git ingress.
let private sourceSha = String.replicate 40 "a"
let private reviewSha = String.replicate 40 "b"
let private integrationSha = String.replicate 40 "c"
let private evidenceSha = String.replicate 64 "d"
let private witnessId = Guid.Parse("9a3a2222-7cf5-4bf0-81bf-a409410c8a11")
let private projects = [ "project-alpha"; "project-beta" ]
let private sharedWorkId = "shared-work"

let private ok = function
    | Ok value -> value
    | Error failure ->
        Assert.Fail(sprintf "Unexpected default-off Work failure: %A" failure)
        Unchecked.defaultof<_>

let private withStore action =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-i0-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    let path = Path.Combine(directory, "work.sqlite")
    try
        let store = WorkSqliteAuthority(path)
        store.Initialize() |> ok |> ignore
        store.InitializeProvenanceV2() |> ok |> ignore
        action store path
    finally
        Directory.Delete(directory, true)

let private key project kind id : WorkSqliteRecordKey =
    { ProjectId = project; Kind = kind; EntityId = id }

let private mutation project kind id effect : WorkSqliteProvenancedMutation =
    let body =
        match kind with
        | WorkSqliteRecordKind.Work ->
            """{"proposalState":"Open","state":"Open","requiredForParent":true}"""
        | WorkSqliteRecordKind.Stage ->
            """{"state":"Available","queueId":"implementation","proposalState":"Open","gate":"Clear"}"""
        | WorkSqliteRecordKind.ExternalEffect ->
            """{"state":"Prepared","oneShotOperationId":"9a3a2222-7cf5-4bf0-81bf-a409410c8a11"}"""
        | _ -> """{"state":"default-off-test-fixture"}"""
    { Mutation =
        { Key = key project kind id
          ExpectedVersion = None
          Value =
            { BodyJson = body
              GraphVersion = 2L
              StageGeneration = 1L
              ClaimGeneration = 0L
              StopGeneration = 1L
              RoleVersion = 1L
              SourceSha = Some sourceSha
              ReviewSha = if kind = WorkSqliteRecordKind.Review then Some reviewSha else None
              IntegrationSha = if kind = WorkSqliteRecordKind.Integration then Some integrationSha else None
              ExternalEffect = effect } }
      Source =
        Some
            { RepositoryId = "original-repository-fixture"
              SourceRef = "refs/heads/main"
              SourceCommit = sourceSha
              VerificationReceiptId = witnessId
              EvidenceSha256 = evidenceSha } }

let private physicalRows () =
    [ for project in projects do
          yield mutation project WorkSqliteRecordKind.Project "authority" WorkSqliteEffect.NoneRecorded
          yield mutation project WorkSqliteRecordKind.Work sharedWorkId WorkSqliteEffect.NoneRecorded
          yield mutation project WorkSqliteRecordKind.Stage sharedWorkId WorkSqliteEffect.NoneRecorded
          yield mutation project WorkSqliteRecordKind.Launch ("launch-" + project) WorkSqliteEffect.NoneRecorded
          yield mutation project WorkSqliteRecordKind.Role "engineer" WorkSqliteEffect.NoneRecorded
          yield mutation project WorkSqliteRecordKind.Review ("review-" + project) WorkSqliteEffect.NoneRecorded
          yield mutation project WorkSqliteRecordKind.Integration ("integration-" + project) WorkSqliteEffect.NoneRecorded
          yield mutation project WorkSqliteRecordKind.ExternalEffect ("effect-" + project)
                    (if project = "project-alpha" then WorkSqliteEffect.Prepared else WorkSqliteEffect.Unknown) ]

[<Test>]
let g1_two_project_duplicate_work_ids_one_account_and_exact_provenance_survive_restart () =
    withStore (fun store path ->
        let rows = physicalRows ()
        let operation = Guid.Parse("bb77ef08-16ce-4f09-a875-d919da4b0d72")
        let capacity =
            [ { AccountId = "shared-account"
                ExpectedVersion = None
                Maximum = Some 1
                ReservedDelta = 0 } ]
        let committed = store.CommitProvenanced(operation, [], rows, capacity) |> ok
        Assert.That(committed.Sequence, Is.EqualTo 1L)
        let secondClient = WorkSqliteAuthority(path)
        for project in projects do
            let sameId = key project WorkSqliteRecordKind.Work sharedWorkId
            let saved = secondClient.ReadRecord sameId |> ok |> Option.get
            Assert.That(saved.Value.SourceSha, Is.EqualTo(Some sourceSha))
            Assert.That(saved.Version, Is.EqualTo 1L)
            Assert.That(secondClient.ReadSourceProvenance sameId |> ok,
                        Is.EqualTo((rows |> List.find (fun row -> row.Mutation.Key = sameId)).Source))
            let review = secondClient.ReadRecord (key project WorkSqliteRecordKind.Review ("review-" + project))
                         |> ok |> Option.get
            let integrated = secondClient.ReadRecord
                                 (key project WorkSqliteRecordKind.Integration ("integration-" + project))
                             |> ok |> Option.get
            Assert.That(review.Value.ReviewSha, Is.EqualTo(Some reviewSha))
            Assert.That(integrated.Value.IntegrationSha, Is.EqualTo(Some integrationSha))
        let prepared = secondClient.ReadRecord (key "project-alpha" WorkSqliteRecordKind.ExternalEffect "effect-project-alpha")
                       |> ok |> Option.get
        let unknown = secondClient.ReadRecord (key "project-beta" WorkSqliteRecordKind.ExternalEffect "effect-project-beta")
                      |> ok |> Option.get
        Assert.That(prepared.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Prepared)
        Assert.That(unknown.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Unknown)
        let replay = secondClient.CommitProvenanced(operation, [], rows, capacity) |> ok
        Assert.That(replay.Replayed, Is.True)
        Assert.That(replay.Sequence, Is.EqualTo committed.Sequence)
        Assert.That(secondClient.CurrentSequence() |> ok, Is.EqualTo 1L)
        Assert.That((secondClient.ReadCapacity "shared-account" |> ok |> Option.get).Maximum, Is.EqualTo 1)
        let changed = { rows.Head with Source = Some { rows.Head.Source.Value with SourceRef = "refs/heads/other" } }
        match secondClient.CommitProvenanced(operation, [], changed :: rows.Tail, capacity) with
        | Error(WorkSqliteFailure.OperationIdReused _) -> ()
        | other -> Assert.Fail(sprintf "Reused UUID must not change provenance: %A" other))

let private specPin = { Version = 1; BlobOid = "spec-pinned-to-source" }

[<Test>]
let g3_draft_proposed_open_requires_verified_lead_and_does_not_emit_early_demand () =
    for project in projects do
        // Identical local Work IDs in different projects have independent proposal versions.
        let draft = WorkProposal.createDraft sharedWorkId specPin |> ok
        Assert.That(WorkProposal.demand true draft, Is.EqualTo WorkProposalDemand.NoDemand, project)
        let submitted = WorkProposal.apply draft.Version WorkProposalCommand.Submit draft |> ok
        Assert.That(submitted.Record.State, Is.EqualTo WorkProposalState.Proposed)
        Assert.That(submitted.Publication.IsNone, Is.True)
        Assert.That(WorkProposal.demand true submitted.Record,
                    Is.EqualTo(WorkProposalDemand.LeadPlanning(sharedWorkId, specPin)))
        let pending = submitted.Record
        let receipt =
            { ReceiptId = "verified-lead-" + project
              WorkId = sharedWorkId
              ExpectedVersion = pending.Version
              Spec = pending.CurrentSpec
              Action = LeadProposalAction.Approve
              AuthorizedPriority = Some 7
              AuthorizedReason = None
              CanonicallyVerified = true }
        match WorkProposal.apply pending.Version
                  (WorkProposalCommand.Approve({ receipt with CanonicallyVerified = false }, 7)) pending with
        | Error WorkProposalError.UnverifiedAuthority -> ()
        | other -> Assert.Fail(sprintf "Unverified approval must fail: %A" other)
        let approved = WorkProposal.apply pending.Version (WorkProposalCommand.Approve(receipt, 7)) pending |> ok
        Assert.That(approved.Record.State, Is.EqualTo WorkProposalState.Open)
        Assert.That(approved.Publication.Value.ApprovedSpec, Is.EqualTo specPin)
        Assert.That(WorkProposal.demand false approved.Record, Is.EqualTo WorkProposalDemand.NoDemand)
        Assert.That(WorkProposal.demand true approved.Record,
                    Is.EqualTo(WorkProposalDemand.ExecutionEligible(sharedWorkId, specPin)))

let private spec name =
    { Title = name; Goal = "Acceptance"; Scope = "Disposable graph"
      AcceptanceCriteria = Set.singleton "Exact gate"; Constraints = Set.empty
      LinkedTaskId = if name = "root" then None else Some("task-" + name) }

let private add id kind parent (graph: WorkCompositionGraph) =
    WorkComposition.apply graph.Version
        (WorkCompositionMutation.Create(id, kind, parent, spec id)) graph |> ok

[<Test>]
let g3_required_descendants_inherit_blocker_but_optional_containment_does_not () =
    let graph =
        WorkComposition.empty
        |> add "root" WorkUnitKind.Project None
        |> add "dependent" WorkUnitKind.System (Some "root")
        |> add "prerequisite" WorkUnitKind.System (Some "root")
        |> add "required" WorkUnitKind.Feature (Some "prerequisite")
        |> add "optional" WorkUnitKind.Feature (Some "prerequisite")
    let graph =
        WorkComposition.apply graph.Version (WorkCompositionMutation.Require("dependent", "prerequisite")) graph |> ok
    let stage id sequence : WorkStage =
        { Id = "stage-" + id; ProjectId = "project-alpha"; SubjectId = "task-" + id
          Kind = WorkKind.Implementation; QueueId = "implementation"; StageGeneration = 1
          State = WorkState.Available; Domain = DomainState.Undecided; Gate = GateState.Clear
          ClaimGeneration = 0; Dependencies = Set.empty; WaivedDependencies = Map.empty
          AvailableAt = sequence; AvailableSequence = sequence; SourceLineage = None
          Push = None; IntegrationAuthorized = false }
    let available = [ stage "optional" 1L; stage "required" 2L; stage "prerequisite" 3L ]
    let queue =
        { Contracts.emptyProject Map.empty with
            GraphVersion = graph.Version
            Works = available |> List.map (fun s -> s.Id, s) |> Map.ofList
            NextAvailability = 3L }
    let ranked =
        WorkBlocking.project
            { ExpectedGraphVersion = graph.Version
              ExpectedProjectVersion = queue.Version
              OptionalUnitIds = Set.singleton "optional" }
            graph queue |> ok
    Assert.That(ranked.BlockingUnits = Set.ofList [ "prerequisite"; "required" ], Is.True)
    let actual = ranked.Eligible |> List.map (fun x -> x.Stage.Id)
    Assert.That((actual = [ "stage-required"; "stage-prerequisite"; "stage-optional" ]), Is.True)
    Assert.That(ranked.Eligible.[2].IsBlocking, Is.False)

[<Test; Category("FutureRed")>]
let red_g2_authenticated_original_git_producer_to_typed_b_ingress () =
    Assert.Fail("RED G2: original Git source/receipt verified by an authenticated producer, not caller-shaped JSON; C1/B owner must supply trusted binding")

[<Test; Category("FutureRed")>]
let red_g4_trusted_complete_eight_kind_json_backup_to_empty_sqlite () =
    Assert.Fail("RED G4: trusted backup Git root, complete typed B body/receipt/audit provenance, deterministic two-project JSON-to-empty-SQLite byte parity and rejection matrix are not integrated")

[<Test; Category("FutureRed")>]
let red_g4_stale_backup_n_newer_source_n_plus_one_persisted_recovery_fence () =
    Assert.Fail("RED G4: restored DB must persist RecoveryRequired before serving; source/review/integration N+1 and unknown prepared effects must block every admission and external effect")

[<Test; Category("FutureRed")>]
let red_g5_authenticated_scoped_complete_work_status_and_backup_recoverability () =
    Assert.Fail("RED G5: H0 read adapter is default-off and reports incomplete backup/recovery status; no end-to-end authenticated Work-first status/recovery proof")
