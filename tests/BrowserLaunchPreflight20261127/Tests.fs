module BrowserLaunchPreflight

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

[<TestCase("missingPrompt", "InvalidPrompt");
  TestCase("blankPrompt", "InvalidPrompt");
  TestCase("wrongPromptType", "InvalidPrompt");
  TestCase("invalidStartUrl", "UnexpectedStartUrl");
  TestCase("timeoutHigh", "InvalidTimeout");
  TestCase("timeoutLow", "InvalidTimeout");
  TestCase("missingTarget", "targetAgentId is required");
  TestCase("unknownTarget", "LaunchAttributionNotRecorded");
  TestCase("valid", "");
  TestCase("generic", "");
  TestCase("unknownOutcome", "OutcomeUnknown");
  NonParallelizable>]
let manualPreflight (scenario: string, expected: string) = task {
    let root = Path.Combine(Path.GetTempPath(), "jarvis-browser-preflight-" + Guid.NewGuid().ToString("N"))
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

        let rt = Runtime(Path.GetDirectoryName(first.RepositoryPath), ConsoleTui.ConsoleTui(), FullTrust)
        let scriptPath = if scenario = "generic" then "browser-scripts/generic.js" else "browser-scripts/chatgpt/launchAgent.js"
        let script = Path.Combine(first.RepositoryPath, scriptPath)
        Directory.CreateDirectory(Path.GetDirectoryName script) |> ignore
        if scenario = "generic" then File.WriteAllText(script, "async function execute(args) { return {status: 'completed'}; }")
        else File.Copy(Path.Combine(AppContext.BaseDirectory, "launchAgent.js"), script)
        let source = File.ReadAllText(script)
        let url = "https://chatgpt.com/g/g-p-fixture/project"
        let args =
            match scenario with
            | "missingPrompt" -> None
            | "blankPrompt" -> Some(JsonSerializer.Serialize({| prompt = " "; startUrl = url |}))
            | "wrongPromptType" -> Some(JsonSerializer.Serialize({| prompt = 7; startUrl = url |}))
            | "invalidStartUrl" -> Some(JsonSerializer.Serialize({| prompt = baseline.Rendered.Prompt; startUrl = "https://chatgpt.com/c/fixture" |}))
            | "generic" -> Some """{"arbitrary":true}"""
            | _ -> Some(JsonSerializer.Serialize({| prompt = baseline.Rendered.Prompt; startUrl = url |}))
        let targetId =
            match scenario with
            | "missingTarget" | "generic" -> None
            | "unknownTarget" -> Some "Unregistered"
            | _ -> Some target
        let timeout =
            match scenario with
            | "timeoutHigh" -> Some 60000
            | "timeoutLow" -> Some 0
            | _ -> Some 10000
        let rejected = expected <> "" && scenario <> "unknownOutcome"

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

        let command =
            Common.BrowserRunInNewTabCommand
                { TargetAgentId = targetId; WindowRegistration = "agents"; Url = url
                  ProjectName = "Fixture"; ScriptPath = scriptPath; ArgsJson = args
                  Active = Some false; CloseTabAfterExecution = Some false; TimeoutMs = timeout }
        let paths prefix =
            let snapshot = current()
            git second.RepositoryPath ["ls-tree"; "-r"; "--name-only"; snapshot.Commit; "--"; prefix]
        let pending = readFrame()
        let dispatch = Client.SignalR.Client.processIncomingCommand rt (JsonSerializer.Serialize<Common.AgentCommand>(command)) actor
        let! firstFinished = Task.WhenAny(dispatch :> Task, pending :> Task)
        let openedTab = obj.ReferenceEquals(firstFinished, pending)
        let mutable executed = false
        if openedTab then
            let! opened = pending
            Assert.That(prop opened "kind", Is.EqualTo("openTab"))
            Assert.That(prop opened "url", Is.EqualTo(url))
            if targetId.IsSome then
                Assert.That(paths "records/browser-launches", Does.EndWith("/intent.json"))
            let tab = "fixture-manual-tab"
            do! sendFrame (JsonSerializer.Serialize({| version=2; kind="registrations"; tabs=[| {| registrationId=tab; alias="new"; agentCreated=true; availability="ready"; registrationGeneration=1 |} |] |}))
            do! sendFrame (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop opened "requestId"; connectionId=connection; tab={| registrationId=tab; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
            let! execute = readFrame()
            executed <- true
            Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
            Assert.That(prop execute "source", Is.EqualTo(source))
            use executeDoc = JsonDocument.Parse execute
            let sentArgs = executeDoc.RootElement.GetProperty("args")
            if scenario = "valid" || scenario = "unknownOutcome" then
                Assert.That(sentArgs.GetProperty("prompt").GetString(), Is.EqualTo(baseline.Rendered.Prompt))
                Assert.That(sentArgs.GetProperty("startUrl").GetString(), Is.EqualTo(url))
            if scenario = "generic" then
                Assert.That(sentArgs.GetProperty("arbitrary").GetBoolean(), Is.True)
            // Fake extension acknowledgement only; no page execution or send.
            let status, reason, stage =
                if rejected then "notSubmitted", expected, "preflight"
                elif scenario = "unknownOutcome" then "outcomeUnknown", "SubmissionUnconfirmed", "acknowledgement"
                elif scenario = "generic" then "completed", "", ""
                else "submitted", "", "acknowledgement"
            do! sendFrame (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId=tab; registrationGeneration=1; status="completed"; result={| status=status; reason=reason; stage=stage |} |}))
        let! _, result, _ = dispatch.WaitAsync(deadline.Token)
        let records = paths "records/browser-launches"
        let recordCount = if String.IsNullOrWhiteSpace records then 0 else records.Split('\n').Length
        TestContext.Progress.WriteLine(sprintf "%s: opened=%b executed=%b records=%d result=%A" scenario openedTab executed recordCount result)
        let noReplay = if openedTab then readFrame() else pending
        let! winner = Task.WhenAny(noReplay :> Task, Task.Delay(250, deadline.Token))
        Assert.That(obj.ReferenceEquals(winner, noReplay), Is.False, "Unexpected additional browser effect")
        socket.Abort()
        if rejected then
            Assert.Multiple(fun () ->
                Assert.That(result.IsError, Is.True, "Invalid requests must fail at the dispatcher")
                Assert.That(sprintf "%A" result, Does.Contain(expected))
                Assert.That(openedTab, Is.False, "Invalid requests must not open a tab")
                Assert.That(executed, Is.False, "Invalid requests must not execute a script")
                Assert.That(recordCount, Is.Zero, "Invalid requests must not create attributed launch records"))
        else
            Assert.That(openedTab && executed, Is.True)
            if scenario = "unknownOutcome" then
                Assert.That(result.IsError, Is.True)
                Assert.That(sprintf "%A" result, Does.Contain(expected))
            else
                let raw = result |> success
                Assert.That(prop raw "status", Is.EqualTo(if scenario = "generic" then "completed" else "submitted"))
            Assert.That(recordCount, Is.EqualTo(if scenario = "generic" then 0 else 2))
            if scenario <> "generic" then
                let outcomePath = records.Split('\n') |> Array.find (fun x -> x.EndsWith("/outcome.json"))
                let content, _ = LedgerStore.readAt second (current()) outcomePath |> success
                Assert.That(prop content.Value "outcome", Is.EqualTo(if scenario = "unknownOutcome" then "outcomeUnknown" else "submitted"))
            let after = getAgentWorkingTurnAt second (current()) target |> success
            Assert.That(after.State, Is.EqualTo("Ready"))
            Assert.That(after.Version, Is.EqualTo(before.Version))
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous)
        Directory.Delete(root, true)
}
