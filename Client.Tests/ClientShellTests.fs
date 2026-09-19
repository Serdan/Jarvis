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
let ``runCommand bounds retained UTF8 output`` () =
    let root = createTempProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, "output.fsx"), "for _ in 1 .. 200 do printf \"æ\"")
        let context = TestContext root
        let cmd =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "output.fsx" ]
              WorkingDirectory = None
              TimeoutSeconds = Some 20
              MaxOutputBytes = Some 63 }

        match runCommand cmd context with
        | Ok output ->
            output.ExitCode |> shouldEqual 0
            Text.Encoding.UTF8.GetByteCount(output.StdOut) <= 63 |> shouldEqual true
            Text.Encoding.UTF8.GetByteCount(output.StdErr) <= 63 |> shouldEqual true
            output.Truncated |> shouldEqual true
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

[<Test>]
let ``listProjectTasks returns empty when config is missing`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let cmd: ListProjectTasksCommand = { ProjectName = "Project1" }

        match listProjectTasks cmd context with
        | Ok result -> result.Tasks |> shouldEqual []
        | Error error -> Assert.Fail($"Expected Ok, got {error}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``project tasks are loaded and executed from jarvis config`` () =
    let root = createTempProject ()
    try
        let project = Path.Combine(root, "Project1")
        let config =
            """{
  "tasks": {
    "version": {
      "description": "Print the dotnet SDK version",
      "executable": "dotnet",
      "args": ["--version"],
      "timeoutSeconds": 10,
      "maxOutputBytes": 4096
    }
  }
}"""

        File.WriteAllText(Path.Combine(project, ".jarvis.json"), config)
        let context = TestContext root

        match listProjectTasks ({ ProjectName = "Project1" }: ListProjectTasksCommand) context with
        | Error error -> Assert.Fail($"Expected task list, got {error}")
        | Ok result ->
            result.Tasks.Length |> shouldEqual 1
            let configuredTask = result.Tasks.Head
            configuredTask.Name |> shouldEqual "version"
            configuredTask.Description |> shouldEqual (Some "Print the dotnet SDK version")
            configuredTask.Executable |> shouldEqual "dotnet"
            configuredTask.Args |> shouldEqual [ "--version" ]

            match runProjectTask "Project1" configuredTask context with
            | Error error -> Assert.Fail($"Expected task execution Ok, got {error}")
            | Ok output ->
                output.ExitCode |> shouldEqual 0
                output.StdOut.Trim().Length > 0 |> shouldEqual true
    finally
        Directory.Delete(root, true)

[<Test>]
let ``project task config rejects malformed task definitions`` () =
    let root = createTempProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, ".jarvis.json"), """{"tasks":{"broken":{"args":[]}}}""")
        let context = TestContext root

        match listProjectTasks ({ ProjectName = "Project1" }: ListProjectTasksCommand) context with
        | Error(Client.ValidationError message) ->
            message.Contains("executable") |> shouldEqual true
        | other -> Assert.Fail($"Expected ValidationError, got {other}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``project task names are case sensitive`` () =
    let root = createTempProject ()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(Path.Combine(project, ".jarvis.json"), """{"tasks":{"version":{"executable":"dotnet","args":["--version"]}}}""")
        let context = TestContext root

        match resolveProjectTask { ProjectName = "Project1"; TaskName = "Version" } context with
        | Error(Client.NotFoundError _) -> ()
        | other -> Assert.Fail($"Expected NotFoundError, got {other}")
    finally
        Directory.Delete(root, true)
