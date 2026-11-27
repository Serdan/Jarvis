module AutomationScheduledReconcileWitness

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

let private gitEnvironment () =
    Environment.GetEnvironmentVariables()
    |> Seq.cast<System.Collections.DictionaryEntry>
    |> Seq.choose (fun entry ->
        let name = entry.Key :?> string
        if name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) then
            Some (name, entry.Value :?> string)
        else None)
    |> Map.ofSeq

let private clearGitEnvironment () =
    for KeyValue(name, _) in gitEnvironment () do
        Environment.SetEnvironmentVariable(name, null)

let private withFixtureGitEnvironment root action =
    let inherited = gitEnvironment ()
    try
        clearGitEnvironment ()
        for name, value in
            [ "GIT_CONFIG_GLOBAL", Path.Combine(root, "gitconfig")
              "GIT_CONFIG_NOSYSTEM", "1"
              "GIT_AUTHOR_NAME", "Fixture"
              "GIT_AUTHOR_EMAIL", "fixture@example.test"
              "GIT_COMMITTER_NAME", "Fixture"
              "GIT_COMMITTER_EMAIL", "fixture@example.test" ] do
            Environment.SetEnvironmentVariable(name, value)
        action ()
    finally
        clearGitEnvironment ()
        for KeyValue(name, value) in inherited do
            Environment.SetEnvironmentVariable(name, value)

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-ready-resume-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        withFixtureGitEnvironment root (fun () ->
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
            action binding assign)
    finally
        Directory.Delete(root, true)

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

let private waitUntil (check: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds(15.)
    while not (check()) && DateTime.UtcNow < deadline do Thread.Sleep(25)
    Assert.That(check(), Is.True, "Timed out waiting for fixture state.")

let private scheduleState (service: BrowserSchedules.Service) id =
    use doc = JsonDocument.Parse(service.Get(id) |> good)
    let p name = doc.RootElement.GetProperty(name: string).GetString()
    p "status", p "attemptId", p "error"

let private socketSend (socket: ClientWebSocket) (value: string) = task {
    let bytes = Encoding.UTF8.GetBytes value
    do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
}

let private socketReceive (socket: ClientWebSocket) = task {
    let bytes = Array.zeroCreate<byte> 8192
    use stream = new MemoryStream()
    let mutable complete = false
    while not complete do
        let! result = socket.ReceiveAsync(ArraySegment<byte>(bytes), CancellationToken.None)
        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Text))
        stream.Write(bytes, 0, result.Count)
        Assert.That(stream.Length, Is.LessThanOrEqualTo(1024L * 1024L))
        complete <- result.EndOfMessage
    return Encoding.UTF8.GetString(stream.ToArray())
}

let private prop (raw: string) (key: string) =
    use doc = JsonDocument.Parse raw
    doc.RootElement.GetProperty(key).GetString()

[<TestCase("disconnect"); TestCase("auth"); TestCase("switch"); NonParallelizable>]
let hostedOriginalNoSendOrDisconnect scenario =
    let lateNoSend = scenario <> "disconnect"
    let switchOff = scenario = "switch"
    let inherited = gitEnvironment ()
    if lateNoSend then
        let failure =
            Assert.Throws<InvalidOperationException>(TestDelegate(fun () ->
                fixture (fun _ _ ->
                    Assert.That(Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL"), Is.EqualTo("fixture@example.test"))
                    raise (InvalidOperationException("intentional fixture failure")))))
        Assert.That(failure.Message, Is.EqualTo("intentional fixture failure"))
        Assert.That(gitEnvironment () = inherited, Is.True, "Git environment must be restored after fixture failure.")
    fixture (fun binding assign ->
        let root = Path.GetDirectoryName(binding.RepositoryPath)
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            let a = route "local"
            let script = Path.Combine(binding.RepositoryPath, a.ScriptPath)
            Directory.CreateDirectory(Path.GetDirectoryName script) |> ignore
            File.Copy(Path.Combine(AppContext.BaseDirectory, "launchAgent.js"), script)
            assign "Worker" "ready-task"
            ProjectCapacityPolicy.updateAutomation binding "fixture" (Guid.NewGuid()) 0 true None Map.empty |> good |> ignore
            let rt = Runtime(root, ConsoleTui.ConsoleTui(), Client.FullTrust)
            let head = (LedgerStore.fetch binding |> good).Commit
            LedgerBindingConfig.bindExisting "local" rt binding.ProjectId binding.LedgerId binding.Epoch
                binding.RemoteName binding.ApprovedFetchUrl binding.ApprovedPushUrl head true |> good |> ignore
            let binding, snapshot = LedgerBindingConfig.loadWithSnapshot "local" rt |> good
            let first = (prepareAt readCurrentLimits binding snapshot "local" |> good).Value
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
                let journal = Path.Combine(root, "schedules.json")
                let readOccurrence () =
                    use doc = JsonDocument.Parse(File.ReadAllText(journal))
                    doc.RootElement.GetProperty("actions").EnumerateArray()
                    |> Seq.map (fun item -> JsonSerializer.Deserialize<BrowserSchedules.ScheduledAction>(item.GetRawText()))
                    |> Seq.find (fun item -> item.Id = a.Id)
                let mutable firstSelection = None
                let mutable actorCalls = 0
                let actor () =
                    let n = Interlocked.Increment(&actorCalls)
                    if lateNoSend && n = 3 then
                        let original = readOccurrence()
                        firstSelection <- Some(original, (BrowserSchedules.tryResumeSelection original).Value)
                        if switchOff then
                            ProjectCapacityPolicy.updateAutomation binding "fixture" (Guid.NewGuid()) 1 false None Map.empty |> good |> ignore
                            Some "operator:fixture"
                        else None
                    else Some "operator:fixture"
                use service = new BrowserSchedules.Service(journal,
                    Program.executeReadySchedule actor rt (Some bridge), now=clock, pollMs=25, reconcileResume=Program.reconcileReadySchedule actor rt)
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
                let mutable previous = ""
                let mutable expected = first
                if lateNoSend then
                    previous <- waitOccurrence ""
                    Assert.That(pending.IsCompleted, Is.False)
                    let _, _, error = scheduleState service a.Id
                    Assert.That(error, Does.Contain(if switchOff then "Automation is disabled" else "stopped before Send"))
                    if switchOff then
                        let original, selection = firstSelection.Value
                        let reconciled = Program.reconcileReadySchedule (fun () -> Some "operator:fixture") rt original selection
                        let! confirmed = reconciled
                        Assert.That(confirmed, Is.True, "Switch off must not inhibit original-intent reconciliation.")
                        ProjectCapacityPolicy.updateAutomation binding "fixture" (Guid.NewGuid()) 2 true None Map.empty |> good |> ignore
                    let current = LedgerStore.fetch binding |> good
                    let retry = (prepareAt readCurrentLimits binding current "local" |> good).Value
                    Assert.That(retry.Ready, Is.EqualTo(first.Ready))
                    Assert.That(retry.OperationId, Is.Not.EqualTo(first.OperationId))
                    expected <- retry
                    advance 60.
                let! frame = pending.WaitAsync(TimeSpan.FromSeconds(15.))
                Assert.That(prop frame "kind", Is.EqualTo("openTab"))
                do! socketSend socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-01","alias":"new","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
                do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop frame "requestId"; connectionId=connection; tab={| registrationId="created-01"; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
                let! execute = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
                use request = JsonDocument.Parse execute
                Assert.That(request.RootElement.GetProperty("source").GetString(), Is.EqualTo(File.ReadAllText(script)))
                let args = request.RootElement.GetProperty("args")
                Assert.That(args.GetProperty("prompt").GetString(), Does.StartWith("Agent Worker, project local. Call inbox."))
                Assert.That(args.GetProperty("prompt").GetString(), Does.Contain(expected.OperationId.ToString("D")))
                Assert.That(args.GetProperty("startUrl").GetString(), Is.EqualTo(a.Url))
                Assert.That(args.EnumerateObject() |> Seq.length, Is.EqualTo(2))
                let receiptPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/intent.json"
                let receiptAt id =
                    let snap = LedgerStore.fetch binding |> good
                    (LedgerStore.getOperationAt binding snap id |> good).Value
                let initialReceipt = receiptAt first.OperationId
                let sendReceipt = receiptAt expected.OperationId
                let snap = LedgerStore.fetch binding |> good
                let requestJson, blob = LedgerStore.readAt binding snap (receiptPath first.OperationId) |> good
                Assert.That(blob.IsSome, Is.True)
                let proofPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/not-invoked.json"
                let proofJson, proofBlob = LedgerStore.readAt binding snap (proofPath first.OperationId) |> good
                if lateNoSend then
                    Assert.That(proofBlob.IsSome, Is.True)
                    use proofDocument = JsonDocument.Parse(proofJson.Value)
                    let evidence = proofDocument.RootElement
                    Assert.That(evidence.GetProperty("intentOperationId").GetString(), Is.EqualTo(initialReceipt.OperationId))
                    Assert.That(evidence.GetProperty("intentRequestHash").GetString(), Is.EqualTo(initialReceipt.RequestHash))
                    Assert.That(evidence.GetProperty("intentSequence").GetInt64(), Is.EqualTo(initialReceipt.Sequence))
                    Assert.That(evidence.GetProperty("stage").GetString(), Is.EqualTo("DefinitelyNotInvokedBeforeSend"))
                else
                    Assert.That(proofBlob.IsNone, Is.True)
                let sentProof, _ = LedgerStore.readAt binding snap (proofPath expected.OperationId) |> good
                Assert.That(sentProof.IsNone, Is.True)
                use originalRequest = JsonDocument.Parse(requestJson.Value)
                let originalSourceHash = originalRequest.RootElement.GetProperty("sourceSha256").GetString()
                Assert.That(originalSourceHash, Is.EqualTo(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(script)))).ToLowerInvariant()))
                let bindingHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(binding)))).ToLowerInvariant()
                let sendAction = readOccurrence()
                let sendSelection = (BrowserSchedules.tryResumeSelection sendAction).Value
                Assert.That(sendSelection.Stage, Is.EqualTo("EnteringSend"))
                Assert.That(sendSelection.OperationId, Is.EqualTo(expected.OperationId))
                Assert.That(sendSelection.BindingSha256, Is.EqualTo(bindingHash))
                let sentRequest, _ = LedgerStore.readAt binding snap (receiptPath expected.OperationId) |> good
                Assert.That(sendSelection.RequestJson, Is.EqualTo(sentRequest.Value))
                let originalAction, selection =
                    if lateNoSend then
                        let action, original = firstSelection.Value
                        Assert.That(original.Stage, Is.EqualTo("Published"))
                        action, original
                    else sendAction, sendSelection
                Assert.That(selection.OperationId, Is.EqualTo(first.OperationId))
                Assert.That(selection.BindingSha256, Is.EqualTo(bindingHash))
                Assert.That(selection.Actor, Is.EqualTo("operator:fixture"))
                Assert.That(selection.RequestJson, Is.EqualTo(requestJson.Value))
                let reconcile original = Program.reconcileReadySchedule (fun () -> Some "operator:fixture") rt originalAction original
                let! original = reconcile selection
                Assert.That(original, Is.True)
                let! sentOriginal = Program.reconcileReadySchedule (fun () -> Some "operator:fixture") rt sendAction sendSelection
                Assert.That(sentOriginal, Is.True)
                let! wrong = reconcile { selection with RequestJson = selection.RequestJson.Replace(originalSourceHash, String.replicate 64 "0") }
                Assert.That(wrong, Is.False)
                let! wrongBinding = reconcile { selection with BindingSha256 = String.replicate 64 "0" }
                Assert.That(wrongBinding, Is.False)
                let! wrongOperation = reconcile { selection with OperationId = Guid.NewGuid() }
                Assert.That(wrongOperation, Is.False)
                let! foreignActor = Program.reconcileReadySchedule (fun () -> Some "other") rt originalAction selection
                Assert.That(foreignActor, Is.False)
                if lateNoSend then
                    do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId="created-01"; registrationGeneration=1; status="completed"; result={| status="submitted"; stage="acknowledgement" |} |}))
                    let! close = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
                    Assert.That(prop close "kind", Is.EqualTo("closeTab"))
                    Assert.That(prop close "registrationId", Is.EqualTo("created-01"))
                    do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="tabClosed"; requestId=prop close "requestId"; connectionId=connection; registrationId="created-01"; registrationGeneration=1 |}))
                else
                    socket.Abort()
                let launched = waitOccurrence previous
                let _, _, error = scheduleState service a.Id
                if not lateNoSend then
                    Assert.That(error, Does.Contain("OutcomeUnknown"))
                    // Socket abort may surface as IOException or ExtensionDisconnected; the stable contract is OutcomeUnknown.
                else Assert.That(error, Is.EqualTo("completed: "))
                Assert.That(receiptAt first.OperationId, Is.EqualTo(initialReceipt))
                Assert.That(receiptAt expected.OperationId, Is.EqualTo(sendReceipt))
                let after = LedgerStore.fetch binding |> good
                Assert.That((prepareAt readCurrentLimits binding after "local" |> good).IsNone, Is.True)
                // A changed launcher and route identity cannot create a new attempt
                // for the same consumed Ready episode, even after disconnect.
                File.AppendAllText(script, "\n// changed source")
                // A disconnected bridge is not enough to prove no replay:
                // restore an authenticated available fake window first.
                use replacement = new ClientWebSocket()
                if not lateNoSend then
                    waitUntil (fun () -> bridge.ListWindows().IsError)
                    replacement.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                    do! replacement.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"), CancellationToken.None)
                    let! challenge = (socketReceive replacement).WaitAsync(TimeSpan.FromSeconds(5.))
                    let response = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(prop challenge "nonce"))).ToLowerInvariant()
                    do! socketSend replacement (JsonSerializer.Serialize({| version=2; kind="authenticate"; proof=response |}))
                    let! _ = (socketReceive replacement).WaitAsync(TimeSpan.FromSeconds(5.))
                    do! socketSend replacement """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-02","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
                    waitUntil (fun () -> bridge.ListWindows() |> Result.exists (fun s -> s.Contains("window-02")))
                let nextFrame = socketReceive (if lateNoSend then socket else replacement)
                service.Schedule(a.Id, 5, "https://chatgpt.com/g/g-p-other/project", a.ProjectName, a.ScriptPath, a.Alias, a.ArgsJson, a.Active,
                    windowRegistration=a.WindowRegistration, closeAfter=true, repeatEverySeconds=60) |> good |> ignore
                advance 6.
                waitOccurrence launched |> ignore
                Assert.That(nextFrame.IsCompleted, Is.False, "Changed route/source must not replay an already invoked episode.")
                let _, _, changedError = scheduleState service a.Id
                Assert.That(changedError, Does.StartWith("heldLaunches:"))
                Assert.That(receiptAt first.OperationId, Is.EqualTo(initialReceipt))
                Assert.That(receiptAt expected.OperationId, Is.EqualTo(sendReceipt))
                let latest = LedgerStore.fetch binding |> good
                Assert.That((prepareAt readCurrentLimits binding latest "local" |> good).IsNone, Is.True)
                Assert.That(LedgerStore.readAt binding latest (proofPath first.OperationId) |> good, Is.EqualTo((proofJson, proofBlob)))
                Assert.That(LedgerStore.readAt binding latest (proofPath expected.OperationId) |> good |> fst, Is.EqualTo(sentProof))
            }).GetAwaiter().GetResult()
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior))
    Assert.That(gitEnvironment () = inherited, Is.True, "Git environment must be restored after fixture success.")


