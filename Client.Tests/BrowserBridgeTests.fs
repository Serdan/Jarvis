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
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"retry-tab","availability":"loading","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "retry-tab"; availability = "loading"; agentCreated = true |}
    |}))
    do! Task.Delay(80)
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"retry-tab","availability":"ready","registrationGeneration":1}]}"""
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
let compositeLaunchNeverReplaysUncertainPostInjectionOutcome () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { submitMessage(); return args; }" "{}" 5000 true
    let! opened = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"uncertain-tab","availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property opened "requestId"
        connectionId = connectionId
        tab = {| registrationId = "uncertain-tab"; availability = "ready"; agentCreated = true |}
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
    let! outcome = launch.WaitAsync(TimeSpan.FromSeconds(3.0))
    match outcome with
    | Error (registrationId, why) ->
        Assert.That(registrationId, Is.EqualTo("uncertain-tab"))
        Assert.That(why, Does.Contain("OutcomeUnknown:RegistrationChangedDuringExecution"))
        // Retain tab for manual reconciliation; never close after uncertain effects.
        match bridge.ListTabs() with
        | Ok listing -> Assert.That(listing, Does.Contain("uncertain-tab"))
        | Error error -> Assert.Fail(error)
    | Ok _ -> Assert.Fail("Post-dispatch unknown cannot be treated as a successful submission")
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

[<TestCase("notSubmitted", true)>]
[<TestCase("outcomeUnknown", false)>]
[<TestCase("uiIncompatible", false)>]
let compositeLaunchKeepsUncertainTabButClosesDefinitiveFailure (scriptStatus: string, shouldClose: bool) = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge
    let launch = BrowserBridge.runInNewTab bridge "agents" "https://chatgpt.com/" "" false
                     "async function execute(args) { return args; }" "{}" 5000 true
    let! openFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-tab-02","alias":"new","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openFrame "requestId"
        connectionId = connectionId
        tab = {| registrationId = "created-tab-02"; availability = "ready"; agentCreated = true |}
    |}))
    let! executeFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property executeFrame "kind", Is.EqualTo("executeJs"))
    do! send socket (JsonSerializer.Serialize({|
        version = 2; kind = "result"; requestId = property executeFrame "requestId"
        connectionId = connectionId; registrationId = "created-tab-02"
        registrationGeneration = 1; status = "completed"
        result = {| status = scriptStatus; stage = "modeSelection"; reason = "ModeToggleUnavailable"
                    diagnostics = {| chatLabels = 0; editableTextboxes = 1;
                                     documentReadyState = "complete" |} |}
    |}))
    if shouldClose then
        let! closeFrame = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.That(property closeFrame "kind", Is.EqualTo("closeTab"))
        do! send socket (JsonSerializer.Serialize({|
            version = 2; kind = "tabClosed"; requestId = property closeFrame "requestId"
            connectionId = connectionId; registrationId = "created-tab-02"
            registrationGeneration = 1
        |}))
    let! result = launch
    match result with
    | Ok response when shouldClose ->
        Assert.That(response, Does.Contain("notSubmitted"))
        Assert.That(response, Does.Contain("closed"))
    | Error (registrationId, reason) when not shouldClose ->
        Assert.That(registrationId, Is.EqualTo("created-tab-02"))
        if scriptStatus = "uiIncompatible" then
            Assert.That(reason, Does.Contain("UiIncompatible:modeSelection:ModeToggleUnavailable"))
            Assert.That(reason, Does.Contain("chatLabels=0"))
            Assert.That(reason, Does.Contain("editableTextboxes=1"))
        else
            Assert.That(reason, Does.Contain("OutcomeUnknown"))
    | _ -> Assert.Fail("Unexpected script failure handling")
}

[<Test>]
let compositeNotReadyCleanupReportsFailureOrConfirmedClosure () = task {
    let port = findPort()
    use bridge = new BrowserBridge.Service(port, "test-secret")
    bridge.Start()
    let! client, connectionId = handshake port "test-secret"
    use socket = client
    do! registerReadyAgentWindow socket bridge

    // These two workflows wait for distinct created tabs that become discarded.
    // They exercise the real 30-second bounded readiness path concurrently.
    let fail = BrowserBridge.runInNewTab bridge "agents" "https://example.test/fail" "fail" false
                   "async function execute(args) { return args; }" "{}" 5000 true
    let close = BrowserBridge.runInNewTab bridge "agents" "https://example.test/close" "close" false
                    "async function execute(args) { return args; }" "{}" 5000 true

    let! openA = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    let! openB = (receive socket).WaitAsync(TimeSpan.FromSeconds(3.0))
    Assert.That(property openA "kind", Is.EqualTo("openTab"))
    Assert.That(property openB "kind", Is.EqualTo("openTab"))
    let idFor frame =
        if property frame "url" = "https://example.test/fail" then "discarded-fail"
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
    Assert.That(property openFrame "windowRegistration", Is.EqualTo("agents"))
    do! send socketB (JsonSerializer.Serialize({|
        version = 2; kind = "tabOpened"; requestId = property openFrame "requestId"
        connectionId = secondId
        tab = {| registrationId = "tab-b-new"; agentCreated = true |}
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
