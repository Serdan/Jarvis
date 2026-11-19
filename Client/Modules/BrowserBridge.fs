module Client.BrowserBridge

open System
open System.Collections.Concurrent
open System.Diagnostics
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
      mutable Windows: JsonElement list
      mutable Features: Set<string> }

type Service(port: int, pairingSecret: string) =
    let listener = new HttpListener()
    let cancellation = new CancellationTokenSource()
    let sync = obj()
    // Each authenticated extension has independent registrations and requests.
    // A newly connected browser profile must not evict an existing one.
    let connections = ResizeArray<Connection>()
    let mutable acceptLoop: Task = Task.CompletedTask

    // Call under sync; explicit connection IDs pin multi-step operations.
    let selectConnections (ownerConnectionId: string option) =
        match ownerConnectionId with
        | Some id ->
            match connections |> Seq.tryFind (fun conn -> conn.Id = id) with
            | Some conn -> Ok [conn]
            | None -> Error "ExtensionDisconnected"
        | None when connections.Count = 0 -> Error "ExtensionDisconnected"
        | None -> Ok (connections |> Seq.toList)

    let matchesRegistration (registration: JsonElement) (primary: string) (value: string) =
        not (String.IsNullOrWhiteSpace value) &&
        ((match registration.TryGetProperty(primary) with
          | true, id when id.ValueKind = JsonValueKind.String -> id.GetString() = value
          | _ -> false) ||
         (match registration.TryGetProperty("alias") with
          | true, alias when alias.ValueKind = JsonValueKind.String -> alias.GetString() = value
          | _ -> false))

    // Resolve across all connections unless an earlier operation provided the
    // authenticated connection which created the tab. Collision is an error.
    let resolveRegistration ownerConnectionId isWindow value =
        match selectConnections ownerConnectionId with
        | Error error -> Error error
        | Ok live ->
            let matches =
                live |> List.collect (fun conn ->
                    (if isWindow then conn.Windows else conn.Registrations)
                    |> List.choose (fun registration ->
                        if matchesRegistration registration
                             (if isWindow then "windowRegistration" else "registrationId") value then
                            Some(conn, registration)
                        else None))
            match matches with
            | [] -> Error (if isWindow then "WindowNotRegistered" else "TabNotRegistered")
            | [single] -> Ok single
            | _ -> Error "AliasAmbiguous"

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
                  Registrations = []; Windows = []; Features = Set.empty }
            try
                try
                    do! authenticated conn
                    lock sync (fun () -> connections.Add conn)
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
                        | "capabilities" ->
                            match doc["features"] with
                            | :? JsonArray as features when features.Count <= 32 ->
                                let parsed = features |> Seq.choose (fun value ->
                                    try
                                        let feature = value.GetValue<string>()
                                        if feature.Length <= 64 then Some feature else None
                                    with _ -> None) |> Set.ofSeq
                                lock sync (fun () -> conn.Features <- parsed)
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
                        | "result" | "error" | "tabOpened" | "tabClosed" | "tabCloseDeferred" | "snapshotReady" | "snapshotChunk" ->
                            let requestId = asString doc "requestId"
                            let mutable tcs = Unchecked.defaultof<TaskCompletionSource<string>>
                            if conn.Pending.TryRemove(requestId, &tcs) then
                                tcs.TrySetResult(payload) |> ignore
                        | "pong" -> ()
                        | _ -> ()
                with _ -> ()
            finally
                lock sync (fun () -> connections.Remove conn |> ignore)
                for KeyValue(_,tcs) in conn.Pending do
                    tcs.TrySetException(IOException("Browser bridge disconnected; outcome unknown.")) |> ignore
                conn.SendLock.Dispose()
                try socket.Abort() with _ -> ()
    }

    let requestSnapshotFrame (conn: Connection) (requestId: string) (frame: string)
                             (timeoutMs: int) (timeoutError: string) = task {
        if conn.Pending.Count >= 4 then return Error "BridgeBusy"
        else
            let pending = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            if not (conn.Pending.TryAdd(requestId, pending)) then return Error "DuplicateRequest"
            else
                try
                    try
                        do! send conn frame
                        let! response = pending.Task.WaitAsync(TimeSpan.FromMilliseconds(float timeoutMs))
                        return Ok response
                    with
                    | :? TimeoutException -> return Error timeoutError
                    | ex -> return Error("SnapshotTransportFailure:" + ex.GetType().Name)
                finally
                    let mutable removed = Unchecked.defaultof<TaskCompletionSource<string>>
                    conn.Pending.TryRemove(requestId, &removed) |> ignore
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

    member _.ListTabs(?ownerConnectionId: string) =
        lock sync (fun () ->
            match selectConnections ownerConnectionId with
            | Error error -> Error error
            | Ok live ->
                let tabs = live |> List.collect (fun conn -> conn.Registrations |> List.map (fun x -> x.Clone()))
                Ok (serialize {| tabs = tabs |}))

    member _.ListWindows() =
        lock sync (fun () ->
            match selectConnections None with
            | Error error -> Error error
            | Ok live ->
                let windows = live |> List.collect (fun conn -> conn.Windows |> List.map (fun x -> x.Clone()))
                Ok (serialize {| windows = windows |}))

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
                match resolveRegistration None true windowRegistration with
                | Error error -> Error error
                | Ok (conn, window) ->
                    let availability =
                        match window.TryGetProperty("availability") with
                        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                        | _ -> "unknown"
                    let selectedId =
                        match window.TryGetProperty("windowRegistration") with
                        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                        | _ -> ""
                    let selectedGeneration =
                        match window.TryGetProperty("registrationGeneration") with
                        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
                        | _ -> 0
                    if availability <> "ready" then Error ("WindowUnavailable:" + availability)
                    elif String.IsNullOrWhiteSpace(selectedId) || selectedGeneration < 1 then Error "WindowRegistrationInvalid"
                    elif conn.Pending.Count >= 4 then Error "BridgeBusy"
                    else Ok(conn, selectedId, selectedGeneration))
            match connection with
            | Error error -> return Error error
            | Ok(conn, selectedWindowId, selectedGeneration) ->
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
                                               windowRegistration = selectedWindowId
                                               registrationGeneration = selectedGeneration
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

    member _.CloseTab(tabRegistration: string, ?ownerConnectionId: string,
                      ?expectedRegistrationGeneration: int,
                      ?requireFreshHomeNoTurns: bool,
                      ?deferredPromptSha256: string) = task {
        let resolved = lock sync (fun () ->
            match resolveRegistration ownerConnectionId false tabRegistration with
            | Error error -> Error error
            | Ok (conn, tab) ->
                let agentCreated =
                    match tab.TryGetProperty("agentCreated") with
                    | true, value -> value.GetBoolean()
                    | _ -> false
                let generation =
                    match tab.TryGetProperty("registrationGeneration") with
                    | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
                    | _ -> 0
                if expectedRegistrationGeneration |> Option.exists (fun expected -> expected <> generation) then
                    Error "TabRegistrationChanged"
                elif not agentCreated then Error "TabNotAgentCreated"
                elif conn.Pending.Count >= 4 then Error "BridgeBusy"
                else Ok(conn, tab))
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
                               registrationId = registrationId; registrationGeneration = generation
                               requireFreshHomeNoTurns = defaultArg requireFreshHomeNoTurns false
                               deferredPromptSha256 = defaultArg deferredPromptSha256 "" |})
                        let! response = completion.Task.WaitAsync(TimeSpan.FromSeconds(15.0))
                        let root = JsonNode.Parse(response)
                        if asString root "connectionId" <> conn.Id ||
                           asString root "registrationId" <> registrationId ||
                           asInt root "registrationGeneration" <> generation then
                            return Error "ResponseIdentityMismatch"
                        elif asString root "kind" = "error" then
                            return Error ("BrowserCloseTabError:" + asString root "error")
                        elif deferredPromptSha256.IsSome && asString root "kind" = "tabCloseDeferred" then
                            return Ok """{"deferred":true}"""
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

    member _.Execute(tabRegistration: string, source: string, argsJson: string, timeoutMs: int,
                     isInline: bool, ?ownerConnectionId: string,
                     ?expectedRegistrationGeneration: int, ?requireAgentCreated: bool) = task {
        let sourceBytes = utf8.GetByteCount(source)
        if sourceBytes > MaxScriptBytes then return Error "InputTooLarge: script exceeds 64 KiB."
        elif utf8.GetByteCount(argsJson) > MaxArgumentsBytes then return Error "InputTooLarge: arguments exceed 64 KiB."
        elif timeoutMs < 1 || timeoutMs > 30000 then return Error "InvalidTimeout"
        else
            try
                use arguments = JsonDocument.Parse(argsJson)
                let resolved = lock sync (fun () ->
                    match resolveRegistration ownerConnectionId false tabRegistration with
                    | Error error -> Error error
                    | Ok (conn, tab) ->
                        let state =
                            match tab.TryGetProperty("availability") with
                            | true, x -> x.GetString()
                            | _ -> ""
                        let generation =
                            match tab.TryGetProperty("registrationGeneration") with
                            | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
                            | _ -> 0
                        let agentCreated =
                            match tab.TryGetProperty("agentCreated") with
                            | true, value when value.ValueKind = JsonValueKind.True -> true
                            | _ -> false
                        if expectedRegistrationGeneration |> Option.exists (fun expected -> expected <> generation) then
                            Error "TabRegistrationChanged"
                        elif defaultArg requireAgentCreated false && not agentCreated then Error "TabNotAgentCreated"
                        elif state <> "ready" then Error ("TabUnavailable:" + state)
                        elif conn.Pending.Count >= 4 then Error "BridgeBusy"
                        else Ok(conn, tab))
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
                                                   requireAgentCreated = defaultArg requireAgentCreated false
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


    // DOM snapshots are read-only browser operations. The extension captures the
    // complete DOM once; oversized results are transferred from that frozen copy.
    member _.CaptureDom(tabRegistration: string, waitJson: string, captureSelector: string,
                        timeoutMs: int, outputPath: string option) = task {
        if String.IsNullOrWhiteSpace(tabRegistration) then return Error "TabNotRegistered"
        elif isNull waitJson || utf8.GetByteCount(waitJson) > 4096 then return Error "InvalidSnapshotOptions"
        elif isNull captureSelector || captureSelector.Length > 512 then return Error "InvalidSnapshotSelector"
        elif timeoutMs < 100 || timeoutMs > 60000 then return Error "InvalidSnapshotTimeout"
        elif outputPath |> Option.exists File.Exists then return Error "SnapshotOutputExists"
        elif outputPath |> Option.exists (fun path -> not (Directory.Exists(Path.GetDirectoryName(path)))) then
            return Error "SnapshotOutputDirectoryMissing"
        else
            // One monotonic deadline covers capture, chunk transfer, validation and
            // publication. No chain of per-chunk timeouts can outlive the caller.
            let started = Stopwatch.GetTimestamp()
            let remainingMs () =
                let left = float timeoutMs - Stopwatch.GetElapsedTime(started).TotalMilliseconds
                if left <= 0. then 0 else max 1 (int (Math.Ceiling(left)))
            try
                use waitDoc = JsonDocument.Parse(waitJson)
                if waitDoc.RootElement.ValueKind <> JsonValueKind.Object then
                    return Error "InvalidSnapshotOptions"
                else
                    let selected = lock sync (fun () ->
                        match resolveRegistration None false tabRegistration with
                        | Error error -> Error error
                        | Ok (conn, tab) ->
                            let status =
                                match tab.TryGetProperty("availability") with
                                | true, value -> value.GetString()
                                | _ -> "unknown"
                            if status <> "ready" then Error("TabUnavailable:" + status)
                            elif not (conn.Features.Contains("domSnapshotV1")) then
                                Error "ExtensionOutdated: DOM snapshots require a newer browser extension"
                            else Ok(conn, tab))
                    match selected with
                    | Error error -> return Error error
                    | Ok(conn, tab) ->
                        let registrationId = tab.GetProperty("registrationId").GetString()
                        let generation = tab.GetProperty("registrationGeneration").GetInt32()
                        let requestId = Guid.NewGuid().ToString("N")
                        let mode = if outputPath.IsSome then "file" else "inline"
                        let frame = serialize {| version = ProtocolVersion; kind = "captureDom";
                               connectionId = conn.Id; requestId = requestId;
                               registrationId = registrationId; registrationGeneration = generation;
                               timeoutMs = timeoutMs; mode = mode;
                               wait = waitDoc.RootElement.Clone(); captureSelector = captureSelector |}
                        // The capture wait consumes the caller's remaining deadline;
                        // its timer is not an independent transport timeout.
                        let! received =
                            let remaining = remainingMs()
                            if remaining <= 0 then Task.FromResult(Error "SnapshotDeadlineExceeded")
                            else requestSnapshotFrame conn requestId frame remaining "SnapshotDeadlineExceeded"
                        match received with
                        | Error e when remainingMs() <= 0 -> return Error "SnapshotDeadlineExceeded"
                        | Error e -> return Error e
                        | Ok raw when remainingMs() <= 0 -> return Error "SnapshotDeadlineExceeded"
                        | Ok raw ->
                            use doc = JsonDocument.Parse(raw)
                            let reply = doc.RootElement
                            let getString (node: JsonElement) (name: string) =
                                match node.TryGetProperty(name) with
                                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                                | _ -> ""
                            if getString reply "connectionId" <> conn.Id ||
                               getString reply "registrationId" <> registrationId ||
                               reply.GetProperty("registrationGeneration").GetInt32() <> generation then
                                return Error "SnapshotResponseIdentityMismatch"
                            elif getString reply "kind" = "error" then
                                return Error("BrowserSnapshotError:" + getString reply "error")
                            elif getString reply "kind" <> "snapshotReady" then
                                return Error "UnexpectedSnapshotResponse"
                            else
                                let bytes = reply.GetProperty("bytes").GetInt32()
                                if bytes < 0 || bytes > 8 * 1024 * 1024 then
                                    return Error "SnapshotTooLarge"
                                else
                                    let title = getString reply "title"
                                    let url = getString reply "url"
                                    let capturedAtUtc = getString reply "capturedAtUtc"
                                    let waitKind = getString reply "waitKind"
                                    match outputPath with
                                    | None ->
                                        let html = getString reply "html"
                                        if utf8.GetByteCount(html) <> bytes then
                                            return Error "SnapshotSizeMismatch"
                                        else
                                            return Ok(serialize
                                                {| status = "completed"; registrationId = registrationId
                                                   title = title; url = url; capturedAtUtc = capturedAtUtc
                                                   waitKind = waitKind; bytes = bytes; html = html |})
                                    | Some destination ->
                                        let snapshotId = getString reply "snapshotId"
                                        let expectedHash = getString reply "sha256"
                                        if snapshotId.Length <> 32 || expectedHash.Length <> 64 then
                                            return Error "InvalidSnapshotTransfer"
                                        elif File.Exists(destination) then
                                            return Error "SnapshotOutputExists"
                                        elif not (Directory.Exists(Path.GetDirectoryName(destination))) then
                                            return Error "SnapshotOutputDirectoryMissing"
                                        else
                                            let temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp"
                                            let mutable error: string option = None
                                            try
                                                use file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                                                use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                                                let mutable offset = 0
                                                while offset < bytes && error.IsNone do
                                                    let chunkRequestId = Guid.NewGuid().ToString("N")
                                                    let chunkFrame = serialize {| version = ProtocolVersion; kind = "snapshotChunk";
                                                           requestId = chunkRequestId; connectionId = conn.Id;
                                                           snapshotId = snapshotId; offset = offset |}
                                                    let remaining = remainingMs()
                                                    let! chunkResult =
                                                        if remaining <= 0 then Task.FromResult(Error "SnapshotDeadlineExceeded")
                                                        else
                                                            let deadlineBounded = remaining <= 10000
                                                            requestSnapshotFrame conn chunkRequestId chunkFrame
                                                                (min remaining 10000)
                                                                (if deadlineBounded then "SnapshotDeadlineExceeded"
                                                                 else "SnapshotTransportTimeout")
                                                    match chunkResult with
                                                    | Error _ when remainingMs() <= 0 -> error <- Some "SnapshotDeadlineExceeded"
                                                    | Error e -> error <- Some e
                                                    | Ok _ when remainingMs() <= 0 -> error <- Some "SnapshotDeadlineExceeded"
                                                    | Ok chunkRaw ->
                                                        try
                                                            use chunkDoc = JsonDocument.Parse(chunkRaw)
                                                            let chunk = chunkDoc.RootElement
                                                            if getString chunk "kind" = "error" then
                                                                error <- Some("BrowserSnapshotError:" + getString chunk "error")
                                                            elif getString chunk "connectionId" <> conn.Id ||
                                                                 getString chunk "kind" <> "snapshotChunk" ||
                                                                   getString chunk "snapshotId" <> snapshotId ||
                                                                   getString chunk "registrationId" <> registrationId ||
                                                                   chunk.GetProperty("registrationGeneration").GetInt32() <> generation ||
                                                                   chunk.GetProperty("offset").GetInt32() <> offset then
                                                                error <- Some "SnapshotChunkIdentityMismatch"
                                                            else
                                                                let data = Convert.FromBase64String(getString chunk "data")
                                                                if data.Length = 0 || data.Length > 32768 ||
                                                                   offset + data.Length > bytes then
                                                                    error <- Some "SnapshotChunkInvalid"
                                                                else
                                                                    file.Write(data)
                                                                    hash.AppendData(data)
                                                                    offset <- offset + data.Length
                                                        with _ -> error <- Some "SnapshotChunkInvalid"
                                                if error.IsNone then
                                                    if remainingMs() <= 0 then
                                                        error <- Some "SnapshotDeadlineExceeded"
                                                    else
                                                        let receivedHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
                                                        if receivedHash <> expectedHash then
                                                            error <- Some "SnapshotChecksumMismatch"
                                                        elif remainingMs() <= 0 then
                                                            error <- Some "SnapshotDeadlineExceeded"
                                                        else
                                                            file.Flush(true)
                                                            if remainingMs() <= 0 then
                                                                error <- Some "SnapshotDeadlineExceeded"
                                                            else
                                                                file.Dispose()
                                                                // Final gate: never publish a snapshot after expiry.
                                                                if remainingMs() <= 0 then
                                                                    error <- Some "SnapshotDeadlineExceeded"
                                                                else
                                                                    File.Move(temp, destination)
                                            with
                                            | :? IOException -> error <- Some "SnapshotOutputWriteFailed"
                                            | ex -> error <- Some("SnapshotTransferFailure:" + ex.GetType().Name)
                                            if File.Exists(temp) then
                                                try File.Delete(temp) with _ -> ()
                                            try
                                                do! send conn (serialize
                                                    {| version = ProtocolVersion; kind = "releaseSnapshot"
                                                       connectionId = conn.Id; snapshotId = snapshotId |})
                                            with _ -> ()
                                            match error with
                                            | Some e -> return Error e
                                            | None ->
                                                return Ok(serialize
                                                    {| status = "completed"; registrationId = registrationId
                                                       title = title; url = url; capturedAtUtc = capturedAtUtc
                                                       waitKind = waitKind; bytes = bytes
                                                       sha256 = expectedHash; exported = true |})
            with
            | :? JsonException -> return Error "InvalidSnapshotOptions"
            | ex -> return Error("SnapshotFailure:" + ex.GetType().Name)
    }

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            listener.Close()
            lock sync (fun () ->
                for conn in connections do conn.Socket.Abort()
                connections.Clear())
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
                let createdTab = openDoc.RootElement.GetProperty("tab")
                registrationId <- createdTab.GetProperty("registrationId").GetString()
                let ownerConnectionId = openDoc.RootElement.GetProperty("connectionId").GetString()
                let createdGeneration =
                    match createdTab.TryGetProperty("registrationGeneration") with
                    | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
                    | _ -> 0
                let agentCreated =
                    match createdTab.TryGetProperty("agentCreated") with
                    | true, value when value.ValueKind = JsonValueKind.True -> true
                    | _ -> false
                if String.IsNullOrWhiteSpace(registrationId) || createdGeneration < 1 || not agentCreated then
                    return Error(registrationId, "OutcomeUnknown: created tab identity or provenance was missing")
                else
                    // Definitive pre-submit failure owns a disposable tab even
                    // when the caller opted to retain successful conversations.
                    // Close only the pinned agent-created registration.
                    let closeDefinitive () = task {
                        match! bridge.CloseTab(registrationId, ownerConnectionId = ownerConnectionId,
                                               expectedRegistrationGeneration = createdGeneration,
                                               requireFreshHomeNoTurns = true) with
                        | Ok _ -> return Ok "closed"
                        // The extension rechecks fresh-home/zero-turn at disposal.
                        // The earlier page result is not an enduring close authority.
                        | Error "BrowserCloseTabError:CleanupSafetyChanged" ->
                            return Ok "retained"
                        // A missing/inconclusive safety probe is not an observed changed route.
                        // Never close/retry, but surface the error with pinned registration.
                        | Error e ->
                            let safe = if e.Length > 100 then e.Substring(0, 100) else e
                            return Error("cleanupFailed:" + safe)
                    }
                    // Unknown execution is not permission to resubmit or to close.
                    // Arm an expiring, extension-verified check for the exact user
                    // turn and durable conversation before any deferred removal.
                    let deferConfirmedCleanup () = task {
                        if not closeAfterExecution || url <> "https://chatgpt.com/" then
                            return "retained"
                        else
                            use args = JsonDocument.Parse(argsJson)
                            match args.RootElement.TryGetProperty("prompt") with
                            | true, field when field.ValueKind = JsonValueKind.String ->
                                let prompt = field.GetString()
                                if String.IsNullOrWhiteSpace(prompt) || prompt.Length > 24000 then
                                    return "retained"
                                else
                                    let normalized = prompt.Replace("\r\n", "\n").Replace("\u00a0", " ").Trim()
                                    let hash = Convert.ToHexString(SHA256.HashData(utf8.GetBytes(normalized))).ToLowerInvariant()
                                    match! bridge.CloseTab(registrationId, ownerConnectionId = ownerConnectionId,
                                                           expectedRegistrationGeneration = createdGeneration,
                                                           deferredPromptSha256 = hash) with
                                    | Ok _ -> return "deferred"
                                    | Error _ -> return "retained"
                            | _ -> return "retained"
                    }
                    // Readiness advertised by the extension is not atomic with
                    // Chrome's live tab status. A page can start another load
                    // between ListTabs and the extension's execute preflight.
                    let readinessStarted = Stopwatch.GetTimestamp()
                    let withinReadinessBudget () =
                        Stopwatch.GetElapsedTime(readinessStarted) < TimeSpan.FromSeconds(30.0)
                    let cleanupNotReady () = task {
                        // No user script was injected. The post-success preference
                        // must not retain a definitive failed launch.
                        match! closeDefinitive() with
                        | Ok cleanup -> return Error(registrationId, "TabNotReady; cleanup=" + cleanup)
                        | Error failure -> return Error(registrationId, "TabNotReady; " + failure)
                    }
                    let mutable ready = false
                    let mutable ownershipChanged = false
                    while not ready && not ownershipChanged && withinReadinessBudget() do
                        match bridge.ListTabs(ownerConnectionId = ownerConnectionId) with
                        | Ok listing ->
                            use listDoc = JsonDocument.Parse(listing)
                            let matching = listDoc.RootElement.GetProperty("tabs").EnumerateArray()
                                           |> Seq.tryFind (fun tab ->
                                               tab.GetProperty("registrationId").GetString() = registrationId)
                            match matching with
                            | Some tab ->
                                let generation =
                                    match tab.TryGetProperty("registrationGeneration") with
                                    | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
                                    | _ -> 0
                                let created =
                                    match tab.TryGetProperty("agentCreated") with
                                    | true, value when value.ValueKind = JsonValueKind.True -> true
                                    | _ -> false
                                ownershipChanged <- generation <> createdGeneration || not created
                                ready <- not ownershipChanged && tab.GetProperty("availability").GetString() = "ready"
                            | None -> ()
                        | Error _ -> ()
                        if not ready && not ownershipChanged then do! Task.Delay(500)
                    if ownershipChanged then
                        return Error(registrationId, "TabRegistrationChanged; cleanup=retained")
                    elif not ready then
                        return! cleanupNotReady()
                    else
                        // These two exact errors are emitted only BEFORE script
                        // injection (Service.Execute's local preflight or the
                        // extension's assess() preflight). They are safe to retry.
                        // All post-dispatch errors remain uncertain: NEVER replay.
                        let isLoadingPreflight = function
                            | "TabUnavailable:loading"
                            | "BrowserExecutionError:TabUnavailable:loading" -> true
                            | _ -> false
                        let mutable execution : Result<string, string> option = None
                        while execution.IsNone && withinReadinessBudget() do
                            let! attempt = bridge.Execute(registrationId, source, argsJson, timeoutMs, false,
                                                          ownerConnectionId = ownerConnectionId,
                                                          expectedRegistrationGeneration = createdGeneration,
                                                          requireAgentCreated = true)
                            match attempt with
                            | Error e when isLoadingPreflight e -> do! Task.Delay(500)
                            | _ -> execution <- Some attempt
                        let script = defaultArg execution (Error "TabUnavailable:loading")
                        match script with
                        | Error e when isLoadingPreflight e -> return! cleanupNotReady()
                        | Error e ->
                            let! cleanup = deferConfirmedCleanup()
                            return Error(registrationId, "OutcomeUnknown: " + e + "; cleanup=" + cleanup)
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
                            // Never equate "script did not click Send" with "no
                            // conversation exists". Only explicitly asserted
                            // fresh-home/zero-turn evidence permits disposal.
                            let disposable =
                                match payload.ValueKind with
                                | JsonValueKind.Object ->
                                    // Contradictory evidence must fail closed even
                                    // if a script accidentally asserts disposal.
                                    let conversationObserved =
                                        match payload.TryGetProperty("reason") with
                                        | true, reason when reason.ValueKind = JsonValueKind.String ->
                                            reason.GetString() = "ExistingConversation" ||
                                            reason.GetString() = "UnexpectedStartUrl"
                                        | _ -> false
                                    match payload.TryGetProperty("safeToDisposeCreatedTab") with
                                    | true, proof -> proof.ValueKind = JsonValueKind.True && not conversationObserved
                                    | _ -> false
                                | _ -> false
                            let disposeFailedLaunch () =
                                if disposable then closeDefinitive()
                                else task { return Ok "retained" }
                            if status = "outcomeUnknown" then
                                let! cleanup = deferConfirmedCleanup()
                                return Error(registrationId, "OutcomeUnknown: page submission was not confirmed; cleanup=" + cleanup)
                            elif status = "uiIncompatible" then
                                // Provider scripts supply a bounded, non-sensitive stage and
                                // reason; cleanup does not discard these diagnostics.
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
                                match! disposeFailedLaunch() with
                                | Ok cleanup -> return Error(registrationId, details + ";cleanup=" + cleanup)
                                | Error failure -> return Error(registrationId, details + ";" + failure)
                            elif status = "notSubmitted" then
                                // An unsent draft is disposable, but a new turn or
                                // route is not, even if this script did not send.
                                match! disposeFailedLaunch() with
                                | Error failure -> return Error(registrationId, "NotSubmitted;" + failure)
                                | Ok cleanup ->
                                    return Ok(JsonSerializer.Serialize(
                                        {| registrationId = registrationId
                                           status = status
                                           scriptResult = payload.Clone()
                                           cleanup = cleanup |}))
                            else
                                let mutable cleanup = "retained"
                                if closeAfterExecution then
                                    let! closed = bridge.CloseTab(registrationId, ownerConnectionId = ownerConnectionId,
                                                                  expectedRegistrationGeneration = createdGeneration)
                                    cleanup <- match closed with
                                               | Ok _ -> "closed"
                                               | Error e -> "cleanupFailed:" + (if e.Length > 100 then e.Substring(0, 100) else e)
                                    if cleanup.StartsWith("cleanupFailed:", StringComparison.Ordinal) && status = "submitted" then
                                        let! deferred = deferConfirmedCleanup()
                                        if deferred = "deferred" then cleanup <- cleanup + "; deferred=armed"
                                return Ok(JsonSerializer.Serialize(
                                    {| registrationId = registrationId
                                       status = status
                                       scriptResult = payload.Clone()
                                       cleanup = cleanup |}))
        with e ->
            return Error(registrationId, "OutcomeUnknown: " + e.GetType().Name)
}
