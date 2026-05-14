module Client.ProjectBrowser

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Common
open Microsoft.FSharp.Core
open Client.IO
open FsToolkit.ErrorHandling
open Client.Effect
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

module private Hash =
    let sha256 (text: string) =
        let bytes = Encoding.UTF8.GetBytes text
        let hash = SHA256.HashData bytes |> Convert.ToHexString
        $"sha256:{hash.ToLowerInvariant()}"

module private Patch =
    let private detectLineEnding (text: string) =
        if text.Contains("\r\n") then "\r\n" else "\n"

    let private splitLines (text: string) =
        text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n') |> Array.toList

    let private tail (line: string) =
        if line.Length <= 1 then "" else line.Substring(1)

    let private parseOldStart (header: string) =
        let minus = header.IndexOf('-', StringComparison.Ordinal)
        if minus < 0 then None
        else
            let rest = header[(minus + 1) ..]
            let number = rest |> Seq.takeWhile Char.IsDigit |> Seq.toArray |> String
            match Int32.TryParse number with
            | true, value -> Some value
            | _ -> None

    let applyUnifiedDiff (patch: string) (content: string) : Client.Result<string> =
        let ending = detectLineEnding content
        let original = splitLines content
        let patchLines = splitLines patch

        let rec skipHeaders (lines: string list) =
            match lines with
            | [] -> []
            | line :: _ when line.StartsWith("@@", StringComparison.Ordinal) -> lines
            | _ :: rest -> skipHeaders rest

        let rec applyHunks oldIndex output (lines: string list) =
            match lines with
            | [] ->
                if oldIndex > original.Length then
                    Error(Client.ValidationError "Patch consumed beyond the end of the file.")
                else
                    Ok(output @ (original |> List.skip oldIndex))
            | header :: rest when header.StartsWith("@@", StringComparison.Ordinal) ->
                match parseOldStart header with
                | None -> Error(Client.ValidationError $"Invalid patch hunk header: {header}")
                | Some oldStart ->
                    let targetIndex = max 0 (oldStart - 1)

                    if targetIndex < oldIndex || targetIndex > original.Length then
                        Error(Client.ValidationError $"Patch hunk targets invalid line {oldStart}.")
                    else
                        let unchanged = original |> List.skip oldIndex |> List.take (targetIndex - oldIndex)

                        let rec applyHunk currentIndex acc (remaining: string list) =
                            match remaining with
                            | [] -> Ok(currentIndex, acc, [])
                            | [ "" ] -> Ok(currentIndex, acc, [])
                            | next :: _ when next.StartsWith("@@", StringComparison.Ordinal) -> Ok(currentIndex, acc, remaining)
                            | next :: tailLines when next.StartsWith("\\", StringComparison.Ordinal) -> applyHunk currentIndex acc tailLines
                            | next :: tailLines when next.StartsWith("+", StringComparison.Ordinal) ->
                                applyHunk currentIndex (tail next :: acc) tailLines
                            | next :: tailLines when next.StartsWith("-", StringComparison.Ordinal) ->
                                if currentIndex >= original.Length then
                                    Error(Client.ValidationError "Patch removal extends beyond the end of the file.")
                                elif original[currentIndex] <> tail next then
                                    Error(Client.ValidationError $"Patch removal mismatch at line {currentIndex + 1}.")
                                else
                                    applyHunk (currentIndex + 1) acc tailLines
                            | next :: tailLines when next.StartsWith(" ", StringComparison.Ordinal) ->
                                if currentIndex >= original.Length then
                                    Error(Client.ValidationError "Patch context extends beyond the end of the file.")
                                elif original[currentIndex] <> tail next then
                                    Error(Client.ValidationError $"Patch context mismatch at line {currentIndex + 1}.")
                                else
                                    applyHunk (currentIndex + 1) (tail next :: acc) tailLines
                            | next :: _ -> Error(Client.ValidationError $"Invalid patch line: {next}")

                        match applyHunk targetIndex [] rest with
                        | Error error -> Error error
                        | Ok(nextOldIndex, hunkOutput, remaining) ->
                            applyHunks nextOldIndex (output @ unchanged @ List.rev hunkOutput) remaining
            | line :: _ -> Error(Client.ValidationError $"Unexpected patch content outside hunk: {line}")

        patchLines
        |> skipHeaders
        |> applyHunks 0 []
        |> Result.map (String.concat ending)

module private Core =
    let private isPathInRoot (root: string) (fullPath: string) =
        let normalize (path: string) =
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/')

        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

        let normalizedRoot = normalize root
        let normalizedPath = normalize fullPath

        normalizedPath.Equals(normalizedRoot, comparison)
        || normalizedPath.StartsWith(normalizedRoot + "/", comparison)

    let private getFullPath (paths: string seq) : IO<'a, string> =
        effect {
            let! (ProjectDirectory root) = ProjectIO.root
            let! fullPath = paths |> Path.combineAll |> Path.combine root |> FileIO.getFullPath

            match fullPath |> isPathInRoot root with
            | true -> return fullPath
            | false -> return! "Path not in project" |> Client.PermissionDenied |> Effect.ofError
        }

    let private listMap f items =
        fun rt -> items |> Seq.map (f >> (fun x -> x rt)) |> Ok

    let listCommands _ = Ok AgentProtocol.listCommandsResult

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
        ProjectPaths.resolveProjectFolder projectName path

    let private parseFilePath (path: string) (ProjectName projectName) =
        ProjectPaths.resolveProjectFile projectName path

    let private resolveWritableFilePath (path: string) (ProjectName projectName) =
        ProjectPaths.resolveWritableProjectFile projectName path

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

            let toItem file =
                fun rt ->
                    let name = FileIO.getFileName file rt

                    match FileIO.getFileInfo file rt with
                    | Ok info ->
                        match ProjectItemKind.ofFileInfo info with
                        | ProjectFileError _ -> Ok(ProjectFile(name, 0L, DateTimeOffset.MinValue, DateTimeOffset.MinValue))
                        | item -> Ok item
                    | Error _ -> Ok(ProjectFile(name, 0L, DateTimeOffset.MinValue, DateTimeOffset.MinValue))

            let! items =
                fun rt ->
                    files
                    |> Seq.map (fun file -> toItem file rt)
                    |> Seq.choose Result.toOption
                    |> Seq.toList
                    |> Ok

            return items
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

        projectName |> parseProjectName >>= parseFolderPath "" >>= projectFilesInfo

    let listProjectDirectory projectName folderPath =
        projectName |> parseProjectName >>= getItems folderPath

    let private combineResults results =
        results
        |> List.fold
            (fun state item ->
                match state, item with
                | Ok values, Ok value -> Ok(value @ values)
                | Error error, _ -> Error error
                | _, Error error -> Error error)
            (Ok [])
        |> Result.map List.rev

    let rec private collectItems currentPath projectName =
        effect {
            let! items = getItems currentPath projectName

            let combinePath name =
                if String.IsNullOrWhiteSpace currentPath then name else Path.Combine(currentPath, name)

            let localItems =
                items
                |> List.map (function
                    | ProjectFile(name, size, created, modified) -> ProjectFile(combinePath name, size, created, modified)
                    | ProjectFolder name -> ProjectFolder(combinePath name)
                    | ProjectFileError(name, error) -> ProjectFileError(combinePath name, error))

            let folders =
                items
                |> List.choose (function
                    | ProjectFolder name -> Some(combinePath name)
                    | _ -> None)

            let! nested =
                fun rt ->
                    folders
                    |> List.map (fun folder -> collectItems folder projectName rt)
                    |> combineResults

            return localItems @ nested
        }

    let searchFiles projectName folderPath (query: string) maxResults =
        let filter items =
            let take = defaultArg maxResults Int32.MaxValue
            items
            |> List.choose (function
                | ProjectFile(name, size, created, modified) when name.Contains(query, StringComparison.OrdinalIgnoreCase) -> Some(ProjectFile(name, size, created, modified))
                | ProjectFolder name when name.Contains(query, StringComparison.OrdinalIgnoreCase) -> Some(ProjectFolder name)
                | _ -> None)
            |> List.truncate take

        parseProjectName projectName >>= collectItems (defaultArg folderPath "") |>> filter

    let readFile projectName filePath =
        parseProjectName projectName >>= parseFilePath filePath >>= FileIO.readAllText

    let searchText projectName folderPath (query: string) maxResults =
        let searchFile projectName item =
            match item with
            | ProjectFile(path, _, _, _) ->
                fun rt ->
                    match readFile projectName path rt with
                    | Ok(Content content) when content.Contains(query, StringComparison.OrdinalIgnoreCase) -> Ok(Some path)
                    | Ok _ -> Ok None
                    | Error _ -> Ok None
            | _ -> fun _ -> Ok None

        effect {
            let! projectName = parseProjectName projectName
            let! items = collectItems (defaultArg folderPath "") projectName
            let! matches =
                fun rt ->
                    items
                    |> Seq.map (fun item -> searchFile (let (ProjectName name) = projectName in name) item rt)
                    |> Seq.choose Result.toOption
                    |> Seq.choose id
                    |> Ok

            return matches |> Seq.truncate (defaultArg maxResults Int32.MaxValue) |> Seq.toList
        }

    let readFiles projectName filePaths =
        let readFile path projectName =
            parseFilePath path projectName >>= FileIO.readAllText
            |>> (fun (Content x) -> Content'.Text x)
            |> Effect.defaultWith Content'.Error

        let readFiles filePaths projectName =
            fun rt -> seq { for path in filePaths -> readFile path projectName rt } |> Ok

        parseProjectName projectName >>= readFiles filePaths

    let private verifyExpectedHash expectedHash path =
        match expectedHash with
        | None -> fun _ -> Ok()
        | Some expected ->
            effect {
                let! current = FileIO.readAllText path
                let (Content content) = current
                let actual = Hash.sha256 content
                if actual = expected then
                    return ()
                else
                    return! Client.ValidationError $"Expected hash {expected}, actual hash {actual}." |> Effect.ofError
            }

    let writeFile projectName filePath content mode expectedHash =
        let write =
            match mode with
            | Append -> FileIO.appendAllText
            | Write -> FileIO.writeAllText

        effect {
            let! path = parseProjectName projectName >>= resolveWritableFilePath filePath
            do! verifyExpectedHash expectedHash path
            do! write path (Content content)
        }

    let patchFile projectName filePath expectedHash format patch =
        match format with
        | UnifiedDiff ->
            effect {
                let! path = parseProjectName projectName >>= parseFilePath filePath
                let! current = FileIO.readAllText path
                let (Content content) = current
                do! verifyExpectedHash expectedHash path
                let! patched =
                    fun _ -> Patch.applyUnifiedDiff patch content

                do! FileIO.writeAllText path (Content patched)
                return Content patched
            }

let listCommands rt = Core.listCommands rt
let listProjects rt = Core.listProjects rt

let getProjectDetails (cmd: GetProjectDetailsCommand) : IO<'rt, (string * Content) list> =
    Core.getProjectDetails cmd.ProjectName

let listDirectory (cmd: ListDirectoryCommand) : IO<'rt, ProjectItemKind list> =
    Core.listProjectDirectory cmd.ProjectName cmd.FolderPath

let searchFiles (cmd: SearchFilesCommand) : IO<'rt, ProjectItemKind list> =
    Core.searchFiles cmd.ProjectName cmd.FolderPath cmd.Query cmd.MaxResults

let searchText (cmd: SearchTextCommand) : IO<'rt, string list> =
    Core.searchText cmd.ProjectName cmd.FolderPath cmd.Query cmd.MaxResults

let readFile (cmd: ReadFileCommand) : IO<'rt, Content> =
    Core.readFile cmd.ProjectName cmd.FilePath

let readFiles (cmd: ReadFilesCommand) : IO<'rt, Content' seq> =
    Core.readFiles cmd.ProjectName cmd.FilePaths

let writeFile (cmd: WriteFileCommand) : IO<'rt, unit> =
    Core.writeFile cmd.ProjectName cmd.FilePath cmd.Content cmd.FileWriteMode cmd.ExpectedHash

let patchFile (cmd: PatchFileCommand) : IO<'rt, Content> =
    Core.patchFile cmd.ProjectName cmd.FilePath cmd.ExpectedHash cmd.Format cmd.Patch
