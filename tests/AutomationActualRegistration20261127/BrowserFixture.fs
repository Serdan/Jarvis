module ActualRegistrationBrowser

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

let waitUntil (label: string) (check: unit -> bool) = task {
    let deadline = DateTime.UtcNow.AddSeconds(15.)
    while not (check()) && DateTime.UtcNow < deadline do
        do! Task.Delay(50)
    Assert.That(check(), Is.True, label)
}

let private send (socket: ClientWebSocket) (value: string) = task {
    let bytes = Encoding.UTF8.GetBytes value
    do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
}

let private receive (socket: ClientWebSocket) = task {
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

/// Real local BrowserBridge, test-only secret and paired fake extension. No
/// browser or remote page is opened; frames are acknowledged in this process.
let withBrowser (rt: Runtime) (run: BrowserBridge.Service -> (unit -> bool) -> (string -> string -> (JsonElement -> unit) -> Task<unit>) -> Task<unit>) = task {
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    use bridge = new BrowserBridge.Service(port, "actual-registration-fixture-secret")
    bridge.Start()
    rt.Browser <- Some bridge
    use socket = new ClientWebSocket()
    socket.Options.SetRequestHeader("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    do! socket.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"), CancellationToken.None)
    let! challenge = (receive socket).WaitAsync(TimeSpan.FromSeconds(5.))
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes("actual-registration-fixture-secret"))
    let proof = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(prop challenge "nonce"))).ToLowerInvariant()
    do! send socket (JsonSerializer.Serialize({| version=2; kind="authenticate"; proof=proof |}))
    let! ready = (receive socket).WaitAsync(TimeSpan.FromSeconds(5.))
    let connection = prop ready "connectionId"
    do! send socket """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-01","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
    do! waitUntil "Paired fake extension window missing" (fun () -> bridge.ListWindows() |> Result.exists (fun s -> s.Contains("window-01")))
    let mutable pending = receive socket
    let noFrame () = not pending.IsCompleted
    let respond (tab: string) (status: string) (inspect: JsonElement -> unit) = task {
        let! frame = pending.WaitAsync(TimeSpan.FromSeconds(15.))
        Assert.That(prop frame "kind", Is.EqualTo("openTab"), "Original registered route must reach the fake extension.")
        do! send socket (JsonSerializer.Serialize({| version=2; kind="registrations"; tabs=[| {| registrationId=tab; alias="new"; agentCreated=true; availability="ready"; registrationGeneration=1 |} |] |}))
        do! send socket (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop frame "requestId"; connectionId=connection; tab={| registrationId=tab; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
        let! execute = (receive socket).WaitAsync(TimeSpan.FromSeconds(5.))
        Assert.That(prop execute "kind", Is.EqualTo("executeJs"))
        use request = JsonDocument.Parse execute
        inspect request.RootElement
        do! send socket (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connection; registrationId=tab; registrationGeneration=1; status="completed"; result={| status=status; stage="acknowledgement"; reason="FixtureOnly" |} |}))
        let! close = (receive socket).WaitAsync(TimeSpan.FromSeconds(5.))
        Assert.That(prop close "kind", Is.EqualTo("closeTab"))
        Assert.That(prop close "registrationId", Is.EqualTo(tab))
        do! send socket (JsonSerializer.Serialize({| version=2; kind="tabClosed"; requestId=prop close "requestId"; connectionId=connection; registrationId=tab; registrationGeneration=1 |}))
        pending <- receive socket
    }
    try
        do! run bridge noFrame respond
    finally
        rt.Browser <- None
        socket.Abort()
}
