module Client.RoslynLsp

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

// Each checkout owns its LSP client session. --daemon-mode allows the official
// server to share its backend without sharing document or project state.
type FormatResult =
    { Content: string
      EditCount: int
      Diagnostics: string list }

type private TextEdit =
    { Start: int
      End: int
      Replacement: string }

let private property (key: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(key, &value) then
        Some value
    else None

let private stringProperty name element =
    property name element
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.String then Option.ofObj(value.GetString())
        else None)

let private positionOffset (content: string) (position: JsonElement) =
    let line = position.GetProperty("line").GetInt32()
    let character = position.GetProperty("character").GetInt32()
    if line < 0 || character < 0 then invalidArg "position" "Negative LSP position."
    let mutable index = 0
    let mutable currentLine = 0
    while currentLine < line && index < content.Length do
        let next = content.IndexOf('\n', index)
        if next < 0 then invalidArg "position" "LSP edit line is outside the document."
        index <- next + 1
        currentLine <- currentLine + 1
    if currentLine <> line then invalidArg "position" "LSP edit line is outside the document."
    let lineEnd = content.IndexOf('\n', index)
    let lineEnd = if lineEnd < 0 then content.Length else lineEnd
    let visibleEnd =
        if lineEnd > index && content[lineEnd - 1] = '\r' then lineEnd - 1 else lineEnd
    if character > visibleEnd - index then
        invalidArg "position" "LSP edit character is outside the document."
    index + character

// UTF-16 offsets are correct for LSP's default position encoding and .NET
// strings. Reject overlapping or invalid edits before constructing any result.
let applyTextEdits (content: string) (edits: JsonElement) : Result<string * int, string> =
    try
        if edits.ValueKind = JsonValueKind.Null then Ok(content, 0)
        elif edits.ValueKind <> JsonValueKind.Array then Error "Roslyn returned non-array formatting edits."
        else
            let parsed =
                edits.EnumerateArray()
                |> Seq.map (fun edit ->
                    let range = edit.GetProperty("range")
                    let start = positionOffset content (range.GetProperty("start"))
                    let finish = positionOffset content (range.GetProperty("end"))
                    if finish < start then invalidArg "edits" "LSP edit end precedes start."
                    { Start = start
                      End = finish
                      Replacement = edit.GetProperty("newText").GetString() })
                |> Seq.sortBy (fun edit -> edit.Start, edit.End)
                |> Seq.toArray
            for i in 1 .. parsed.Length - 1 do
                if parsed[i].Start < parsed[i - 1].End
                   || (parsed[i].Start = parsed[i - 1].Start && parsed[i].End = parsed[i - 1].End) then
                    invalidArg "edits" "Overlapping LSP formatting edits."
            let mutable updated = content
            for edit in Array.rev parsed do
                updated <- updated.Substring(0, edit.Start) + edit.Replacement + updated.Substring(edit.End)
            Ok(updated, parsed.Length)
    with ex -> Error("Invalid Roslyn formatting response: " + ex.Message)

let private makeFrame (json: string) =
    let payload = Encoding.UTF8.GetBytes json
    let header = Encoding.ASCII.GetBytes(sprintf "Content-Length: %d\r\n\r\n" payload.Length)
    header, payload

let private readFrame (stream: Stream) =
    let header = ResizeArray<byte>()
    let mutable complete = false
    while not complete do
        let b = stream.ReadByte()
        if b < 0 then raise (EndOfStreamException "Roslyn closed its stdout stream.")
        header.Add(byte b)
        if header.Count > 8192 then raise (InvalidDataException "LSP header too long.")
        let n = header.Count
        complete <-
            n >= 4 &&
            header[n-4] = 13uy && header[n-3] = 10uy &&
            header[n-2] = 13uy && header[n-1] = 10uy
    let lines =
        Encoding.ASCII.GetString(header.ToArray()).Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)
    let length =
        lines
        |> Array.tryPick (fun line ->
            let prefix = "Content-Length:"
            if line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                match Int32.TryParse(line.Substring(prefix.Length).Trim()) with
                | true, value -> Some value
                | _ -> None
            else None)
        |> Option.defaultWith (fun () -> raise (InvalidDataException "LSP frame missing Content-Length."))
    if length < 0 || length > 8 * 1024 * 1024 then
        raise (InvalidDataException "LSP payload exceeds the bounded response size.")
    let body = Array.zeroCreate<byte> length
    let mutable offset = 0
    while offset < body.Length do
        let read = stream.Read(body, offset, body.Length - offset)
        if read <= 0 then raise (EndOfStreamException "Truncated LSP response.")
        offset <- offset + read
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(body))
    document.RootElement.Clone()

let private roslynExecutable () =
    // Explicit operator-owned tool selection supports hermetic runners whose
    // HOME is isolated from the globally installed dotnet tool store. The
    // repository hook config cannot set or control this executable.
    let overrideDll = Environment.GetEnvironmentVariable("JARVIS_ROSLYN_LSP_DLL")
    let approvedOverride =
        not (String.IsNullOrWhiteSpace overrideDll)
        && Path.IsPathFullyQualified overrideDll
        && String.Equals(Path.GetFileName overrideDll, "roslyn-language-server.dll",
                         StringComparison.OrdinalIgnoreCase)
        && File.Exists overrideDll
    // The dotnet global-tool shim can point at the system's old .NET runtime
    // even when a newer SDK is selected through mise. Run the managed DLL
    // through the user's dotnet command when a global installation exists.
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    let store = Path.Combine(home, ".dotnet", "tools", ".store", "roslyn-language-server")
    let dll =
        if Directory.Exists store then
            Directory.EnumerateFiles(store, "roslyn-language-server.dll", SearchOption.AllDirectories)
            |> Seq.tryHead
        else None
    match if approvedOverride then Some overrideDll else dll with
    | Some path -> "dotnet", [ path ]
    | None -> "roslyn-language-server", []

type private Session(root: string) =
    let process = new Process()
    let writerGate = obj()
    let gate = new SemaphoreSlim(1, 1)
    let pending = ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>()
    let diagnostics = ConcurrentDictionary<string, string list>()
    let documents = Dictionary<string, int>(StringComparer.Ordinal)
    let mutable nextId = 0
    let mutable disposed = 0

    let send (message: obj) =
        lock writerGate (fun () ->
            if Volatile.Read(&disposed) <> 0 || process.HasExited then
                raise (IOException "Roslyn language server exited.")
            let json = JsonSerializer.Serialize(message, message.GetType())
            let header, payload = makeFrame json
            let stream = process.StandardInput.BaseStream
            stream.Write(header, 0, header.Length)
            stream.Write(payload, 0, payload.Length)
            stream.Flush())

    let notification methodName (parameters: obj) =
        send (box {| jsonrpc = "2.0"; method = methodName; ``params`` = parameters |})

    let request methodName (parameters: obj) (timeout: float) =
        let id = Interlocked.Increment(&nextId)
        let completion = TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)
        if not (pending.TryAdd(id, completion)) then
            raise (InvalidOperationException "Duplicate LSP request identifier.")
        try
            send (box {| jsonrpc = "2.0"; id = id; method = methodName; ``params`` = parameters |})
            if not (completion.Task.Wait(TimeSpan.FromSeconds timeout)) then
                notification "$/cancelRequest" (box {| id = id |})
                raise (TimeoutException(sprintf "Roslyn %s did not respond within %.0f seconds." methodName timeout))
            let response = completion.Task.GetAwaiter().GetResult()
            match property "error" response with
            | Some error when error.ValueKind <> JsonValueKind.Null ->
                raise (InvalidOperationException(sprintf "Roslyn %s error: %s" methodName (error.GetRawText())))
            | _ ->
                property "result" response
                |> Option.defaultWith (fun () -> raise (InvalidDataException "LSP response has no result."))
        finally
            let mutable removed = Unchecked.defaultof<TaskCompletionSource<JsonElement>>
            pending.TryRemove(id, &removed) |> ignore

    let reply id result =
        send (box {| jsonrpc = "2.0"; id = id; result = result |})

    let replyError id message =
        send (box {| jsonrpc = "2.0"; id = id; error = {| code = -32601; message = message |} |})

    let readLoop () =
        try
            while Volatile.Read(&disposed) = 0 && not process.HasExited do
                let message = readFrame process.StandardOutput.BaseStream
                match property "id" message, stringProperty "method" message with
                | Some id, Some methodName ->
                    match methodName with
                    | "workspace/configuration" ->
                        let count =
                            property "params" message
                            |> Option.bind (property "items")
                            |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
                            |> Option.map (fun value -> value.GetArrayLength())
                            |> Option.defaultValue 0
                        reply id (box (Array.create count null))
                    | "workspace/applyEdit" ->
                        reply id (box {| applied = false; failureReason = "Jarvis does not allow server-initiated edits." |})
                    | "client/registerCapability"
                    | "client/unregisterCapability"
                    | "window/workDoneProgress/create"
                    | "workspace/diagnostic/refresh"
                    | "workspace/codeLens/refresh"
                    | "workspace/semanticTokens/refresh" ->
                        reply id null
                    | _ -> replyError id ("Unsupported Roslyn request: " + methodName)
                | Some id, None when id.ValueKind = JsonValueKind.Number ->
                    let mutable completion = Unchecked.defaultof<TaskCompletionSource<JsonElement>>
                    if pending.TryRemove(id.GetInt32(), &completion) then
                        completion.TrySetResult(message) |> ignore
                | _, Some "textDocument/publishDiagnostics" ->
                    match property "params" message with
                    | Some parameters ->
                        match stringProperty "uri" parameters, property "diagnostics" parameters with
                        | Some uri, Some list when list.ValueKind = JsonValueKind.Array ->
                            let entries =
                                list.EnumerateArray()
                                |> Seq.truncate 30
                                |> Seq.choose (stringProperty "message")
                                |> Seq.toList
                            diagnostics[uri] <- entries
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
        with ex ->
            if Volatile.Read(&disposed) = 0 then
                for KeyValue(_, completion) in pending do
                    completion.TrySetException(IOException("Roslyn LSP connection ended: " + ex.Message, ex)) |> ignore

    do
        let command, prefix = roslynExecutable()
        let info = ProcessStartInfo()
        info.FileName <- command
        info.WorkingDirectory <- root
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        for arg in prefix @ [ "--stdio"; "--daemon-mode"; "--autoLoadProjects"; "--telemetryLevel"; "off" ] do
            info.ArgumentList.Add(arg)
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
        info.Environment["DOTNET_NOLOGO"] <- "1"
        process.StartInfo <- info
        if not (process.Start()) then raise (IOException "Failed to launch Roslyn language server.")
        // Never let a verbose Roslyn stderr pipe block its protocol writer.
        process.ErrorDataReceived.Add(fun _ -> ())
        process.BeginErrorReadLine()
        Task.Run(Action readLoop) |> ignore

    member _.Initialize() =
        let rootUri = Uri(Path.GetFullPath root + string Path.DirectorySeparatorChar).AbsoluteUri
        request "initialize"
            (box {| processId = Environment.ProcessId
                    rootUri = rootUri
                    workspaceFolders = [| {| uri = rootUri; name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) |} |]
                    capabilities = {| workspace = {| configuration = true; workspaceFolders = true |}
                                      textDocument = {| synchronization = {| didSave = false |} |} |}
                    clientInfo = {| name = "Jarvis"; version = "1" |}
                    initializationOptions = {| |}
                    trace = "off" |}) 30. |> ignore
        notification "initialized" (box {| |})

    member _.IsAlive = Volatile.Read(&disposed) = 0 && not process.HasExited

    member _.Format(filePath: string, content: string) =
        gate.Wait()
        try
            let uri = Uri(Path.GetFullPath filePath).AbsoluteUri
            match documents.TryGetValue uri with
            | true, version ->
                let version = version + 1
                documents[uri] <- version
                notification "textDocument/didChange"
                    (box {| textDocument = {| uri = uri; version = version |}
                            contentChanges = [| {| text = content |} |] |})
            | _ ->
                documents[uri] <- 1
                notification "textDocument/didOpen"
                    (box {| textDocument = {| uri = uri; languageId = "csharp"
                                              version = 1; text = content |} |})
            let result =
                request "textDocument/formatting"
                    (box {| textDocument = {| uri = uri |}
                            options = {| tabSize = 4; insertSpaces = true
                                         trimTrailingWhitespace = false
                                         insertFinalNewline = false |} |}) 30.
            match applyTextEdits content result with
            | Ok(updated, count) ->
                { Content = updated
                  EditCount = count
                  Diagnostics =
                    match diagnostics.TryGetValue uri with
                    | true, entries -> entries
                    | _ -> [] }
            | Error message -> invalidOp message
        finally gate.Release() |> ignore

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                for KeyValue(_, completion) in pending do
                    completion.TrySetCanceled() |> ignore
                try
                    if not process.HasExited then process.Kill(true)
                with _ -> ()
                process.Dispose()
                gate.Dispose()

let private sessions = ConcurrentDictionary<string, Lazy<Task<Session>>>(StringComparer.Ordinal)

let private remove root =
    let mutable old = Unchecked.defaultof<Lazy<Task<Session>>>
    if sessions.TryRemove(root, &old) && old.IsValueCreated then
        let initialized = old.Value
        if initialized.IsCompletedSuccessfully then
            (initialized.Result :> IDisposable).Dispose()
        elif not initialized.IsCompleted then
            // Shutdown or worktree removal must also dispose a session whose
            // asynchronous initialization is still in flight.
            initialized.ContinueWith(fun (finished: Task<Session>) ->
                if finished.IsCompletedSuccessfully then
                    (finished.Result :> IDisposable).Dispose()) |> ignore

let private getLazy root =
    sessions.GetOrAdd(root, fun path ->
        lazy (Task.Run(fun () ->
            let session = new Session(path)
            try
                session.Initialize()
                session
            with _ ->
                (session :> IDisposable).Dispose()
                reraise())))

let prewarm (root: string) =
    let root = Path.GetFullPath root
    let session = getLazy root
    // Nonblocking and idempotent; a later formatting request sees startup errors.
    let task = session.Value
    task.ContinueWith(fun (finished: Task<Session>) ->
        if finished.IsFaulted || finished.IsCanceled then remove root) |> ignore

let formatDocument root filePath content =
    let root = Path.GetFullPath root
    let acquire () =
        let instance = getLazy root
        try
            let session = instance.Value.GetAwaiter().GetResult()
            if not session.IsAlive then
                remove root
                getLazy root |> fun next -> next.Value.GetAwaiter().GetResult()
            else session
        with _ ->
            remove root
            reraise()
    try
        acquire().Format(filePath, content) |> Ok
    with ex ->
        remove root
        Error ex.Message

let disposeCheckout root =
    remove (Path.GetFullPath root)

let disposeAll () =
    for key in sessions.Keys do remove key
