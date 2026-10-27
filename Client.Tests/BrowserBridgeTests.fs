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
    do! send socket (JsonSerializer.Serialize({| version = 1; kind = "authenticate"; proof = signature |}))
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
    do! send socket """{"version":1,"kind":"registrations","tabs":[{"registrationId":"registered-01","alias":"agent-7","availability":"ready","registrationGeneration":1,"boundUrl":"https://example.test/one"},{"registrationId":"registered-02","alias":"agent-8","availability":"discarded","registrationGeneration":1,"boundUrl":"https://example.test/two"}]}"""
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
        version = 1; kind = "result"; requestId = requestId
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
    do! send client """{"version":1,"kind":"authenticate","proof":"not-a-proof"}"""
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
    do! send socket """{"version":1,"kind":"registrations","tabs":[{"registrationId":"registered-01","alias":"agent-7","availability":"ready","registrationGeneration":1},{"registrationId":"registered-02","alias":"agent-8","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        match bridge.ListTabs() with
        | Ok json -> json.Contains("agent-7") && json.Contains("agent-8")
        | Error _ -> false)

    let first = bridge.Execute("agent-7", "document.title", "{}", 2000, true)
    let second = bridge.Execute("agent-8", "document.title", "{}", 2000, true)
    let! frameA = (receive socket).WaitAsync(TimeSpan.FromSeconds(3))
    let! frameB = (receive socket).WaitAsync(TimeSpan.FromSeconds(3))
    let frames = [frameA;frameB]
    let find id = frames |> List.find (fun frame -> property frame "registrationId" = id)
    let response id value =
        JsonSerializer.Serialize({|
            version = 1; kind = "result"; requestId = property (find id) "requestId"
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
    let! errorFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3))
    let errorReply = JsonSerializer.Serialize({|
        version = 1; kind = "error"; requestId = property errorFrame "requestId"
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
    do! send socket """{"version":1,"kind":"registrations","tabs":[{"registrationId":"registered-01","alias":"agent-7","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        match bridge.ListTabs() with
        | Ok json -> json.Contains("agent-7")
        | _ -> false)
    let execution = bridge.Execute("agent-7", "submitSomething()", "{}", 100, true)
    let! frame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3))
    Assert.That(property frame "kind", Is.EqualTo("executeJs"))
    let! result = execution
    match result with
    | Error e -> Assert.That(e.Contains("OutcomeUnknown"), Is.True)
    | Ok _ -> Assert.Fail("A timeout cannot claim a side-effecting action succeeded")
}
