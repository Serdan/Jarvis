module Client.ProjectBrowser

open System.IO
open Common
open Microsoft.FSharp.Core
open Client.IO
open FsToolkit.ErrorHandling
open Client.Effect

module private Core =
    let private getFullPath (paths: string seq) : IO<'a, string> =
        effect {
            let! (ProjectDirectory root) = ProjectIO.root

            let! fullPath = paths |> Path.combineAll |> Path.combine root |> FileIO.getFullPath

            match fullPath |> String.startsWith root with
            | true -> return fullPath
            | false -> return! "Path not in project" |> NotFoundError |> Effect.ofError
        }

    let private listMap f items =
        fun rt -> items |> Seq.map (f >> (fun x -> x rt)) |> Ok

    let listProjects rt =
        effect {
            let! root = ProjectIO.root |>> ProjectDirectory.toFolderPath
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

    let openProject projectName =
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

        projectName |> parseProjectName >>= parseFolderPath "" >>= projectFilesInfo

    let listProjectDirectory projectName folderPath =
        projectName |> parseProjectName >>= getItems folderPath

    let readFile projectName filePath =
        parseProjectName projectName >>= parseFilePath filePath >>= FileIO.readAllText

    let readFiles projectName filePaths =
        let readFile path projectName =
            parseFilePath path projectName >>= FileIO.readAllText
            |>> (fun (Content x) -> Content'.Text x)
            |> Effect.defaultWith Content'.Error

        let readFiles filePaths projectName =
            fun rt -> seq { for path in filePaths -> readFile path projectName rt } |> Ok

        parseProjectName projectName >>= readFiles filePaths

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

        readFile projectName filePath >>= (Effect.lift' update) >>= save

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

let listProjects rt = Core.listProjects rt

let listDirectory (cmd: ListDirectoryCommand) : IO<'rt, ProjectItemKind list> =
    Core.listProjectDirectory cmd.ProjectName cmd.FolderPath

let openProject (cmd: OpenProjectCommand) : IO<'rt, (string * Content) list> = Core.openProject cmd.ProjectName

let readFile (cmd: ReadFileCommand) : IO<'rt, Content> =
    Core.readFile cmd.ProjectName cmd.FilePath

let readFiles (cmd: ReadFilesCommand) : IO<'rt, Content' seq> =
    Core.readFiles cmd.ProjectName cmd.FilePaths

let writeFile (cmd: WriteFileCommand) : IO<'rt, unit> =
    Core.writeFile cmd.ProjectName cmd.FilePath cmd.Content cmd.FileWriteMode

let replaceSection (cmd: TextReplaceSectionCommand) : IO<'rt, Content> =
    Core.replaceSection cmd.ProjectName cmd.FilePath cmd.SectionIdentifiers cmd.Content

let replaceText (cmd: TextReplaceCommand) : IO<'rt, Content> =
    Core.replaceText cmd.ProjectName cmd.FilePath cmd.Search cmd.Content

let insertBefore (cmd: TextReplaceCommand) : IO<'rt, Content> =
    Core.insertBefore cmd.ProjectName cmd.FilePath cmd.Search cmd.Content

let insertAfter (cmd: TextReplaceCommand) : IO<'rt, Content> =
    Core.insertAfter cmd.ProjectName cmd.FilePath cmd.Search cmd.Content
