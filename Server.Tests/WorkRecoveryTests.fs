module ServerWorkRecoveryTests

open System
open NUnit.Framework
open Jarvis.WorkRecovery

let private sha c = String.replicate 40 (string c)
let private clear = StopFence.Cleared ("operator-cleared", 3, true)

let private lineage =
    { WorkVersion = 4
      Generation = 2
      ProjectGraphVersion = 3
      RoleAuthoritySha = sha 'c' }

let private source =
    { ProjectId = "alpha"
      WorkId = "W"
      RepositoryId = "repo:jarvis"
      SourceRef = "refs/heads/feature/recovery"
      SourceSha = sha 'a'
      OriginalLineage = lineage
      ReceiptId = "source-verified"
      ReachabilityVerified = true }

let private review =
    { ProjectId = "alpha"
      WorkId = "W"
      ReviewId = "frozen-r1"
      OriginalLineage = lineage
      SourceRepositoryId = source.RepositoryId
      SourceRef = source.SourceRef
      SourceReceiptId = source.ReceiptId
      FrozenSourceSha = source.SourceSha
      Verdict = ReviewVerdict.Accepted
      ReceiptId = "review-accepted"
      CanonicallyVerified = true }

let private integrationApproval =
    { ReceiptId = "original-integration-approval"
      OriginalLineage = lineage
      SourceRepositoryId = source.RepositoryId
      SourceRef = source.SourceRef
      SourceReceiptId = source.ReceiptId
      SourceSha = source.SourceSha
      AcceptedReviewId = review.ReviewId
      AcceptedReviewReceiptId = review.ReceiptId
      TargetRef = "refs/heads/main"
      CanonicallyVerified = true }

let private integration =
    { ProjectId = "alpha"
      WorkId = "W"
      SubmissionId = "submission-1"
      OriginalLineage = lineage
      SourceRepositoryId = source.RepositoryId
      SourceRef = source.SourceRef
      AcceptedReviewId = review.ReviewId
      AcceptedReviewReceiptId = review.ReceiptId
      Authorization = integrationApproval
      AcceptedSourceSha = source.SourceSha
      TargetRef = "refs/heads/main"
      IntegratedSha = sha 'b'
      ReceiptId = "integration-verified"
      ContainsSourceVerified = true
      CanonicallyVerified = true }

let private work id goal =
    { WorkId = id
      WorkVersion = 4
      Generation = 2
      RoleAuthoritySha = lineage.RoleAuthoritySha
      Goal = goal
      CapturedInFlightLaunch = false
      CapturedLaunches = []
      Source = None
      Review = None
      Integration = None
      Effects = []
      Release = None }

let private capturedLaunch id =
    { ProjectId = "alpha"
      WorkId = "W"
      Generation = 2
      OperationId = id }

let private completed id =
    { work id ArtifactGoal.Integrated with
        Source = Some source
        Review = Some review
        Integration = Some integration }

let private sealedGap =
    { ProjectId = "alpha"
      RestoredProjectSequence = 7L
      KnownPreviousProjectSequence = 8L
      CoveredThroughSequence = 8L
      ReceiptId = "full-provenance-gap-reconciliation"
      CanonicallyVerified = true
      WorkGraphCovered = true
      ExternalEffectsCovered = true }

let private accountSeal =
    { SnapshotDatabaseSequence = 10L
      KnownPreviousDatabaseSequence = 12L
      CoveredThroughDatabaseSequence = 12L
      ReceiptId = "account-highwater-complete"
      CanonicallyVerified = true
      ProjectSetCovered = true
      ExternalEffectsCovered = true }

let private project id works =
    { ProjectId = id
      GraphVersion = 3
      RestoredSequence = 7L
      KnownPreviousSequence = Some 8L
      Stop = clear
      GapSeal = Some { sealedGap with ProjectId = id }
      Works = works }

let private snapshot projects =
    { SchemaVersion = 1
      SnapshotDatabaseSequence = 10L
      KnownPreviousDatabaseSequence = Some 12L
      CompleteProjectSetVerified = true
      AccountGapSeal = Some accountSeal
      GlobalStop = clear
      Projects = projects }

let private decision input projectId workId =
    (WorkRecovery.plan input).Projects
    |> List.find (fun project -> project.ProjectId = projectId)
    |> fun project -> project.Works |> List.find (fun work -> work.WorkId = workId)

let private authorize input projectId workId =
    let pre = decision input projectId workId
    let originalProject = input.Projects |> List.find (fun project -> project.ProjectId = projectId)
    let originalWork = originalProject.Works |> List.find (fun work -> work.WorkId = workId)
    let authorization =
        { ProjectId = projectId
          WorkId = workId
          ReceiptId = "explicit-recovery-authorization"
          AuthorizationRevision = 10L
          CanonicallyVerified = true
          SnapshotDatabaseSequence = input.SnapshotDatabaseSequence
          ReconciledThroughProjectSequence = originalProject.KnownPreviousSequence.Value
          ProjectGraphVersion = originalProject.GraphVersion
          WorkVersion = originalWork.WorkVersion
          Generation = originalWork.Generation
          RoleAuthoritySha = originalWork.RoleAuthoritySha
          EvidenceReceipts = pre.EvidenceReceipts
          EvidenceDigest = pre.EvidenceDigest }
    { input with
        Projects =
            input.Projects
            |> List.map (fun project ->
                if project.ProjectId = projectId then
                    { project with
                        Works =
                            project.Works
                            |> List.map (fun work ->
                                if work.WorkId = workId then
                                    { work with Release = Some authorization }
                                else work) }
                else project) }

let private has requirement requirements =
    Assert.That(requirements |> List.contains requirement, Is.True)

[<Test>]
let newer_source_frozen_accepted_review_and_integration_require_separate_explicit_authorization () =
    let initial = snapshot [ project "alpha" [ completed "W" ] ]
    let pending = decision initial "alpha" "W"
    Assert.That(pending.ReleaseAuthorized, Is.False)
    Assert.That(pending.NormalDispatchAllowed, Is.False)
    Assert.That(pending.Requirements, Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
    Assert.That(pending.EvidenceReceipts.Contains("source:source-verified"), Is.True)
    Assert.That(pending.EvidenceReceipts.Contains("review:review-accepted"), Is.True)
    Assert.That(pending.EvidenceReceipts.Contains("integration:integration-verified"), Is.True)
    Assert.That(pending.EvidenceReceipts.Contains("gap:full-provenance-gap-reconciliation"), Is.True)
    let resolved = authorize initial "alpha" "W"
    let result = decision resolved "alpha" "W"
    Assert.That(result.Requirements, Is.Empty)
    Assert.That(result.ReleaseAuthorized, Is.True)
    // The already-integrated Work is reconciled, not launched again.
    Assert.That(result.NormalDispatchAllowed, Is.False)
    Assert.That((WorkRecovery.plan resolved).RecoveryQueue, Is.Empty)
    Assert.That((WorkRecovery.plan resolved).NormalDispatchAllowed, Is.False)

[<Test>]
let newer_commit_is_not_a_lost_review_verdict_and_review_is_not_inferred_from_git_ancestry () =
    let candidate = { completed "W" with Review = None }
    let input = snapshot [ project "alpha" [ candidate ] ] |> fun state -> authorize state "alpha" "W"
    let requirements = (decision input "alpha" "W").Requirements
    has Requirement.FreshIndependentReviewRequired requirements
    has Requirement.InvalidIntegration requirements
    Assert.That((decision input "alpha" "W").ReleaseAuthorized, Is.False)

[<Test>]
let changed_or_rejected_frozen_review_is_not_accepted_for_integration () =
    for bad in
        [ { review with Verdict = ReviewVerdict.ChangesRequested }
          { review with FrozenSourceSha = sha 'f' }
          { review with CanonicallyVerified = false } ] do
        let input = snapshot [ project "alpha" [ { completed "W" with Review = Some bad } ] ]
        has Requirement.InvalidFrozenReview (decision input "alpha" "W").Requirements
        has Requirement.InvalidIntegration (decision input "alpha" "W").Requirements

[<Test>]
let lost_inflight_launch_cannot_be_dispatched_again_without_terminal_proof () =
    let original =
        { work "W" ArtifactGoal.Unchanged with
            CapturedInFlightLaunch = true
            CapturedLaunches = [ capturedLaunch "L" ] }
    let input = snapshot [ project "alpha" [ original ] ] |> fun state -> authorize state "alpha" "W"
    let result = decision input "alpha" "W"
    has Requirement.InFlightLaunchReconciliation result.Requirements
    Assert.That(result.NormalDispatchAllowed, Is.False)
    let terminalJournal =
        { OperationId = "L"
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.Launch
          SourceSha = None
          Outcome = EffectOutcome.Observed ("launched-terminal", true, true, true) }
    let repaired =
        { input with
            Projects =
                [ project "alpha" [ { original with Effects = [ terminalJournal ] } ] ] }
    // The earlier authorization cannot be reused with the changed receipt set.
    has Requirement.RecoveryAuthorizationRequired (decision repaired "alpha" "W").Requirements
    let released = authorize repaired "alpha" "W"
    Assert.That((decision released "alpha" "W").NormalDispatchAllowed, Is.True)

[<Test>]
let unrelated_terminal_launch_of_same_work_and_generation_cannot_clear_original_launch_fence () =
    let terminal operation =
        { OperationId = operation
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.Launch
          SourceSha = None
          Outcome = EffectOutcome.Observed ("terminal-" + operation, true, true, true) }
    let captured =
        { work "W" ArtifactGoal.Unchanged with
            CapturedInFlightLaunch = true
            CapturedLaunches = [ capturedLaunch "L-active" ]
            Effects = [ terminal "L-other" ] }
    // The unrelated launch has genuinely verified terminal evidence. The
    // captured original is still unknown; even a newly minted recovery
    // authorization using this evidence cannot bypass the reconciliation.
    let input = snapshot [ project "alpha" [ captured ] ] |> fun s -> authorize s "alpha" "W"
    let blocked = decision input "alpha" "W"
    has Requirement.InFlightLaunchReconciliation blocked.Requirements
    Assert.That(blocked.ReleaseAuthorized, Is.False)
    Assert.That(blocked.NormalDispatchAllowed, Is.False)
    let matching = { captured with Effects = [ terminal "L-other"; terminal "L-active" ] }
    let repaired = snapshot [ project "alpha" [ matching ] ]
    Assert.That((decision repaired "alpha" "W").Requirements,
                Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
    let released = authorize repaired "alpha" "W"
    Assert.That((decision released "alpha" "W").NormalDispatchAllowed, Is.True)

[<Test>]
let captured_launches_require_all_exact_terminal_evidence_and_unknown_identity_stays_fenced () =
    let settled operation =
        { OperationId = operation
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.Launch
          SourceSha = None
          Outcome = EffectOutcome.DefinitiveNoEffect ("no-effect-" + operation, true, true) }
    let identities = [ capturedLaunch "L-active"; capturedLaunch "L-next" ]
    let original =
        { work "W" ArtifactGoal.Unchanged with
            CapturedInFlightLaunch = true
            CapturedLaunches = identities
            Effects = [ settled "L-active" ] }
    let pending = snapshot [ project "alpha" [ original ] ]
    has Requirement.InFlightLaunchReconciliation (decision pending "alpha" "W").Requirements
    let both = { original with Effects = [ settled "L-active"; settled "L-next" ] }
    let approved = authorize (snapshot [ project "alpha" [ both ] ]) "alpha" "W"
    Assert.That((decision approved "alpha" "W").NormalDispatchAllowed, Is.True)

    // Missing original identity cannot be reconstructed from any other
    // journal, and inconsistent/malformed captured metadata remains blocked.
    for invalid in
        [ { original with CapturedLaunches = [] }
          { original with CapturedLaunches = [ capturedLaunch "L-other" ] }
          { original with CapturedInFlightLaunch = false }
          { both with CapturedLaunches = identities @ [ capturedLaunch "L-active" ] }
          { both with CapturedLaunches = [ { capturedLaunch "L-active" with WorkId = "other" } ] }
          { both with CapturedLaunches = [ { capturedLaunch "L-active" with Generation = 3 } ] }
          { both with CapturedLaunches = [ { capturedLaunch "L-active" with ProjectId = "other" } ] } ] do
        let candidate = snapshot [ project "alpha" [ invalid ] ] |> fun s -> authorize s "alpha" "W"
        has Requirement.InFlightLaunchReconciliation (decision candidate "alpha" "W").Requirements
        Assert.That((decision candidate "alpha" "W").NormalDispatchAllowed, Is.False)

[<Test>]
let captured_launch_identity_changes_invalidate_existing_authorization_even_with_same_effect_receipts () =
    let settled operation =
        { OperationId = operation
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.Launch
          SourceSha = None
          Outcome = EffectOutcome.DefinitiveNoEffect ("terminal-" + operation, true, true) }
    let original =
        { work "W" ArtifactGoal.Unchanged with
            CapturedInFlightLaunch = true
            CapturedLaunches = [ capturedLaunch "L-original" ]
            Effects = [ settled "L-original"; settled "L-other" ] }
    let approved = authorize (snapshot [ project "alpha" [ original ] ]) "alpha" "W"
    let swapped =
        { original with CapturedLaunches = [ capturedLaunch "L-other" ]; Release = approved.Projects.Head.Works.Head.Release }
    let changed = snapshot [ project "alpha" [ swapped ] ]
    let before = decision approved "alpha" "W"
    let after = decision changed "alpha" "W"
    Assert.That(after.EvidenceReceipts, Is.EqualTo(box before.EvidenceReceipts))
    Assert.That(after.EvidenceDigest, Is.Not.EqualTo(before.EvidenceDigest))
    Assert.That(after.Requirements, Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
    Assert.That(after.ReleaseAuthorized, Is.False)
    let reordered = { original with CapturedLaunches = [ capturedLaunch "L-other"; capturedLaunch "L-original" ] }
    let two = authorize (snapshot [ project "alpha" [ reordered ] ]) "alpha" "W"
    let reorderedAgain =
        { two with Projects = [ { two.Projects.Head with
                                   Works = [ { two.Projects.Head.Works.Head with
                                                 CapturedLaunches = List.rev reordered.CapturedLaunches } ] } ] }
    Assert.That((decision reorderedAgain "alpha" "W").ReleaseAuthorized, Is.True)

[<Test>]
let unknown_source_push_and_unreconciled_original_job_keep_effect_fenced () =
    let push outcome =
        { OperationId = "push-op"
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.SourcePush
          SourceSha = Some source.SourceSha
          Outcome = outcome }
    for outcome in
        [ EffectOutcome.Unknown
          EffectOutcome.DefinitiveNoEffect ("no-effect", false, true)
          EffectOutcome.Observed ("observed", true, false, true) ] do
        let input =
            snapshot [ project "alpha" [ { completed "W" with Effects = [ push outcome ] } ] ]
            |> fun state -> authorize state "alpha" "W"
        Assert.That((decision input "alpha" "W").ReleaseAuthorized, Is.False)
        Assert.That((decision input "alpha" "W").Requirements.IsEmpty, Is.False)
    let observed = push (EffectOutcome.Observed ("observed", true, true, true))
    let input = snapshot [ project "alpha" [ { completed "W" with Effects = [ observed ] } ] ]
    let resolved = authorize input "alpha" "W"
    Assert.That((decision resolved "alpha" "W").ReleaseAuthorized, Is.True)

[<Test>]
let two_projects_reconcile_independently_but_neither_unknown_stop_nor_global_stop_can_dispatch () =
    let alpha = project "alpha" [ work "A" ArtifactGoal.Unchanged ]
    let beta = project "beta" [ { work "B" ArtifactGoal.Unchanged with CapturedInFlightLaunch = true } ]
    let first = snapshot [ beta; alpha ] |> fun state -> authorize state "alpha" "A"
    let plan = WorkRecovery.plan first
    Assert.That(plan.NormalDispatchAllowed, Is.True)
    Assert.That(plan.Projects |> List.map (fun p -> p.ProjectId), Is.EqualTo(box [ "alpha"; "beta" ]))
    Assert.That((decision first "alpha" "A").NormalDispatchAllowed, Is.True)
    Assert.That((decision first "beta" "B").NormalDispatchAllowed, Is.False)
    Assert.That(plan.RecoveryQueue |> List.exists (fun item -> item.ProjectId = "beta" && item.WorkId = Some "B"), Is.True)
    Assert.That(plan.RecoveryQueue |> List.forall (fun item -> item.Queue = "Recovery"), Is.True)
    for stop in [ StopFence.Active; StopFence.Unknown; StopFence.Cleared ("forged", 3, false) ] do
        let blocked = WorkRecovery.plan { first with GlobalStop = stop }
        has Requirement.GlobalStopClearance blocked.GlobalRequirements
        Assert.That(blocked.NormalDispatchAllowed, Is.False)
        Assert.That(blocked.Projects |> List.forall (fun p -> not p.NormalDispatchAllowed), Is.True)

[<Test>]
let scoped_release_is_bound_to_snapshot_project_graph_generation_version_and_exact_receipts () =
    let baseInput = snapshot [ project "alpha" [ work "W" ArtifactGoal.Unchanged ] ]
    let authorized = authorize baseInput "alpha" "W"
    Assert.That((decision authorized "alpha" "W").ReleaseAuthorized, Is.True)
    let originalProject = authorized.Projects.Head
    let originalWork = originalProject.Works.Head
    let changes =
        [ { originalProject with GraphVersion = originalProject.GraphVersion + 1 }
          { originalProject with KnownPreviousSequence = Some 9L }
          { originalProject with GapSeal = Some { sealedGap with ReceiptId = "different-gap-receipt" } }
          { originalProject with Works = [ { originalWork with WorkVersion = originalWork.WorkVersion + 1 } ] }
          { originalProject with Works = [ { originalWork with Generation = originalWork.Generation + 1 } ] } ]
    for candidate in changes do
        let updated = { authorized with Projects = [ candidate ] }
        Assert.That((decision updated "alpha" "W").ReleaseAuthorized, Is.False)
        Assert.That((decision updated "alpha" "W").NormalDispatchAllowed, Is.False)

[<Test>]
let unknown_highwater_and_invalid_gap_coverage_are_not_equivalent_to_no_prior_actions () =
    let state = snapshot [ project "alpha" [ work "W" ArtifactGoal.Unchanged ] ]
    let missingDb = { state with KnownPreviousDatabaseSequence = None }
    has Requirement.UnknownDatabaseHighWater (WorkRecovery.plan missingDb).GlobalRequirements
    let missingProject =
        { state with Projects = [ { state.Projects.Head with KnownPreviousSequence = None } ] }
    has Requirement.UnknownProjectHighWater (WorkRecovery.plan missingProject).Projects.Head.Requirements
    let invalidGapProject =
        { state.Projects.Head with GapSeal = Some { sealedGap with ExternalEffectsCovered = false } }
    let incomplete = { state with Projects = [ invalidGapProject ] }
    has Requirement.SequenceGapSealRequired (WorkRecovery.plan incomplete).Projects.Head.Requirements
    let unverifiedSet = { state with CompleteProjectSetVerified = false }
    has Requirement.UnverifiedProjectSet (WorkRecovery.plan unverifiedSet).GlobalRequirements
    let later = { state with KnownPreviousDatabaseSequence = Some 9L }
    has Requirement.InvalidDatabaseSequence (WorkRecovery.plan later).GlobalRequirements

[<Test>]
let duplicate_identities_and_conflicting_journals_fail_closed_instead_of_replaying () =
    let journal =
        { OperationId = "L"
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.Launch
          SourceSha = None
          Outcome = EffectOutcome.Observed ("terminal", true, true, true) }
    let original = { work "W" ArtifactGoal.Unchanged with Effects = [ journal; journal ] }
    let initial = snapshot [ project "alpha" [ original ] ] |> fun state -> authorize state "alpha" "W"
    Assert.That((decision initial "alpha" "W").NormalDispatchAllowed, Is.True)
    let conflict = { journal with Outcome = EffectOutcome.Unknown }
    let withConflict =
        { initial with
            Projects = [ project "alpha" [ { original with Effects = [ journal; conflict ] } ] ] }
    has (Requirement.ConflictingExternalEffect "L") (decision withConflict "alpha" "W").Requirements
    let duplicateWork = snapshot [ project "alpha" [ original; original ] ]
    has Requirement.DuplicateWorkIdentity (WorkRecovery.plan duplicateWork).Projects.Head.Requirements
    let duplicateProject = snapshot [ project "alpha" [ original ]; project "alpha" [ original ] ]
    has Requirement.DuplicateProjectIdentity (WorkRecovery.plan duplicateProject).GlobalRequirements

[<Test>]
let unexpected_new_git_artifact_never_silently_resumes_old_pending_stage () =
    let unexpected = { work "W" ArtifactGoal.Unchanged with Source = Some source }
    let input = snapshot [ project "alpha" [ unexpected ] ] |> fun state -> authorize state "alpha" "W"
    has Requirement.UnexpectedSourceArtifact (decision input "alpha" "W").Requirements
    Assert.That((decision input "alpha" "W").NormalDispatchAllowed, Is.False)

[<Test>]
let pure_plan_has_stable_queue_ids_and_does_not_create_persistent_agent_or_manual_grant () =
    let a = project "alpha" [ completed "A"; work "B" ArtifactGoal.Unchanged ]
    let b = project "beta" [ work "C" ArtifactGoal.Unchanged ]
    let first = snapshot [ a; b ]
    let second = snapshot [ b; { a with Works = List.rev a.Works } ]
    let plan1 = WorkRecovery.plan first
    let plan2 = WorkRecovery.plan second
    Assert.That(plan1, Is.EqualTo(plan2))
    Assert.That(plan1.RecoveryQueue |> List.map (fun q -> q.Id) |> Set.ofList |> Set.count,
                Is.EqualTo(plan1.RecoveryQueue.Length))
    Assert.That(plan1.RecoveryQueue |> List.forall (fun item -> item.Queue = "Recovery"), Is.True)
    // Recovery queue items carry no Agent, model session, bearer or manual grant.
    Assert.That(plan1.NormalDispatchAllowed, Is.False)

[<Test>]
let recovery_release_and_git_proofs_are_bound_to_exact_project_and_work_identity () =
    let alpha = snapshot [ project "alpha" [ work "W" ArtifactGoal.Unchanged ] ]
    let authorized = authorize alpha "alpha" "W"
    let stolenAuthorization = authorized.Projects.Head.Works.Head.Release
    let sameVersionsOtherProject =
        snapshot [ project "beta" [ { work "W" ArtifactGoal.Unchanged with Release = stolenAuthorization } ] ]
    has Requirement.RecoveryAuthorizationRequired (decision sameVersionsOtherProject "beta" "W").Requirements
    let sameProjectOtherWork =
        snapshot [ project "alpha" [ { work "different" ArtifactGoal.Unchanged with Release = stolenAuthorization } ] ]
    has Requirement.RecoveryAuthorizationRequired (decision sameProjectOtherWork "alpha" "different").Requirements
    let stolenEvidence =
        snapshot [ project "beta" [ completed "W" ] ]
    has Requirement.UnverifiedSourceProof (decision stolenEvidence "beta" "W").Requirements
    has Requirement.InvalidFrozenReview (decision stolenEvidence "beta" "W").Requirements
    has Requirement.InvalidIntegration (decision stolenEvidence "beta" "W").Requirements
    let stolenGap =
        snapshot [ { project "beta" [ work "W" ArtifactGoal.Unchanged ] with GapSeal = Some sealedGap } ]
    has Requirement.SequenceGapSealRequired (WorkRecovery.plan stolenGap).Projects.Head.Requirements

[<Test>]
let same_external_operation_id_cannot_settle_two_work_scopes_even_across_projects () =
    let effect workId =
        { OperationId = "single-use-operation"
          WorkId = workId
          Generation = 2
          Kind = EffectKind.Launch
          SourceSha = None
          Outcome = EffectOutcome.DefinitiveNoEffect ("terminal-proof", true, true) }
    let alpha = project "alpha" [ { work "A" ArtifactGoal.Unchanged with Effects = [ effect "A" ] } ]
    let beta = project "beta" [ { work "B" ArtifactGoal.Unchanged with Effects = [ effect "B" ] } ]
    let original = snapshot [ alpha; beta ]
    let result = WorkRecovery.plan original
    has (Requirement.ConflictingExternalEffect "single-use-operation") result.GlobalRequirements
    Assert.That(result.NormalDispatchAllowed, Is.False)
    let authorized = original |> fun s -> authorize s "alpha" "A" |> fun s -> authorize s "beta" "B"
    Assert.That((decision authorized "alpha" "A").ReleaseAuthorized, Is.False)
    Assert.That((decision authorized "beta" "B").ReleaseAuthorized, Is.False)

[<Test>]
let stale_account_gap_must_cover_projects_and_external_effects_beyond_the_snapshot () =
    let initial = snapshot [ project "alpha" [ work "W" ArtifactGoal.Unchanged ] ]
    let valid = authorize initial "alpha" "W"
    Assert.That((decision valid "alpha" "W").ReleaseAuthorized, Is.True)
    for corrupted in
        [ None
          Some { accountSeal with ProjectSetCovered = false }
          Some { accountSeal with ExternalEffectsCovered = false }
          Some { accountSeal with CanonicallyVerified = false }
          Some { accountSeal with CoveredThroughDatabaseSequence = 11L }
          Some { accountSeal with KnownPreviousDatabaseSequence = 13L } ] do
        let input = { valid with AccountGapSeal = corrupted }
        has Requirement.AccountSequenceGapSealRequired (WorkRecovery.plan input).GlobalRequirements
        Assert.That((decision input "alpha" "W").NormalDispatchAllowed, Is.False)
    let projectGraphMissing =
        { valid with
            Projects =
                [ { valid.Projects.Head with
                      GapSeal = Some { sealedGap with WorkGraphCovered = false } } ] }
    has Requirement.SequenceGapSealRequired (WorkRecovery.plan projectGraphMissing).Projects.Head.Requirements

[<Test>]
let project_recovery_queue_identity_is_disjoint_from_adversarial_work_id () =
    let badProject =
        { project "alpha" [ work "(project)" ArtifactGoal.Unchanged ] with GapSeal = None }
    let result = WorkRecovery.plan (snapshot [ badProject ])
    Assert.That(result.RecoveryQueue.Length, Is.EqualTo(2))
    Assert.That(result.RecoveryQueue |> List.map (fun item -> item.Id) |> Set.ofList |> Set.count,
                Is.EqualTo(2))
    Assert.That(result.RecoveryQueue |> List.exists (fun item -> item.WorkId.IsNone), Is.True)
    Assert.That(result.RecoveryQueue |> List.exists (fun item -> item.WorkId = Some "(project)"), Is.True)

[<Test>]
let historical_accepted_review_and_integration_cannot_cross_work_generation_or_role_graph_lineage () =
    let current = { lineage with Generation = 3 }
    let next =
        { completed "W" with
            Generation = 3
            Source = Some { source with OriginalLineage = current } }
    let stale = snapshot [ project "alpha" [ next ] ]
    let denied = decision (authorize stale "alpha" "W") "alpha" "W"
    has Requirement.InvalidFrozenReview denied.Requirements
    has Requirement.InvalidIntegration denied.Requirements
    Assert.That(denied.ReleaseAuthorized, Is.False)
    let fresh =
        { next with
            Review = Some { review with OriginalLineage = current }
            Integration =
                Some { integration with
                           OriginalLineage = current
                           Authorization = { integrationApproval with OriginalLineage = current } } }
    let approved = authorize (snapshot [ project "alpha" [ fresh ] ]) "alpha" "W"
    Assert.That((decision approved "alpha" "W").ReleaseAuthorized, Is.True)

    // Even with exactly the same source SHA, independent role/graph/version
    // revisions require their own originally bound review and integration.
    let changedRole = sha 'd'
    let roleStage = { lineage with RoleAuthoritySha = changedRole }
    let roleChanged =
        { completed "W" with
            RoleAuthoritySha = changedRole
            Source = Some { source with OriginalLineage = roleStage } }
    let deniedRole = decision (snapshot [ project "alpha" [ roleChanged ] ]) "alpha" "W"
    has Requirement.InvalidFrozenReview deniedRole.Requirements
    has Requirement.InvalidIntegration deniedRole.Requirements

    let graphStage = { lineage with ProjectGraphVersion = 4 }
    let graphChanged =
        { project "alpha" [ { completed "W" with Source = Some { source with OriginalLineage = graphStage } } ] with
            GraphVersion = 4 }
    let deniedGraph = decision (snapshot [ graphChanged ]) "alpha" "W"
    has Requirement.InvalidFrozenReview deniedGraph.Requirements
    has Requirement.InvalidIntegration deniedGraph.Requirements

    let versionStage = { lineage with WorkVersion = 5 }
    let versionChanged =
        { completed "W" with
            WorkVersion = 5
            Source = Some { source with OriginalLineage = versionStage } }
    let deniedVersion = decision (snapshot [ project "alpha" [ versionChanged ] ]) "alpha" "W"
    has Requirement.InvalidFrozenReview deniedVersion.Requirements
    has Requirement.InvalidIntegration deniedVersion.Requirements

[<Test>]
let integration_requires_original_verified_authorization_and_exact_accepted_review_receipt () =
    for stale in
        [ { integration with AcceptedReviewId = "earlier-review" }
          { integration with AcceptedReviewReceiptId = "earlier-review-receipt" }
          { integration with Authorization = { integrationApproval with ReceiptId = "" } }
          { integration with Authorization = { integrationApproval with CanonicallyVerified = false } }
          { integration with Authorization = { integrationApproval with OriginalLineage = { lineage with Generation = 1 } } }
          { integration with Authorization = { integrationApproval with AcceptedReviewReceiptId = "old-review" } }
          { integration with Authorization = { integrationApproval with SourceSha = sha 'd' } }
          { integration with SourceRepositoryId = "other-repo" }
          { integration with SourceRef = "refs/heads/other" } ] do
        let candidate = { completed "W" with Integration = Some stale }
        has Requirement.InvalidIntegration (decision (snapshot [ project "alpha" [ candidate ] ]) "alpha" "W").Requirements

[<Test>]
let same_receipt_ids_cannot_preserve_release_after_source_or_integration_payload_changes () =
    let produced = { work "W" ArtifactGoal.SourceProduced with Source = Some source }
    let approvedSource = authorize (snapshot [ project "alpha" [ produced ] ]) "alpha" "W"
    Assert.That((decision approvedSource "alpha" "W").ReleaseAuthorized, Is.True)
    for modified in
        [ { source with SourceSha = sha 'f' }
          { source with RepositoryId = "repo:another" }
          { source with SourceRef = "refs/heads/other" } ] do
        let currentProject = approvedSource.Projects.Head
        let currentWork = currentProject.Works.Head
        let changed =
            { approvedSource with
                Projects = [ { currentProject with Works = [ { currentWork with Source = Some modified } ] } ] }
        let pending = decision changed "alpha" "W"
        Assert.That(pending.EvidenceReceipts, Is.EqualTo(box ((decision approvedSource "alpha" "W").EvidenceReceipts)))
        Assert.That(pending.Requirements, Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
        Assert.That(pending.ReleaseAuthorized, Is.False)
        let renewed = authorize changed "alpha" "W"
        Assert.That((decision renewed "alpha" "W").ReleaseAuthorized, Is.True)

    let approvedIntegration = authorize (snapshot [ project "alpha" [ completed "W" ] ]) "alpha" "W"
    let original = approvedIntegration.Projects.Head.Works.Head
    for modified in
        [ { integration with IntegratedSha = sha 'e' }
          { integration with TargetRef = "refs/heads/release"
                             Authorization = { integrationApproval with TargetRef = "refs/heads/release" } }
          { integration with SubmissionId = "submission-2" } ] do
        let altered =
            { approvedIntegration with
                Projects =
                    [ { approvedIntegration.Projects.Head with
                          Works = [ { original with Integration = Some modified } ] } ] }
        let pending = decision altered "alpha" "W"
        Assert.That(pending.EvidenceReceipts, Is.EqualTo(box ((decision approvedIntegration "alpha" "W").EvidenceReceipts)))
        Assert.That(pending.Requirements, Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
        Assert.That(pending.ReleaseAuthorized, Is.False)

    // Review identity also participates even when the accepted-review and
    // integration receipt IDs, source SHA and Work generation stay unchanged.
    let newReview = { review with ReviewId = "new-review" }
    let pairedIntegration =
        { integration with
            AcceptedReviewId = newReview.ReviewId
            Authorization = { integrationApproval with AcceptedReviewId = newReview.ReviewId } }
    let reviewChanged =
        { approvedIntegration with
            Projects =
                [ { approvedIntegration.Projects.Head with
                      Works =
                          [ { original with
                                Review = Some newReview
                                Integration = Some pairedIntegration } ] } ] }
    let pendingReview = decision reviewChanged "alpha" "W"
    Assert.That(pendingReview.Requirements, Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
    Assert.That(pendingReview.ReleaseAuthorized, Is.False)

[<Test>]
let unchanged_seal_receipt_and_external_effect_receipt_cannot_hide_changed_payload () =
    let approved = authorize (snapshot [ project "alpha" [ work "W" ArtifactGoal.Unchanged ] ]) "alpha" "W"
    let currentProject = approved.Projects.Head
    let broadenedSeal =
        { currentProject with
            GapSeal = Some { sealedGap with CoveredThroughSequence = 9L } }
    let candidate = { approved with Projects = [ broadenedSeal ] }
    Assert.That((decision candidate "alpha" "W").Requirements,
                Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))
    Assert.That((decision candidate "alpha" "W").EvidenceReceipts,
                Is.EqualTo(box ((decision approved "alpha" "W").EvidenceReceipts)))
    let accountChanged =
        { approved with AccountGapSeal = Some { accountSeal with CoveredThroughDatabaseSequence = 13L } }
    Assert.That((decision accountChanged "alpha" "W").Requirements,
                Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))

    let push =
        { OperationId = "source-push"
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.SourcePush
          SourceSha = Some source.SourceSha
          Outcome = EffectOutcome.Observed ("observed", true, true, true) }
    let produced = { work "W" ArtifactGoal.SourceProduced with Source = Some source; Effects = [ push ] }
    let approvedPush = authorize (snapshot [ project "alpha" [ produced ] ]) "alpha" "W"
    Assert.That((decision approvedPush "alpha" "W").ReleaseAuthorized, Is.True)
    let updatedPush = { push with Outcome = EffectOutcome.Observed ("observed", true, true, false) }
    let modifiedWork = { approvedPush.Projects.Head.Works.Head with Effects = [ updatedPush ] }
    let changed =
        { approvedPush with
            Projects = [ { approvedPush.Projects.Head with Works = [ modifiedWork ] } ] }
    Assert.That((decision changed "alpha" "W").EvidenceReceipts,
                Is.EqualTo(box ((decision approvedPush "alpha" "W").EvidenceReceipts)))
    Assert.That((decision changed "alpha" "W").Requirements,
                Is.EqualTo(box [ Requirement.RecoveryAuthorizationRequired ]))

[<Test>]
let evidence_fingerprint_is_order_invariant_but_tracks_all_effect_rows () =
    let effect id =
        { OperationId = id
          WorkId = "W"
          Generation = 2
          Kind = EffectKind.Launch
          SourceSha = None
          Outcome = EffectOutcome.DefinitiveNoEffect ("terminal-" + id, true, true) }
    let item = { work "W" ArtifactGoal.Unchanged with Effects = [ effect "b"; effect "a" ] }
    let approved = authorize (snapshot [ project "alpha" [ item ] ]) "alpha" "W"
    let shuffled =
        { approved with
            Projects = [ { approved.Projects.Head with
                              Works = [ { approved.Projects.Head.Works.Head with Effects = [ effect "a"; effect "b" ] } ] } ] }
    let before = decision approved "alpha" "W"
    let after = decision shuffled "alpha" "W"
    Assert.That(after.EvidenceDigest, Is.EqualTo(before.EvidenceDigest))
    Assert.That(after.ReleaseAuthorized, Is.True)
