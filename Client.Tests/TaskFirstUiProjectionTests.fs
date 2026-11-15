module Client.Tests.TaskFirstUiProjectionTests

open System
open NUnit.Framework
open Jarvis.WorkQueues
open Client.TaskFirstUiProjection

let private stage id dependencies =
    { Id = id
      ProjectId = "P"
      SubjectId = "task-" + id
      Kind = WorkKind.FrozenReview
      QueueId = "review"
      StageGeneration = 1
      State = WorkState.Available
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.ofList dependencies
      WaivedDependencies = Map.empty
      AvailableAt = 100L
      AvailableSequence = 1L
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private snapshot (stages: WorkStage list) =
    { Contracts.emptyProject (Map.ofList [ "Lead", Set.empty ]) with
        Works = stages |> List.map (fun stage -> stage.Id, stage) |> Map.ofList }

let private account = Contracts.emptyAccount 3

let private view p =
    projectDashboard "P" Map.empty (Ok p) (Ok account) (Some 200L) 50L

let private known = function
    | Evidence.Known value -> value
    | Evidence.Unknown reason -> failwithf "Expected complete verified view, got UNKNOWN: %s" reason

let private unknown = function
    | Evidence.Unknown _ -> ()
    | Evidence.Known _ -> Assert.Fail("Unverified view must not be presented as known/empty")

let private nextIds dashboard =
    dashboard.Next |> known |> List.map (fun row -> row.WorkId)

let private blockedIds dashboard =
    dashboard.Blocked |> known |> List.map (fun row -> row.WorkId)

let private launch id state lease =
    { Id = id
      ProjectId = "P"
      RoleId = "Lead"
      RoleVersion = 1
      AccountTokenId = "account-" + id
      State = state
      Lease = lease
      Version = 1
      Credit = None
      PrimaryWorkId = None
      EverClaimed = false
      AncillaryMessages = Set.empty
      DispatchCount = 0 }

let private message id subject state =
    { Id = id
      Subject = subject
      Body = "body-" + id
      State = state
      HandlerGeneration = 3
      WorkId = None }

[<Test>]
let blockingFirstBeatsEarlierTimestampAndStableFifoBreaksTies () =
    let a = stage "A" [] |> fun s -> { s with AvailableAt = 200L }
    let b = { stage "B" [ "A" ] with State = WorkState.Blocked }
    let c = { stage "C" [] with AvailableAt = 10L; AvailableSequence = 1L }
    let d = { stage "D" [] with AvailableAt = 10L; AvailableSequence = 2L }
    let result = view (snapshot [ d; c; b; a ])
    Assert.That(nextIds result, Is.EqualTo(box [ "A"; "C"; "D" ]))
    Assert.That(blockedIds result, Does.Contain("B"))
    Assert.That((result.QueueHealth |> known).Runnable, Is.EqualTo(3))

[<Test>]
let wakeAndReadOnlyYieldMoveStageBehindEarlierAvailability () =
    let a = stage "A" []
    let blocked = { stage "B" [ "A" ] with State = WorkState.Blocked }
    let c = { stage "C" [] with AvailableAt = 100L; AvailableSequence = 1L }
    let before = view (snapshot [ a; blocked; c ])
    Assert.That(blockedIds before, Does.Contain("B"))
    let doneA = { a with State = WorkState.Completed }
    let requeued = { blocked with State = WorkState.Available
                                  AvailableAt = 101L
                                  AvailableSequence = 8L }
    let after = view (snapshot [ doneA; requeued; c ])
    Assert.That(nextIds after, Is.EqualTo(box [ "C"; "B" ]))
    Assert.That(blockedIds after, Is.Empty)

[<Test>]
let multiStageHardCycleShowsExactTraversableStagePathAndNoNextWork () =
    let a = { stage "A" [ "B" ] with State = WorkState.Blocked }
    let b = { stage "B" [ "A" ] with State = WorkState.Blocked }
    let result = view (snapshot [ a; b ])
    let cycles = result.Cycles |> known
    Assert.That(cycles.Length, Is.EqualTo(1))
    Assert.That(cycles.Head.ExactStagePath, Is.EqualTo(box [ "A"; "B"; "A" ]))
    Assert.That(cycles.Head.WitnessEdges,
                Is.EqualTo(box [ ("A", "B"); ("B", "A") ]))
    Assert.That(nextIds result, Is.Empty)
    Assert.That(blockedIds result, Is.EquivalentTo([ "A"; "B" ]))

[<Test>]
let selfLoopAlwaysAppearsAsHardCycle () =
    let result = view (snapshot [ { stage "A" [ "A" ] with State = WorkState.Blocked } ])
    Assert.That((result.Cycles |> known).Head.ExactStagePath, Is.EqualTo(box [ "A"; "A" ]))
    Assert.That(nextIds result, Is.Empty)

[<Test>]
let aWaivedCircularDependencyIsStillNotPresentedAsRunnable () =
    let cycle =
        { stage "A" [ "A" ] with WaivedDependencies = Map.ofList [ "A", "waiver" ] }
    // The accepted core may consider a waiver satisfied; a read-only UI must
    // not accidentally turn the underlying SCC into a green runnable row.
    let result = view (snapshot [ cycle ])
    Assert.That(nextIds result, Is.Empty)
    Assert.That(blockedIds result, Is.EqualTo(box [ "A" ]))

[<Test>]
let uncoveredQueueIsUnknownRatherThanAnEmptyNextList () =
    let result = view { snapshot [] with QueueCovered = false }
    unknown result.Next
    unknown result.Blocked
    unknown result.QueueHealth
    let lines = renderPreview result |> String.concat "\n"
    Assert.That(lines, Does.Contain("UNKNOWN"))
    Assert.That(lines, Does.Not.Contain("Next (blocking-first FIFO)\n  (none"))

[<Test>]
let uncoveredGraphMakesBothCycleAndNextViewsUnknown () =
    let result = view { snapshot [ stage "A" [] ] with GraphCovered = false }
    unknown result.Cycles
    unknown result.Next
    unknown result.QueueHealth

[<Test>]
let missingExactPrerequisiteStageGenerationIsUnknown () =
    let result = view (snapshot [ stage "A" [ "lost-stage" ] ])
    unknown result.Cycles
    unknown result.Next
    unknown result.Blocked

[<Test>]
let missingMessageIndexIsNotAnEmptyThread () =
    let result = view { snapshot [] with MessageCovered = false }
    unknown result.Threads
    unknown result.Next

[<Test>]
let incompleteSourceLineageNeverYieldsAFalselyRunnableRow () =
    let result = view { snapshot [ stage "A" [] ] with SourceCovered = false }
    unknown result.Next
    unknown result.QueueHealth

[<Test>]
let aRoleLeadThreadExistsWithoutAnySessionOrMessage () =
    let result = view (snapshot [])
    let threads = result.Threads |> known
    let lead = threads |> List.find (fun thread -> thread.Subject = Subject.Role "Lead")
    Assert.That(lead.MessagesByCanonicalId, Is.Empty)
    Assert.That(result.Launches |> known, Is.Empty)
    let compose = previewComposer (Subject.Role "Lead") Map.empty "What is next?"
    Assert.That(compose.CanPost, Is.False)
    Assert.That(compose.SubjectTitle, Is.EqualTo("Role: Lead"))
    Assert.That(compose.DraftBody, Is.EqualTo("What is next?"))

[<Test>]
let taskAndRoleConversationsPreserveAllStatesWithoutChronologyClaims () =
    let original = snapshot []
    let items =
        [ message "a" (Subject.Role "Lead") MessageState.Pending
          message "b" (Subject.Role "Lead") (MessageState.ClaimedPrimary "L1")
          message "c" (Subject.Task "T1") (MessageState.ClaimedAncillary "L2")
          message "d" (Subject.Task "T1") (MessageState.Handled "reply") ]
    let p = { original with Messages = items |> List.map (fun m -> m.Id, m) |> Map.ofList }
    let result = view p
    let threads = result.Threads |> known
    let lead = threads |> List.find (fun t -> t.Subject = Subject.Role "Lead")
    Assert.That(lead.MessagesByCanonicalId |> List.map (fun m -> m.State),
                Is.EqualTo(box [ "Pending"; "Claimed (primary)" ]))
    let task = threads |> List.find (fun t -> t.Subject = Subject.Task "T1")
    Assert.That(task.MessagesByCanonicalId |> List.map (fun m -> m.State),
                Is.EqualTo(box [ "Claimed (ancillary)"; "Handled (reply reply)" ]))
    Assert.That(task.MessagesByCanonicalId.Head.ClaimLaunchId, Is.EqualTo(Some "L2"))
    Assert.That(p.Messages.["a"].State, Is.EqualTo(MessageState.Pending))

[<Test>]
let unknownBrowserDispatchShowsLaunchIdButNeverInventsAgentUuid () =
    let pending =
        launch "launch-1" (LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown) None
    let result = view { snapshot [] with Launches = Map.ofList [ pending.Id, pending ] }
    let only = result.Launches |> known |> List.exactlyOne
    Assert.That(only.LaunchId, Is.EqualTo("launch-1"))
    Assert.That(Option.isNone only.AgentUuid, Is.True)
    Assert.That(only.Phase, Does.Contain("unknown"))
    Assert.That(Option.isSome only.RecoveryWarning, Is.True)

[<Test>]
let agentUuidAppearsOnlyAfterVerifiedRegistration () =
    let before = launch "L" (LaunchState.SubmittedUnregistered DispatchState.Submitted) None
    let after =
        { before with
            State = LaunchState.Registered("uuid-1", "session-1")
            Lease = Some { LastSeenAt = 190L; Generation = 1; StopRequested = false } }
    let lookup (item: Launch) =
        (view { snapshot [] with Launches = Map.ofList [ "L", item ] }).Launches
        |> known |> List.exactlyOne
    Assert.That(Option.isNone (lookup before).AgentUuid, Is.True)
    Assert.That((lookup after).AgentUuid, Is.EqualTo(Some "uuid-1"))

[<Test>]
let staleHeartbeatAndStopRecoveriesRemainExplicitlyUnproven () =
    let claimed = { stage "A" [] with State = WorkState.Claimed "L" }
    let staleLease = { LastSeenAt = 100L; Generation = 1; StopRequested = false }
    let registered = launch "L" (LaunchState.Registered("agent-uuid", "session")) (Some staleLease)
    let p = { snapshot [ claimed ] with Launches = Map.ofList [ "L", registered ] }
    let current = view p
    match (current.ActiveAndStalled |> known).Head.State with
    | RowState.StaleHeartbeat "L" -> ()
    | unexpected -> Assert.Fail(sprintf "Expected stale heartbeat, got %A" unexpected)
    let stopped = { registered with Lease = Some { staleLease with StopRequested = true } }
    let stoppedResult = view { p with Launches = Map.ofList [ "L", stopped ] }
    match (stoppedResult.ActiveAndStalled |> known).Head.State with
    | RowState.StopRecovery "L" -> ()
    | unexpected -> Assert.Fail(sprintf "Expected STOP recovery, got %A" unexpected)

[<Test>]
let missingLeaseOrClaimedLaunchIsUnknownNotActive () =
    let claimed = { stage "A" [] with State = WorkState.Claimed "L" }
    let absent = view (snapshot [ claimed ])
    match (absent.ActiveAndStalled |> known).Head.State with
    | RowState.Uncertain _ -> ()
    | unexpected -> Assert.Fail(sprintf "Unknown launch appeared active: %A" unexpected)
    let registered = launch "L" (LaunchState.Registered("uuid", "session")) None
    let missingLease = view { snapshot [ claimed ] with Launches = Map.ofList [ "L", registered ] }
    match (missingLease.ActiveAndStalled |> known).Head.State with
    | RowState.Uncertain _ -> ()
    | unexpected -> Assert.Fail(sprintf "Missing lease appeared active: %A" unexpected)

[<Test>]
let integrationGateAndSourceRecoveryRemainVisibleHoldsNotNext () =
    let integration =
        { stage "I" [] with Gate = GateState.IntegrationGateHold(3, "original-push") }
    let source =
        { stage "S" [] with Gate = GateState.SourceRecoveryBlocked "journal uncertain" }
    let result = view (snapshot [ integration; source ])
    Assert.That(nextIds result, Is.Empty)
    let blocked = result.Blocked |> known
    Assert.That(blocked.Length, Is.EqualTo(2))
    Assert.That(blocked |> List.forall (fun row ->
        match row.State with
        | RowState.RecoveryHold _ -> true
        | _ -> false), Is.True)

[<Test>]
let capacityUnknownCannotBeMistakenForAvailableTokens () =
    let result =
        projectDashboard "P" Map.empty (Ok(snapshot []))
            (Error "account ledger not verified") None 50L
    unknown result.Capacity
    let lines = renderPreview result |> String.concat "\n"
    Assert.That(lines, Does.Contain("Account-global capacity\n  UNKNOWN"))

[<Test>]
let accountOccupancyCountsUnreleasedTokensAndFlagsOvercommit () =
    let occupied =
        { Contracts.emptyAccount 1 with
            Tokens = Map.ofList [ "L1", false; "L2", false; "released", true ] }
    let result = projectDashboard "P" Map.empty (Ok(snapshot [])) (Ok occupied) None 50L
    let capacity = result.Capacity |> known
    Assert.That(capacity.Occupied, Is.EqualTo(2))
    Assert.That(capacity.Free, Is.EqualTo(0))
    Assert.That(capacity.Overcommitted, Is.True)

[<Test>]
let projectIdentityMismatchIsUnknownNotAnotherProjectsWork () =
    let alien = { stage "A" [] with ProjectId = "Q" }
    let result = view (snapshot [ alien ])
    unknown result.Next
    unknown result.QueueHealth

[<Test>]
let rendererUsesTaskTitlesAndExplicitReadOnlyLabel () =
    let p = snapshot [ stage "A" [] ]
    let result =
        projectDashboard "P" (Map.ofList [ "task-A", "Review fixes" ])
            (Ok p) (Ok account) None 50L
    let output = renderPreview result |> String.concat "\n"
    Assert.That(output, Does.Contain("TASK-FIRST PREVIEW - READ ONLY"))
    Assert.That(output, Does.Contain("Review fixes"))
    Assert.That(output, Does.Contain("Role: Lead"))
    Assert.That(output, Does.Not.Contain("Post message now"))


[<Test>]
let claimedHardEdgeMutationNeverProjectsActiveEvenWithFreshLease () =
    let a = { stage "A" [] with State = WorkState.Claimed "L" }
    let b = stage "B" []
    let registered =
        launch "L" (LaunchState.Registered("agent-uuid", "session-1"))
            (Some { LastSeenAt = 195L; Generation = 1; StopRequested = false })
    let original =
        { snapshot [ a; b ] with Launches = Map.ofList [ "L", registered ] }
    // Use the accepted WQ01/02 transition, not an impossible hand-rolled gate.
    let mutated =
        match Contracts.setHardPrerequisite original.Version
                "claimed-hard-edge" "A" "B" 200L original with
        | Ok project -> project
        | Error reason -> failwithf "Canonical hard-edge mutation rejected: %A" reason
    Assert.That(mutated.Works.["A"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(mutated.Works.["A"].Gate,
                Is.EqualTo(GateState.DependencyGatePending mutated.GraphVersion))
    for at in [ Some 200L; Some 400L; None ] do
        let result =
            projectDashboard "P" Map.empty (Ok mutated) (Ok account) at 50L
        Assert.That(nextIds result, Does.Not.Contain("A"))
        let claimedRow =
            result.ActiveAndStalled |> known
            |> List.find (fun item -> item.WorkId = "A")
        match claimedRow.State with
        | RowState.RecoveryHold reason ->
            Assert.That(reason, Does.Contain("DependencyGatePending"))
        | unexpected -> Assert.Fail(sprintf "Claimed dependency gate incorrectly healthy: %A" unexpected)
        Assert.That(blockedIds result, Does.Contain("A"))
        Assert.That((result.QueueHealth |> known).Blocked, Is.GreaterThanOrEqualTo(1))
        let output = renderPreview result |> String.concat "\n"
        Assert.That(output, Does.Contain("HELD"))
        Assert.That(output, Does.Not.Contain("ACTIVE claimed by L"))

[<Test>]
let everyNonclearClaimedGateIsHeldAcrossClockAndLeaseVariants () =
    let baseStage = { stage "A" [] with State = WorkState.Claimed "L" }
    let gates =
        [ GateState.DependencyGatePending 9
          GateState.IntegrationGateHold(9, "original-push")
          GateState.ExternalEffectObservedPendingGate(9, "original-push", "sha")
          GateState.SourceRecoveryBlocked "journal not verified" ]
    let lease =
        { LastSeenAt = 180L; Generation = 1; StopRequested = false }
    let registered =
        launch "L" (LaunchState.Registered("uuid-1", "session-1")) (Some lease)
    for gate in gates do
        for at, currentLease in
            [ Some 200L, lease
              Some 400L, lease
              None, lease
              Some 200L, { lease with StopRequested = true } ] do
            let p =
                { snapshot [ { baseStage with Gate = gate } ] with
                    Launches = Map.ofList [ "L", { registered with Lease = Some currentLease } ] }
            let dashboard =
                projectDashboard "P" Map.empty (Ok p) (Ok account) at 50L
            match (dashboard.ActiveAndStalled |> known |> List.exactlyOne).State with
            | RowState.RecoveryHold _ -> ()
            | unexpected -> Assert.Fail(sprintf "Gate %A at %A was %A" gate at unexpected)
            Assert.That(blockedIds dashboard, Is.EqualTo(box [ "A" ]))
            Assert.That((dashboard.QueueHealth |> known).Blocked, Is.EqualTo(1))
            Assert.That(nextIds dashboard, Is.Empty)

[<Test>]
let missingObservationClockOrHeartbeatThresholdCannotAssertActive () =
    let claimed = { stage "A" [] with State = WorkState.Claimed "L" }
    let registered =
        launch "L" (LaunchState.Registered("uuid-1", "session-1"))
            (Some { LastSeenAt = 190L; Generation = 1; StopRequested = false })
    let p = { snapshot [ claimed ] with Launches = Map.ofList [ "L", registered ] }
    for at, threshold in [ None, 50L; Some 200L, 0L ] do
        let result =
            projectDashboard "P" Map.empty (Ok p) (Ok account) at threshold
        match (result.ActiveAndStalled |> known |> List.exactlyOne).State with
        | RowState.Uncertain _ -> ()
        | unexpected -> Assert.Fail(sprintf "Unverified clock projected as %A" unexpected)
        let launchRow = result.Launches |> known |> List.exactlyOne
        Assert.That(launchRow.RecoveryWarning.IsSome, Is.True)

[<Test>]
let mismatchedMessageMapKeyAndCanonicalIdFailClosed () =
    let original = snapshot []
    let canonical = message "canonical-message" (Subject.Role "Lead") MessageState.Pending
    let corrupted =
        { original with Messages = Map.ofList [ "wrong-index-key", canonical ] }
    let result = view corrupted
    unknown result.Threads
    unknown result.Next
    unknown result.QueueHealth
    Assert.That(renderPreview result |> String.concat "\n",
                Does.Not.Contain("canonical-message [Pending]"))

[<Test>]
let duplicateOrBlankMessageIdentitiesAndSubjectsAreUnknown () =
    let msg = message "same-message" (Subject.Role "Lead") MessageState.Pending
    let duplicate =
        { snapshot [] with Messages = Map.ofList [ "one", msg; "two", msg ] }
    let invalidIdMessage = message " " (Subject.Role "Lead") MessageState.Pending
    let blankId =
        { snapshot [] with Messages = Map.ofList [ " ", invalidIdMessage ] }
    let invalidSubjectMessage = message "id" (Subject.Task " ") MessageState.Pending
    let blankSubject =
        { snapshot [] with Messages = Map.ofList [ "id", invalidSubjectMessage ] }
    for p in [ duplicate; blankId; blankSubject ] do
        let dashboard = view p
        unknown dashboard.Threads
        unknown dashboard.Next

[<Test>]
let missingOrReleasedGlobalTokenNeverProjectsFreeCapacityForRegisteredLaunch () =
    let registered =
        launch "L" (LaunchState.Registered("uuid-1", "session-1"))
            (Some { LastSeenAt = 190L; Generation = 1; StopRequested = false })
    let p = { snapshot [] with Launches = Map.ofList [ "L", registered ] }
    let snapshots =
        [ account
          { account with Tokens = Map.ofList [ "L", true ] } ]
    for globalSnapshot in snapshots do
        let result =
            projectDashboard "P" Map.empty (Ok p) (Ok globalSnapshot)
                (Some 200L) 50L
        unknown result.Capacity
        let lines = renderPreview result |> String.concat "\n"
        Assert.That(lines, Does.Contain("Account-global capacity\n  UNKNOWN"))
        Assert.That(lines, Does.Not.Contain("0/3 occupied; 3 free"))

[<Test>]
let tokenIdentityMatchWithoutCrossLedgerHighWaterStillCannotPromiseFreeCapacity () =
    let registered =
        launch "L" (LaunchState.Registered("uuid-1", "session-1"))
            (Some { LastSeenAt = 190L; Generation = 1; StopRequested = false })
    let p = { snapshot [] with Launches = Map.ofList [ "L", registered ] }
    let accountWithReservation =
        { account with Tokens = Map.ofList [ "L", false ] }
    let dashboard =
        projectDashboard "P" Map.empty (Ok p) (Ok accountWithReservation) (Some 200L) 50L
    unknown dashboard.Capacity
    Assert.That(renderPreview dashboard |> String.concat "\n",
                Does.Contain("high-water join unverified"))

[<Test>]
let duplicateProjectAdmissionIdentityCannotMaskAccountCapacity () =
    let a = launch "L" LaunchState.ReservedUnregistered None
    let otherLaunch = launch "M" LaunchState.ReservedUnregistered None
    let b = { otherLaunch with AccountTokenId = a.AccountTokenId }
    let p = { snapshot [] with Launches = Map.ofList [ "L", a; "M", b ] }
    let reservations =
        { account with Tokens = Map.ofList [ "L", false; "M", false ] }
    let result =
        projectDashboard "P" Map.empty (Ok p) (Ok reservations) (Some 200L) 50L
    unknown result.Capacity

[<Test>]
let unavailableProjectSnapshotCannotProjectJoinedGlobalFreeTokens () =
    let result =
        projectDashboard "P" Map.empty (Error "head unverified") (Ok account)
            (Some 200L) 50L
    unknown result.Capacity
