module Client.Tests.WorkStatefulTransportTests

open System
open NUnit.Framework
open Jarvis.WorkStatefulApi
open Client.WorkStatefulContracts
open Client.WorkStatefulTransport
open Client.WorkStatefulProjection

let private node =
    { ProjectId = "project-A"
      Id = "root"
      RowVersion = 2L
      GraphVersion = 2L
      Kind = Some "Project"
      Title = Some "Project A"
      ParentId = None
      State = Some "Open"
      ProposalState = Some "Open"
      ApprovalReceipt = Some "decision-1"
      RequiredForParent = Some false
      AcceptanceEvidence = None }

let private stage =
    { ProjectId = "project-A"
      Id = "work-1"
      RowVersion = 2L
      GraphVersion = 2L
      NodeId = Some "root"
      QueueId = Some "implementation"
      Kind = Some "Implementation"
      State = Some "Available"
      ProposalState = Some "Open"
      Gate = Some "Clear"
      ClaimLaunchId = None
      StageGeneration = 3L
      ClaimGeneration = 0L
      StopGeneration = 4L
      SourceCommit = Some(String.replicate 40 "a")
      ReviewCommit = None
      IntegrationCommit = None
      AvailableSequence = Some 12L
      LeadPriority = Some 100 }

let private response =
    { ApiVersion = WorkReadVersion.Current
      AuthorityId = "trusted-server-A"
      ProjectId = "project-A"
      Revision = 20L
      CapturedAt = DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero)
      ProjectRowVersion = 1L
      GraphVersion = 2L
      StopGeneration = 4L
      VerifiedRoleId = Some "worker"
      Nodes = [ node ]
      Stages = [ stage ]
      Roles = [ { ProjectId = "project-A"; Id = "worker"; RoleVersion = 2L
                  Queues = Some [ "implementation" ] } ]
      Capacity = Some { Limit = 2; Occupied = 1; RowVersion = 1L }
      BackupUnknownReason = "No accepted F1 backup witness"
      RecoveryUnknownReason = "No accepted G1 Recovery witness"
      Diagnostics = [] }

let private channel response =
    { new IAuthenticatedWorkSnapshotReader with
        member _.ReadProjectSnapshot _ = Ok response }

let private fetchResponse response =
    fetch (channel response) "trusted-server-A" "project-A" (Some "worker")

let private success = function
    | Ok value -> value
    | Error value -> Assert.Fail(sprintf "Unexpected transport failure: %A" value); Unchecked.defaultof<_>

[<Test>]
let authenticated_read_is_explicitly_read_only_and_unknown_proofs_cannot_enable_claim () =
    let actual = fetchResponse response |> success
    Assert.That(actual.Snapshot.Revision, Is.EqualTo 20L)
    Assert.That(actual.Snapshot.Nodes.Length, Is.EqualTo 1)
    Assert.That(actual.Snapshot.Stages.Length, Is.EqualTo 1)
    match actual.Snapshot.Origin with
    | SnapshotOrigin.AuthenticatedServer id -> Assert.That(id, Is.EqualTo "trusted-server-A")
    | other -> Assert.Fail(sprintf "Expected authenticated channel origin: %A" other)
    match actual.Snapshot.Scope with
    | Evidence.Verified (_, scope) ->
        Assert.That(scope.CanClaim, Is.False)
        Assert.That(scope.AllowedProjectIds = Set.singleton "project-A", Is.True)
    | _ -> Assert.Fail("Expected verified read scope only")
    match actual.Snapshot.Stages.Head.Eligible with
    | Evidence.Unknown _ -> ()
    | _ -> Assert.Fail("Stage Available cannot establish executable eligibility")
    match actual.Snapshot.Stages.Head.LeadPriority with
    | Evidence.Unknown _ -> ()
    | _ -> Assert.Fail("Unverified payload priority cannot become Lead proof")
    match actual.Snapshot.Backup, actual.Snapshot.Recovery with
    | Evidence.Unknown _, Evidence.Unknown _ -> ()
    | _ -> Assert.Fail("Unreviewed F/G placeholders must remain unknown")
    Assert.That(actual.Snapshot.Nodes.Head.Acceptance, Is.EqualTo(AcceptanceState.Unknown "Independent acceptance not attested by this read projection"))
    match actual.Snapshot.Nodes.Head.Proposal with
    | ProposalState.Unknown _ -> ()
    | _ -> Assert.Fail("An unverified JSON approvalReceipt is not an authenticated Lead decision")
    let view =
        renderAt response.CapturedAt
            { ProjectId = Some "project-A"; RoleId = Some "worker"
              QueueId = None; Search = "" }
            actual.Snapshot
    Assert.That(view.Banner, Does.StartWith("Authenticated Work snapshot"))
    Assert.That(view.ActionsEnabled, Is.False)
    Assert.That(view.Stages.Head.Readiness, Does.Contain("UNKNOWN"))
    Assert.That(view.Backup, Does.Contain("UNKNOWN"))
    Assert.That(view.Recovery, Does.Contain("UNKNOWN"))

[<Test>]
let forged_or_misrouted_scope_is_rejected_before_projection () =
    let cases =
        [ { response with AuthorityId = "fixture-server" }
          { response with ProjectId = "project-B" }
          { response with VerifiedRoleId = Some "admin" }
          { response with ApiVersion = 123 }
          { response with Nodes = [ { node with ProjectId = "project-B" } ] }
          { response with Stages = [ { stage with ProjectId = "project-B" } ] }
          { response with Roles = [ { response.Roles.Head with ProjectId = "project-B" } ] }
          { response with Stages = [ stage; stage ] }
          { response with Capacity = Some { Limit = 1; Occupied = 2; RowVersion = 1L } } ]
    for candidate in cases do
        match fetchResponse candidate with
        | Error(ReadTransportFailure.InvalidResponse _) -> ()
        | other -> Assert.Fail(sprintf "An invalid scoped response was accepted: %A" other)

[<Test>]
let incomplete_node_and_stage_metadata_remains_visible_as_omission_not_a_fake_authorization () =
    let partial =
        { response with
            Nodes = [ { node with RequiredForParent = None } ]
            Stages = [ { stage with NodeId = None } ]
            Capacity = None
            Roles = [] }
    let result = fetchResponse partial |> success
    Assert.That(result.Snapshot.Nodes, Is.Empty)
    Assert.That(result.Snapshot.Stages, Is.Empty)
    Assert.That(result.Diagnostics |> String.concat " ", Does.Contain("omitted"))
    match result.Snapshot.Capacity, result.Snapshot.RoleQueues with
    | Evidence.Unknown _, Evidence.Unknown _ -> ()
    | _ -> Assert.Fail("Missing capacity/roles must not become verified")

[<Test>]
let claimed_stage_preserves_distinct_stage_and_claim_generations () =
    let claimed = { stage with State = Some "Claimed"; ClaimLaunchId = Some "launch-7"; ClaimGeneration = 7L }
    let actual = fetchResponse { response with Stages = [ claimed ] } |> success
    Assert.That(actual.Snapshot.Stages.Head.Generation, Is.EqualTo 3)
    match actual.Snapshot.Stages.Head.State with
    | StageState.Claimed claim ->
        Assert.That(claim.LaunchId, Is.EqualTo "launch-7")
        Assert.That(claim.Generation, Is.EqualTo 7)
        match claim.Execution with
        | Evidence.Unknown _ -> ()
        | _ -> Assert.Fail("Reported claim must not attest to live execution")
    | other -> Assert.Fail(sprintf "Expected reported claim, got %A" other)

[<Test>]
let invalid_claim_provenance_is_omitted_with_explicit_diagnostic () =
    let claimed = { stage with State = Some "Claimed"; ClaimLaunchId = Some "launch-7"; ClaimGeneration = 7L }
    let candidates =
        [ { claimed with ClaimGeneration = 0L }
          { claimed with ClaimGeneration = -1L }
          { claimed with ClaimGeneration = int64 Int32.MaxValue + 1L }
          { claimed with ClaimLaunchId = None } ]
    for candidate in candidates do
        let actual = fetchResponse { response with Stages = [ candidate ] } |> success
        Assert.That(actual.Snapshot.Stages, Is.Empty)
        Assert.That(actual.Diagnostics |> String.concat " ", Does.Contain("Claimed stage omitted"))

[<Test>]
let source_sha_survives_as_reported_unverified_metadata_only () =
    let sha = String.replicate 40 "a"
    let actual = fetchResponse response |> success
    match Map.find ("project-A", "work-1") actual.StageSources with
    | ReadSourceProvenance.ReportedUnverified reported -> Assert.That(reported, Is.EqualTo sha)
    | other -> Assert.Fail(sprintf "Expected reported unverified source SHA, got %A" other)
    match actual.Snapshot.Stages.Head.Review, actual.Snapshot.Stages.Head.Integration with
    | ReviewStage.Unknown _, IntegrationStage.Unknown _ -> ()
    | other -> Assert.Fail(sprintf "Source SHA must not imply review/integration acceptance: %A" other)

    let malformed = fetchResponse { response with Stages = [ { stage with SourceCommit = Some "not-a-sha" } ] } |> success
    match Map.find ("project-A", "work-1") malformed.StageSources with
    | ReadSourceProvenance.Unknown _ -> ()
    | other -> Assert.Fail(sprintf "Malformed SHA must be UNKNOWN, got %A" other)
    Assert.That(malformed.Diagnostics |> String.concat " ", Does.Contain("source SHA malformed"))

    let absent = fetchResponse { response with Stages = [ { stage with SourceCommit = None } ] } |> success
    match Map.find ("project-A", "work-1") absent.StageSources with
    | ReadSourceProvenance.Unknown _ -> ()
    | other -> Assert.Fail(sprintf "Absent source SHA must be UNKNOWN, got %A" other)
