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

      WriteAllText = fun _ _ -> Ok()

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
        commands.ProtocolVersion |> shouldEqual "2.1"
        commands.Commands |> List.exists (fun c -> c.Name = "PatchFile") |> shouldEqual true
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
          ExpectedHash = None }

    let result = writeFile cmd fakeContext
    result |> shouldEqual (Ok())

[<Test>]
let ``appendToFile should add content to existing file`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "todo.md"
          Content = "Appended content"
          FileWriteMode = FileWriteMode.Append
          ExpectedHash = None }

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
let ``searchText returns matching files`` () =
    let cmd =
        { ProjectName = "Project1"
          Query = "Old Text"
          FolderPath = None
          IncludeGlobs = []
          ExcludeGlobs = []
          MaxResults = Some 10 }

    let result = searchText cmd fakeContext
    result |> shouldEqual (Ok [ "readme.md"; Path.Combine("src", "deep.md") ])

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
