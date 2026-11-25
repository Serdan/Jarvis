module AutomationFixedJointBrowser

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
open AutomationHealthHostedFixture

let private good = success

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



/// Drive the real recurring scheduler and the production caller against a loopback
/// fake extension. Only the remote browser effect is replaced.
let withScheduler (root: string) (rt: Runtime) (project: string) (repository: string) (run: (unit -> string) -> (string -> string -> string -> Task<string>) -> (string -> string -> Task<unit>) -> Task<unit>) = task {
    let a = route project
    let script = Path.Combine(repository, a.ScriptPath)
    Directory.CreateDirectory(Path.GetDirectoryName script) |> ignore
    File.Copy(Path.Combine(AppContext.BaseDirectory, "launchAgent.js"), script)
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
    use service = new BrowserSchedules.Service(Path.Combine(root, "joint-schedules.json"),
        Program.executeReadySchedule actor rt (Some bridge), now=clock, pollMs=25)
    rt.BrowserScheduler <- Some service
    service.Schedule(a.Id, 5, a.Url, a.ProjectName, a.ScriptPath, a.Alias, a.ArgsJson, a.Active,
        windowRegistration=a.WindowRegistration, closeAfter=false, repeatEverySeconds=60) |> good |> ignore
    let mutable occurrence = ""
    let mutable seconds = 6.
    let mutable pending = socketReceive socket
    let tick () =
        advance seconds
        seconds <- 60.
    let finish () =
        waitUntil (fun () ->
            let status, attempt, _ = scheduleState service a.Id
            status = "pending" && attempt <> occurrence && not (String.IsNullOrWhiteSpace attempt))
        let _, attempt, error = scheduleState service a.Id
        occurrence <- attempt
        error
    let noEffect () =
        tick()
        let error = finish()
        Assert.That(pending.IsCompleted, Is.False, "Unexpected browser frame in held occurrence: " + error)
        error
    let send (expectedPrompt: string) (tab: string) (status: string) = task {
        tick()
        let! frame = pending.WaitAsync(TimeSpan.FromSeconds(15.))
        Assert.That(prop frame "kind", Is.EqualTo("openTab"))
        do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="registrations"; tabs=[| {| registrationId=tab; alias="new"; agentCreated=true; availability="ready"; registrationGeneration=1 |} |] |}))
        do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop frame "requestId"; connectionId=connection; tab={| registrationId=tab; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
        let! execute = (socketReceive socket).WaitAsync(TimeSpan.FromSeconds(5.))
        Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
        use request = JsonDocument.Parse execute
        Assert.That(request.RootElement.GetProperty("source").GetString(), Is.EqualTo(File.ReadAllText(script)))
        let args = request.RootElement.GetProperty("args")
        Assert.That(args.GetProperty("prompt").GetString(), Is.EqualTo(expectedPrompt))
        Assert.That(args.GetProperty("startUrl").GetString(), Is.EqualTo(a.Url))
        Assert.That(args.EnumerateObject() |> Seq.length, Is.EqualTo(2))
        do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId=tab; registrationGeneration=1; status="completed"; result={| status=status; stage="acknowledgement"; reason="SubmissionUnconfirmed" |} |}))
        let error = finish()
        pending <- socketReceive socket
        return error
    }
    let refuseManual actor target = task {
        let command =
            Common.BrowserRunInNewTabCommand
                { TargetAgentId = Some target; WindowRegistration = "agents"; Url = a.Url
                  ProjectName = project; ScriptPath = a.ScriptPath; ArgsJson = None
                  Active = None; CloseTabAfterExecution = None; TimeoutMs = Some 5000 }
        let dispatch = Client.SignalR.Client.processIncomingCommand rt
                           (JsonSerializer.Serialize<Common.AgentCommand>(command)) actor
        let! firstFinished = Task.WhenAny(dispatch :> Task, pending :> Task)
        if obj.ReferenceEquals(firstFinished, dispatch) then
            let! _, result, _ = dispatch
            failwithf "Manual dispatch stopped before fake browser: %A" result
        let! frame = pending.WaitAsync(TimeSpan.FromSeconds(10.))
        Assert.That(prop frame "kind", Is.EqualTo("openTab"))
        do! socketSend socket (JsonSerializer.Serialize({| version=2; kind="error"; connectionId=connection; requestId=prop frame "requestId"; error="FixtureRefusedOpen" |}))
        let! _, result, _ = dispatch.WaitAsync(TimeSpan.FromSeconds(10.))
        Assert.That(sprintf "%A" result, Does.Contain("FixtureRefusedOpen"))
        pending <- socketReceive socket
    }
    service.Start()
    try
        do! run noEffect send refuseManual
    finally
        rt.BrowserScheduler <- None
        rt.Browser <- None
}
