module HashAndPatchTests

open System.IO
open Client
open Client.ProjectBrowser
open Common
open NUnit.Framework
open FsUnitTyped

let content = "hello\nworld\n"

type TestContext(?initialContent: string) =
    let mutable currentContent = defaultArg initialContent content
    let mutable writeCount = 0

    member _.Content = currentContent
    member _.WriteCount = writeCount

    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory "/fake/projects"; SpecialFiles = []; FolderFilters = [] }

    interface FileIO with
        member _.File =
            { getFullPath = _.Replace('\\', '/') >> Ok
              ReadAllText = fun _ -> Ok(Content currentContent)
              WriteAllText = fun _ (Content text) ->
                  currentContent <- text
                  writeCount <- writeCount + 1
                  Ok()
              parseFile = fun path -> Ok(FilePath path)
              CopyFile = fun _ _ _ -> Ok()
              AppendAllText = fun _ (Content text) ->
                  currentContent <- currentContent + text
                  writeCount <- writeCount + 1
                  Ok()
              parseFolder = fun _ -> Ok(FolderPath "/fake/projects/Project1")
              GetFiles = fun _ -> Ok Seq.empty
              getChildFolders = fun _ -> Ok(Seq.ofList [ FolderPath "/fake/projects/Project1" ])
              GetFileInfo = fun _ -> Error(NotFoundError "unused")
              GetFolderName = fun (FolderPath path) -> Path.GetFileName path
              getFileName = fun (FilePath path) -> Path.GetFileName path }

let patchCommand patch expectedHash dryRun returnContent =
    { ProjectName = "Project1"
      FilePath = "test.txt"
      ExpectedHash = expectedHash
      Format = PatchFormat.UnifiedDiff
      Patch = patch
      DryRun = dryRun
      FuzzyContextLines = None
      ReturnContent = returnContent }

[<Test>]
let ``writeFile rejects mismatched expected hash`` () =
    let context = TestContext()

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
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch (Some "sha256:not-the-right-hash") None None

    match patchFile cmd context with
    | Error(ValidationError message) -> message.Contains("Expected hash") |> shouldEqual true
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile returns structured result`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch None None (Some true)

    match patchFile cmd context with
    | Ok result ->
        result.Applied |> shouldEqual true
        result.DryRun |> shouldEqual false
        result.FilePath |> shouldEqual "test.txt"
        result.HunksApplied |> shouldEqual 1
        result.ChangedLines |> shouldEqual 2
        result.BeforeHash.StartsWith("sha256:") |> shouldEqual true
        result.AfterHash.IsSome |> shouldEqual true
        result.Content |> shouldEqual (Some "hello\nthere\n")
        result.Diagnostics.Length |> shouldEqual 1
        context.Content |> shouldEqual "hello\nthere\n"
        context.WriteCount |> shouldEqual 1
    | Error error -> Assert.Fail($"Expected successful patch, got {error}")

[<Test>]
let ``patchFile dry run does not write`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch None (Some true) (Some false)

    match patchFile cmd context with
    | Ok result ->
        result.Applied |> shouldEqual true
        result.DryRun |> shouldEqual true
        result.Content |> shouldEqual None
        result.AfterHash.IsSome |> shouldEqual true
        context.Content |> shouldEqual content
        context.WriteCount |> shouldEqual 0
    | Error error -> Assert.Fail($"Expected successful dry run, got {error}")

[<Test>]
let ``patchFile context mismatch includes diagnostic context`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-missing\n+there\n"
    let cmd = patchCommand patch None None None

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("Patch hunk 1 failed") |> shouldEqual true
        message.Contains("Expected context") |> shouldEqual true
        message.Contains("Actual context") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")
