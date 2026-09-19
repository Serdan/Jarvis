module PathSafetyTests

open System
open System.IO
open Client
open Client.ClientShell
open Client.IO
open Client.ProjectBrowser
open Client.ProjectPaths
open Client.JobManager
open Common
open NUnit.Framework

let createTempProject () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-paths-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
    root

type TestContext(root: string) =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory root; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl

[<Test>]
let ``startJob rejects working directory outside project root`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let cmd =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "--version" ]
              WorkingDirectory = Some ".."
              MaxOutputBytes = Some 4096 }

        match startJob cmd context with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``getGitDiff rejects path outside project root`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let cmd = { ProjectName = "Project1"; Path = Some "../outside.txt"; MaxOutputBytes = Some 4096 }

        match getGitDiff cmd context with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``gitCommit rejects option-like path`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let cmd =
            { ProjectName = "Project1"
              Message = "bad"
              Body = None
              Paths = [ "-danger" ]
              AllowEmpty = false }

        match gitCommit cmd context with
        | Error(Client.ValidationError _) -> ()
        | other -> Assert.Fail($"Expected ValidationError, got {other}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``project paths reject symbolic link escapes`` () =
    let root = createTempProject ()
    let outside = Path.Combine(Path.GetTempPath(), "jarvis-outside-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(outside) |> ignore
    File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside")

    try
        let link = Path.Combine(root, "Project1", "outside-link")

        try
            Directory.CreateSymbolicLink(link, outside) |> ignore
        with
        | :? UnauthorizedAccessException ->
            Assert.Ignore("Symbolic links are not available in this test environment.")
        | :? PlatformNotSupportedException ->
            Assert.Ignore("Symbolic links are not supported on this platform.")

        let context = TestContext root

        match resolveProjectFile "Project1" (Path.Combine("outside-link", "secret.txt")) context with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")
    finally
        if Directory.Exists root then Directory.Delete(root, true)
        if Directory.Exists outside then Directory.Delete(outside, true)

[<Test>]
let ``searchText reports multiple matches with line and column from real files`` () =
    let root = createTempProject ()

    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "sample.txt"), "prefix needle and NEEDLE\nnext needle")
        let context = TestContext root
        let cmd =
            { ProjectName = "Project1"
              Query = "needle"
              FolderPath = None
              IncludeGlobs = []
              ExcludeGlobs = []
              MaxResults = Some 2 }

        match searchText cmd context with
        | Error error -> Assert.Fail($"Expected SearchText Ok, got {error}")
        | Ok matches ->
            let expected =
                [ { FilePath = "sample.txt"
                    Line = 1
                    Column = 8
                    Preview = "prefix needle and NEEDLE" }
                  { FilePath = "sample.txt"
                    Line = 1
                    Column = 19
                    Preview = "prefix needle and NEEDLE" } ]

            if matches <> expected then
                Assert.Fail($"Expected {expected}, got {matches}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``writeFile creates missing parent directories when requested`` () =
    let root = createTempProject ()

    try
        let context = TestContext root
        let relativePath = Path.Combine("nested", "deeper", "created.txt")
        let cmd =
            { ProjectName = "Project1"
              FilePath = relativePath
              Content = "created"
              FileWriteMode = FileWriteMode.Write
              ExpectedHash = None
              CreateParents = Some true }

        match writeFile cmd context with
        | Error error -> Assert.Fail($"Expected WriteFile Ok, got {error}")
        | Ok() ->
            let fullPath = Path.Combine(root, "Project1", relativePath)

            if not (File.Exists fullPath) then
                Assert.Fail($"Expected file to exist: {fullPath}")

            if File.ReadAllText(fullPath) <> "created" then
                Assert.Fail("Expected created file content.")
    finally
        Directory.Delete(root, true)
