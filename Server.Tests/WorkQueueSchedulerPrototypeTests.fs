module ServerWorkQueueSchedulerPrototypeTests

open System
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Jarvis.WorkQueues.InertScheduler
open Jarvis.WorkQueues.SchedulerDispatchPrototype

let private unwrap = function
    | Ok x -> x
    | Error e -> failwithf "Unexpected WQ08 result %A" e

let private roleSnapshot =
    { Id = "Worker"
      Version = 3
      Queues = Set.singleton "implementation"
      InstructionsDigest = "sha256:worker-config" }

let private configuredRole =
    { RoleId = "Worker"
      Version = 3
      Instructions = "Read your inbox and resume."
      QueueSubscriptions = Set.singleton "implementation"
      Enabled = true }

let private available : WorkStage =
    { Id = "work-1"
      ProjectId = "alpha"
      SubjectId = "task-1"
      Kind = WorkKind.Implementation
      QueueId = "implementation"
      StageGeneration = 2
      State = WorkState.Available
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.empty
      WaivedDependencies = Map.empty
      AvailableAt = 1L
      AvailableSequence = 1L
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private provenance =
    { AccountHead = "account-head-1"
      ProjectHead = "project-head-1"
      SourceRef = Some "refs/heads/feature/task-1"
      SourceCommit = Some "1234567890abcdef1234567890abcdef12345678" }

let private setup () =
    let account = emptyAccount 1
    let project = { emptyProject (Map.ofList ["Worker", roleSnapshot.Queues]) with
                        Works = Map.ofList [available.Id, available] }
    let snapshot =
        { Account = account
          Projects = [{ Id = "alpha"; State = project; Roles = [roleSnapshot] }]
          CompleteProjectSet = true
          CompleteRoleVersions = true }
    // Deliberately only the internal hypothetical fixture seam can produce a
    // positive plan until WQ04 same-head source authority is available.
    let plan =
        match planHypotheticalFixtureOnly snapshot "launch-1" |> unwrap with
        | Some found -> found
        | None -> failwith "Expected a cap=1 Worker plan"
    let reserved = reserveAccount plan account |> unwrap
    let admitted = admitAfterReserve plan reserved project |> unwrap
    let demand : RankedRoleDemand =
        { WorkId = available.Id
          ProjectId = available.ProjectId
          StageGeneration = available.StageGeneration
          QueueId = available.QueueId
          RoleId = "Worker"
          RoleVersion = 3
          BlocksUnfinishedStage = false
          AvailableAt = available.AvailableAt
          AvailableSequence = available.AvailableSequence }
    plan, demand, reserved, admitted

type private Probe(initial: Project, response: OriginalBrowserOutcome) =
    let mutable entry: OriginalBrowserJournalEntry option = None
    let mutable current = initial
    let mutable sends = 0
    let mutable markFailure: string option = None
    let mutable recordFailure: string option = None
    let mutable projectFailure: string option = None

    member _.Current with get () = current and set x = current <- x
    member _.Sends = sends
    member _.Entry with get () = entry and set x = entry <- x
    member _.FailMark text = markFailure <- Some text
    member _.FailRecord text = recordFailure <- Some text
    member _.FailProject text = projectFailure <- Some text

    member _.Ports : SchedulerDispatchPorts =
        { TryMarkOriginal = fun intent ->
              match markFailure with
              | Some problem -> Error problem
              | None ->
                  match entry with
                  | Some prior -> Ok(OriginalMarkResult.Existing prior)
                  | None ->
                      entry <- Some { Intent = intent; Outcome = None }
                      Ok OriginalMarkResult.Marked
          FindOriginal = fun _ -> Ok entry
          SubmitOriginal = fun _ ->
              sends <- sends + 1
              response
          RecordOriginalOutcome = fun intent outcome ->
              match recordFailure with
              | Some problem -> Error problem
              | None ->
                  match entry with
                  | Some prior when prior.Intent = intent ->
                      entry <- Some { prior with Outcome = Some outcome }
                      Ok ()
                  | _ -> Error "Original marker missing"
          RecordProjectDispatch = fun plan outcome ->
              match projectFailure with
              | Some problem -> Error problem
              | None ->
                  match recordDispatchOutcome plan outcome current with
                  | Error e -> Error(sprintf "%A" e)
                  | Ok updated ->
                      current <- updated
                      Ok () }

[<Test>]
let cap_one_direct_worker_submit_and_first_connect_same_execution_retry () =
    let plan, demand, account, admitted = setup ()
    Assert.That(plan.Launch.RoleId, Is.EqualTo("Worker"))
    Assert.That(account.Tokens.Count, Is.EqualTo(1))
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "browser-receipt-1")
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.Submitted(_, receipt) ->
        Assert.That(receipt, Is.EqualTo("browser-receipt-1"))
    | other -> Assert.Fail(sprintf "Expected submitted: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))
    // A second invocation sees the submitted project state and original
    // browser journal; it repairs/replays the original CAS without resending.
    match runOriginal probe.Ports plan demand configuredRole provenance account probe.Current with
    | SchedulerDispatchResult.Submitted(_, receipt) ->
        Assert.That(receipt, Is.EqualTo("browser-receipt-1"))
    | other -> Assert.Fail(sprintf "Expected receipt replay: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))
    let uuid = "e65c295e-4e2a-422e-a9cf-d0be67737ef6"
    let registered, first =
        initializeFirstConnect probe.Current.Version "first-connect-op" plan.Launch.Id
            "session-from-actual-execution" 10L (fun () -> uuid) roleSnapshot probe.Current
        |> unwrap
    let _, repeated =
        initializeFirstConnect registered.Version "first-connect-op" plan.Launch.Id
            "session-from-actual-execution" 11L
            (fun () -> failwith "retry must not allocate another UUID") roleSnapshot registered
        |> unwrap
    Assert.That(first.AgentUuid, Is.EqualTo(uuid))
    Assert.That(repeated.AgentUuid, Is.EqualTo(uuid))
    match initializeFirstConnect registered.Version "other-session-op" plan.Launch.Id
            "another-session" 12L (fun () -> Guid.NewGuid().ToString()) roleSnapshot registered with
    | Error Rejection.AlreadyRegistered -> ()
    | other -> Assert.Fail(sprintf "Expected other-session rejection: %A" other)

[<Test>]
let browser_submit_unknown_preserves_original_and_never_sends_twice () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Unknown "browser transport closed")
    let initial = runOriginal probe.Ports plan demand configuredRole provenance account admitted
    match initial with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        let packet = manualRepairPacket issue
        Assert.That(packet, Does.Contain(plan.DispatchOperationId))
        Assert.That(packet, Does.Contain("account-head-1"))
        Assert.That(packet, Does.Contain("project-head-1"))
        Assert.That(packet, Does.Contain("refs/heads/feature/task-1"))
        Assert.That(packet, Does.Contain("backup current ledger Git SHA and diff"))
    | other -> Assert.Fail(sprintf "Expected unknown original outcome: %A" other)
    match runOriginal probe.Ports plan demand configuredRole provenance account probe.Current with
    | SchedulerDispatchResult.NeedsReconciliation _ -> ()
    | other -> Assert.Fail(sprintf "Expected original-only recovery: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))

[<Test>]
let already_marked_or_uncertain_marker_never_triggers_browser_resend () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "should-not-submit")
    let original =
        prepareOriginal plan demand configuredRole provenance account admitted |> unwrap
    // A process may have died between durable marker CAS and browser return.
    probe.Entry <- Some { Intent = original; Outcome = None }
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        Assert.That(issue.Intent.OperationId, Is.EqualTo(plan.DispatchOperationId))
    | other -> Assert.Fail(sprintf "Expected indeterminate prior attempt: %A" other)
    Assert.That(probe.Sends, Is.Zero)
    let timedOut = Probe(admitted, OriginalBrowserOutcome.Accepted "should-not-submit")
    timedOut.FailMark "Git CAS timeout"
    match runOriginal timedOut.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        Assert.That(issue.Reason, Does.Contain("Git CAS timeout"))
    | other -> Assert.Fail(sprintf "Expected marker uncertainty: %A" other)
    Assert.That(timedOut.Sends, Is.Zero)

[<Test>]
let original_browser_receipt_survives_project_cas_failure_for_manual_repair () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "browser-receipt-1")
    probe.FailProject "remote project head moved"
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        Assert.That(issue.Reason, Does.Contain("project dispatch CAS unresolved"))
        Assert.That(manualRepairPacket issue, Does.Contain("browser-receipt-1"))
    | other -> Assert.Fail(sprintf "Expected project CAS reconciliation: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation _ -> ()
    | other -> Assert.Fail(sprintf "Expected same original journal replay: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))

[<Test>]
let definitive_no_effect_is_visible_and_does_not_automatically_retry () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.DefinitiveNoEffect "provider rejected before send")
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.DefinitiveNoEffect issue ->
        Assert.That(manualRepairPacket issue, Does.Contain("project terminal before account release"))
    | other -> Assert.Fail(sprintf "Expected no effect: %A" other)
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.DefinitiveNoEffect _ -> ()
    | other -> Assert.Fail(sprintf "Expected no-effect replay: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))

[<Test>]
let incomplete_original_provenance_and_mismatched_wq04_role_are_actionable_unknowns () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "should-not-submit")
    let missing = { provenance with AccountHead = "" }
    match runOriginal probe.Ports plan demand configuredRole missing account admitted with
    | SchedulerDispatchResult.NotReady why -> Assert.That(why, Does.Contain("account/project head"))
    | other -> Assert.Fail(sprintf "Expected provenance check: %A" other)
    match runOriginal probe.Ports plan { demand with RoleVersion = 9 }
            configuredRole provenance account admitted with
    | SchedulerDispatchResult.NotReady why -> Assert.That(why, Does.Contain("WQ04 demand"))
    | other -> Assert.Fail(sprintf "Expected role evidence check: %A" other)
    Assert.That(probe.Sends, Is.Zero)

[<Test>]
let missing_browser_result_persistence_stays_unknown_with_original_observation () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "browser-receipt-1")
    probe.FailRecord "journal write timeout"
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        Assert.That(manualRepairPacket issue, Does.Contain("browser-receipt-1"))
        Assert.That(issue.Reason, Does.Contain("could not be saved"))
    | other -> Assert.Fail(sprintf "Expected journal uncertainty: %A" other)
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation _ -> ()
    | other -> Assert.Fail(sprintf "Expected one-shot marker reconciliation: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))

type private AdmissionBehavior =
    | Normal
    | FailBeforeCommit
    | FailAfterCommit

let private admissionPorts (probe: Probe) behavior =
    let mutable account = emptyAccount 1
    let mutable project =
        { emptyProject (Map.ofList ["Worker", roleSnapshot.Queues]) with
            Works = Map.ofList [available.Id, available] }
    let ports : SchedulerAdmissionPorts =
        { ReserveGlobal = fun plan ->
              match reserveAccount plan account with
              | Error err -> Error(sprintf "%A" err)
              | Ok next ->
                  account <- next
                  Ok next
          AdmitProject = fun plan reserved ->
              match behavior with
              | FailBeforeCommit -> Error "project admission response unavailable before commit"
              | _ ->
                  match admitAfterReserve plan reserved project with
                  | Error err -> Error(sprintf "%A" err)
                  | Ok next ->
                      project <- next
                      probe.Current <- next
                      if behavior = FailAfterCommit then Error "project CAS reply lost"
                      else Ok next
          AbortUnboundC01 = fun plan ->
              match abortUnboundC01 plan project with
              | Error err -> Error(sprintf "%A" err)
              | Ok next ->
                  project <- next
                  Ok next
          ReleaseAborted = fun plan reserved terminal ->
              match releaseAfterTerminal plan terminal reserved with
              | Error err -> Error(sprintf "%A" err)
              | Ok next ->
                  account <- next
                  Ok ()
          Browser = probe.Ports }
    ports, (fun () -> account), (fun () -> project)

[<Test>]
let prepared_non_agent_tick_runs_account_then_project_then_original_browser () =
    let plan, demand, _, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "browser-receipt-1")
    let ports, getAccount, getProject = admissionPorts probe Normal
    match drivePreparedLaunch ports plan demand configuredRole provenance with
    | SchedulerDispatchResult.Submitted(_, receipt) ->
        Assert.That(receipt, Is.EqualTo("browser-receipt-1"))
    | other -> Assert.Fail(sprintf "Expected direct tick submission: %A" other)
    Assert.That(getAccount().Tokens.[plan.Launch.Id], Is.False)
    Assert.That(getProject().Launches.[plan.Launch.Id].Credit, Is.EqualTo(Some plan.WorkId))
    Assert.That(probe.Sends, Is.EqualTo(1))

[<Test>]
let prepared_tick_c01_abort_only_releases_after_project_tombstone () =
    let plan, demand, _, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "must-not-send")
    let ports, getAccount, getProject = admissionPorts probe FailBeforeCommit
    match drivePreparedLaunch ports plan demand configuredRole provenance with
    | SchedulerDispatchResult.AbortedC01 original ->
        Assert.That(original.OperationId, Is.EqualTo(plan.DispatchOperationId))
    | other -> Assert.Fail(sprintf "Expected successful C01 abort: %A" other)
    Assert.That(getAccount().Tokens.[plan.Launch.Id], Is.True)
    Assert.That(probe.Sends, Is.Zero)
    match getProject().Launches.[plan.Launch.Id].State with
    | LaunchState.TerminalPendingRelease _ -> ()
    | other -> Assert.Fail(sprintf "Missing C01 terminal receipt: %A" other)

[<Test>]
let lost_admission_reply_with_existing_binding_never_releases_or_dispatches () =
    let plan, demand, _, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "must-not-send")
    let ports, getAccount, getProject = admissionPorts probe FailAfterCommit
    match drivePreparedLaunch ports plan demand configuredRole provenance with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        Assert.That(issue.Reason, Does.Contain("C01 abort not proven"))
        Assert.That(manualRepairPacket issue, Does.Contain(plan.DispatchOperationId))
    | other -> Assert.Fail(sprintf "Expected unresolved original C01 race: %A" other)
    Assert.That(getAccount().Tokens.[plan.Launch.Id], Is.False)
    Assert.That(getProject().Launches.[plan.Launch.Id].Credit, Is.EqualTo(Some plan.WorkId))
    Assert.That(probe.Sends, Is.Zero)

[<Test>]
let thrown_project_transport_after_browser_acceptance_remains_recoverable () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Accepted "original-browser-receipt")
    let unavailable =
        { probe.Ports with
            RecordProjectDispatch = fun _ _ -> failwith "project adapter disconnected" }
    match runOriginal unavailable plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        Assert.That(issue.Reason, Does.Contain("project adapter disconnected"))
        Assert.That(manualRepairPacket issue, Does.Contain("original-browser-receipt"))
    | other -> Assert.Fail(sprintf "Expected transport uncertainty: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.Submitted(_, receipt) ->
        Assert.That(receipt, Is.EqualTo("original-browser-receipt"))
    | other -> Assert.Fail(sprintf "Expected one-shot original repair: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))

[<Test>]
let thrown_journal_read_during_original_replay_is_visible_not_a_resend () =
    let plan, demand, account, admitted = setup ()
    let probe = Probe(admitted, OriginalBrowserOutcome.Unknown "send result missing")
    match runOriginal probe.Ports plan demand configuredRole provenance account admitted with
    | SchedulerDispatchResult.NeedsReconciliation _ -> ()
    | other -> Assert.Fail(sprintf "Expected unknown submission: %A" other)
    let disconnected =
        { probe.Ports with FindOriginal = fun _ -> failwith "journal adapter disconnected" }
    match runOriginal disconnected plan demand configuredRole provenance account probe.Current with
    | SchedulerDispatchResult.NeedsReconciliation issue ->
        Assert.That(issue.Reason, Does.Contain("journal adapter disconnected"))
    | other -> Assert.Fail(sprintf "Expected journal read uncertainty: %A" other)
    Assert.That(probe.Sends, Is.EqualTo(1))
