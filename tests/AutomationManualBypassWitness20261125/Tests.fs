module AutomationManualBypassWitness

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ReviewWorkflow.ReviewWorkflow
open Client.RoleInstructions
open AutomationHealthHostedFixture

let private prop (raw: string) (name: string) =
    use doc = JsonDocument.Parse raw
    doc.RootElement.GetProperty(name).GetString()

let private send (socket: ClientWebSocket) (ct: CancellationToken) (raw: string) =
    socket.SendAsync(ArraySegment<byte>(Encoding.UTF8.GetBytes raw), WebSocketMessageType.Text, true, ct)

let private receive (socket: ClientWebSocket) (ct: CancellationToken) = task {
    use stream = new MemoryStream()
    let bytes = Array.zeroCreate<byte> 8192
    let mutable finished = false
    while not finished do
        let! part = socket.ReceiveAsync(ArraySegment<byte>(bytes), ct)
        Assert.That(part.MessageType, Is.EqualTo(WebSocketMessageType.Text))
        stream.Write(bytes, 0, part.Count)
        Assert.That(stream.Length, Is.LessThanOrEqualTo(1024L * 1024L))
        finished <- part.EndOfMessage
    return Encoding.UTF8.GetString(stream.ToArray())
}

[<TestCase(false); TestCase(true); NonParallelizable>]
let attributedManualSubmissionIgnoresSchedulerCapacity (unavailable: bool) = task {
    let root = Path.Combine(Path.GetTempPath(), "jarvis-manual-bypass-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
    try
        let first, second, _, _, _ = create root
        let actor = "Worker" // existing canonical Working agent
        let target = "Target"
        registerAgent second (Guid.NewGuid()) target |> success |> ignore
        createTask second (Guid.NewGuid()) "target-task" "Ready target" |> success |> ignore
        assignTask second (Guid.NewGuid()) "target-task" (Guid.NewGuid()) target |> success |> ignore
        RoleInstructions.setLaunchTemplate second "fixture" (Guid.NewGuid()) 0
            "Agent {{agentId}}, project {{projectName}}. Call inbox." |> success |> ignore

        let current () = LedgerStore.fetch second |> success
        let prepare () = ScheduledReadyResume.prepareAt ScheduledReadyResume.readCurrentLimits second (current()) "Fixture"
        let baseline = prepare() |> success |> Option.defaultWith (fun () -> failwith "Expected eligible Ready target before cap")
        Assert.That(baseline.Ready.AgentId, Is.EqualTo(target))
        let before = getAgentWorkingTurnAt second (current()) target |> success
        Assert.That(before.State, Is.EqualTo("Ready"))
        Assert.That((getAgentWorkingTurnAt second (current()) actor |> success).State, Is.EqualTo("Working"))

        ProjectCapacityPolicy.updateOptional second "fixture" (Guid.NewGuid()) 0 (Some 1) Map.empty |> success |> ignore
        Assert.That((prepare() |> success).IsNone, Is.True, "Scheduler must hold at canonical Working cap")
        if unavailable then
            // Corrupt ONLY the disposable cap record, keeping ledger/assignment/identity available.
            // Missing policy means disabled, not unavailable; require an actual reader error.
            let path = "records/project-capacity-policy/policy.json"
            let _, oid = LedgerStore.readAt second (current()) path |> success
            Assert.That(oid.IsSome, Is.True)
            let intent : LedgerIntent =
                { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "FixtureMalformedCapacity"; RequestJson = "{}" }
            LedgerStore.transact second intent (fun _ ->
                Ok [{ Path = path; ExpectedBlobOid = oid; Content = "{}" }]) |> success |> ignore
            Assert.That((ScheduledReadyResume.readCurrentLimits second (current())).IsError, Is.True)
            Assert.That((prepare()).IsError, Is.True, "Scheduler must not reinterpret unavailable data as disabled")

        let rt = Runtime(Path.GetDirectoryName(first.RepositoryPath), ConsoleTui.ConsoleTui(), FullTrust)
        let scriptPath = "browser-scripts/chatgpt/launchAgent.js"
        let script = Path.Combine(first.RepositoryPath, scriptPath)
        Directory.CreateDirectory(Path.GetDirectoryName script) |> ignore
        File.Copy(Path.Combine(AppContext.BaseDirectory, "launchAgent.js"), script)
        let source = File.ReadAllText(script)
        let url = "https://chatgpt.com/g/g-p-fixture/project"
        let args = JsonSerializer.Serialize({| prompt = baseline.Rendered.Prompt; startUrl = url |})

        let port =
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start()
            (listener.LocalEndpoint :?> IPEndPoint).Port
        let secret = "disposable-manual-bypass"
        use bridge = new BrowserBridge.Service(port, secret)
        bridge.Start()
        rt.Browser <- Some bridge
        use socket = new ClientWebSocket()
        socket.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60.))
        let sendFrame = send socket deadline.Token
        let readFrame () = receive socket deadline.Token
        do! socket.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/" port), deadline.Token)
        let! challenge = readFrame()
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
        let proof = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(prop challenge "nonce"))).ToLowerInvariant()
        do! sendFrame (JsonSerializer.Serialize({| version=2; kind="authenticate"; proof=proof |}))
        let! ready = readFrame()
        Assert.That(prop ready "kind", Is.EqualTo("ready"))
        let connection = prop ready "connectionId"
        do! sendFrame """{"version":2,"kind":"windows","windows":[{"windowRegistration":"fixture-window","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
        let mutable registered = false
        for _ in 1..100 do
            if not registered then
                registered <- bridge.ListWindows() |> Result.exists (fun x -> x.Contains("fixture-window"))
                if not registered then do! Task.Delay(25, deadline.Token)
        Assert.That(registered, Is.True)

        let launch targetId =
            let command =
                Common.BrowserRunInNewTabCommand
                    { TargetAgentId = targetId; WindowRegistration = "agents"; Url = url
                      ProjectName = "Fixture"; ScriptPath = scriptPath; ArgsJson = Some args
                      Active = Some false; CloseTabAfterExecution = Some false; TimeoutMs = Some 10000 }
            Client.SignalR.Client.processIncomingCommand rt (JsonSerializer.Serialize<Common.AgentCommand>(command)) actor
        let paths prefix =
            let snapshot = current()
            git second.RepositoryPath ["ls-tree"; "-r"; "--name-only"; snapshot.Commit; "--"; prefix]
        let pending = readFrame()
        let! _, missing, _ = launch None
        Assert.That(missing.IsError, Is.True)
        Assert.That(sprintf "%A" missing, Does.Contain("targetAgentId is required"))
        let! _, unregistered, _ = launch (Some "Unregistered")
        Assert.That(unregistered.IsError, Is.True)
        Assert.That(sprintf "%A" unregistered, Does.Contain("LaunchAttributionNotRecorded"))
        Assert.That(paths "records/browser-launches", Is.Empty)
        Assert.That(pending.IsCompleted, Is.False, "Identity guards must emit no browser request")

        let dispatch = launch (Some target)
        let! firstFinished = Task.WhenAny(dispatch :> Task, pending :> Task)
        if obj.ReferenceEquals(firstFinished, dispatch) then
            let! _, result, _ = dispatch
            failwithf "Manual dispatch stopped before browser: %A" result
        let! opened = pending
        Assert.That(prop opened "kind", Is.EqualTo("openTab"))
        Assert.That(prop opened "url", Is.EqualTo(url))
        // Real intent is durable before the external effect.
        let intentPath = paths "records/browser-launches"
        Assert.That(intentPath, Does.EndWith("/intent.json"))
        Assert.That(intentPath.Contains("\n"), Is.False)
        let tab = "fixture-manual-tab"
        do! sendFrame (JsonSerializer.Serialize({| version=2; kind="registrations"; tabs=[| {| registrationId=tab; alias="new"; agentCreated=true; availability="ready"; registrationGeneration=1 |} |] |}))
        do! sendFrame (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop opened "requestId"; connectionId=connection; tab={| registrationId=tab; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
        let! execute = readFrame()
        Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
        Assert.That(prop execute "source", Is.EqualTo(source))
        use executeDoc = JsonDocument.Parse execute
        let sentArgs = executeDoc.RootElement.GetProperty("args")
        Assert.That(sentArgs.GetProperty("prompt").GetString(), Is.EqualTo(baseline.Rendered.Prompt))
        Assert.That(sentArgs.GetProperty("startUrl").GetString(), Is.EqualTo(url))
        // Only the remote extension/provider acknowledgement is faked.
        do! sendFrame (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId=tab; registrationGeneration=1; status="completed"; result={| status="submitted"; stage="acknowledgement" |} |}))
        let! _, result, _ = dispatch.WaitAsync(deadline.Token)
        let raw = result |> success
        Assert.That(prop raw "status", Is.EqualTo("submitted"))
        Assert.That(prop raw "registrationId", Is.EqualTo(tab))

        let files = (paths "records/browser-launches").Split('\n')
        Assert.That(files.Length, Is.EqualTo(2))
        let outcomePath = files |> Array.find (fun x -> x.EndsWith("/outcome.json"))
        let read path =
            let content, _ = LedgerStore.readAt second (current()) path |> success
            content.Value
        let intent = read intentPath
        let outcome = read outcomePath
        Assert.That(prop intent "targetAgentId", Is.EqualTo(target))
        Assert.That(prop intent "taskId", Is.EqualTo("target-task"))
        Assert.That(prop intent "launchId", Is.EqualTo(prop outcome "launchId"))
        Assert.That(prop intent "executedSourceSha256", Is.EqualTo(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes source)).ToLowerInvariant()))
        Assert.That(prop outcome "outcome", Is.EqualTo("submitted"))
        Assert.That(prop outcome "registrationId", Is.EqualTo(tab))
        Assert.That(paths "records/scheduled-ready-resume", Is.Empty)
        let after = getAgentWorkingTurnAt second (current()) target |> success
        Assert.That(after.State, Is.EqualTo("Ready"), "Browser submission is not assignment activation")
        Assert.That(after.Version, Is.EqualTo(before.Version))
        let noReplay = readFrame()
        let! winner = Task.WhenAny(noReplay :> Task, Task.Delay(250, deadline.Token))
        Assert.That(obj.ReferenceEquals(winner, noReplay), Is.False, "No second browser effect in the completed invocation")
        socket.Abort()
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous)
        Directory.Delete(root, true)
}
