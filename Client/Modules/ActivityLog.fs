module Client.ActivityLog

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json

[<CLIMutable>]
type Snapshot =
    { Version: int
      ActivityId: string
      Origin: string
      StartedAt: DateTimeOffset
      ProjectName: string
      CommandName: string
      Message: string
      Reason: string
      Detail: string
      Status: string
      DurationMs: Nullable<int64>
      Result: string
      Error: string }

type Settings =
    { DirectoryPath: string
      RetentionDays: int
      MaxFileBytes: int64 }

let defaultSettings () =
    let root = Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData
    { DirectoryPath = Path.Combine(root, "Jarvis", "activity")
      RetentionDays = 14
      MaxFileBytes = 10L * 1024L * 1024L }

let private privateDirectoryMode =
    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

let private privateFileMode =
    UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private isUnix () = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()

// A large or corrupt retained file must not force an unbounded replay or
// unbounded single-line JSON allocation. Older entries remain on disk.
let private maxReplayBytes = 32L * 1024L * 1024L
let private maxReplayLineBytes = 512 * 1024
let private maxRecentEntries = 5000
let private maxSeenBytes = 4 * 1024 * 1024

let private rejectLink path =
    // LinkTarget also recognizes a dangling symlink (File.Exists does not).
    if FileInfo(path).LinkTarget <> null || DirectoryInfo(path).LinkTarget <> null then
        raise (UnauthorizedAccessException "A Jarvis activity storage path is a symbolic link.")

let private ensureDirectory path =
    rejectLink path
    if isUnix() then
        Directory.CreateDirectory(path, privateDirectoryMode) |> ignore
    else
        // %LOCALAPPDATA% normally inherits the user's private Windows ACL.
        Directory.CreateDirectory(path) |> ignore
    rejectLink path
    if isUnix() then File.SetUnixFileMode(path, privateDirectoryMode)

let private secureFile path =
    rejectLink path
    if Directory.Exists path then
        raise (IOException "An activity history file path is a directory.")
    if File.Exists path && isUnix() then
        File.SetUnixFileMode(path, privateFileMode)

let private createPrivateFile path =
    rejectLink path
    let options = FileStreamOptions()
    options.Mode <- FileMode.CreateNew
    options.Access <- FileAccess.Write
    options.Share <- FileShare.None
    if isUnix() then options.UnixCreateMode <- privateFileMode
    new FileStream(path, options)

let private openAppend path =
    secureFile path
    let options = FileStreamOptions()
    options.Mode <- FileMode.Append
    options.Access <- FileAccess.Write
    options.Share <- FileShare.ReadWrite
    if isUnix() then options.UnixCreateMode <- privateFileMode
    let stream = new FileStream(path, options)
    try
        // Also repair a file created by a concurrent cooperating process.
        secureFile path
        stream
    with _ ->
        stream.Dispose()
        reraise()

let private isIncompleteStatus status =
    String.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
    || String.Equals(status, "Awaiting permission", StringComparison.OrdinalIgnoreCase)

let private normalizeReplayed snapshot =
    if isIncompleteStatus snapshot.Status then
        { snapshot with
            Status = "Interrupted"
            DurationMs = Nullable()
            Result = null
            Error = "Client stopped before this activity completed." }
    else snapshot

// Date and rotation segment determine chronology; alphabetic filename order
// puts an unsuffixed .jsonl after -001.jsonl and is not sufficient.
let private tryLogKey (path: string) =
    let name = Path.GetFileName path
    if not (name.StartsWith("activity-", StringComparison.Ordinal)
            && name.EndsWith(".jsonl", StringComparison.Ordinal)) then None
    else
        let middle = name.Substring(9, name.Length - 9 - 6)
        if middle.Length < 10 then None
        else
            let datePart = middle.Substring(0, 10)
            let mutable date = Unchecked.defaultof<DateOnly>
            if not (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, &date)) then None
            elif middle.Length = 10 then Some(date.DayNumber, 0)
            elif middle.Length > 11 && middle[10] = '-' then
                let suffix = middle.Substring 11
                let mutable segment = 0
                if suffix |> Seq.forall Char.IsAsciiDigit
                   && Int32.TryParse(suffix, &segment) then
                    Some(date.DayNumber, segment)
                else None
            else None

type Store(settings: Settings) =
    do
        if String.IsNullOrWhiteSpace settings.DirectoryPath then invalidArg "settings" "Missing activity directory."
        if settings.MaxFileBytes <= 0L then invalidArg "settings" "Activity log segment size must be positive."

    let syncRoot = obj()
    let serializerOptions = JsonSerializerOptions()
    let mutable currentDate = ""
    let mutable currentStream: FileStream option = None
    let mutable currentWriter: StreamWriter option = None
    let mutable lastIssue: string option = None
    let mutable replayBytes = 0L
    let seenPath = Path.Combine(settings.DirectoryPath, "agent-seen.json")

    // Best-effort activity writes must not fail the agent command they describe.
    // Unlike the old silent catch-all, errors are observable and deduplicated.
    // No paths, command arguments, or serialized content enter diagnostics.
    let report issue (ex: exn) =
        let diagnostic = $"Jarvis activity persistence: {issue} ({ex.GetType().Name}). Check storage permissions and disk space."
        if lastIssue <> Some diagnostic then
            lastIssue <- Some diagnostic
            // The diagnostic channel itself may be closed or unwritable.
            // Logging a persistence failure must not abort agent execution.
            try eprintfn "%s" diagnostic with _ -> ()

    let closeWriter () =
        try currentWriter |> Option.iter (fun writer -> writer.Dispose())
        finally
            currentWriter <- None
            currentStream <- None
            currentDate <- ""

    let allLogFiles () =
        ensureDirectory settings.DirectoryPath
        if not (Directory.Exists(settings.DirectoryPath)) then [||]
        else
            Directory.EnumerateFiles(settings.DirectoryPath, "activity-*.jsonl")
            |> Seq.map (fun path ->
                // Even malformed legacy names may hold sensitive command
                // details. Repair all matching log files, not just valid
                // chronological segment names.
                rejectLink path
                path)
            |> Seq.toArray

    let logFiles () =
        allLogFiles()
        |> Array.choose (fun path -> tryLogKey path |> Option.map (fun key -> key, path))

    let pruneOldFiles (now: DateTimeOffset) =
        let cutoff = now.UtcDateTime.AddDays(-(float (max 1 settings.RetentionDays)))
        for path in allLogFiles() do
            secureFile path
            if File.GetLastWriteTimeUtc(path) < cutoff then File.Delete path

    let filePathFor (date: string) (segment: int) =
        if segment = 0 then Path.Combine(settings.DirectoryPath, $"activity-{date}.jsonl")
        else Path.Combine(settings.DirectoryPath, $"activity-{date}-{segment:D3}.jsonl")

    let selectWritablePath date =
        let mutable segment = 0
        let mutable selected = filePathFor date segment
        let mutable found = false
        while not found do
            secureFile selected
            if not (File.Exists selected) || FileInfo(selected).Length < settings.MaxFileBytes then
                found <- true
            else
                if segment = Int32.MaxValue then
                    raise (IOException "Activity log rotation limit exceeded.")
                segment <- segment + 1
                selected <- filePathFor date segment
        selected

    let openWriter (now: DateTimeOffset) =
        ensureDirectory settings.DirectoryPath
        pruneOldFiles now
        let date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        let path = selectWritablePath date
        let stream = openAppend path
        try
            let writer = new StreamWriter(stream, UTF8Encoding(false))
            writer.AutoFlush <- true
            currentDate <- date
            currentStream <- Some stream
            currentWriter <- Some writer
        with _ ->
            stream.Dispose()
            reraise()

    let ensureWriter (now: DateTimeOffset) =
        let date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        let rotate =
            currentDate <> date ||
            (currentStream |> Option.exists (fun stream -> stream.Length >= settings.MaxFileBytes))
        if currentWriter.IsNone || rotate then
            closeWriter()
            openWriter now

    // A reverse block reader allocates at most one bounded UTF-8 line and one
    // fixed-size buffer. It stops as soon as the requested number of distinct
    // IDs is found; a bounded byte budget applies even with endless duplicates.
    let scanBackwards path shouldContinue needsMore onLine =
        secureFile path
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite ||| FileShare.Delete)
        let buffer = Array.zeroCreate<byte> (16 * 1024)
        let reversed = ResizeArray<byte>()
        let mutable oversized = false
        let mutable cursor = stream.Length
        let emit () =
            if not oversized && reversed.Count > 0 then
                let raw = reversed.ToArray()
                Array.Reverse raw
                try
                    let decoded = UTF8Encoding(false, true).GetString raw
                    // StreamReader stripped the UTF-8 BOM in the old forward
                    // reader. Preserve that behavior for existing log files.
                    let line =
                        if decoded.Length > 0 && decoded[0] = '\uFEFF' then decoded.Substring(1)
                        else decoded
                    onLine line
                with
                | :? DecoderFallbackException
                | :? JsonException -> ()
            reversed.Clear()
            oversized <- false
        while cursor > 0L && shouldContinue() do
            let take = int (min (int64 buffer.Length) (min cursor (maxReplayBytes - replayBytes)))
            if take > 0 then
                cursor <- cursor - int64 take
                stream.Seek(cursor, SeekOrigin.Begin) |> ignore
                stream.ReadExactly(buffer, 0, take)
                replayBytes <- replayBytes + int64 take
                let mutable offset = take - 1
                while offset >= 0 && needsMore() do
                    let value = buffer[offset]
                    if value = 10uy then emit()
                    elif not oversized then
                        if reversed.Count < maxReplayLineBytes then reversed.Add value
                        else
                            oversized <- true
                            reversed.Clear()
                    offset <- offset - 1
            else cursor <- 0L
        if cursor = 0L && needsMore() then emit()

    do
        ensureDirectory settings.DirectoryPath
        // Repair existing installations before any read or append, not only
        // newly created files. Reject symlinks even if they are dangling.
        secureFile seenPath
        pruneOldFiles DateTimeOffset.Now

    member _.LastPersistenceError = lock syncRoot (fun () -> lastIssue)
    member _.LastReplayBytesScanned = lock syncRoot (fun () -> replayBytes)

    member _.Append(snapshot: Snapshot) =
        lock syncRoot (fun () ->
            try
                ensureWriter DateTimeOffset.Now
                let json = JsonSerializer.Serialize(snapshot, serializerOptions)
                match currentWriter, currentStream with
                | Some writer, Some stream ->
                    writer.WriteLine(json)
                    writer.Flush()
                    stream.Flush()
                | _ -> invalidOp "Activity history stream unavailable."
            with ex ->
                report "append failed" ex
                try closeWriter() with closeError -> report "close failed" closeError)

    /// Session-attributed agent activity; persisted separately from the log.
    member _.LoadAgentSeen() =
        lock syncRoot (fun () ->
            try
                ensureDirectory settings.DirectoryPath
                secureFile seenPath
                if not (File.Exists seenPath) then []
                else
                    // Readers must not block another client's atomic rename
                    // while it publishes a newer complete snapshot on Windows.
                    use input = new FileStream(seenPath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite ||| FileShare.Delete)
                    use content = new MemoryStream()
                    let buffer = Array.zeroCreate<byte> 8192
                    let mutable doneReading = false
                    while not doneReading do
                        let read = input.Read(buffer, 0, min buffer.Length (maxSeenBytes + 1 - int content.Length))
                        if read = 0 then doneReading <- true
                        else content.Write(buffer, 0, read)
                        if content.Length > int64 maxSeenBytes then
                            raise (InvalidDataException "Jarvis agent-seen state exceeds the size limit.")
                    let values =
                        JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(content.ToArray())
                    if isNull values then [] else
                    values |> Seq.map (fun entry -> entry.Key, entry.Value) |> Seq.toList
            with
            | :? JsonException as ex ->
                report "agent-seen state could not be decoded" ex
                []
            | ex ->
                report "agent-seen read failed" ex
                reraise())

    member _.SaveAgentSeen(values: seq<string * DateTimeOffset>) =
        lock syncRoot (fun () ->
            let temporary = Path.Combine(settings.DirectoryPath, $".agent-seen.{Guid.NewGuid():N}.tmp")
            try
                try
                    ensureDirectory settings.DirectoryPath
                    secureFile seenPath
                    let snapshot = Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
                    for key, value in values do snapshot[key] <- value
                    do
                        use stream = createPrivateFile temporary
                        use writer = new StreamWriter(stream, UTF8Encoding(false))
                        writer.Write(JsonSerializer.Serialize(snapshot))
                        writer.Flush()
                        stream.Flush(true)
                    secureFile seenPath
                    File.Move(temporary, seenPath, true)
                    secureFile seenPath
                with ex ->
                    report "agent-seen save failed" ex
                    raise (IOException("Unable to save the Jarvis agent-seen snapshot.", ex))
            finally
                // File.Delete unlinks a symlink itself, never its target.
                // Cleanup must not mask the original persistence failure.
                try
                    if FileInfo(temporary).LinkTarget <> null || File.Exists temporary then
                        File.Delete temporary
                with ex -> report "agent-seen staging cleanup failed" ex)

    member _.LoadRecent(maxEntries: int) =
        lock syncRoot (fun () ->
            replayBytes <- 0L
            let limit = min maxRecentEntries (max 0 maxEntries)
            if limit = 0 then []
            else
                currentWriter |> Option.iter (fun writer -> writer.Flush())
                let discovered = HashSet<string>(StringComparer.Ordinal)
                let recent = ResizeArray<Snapshot>()
                let shouldContinue () =
                    recent.Count < limit && replayBytes < maxReplayBytes
                let handleLine (line: string) =
                    try
                        let snapshot = JsonSerializer.Deserialize<Snapshot>(line, serializerOptions)
                        if not (isNull (box snapshot)) &&
                           snapshot.Version = 1 &&
                           not (String.IsNullOrWhiteSpace snapshot.ActivityId) &&
                           discovered.Add snapshot.ActivityId then
                            recent.Add(normalizeReplayed snapshot)
                    with :? JsonException -> ()
                for _, path in logFiles() |> Array.sortByDescending fst do
                    if shouldContinue() then
                        try scanBackwards path shouldContinue (fun () -> recent.Count < limit) handleLine
                        with
                        | :? UnauthorizedAccessException as ex ->
                            report "replay rejected unsafe file" ex
                            reraise()
                        | ex -> report "replay skipped unreadable file" ex
                // The latest persisted state wins, while the TUI retains each
                // activity's original start-time display order rather than
                // reordering old activities on completion.
                recent
                |> Seq.rev
                |> Seq.mapi (fun order snapshot -> (snapshot.StartedAt, order), snapshot)
                |> Seq.sortBy fst
                |> Seq.map snd
                |> Seq.toList)

    interface IDisposable with
        member _.Dispose() = lock syncRoot closeWriter

    static member CreateDefault() = new Store(defaultSettings())
