module Client.IO.FileOperations

open System.IO
open Client
open Client.Effect

let readAllText (FilePath filePath) =
    try
        File.ReadAllText(filePath) |> Content |> Ok
    with e ->
        e |> GenericError |> Error

let writeAllText (FilePath filePath) (Content content) =
    try
        File.WriteAllText(filePath, content) |> Ok
    with e ->
        e |> GenericError |> Error

let parseFile path =
    match File.Exists path with
    | true -> path |> FilePath |> Ok
    | false -> $"File does not exist: {path}" |> NotFoundError |> Error

let copyFile (FilePath source) (FilePath destination) overwrite =
    try
        File.Copy(source, destination, overwrite) |> Ok
    with e ->
        e |> GenericError |> Error

let appendAllText (FilePath filePath) (Content content) =
    try
        File.AppendAllText(filePath, content) |> Ok
    with e ->
        e |> GenericError |> Error

let parseFolder path =
    match Directory.Exists path with
    | true -> path |> FolderPath |> Ok
    | false -> $"Folder does not exist: {path}" |> NotFoundError |> Error

let getFiles (FolderPath path) =
    try
        Directory.EnumerateFiles path |> Seq.map FilePath |> Ok
    with e ->
        e |> GenericError |> Error

let getChildFolders (FolderPath path) =
    try
        Directory.EnumerateDirectories path |> Seq.map FolderPath |> Ok
    with e ->
        e |> GenericError |> Error

let getFileInfo (FilePath filePath) =
    try
        let info = FileInfo filePath

        if info.Exists then
            info |> Ok
        else
            "File does not exist" |> NotFoundError |> Error
    with e ->
        e |> GenericError |> Error

let getFolderName (FolderPath folderPath) = Path.GetFileName folderPath

let getFileName (FilePath filePath) = Path.GetFileName filePath
