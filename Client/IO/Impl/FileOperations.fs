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

let readAllText (FilePath filePath) =
    try
        let info = FileInfo(filePath)
        if info.Length > int64 AgentProtocol.maxLegacyTextReadBytes then
            Error(ValidationError
                $"File exceeds the {AgentProtocol.maxLegacyTextReadBytes} byte direct-read limit. Use ReadFilePage to inspect it with a continuation.")
        else
            File.ReadAllText(filePath) |> Content |> Ok
    with e ->
        e |> ExceptionError |> Error

let readAllBytes (FilePath filePath) =
    try
        let info = FileInfo(filePath)
        if info.Length > int64 AgentProtocol.maxExportBytes then
            Error(ValidationError "File exceeds the bounded binary read limit.")
        else File.ReadAllBytes(filePath) |> Ok
    with e ->
        e |> ExceptionError |> Error

let readLines (FilePath filePath) startLine endLine =
    try
        let info = FileInfo(filePath)
        if info.Length > int64 AgentProtocol.maxRangeScanBytes then
            Error(ValidationError "File exceeds the line-range scan budget; use ReadFilePage.")
        else
            use reader = File.OpenText(filePath)
            let results = ResizeArray<int * string>()
            let mutable lineNumber = 0
            let mutable outputBytes = 0
            let mutable finished = false
            let mutable budgetExceeded = false
            while not finished && not budgetExceeded do
                // ReadLine is bounded by the checked file-size ceiling, so
                // even a file consisting of one line cannot allocate 100 MiB.
                let line = reader.ReadLine()
                if isNull line then finished <- true
                else
                    lineNumber <- lineNumber + 1
                    if endLine |> Option.exists (fun last -> lineNumber > last) then
                        finished <- true
                    elif lineNumber >= startLine then
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
    with e ->
        e |> ExceptionError |> Error

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
        let info = FileInfo(filePath)
        if info.Length > int64 AgentProtocol.maxSearchFileBytes then
            Error(ValidationError "Text search reached its per-file scan limit; narrow the search or inspect using ReadFilePage.")
        else
            use reader = File.OpenText(filePath)
            let matches = ResizeArray<int * int * string>()
            let mutable lineNumber = 0
            let mutable line = reader.ReadLine()

            while not (isNull line) && matches.Count < maxResults do
                lineNumber <- lineNumber + 1
                let mutable searchIndex = 0
                let mutable searching = true

                while searching && matches.Count < maxResults do
                    let matchIndex = line.IndexOf(query, searchIndex, StringComparison.OrdinalIgnoreCase)

                    if matchIndex < 0 then
                        searching <- false
                    else
                        matches.Add(lineNumber, matchIndex + 1, previewLine line matchIndex)
                        searchIndex <- matchIndex + max 1 query.Length

                if matches.Count < maxResults then
                    line <- reader.ReadLine()

            matches |> Seq.toList |> Ok
    with e ->
        e |> ExceptionError |> Error

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
