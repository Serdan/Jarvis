module Client.Tests.WorkQueueCrossProjectHostPreviewTests

open System
open System.Text.Json
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Jarvis.WorkQueues.WorkQueueProducers
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.AccountCapacityLedger
open Client.WorkQueueCrossProjectPlanner
open Client.WorkQueueCrossProjectHostPreview

[<TestFixture>]
type WorkQueueCrossProjectHostPreviewTests() =
    let projectA = "11111111-1111-4111-8111-111111111111"
    let projectB = "22222222-2222-4222-8222-222222222222"
    let launchId = "33333333-3333-4333-8333-333333333333"
    let admission = "44444444-4444-4444-8444-444444444444"
    let originalId = "wq08:dispatch:original-one-shot"
    let role: RoleConfiguration =
        { RoleId = "Worker"
          Version = 4
          Instructions = "Read inbox."
          QueueSubscriptions = set ["implementation"]
          Enabled = true }
    let catalog: RoleCatalog =
        { Revision = 3
          Covered = true
          Current = Map.ofList ["Worker", 4]
          Versions = Map.ofList ["Worker", Map.ofList [4, role]]
          Receipts = Map.empty }

    let stage id project at sequence deps =
        { Id = id
          ProjectId = project
          SubjectId = "task-" + id
          Kind = WorkKind.Implementation
          QueueId = "implementation"
          StageGeneration = 1
          State = WorkState.Available
          Domain = DomainState.Undecided
          Gate = GateState.Clear
          ClaimGeneration = 0
          Dependencies = deps
          WaivedDependencies = Map.empty
          AvailableAt = at
          AvailableSequence = sequence
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }

    let a = stage "work-a" projectA 100L 1L Set.empty
    let b = stage "work-b" projectB 200L 2L Set.empty
    let dependent = stage "work-dependent" projectB 300L 3L (set ["work-b"])

    let project works launches =
        { emptyProject (Map.ofList ["Worker", role.QueueSubscriptions]) with
            QueueCovered = true
            GraphCovered = true
            MessageCovered = true
            SourceCovered = true
            Works = works |> List.map (fun (work: WorkStage) -> work.Id, work) |> Map.ofList
            Launches = launches |> List.map (fun (launch: Launch) -> launch.Id, launch) |> Map.ofList }

    let head id = if id = projectA then String.replicate 40 "a" else String.replicate 40 "b"
    let ledgerId id =
        if id = projectA then "55555555-5555-4555-8555-555555555555"
        else "66666666-6666-4666-8666-666666666666"

    let proof id (value: Project) =
        { ProjectId = id
          LedgerId = ledgerId id
          Epoch = 1
          Head = head id
          ProjectBlobOid = "project-blob-" + id
          ProjectVersion = value.Version
          CatalogBlobOid = "catalog-blob-" + id
          CatalogRevision = catalog.Revision
          ProvisionOperationId = "role-provision" }

    let origin (work: WorkStage) : CanonicalWorkOrigin =
        { SchemaVersion = 1
          ProjectId = work.ProjectId
          WorkId = work.Id
          SourceHead = head work.ProjectId
          SourceHighWater = 12L
          SourceOperationId = "source-" + work.Id
          SourceOperationSequence = 4L
          SourceOperationKind = "ImplementationReady"
          SourceOperationActor = "fixture"
          SourceRequestHash = "request-hash-" + work.Id
          SourceOriginHead = head work.ProjectId
          SourceEntityPath = "tasks/" + work.Id + "/task.json"
          SourceEntityBlobOid = "source-blob"
          SourceEntitySha256 = "source-hash"
          SourceSelector = "ImplementationReady"
          SourceRef = "refs/heads/" + work.Id
          SourceCommit = String.replicate 40 "c"
          RoleId = "Worker"
          RoleVersion = 4
          AccountCreditStatus = "NoCredit"
          Ambiguity = "" }

    let account tokens =
        { Head = String.replicate 40 "e"
          State =
            { SchemaVersion = 1
              AccountId = "77777777-7777-4777-8777-777777777777"
              LedgerId = "88888888-8888-4888-8888-888888888888"
              Epoch = 1
              MigrationPhase = "Activated"
              StateVersion = 1L
              ConfigVersion = 1L
              MaxConcurrentAgents = 1
              NextSequence = 4L
              Tokens = tokens } }

    let token id =
        { LaunchId = launchId
          ProjectId = id
          ProjectLedgerId = ledgerId id
          ProjectLedgerEpoch = 1
          ProjectAuthoritySha256 = "sha-" + id
          RoleId = "Worker"
          RoleConfigVersion = 4L
          AdmissionOperationId = admission
          Status = "Held"
          ReleaseProof = Unchecked.defaultof<_> }

    let launch id state : Launch =
        { Id = launchId
          ProjectId = id
          RoleId = "Worker"
          RoleVersion = 4
          AccountTokenId = admission
          State = state
          Lease = None
          Version = 1
          Credit = None
          PrimaryWorkId = None
          EverClaimed = false
          AncillaryMessages = Set.empty
          DispatchCount = 0 }

    let journal id outcome : OriginalBrowserJournalEntry =
        { Intent =
            { OperationId = originalId
              LaunchId = launchId
              ProjectId = id
              WorkId = "work-a"
              WorkGeneration = 1
              RoleId = "Worker"
              RoleVersion = 4
              RequestFingerprint = "fingerprint"
              Provenance =
                { AccountHead = String.replicate 40 "e"
                  ProjectHead = head id
                  SourceRef = Some "refs/heads/work-a"
                  SourceCommit = Some(String.replicate 40 "c") } }
          Outcome = outcome }

    let projectPort id works launches original =
        let value = project works launches
        let pinned: PinnedRoleView = { Project = value; Catalog = catalog; Proof = proof id value }
        let snapshot: LedgerSnapshot = { Commit = head id; Header = Unchecked.defaultof<_> }
        { Label = id
          ProjectAuthoritySha256 = "sha-" + id
          ReadProject = fun () -> Ok(snapshot, pinned)
          ReadOrigin = fun _ workId ->
              works
              |> List.tryFind (fun (work: WorkStage) -> work.Id = workId)
              |> Option.map (fun work -> origin work, "origin-blob-" + work.Id)
              |> Ok
          ReadOriginal = fun _ -> Ok original }

    let portA = projectPort projectA [a] [] None
    let portB = projectPort projectB [b] [] None

    let run acct projectPorts =
        WorkQueueCrossProjectHostPreview.previewWith
            { CompleteProjectSet = true
              ReadAccount = fun () -> Ok acct
              Projects = projectPorts }

    [<Test>]
    member _.TwoApprovedProjectsPickOneOldestWithPinnedRoleAndSource() =
        let result = run (account [||]) [portB; portA]
        let reversed = run (account [||]) [portA; portB]
        Assert.That(result.CanDispatch, Is.False)
        Assert.That(result.Mode, Is.EqualTo("RealCrossProjectReadOnlyDefaultOff"))
        Assert.That(result.ProjectSetComplete, Is.True)
        Assert.That(result.MaxConcurrentAgents, Is.EqualTo(Some 1))
        Assert.That(result.OccupiedAgents, Is.EqualTo(Some 0))
        Assert.That(result.Projects.Length, Is.EqualTo(2))
        Assert.That(result.Projects |> List.forall _.Head.IsSome, Is.True)
        let winner = result.SimulatedWinner |> Option.get
        Assert.That(winner.ProjectId, Is.EqualTo(projectA))
        Assert.That(winner.WorkId, Is.EqualTo(a.Id))
        Assert.That(winner.ProjectHead, Is.EqualTo(head projectA))
        Assert.That(winner.RoleVersion, Is.EqualTo(4))
        Assert.That(winner.SourceOperationId, Is.EqualTo("source-work-a"))
        Assert.That(winner.SourceRef, Is.EqualTo(Some "refs/heads/work-a"))
        Assert.That(winner.SourceCommit, Is.EqualTo(Some(String.replicate 40 "c")))
        Assert.That(result, Is.EqualTo(reversed))
        // WQ04 projections have no durable presend intent and cannot be
        // upgraded to an executable WQ08 WouldLaunch decision.
        Assert.That(result.Planner.Decision, Is.EqualTo(PlannerDecision.NeedsAttention))
        Assert.That(result.Issues |> List.exists (fun value -> value.Code = "NoDurablePreSendIntent"), Is.True)

    [<Test>]
    member _.BlockingUnfinishedStageWinsOverOlderProjectDemand() =
        let blockingB = projectPort projectB [b; dependent] [] None
        let result = run (account [||]) [portA; blockingB]
        let winner = result.SimulatedWinner |> Option.get
        Assert.That(winner.ProjectId, Is.EqualTo(projectB))
        Assert.That(winner.WorkId, Is.EqualTo(b.Id))
        Assert.That(winner.BlocksOtherWork, Is.True)
        Assert.That(result.CanDispatch, Is.False)

    [<Test>]
    member _.AccountCapOneFullPreservesOriginalLaunchAndReportsOccupancy() =
        let existing = launch projectA (LaunchState.Registered("agent", "secret-session"))
        let withLaunch = projectPort projectA [a] [existing]
                            (Some (journal projectA (Some(OriginalBrowserOutcome.Accepted "accepted"))))
        let result = run (account [|token projectA|]) [withLaunch; portB]
        Assert.That(result.OccupiedAgents, Is.EqualTo(Some 1))
        Assert.That(result.MaxConcurrentAgents, Is.EqualTo(Some 1))
        Assert.That(result.AvailableSlots, Is.EqualTo(Some 0))
        Assert.That(result.Issues |> List.exists (fun value ->
            value.Code = "CapacityFull"), Is.True)
        Assert.That(result.SimulatedWinner.IsSome, Is.True)
        Assert.That(result.Planner.CanDispatch, Is.False)
        Assert.That(result.Planner.VisibleLaunches.Length, Is.EqualTo(1))
        let original = result.Projects |> List.find (fun value -> value.ProjectId = projectA)
        Assert.That(original.ObservedLaunches.Head.OriginalBrowserOperationId, Is.EqualTo(Some originalId))
        Assert.That(original.ObservedLaunches.Head.OriginalBrowserStatus, Is.EqualTo("Accepted"))
        Assert.That(original.ObservedLaunches.Head.Phase, Is.EqualTo("Registered"))
        let json = JsonSerializer.Serialize result
        Assert.That(json, Does.Not.Contain("secret-session"))
        Assert.That(json, Does.Contain(originalId))

    [<Test>]
    member _.StaleHeadOrMissingSourceLineageIsNotAGlobalPositive() =
        let projectValue = project [b] []
        let staleProof = { proof projectB projectValue with Head = "stale-head" }
        let stale = { portB with
                        ReadProject = fun () ->
                            Ok({ Commit = head projectB; Header = Unchecked.defaultof<_> },
                               { Project = projectValue; Catalog = catalog; Proof = staleProof }) }
        let staleResult = run (account [||]) [portA; stale]
        Assert.That(staleResult.ProjectSetComplete, Is.False)
        Assert.That(staleResult.SimulatedWinner.IsNone, Is.True)
        Assert.That(staleResult.Issues |> List.exists (fun value -> value.Code = "ProjectCoverageUnknown"), Is.True)

        let absent = { portB with ReadOrigin = fun _ _ -> Ok None }
        let missing = run (account [||]) [portA; absent]
        Assert.That(missing.ProjectSetComplete, Is.False)
        Assert.That(missing.SimulatedWinner.IsNone, Is.True)
        Assert.That(missing.Issues |> List.exists (fun value -> value.Code = "SourceLineageUnknown"), Is.True)

    [<Test>]
    member _.UnknownOriginalBrowserSendNeverBecomesDefinitiveNoEffect() =
        let pending = launch projectA (LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown)
        let withUnknown = projectPort projectA [a] [pending]
                              (Some(journal projectA (Some(OriginalBrowserOutcome.Unknown "ack lost after send"))))
        let result = run (account [|token projectA|]) [withUnknown; portB]
        let observed = result.Projects |> List.find (fun value -> value.ProjectId = projectA)
        Assert.That(observed.ObservedLaunches.Head.OriginalBrowserOperationId, Is.EqualTo(Some originalId))
        Assert.That(observed.ObservedLaunches.Head.OriginalBrowserStatus, Is.EqualTo("Unknown"))
        Assert.That(result.Planner.Decision, Is.EqualTo(PlannerDecision.Unknown))
        Assert.That(result.Planner.Issues |> List.exists (fun value ->
            value.Code = "OriginalBrowserOutcomeUnknown"), Is.True)
        Assert.That(result.CanDispatch, Is.False)

    [<Test>]
    member _.MissingProjectIsVisibleButDoesNotEraseOtherPinnedInspection() =
        let unavailable = { portB with ReadProject = fun () -> Error "remote unreachable" }
        let result = run (account [||]) [portA; unavailable]
        Assert.That(result.ProjectSetComplete, Is.False)
        Assert.That(result.SimulatedWinner.IsNone, Is.True)
        Assert.That(result.Projects.Length, Is.EqualTo(2))
        Assert.That(result.Projects |> List.exists (fun value ->
            value.ProjectId = projectA && value.Head = Some(head projectA)), Is.True)
        Assert.That(result.Issues |> List.exists (fun value ->
            value.Code = "ProjectReadUnknown" && value.NextAction.Contains "remote unreachable"), Is.True)
