module Client.ProjectBrowser

open System
open System.IO
open Client.Lib
open Microsoft.FSharp.Core

/// map without context
let inline (|->) v f = Context.map f v
/// map with context last
let inline (|=>) v f = Context.map' f v
/// map with context first
let inline (|=>>) v f = Context.map'' f v

/// bind without context
let inline (>->) v f = Context.bind f v
/// bind with context last
let inline (>=>) v f = Context.bind' f v
/// bind with context first
let inline (>=>>) v f = Context.bind'' f v

let inline (|?>) f e = Context.defaultWith e f

let option = OptionBuilder()

type FileOperations =
    { ReadAllText: FilePath -> Result<Content>
      WriteAllText: FilePath -> Content -> Result<unit>
      parseFile: string -> Result<FilePath>
      CopyFile: FilePath -> FilePath -> bool -> Result<unit>
      AppendAllText: FilePath -> Content -> Result<unit>
      parseFolder: string -> Result<FolderPath>
      GetFiles: FolderPath -> Result<FilePath seq>
      getChildFolders: FolderPath -> Result<FolderPath seq>
      GetFileInfo: FilePath -> Result<FileInfo>
      GetFolderName: FolderPath -> string
      getFileName: FilePath -> string }

let fileOperations =
    { ReadAllText =
        fun (FilePath filePath) ->
            try
                File.ReadAllText(filePath) |> Content |> Ok
            with e ->
                Error e
      WriteAllText =
        fun (FilePath filePath) (Content content) ->
            try
                File.WriteAllText(filePath, content) |> Ok
            with e ->
                Error e
      parseFile =
        fun path ->
            match File.Exists path with
            | true -> path |> FilePath |> Ok
            | false -> $"File does not exist: {path}" |> Exception |> Error
      CopyFile =
        fun (FilePath source) (FilePath destination) (overwrite: bool) ->
            try
                File.Copy(source, destination, overwrite) |> Ok
            with e ->
                Error e
      AppendAllText =
        fun (FilePath filePath) (Content content) ->
            try
                File.AppendAllText(filePath, content) |> Ok
            with e ->
                Error e
      parseFolder =
        fun path ->
            match Directory.Exists path with
            | true -> path |> FolderPath |> Ok
            | false -> $"Folder does not exist: {path}" |> Exception |> Error
      GetFiles =
        fun (FolderPath path) ->
            try
                Directory.EnumerateFiles path |> Seq.map FilePath |> Ok
            with e ->
                Error e
      getChildFolders =
        fun (FolderPath path) ->
            try
                Directory.EnumerateDirectories path |> Seq.map FolderPath |> Ok
            with e ->
                Error e
      GetFileInfo =
        fun (FilePath filePath) ->
            try
                FileInfo(filePath) |> Ok
            with e ->
                Error e
      GetFolderName = fun (FolderPath path) -> Path.GetFileName path
      getFileName = fun (FilePath path) -> Path.GetFileName path }

type FileIO =
    abstract File: FileOperations

module private FileIO =
    let parseFile (ctx: #FileIO) = ctx.File.parseFile
    let parseFolder (ctx: #FileIO) = ctx.File.parseFolder
    let getChildFolders (ctx: #FileIO) = ctx.File.getChildFolders
    let getFolderName (ctx: #FileIO) = ctx.File.GetFolderName
    let getFileName (ctx: #FileIO) = ctx.File.getFileName
    let getFiles (ctx: #FileIO) = ctx.File.GetFiles
    let getFileInfo (ctx: #FileIO) = ctx.File.GetFileInfo
    let readAllText (ctx: #FileIO) = ctx.File.ReadAllText
    let writeAllText (ctx: #FileIO) = ctx.File.WriteAllText
    let appendAllText (ctx: #FileIO) = ctx.File.AppendAllText

type ProjectData =
    { Root: ProjectDirectory
      SpecialFiles: string list
      FolderFilters: (string -> bool) list }

type ProjectIO =
    abstract Project: ProjectData

module ProjectIO =
    let folderFilters (ctx: #ProjectIO) = ctx.Project.FolderFilters
    let specialFiles (ctx: #ProjectIO) = ctx.Project.SpecialFiles

let private projectFiles = [ "readme.md"; "notes.md"; "todo.md" ]

let private folderFilters: (string -> bool) list =
    [ (_.StartsWith('.') >> not)
      (_.Equals("bin") >> not)
      (_.Equals("obj") >> not) ]

type Context(root: string) =
    interface ProjectIO with
        member this.Project =
            { Root = ProjectDirectory root
              FolderFilters = folderFilters
              SpecialFiles = projectFiles }

    interface FileIO with
        member this.File = fileOperations

let private getFullPath paths (ctx: #ProjectIO) =
    paths
    |> Path.combineAll
    |> Path.combine ctx.Project.Root.Value
    |> Path.getFullPath
    |> Result.bind (fun path ->
        match path |> String.startsWith ctx.Project.Root.Value with
        | true -> Ok path
        | false -> Error(Exception $"Invalid path: {path}"))

let listProjects (ctx: #ProjectIO & #FileIO) =
    ctx.File.getChildFolders ctx.Project.Root.ToFolderPath
    |> Result.map (fun folders -> folders |> Seq.map (ctx.File.GetFolderName >> ProjectName))

let private parseProjectName (projectName: string) =
    listProjects
    |=>> (fun _ -> Seq.tryFind (fun (ProjectName name) -> name = projectName))
    |?> (fun () -> Exception $"Unknown project name: {projectName}")

let private parseFolderPath (path: string) (ProjectName projectName) =
    getFullPath [ projectName; path ] >=>> FileIO.parseFolder

let private parseFilePath (path: string) (ProjectName projectName) =
    getFullPath [ projectName; path ] >=>> FileIO.parseFile

let private getFolderNames path projectName =
    parseFolderPath path projectName >=>> FileIO.getChildFolders
    |=>> (FileIO.getFolderName >> Seq.map)
    |=>> (ProjectIO.folderFilters >> Seq.filterAll)
    |-> (Seq.map ProjectFolder >> Seq.toList)

let private getFileNames path projectName =
    parseFolderPath path projectName >=>> FileIO.getFiles
    |=>> (FileIO.getFileInfo >> Seq.map)
    |-> Seq.choose Result.toOption
    |-> (Seq.map ProjectItemKind.ofFileInfo >> Seq.toList)

let private getItems path projectName =
    let folderNames = getFolderNames path projectName
    let fileNames = getFileNames path projectName
    Context.concat folderNames fileNames

let getProjectDetails projectName =
    let projectFilesInfo (FolderPath path) (ctx: #ProjectIO & #FileIO) =
        ctx.Project.SpecialFiles
        |> Seq.map (Path.combine path >> FilePath)
        |> Seq.map ctx.File.GetFileInfo
        |> Seq.choose Result.toOption
        |> Seq.toList

    parseProjectName projectName >=> parseFolderPath "" |=> projectFilesInfo

let listProjectDirectory projectName folderPath =
    parseProjectName projectName >=> getItems folderPath

let openFile projectName filePath =
    parseProjectName projectName >=> parseFilePath filePath >=>> FileIO.readAllText

let writeFile projectName filePath content mode =
    let action =
        match mode with
        | Append -> FileIO.appendAllText
        | Write -> FileIO.writeAllText

    parseProjectName projectName >=> parseFilePath filePath
    >=>> (action >> (fun f path -> f path (Content content)))

let replaceSection projectName filePath sectionIdentifiers replacementContent =
    let search (Content content) =
        option {
            let! start = content |> String.tryIndexOf sectionIdentifiers.Start
            let! end' = content |> String.tryIndexOf' sectionIdentifiers.End start

            return
                content[..start]
                + replacementContent
                + content[(end' + sectionIdentifiers.End.Length) ..]
        }
        |> Result.ofOption (fun () -> "Section identifiers not found in file." |> Exception)

    openFile projectName filePath >-> search
    >=> (fun content ctx ->
        writeFile projectName filePath content FileWriteMode.Write ctx
        |> Result.map (fun () -> content))

let replaceText projectName filePath searchText replacementText =
    let search (Content content) =
        option {
            let! index = content |> String.tryIndexOf searchText
            return content[..index] + replacementText + content[(index + searchText.Length) ..]
        }
        |> Result.ofOption (fun () -> "Search text not found in file." |> Exception)

    openFile projectName filePath >-> search
    >=> (fun content ctx ->
        writeFile projectName filePath content FileWriteMode.Write ctx
        |> Result.map (fun () -> content))

let insertBefore projectName filePath searchText insertContent =
    let search (Content content) =
        option {
            let! index = content |> String.tryIndexOf searchText
            return content[..index] + insertContent + content[index..]
        }
        |> Result.ofOption (fun () -> "Search text not found in file." |> Exception)

    openFile projectName filePath >-> search
    >=> (fun content ctx ->
        writeFile projectName filePath content FileWriteMode.Write ctx
        |> Result.map (fun () -> content))


let insertAfter projectName filePath searchText insertContent =
    let search (Content content) =
        option {
            let! index = content |> String.tryIndexOf searchText

            return
                content[.. (index + searchText.Length)]
                + insertContent
                + content[(index + searchText.Length) ..]
        }
        |> Result.ofOption (fun () -> "Search text not found in file." |> Exception)

    openFile projectName filePath >-> search
    >=> (fun content ctx ->
        writeFile projectName filePath content FileWriteMode.Write ctx
        |> Result.map (fun () -> content))
