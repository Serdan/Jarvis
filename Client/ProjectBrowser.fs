module Client.ProjectBrowser

open System.IO
open System.Net.Http
open Client.Lib
open Common
open Microsoft.FSharp.Core
open Client.IO
open FsToolkit.ErrorHandling
open Client.Effect

let option = OptionBuilder()

let fileOperations =
    { ReadAllText = FileOperations.readAllText
      WriteAllText = FileOperations.writeAllText
      parseFile = FileOperations.parseFile
      CopyFile = FileOperations.copyFile
      AppendAllText = FileOperations.appendAllText
      parseFolder = FileOperations.parseFolder
      GetFiles = FileOperations.getFiles
      getChildFolders = FileOperations.getChildFolders
      GetFileInfo = FileOperations.getFileInfo
      GetFolderName = FileOperations.getFolderName
      getFileName = FileOperations.getFileName }

module private FileIO =
    let parseFile file (rt: #FileIO) = rt.File.parseFile file
    let parseFolder path (rt: #FileIO) = rt.File.parseFolder path
    let getChildFolders path (rt: #FileIO) = rt.File.getChildFolders path
    let getFolderName path (rt: #FileIO) = rt.File.GetFolderName path
    let getFileName file (rt: #FileIO) = rt.File.getFileName file
    let getFiles path (rt: #FileIO) = rt.File.GetFiles path
    let getFileInfo file (rt: #FileIO) = rt.File.GetFileInfo file
    let readAllText file (rt: #FileIO) = rt.File.ReadAllText file
    let writeAllText filePath content (rt: #FileIO) = rt.File.WriteAllText filePath content
    let appendAllText file content (rt: #FileIO) = rt.File.AppendAllText file content

module ProjectIO =
    let root (rt: #ProjectIO) = rt.Project.Root |> Ok
    let folderFilters (rt: #ProjectIO) = rt.Project.FolderFilters
    let specialFiles (rt: #ProjectIO) = rt.Project.SpecialFiles |> Ok

let private projectFiles = [ "readme.md"; "notes.md"; "todo.md" ]

let private folderFilters: (string -> bool) list =
    [ (_.StartsWith('.') >> not)
      (_.Equals("bin") >> not)
      (_.Equals("obj") >> not) ]

type Runtime(root: string) =
    member _.httpClient = new HttpClient()

    interface ProjectIO with
        member this.Project =
            { Root = ProjectDirectory root
              FolderFilters = folderFilters
              SpecialFiles = projectFiles }

    interface FileIO with
        member this.File = fileOperations

    interface WebIO with
        member this.Browser =
            { LoadPage =
                fun (Url url) ->
                    taskResult {
                        try
                            let! result = this.httpClient.GetStringAsync(url)
                            return! result |> Content |> Ok
                        with e ->
                            return! e |> GenericError |> Error
                    } }

let private getFullPath paths (ctx: #ProjectIO) =
    let (ProjectDirectory root) = ctx.Project.Root

    paths
    |> Path.combineAll
    |> Path.combine root
    |> Path.getFullPath
    |> Result.mapError GenericError
    |> Result.bind (fun path ->
        match path |> String.startsWith root with
        | true -> Ok path
        | false -> $"Invalid path: {path}" |> NotFoundError |> Error)

let listMap f items =
    fun rt -> items |> Seq.map (f >> (fun x -> x rt)) |> Ok

let listProjects rt =
    effect {
        let! root = ProjectIO.root |>> _.ToFolderPath
        let! children = FileIO.getChildFolders root
        let! names = children |> listMap FileIO.getFolderName
        return names |> Seq.map ProjectName
    }
    <| rt

let private parseProjectName (projectName: string) =
    effect {
        let! projects = listProjects

        return!
            projects
            |> Seq.tryFind (fun (ProjectName name) -> name = projectName)
            |> Effect.ofOption (fun () -> NotFoundError $"Unknown project name: {projectName}")
    }

let private parseFolderPath (path: string) (ProjectName projectName) =
    getFullPath [ projectName; path ] >>= FileIO.parseFolder

let private parseFilePath (path: string) (ProjectName projectName) =
    getFullPath [ projectName; path ] >>= FileIO.parseFile

let private getFolderNames path projectName =
    effect {
        let! path = parseFolderPath path projectName
        let! children = FileIO.getChildFolders path
        let! names = children |> listMap FileIO.getFolderName
        let! filtered = fun rt -> names |> Seq.filterAll (ProjectIO.folderFilters rt) |> Ok
        return filtered |> Seq.map ProjectFolder |> Seq.toList
    }

let private getFileNames path projectName =
    effect {
        let! path = parseFolderPath path projectName
        let! files = FileIO.getFiles path
        let! infos = files |> listMap FileIO.getFileInfo |>> (Seq.choose Result.toOption)

        return infos |> Seq.map ProjectItemKind.ofFileInfo |> Seq.toList
    }

let private getItems path projectName =
    let folderNames = getFolderNames path projectName
    let fileNames = getFileNames path projectName
    Effect.concat folderNames fileNames

let getProjectDetails projectName =
    let loadFile (fileInfo: FileInfo) =
        effect {
            let! text = FileIO.readAllText (FilePath fileInfo.FullName)
            return (fileInfo.Name, text)
        }

    let projectFilesInfo (FolderPath path) =
        effect {
            let! specialFiles = ProjectIO.specialFiles |>> (Seq.map (Path.combine path >> FilePath))
            let! infos = specialFiles |> listMap FileIO.getFileInfo |>> Seq.choose Result.toOption
            let! data = infos |> listMap loadFile |>> Seq.choose Result.toOption
            return data |> Seq.toList
        }

    projectName |> (parseProjectName >=> parseFolderPath "" >=> projectFilesInfo)


let listProjectDirectory projectName folderPath =
    projectName |> (parseProjectName >=> getItems folderPath)

let openFile projectName filePath =
    parseProjectName projectName >>= parseFilePath filePath >>= FileIO.readAllText

let writeFile projectName filePath content mode =
    let write =
        match mode with
        | Append -> FileIO.appendAllText
        | Write -> FileIO.writeAllText

    effect {
        let! path = parseProjectName projectName >>= parseFilePath filePath
        do! write path (Content content)
    }

let private updateFile projectName filePath update =
    let save content =
        effect {
            do! writeFile projectName filePath content FileWriteMode.Write
            return Content content
        }

    openFile projectName filePath >>= (Effect.lift' update) >>= save

let replaceSection projectName filePath sectionIdentifiers replacementContent =
    let update (Content content) =
        option {
            let! start = content |> String.tryIndexOf sectionIdentifiers.Start
            let! end' = content |> String.tryIndexOf' sectionIdentifiers.End start

            return
                content[..start]
                + replacementContent
                + content[(end' + sectionIdentifiers.End.Length) ..]
        }
        |> Result.ofOption (fun () -> "Section identifiers not found in file." |> NotFoundError)

    updateFile projectName filePath update

let replaceText projectName filePath searchText replacementText =
    let update (Content content) =
        option {
            let! index = content |> String.tryIndexOf searchText
            return content[..index] + replacementText + content[(index + searchText.Length) ..]
        }
        |> Result.ofOption (fun () -> "Search text not found in file." |> NotFoundError)

    updateFile projectName filePath update

let insertBefore projectName filePath searchText insertContent =
    let update (Content content) =
        option {
            let! index = content |> String.tryIndexOf searchText
            return content[..index] + insertContent + content[index..]
        }
        |> Result.ofOption (fun () -> "Search text not found in file." |> NotFoundError)

    updateFile projectName filePath update

let insertAfter projectName filePath searchText insertContent =
    let update (Content content) =
        option {
            let! index = content |> String.tryIndexOf searchText

            return
                content[.. (index + searchText.Length)]
                + insertContent
                + content[(index + searchText.Length) ..]
        }
        |> Result.ofOption (fun () -> "Search text not found in file." |> NotFoundError)

    updateFile projectName filePath update
