module PathSafetyTests

open System
open System.IO
open Client
open Client.ClientShell
open Client.IO
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
