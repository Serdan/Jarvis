module Client.ProjectBrowser

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
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
    type ApplyResult =
        { Content: string
          HunksApplied: int
          ChangedLines: int
          Diagnostics: PatchHunkDiagnostic list }

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

    let private nearbyContext lineIndex (lines: string list) =
        let index = lineIndex |> max 0 |> min lines.Length
        let start = max 0 (index - 3)
        lines |> List.skip start |> List.truncate 7

    let private expectedContext (hunkLines: string list) =
        hunkLines
        |> List.choose (fun line ->
            if line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("-", StringComparison.Ordinal) then
                Some(tail line)
            else
                None)
        |> List.truncate 8

    let private diagnostic hunkIndex status originalStart appliedStart message expected actual =
        { HunkIndex = hunkIndex
          Status = status
          OriginalStartLine = originalStart
          AppliedStartLine = appliedStart
          Message = message
          ExpectedContext = expected
          ActualContext = actual }

    let private failWith (diagnostic: PatchHunkDiagnostic) =
        let expected =
            if diagnostic.ExpectedContext.IsEmpty then "<none>"
            else diagnostic.ExpectedContext |> String.concat "\n"

        let actual =
            if diagnostic.ActualContext.IsEmpty then "<none>"
            else diagnostic.ActualContext |> String.concat "\n"

        Client.ValidationError $"Patch hunk {diagnostic.HunkIndex} failed: {diagnostic.Message}\nExpected context:\n{expected}\nActual context:\n{actual}"
        |> Error

    let private changedLineCount (lines: string list) =
        lines
        |> List.filter (fun line ->
            (line.StartsWith("+", StringComparison.Ordinal) && not (line.StartsWith("+++", StringComparison.Ordinal)))
            || (line.StartsWith("-", StringComparison.Ordinal) && not (line.StartsWith("---", StringComparison.Ordinal))))
        |> List.length

    let private collectHunk (lines: string list) =
        let rec loop acc remaining =
            match remaining with
            | [] -> List.rev acc, []
            | [ "" ] -> List.rev acc, []
            | next :: _ when next.StartsWith("@@", StringComparison.Ordinal) -> List.rev acc, remaining
            | next :: tailLines -> loop (next :: acc) tailLines

        loop [] lines

    let private canMatchAt (original: string list) startIndex (hunkLines: string list) =
        let rec loop currentIndex hasEvidence (remaining: string list) =
            match remaining with
            | [] -> Ok hasEvidence
            | next :: tailLines when next.StartsWith("\\", StringComparison.Ordinal) -> loop currentIndex hasEvidence tailLines
            | next :: tailLines when next.StartsWith("+", StringComparison.Ordinal) -> loop currentIndex hasEvidence tailLines
            | next :: tailLines when next.StartsWith("-", StringComparison.Ordinal) ->
                if currentIndex >= original.Length then Error "Patch removal extends beyond the end of the file."
                elif original[currentIndex] <> tail next then Error $"Patch removal mismatch at line {currentIndex + 1}."
                else loop (currentIndex + 1) true tailLines
            | next :: tailLines when next.StartsWith(" ", StringComparison.Ordinal) ->
                if currentIndex >= original.Length then Error "Patch context extends beyond the end of the file."
                elif original[currentIndex] <> tail next then Error $"Patch context mismatch at line {currentIndex + 1}."
                else loop (currentIndex + 1) true tailLines
            | next :: _ -> Error $"Invalid patch line: {next}"

        if startIndex < 0 || startIndex > original.Length then
            Error $"Patch target index {startIndex + 1} is outside the file."
        else
            loop startIndex false hunkLines

    let private applyHunkAt (hunkLines: string list) startIndex =
        let rec loop currentIndex acc (remaining: string list) =
            match remaining with
            | [] -> Ok(currentIndex, acc)
            | next :: tailLines when next.StartsWith("\\", StringComparison.Ordinal) -> loop currentIndex acc tailLines
            | next :: tailLines when next.StartsWith("+", StringComparison.Ordinal) -> loop currentIndex (tail next :: acc) tailLines
            | next :: tailLines when next.StartsWith("-", StringComparison.Ordinal) -> loop (currentIndex + 1) acc tailLines
            | next :: tailLines when next.StartsWith(" ", StringComparison.Ordinal) -> loop (currentIndex + 1) (tail next :: acc) tailLines
            | next :: _ -> Error(Client.ValidationError $"Invalid patch line: {next}")

        loop startIndex [] hunkLines

    let private findFuzzyCandidates oldIndex targetIndex fuzzyContextLines (original: string list) (hunkLines: string list) =
        let searchStart = max oldIndex (targetIndex - fuzzyContextLines)
        let searchEnd = min original.Length (targetIndex + fuzzyContextLines)

        [ searchStart .. searchEnd ]
        |> List.choose (fun candidate ->
            match canMatchAt original candidate hunkLines with
            | Ok true -> Some candidate
            | _ -> None)

    let applyUnifiedDiff fuzzyContextLines (patch: string) (content: string) : Client.Result<ApplyResult> =
        let ending = detectLineEnding content
        let original = splitLines content
        let patchLines = splitLines patch

        let rec skipHeaders (lines: string list) =
            match lines with
            | [] -> []
            | line :: _ when line.StartsWith("@@", StringComparison.Ordinal) -> lines
            | _ :: rest -> skipHeaders rest

        let rec applyHunks hunkIndex oldIndex output diagnostics changedLines (lines: string list) =
            match lines with
            | [] ->
                if oldIndex > original.Length then
                    diagnostic hunkIndex Failed None None "Patch consumed beyond the end of the file." [] [] |> failWith
                else
                    Ok
                        { Content = String.concat ending (output @ (original |> List.skip oldIndex))
                          HunksApplied = hunkIndex
                          ChangedLines = changedLines
                          Diagnostics = List.rev diagnostics }
            | header :: rest when header.StartsWith("@@", StringComparison.Ordinal) ->
                match parseOldStart header with
                | None ->
                    diagnostic (hunkIndex + 1) Failed None None $"Invalid patch hunk header: {header}" [] [] |> failWith
                | Some oldStart ->
                    let currentHunkIndex = hunkIndex + 1
                    let targetIndex = max 0 (oldStart - 1)
                    let hunkLines, remaining = collectHunk rest
                    let expected = expectedContext hunkLines

                    if targetIndex < oldIndex || targetIndex > original.Length then
                        diagnostic currentHunkIndex Failed (Some oldStart) None $"Patch hunk targets invalid line {oldStart}." expected (nearbyContext targetIndex original) |> failWith
                    else
                        let strictMatch = canMatchAt original targetIndex hunkLines

                        let placement =
                            match strictMatch with
                            | Ok true -> Ok(targetIndex, AppliedStrict, "Applied strictly.")
                            | Ok false ->
                                diagnostic currentHunkIndex Failed (Some oldStart) (Some oldStart) "Patch hunk has no context or removal lines; fuzzy matching requires evidence." expected (nearbyContext targetIndex original) |> failWith
                            | Error strictError when fuzzyContextLines <= 0 ->
                                diagnostic currentHunkIndex Failed (Some oldStart) (Some oldStart) strictError expected (nearbyContext targetIndex original) |> failWith
                            | Error strictError ->
                                match findFuzzyCandidates oldIndex targetIndex fuzzyContextLines original hunkLines with
                                | [ candidate ] ->
                                    let offset = candidate - targetIndex
                                    Ok(candidate, AppliedWithOffset offset, $"Applied with offset {offset}.")
                                | [] ->
                                    diagnostic currentHunkIndex Failed (Some oldStart) (Some oldStart) $"{strictError} No valid fuzzy match found within {fuzzyContextLines} lines." expected (nearbyContext targetIndex original) |> failWith
                                | candidates ->
                                    let candidateLines = candidates |> List.map (fun x -> string (x + 1)) |> String.concat ", "
                                    diagnostic currentHunkIndex Failed (Some oldStart) None $"Ambiguous fuzzy match. Candidate lines: {candidateLines}." expected (nearbyContext targetIndex original) |> failWith

                        match placement with
                        | Error error -> Error error
                        | Ok(appliedIndex, status, message) ->
                            let unchanged = original |> List.skip oldIndex |> List.take (appliedIndex - oldIndex)

                            match applyHunkAt hunkLines appliedIndex with
                            | Error error -> Error error
                            | Ok(nextOldIndex, hunkOutput) ->
                                let appliedLine = appliedIndex + 1
                                let diag = diagnostic currentHunkIndex status (Some oldStart) (Some appliedLine) message expected []
                                applyHunks currentHunkIndex nextOldIndex (output @ unchanged @ List.rev hunkOutput) (diag :: diagnostics) (changedLines + changedLineCount hunkLines) remaining
            | line :: _ ->
                diagnostic (hunkIndex + 1) Failed None None $"Unexpected patch content outside hunk: {line}" [] [] |> failWith

        patchLines
        |> skipHeaders
        |> applyHunks 0 0 [] [] 0

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

    let private validateSkillName (skillName: string) =
        if String.IsNullOrWhiteSpace skillName then
            Error(Client.ValidationError "Skill name cannot be empty.")
        elif skillName = "." || skillName = ".." then
            Error(Client.ValidationError "Skill name is invalid.")
        elif skillName.Contains('/') || skillName.Contains('\\') then
            Error(Client.ValidationError "Skill name must be a single directory name.")
        else
            Ok(skillName.Trim())

    let private skillPath skillName fileName =
        Path.Combine(".jarvis", "skills", skillName, fileName)

    let private readSkillContent parsedProject skillName =
        fun rt ->
            let rec tryCandidates candidates =
                match candidates with
                | [] ->
                    Error(NotFoundError $"Skill '{skillName}' does not contain SKILL.md or skill.md.")
                | relativePath :: rest ->
                    match parseFilePath relativePath parsedProject rt with
                    | Error(NotFoundError _) -> tryCandidates rest
                    | Error error -> Error error
                    | Ok filePath ->
                        match FileIO.readAllText filePath rt with
                        | Ok content -> Ok content
                        | Error(NotFoundError _) -> tryCandidates rest
                        | Error error -> Error error

            tryCandidates
                [ skillPath skillName "SKILL.md"
                  skillPath skillName "skill.md" ]

    let private skillDescription (Content content) =
        content.Replace("\r\n", "\n").Split('\n')
        |> Seq.map (fun line -> line.Trim())
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.tryFind (fun line ->
            not (line.StartsWith("#", StringComparison.Ordinal))
            && line <> "---")
        |> Option.map (fun line ->
            if line.Length <= 240 then line
            else line.Substring(0, 239) + "…")

    let listSkills projectName =
        fun rt ->
            match parseProjectName projectName rt with
            | Error error -> Error error
            | Ok parsedProject ->
                let skillsPath = Path.Combine(".jarvis", "skills")

                match parseFolderPath skillsPath parsedProject rt with
                | Error(NotFoundError _) -> Ok { Skills = [] }
                | Error error -> Error error
                | Ok skillsFolder ->
                    match FileIO.getChildFolders skillsFolder rt with
                    | Error(NotFoundError _) -> Ok { Skills = [] }
                    | Error error -> Error error
                    | Ok folders ->
                        let skills =
                            folders
                            |> Seq.choose (fun folder ->
                                let name = FileIO.getFolderName folder rt

                                match validateSkillName name with
                                | Error _ -> None
                                | Ok validated ->
                                    match readSkillContent parsedProject validated rt with
                                    | Ok content ->
                                        Some
                                            { Name = validated
                                              Description = skillDescription content }
                                    | Error(NotFoundError _) -> None
                                    | Error _ -> None)
                            |> Seq.sortWith (fun left right ->
                                StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name))
                            |> Seq.toList

                        Ok { Skills = skills }

    let getSkill projectName skillName =
        fun rt ->
            match validateSkillName skillName with
            | Error error -> Error error
            | Ok validated ->
                match parseProjectName projectName rt with
                | Error error -> Error error
                | Ok parsedProject ->
                    match readSkillContent parsedProject validated rt with
                    | Error error -> Error error
                    | Ok(Content content) ->
                        Ok
                            { Name = validated
                              Content = content }

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

    let readFileWithOptions projectName filePath startLine endLine includeLineNumbers =
        let includeLineNumbers = defaultArg includeLineNumbers false

        match startLine, endLine, includeLineNumbers with
        | None, None, false -> readFile projectName filePath
        | _ ->
            effect {
                let startLine = defaultArg startLine 1

                if startLine < 1 then
                    return! Client.ValidationError "StartLine must be greater than zero." |> Effect.ofError
                elif endLine |> Option.exists (fun value -> value < startLine) then
                    return! Client.ValidationError "EndLine must be greater than or equal to StartLine." |> Effect.ofError
                else
                    let! parsedProject = parseProjectName projectName
                    let! path = parseFilePath filePath parsedProject
                    let! lines = FileIO.readLines path startLine endLine

                    let content =
                        lines
                        |> List.map (fun (lineNumber, line) ->
                            if includeLineNumbers then $"{lineNumber}\t{line}" else line)
                        |> String.concat Environment.NewLine

                    return Content content
            }

    let private compileGlob (glob: string) =
        let normalized = glob.Replace('\\', '/')
        let matchFileNameOnly = not (normalized.Contains('/'))
        let pattern = StringBuilder("^")
        let mutable index = 0

        while index < normalized.Length do
            match normalized[index] with
            | '*' when index + 1 < normalized.Length && normalized[index + 1] = '*' ->
                if index + 2 < normalized.Length && normalized[index + 2] = '/' then
                    pattern.Append("(?:.*/)?") |> ignore
                    index <- index + 3
                else
                    pattern.Append(".*") |> ignore
                    index <- index + 2
            | '*' ->
                pattern.Append("[^/]*") |> ignore
                index <- index + 1
            | '?' ->
                pattern.Append("[^/]") |> ignore
                index <- index + 1
            | character ->
                pattern.Append(Regex.Escape(string character)) |> ignore
                index <- index + 1

        pattern.Append("$") |> ignore

        let options =
            if OperatingSystem.IsWindows() then RegexOptions.IgnoreCase
            else RegexOptions.None

        let regex = Regex(pattern.ToString(), options)

        fun (path: string) ->
            let normalizedPath = path.Replace('\\', '/')
            let candidate =
                if matchFileNameOnly then Path.GetFileName normalizedPath
                else normalizedPath
            regex.IsMatch candidate

    let searchText projectName folderPath (query: string) includeGlobs excludeGlobs maxResults =
        let includeMatchers = includeGlobs |> List.map compileGlob
        let excludeMatchers = excludeGlobs |> List.map compileGlob
        let take = defaultArg maxResults Int32.MaxValue

        let shouldSearch path =
            (includeMatchers.IsEmpty || includeMatchers |> List.exists (fun matches -> matches path))
            && not (excludeMatchers |> List.exists (fun matches -> matches path))

        effect {
            if String.IsNullOrEmpty query then
                return! Client.ValidationError "Search query cannot be empty." |> Effect.ofError
            elif take <= 0 then
                return! Client.ValidationError "MaxResults must be greater than zero." |> Effect.ofError
            else
                let! parsedProject = parseProjectName projectName

                let searchFile relativePath remaining rt =
                    match parseFilePath relativePath parsedProject rt with
                    | Error _ -> Ok []
                    | Ok filePath ->
                        match FileIO.searchText filePath query remaining rt with
                        | Error _ -> Ok []
                        | Ok matches ->
                            matches
                            |> List.map (fun (line, column, preview) ->
                                { FilePath = relativePath
                                  Line = line
                                  Column = column
                                  Preview = preview })
                            |> Ok

                let rec searchFolder currentPath remaining rt =
                    if remaining <= 0 then
                        Ok []
                    else
                        match getItems currentPath parsedProject rt with
                        | Error error -> Error error
                        | Ok items ->
                            let qualify name =
                                if String.IsNullOrWhiteSpace currentPath then name
                                else Path.Combine(currentPath, name)

                            let files =
                                items
                                |> List.choose (function
                                    | ProjectFile(name, _, _, _) -> Some(qualify name)
                                    | _ -> None)

                            let rec searchFiles remaining acc pending =
                                match pending with
                                | [] -> Ok acc
                                | _ when remaining <= 0 -> Ok acc
                                | path :: rest when not (shouldSearch path) ->
                                    searchFiles remaining acc rest
                                | path :: rest ->
                                    match searchFile path remaining rt with
                                    | Error _ -> searchFiles remaining acc rest
                                    | Ok matches ->
                                        searchFiles (remaining - matches.Length) (acc @ matches) rest

                            match searchFiles remaining [] files with
                            | Error error -> Error error
                            | Ok localMatches ->
                                let remainingAfterLocal = remaining - localMatches.Length

                                if remainingAfterLocal <= 0 then
                                    Ok localMatches
                                else
                                    let folders =
                                        items
                                        |> List.choose (function
                                            | ProjectFolder name -> Some(qualify name)
                                            | _ -> None)

                                    let rec searchFolders remaining acc pending =
                                        match pending with
                                        | [] -> Ok acc
                                        | _ when remaining <= 0 -> Ok acc
                                        | folder :: rest ->
                                            match searchFolder folder remaining rt with
                                            | Error error -> Error error
                                            | Ok matches ->
                                                searchFolders (remaining - matches.Length) (acc @ matches) rest

                                    match searchFolders remainingAfterLocal [] folders with
                                    | Error error -> Error error
                                    | Ok nested -> Ok(localMatches @ nested)

                return! fun rt -> searchFolder (defaultArg folderPath "") take rt
        }

    let readFiles projectName filePaths =
        let readFile path projectName =
            parseFilePath path projectName >>= FileIO.readAllText
            |>> (fun (Content x) -> Content'.Text x)
            |> Effect.defaultWith Content'.Error

        let readFiles filePaths projectName =
            fun rt -> seq { for path in filePaths -> readFile path projectName rt } |> Ok

        parseProjectName projectName >>= readFiles filePaths

    let readImage projectName filePath =
        let detectMimeType (path: string) =
            match Path.GetExtension(path).ToLowerInvariant() with
            | ".png" -> Ok "image/png"
            | ".jpg"
            | ".jpeg" -> Ok "image/jpeg"
            | ".webp" -> Ok "image/webp"
            | extension ->
                Error(Client.ValidationError $"Unsupported image type '{extension}'. Supported types are PNG, JPEG, and WebP.")

        effect {
            let! parsedProject = parseProjectName projectName
            let! path = parseFilePath filePath parsedProject
            let! info = FileIO.getFileInfo path

            if info.Length > int64 AgentProtocol.maxImageBytes then
                return!
                    Client.ValidationError
                        $"Image is {info.Length} bytes and exceeds the {AgentProtocol.maxImageBytes} byte limit."
                    |> Effect.ofError
            else
                let! mimeType = fun _ -> detectMimeType filePath
                let! data = FileIO.readAllBytes path
                return
                    { FilePath = filePath
                      MimeType = mimeType
                      Data = data }
        }

    let private verifyExpectedHash expectedHash content =
        let actual = Hash.sha256 content
        match expectedHash with
        | None -> Ok actual
        | Some expected when expected = actual -> Ok actual
        | Some expected -> Error(Client.ValidationError $"Expected hash {expected}, actual hash {actual}.")

    let private rejectBinaryContent (content: string) =
        if content.Contains('\u0000') then
            Error(Client.ValidationError "PatchFile does not support binary files.")
        else
            Ok()

    let writeFile projectName filePath content mode expectedHash createParents =
        let write =
            match mode with
            | Append -> FileIO.appendAllText
            | Write -> FileIO.writeAllText

        let ensureParent (FilePath fullPath) =
            effect {
                if defaultArg createParents false then
                    let parent = Path.GetDirectoryName(fullPath)
                    do! FileIO.createDirectory (FolderPath parent)
            }

        effect {
            let! path = parseProjectName projectName >>= resolveWritableFilePath filePath

            match expectedHash with
            | Some _ ->
                let! existing = FileIO.readAllText path
                let (Content existingContent) = existing
                let! _ = fun _ -> verifyExpectedHash expectedHash existingContent
                do! ensureParent path
                do! write path (Content content)
            | None ->
                do! ensureParent path
                do! write path (Content content)
        }

    let patchFile projectName filePath expectedHash format patch dryRun fuzzyContextLines returnContent =
        match format with
        | UnifiedDiff ->
            effect {
                let dryRun = defaultArg dryRun false
                let fuzzyContextLines = defaultArg fuzzyContextLines AgentProtocol.defaultPatchFuzzyContextLines
                let returnContent = defaultArg returnContent false

                if fuzzyContextLines < 0 || fuzzyContextLines > 50 then
                    return! Client.ValidationError "FuzzyContextLines must be between 0 and 50." |> Effect.ofError
                else
                    let! path = parseProjectName projectName >>= parseFilePath filePath
                    let! current = FileIO.readAllText path
                    let (Content content) = current
                    let! beforeHash = fun _ -> verifyExpectedHash expectedHash content
                    do! fun _ -> rejectBinaryContent content
                    let! applied = fun _ -> Patch.applyUnifiedDiff fuzzyContextLines patch content
                    let afterHash = Hash.sha256 applied.Content

                    do!
                        if dryRun then
                            fun _ -> Ok()
                        else
                            FileIO.writeAllText path (Content applied.Content)

                    return
                        { Applied = true
                          DryRun = dryRun
                          FilePath = filePath
                          HunksApplied = applied.HunksApplied
                          ChangedLines = applied.ChangedLines
                          BeforeHash = beforeHash
                          AfterHash = Some afterHash
                          Content = if returnContent then Some applied.Content else None
                          Diagnostics = applied.Diagnostics }
            }

let listCommands rt = Core.listCommands rt
let listProjects rt = Core.listProjects rt

let getProjectDetails (cmd: GetProjectDetailsCommand) : IO<'rt, (string * Content) list> =
    Core.getProjectDetails cmd.ProjectName

let listSkills (cmd: ListSkillsCommand) : IO<'rt, ListSkillsResult> =
    Core.listSkills cmd.ProjectName

let getSkill (cmd: GetSkillCommand) : IO<'rt, GetSkillResult> =
    Core.getSkill cmd.ProjectName cmd.SkillName

let listDirectory (cmd: ListDirectoryCommand) : IO<'rt, ProjectItemKind list> =
    Core.listProjectDirectory cmd.ProjectName cmd.FolderPath

let searchFiles (cmd: SearchFilesCommand) : IO<'rt, ProjectItemKind list> =
    Core.searchFiles cmd.ProjectName cmd.FolderPath cmd.Query cmd.MaxResults

let searchText (cmd: SearchTextCommand) : IO<'rt, SearchTextMatch list> =
    Core.searchText cmd.ProjectName cmd.FolderPath cmd.Query cmd.IncludeGlobs cmd.ExcludeGlobs cmd.MaxResults

let readFile (cmd: ReadFileCommand) : IO<'rt, Content> =
    Core.readFileWithOptions cmd.ProjectName cmd.FilePath cmd.StartLine cmd.EndLine cmd.IncludeLineNumbers

let readFiles (cmd: ReadFilesCommand) : IO<'rt, Content' seq> =
    Core.readFiles cmd.ProjectName cmd.FilePaths

let readImage (cmd: ReadImageCommand) : IO<'rt, ReadImageResult> =
    Core.readImage cmd.ProjectName cmd.FilePath

let writeFile (cmd: WriteFileCommand) : IO<'rt, unit> =
    Core.writeFile cmd.ProjectName cmd.FilePath cmd.Content cmd.FileWriteMode cmd.ExpectedHash cmd.CreateParents

let patchFile (cmd: PatchFileCommand) : IO<'rt, PatchFileResult> =
    Core.patchFile cmd.ProjectName cmd.FilePath cmd.ExpectedHash cmd.Format cmd.Patch cmd.DryRun cmd.FuzzyContextLines cmd.ReturnContent
