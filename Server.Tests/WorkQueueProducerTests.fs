module ServerWorkQueueProducerTests

open System
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
module Producer = Jarvis.WorkQueues.WorkQueueProducers
let workId = Producer.workId
let projectEvent = Producer.projectEvent
let applyEvent = Producer.applyEvent
let emptyRoleCatalog = Producer.emptyRoleCatalog
let configureRole = Producer.configureRole
let selectRoleDemand = Producer.selectRoleDemand

let private ok = function
    | Ok result -> result
    | Error error -> failwithf "Unexpected WQ-04 failure: %A" error

let private error (expected: Rejection) (result: Result<'T, Rejection>) =
    match result with
    | Error actual -> Assert.That((actual = expected), Is.True, sprintf "Expected %A; got %A" expected actual)
    | Ok _ -> Assert.Fail(sprintf "Expected %A" expected)

let private project () =
    emptyProject
        (Map.ofList [ "Lead", set [ "planning"; "role:Lead"; "challenge" ]
                      "Worker", set [ "implementation" ]
                      "Reviewer", set [ "review" ]
                      "Flexible", set [ "implementation"; "review" ] ])

let private source task =
    { RepositoryId = "Jarvis"
      TaskId = task
      TargetRef = "refs/heads/main"
      LineageId = "main-" + task
      PinnedBaseCommit = String.replicate 40 "a"
      ReachableRef = None
      ContinuationSha = None
      Generation = 1 }

let private event id kind prerequisites : QueueProducerEvent =
    { ProjectId = "Jarvis"
      SourceId = id
      StageGeneration = 1
      Origin = kind
      HardPrerequisites = set prerequisites }

let private publish (p: Project) at input =
    applyEvent p.Version ("producer:" + input.SourceId) at input p |> ok

let private roleConfig role version queues =
    { RoleId = role
      Version = version
      Instructions = "Pinned instructions for " + role
      QueueSubscriptions = Set.ofList queues
      Enabled = true }

let private roles () =
    let one role queues catalog =
        configureRole catalog.Revision 0 ("configure:" + role)
            (roleConfig role 1 queues) catalog |> ok
    emptyRoleCatalog
    |> one "Lead" [ "planning"; "role:Lead"; "challenge" ]
    |> one "Worker" [ "implementation" ]
    |> one "Reviewer" [ "review" ]
    |> one "Flexible" [ "implementation"; "review" ]

let private coverage (p: Project) (catalog: RoleCatalog) =
    { ProjectVersion = p.Version
      GraphVersion = p.GraphVersion
      ProducerHighWater = 10L
      QueueIndexedThrough = 10L
      MessageHighWater = 4L
      MessageIndexedThrough = 4L
      SourceHighWater = 3L
      SourceIndexedThrough = 3L
      RoleCatalogRevision = catalog.Revision
      ProjectHeadVerified = true
      CrossProjectCovered = true }

let private producerAdmissionBlocked =
    Rejection.IncompleteCoverage
        "WQ-04 authoritative source/index and pinned role admission proof unavailable"

let private launch id role =
    { Id = id; ProjectId = "Jarvis"; RoleId = role; RoleVersion = 1
      AccountTokenId = "token-" + id
      State = LaunchState.ReservedUnregistered
      Lease = None; Version = 0; Credit = None
      PrimaryWorkId = None; EverClaimed = false
      AncillaryMessages = Set.empty; DispatchCount = 0 }

let private taskMessage id =
    { Id = id; Subject = Subject.Task "T"; Body = "Please examine"
      State = MessageState.Pending; HandlerGeneration = 0; WorkId = None }

let private roleMessage id role =
    { Id = id; Subject = Subject.Role role; Body = "Please handle"
      State = MessageState.Pending; HandlerGeneration = 0; WorkId = None }

[<Test>]
let stage_identity_is_deterministic_and_generation_bound () =
    let sourceEvent = event "task-request-T" (QueueProducerOrigin.TaskRequested "T") []
    let id = workId sourceEvent
    Assert.That(workId sourceEvent, Is.EqualTo(id))
    Assert.That(workId { sourceEvent with StageGeneration = 2 }, Is.Not.EqualTo(id))
    Assert.That(workId { sourceEvent with SourceId = "task-request-U" }, Is.Not.EqualTo(id))
    let p = publish (project ()) 11L sourceEvent
    Assert.That(p.Works.ContainsKey id, Is.True)
    Assert.That(p.Works.[id].AvailableSequence, Is.EqualTo(1L))
    Assert.That(p.Works.[id].AvailableAt, Is.EqualTo(11L))
    error (Rejection.InvalidState "duplicate immutable work ID")
        (applyEvent p.Version "different-event-op" 12L sourceEvent p)
    Assert.That(p.ProducerCount, Is.EqualTo(0))

[<Test>]
let producer_does_not_accept_forged_source_identity_or_unknown_hard_prerequisite () =
    let p = project ()
    let invalidLineage = { (source "T") with PinnedBaseCommit = "fake" }
    let bad = event "impl-1" (QueueProducerOrigin.ImplementationReady("T", invalidLineage)) []
    error (Rejection.InvalidState "producer lacks exact canonical event evidence")
        (projectEvent p bad)
    let missing = event "plan-T" (QueueProducerOrigin.TaskRequested "T") [ "missing-stage" ]
    error (Rejection.IncompleteCoverage "producer hard prerequisite stage not indexed")
        (projectEvent p missing)
    let sourceEvent = event "plan-T" (QueueProducerOrigin.TaskRequested "T") []
    let p = publish p 10L sourceEvent
    error Rejection.CasConflict (applyEvent 0 "late-op" 12L
        (event "impl-T" (QueueProducerOrigin.ImplementationReady("T", source "T")) []) p)

[<Test>]
let task_message_is_passive_role_message_has_one_distinct_runnable_generation () =
    let task = event "operator-msg-1" (QueueProducerOrigin.OperatorMessage(taskMessage "M1")) []
    let role = event "operator-msg-2" (QueueProducerOrigin.OperatorMessage(roleMessage "M2" "Lead")) []
    let p = project () |> fun p -> publish p 10L task |> fun p -> publish p 11L role
    Assert.That(p.Messages.["M1"].WorkId.IsNone, Is.True)
    Assert.That(p.Works.Count, Is.EqualTo(1))
    Assert.That(p.Messages.["M2"].WorkId, Is.EqualTo(Some(workId role)))
    Assert.That(p.Works.[workId role].AvailableAt, Is.EqualTo(11L))
    let catalog = roles ()
    // Role demand cannot be admitted while the catalog is not persisted
    // at the same project-CAS boundary as launch eligibility.
    error producerAdmissionBlocked (selectRoleDemand (coverage p catalog) catalog p "Lead")
    let duplicate = event "operator-msg-another-projection" (QueueProducerOrigin.OperatorMessage(roleMessage "M2" "Lead")) []
    error (Rejection.InvalidState "duplicate message ID")
        (applyEvent p.Version "post-duplicate" 12L duplicate p)
    let invalidMessage =
        { (roleMessage "M3" "Lead") with State = MessageState.ClaimedAncillary "other" }
    let forged = event "invalid-msg" (QueueProducerOrigin.OperatorMessage invalidMessage) []
    error (Rejection.InvalidState "producer lacks exact canonical event evidence")
        (projectEvent p forged)

[<Test>]
let blocking_review_beats_older_ordinary_work_and_blocked_work_stays_blocked () =
    let older = event "plan-A" (QueueProducerOrigin.TaskRequested "A") []
    let review = event "review-R" (QueueProducerOrigin.FrozenReviewRequested(
                    "B", "R", String.replicate 40 "a", "refs/heads/frozen")) []
    let dependent = event "correction-B" (QueueProducerOrigin.ReviewCorrection("B", "R", source "B")) [ workId review ]
    let p = project () |> fun p -> publish p 10L older
                       |> fun p -> publish p 20L review
                       |> fun p -> publish p 30L dependent
    let order = orderAvailable p |> ok |> List.map (fun s -> s.Id)
    Assert.That(order, Is.EqualTo(box [ workId review; workId older ]))
    Assert.That(p.Works.[workId dependent].State, Is.EqualTo(WorkState.Blocked))
    let catalog = roles ()
    error producerAdmissionBlocked (selectRoleDemand (coverage p catalog) catalog p "Reviewer")
    error producerAdmissionBlocked (selectRoleDemand (coverage p catalog) catalog p "Worker")

[<Test>]
let versioned_roles_pin_old_launches_and_fail_closed_when_history_is_missing () =
    let p = project () |> fun p -> publish p 1L (event "plan-T" (QueueProducerOrigin.TaskRequested "T") [])
    let catalog = roles ()
    let previous = catalog.Current.["Lead"]
    let v2 = roleConfig "Lead" (previous + 1) [ "role:Lead" ]
    let revised = configureRole catalog.Revision previous "role-lead-v2" v2 catalog |> ok
    Assert.That(revised.Versions.["Lead"].[1].QueueSubscriptions.Contains "planning", Is.True)
    Assert.That(revised.Versions.["Lead"].[2].QueueSubscriptions.Contains "planning", Is.False)
    Assert.That(configureRole catalog.Revision previous "role-lead-v2" v2 revised |> ok,
                Is.EqualTo(revised))
    error Rejection.OperationIdReused
        (configureRole revised.Revision 2 "role-lead-v2" { v2 with Instructions = "changed" } revised)
    error Rejection.CasConflict
        (configureRole catalog.Revision 1 "role-lead-v3" (roleConfig "Lead" 2 [ "planning" ]) revised)
    let p = admitProject p.Version "admit:old" (launch "old" "Lead") p |> ok
    // Pure v1/v2 history exists, but it cannot authorize WQ-02's legacy
    // unversioned match/claim predicate until a CAS-visible shared catalog
    // is implemented. Both consistent and incomplete snapshots fail closed.
    error producerAdmissionBlocked (selectRoleDemand (coverage p revised) revised p "Lead")
    let invalid = { revised with Versions = revised.Versions.Add("Lead", Map.ofList [ 2, v2 ]) }
    error producerAdmissionBlocked (selectRoleDemand (coverage p invalid) invalid p "Lead")

[<Test>]
let role_demand_rematches_existing_launch_before_choosing_older_work () =
    let r = event "review-R" (QueueProducerOrigin.FrozenReviewRequested(
                    "T", "R", String.replicate 40 "a", "refs/heads/frozen")) []
    let i = event "impl-T" (QueueProducerOrigin.ImplementationReady("T", source "T")) []
    let p = project () |> fun p -> publish p 1L r |> fun p -> publish p 2L i
    let p = admitProject p.Version "admit:reviewer" (launch "reviewer" "Reviewer") p |> ok
    let catalog = roles ()
    error producerAdmissionBlocked (selectRoleDemand (coverage p catalog) catalog p "Flexible")
    Assert.That(p.Launches.["reviewer"].Credit, Is.EqualTo(Some(workId r)))

[<Test>]
let missing_highwater_or_cross_project_evidence_is_not_a_healthy_empty_queue () =
    let p = project ()
    let catalog = roles ()
    let source = coverage p catalog
    error (Rejection.IncompleteCoverage "producer queue high-water/index")
        (selectRoleDemand { source with QueueIndexedThrough = 9L } catalog p "Lead")
    error (Rejection.IncompleteCoverage "source/effect high-water/index")
        (selectRoleDemand { source with SourceIndexedThrough = 0L } catalog p "Lead")
    error (Rejection.IncompleteCoverage "unverified project/cross-project producer head")
        (selectRoleDemand { source with CrossProjectCovered = false } catalog p "Lead")
    error (Rejection.IncompleteCoverage "source/index generation does not match pinned project head")
        (selectRoleDemand { source with GraphVersion = p.GraphVersion + 1 } catalog p "Lead")
    // Equal self-reported counters, even with every boolean set, are not
    // authoritative coverage of a possibly missing canonical producer event.
    error producerAdmissionBlocked (selectRoleDemand source catalog p "Lead")

[<Test>]
let circular_hard_stages_warn_but_do_not_run_and_edit_clears_warning () =
    let a = event "A" (QueueProducerOrigin.TaskRequested "A") []
    let b = event "B" (QueueProducerOrigin.TaskRequested "B") []
    let p = project () |> fun p -> publish p 1L a |> fun p -> publish p 2L b
    let p = setHardPrerequisite p.Version "dep-A-B" (workId a) (workId b) 10L p |> ok
    let p = setHardPrerequisite p.Version "dep-B-A" (workId b) (workId a) 11L p |> ok
    let diagnostics = WorkQueueDependencyDiagnostics.analyze p.GraphVersion true p.Works |> ok
    Assert.That(diagnostics.CycleWarnings.Length, Is.EqualTo(1))
    Assert.That(diagnostics.CycleWarnings.Head.WitnessEdges.Length, Is.EqualTo(2))
    Assert.That(orderAvailable p |> ok, Is.Empty)
    let catalog = roles ()
    error producerAdmissionBlocked (selectRoleDemand (coverage p catalog) catalog p "Lead")
    let beforeWake = p.NextAvailability
    let clear = removeHardPrerequisite p.Version "remove-B-A" (workId b) (workId a) 12L p |> ok
    let after = WorkQueueDependencyDiagnostics.analyze clear.GraphVersion true clear.Works |> ok
    Assert.That(after.CycleWarnings, Is.Empty)
    Assert.That(clear.Works.[workId b].State, Is.EqualTo(WorkState.Available))
    Assert.That(clear.Works.[workId b].AvailableAt, Is.EqualTo(12L))
    Assert.That(clear.Works.[workId b].AvailableSequence, Is.GreaterThan(beforeWake))
    Assert.That(orderAvailable clear |> ok |> List.map (fun stage -> stage.Id),
                Is.EqualTo(box [ workId b ]))

[<Test>]
let integration_policy_never_reruns_push_before_exact_completed_authorization () =
    let integration = event "accepted-R" (QueueProducerOrigin.AcceptedIntegration(
                    "T", "R", String.replicate 40 "a", "refs/heads/main")) []
    let priorReview =
        event "review-R" (QueueProducerOrigin.FrozenReviewRequested(
                    "T", "R", String.replicate 40 "a", "refs/heads/frozen")) []
    let p = project () |> fun p -> publish p 1L priorReview
    let integration = { integration with HardPrerequisites = set [ workId priorReview ] }
    let p = publish p 2L integration
    let followup = event "gate-resolved" (QueueProducerOrigin.IntegrationPolicyResolved(
                    "T", workId integration, "gate-receipt")) [ workId integration ]
    error (Rejection.InvalidState "producer lacks exact canonical event evidence") (projectEvent p followup)
    let i = p.Works.[workId integration]
    let held = { i with State = WorkState.GateException
                        Gate = GateState.ExternalEffectObservedPendingGate(2, "push-1", "sha") }
    let p = { p with Works = p.Works.Add(i.Id, held) }
    error (Rejection.InvalidState "producer lacks exact canonical event evidence") (projectEvent p followup)
    let authorized =
        { i with
            State = WorkState.Completed
            Gate = GateState.Clear
            IntegrationAuthorized = true
            Domain = DomainState.Integrated "sha" }
    let p = { p with Works = p.Works.Add(i.Id, authorized) }
    let p = applyEvent p.Version "policy-op-original" 3L followup p |> ok
    Assert.That(p.Works.[workId followup].QueueId, Is.EqualTo("planning"))
    Assert.That(p.Works.[workId followup].Kind, Is.EqualTo(WorkKind.Planning))
    let replay = applyEvent p.Version "policy-op-original" 4L followup p |> ok
    Assert.That(replay, Is.EqualTo(p))
    let changed = { followup with
                        SourceId = "different-caller-source-id"
                        Origin = QueueProducerOrigin.IntegrationPolicyResolved(
                            "T", workId integration, "another-caller-receipt") }
    Assert.That(workId changed, Is.EqualTo(workId followup),
                "Logical policy successor must not depend on mutable caller labels")
    error Rejection.OperationIdReused
        (applyEvent p.Version "policy-op-original" 4L changed p)
    error (Rejection.InvalidState "duplicate immutable work ID")
        (applyEvent p.Version "policy-op-second" 4L changed p)
    let anotherGeneration = { changed with StageGeneration = 2 }
    Assert.That(workId anotherGeneration, Is.EqualTo(workId followup))
    error (Rejection.InvalidState "producer lacks exact canonical event evidence")
        (applyEvent p.Version "policy-op-third" 4L anotherGeneration p)

[<Test>]
let producer_fixture_replay_tracks_full_event_payload_not_only_derived_stage () =
    let initial = project ()
    let sourceEvent = event "source-id-1" (QueueProducerOrigin.TaskRequested "T") []
    let published = applyEvent initial.Version "semantic-operation" 100L sourceEvent initial |> ok
    let replay = applyEvent published.Version "semantic-operation" 101L sourceEvent published |> ok
    Assert.That(replay, Is.EqualTo(published))
    error Rejection.OperationIdReused
        (applyEvent published.Version "semantic-operation" 101L
            { sourceEvent with SourceId = "changed-source" } published)
    error Rejection.OperationIdReused
        (applyEvent published.Version "semantic-operation" 101L
            { sourceEvent with Origin = QueueProducerOrigin.TaskRequested "Other" } published)
