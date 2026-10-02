module Client.ActivityLog

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

[<CLIMutable>]
type Snapshot =
    { Version: int
      ActivityId: string
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
    let root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    { DirectoryPath = Path.Combine(root, "Jarvis", "activity")
      RetentionDays = 14
      MaxFileBytes = 10L * 1024L * 1024L }

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
    else
        snapshot

type Store(settings: Settings) =
    let syncRoot = obj()
    let serializerOptions = JsonSerializerOptions()
    let mutable currentDate = ""
    let mutable currentStream: FileStream option = None
    let mutable currentWriter: StreamWriter option = None

    let closeWriter () =
        currentWriter |> Option.iter (fun writer -> writer.Dispose())
        currentWriter <- None
        currentStream <- None
        currentDate <- ""

    let pruneOldFiles (now: DateTimeOffset) =
        let cutoff = now.UtcDateTime.AddDays(-(float (max 1 settings.RetentionDays)))

        if Directory.Exists(settings.DirectoryPath) then
            for path in Directory.EnumerateFiles(settings.DirectoryPath, "activity-*.jsonl") do
                try
                    if File.GetLastWriteTimeUtc(path) < cutoff then
                        File.Delete(path)
                with _ ->
                    ()

    let filePathFor (date: string) (segment: int) =
        if segment = 0 then
            Path.Combine(settings.DirectoryPath, $"activity-{date}.jsonl")
        else
            let suffix = segment.ToString("D3")
            Path.Combine(settings.DirectoryPath, $"activity-{date}-{suffix}.jsonl")

    let selectWritablePath date =
        let mutable segment = 0
        let mutable selected = filePathFor date segment
        let mutable found = false

        while not found do
            if not (File.Exists(selected)) || FileInfo(selected).Length < settings.MaxFileBytes then
                found <- true
            else
                segment <- segment + 1
                selected <- filePathFor date segment

        selected

    let openWriter (now: DateTimeOffset) =
        Directory.CreateDirectory(settings.DirectoryPath) |> ignore
        pruneOldFiles now
        let date = now.ToString("yyyy-MM-dd")
        let path = selectWritablePath date
        let stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
        let writer = new StreamWriter(stream, UTF8Encoding(false))
        writer.AutoFlush <- true
        currentDate <- date
        currentStream <- Some stream
        currentWriter <- Some writer

    let ensureWriter (now: DateTimeOffset) =
        let date = now.ToString("yyyy-MM-dd")
        let rotate =
            currentDate <> date
            || currentStream
               |> Option.exists (fun stream -> stream.Length >= settings.MaxFileBytes)

        if currentWriter.IsNone || rotate then
            closeWriter ()
            openWriter now

    do
        Directory.CreateDirectory(settings.DirectoryPath) |> ignore
        pruneOldFiles DateTimeOffset.Now

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
                | _ ->
                    ()
            with _ ->
                closeWriter ())

    member _.LoadRecent(maxEntries: int) =
        lock syncRoot (fun () ->
            currentWriter |> Option.iter (fun writer -> writer.Flush())

            let latest = Dictionary<string, Snapshot>(StringComparer.Ordinal)
            let order = ResizeArray<string>()

            if Directory.Exists(settings.DirectoryPath) then
                settings.DirectoryPath
                |> fun directory -> Directory.EnumerateFiles(directory, "activity-*.jsonl")
                |> Seq.sort
                |> Seq.iter (fun path ->
                    try
                        for line in File.ReadLines(path) do
                            if not (String.IsNullOrWhiteSpace line) then
                                try
                                    let snapshot = JsonSerializer.Deserialize<Snapshot>(line, serializerOptions)

                                    if not (isNull (box snapshot))
                                       && snapshot.Version = 1
                                       && not (String.IsNullOrWhiteSpace snapshot.ActivityId) then
                                        if not (latest.ContainsKey snapshot.ActivityId) then
                                            order.Add snapshot.ActivityId

                                        latest[snapshot.ActivityId] <- snapshot
                                with _ ->
                                    ()
                    with _ ->
                        ())

            let start = max 0 (order.Count - max 0 maxEntries)

            [ for index in start .. order.Count - 1 do
                  let id = order[index]

                  match latest.TryGetValue id with
                  | true, snapshot -> yield normalizeReplayed snapshot
                  | false, _ -> () ])

    interface IDisposable with
        member _.Dispose() =
            lock syncRoot closeWriter

    static member CreateDefault() =
        new Store(defaultSettings())
