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
              ReadLines = fun _ _ _ -> Ok []
              SearchText = fun _ _ _ -> Ok []
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

let patchCommandWithFuzzy patch expectedHash dryRun returnContent fuzzyContextLines =
    { ProjectName = "Project1"
      FilePath = "test.txt"
      ExpectedHash = expectedHash
      Format = PatchFormat.UnifiedDiff
      Patch = patch
      DryRun = dryRun
      FuzzyContextLines = fuzzyContextLines
      ReturnContent = returnContent }

let patchCommand patch expectedHash dryRun returnContent =
    patchCommandWithFuzzy patch expectedHash dryRun returnContent None

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


[<Test>]
let ``patchFile defaults to a small fuzzy context window`` () =
    let context = TestContext("intro\nhello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch None None (Some true)

    match patchFile cmd context with
    | Ok result ->
        result.Content |> shouldEqual (Some "intro\nhello\nthere\n")
        match result.Diagnostics.Head.Status with
        | AppliedWithOffset offset -> offset |> shouldEqual 1
        | other -> Assert.Fail($"Expected AppliedWithOffset, got {other}")
    | Error error -> Assert.Fail($"Expected default fuzzy patch to succeed, got {error}")

[<Test>]
let ``patchFile fuzzy context zero remains strict only`` () =
    let context = TestContext("intro\nhello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None None (Some 0)

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("mismatch") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected strict-only ValidationError, got {other}")

[<Test>]
let ``patchFile fuzzy mode applies hunk shifted down within window`` () =
    let context = TestContext("intro\nhello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None (Some true) (Some 2)

    match patchFile cmd context with
    | Ok result ->
        result.Content |> shouldEqual (Some "intro\nhello\nthere\n")
        result.Diagnostics.Length |> shouldEqual 1
        match result.Diagnostics.Head.Status with
        | AppliedWithOffset offset -> offset |> shouldEqual 1
        | other -> Assert.Fail($"Expected AppliedWithOffset, got {other}")
    | Error error -> Assert.Fail($"Expected successful fuzzy patch, got {error}")

[<Test>]
let ``patchFile fuzzy mode applies hunk shifted up within window`` () =
    let context = TestContext("hello\nworld\noutro\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -2,2 +2,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None (Some true) (Some 2)

    match patchFile cmd context with
    | Ok result ->
        result.Content |> shouldEqual (Some "hello\nthere\noutro\n")
        match result.Diagnostics.Head.Status with
        | AppliedWithOffset offset -> offset |> shouldEqual -1
        | other -> Assert.Fail($"Expected AppliedWithOffset, got {other}")
    | Error error -> Assert.Fail($"Expected successful fuzzy patch, got {error}")

[<Test>]
let ``patchFile fuzzy mode fails when match is outside window`` () =
    let context = TestContext("a\nb\nc\nhello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None None (Some 2)

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("No valid fuzzy match") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile fuzzy mode fails on ambiguous matches`` () =
    let context = TestContext("hello\nworld\nspacer\nhello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -2,2 +2,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None None (Some 4)

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("Ambiguous fuzzy match") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile fuzzy mode rejects addition only hunks`` () =
    let context = TestContext("hello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,0 +1,1 @@\n+inserted\n"
    let cmd = patchCommandWithFuzzy patch None None None (Some 2)

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("requires evidence") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")


[<Test>]
let ``patchFile preserves CRLF line endings`` () =
    let context = TestContext("hello\r\nworld\r\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch None None (Some true)

    match patchFile cmd context with
    | Ok result ->
        result.Content |> shouldEqual (Some "hello\r\nthere\r\n")
        context.Content |> shouldEqual "hello\r\nthere\r\n"
    | Error error -> Assert.Fail($"Expected successful CRLF patch, got {error}")

[<Test>]
let ``patchFile ambiguous fuzzy diagnostic lists candidate lines`` () =
    let context = TestContext("hello\nworld\nspacer\nhello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -2,2 +2,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None None (Some 4)

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("Ambiguous fuzzy match") |> shouldEqual true
        message.Contains("Candidate lines: 1, 4") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile fuzzy result records applied line`` () =
    let context = TestContext("intro\nhello\nworld\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None (Some true) (Some 2)

    match patchFile cmd context with
    | Ok result -> result.Diagnostics.Head.AppliedStartLine |> shouldEqual (Some 2)
    | Error error -> Assert.Fail($"Expected successful fuzzy patch, got {error}")


[<Test>]
let ``patchFile rejects binary-looking content`` () =
    let context = TestContext("hello\u0000world\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,1 +1,1 @@\n-hello\u0000world\n+hello there\n"
    let cmd = patchCommand patch None None None

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("binary files") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")


[<Test>]
let ``patchFile multiple hunks are atomic when later hunk fails`` () =
    let initial = "alpha\none\nbeta\ntwo\n"
    let context = TestContext(initial)
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n alpha\n-one\n+ONE\n@@ -3,2 +3,2 @@\n beta\n-missing\n+TWO\n"
    let cmd = patchCommand patch None None None

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("Patch hunk 2 failed") |> shouldEqual true
        context.Content |> shouldEqual initial
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile applies multiple hunks`` () =
    let context = TestContext("alpha\none\nbeta\ntwo\n")
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n alpha\n-one\n+ONE\n@@ -3,2 +3,2 @@\n beta\n-two\n+TWO\n"
    let cmd = patchCommand patch None None (Some true)

    match patchFile cmd context with
    | Ok result ->
        result.Content |> shouldEqual (Some "alpha\nONE\nbeta\nTWO\n")
        result.HunksApplied |> shouldEqual 2
        result.ChangedLines |> shouldEqual 4
        context.WriteCount |> shouldEqual 1
    | Error error -> Assert.Fail($"Expected successful multi-hunk patch, got {error}")

[<Test>]
let ``patchFile rejects negative fuzzy context lines`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None None (Some -1)

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("FuzzyContextLines") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile rejects excessive fuzzy context lines`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommandWithFuzzy patch None None None (Some 51)

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("FuzzyContextLines") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile rejects malformed hunk header`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ nope @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch None None None

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("Invalid patch hunk header") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile rejects invalid patch line`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n?bad\n"
    let cmd = patchCommand patch None None None

    match patchFile cmd context with
    | Error(ValidationError message) ->
        message.Contains("Invalid patch line") |> shouldEqual true
        context.WriteCount |> shouldEqual 0
    | other -> Assert.Fail($"Expected ValidationError, got {other}")

[<Test>]
let ``patchFile return content false omits content on write`` () =
    let context = TestContext()
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch None None (Some false)

    match patchFile cmd context with
    | Ok result ->
        result.Content |> shouldEqual None
        context.Content |> shouldEqual "hello\nthere\n"
    | Error error -> Assert.Fail($"Expected successful patch, got {error}")

[<Test>]
let ``patchFile accepts matching expected hash`` () =
    let context = TestContext()
    let expectedHash = Some "sha256:4a1e67f2fe1d1cc7b31d0ca2ec441da4778203a036a77da10344c85e24ff0f92"
    let patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1,2 +1,2 @@\n hello\n-world\n+there\n"
    let cmd = patchCommand patch expectedHash None (Some true)

    match patchFile cmd context with
    | Ok result ->
        result.BeforeHash |> shouldEqual expectedHash.Value
        result.Content |> shouldEqual (Some "hello\nthere\n")
    | Error error -> Assert.Fail($"Expected successful patch, got {error}")
