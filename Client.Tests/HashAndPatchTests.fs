module HashAndPatchTests

open System.IO
open Client
open Client.ProjectBrowser
open Common
open NUnit.Framework
open FsUnitTyped

let content = "hello\nworld\n"

type TestContext() =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory "/fake/projects"; SpecialFiles = []; FolderFilters = [] }

    interface FileIO with
        member _.File =
            { getFullPath = _.Replace('\\', '/') >> Ok
              ReadAllText = fun _ -> Ok(Content content)
              WriteAllText = fun _ _ -> Ok()
              parseFile = fun path -> Ok(FilePath path)
              CopyFile = fun _ _ _ -> Ok()
              AppendAllText = fun _ _ -> Ok()
              parseFolder = fun _ -> Ok(FolderPath "/fake/projects/Project1")
              GetFiles = fun _ -> Ok Seq.empty
              getChildFolders = fun _ -> Ok(Seq.ofList [ FolderPath "/fake/projects/Project1" ])
              GetFileInfo = fun _ -> Error(NotFoundError "unused")
              GetFolderName = fun (FolderPath path) -> Path.GetFileName path
              getFileName = fun (FilePath path) -> Path.GetFileName path }

let context = TestContext()

[<Test>]
let ``writeFile rejects mismatched expected hash`` () =
    let cmd =
        { ProjectName = "Project1"
          FilePath = "test.txt"
          Content = "new"
          FileWriteMode = FileWriteMode.Write
          ExpectedHash = Some "sha256:not-the-right-hash" }

    match writeFile cmd context with
    | Error(ValidationError message) -> message.Contains("Expected hash") |> shouldEqual true
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile rejects mismatched expected hash`` () =
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"

    let cmd =
        { ProjectName = "Project1"
          FilePath = "test.txt"
          ExpectedHash = Some "sha256:not-the-right-hash"
          Format = PatchFormat.UnifiedDiff
          Patch = patch }

    match patchFile cmd context with
    | Error(ValidationError message) -> message.Contains("Expected hash") |> shouldEqual true
    | other -> Assert.Fail($"Expected ValidationError, got {other}")
