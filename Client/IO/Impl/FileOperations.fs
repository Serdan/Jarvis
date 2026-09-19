module Client.IO.FileOperations

open System
open System.IO
open Client

let getFullPath path =
    try
        Path.GetFullPath path |> Ok
    with e ->
        e |> ExceptionError |> Error

let readAllText (FilePath filePath) =
    try
        File.ReadAllText(filePath) |> Content |> Ok
    with e ->
        e |> ExceptionError |> Error

let readLines (FilePath filePath) startLine endLine =
    try
        File.ReadLines(filePath)
        |> Seq.indexed
        |> Seq.skipWhile (fun (index, _) -> index < startLine - 1)
        |> fun lines ->
            match endLine with
            | Some endLine -> lines |> Seq.takeWhile (fun (index, _) -> index < endLine)
            | None -> lines
        |> Seq.map (fun (index, line) -> index + 1, line)
        |> Seq.toList
        |> Ok
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
