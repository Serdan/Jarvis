module Client.BrowserBridge

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

[<Literal>]
let ProtocolVersion = 2
[<Literal>]
let MaxScriptBytes = 65536
[<Literal>]
let MaxArgumentsBytes = 65536
[<Literal>]
let MaxResultBytes = 262144
[<Literal>]
let MaxFrameBytes = 400000

let private utf8 = UTF8Encoding(false, true)

let secretFile () =
    Path.Combine(Path.GetDirectoryName(Client.ClientConfig.configPath()), "browser-bridge.key")

let getOrCreateSecret () =
    let path = secretFile()
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    if File.Exists(path) then
        if File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
            invalidOp "Browser bridge pairing key must not be a symlink."
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let mode = File.GetUnixFileMode(path)
            if (mode &&& (UnixFileMode.GroupRead ||| UnixFileMode.OtherRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)) <> enum 0 then
                invalidOp "Browser bridge key permissions are too broad."
        File.ReadAllText(path).Trim()
    else
        let key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        use file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        use writer = new StreamWriter(file, utf8)
        writer.Write(key)
        key

let private asString (node: JsonNode) (key: string) =
    try
        match node[key] with
        | null -> ""
        | value -> value.GetValue<string>()
    with _ -> ""

let private asInt (node: JsonNode) (key: string) =
    try node[key].GetValue<int>() with _ -> 0

let private serialize (node: obj) = JsonSerializer.Serialize(node)

type private Connection =
    { Socket: WebSocket
      Id: string
      SendLock: SemaphoreSlim
      Pending: ConcurrentDictionary<string, TaskCompletionSource<string>>
      mutable Registrations: JsonElement list
      mutable Windows: JsonElement list }

type Service(port: int, pairingSecret: string) =
    let listener = new HttpListener()
    let cancellation = new CancellationTokenSource()
    let sync = obj()
    let mutable active: Connection option = None
    let mutable acceptLoop: Task = Task.CompletedTask

    let send (conn: Connection) (payload: string) = task {
        if Encoding.UTF8.GetByteCount(payload) > MaxFrameBytes then
            invalidOp "Browser bridge frame too large."
        do! conn.SendLock.WaitAsync(cancellation.Token)
        try
            let bytes = utf8.GetBytes(payload)
            do! conn.Socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token)
        finally conn.SendLock.Release() |> ignore
    }

    let read (socket: WebSocket) = task {
        let buffer = Array.zeroCreate<byte> 8192
        use stream = new MemoryStream()
        let mutable complete = false
        while not complete do
            let! part = socket.ReceiveAsync(ArraySegment<byte>(buffer), cancellation.Token)
            if part.MessageType = WebSocketMessageType.Close then
                raise (WebSocketException("Browser extension disconnected."))
            if part.MessageType <> WebSocketMessageType.Text then
                invalidOp "Only text WebSocket messages are accepted."
            stream.Write(buffer, 0, part.Count)
            if stream.Length > int64 MaxFrameBytes then
                invalidOp "Browser bridge frame exceeds limit."
            complete <- part.EndOfMessage
        return utf8.GetString(stream.ToArray())
    }

    let validOrigin (origin: string) =
        if not (origin.StartsWith("chrome-extension://", StringComparison.Ordinal)) then false
        else
            let id = origin.Substring("chrome-extension://".Length)
            id.Length = 32 && (id |> Seq.forall (fun c -> c >= 'a' && c <= 'p'))

    let authenticated (conn: Connection) = task {
        let nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        do! send conn (serialize {| version = ProtocolVersion; kind = "challenge"; nonce = nonce |})
        // A bounded challenge window prevents unauthenticated clients holding connections indefinitely.
        let! incoming = (read conn.Socket).WaitAsync(TimeSpan.FromSeconds(10.0))
        use packet = JsonDocument.Parse(incoming)
        let root = JsonNode.Parse(incoming)
        let signature = asString root "proof"
        let expected =
            use hmac = new HMACSHA256(utf8.GetBytes(pairingSecret))
            Convert.ToHexString(hmac.ComputeHash(utf8.GetBytes(nonce))).ToLowerInvariant()
        let valid =
            asInt root "version" = ProtocolVersion &&
            asString root "kind" = "authenticate" &&
            signature.Length = expected.Length &&
            CryptographicOperations.FixedTimeEquals(utf8.GetBytes(signature), utf8.GetBytes(expected))
        if not valid then invalidOp "Browser bridge authentication failed."
        do! send conn (serialize {| version = ProtocolVersion; kind = "ready"; connectionId = conn.Id |})
    }

    let handle (context: HttpListenerContext) = task {
        if not context.Request.IsWebSocketRequest ||
           not (validOrigin (context.Request.Headers["Origin"] |> Option.ofObj |> Option.defaultValue "")) then
            context.Response.StatusCode <- 403
            context.Response.Close()
        else
            let! upgrade = context.AcceptWebSocketAsync(null)
            use socket = upgrade.WebSocket
            let conn =
                { Socket = socket; Id = Guid.NewGuid().ToString("N")
                  SendLock = new SemaphoreSlim(1, 1)
                  Pending = ConcurrentDictionary()
                  Registrations = []; Windows = [] }
            try
                try
                    do! authenticated conn
                    lock sync (fun () ->
                        match active with
                        | Some old ->
                            for KeyValue(_,tcs) in old.Pending do
                                tcs.TrySetException(IOException("Browser extension replaced connection; outcome unknown.")) |> ignore
                            try old.Socket.Abort() with _ -> ()
                        | None -> ()
                        active <- Some conn)
                    while socket.State = WebSocketState.Open && not cancellation.IsCancellationRequested do
                        let! payload = read socket
                        let doc = JsonNode.Parse(payload)
                        if asInt doc "version" <> ProtocolVersion then
                            invalidOp "Unsupported browser bridge protocol version."
                        match asString doc "kind" with
                        | "registrations" ->
                            match doc["tabs"] with
                            | :? JsonArray as tabs when tabs.Count <= 100 ->
                                let parsed =
                                    tabs
                                    |> Seq.choose (fun t ->
                                        if isNull t then None
                                        else
                                            let id = asString t "registrationId"
                                            let alias = asString t "alias"
                                            if id.Length < 8 || id.Length > 128 || alias.Length > 128 then None
                                            else Some (JsonSerializer.SerializeToElement(t)))
                                    |> Seq.toList
                                lock sync (fun () -> conn.Registrations <- parsed)
                            | _ -> ()
                        | "windows" ->
                            match doc["windows"] with
                            | :? JsonArray as windows when windows.Count <= 100 ->
                                let parsed = windows |> Seq.choose (fun w ->
                                    if isNull w then None
                                    else
                                        let id = asString w "windowRegistration"
                                        let alias = asString w "alias"
                                        if id.Length < 8 || id.Length > 128 || alias.Length > 128 then None
                                        else Some(JsonSerializer.SerializeToElement(w))) |> Seq.toList
                                lock sync (fun () -> conn.Windows <- parsed)
                            | _ -> ()
                        | "result" | "error" | "tabOpened" | "tabClosed" ->
                            let requestId = asString doc "requestId"
                            let mutable tcs = Unchecked.defaultof<TaskCompletionSource<string>>
                            if conn.Pending.TryRemove(requestId, &tcs) then
                                tcs.TrySetResult(payload) |> ignore
                        | "pong" -> ()
                        | _ -> ()
                with _ -> ()
            finally
                lock sync (fun () ->
                    if active |> Option.exists (fun x -> obj.ReferenceEquals(x, conn)) then active <- None)
                for KeyValue(_,tcs) in conn.Pending do
                    tcs.TrySetException(IOException("Browser bridge disconnected; outcome unknown.")) |> ignore
                conn.SendLock.Dispose()
                try socket.Abort() with _ -> ()
    }

    member _.Start() =
        if port < 1024 || port > 65535 then invalidArg "port" "Invalid browser bridge port."
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()
        acceptLoop <- task {
            try
                while not cancellation.IsCancellationRequested do
                    let! context = listener.GetContextAsync()
                    handle context |> ignore
            with
            | :? HttpListenerException when cancellation.IsCancellationRequested -> ()
            | :? ObjectDisposedException when cancellation.IsCancellationRequested -> ()
        }

    member _.ListTabs() =
        lock sync (fun () ->
            match active with
            | None -> Error "ExtensionDisconnected"
            | Some conn ->
                let tabs = conn.Registrations |> List.map (fun x -> x.Clone())
                Ok (serialize {| tabs = tabs |}))

    member _.ListWindows() =
        lock sync (fun () ->
            match active with
            | None -> Error "ExtensionDisconnected"
            | Some conn -> Ok (serialize {| windows = conn.Windows |> List.map (fun x -> x.Clone()) |}))

    member _.OpenTab(windowRegistration: string, url: string, alias: string, makeActive: bool) = task {
        let isValid =
            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri ->
                (uri.Scheme = "http" || uri.Scheme = "https") &&
                String.IsNullOrEmpty(uri.UserInfo) &&
                url.Length <= 2048
            | _ -> false
        if String.IsNullOrWhiteSpace(windowRegistration) then return Error "WindowNotRegistered"
        elif not isValid then return Error "InvalidTabUrl: absolute HTTP(S) URL without credentials required."
        elif isNull alias || alias.Length > 128 || (alias |> Seq.exists Char.IsControl) then
            return Error "InvalidTabAlias"
        else
            let connection = lock sync (fun () ->
                match active with
                | None -> Error "ExtensionDisconnected"
                | Some conn when conn.Pending.Count >= 4 -> Error "BridgeBusy"
                | Some conn -> Ok conn)
            match connection with
            | Error error -> return Error error
            | Ok conn ->
                let requestId = Guid.NewGuid().ToString("N")
                let completion = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                if not (conn.Pending.TryAdd(requestId, completion)) then
                    return Error "DuplicateRequest"
                else
                    try
                        try
                            let frame = serialize
                                            {| version = ProtocolVersion
                                               kind = "openTab"
                                               requestId = requestId
                                               connectionId = conn.Id
                                               url = url
                                               windowRegistration = windowRegistration
                                               alias = alias
                                               active = makeActive |}
                            do! send conn frame
                            let! response = completion.Task.WaitAsync(TimeSpan.FromSeconds(15.0))
                            if utf8.GetByteCount(response) > MaxResultBytes then
                                return Error "OutputTooLarge"
                            else
                                let document = JsonNode.Parse(response)
                                if asString document "connectionId" <> conn.Id then
                                    return Error "ResponseIdentityMismatch"
                                elif asString document "kind" = "error" then
                                    return Error ("BrowserOpenTabError:" + asString document "error")
                                elif asString document "kind" <> "tabOpened" then
                                    return Error "UnexpectedBrowserResponse"
                                else
                                    return Ok response
                        with
                        | :? TimeoutException -> return Error "OutcomeUnknown: tab may have been opened."
                        | ex -> return Error ("OutcomeUnknown: " + ex.GetType().Name)
                    finally
                        let mutable removed = Unchecked.defaultof<TaskCompletionSource<string>>
                        conn.Pending.TryRemove(requestId, &removed) |> ignore
    }

    member _.CloseTab(tabRegistration: string) = task {
        let resolved = lock sync (fun () ->
            match active with
            | None -> Error "ExtensionDisconnected"
            | Some conn ->
                match conn.Registrations |> List.filter (fun tab ->
                    tab.GetProperty("registrationId").GetString() = tabRegistration ||
                    (match tab.TryGetProperty("alias") with | true, a -> a.GetString() = tabRegistration | _ -> false)) with
                | [] -> Error "TabNotRegistered"
                | [tab] ->
                    let agentCreated =
                        match tab.TryGetProperty("agentCreated") with
                        | true, value -> value.GetBoolean()
                        | _ -> false
                    if not agentCreated then Error "TabNotAgentCreated"
                    elif conn.Pending.Count >= 4 then Error "BridgeBusy"
                    else Ok(conn, tab)
                | _ -> Error "AliasAmbiguous")
        match resolved with
        | Error e -> return Error e
        | Ok(conn, tab) ->
            let requestId = Guid.NewGuid().ToString("N")
            let registrationId = tab.GetProperty("registrationId").GetString()
            let generation = tab.GetProperty("registrationGeneration").GetInt32()
            let completion = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            if not (conn.Pending.TryAdd(requestId, completion)) then return Error "DuplicateRequest"
            else
                try
                    try
                        do! send conn (serialize
                            {| version = ProtocolVersion; kind = "closeTab"
                               requestId = requestId; connectionId = conn.Id
                               registrationId = registrationId; registrationGeneration = generation |})
                        let! response = completion.Task.WaitAsync(TimeSpan.FromSeconds(15.0))
                        let root = JsonNode.Parse(response)
                        if asString root "connectionId" <> conn.Id ||
                           asString root "registrationId" <> registrationId ||
                           asInt root "registrationGeneration" <> generation then
                            return Error "ResponseIdentityMismatch"
                        elif asString root "kind" = "error" then
                            return Error ("BrowserCloseTabError:" + asString root "error")
                        elif asString root "kind" <> "tabClosed" then
                            return Error "UnexpectedBrowserResponse"
                        else return Ok """{"closed":true}"""
                    with
                    | :? TimeoutException -> return Error "OutcomeUnknown: tab may have been closed."
                    | ex -> return Error ("OutcomeUnknown: " + ex.GetType().Name)
                finally
                    let mutable removed = Unchecked.defaultof<TaskCompletionSource<string>>
                    conn.Pending.TryRemove(requestId, &removed) |> ignore
    }

    member _.Execute(tabRegistration: string, source: string, argsJson: string, timeoutMs: int, isInline: bool) = task {
        let sourceBytes = utf8.GetByteCount(source)
        if sourceBytes > MaxScriptBytes then return Error "InputTooLarge: script exceeds 64 KiB."
        elif utf8.GetByteCount(argsJson) > MaxArgumentsBytes then return Error "InputTooLarge: arguments exceed 64 KiB."
        elif timeoutMs < 1 || timeoutMs > 30000 then return Error "InvalidTimeout"
        else
            try
                use arguments = JsonDocument.Parse(argsJson)
                let resolved = lock sync (fun () ->
                    match active with
                    | None -> Error "ExtensionDisconnected"
                    | Some conn ->
                        let matching =
                            conn.Registrations
                            |> List.filter (fun tab ->
                                let id = tab.GetProperty("registrationId").GetString()
                                let alias =
                                    match tab.TryGetProperty("alias") with
                                    | true, x -> x.GetString()
                                    | _ -> ""
                                id = tabRegistration || alias = tabRegistration)
                        match matching with
                        | [] -> Error "TabNotRegistered"
                        | [tab] ->
                            let state =
                                match tab.TryGetProperty("availability") with
                                | true, x -> x.GetString()
                                | _ -> ""
                            if state <> "ready" then Error ("TabUnavailable:" + state)
                            elif conn.Pending.Count >= 4 then Error "BridgeBusy"
                            else Ok(conn, tab)
                        | _ -> Error "AliasAmbiguous")
                match resolved with
                | Error error -> return Error error
                | Ok(conn, tab) ->
                    let id = Guid.NewGuid().ToString("N")
                    let registrationId = tab.GetProperty("registrationId").GetString()
                    let generation =
                        match tab.TryGetProperty("registrationGeneration") with
                        | true, x -> x.GetInt32()
                        | _ -> 0
                    let promise = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                    if not (conn.Pending.TryAdd(id, promise)) then return Error "DuplicateRequest"
                    else
                        try
                            try
                                let args = arguments.RootElement.Clone()
                                let frame = serialize
                                                {| version = ProtocolVersion
                                                   kind = "executeJs"
                                                   requestId = id
                                                   connectionId = conn.Id
                                                   registrationId = registrationId
                                                   registrationGeneration = generation
                                                   source = source
                                                   entrypoint = (if isInline then "expression" else "execute")
                                                   args = args
                                                   timeoutMs = timeoutMs |}
                                do! send conn frame
                                let! response = promise.Task.WaitAsync(TimeSpan.FromMilliseconds(float timeoutMs))
                                if utf8.GetByteCount(response) > MaxResultBytes then return Error "OutputTooLarge"
                                else
                                    use returned = JsonDocument.Parse(response)
                                    let node = JsonNode.Parse(response)
                                    if asString node "connectionId" <> conn.Id ||
                                       asString node "registrationId" <> registrationId ||
                                       asInt node "registrationGeneration" <> generation then
                                        return Error "ResponseIdentityMismatch"
                                    elif asString node "kind" = "error" then
                                        return Error ("BrowserExecutionError:" + asString node "error")
                                    elif asString node "kind" <> "result" then
                                        return Error "UnexpectedBrowserResponse"
                                    else return Ok response
                            with
                            | :? TimeoutException -> return Error "OutcomeUnknown: execution timed out after dispatch."
                            | ex -> return Error ("OutcomeUnknown: " + ex.GetType().Name)
                        finally
                            let mutable removed = Unchecked.defaultof<TaskCompletionSource<string>>
                            conn.Pending.TryRemove(id, &removed) |> ignore
            with
            | :? JsonException -> return Error "InvalidArgumentsJson"
            | ex -> return Error ("BridgeFailure:" + ex.GetType().Name)
    }

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            listener.Close()
            lock sync (fun () ->
                active |> Option.iter (fun conn -> conn.Socket.Abort())
                active <- None)
            cancellation.Dispose()


// Generic one-call browser workflow shared by immediate agent handoffs and scheduled actions.
// Project scripts own site-specific semantics; Jarvis only owns created-tab lifecycle.
let runInNewTab (bridge: Service) (windowRegistration: string) (url: string)
                (alias: string) (makeActive: bool) (source: string) (argsJson: string)
                (timeoutMs: int) (closeAfterExecution: bool) = task {
    let invalid =
        if String.IsNullOrWhiteSpace(windowRegistration) then Some "WindowNotRegistered"
        elif utf8.GetByteCount(source) > MaxScriptBytes then Some "InputTooLarge: script exceeds 64 KiB"
        elif utf8.GetByteCount(argsJson) > MaxArgumentsBytes then Some "InputTooLarge: arguments exceed 64 KiB"
        elif timeoutMs < 1 || timeoutMs > 30000 then Some "InvalidTimeout"
        else
            try
                use doc = JsonDocument.Parse(argsJson)
                if doc.RootElement.ValueKind <> JsonValueKind.Object then Some "InvalidArgumentsJson"
                else None
            with :? JsonException -> Some "InvalidArgumentsJson"
    match invalid with
    | Some reason -> return Error("", reason)
    | None ->
        let mutable registrationId = ""
        try
            let! opened = bridge.OpenTab(windowRegistration, url, alias, makeActive)
            match opened with
            | Error e -> return Error("", e)
            | Ok response ->
                use openDoc = JsonDocument.Parse(response)
                registrationId <- openDoc.RootElement.GetProperty("tab").GetProperty("registrationId").GetString()
                if String.IsNullOrWhiteSpace(registrationId) then
                    return Error("", "OutcomeUnknown: created tab response lacked registration ID")
                else
                    let mutable ready = false
                    let mutable attempts = 0
                    while not ready && attempts < 60 do
                        match bridge.ListTabs() with
                        | Ok listing ->
                            use listDoc = JsonDocument.Parse(listing)
                            ready <- listDoc.RootElement.GetProperty("tabs").EnumerateArray()
                                     |> Seq.exists (fun tab ->
                                         tab.GetProperty("registrationId").GetString() = registrationId &&
                                         tab.GetProperty("availability").GetString() = "ready")
                        | Error _ -> ()
                        if not ready then
                            attempts <- attempts + 1
                            do! Task.Delay(500)
                    if not ready then
                        // No page script was dispatched; cleanup cannot interrupt a submission.
                        if closeAfterExecution then
                            let! _ = bridge.CloseTab(registrationId)
                            ()
                        return Error(registrationId, "TabNotReady")
                    else
                        let! script = bridge.Execute(registrationId, source, argsJson, timeoutMs, false)
                        match script with
                        | Error e -> return Error(registrationId, "OutcomeUnknown: " + e)
                        | Ok result ->
                            use doc = JsonDocument.Parse(result)
                            let payload = doc.RootElement.GetProperty("result")
                            let status =
                                match payload.ValueKind with
                                | JsonValueKind.Object ->
                                    match payload.TryGetProperty("status") with
                                    | true, x when x.ValueKind = JsonValueKind.String -> x.GetString()
                                    | _ -> "completed"
                                | _ -> "completed"
                            if status = "outcomeUnknown" then
                                return Error(registrationId, "OutcomeUnknown: page submission was not confirmed")
                            elif status = "uiIncompatible" then
                                // Provider scripts supply a bounded, non-sensitive stage and
                                // reason. Preserve the created tab for manual DOM inspection.
                                let safeField (name: string) =
                                    match payload.TryGetProperty(name) with
                                    | true, field when field.ValueKind = JsonValueKind.String ->
                                        field.GetString()
                                        |> Seq.filter (fun c -> Char.IsLetterOrDigit(c) || c = '-' || c = '_')
                                        |> Seq.truncate 64
                                        |> Seq.toArray
                                        |> String
                                    | _ -> "unknown"
                                // Keep generic diagnostics numeric and bounded: no DOM,
                                // draft text, HTML, or arbitrary provider strings.
                                let diagnostics =
                                    match payload.TryGetProperty("diagnostics") with
                                    | true, data when data.ValueKind = JsonValueKind.Object ->
                                        data.EnumerateObject()
                                        |> Seq.truncate 12
                                        |> Seq.choose (fun pair ->
                                            let key = pair.Name
                                            if key.Length > 32 ||
                                               not (key |> Seq.forall (fun c -> Char.IsLetterOrDigit(c) || c = '_')) then None
                                            else
                                                match pair.Value.ValueKind with
                                                | JsonValueKind.Number ->
                                                    match pair.Value.TryGetInt32() with
                                                    | true, n when n >= 0 && n <= 10000 ->
                                                        Some(key + "=" + string n)
                                                    | _ -> None
                                                | JsonValueKind.True -> Some(key + "=true")
                                                | JsonValueKind.False -> Some(key + "=false")
                                                | JsonValueKind.String when key = "documentReadyState" ->
                                                    match pair.Value.GetString() with
                                                    | "loading" | "interactive" | "complete" as value ->
                                                        Some(key + "=" + value)
                                                    | _ -> None
                                                | _ -> None)
                                        |> String.concat ","
                                    | _ -> ""
                                let summary = "UiIncompatible:" + safeField "stage" + ":" + safeField "reason"
                                let details = if diagnostics = "" then summary else summary + ";" + diagnostics
                                return Error(registrationId, details)
                            else
                                let mutable cleanup = "retained"
                                if closeAfterExecution then
                                    let! closed = bridge.CloseTab(registrationId)
                                    cleanup <- match closed with
                                               | Ok _ -> "closed"
                                               | Error e -> "cleanupFailed:" + (if e.Length > 100 then e.Substring(0, 100) else e)
                                return Ok(JsonSerializer.Serialize(
                                    {| registrationId = registrationId
                                       status = status
                                       scriptResult = payload.Clone()
                                       cleanup = cleanup |}))
        with e ->
            return Error(registrationId, "OutcomeUnknown: " + e.GetType().Name)
}
