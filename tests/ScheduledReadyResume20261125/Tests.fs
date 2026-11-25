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
      CloseTabAfterExecution = false; RepeatEverySeconds = 60; Status = "running"
      RegistrationId = ""; ConversationUrl = ""; Cleanup = ""; AttemptId = "attempt"; RevisionId = "revision"
      Error = ""; UpdatedAtUtc = DateTimeOffset.UtcNow; ActionKind = "BrowserScript"; TriggerKind = "Every"
      Executable = ""; CommandArgs = []; WorkingDirectory = ""; TimeoutSeconds = 30; MaxOutputBytes = 100
      IdleAgentId = ""; IdleSeconds = 0; LastActivityAtUtc = DateTimeOffset.MinValue
      StartedAtUtc = DateTimeOffset.UtcNow; Disabled = false; LastResult = "" }

[<Test>]
let onlyExplicitRecurringRoute () =
    let a = route "Fixture"
    Assert.That(BrowserSchedules.isReadyResume a, Is.True)
    Assert.That(validateRoute a, Is.EqualTo(Ok () : Result<unit,string>))
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
        Assert.That((ReviewWorkflow.getResponseAlerts binding "Worker" |> good).Pending.Length, Is.EqualTo(1)))

let private waitUntil (check: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds(15.)
    while not (check()) && DateTime.UtcNow < deadline do Thread.Sleep(25)
    Assert.That(check(), Is.True, "Timed out waiting for fixture state.")

let private scheduleState (service: BrowserSchedules.Service) id =
    use doc = JsonDocument.Parse(service.Get(id) |> good)
    let p name = doc.RootElement.GetProperty(name: string).GetString()
    p "status", p "attemptId", p "error"

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
                let actor () = Some "operator:fixture"
                use service = new BrowserSchedules.Service(Path.Combine(root, "schedules.json"),
                    Program.executeReadySchedule actor rt (Some bridge), now=clock, pollMs=25)
                rt.BrowserScheduler <- Some service
                service.Schedule(a.Id, 5, a.Url, a.ProjectName, a.ScriptPath, a.Alias, a.ArgsJson, a.Active,
                    windowRegistration=a.WindowRegistration, closeAfter=false, repeatEverySeconds=60) |> good |> ignore
                let waitOccurrence old =
                    waitUntil (fun () ->
                        let status, attempt, _ = scheduleState service a.Id
                        status = "pending" && attempt <> old && not (String.IsNullOrWhiteSpace attempt))
                    let _, attempt, _ = scheduleState service a.Id
                    attempt
                service.Start()
                let pending = socketReceive socket
                advance 6.
                let capped = waitOccurrence ""
                Assert.That(pending.IsCompleted, Is.False, "Enabled cap must produce no browser effect.")
                ProjectCapacityPolicy.updateOptional binding "fixture" (Guid.NewGuid()) 1 None Map.empty
                |> good |> ignore
                advance 60.
                let! frame = pending.WaitAsync(TimeSpan.FromSeconds(15.))
                Assert.That(prop frame "kind", Is.EqualTo("openTab"))
                do! socketSend socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-01","alias":"new","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop frame "requestId"; connectionId=connection; tab={| registrationId="created-01"; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
                let! execute = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
                use request = JsonDocument.Parse execute
                let args = request.RootElement.GetProperty("args")
                Assert.That(args.GetProperty("prompt").GetString(), Is.EqualTo("Agent Worker, project local. Call inbox."))
                Assert.That(args.GetProperty("startUrl").GetString(), Is.EqualTo(a.Url))
                Assert.That(args.EnumerateObject() |> Seq.length, Is.EqualTo(2))
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId="created-01"; registrationGeneration=1; status="completed"; result={| status="outcomeUnknown"; stage="acknowledgement"; reason="SubmissionUnconfirmed" |} |}))
                let launched = waitOccurrence capped
                let _, _, error = scheduleState service a.Id
                Assert.That(error, Does.Contain("OutcomeUnknown"))
                let noReplay = socketReceive socket
                advance 60.
                waitOccurrence launched |> ignore
                Assert.That(noReplay.IsCompleted, Is.False, "Original Ready episode must never replay after unknown send.")
            }).GetAwaiter().GetResult()
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior))
