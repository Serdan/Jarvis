module ScheduledReadyResumeTests

open System
open System.IO
open System.Diagnostics
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open Client.ScheduledReadyResume

let private good = function Ok value -> value | Error issue -> failwithf "%A" issue

let private git directory args =
    use proc = new Process()
    proc.StartInfo.FileName <- "git"
    proc.StartInfo.WorkingDirectory <- directory
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    for arg in args do proc.StartInfo.ArgumentList.Add arg
    if not (proc.Start()) then failwith "Cannot start fixture git."
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwith error
    output.Trim()

let private fixtureIn root name =
    let bare = Path.Combine(root, name + ".git")
    let local = Path.Combine(root, name)
    git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
    git root ["clone"; bare; local] |> ignore
    File.WriteAllText(Path.Combine(local, "source.txt"), "fixture")
    git local ["add"; "source.txt"] |> ignore
    git local ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "fixture"] |> ignore
    git local ["push"; "-u"; "origin"; "HEAD"] |> ignore
    let binding =
        { RepositoryPath = local; RemoteName = "origin"; ApprovedFetchUrl = bare; ApprovedPushUrl = bare
          ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
    LedgerStore.initialize binding true |> good |> ignore
    let assign agent task =
        ReviewWorkflow.registerAgent binding (Guid.NewGuid()) agent |> good |> ignore
        ReviewWorkflow.createTask binding (Guid.NewGuid()) task task |> good |> ignore
        ReviewWorkflow.assignTask binding (Guid.NewGuid()) task (Guid.NewGuid()) agent |> good |> ignore
    RoleInstructions.setLaunchTemplate binding "fixture" (Guid.NewGuid()) 0 "Agent {{agentId}}, project {{projectName}}. Call inbox."
    |> good |> ignore
    binding, assign

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-ready-resume-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let binding, assign = fixtureIn root "local"
        action binding assign
    finally
        Directory.Delete(root, true)

let private ready agent role priority sequence =
    let r =
        { AgentId = agent; StateSequence = sequence; TaskId = agent; AttemptId = agent
          Generation = 1; Priority = priority; IssuedSequence = sequence }
    { AgentId = agent; Working = false; Roles = Set.singleton role; Ready = Some r }

let private working agent role = { AgentId = agent; Working = true; Roles = Set.singleton role; Ready = None }

[<Test>]
let ``Working counts only and excess drains without cancellation`` () =
    let a = ready "a" "Worker" 0 3L
    let b = ready "b" "Reviewer" 5 4L
    let x = working "manual-x" "Worker"
    let y = working "manual-y" "Worker"
    let roster = [a; b; x; y]
    let ids limits entries = candidates limits entries |> List.map _.AgentId
    Assert.That(ids disabled roster, Is.EqualTo(["b"; "a"] :> obj))
    Assert.That(ids { Total = Some 2; Roles = Map.empty } roster, Is.Empty)
    Assert.That(ids { Total = Some 1; Roles = Map.empty } roster, Is.Empty)
    Assert.That(ids { Total = Some 3; Roles = Map.empty } roster, Is.EqualTo(["b"; "a"] :> obj))
    Assert.That(ids { Total = None; Roles = Map.ofList ["Worker", 1] } roster, Is.EqualTo(["b"] :> obj))
    Assert.That(ids { Total = None; Roles = Map.ofList ["Worker", 1] } [a; b; x], Is.EqualTo(["b"] :> obj))
    Assert.That(ids { Total = None; Roles = Map.ofList ["Worker", 1] } [a; b], Is.EqualTo(["b"; "a"] :> obj))

let private run ports binding =
    executeOnce ports binding "fixture" "Fixture" "launchAgent.js" "script-sha" "route-sha"
    |> fun task -> task.GetAwaiter().GetResult()

let private held = function HeldCandidates [_] -> true | _ -> false

let private captureLaunch binding =
    let mutable captured = None
    let ports =
        { ReadLimits = readCurrentLimits
          CanSend = fun _ -> Task.FromResult(Ok true)
          Send = fun prepared -> captured <- Some prepared; Task.FromResult(Ok "submitted") }
    Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
    captured.Value, ports

[<TestCase(299, false)>]
[<TestCase(300, true)>]
[<TestCase(301, true)>]
let deadlineRequiresVerifiedEvidence seconds expired =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let launch, ports = captureLaunch binding
        let id = launch.OperationId.ToString("D")
        let sent = launch.SendBoundaryAtUtc.Value
        let now = sent.AddSeconds(float seconds)
        let deadline = sent.AddMinutes(5.)
        let evidence = { LastSeenAtUtc = sent.AddSeconds(-1.); ServerNowAtUtc = now }
        match reconcileAt binding id now None |> good with
        | LaunchActivityEvidenceUnavailable _ when expired -> ()
        | LaunchAwaitingCheckin at when not expired -> Assert.That(at, Is.EqualTo(deadline))
        | other -> Assert.Fail(sprintf "Unexpected absent-evidence result: %A" other)
        Assert.That(run ports binding |> held, Is.True)
        match reconcileAt binding id now (Some evidence) |> good with
        | LaunchReplacementAuthorized at when expired -> Assert.That(at, Is.EqualTo(now))
        | LaunchAwaitingCheckin at when not expired -> Assert.That(at, Is.EqualTo(deadline))
        | other -> Assert.Fail(sprintf "Unexpected verified-evidence result: %A" other)
        if expired then
            let replacement, replacementPorts = captureLaunch binding
            Assert.That(replacement.Ready, Is.EqualTo(launch.Ready))
            Assert.That(replacement.OperationId, Is.Not.EqualTo(launch.OperationId))
            match recordCheckin binding "Worker" id now |> good with
            | CheckinLate _ -> ()
            | other -> Assert.Fail(sprintf "Superseded check-in accepted: %A" other)
            Assert.That(run replacementPorts binding |> held, Is.True)
        else Assert.That(run ports binding |> held, Is.True))

[<Test>]
let exactCheckinPreventsReplacement () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let launch, ports = captureLaunch binding
        let id = launch.OperationId.ToString("D")
        let sent = launch.SendBoundaryAtUtc.Value
        Assert.That(recordCheckin binding "Wrong" id (sent.AddSeconds(1.)) |> Result.isError, Is.True)
        Assert.That(recordCheckin binding "Worker" (Guid.NewGuid().ToString("D")) (sent.AddSeconds(1.)) |> Result.isError, Is.True)
        let at = sent.AddSeconds(299.)
        Assert.That(recordCheckin binding "Worker" id at |> good, Is.EqualTo(CheckinConfirmed at))
        Assert.That(recordCheckin binding "Worker" id (at.AddSeconds(2.)) |> good, Is.EqualTo(CheckinDuplicate at))
        let now = sent.AddSeconds(301.)
        let evidence = { LastSeenAtUtc = sent.AddSeconds(-1.); ServerNowAtUtc = now }
        Assert.That(reconcileAt binding id now (Some evidence) |> good, Is.EqualTo(LaunchStarted at))
        Assert.That(run ports binding |> held, Is.True))

[<Test>]
let unrelatedActivityCannotAuthorizeReplacement () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let launch, ports = captureLaunch binding
        let sent = launch.SendBoundaryAtUtc.Value
        let now = sent.AddSeconds(301.)
        let evidence = { LastSeenAtUtc = sent.AddSeconds(1.); ServerNowAtUtc = now }
        match reconcileAt binding (launch.OperationId.ToString("D")) now (Some evidence) |> good with
        | LaunchActivityEvidenceUnavailable _ -> ()
        | other -> Assert.Fail(sprintf "Unattributed activity authorized replacement: %A" other)
        Assert.That(run ports binding |> held, Is.True))

[<Test>]
let operatorResolutionRequiresOriginalOwnerAndExactAssignment () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let launch, ports = captureLaunch binding
        let id = launch.OperationId.ToString("D")
        let at = launch.SendBoundaryAtUtc.Value.AddSeconds(301.)
        let resolve actor ready rationale ended = resolveOperator binding actor id ready rationale ended at
        Assert.That(resolve "fixture" launch.Ready "verified" false |> Result.isError, Is.True)
        Assert.That(resolve "foreign" launch.Ready "verified" true |> Result.isError, Is.True)
        Assert.That(resolve "fixture" { launch.Ready with Generation = launch.Ready.Generation + 1 } "verified" true |> Result.isError, Is.True)
        Assert.That(resolve "fixture" launch.Ready "" true |> Result.isError, Is.True)
        Assert.That(resolve "fixture" launch.Ready "verified ended" true |> good, Is.EqualTo(OperatorResolutionRecorded at))
        Assert.That(resolve "fixture" launch.Ready "verified ended" true |> good, Is.EqualTo(OperatorResolutionAlreadyRecorded at))
        let replacement, replacementPorts = captureLaunch binding
        Assert.That(replacement.Ready, Is.EqualTo(launch.Ready))
        Assert.That(replacement.OperationId, Is.Not.EqualTo(launch.OperationId))
        Assert.That(run replacementPorts binding |> held, Is.True)
        match recordCheckin binding "Worker" id at |> good with
        | CheckinLate _ -> ()
        | other -> Assert.Fail(sprintf "Old launch check-in not fenced: %A" other))

[<Test>]
let heldLaunchDoesNotBlockOtherReadyAgent () =
    fixture (fun binding assign ->
        assign "Worker A" "ready-a"
        let first, _ = captureLaunch binding
        assign "Worker B" "ready-b"
        let second, ports = captureLaunch binding
        Assert.That(second.Ready.AgentId, Is.Not.EqualTo(first.Ready.AgentId))
        match run ports binding with
        | HeldCandidates launches ->
            Assert.That(launches |> List.map _.LaunchId |> Set.ofList,
                        Is.EqualTo(Set.ofList [first.OperationId; second.OperationId] :> obj))
        | other -> Assert.Fail(sprintf "Expected two separately reported holds: %A" other))

[<Test>]
let expiryAndCheckinRaceNeverAuthorizeTwoOutcomes () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let launch, _ = captureLaunch binding
        let id = launch.OperationId.ToString("D")
        let sent = launch.SendBoundaryAtUtc.Value
        let expiry = sent.AddSeconds(300.)
        let evidence = { LastSeenAtUtc = sent.AddSeconds(-1.); ServerNowAtUtc = expiry }
        let checkin = Task.Run(fun () -> recordCheckin binding "Worker" id (sent.AddSeconds(299.)))
        let timeout = Task.Run(fun () -> reconcileAt binding id expiry (Some evidence))
        Task.WaitAll([| checkin :> Task; timeout :> Task |])
        let snapshot = LedgerStore.fetch binding |> good
        let state = (inspectLaunchAt binding snapshot launch.OperationId |> good).Value
        Assert.That(state.CheckinAtUtc.IsSome <> state.NoCheckinAtUtc.IsSome, Is.True,
                    "Exactly one terminal proof must survive competing transitions."))

[<Test>]
let ``actual canonical Ready resumes once and unknown browser send cannot replay`` () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let mutable sends = 0
        let ports =
            { ReadLimits = fun _ _ -> Ok disabled
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun prepared ->
                  sends <- sends + 1
                  Assert.That(prepared.Rendered.Prompt, Does.StartWith("Agent Worker, project Fixture. Call inbox."))
                  Assert.That(prepared.Rendered.Prompt, Does.Contain(prepared.OperationId.ToString("D")))
                  Task.FromResult(Error(SendOutcomeUnknown "unknown browser receipt")) }
        Assert.That(run ports binding, Is.EqualTo(OutcomeUnknown "unknown browser receipt"))
        Assert.That(run ports binding |> held, Is.True)
        Assert.That(sends, Is.EqualTo(1)))

[<Test>]
let ``confirmed tab capacity rejection records no-send proof and retries next tick`` () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let mutable sends = 0
        let ports =
            { ReadLimits = fun _ _ -> Ok disabled
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun _ ->
                  sends <- sends + 1
                  if sends = 1 then
                      Task.FromResult(Error(DefinitelyNotInvoked "TabCapacityHold: AgentTabLimitReached"))
                  else
                      Task.FromResult(Ok "submitted") }
        match run ports binding with
        | Held _ -> ()
        | other -> Assert.Fail("Expected a durable no-send hold, got " + string other)
        Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
        Assert.That(sends, Is.EqualTo(2)))

[<Test>]
let ``actual Working count holds scheduler then resumes below cap`` () =
    fixture (fun binding assign ->
        assign "Manual" "manual-task"
        ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) "manual-task" "Manual" |> good |> ignore
        assign "Worker" "ready-task"
        let mutable sends = 0
        let mutable limits = { Total = Some 1; Roles = Map.empty }
        let ports =
            { ReadLimits = fun _ _ -> Ok limits
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "submitted") }
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        limits <- { Total = Some 2; Roles = Map.empty }
        Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
        Assert.That(sends, Is.EqualTo(1)))

[<Test>]
let ``STOP hold never sends or consumes Ready episode`` () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let mutable allowed = false
        let mutable sends = 0
        let ports =
            { ReadLimits = fun _ _ -> Ok disabled
              CanSend = fun _ -> Task.FromResult(Ok allowed)
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "submitted") }
        Assert.That(run ports binding, Is.EqualTo(Held "Scheduled launch stopped."))
        Assert.That(sends, Is.Zero)
        allowed <- true
        Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
        Assert.That(sends, Is.EqualTo(1)))

[<Test>]
let ``canonical STOP alert is not consumed and prevents Send`` () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        ReviewWorkflow.queueResponseAlert binding (Guid.NewGuid()) "fixture" "Worker" "STOP this session"
        |> good |> ignore
        let mutable sends = 0
        let ports =
            { ReadLimits = fun _ _ -> Ok disabled
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "submitted") }
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        Assert.That(sends, Is.Zero)
        Assert.That((ReviewWorkflow.getResponseAlerts binding "Worker" |> good).Pending.Length, Is.EqualTo(1)))

open Client.ChallengeWorkflow

[<Test>]
let ``canonical role-only caps include manually Working agents`` () =
    fixture (fun binding assign ->
        assign "Manual" "manual-task"
        assign "Worker" "worker-task"
        assign "Reviewer" "review-task"
        RoleInstructions.createRole binding "fixture" (Guid.NewGuid()) "Worker" "Implementation" "Implement"
        |> good |> ignore
        RoleInstructions.createRole binding "fixture" (Guid.NewGuid()) "Reviewer" "Review" "Review"
        |> good |> ignore
        for agent, role in ["Manual", "Worker"; "Worker", "Worker"; "Reviewer", "Reviewer"] do
            RoleInstructions.attachRole binding "fixture" (Guid.NewGuid()) agent role 0 |> good |> ignore
        ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) "manual-task" "Manual" |> good |> ignore
        let mutable selected = []
        let ports =
            { ReadLimits = fun _ _ -> Ok { Total = None; Roles = Map.ofList ["Worker", 1] }
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun prepared ->
                  selected <- prepared.Ready.AgentId :: selected
                  Task.FromResult(Ok "submitted") }
        Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
        Assert.That(run ports binding |> held, Is.True)
        Assert.That(selected, Is.EqualTo(["Reviewer"] :> obj)))

[<Test>]
let ``actual challenge wait blocks launch and resolution makes the new generation eligible`` () =
    fixture (fun binding assign ->
        assign "Worker" "wait-task"
        ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "Lead" |> good |> ignore
        let taskId = "wait-task"
        ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) taskId "Worker" |> good |> ignore
        let _, task, attempt = ReviewWorkflow.getTask binding taskId |> good
        let agent = (ReviewWorkflow.getAgentInbox binding "Worker" |> good).Agent
        let challenge =
            ChallengeWorkflow.create binding (Guid.NewGuid()) "Worker" "Wait for gate" taskId
                [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Gate" } ]
            |> good
        ChallengeWorkflow.blockAttempt binding (Guid.NewGuid()) "Worker" taskId attempt.Value.AttemptId
            task.Version attempt.Value.Version agent.Version attempt.Value.AssignmentGeneration "Waiting" [challenge.ChallengeId]
        |> good |> ignore
        let mutable generations = []
        let ports =
            { ReadLimits = fun _ _ -> Ok disabled
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun prepared ->
                  generations <- prepared.Ready.Generation :: generations
                  Task.FromResult(Ok "submitted") }
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        ChallengeWorkflow.resolve binding (Guid.NewGuid()) "Lead" challenge.ChallengeId
            challenge.ConditionIds.Head 1 "Passed" "" |> good |> ignore
        Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
        Assert.That(generations, Is.EqualTo([2] :> obj))
        Assert.That(run ports binding |> held, Is.True))

[<Test>]
let ``canonical transient excess drains naturally without cancellation`` () =
    fixture (fun binding assign ->
        assign "Manual1" "manual-one"
        assign "Manual2" "manual-two"
        assign "Worker" "ready-task"
        ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) "manual-one" "Manual1" |> good |> ignore
        ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) "manual-two" "Manual2" |> good |> ignore
        let mutable sends = 0
        let ports =
            { ReadLimits = fun _ _ -> Ok { Total = Some 1; Roles = Map.empty }
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "submitted") }
        let yieldNaturally taskId agentId =
            let _, _, attempt = ReviewWorkflow.getTask binding taskId |> good
            let agent = (ReviewWorkflow.getAgentInbox binding agentId |> good).Agent
            ReviewWorkflow.yieldAgent binding (Guid.NewGuid()) taskId agentId attempt.Value.Version
                agent.Version attempt.Value.AssignmentGeneration "Checkpoint" "Continue" |> good |> ignore
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        Assert.That((ReviewWorkflow.getAgentInbox binding "Manual1" |> good).Agent.Execution, Is.EqualTo("Working"))
        Assert.That((ReviewWorkflow.getAgentInbox binding "Manual2" |> good).Agent.Execution, Is.EqualTo("Working"))
        yieldNaturally "manual-one" "Manual1"
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        yieldNaturally "manual-two" "Manual2"
        Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
        Assert.That(sends, Is.EqualTo(1)))

open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Client
open Client.OrchestrationApi

let private route (project: string) : BrowserSchedules.ScheduledAction =
    { Id = "ready-resume"; DueAtUtc = DateTimeOffset.UtcNow; Url = "https://chatgpt.com/g/g-p-fixture/project"
      ProjectName = project; ScriptPath = "browser-scripts/chatgpt/launchAgent.js"; Alias = "agents"
      ArgsJson = """{"resumeReady":true}"""; Active = false; WindowRegistration = "agents"
      CloseTabAfterExecution = true; RepeatEverySeconds = 60; Status = "running"
      RegistrationId = ""; ConversationUrl = ""; Cleanup = ""; AttemptId = "attempt"; RevisionId = "revision"
      Error = ""; UpdatedAtUtc = DateTimeOffset.UtcNow; ActionKind = "BrowserScript"; TriggerKind = "Every"
      Executable = ""; CommandArgs = []; WorkingDirectory = ""; TimeoutSeconds = 30; MaxOutputBytes = 100
      IdleAgentId = ""; IdleSeconds = 0; LastActivityAtUtc = DateTimeOffset.MinValue
      StartedAtUtc = DateTimeOffset.UtcNow; Disabled = false; CheckinWakeAtUtc = DateTimeOffset.MinValue; LastResult = "" }

[<Test>]
let onlyExplicitRecurringRoute () =
    let a = route "Fixture"
    Assert.That(BrowserSchedules.isReadyResume a, Is.True)
    Assert.That(validateRoute a, Is.EqualTo(Ok () : Result<unit,string>))
    Assert.That(validateRoute { a with CloseTabAfterExecution = false }, Is.EqualTo(Ok () : Result<unit,string>))
    for invalid in [
        { a with TriggerKind = "Once"; RepeatEverySeconds = 0 }
        { a with ArgsJson = """{"resumeReady":true,"prompt":"injected"}""" }
        { a with Url = "https://chatgpt.com/c/conversation" }
        { a with Url = "https://example.test/" }
        { a with ProjectName = "Fixture::other" }
        { a with WindowRegistration = "" } ] do
        Assert.That((validateRoute invalid).IsError, Is.True)
    Assert.That(BrowserSchedules.isReadyResume { a with ArgsJson = "{}" }, Is.False)
    Assert.That(BrowserSchedules.isReadyResume { a with ScriptPath = "other.js" }, Is.False)

[<Test>]
let canonicalOptionalPolicyChanges () =
    fixture (fun binding assign ->
        assign "Manual" "manual-task"
        assign "Worker" "ready-task"
        ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) "manual-task" "Manual" |> good |> ignore
        let mutable sends = 0
        let ports =
            { ReadLimits = readCurrentLimits
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "submitted") }
        ProjectCapacityPolicy.updateOptional binding "fixture" (Guid.NewGuid()) 0 (Some 1) Map.empty
        |> good |> ignore
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        ProjectCapacityPolicy.updateOptional binding "fixture" (Guid.NewGuid()) 1 None Map.empty
        |> good |> ignore
        Assert.That(run ports binding, Is.EqualTo(Submitted "submitted"))
        Assert.That(sends, Is.EqualTo(1)))

[<Test>]
let lateStopAfterIntent () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let mutable checks = 0
        let mutable sends = 0
        let ports =
            { ReadLimits = readCurrentLimits
              CanSend = fun ready ->
                  checks <- checks + 1
                  if checks = 2 then
                      ReviewWorkflow.queueResponseAlert binding (Guid.NewGuid()) "fixture" "Worker" "STOP now"
                      |> good |> ignore
                  let latest = LedgerStore.fetch binding |> good
                  Task.FromResult(stillReadyAt binding latest ready |> Result.mapError (sprintf "%A"))
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "unexpected") }
        Assert.That(run ports binding, Is.EqualTo(Held "Scheduled launch stopped before Send."))
        Assert.That(checks, Is.EqualTo(2))
        Assert.That(sends, Is.Zero)
        let alert = (ReviewWorkflow.getResponseAlerts binding "Worker" |> good).Pending.Head
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        Assert.That(sends, Is.Zero)
        ReviewWorkflow.clearResponseAlert binding (Guid.NewGuid()) "fixture" "Worker" (Guid.Parse alert.AlertId)
        |> good |> ignore
        Assert.That(run ports binding, Is.EqualTo(Submitted "unexpected"))
        Assert.That(run ports binding |> held, Is.True)
        Assert.That(sends, Is.EqualTo(1)))

[<TestCase(false)>]
[<TestCase(true)>]
let lateDenialThenConcurrentHealthyPolls isError =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let mutable checks = 0
        let mutable sends = 0
        let ports =
            { ReadLimits = readCurrentLimits
              CanSend = fun _ ->
                  checks <- checks + 1
                  Task.FromResult(if checks = 2 then
                                      if isError then Error "temporary preflight failure" else Ok false
                                  else Ok true)
              Send = fun _ -> Interlocked.Increment(&sends) |> ignore; Task.FromResult(Ok "submitted") }
        let before = LedgerStore.fetch binding |> good
        let first = (prepareAt readCurrentLimits binding before "Fixture" |> good).Value
        Assert.That(run ports binding, Is.EqualTo(Held (if isError then "temporary preflight failure" else "Scheduled launch stopped before Send.")))
        Assert.That(sends, Is.Zero)
        let after = LedgerStore.fetch binding |> good
        let second = (prepareAt readCurrentLimits binding after "Fixture" |> good).Value
        Assert.That(second.Ready, Is.EqualTo(first.Ready))
        Assert.That(second.OperationId, Is.Not.EqualTo(first.OperationId))
        Assert.That((LedgerStore.getOperationAt binding after first.OperationId |> good).IsSome, Is.True)
        let healthy = { ports with CanSend = fun _ -> Task.FromResult(Ok true) }
        use barrier = new Barrier(2)
        let compete () =
            Task.Run(fun () ->
                let mutable firstCheck = true
                let preflight _ =
                    if firstCheck then
                        firstCheck <- false
                        if not (barrier.SignalAndWait(TimeSpan.FromSeconds(15.))) then failwith "race barrier timed out"
                    Task.FromResult(Ok true)
                let concurrent = { healthy with CanSend = preflight }
                run concurrent binding)
        let a = compete()
        let b = compete()
        Task.WaitAll([| a :> Task; b :> Task |])
        Assert.That(run healthy binding |> held, Is.True)
        Assert.That(sends, Is.EqualTo(1))
        let final = LedgerStore.fetch binding |> good
        Assert.That((LedgerStore.getOperationAt binding final second.OperationId |> good).IsSome, Is.True))

let private waitUntil (check: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds(15.)
    while not (check()) && DateTime.UtcNow < deadline do Thread.Sleep(25)
    Assert.That(check(), Is.True, "Timed out waiting for fixture state.")

let private scheduleState (service: BrowserSchedules.Service) id =
    use doc = JsonDocument.Parse(service.Get(id) |> good)
    let p name = doc.RootElement.GetProperty(name: string).GetString()
    p "status", p "attemptId", p "error"

[<Test>]
let durableDeadlineWakeSurvivesRestartWithoutChangingCadence () =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-checkin-wake-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(folder) |> ignore
    try
        let path = Path.Combine(folder, "schedules.json")
        let origin = DateTimeOffset.UtcNow
        let mutable nowTicks = origin.Ticks
        let clock () = DateTimeOffset(Interlocked.Read(&nowTicks), TimeSpan.Zero)
        let setTime at = Interlocked.Exchange(&nowTicks, (at: DateTimeOffset).Ticks) |> ignore
        let wake = origin.AddSeconds(300.)
        let cadence = origin.AddSeconds(3605.)
        let action = route "Fixture"
        let mutable calls = 0
        let mutable firstService: BrowserSchedules.Service option = None
        let execute claimed =
            Interlocked.Increment(&calls) |> ignore
            Assert.That(firstService.Value.RecordCheckinWake(claimed, Some wake, true), Is.True)
            Task.FromResult(Ok """{"status":"completed"}""")
        let waitFinished (service: BrowserSchedules.Service) old =
            waitUntil(fun () ->
                let status, attempt, _ = scheduleState service action.Id
                status = "pending" && attempt <> old && not (String.IsNullOrWhiteSpace attempt))
        let dueAndWake (service: BrowserSchedules.Service) =
            use doc = JsonDocument.Parse(service.Get(action.Id) |> good)
            doc.RootElement.GetProperty("dueAtUtc").GetDateTimeOffset(),
            doc.RootElement.GetProperty("checkinWakeAtUtc").GetDateTimeOffset()
        do
            use service = new BrowserSchedules.Service(path, execute, now=clock, pollMs=25)
            firstService <- Some service
            service.Schedule(action.Id, 5, action.Url, action.ProjectName, action.ScriptPath, action.Alias, action.ArgsJson, action.Active,
                windowRegistration=action.WindowRegistration, closeAfter=true, repeatEverySeconds=3600) |> good |> ignore
            service.Start()
            setTime (origin.AddSeconds(5.))
            waitFinished service ""
            Assert.That(dueAndWake service, Is.EqualTo((cadence, wake)))
        use restarted = new BrowserSchedules.Service(path, (fun _ ->
            Interlocked.Increment(&calls) |> ignore
            Task.FromResult(Ok """{"status":"completed"}""")), now=clock, pollMs=25)
        let _, previous, _ = scheduleState restarted action.Id
        restarted.Start()
        setTime (wake.AddSeconds(-1.))
        Thread.Sleep(100)
        Assert.That(Volatile.Read(&calls), Is.EqualTo(1))
        setTime wake
        waitFinished restarted previous
        Assert.That(Volatile.Read(&calls), Is.EqualTo(2))
        Assert.That(dueAndWake restarted, Is.EqualTo((cadence, DateTimeOffset.MinValue)))
        setTime (wake.AddSeconds(1.))
        Thread.Sleep(100)
        Assert.That(Volatile.Read(&calls), Is.EqualTo(2))
    finally Directory.Delete(folder, true)

[<Test>]
let interruptedClaimIsNotReplayed () =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-ready-journal-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(folder) |> ignore
    try
        let path = Path.Combine(folder, "schedules.json")
        let original = route "Fixture"
        File.WriteAllText(path, JsonSerializer.Serialize({| schemaVersion = 3; actions = [original] |}))
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let mutable nowTicks = DateTimeOffset.UtcNow.Ticks
        let clock () = DateTimeOffset(Interlocked.Read(&nowTicks), TimeSpan.Zero)
        let mutable calls = 0
        use service = new BrowserSchedules.Service(path, (fun _ ->
            Interlocked.Increment(&calls) |> ignore
            Task.FromResult(Ok """{"status":"completed"}""")), now=clock, pollMs=25)
        let status, attempt, error = scheduleState service original.Id
        Assert.That(status, Is.EqualTo("suspended"))
        Assert.That(attempt, Is.EqualTo(original.AttemptId))
        Assert.That(error, Does.StartWith("ResumePublicationUncertain:"))
        let schedule id project script args =
            service.Schedule(id, 5, original.Url, project, script, "", args, false,
                windowRegistration="agents", repeatEverySeconds=60)
        Assert.That((schedule original.Id "Fixture" original.ScriptPath original.ArgsJson).IsError, Is.True)
        schedule "alternate" "Fixture" original.ScriptPath original.ArgsJson |> good |> ignore
        schedule "generic" "Fixture" "other.js" "{}" |> good |> ignore
        Interlocked.Add(&nowTicks, TimeSpan.FromSeconds(6.).Ticks) |> ignore
        service.Start()
        waitUntil (fun () ->
            let status, attempt, _ = scheduleState service "generic"
            status = "pending" && not (String.IsNullOrWhiteSpace attempt))
        Assert.That(Volatile.Read(&calls), Is.EqualTo(1))
        let _, otherAttempt, _ = scheduleState service "alternate"
        Assert.That(otherAttempt, Is.Empty)
    finally
        Directory.Delete(folder, true)

let private socketSend (socket: ClientWebSocket) (value: string) = task {
    let bytes = Encoding.UTF8.GetBytes value
    do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
}

let private socketReceive (socket: ClientWebSocket) = task {
    let bytes = Array.zeroCreate<byte> 8192
    let! result = socket.ReceiveAsync(ArraySegment<byte>(bytes), CancellationToken.None)
    Assert.That(result.EndOfMessage, Is.True)
    return Encoding.UTF8.GetString(bytes, 0, result.Count)
}

let private prop (raw: string) (key: string) =
    use doc = JsonDocument.Parse raw
    doc.RootElement.GetProperty(key).GetString()

[<Test; NonParallelizable>]
let actualScheduledCallerWithFakeBrowser () =
    fixture (fun binding assign ->
        let root = Path.GetDirectoryName(binding.RepositoryPath)
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let a = route "local"
            let script = Path.Combine(binding.RepositoryPath, a.ScriptPath)
            Directory.CreateDirectory(Path.GetDirectoryName script) |> ignore
            File.WriteAllText(script, "async function execute(args) { return args; }")
            assign "Manual" "manual-task"
            ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid()) "manual-task" "Manual" |> good |> ignore
            assign "Worker" "ready-task"
            ProjectCapacityPolicy.updateOptional binding "fixture" (Guid.NewGuid()) 0 (Some 1) Map.empty
            |> good |> ignore
            let rt = Runtime(root, ConsoleTui.ConsoleTui(), Client.FullTrust)
            let head = (LedgerStore.fetch binding |> good).Commit
            LedgerBindingConfig.bindExisting "local" rt binding.ProjectId binding.LedgerId binding.Epoch
                binding.RemoteName binding.ApprovedFetchUrl binding.ApprovedPushUrl head true |> good |> ignore
            (task {
                use listener = new TcpListener(IPAddress.Loopback, 0)
                listener.Start()
                let port = (listener.LocalEndpoint :?> IPEndPoint).Port
                listener.Stop()
                use bridge = new BrowserBridge.Service(port, "test-secret")
                bridge.Start()
                rt.Browser <- Some bridge
                use socket = new ClientWebSocket()
                socket.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                do! socket.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"), CancellationToken.None)
                let! challenge = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                use hmac = new HMACSHA256(Encoding.UTF8.GetBytes("test-secret"))
                let proof = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(prop challenge "nonce"))).ToLowerInvariant()
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="authenticate"; proof=proof |}))
                let! ready = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                let connection = prop ready "connectionId"
                do! socketSend socket """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-01","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
                waitUntil (fun () -> bridge.ListWindows() |> Result.exists (fun s -> s.Contains("window-01")))
                let mutable nowTicks = DateTimeOffset.UtcNow.Ticks
                let clock () = DateTimeOffset(Interlocked.Read(&nowTicks), TimeSpan.Zero)
                let advance (seconds: float) = Interlocked.Add(&nowTicks, TimeSpan.FromSeconds(seconds).Ticks) |> ignore
                let actorProvider : (unit -> string option) ref = ref (fun () -> None)
                let mutable registered = true
                use service = new BrowserSchedules.Service(Path.Combine(root, "schedules.json"),
                    Program.executeRegisteredSchedule (fun () -> Ok ClientConfig.Disabled) actorProvider rt (ConsoleTui.ConsoleTui()) (Some bridge), now=clock, pollMs=25)
                rt.BrowserScheduler <- Some service
                service.Schedule(a.Id, 5, a.Url, a.ProjectName, a.ScriptPath, a.Alias, a.ArgsJson, a.Active,
                    windowRegistration=a.WindowRegistration, closeAfter=true, repeatEverySeconds=60) |> good |> ignore
                let waitOccurrence old =
                    waitUntil (fun () ->
                        let status, attempt, _ = scheduleState service a.Id
                        status = "pending" && attempt <> old && not (String.IsNullOrWhiteSpace attempt))
                    let _, attempt, _ = scheduleState service a.Id
                    attempt
                service.Start()
                let pending = socketReceive socket
                advance 6.
                let unavailable = waitOccurrence ""
                let _, _, error = scheduleState service a.Id
                Assert.That(error, Does.Contain("Authenticated active resume schedule unavailable"))
                Assert.That(pending.IsCompleted, Is.False, "No authenticated provider must mean no browser effect.")
                actorProvider.Value <- (fun () -> if Volatile.Read(&registered) then Some "operator:fixture" else None)
                advance 60.
                let absentSwitch = waitOccurrence unavailable
                let _, _, error = scheduleState service a.Id
                Assert.That(error, Does.Contain("Project Automation is disabled"), "The already constructed callback must read the installed actor and reach the policy gate.")
                Assert.That(pending.IsCompleted, Is.False, "Legacy capacity policy must not enable Automation.")
                ProjectCapacityPolicy.updateAutomation binding "fixture" (Guid.NewGuid()) 1 true (Some 1) Map.empty
                |> good |> ignore
                advance 60.
                let capped = waitOccurrence absentSwitch
                Assert.That(pending.IsCompleted, Is.False, "Enabled cap must produce no browser effect.")
                ProjectCapacityPolicy.updateAutomation binding "fixture" (Guid.NewGuid()) 2 true None Map.empty
                |> good |> ignore
                Volatile.Write(&registered, false)
                advance 60.
                let unregistered = waitOccurrence capped
                let _, _, error = scheduleState service a.Id
                Assert.That(error, Does.Contain("Authenticated active resume schedule unavailable"))
                Assert.That(pending.IsCompleted, Is.False, "Registration loss must prevent dispatch even when policy permits it.")
                Volatile.Write(&registered, true)
                advance 60.
                let! frame = pending.WaitAsync(TimeSpan.FromSeconds(15.))
                Assert.That(prop frame "kind", Is.EqualTo("openTab"))
                do! socketSend socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-01","alias":"new","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop frame "requestId"; connectionId=connection; tab={| registrationId="created-01"; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
                let! execute = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
                use request = JsonDocument.Parse execute
                let args = request.RootElement.GetProperty("args")
                Assert.That(args.GetProperty("prompt").GetString(), Does.StartWith("Agent Worker, project local. Call inbox."))
                Assert.That(args.GetProperty("prompt").GetString(), Does.Contain("launchId"))
                Assert.That(args.GetProperty("startUrl").GetString(), Is.EqualTo(a.Url))
                Assert.That(args.EnumerateObject() |> Seq.length, Is.EqualTo(2))
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId="created-01"; registrationGeneration=1; status="completed"; result={| status="outcomeUnknown"; stage="acknowledgement"; reason="SubmissionUnconfirmed" |} |}))
                let! close = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                Assert.That(prop close "kind", Is.EqualTo("closeTab"))
                Assert.That(prop close "registrationId", Is.EqualTo("created-01"))
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="tabClosed"; requestId=prop close "requestId"; connectionId=connection; registrationId="created-01"; registrationGeneration=1 |}))
                let launched = waitOccurrence unregistered
                let _, _, error = scheduleState service a.Id
                Assert.That(error, Does.Contain("OutcomeUnknown"))
                let noReplay = socketReceive socket
                advance 60.
                waitOccurrence launched |> ignore
                Assert.That(noReplay.IsCompleted, Is.False, "Original Ready episode must never replay after unknown send.")
            }).GetAwaiter().GetResult()
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior))


[<Test; NonParallelizable>]
let recurringLegacyLauncherUsesCanonicalProjectOptIn () =
    fixture (fun a _ ->
        let root = Path.GetDirectoryName(a.RepositoryPath)
        let b, _ = fixtureIn root "other"
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let checkout = Path.Combine(root, "legacy-checkout")
            git a.RepositoryPath ["worktree"; "add"; "-b"; "legacy-checkout"; checkout] |> ignore
            let launcher = (route "local").ScriptPath
            let writeScript folder script =
                let path = Path.Combine(folder, script)
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, "async function execute(args) { return args; }")
            writeScript checkout launcher
            writeScript b.RepositoryPath launcher
            writeScript b.RepositoryPath "other.js"
            let tui = ConsoleTui.ConsoleTui()
            let rt = Runtime(root, tui, Client.FullTrust)
            for name, binding in ["local", a; "other", b] do
                let head = (LedgerStore.fetch binding |> good).Commit
                LedgerBindingConfig.bindExisting name rt binding.ProjectId binding.LedgerId binding.Epoch
                    binding.RemoteName binding.ApprovedFetchUrl binding.ApprovedPushUrl head true |> good |> ignore
            ProjectCapacityPolicy.updateAutomation b "fixture" (Guid.NewGuid()) 0 false None Map.empty |> good |> ignore
            (task {
                use listener = new TcpListener(IPAddress.Loopback, 0)
                listener.Start()
                let port = (listener.LocalEndpoint :?> IPEndPoint).Port
                listener.Stop()
                use bridge = new BrowserBridge.Service(port, "test-secret")
                bridge.Start()
                rt.Browser <- Some bridge
                use socket = new ClientWebSocket()
                socket.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                do! socket.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"), CancellationToken.None)
                let! challenge = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                use hmac = new HMACSHA256(Encoding.UTF8.GetBytes("test-secret"))
                let proof = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(prop challenge "nonce"))).ToLowerInvariant()
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="authenticate"; proof=proof |}))
                let! ready = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                let connection = prop ready "connectionId"
                do! socketSend socket """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-01","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
                waitUntil (fun () -> bridge.ListWindows() |> Result.exists (fun s -> s.Contains("window-01")))
                let mutable nowTicks = DateTimeOffset.UtcNow.Ticks
                let clock () = DateTimeOffset(Interlocked.Read(&nowTicks), TimeSpan.Zero)
                let advance (seconds: float) = Interlocked.Add(&nowTicks, TimeSpan.FromSeconds(seconds).Ticks) |> ignore
                let mutable raceReads = 0
                let mutable raceArmed = 0
                let readFence () =
                    if Volatile.Read(&raceArmed) = 1 && Interlocked.Increment(&raceReads) = 2 then
                        ProjectCapacityPolicy.updateAutomation a "fixture" (Guid.NewGuid()) 1 false None Map.empty |> good |> ignore
                        Volatile.Write(&raceArmed, 0)
                    Ok ClientConfig.Disabled
                let actor () = Some "operator:fixture"
                use service = new BrowserSchedules.Service(Path.Combine(root, "legacy-schedules.json"),
                    Program.executeRegisteredSchedule readFence (ref actor) rt tui (Some bridge), now=clock, pollMs=25)
                rt.BrowserScheduler <- Some service
                let schedule id project script =
                    let route = route project
                    service.Schedule(id, 5, route.Url, project, script, route.Alias, "{}", false,
                        windowRegistration=route.WindowRegistration, closeAfter=false, repeatEverySeconds=60) |> good |> ignore
                let waitOccurrence id old =
                    waitUntil (fun () ->
                        let status, attempt, _ = scheduleState service id
                        status = "pending" && attempt <> old && not (String.IsNullOrWhiteSpace attempt))
                    let _, attempt, error = scheduleState service id
                    attempt, error
                let hold id old =
                    let attempt, error = waitOccurrence id old
                    Assert.That(error, Does.StartWith("failed: AutomationHold:"))
                    attempt
                let finish (pending: Task<string>) registration = task {
                    let! frame = pending.WaitAsync(TimeSpan.FromSeconds(15.))
                    Assert.That(prop frame "kind", Is.EqualTo("openTab"))
                    do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="registrations"; tabs=[| {| registrationId=registration; alias="new"; agentCreated=true; availability="ready"; registrationGeneration=1 |} |] |}))
                    do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop frame "requestId"; connectionId=connection; tab={| registrationId=registration; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
                    let! execute = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                    Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
                    do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId=registration; registrationGeneration=1; status="completed"; result={| ok=true |} |}))
                }
                schedule "legacy-a" "local::legacy-checkout" launcher
                service.Start()
                let pending = socketReceive socket
                advance 6.
                let absent = hold "legacy-a" ""
                Assert.That(pending.IsCompleted, Is.False, "Absent switch must not send.")
                ProjectCapacityPolicy.updateAutomation a "fixture" (Guid.NewGuid()) 0 true None Map.empty |> good |> ignore
                schedule "legacy-b" "other" launcher
                advance 6.
                hold "legacy-b" "" |> ignore
                Assert.That(pending.IsCompleted, Is.False, "Other project remains off when A is enabled.")
                service.Cancel("legacy-b") |> good |> ignore
                schedule "unrelated" "other" "other.js"
                advance 6.
                do! finish pending "created-unrelated"
                let _, error = waitOccurrence "unrelated" ""
                Assert.That(error, Is.EqualTo("completed: "), "Unrelated scheduled script must still execute while Automation is off.")
                service.Cancel("unrelated") |> good |> ignore
                let pending = socketReceive socket
                Volatile.Write(&raceArmed, 1)
                advance 60.
                let raced = hold "legacy-a" absent
                Assert.That(Volatile.Read(&raceReads), Is.EqualTo(2))
                Assert.That(pending.IsCompleted, Is.False, "Disable observed at final boundary must not send.")
                advance 60.
                let off = hold "legacy-a" raced
                Assert.That(pending.IsCompleted, Is.False, "Explicitly off recurrence must not send.")
                ProjectCapacityPolicy.updateAutomation a "fixture" (Guid.NewGuid()) 2 true None Map.empty |> good |> ignore
                advance 60.
                do! finish pending "created-legacy"
                let _, error = waitOccurrence "legacy-a" off
                Assert.That(error, Is.EqualTo("completed: "), "Re-enable must allow next occurrence using canonical main policy for worktree.")
            }).GetAwaiter().GetResult()
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior))

[<TestCase("PreSelection")>]
[<TestCase("SelectedNoReceipt")>]
[<TestCase("PublicationUnknown")>]
[<TestCase("SelectedWithReceipt")>]
[<TestCase("Published")>]
[<TestCase("EnteringSend")>]
[<NonParallelizable>]
let restartAtDurableResumeBoundary boundary =
    fixture (fun initialBinding assign ->
        let root = Path.GetDirectoryName(initialBinding.RepositoryPath)
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            assign "Worker" "original-task"
            let rt = Runtime(root, ConsoleTui.ConsoleTui(), Client.FullTrust)
            let head = (LedgerStore.fetch initialBinding |> good).Commit
            LedgerBindingConfig.bindExisting "local" rt initialBinding.ProjectId initialBinding.LedgerId initialBinding.Epoch
                initialBinding.RemoteName initialBinding.ApprovedFetchUrl initialBinding.ApprovedPushUrl head true |> good |> ignore
            let binding, _ = LedgerBindingConfig.loadWithSnapshot "local" rt |> good
            let bindingHash =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(binding)))).ToLowerInvariant()
            let actor () = Some "operator:fixture"
            let hook = Path.Combine(binding.ApprovedPushUrl, "hooks", "pre-receive")
            let uncertainWithoutReceipt = boundary = "SelectedNoReceipt" || boundary = "PublicationUnknown"
            if boundary = "PublicationUnknown" then
                File.WriteAllText(hook, "#!/bin/sh\nexit 1\n")
                if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                    File.SetUnixFileMode(hook, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let path = Path.Combine(root, "restart-schedules.json")
            let a = route "local"
            let mutable nowTicks = DateTimeOffset.UtcNow.Ticks
            let clock () = DateTimeOffset(Interlocked.Read(&nowTicks), TimeSpan.Zero)
            let advance (seconds: float) = Interlocked.Add(&nowTicks, TimeSpan.FromSeconds(seconds).Ticks) |> ignore
            use interrupted = new ManualResetEventSlim(false)
            use release = new ManualResetEventSlim(false)
            use oldExited = new ManualResetEventSlim(false)
            let mutable firstRun = true
            let mutable service : BrowserSchedules.Service option = None
            let mutable original : Prepared option = None
            let sent = System.Collections.Concurrent.ConcurrentQueue<string>()
            let pause () =
                interrupted.Set()
                if not (release.Wait(TimeSpan.FromSeconds(30.))) then failwith "Interrupted fixture was not released."
                raise (OperationCanceledException("Simulated process interruption"))
            let execute (action: BrowserSchedules.ScheduledAction) = task {
                let wasFirst = firstRun
                try
                    if wasFirst && boundary = "PreSelection" then pause()
                    let track stage prepared =
                        let intent = intentFor (actor().Value) action.ScriptPath "fixture-source" "fixture-route" prepared
                        let selection : BrowserSchedules.ResumeSelection =
                            { Stage = stage; BindingSha256 = bindingHash; OperationId = intent.OperationId
                              Actor = intent.Actor; RequestJson = intent.RequestJson }
                        if wasFirst then
                            original <- Some prepared
                            if boundary = "SelectedWithReceipt" && stage = "Published" then pause()
                        let written = service.Value.RecordResumeStage(action, selection)
                        if wasFirst && ((boundary = "SelectedNoReceipt" && stage = "Selected") ||
                                        (boundary = "Published" && stage = "Published")) then pause()
                        written
                    let ports =
                        { ReadLimits = readCurrentLimits
                          CanSend = fun _ -> Task.FromResult(Ok true)
                          Send = fun prepared -> task {
                              sent.Enqueue(prepared.Ready.AgentId)
                              if wasFirst && boundary = "EnteringSend" then pause()
                              return Ok "submitted" } }
                    let! result = executeOnceTracked track ports binding (actor().Value) "local" action.ScriptPath "fixture-source" "fixture-route"
                    match result with
                    | Submitted _ | NoEligibleReady -> return Ok """{"status":"completed"}"""
                    | Held reason | OutcomeUnknown reason -> return Error("", reason)
                    | PublicationUncertain(id, reason) -> return Error("", $"ResumePublicationUncertain:{id:D}:{reason}")
                finally
                    if wasFirst then oldExited.Set()
            }
            let makeService () =
                new BrowserSchedules.Service(path, execute, now=clock, pollMs=25,
                    reconcileResume=Program.reconcileReadySchedule actor rt)
            let first = makeService()
            service <- Some first
            try
                first.Schedule(a.Id, 5, a.Url, a.ProjectName, a.ScriptPath, a.Alias, a.ArgsJson, a.Active,
                    windowRegistration=a.WindowRegistration, closeAfter=false, repeatEverySeconds=60) |> good |> ignore
                first.Start()
                advance 6.
                if boundary = "PublicationUnknown" then
                    waitUntil (fun () ->
                        let status, _, _ = scheduleState first a.Id
                        status = "suspended")
                else
                    Assert.That(interrupted.Wait(TimeSpan.FromSeconds(20.)), Is.True, "Expected durable interruption boundary.")
                let status, oldAttempt, evidence = scheduleState first a.Id
                Assert.That(status, Is.EqualTo(if boundary = "PublicationUnknown" then "suspended" else "running"))
                Assert.That(evidence, Does.StartWith(if boundary = "PublicationUnknown" then "ResumePublicationUncertain:" else "ResumeStage:"))
                let priorOperation = original |> Option.map _.OperationId
                let snapshot = LedgerStore.fetch binding |> good
                match priorOperation with
                | Some id ->
                    Assert.That((LedgerStore.getOperationAt binding snapshot id |> good).IsSome,
                        Is.EqualTo(not uncertainWithoutReceipt))
                | None -> Assert.That(boundary, Is.EqualTo("PreSelection"))
                (first :> IDisposable).Dispose()
                firstRun <- false
                release.Set()
                Assert.That(oldExited.Wait(TimeSpan.FromSeconds(5.)), Is.True)
                if boundary = "PublicationUnknown" then File.Delete(hook)
                if boundary <> "PreSelection" then assign "Other" "other-task"
                use restored = makeService()
                service <- Some restored
                let restoredStatus, restoredAttempt, _ = scheduleState restored a.Id
                Assert.That(restoredAttempt, Is.EqualTo(oldAttempt))
                Assert.That(restoredStatus, Is.EqualTo(if boundary = "PreSelection" then "pending" else "suspended"))
                restored.Start()
                if uncertainWithoutReceipt then
                    advance 120.
                    Thread.Sleep(600)
                    let state, attempt, _ = scheduleState restored a.Id
                    Assert.That(state, Is.EqualTo("suspended"))
                    Assert.That(attempt, Is.EqualTo(oldAttempt))
                    Assert.That(sent.ToArray(), Is.Empty)
                    let after = LedgerStore.fetch binding |> good
                    Assert.That((LedgerStore.getOperationAt binding after priorOperation.Value |> good).IsNone, Is.True)
                else
                    waitUntil (fun () ->
                        let status, attempt, _ = scheduleState restored a.Id
                        status = "pending" && attempt = oldAttempt)
                    advance 61.
                    waitUntil (fun () ->
                        let status, attempt, _ = scheduleState restored a.Id
                        status = "pending" && attempt <> oldAttempt)
                    let expected =
                        if boundary = "PreSelection" then [|"Worker"|]
                        elif boundary = "EnteringSend" then [|"Worker"; "Other"|]
                        else [|"Other"|]
                    Assert.That(sent.ToArray(), Is.EqualTo(expected :> obj))
                    let _, resumedAttempt, _ = scheduleState restored a.Id
                    advance 60.
                    waitUntil (fun () ->
                        let status, attempt, _ = scheduleState restored a.Id
                        status = "pending" && attempt <> resumedAttempt)
                    Assert.That(sent.ToArray(), Is.EqualTo(expected :> obj))
                    match priorOperation with
                    | Some id ->
                        let after = LedgerStore.fetch binding |> good
                        Assert.That(LedgerStore.getOperationAt binding after id |> good,
                            Is.EqualTo(LedgerStore.getOperationAt binding snapshot id |> good))
                    | None -> ()
            finally
                release.Set()
                if not oldExited.IsSet then
                    (first :> IDisposable).Dispose()
                    oldExited.Wait(TimeSpan.FromSeconds(5.)) |> ignore
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior))


[<Test>]
let failedTerminalProofNeverAuthorizesRetry () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let hook = Path.Combine(binding.ApprovedPushUrl, "hooks", "pre-receive")
        let mutable checks = 0
        let mutable sends = 0
        let ports =
            { ReadLimits = readCurrentLimits
              CanSend = fun _ ->
                  checks <- checks + 1
                  if checks = 2 then
                      File.WriteAllText(hook, "#!/bin/sh\nexit 1\n")
                      if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                          File.SetUnixFileMode(hook, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                      Task.FromResult(Ok false)
                  else Task.FromResult(Ok true)
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "unexpected") }
        match run ports binding with
        | OutcomeUnknown reason -> Assert.That(reason, Does.Contain("Non-invocation proof not confirmed"))
        | other -> Assert.Fail(sprintf "Expected uncertain proof, got %A" other)
        File.Delete(hook)
        let healthy = { ports with CanSend = fun _ -> Task.FromResult(Ok true) }
        Assert.That(run healthy binding |> held, Is.True)
        Assert.That(sends, Is.Zero))

[<Test>]
let legacyIntentRemainsFencedWithoutPositiveProof () =
    fixture (fun binding assign ->
        assign "Worker" "ready-task"
        let snapshot = LedgerStore.fetch binding |> good
        let prepared = (prepareAt readCurrentLimits binding snapshot "Fixture" |> good).Value
        let legacyRequest =
            JsonSerializer.Serialize(
                {| ready = prepared.Ready; scriptPath = "launchAgent.js"; sourceSha256 = "script-sha"
                   routeSha256 = "route-sha"; promptSha256 = prepared.Rendered.PromptSha256
                   templateVersion = prepared.Rendered.TemplateVersion
                   templateBlobOid = prepared.Rendered.TemplateBlobOid |})
        let intent : LedgerIntent =
            { OperationId = prepared.OperationId; Actor = "fixture"
              Kind = "ScheduledReadyResumeIntent"; RequestJson = legacyRequest }
        LedgerStore.transact binding intent (fun _ ->
            Ok [{ Path = $"records/scheduled-ready-resume/{prepared.OperationId:N}/intent.json"
                  ExpectedBlobOid = None; Content = legacyRequest }]) |> good |> ignore
        let mutable sends = 0
        let ports =
            { ReadLimits = readCurrentLimits
              CanSend = fun _ -> Task.FromResult(Ok true)
              Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "unexpected") }
        Assert.That(run ports binding |> held, Is.True)
        Assert.That(sends, Is.Zero))

[<Test>]
let automationSwitchIsIndependentAcrossVerifiedProjects () =
    fixture (fun a assignA ->
        fixture (fun b assignB ->
            assignA "Worker" "a-task"
            assignB "Worker" "b-task"
            let mutable sends = 0
            let ports =
                { ReadLimits = readAutomationLimits
                  CanSend = fun _ -> Task.FromResult(Ok true)
                  Send = fun _ -> sends <- sends + 1; Task.FromResult(Ok "submitted") }
            let held = function Held _ -> true | _ -> false
            Assert.That(run ports a |> held, Is.True)
            Assert.That(run ports b |> held, Is.True)
            ProjectCapacityPolicy.updateAutomation a "fixture" (Guid.NewGuid()) 0 true None Map.empty |> good |> ignore
            Assert.That(run ports a, Is.EqualTo(Submitted "submitted"))
            Assert.That(run ports b |> held, Is.True)
            Assert.That(sends, Is.EqualTo(1))
            let snapshot = LedgerStore.fetch a |> good
            Assert.That(readAutomationLimits b snapshot |> Result.isError, Is.True)
            Assert.That(readAutomationLimits a { snapshot with Commit = String('0', 40) } |> Result.isError, Is.True)
            // Restarted caller must read persisted policy, not a process-global opt-in.
            Assert.That(readAutomationLimits a (LedgerStore.fetch a |> good) |> Result.isOk, Is.True)
            ProjectCapacityPolicy.updateAutomation a "fixture" (Guid.NewGuid()) 1 false None Map.empty |> good |> ignore
            Assert.That(run ports a |> held, Is.True)
            Assert.That(sends, Is.EqualTo(1))))
