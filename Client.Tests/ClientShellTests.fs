module ClientShellTests

open System
open System.IO
open Client
open Client.ClientShell
open Client.IO
open Common
open NUnit.Framework
open FsUnitTyped

let createTempProject () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
    root

type TestContext(root: string) =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory root; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl

[<Test>]
let ``runCommand executes structured command`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let cmd =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "--version" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 10
              MaxOutputBytes = Some 4096 }

        match runCommand cmd context with
        | Ok output ->
            output.ExitCode |> shouldEqual 0
            output.StdOut.Trim().Length > 0 |> shouldEqual true
        | Error error -> Assert.Fail($"Expected Ok, got {error}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``runCommand rejects shell executable`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let cmd =
            { ProjectName = "Project1"
              Executable = "bash"
              Args = [ "-lc"; "echo unsafe" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 10
              MaxOutputBytes = Some 4096 }

        match runCommand cmd context with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")
    finally
        Directory.Delete(root, true)
