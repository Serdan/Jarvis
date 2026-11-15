module ServerWorkQueueContractTests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts

let private success = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected contract rejection: %A" error

let private rejected (expected: Rejection) (result: Result<'T, Rejection>) =
    match result with
    | Error rejection -> Assert.That((rejection = expected), Is.True, sprintf "Expected %A, got %A" expected rejection)
    | Ok _ -> Assert.Fail(sprintf "Expected rejection %A" expected)

let private subscriptions =
    Map.ofList
        [ "Lead", Set.ofList [ "planning"; "role:Lead" ]
          "Worker", Set.singleton "implementation"
          "Flexible", Set.ofList [ "implementation"; "review" ]
          "Reviewer", Set.singleton "review"
          "Integrator", Set.singleton "integration" ]

let private empty () = emptyProject subscriptions

let private stage id (kind: WorkKind) queue timestamp deps (state: WorkState) : WorkStage =
    { Id = id
      ProjectId = "Jarvis"
      SubjectId = "task-" + id
      Kind = kind
      QueueId = queue
      StageGeneration = 1
      State = state
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.ofList deps
      WaivedDependencies = Map.empty
      AvailableAt = timestamp
      AvailableSequence = timestamp
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private implementation id timestamp deps =
    stage id WorkKind.Implementation "implementation" timestamp deps WorkState.Available

let private review id timestamp deps =
    stage id WorkKind.FrozenReview "review" timestamp deps WorkState.Available

let private integration id timestamp deps =
    stage id WorkKind.Integration "integration" timestamp deps WorkState.Available

let private launch id role : Launch =
    { Id = id
      ProjectId = "Jarvis"
      RoleId = role
      RoleVersion = 1
      AccountTokenId = "token:" + id
      State = LaunchState.ReservedUnregistered
      Lease = None
      Version = 0
      Credit = None
      PrimaryWorkId = None
      EverClaimed = false
      AncillaryMessages = Set.empty
      DispatchCount = 0 }

let private publish (p: Project) (work: WorkStage) = publishStage p.Version ("publish:" + work.Id) work p |> success
let private admit (p: Project) id role = admitProject p.Version ("admit:" + id) (launch id role) p |> success
let private submit (p: Project) id = dispatchOnce p.Version ("submit:" + id) id DispatchState.Submitted p |> success
let private initialize (p: Project) id =
    initializeAgent p.Version ("initialize:" + id) id ("provider-session:" + id) ("uuid:" + id) 0L p |> success

let private claim (p: Project) id = claimNext p.Version ("claim:" + id) id p |> success
let private activated id role (work: WorkStage) =
    empty () |> fun p -> publish p work |> fun p -> admit p id role |> fun p -> submit p id |> fun p -> initialize p id |> fun p -> claim p id

let private message id subject : SubjectMessage =
    { Id = id
      Subject = subject
      Body = "Message " + id
      State = MessageState.Pending
      HandlerGeneration = 0
      WorkId = None }

// Existing fixtures act as the current handler, whose receipt must pin the
// generation returned by the atomic claim. Race tests below pass old generations
// explicitly rather than silently refreshing them.
let private completePrimaryCurrent expected op launchId workId messageId replyId now (p: Project) =
    completeMessageWork expected op launchId workId messageId p.Messages.[messageId].HandlerGeneration replyId now p

let private completeAncillaryCurrent expected op launchId messageId replyId now (p: Project) =
    completeAncillaryMessage expected op launchId messageId p.Messages.[messageId].HandlerGeneration replyId now p

let private verifiedPush originalId sourceSha targetRef remoteCommit : VerifiedRemoteEffect =
    { OriginalPushOperationId = originalId
      AcceptedSourceSha = sourceSha
      TargetRef = targetRef
      RemoteCommitSha = remoteCommit
      AncestryVerified = true
      JobsReconciled = true }

let private sourceLineage =
    { RepositoryId = "Jarvis"
      TaskId = "task-A"
      TargetRef = "refs/heads/main"
      LineageId = "worker-source"
      PinnedBaseCommit = "base-A"
      ReachableRef = None
      ContinuationSha = None
      Generation = 1 }

let private sourceProof (claimed: WorkStage) : RemoteSourceSnapshot =
    let lineage = claimed.SourceLineage.Value
    { RepositoryId = lineage.RepositoryId
      TaskId = lineage.TaskId
      LineageId = lineage.LineageId
      StageId = claimed.Id
      StageGeneration = claimed.StageGeneration
      ClaimGeneration = claimed.ClaimGeneration
      LineageGeneration = lineage.Generation
      ExpectedRef = lineage.ReachableRef |> Option.defaultValue lineage.TargetRef
      ExpectedSha = lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit
      ContinuationRef = "refs/heads/worker-A"
      ContinuationSha = "accepted-A"
      ReachabilityVerified = true
      JobsReconciled = true }

let private waiver workId dependencies =
    { ReceiptId = "canonical-approval:1"
      WorkId = workId
      Dependencies = Set.ofList dependencies
      AccountApprovalVersion = 3
      ApprovedByRole = "Lead"
      CanonicallyVerified = true }

let private roleMessageWork id timestamp : WorkStage =
    { stage ("role-msg:" + id) (WorkKind.RoleMessage id) "role:Lead"
        timestamp [] WorkState.Available with
        SubjectId = "Lead" }

let private publishRoleMessage (p: Project) id timestamp =
    postMessage p.Version ("post:" + id) (message id (Subject.Role "Lead"))
        (Some(roleMessageWork id timestamp)) p |> success

[<Test>]
let case_01_cap_one_scheduler_needs_no_manager_token () =
    let account = emptyAccount 1
    let account = reserveGlobal account.Version "reserve:L1" "L1" account |> success
    rejected Rejection.NoCapacity (reserveGlobal account.Version "reserve:L2" "L2" account)
    let project = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> admit p "L1" "Worker"
    Assert.That(project.Launches.["L1"].Credit, Is.EqualTo(Some "A"))
    Assert.That(account.Tokens.Count, Is.EqualTo(1))

[<Test>]
let case_02_and_12_empty_or_incomplete_never_looks_like_safe_nowork () =
    let project = empty ()
    Assert.That(orderAvailable project |> success, Is.Empty)
    let partial = { project with QueueCovered = false }
    rejected (Rejection.IncompleteCoverage "queue high-water/index") (orderAvailable partial)
    let missing = publish project (implementation "A" 1L ["absent"])
    rejected (Rejection.IncompleteCoverage "missing prerequisite stage generation") (orderAvailable missing)

[<Test>]
let case_03_04_05_06_blockers_win_but_blocked_prerequisites_do_not_run () =
    let completed = { implementation "Z" 0L [] with State = WorkState.Completed }
    let project =
        empty ()
        |> fun p -> publish p completed
        |> fun p -> publish p (implementation "C" 1L [])
        |> fun p -> publish p (review "R" 4L [])
        |> fun p -> publish p (implementation "B" 6L ["R"])
        |> fun p -> publish p (implementation "A" 2L [])
    let ordered = orderAvailable project |> success |> List.map (fun x -> x.Id)
    Assert.That(ordered, Is.EqualTo(box [ "R"; "C"; "A" ]))
    let blockedR = { project.Works.["R"] with State = WorkState.Blocked; Dependencies = Set.singleton "Z" }
    let project = { project with Works = project.Works.Add("R", blockedR) }
    Assert.That(orderAvailable project |> success |> List.map (fun x -> x.Id), Is.EqualTo(box [ "C"; "A" ]))

[<Test>]
let case_07_09_fifo_key_is_stable_when_blocker_tier_changes () =
    let project =
        empty () |> fun p -> publish p (implementation "B" 4L []) |> fun p -> publish p (implementation "A" 2L []) |> fun p -> publish p (implementation "C" 6L ["A"])
    Assert.That(orderAvailable project |> success |> List.map (fun w -> w.Id), Is.EqualTo(box [ "A"; "B" ]))
    Assert.That(project.Works.["C"].State, Is.EqualTo(WorkState.Blocked))
    let withoutEdge =
        waiveHardPrerequisite project.Version "waive-C-A" "C" "A" (waiver "C" ["A"]) 10000L project |> success
    Assert.That(withoutEdge.Works.["A"].AvailableSequence, Is.EqualTo(2L))
    Assert.That(orderAvailable withoutEdge |> success |> List.map (fun w -> w.Id),
                Is.EqualTo(box [ "A"; "B"; "C" ]))
    let crossProjectKeys =
        [ "P2", 1L, 2L, "A"; "P1", 1L, 2L, "B" ] |> List.sort
    Assert.That(crossProjectKeys.Head, Is.EqualTo(("P1", 1L, 2L, "B")))

[<Test>]
let case_10_11_scc_cycle_and_self_loop_warn_without_promoting_blocked () =
    let a = { implementation "A" 1L ["B"] with State = WorkState.Blocked }
    let b = { implementation "B" 2L ["C"] with State = WorkState.Blocked }
    let c = { implementation "C" 3L ["A"] with State = WorkState.Blocked }
    let self = { implementation "Self" 4L ["Self"] with State = WorkState.Blocked }
    let project = empty () |> fun p -> publish p a |> fun p -> publish p b |> fun p -> publish p c |> fun p -> publish p self
    let warnings = circularHardDependencies project |> success
    Assert.That(warnings.Length, Is.EqualTo(2))
    Assert.That(warnings |> List.exists (fun w -> w.Members = [ "A"; "B"; "C" ]), Is.True)
    Assert.That(warnings |> List.exists (fun w -> w.Members = [ "Self" ] && w.WitnessEdges = [ "Self", "Self" ]), Is.True)
    Assert.That(orderAvailable project |> success, Is.Empty)
    let revised = { project with Works = project.Works.Add("C", { c with Dependencies = Set.empty }) }
    Assert.That(circularHardDependencies revised |> success |> List.length, Is.EqualTo(1))
    rejected (Rejection.IncompleteCoverage "dependency graph")
        (circularHardDependencies { project with GraphCovered = false })

[<Test>]
let case_13_14_account_capacity_and_resize_serialize_on_one_cas () =
    let initial = emptyAccount 2
    let a = reserveGlobal 0 "reserve:A" "A" initial |> success
    rejected Rejection.CasConflict (reserveGlobal 0 "reserve:B" "B" a)
    let b = reserveGlobal a.Version "reserve:B" "B" a |> success
    rejected Rejection.NoCapacity (reserveGlobal b.Version "reserve:C" "C" b)
    rejected Rejection.NoCapacity (resizeGlobal b.Version "shrink:1" 1 b)
    let expanded = resizeGlobal b.Version "grow:3" 3 b |> success
    Assert.That(expanded.MaxConcurrentAgents, Is.EqualTo(3))

[<Test>]
let case_15_16_17_18_one_first_connect_uuid_and_idempotent_registration () =
    let p = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> admit p "L" "Worker" |> fun p -> submit p "L"
    Assert.That(p.Launches.["L"].State, Is.EqualTo(LaunchState.SubmittedUnregistered DispatchState.Submitted))
    let first = initializeAgent p.Version "init-op" "L" "session-1" "server-uuid-1" 0L p |> success
    let replay = initializeAgent p.Version "init-op" "L" "session-1" "random-uuid-ignored" 0L first |> success
    Assert.That(replay, Is.EqualTo(first))
    Assert.That(first.Launches.["L"].State, Is.EqualTo(LaunchState.Registered("server-uuid-1", "session-1")))
    rejected Rejection.AlreadyRegistered
        (initializeAgent first.Version "init-other" "L" "session-2" "server-uuid-2" 0L first)
    rejected Rejection.OperationIdReused
        (initializeAgent first.Version "init-op" "L" "different-payload" "server-uuid-1" 0L first)
    rejected Rejection.CasConflict
        (initializeAgent p.Version "stale-op" "L" "session-2" "server-uuid-2" 0L first)

[<Test>]
let case_19_20_submit_is_one_shot_even_if_outcome_unknown () =
    let p = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> admit p "L" "Worker"
    let unknown = dispatchOnce p.Version "submit-op" "L" DispatchState.OutcomeUnknown p |> success
    Assert.That(unknown.Launches.["L"].DispatchCount, Is.EqualTo(1))
    let replay = dispatchOnce p.Version "submit-op" "L" DispatchState.OutcomeUnknown unknown |> success
    Assert.That(replay.Launches.["L"].DispatchCount, Is.EqualTo(1))
    rejected (Rejection.InvalidState "dispatch is one-shot and requires reserved launch")
        (dispatchOnce unknown.Version "new-submit" "L" DispatchState.Submitted unknown)

[<Test>]
let case_22_23_global_first_abort_only_tombstone_fences_late_admission () =
    let account = reserveGlobal 0 "token-op" "L" (emptyAccount 1) |> success
    let project = empty () |> fun p -> publish p (implementation "A" 1L [])
    let launch = launch "L" "Worker"
    let originalVersion = project.Version
    let aborted = abortUnboundAdmission originalVersion "abort-op" launch project |> success
    rejected Rejection.CasConflict (admitProject originalVersion "late-admit" launch aborted)
    rejected (Rejection.InvalidState "launch ID reused")
        (admitProject aborted.Version "admit-new-op" launch aborted)
    let released = releaseGlobal account.Version "release-L" "L" aborted account |> success
    Assert.That(released.Tokens.["L"], Is.True)
    Assert.That(releaseGlobal released.Version "release-L" "L" aborted released |> success, Is.EqualTo(released))
    rejected Rejection.MissingProof (releaseGlobal account.Version "early-release" "L" project account)

[<Test>]
let case_24_25_injective_credits_preserve_A_only_reservation () =
    let project = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> publish p (review "B" 2L [])
    let flexibleFirst = admit project "FlexibleLaunch" "Flexible"
    Assert.That(flexibleFirst.Launches.["FlexibleLaunch"].Credit, Is.EqualTo(Some "A"))
    let withReviewer = admit flexibleFirst "ReviewOnly" "Reviewer"
    Assert.That(withReviewer.Launches.["ReviewOnly"].Credit, Is.EqualTo(Some "B"))
    let withWorker = withReviewer
    rejected Rejection.NoCompatibleMatching
        (admitProject withWorker.Version "admit:Worker" (launch "Worker" "Worker") withWorker)
    let p =
        empty ()
        |> fun p -> publish p (implementation "A" 1L [])
        |> fun p -> publish p (review "B" 2L [])
        |> fun p -> admit p "FlexibleLaunch" "Flexible"
        |> fun p -> admit p "Worker" "Worker"
    Assert.That(p.Launches.["Worker"].Credit, Is.EqualTo(Some "A"))
    Assert.That(p.Launches.["FlexibleLaunch"].Credit, Is.EqualTo(Some "B"))
    let p = p |> fun p -> submit p "FlexibleLaunch" |> fun p -> initialize p "FlexibleLaunch"
    let claimed = claim p "FlexibleLaunch"
    Assert.That(claimed.Launches.["FlexibleLaunch"].PrimaryWorkId, Is.EqualTo(Some "B"))
    Assert.That(claimed.Launches.["Worker"].Credit, Is.EqualTo(Some "A"))

[<Test>]
let case_27_28_claim_nowork_and_one_primary_are_mutually_exclusive () =
    let registered = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> admit p "L" "Worker" |> fun p -> submit p "L" |> fun p -> initialize p "L"
    let claimed = claim registered "L"
    rejected Rejection.CasConflict (terminalizeNoClaim registered.Version "stale-no-work" "L" claimed)
    rejected (Rejection.InvalidState "primary claim already existed")
        (terminalizeNoClaim claimed.Version "no-work-after-claim" "L" claimed)
    let doneWork = completeOrdinary claimed.Version "completed" "L" "A" 10000L claimed |> success
    rejected (Rejection.InvalidState "requires registered launch with no prior primary claim")
        (claimNext doneWork.Version "second-claim" "L" doneWork)
    rejected Rejection.CasConflict (completeOrdinary claimed.Version "stale-complete" "L" "A" 10000L doneWork)

[<Test>]
let case_31_66_68_review_verdict_is_domain_final_without_releasing_claim () =
    let p =
        empty ()
        |> fun p -> publish p (implementation "B" 1L [])
        |> fun p -> publish p (review "R" 2L [])
        |> fun p -> admit p "L" "Reviewer"
        |> fun p -> submit p "L"
        |> fun p -> initialize p "L"
        |> fun p -> claim p "L"
    let decided = decideReview p.Version "verdict" "R" "accepted:sha" p |> success
    Assert.That(decided.Works.["R"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(decided.Works.["R"].Domain,
                Is.EqualTo(DomainState.FinalizedPendingTerminal "accepted:sha"))
    rejected Rejection.DomainAlreadyFinalized
        (setHardPrerequisite decided.Version "new-edge" "R" "B" 10000L decided)
    let terminal = completeOrdinary decided.Version "review-terminal" "L" "R" 10000L decided |> success
    Assert.That(terminal.Works.["R"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(terminal.ProducerCount, Is.EqualTo(1))
    Assert.That(decideReview decided.Version "verdict" "R" "accepted:sha" terminal |> success,
                Is.EqualTo(terminal))

[<Test>]
let case_34_44_50_51_dedicated_role_message_completion_is_atomic () =
    let account = reserveGlobal 0 "reserve:L" "L" (emptyAccount 1) |> success
    let p = empty () |> fun p -> publishRoleMessage p "M1" 1L |> fun p -> admit p "L" "Lead" |> fun p -> submit p "L" |> fun p -> initialize p "L" |> fun p -> claim p "L"
    let workId = "role-msg:M1"
    Assert.That(p.Messages.["M1"].State, Is.EqualTo(MessageState.ClaimedPrimary "L"))
    rejected (Rejection.InvalidState "primary claim already existed")
        (terminalizeNoClaim p.Version "naughty-no-work" "L" p)
    rejected Rejection.MissingProof (releaseGlobal account.Version "too-early" "L" p account)
    let handled = completePrimaryCurrent p.Version "reply-op" "L" workId "M1" "reply-1" 10000L p |> success
    Assert.That(handled.Messages.["M1"].State, Is.EqualTo(MessageState.Handled "reply-1"))
    Assert.That(handled.Works.[workId].State, Is.EqualTo(WorkState.Completed))
    Assert.That(handled.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:reply-op"))
    Assert.That(completePrimaryCurrent p.Version "reply-op" "L" workId "M1" "reply-1" 10000L handled |> success,
                Is.EqualTo(handled))
    let released = releaseGlobal account.Version "release:L" "L" handled account |> success
    Assert.That(released.Tokens.["L"], Is.True)

[<Test>]
let case_52_53_dedicated_role_message_claims_exactly_one_message () =
    let p =
        empty ()
        |> fun p -> publishRoleMessage p "M1" 1L
        |> fun p -> publishRoleMessage p "M2" 2L
        |> fun p -> admit p "L1" "Lead"
        |> fun p -> admit p "L2" "Lead"
        |> fun p -> submit p "L1"
        |> fun p -> initialize p "L1"
        |> fun p -> claim p "L1"
    Assert.That(p.Launches.["L1"].PrimaryWorkId, Is.EqualTo(Some "role-msg:M1"))
    Assert.That(p.Messages.["M2"].State, Is.EqualTo(MessageState.Pending))
    let handled = completePrimaryCurrent p.Version "handled:M1" "L1" "role-msg:M1" "M1" "r1" 10000L p |> success
    Assert.That(handled.Works.["role-msg:M2"].State, Is.EqualTo(WorkState.Available))
    Assert.That(handled.Launches.["L2"].Credit, Is.EqualTo(Some "role-msg:M2"))

[<Test>]
let case_36_54_64_ancillary_handler_never_completes_own_primary_claim () =
    let p =
        empty ()
        |> fun p -> publish p (stage "LeadPlan" WorkKind.Planning "planning" 0L [] WorkState.Available)
        |> fun p -> publishRoleMessage p "M" 1L
        |> fun p -> admit p "Active" "Lead"
        |> fun p -> admit p "Reserved" "Lead"
        |> fun p -> submit p "Active"
        |> fun p -> initialize p "Active"
        |> fun p -> claim p "Active"
    Assert.That(p.Launches.["Active"].PrimaryWorkId, Is.EqualTo(Some "LeadPlan"))
    rejected Rejection.NoCompatibleMatching
        (claimAncillaryMessage p.Version "steal" "Active" "M" p)
    let withMore = publish p (stage "Other" WorkKind.Planning "planning" 2L [] WorkState.Available)
    let ancillary = claimAncillaryMessage withMore.Version "claim-ancillary" "Active" "M" withMore |> success
    Assert.That(ancillary.Launches.["Reserved"].Credit, Is.EqualTo(Some "Other"))
    let acknowledged =
        completeAncillaryCurrent ancillary.Version "ack-ancillary" "Active" "M" "reply" 10000L ancillary |> success
    Assert.That(acknowledged.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(acknowledged.Works.["LeadPlan"].State, Is.EqualTo(WorkState.Claimed "Active"))
    Assert.That(acknowledged.Launches.["Active"].State,
                Is.EqualTo(LaunchState.Registered("uuid:Active", "provider-session:Active")))

[<Test>]
let case_38_55_56_task_messages_are_passive_when_task_has_no_eligible_actor () =
    let blocked = { implementation "T" 1L ["T"] with State = WorkState.Blocked }
    let p = empty () |> fun p -> publish p blocked
    let m = message "M" (Subject.Task "task-T")
    let p = postMessage p.Version "post-task" m None p |> success
    Assert.That(p.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
    Assert.That(p.Messages.["M"].WorkId.IsNone, Is.True)
    Assert.That(orderAvailable p |> success, Is.Empty)
    rejected Rejection.NoCompatibleMatching (admitProject p.Version "admit:new" (launch "New" "Lead") p)

[<Test>]
let case_57_58_59_session_keys_are_per_provider_execution () =
    let keys = Map.empty<string, string>
    let keys, k1 = sessionKeyForExecution "physical-exec-1" "opaque-entropy-one" keys |> success
    let keys, again = sessionKeyForExecution "physical-exec-1" "ignored-on-retry" keys |> success
    let keys, k2 = sessionKeyForExecution "physical-exec-2" "opaque-entropy-two" keys |> success
    Assert.That(k1, Is.EqualTo(again))
    Assert.That(k1, Is.Not.EqualTo(k2))
    rejected (Rejection.InvalidState "copied provider-session key")
        (sessionKeyForExecution "physical-exec-3" "opaque-entropy-one" keys)

[<Test>]
let case_60_61_dependency_gate_and_completion_commit_are_mutually_exclusive () =
    let p =
        empty ()
        |> fun p -> publish p (implementation "B" 3L [])
        |> fun p -> publish p (implementation "A" 2L [])
        |> fun p -> admit p "L" "Worker"
        |> fun p -> submit p "L"
        |> fun p -> initialize p "L"
    // B is older; use a separate fixture where only A is Available.
    let p = claim p "L"
    let gated = setHardPrerequisite p.Version "edge" "A" "B" 10000L p |> success
    Assert.That(gated.Works.["A"].Gate, Is.EqualTo(GateState.DependencyGatePending gated.GraphVersion))
    rejected Rejection.CasConflict (completeOrdinary p.Version "race-complete" "L" "A" 10000L gated)
    rejected Rejection.DependencyGatePending
        (completeOrdinary gated.Version "try-complete" "L" "A" 10000L gated)
    let completed = completeOrdinary p.Version "complete-first" "L" "A" 10000L p |> success
    rejected Rejection.StageAlreadyTerminal
        (setHardPrerequisite completed.Version "too-late" "A" "B" 10000L completed)

[<Test>]
let case_62_63_safe_dependency_gate_resolution_blocks_or_requeues_once () =
    let p = activated "L" "Worker" { implementation "A" 2L [] with SourceLineage = Some sourceLineage }
    let p = publish p (implementation "B" 1L [])
    let gated = setHardPrerequisite p.Version "edge" "A" "B" 10000L p |> success
    rejected Rejection.UnsafeExternalEffect
        (resolveDependencyGate gated.Version "unsafe" "L" "A" (SourceHandoffProof.Unsafe "unreconciled worktree") 10L gated)
    let proof = SourceHandoffProof.RemoteSnapshot(sourceProof gated.Works.["A"])
    let blocked = resolveDependencyGate gated.Version "safe" "L" "A" proof 10L gated |> success
    Assert.That(blocked.Works.["A"].SourceLineage.Value.ReachableRef, Is.EqualTo(Some "refs/heads/worker-A"))
    Assert.That(blocked.Works.["A"].SourceLineage.Value.ContinuationSha, Is.EqualTo(Some "accepted-A"))
    Assert.That(blocked.Works.["A"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(blocked.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:safe"))
    let satisfied = { gated with Works = gated.Works.Add("B", { gated.Works.["B"] with State = WorkState.Completed }) }
    let available = resolveDependencyGate satisfied.Version "safe-wake" "L" "A" proof 11L satisfied |> success
    Assert.That(available.Works.["A"].State, Is.EqualTo(WorkState.Available))
    Assert.That(available.Works.["A"].AvailableAt, Is.EqualTo(11L))
    Assert.That(available.Works.["A"].AvailableSequence, Is.GreaterThan(2L))

[<Test>]
let case_66_67_73_verdict_and_new_hard_edge_are_version_fenced () =
    let p = activated "R" "Reviewer" (review "Frozen" 1L [])
    let p = publish p (implementation "B" 2L [])
    let gated = setHardPrerequisite p.Version "edge-first" "Frozen" "B" 10000L p |> success
    rejected Rejection.DependencyGatePending
        (decideReview gated.Version "late-verdict" "Frozen" "accepted" gated)
    rejected Rejection.CasConflict
        (decideReview p.Version "losing-verdict" "Frozen" "accepted" gated)
    let decided = decideReview p.Version "verdict-first" "Frozen" "accepted" p |> success
    rejected Rejection.DomainAlreadyFinalized
        (setHardPrerequisite decided.Version "losing-edge" "Frozen" "B" 10000L decided)

[<Test>]
let case_69_70_integration_final_vs_gate () =
    let p = activated "I" "Integrator" (integration "Int" 1L [])
    let p = publish p (implementation "B" 2L [])
    rejected Rejection.MissingProof
        (finalizeIntegration p.Version "early-without-remote" "Int" "canonical-result" p)
    let journaled = startOriginalPush p.Version "journal-before-final" "I" "Int" "push-original" "source-int-sha" "refs/heads/main" p |> success
    let observed = recordUngatedPushEffect journaled.Version "remote-proof" "Int" (verifiedPush "push-original" "source-int-sha" "refs/heads/main" "sha:remote") journaled |> success
    let complete = finalizeIntegration observed.Version "complete-integration" "Int" "canonical-result" observed |> success
    rejected Rejection.DomainAlreadyFinalized
        (setHardPrerequisite complete.Version "edge-late" "Int" "B" 10000L complete)
    let gated = setHardPrerequisite p.Version "edge-first" "Int" "B" 10000L p |> success
    rejected Rejection.DependencyGatePending
        (finalizeIntegration gated.Version "late-final" "Int" "sha:remote" gated)
    Assert.That(gated.Works.["Int"].IntegrationAuthorized, Is.False)

[<Test>]
let case_74_75_78_79_external_push_is_fact_not_authorization () =
    let account = reserveGlobal 0 "reserve:I" "I" (emptyAccount 1) |> success
    let p = activated "I" "Integrator" (integration "W" 1L [])
    let prerequisite = (implementation "B" 2L [])
    let p = publish p prerequisite
    let intent = startOriginalPush p.Version "journal" "I" "W" "original-push" "accepted-source" "refs/heads/main" p |> success
    let gated = setHardPrerequisite intent.Version "lead-gate" "W" "B" 10000L intent |> success
    Assert.That(gated.Works.["W"].Gate,
                Is.EqualTo(GateState.IntegrationGateHold(gated.GraphVersion, "original-push")))
    rejected Rejection.UnsafeExternalEffect
        (resolveDependencyGate gated.Version "unsafe-resolve" "I" "W" (SourceHandoffProof.ReadOnly "all-jobs-reconciled") 10L gated)
    let observed =
        reconcileExternalEffect gated.Version "reconcile" "I" "W"
            (verifiedPush "original-push" "accepted-source" "refs/heads/main" "remote-commit") 10000L gated |> success
    Assert.That(observed.Works.["W"].State, Is.EqualTo(WorkState.GateException))
    Assert.That(observed.Works.["W"].IntegrationAuthorized, Is.False)
    Assert.That(observed.ProducerCount, Is.EqualTo(0))
    rejected Rejection.MissingProof
        (resolveIntegrationGateException observed.Version "premature" "W" None 10000L observed)
    let released = releaseGlobal account.Version "release-I" "I" observed account |> success
    Assert.That(released.Tokens.["I"], Is.True)
    let authorized =
        resolveIntegrationGateException observed.Version "waive" "W" (Some { waiver "W" ["B"] with ReceiptId = "lead-waiver:v1" }) 10000L observed |> success
    Assert.That(authorized.Works.["W"].Domain, Is.EqualTo(DomainState.Integrated "remote-commit"))
    Assert.That(authorized.Works.["W"].IntegrationAuthorized, Is.True)
    Assert.That(authorized.ProducerCount, Is.EqualTo(1))
    Assert.That(authorized.Works.["W"].WaivedDependencies.["B"], Is.EqualTo("lead-waiver:v1"))
    Assert.That(resolveIntegrationGateException observed.Version "waive" "W" (Some { waiver "W" ["B"] with ReceiptId = "lead-waiver:v1" }) 10000L authorized |> success,
                Is.EqualTo(authorized))

[<Test>]
let case_76_77_80_unknown_external_push_never_releases_token_or_downstream () =
    let account = reserveGlobal 0 "reserve:I" "I" (emptyAccount 1) |> success
    let p = activated "I" "Integrator" (integration "W" 1L [])
    let p = publish p (implementation "B" 2L [])
    let intent = startOriginalPush p.Version "journal" "I" "W" "push" "accepted-source" "refs/heads/main" p |> success
    let gated = setHardPrerequisite intent.Version "gate" "W" "B" 10000L intent |> success
    rejected Rejection.UnsafeExternalEffect
        (reconcileExternalEffect gated.Version "uncertain" "I" "W" { (verifiedPush "push" "accepted-source" "refs/heads/main" "negative-read") with AncestryVerified = false } 10000L gated)
    rejected Rejection.MissingProof (releaseGlobal account.Version "release" "I" gated account)
    Assert.That(gated.Works.["W"].IntegrationAuthorized, Is.False)
    rejected Rejection.CasConflict
        (reconcileExternalEffect intent.Version "stale-reconcile" "I" "W" (verifiedPush "push" "accepted-source" "refs/heads/main" "commit") 10000L gated)

[<Test>]
let case_45_46_48_49_migration_epoch_requires_every_project_activated () =
    let account = emptyAccount 1
    let project = empty ()
    Assert.That(canDispatch account [ project ], Is.True)
    Assert.That(canDispatch { account with Migration = MigrationEpoch.Preparing } [ project ], Is.False)
    Assert.That(canDispatch account [ project; { project with Migration = MigrationEpoch.Validated } ], Is.False)
    Assert.That(canDispatch account [ { project with SourceCovered = false } ], Is.False)

let private git folder args =
    let info = ProcessStartInfo("git")
    info.WorkingDirectory <- folder
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in args do info.ArgumentList.Add argument
    use gitProcess = Process.Start info
    let stdout = gitProcess.StandardOutput.ReadToEnd()
    let stderr = gitProcess.StandardError.ReadToEnd()
    if not (gitProcess.WaitForExit(15000)) then
        gitProcess.Kill(true)
        failwith "Git fixture timed out"
    gitProcess.ExitCode, stdout + stderr

let private mustGit folder args =
    let exit, output = git folder args
    if exit <> 0 then failwithf "git %A failed: %s" args output

// Real disposable Git repos: two clients with the same verified orphan-ledger parent.
// The loser must fetch/reread and retry a new mutation; no force push or production refs.
[<Test>]
let two_client_git_cas_rejects_stale_snapshot_and_replays_original_receipt () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-wq-cas-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        mustGit root [ "init"; "--bare"; "--initial-branch=ledger"; "project.git" ]
        mustGit root [ "clone"; "project.git"; "a" ]
        let a = Path.Combine(root, "a")
        mustGit a [ "config"; "user.email"; "wq-fixture@example.invalid" ]
        mustGit a [ "config"; "user.name"; "Contract Fixture" ]
        File.WriteAllText(Path.Combine(a, "receipt.txt"), "head=0\n")
        mustGit a [ "add"; "receipt.txt" ]
        mustGit a [ "commit"; "-m"; "seed" ]
        mustGit a [ "push"; "origin"; "HEAD:ledger" ]
        mustGit root [ "clone"; "--branch"; "ledger"; "project.git"; "b" ]
        let b = Path.Combine(root, "b")
        mustGit b [ "config"; "user.email"; "wq-fixture@example.invalid" ]
        mustGit b [ "config"; "user.name"; "Contract Fixture" ]
        File.WriteAllText(Path.Combine(a, "receipt.txt"), "head=1\nop=winner\n")
        mustGit a [ "add"; "receipt.txt" ]
        mustGit a [ "commit"; "-m"; "client A CAS" ]
        File.WriteAllText(Path.Combine(b, "receipt.txt"), "head=1\nop=stale\n")
        mustGit b [ "add"; "receipt.txt" ]
        mustGit b [ "commit"; "-m"; "client B competing CAS" ]
        mustGit a [ "push"; "origin"; "HEAD:ledger" ]
        let loserCode, _ = git b [ "push"; "origin"; "HEAD:ledger" ]
        Assert.That(loserCode, Is.Not.EqualTo(0))
        mustGit b [ "fetch"; "origin"; "ledger" ]
        let _, remote = git b [ "show"; "origin/ledger:receipt.txt" ]
        Assert.That(remote, Does.Contain("op=winner"))
        Assert.That(remote, Does.Not.Contain("op=stale"))
    finally
        if Directory.Exists root then Directory.Delete(root, true)


[<Test>]
let case_29_30_32_one_actionable_writer_per_explicit_source_lineage () =
    let lineage =
        { RepositoryId = "Jarvis"
          TaskId = "task-source"
          TargetRef = "refs/heads/main"
          LineageId = "original"
          PinnedBaseCommit = "base-sha"
          ReachableRef = None
          ContinuationSha = None
          Generation = 1 }
    let a = { implementation "SourceA" 1L [] with SourceLineage = Some lineage }
    let b = { implementation "SourceB" 2L [] with SourceLineage = Some { lineage with Generation = 2 } }
    let current = empty () |> fun p -> publish p a
    rejected (Rejection.InvalidState "concurrent source writer lineage")
        (publishStage current.Version "second-writer" b current)
    let terminal =
        { current with
            Works = current.Works.Add("SourceA", { a with State = WorkState.Completed }) }
    let next = publishStage terminal.Version "second-writer" b terminal |> success
    Assert.That(next.Works.ContainsKey "SourceB", Is.True)

[<Test>]
let case_31_decided_review_cannot_be_skipped_by_completing_work () =
    let claimed = activated "R" "Reviewer" (review "Frozen" 1L [])
    rejected (Rejection.InvalidState "review/integration requires immutable final domain receipt")
        (completeOrdinary claimed.Version "skip-verdict" "R" "Frozen" 10000L claimed)
    Assert.That(claimed.Works.["Frozen"].State, Is.EqualTo(WorkState.Claimed "R"))
    Assert.That(claimed.ProducerCount, Is.Zero)

[<Test>]
let case_37_54_unfinished_ancillary_handler_requeues_on_primary_terminal () =
    let planning = stage "Plan" WorkKind.Planning "planning" 1L [] WorkState.Available
    let p =
        empty ()
        |> fun p -> publish p planning
        |> fun p -> publishRoleMessage p "M" 2L
        |> fun p -> admit p "L" "Lead"
        |> fun p -> submit p "L"
        |> fun p -> initialize p "L"
        |> fun p -> claim p "L"
    Assert.That(p.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "Plan"))
    let handling = claimAncillaryMessage p.Version "take-M" "L" "M" p |> success
    Assert.That(handling.Messages.["M"].State, Is.EqualTo(MessageState.ClaimedAncillary "L"))
    let terminal = completeOrdinary handling.Version "finish-plan" "L" "Plan" 10000L handling |> success
    Assert.That(terminal.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
    Assert.That(terminal.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Available))
    Assert.That(terminal.Works.["role-msg:M"].AvailableSequence, Is.GreaterThan(2L))
    Assert.That(terminal.Launches.["L"].AncillaryMessages, Is.Empty)
    Assert.That(terminal.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:finish-plan"))

[<Test>]
let case_70_74_remote_source_target_proof_must_match_journal () =
    let p = activated "I" "Integrator" (integration "W" 1L [])
    let journaled =
        startOriginalPush p.Version "push-intent" "I" "W"
            "original-op" "accepted-source-sha" "refs/heads/main" p |> success
    rejected Rejection.MissingProof
        (recordUngatedPushEffect journaled.Version "wrong-source" "W"
            (verifiedPush "original-op" "wrong-sha" "refs/heads/main" "remote-sha") journaled)
    rejected Rejection.MissingProof
        (recordUngatedPushEffect journaled.Version "wrong-target" "W"
            (verifiedPush "original-op" "accepted-source-sha" "refs/heads/other" "remote-sha") journaled)
    rejected Rejection.DependencyGatePending
        (recordUngatedPushEffect journaled.Version "unverified" "W"
            { (verifiedPush "original-op" "accepted-source-sha" "refs/heads/main" "remote-sha") with
                AncestryVerified = false } journaled)
    let observed =
        recordUngatedPushEffect journaled.Version "verified" "W"
            (verifiedPush "original-op" "accepted-source-sha" "refs/heads/main" "remote-sha") journaled |> success
    Assert.That(observed.Works.["W"].Push,
                Is.EqualTo(Some
                    { OperationId = "original-op"
                      AcceptedSourceSha = "accepted-source-sha"
                      TargetRef = "refs/heads/main"
                      Outcome = PushOutcome.Observed "remote-sha" }))

[<Test>]
let case_71_76_definitive_no_effect_requires_terminal_job_proof () =
    let p = activated "I" "Integrator" (integration "W" 1L [])
    let p = publish p (implementation "B" 2L [])
    let intent =
        startOriginalPush p.Version "push-intent" "I" "W"
            "original-op" "accepted-source" "refs/heads/main" p |> success
    let held = setHardPrerequisite intent.Version "new-edge" "W" "B" 10000L intent |> success
    let negativeOnly =
        { OriginalPushOperationId = "original-op"
          DispatchedOperationTerminal = false
          JobsReconciled = true }
    rejected Rejection.UnsafeExternalEffect
        (recordDefinitiveNoEffect held.Version "negative-read" "W" negativeOnly held)
    rejected Rejection.UnsafeExternalEffect
        (resolveDependencyGate held.Version "unsafe-gate" "I" "W"
            (SourceHandoffProof.ReadOnly "source-safe") 4L held)
    let settled =
        recordDefinitiveNoEffect held.Version "dispatch-terminal" "W"
            { negativeOnly with DispatchedOperationTerminal = true } held |> success
    let blocked =
        resolveDependencyGate settled.Version "safe-gate" "I" "W"
            (SourceHandoffProof.ReadOnly "source-safe") 5L settled |> success
    Assert.That(blocked.Works.["W"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(blocked.Works.["W"].IntegrationAuthorized, Is.False)

[<Test>]
let case_78_79_satisfied_prerequisite_resolves_observed_hold_without_waiver () =
    let p = activated "I" "Integrator" (integration "W" 1L [])
    let p = publish p (implementation "B" 2L [])
    let intent =
        startOriginalPush p.Version "push-intent" "I" "W"
            "original-op" "accepted-source" "refs/heads/main" p |> success
    let gated = setHardPrerequisite intent.Version "new-edge" "W" "B" 10000L intent |> success
    let observed =
        reconcileExternalEffect gated.Version "original-effect" "I" "W"
            (verifiedPush "original-op" "accepted-source" "refs/heads/main" "remote-sha") 10000L gated |> success
    let satisfied =
        { observed with
            Works = observed.Works.Add("B", { observed.Works.["B"] with State = WorkState.Completed }) }
    let released =
        resolveIntegrationGateException satisfied.Version "verified-dependency" "W" None 10000L satisfied |> success
    Assert.That(released.Works.["W"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(released.Works.["W"].WaivedDependencies.IsEmpty, Is.True)
    Assert.That(released.ProducerCount, Is.EqualTo(1))

[<Test>]
let case_43_global_token_needs_matching_project_launch_terminal_proof () =
    let account = reserveGlobal 0 "reserve:L" "L" (emptyAccount 1) |> success
    let a = empty () |> fun p -> publish p (implementation "A" 1L [])
    let admitted = admit a "L" "Worker"
    let otherProject = { empty () with Version = admitted.Version }
    rejected Rejection.MissingProof
        (releaseGlobal account.Version "premature" "L" admitted account)
    rejected Rejection.MissingProof
        (releaseGlobal account.Version "cross-project" "L" otherProject account)


[<Test>]
let case_40_41_stalled_no_claim_recovery_fences_heartbeat_and_claim () =
    let account = reserveGlobal 0 "reserve:L" "L" (emptyAccount 1) |> success
    let connected = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> admit p "L" "Worker" |> fun p -> submit p "L" |> fun p -> initialize p "L"
    let liveLease = connected.Launches.["L"].Lease.Value
    Assert.That(liveLease.Generation, Is.EqualTo(1))
    rejected (Rejection.InvalidState "lease is still live or already fenced")
        (markStalled connected.Version "too-soon" "L" liveLease.Generation 9L 10L connected)
    let stopped = markStalled connected.Version "stalled" "L" liveLease.Generation 11L 10L connected |> success
    let stoppedGeneration = stopped.Launches.["L"].Lease.Value.Generation
    rejected Rejection.CasConflict
        (heartbeat connected.Version "racing-heartbeat" "L" liveLease.Generation 9L stopped)
    rejected (Rejection.InvalidState "heartbeat cannot restore stalled or terminal launch")
        (heartbeat stopped.Version "late-heartbeat" "L" stoppedGeneration 12L stopped)
    rejected (Rejection.InvalidState "requires registered launch with no prior primary claim")
        (claimNext stopped.Version "late-claim" "L" stopped)
    rejected Rejection.UnsafeExternalEffect
        (recoverRegisteredNoClaim stopped.Version "early-recovery" "L" stoppedGeneration 20L 30L true stopped)
    rejected Rejection.UnsafeExternalEffect
        (recoverRegisteredNoClaim stopped.Version "unknown-journal" "L" stoppedGeneration 31L 30L false stopped)
    let reclaimed =
        recoverRegisteredNoClaim stopped.Version "safe-recovery" "L" stoppedGeneration 31L 30L true stopped |> success
    Assert.That(reclaimed.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "stalled-no-claim:safe-recovery"))
    Assert.That(reclaimed.Works.["A"].State, Is.EqualTo(WorkState.Available))
    rejected Rejection.CasConflict (claimNext stopped.Version "stale-claim" "L" reclaimed)
    let released = releaseGlobal account.Version "release:L" "L" reclaimed account |> success
    Assert.That(released.Tokens.["L"], Is.True)

[<Test>]
let case_40_registered_heartbeat_before_stall_wins_cas () =
    let connected = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> admit p "L" "Worker" |> fun p -> submit p "L" |> fun p -> initialize p "L"
    let refreshed = heartbeat connected.Version "heartbeat" "L" 1 9L connected |> success
    rejected Rejection.CasConflict (markStalled connected.Version "stale-stop" "L" 1 12L 10L refreshed)
    rejected (Rejection.InvalidState "lease is still live or already fenced")
        (markStalled refreshed.Version "new-too-soon" "L" 2 12L 10L refreshed)
    let stopped = markStalled refreshed.Version "new-stop" "L" 2 19L 10L refreshed |> success
    Assert.That(stopped.Launches.["L"].Lease.Value.StopRequested, Is.True)

[<Test>]
let case_21_unregistered_dispatch_recovery_fences_late_initialize () =
    let account = reserveGlobal 0 "reserve:L" "L" (emptyAccount 1) |> success
    let reserved = empty () |> fun p -> publish p (implementation "A" 1L []) |> fun p -> admit p "L" "Worker"
    let submitted = dispatchOnce reserved.Version "uncertain-dispatch" "L" DispatchState.OutcomeUnknown reserved |> success
    rejected Rejection.UnsafeExternalEffect
        (finishUnregistered submitted.Version "before-deadline" "L" true false submitted)
    rejected Rejection.UnsafeExternalEffect
        (finishUnregistered submitted.Version "unknown-journal" "L" false true submitted)
    let reclaimed = finishUnregistered submitted.Version "fenced-unregistered" "L" true true submitted |> success
    Assert.That(reclaimed.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "unregistered:fenced-unregistered"))
    rejected Rejection.Terminal
        (initializeAgent reclaimed.Version "late-first-connect" "L" "new-provider" "uuid-new" 99L reclaimed)
    let released = releaseGlobal account.Version "release:L" "L" reclaimed account |> success
    Assert.That(released.Tokens.["L"], Is.True)


// Review #1 regressions: all four P1 findings are exercised against the
// pure CAS contract, including the stale-client and original-receipt paths.
[<Test>]
let wq01_blocked_prerequisite_completion_wakes_once_with_new_fifo_sequence () =
    let initial =
        empty ()
        |> fun p -> publish p (implementation "B" 1L [])
        |> fun p -> publish p (implementation "A" 2L [])
    let blocked = setHardPrerequisite initial.Version "edge:A-B" "A" "B" 10000L initial |> success
    Assert.That(blocked.Works.["A"].State, Is.EqualTo(WorkState.Blocked))
    let claimed =
        blocked |> fun p -> admit p "LB" "Worker"
                |> fun p -> submit p "LB"
                |> fun p -> initialize p "LB"
                |> fun p -> claim p "LB"
    Assert.That(claimed.Launches.["LB"].PrimaryWorkId, Is.EqualTo(Some "B"))
    let woken = completeOrdinary claimed.Version "finish:B" "LB" "B" 10000L claimed |> success
    let a = woken.Works.["A"]
    Assert.That(a.State, Is.EqualTo(WorkState.Available))
    Assert.That(a.AvailableSequence, Is.GreaterThan(2L))
    Assert.That(a.AvailableSequence, Is.EqualTo(woken.NextAvailability))
    Assert.That(a.AvailableAt, Is.EqualTo(10000L))
    let replay = completeOrdinary claimed.Version "finish:B" "LB" "B" 10000L woken |> success
    Assert.That(replay, Is.EqualTo(woken))
    rejected Rejection.CasConflict (completeOrdinary claimed.Version "stale:B" "LB" "B" 10000L woken)
    let initiallyBlocked =
        empty ()
        |> fun p -> publish p (implementation "B" 1L [])
        |> fun p -> publish p { implementation "A" 2L ["B"] with State = WorkState.Blocked }
        |> fun p -> admit p "LB" "Worker"
        |> fun p -> submit p "LB"
        |> fun p -> initialize p "LB"
        |> fun p -> claim p "LB"
    let initialWake = completeOrdinary initiallyBlocked.Version "finish:initial-B" "LB" "B" 10000L initiallyBlocked |> success
    Assert.That(initialWake.Works.["A"].State, Is.EqualTo(WorkState.Available))

[<Test>]
let wq01_cancelled_prerequisite_requires_scope_verified_waiver_and_cycles_remain_blocked () =
    let p =
        empty ()
        |> fun p -> publish p (implementation "B" 1L [])
        |> fun p -> publish p { implementation "A" 2L ["B"] with State = WorkState.Blocked }
    let cancelled = cancelUnclaimedStage p.Version "cancel:B" "B" "no longer applicable" 10000L p |> success
    Assert.That(cancelled.Works.["A"].State, Is.EqualTo(WorkState.Blocked))
    rejected Rejection.MissingProof
        (waiveHardPrerequisite cancelled.Version "bad-waiver" "A" "B"
            { waiver "A" ["B"] with CanonicallyVerified = false } 10000L cancelled)
    rejected Rejection.MissingProof
        (waiveHardPrerequisite cancelled.Version "wrong-scope" "A" "B"
            (waiver "unrelated" ["B"]) 10000L cancelled)
    let unblocked = waiveHardPrerequisite cancelled.Version "valid-waiver" "A" "B" (waiver "A" ["B"]) 10000L cancelled |> success
    Assert.That(unblocked.Works.["A"].State, Is.EqualTo(WorkState.Available))
    Assert.That(waiveHardPrerequisite cancelled.Version "valid-waiver" "A" "B" (waiver "A" ["B"]) 10000L unblocked |> success,
                Is.EqualTo(unblocked))
    let cycle =
        empty ()
        |> fun p -> publish p { implementation "X" 1L ["Y"] with State = WorkState.Blocked }
        |> fun p -> publish p { implementation "Y" 2L ["X"] with State = WorkState.Blocked }
    let unresolved = waiveHardPrerequisite cycle.Version "waive:X" "X" "Y"
                         { waiver "X" ["Y"] with CanonicallyVerified = false } 10000L cycle
    rejected Rejection.MissingProof unresolved
    Assert.That(orderAvailable cycle |> success, Is.Empty)

[<Test>]
let wq01_eligibility_edit_invalidates_each_preclaim_launch_phase_until_terminal_proof () =
    for phase in 0..2 do
        let account = reserveGlobal 0 ("account-" + string phase) "L" (emptyAccount 1) |> success
        let original =
            empty ()
            |> fun p -> publish p (implementation "A" 1L [])
            |> fun p -> publish p { implementation "B" 2L ["B"] with State = WorkState.Blocked }
            |> fun p -> admit p "L" "Worker"
        let original =
            if phase = 0 then original
            elif phase = 1 then submit original "L"
            else initialize (submit original "L") "L"
        let oldState = original.Launches.["L"].State
        let invalidated = setHardPrerequisite original.Version ("hard-edge-" + string phase) "A" "B" 10000L original |> success
        Assert.That(invalidated.Launches.["L"].State,
                    Is.EqualTo(LaunchState.DemandInvalidatedPendingTerminal oldState))
        Assert.That(invalidated.Launches.["L"].Credit, Is.EqualTo(None))
        rejected Rejection.MissingProof (releaseGlobal account.Version ("early-" + string phase) "L" invalidated account)
        rejected Rejection.CasConflict (claimNext original.Version ("stale-claim-" + string phase) "L" invalidated)
        rejected (Rejection.InvalidState "requires registered launch with no prior primary claim")
            (claimNext invalidated.Version ("fenced-claim-" + string phase) "L" invalidated)
        let noEvidence = { DispatchJournalSettled = false; SessionTerminalVerified = false }
        if phase > 0 then
            rejected Rejection.UnsafeExternalEffect
                (finishInvalidatedLaunch invalidated.Version ("early-terminal-" + string phase) "L" noEvidence invalidated)
        let journalEvidence = { noEvidence with DispatchJournalSettled = true }
        if phase = 2 then
            rejected Rejection.UnsafeExternalEffect
                (finishInvalidatedLaunch invalidated.Version "registered-without-session" "L" journalEvidence invalidated)
        let fullySettled = { DispatchJournalSettled = true; SessionTerminalVerified = true }
        let terminal = finishInvalidatedLaunch invalidated.Version ("invalidated-terminal-" + string phase) "L" fullySettled invalidated |> success
        let released = releaseGlobal account.Version ("release-" + string phase) "L" terminal account |> success
        Assert.That(released.Tokens.["L"], Is.True)
        Assert.That(finishInvalidatedLaunch invalidated.Version ("invalidated-terminal-" + string phase) "L" fullySettled terminal |> success,
                    Is.EqualTo(terminal))

[<Test>]
let wq01_credit_invalidation_maximum_rematch_is_injective_and_admission_is_fenced () =
    let start =
        empty ()
        |> fun p -> publish p (implementation "A" 1L [])
        |> fun p -> publish p (review "R" 2L [])
        |> fun p -> publish p { implementation "B" 3L ["B"] with State = WorkState.Blocked }
        |> fun p -> admit p "FlexibleLaunch" "Flexible"
        |> fun p -> admit p "WorkerLaunch" "Worker"
    let changed = setHardPrerequisite start.Version "block-A" "A" "B" 10000L start |> success
    Assert.That(changed.Launches.["FlexibleLaunch"].Credit, Is.EqualTo(Some "R"))
    Assert.That(changed.Launches.["WorkerLaunch"].State,
                Is.EqualTo(LaunchState.DemandInvalidatedPendingTerminal LaunchState.ReservedUnregistered))
    Assert.That(changed.Launches.["WorkerLaunch"].Credit, Is.EqualTo(None))
    rejected Rejection.CasConflict
        (admitProject start.Version "competing-admission" (launch "Competing" "Flexible") changed)
    rejected Rejection.NoCompatibleMatching
        (admitProject changed.Version "fresh-admission" (launch "Competing" "Flexible") changed)

[<Test>]
let wq01_full_mutation_payload_receipts_reject_semantic_reuse () =
    let p = empty () |> fun p -> publish p (implementation "B" 1L [])
    let withA =
        publishStage p.Version "same-publish-op" (implementation "A" 2L ["B"]) p |> success
    rejected Rejection.OperationIdReused
        (publishStage withA.Version "same-publish-op" (implementation "A" 2L []) withA)
    rejected Rejection.OperationIdReused
        (publishStage withA.Version "same-publish-op"
            { implementation "A" 2L ["B"] with SourceLineage = Some sourceLineage } withA)
    let m = message "M" (Subject.Role "Lead")
    let work = roleMessageWork "M" 3L
    let posted = postMessage withA.Version "same-message-op" m (Some work) withA |> success
    rejected Rejection.OperationIdReused
        (postMessage posted.Version "same-message-op" { m with Subject = Subject.Task "task-A" } (Some work) posted)
    rejected Rejection.OperationIdReused
        (postMessage posted.Version "same-message-op" m
            (Some { work with QueueId = "role:another" }) posted)
    let admitted = admitProject posted.Version "same-admit-op" (launch "L" "Worker") posted |> success
    rejected Rejection.OperationIdReused
        (admitProject admitted.Version "same-admit-op"
            { launch "L" "Worker" with AccountTokenId = "token:different" } admitted)
    rejected Rejection.OperationIdReused
        (admitProject admitted.Version "same-admit-op"
            { launch "L" "Worker" with RoleVersion = 2 } admitted)

[<Test>]
let wq01_handoff_rejects_unrelated_wip_and_pins_continuation_for_successor () =
    let initial =
        activated "L" "Worker" { implementation "A" 2L [] with SourceLineage = Some sourceLineage }
        |> fun p -> publish p { implementation "B" 1L ["B"] with State = WorkState.Blocked }
    let gated = setHardPrerequisite initial.Version "new-hard-edge" "A" "B" 10000L initial |> success
    let good = sourceProof gated.Works.["A"]
    let attempt evidence name =
        resolveDependencyGate gated.Version name "L" "A" (SourceHandoffProof.RemoteSnapshot evidence) 5L gated
    rejected Rejection.MissingProof (attempt { good with RepositoryId = "unrelated" } "wrong-repo")
    rejected Rejection.MissingProof (attempt { good with TaskId = "another-task" } "wrong-task")
    rejected Rejection.MissingProof (attempt { good with LineageId = "another-lineage" } "wrong-lineage")
    rejected Rejection.MissingProof (attempt { good with ExpectedRef = "refs/heads/wrong" } "wrong-source-ref")
    rejected Rejection.MissingProof (attempt { good with ExpectedSha = "wrong-baseline" } "wrong-source-sha")
    rejected Rejection.MissingProof (attempt { good with ClaimGeneration = 99 } "wrong-activation")
    rejected Rejection.MissingProof (attempt { good with ContinuationSha = "" } "missing-continuation")
    rejected Rejection.MissingProof (attempt { good with ReachabilityVerified = false } "unverified-reachability")
    rejected Rejection.MissingProof
        (resolveDependencyGate gated.Version "wrong-readonly" "L" "A"
            (SourceHandoffProof.ReadOnly "some-text") 5L gated)
    let blocked = attempt good "verified-handoff" |> success
    Assert.That(blocked.Works.["A"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(blocked.Works.["A"].SourceLineage.Value.ReachableRef, Is.EqualTo(Some good.ContinuationRef))
    Assert.That(blocked.Works.["A"].SourceLineage.Value.ContinuationSha, Is.EqualTo(Some good.ContinuationSha))
    Assert.That(blocked.Works.["A"].SourceLineage.Value.Generation, Is.EqualTo(2))
    let woken =
        waiveHardPrerequisite blocked.Version "verified-waiver" "A" "B" (waiver "A" ["B"]) 10000L blocked |> success
    let successor = woken |> fun p -> admit p "Next" "Worker"
                          |> fun p -> submit p "Next"
                          |> fun p -> initialize p "Next"
                          |> fun p -> claim p "Next"
    Assert.That(successor.Launches.["Next"].PrimaryWorkId, Is.EqualTo(Some "A"))
    Assert.That(successor.Works.["A"].SourceLineage.Value.ContinuationSha, Is.EqualTo(Some good.ContinuationSha))
    Assert.That(successor.Works.["A"].SourceLineage.Value.ReachableRef, Is.EqualTo(Some good.ContinuationRef))


// Review #2 regressions: timestamp and FIFO sequence are distinct witnesses,
// and the Phase A Blocked state has no authority without an unmet hard edge.
[<Test>]
let wq01_wake_time_does_not_overtake_older_ready_work_and_ties_use_sequence () =
    let initial =
        empty ()
        |> fun p -> publish p { implementation "B" 1000L [] with AvailableSequence = 1L }
        |> fun p -> publish p { implementation "C" 2000L [] with AvailableSequence = 2L }
        |> fun p -> publish p { implementation "A" 1500L ["B"] with State = WorkState.Blocked; AvailableSequence = 3L }
        |> fun p -> publish p { implementation "D" 1600L ["B"] with State = WorkState.Blocked; AvailableSequence = 4L }
    let claimed =
        initial
        |> fun p -> admit p "LB" "Worker"
        |> fun p -> submit p "LB"
        |> fun p -> initialize p "LB"
        |> fun p -> claim p "LB"
    Assert.That(claimed.Launches.["LB"].PrimaryWorkId, Is.EqualTo(Some "B"))
    let woken = completeOrdinary claimed.Version "finish:B/wake" "LB" "B" 3000L claimed |> success
    Assert.That(woken.Works.["A"].AvailableAt, Is.EqualTo(3000L))
    Assert.That(woken.Works.["D"].AvailableAt, Is.EqualTo(3000L))
    Assert.That(woken.Works.["A"].AvailableSequence, Is.EqualTo(5L))
    Assert.That(woken.Works.["D"].AvailableSequence, Is.EqualTo(6L))
    Assert.That(woken.Works.["C"].AvailableAt, Is.EqualTo(2000L))
    Assert.That(orderAvailable woken |> success |> List.map (fun x -> x.Id),
                Is.EqualTo(box [ "C"; "A"; "D" ]))
    Assert.That(
        completeOrdinary claimed.Version "finish:B/wake" "LB" "B" 3000L woken |> success,
        Is.EqualTo(woken))
    rejected Rejection.OperationIdReused
        (completeOrdinary claimed.Version "finish:B/wake" "LB" "B" 3001L woken)
    rejected Rejection.CasConflict
        (completeOrdinary claimed.Version "stale-other-client" "LB" "B" 3000L woken)

[<Test>]
let wq01_blocked_publication_requires_a_real_unmet_hard_edge () =
    let emptyProject = empty ()
    let invalid = { implementation "B" 1L [] with State = WorkState.Blocked }
    rejected (Rejection.InvalidState "Blocked requires an unmet hard prerequisite")
        (publishStage emptyProject.Version "invalid:stage" invalid emptyProject)
    rejected (Rejection.InvalidState "Blocked requires an unmet hard prerequisite")
        (postMessage emptyProject.Version "invalid:role"
            (message "M" (Subject.Role "Lead"))
            (Some { roleMessageWork "M" 2L with State = WorkState.Blocked }) emptyProject)
    let completePrerequisite =
        publish emptyProject { implementation "Done" 1L [] with State = WorkState.Completed }
    rejected (Rejection.InvalidState "Blocked requires an unmet hard prerequisite")
        (publishStage completePrerequisite.Version "invalid:already-satisfied"
            { implementation "Late" 3L ["Done"] with State = WorkState.Blocked }
            completePrerequisite)

    // A self-cycle, its dependent and a Role-message generation cannot be
    // awakened by editing/completing an unrelated stage.
    let p = activated "L" "Worker" (implementation "Unrelated" 10L [])
    let p = publish p { implementation "B" 20L ["B"] with State = WorkState.Blocked }
    let p = publish p (implementation "A" 30L ["B"])
    let p =
        postMessage p.Version "post:valid-role"
            (message "Valid" (Subject.Role "Lead"))
            (Some { roleMessageWork "Valid" 35L with
                        Dependencies = Set.singleton "B"
                        State = WorkState.Blocked }) p |> success
    let p = publish p (implementation "C" 40L [])
    let changed = setHardPrerequisite p.Version "unrelated-edge" "C" "B" 1000L p |> success
    for id in [ "A"; "B"; "role-msg:Valid" ] do
        Assert.That(changed.Works.[id].State, Is.EqualTo(WorkState.Blocked))
    let completed =
        completeOrdinary changed.Version "complete:unrelated" "L" "Unrelated" 2000L changed |> success
    for id in [ "A"; "B"; "role-msg:Valid" ] do
        Assert.That(completed.Works.[id].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(completed.Messages.["Valid"].State, Is.EqualTo(MessageState.Pending))

[<Test>]
let wq01_role_message_initially_blocked_by_hard_edge_wakes_on_same_edge_completion () =
    let p = empty () |> fun p -> publish p (implementation "B" 100L [])
    let p =
        postMessage p.Version "post:blocked-role" (message "M" (Subject.Role "Lead"))
            (Some { roleMessageWork "M" 200L with
                        Dependencies = Set.singleton "B"
                        State = WorkState.Blocked }) p |> success
    Assert.That(p.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Blocked))
    let claimed = p |> fun p -> admit p "LB" "Worker"
                    |> fun p -> submit p "LB"
                    |> fun p -> initialize p "LB"
                    |> fun p -> claim p "LB"
    let woken = completeOrdinary claimed.Version "complete:role-prereq" "LB" "B" 4000L claimed |> success
    Assert.That(woken.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Available))
    Assert.That(woken.Works.["role-msg:M"].AvailableAt, Is.EqualTo(4000L))
    Assert.That(woken.Messages.["M"].State, Is.EqualTo(MessageState.Pending))


// Review #3: ancillary Role-message claims must obey the same hard-edge
// graph CAS as primary work, and must recover without orphaning a gate.
let private wq01ClaimedAncillaryWithNewHardEdge () =
    let initial =
        empty ()
        |> fun p -> publish p (stage "Plan" WorkKind.Planning "planning" 1L [] WorkState.Available)
        |> fun p -> publishRoleMessage p "M" 2L
        |> fun p -> publish p (implementation "B" 3L [])
        |> fun p -> admit p "L" "Lead"
        |> fun p -> submit p "L"
        |> fun p -> initialize p "L"
        |> fun p -> claim p "L"
    Assert.That(initial.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "Plan"))
    let dependent = publish initial (implementation "D" 4L ["role-msg:M"])
    Assert.That(dependent.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    let ancillary = claimAncillaryMessage dependent.Version "take-ancillary-M" "L" "M" dependent |> success
    Assert.That(ancillary.Messages.["M"].State, Is.EqualTo(MessageState.ClaimedAncillary "L"))
    let gated =
        setHardPrerequisite ancillary.Version "new-hard-edge:W-B" "role-msg:M" "B" 5000L ancillary
        |> success
    Assert.That(gated.Works.["role-msg:M"].Gate,
                Is.EqualTo(GateState.DependencyGatePending gated.GraphVersion))
    ancillary, gated

[<Test>]
let wq01_ancillary_hard_edge_wins_before_reply_and_stale_clone_cannot_publish () =
    let before, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    rejected Rejection.DependencyGatePending
        (completeAncillaryCurrent gated.Version "ack-after-edge" "L" "M" "reply" 6000L gated)
    rejected Rejection.CasConflict
        (completeAncillaryCurrent before.Version "stale-clone-ack" "L" "M" "reply" 6000L gated)
    Assert.That(gated.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(gated.Messages.["M"].State, Is.EqualTo(MessageState.ClaimedAncillary "L"))
    Assert.That(gated.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    // The opposite CAS winner may finish the message, but a late hard-edge
    // operation must not resurrect or mutate its already-terminal generation.
    let completed =
        completeAncillaryCurrent before.Version "ack-before-edge" "L" "M" "reply" 6000L before
        |> success
    Assert.That(completed.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(completed.Messages.["M"].State, Is.EqualTo(MessageState.Handled "reply"))
    Assert.That(completed.Works.["D"].State, Is.EqualTo(WorkState.Available))
    rejected Rejection.CasConflict
        (setHardPrerequisite before.Version "stale-edge" "role-msg:M" "B" 7000L completed)
    rejected Rejection.StageAlreadyTerminal
        (setHardPrerequisite completed.Version "too-late-edge" "role-msg:M" "B" 7000L completed)
    let replay =
        completeAncillaryCurrent before.Version "ack-before-edge" "L" "M" "reply" 6000L completed
        |> success
    Assert.That(replay, Is.EqualTo(completed))
    rejected Rejection.OperationIdReused
        (completeAncillaryCurrent completed.Version "ack-before-edge" "L" "M" "different-reply" 6000L completed)

[<Test>]
let wq01_gated_ancillary_terminal_blocks_then_wakes_once_on_real_dependency_completion () =
    let before, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let original = gated.Works.["role-msg:M"]
    let terminal = completeOrdinary gated.Version "finish-primary" "L" "Plan" 10000L gated |> success
    let blocked = terminal.Works.["role-msg:M"]
    Assert.That(blocked.State, Is.EqualTo(WorkState.Blocked))
    Assert.That(blocked.Gate, Is.EqualTo(GateState.Clear))
    Assert.That(blocked.ClaimGeneration, Is.EqualTo(original.ClaimGeneration + 1))
    Assert.That(blocked.AvailableSequence, Is.EqualTo(original.AvailableSequence))
    Assert.That(terminal.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
    Assert.That(terminal.Launches.["L"].AncillaryMessages, Is.Empty)
    Assert.That(terminal.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:finish-primary"))
    Assert.That(terminal.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(orderAvailable terminal |> success |> List.exists (fun w -> w.Id = "role-msg:M"), Is.False)
    let claimedB =
        terminal |> fun p -> admit p "WorkerB" "Worker"
                 |> fun p -> submit p "WorkerB"
                 |> fun p -> initialize p "WorkerB"
                 |> fun p -> claim p "WorkerB"
    Assert.That(claimedB.Launches.["WorkerB"].PrimaryWorkId, Is.EqualTo(Some "B"))
    let woken = completeOrdinary claimedB.Version "finish-B" "WorkerB" "B" 12000L claimedB |> success
    let revived = woken.Works.["role-msg:M"]
    Assert.That(revived.State, Is.EqualTo(WorkState.Available))
    Assert.That(revived.Gate, Is.EqualTo(GateState.Clear))
    Assert.That(revived.AvailableAt, Is.EqualTo(12000L))
    Assert.That(revived.AvailableSequence, Is.GreaterThan(original.AvailableSequence))
    Assert.That(revived.AvailableSequence, Is.EqualTo(woken.NextAvailability))
    Assert.That(woken.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    let replay = completeOrdinary claimedB.Version "finish-B" "WorkerB" "B" 12000L woken |> success
    Assert.That(replay, Is.EqualTo(woken))
    rejected Rejection.CasConflict
        (completeOrdinary claimedB.Version "stale-finish-B" "WorkerB" "B" 12000L woken)
    let next =
        woken |> fun p -> admit p "NextLead" "Lead"
              |> fun p -> submit p "NextLead"
              |> fun p -> initialize p "NextLead"
              |> fun p -> claim p "NextLead"
    Assert.That(next.Launches.["NextLead"].PrimaryWorkId, Is.EqualTo(Some "role-msg:M"))
    let answered =
        completePrimaryCurrent next.Version "answer-requeued" "NextLead" "role-msg:M" "M" "answer" 14000L next
        |> success
    Assert.That(answered.Messages.["M"].State, Is.EqualTo(MessageState.Handled "answer"))
    Assert.That(answered.Works.["D"].State, Is.EqualTo(WorkState.Available))

[<Test>]
let wq01_ancillary_gate_waiver_and_terminal_edge_race_preserve_launch_credits () =
    let before, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let withSecondPlan =
        gated
        |> fun p -> publish p (stage "OtherPlan" WorkKind.Planning "planning" 5L [] WorkState.Available)
        |> fun p -> admit p "OtherLead" "Lead"
    Assert.That(withSecondPlan.Launches.["OtherLead"].Credit, Is.EqualTo(Some "OtherPlan"))
    let terminal = completeOrdinary withSecondPlan.Version "primary-term-waiver" "L" "Plan" 10000L withSecondPlan |> success
    Assert.That(terminal.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(terminal.Launches.["OtherLead"].Credit, Is.EqualTo(Some "OtherPlan"))
    rejected Rejection.MissingProof
        (waiveHardPrerequisite terminal.Version "unverified-waiver" "role-msg:M" "B"
            { waiver "role-msg:M" ["B"] with CanonicallyVerified = false } 11000L terminal)
    let woken =
        waiveHardPrerequisite terminal.Version "approved-waiver" "role-msg:M" "B"
            (waiver "role-msg:M" ["B"]) 15000L terminal |> success
    Assert.That(woken.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Available))
    Assert.That(woken.Works.["role-msg:M"].Gate, Is.EqualTo(GateState.Clear))
    Assert.That(woken.Works.["role-msg:M"].AvailableAt, Is.EqualTo(15000L))
    Assert.That(woken.Launches.["OtherLead"].Credit, Is.EqualTo(Some "role-msg:M"))
    Assert.That(woken.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    let replay = waiveHardPrerequisite terminal.Version "approved-waiver" "role-msg:M" "B"
                     (waiver "role-msg:M" ["B"]) 15000L woken |> success
    Assert.That(replay, Is.EqualTo(woken))
    let resumed = woken |> fun p -> submit p "OtherLead" |> fun p -> initialize p "OtherLead" |> fun p -> claim p "OtherLead"
    Assert.That(resumed.Launches.["OtherLead"].PrimaryWorkId, Is.EqualTo(Some "role-msg:M"))
    let completed = completePrimaryCurrent resumed.Version "answered-after-waiver" "OtherLead" "role-msg:M" "M" "reply" 16000L resumed |> success
    Assert.That(completed.Works.["D"].State, Is.EqualTo(WorkState.Available))
    // Primary terminal winning before the edge is equally safe: the newly
    // available work becomes ordinarily Blocked when the later edge wins.
    let terminalFirst = completeOrdinary before.Version "primary-before-edge" "L" "Plan" 10000L before |> success
    rejected Rejection.CasConflict
        (setHardPrerequisite before.Version "stale-after-terminal" "role-msg:M" "B" 11000L terminalFirst)
    let edgeAfter = setHardPrerequisite terminalFirst.Version "edge-after-terminal" "role-msg:M" "B" 11000L terminalFirst |> success
    Assert.That(edgeAfter.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(edgeAfter.Works.["role-msg:M"].Gate, Is.EqualTo(GateState.Clear))
    Assert.That(edgeAfter.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
    // A Role stage with invented external source state cannot be terminally
    // released by this side-effect-free contract without reconciliation.
    let fakeSource = { gated.Works.["role-msg:M"] with SourceLineage = Some sourceLineage }
    let unsafe = { gated with Works = gated.Works.Add("role-msg:M", fakeSource) }
    rejected Rejection.UnsafeExternalEffect
        (completeOrdinary unsafe.Version "unsafe-source-terminal" "L" "Plan" 12000L unsafe)

// Review #4: a gated ancillary Role handler must be independently recoverable
// while its unrelated primary Plan remains Claimed by the same registered launch.
let private resolveAncillaryW (p: Project) op now =
    let w = p.Works.["role-msg:M"]
    let launch = p.Launches.["L"]
    let gateVersion =
        match w.Gate with
        | GateState.DependencyGatePending graph -> graph
        | gate -> failwithf "Expected pending ancillary gate, got %A" gate
    resolveAncillaryDependencyGate p.Version op "L" "M" w.Id
        launch.Version w.ClaimGeneration gateVersion now p

let private completeAncillaryBWhilePlanLives (p: Project) op now =
    let claimed =
        p |> fun p -> admit p "WorkerB" "Worker"
          |> fun p -> submit p "WorkerB"
          |> fun p -> initialize p "WorkerB"
          |> fun p -> claim p "WorkerB"
    Assert.That(claimed.Launches.["WorkerB"].PrimaryWorkId, Is.EqualTo(Some "B"))
    completeOrdinary claimed.Version op "WorkerB" "B" now claimed |> success

[<Test>]
let wq01_ancillary_gate_resolves_after_prerequisite_completes_while_plan_still_active () =
    let _, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let waiting = completeAncillaryBWhilePlanLives gated "finish-B-before-Plan" 12000L
    Assert.That(waiting.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(waiting.Works.["role-msg:M"].Gate,
                Is.EqualTo(GateState.DependencyGatePending gated.GraphVersion))
    rejected Rejection.DependencyGatePending
        (completeAncillaryCurrent waiting.Version "cannot-answer-unresolved" "L" "M" "reply" 12001L waiting)
    let original = waiting.Works.["role-msg:M"]
    let originalLaunch = waiting.Launches.["L"]
    let resolved = resolveAncillaryW waiting "resolve-completed-B" 13000L |> success
    let revived = resolved.Works.["role-msg:M"]
    Assert.That(revived.State, Is.EqualTo(WorkState.Available))
    Assert.That(revived.Gate, Is.EqualTo(GateState.Clear))
    Assert.That(revived.ClaimGeneration, Is.EqualTo(original.ClaimGeneration + 1))
    Assert.That(revived.AvailableAt, Is.EqualTo(13000L))
    Assert.That(revived.AvailableSequence, Is.GreaterThan(original.AvailableSequence))
    Assert.That(resolved.NextAvailability, Is.EqualTo(revived.AvailableSequence))
    Assert.That(resolved.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
    Assert.That(resolved.Launches.["L"].AncillaryMessages, Is.Empty)
    Assert.That(resolved.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "Plan"))
    Assert.That(resolved.Works.["Plan"], Is.EqualTo(waiting.Works.["Plan"]))
    Assert.That(resolved.Launches.["L"].State, Is.EqualTo(originalLaunch.State))
    Assert.That(resolved.Launches.["L"].Lease, Is.EqualTo(originalLaunch.Lease))
    Assert.That(resolved.Launches.["L"].AccountTokenId, Is.EqualTo(originalLaunch.AccountTokenId))
    Assert.That(resolved.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    let replay = resolveAncillaryDependencyGate waiting.Version "resolve-completed-B" "L" "M" "role-msg:M"
                     originalLaunch.Version original.ClaimGeneration gated.GraphVersion 13000L resolved |> success
    Assert.That(replay, Is.EqualTo(resolved))
    rejected Rejection.OperationIdReused
        (resolveAncillaryDependencyGate waiting.Version "resolve-completed-B" "L" "M" "role-msg:M"
            originalLaunch.Version original.ClaimGeneration gated.GraphVersion 13001L resolved)
    rejected (Rejection.InvalidState "no live ancillary message handler")
        (completeAncillaryCurrent resolved.Version "invalid-old-claim" "L" "M" "reply" 14000L resolved)
    let reclaimed = claimAncillaryMessage resolved.Version "reclaim-M-while-Plan-live" "L" "M" resolved |> success
    Assert.That(reclaimed.Works.["role-msg:M"].ClaimGeneration, Is.EqualTo(revived.ClaimGeneration + 1))
    let answered = completeAncillaryCurrent reclaimed.Version "answer-M-after-resolve" "L" "M" "reply" 15000L reclaimed |> success
    Assert.That(answered.Messages.["M"].State, Is.EqualTo(MessageState.Handled "reply"))
    Assert.That(answered.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(answered.Works.["D"].State, Is.EqualTo(WorkState.Available))
    Assert.That(answered.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(answered.Launches.["L"].State, Is.EqualTo(originalLaunch.State))

[<Test>]
let wq01_ancillary_gate_can_block_independently_then_wake_only_after_B_completion () =
    let _, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let oldWork = gated.Works.["role-msg:M"]
    let released = resolveAncillaryW gated "release-unmet-ancillary" 8000L |> success
    Assert.That(released.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(released.Works.["role-msg:M"].Gate, Is.EqualTo(GateState.Clear))
    Assert.That(released.Works.["role-msg:M"].AvailableSequence, Is.EqualTo(oldWork.AvailableSequence))
    Assert.That(released.NextAvailability, Is.EqualTo(gated.NextAvailability))
    Assert.That(released.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
    Assert.That(released.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(released.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(orderAvailable released |> success |> List.exists (fun w -> w.Id = "role-msg:M"), Is.False)
    let woken = completeAncillaryBWhilePlanLives released "finish-B-after-independent-release" 9000L
    Assert.That(woken.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Available))
    Assert.That(woken.Works.["role-msg:M"].AvailableAt, Is.EqualTo(9000L))
    Assert.That(woken.Works.["role-msg:M"].AvailableSequence, Is.GreaterThan(oldWork.AvailableSequence))
    Assert.That(woken.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(woken.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
    let reclaimed = claimAncillaryMessage woken.Version "claim-woken-W" "L" "M" woken |> success
    let completed = completeAncillaryCurrent reclaimed.Version "answer-after-B" "L" "M" "reply" 10000L reclaimed |> success
    Assert.That(completed.Works.["D"].State, Is.EqualTo(WorkState.Available))
    Assert.That(completed.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))

[<Test>]
let wq01_ancillary_gate_verified_waiver_recovers_before_primary_terminal () =
    let _, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    rejected Rejection.MissingProof
        (waiveHardPrerequisite gated.Version "bad-waiver-while-primary" "role-msg:M" "B"
            { waiver "role-msg:M" ["B"] with CanonicallyVerified = false } 11000L gated)
    let waived =
        waiveHardPrerequisite gated.Version "verified-waiver-before-Plan" "role-msg:M" "B"
            (waiver "role-msg:M" ["B"]) 11000L gated |> success
    Assert.That(waived.Works.["role-msg:M"].Gate,
                Is.EqualTo(GateState.DependencyGatePending gated.GraphVersion))
    let resolved = resolveAncillaryW waived "resolve-waived-ancillary" 12000L |> success
    Assert.That(resolved.Works.["B"].State, Is.EqualTo(WorkState.Available))
    Assert.That(resolved.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Available))
    Assert.That(resolved.Works.["role-msg:M"].AvailableAt, Is.EqualTo(12000L))
    Assert.That(resolved.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(resolved.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    let reclaimed = claimAncillaryMessage resolved.Version "claim-waived-W" "L" "M" resolved |> success
    let replied = completeAncillaryCurrent reclaimed.Version "answer-waived-W" "L" "M" "reply" 13000L reclaimed |> success
    Assert.That(replied.Messages.["M"].State, Is.EqualTo(MessageState.Handled "reply"))
    Assert.That(replied.Works.["D"].State, Is.EqualTo(WorkState.Available))
    Assert.That(replied.Works.["B"].State, Is.EqualTo(WorkState.Available))
    Assert.That(replied.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "Plan"))

[<Test>]
let wq01_ancillary_gate_cas_races_and_source_fence_preserve_primary_and_token () =
    let before, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let original = gated.Works.["role-msg:M"]
    let launch = gated.Launches.["L"]
    let resolved = resolveAncillaryW gated "resolve-before-Plan-terminal" 8000L |> success
    rejected Rejection.CasConflict
        (completeOrdinary gated.Version "stale-primary-completion" "L" "Plan" 9000L resolved)
    let primaryTerminal =
        completeOrdinary resolved.Version "terminal-after-resolver" "L" "Plan" 9000L resolved |> success
    Assert.That(primaryTerminal.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:terminal-after-resolver"))
    Assert.That(primaryTerminal.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(primaryTerminal.Launches.["L"].AncillaryMessages, Is.Empty)
    let terminalFirst = completeOrdinary gated.Version "terminal-before-resolver" "L" "Plan" 9000L gated |> success
    Assert.That(terminalFirst.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Blocked))
    rejected Rejection.CasConflict
        (resolveAncillaryDependencyGate gated.Version "stale-resolver-after-terminal" "L" "M" "role-msg:M"
            launch.Version original.ClaimGeneration gated.GraphVersion 9001L terminalFirst)
    rejected (Rejection.InvalidState "stale ancillary claim/launch/gate generation")
        (resolveAncillaryDependencyGate terminalFirst.Version "new-resolver-after-terminal" "L" "M" "role-msg:M"
            launch.Version original.ClaimGeneration gated.GraphVersion 9001L terminalFirst)
    rejected (Rejection.InvalidState "stale ancillary claim/launch/gate generation")
        (resolveAncillaryDependencyGate gated.Version "bad-claim-generation" "L" "M" "role-msg:M"
            launch.Version (original.ClaimGeneration - 1) gated.GraphVersion 9001L gated)
    rejected (Rejection.InvalidState "stale ancillary claim/launch/gate generation")
        (resolveAncillaryDependencyGate gated.Version "bad-gate-generation" "L" "M" "role-msg:M"
            launch.Version original.ClaimGeneration (gated.GraphVersion + 1) 9001L gated)
    rejected Rejection.CasConflict
        (resolveAncillaryDependencyGate before.Version "stale-two-client-snapshot" "L" "M" "role-msg:M"
            launch.Version original.ClaimGeneration gated.GraphVersion 9001L gated)
    let invalidSource = { original with SourceLineage = Some sourceLineage }
    let unsafe = { gated with Works = gated.Works.Add(original.Id, invalidSource) }
    rejected Rejection.UnsafeExternalEffect
        (resolveAncillaryW unsafe "unsafe-source-ancillary" 10000L)

[<Test>]
let wq01_ancillary_resolve_rematches_unclaimed_credits_without_new_token () =
    let _, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let credited =
        gated
        |> fun p -> publish p (stage "OtherPlan" WorkKind.Planning "planning" 5L [] WorkState.Available)
        |> fun p -> admit p "OtherLead" "Lead"
    Assert.That(credited.Launches.["OtherLead"].Credit, Is.EqualTo(Some "OtherPlan"))
    let withB = completeAncillaryBWhilePlanLives credited "B-while-credited" 11000L
    let account = reserveGlobal (emptyAccount 2).Version "global:L" "L" (emptyAccount 2) |> success
    let account = reserveGlobal account.Version "global:OtherLead" "OtherLead" account |> success
    Assert.That(account.Tokens.Count, Is.EqualTo(2))
    let resolved = resolveAncillaryW withB "release-W-with-credits" 12000L |> success
    let credit = resolved.Launches.["OtherLead"].Credit
    Assert.That(credit.IsSome, Is.True)
    Assert.That(credit |> Option.map (fun id -> resolved.Works.[id].State), Is.EqualTo(Some WorkState.Available))
    Assert.That(resolved.Launches.["L"].Credit, Is.EqualTo(None))
    Assert.That(resolved.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "Plan"))
    Assert.That(resolved.Launches.["L"].AccountTokenId, Is.EqualTo(credited.Launches.["L"].AccountTokenId))
    Assert.That(resolved.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(account.Tokens.Count, Is.EqualTo(2))


// Review #5: a current project CAS is not enough to authorize a reply from
// a handler that was invalidated and later reacquired by the same launch.
[<TestCase(false)>]
[<TestCase(true)>]
let wq01_handler_generation_fences_old_reply_after_same_launch_reclaim (useWaiver: bool) =
    let _, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let g1 = gated.Messages.["M"].HandlerGeneration
    Assert.That(g1, Is.EqualTo(gated.Works.["role-msg:M"].ClaimGeneration))
    let satisfied =
        if useWaiver then
            waiveHardPrerequisite gated.Version "generation-waiver" "role-msg:M" "B"
                (waiver "role-msg:M" ["B"]) 11000L gated |> success
        else
            completeAncillaryBWhilePlanLives gated "generation-B-complete" 11000L
    let originalPlan = satisfied.Works.["Plan"]
    let originalToken = satisfied.Launches.["L"].AccountTokenId
    let released = resolveAncillaryW satisfied "generation-release" 12000L |> success
    let g2 = released.Messages.["M"].HandlerGeneration
    Assert.That(g2, Is.EqualTo(g1 + 1))
    Assert.That(g2, Is.EqualTo(released.Works.["role-msg:M"].ClaimGeneration))
    let reclaimed = claimAncillaryMessage released.Version "generation-reclaim" "L" "M" released |> success
    let g3 = reclaimed.Messages.["M"].HandlerGeneration
    Assert.That(g3, Is.EqualTo(g2 + 1))
    Assert.That(g3, Is.EqualTo(reclaimed.Works.["role-msg:M"].ClaimGeneration))
    // Two clients: stale project-version CAS and, critically, a stale handler
    // generation with a FRESH project version and distinct operation ID.
    rejected Rejection.CasConflict
        (completeAncillaryMessage released.Version "stale-project-client" "L" "M" g1 "old-answer" 13000L reclaimed)
    rejected (Rejection.InvalidState "stale ancillary handler generation")
        (completeAncillaryMessage reclaimed.Version "stale-handler-client" "L" "M" g1 "old-answer" 13000L reclaimed)
    Assert.That(reclaimed.Messages.["M"].State, Is.EqualTo(MessageState.ClaimedAncillary "L"))
    Assert.That(reclaimed.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(reclaimed.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(reclaimed.Works.["Plan"], Is.EqualTo(originalPlan))
    Assert.That(reclaimed.Launches.["L"].AccountTokenId, Is.EqualTo(originalToken))
    Assert.That(reclaimed.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "Plan"))
    let handled =
        completeAncillaryMessage reclaimed.Version "generation-g3-reply" "L" "M" g3 "new-answer" 14000L reclaimed |> success
    Assert.That(handled.Messages.["M"].State, Is.EqualTo(MessageState.Handled "new-answer"))
    Assert.That(handled.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(handled.Works.["D"].State, Is.EqualTo(WorkState.Available))
    Assert.That(handled.Works.["Plan"], Is.EqualTo(originalPlan))
    // Losing the success receipt replays the exact g3 payload, not merely M/L.
    let replay =
        completeAncillaryMessage reclaimed.Version "generation-g3-reply" "L" "M" g3 "new-answer" 14000L handled |> success
    Assert.That(replay, Is.EqualTo(handled))
    rejected Rejection.OperationIdReused
        (completeAncillaryMessage handled.Version "generation-g3-reply" "L" "M" g1 "new-answer" 14000L handled)

[<Test>]
let wq01_handler_generation_primary_terminal_race_fences_invalidated_ancillary () =
    let _, gated = wq01ClaimedAncillaryWithNewHardEdge ()
    let satisfied = completeAncillaryBWhilePlanLives gated "race-finish-B" 11000L
    let released = resolveAncillaryW satisfied "race-release" 12000L |> success
    let reclaimed = claimAncillaryMessage released.Version "race-reclaim" "L" "M" released |> success
    let g3 = reclaimed.Messages.["M"].HandlerGeneration
    let token = reclaimed.Launches.["L"].AccountTokenId
    // Primary terminal wins: the ancillary generation is invalidated in the
    // same CAS. Neither a stale client nor a refreshed version can finish it.
    let terminal = completeOrdinary reclaimed.Version "race-terminal-first" "L" "Plan" 13000L reclaimed |> success
    rejected Rejection.CasConflict
        (completeAncillaryMessage reclaimed.Version "race-stale-cas" "L" "M" g3 "answer" 14000L terminal)
    rejected (Rejection.InvalidState "no live ancillary message handler")
        (completeAncillaryMessage terminal.Version "race-new-cas" "L" "M" g3 "answer" 14000L terminal)
    Assert.That(terminal.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
    Assert.That(terminal.Messages.["M"].HandlerGeneration, Is.EqualTo(g3 + 1))
    Assert.That(terminal.Works.["role-msg:M"].ClaimGeneration, Is.EqualTo(g3 + 1))
    Assert.That(terminal.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(terminal.Launches.["L"].AccountTokenId, Is.EqualTo(token))
    Assert.That(terminal.Launches.["L"].State, Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:race-terminal-first"))
    // Opposite CAS winner: valid g3 reply handles once, then Plan can finish
    // without requeueing or clearing the handled message.
    let answered =
        completeAncillaryMessage reclaimed.Version "race-answer-first" "L" "M" g3 "new-answer" 13000L reclaimed |> success
    rejected Rejection.CasConflict
        (completeOrdinary reclaimed.Version "race-terminal-stale-client" "L" "Plan" 14000L answered)
    let finished = completeOrdinary answered.Version "race-terminal-after-answer" "L" "Plan" 14000L answered |> success
    Assert.That(finished.Messages.["M"].State, Is.EqualTo(MessageState.Handled "new-answer"))
    Assert.That(finished.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(finished.Works.["D"].State, Is.EqualTo(WorkState.Available))
    Assert.That(finished.Launches.["L"].AccountTokenId, Is.EqualTo(token))

[<Test>]
let wq01_handler_generations_remain_fenced_over_repeated_gate_release_reclaim_cycles () =
    let initial =
        empty ()
        |> fun p -> publish p (stage "Plan" WorkKind.Planning "planning" 1L [] WorkState.Available)
        |> fun p -> publishRoleMessage p "M" 2L
        |> fun p -> admit p "L" "Lead"
        |> fun p -> submit p "L"
        |> fun p -> initialize p "L"
        |> fun p -> claim p "L"
    let withDownstream = publish initial (implementation "D" 3L ["role-msg:M"])
    let mutable current = claimAncillaryMessage withDownstream.Version "initial-ancillary" "L" "M" withDownstream |> success
    let token = current.Launches.["L"].AccountTokenId
    let mutable olderGenerations : int list = []
    for i in 1..5 do
        let g = current.Messages.["M"].HandlerGeneration
        Assert.That(current.Works.["role-msg:M"].ClaimGeneration, Is.EqualTo(g))
        olderGenerations <- g :: olderGenerations
        let prerequisite = sprintf "B%d" i
        current <- publish current (implementation prerequisite (100L + int64 i) [])
        current <- setHardPrerequisite current.Version (sprintf "cycle-edge-%d" i)
                       "role-msg:M" prerequisite (2000L + int64 i) current |> success
        rejected Rejection.DependencyGatePending
            (completeAncillaryMessage current.Version (sprintf "cycle-gated-answer-%d" i)
                "L" "M" g "uncommitted" (2100L + int64 i) current)
        current <- resolveAncillaryW current (sprintf "cycle-release-%d" i) (3000L + int64 i) |> success
        Assert.That(current.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Blocked))
        Assert.That(current.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
        Assert.That(current.Messages.["M"].HandlerGeneration, Is.EqualTo(g + 1))
        let worker = sprintf "WorkerB%d" i
        current <- current |> fun p -> admit p worker "Worker"
                           |> fun p -> submit p worker
                           |> fun p -> initialize p worker
                           |> fun p -> claim p worker
        Assert.That(current.Launches.[worker].PrimaryWorkId, Is.EqualTo(Some prerequisite))
        current <- completeOrdinary current.Version (sprintf "cycle-finish-B%d" i)
                       worker prerequisite (4000L + int64 i) current |> success
        Assert.That(current.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Available))
        current <- claimAncillaryMessage current.Version (sprintf "cycle-reclaim-%d" i) "L" "M" current |> success
        Assert.That(current.Messages.["M"].HandlerGeneration, Is.EqualTo(g + 2))
        Assert.That(current.Works.["role-msg:M"].ClaimGeneration, Is.EqualTo(g + 2))
        for stale in olderGenerations do
            rejected (Rejection.InvalidState "stale ancillary handler generation")
                (completeAncillaryMessage current.Version
                    (sprintf "cycle-stale-%d-%d" i stale) "L" "M" stale "old"
                    (5000L + int64 i) current)
        Assert.That(current.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
        Assert.That(current.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
        Assert.That(current.Launches.["L"].AccountTokenId, Is.EqualTo(token))
    let liveGeneration = current.Messages.["M"].HandlerGeneration
    let answered =
        completeAncillaryMessage current.Version "cycles-current-reply" "L" "M"
            liveGeneration "final-reply" 9000L current |> success
    Assert.That(answered.Works.["D"].State, Is.EqualTo(WorkState.Available))
    Assert.That(answered.Messages.["M"].HandlerGeneration, Is.EqualTo(liveGeneration))
    Assert.That(answered.Messages.["M"].State, Is.EqualTo(MessageState.Handled "final-reply"))

[<Test>]
let wq01_workless_ancillary_handler_cannot_reuse_preclaim_generation () =
    let primary =
        empty ()
        |> fun p -> publish p (stage "Plan" WorkKind.Planning "planning" 1L [] WorkState.Available)
        |> fun p -> admit p "L" "Lead"
        |> fun p -> submit p "L"
        |> fun p -> initialize p "L"
        |> fun p -> claim p "L"
    let posted =
        postMessage primary.Version "post-workless-role" (message "W" (Subject.Task "task-Plan")) None primary |> success
    let claimed = claimAncillaryMessage posted.Version "claim-workless-role" "L" "W" posted |> success
    let generation = claimed.Messages.["W"].HandlerGeneration
    Assert.That(generation, Is.GreaterThan(0))
    rejected (Rejection.InvalidState "stale ancillary handler generation")
        (completeAncillaryMessage claimed.Version "old-workless-answer" "L" "W"
            (generation - 1) "old" 10000L claimed)
    let handled =
        completeAncillaryMessage claimed.Version "new-workless-answer" "L" "W"
            generation "new" 10000L claimed |> success
    Assert.That(handled.Messages.["W"].State, Is.EqualTo(MessageState.Handled "new"))
    Assert.That(handled.Works.["Plan"].State, Is.EqualTo(WorkState.Claimed "L"))
    let replay =
        completeAncillaryMessage claimed.Version "new-workless-answer" "L" "W"
            generation "new" 10000L handled |> success
    Assert.That(replay, Is.EqualTo(handled))
    rejected Rejection.OperationIdReused
        (completeAncillaryMessage handled.Version "new-workless-answer" "L" "W"
            (generation - 1) "new" 10000L handled)
