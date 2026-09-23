module ProjectBrowserTests

open System.IO
open Client
open Client.Effect
open Client.ProjectBrowser
open Common
open NUnit.Framework
open FsUnitTyped

let readmeContent =
    "Project 1 Readme\n# Start Config\nOld Text\n# End Config\n# Section Header\nBody\n# Section Footer\n"

let todoContent = "Project 1 Todo"

let fakeFileOperations =
    { getFullPath = _.Replace('\\', '/') >> Ok
      ReadAllText =
        fun (FilePath filePath) ->
            match filePath with
            | "/fake/projects/Project1/readme.md" -> Ok(Content readmeContent)
            | "/fake/projects/Project1/todo.md" -> Ok(Content todoContent)
            | "/fake/projects/Project1/src/deep.md" -> Ok(Content "Nested Old Text")
            | _ -> Error(NotFoundError "File not found")

      ReadLines =
        fun (FilePath filePath) startLine endLine ->
            let content =
                match filePath with
                | "/fake/projects/Project1/readme.md" -> Some readmeContent
                | "/fake/projects/Project1/todo.md" -> Some todoContent
                | "/fake/projects/Project1/src/deep.md" -> Some "Nested Old Text"
                | _ -> None

            match content with
            | None -> Error(NotFoundError "File not found")
            | Some content ->
                content.Replace("\r\n", "\n").Split('\n')
                |> Seq.mapi (fun index line -> index + 1, line)
                |> Seq.filter (fun (lineNumber, _) -> lineNumber >= startLine)
                |> Seq.filter (fun (lineNumber, _) -> endLine |> Option.forall (fun lastLine -> lineNumber <= lastLine))
                |> Seq.toList
                |> Ok

      SearchText =
        fun (FilePath filePath) query maxResults ->
            let content =
                match filePath with
                | "/fake/projects/Project1/readme.md" -> Some readmeContent
                | "/fake/projects/Project1/todo.md" -> Some todoContent
                | "/fake/projects/Project1/src/deep.md" -> Some "Nested Old Text"
                | _ -> None

            match content with
            | None -> Error(NotFoundError "File not found")
            | Some content ->
                content.Replace("\r\n", "\n").Split('\n')
                |> Seq.mapi (fun lineIndex line -> lineIndex + 1, line)
                |> Seq.collect (fun (lineNumber, line) ->
                    seq {
                        let mutable searchIndex = 0
                        let mutable searching = true

                        while searching do
                            let index = line.IndexOf(query, searchIndex, System.StringComparison.OrdinalIgnoreCase)

                            if index < 0 then
                                searching <- false
                            else
                                yield lineNumber, index + 1, line
                                searchIndex <- index + max 1 query.Length
                    })
                |> Seq.truncate maxResults
                |> Seq.toList
                |> Ok

      WriteAllText = fun _ _ -> Ok()
      CreateDirectory = fun _ -> Ok()

      parseFile =
        fun path ->
            match path with
            | "/fake/projects/Project1/readme.md"
            | "/fake/projects/Project1/todo.md"
            | "/fake/projects/Project1/src/deep.md" -> Ok(FilePath path)
            | _ -> Error(NotFoundError $"File does not exist: {path}")

      CopyFile = fun _ _ _ -> Ok()
      AppendAllText = fun _ _ -> Ok()

      parseFolder =
        fun path ->
            match path with
            | "/fake/projects/Project1" -> Ok(FolderPath path)
            | "/fake/projects/Project1/src" -> Ok(FolderPath path)
            | "/fake/projects" -> Ok(FolderPath path)
            | _ -> Error(NotFoundError path)

      GetFiles =
        fun (FolderPath folderPath) ->
            match folderPath with
            | "/fake/projects/Project1" ->
                Ok(
                    Seq.ofList
                        [ FilePath "/fake/projects/Project1/readme.md"
                          FilePath "/fake/projects/Project1/todo.md" ]
                )
            | "/fake/projects/Project1/src" -> Ok(Seq.ofList [ FilePath "/fake/projects/Project1/src/deep.md" ])
            | _ -> Error(NotFoundError folderPath)

      getChildFolders =
        fun (FolderPath folderPath) ->
            match folderPath with
            | "/fake/projects" ->
                Ok(Seq.ofList [ FolderPath "/fake/projects/Project1"; FolderPath "/fake/projects/Project2" ])
            | "/fake/projects/Project1" -> Ok(Seq.ofList [ FolderPath "/fake/projects/Project1/src" ])
            | "/fake/projects/Project1/src" -> Ok Seq.empty
            | _ -> Error(NotFoundError "Folder not found")

      GetFileInfo =
        fun (FilePath filePath) ->
            match filePath with
            | "/fake/projects/Project1/readme.md" -> Ok(FileInfo filePath)
            | "/fake/projects/Project1/todo.md" -> Ok(FileInfo filePath)
            | _ -> Error(NotFoundError "File not found")

      GetFolderName = fun (FolderPath folderPath) -> Path.GetFileName folderPath
      getFileName = fun (FilePath filePath) -> Path.GetFileName filePath }

let fakeProjectData =
    { Root = ProjectDirectory("/fake/projects")
      SpecialFiles = [ "readme.md"; "todo.md" ]
      FolderFilters = [ (fun folder -> folder <> "bin"); (fun folder -> folder <> "obj") ] }

type FakeContext() =
    interface ProjectIO with
        member this.Project = fakeProjectData

    interface FileIO with
        member this.File = fakeFileOperations

let fakeContext = FakeContext()

[<Test>]
let ``listCommands returns protocol 2 capabilities`` () =
    let result = listCommands fakeContext

    match result with
    | Ok commands ->
        commands.ProtocolVersion |> shouldEqual "2.8"
        let capability name =
            commands.Commands
            |> List.find (fun command -> command.Name = name)

        capability "PatchFile" |> _.SupportsDryRun |> shouldEqual true
        capability "WriteFile" |> _.SupportsDryRun |> shouldEqual false
        capability "RunCommand" |> _.SupportsDryRun |> shouldEqual false
        capability "ListProjectTasks" |> _.Permissions |> shouldEqual [ ReadOnly ]
        capability "RunProjectTask" |> _.Permissions |> shouldEqual [ ProcessExecution ]
        capability "GitCommit" |> _.SupportsDryRun |> shouldEqual false
        capability "StartJob" |> _.SupportsDryRun |> shouldEqual false
        capability "ReadFile" |> _.MaxOutputBytes |> shouldEqual (Some AgentProtocol.maxResponseBytes)
        capability "ReadFiles" |> _.MaxOutputBytes |> shouldEqual (Some AgentProtocol.maxResponseBytes)
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {EffectError.toString e}")

[<Test>]
let ``listProjects returns existing projects`` () =
    let result = listProjects fakeContext
    let expected = seq [ ProjectName "Project1"; ProjectName "Project2" ] |> Ok
    expected |> shouldEqual result

[<Test>]
let ``getProjectDetails retrieves special files`` () =
    let result = getProjectDetails { ProjectName = "Project1" } fakeContext

    match result with
    | Ok details ->
        details |> shouldContain ("readme.md", Content readmeContent)
        details |> shouldContain ("todo.md", Content todoContent)
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {EffectError.toString e}")

[<Test>]
let ``writeFile should write new content to file`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "newfile.md"
          Content = "New content written"
          FileWriteMode = FileWriteMode.Write
          ExpectedHash = None
          CreateParents = None }

    let result = writeFile cmd fakeContext
    result |> shouldEqual (Ok())

[<Test>]
let ``appendToFile should add content to existing file`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "todo.md"
          Content = "Appended content"
          FileWriteMode = FileWriteMode.Append
          ExpectedHash = None
          CreateParents = None }

    let result = writeFile cmd fakeContext
    result |> shouldEqual (Ok())

[<Test>]
let ``patchFile should modify specific text`` () =
    let patch =
        "--- a/readme.md\n+++ b/readme.md\n@@ -1,7 +1,7 @@\n Project 1 Readme\n # Start Config\n-Old Text\n+New Text\n # End Config\n # Section Header\n Body\n # Section Footer\n"

    let cmd =
        { ProjectName = "Project1"
          FilePath = "readme.md"
          ExpectedHash = None
          Format = PatchFormat.UnifiedDiff
          Patch = patch
          DryRun = None
          FuzzyContextLines = None
          ReturnContent = None }

    let result = patchFile cmd fakeContext

    match result with
    | Ok patchResult ->
        patchResult.Applied |> shouldEqual true
        patchResult.HunksApplied |> shouldEqual 1
        patchResult.ChangedLines |> shouldEqual 2
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {EffectError.toString e}")

[<Test>]
let ``searchFiles returns matching project items`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "readme"
          FolderPath = None
          MaxResults = Some 10 }

    let result = searchFiles cmd fakeContext

    match result with
    | Ok items -> items.Length |> shouldEqual 1
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {EffectError.toString e}")

[<Test>]
let ``searchText returns structured matches`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "Old Text"
          FolderPath = None
          IncludeGlobs = []
          ExcludeGlobs = []
          MaxResults = Some 10 }

    let result = searchText cmd fakeContext
    result
    |> shouldEqual
        (Ok
            [ { FilePath = "readme.md"
                Line = 3
                Column = 1
                Preview = "Old Text" }
              { FilePath = Path.Combine("src", "deep.md")
                Line = 1
                Column = 8
                Preview = "Nested Old Text" } ])

[<Test>]
let ``searchText respects include globs`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "Old Text"
          FolderPath = None
          IncludeGlobs = [ "src/**" ]
          ExcludeGlobs = []
          MaxResults = Some 10 }

    searchText cmd fakeContext
    |> shouldEqual
        (Ok
            [ { FilePath = Path.Combine("src", "deep.md")
                Line = 1
                Column = 8
                Preview = "Nested Old Text" } ])

[<Test>]
let ``searchText respects exclude globs`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "Old Text"
          FolderPath = None
          IncludeGlobs = []
          ExcludeGlobs = [ "src/**" ]
          MaxResults = Some 10 }

    searchText cmd fakeContext
    |> shouldEqual
        (Ok
            [ { FilePath = "readme.md"
                Line = 3
                Column = 1
                Preview = "Old Text" } ])

[<Test>]
let ``searchFiles recursively returns nested project items`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "deep"
          FolderPath = None
          MaxResults = Some 10 }

    match searchFiles cmd fakeContext with
    | Ok items -> items |> shouldContain (ProjectFile(Path.Combine("src", "deep.md"), 0L, System.DateTimeOffset.MinValue, System.DateTimeOffset.MinValue))
    | Error e -> Assert.Fail($"Expected Ok, but got Error: {EffectError.toString e}")


[<Test>]
let ``unknown project returns not found`` () =
    let cmd = { ProjectName = "Missing"; FolderPath = "" }

    match listDirectory cmd fakeContext with
    | Error(NotFoundError message) -> message.Contains("Unknown project name") |> shouldEqual true
    | other -> Assert.Fail($"Expected NotFoundError, got {other}")

[<Test>]
let ``listDirectory returns files and folders`` () =
    let cmd = { ProjectName = "Project1"; FolderPath = "" }

    match listDirectory cmd fakeContext with
    | Ok items ->
        items |> shouldContain (ProjectFolder "src")
        items |> shouldContain (ProjectFile("readme.md", 0L, System.DateTimeOffset.MinValue, System.DateTimeOffset.MinValue))
        items |> shouldContain (ProjectFile("todo.md", 0L, System.DateTimeOffset.MinValue, System.DateTimeOffset.MinValue))
    | Error e -> Assert.Fail($"Expected Ok, got Error: {EffectError.toString e}")

[<Test>]
let ``listDirectory aggregates missing folder errors`` () =
    let cmd = { ProjectName = "Project1"; FolderPath = "missing" }

    match listDirectory cmd fakeContext with
    | Error(AggregatedErrors errors) -> errors.Length |> shouldEqual 2
    | other -> Assert.Fail($"Expected AggregatedErrors, got {other}")

[<Test>]
let ``readFile returns content`` () =
    let cmd = { ProjectName = "Project1"; FilePath = "readme.md"; StartLine = None; EndLine = None; IncludeLineNumbers = None }
    readFile cmd fakeContext |> shouldEqual (Ok(Content readmeContent))

[<Test>]
let ``readFile returns requested line range`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "readme.md"
          StartLine = Some 2
          EndLine = Some 4
          IncludeLineNumbers = None }

    let expected =
        [ "# Start Config"; "Old Text"; "# End Config" ]
        |> String.concat System.Environment.NewLine
        |> Content
        |> Ok

    readFile cmd fakeContext |> shouldEqual expected

[<Test>]
let ``readFile can include line numbers`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "readme.md"
          StartLine = Some 2
          EndLine = Some 3
          IncludeLineNumbers = Some true }

    let expected =
        [ "2\t# Start Config"; "3\tOld Text" ]
        |> String.concat System.Environment.NewLine
        |> Content
        |> Ok

    readFile cmd fakeContext |> shouldEqual expected

[<Test>]
let ``readFile rejects invalid line range`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "readme.md"
          StartLine = Some 4
          EndLine = Some 2
          IncludeLineNumbers = None }

    match readFile cmd fakeContext with
    | Error(ValidationError _) -> ()
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``readFile returns file error`` () =
    let cmd = { ProjectName = "Project1"; FilePath = "missing.md"; StartLine = None; EndLine = None; IncludeLineNumbers = None }

    match readFile cmd fakeContext with
    | Error(NotFoundError _) -> ()
    | other -> Assert.Fail($"Expected NotFoundError, got {other}")

[<Test>]
let ``readFiles preserves per-file errors`` () =
    let cmd = { ProjectName = "Project1"; FilePaths = [ "readme.md"; "missing.md"; "todo.md" ] }

    match readFiles cmd fakeContext with
    | Ok results ->
        let values = results |> Seq.toList
        values.Length |> shouldEqual 3
        values[0] |> shouldEqual (Content'.Text readmeContent)
        match values[1] with
        | Content'.Error(NotFoundError _) -> ()
        | other -> Assert.Fail($"Expected per-file NotFoundError, got {other}")
        values[2] |> shouldEqual (Content'.Text todoContent)
    | Error e -> Assert.Fail($"Expected Ok, got Error: {EffectError.toString e}")

[<Test>]
let ``searchFiles respects max results`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = ".md"
          FolderPath = None
          MaxResults = Some 1 }

    match searchFiles cmd fakeContext with
    | Ok items -> items.Length |> shouldEqual 1
    | Error e -> Assert.Fail($"Expected Ok, got Error: {EffectError.toString e}")

[<Test>]
let ``searchFiles can scope to folder`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "deep"
          FolderPath = Some "src"
          MaxResults = None }

    match searchFiles cmd fakeContext with
    | Ok items -> items |> shouldEqual [ ProjectFile(Path.Combine("src", "deep.md"), 0L, System.DateTimeOffset.MinValue, System.DateTimeOffset.MinValue) ]
    | Error e -> Assert.Fail($"Expected Ok, got Error: {EffectError.toString e}")

[<Test>]
let ``searchText respects max results`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "Old Text"
          FolderPath = None
          IncludeGlobs = []
          ExcludeGlobs = []
          MaxResults = Some 1 }

    searchText cmd fakeContext
    |> shouldEqual
        (Ok
            [ { FilePath = "readme.md"
                Line = 3
                Column = 1
                Preview = "Old Text" } ])

[<Test>]
let ``searchText returns empty when no matches`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "does-not-exist"
          FolderPath = None
          IncludeGlobs = []
          ExcludeGlobs = []
          MaxResults = None }

    searchText cmd fakeContext |> shouldEqual (Ok [])
