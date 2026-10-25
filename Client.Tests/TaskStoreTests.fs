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
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None } context with
        | Ok [item] ->
            item.Id |> shouldEqual "311"
            item.HasState |> shouldEqual false
            item.Status |> shouldEqual "open"
        | other -> Assert.Fail($"Expected legacy task: {other}")
        match getTask { ProjectName = "Project1"; TaskId = "311" } context with
        | Ok detail ->
            detail.Html |> shouldEqual "<h1>Loading</h1>"
            detail.Task.DescriptionPath |> shouldEqual "docs/tasks/311-responsive-loading.html"
        | other -> Assert.Fail($"Expected HTML content: {other}")
        File.Exists(Path.Combine(folder, "311.json")) |> shouldEqual false)

[<Test>]
let ``JSON task state is parsed and can be filtered`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(Path.Combine(folder, "282.html"), "<h1>Input</h1>")
        File.WriteAllText(Path.Combine(folder, "282.json"), """{"schemaVersion":1,"id":"282","title":"Input","status":"active","dependencies":[]}""")
        match listTasks { ProjectName = "Project1"; Status = Some "active"; Owner = None; DependsOn = None } context with
        | Ok [item] ->
            item.Title |> shouldEqual "Input"
            item.Revision.IsSome |> shouldEqual true
            item.Diagnostics.IsEmpty |> shouldEqual true
        | other -> Assert.Fail($"Expected active task: {other}")
        match listTasks { ProjectName = "Project1"; Status = Some "completed"; Owner = None; DependsOn = None } context with
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
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None } context with
        | Ok [item] -> item.Diagnostics.IsEmpty |> shouldEqual false
        | other -> Assert.Fail($"Malformed task omitted: {other}"))

[<Test>]
let ``task directory traversal is rejected`` () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, ".jarvis.json"), """{"taskDirectory":"../outside"}""")
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Directory traversal must be rejected."))

[<Test>]
let ``create commits HTML and JSON task without unrelated staged file`` () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "unrelated.txt"), "user")
        GitCommandTests.expectGitSuccess context [ "add"; "unrelated.txt" ]
        let cmd = { ProjectName = "Project1"; TaskId = Some "482"; Title = "Test task"; Html = "<h1>Test task</h1>"; Dependencies = [] }
        match createTask cmd context with
        | Ok outcome ->
            outcome.CommitHash.Length |> shouldEqual 40
            outcome.Task.Task.HasState |> shouldEqual true
            outcome.Task.Html |> shouldEqual cmd.Html
        | Error e -> Assert.Fail($"CreateTask failed: {e}")
        let changed = GitCommandTests.gitOutput context [ "show"; "--format="; "--name-only"; "HEAD" ]
        changed.Contains("docs/tasks/482.html") |> shouldEqual true
        changed.Contains("docs/tasks/482.json") |> shouldEqual true
        changed.Contains("unrelated.txt") |> shouldEqual false
        let staged = GitCommandTests.gitOutput context [ "diff"; "--cached"; "--name-only" ]
        staged.Contains("unrelated.txt") |> shouldEqual true)

[<Test>]
let ``update uses optimistic revision and preserves HTML`` () =
    withProject (fun _ _ context ->
        let made =
            match createTask { ProjectName = "Project1"; TaskId = Some "update"; Title = "Original"; Html = "<p>human</p>"; Dependencies = [] } context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Create failed: {e}"
        let oldRevision = made.Task.Task.Revision
        let change = { ProjectName = "Project1"; TaskId = "update"; ExpectedRevision = oldRevision; Title = None; Status = Some "active"; Dependencies = None; DescriptionCommit = None }
        match updateTask change context with
        | Ok changed ->
            changed.Task.Task.Status |> shouldEqual "active"
            changed.Task.Html |> shouldEqual "<p>human</p>"
            changed.Task.Task.Revision <> oldRevision |> shouldEqual true
        | Error e -> Assert.Fail($"Update failed: {e}")
        match updateTask change context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Stale revision accepted."))

[<Test>]
let ``claim checkpoint and completion retain committed history`` () =
    withProject (fun _ _ context ->
        let made =
            match createTask { ProjectName = "Project1"; TaskId = Some "chain"; Title = "Chain"; Html = "<h1>Chain</h1>"; Dependencies = [] } context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Create failed: {e}"
        let claim = { ProjectName = "Project1"; TaskId = "chain"; ExpectedRevision = made.Task.Task.Revision; Owner = "agent-a"; AllowReassign = false }
        let claimed =
            match claimTask claim context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Claim failed: {e}"
        match claimTask { claim with Owner = "agent-b"; ExpectedRevision = claimed.Task.Task.Revision } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Other agent stole task without reassignment.")
        let checkpoint =
            { ProjectName = "Project1"; TaskId = "chain"; ExpectedRevision = claimed.Task.Task.Revision
              Status = "active"; Summary = "Implemented first slice"; NextAction = "Verify"; Verified = [ "unit tests" ]; Blocker = None; LastCommit = None; DescriptionCommit = None }
        let progressed =
            match checkpointTask checkpoint context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Checkpoint failed: {e}"
        progressed.Task.StateJson.Value.Contains("Verify") |> shouldEqual true
        let completed =
            match completeTask
                { ProjectName = "Project1"; TaskId = "chain"; ExpectedRevision = progressed.Task.Task.Revision
                  Verified = [ "unit tests" ]; FinalCommit = Some "abc123" } context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Complete failed: {e}"
        completed.Task.Task.Status |> shouldEqual "completed"
        completed.Task.Html |> shouldEqual "<h1>Chain</h1>"
        match getTaskHistory { ProjectName = "Project1"; TaskId = "chain"; Limit = Some 20 } context with
        | Ok history ->
            history.Contains("Create task chain") |> shouldEqual true
            history.Contains("Complete task chain") |> shouldEqual true
        | Error e -> Assert.Fail($"History failed: {e}"))

[<Test>]
let ``HTML-only legacy task materializes JSON on first update`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        let html = Path.Combine(folder, "legacy.html")
        File.WriteAllText(html, "<h1>Legacy</h1>")
        GitCommandTests.expectGitSuccess context [ "add"; "docs/tasks/legacy.html" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Legacy task" ]
        match updateTask { ProjectName = "Project1"; TaskId = "legacy"; ExpectedRevision = None; Title = Some "Legacy task"; Status = Some "blocked"; Dependencies = None; DescriptionCommit = None } context with
        | Ok outcome ->
            outcome.Task.Task.Status |> shouldEqual "blocked"
            File.ReadAllText(html) |> shouldEqual "<h1>Legacy</h1>"
            File.Exists(Path.Combine(folder, "legacy.json")) |> shouldEqual true
        | Error e -> Assert.Fail($"Lazy metadata creation failed: {e}"))

[<Test>]
let ``pre-staged edits to task JSON are rejected`` () =
    withProject (fun _ project context ->
        match createTask { ProjectName = "Project1"; TaskId = Some "indexed"; Title = "Indexed"; Html = "<p>Index</p>"; Dependencies = [] } context with
        | Error e -> Assert.Fail($"Create failed: {e}")
        | Ok created ->
            let json = Path.Combine(project, "docs", "tasks", "indexed.json")
            let edited = File.ReadAllText(json).Replace("open", "blocked")
            File.WriteAllText(json, edited)
            GitCommandTests.expectGitSuccess context [ "add"; "docs/tasks/indexed.json" ]
            match updateTask { ProjectName = "Project1"; TaskId = "indexed"; ExpectedRevision = None; Title = None; Status = Some "active"; Dependencies = None; DescriptionCommit = None } context with
            | Error _ -> File.ReadAllText(json) |> shouldEqual edited
            | Ok _ -> Assert.Fail("Should reject pre-staged task edits."))

[<Test>]
let ``independent feature worktrees retain task changes until merged`` () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        let create name =
            match Client.Worktrees.createWorktree { ProjectName = "Project1"; Name = name; Branch = "feature/" + name; BaseRef = "HEAD" } context with
            | Ok _ -> ()
            | Error e -> Assert.Fail($"CreateWorktree failed: {e}")
        create "agent-a"
        create "agent-b"
        for agent in [ "agent-a"; "agent-b" ] do
            match createTask
                { ProjectName = "Project1::" + agent; TaskId = Some agent
                  Title = agent; Html = "<h1>" + agent + "</h1>"; Dependencies = [] } context with
            | Ok _ -> ()
            | Error e -> Assert.Fail($"Branch-local CreateTask failed: {e}")
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Feature tasks leaked into main: {other}")
        GitCommandTests.expectGitSuccess context [ "merge"; "--no-ff"; "feature/agent-a"; "-m"; "Merge agent-a" ]
        GitCommandTests.expectGitSuccess context [ "merge"; "--no-ff"; "feature/agent-b"; "-m"; "Merge agent-b" ]
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None } context with
        | Ok tasks ->
            tasks |> List.map (fun item -> item.Id) |> Set.ofList
            |> shouldEqual (set [ "agent-a"; "agent-b" ])
        | Error e -> Assert.Fail($"Merged task list failed: {e}"))

[<Test>]
let ``numeric task ID updates preserve existing slugged HTML`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        let htmlPath = Path.Combine(folder, "305-component-authority.html")
        File.WriteAllText(htmlPath, "<h1>Component authority</h1>")
        GitCommandTests.expectGitSuccess context [ "add"; "docs/tasks/305-component-authority.html" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Describe task 305" ]
        match updateTask
            { ProjectName = "Project1"; TaskId = "305"; ExpectedRevision = None
              Title = Some "Component authority"; Status = Some "active"; Dependencies = None; DescriptionCommit = None } context with
        | Ok outcome ->
            outcome.Task.Task.Id |> shouldEqual "305"
            outcome.Task.Task.DescriptionPath |> shouldEqual "docs/tasks/305-component-authority.html"
            File.ReadAllText(htmlPath) |> shouldEqual "<h1>Component authority</h1>"
            File.Exists(Path.Combine(folder, "305.json")) |> shouldEqual true
            match getTaskHistory { ProjectName = "Project1"; TaskId = "305"; Limit = Some 10 } context with
            | Ok history ->
                history.Contains("Describe task 305") |> shouldEqual true
                history.Contains("<h1>Component authority</h1>") |> shouldEqual true
            | Error e -> Assert.Fail($"History missed slugged HTML: {e}")
        | Error e -> Assert.Fail($"Numeric task lookup failed: {e}"))

[<Test>]
let ``task owner and dependency filters reflect committed metadata`` () =
    withProject (fun _ _ context ->
        let created =
            match createTask
                { ProjectName = "Project1"; TaskId = Some "filtered"; Title = "Filtered"
                  Html = "<h1>Filtered</h1>"; Dependencies = [ "parent" ] } context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Create failed: {e}"
        match claimTask
            { ProjectName = "Project1"; TaskId = "filtered"; ExpectedRevision = created.Task.Task.Revision
              Owner = "agent-a"; AllowReassign = false } context with
        | Error e -> Assert.Fail($"Claim failed: {e}")
        | Ok _ -> ()
        match listTasks
            { ProjectName = "Project1"; Status = Some "active"
              Owner = Some "agent-a"; DependsOn = Some "parent" } context with
        | Ok [task] ->
            task.Id |> shouldEqual "filtered"
            task.Dependencies |> shouldEqual [ "parent" ]
            task.Owner |> shouldEqual (Some "agent-a")
        | other -> Assert.Fail($"Expected filtered task: {other}")
        match listTasks
            { ProjectName = "Project1"; Status = None
              Owner = Some "agent-b"; DependsOn = None } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Unexpected task matched owner: {other}"))

[<Test>]
let ``interrupted task creation can recover matching HTML without rewriting it`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        let html = Path.Combine(folder, "interrupted.html")
        File.WriteAllText(html, "<h1>Interrupted</h1>")
        match createTask
            { ProjectName = "Project1"; TaskId = Some "interrupted"
              Title = "Interrupted"; Html = "<h1>Interrupted</h1>"; Dependencies = [] } context with
        | Ok result ->
            result.Task.Task.HasState |> shouldEqual true
            File.ReadAllText(html) |> shouldEqual "<h1>Interrupted</h1>"
            result.ChangedPaths.Length |> shouldEqual 2
        | Error e -> Assert.Fail($"Interrupted task recovery failed: {e}"))

[<Test>]
let ``cross-worktree task view includes branch-local tasks absent from main`` () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        for name in [ "agent-a"; "agent-b" ] do
            match Client.Worktrees.createWorktree
                { ProjectName = "Project1"; Name = name; Branch = "feature/" + name; BaseRef = "HEAD" } context with
            | Error e -> Assert.Fail($"Could not create {name}: {e}")
            | Ok _ -> ()
            match createTask
                { ProjectName = "Project1::" + name; TaskId = Some name; Title = name
                  Html = "<h1>" + name + "</h1>"; Dependencies = [] } context with
            | Error e -> Assert.Fail($"Could not create task in {name}: {e}")
            | Ok _ -> ()
        match compareWorktreeTasks { ProjectName = "Project1" } context with
        | Error e -> Assert.Fail($"Compare failed: {e}")
        | Ok groups ->
            groups.Length |> shouldEqual 3
            groups |> List.forall (fun group -> group.Error.IsNone) |> shouldEqual true
            let main = groups |> List.find (fun group -> group.Worktree.IsMain)
            main.Tasks.IsEmpty |> shouldEqual true
            let ids =
                groups |> List.filter (fun group -> not group.Worktree.IsMain)
                |> List.collect (fun group -> group.Tasks |> List.map (fun task -> task.Id))
                |> Set.ofList
            ids |> shouldEqual (set [ "agent-a"; "agent-b" ]))

[<Test>]
let ``task update refuses and preserves unstaged JSON edits`` () =
    withProject (fun _ project context ->
        match createTask
            { ProjectName = "Project1"; TaskId = Some "unstaged"; Title = "Original"
              Html = "<h1>Unstaged</h1>"; Dependencies = [] } context with
        | Error e -> Assert.Fail($"Create failed: {e}")
        | Ok created ->
            let json = Path.Combine(project, "docs", "tasks", "unstaged.json")
            let original = File.ReadAllText(json)
            let manual = original.Replace("open", "blocked")
            File.WriteAllText(json, manual)
            let command =
                { ProjectName = "Project1"; TaskId = "unstaged"
                  ExpectedRevision = None; Title = Some "Agent update"
                  Status = Some "active"; Dependencies = None; DescriptionCommit = None }
            match updateTask command context with
            | Error _ -> File.ReadAllText(json) |> shouldEqual manual
            | Ok _ -> Assert.Fail("Task update overwrote unstaged JSON."))

[<Test>]
let ``date-prefixed task IDs retain year month day and do not collide`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        let ids = [ "2026-10-24-client-configuration"; "2026-10-24-runpod-benchmark-lifecycle" ]
        for id in ids do
            File.WriteAllText(Path.Combine(folder, id + ".html"), "<h1>" + id + "</h1>")
            let json = """{"schemaVersion":1,"id":"TASK_ID","title":"Fixture","status":"open"}""".Replace("TASK_ID", id)
            File.WriteAllText(Path.Combine(folder, id + ".json"), json)
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None } context with
        | Ok tasks ->
            (tasks |> List.map (fun task -> task.Id)) |> shouldEqual ids
            tasks |> List.forall (fun task -> task.Diagnostics.IsEmpty && task.HasState) |> shouldEqual true
        | Error error -> Assert.Fail($"Date task discovery failed: {error}")
        for id in ids do
            match getTask { ProjectName = "Project1"; TaskId = id } context with
            | Ok task ->
                task.Task.Id |> shouldEqual id
                task.Task.DescriptionPath |> shouldEqual ("docs/tasks/" + id + ".html")
                task.Task.StatePath |> shouldEqual ("docs/tasks/" + id + ".json")
            | Error error -> Assert.Fail($"Cannot get date task {id}: {error}")
        match getTask { ProjectName = "Project1"; TaskId = "2026" } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Date tasks were incorrectly aliased to year 2026."))

[<Test>]
let ``legacy in_progress status reads as active without rewriting source JSON`` () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        let id = "2026-10-24-runpod-benchmark-lifecycle"
        File.WriteAllText(Path.Combine(folder, id + ".html"), "<h1>Runpod lifecycle</h1>")
        let path = Path.Combine(folder, id + ".json")
        let original = """{"schemaVersion":1,"id":"2026-10-24-runpod-benchmark-lifecycle","title":"Runpod lifecycle","status":"in_progress","customField":"preserve"}"""
        File.WriteAllText(path, original)
        for requestedStatus in [ "active"; "in_progress" ] do
            match listTasks { ProjectName = "Project1"; Status = Some requestedStatus; Owner = None; DependsOn = None } context with
            | Ok [task] ->
                task.Id |> shouldEqual id
                task.Status |> shouldEqual "active"
                task.Diagnostics |> shouldEqual []
            | other -> Assert.Fail($"Legacy status filter {requestedStatus} failed: {other}")
        match getTask { ProjectName = "Project1"; TaskId = id } context with
        | Ok task ->
            task.Task.Status |> shouldEqual "active"
            task.StateJson |> shouldEqual (Some original)
        | Error error -> Assert.Fail($"Legacy status get failed: {error}")
        File.ReadAllText(path) |> shouldEqual original
        GitCommandTests.expectGitSuccess context [ "add"; "docs/tasks/" + id + ".html"; "docs/tasks/" + id + ".json" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Existing Runpod task checkpoint" ]
        match updateTask
            { ProjectName = "Project1"; TaskId = id; ExpectedRevision = None
              Title = None; Status = Some "blocked"; Dependencies = None; DescriptionCommit = None } context with
        | Ok updated ->
            updated.Task.Task.Status |> shouldEqual "blocked"
            updated.Task.StateJson.Value.Contains("preserve") |> shouldEqual true
        | Error error -> Assert.Fail($"Cannot update legacy status task: {error}"))
