module BrowserBridgeTests

open System
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Client
open NUnit.Framework

let private findPort () =
    use socket = new TcpListener(IPAddress.Loopback, 0)
    socket.Start()
    let port = (socket.LocalEndpoint :?> IPEndPoint).Port
    socket.Stop()
    port

let private send (socket: ClientWebSocket) (value: string) = task {
    let bytes = Encoding.UTF8.GetBytes(value)
    do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
}

let private receive (socket: ClientWebSocket) = task {
    let buffer = Array.zeroCreate<byte> 8192
    use stream = new IO.MemoryStream()
    let mutable finished = false
    while not finished do
        let! part = socket.ReceiveAsync(ArraySegment<byte>(buffer), CancellationToken.None)
        if part.MessageType = WebSocketMessageType.Close then failwith "Socket closed"
        stream.Write(buffer, 0, part.Count)
        finished <- part.EndOfMessage
    return Encoding.UTF8.GetString(stream.ToArray())
}

let private property (doc: string) (name: string) =
    use json = JsonDocument.Parse(doc)
    json.RootElement.GetProperty(name).GetString()

let private handshake (port: int) (secret: string) = task {
    let socket = new ClientWebSocket()
    socket.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    do! socket.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"), CancellationToken.None)
    let! challenge = receive socket
    let nonce = property challenge "nonce"
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
    let signature =
        Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant()
    do! send socket (JsonSerializer.Serialize({| version = 2; kind = "authenticate"; proof = signature |}))
    let! ready = receive socket
    Assert.That(property ready "kind", Is.EqualTo("ready"))
    return socket, property ready "connectionId"
}

let private waitUntil condition = task {
    let mutable valid = false
    for _ in 1..40 do
        if condition() then valid <- true
        elif not valid then do! Task.Delay(25)
    Assert.That(valid, Is.True, "Timed out awaiting registry synchronization")
}

[<Test>]
let pairingAndExecution () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    Assert.That(bridge.ListTabs().IsError, Is.True)
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"registered-01","alias":"agent-7","availability":"ready","registrationGeneration":1,"boundUrl":"https://example.test/one"},{"registrationId":"registered-02","alias":"agent-8","availability":"discarded","registrationGeneration":1,"boundUrl":"https://example.test/two"}]}"""
    do! waitUntil (fun () ->
        match bridge.ListTabs() with
        | Ok json -> json.Contains("agent-7") && json.Contains("agent-8")
        | Error _ -> false)
    let runnable = bridge.Execute("agent-7", "async function execute(args) { return document.title; }", """{"key":"value"}""", 3000, false)
    let! invocation = receive socket
    Assert.That(property invocation "kind", Is.EqualTo("executeJs"))
    Assert.That(property invocation "registrationId", Is.EqualTo("registered-01"))
    Assert.That(property invocation "entrypoint", Is.EqualTo("execute"))
    let requestId = property invocation "requestId"
    let result = JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = requestId
        connectionId = connectionId
        registrationId = "registered-01"; registrationGeneration = 1
        status = "completed"; result = {| title = "Test page" |}
    |})
    do! send socket result
    let! outcome = runnable
    match outcome with
    | Error error -> Assert.Fail("Expected successful execution, got " + error)
    | Ok value -> Assert.That(value.Contains("Test page"), Is.True)
    let! discarded = bridge.Execute("agent-8", "document.title", "{}", 3000, true)
    match discarded with
    | Error text -> Assert.That(text, Is.EqualTo("TabUnavailable:discarded"))
    | Ok _ -> Assert.Fail("Discarded tab should not execute")
    let! unknown = bridge.Execute("unregistered", "document.title", "{}", 3000, true)
    match unknown with
    | Error text -> Assert.That(text, Is.EqualTo("TabNotRegistered"))
    | Ok _ -> Assert.Fail("Unregistered tab should not execute")
}

[<Test>]
let invalidPairing () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    use client = new ClientWebSocket()
    client.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    do! client.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"), CancellationToken.None)
    let! challenge = receive client
    Assert.That(property challenge "kind", Is.EqualTo("challenge"))
    do! send client """{"version":2,"kind":"authenticate","proof":"not-a-proof"}"""
    do! Task.Delay(50)
    Assert.That(bridge.ListTabs().IsError, Is.True)
}

[<Test>]
let invalidOrigin () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    use socket = new ClientWebSocket()
    socket.Options.SetRequestHeader("Origin", "https://attacker.example")
    try
        do! socket.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"), CancellationToken.None)
        Assert.Fail("Hostile origin should have been rejected")
    with :? WebSocketException -> ()
}

[<Test>]
let concurrentRequestsAndErrors () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"registered-01","alias":"agent-7","availability":"ready","registrationGeneration":1},{"registrationId":"registered-02","alias":"agent-8","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        match bridge.ListTabs() with
        | Ok json -> json.Contains("agent-7") && json.Contains("agent-8")
        | Error _ -> false)

    let first = bridge.Execute("agent-7", "document.title", "{}", 2000, true)
    let second = bridge.Execute("agent-8", "document.title", "{}", 2000, true)
    let! frameA = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    let! frameB = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    let frames = [frameA;frameB]
    let find id = frames |> List.find (fun frame -> property frame "registrationId" = id)
    let response id value =
        JsonSerializer.Serialize({|
            version = 2; kind = "result"; requestId = property (find id) "requestId"
            connectionId = connectionId; registrationId = id; registrationGeneration = 1
            status = "completed"; result = value
        |})
    do! send socket (response "registered-02" "response-from-eight")
    do! send socket (response "registered-01" "response-from-seven")
    let! a = first
    let! b = second
    match a,b with
    | Ok a,Ok b ->
        Assert.That(a.Contains("response-from-seven"), Is.True)
        Assert.That(b.Contains("response-from-eight"), Is.True)
    | _ -> Assert.Fail("Expected independent in-order results for both tabs")

    let errorTask = bridge.Execute("agent-7", "invalid()", "{}", 2000, true)
    let! errorFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    let errorReply = JsonSerializer.Serialize({|
        version = 2; kind = "error"; requestId = property errorFrame "requestId"
        connectionId = connectionId; registrationId = "registered-01"
        registrationGeneration = 1; error = "ScriptError:Failed"
    |})
    do! send socket errorReply
    let! rejected = errorTask
    match rejected with
    | Error e -> Assert.That(e.Contains("BrowserExecutionError"), Is.True)
    | Ok _ -> Assert.Fail("Extension execution errors must not be successful MCP results")
}

[<Test>]
let timedOutMutationIsNotReplayed () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, _ = handshake port "test-secret"
    use socket = client
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"registered-01","alias":"agent-7","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        match bridge.ListTabs() with
        | Ok json -> json.Contains("agent-7")
        | _ -> false)
    let execution = bridge.Execute("agent-7", "submitSomething()", "{}", 100, true)
    let! frame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property frame "kind", Is.EqualTo("executeJs"))
    let! result = execution
    match result with
    | Error e -> Assert.That(e.Contains("OutcomeUnknown"), Is.True)
    | Ok _ -> Assert.Fail("A timeout cannot claim a side-effecting action succeeded")
}


[<Test>]
let openTabUsesPairedExtensionAndReturnsRegistration () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client

    let opening = bridge.OpenTab("agents", "https://example.test/chat/new", "agent-new", false)
    let! request = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property request "kind", Is.EqualTo("openTab"))
    Assert.That(property request "url", Is.EqualTo("https://example.test/chat/new"))
    Assert.That(property request "windowRegistration", Is.EqualTo("agents"))
    Assert.That(property request "alias", Is.EqualTo("agent-new"))
    use requestDoc = JsonDocument.Parse(request)
    Assert.That(requestDoc.RootElement.GetProperty("active").GetBoolean(), Is.False)
    let response = JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"
        requestId = property request "requestId"; connectionId = connectionId
        tab = {| registrationId = "created-id-01"; alias = "agent-new"; availability = "loading"; agentCreated = true |}
    |})
    do! send socket response
    let! result = opening
    match result with
    | Ok json -> Assert.That(json.Contains("created-id-01"), Is.True)
    | Error error -> Assert.Fail("Expected new tab registration: " + error)

    let second = bridge.OpenTab("agents", "https://example.test/chat/second", "agent-new-2", false)
    let! request2 = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "error"; connectionId = connectionId
        requestId = property request2 "requestId"; error = "AgentCreatedTabsDisabled"
    |}))
    let! rejected = second
    match rejected with
    | Error error -> Assert.That(error, Does.Contain("AgentCreatedTabsDisabled"))
    | Ok _ -> Assert.Fail("Extension denial must not be reported as tab creation success")
}

[<Test>]
let openTabRejectsUnsafeUrlsBeforeDispatch () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    for address in [ "javascript:alert(1)"; "file:///etc/passwd"; "https://user:secret@example.test/";
                     "/relative"; "data:text/html,hi" ] do
        let! result = bridge.OpenTab("agents", address, "", false)
        match result with
        | Error message -> Assert.That(message, Does.StartWith("InvalidTabUrl"))
        | Ok _ -> Assert.Fail("Unsafe URL accepted: " + address)
}


[<Test>]
let immediateLaunchUsesRegisteredWindowAndClosesOnlyCreatedTab () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! send socket """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-01","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        match bridge.ListWindows() with
        | Ok json -> json.Contains("agents")
        | _ -> false)
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { return {status:'submitted'}; }" """{"prompt":"Go"}""" 5000 true
    let! openedRequest = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openedRequest "kind", Is.EqualTo("openTab"))
    Assert.That(property openedRequest "windowRegistration", Is.EqualTo("agents"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-tab-01","alias":"agent-new","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openedRequest "requestId"
        connectionId = connectionId
        tab = {| registrationId = "created-tab-01"; availability = "ready"; agentCreated = true |}
    |}))
    let! executeFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeFrame "kind", Is.EqualTo("executeJs"))
    Assert.That(property executeFrame "registrationId", Is.EqualTo("created-tab-01"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeFrame "requestId"
        connectionId = connectionId; registrationId = "created-tab-01"
        registrationGeneration = 1; status = "completed"
        result = {| status = "submitted"; conversationUrl = "https://chatgpt.com/c/test" |}
    |}))
    let! closeFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closeFrame "kind", Is.EqualTo("closeTab"))
    Assert.That(property closeFrame "registrationId", Is.EqualTo("created-tab-01"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabClosed"; requestId = property closeFrame "requestId"
        connectionId = connectionId; registrationId = "created-tab-01"
        registrationGeneration = 1
    |}))
    let! result = launch
    match result with
    | Error (_, reason) -> Assert.Fail("Expected successful launch: " + reason)
    | Ok json ->
        Assert.That(json, Does.Contain("submitted"))
        Assert.That(json, Does.Contain("closed"))
        Assert.That(json, Does.Contain("https://chatgpt.com/c/test"))
}
