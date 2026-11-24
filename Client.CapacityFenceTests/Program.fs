module CapacityFenceTests
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
open Client
open Common
open Common.AgentProtocol

let check b message = if not b then failwith message
let send (s:ClientWebSocket) value = task {
    let bytes = Encoding.UTF8.GetBytes(value:string)
    do! s.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
}
let receive (s:ClientWebSocket) = task {
    let bytes = Array.zeroCreate<byte> 8192
    let! r = s.ReceiveAsync(ArraySegment<byte>(bytes), CancellationToken.None)
    return Encoding.UTF8.GetString(bytes, 0, r.Count)
}
let prop (text:string) key =
    use doc = JsonDocument.Parse(text)
    doc.RootElement.GetProperty(key:string).GetString()

let run () = task {
    let root = Path.Combine(Path.GetTempPath(), "capacity-fence-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root)
    try
        let config = ClientConfig.configPath()
        Directory.CreateDirectory(Path.GetDirectoryName(config)) |> ignore
        let write (text:string) = File.WriteAllText(config,text)
        check (ClientConfig.capacityFenceModeAt config = Ok ClientConfig.Disabled) "missing defaults disabled"
        for raw in [ """{"schemaVersion":1}"""; """{"schemaVersion":1,"capacityFenceEnabled":false}""" ] do
            write raw
            check (ClientConfig.capacityFenceModeAt config = Ok ClientConfig.Disabled) "disabled"
        write """{"schemaVersion":1,"capacityFenceEnabled":true}"""
        check (ClientConfig.capacityFenceModeAt config = Ok ClientConfig.NegativeOnly) "enabled"
        let snapshot = ClientConfig.loadAt config |> Result.defaultWith failwith
        ClientConfig.save snapshot snapshot.Stored |> Result.defaultWith failwith |> ignore
        check ((File.ReadAllText config).Contains("\"capacityFenceEnabled\"")) "settings save preserved unknown property"
        for raw in [ """{"schemaVersion":1,"capacityFenceEnabled":null}"""; """{"schemaVersion":1,"capacityFenceEnabled":"true"}"""; """{"schemaVersion":2}"""; "{"; " " ] do
            write raw
            check ((ClientConfig.capacityFenceModeAt config).IsError) "invalid config must fail closed"

        let project = Path.Combine(root,"Project1")
        Directory.CreateDirectory(project) |> ignore
        File.WriteAllText(Path.Combine(project,"launch.js"), "async function execute(args) { return args; }")
        use listener = new TcpListener(IPAddress.Loopback,0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        use bridge = new BrowserBridge.Service(port,"test-secret")
        bridge.Start()
        use socket = new ClientWebSocket()
        socket.Options.SetRequestHeader("Origin","chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        do! socket.ConnectAsync(Uri("ws://127.0.0.1:" + string port + "/"),CancellationToken.None)
        let! challenge = (receive socket).WaitAsync(TimeSpan.FromSeconds(5.))
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes("test-secret"))
        let proof = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(prop challenge "nonce"))).ToLowerInvariant()
        do! send socket (JsonSerializer.Serialize({| version=2; kind="authenticate"; proof=proof |}))
        let! ready = (receive socket).WaitAsync(TimeSpan.FromSeconds(5.))
        let connectionId = prop ready "connectionId"
        do! send socket """{"version":2,"kind":"windows","windows":[{"windowRegistration":"window-01","alias":"agents","availability":"ready","registrationGeneration":1}]}"""
        let mutable registered = false
        for _ in 1..80 do
            if bridge.ListWindows() |> Result.exists (fun s -> s.Contains("window-01")) then registered <- true
            elif not registered then do! Task.Delay(25)
        check registered "window registration"
        let rt = Runtime(root, ConsoleTui.ConsoleTui(), PartialTrust)
        rt.Browser <- Some bridge
        let command = BrowserRunInNewTabCommand { WindowRegistration="agents"; Url="https://example.test/"; ProjectName="Project1"; ScriptPath="launch.js"; ArgsJson=None; Active=None; CloseTabAfterExecution=Some false; TimeoutMs=Some 1000 }
        let pending = receive socket
        for raw in [ """{"schemaVersion":1,"capacityFenceEnabled":true}"""; """{"schemaVersion":1,"capacityFenceEnabled":"bad"}""" ] do
            write raw
            let! result = Client.SignalR.Client.receiveCommand rt command
            check (sprintf "%A" result |> fun s -> s.Contains("HOLD")) "actual dispatch must HOLD"
            do! Task.Delay(100)
            check (not pending.IsCompleted) "HOLD must emit no browser request"
        write """{"schemaVersion":1,"capacityFenceEnabled":false}"""
        let launch = Client.SignalR.Client.receiveCommand rt command
        let! frame = pending.WaitAsync(TimeSpan.FromSeconds(5.))
        check (prop frame "kind" = "openTab") "disabled dispatch must reach browser"
        do! send socket """{"version":2,"kind":"registrations","tabs":[{"registrationId":"created-01","alias":"new","agentCreated":true,"availability":"ready","registrationGeneration":1}]}"""
        do! send socket (JsonSerializer.Serialize({| version=2; kind="tabOpened"; requestId=prop frame "requestId"; connectionId=connectionId; tab={| registrationId="created-01"; availability="ready"; agentCreated=true; registrationGeneration=1 |} |}))
        let! execute = (receive socket).WaitAsync(TimeSpan.FromSeconds(5.))
        check (prop execute "kind" = "executeJs") "disabled dispatch executes"
        do! send socket (JsonSerializer.Serialize({| version=2; kind="result"; requestId=prop execute "requestId"; connectionId=connectionId; registrationId="created-01"; registrationGeneration=1; status="completed"; result= {| ok=true |} |}))
        let! outcome = launch.WaitAsync(TimeSpan.FromSeconds(5.))
        check outcome.IsOk "disabled dispatch completes"
        ()
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior)
        Directory.Delete(root,true)
}
[<EntryPoint>]
let main _ =
    run().GetAwaiter().GetResult()
    printfn "capacity fence checks passed"
    0
