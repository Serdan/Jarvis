module TaskStoreTests

open System
open System.IO
open Common
open Client
open Client.IO
open Client.TaskStore
open NUnit.Framework
open FsUnitTyped

let private withProject action =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        action root project context
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let ``legacy HTML task is listed without creating state`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(Path.Combine(folder, "311-responsive-loading.html"), "<h1>Loading</h1>")
        match listTasks { ProjectName = "Project1"; Status = None } context with
        | Ok [item] ->
            item.Id |> shouldEqual "311-responsive-loading"
            item.HasState |> shouldEqual false
            item.Status |> shouldEqual "open"
        | other -> Assert.Fail($"Expected legacy task: {other}")
        match getTask { ProjectName = "Project1"; TaskId = "311-responsive-loading" } context with
        | Ok detail -> detail.Html |> shouldEqual "<h1>Loading</h1>"
        | other -> Assert.Fail($"Expected HTML content: {other}")
        File.Exists(Path.Combine(folder, "311-responsive-loading.json")) |> shouldEqual false)

[<Test>]
let ``JSON task state is parsed and can be filtered`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(Path.Combine(folder, "282.html"), "<h1>Input</h1>")
        File.WriteAllText(Path.Combine(folder, "282.json"), """{"schemaVersion":1,"id":"282","title":"Input","status":"active","dependencies":[]}""")
        match listTasks { ProjectName = "Project1"; Status = Some "active" } context with
        | Ok [item] ->
            item.Title |> shouldEqual "Input"
            item.Revision.IsSome |> shouldEqual true
            item.Diagnostics.IsEmpty |> shouldEqual true
        | other -> Assert.Fail($"Expected active task: {other}")
        match listTasks { ProjectName = "Project1"; Status = Some "completed" } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Expected filtered empty list: {other}"))

[<Test>]
let ``configured task directory is used`` () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, ".jarvis.json"), """{"taskDirectory":".agents/tasks"}""")
        let folder = Path.Combine(project, ".agents", "tasks")
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(Path.Combine(folder, "alpha.html"), "<p>Alpha</p>")
        match getTask { ProjectName = "Project1"; TaskId = "alpha" } context with
        | Ok detail -> detail.Task.DescriptionPath |> shouldEqual ".agents/tasks/alpha.html"
        | other -> Assert.Fail($"Configured path failed: {other}"))

[<Test>]
let ``malformed state remains visible with diagnostics`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(Path.Combine(folder, "broken.html"), "<p>Broken</p>")
        File.WriteAllText(Path.Combine(folder, "broken.json"), """{"schemaVersion":1,"id":"wrong","status":"active"}""")
        match listTasks { ProjectName = "Project1"; Status = None } context with
        | Ok [item] -> item.Diagnostics.IsEmpty |> shouldEqual false
        | other -> Assert.Fail($"Malformed task omitted: {other}"))

[<Test>]
let ``task directory traversal is rejected`` () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, ".jarvis.json"), """{"taskDirectory":"../outside"}""")
        match listTasks { ProjectName = "Project1"; Status = None } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Directory traversal must be rejected."))
