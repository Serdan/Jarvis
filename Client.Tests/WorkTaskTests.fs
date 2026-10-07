module WorkTaskTests

open System
open System.IO
open Client
open Client.ClientShell
open Client.IO
open Common
open NUnit.Framework
open FsUnitTyped

let createTempProject () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-work-task-" + Guid.NewGuid().ToString("N"))
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
      Reason = None
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

let commitTask context project (content: string) =
    let tasks = Path.Combine(project, "tasks")
    Directory.CreateDirectory(tasks) |> ignore
    File.WriteAllText(Path.Combine(tasks, "228.md"), content)
    expectGitSuccess context [ "add"; "--"; "tasks/228.md" ]
    expectGitSuccess context [ "commit"; "-m"; "Add task 228" ]

[<Test>]
let workTaskPersistsCheckpointAndDeletesStateOnCompletion () =
    let root, project = createTempProject ()
    try
        let context = TestContext root
        initRepo context
        commitTask context project "# Task 228\nImplement progression integration.\n"

        let attached =
            match attachWorkTask { ProjectName = "Project1"; TaskId = "228"; TaskPath = "tasks/228.md" } context with
            | Ok task -> task
            | Error error -> failwithf "Attach failed: %A" error

        attached.TaskPath |> shouldEqual "tasks/228.md"
        attached.Status |> shouldEqual "active"
        attached.TaskContent.Value.Contains("progression integration") |> shouldEqual true
        File.Exists(Path.Combine(project, ".jarvis", "tasks", "228.json")) |> shouldEqual true

        match runCommand (git "Project1" [ "status"; "--porcelain" ]) context with
        | Ok result -> result.StdOut.Contains(".jarvis/tasks") |> shouldEqual false
        | Error error -> failwithf "Git status failed: %A" error

        let checkpoint =
            { ProjectName = "Project1"
              TaskId = "228"
              Status = "blocked"
              Summary = "Core progression model implemented."
              NextAction = "Adapt character creation."
              Verified = [ "Focused progression tests pass." ]
              Blocker = Some "Waiting for creator mapping."
              LastCommit = None }

        match checkpointWorkTask checkpoint context with
        | Ok task ->
            task.Status |> shouldEqual "blocked"
            task.NextAction |> shouldEqual (Some "Adapt character creation.")
            task.Verified |> shouldEqual [ "Focused progression tests pass." ]
        | Error error -> failwithf "Checkpoint failed: %A" error

        File.WriteAllText(Path.Combine(project, "tasks", "228.md"), "# Task 228\nUpdated requirements.\n")
        expectGitSuccess context [ "add"; "--"; "tasks/228.md" ]
        expectGitSuccess context [ "commit"; "-m"; "Update task 228" ]

        match getWorkTask { ProjectName = "Project1"; TaskId = "228" } context with
        | Ok task ->
            task.TaskChanged |> shouldEqual true
            task.TaskDirty |> shouldEqual false
            task.TaskContent.Value.Contains("Updated requirements") |> shouldEqual true
        | Error error -> failwithf "Get failed: %A" error

        match completeWorkTask { ProjectName = "Project1"; TaskId = "228" } context with
        | Ok result ->
            result.TaskId |> shouldEqual "228"
            String.IsNullOrWhiteSpace result.FinalCommit |> shouldEqual false
        | Error error -> failwithf "Complete failed: %A" error

        File.Exists(Path.Combine(project, ".jarvis", "tasks", "228.json")) |> shouldEqual false
        match getWorkTask { ProjectName = "Project1"; TaskId = "228" } context with
        | Error(NotFoundError _) -> ()
        | other -> Assert.Fail($"Expected missing completed work task, got {other}")
    finally
        Directory.Delete(root, true)

[<Test>]
let attachRequiresCommittedCleanTaskDescription () =
    let root, project = createTempProject ()
    try
        let context = TestContext root
        initRepo context
        let tasks = Path.Combine(project, "tasks")
        Directory.CreateDirectory(tasks) |> ignore
        File.WriteAllText(Path.Combine(tasks, "new.md"), "# New task\n")

        match attachWorkTask { ProjectName = "Project1"; TaskId = "new"; TaskPath = "tasks/new.md" } context with
        | Error(ValidationError message) -> message.Contains("tracked by git") |> shouldEqual true
        | other -> Assert.Fail($"Expected untracked task rejection, got {other}")

        commitTask context project "# Task 228\nCommitted.\n"
        File.AppendAllText(Path.Combine(project, "tasks", "228.md"), "dirty\n")

        match attachWorkTask { ProjectName = "Project1"; TaskId = "228"; TaskPath = "tasks/228.md" } context with
        | Error(ValidationError message) -> message.Contains("Commit the task description") |> shouldEqual true
        | other -> Assert.Fail($"Expected dirty task rejection, got {other}")
    finally
        Directory.Delete(root, true)
