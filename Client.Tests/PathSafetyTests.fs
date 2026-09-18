module PathSafetyTests

open System
open System.IO
open Client
open Client.ClientShell
open Client.IO
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
