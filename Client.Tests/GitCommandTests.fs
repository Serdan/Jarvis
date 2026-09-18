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

let gitOutput context args =
    match runCommand (git "Project1" args) context with
    | Ok result when result.ExitCode = 0 -> result.StdOut.Trim()
    | other -> failwith $"Expected git success for {args}, got {other}"

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

[<Test>]
let ``gitCommit leaves unrelated staged changes out of commit`` () =
    let root, project = createTempProject ()
    try
        let context = TestContext root
        initRepo context

        let selected = Path.Combine(project, "selected.txt")
        let unrelated = Path.Combine(project, "unrelated.txt")
        File.WriteAllText(selected, "initial selected\n")
        File.WriteAllText(unrelated, "initial unrelated\n")
        expectGitSuccess context [ "add"; "--"; "selected.txt"; "unrelated.txt" ]
        expectGitSuccess context [ "commit"; "-m"; "Initial" ]

        File.WriteAllText(selected, "changed selected\n")
        File.WriteAllText(unrelated, "changed unrelated\n")
        expectGitSuccess context [ "add"; "--"; "unrelated.txt" ]

        let cmd =
            { ProjectName = "Project1"
              Message = "Commit selected"
              Body = None
              Paths = [ "selected.txt" ]
              AllowEmpty = false }

        match gitCommit cmd context with
        | Ok _ -> ()
        | Error error -> Assert.Fail($"Expected GitCommit Ok, got {error}")

        let committedPaths =
            gitOutput context [ "show"; "--format="; "--name-only"; "HEAD" ]
            |> _.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        committedPaths |> shouldContain "selected.txt"
        committedPaths |> shouldNotContain "unrelated.txt"

        let stagedPaths =
            gitOutput context [ "diff"; "--cached"; "--name-only" ]
            |> _.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        stagedPaths |> shouldContain "unrelated.txt"
    finally
        Directory.Delete(root, true)

[<Test>]
let ``empty gitCommit refuses unrelated staged changes`` () =
    let root, project = createTempProject ()
    try
        let context = TestContext root
        initRepo context

        let file = Path.Combine(project, "staged.txt")
        File.WriteAllText(file, "initial\n")
        expectGitSuccess context [ "add"; "--"; "staged.txt" ]
        expectGitSuccess context [ "commit"; "-m"; "Initial" ]

        File.WriteAllText(file, "changed\n")
        expectGitSuccess context [ "add"; "--"; "staged.txt" ]

        let cmd =
            { ProjectName = "Project1"
              Message = "Empty"
              Body = None
              Paths = []
              AllowEmpty = true }

        match gitCommit cmd context with
        | Error(Client.ValidationError message) ->
            message.Contains("staged changes") |> shouldEqual true
        | other -> Assert.Fail($"Expected ValidationError, got {other}")
    finally
        Directory.Delete(root, true)
