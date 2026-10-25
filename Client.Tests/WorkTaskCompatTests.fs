module WorkTaskCompatTests

open System
open System.IO
open Client
open Common
open NUnit.Framework
open FsUnitTyped

let private withHtmlTask action =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(Path.Combine(folder, "305-component-authority.html"), "<h1>Authority</h1>")
        GitCommandTests.expectGitSuccess context [ "add"; "docs/tasks/305-component-authority.html" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Describe task 305" ]
        action root project context
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let ``legacy AttachWorkTask API creates Git-backed state for canonical HTML`` () =
    withHtmlTask (fun _ project context ->
        let cmd =
            { ProjectName = "Project1"; TaskId = "305"
              TaskPath = "docs/tasks/305-component-authority.html" }
        match Client.WorkTaskCompat.attachWorkTask cmd context with
        | Ok task ->
            task.Status |> shouldEqual "active"
            task.TaskContent |> shouldEqual (Some "<h1>Authority</h1>")
        | Error error -> Assert.Fail($"Attach failed: {error}")
        File.Exists(Path.Combine(project, "docs", "tasks", "305.json")) |> shouldEqual true
        File.Exists(Path.Combine(project, ".jarvis", "tasks", "305.json")) |> shouldEqual false
        match Client.WorkTaskCompat.listWorkTasks { ProjectName = "Project1" } context with
        | Ok list -> list.Tasks.Length |> shouldEqual 1
        | Error err -> Assert.Fail($"List failed: {err}"))

[<Test>]
let ``old ignored HTML checkpoint migrates into canonical JSON on update`` () =
    withHtmlTask (fun _ project context ->
        let attach =
            { ProjectName = "Project1"; TaskId = "305"
              TaskPath = "docs/tasks/305-component-authority.html" }
        // Simulate a checkpoint created by the original Jarvis client.
        match Client.ClientShell.attachWorkTask attach context with
        | Error err -> Assert.Fail($"Legacy attach failed: {err}")
        | Ok _ -> ()
        let oldFile = Path.Combine(project, ".jarvis", "tasks", "305.json")
        File.Exists(oldFile) |> shouldEqual true
        let checkpoint =
            { ProjectName = "Project1"; TaskId = "305"
              Status = "blocked"; Summary = "Verified preparation"; NextAction = "Continue COW"
              Verified = [ "focused tests" ]; Blocker = Some "Pending integration"; LastCommit = Some "abc123" }
        match Client.WorkTaskCompat.checkpointWorkTask checkpoint context with
        | Ok task ->
            task.Status |> shouldEqual "blocked"
            task.NextAction |> shouldEqual (Some "Continue COW")
            task.Blocker |> shouldEqual (Some "Pending integration")
            task.Verified |> shouldEqual [ "focused tests" ]
        | Error err -> Assert.Fail($"Migration failed: {err}")
        File.Exists(oldFile) |> shouldEqual false
        match Client.TaskStore.getTask { ProjectName = "Project1"; TaskId = "305" } context with
        | Ok detail -> detail.StateJson.Value.Contains("Continue COW") |> shouldEqual true
        | Error err -> Assert.Fail($"Git state missing: {err}"))

[<Test>]
let ``legacy completion command retains Git-backed completed state`` () =
    withHtmlTask (fun _ project context ->
        match Client.WorkTaskCompat.attachWorkTask
            { ProjectName = "Project1"; TaskId = "305"
              TaskPath = "docs/tasks/305-component-authority.html" } context with
        | Error err -> Assert.Fail($"Attach failed: {err}")
        | Ok _ -> ()
        match Client.WorkTaskCompat.completeWorkTask { ProjectName = "Project1"; TaskId = "305" } context with
        | Error err -> Assert.Fail($"Completion failed: {err}")
        | Ok completed -> completed.FinalCommit.Length |> shouldEqual 40
        match Client.TaskStore.getTask { ProjectName = "Project1"; TaskId = "305" } context with
        | Ok task -> task.Task.Status |> shouldEqual "completed"
        | Error err -> Assert.Fail($"Completed state missing: {err}")
        File.Exists(Path.Combine(project, "docs", "tasks", "305.json")) |> shouldEqual true)

[<Test>]
let ``Git-backed legacy GetWorkTask detects subsequent HTML description revisions`` () =
    withHtmlTask (fun _ project context ->
        let initial =
            match Client.WorkTaskCompat.attachWorkTask
                { ProjectName = "Project1"; TaskId = "305"
                  TaskPath = "docs/tasks/305-component-authority.html" } context with
            | Ok task -> task
            | Error e -> failwith $"Attach failed: {e}"
        let path = Path.Combine(project, "docs", "tasks", "305-component-authority.html")
        File.WriteAllText(path, "<h1>Revised authority</h1>")
        GitCommandTests.expectGitSuccess context [ "add"; "docs/tasks/305-component-authority.html" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Revise description" ]
        match Client.WorkTaskCompat.getWorkTask { ProjectName = "Project1"; TaskId = "305" } context with
        | Ok task ->
            task.TaskChanged |> shouldEqual true
            task.TaskCommit |> shouldEqual initial.TaskCommit
            task.CurrentTaskCommit <> Some initial.TaskCommit |> shouldEqual true
        | Error e -> Assert.Fail($"Description change check failed: {e}"))
