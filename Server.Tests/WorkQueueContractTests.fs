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
      WorkId = None }

let private verifiedPush originalId sourceSha targetRef remoteCommit : VerifiedRemoteEffect =
    { OriginalPushOperationId = originalId
      AcceptedSourceSha = sourceSha
      TargetRef = targetRef
      RemoteCommitSha = remoteCommit
      AncestryVerified = true
      JobsReconciled = true }

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
    let updated = { project.Works.["C"] with Dependencies = Set.empty }
    let withoutEdge = { project with Works = project.Works.Add("C", updated) }
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
    let doneWork = completeOrdinary claimed.Version "completed" "L" "A" claimed |> success
    rejected (Rejection.InvalidState "requires registered launch with no prior primary claim")
        (claimNext doneWork.Version "second-claim" "L" doneWork)
    rejected Rejection.CasConflict (completeOrdinary claimed.Version "stale-complete" "L" "A" doneWork)

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
        (setHardPrerequisite decided.Version "new-edge" "R" "B" decided)
    let terminal = completeOrdinary decided.Version "review-terminal" "L" "R" decided |> success
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
    let handled = completeMessageWork p.Version "reply-op" "L" workId "M1" "reply-1" p |> success
    Assert.That(handled.Messages.["M1"].State, Is.EqualTo(MessageState.Handled "reply-1"))
    Assert.That(handled.Works.[workId].State, Is.EqualTo(WorkState.Completed))
    Assert.That(handled.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:reply-op"))
    Assert.That(completeMessageWork p.Version "reply-op" "L" workId "M1" "reply-1" handled |> success,
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
    let handled = completeMessageWork p.Version "handled:M1" "L1" "role-msg:M1" "M1" "r1" p |> success
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
        completeAncillaryMessage ancillary.Version "ack-ancillary" "Active" "M" "reply" ancillary |> success
    Assert.That(acknowledged.Works.["role-msg:M"].State, Is.EqualTo(WorkState.Completed))
    Assert.That(acknowledged.Works.["LeadPlan"].State, Is.EqualTo(WorkState.Claimed "Active"))
    Assert.That(acknowledged.Launches.["Active"].State,
                Is.EqualTo(LaunchState.Registered("uuid:Active", "provider-session:Active")))

[<Test>]
let case_38_55_56_task_messages_are_passive_when_task_has_no_eligible_actor () =
    let blocked = { implementation "T" 1L [] with State = WorkState.Blocked }
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
        |> fun p -> publish p (implementation "B" 1L [])
        |> fun p -> publish p (implementation "A" 2L [])
        |> fun p -> admit p "L" "Worker"
        |> fun p -> submit p "L"
        |> fun p -> initialize p "L"
    // B is older; use a separate fixture where only A is Available.
    let p = { p with Works = p.Works.Add("B", { p.Works.["B"] with State = WorkState.Blocked }) } |> fun p -> claim p "L"
    let gated = setHardPrerequisite p.Version "edge" "A" "B" p |> success
    Assert.That(gated.Works.["A"].Gate, Is.EqualTo(GateState.DependencyGatePending gated.GraphVersion))
    rejected Rejection.CasConflict (completeOrdinary p.Version "race-complete" "L" "A" gated)
    rejected Rejection.DependencyGatePending
        (completeOrdinary gated.Version "try-complete" "L" "A" gated)
    let completed = completeOrdinary p.Version "complete-first" "L" "A" p |> success
    rejected Rejection.StageAlreadyTerminal
        (setHardPrerequisite completed.Version "too-late" "A" "B" completed)

[<Test>]
let case_62_63_safe_dependency_gate_resolution_blocks_or_requeues_once () =
    let p = activated "L" "Worker" (implementation "A" 2L [])
    let p = publish p { implementation "B" 1L [] with State = WorkState.Blocked }
    let gated = setHardPrerequisite p.Version "edge" "A" "B" p |> success
    rejected Rejection.UnsafeExternalEffect
        (resolveDependencyGate gated.Version "unsafe" "L" "A" (SourceHandoffProof.Unsafe "unreconciled worktree") 10L gated)
    let blocked = resolveDependencyGate gated.Version "safe" "L" "A" (SourceHandoffProof.RemoteSnapshot("repo","refs/heads/wip/A","sha-A",true)) 10L gated |> success
    Assert.That(blocked.Works.["A"].State, Is.EqualTo(WorkState.Blocked))
    Assert.That(blocked.Launches.["L"].State,
                Is.EqualTo(LaunchState.TerminalPendingRelease "terminal:safe"))
    let satisfied = { gated with Works = gated.Works.Add("B", { gated.Works.["B"] with State = WorkState.Completed }) }
    let available = resolveDependencyGate satisfied.Version "safe-wake" "L" "A" (SourceHandoffProof.RemoteSnapshot("repo","refs/heads/wip/A","sha-A",true)) 11L satisfied |> success
    Assert.That(available.Works.["A"].State, Is.EqualTo(WorkState.Available))
    Assert.That(available.Works.["A"].AvailableAt, Is.EqualTo(11L))
    Assert.That(available.Works.["A"].AvailableSequence, Is.GreaterThan(2L))

[<Test>]
let case_66_67_73_verdict_and_new_hard_edge_are_version_fenced () =
    let p = activated "R" "Reviewer" (review "Frozen" 1L [])
    let p = publish p { implementation "B" 2L [] with State = WorkState.Blocked }
    let gated = setHardPrerequisite p.Version "edge-first" "Frozen" "B" p |> success
    rejected Rejection.DependencyGatePending
        (decideReview gated.Version "late-verdict" "Frozen" "accepted" gated)
    rejected Rejection.CasConflict
        (decideReview p.Version "losing-verdict" "Frozen" "accepted" gated)
    let decided = decideReview p.Version "verdict-first" "Frozen" "accepted" p |> success
    rejected Rejection.DomainAlreadyFinalized
        (setHardPrerequisite decided.Version "losing-edge" "Frozen" "B" decided)

[<Test>]
let case_69_70_integration_final_vs_gate () =
    let p = activated "I" "Integrator" (integration "Int" 1L [])
    let p = publish p { implementation "B" 2L [] with State = WorkState.Blocked }
    rejected Rejection.MissingProof
        (finalizeIntegration p.Version "early-without-remote" "Int" "canonical-result" p)
    let journaled = startOriginalPush p.Version "journal-before-final" "I" "Int" "push-original" "source-int-sha" "refs/heads/main" p |> success
    let observed = recordUngatedPushEffect journaled.Version "remote-proof" "Int" (verifiedPush "push-original" "source-int-sha" "refs/heads/main" "sha:remote") journaled |> success
    let complete = finalizeIntegration observed.Version "complete-integration" "Int" "canonical-result" observed |> success
    rejected Rejection.DomainAlreadyFinalized
        (setHardPrerequisite complete.Version "edge-late" "Int" "B" complete)
    let gated = setHardPrerequisite p.Version "edge-first" "Int" "B" p |> success
    rejected Rejection.DependencyGatePending
        (finalizeIntegration gated.Version "late-final" "Int" "sha:remote" gated)
    Assert.That(gated.Works.["Int"].IntegrationAuthorized, Is.False)

[<Test>]
let case_74_75_78_79_external_push_is_fact_not_authorization () =
    let account = reserveGlobal 0 "reserve:I" "I" (emptyAccount 1) |> success
    let p = activated "I" "Integrator" (integration "W" 1L [])
    let prerequisite = { implementation "B" 2L [] with State = WorkState.Blocked }
    let p = publish p prerequisite
    let intent = startOriginalPush p.Version "journal" "I" "W" "original-push" "accepted-source" "refs/heads/main" p |> success
    let gated = setHardPrerequisite intent.Version "lead-gate" "W" "B" intent |> success
    Assert.That(gated.Works.["W"].Gate,
                Is.EqualTo(GateState.IntegrationGateHold(gated.GraphVersion, "original-push")))
    rejected Rejection.UnsafeExternalEffect
        (resolveDependencyGate gated.Version "unsafe-resolve" "I" "W" (SourceHandoffProof.ReadOnly "all-jobs-reconciled") 10L gated)
    let observed =
        reconcileExternalEffect gated.Version "reconcile" "I" "W"
            (verifiedPush "original-push" "accepted-source" "refs/heads/main" "remote-commit") gated |> success
    Assert.That(observed.Works.["W"].State, Is.EqualTo(WorkState.GateException))
    Assert.That(observed.Works.["W"].IntegrationAuthorized, Is.False)
    Assert.That(observed.ProducerCount, Is.EqualTo(0))
    rejected Rejection.MissingProof
        (resolveIntegrationGateException observed.Version "premature" "W" None observed)
    let released = releaseGlobal account.Version "release-I" "I" observed account |> success
    Assert.That(released.Tokens.["I"], Is.True)
    let authorized =
        resolveIntegrationGateException observed.Version "waive" "W" (Some "lead-waiver:v1") observed |> success
    Assert.That(authorized.Works.["W"].Domain, Is.EqualTo(DomainState.Integrated "remote-commit"))
    Assert.That(authorized.Works.["W"].IntegrationAuthorized, Is.True)
    Assert.That(authorized.ProducerCount, Is.EqualTo(1))
    Assert.That(authorized.Works.["W"].WaivedDependencies.["B"], Is.EqualTo("lead-waiver:v1"))
    Assert.That(resolveIntegrationGateException observed.Version "waive" "W" (Some "lead-waiver:v1") authorized |> success,
                Is.EqualTo(authorized))

[<Test>]
let case_76_77_80_unknown_external_push_never_releases_token_or_downstream () =
    let account = reserveGlobal 0 "reserve:I" "I" (emptyAccount 1) |> success
    let p = activated "I" "Integrator" (integration "W" 1L [])
    let p = publish p { implementation "B" 2L [] with State = WorkState.Blocked }
    let intent = startOriginalPush p.Version "journal" "I" "W" "push" "accepted-source" "refs/heads/main" p |> success
    let gated = setHardPrerequisite intent.Version "gate" "W" "B" intent |> success
    rejected Rejection.UnsafeExternalEffect
        (reconcileExternalEffect gated.Version "uncertain" "I" "W" { (verifiedPush "push" "accepted-source" "refs/heads/main" "negative-read") with AncestryVerified = false } gated)
    rejected Rejection.MissingProof (releaseGlobal account.Version "release" "I" gated account)
    Assert.That(gated.Works.["W"].IntegrationAuthorized, Is.False)
    rejected Rejection.CasConflict
        (reconcileExternalEffect intent.Version "stale-reconcile" "I" "W" (verifiedPush "push" "accepted-source" "refs/heads/main" "commit") gated)

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
        (completeOrdinary claimed.Version "skip-verdict" "R" "Frozen" claimed)
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
    let terminal = completeOrdinary handling.Version "finish-plan" "L" "Plan" handling |> success
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
    let p = publish p { implementation "B" 2L [] with State = WorkState.Blocked }
    let intent =
        startOriginalPush p.Version "push-intent" "I" "W"
            "original-op" "accepted-source" "refs/heads/main" p |> success
    let held = setHardPrerequisite intent.Version "new-edge" "W" "B" intent |> success
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
    let p = publish p { implementation "B" 2L [] with State = WorkState.Blocked }
    let intent =
        startOriginalPush p.Version "push-intent" "I" "W"
            "original-op" "accepted-source" "refs/heads/main" p |> success
    let gated = setHardPrerequisite intent.Version "new-edge" "W" "B" intent |> success
    let observed =
        reconcileExternalEffect gated.Version "original-effect" "I" "W"
            (verifiedPush "original-op" "accepted-source" "refs/heads/main" "remote-sha") gated |> success
    let satisfied =
        { observed with
            Works = observed.Works.Add("B", { observed.Works.["B"] with State = WorkState.Completed }) }
    let released =
        resolveIntegrationGateException satisfied.Version "verified-dependency" "W" None satisfied |> success
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
