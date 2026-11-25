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

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-ready-resume-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let bare = Path.Combine(root, "remote.git")
        let local = Path.Combine(root, "local")
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
                  Assert.That(prepared.Rendered.Prompt, Is.EqualTo("Agent Worker, project Fixture. Call inbox."))
                  Task.FromResult(Error "unknown browser receipt") }
        Assert.That(run ports binding, Is.EqualTo(OutcomeUnknown "unknown browser receipt"))
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
        Assert.That(sends, Is.EqualTo(1)))

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
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady))
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
        Assert.That(run ports binding, Is.EqualTo(NoEligibleReady)))

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
