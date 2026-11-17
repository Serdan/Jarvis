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
let unjoinedAccountSnapshotCannotCertifyOvercommitOrFreeTokens () =
    let occupied =
        { Contracts.emptyAccount 1 with
            Tokens = Map.ofList [ "L1", false; "L2", false; "released", true ] }
    let result = projectDashboard "P" Map.empty (Ok(snapshot [])) (Ok occupied) None 50L
    // Even account-only token accounting does not prove the cross-ledger
    // head/high-water join required before displaying safe free capacity.
    unknown result.Capacity
    let preview = renderPreview result |> String.concat "\n"
    Assert.That(preview, Does.Contain("cross-ledger heads and high-water join unverified"))
    Assert.That(preview, Does.Not.Contain("2/1 occupied"))

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


// Reviewer4 ChangesRequested seq3686: use the accepted *real* hard-edge
// mutation, not a fabricated tuple of claimed work plus dependency gate.
[<Test>]
let claimedDependencyGateMutationIsHeldAtFreshAndStaleClocks () =
    let claimed = { stage "A" [] with State = WorkState.Claimed "L" }
    let prerequisite = stage "B" []
    let lease = { LastSeenAt = 190L; Generation = 1; StopRequested = false }
    let registered = launch "L" (LaunchState.Registered("uuid-L", "session-L")) (Some lease)
    let p =
        { snapshot [ claimed; prerequisite ] with
            Launches = Map.ofList [ "L", registered ] }
    let gated =
        match Contracts.setHardPrerequisite p.Version "add-hard-edge-reviewer4" "A" "B" 200L p with
        | Ok next -> next
        | Error error -> failwithf "Accepted canonical setHardPrerequisite rejected fixture: %A" error
    Assert.That(gated.Works.["A"].State, Is.EqualTo(WorkState.Claimed "L"))
    Assert.That(gated.Works.["A"].Gate, Is.EqualTo(GateState.DependencyGatePending gated.GraphVersion))
    let accountWithReservation = { account with Tokens = Map.ofList [ "L", false ] }
    for clock in [ Some 200L; Some 260L; None ] do
        let result = projectDashboard "P" Map.empty (Ok gated) (Ok accountWithReservation) clock 50L
        let claimedRow = result.ActiveAndStalled |> known |> List.find (fun row -> row.WorkId = "A")
        match claimedRow.State with
        | RowState.RecoveryHold reason ->
            Assert.That(reason, Does.Contain("Claim retained by L"))
            Assert.That(reason, Does.Contain("DependencyGatePending"))
        | state -> Assert.Fail(sprintf "Claimed work with non-clear gate appeared healthy: %A" state)
        Assert.That(blockedIds result, Does.Contain("A"))
        Assert.That((result.QueueHealth |> known).Blocked, Is.EqualTo(1))
        Assert.That(nextIds result, Does.Not.Contain("A"))
        let output = renderPreview result |> String.concat "\n"
        Assert.That(output, Does.Contain("HELD"))
        Assert.That(output, Does.Not.Contain("ACTIVE claimed by L"))
        unknown result.Capacity

// The gate itself takes precedence over lease/clock health: STOP, stale
// heartbeat and a missing clock must not relabel a held claim as ACTIVE.
[<Test>]
let everyNonclearClaimedGateIsHeldAndCountedAcrossLeaseStates () =
    let claimed = { stage "A" [] with State = WorkState.Claimed "L" }
    let registered =
        launch "L" (LaunchState.Registered("uuid-L", "session-L"))
            (Some { LastSeenAt = 100L; Generation = 1; StopRequested = false })
    let p = { snapshot [ claimed ] with Launches = Map.ofList [ "L", registered ] }
    let accountWithReservation = { account with Tokens = Map.ofList [ "L", false ] }
    let cases =
        [ GateState.DependencyGatePending 2, "DependencyGatePending"
          GateState.IntegrationGateHold(2, "original-push"), "IntegrationGateHold"
          GateState.ExternalEffectObservedPendingGate(2, "original-push", "sha"), "External effect"
          GateState.SourceRecoveryBlocked "journal unknown", "SourceRecoveryBlocked" ]
    for gate, expectedReason in cases do
        for now in [ Some 101L; Some 260L; None ] do
            let projected = { p with Works = Map.ofList [ "A", { claimed with Gate = gate } ] }
            let result =
                projectDashboard "P" Map.empty (Ok projected) (Ok accountWithReservation) now 50L
            match (result.ActiveAndStalled |> known |> List.exactlyOne).State with
            | RowState.RecoveryHold reason ->
                Assert.That(reason, Does.Contain("Claim retained by L"))
                Assert.That(reason, Does.Contain(expectedReason))
            | state -> Assert.Fail(sprintf "Non-clear gate %A rendered as %A" gate state)
            Assert.That(blockedIds result, Is.EqualTo(box [ "A" ]))
            Assert.That((result.QueueHealth |> known).Blocked, Is.EqualTo(1))
            Assert.That(nextIds result, Is.Empty)

[<Test>]
let missingClockOrInvalidLivenessThresholdCanNeverShowClaimActive () =
    let claimed = { stage "A" [] with State = WorkState.Claimed "L" }
    let registered =
        launch "L" (LaunchState.Registered("uuid-L", "session-L"))
            (Some { LastSeenAt = 190L; Generation = 1; StopRequested = false })
    let p = { snapshot [ claimed ] with Launches = Map.ofList [ "L", registered ] }
    let accountWithReservation = { account with Tokens = Map.ofList [ "L", false ] }
    for clock, threshold in [ None, 50L; Some 200L, 0L; Some 200L, -1L ] do
        let result = projectDashboard "P" Map.empty (Ok p) (Ok accountWithReservation) clock threshold
        match (result.ActiveAndStalled |> known |> List.exactlyOne).State with
        | RowState.Uncertain _ -> ()
        | state -> Assert.Fail(sprintf "Clock/threshold uncertainty showed %A" state)
        Assert.That((result.Launches |> known |> List.exactlyOne).RecoveryWarning.IsSome, Is.True)

[<Test>]
let messageIndexMismatchDuplicateCanonicalIdsAndBlankSubjectsAreUnknown () =
    let canonical = message "canonical-id" (Subject.Role "Lead") MessageState.Pending
    let badIndexes =
        [ Map.ofList [ "wrong-map-key", canonical ]
          Map.ofList [ "first", canonical; "second", canonical ]
          Map.ofList [ "", { canonical with Id = "" } ]
          Map.ofList [ "canonical-id", { canonical with Subject = Subject.Task "" } ]
          Map.ofList [ "canonical-id", { canonical with Subject = Subject.Role "   " } ] ]
    for messages in badIndexes do
        let result = view { snapshot [] with Messages = messages; MessageCovered = true }
        unknown result.Threads
        let output = renderPreview result |> String.concat "\n"
        Assert.That(output, Does.Contain("Message index/identity incomplete"))
        Assert.That(output, Does.Not.Contain("canonical-id [Pending]"))
    let valid = view { snapshot [] with Messages = Map.ofList [ canonical.Id, canonical ] }
    Assert.That((valid.Threads |> known).Length, Is.GreaterThan(0))

[<Test>]
let missingReleasedOrDuplicateAccountTokensCannotLookLikeFreeCapacity () =
    let registered = launch "L" (LaunchState.Registered("uuid-L", "session-L")) None
    let project = { snapshot [] with Launches = Map.ofList [ "L", registered ] }
    let missing = projectDashboard "P" Map.empty (Ok project) (Ok account) (Some 200L) 50L
    unknown missing.Capacity
    let output = renderPreview missing |> String.concat "\n"
    Assert.That(output, Does.Contain("Account-global capacity\n  UNKNOWN"))
    Assert.That(output, Does.Not.Contain("0/3 occupied; 3 free"))
    let released = { account with Tokens = Map.ofList [ "L", true ] }
    unknown (projectDashboard "P" Map.empty (Ok project) (Ok released) None 50L).Capacity
    let noAdmission =
        { project with
            Launches = Map.ofList [ "L", { registered with AccountTokenId = " " } ] }
    unknown (projectDashboard "P" Map.empty (Ok noAdmission) (Ok account) None 50L).Capacity
    let another = launch "L2" LaunchState.ReservedUnregistered None
    let duplicate =
        { project with
            Launches =
                Map.ofList [ "L", registered; "L2", { another with AccountTokenId = registered.AccountTokenId } ] }
    let tokens = { account with Tokens = Map.ofList [ "L", false; "L2", false ] }
    unknown (projectDashboard "P" Map.empty (Ok duplicate) (Ok tokens) None 50L).Capacity
    unknown (projectDashboard "P" Map.empty (Ok { project with QueueCovered = false })
                (Ok tokens) None 50L).Capacity

[<Test>]
let matchedButUnjoinedLiveTerminalAndUnrelatedAccountSnapshotsRemainUnknown () =
    let registered = launch "L" (LaunchState.Registered("uuid-L", "session-L")) None
    let project = { snapshot [] with Launches = Map.ofList [ "L", registered ]; Version = 41 }
    let accountWithOtherProject =
        { account with Version = 41; Tokens = Map.ofList [ "L", false; "other-project", false ] }
    // IDs, token values AND numeric versions match, but no independently
    // verified cross-ledger head/high-water fence accompanies these DTOs.
    let matched =
        projectDashboard "P" Map.empty (Ok project) (Ok accountWithOtherProject) None 50L
    unknown matched.Capacity
    Assert.That((matched.Launches |> known).Length, Is.EqualTo(1))
    let preview = renderPreview matched |> String.concat "\n"
    Assert.That(preview, Does.Contain("cross-ledger heads and high-water join unverified"))
    Assert.That(preview, Does.Not.Contain("2/3 occupied; 1 free"))
    let terminal = { registered with State = LaunchState.TerminalPendingRelease "terminal-proof" }
    let fullyReleased = { project with Version = 42; Launches = Map.ofList [ "L", terminal ] }
    let accountReleased = { account with Version = 42; Tokens = Map.ofList [ "L", true ] }
    let completed =
        projectDashboard "P" Map.empty (Ok fullyReleased) (Ok accountReleased) None 50L
    unknown completed.Capacity
    // Even when this project has no launch, other projects may occupy
    // tokens or mutate account state between two independent reads.
    let onlyElsewhere = { snapshot [] with Version = 42 }
    unknown (projectDashboard "P" Map.empty (Ok onlyElsewhere) (Ok accountWithOtherProject)
                None 50L).Capacity
    unknown (view (snapshot [])).Capacity
    unknown (projectDashboard "P" Map.empty (Error "project ledger unknown") (Ok account)
                None 50L).Capacity


[<Test>]
let terminalRendererEscapesUntrustedMessageControlCharacters () =
    let original = message "message" (Subject.Role "Lead") (MessageState.Handled "\u001b[31mreply")
    let item = { original with Body = "body\u001b[32mNEXT\n  forged green row" }
    let project = { snapshot [] with Messages = Map.ofList [ item.Id, item ] }
    let result = view project
    let rendered = renderPreview result |> String.concat "\n"
    Assert.That(rendered, Does.Contain("forged green row"))
    let remainingEscapeOffsets =
        rendered
        |> Seq.indexed
        |> Seq.choose (fun (offset, ch) -> if int ch = 27 then Some offset else None)
        |> Seq.toList
    Assert.That(remainingEscapeOffsets, Is.Empty, "No terminal ESC codepoint may survive preview encoding")
    Assert.That(rendered, Does.Not.Contain("NEXT\n  forged"))
    Assert.That((result.Threads |> known).Length, Is.GreaterThan(0))
    // Source DTO retains the canonical body; only terminal preview is escaped.
    Assert.That((result.Threads |> known |> List.find (fun r -> r.Subject = Subject.Role "Lead"))
                    .MessagesByCanonicalId.Head.Body, Is.EqualTo(item.Body))


[<Test>]
let twoCloneStaleReorderedAndMatchedVersionsCannotForgeAccountJoin () =
    let registered = launch "L" (LaunchState.Registered("uuid-L", "session-L")) None
    // Two independent clone views retain a matching launch/token pair, but
    // neither DTO carries the Git-ledger head or a verified joined high-water.
    let cloneAProject = { snapshot [] with Version = 12; Launches = Map.ofList [ "L", registered ] }
    let cloneAAccount =
        { account with Version = 12; Tokens = Map.ofList [ "L", false; "other-project", false ] }
    let cloneBProject = { cloneAProject with Version = 13 }
    let cloneBAccount = { cloneAAccount with Version = 13 }
    let readOrders =
        [ "both from clone A", cloneAProject, cloneAAccount
          "project B / stale account A", cloneBProject, cloneAAccount
          "stale project A / account B", cloneAProject, cloneBAccount
          "both from clone B", cloneBProject, cloneBAccount ]
    for (label, projectRead, accountRead) in readOrders do
        let result = projectDashboard "P" Map.empty (Ok projectRead) (Ok accountRead) None 50L
        match result.Capacity with
        | Evidence.Unknown reason ->
            Assert.That(reason, Does.Contain("cross-ledger heads and high-water join unverified"), label)
        | Evidence.Known capacity ->
            Assert.Fail(sprintf "%s manufactured %d known free tokens from unjoined views" label capacity.Free)
        Assert.That((result.Launches |> known).Length, Is.EqualTo(1), label)


[<Test>]
let terminalPreviewSanitizesTitlesIdsCyclePathsAndUnknownReasonsWithoutChangingDto () =
    let injection = "\u001b[31mFORGED\nNEW ROW\u2028LINE\u2029BLOCK\u200eFORMAT"
    let original = snapshot [ stage "A" [] ]
    let projected =
        projectDashboard "P" (Map.ofList [ "task-A", injection ])
            (Ok original) (Ok account) None 50L
    let firstRow = (projected.Next |> known).Head
    Assert.That(firstRow.SubjectTitle, Is.EqualTo(injection),
                "The raw projection retains canonical text; escaping is presentation-only")
    let forgedRow =
        { firstRow with WorkId = injection
                        ProjectId = injection
                        SubjectTitle = injection
                        Kind = injection
                        HardBlockers = [ injection ]
                        State = RowState.RecoveryHold injection }
    let msg =
        { MessageId = injection
          Body = injection
          State = injection
          ClaimLaunchId = Some injection
          HandlerGeneration = 1
          WorkId = Some injection }
    let threads =
        [ { Subject = Subject.Role injection
            SubjectTitle = "Role: " + injection
            MessagesByCanonicalId = [ msg ] } ]
    let launches =
        [ { LaunchId = injection
            RoleId = injection
            Phase = injection
            AgentUuid = Some injection
            RecoveryWarning = Some injection } ]
    let health = projected.QueueHealth |> known
    let preview =
        { projected with
            Next = Evidence.Known [ forgedRow ]
            ActiveAndStalled = Evidence.Known [ forgedRow ]
            Blocked = Evidence.Known [ forgedRow ]
            Cycles = Evidence.Known
                [ { Members = [ injection ]
                    ExactStagePath = [ injection; injection ]
                    WitnessEdges = [ injection, injection ] } ]
            Threads = Evidence.Known threads
            Launches = Evidence.Known launches
            Capacity = Evidence.Unknown injection
            QueueHealth = Evidence.Known { health with ProjectId = injection } }
    let rendered = renderPreview preview
    let unsafeCharacter ch =
        Char.IsControl ch
        || match Char.GetUnicodeCategory ch with
           | Globalization.UnicodeCategory.Format
           | Globalization.UnicodeCategory.LineSeparator
           | Globalization.UnicodeCategory.ParagraphSeparator -> true
           | _ -> false
    for line in rendered do
        Assert.That(line |> Seq.exists unsafeCharacter, Is.False,
                    sprintf "Untrusted terminal control escaped sanitization: %A" line)
    let text = rendered |> String.concat "\n"
    Assert.That(text, Does.Contain("FORGED"))
    Assert.That(text, Does.Not.Contain("FORGED\nNEW ROW"))
    Assert.That((projected.Next |> known).Head.SubjectTitle, Is.EqualTo(injection))


[<Test>]
let leadPriorityVisibleInNextSelectionWithHardBlockingFirst () =
    let a = { stage "A" [] with AvailableAt = 100L }
    let b = { stage "B" [] with AvailableAt = 300L }
    let c = { stage "C" [] with AvailableAt = 200L }
    let held = { stage "D" ["B"] with State = WorkState.Blocked }
    let original = snapshot [a; b; c; held]
    let adjusted =
        original
        |> fun p -> Contracts.assignPriority p.Version "wq14-tui-A" "A" 10 "Ordinary low" p |> (function Ok v -> v | Error e -> failwithf "%A" e)
        |> fun p -> Contracts.assignPriority p.Version "wq14-tui-C" "C" 40 "Ordinary high" p |> (function Ok v -> v | Error e -> failwithf "%A" e)
        |> fun p -> Contracts.assignPriority p.Version "wq14-tui-B" "B" -20 "Hard blocker" p |> (function Ok v -> v | Error e -> failwithf "%A" e)
    Assert.That(nextIds (view adjusted), Is.EqualTo(box ["B"; "C"; "A"]))
    Assert.That(blockedIds (view adjusted), Does.Contain("D"))
