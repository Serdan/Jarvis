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
open Common
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

let private registerReadyAgentWindow (socket: ClientWebSocket) (bridge: BrowserBridge.Service) = task {
    do! send socket """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-agents","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        match bridge.ListWindows() with
        | Ok listing -> listing.Contains("window-agents")
        | Error _ -> false)
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
    do! registerReadyAgentWindow socket bridge

    let opening = bridge.OpenTab("agents", "https://example.test/chat/new", "agent-new", false)
    let! request = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property request "kind", Is.EqualTo("openTab"))
    Assert.That(property request "url", Is.EqualTo("https://example.test/chat/new"))
    Assert.That(property request "windowRegistration", Is.EqualTo("window-agents"))
    use selectedWindow = JsonDocument.Parse(request)
    Assert.That(selectedWindow.RootElement.GetProperty("registrationGeneration").GetInt32(), Is.EqualTo(1))
    Assert.That(property request "alias", Is.EqualTo("agent-new"))
    use requestDoc = JsonDocument.Parse(request)
    Assert.That(requestDoc.RootElement.GetProperty("active").GetBoolean(), Is.False)
    let response = JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"
        requestId = property request "requestId"; connectionId = connectionId
        tab = {| registrationId = "created-id-01"; alias = "agent-new"; availability = "loading"; agentCreated = true; registrationGeneration = 1 |}
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
    Assert.That(property openedRequest "windowRegistration", Is.EqualTo("window-01"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-tab-01","alias":"agent-new","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openedRequest "requestId"
        connectionId = connectionId
        tab = {| registrationId = "created-tab-01"; availability = "ready"; agentCreated = true; registrationGeneration = 1 |}
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


[<Test>]
let compositeLaunchRetriesOnlyConfirmedPreInjectionLoadingFailures () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { return { status: 'submitted' }; }"
                     """{"prompt":"Hi"}""" 5000 false
    let! opened = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property opened "kind", Is.EqualTo("openTab"))

    // New tab advertises loading before navigating; composite must not inject
    // until it observes ready, even if tabOpened was already acknowledged.
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"retry-tab","agentCreated":true,"availability":"loading","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "retry-tab"; availability = "loading"; agentCreated = true; registrationGeneration = 1 |}
    |}))
    do! Task.Delay(80)
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"retry-tab","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    let! firstExecute = (receive socket).WaitAsync(TimeSpan.FromSeconds(4.0))
    Assert.That(property firstExecute "kind", Is.EqualTo("executeJs"))
    Assert.That(property firstExecute "registrationId", Is.EqualTo("retry-tab"))

    // Chrome navigated again just after ListTabs returned ready. The real
    // extension checks availability before injecting and explicitly rejects.
    // This negative acknowledgement is safe to retry, unlike a transport
    // timeout or an exception after script execution.
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "error"; error = "TabUnavailable:loading"
        requestId = property firstExecute "requestId"; connectionId = connectionId
        registrationId = "retry-tab"; registrationGeneration = 1
    |}))
    let! retryExecute = (receive socket).WaitAsync(TimeSpan.FromSeconds(4.0))
    Assert.That(property retryExecute "kind", Is.EqualTo("executeJs"))
    Assert.That(property retryExecute "registrationId", Is.EqualTo("retry-tab"))
    Assert.That(property retryExecute "requestId", Is.Not.EqualTo(property firstExecute "requestId"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property retryExecute "requestId"
        connectionId = connectionId; registrationId = "retry-tab"
        registrationGeneration = 1; status = "completed"
        result = {| status = "submitted"; conversationUrl = "https://chatgpt.com/c/once" |}
    |}))
    let! result = launch
    match result with
    | Ok value ->
        Assert.That(value, Does.Contain("submitted"))
        Assert.That(value, Does.Contain("https://chatgpt.com/c/once"))
        Assert.That(value, Does.Contain("retained"))
    | Error (_, why) -> Assert.Fail("Pre-injection retry should succeed: " + why)
}


[<Test>]
let compositeLaunchClosesCreatedTabWithoutReplayingUncertainPostInjectionOutcome () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { submitMessage(); return args; }" "{}" 5000 true
    let! opened = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"uncertain-tab","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "uncertain-tab"; availability = "ready"; agentCreated = true; registrationGeneration = 1 |}
    |}))
    let! executed = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executed "kind", Is.EqualTo("executeJs"))
    // The extension may discover a registration change AFTER the JavaScript
    // has already run. Never interpret this as a safe preflight or resubmit.
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "error"; error = "OutcomeUnknown:RegistrationChangedDuringExecution"
        requestId = property executed "requestId"; connectionId = connectionId
        registrationId = "uncertain-tab"; registrationGeneration = 1
    |}))
    // No script execution is retried after the uncertain post-injection result.
    // The explicit default is to close the created tab, not wait for another
    // conversation outcome or require a fresh-home proof.
    let! closing = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closing "kind", Is.EqualTo("closeTab"))
    Assert.That(property closing "registrationId", Is.EqualTo("uncertain-tab"))
    use closeFrame = JsonDocument.Parse(closing)
    Assert.That(closeFrame.RootElement.GetProperty("registrationGeneration").GetInt32(), Is.EqualTo(1))
    Assert.That(closeFrame.RootElement.GetProperty("requireFreshHomeNoTurns").GetBoolean(), Is.False)
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabClosed"; requestId = property closing "requestId"
        connectionId = connectionId; registrationId = "uncertain-tab"
        registrationGeneration = 1
    |}))
    do! send socket """{"version":2,"kind":"registrations","tabs":[]}"""
    let! outcome = launch.WaitAsync(TimeSpan.FromSeconds(3.0))
    match outcome with
    | Error (registrationId, why) ->
        Assert.That(registrationId, Is.EqualTo("uncertain-tab"))
        Assert.That(why, Does.Contain("OutcomeUnknown:RegistrationChangedDuringExecution"))
        Assert.That(why, Does.Contain("cleanup=closed"))
    | Ok _ -> Assert.Fail("Post-dispatch unknown cannot be treated as a successful submission")
    do! waitUntil (fun () ->
        bridge.ListTabs() |> Result.exists (fun tabs -> not (tabs.Contains("uncertain-tab"))))
    use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200.0))
    try
        let! next = socket.ReceiveAsync(ArraySegment<byte>(Array.zeroCreate<byte> 8192), cts.Token)
        Assert.Fail("Uncertain post-injection result must not replay execution: " + string next.MessageType)
    with :? OperationCanceledException -> ()
}

[<Test>]
let compositeLaunchRejectsInvalidInputsBeforeOpeningTab () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let cases =
        [ "not-json", 15000, "InvalidArgumentsJson"
          "{}", 0, "InvalidTimeout"
          "{}", 30001, "InvalidTimeout"
          String.replicate 65537 "x", 15000, "InputTooLarge" ]
    for args, timeout, expected in cases do
        let! outcome = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false "async function execute(args) { return args; }" args timeout true
        match outcome with
        | Error (registrationId, reason) ->
            Assert.That(registrationId, Is.Empty)
            Assert.That(reason, Does.Contain(expected))
        | Ok _ -> Assert.Fail("Invalid composite launch was accepted")
}

[<Test>]
let launchArgumentBudgetIncludesOptionalMetadataAndExactEscaping () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let options = JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
    let prefix = String.replicate 21800 "界" + "\"\\\n"
    let compose prompt = JsonSerializer.Serialize({| prompt = prompt; timeoutMs = 1000; selectionOnly = true |}, options)
    let padding = BrowserBridge.MaxArgumentsBytes - System.Text.Encoding.UTF8.GetByteCount(compose prefix)
    let prompt = prefix + String.replicate padding "x"
    let exact = compose prompt
    Assert.That(Client.BrowserArguments.byteCount exact, Is.EqualTo(BrowserBridge.MaxArgumentsBytes))
    Assert.That(Client.BrowserArguments.fits (Client.BrowserArguments.serializeMinimalLaunchPrompt prompt), Is.True)
    use decoded = JsonDocument.Parse(exact)
    Assert.That(decoded.RootElement.GetProperty("prompt").GetString(), Is.EqualTo(prompt))
    let run args =
        BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false "async function execute(args) { return args; }" args 15000 true
    // No extension is connected. The exact boundary must reach the
    // ordinary connection check, whereas one extra byte fails before it.
    match! run exact with
    | Error (registrationId, reason) ->
        Assert.That(registrationId, Is.Empty)
        Assert.That(reason, Is.EqualTo("ExtensionDisconnected"))
    | Ok _ -> Assert.Fail("No browser extension was connected")
    let oversized = compose (prompt + "x")
    Assert.That(Client.BrowserArguments.byteCount oversized, Is.EqualTo(BrowserBridge.MaxArgumentsBytes + 1))
    match! run oversized with
    | Error (registrationId, reason) ->
        Assert.That(registrationId, Is.Empty)
        Assert.That(reason, Does.Contain("InputTooLarge"))
    | Ok _ -> Assert.Fail("Oversized arguments must fail before tab creation")
}

[<TestCase("notSubmitted", true)>]
[<TestCase("uiIncompatible", true)>]
[<TestCase("outcomeUnknown", false)>]
let compositeLaunchKeepsUncertainTabButClosesDefinitiveFailure (scriptStatus: string, shouldClose: bool) = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { return args; }" "{}" 5000 false
    let! openFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-tab-02","alias":"new","agentCreated":true,"availability":"ready","registrationGeneration":1},{"registrationId":"manual-existing","alias":"personal","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openFrame "requestId"
        connectionId = connectionId
        tab = {| registrationId = "created-tab-02"; availability = "ready"; agentCreated = true; registrationGeneration = 1 |}
    |}))
    let! executeFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeFrame "kind", Is.EqualTo("executeJs"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeFrame "requestId"
        connectionId = connectionId; registrationId = "created-tab-02"
        registrationGeneration = 1; status = "completed"
        result = {| status = scriptStatus; stage = "modeSelection"; reason = if scriptStatus = "notSubmitted" then "UnexpectedSendState" else "ModeToggleUnavailable"
                    safeToDisposeCreatedTab = shouldClose
                    diagnostics = {| chatLabels = 0; editableTextboxes = 1;
                                     documentReadyState = "complete" |} |}
    |}))
    if shouldClose then
        let! closeFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.That(property closeFrame "kind", Is.EqualTo("closeTab"))
        use guardedClose = JsonDocument.Parse(closeFrame)
        Assert.That(guardedClose.RootElement.GetProperty("requireFreshHomeNoTurns").GetBoolean(), Is.True)
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "tabClosed"; requestId = property closeFrame "requestId"
            connectionId = connectionId; registrationId = "created-tab-02"
            registrationGeneration = 1
        |}))
        do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"manual-existing","alias":"personal","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
        do! waitUntil (fun () -> bridge.ListTabs() |> Result.exists (fun x -> x.Contains("manual-existing") && not (x.Contains("created-tab-02"))))
    let! result = launch
    match result with
    | Ok response when scriptStatus = "notSubmitted" ->
        Assert.That(response, Does.Contain("notSubmitted"))
        Assert.That(response, Does.Contain("closed"))
        Assert.That(response, Does.Contain("UnexpectedSendState"))
    | Error (registrationId, reason) when scriptStatus = "uiIncompatible" ->
        Assert.That(registrationId, Is.EqualTo("created-tab-02"))
        Assert.That(reason, Does.Contain("UiIncompatible:modeSelection:ModeToggleUnavailable"))
        Assert.That(reason, Does.Contain("chatLabels=0"))
        Assert.That(reason, Does.Contain("editableTextboxes=1"))
        Assert.That(reason, Does.Contain("cleanup=closed"))
    | Error (registrationId, reason) when scriptStatus = "outcomeUnknown" ->
        Assert.That(registrationId, Is.EqualTo("created-tab-02"))
        Assert.That(reason, Does.Contain("OutcomeUnknown"))
        Assert.That(bridge.ListTabs() |> Result.exists (fun x -> x.Contains("created-tab-02") && x.Contains("manual-existing")), Is.True)
    | _ -> Assert.Fail("Unexpected script failure handling")
}

[<TestCase("notSubmitted")>]
[<TestCase("uiIncompatible")>]
let definitiveLaunchCleanupFailureIsSurfacedWithoutRetry (scriptStatus: string) = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { return args; }" "{}" 5000 false
    let! opened = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property opened "kind", Is.EqualTo("openTab"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"cleanup-failure","agentCreated":true,"availability":"ready","registrationGeneration":4},{"registrationId":"personal-tab","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "cleanup-failure"; agentCreated = true
                 availability = "ready"; registrationGeneration = 4 |}
    |}))
    let! execution = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property execution "kind", Is.EqualTo("executeJs"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property execution "requestId"
        connectionId = connectionId; registrationId = "cleanup-failure"; registrationGeneration = 4
        result = {| status = scriptStatus; stage = "composer"; reason = "UnexpectedSendState"; safeToDisposeCreatedTab = true |}
    |}))
    let! closing = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closing "kind", Is.EqualTo("closeTab"))
    Assert.That(property closing "registrationId", Is.EqualTo("cleanup-failure"))
    use guardedClose = JsonDocument.Parse(closing)
    Assert.That(guardedClose.RootElement.GetProperty("requireFreshHomeNoTurns").GetBoolean(), Is.True)
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "error"; requestId = property closing "requestId"
        connectionId = connectionId; registrationId = "cleanup-failure"; registrationGeneration = 4
        error = "RegistrationStale"
    |}))
    match! launch with
    | Error (id, reason) ->
        Assert.That(id, Is.EqualTo("cleanup-failure"))
        Assert.That(reason, Does.Contain("cleanupFailed:BrowserCloseTabError:RegistrationStale"))
        Assert.That(reason, Does.Contain(if scriptStatus = "uiIncompatible" then "UiIncompatible" else "NotSubmitted"))
    | Ok _ -> Assert.Fail("Unverified failed-launch cleanup cannot report success")
    Assert.That(bridge.ListTabs() |> Result.exists (fun x -> x.Contains("personal-tab")), Is.True)
}

// The bridge must not treat the page's earlier true disposal proof as a
// license to close later. The extension rechecks at the final disposal point,
// including pending /c/ navigation that the old committed document cannot see.
// The extension's deterministic harness supplies the timing-specific cases;
// this composed bridge test distinguishes a verified changed conversation
// (normal retention) from an unverified probe (explicit cleanup failure).
[<TestCase("notSubmitted", "CleanupSafetyChanged", "pending-at-admission")>]
[<TestCase("notSubmitted", "CleanupSafetyChanged", "pending-during-probe")>]
[<TestCase("notSubmitted", "CleanupSafetyChanged", "pending-after-probe")>]
[<TestCase("uiIncompatible", "CleanupSafetyChanged", "pending-after-probe")>]
[<TestCase("notSubmitted", "CleanupSafetyUnverified", "inspection-unavailable")>]
[<TestCase("uiIncompatible", "CleanupSafetyUnverified", "inspection-unavailable")>]
let composedLateCleanupGuardRetainsTabWithoutRetry
    (scriptStatus: string, guardFailure: string, scenario: string) = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "late-route" false
                     "async function execute(args) { return args; }" "{}" 5000 false
    let! opened = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property opened "kind", Is.EqualTo("openTab"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"late-conversation","agentCreated":true,"availability":"ready","registrationGeneration":3},{"registrationId":"personal-tab","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "late-conversation"; agentCreated = true
                 availability = "ready"; registrationGeneration = 3 |}
    |}))
    let! executing = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executing "kind", Is.EqualTo("executeJs"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executing "requestId"
        connectionId = connectionId; registrationId = "late-conversation"
        registrationGeneration = 3
        result = {| status = scriptStatus; stage = "composer"; reason = "UnexpectedSendState"
                    safeToDisposeCreatedTab = true |}
    |}))
    let! closing = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closing "kind", Is.EqualTo("closeTab"))
    Assert.That(property closing "registrationId", Is.EqualTo("late-conversation"))
    use frame = JsonDocument.Parse(closing)
    Assert.That(frame.RootElement.GetProperty("registrationGeneration").GetInt32(), Is.EqualTo(3))
    Assert.That(frame.RootElement.GetProperty("requireFreshHomeNoTurns").GetBoolean(), Is.True)
    // Real extension rejects after observing a new /c/ route or a user bubble.
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "error"; requestId = property closing "requestId"
        connectionId = connectionId; registrationId = "late-conversation"
        registrationGeneration = 3; error = guardFailure
    |}))
    match! launch.WaitAsync(TimeSpan.FromSeconds(3.0)) with
    | Error (id, reason) when guardFailure = "CleanupSafetyUnverified" ->
        Assert.That(id, Is.EqualTo("late-conversation"), scenario)
        Assert.That(reason, Does.Contain("cleanupFailed:BrowserCloseTabError:CleanupSafetyUnverified"), scenario)
        Assert.That(reason, Does.Contain(if scriptStatus = "uiIncompatible" then "UiIncompatible" else "NotSubmitted"), scenario)
        Assert.That(reason, Does.Not.Contain("cleanup=retained"), scenario)
    | Ok response when scriptStatus = "notSubmitted" && guardFailure = "CleanupSafetyChanged" ->
        use result = JsonDocument.Parse(response)
        Assert.That(result.RootElement.GetProperty("status").GetString(), Is.EqualTo("notSubmitted"), scenario)
        Assert.That(result.RootElement.GetProperty("cleanup").GetString(), Is.EqualTo("retained"), scenario)
        let scriptResult = result.RootElement.GetProperty("scriptResult")
        Assert.That(scriptResult.GetProperty("safeToDisposeCreatedTab").GetBoolean(), Is.True, scenario)
    | Error (id, reason) when scriptStatus = "uiIncompatible" && guardFailure = "CleanupSafetyChanged" ->
        Assert.That(id, Is.EqualTo("late-conversation"))
        Assert.That(reason, Does.Contain("cleanup=retained"))
    | _ -> Assert.Fail("Guarded cleanup must retain the tab without claiming closure")
    Assert.That(bridge.ListTabs() |> Result.exists (fun tabs ->
        tabs.Contains("late-conversation") && tabs.Contains("personal-tab")), Is.True)
    use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150.0))
    try
        let! unexpected = socket.ReceiveAsync(ArraySegment<byte>(Array.zeroCreate<byte> 8192), cts.Token)
        Assert.Fail("Unexpected extra close or resend after guarded retention: " + string unexpected.MessageType)
    with :? OperationCanceledException -> ()
}

// The page script and bridge share an explicit disposal contract: no Send
// attempt alone is insufficient when another conversation/route appears.
[<TestCase("notSubmitted", "ExistingConversation", "false", false)>]
[<TestCase("notSubmitted", "UnexpectedStartUrl", "false", false)>]
[<TestCase("notSubmitted", "ExistingConversation", "true", false)>]
[<TestCase("notSubmitted", "UnexpectedStartUrl", "true", false)>]
[<TestCase("notSubmitted", "UnexpectedSendState", "true", true)>]
[<TestCase("uiIncompatible", "ComposerChanged", "false", false)>]
[<TestCase("uiIncompatible", "DraftClearFailed", "true", true)>]
[<TestCase("notSubmitted", "UnexpectedSendState", "absent", false)>]
[<TestCase("notSubmitted", "UnexpectedSendState", "\"true\"", false)>]
[<TestCase("outcomeUnknown", "SubmissionUnconfirmed", "true", false)>]
let composedLauncherDisposalHonorsFreshHomeProof
    (scriptStatus: string, reason: string, proof: string, shouldClose: bool) = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "safe-close" false
                     "async function execute(args) { return args; }" "{}" 5000 false
    let! opened = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property opened "kind", Is.EqualTo("openTab"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"launcher-created","agentCreated":true,"availability":"ready","registrationGeneration":3},{"registrationId":"unrelated-conversation","agentCreated":true,"availability":"ready","registrationGeneration":1},{"registrationId":"personal-tab","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "launcher-created"; agentCreated = true
                 availability = "ready"; registrationGeneration = 3 |}
    |}))
    let! executeFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeFrame "kind", Is.EqualTo("executeJs"))
    Assert.That(property executeFrame "registrationId", Is.EqualTo("launcher-created"))
    let proofField = if proof = "absent" then "" else ",\"safeToDisposeCreatedTab\":" + proof
    let pageJson =
        "{\"status\":\"" + scriptStatus + "\",\"stage\":\"draftStabilization\",\"reason\":\"" +
        reason + "\"" + proofField + "}"
    let pageResult = JsonSerializer.Deserialize<JsonElement>(pageJson)
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeFrame "requestId"
        connectionId = connectionId; registrationId = "launcher-created"
        registrationGeneration = 3; status = "completed"; result = pageResult
    |}))
    if shouldClose then
        let! closing = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.That(property closing "kind", Is.EqualTo("closeTab"))
        Assert.That(property closing "registrationId", Is.EqualTo("launcher-created"))
        use closeDoc = JsonDocument.Parse(closing)
        Assert.That(closeDoc.RootElement.GetProperty("registrationGeneration").GetInt32(), Is.EqualTo(3))
        Assert.That(closeDoc.RootElement.GetProperty("requireFreshHomeNoTurns").GetBoolean(), Is.True)
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "tabClosed"; requestId = property closing "requestId"
            connectionId = connectionId; registrationId = "launcher-created"
            registrationGeneration = 3
        |}))
        do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"unrelated-conversation","agentCreated":true,"availability":"ready","registrationGeneration":1},{"registrationId":"personal-tab","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
    let! outcome = launch.WaitAsync(TimeSpan.FromSeconds(3.0))
    match outcome with
    | Ok json when scriptStatus = "notSubmitted" ->
        Assert.That(json, Does.Contain("notSubmitted"))
        Assert.That(json, Does.Contain(if shouldClose then "cleanup\":\"closed" else "cleanup\":\"retained"))
        Assert.That(json, Does.Contain(reason))
    | Error (id, text) when scriptStatus = "uiIncompatible" ->
        Assert.That(id, Is.EqualTo("launcher-created"))
        Assert.That(text, Does.Contain("UiIncompatible"))
        Assert.That(text, Does.Contain(if shouldClose then "cleanup=closed" else "cleanup=retained"))
    | Error (id, text) when scriptStatus = "outcomeUnknown" ->
        Assert.That(id, Is.EqualTo("launcher-created"))
        Assert.That(text, Does.Contain("OutcomeUnknown"))
    | _ -> Assert.Fail("Unexpected result at launcher/bridge cleanup boundary")
    do! waitUntil (fun () ->
        bridge.ListTabs() |> Result.exists (fun tabs ->
            tabs.Contains("personal-tab") && tabs.Contains("unrelated-conversation") &&
            (tabs.Contains("launcher-created") <> shouldClose)))
    if not shouldClose then
        // Bounded negative-space assertion: no close or replay frame may be
        // emitted on ambiguous, unproven, or confirmed-conversation states.
        use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200.0))
        try
            let! frame = socket.ReceiveAsync(ArraySegment<byte>(Array.zeroCreate<byte> 8192), cts.Token)
            Assert.Fail("Unexpected bridge command after retained failure: " + string frame.MessageType)
        with :? OperationCanceledException -> ()
    return ()
}

[<TestCase("outcomeUnknown")>]
[<TestCase("notSubmitted")>]
[<TestCase("uiIncompatible")>]
[<TestCase("bridgeError")>]
let closeAfterExecutionReclaimsTabEvenWhenSubmissionIsUncertain (scriptStatus: string) = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let mutable captured : JsonElement option = None
    let launch = BrowserBridge.runInNewTabWithObservation bridge "agents" "https://chatgpt.com/" "cleanup-all" false
                     "async function execute(args) { return args; }" "{}" 5000 true (fun payload -> captured <- Some payload)
    let! opened = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property opened "kind", Is.EqualTo("openTab"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"cleanup-all-created","agentCreated":true,"availability":"ready","registrationGeneration":3},{"registrationId":"personal-tab","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "cleanup-all-created"; agentCreated = true
                 availability = "ready"; registrationGeneration = 3 |}
    |}))
    let! executing = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executing "kind", Is.EqualTo("executeJs"))
    if scriptStatus = "bridgeError" then
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "error"; requestId = property executing "requestId"
            connectionId = connectionId; registrationId = "cleanup-all-created"
            registrationGeneration = 3; error = "ExecutionLost"
        |}))
    else
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "result"; requestId = property executing "requestId"
            connectionId = connectionId; registrationId = "cleanup-all-created"
            registrationGeneration = 3
            result = {| status = scriptStatus
                        stage = if scriptStatus = "outcomeUnknown" then "acknowledgement" else "composer"
                        reason = if scriptStatus = "outcomeUnknown" then "SubmissionUnconfirmed" else "Unknown"
                        timing = {| budgetAtSendMs = 2000; elapsedSinceSendMs = 2500 |}
                        observedSelection = {| mode = "Work"; model = "shown model"; effort = "Medium" |} |}
        |}))
    let! closing = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closing "kind", Is.EqualTo("closeTab"))
    Assert.That(property closing "registrationId", Is.EqualTo("cleanup-all-created"))
    use frame = JsonDocument.Parse(closing)
    Assert.That(frame.RootElement.GetProperty("requireFreshHomeNoTurns").GetBoolean(), Is.False)
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabClosed"; requestId = property closing "requestId"
        connectionId = connectionId; registrationId = "cleanup-all-created"
        registrationGeneration = 3
    |}))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"personal-tab","agentCreated":false,"availability":"ready","registrationGeneration":1}]}"""
    let! result = launch.WaitAsync(TimeSpan.FromSeconds(3.0))
    if scriptStatus = "bridgeError" then Assert.That(captured.IsNone, Is.True)
    else
        Assert.That(captured.IsSome, Is.True)
        Assert.That(captured.Value.GetProperty("observedSelection").GetProperty("model").GetString(), Is.EqualTo("shown model"))
    match result with
    | Ok response when scriptStatus = "notSubmitted" ->
        Assert.That(response, Does.Contain("\"cleanup\":\"closed\""))
    | Error (id, reason) ->
        Assert.That(id, Is.EqualTo("cleanup-all-created"))
        Assert.That(reason, Does.Contain("cleanup=closed"))
        if scriptStatus = "outcomeUnknown" then
            Assert.That(reason, Does.Contain("OutcomeUnknown:acknowledgement:SubmissionUnconfirmed"))
            Assert.That(reason, Does.Contain("elapsedSinceSendMs=2500"))
    | _ -> Assert.Fail("Expected confirmed cleanup after an uncertain or failed launch")
    do! waitUntil (fun () ->
        bridge.ListTabs() |> Result.exists (fun tabs ->
            tabs.Contains("personal-tab") && not (tabs.Contains("cleanup-all-created"))))
}

[<Test>]
let compositeNotReadyCleanupReportsFailureOrConfirmedClosure () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge

    // Different site origins may still launch concurrently while their tabs
    // wait for the real 30-second bounded readiness path and become discarded.
    // including the MCP's default closeAfterExecution=false.
    let fail = BrowserBridge.runInNewTab bridge "agents" "https://fail.example.test/fail" "fail" false
                   "async function execute(args) { return args; }" "{}" 5000 false
    let close = BrowserBridge.runInNewTab bridge "agents" "https://close.example.test/close" "close" false
                    "async function execute(args) { return args; }" "{}" 5000 false

    let! openA = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    let! openB = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openA "kind", Is.EqualTo("openTab"))
    Assert.That(property openB "kind", Is.EqualTo("openTab"))
    let idFor frame =
        if property frame "url" = "https://fail.example.test/fail" then "discarded-fail"
        else "discarded-close"
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"discarded-fail","alias":"fail","agentCreated":true,"availability":"discarded","registrationGeneration":1},{"registrationId":"discarded-close","alias":"close","agentCreated":true,"availability":"discarded","registrationGeneration":1}]}"""
    for frame in [openA; openB] do
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "tabOpened"; requestId = property frame "requestId"
            connectionId = connectionId
            tab = {| registrationId = idFor frame; agentCreated = true
                     availability = "discarded"; registrationGeneration = 1 |}
        |}))
    for _ in 1..2 do
        let! frame = (receive socket).WaitAsync(TimeSpan.FromSeconds(40.0))
        Assert.That(property frame "kind", Is.EqualTo("closeTab"))
        use guardedClose = JsonDocument.Parse(frame)
        Assert.That(guardedClose.RootElement.GetProperty("requireFreshHomeNoTurns").GetBoolean(), Is.True)
        let tabId = property frame "registrationId"
        let failCleanup = tabId = "discarded-fail"
        do! send socket (JsonSerializer.Serialize({|
            version = 2
            kind = if failCleanup then "error" else "tabClosed"
            requestId = property frame "requestId"
            connectionId = connectionId; registrationId = tabId
            registrationGeneration = 1
            error = if failCleanup then "RegistrationStale" else ""
        |}))
    let! failed = fail
    let! closed = close
    match failed with
    | Error (registrationId, reason) ->
        Assert.That(registrationId, Is.EqualTo("discarded-fail"))
        Assert.That(reason, Does.Contain("TabNotReady; cleanupFailed:BrowserCloseTabError:RegistrationStale"))
    | Ok _ -> Assert.Fail("Failed cleanup must never report success")
    match closed with
    | Error (registrationId, reason) ->
        Assert.That(registrationId, Is.EqualTo("discarded-close"))
        Assert.That(reason, Is.EqualTo("TabNotReady; cleanup=closed"))
    | Ok _ -> Assert.Fail("An unready page must not report an executed script")
}

[<Test>]
let liveDomSnapshotReturnsPostHydrationMarkup () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! send socket """{"version":2,"kind":"capabilities","features":["domSnapshotV1"]}"""
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"dom-tab-01","alias":"hydration","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () -> bridge.ListTabs() |> Result.exists (fun x -> x.Contains("hydration")))
    let waitJson = """{"kind":"selector","selector":"#results","state":"textContains","text":"Loaded"}"""
    let pending = bridge.CaptureDom("hydration",waitJson,"#results",2000,None)
    let! request = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property request "kind", Is.EqualTo("captureDom"))
    Assert.That(property request "mode", Is.EqualTo("inline"))
    Assert.That(property request "captureSelector", Is.EqualTo("#results"))
    use sent = JsonDocument.Parse(request)
    Assert.That(sent.RootElement.GetProperty("wait").GetProperty("kind").GetString(), Is.EqualTo("selector"))
    let html = "<div id='results'>Loaded Ω after JS</div>"
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "snapshotReady"; requestId = property request "requestId"
        connectionId = connectionId; registrationId = "dom-tab-01"; registrationGeneration = 1
        status = "completed"; bytes = Encoding.UTF8.GetByteCount(html)
        title = "Dynamic"; url = "https://example.test/page"
        capturedAtUtc = "2026-11-10T09:00:00Z"; waitKind = "selector"; html = html
    |}))
    let! result = pending
    match result with
    | Error reason -> Assert.Fail("Expected DOM snapshot: " + reason)
    | Ok value ->
        use json = JsonDocument.Parse(value)
        Assert.That(json.RootElement.GetProperty("html").GetString(), Is.EqualTo(html))
        Assert.That(json.RootElement.GetProperty("waitKind").GetString(), Is.EqualTo("selector"))
        Assert.That(json.RootElement.GetProperty("bytes").GetInt32(), Is.EqualTo(Encoding.UTF8.GetByteCount(html)))
    let timeout = bridge.CaptureDom("hydration", """{"kind":"event","name":"never","target":"document"}""", "",2000,None)
    let! expiredRequest = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "error"; requestId = property expiredRequest "requestId"
        connectionId = connectionId; registrationId = "dom-tab-01"; registrationGeneration = 1
        error = "SnapshotTimeout"
    |}))
    let! expired = timeout
    match expired with
    | Error reason -> Assert.That(reason, Does.Contain("SnapshotTimeout"))
    | Ok _ -> Assert.Fail("Timed-out event must not return a snapshot")
}

[<Test>]
let snapshotLargeFileExportTransfersExactUtf8AndNeverOverwrites () = task {
    let dir = IO.Path.Combine(IO.Path.GetTempPath(), "jarvis-dom-" + Guid.NewGuid().ToString("N"))
    IO.Directory.CreateDirectory(dir) |> ignore
    let target = IO.Path.Combine(dir, "snapshot.html")
    try
        let port = findPort()
        use bridge = new BrowserBridge.Service(port, "test-secret")
        bridge.Start()
        let! client, connectionId = handshake port "test-secret"
        use socket = client
        do! send socket """{"version":2,"kind":"capabilities","features":["domSnapshotV1"]}"""
        do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"dom-tab-02","alias":"export","availability":"ready","registrationGeneration":1}]}"""
        do! waitUntil (fun () -> bridge.ListTabs() |> Result.exists (fun x -> x.Contains("dom-tab-02")))
        let markup = "<html><body>" + String.replicate 30000 "🦊" + "</body></html>"
        let data = Encoding.UTF8.GetBytes(markup)
        let sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
        let capture = bridge.CaptureDom("export","""{"kind":"immediate"}""","",2000,Some target)
        let! request = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.That(property request "mode", Is.EqualTo("file"))
        let snapshotId = "0000000000000000000000000000000a"
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "snapshotReady"; requestId = property request "requestId"
            connectionId = connectionId; registrationId = "dom-tab-02"; registrationGeneration = 1
            status = "completed"; bytes = data.Length
            title = "Large"; url = "https://example.test/dynamic"
            capturedAtUtc = "2026-11-10T09:00:00Z"; waitKind = "immediate"
            snapshotId = snapshotId; sha256 = sha256
        |}))
        let mutable offset = 0
        while offset < data.Length do
            let! chunk = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
            Assert.That(property chunk "kind", Is.EqualTo("snapshotChunk"))
            use packet = JsonDocument.Parse(chunk)
            let requestedOffset = packet.RootElement.GetProperty("offset").GetInt32()
            Assert.That(requestedOffset, Is.EqualTo(offset))
            let count = min 32768 (data.Length-offset)
            do! send socket (JsonSerializer.Serialize({|
                version = 2; kind = "snapshotChunk"; requestId = property chunk "requestId"
                connectionId = connectionId; registrationId = "dom-tab-02"; registrationGeneration = 1
                snapshotId = snapshotId; offset = offset
                data = Convert.ToBase64String(data, offset, count)
            |}))
            offset <- offset + count
        let! release = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.That(property release "kind", Is.EqualTo("releaseSnapshot"))
        let! result = capture
        match result with
        | Error reason -> Assert.Fail("Expected exported DOM snapshot: " + reason)
        | Ok value -> Assert.That(value, Does.Contain(sha256))
        Assert.That(IO.File.ReadAllBytes(target), Is.EqualTo(data :> obj))
        let! rejected = bridge.CaptureDom("export","""{"kind":"immediate"}""","",2000,Some target)
        match rejected with
        | Error reason -> Assert.That(reason, Is.EqualTo("SnapshotOutputExists"))
        | Ok _ -> Assert.Fail("Existing output must be preserved")
        Assert.That(IO.File.ReadAllBytes(target), Is.EqualTo(data :> obj))
    finally
        IO.Directory.Delete(dir, true)
}

[<Test>]
let olderExtensionFailsSnapshotCapabilityCheckWithoutDispatch () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, _ = handshake port "test-secret"
    use socket = client
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"old-tab-01","alias":"old-extension","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () -> bridge.ListTabs() |> Result.exists (fun x -> x.Contains("old-extension")))
    let! outcome = bridge.CaptureDom("old-extension","""{"kind":"immediate"}""","",1000,None)
    match outcome with
    | Error reason -> Assert.That(reason, Does.Contain("ExtensionOutdated"))
    | Ok _ -> Assert.Fail("An older extension must not silently time out")
}

[<Test>]
let failedSnapshotIntegrityLeavesNoPartialFile () = task {
    let dir = IO.Path.Combine(IO.Path.GetTempPath(), "jarvis-dom-integrity-" + Guid.NewGuid().ToString("N"))
    IO.Directory.CreateDirectory(dir) |> ignore
    let target = IO.Path.Combine(dir, "snapshot.html")
    try
        let port = findPort()
        use bridge = new BrowserBridge.Service(port, "test-secret")
        bridge.Start()
        let! client, connectionId = handshake port "test-secret"
        use socket = client
        do! send socket """{"version":2,"kind":"capabilities","features":["domSnapshotV1"]}"""
        do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"corrupt-tab-01","alias":"corrupt","availability":"ready","registrationGeneration":1}]}"""
        do! waitUntil (fun () -> bridge.ListTabs() |> Result.exists (fun x -> x.Contains("corrupt-tab-01")))
        let capture = bridge.CaptureDom("corrupt","""{"kind":"immediate"}""","",2000,Some target)
        let! request = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        let bytes = Encoding.UTF8.GetBytes("<p>UTF8 Ω</p>")
        let snapshotId = "0000000000000000000000000000000b"
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "snapshotReady"; requestId = property request "requestId"
            connectionId = connectionId; registrationId = "corrupt-tab-01"; registrationGeneration = 1
            status = "completed"; bytes = bytes.Length
            title = "Page"; url = "https://example.test/page"
            capturedAtUtc = "2026-11-10T09:00:00Z"; waitKind = "immediate"
            snapshotId = snapshotId; sha256 = String.replicate 64 "0"
        |}))
        let! chunk = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.That(property chunk "kind", Is.EqualTo("snapshotChunk"))
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "snapshotChunk"; requestId = property chunk "requestId"
            connectionId = connectionId; registrationId = "corrupt-tab-01"; registrationGeneration = 1
            snapshotId = snapshotId; offset = 0; data = Convert.ToBase64String(bytes)
        |}))
        let! release = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.That(property release "kind", Is.EqualTo("releaseSnapshot"))
        let! result = capture
        match result with
        | Error reason -> Assert.That(reason, Is.EqualTo("SnapshotChecksumMismatch"))
        | Ok _ -> Assert.Fail("Corrupt snapshot must not be written")
        Assert.That(IO.File.Exists(target), Is.False)
        Assert.That(IO.Directory.GetFiles(dir), Is.Empty)
    finally
        IO.Directory.Delete(dir, true)
}

[<Test>]
let everyBrowserCommandHasSafeActivityDetail () =
    // TUI.StartActivity evaluates AgentCommandInfo.invocation BEFORE command
    // dispatch. Any missing union case prevents browser operations entirely.
    let commands: AgentCommand list =
        [ BrowserListTabsCommand
          BrowserListWindowsCommand
          ScheduleBrowserExecutionCommand {
              ScheduleId = "test"; RunAfterSeconds = 1; Url = "https://example.test"
              ProjectName = "Jarvis"; ScriptPath = "test.js"; ArgsJson = None
              Alias = None; Active = None; WindowRegistration = Some "agents"
              CloseTabAfterExecution = None; RepeatEverySeconds = None }
          ListScheduledBrowserExecutionsCommand
          CancelScheduledBrowserExecutionCommand { ScheduleId = "test" }
          BrowserOpenTabCommand {
              WindowRegistration = "agents"; Url = "https://example.test"; Alias = None; Active = None }
          BrowserCloseTabCommand { TabRegistration = "tab" }
          BrowserRunInNewTabCommand {
              TargetAgentId = None
              WindowRegistration = "agents"; Url = "https://example.test"; ProjectName = "Jarvis"
              ScriptPath = "test.js"; ArgsJson = None; Active = None
              CloseTabAfterExecution = None; TimeoutMs = None }
          BrowserExecuteFileCommand {
              ProjectName = "Jarvis"; ScriptPath = "test.js"; TabRegistration = "tab"
              ArgsJson = None; TimeoutMs = None }
          BrowserExecuteJsCommand {
              TabRegistration = "tab"; Code = "document.title"; ArgsJson = None; TimeoutMs = None }
          BrowserCaptureDomCommand {
              TabRegistration = "tab"; WaitJson = None; CaptureSelector = None; TimeoutMs = None }
          BrowserExportDomCommand {
              ProjectName = "Jarvis"; FilePath = "snapshot.html"; TabRegistration = "tab"
              WaitJson = None; CaptureSelector = None; TimeoutMs = None } ]
    for command in commands do
        let text = AgentCommandInfo.invocation command
        Assert.That(text, Is.Not.Null.And.Not.Empty)

[<Test>]
let snapshotExportDeadlineStopsSlowChunksWithoutPublishing () = task {
    let dir = IO.Path.Combine(IO.Path.GetTempPath(), "jarvis-dom-deadline-" + Guid.NewGuid().ToString("N"))
    IO.Directory.CreateDirectory(dir) |> ignore
    let target = IO.Path.Combine(dir, "slow.html")
    try
        let port = findPort()
        use bridge = new BrowserBridge.Service(port, "test-secret")
        bridge.Start()
        let! client, connectionId = handshake port "test-secret"
        use socket = client
        do! send socket """{"version":2,"kind":"capabilities","features":["domSnapshotV1"]}"""
        do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"slow-tab-01","alias":"slow-export","availability":"ready","registrationGeneration":1}]}"""
        do! waitUntil (fun () -> bridge.ListTabs() |> Result.exists (fun x -> x.Contains("slow-tab-01")))
        let markup = "<html>" + String.replicate 200000 "a" + "</html>"
        let data = Encoding.UTF8.GetBytes(markup)
        let sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
        let snapshotId = "0000000000000000000000000000000c"
        // Individual chunks arrive within 10 seconds, but the entire transfer
        // exceeds this one overall caller deadline.
        let pending = bridge.CaptureDom("slow-export","""{"kind":"immediate"}""","",400,Some target)
        let! request = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "snapshotReady"; requestId = property request "requestId"
            connectionId = connectionId; registrationId = "slow-tab-01"; registrationGeneration = 1
            status = "completed"; bytes = data.Length; title = "Slow"
            url = "https://example.test/page"; capturedAtUtc = "2026-11-10T09:00:00Z"
            waitKind = "immediate"; snapshotId = snapshotId; sha256 = sha256
        |}))
        let mutable finished = false
        let mutable responded = 0
        while not finished do
            let! frame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
            if property frame "kind" = "releaseSnapshot" then
                finished <- true
            else
                Assert.That(property frame "kind", Is.EqualTo("snapshotChunk"))
                use packet = JsonDocument.Parse(frame)
                let offset = packet.RootElement.GetProperty("offset").GetInt32()
                let length = min 32768 (data.Length - offset)
                do! Task.Delay(120)
                do! send socket (JsonSerializer.Serialize({|
                    version = 2; kind = "snapshotChunk"; requestId = property frame "requestId"
                    connectionId = connectionId; registrationId = "slow-tab-01"; registrationGeneration = 1
                    snapshotId = snapshotId; offset = offset
                    data = Convert.ToBase64String(data, offset, length)
                |}))
                responded <- responded + 1
        let! outcome = pending
        match outcome with
        | Error reason -> Assert.That(reason, Does.Contain("SnapshotDeadlineExceeded"))
        | Ok _ -> Assert.Fail("Slow transfer must not publish after caller deadline")
        Assert.That(responded, Is.GreaterThanOrEqualTo(2))
        Assert.That(IO.File.Exists(target), Is.False)
        Assert.That(IO.Directory.GetFiles(dir), Is.Empty)
        do! Task.Delay(150) // Late replies cannot resurrect a finished export.
        Assert.That(IO.File.Exists(target), Is.False)
    finally
        IO.Directory.Delete(dir, true)
}


[<Test>]
let snapshotCaptureDeadlineClassifiesUnansweredRequest () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, _ = handshake port "test-secret"
    use socket = client
    do! send socket """{"version":2,"kind":"capabilities","features":["domSnapshotV1"]}"""
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"silent-tab-01","alias":"no-reply","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () -> bridge.ListTabs() |> Result.exists (fun x -> x.Contains("silent-tab-01")))
    let capture = bridge.CaptureDom("no-reply", """{"kind":"immediate"}""", "", 200, None)
    let! request = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property request "kind", Is.EqualTo("captureDom"))
    // The transport wait uses the caller's remaining budget; no independent
    // transport deadline exists here, regardless of timer scheduling.
    let! outcome = capture
    match outcome with
    | Error reason -> Assert.That(reason, Is.EqualTo("SnapshotDeadlineExceeded"))
    | Ok _ -> Assert.Fail("A silent extension must not return a DOM snapshot")
}


[<Test>]
let twoExtensionsKeepIndependentRegistriesAndRouteByOwner () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! first, firstId = handshake port "test-secret"
    use socketA = first
    let! second, secondId = handshake port "test-secret"
    use socketB = second
    do! send socketA """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-main","alias":"main","availability":"ready","registrationGeneration":1}]}"""
    do! send socketA """{"version":2,"kind":"registrations","tabs":[{"registrationId":"tab-a-ready","alias":"first","availability":"ready","registrationGeneration":1}]}"""
    do! send socketA """{"version":2,"kind":"capabilities","features":["domSnapshotV1"]}"""
    do! send socketB """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-agents","alias":"agents","availability":"ready","registrationGeneration":2}]}"""
    do! send socketB """{"version":2,"kind":"registrations","tabs":[{"registrationId":"tab-b-ready","alias":"second","availability":"ready","registrationGeneration":2},{"registrationId":"tab-b-created","alias":"closing","agentCreated":true,"availability":"ready","registrationGeneration":2}]}"""
    do! waitUntil (fun () ->
        bridge.ListWindows() |> Result.exists (fun json -> json.Contains("window-main") && json.Contains("window-agents")) &&
        bridge.ListTabs() |> Result.exists (fun json -> json.Contains("tab-a-ready") && json.Contains("tab-b-ready")))

    let executionA = bridge.Execute("first", "document.title", "{}", 3000, true)
    let! executeA = (receive socketA).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeA "kind", Is.EqualTo("executeJs"))
    Assert.That(property executeA "registrationId", Is.EqualTo("tab-a-ready"))
    do! send socketA (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeA "requestId"
        connectionId = firstId; registrationId = "tab-a-ready"; registrationGeneration = 1
        result = "first-profile"
    |}))
    match! executionA with
    | Ok value -> Assert.That(value, Does.Contain("first-profile"))
    | Error error -> Assert.Fail(error)

    let executionB = bridge.Execute("second", "document.title", "{}", 3000, true)
    let! executeB = (receive socketB).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeB "kind", Is.EqualTo("executeJs"))
    Assert.That(property executeB "registrationId", Is.EqualTo("tab-b-ready"))
    do! send socketB (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeB "requestId"
        connectionId = secondId; registrationId = "tab-b-ready"; registrationGeneration = 2
        result = "second-profile"
    |}))
    match! executionB with
    | Ok value -> Assert.That(value, Does.Contain("second-profile"))
    | Error error -> Assert.Fail(error)

    let opening = bridge.OpenTab("agents", "https://example.test/new", "new", false)
    let! openFrame = (receive socketB).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openFrame "kind", Is.EqualTo("openTab"))
    Assert.That(property openFrame "windowRegistration", Is.EqualTo("window-agents"))
    do! send socketB (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openFrame "requestId"
        connectionId = secondId
        tab = {| registrationId = "tab-b-new"; agentCreated = true; registrationGeneration = 1 |}
    |}))
    match! opening with
    | Ok value -> Assert.That(value, Does.Contain("tab-b-new"))
    | Error error -> Assert.Fail(error)

    let closing = bridge.CloseTab("closing")
    let! closeFrame = (receive socketB).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closeFrame "kind", Is.EqualTo("closeTab"))
    Assert.That(property closeFrame "registrationId", Is.EqualTo("tab-b-created"))
    do! send socketB (JsonSerializer.Serialize({|
        version = 2; kind = "tabClosed"; requestId = property closeFrame "requestId"
        connectionId = secondId; registrationId = "tab-b-created"; registrationGeneration = 2
    |}))
    match! closing with
    | Ok _ -> ()
    | Error error -> Assert.Fail(error)

    let capturing = bridge.CaptureDom("first", """{"kind":"immediate"}""", "", 3000, None)
    let! captureFrame = (receive socketA).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property captureFrame "kind", Is.EqualTo("captureDom"))
    Assert.That(property captureFrame "registrationId", Is.EqualTo("tab-a-ready"))
    let markup = "<div>owned by first extension</div>"
    do! send socketA (JsonSerializer.Serialize({|
        version = 2; kind = "snapshotReady"; requestId = property captureFrame "requestId"
        connectionId = firstId; registrationId = "tab-a-ready"; registrationGeneration = 1
        bytes = Encoding.UTF8.GetByteCount(markup)
        title = "First"; url = "https://example.test/first"
        capturedAtUtc = "2026-11-15T01:00:00Z"; waitKind = "immediate"; html = markup
    |}))
    match! capturing with
    | Ok value -> Assert.That(value, Does.Contain("owned by first extension"))
    | Error error -> Assert.Fail(error)
}

[<Test>]
let revokedAmbiguousAndDisconnectedExtensionsFailClosed () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! first, firstId = handshake port "test-secret"
    use socketA = first
    let! second, _ = handshake port "test-secret"
    use socketB = second
    do! send socketA """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-main","alias":"main","availability":"ready","registrationGeneration":1}]}"""
    do! send socketA """{"version":2,"kind":"registrations","tabs":[{"registrationId":"tab-a-owned","alias":"collision","availability":"ready","registrationGeneration":1}]}"""
    do! send socketB """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-revoked","alias":"agents","availability":"needsReauthorization","registrationGeneration":1}]}"""
    do! send socketB """{"version":2,"kind":"registrations","tabs":[{"registrationId":"tab-b-revoked","alias":"collision","availability":"needsReauthorization","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        bridge.ListWindows() |> Result.exists (fun json -> json.Contains("window-main") && json.Contains("window-revoked")) &&
        bridge.ListTabs() |> Result.exists (fun json -> json.Contains("tab-a-owned") && json.Contains("tab-b-revoked")))

    match! bridge.OpenTab("agents", "https://example.test/", "", false) with
    | Error error -> Assert.That(error, Is.EqualTo("WindowUnavailable:needsReauthorization"))
    | Ok _ -> Assert.Fail("Revoked window must not be used")
    match! bridge.Execute("collision", "document.title", "{}", 3000, true) with
    | Error error -> Assert.That(error, Is.EqualTo("AliasAmbiguous"))
    | Ok _ -> Assert.Fail("Colliding aliases must fail closed")
    match! bridge.Execute("tab-b-revoked", "document.title", "{}", 3000, true) with
    | Error error -> Assert.That(error, Is.EqualTo("TabUnavailable:needsReauthorization"))
    | Ok _ -> Assert.Fail("Revoked tab must not execute")

    do! send socketB """{"version":2,"kind":"registrations","tabs":[{"registrationId":"tab-a-owned","alias":"other","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        bridge.ListTabs() |> Result.exists (fun json -> json.Contains("other")))
    match! bridge.Execute("tab-a-owned", "document.title", "{}", 3000, true) with
    | Error error -> Assert.That(error, Is.EqualTo("AliasAmbiguous"))
    | Ok _ -> Assert.Fail("Colliding registration IDs must fail closed")

    socketB.Abort()
    do! waitUntil (fun () ->
        bridge.ListWindows() |> Result.exists (fun json -> json.Contains("window-main") && not (json.Contains("window-revoked"))))
    match! bridge.OpenTab("agents", "https://example.test/", "", false) with
    | Error error -> Assert.That(error, Is.EqualTo("WindowNotRegistered"))
    | Ok _ -> Assert.Fail("Disconnected extension cannot receive opens")
    let execution = bridge.Execute("collision", "document.title", "{}", 3000, true)
    let! frame = (receive socketA).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property frame "registrationId", Is.EqualTo("tab-a-owned"))
    do! send socketA (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property frame "requestId"
        connectionId = firstId; registrationId = "tab-a-owned"; registrationGeneration = 1
        result = "surviving-profile"
    |}))
    match! execution with
    | Ok value -> Assert.That(value, Does.Contain("surviving-profile"))
    | Error error -> Assert.Fail(error)
}


[<Test>]
let secondExtensionArrivingDuringExecutionDoesNotAbortFirst () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! first, firstId = handshake port "test-secret"
    use socketA = first
    do! send socketA """{"version":2,"kind":"registrations","tabs":[{"registrationId":"inflight-a","alias":"inflight","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        bridge.ListTabs() |> Result.exists (fun value -> value.Contains("inflight-a")))
    let pending = bridge.Execute("inflight", "performOneNonIdempotentAction()", "{}", 3000, true)
    let! request = (receive socketA).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property request "kind", Is.EqualTo("executeJs"))
    let! second, _ = handshake port "test-secret"
    use socketB = second
    do! send socketB """{"version":2,"kind":"windows","windows":[{"windowRegistration":"other-window","alias":"other","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        bridge.ListWindows() |> Result.exists (fun value -> value.Contains("other-window")))
    do! send socketA (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property request "requestId"
        connectionId = firstId; registrationId = "inflight-a"; registrationGeneration = 1
        result = {| status = "submitted"; count = 1 |}
    |}))
    match! pending with
    | Ok value -> Assert.That(value, Does.Contain("\"count\":1"))
    | Error error -> Assert.Fail("A second extension must not cancel the first: " + error)
    Assert.That(bridge.ListTabs() |> Result.exists (fun value -> value.Contains("inflight-a")), Is.True)
}

[<Test>]
let compositeLaunchPinsOwnershipAcrossCollidingSecondExtension () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! first, firstId = handshake port "test-secret"
    use socketA = first
    do! registerReadyAgentWindow socketA bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { return { status: 'submitted' }; }" "{}" 3000 true
    let! openFrame = (receive socketA).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openFrame "kind", Is.EqualTo("openTab"))
    do! send socketA """{"version":2,"kind":"registrations","tabs":[{"registrationId":"same-created-id","alias":"mine","agentCreated":true,"availability":"loading","registrationGeneration":7}]}"""
    do! send socketA (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openFrame "requestId"
        connectionId = firstId
        tab = {| registrationId = "same-created-id"; agentCreated = true
                 availability = "loading"; registrationGeneration = 7 |}
    |}))
    let! second, _ = handshake port "test-secret"
    use socketB = second
    do! send socketB """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-other","alias":"other","availability":"ready","registrationGeneration":1}]}"""
    do! send socketB """{"version":2,"kind":"registrations","tabs":[{"registrationId":"same-created-id","alias":"other-tab","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil (fun () ->
        bridge.ListTabs() |> Result.exists (fun json -> json.Contains("other-tab")))
    // The unrelated profile must not make the original loading page appear ready.
    do! Task.Delay(125)
    do! send socketA """{"version":2,"kind":"registrations","tabs":[{"registrationId":"same-created-id","alias":"mine","agentCreated":true,"availability":"ready","registrationGeneration":7}]}"""
    let! execution = (receive socketA).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property execution "kind", Is.EqualTo("executeJs"))
    use executePacket = JsonDocument.Parse(execution)
    Assert.That(executePacket.RootElement.GetProperty("registrationGeneration").GetInt32(), Is.EqualTo(7))
    Assert.That(property execution "registrationId", Is.EqualTo("same-created-id"))
    do! send socketA (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property execution "requestId"
        connectionId = firstId; registrationId = "same-created-id"; registrationGeneration = 7
        result = {| status = "submitted" |}
    |}))
    let! closing = (receive socketA).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closing "kind", Is.EqualTo("closeTab"))
    Assert.That(property closing "registrationId", Is.EqualTo("same-created-id"))
    do! send socketA (JsonSerializer.Serialize({|
        version = 2; kind = "tabClosed"; requestId = property closing "requestId"
        connectionId = firstId; registrationId = "same-created-id"; registrationGeneration = 7
    |}))
    match! launch with
    | Ok value -> Assert.That(value, Does.Contain("closed"))
    | Error (_, error) -> Assert.Fail("Pinned multi-step launch failed: " + error)
    match! bridge.Execute("same-created-id", "document.title", "{}", 3000, true) with
    | Error error -> Assert.That(error, Is.EqualTo("AliasAmbiguous"))
    | Ok _ -> Assert.Fail("An unpinned call must reject duplicate registration IDs")
}

[<Test>]
let compositeLaunchRejectsManualReRegistrationWithoutInjection () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { return {status:'submitted'}; }" "{}" 5000 true
    let! openFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openFrame "kind", Is.EqualTo("openTab"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"ownership-tab","alias":"owned","agentCreated":true,"availability":"loading","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openFrame "requestId"
        connectionId = connectionId
        tab = {| registrationId = "ownership-tab"; agentCreated = true
                 registrationGeneration = 1; availability = "loading" |}
    |}))
    do! waitUntil (fun () ->
        bridge.ListTabs() |> Result.exists (fun x -> x.Contains("ownership-tab")))
    // Manual registration adopts the same tab ID but changes its generation
    // and removes agent-created provenance. The composite must not execute or close it.
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"ownership-tab","alias":"user-owned","agentCreated":false,"availability":"ready","registrationGeneration":2}]}"""
    match! launch.WaitAsync(TimeSpan.FromSeconds(3.0)) with
    | Error (id, reason) ->
        Assert.That(id, Is.EqualTo("ownership-tab"))
        Assert.That(reason, Is.EqualTo("TabRegistrationChanged; cleanup=retained"))
    | Ok _ -> Assert.Fail("Manually re-registered tab was accepted for bootstrap")

    match! bridge.Execute("ownership-tab", "dangerousSubmit()", "{}", 1000, true,
                          ownerConnectionId = connectionId,
                          expectedRegistrationGeneration = 1, requireAgentCreated = true) with
    | Error error -> Assert.That(error, Is.EqualTo("TabRegistrationChanged"))
    | Ok _ -> Assert.Fail("Stale generation must be rejected before execute dispatch")
    match! bridge.CloseTab("ownership-tab", ownerConnectionId = connectionId,
                           expectedRegistrationGeneration = 1) with
    | Error error -> Assert.That(error, Is.EqualTo("TabRegistrationChanged"))
    | Ok _ -> Assert.Fail("Stale generation must never close a user-owned tab")
}


[<Test>]
let sameOriginCompositeLaunchesWaitThroughSubmissionAndCleanup () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let script = "async function execute(args) { return { status: 'submitted' }; }"
    let first = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "first" false
                    script """{"prompt":"first"}""" 5000 true
    let! openFirst = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openFirst "kind", Is.EqualTo("openTab"))

    // This call shares the origin and must not open its tab before the first
    // launch has finished both execution and created-tab cleanup.
    let second = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "second" false
                     script """{"prompt":"second"}""" 5000 true
    let nextFrame = receive socket
    do! Task.Delay(150)
    Assert.That(nextFrame.IsCompleted, Is.False,
                "Concurrent launch opened another tab while the first still owned the shared origin")

    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"serialized-first","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openFirst "requestId"
        connectionId = connectionId
        tab = {| registrationId = "serialized-first"; availability = "ready"
                 agentCreated = true; registrationGeneration = 1 |}
    |}))
    let! executeFirst = nextFrame.WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeFirst "kind", Is.EqualTo("executeJs"))
    Assert.That(property executeFirst "registrationId", Is.EqualTo("serialized-first"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeFirst "requestId"
        connectionId = connectionId; registrationId = "serialized-first"
        registrationGeneration = 1; result = {| status = "submitted" |}
    |}))
    let! closeFirst = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closeFirst "kind", Is.EqualTo("closeTab"))
    Assert.That(property closeFirst "registrationId", Is.EqualTo("serialized-first"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabClosed"; requestId = property closeFirst "requestId"
        connectionId = connectionId; registrationId = "serialized-first"; registrationGeneration = 1
    |}))

    // Only after the first cleanup is acknowledged may the second create a tab.
    let! openSecond = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openSecond "kind", Is.EqualTo("openTab"))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"serialized-second","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openSecond "requestId"
        connectionId = connectionId
        tab = {| registrationId = "serialized-second"; availability = "ready"
                 agentCreated = true; registrationGeneration = 1 |}
    |}))
    let! executeSecond = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeSecond "kind", Is.EqualTo("executeJs"))
    Assert.That(property executeSecond "registrationId", Is.EqualTo("serialized-second"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeSecond "requestId"
        connectionId = connectionId; registrationId = "serialized-second"
        registrationGeneration = 1; result = {| status = "submitted" |}
    |}))
    let! closeSecond = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property closeSecond "kind", Is.EqualTo("closeTab"))
    Assert.That(property closeSecond "registrationId", Is.EqualTo("serialized-second"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabClosed"; requestId = property closeSecond "requestId"
        connectionId = connectionId; registrationId = "serialized-second"; registrationGeneration = 1
    |}))

    match! first with
    | Ok result -> Assert.That(result, Does.Contain("closed"))
    | Error (_, error) -> Assert.Fail("First launch failed: " + error)
    match! second with
    | Ok result -> Assert.That(result, Does.Contain("closed"))
    | Error (_, error) -> Assert.Fail("Second launch failed: " + error)
}
