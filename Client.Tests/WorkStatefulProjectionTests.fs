module Client.Tests.WorkStatefulProjectionTests

open System
open NUnit.Framework
open Client.WorkStatefulContracts
open Client.WorkStatefulProjection

let private now = DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero)
let private revision = 10L
let private oidA = String.replicate 40 "a"
let private oidB = String.replicate 40 "b"
let private verified value = Evidence.Verified(revision, value)
let private readScope = { AllowedProjectIds = set [ "p" ]; Authenticated = true; CanClaim = true }
let private defaultQuery = { ProjectId = None; QueueId = None; RoleId = None; Search = "" }
let private key id = "p/" + id

let private node id parent kind state proposal =
    { Id = id; ProjectId = "p"; ParentId = parent
      Kind = kind; Title = "Title " + id; Version = 1
      State = state; Proposal = proposal
      RequiredForParent = true
      Acceptance = AcceptanceState.AwaitingIndependentAcceptance }

let private project = node "p" None NodeKind.Project NodeState.Open (ProposalState.Approved "lead-1")
let private system = node "s" (Some "p") NodeKind.System NodeState.Open (ProposalState.Approved "lead-2")
let private feature = node "f" (Some "s") NodeKind.Feature NodeState.Open (ProposalState.Approved "lead-3")

let private stage id nodeId queue state sequence =
    { Id = id; ProjectId = "p"; NodeId = nodeId; QueueId = queue
      Kind = StageKind.Implementation; Generation = 1; State = state
      LeadPriority = verified 5; IsBlocking = verified false
      Eligible = verified true; AvailableSequence = sequence
      UnmetHardDependencies = verified []
      Review = ReviewStage.NotRequired
      Integration = IntegrationStage.NotRequired }

let private snapshot nodes stages =
    { Version = ApiVersion; Revision = revision
      CapturedAt = now
      Origin = SnapshotOrigin.ExplicitFixture "typed isolated test"
      Scope = Evidence.Unknown "No live client grant in fixture"
      RoleQueues = verified (Map.ofList [ "Worker", set [ "implementation" ]; "Reviewer", set [ "review" ] ])
      Capacity = verified { Limit = 3; Occupied = 1 }
      Backup = verified
          { ExportSchemaVersion = 1; ExportedRevision = 8L
            SnapshotAt = now.AddMinutes(-30.0)
            PublishedAt = Some(now.AddMinutes(-20.0))
            VerifiedExport = true }
      Recovery = verified RecoveryMode.Normal
      Nodes = nodes; Stages = stages }

[<Test>]
let explicitFixtureCannotBeMistakenForLiveAuthority () =
    let view = renderAt now defaultQuery (snapshot [ project; system; feature ] [ stage "w" "f" "implementation" StageState.Available 1L ])
    Assert.That(view.Banner, Does.StartWith("FIXTURE PREVIEW"))
    Assert.That(view.ActionsEnabled, Is.False)
    Assert.That(view.Stages.Head.Readiness, Does.Contain("Fixture has no live command authority"))
    Assert.That(view.HistoricalAgentAudit, Does.Contain("separate read-only view"))

[<Test>]
let hierarchicalAncestryIsReadableAndParentAcceptanceIsIndependent () =
    let view = renderAt now defaultQuery (snapshot [ feature; project; system ] [])
    let f = view.Nodes |> List.find (fun row -> row.Key = key "f")
    Assert.That(f.Path, Is.EqualTo("Project: Title p / System: Title s / Feature: Title f"))
    Assert.That(f.Acceptance, Does.StartWith("Awaiting independent"))
    Assert.That(f.Details |> String.concat " ", Does.Contain("does not automatically accept"))

[<Test>]
let draftProposedAndReturnedCannotBeInferredApprovedFromAvailableStage () =
    let draft = node "draft" (Some "p") NodeKind.Feature NodeState.Draft (ProposalState.Returned "Scope too broad")
    let proposed = node "proposed" (Some "p") NodeKind.Feature NodeState.Proposed ProposalState.AwaitingLead
    let unapproved = node "unapproved" (Some "p") NodeKind.Feature NodeState.Open ProposalState.NotSubmitted
    let stages =
        [ stage "wd" "draft" "implementation" StageState.Available 1L
          stage "wp" "proposed" "implementation" StageState.Available 2L
          stage "wu" "unapproved" "implementation" StageState.Available 3L ]
    let view = renderAt now defaultQuery (snapshot [ project; draft; proposed; unapproved ] stages)
    Assert.That(view.Nodes |> List.find (fun row -> row.Key = key "draft") |> _.State, Is.EqualTo("Draft / Returned"))
    Assert.That(view.Nodes |> List.find (fun row -> row.Key = key "proposed") |> _.State, Does.StartWith("Proposed"))
    Assert.That(view.Nodes |> List.find (fun row -> row.Key = key "unapproved") |> _.State, Does.StartWith("UNKNOWN"))
    Assert.That(view.Stages |> List.forall (fun row -> row.Readiness.StartsWith("NotReady")), Is.True)

[<Test>]
let availableRankingIsBlockingThenLeadPriorityThenOldestSequence () =
    let a = { stage "a" "f" "implementation" StageState.Available 11L with IsBlocking = verified true; LeadPriority = verified 2 }
    let b = { stage "b" "f" "implementation" StageState.Available 13L with IsBlocking = verified true; LeadPriority = verified 5 }
    let c = { stage "c" "f" "implementation" StageState.Available 12L with IsBlocking = verified true; LeadPriority = verified 5 }
    let d = { stage "d" "f" "implementation" StageState.Available 1L with IsBlocking = verified false; LeadPriority = verified 99 }
    let blocked = { stage "blocked" "f" "implementation" (StageState.Blocked [ "dependency" ]) 0L with IsBlocking = verified true; LeadPriority = verified 999 }
    let view = renderAt now defaultQuery (snapshot [ project; system; feature ] [ blocked; d; a; b; c ])
    Assert.That(view.Stages |> List.map _.Key, Is.EqualTo(box (List.map key [ "c"; "b"; "a"; "d"; "blocked" ])))
    Assert.That(view.Stages |> List.find (fun row -> row.Key = key "blocked") |> _.Readiness, Does.StartWith("Blocked"))

[<Test>]
let roleFilteringUsesOnlyVersionedRoleSubscriptions () =
    let work = stage "w" "f" "implementation" StageState.Available 1L
    let rev = stage "r" "f" "review" StageState.Available 2L
    let snap = snapshot [ project; system; feature ] [ work; rev ]
    let view = renderAt now { defaultQuery with RoleId = Some "Reviewer" } snap
    Assert.That(view.Stages |> List.map _.Key, Is.EqualTo(box [ key "r" ]))
    let stale = renderAt now { defaultQuery with RoleId = Some "Reviewer" }
                    { snap with RoleQueues = Evidence.Verified(9L, Map.ofList [ "Reviewer", set [ "review" ] ]) }
    Assert.That(stale.Stages.IsEmpty, Is.True)
    Assert.That(stale.Diagnostics |> String.concat " ", Does.Contain("cannot be verified"))

[<Test>]
let missingCapacityOrRecoveryEvidenceIsNotReadyDespiteAvailableState () =
    let original = snapshot [ project; system; feature ] [ stage "w" "f" "implementation" StageState.Available 1L ]
    let uncertain =
        { original with Capacity = Evidence.Unknown "Account global join unavailable"
                        Recovery = Evidence.Unknown "Restore mode not observed" }
        |> renderAt now defaultQuery
    Assert.That(uncertain.Stages.Head.Readiness, Does.Contain("Account global join unavailable"))
    Assert.That(uncertain.Stages.Head.Readiness, Does.Contain("Recovery unknown"))
    Assert.That(uncertain.Capacity, Does.StartWith("UNKNOWN"))
    Assert.That(uncertain.Recovery, Does.StartWith("UNKNOWN"))

[<Test>]
let recoveryFenceIsVisibleAndNeverConfusedWithNormalReadiness () =
    let snap =
        { snapshot [ project; system; feature ] [ stage "w" "f" "implementation" StageState.Available 1L ] with
            Recovery = verified (RecoveryMode.Fenced "Restored snapshot older than source branch") }
    let view = renderAt now defaultQuery snap
    Assert.That(view.Recovery, Does.StartWith("FENCED"))
    Assert.That(view.Stages.Head.Readiness, Does.Contain("Recovery fence"))

[<Test>]
let duplicateLaunchClaimIsFlaggedAsConflictRatherThanAnAgentOwner () =
    let claim = { LaunchId = "launch-1"; Generation = 2; Execution = verified ExecutionObservation.Running }
    let a = stage "w1" "f" "implementation" (StageState.Claimed claim) 1L
    let b = stage "w2" "f" "implementation" (StageState.Claimed claim) 2L
    let view = renderAt now defaultQuery (snapshot [ project; system; feature ] [ a; b ])
    Assert.That(view.Diagnostics |> String.concat " ", Does.Contain("Multiple primary claims"))
    Assert.That(view.Stages |> List.forall (fun row -> row.Claim.StartsWith("UNKNOWN")), Is.True)

[<Test>]
let claimSessionLivenessMustBePositiveAndAtSameRevision () =
    let stale = { LaunchId = "l"; Generation = 1; Execution = Evidence.Verified(9L, ExecutionObservation.Running) }
    let fresh = { LaunchId = "m"; Generation = 1; Execution = verified ExecutionObservation.Running }
    let view = renderAt now defaultQuery
                   (snapshot [ project; system; feature ]
                     [ stage "stale" "f" "implementation" (StageState.Claimed stale) 1L
                       stage "fresh" "f" "implementation" (StageState.Claimed fresh) 2L ])
    Assert.That(view.Stages |> List.find (fun row -> row.Key = key "stale") |> _.Claim, Does.Contain("liveness UNKNOWN"))
    Assert.That(view.Stages |> List.find (fun row -> row.Key = key "fresh") |> _.Claim, Does.Contain("observed running"))
    Assert.That(view.Stages |> List.forall (fun row -> row.Claim.Contains("not a durable Agent owner") || row.Claim.StartsWith("UNKNOWN")), Is.True)

[<Test>]
let exactFrozenReviewIntegrationAndBackupLagAreNotInvented () =
    let work =
        { stage "r" "f" "review" StageState.Finished 1L with
            Review = ReviewStage.Accepted oidA
            Integration = IntegrationStage.Integrated oidB }
    let view = renderAt now defaultQuery (snapshot [ project; system; feature ] [ work ])
    Assert.That(view.Stages.Head.Review, Does.Contain(oidA))
    Assert.That(view.Stages.Head.Integration, Does.Contain(oidB))
    Assert.That(view.Backup, Does.Contain("lag 2"))
    Assert.That(view.Backup, Does.Contain("age 30m"))
    let uncertain = { work with Review = ReviewStage.Accepted "not-a-sha" }
    Assert.That((renderAt now defaultQuery (snapshot [ project; feature; system ] [ uncertain ])).Stages.Head.Review,
                Does.StartWith("UNKNOWN"))

[<Test>]
let scopedServerSnapshotFailsClosedWithoutLeakingOtherProjectTitles () =
    let privateNode = { project with Id = "secret"; ProjectId = "other"; Title = "Never display this" }
    let baseline = snapshot [ project; privateNode ] []
    let live = { baseline with Origin = SnapshotOrigin.AuthenticatedServer "Jarvis"
                               Scope = verified readScope }
    let view = renderAt now defaultQuery live
    Assert.That(view.Nodes |> List.map _.Key, Is.EqualTo(box [ key "p" ]))
    Assert.That(view.Nodes |> List.map _.Path |> String.concat " ", Does.Not.Contain("Never display this"))
    let denied = renderAt now { defaultQuery with ProjectId = Some "other" } live
    Assert.That(denied.Nodes.IsEmpty, Is.True)
    Assert.That(denied.Banner, Does.Contain("not in the authenticated scope"))
    let missingScope = renderAt now defaultQuery { live with Scope = Evidence.Unknown "No verified token" }
    Assert.That(missingScope.Nodes.IsEmpty, Is.True)
    Assert.That(missingScope.Banner, Does.Contain("UNKNOWN"))

[<Test>]
let unsupportedSchemaDuplicateIdsAndInvalidAncestryAreExplicit () =
    let baseline = snapshot [ project; feature; system ] []
    let unsupported = renderAt now defaultQuery { baseline with Version = 2 }
    Assert.That(unsupported.Nodes.IsEmpty, Is.True)
    Assert.That(unsupported.Banner, Does.Contain("Unsupported"))
    let duplicated = renderAt now defaultQuery { baseline with Nodes = project :: baseline.Nodes }
    Assert.That(duplicated.Banner, Does.Contain("Duplicate"))
    let cycleA = { system with ParentId = Some "f" }
    let bad = renderAt now defaultQuery { baseline with Nodes = [ project; cycleA; feature ] }
    Assert.That(bad.Diagnostics |> String.concat " ", Does.Contain("Cycle"))
    Assert.That(bad.Nodes |> List.find (fun row -> row.Key = key "f") |> _.Path, Does.StartWith("UNKNOWN"))

[<Test>]
let missingGraphLinksAndZeroGenerationNeverLookRunnable () =
    let broken = { feature with ParentId = Some "lost-system" }
    let invalid = { stage "invalid" "f" "implementation" StageState.Available 1L with Generation = 0 }
    let orphanStage = stage "orphan" "missing" "implementation" StageState.Available 2L
    let view = renderAt now defaultQuery
                   (snapshot [ project; broken ] [ invalid; orphanStage ])
    Assert.That(view.Stages |> List.find (fun row -> row.Key = key "invalid") |> _.Readiness,
                Does.Contain("invalid stage generation"))
    Assert.That(view.Diagnostics |> String.concat " ", Does.Contain("Unresolved Work stage node links"))
    Assert.That(view.Stages |> List.find (fun row -> row.Key = key "invalid") |> _.Readiness,
                Does.Contain("Graph ancestry UNKNOWN"))

[<Test>]
let duplicateStageIdsFailClosedInsteadOfRenderingMultipleActions () =
    let work = stage "same" "f" "implementation" StageState.Available 1L
    let view = renderAt now defaultQuery (snapshot [ project; system; feature ] [ work; work ])
    Assert.That(view.Stages.IsEmpty, Is.True)
    Assert.That(view.Diagnostics |> String.concat " ", Does.Contain("Duplicate Work stage identities"))
    Assert.That(view.ActionsEnabled, Is.False)

[<Test>]
let unsatisfiedHardPrerequisiteAndStalePrerequisiteEvidenceFailClosed () =
    let baseline = stage "w" "f" "implementation" StageState.Available 1L
    let unmet = { baseline with UnmetHardDependencies = verified [ "prior-stage" ] }
    let stale = { baseline with UnmetHardDependencies = Evidence.Verified(9L, []) }
    let view = renderAt now defaultQuery (snapshot [ project; system; feature ] [ unmet ])
    Assert.That(view.Stages.Head.Readiness, Does.Contain("Unmet hard prerequisites"))
    Assert.That(view.Stages.Head.Readiness, Does.Contain("prior-stage"))
    let unknown = renderAt now defaultQuery (snapshot [ project; system; feature ] [ stale ])
    Assert.That(unknown.Stages.Head.Readiness, Does.Contain("Hard prerequisite evidence UNKNOWN"))

[<Test>]
let unauthorizedStageWithCollidingLocalNodeIdIsNeverProjected () =
    let privateRoot = { project with Id = "private-root"; ProjectId = "other"; Title = "SECRET project" }
    let claim = { LaunchId = "secret-launch"; Generation = 9; Execution = verified ExecutionObservation.Running }
    let leaked =
        { stage "confidential-review" "f" "review" (StageState.Claimed claim) 3L with
            ProjectId = "other"
            Review = ReviewStage.Accepted oidA
            Integration = IntegrationStage.Integrated oidB
            LeadPriority = verified 999 }
    let inside = stage "inside" "f" "implementation" StageState.Available 1L
    let live =
        { snapshot [ project; system; feature; privateRoot ] [ inside; leaked ] with
            Origin = SnapshotOrigin.AuthenticatedServer "Jarvis"
            Scope = verified readScope }
    let view = renderAt now defaultQuery live
    Assert.That(view.Stages |> List.map _.Key, Is.EqualTo(box [ key "inside" ]))
    let allVisible =
        [ yield view.Banner
          yield! view.Nodes |> List.collect (fun n -> n.Path :: n.Details)
          yield! view.Stages |> List.collect (fun s ->
              [ s.Key; s.Path; s.Claim; s.Review; s.Integration; s.LeadPriority ] @ s.Details)
          yield! view.Diagnostics ]
        |> String.concat " "
    for secret in [ "SECRET"; "confidential-review"; "secret-launch"; oidA; oidB; "999" ] do
        Assert.That(allVisible, Does.Not.Contain(secret), "Unauthorized stage data must remain outside the view")

[<Test>]
let multipleAuthorizedProjectsCanReuseLocalNodeAndStageIds () =
    let otherProject = { project with ProjectId = "other"; Title = "Other project" }
    let otherSystem = { system with ProjectId = "other"; Title = "Other system" }
    let otherFeature = { feature with ProjectId = "other"; Title = "Other feature" }
    let outsideStage = { stage "w" "f" "implementation" StageState.Available 2L with ProjectId = "other" }
    let insideStage = stage "w" "f" "implementation" StageState.Available 1L
    let scope = { readScope with AllowedProjectIds = set [ "p"; "other" ] }
    let live =
        { snapshot [ project; system; feature; otherProject; otherSystem; otherFeature ]
                   [ insideStage; outsideStage ] with
            Origin = SnapshotOrigin.AuthenticatedServer "Jarvis"
            Scope = verified scope }
    let view = renderAt now defaultQuery live
    Assert.That(view.Nodes.Length, Is.EqualTo(6))
    Assert.That(view.Stages |> List.map _.Key, Is.EquivalentTo([ "p/w"; "other/w" ]))
    Assert.That(view.Nodes |> List.find (fun n -> n.Key = "other/f") |> _.Path,
                Is.EqualTo("Project: Other project / System: Other system / Feature: Other feature"))
    Assert.That(view.Stages |> List.find (fun s -> s.Key = "other/w") |> _.Path,
                Does.Contain("Other feature"))
    Assert.That(view.Diagnostics |> String.concat " ", Does.Not.Contain("Duplicate"))
    let onlyP = renderAt now { defaultQuery with ProjectId = Some "p" } live
    Assert.That(onlyP.Stages |> List.map _.Key, Is.EqualTo(box [ key "w" ]))

[<Test>]
let malformedStageIdentityCannotAppearActionable () =
    let malformed = { stage "" "f" "implementation" StageState.Available 1L with ProjectId = "p" }
    let view = renderAt now defaultQuery (snapshot [ project; system; feature ] [ malformed ])
    Assert.That(view.Stages.IsEmpty, Is.True)
    Assert.That(view.Diagnostics |> String.concat " ", Does.Contain("Missing canonical project/stage identity"))

[<Test>]
let versionedCommandEnvelopesBindProjectAndExactStageGeneration () =
    let claim: CommandRequest =
        { ApiVersion = ApiVersion
          ProjectId = "p"
          OperationId = Guid.NewGuid()
          ExpectedSnapshotRevision = revision
          ExpectedSubjectVersion = 4
          ExpectedStageGeneration = Some 3
          Command = WorkCommand.Claim "w" }
    Assert.That(claim.ProjectId, Is.EqualTo("p"))
    Assert.That(claim.ExpectedStageGeneration, Is.EqualTo(Some 3))
    let recovery = { claim with ExpectedStageGeneration = None; Command = WorkCommand.ReleaseRecoveryFence "recovery-evidence" }
    Assert.That(recovery.ProjectId, Is.EqualTo("p"))
