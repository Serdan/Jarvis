module Client.IO.FileOperations

open System
open System.IO
open System.Text
open Client
open Common

let getFullPath path =
    try
        Path.GetFullPath path |> Ok
    with e ->
        e |> ExceptionError |> Error

// All text/binary readers share a bounded source. Length checks by
// themselves are not sufficient: another writer may extend a file after the
// metadata was inspected but before the consuming read returns.
exception private SourceReadLimitExceeded of string

type private LimitedSource(source: Stream, maxBytes: int64) =
    inherit Stream()
    let mutable total = 0L
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Flush() = ()
    override _.Length = raise (NotSupportedException())
    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.Read(buffer: byte[], offset, count) =
        let remaining = maxBytes - total
        if remaining < 0L then
            raise (SourceReadLimitExceeded "Source byte budget exceeded.")
        let permitted = int (min (int64 count) (remaining + 1L))
        let read = source.Read(buffer, offset, permitted)
        total <- total + int64 read
        if total > maxBytes then
            raise (SourceReadLimitExceeded "Source byte budget exceeded.")
        read
    override _.Dispose(disposing) =
        if disposing then source.Dispose()
        base.Dispose(disposing)

let private openLimitedText path limit =
    let source =
        new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite ||| FileShare.Delete)
    let limited = new LimitedSource(source, int64 limit)
    new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks = true)

let private maxSingleLineChars = 256 * 1024

// StreamReader.ReadLine() may create a single huge string before callers can
// inspect its size. Bound each line while consuming it instead.
let private readLineBounded (reader: StreamReader) =
    let line = StringBuilder()
    let mutable stop = false
    let mutable eof = false
    while not stop do
        let next = reader.Read()
        if next < 0 then
            eof <- true
            stop <- true
        elif next = int '\n' then
            stop <- true
        elif next = int '\r' then
            stop <- true
            if reader.Peek() = int '\n' then reader.Read() |> ignore
        elif line.Length >= maxSingleLineChars then
            raise (SourceReadLimitExceeded "Source contains a line longer than the bounded text reader supports; use ReadFilePage.")
        else
            line.Append(char next) |> ignore
    if eof && line.Length = 0 then None
    else Some(line.ToString())

let readAllText (FilePath filePath) =
    try
        use reader = openLimitedText filePath AgentProtocol.maxLegacyTextReadBytes
        let content = reader.ReadToEnd()
        let result = Content content
        // ReadFile returns the unwrapped string on the wire, not the F#
        // internal Content DU. Measure precisely that serialized string.
        if System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(content).Length > 700 * 1024 then
            Error(ValidationError "Direct text response exceeds the serialized byte budget; use ReadFilePage.")
        else Ok result
    with
    | SourceReadLimitExceeded _ ->
        Error(ValidationError
            $"File exceeds the {AgentProtocol.maxLegacyTextReadBytes} byte direct-read limit; use ReadFilePage.")
    | e -> Error(ExceptionError e)

let readAllBytes (FilePath filePath) =
    try
        use file = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                  FileShare.ReadWrite ||| FileShare.Delete)
        use bounded = new LimitedSource(file, int64 AgentProtocol.maxExportBytes)
        use output = new MemoryStream()
        bounded.CopyTo(output, 64 * 1024)
        Ok(output.ToArray())
    with
    | SourceReadLimitExceeded _ ->
        Error(ValidationError "File exceeds the bounded binary read limit.")
    | e -> Error(ExceptionError e)

let readLines (FilePath filePath) startLine endLine =
    try
        use reader = openLimitedText filePath AgentProtocol.maxRangeScanBytes
        let timer = Diagnostics.Stopwatch.StartNew()
        let results = ResizeArray<int * string>()
        let mutable lineNumber = 0
        let mutable outputBytes = 0
        let mutable finished = false
        let mutable budgetExceeded = false
        while not finished && not budgetExceeded do
            if timer.Elapsed > TimeSpan.FromSeconds 3. then
                raise (TimeoutException "Line-range scan exceeded its three-second work budget.")
            if endLine |> Option.exists (fun last -> lineNumber >= last) then
                finished <- true
            else
                match readLineBounded reader with
                | None -> finished <- true
                | Some line ->
                    lineNumber <- lineNumber + 1
                    if lineNumber >= startLine then
                        let size = Encoding.UTF8.GetByteCount(line) + 32
                        if results.Count >= AgentProtocol.maxRangeLines
                           || size > AgentProtocol.maxRangeReturnBytes - outputBytes then
                            budgetExceeded <- true
                        else
                            outputBytes <- outputBytes + size
                            results.Add(lineNumber, line)
        if budgetExceeded then
            Error(ValidationError
                $"Read range exceeds {AgentProtocol.maxRangeLines} lines or {AgentProtocol.maxRangeReturnBytes} response bytes; narrow the range or use ReadFilePage.")
        else Ok(results |> Seq.toList)
    with
    | SourceReadLimitExceeded message ->
        Error(ValidationError $"Line-range scan incomplete: {message}")
    | :? TimeoutException ->
        Error(ValidationError "Line-range scan reached its three-second work limit; use ReadFilePage.")
    | e -> Error(ExceptionError e)

let private previewLine (line: string) matchIndex =
    let maxLength = 200

    if line.Length <= maxLength then
        line
    else
        let start = max 0 (matchIndex - 80)
        let start = min start (line.Length - maxLength)
        let length = min maxLength (line.Length - start)
        let prefix = if start > 0 then "..." else ""
        let suffix = if start + length < line.Length then "..." else ""
        prefix + line.Substring(start, length) + suffix

let searchText (FilePath filePath) (query: string) maxResults =
    try
        use reader = openLimitedText filePath AgentProtocol.maxSearchFileBytes
        let timer = Diagnostics.Stopwatch.StartNew()
        let matches = ResizeArray<int * int * string>()
        let mutable lineNumber = 0
        let mutable line = if maxResults > 0 then readLineBounded reader else None
        while line.IsSome && matches.Count < maxResults do
            if timer.Elapsed > TimeSpan.FromSeconds 3. then
                raise (TimeoutException "Text scan exceeded its three-second work limit.")
            lineNumber <- lineNumber + 1
            let value = Option.get line
            let mutable searchIndex = 0
            let mutable searching = true
            while searching && matches.Count < maxResults do
                let matchIndex = value.IndexOf(query, searchIndex, StringComparison.OrdinalIgnoreCase)
                if matchIndex < 0 then searching <- false
                else
                    matches.Add(lineNumber, matchIndex + 1, previewLine value matchIndex)
                    searchIndex <- matchIndex + max 1 query.Length
            if matches.Count < maxResults then line <- readLineBounded reader
        matches |> Seq.toList |> Ok
    with
    | SourceReadLimitExceeded message ->
        Error(ValidationError $"Text search incomplete: {message}")
    | :? TimeoutException as timeout -> Error(ExceptionError timeout)
    | e -> Error(ExceptionError e)

let createDirectory (FolderPath folderPath) =
    try
        Directory.CreateDirectory(folderPath) |> ignore
        Ok()
    with e ->
        e |> ExceptionError |> Error

let writeAllText (FilePath filePath) (Content content) =
    try
        File.WriteAllText(filePath, content) |> Ok
    with e ->
        e |> ExceptionError |> Error

let parseFile path =
    match File.Exists path with
    | true -> path |> FilePath |> Ok
    | false -> $"File does not exist: {path}" |> NotFoundError |> Error

let copyFile (FilePath source) (FilePath destination) overwrite =
    try
        File.Copy(source, destination, overwrite) |> Ok
    with e ->
        e |> ExceptionError |> Error

let appendAllText (FilePath filePath) (Content content) =
    try
        File.AppendAllText(filePath, content) |> Ok
    with e ->
        e |> ExceptionError |> Error

let parseFolder path =
    match Directory.Exists path with
    | true -> path |> FolderPath |> Ok
    | false -> $"Folder does not exist: {path}" |> NotFoundError |> Error

let getFiles (FolderPath path) =
    try
        Directory.EnumerateFiles path |> Seq.map FilePath |> Ok
    with e ->
        e |> ExceptionError |> Error

let getChildFolders (FolderPath path) =
    try
        Directory.EnumerateDirectories path |> Seq.map FolderPath |> Ok
    with e ->
        e |> ExceptionError |> Error

let getFileInfo (FilePath filePath) =
    try
        let info = FileInfo filePath

        if info.Exists then
            info |> Ok
        else
            "File does not exist" |> NotFoundError |> Error
    with e ->
        e |> ExceptionError |> Error

let getFolderName (FolderPath folderPath) = Path.GetFileName folderPath

let getFileName (FilePath filePath) = Path.GetFileName filePath

let impl =
    { getFullPath = getFullPath
      ReadAllText = readAllText
      ReadAllBytes = readAllBytes
      ReadLines = readLines
      SearchText = searchText
      WriteAllText = writeAllText
      CreateDirectory = createDirectory
      parseFile = parseFile
      CopyFile = copyFile
      AppendAllText = appendAllText
      parseFolder = parseFolder
      GetFiles = getFiles
      getChildFolders = getChildFolders
      GetFileInfo = getFileInfo
      GetFolderName = getFolderName
      getFileName = getFileName }
