module GitCommandTests

open System
open System.IO
open Client
open Client.ClientShell
open Client.IO
open Common
open NUnit.Framework
open FsUnitTyped

let createTempProject () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-git-" + Guid.NewGuid().ToString("N"))
    let project = Path.Combine(root, "Project1")
    Directory.CreateDirectory(project) |> ignore
    root, project

type TestContext(root: string) =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory root; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl

let git projectName args =
    { ProjectName = projectName
      Executable = "git"
      Args = args
      WorkingDirectory = None
      TimeoutSeconds = Some 10
      MaxOutputBytes = Some 4096 }

let expectGitSuccess context args =
    match runCommand (git "Project1" args) context with
    | Ok result when result.ExitCode = 0 -> ()
    | other -> Assert.Fail($"Expected git success for {args}, got {other}")

let initRepo context =
    expectGitSuccess context [ "init" ]
    expectGitSuccess context [ "config"; "user.name"; "Jarvis Test" ]
    expectGitSuccess context [ "config"; "user.email"; "jarvis@example.invalid" ]

[<Test>]
let ``getGitStatus reports untracked file`` () =
    let root, project = createTempProject ()
    try
        let context = TestContext root
        initRepo context

        File.WriteAllText(Path.Combine(project, "new.txt"), "hello")

        match getGitStatus { ProjectName = "Project1" } context with
        | Ok result ->
            result.ExitCode |> shouldEqual 0
            result.StdOut.Contains("new.txt") |> shouldEqual true
        | Error error -> Assert.Fail($"Expected GetGitStatus Ok, got {error}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``getGitDiff reports modified tracked file`` () =
    let root, project = createTempProject ()
    try
        let context = TestContext root
        initRepo context

        let file = Path.Combine(project, "tracked.txt")
        File.WriteAllText(file, "before\n")
        expectGitSuccess context [ "add"; "tracked.txt" ]
        expectGitSuccess context [ "commit"; "-m"; "Initial" ]

        File.WriteAllText(file, "after\n")

        match getGitDiff { ProjectName = "Project1"; Path = Some "tracked.txt"; MaxOutputBytes = Some 4096 } context with
        | Ok result ->
            result.ExitCode |> shouldEqual 0
            result.StdOut.Contains("-before") |> shouldEqual true
            result.StdOut.Contains("+after") |> shouldEqual true
        | Error error -> Assert.Fail($"Expected GetGitDiff Ok, got {error}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``gitCommit stages selected path and returns hash`` () =
    let root, project = createTempProject ()
    try
        let context = TestContext root
        initRepo context

        File.WriteAllText(Path.Combine(project, "commit.txt"), "commit me\n")

        let cmd =
            { ProjectName = "Project1"
              Message = "Add test file"
              Body = Some "Created by test."
              Paths = [ "commit.txt" ]
              AllowEmpty = false }

        match gitCommit cmd context with
        | Ok result ->
            result.CommitHash.Length |> shouldEqual 40
        | Error error -> Assert.Fail($"Expected GitCommit Ok, got {error}")
    finally
        Directory.Delete(root, true)
