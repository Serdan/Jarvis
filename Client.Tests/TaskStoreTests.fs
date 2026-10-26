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
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
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
        match listTasks { ProjectName = "Project1"; Status = Some "active"; Owner = None; DependsOn = None; NextActor = None } context with
        | Ok [item] ->
            item.Title |> shouldEqual "Input"
            item.Revision.IsSome |> shouldEqual true
            item.Diagnostics.IsEmpty |> shouldEqual true
        | other -> Assert.Fail($"Expected active task: {other}")
        match listTasks { ProjectName = "Project1"; Status = Some "completed"; Owner = None; DependsOn = None; NextActor = None } context with
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
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
        | Ok [item] -> item.Diagnostics.IsEmpty |> shouldEqual false
        | other -> Assert.Fail($"Malformed task omitted: {other}"))

[<Test>]
let ``task directory traversal is rejected`` () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, ".jarvis.json"), """{"taskDirectory":"../outside"}""")
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
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
        let change = { ProjectName = "Project1"; TaskId = "update"; ExpectedRevision = oldRevision; Title = None; Status = Some "active"; Dependencies = None; DescriptionCommit = None; AssignmentInstructions = None; AssignmentBaseCommit = None; ExpectedAssignmentRevision = None; NextActor = None; ActorId = None }
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
              Status = "active"; Summary = "Implemented first slice"; NextAction = "Verify"; Verified = [ "unit tests" ]; Blocker = None; LastCommit = None; DescriptionCommit = None; AcknowledgedAssignmentRevision = None; ActorId = None }
        let progressed =
            match checkpointTask checkpoint context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Checkpoint failed: {e}"
        progressed.Task.StateJson.Value.Contains("Verify") |> shouldEqual true
        let completed =
            match completeTask
                { ProjectName = "Project1"; TaskId = "chain"; ExpectedRevision = progressed.Task.Task.Revision
                  Verified = [ "unit tests" ]; FinalCommit = Some "abc123"; AcknowledgedAssignmentRevision = None; ActorId = None } context with
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
        match updateTask { ProjectName = "Project1"; TaskId = "legacy"; ExpectedRevision = None; Title = Some "Legacy task"; Status = Some "blocked"; Dependencies = None; DescriptionCommit = None; AssignmentInstructions = None; AssignmentBaseCommit = None; ExpectedAssignmentRevision = None; NextActor = None; ActorId = None } context with
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
            match updateTask { ProjectName = "Project1"; TaskId = "indexed"; ExpectedRevision = None; Title = None; Status = Some "active"; Dependencies = None; DescriptionCommit = None; AssignmentInstructions = None; AssignmentBaseCommit = None; ExpectedAssignmentRevision = None; NextActor = None; ActorId = None } context with
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
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Feature tasks leaked into main: {other}")
        GitCommandTests.expectGitSuccess context [ "merge"; "--no-ff"; "feature/agent-a"; "-m"; "Merge agent-a" ]
        GitCommandTests.expectGitSuccess context [ "merge"; "--no-ff"; "feature/agent-b"; "-m"; "Merge agent-b" ]
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
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
              Title = Some "Component authority"; Status = Some "active"; Dependencies = None; DescriptionCommit = None; AssignmentInstructions = None; AssignmentBaseCommit = None; ExpectedAssignmentRevision = None; NextActor = None; ActorId = None } context with
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
              Owner = Some "agent-a"; DependsOn = Some "parent"; NextActor = None } context with
        | Ok [task] ->
            task.Id |> shouldEqual "filtered"
            task.Dependencies |> shouldEqual [ "parent" ]
            task.Owner |> shouldEqual (Some "agent-a")
        | other -> Assert.Fail($"Expected filtered task: {other}")
        match listTasks
            { ProjectName = "Project1"; Status = None
              Owner = Some "agent-b"; DependsOn = None; NextActor = None } context with
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
        match compareWorktreeTasks { ProjectName = "Project1"; NextActor = None } context with
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
                  Status = Some "active"; Dependencies = None; DescriptionCommit = None; AssignmentInstructions = None; AssignmentBaseCommit = None; ExpectedAssignmentRevision = None; NextActor = None; ActorId = None }
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
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
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
            match listTasks { ProjectName = "Project1"; Status = Some requestedStatus; Owner = None; DependsOn = None; NextActor = None } context with
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
              Title = None; Status = Some "blocked"; Dependencies = None; DescriptionCommit = None; AssignmentInstructions = None; AssignmentBaseCommit = None; ExpectedAssignmentRevision = None; NextActor = None; ActorId = None } context with
        | Ok updated ->
            updated.Task.Task.Status |> shouldEqual "blocked"
            updated.Task.StateJson.Value.Contains("preserve") |> shouldEqual true
        | Error error -> Assert.Fail($"Cannot update legacy status task: {error}"))

let private jsonString (node: System.Text.Json.Nodes.JsonNode) (path: string list) =
    let result = path |> List.fold (fun (current: System.Text.Json.Nodes.JsonNode) segment -> current[segment]) node
    result.GetValue<string>()

[<Test>]
let ``reviewer assignment preserves agent claim and checkpoint and requires exact acknowledgement`` () =
    withProject (fun _ _ context ->
        let projectName = "Project1"
        let taskId = "review-handoff"
        let created =
            match createTask
                { ProjectName = projectName; TaskId = Some taskId
                  Title = "Reviewer handoff"; Html = "<h1>Reviewer handoff</h1>"; Dependencies = [] } context with
            | Ok value -> value
            | Error e -> failwith $"Create failed: {e}"
        let claimed =
            match claimTask
                { ProjectName = projectName; TaskId = taskId
                  ExpectedRevision = created.Task.Task.Revision
                  Owner = "agent-6"; AllowReassign = false } context with
            | Ok value -> value
            | Error e -> failwith $"Claim failed: {e}"
        let original =
            match checkpointTask
                { ProjectName = projectName; TaskId = taskId
                  ExpectedRevision = claimed.Task.Task.Revision
                  Status = "active"; Summary = "Storage passes tests"
                  NextAction = "Review checkpoint"; Verified = [ "rollback tested" ]
                  Blocker = None; LastCommit = Some "abc"; DescriptionCommit = None
                  AcknowledgedAssignmentRevision = None; ActorId = Some "agent-6" } context with
            | Ok value -> value
            | Error e -> failwith $"Initial checkpoint failed: {e}"
        original.Task.Task.NextActor |> shouldEqual (Some "reviewer")
        let assigned =
            match updateTask
                { ProjectName = projectName; TaskId = taskId
                  ExpectedRevision = original.Task.Task.Revision
                  Title = None; Status = None; Dependencies = None; DescriptionCommit = None
                  AssignmentInstructions = Some "Integrate the real position writer"
                  AssignmentBaseCommit = Some "base123"
                  ExpectedAssignmentRevision = None; NextActor = None; ActorId = Some "orchestrator" } context with
            | Ok value -> value
            | Error e -> failwith $"Assign failed: {e}"
        assigned.Task.Task.Owner |> shouldEqual (Some "agent-6")
        assigned.Task.Task.Status |> shouldEqual "active"
        assigned.Task.Task.NextActor |> shouldEqual (Some "agent")
        assigned.Task.Task.LastUpdatedBy |> shouldEqual (Some "reviewer:orchestrator")
        let firstRevision = assigned.Task.Task.AssignmentRevision.Value
        let assignedState = System.Text.Json.Nodes.JsonNode.Parse(assigned.Task.StateJson.Value)
        jsonString assignedState [ "checkpoint"; "summary" ] |> shouldEqual "Storage passes tests"
        assignedState.ToJsonString().Contains("rollback tested") |> shouldEqual true
        jsonString assignedState [ "assignment"; "instructions" ] |> shouldEqual "Integrate the real position writer"
        jsonString assignedState [ "assignment"; "baseCommit" ] |> shouldEqual "base123"
        match listTasks
            { ProjectName = projectName; Status = Some "active"; Owner = Some "agent-6"
              DependsOn = None; NextActor = Some "agent" } context with
        | Ok [ item ] -> item.Id |> shouldEqual taskId
        | other -> Assert.Fail($"Expected agent handoff task: {other}")
        let checkpoint ack revision =
            checkpointTask
                { ProjectName = projectName; TaskId = taskId
                  ExpectedRevision = revision; Status = "active"
                  Summary = "Writer integrated"; NextAction = "Review implementation"
                  Verified = [ "movement tests pass" ]
                  Blocker = None; LastCommit = Some "def"; DescriptionCommit = None
                  AcknowledgedAssignmentRevision = ack; ActorId = Some "agent-6" } context
        match checkpoint None assigned.Task.Task.Revision with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Missing assignment acknowledgement was accepted")
        match checkpoint (Some "obsolete") assigned.Task.Task.Revision with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Stale assignment acknowledgement was accepted")
        let acknowledged =
            match checkpoint (Some firstRevision) assigned.Task.Task.Revision with
            | Ok value -> value
            | Error e -> failwith $"Ack failed: {e}"
        acknowledged.Task.Task.NextActor |> shouldEqual (Some "reviewer")
        acknowledged.Task.Task.LastUpdatedBy |> shouldEqual (Some "agent:agent-6")
        let checkpointState = System.Text.Json.Nodes.JsonNode.Parse(acknowledged.Task.StateJson.Value)
        jsonString checkpointState [ "assignment"; "revision" ] |> shouldEqual firstRevision
        jsonString checkpointState [ "checkpoint"; "acknowledgedAssignmentRevision" ] |> shouldEqual firstRevision
        let reassigned =
            match updateTask
                { ProjectName = projectName; TaskId = taskId
                  ExpectedRevision = acknowledged.Task.Task.Revision
                  Title = None; Status = None; Dependencies = None; DescriptionCommit = None
                  AssignmentInstructions = Some "Verify actor ordering as well"
                  AssignmentBaseCommit = None
                  ExpectedAssignmentRevision = Some firstRevision
                  NextActor = None; ActorId = Some "reviewer-1" } context with
            | Ok value -> value
            | Error e -> failwith $"Second assignment failed: {e}"
        let secondRevision = reassigned.Task.Task.AssignmentRevision.Value
        (secondRevision <> firstRevision) |> shouldEqual true
        reassigned.Task.Task.NextActor |> shouldEqual (Some "agent")
        reassigned.Task.Task.Owner |> shouldEqual (Some "agent-6")
        let previousCheckpoint = System.Text.Json.Nodes.JsonNode.Parse(reassigned.Task.StateJson.Value)
        jsonString previousCheckpoint [ "checkpoint"; "summary" ] |> shouldEqual "Writer integrated"
        match checkpoint (Some firstRevision) reassigned.Task.Task.Revision with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Checkpoint for previous assignment was accepted")
        match updateTask
            { ProjectName = projectName; TaskId = taskId
              ExpectedRevision = reassigned.Task.Task.Revision
              Title = None; Status = None; Dependencies = None; DescriptionCommit = None
              AssignmentInstructions = Some "Do not commit this"
              AssignmentBaseCommit = None; ExpectedAssignmentRevision = Some firstRevision
              NextActor = None; ActorId = Some "reviewer-1" } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Stale reviewer assignment revision was accepted")
        let finalCheckpoint =
            match checkpoint (Some secondRevision) reassigned.Task.Task.Revision with
            | Ok value -> value
            | Error e -> failwith $"Second acknowledgement failed: {e}"
        match completeTask
            { ProjectName = projectName; TaskId = taskId
              ExpectedRevision = finalCheckpoint.Task.Task.Revision
              Verified = [ "reviewed" ]; FinalCommit = Some "final123"
              AcknowledgedAssignmentRevision = Some secondRevision; ActorId = Some "agent-6" } context with
        | Error e -> Assert.Fail($"Complete task failed: {e}")
        | Ok result ->
            result.Task.Task.Status |> shouldEqual "completed"
            result.Task.Task.NextActor |> shouldEqual None
            result.Task.Task.Owner |> shouldEqual (Some "agent-6")
        match listTasks
            { ProjectName = projectName; Status = Some "completed"; Owner = None
              DependsOn = None; NextActor = Some "reviewer" } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Completed task must not remain pending review: {other}")
        match listTasks
            { ProjectName = projectName; Status = Some "completed"; Owner = None
              DependsOn = None; NextActor = None } context with
        | Ok [ item ] ->
            item.Id |> shouldEqual taskId
            item.Owner |> shouldEqual (Some "agent-6")
            item.NextActor |> shouldEqual None
        | other -> Assert.Fail($"Completed task missing from unfiltered historical list: {other}"))

[<Test>]
let ``invalid reviewer handoff fails without changing ownership or Git history`` () =
    withProject (fun _ _ context ->
        let projectName = "Project1"
        let taskId = "invalid-handoff"
        let created =
            match createTask
                { ProjectName = projectName; TaskId = Some taskId
                  Title = "Invalid assignment"; Html = "<h1>Invalid</h1>"; Dependencies = [] } context with
            | Ok value -> value
            | Error e -> failwith $"Create failed: {e}"
        let revision = created.Task.Task.Revision
        let head = GitCommandTests.gitOutput context [ "rev-parse"; "HEAD" ]
        let command =
            { ProjectName = projectName; TaskId = taskId; ExpectedRevision = revision
              Title = None; Status = None; Dependencies = None; DescriptionCommit = None
              AssignmentInstructions = Some "Implement this"; AssignmentBaseCommit = None
              ExpectedAssignmentRevision = None; NextActor = None; ActorId = None }
        match updateTask command context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Assignment without reviewer ID was accepted")
        match updateTask { command with ActorId = Some "orchestrator"; NextActor = Some "not-a-turn" } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Invalid next actor was accepted")
        match updateTask
            { command with AssignmentInstructions = None; NextActor = Some "reviewer" } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Turn update without reviewer ID was accepted")
        let after = GitCommandTests.gitOutput context [ "rev-parse"; "HEAD" ]
        head |> shouldEqual after
        match getTask { ProjectName = projectName; TaskId = taskId } context with
        | Ok details ->
            details.Task.Revision |> shouldEqual revision
            details.Task.Owner |> shouldEqual None
            details.Task.AssignmentRevision |> shouldEqual None
        | Error e -> Assert.Fail($"Cannot re-read task: {e}"))

[<Test>]
let missingTaskDirectoryIsEmptyUntilCreation () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.Exists folder |> shouldEqual false
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Missing task folder should list as empty: {other}")
        Directory.Exists folder |> shouldEqual false
        match createTask
            { ProjectName = "Project1"; TaskId = Some "new-directory"
              Title = "New"; Html = "<h1>New</h1>"; Dependencies = [] } context with
        | Ok _ -> Directory.Exists folder |> shouldEqual true
        | Error err -> Assert.Fail($"Create should initialize task folder: {err}"))

[<Test>]
let tasksWithoutGitRepositoryAllowReadsButNotWrites () =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        File.WriteAllText(Path.Combine(folder, "legacy.html"), "<h1>Legacy</h1>")
        match listTasks { ProjectName = "Project1"; Status = None; Owner = None; DependsOn = None; NextActor = None } context with
        | Ok [item] -> item.Id |> shouldEqual "legacy"
        | other -> Assert.Fail($"Read-only tasks should work without Git: {other}")
        match createTask
            { ProjectName = "Project1"; TaskId = Some "no-repo"
              Title = "No repo"; Html = "<p>no repo</p>"; Dependencies = [] } context with
        | Error(Client.ValidationError message) ->
            message.Contains("not a Git working repository") |> shouldEqual true
        | other -> Assert.Fail($"Expected explicit non-repository error: {other}")
        File.Exists(Path.Combine(folder, "no-repo.html")) |> shouldEqual false
        File.Exists(Path.Combine(folder, "no-repo.json")) |> shouldEqual false
        match updateTask
            { ProjectName = "Project1"; TaskId = "legacy"; ExpectedRevision = None
              Title = None; Status = Some "active"; Dependencies = None
              DescriptionCommit = None; AssignmentInstructions = None
              AssignmentBaseCommit = None; ExpectedAssignmentRevision = None
              NextActor = None; ActorId = None } context with
        | Error(Client.ValidationError message) ->
            message.Contains("not a Git working repository") |> shouldEqual true
        | other -> Assert.Fail($"Existing task update should report non-repository: {other}")
        File.Exists(Path.Combine(folder, "legacy.json")) |> shouldEqual false
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Test; NonParallelizable>]
let missingGitExecutableFailsBeforeWritingFiles () =
    withProject (fun _ project context ->
        let previousPath = Environment.GetEnvironmentVariable("PATH")
        let absentPath = Path.Combine(project, "no-git-executables")
        Directory.CreateDirectory absentPath |> ignore
        try
            Environment.SetEnvironmentVariable("PATH", absentPath)
            match createTask
                { ProjectName = "Project1"; TaskId = Some "git-missing"
                  Title = "No Git"; Html = "<h1>No Git</h1>"; Dependencies = [] } context with
            | Error(Client.ValidationError message) ->
                message.Contains("Git is not installed") |> shouldEqual true
            | other -> Assert.Fail($"Expected executable-not-found error: {other}")
        finally Environment.SetEnvironmentVariable("PATH", previousPath)
        Directory.Exists(Path.Combine(project, "docs", "tasks")) |> shouldEqual false)

let private rejectCommitHook project =
    if OperatingSystem.IsWindows() then Assert.Ignore("Shell hook fixture runs on Unix")
    let hooks = Path.Combine(project, ".git", "hooks")
    Directory.CreateDirectory hooks |> ignore
    let hook = Path.Combine(hooks, "pre-commit")
    File.WriteAllText(hook, "#!/bin/sh\nprintf 'rejected by fixture hook\\n' >&2\nexit 1\n")
    File.SetUnixFileMode(hook, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    hook

[<Test>]
let failedFirstTaskCommitRestoresFilesAndIndexAndCanRetry () =
    withProject (fun _ project context ->
        let unrelated = Path.Combine(project, "unrelated.txt")
        File.WriteAllText(unrelated, "user staged file")
        GitCommandTests.expectGitSuccess context [ "add"; "unrelated.txt" ]
        let hook = rejectCommitHook project
        let cmd =
            { ProjectName = "Project1"; TaskId = Some "retry-first"
              Title = "Retry first"; Html = "<h1>Retry</h1>"; Dependencies = [] }
        match createTask cmd context with
        | Error(Client.ValidationError message) ->
            message.Contains("restored to their previous state") |> shouldEqual true
        | other -> Assert.Fail($"Expected recoverable commit failure: {other}")
        File.Exists(Path.Combine(project, "docs", "tasks", "retry-first.html")) |> shouldEqual false
        File.Exists(Path.Combine(project, "docs", "tasks", "retry-first.json")) |> shouldEqual false
        GitCommandTests.gitOutput context [ "diff"; "--cached"; "--name-only" ] |> shouldEqual "unrelated.txt"
        File.Delete hook
        match createTask cmd context with
        | Error error -> Assert.Fail($"Retry failed after removing hook: {error}")
        | Ok committed ->
            committed.CommitHash.Length |> shouldEqual 40
            committed.Task.Task.Id |> shouldEqual "retry-first"
        GitCommandTests.gitOutput context [ "diff"; "--cached"; "--name-only" ] |> shouldEqual "unrelated.txt")

[<Test>]
let failedTaskUpdateRestoresOriginalBytesAndCanRetry () =
    withProject (fun _ project context ->
        let created =
            match createTask
                { ProjectName = "Project1"; TaskId = Some "retry-update"
                  Title = "Original"; Html = "<h1>Original</h1>"; Dependencies = [] } context with
            | Ok value -> value
            | Error e -> failwith $"Setup failed: {e}"
        let originalHead = GitCommandTests.gitOutput context [ "rev-parse"; "HEAD" ]
        let json = Path.Combine(project, "docs", "tasks", "retry-update.json")
        let originalBytes = File.ReadAllBytes json
        File.WriteAllText(Path.Combine(project, "unrelated.txt"), "staged")
        GitCommandTests.expectGitSuccess context [ "add"; "unrelated.txt" ]
        let hook = rejectCommitHook project
        let change =
            { ProjectName = "Project1"; TaskId = "retry-update"; ExpectedRevision = created.Task.Task.Revision
              Title = Some "Revised"; Status = Some "active"; Dependencies = None
              DescriptionCommit = None; AssignmentInstructions = None
              AssignmentBaseCommit = None; ExpectedAssignmentRevision = None
              NextActor = None; ActorId = None }
        match updateTask change context with
        | Error(Client.ValidationError message) ->
            message.Contains("restored to their previous state") |> shouldEqual true
        | other -> Assert.Fail($"Expected rollback on update: {other}")
        File.ReadAllBytes json |> shouldEqual originalBytes
        GitCommandTests.gitOutput context [ "rev-parse"; "HEAD" ] |> shouldEqual originalHead
        GitCommandTests.gitOutput context [ "diff"; "--cached"; "--name-only" ] |> shouldEqual "unrelated.txt"
        File.Delete hook
        match updateTask change context with
        | Ok updated -> updated.Task.Task.Title |> shouldEqual "Revised"
        | Error e -> Assert.Fail($"Retry failed after hook removal: {e}")
        GitCommandTests.gitOutput context [ "diff"; "--cached"; "--name-only" ] |> shouldEqual "unrelated.txt")

[<Test>]
let failedLazyMetadataCommitPreservesExistingHTML () =
    withProject (fun _ project context ->
        let folder = Path.Combine(project, "docs", "tasks")
        Directory.CreateDirectory folder |> ignore
        let html = Path.Combine(folder, "lazy.html")
        File.WriteAllText(html, "<h1>Existing description</h1>")
        GitCommandTests.expectGitSuccess context [ "add"; "docs/tasks/lazy.html" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Original task HTML" ]
        let hook = rejectCommitHook project
        let cmd =
            { ProjectName = "Project1"; TaskId = "lazy"; ExpectedRevision = None
              Title = Some "Metadata"; Status = Some "active"; Dependencies = None
              DescriptionCommit = None; AssignmentInstructions = None
              AssignmentBaseCommit = None; ExpectedAssignmentRevision = None
              NextActor = None; ActorId = None }
        match updateTask cmd context with
        | Error(Client.ValidationError message) ->
            message.Contains("restored to their previous state") |> shouldEqual true
        | other -> Assert.Fail($"Expected lazy state rollback: {other}")
        File.ReadAllText html |> shouldEqual "<h1>Existing description</h1>"
        File.Exists(Path.Combine(folder, "lazy.json")) |> shouldEqual false
        GitCommandTests.gitOutput context [ "status"; "--porcelain"; "--"; "docs/tasks" ] |> shouldEqual ""
        File.Delete hook
        match updateTask cmd context with
        | Ok result -> result.Task.Task.Status |> shouldEqual "active"
        | Error e -> Assert.Fail($"Lazy task retry failed: {e}"))

[<Test>]
let failedHookThatChangesTaskFilePreservesUserChanges () =
    withProject (fun _ project context ->
        let created =
            match createTask
                { ProjectName = "Project1"; TaskId = Some "hook-change"
                  Title = "Original"; Html = "<p>Original</p>"; Dependencies = [] } context with
            | Ok item -> item
            | Error e -> failwith $"Setup failed: {e}"
        let hook = rejectCommitHook project
        File.WriteAllText(hook, "#!/bin/sh\nprintf '\\nmanual-hook-change\\n' >> docs/tasks/hook-change.json\nexit 1\n")
        let json = Path.Combine(project, "docs", "tasks", "hook-change.json")
        let cmd =
            { ProjectName = "Project1"; TaskId = "hook-change"
              ExpectedRevision = created.Task.Task.Revision
              Title = Some "Updated"; Status = None; Dependencies = None
              DescriptionCommit = None; AssignmentInstructions = None
              AssignmentBaseCommit = None; ExpectedAssignmentRevision = None
              NextActor = None; ActorId = None }
        match updateTask cmd context with
        | Error(Client.ValidationError message) ->
            message.Contains("no rollback was performed") |> shouldEqual true
        | other -> Assert.Fail($"External hook edits should be preserved: {other}")
        File.ReadAllText(json).Contains("manual-hook-change") |> shouldEqual true
        File.Delete hook)

[<Test>]
let compareWorktreeTasksFiltersReviewerAndAgentHandoffs () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        for name, turn in [ "ready-agent", "agent"; "ready-reviewer", "reviewer"; "done-branch", "completed" ] do
            match Client.Worktrees.createWorktree
                { ProjectName = "Project1"; Name = name; Branch = "feature/" + name; BaseRef = "HEAD" } context with
            | Error error -> Assert.Fail($"Could not create {name}: {error}")
            | Ok _ -> ()
            let selector = "Project1::" + name
            let created =
                match createTask
                    { ProjectName = selector; TaskId = Some name
                      Title = name; Html = "<h1>" + name + "</h1>"; Dependencies = [] } context with
                | Ok result -> result
                | Error e -> failwith $"Create failed: {e}"
            let claimed =
                match claimTask
                    { ProjectName = selector; TaskId = name
                      ExpectedRevision = created.Task.Task.Revision
                      Owner = (if name = "ready-reviewer" then "agent-7" else "agent-" + name); AllowReassign = false } context with
                | Ok result -> result
                | Error e -> failwith $"Claim failed: {e}"
            if turn = "reviewer" then
                match checkpointTask
                    { ProjectName = selector; TaskId = name; ExpectedRevision = claimed.Task.Task.Revision
                      Status = "active"; Summary = "Slice ready"; NextAction = "Review"
                      Verified = [ "tests" ]; Blocker = None; LastCommit = Some "abc"
                      DescriptionCommit = None; AcknowledgedAssignmentRevision = None
                      ActorId = Some "agent-7" } context with
                | Ok _ -> ()
                | Error e -> Assert.Fail($"Checkpoint failed: {e}")
            elif turn = "completed" then
                match completeTask
                    { ProjectName = selector; TaskId = name; ExpectedRevision = claimed.Task.Task.Revision
                      Verified = [ "accepted" ]; FinalCommit = Some "def"
                      AcknowledgedAssignmentRevision = None; ActorId = Some "agent-7" } context with
                | Ok result -> result.Task.Task.NextActor |> shouldEqual None
                | Error e -> Assert.Fail($"Complete failed: {e}")
        let compare turn =
            match compareWorktreeTasks { ProjectName = "Project1"; NextActor = turn } context with
            | Ok groups ->
                groups.Length |> shouldEqual 4
                groups |> List.forall (fun group -> group.Error.IsNone) |> shouldEqual true
                groups
            | Error e -> failwith $"Compare failed: {e}"
        let all =
            compare None |> List.collect (fun group -> group.Tasks |> List.map (fun task -> task.Id))
            |> Set.ofList
        all |> shouldEqual (set [ "ready-agent"; "ready-reviewer"; "done-branch" ])
        let review =
            compare (Some "reviewer")
            |> List.collect (fun group -> group.Tasks |> List.map (fun task -> group.Worktree, task))
        match review with
        | [ (worktree, task) ] ->
            worktree.Branch |> shouldEqual (Some "feature/ready-reviewer")
            worktree.Path.EndsWith("ready-reviewer") |> shouldEqual true
            task.Id |> shouldEqual "ready-reviewer"
            task.Owner |> shouldEqual (Some "agent-7")
            task.Revision.IsSome |> shouldEqual true
            task.NextActor |> shouldEqual (Some "reviewer")
        | other -> Assert.Fail($"Expected one reviewer task, got: {other}")
        let agent =
            compare (Some "agent")
            |> List.collect (fun group -> group.Tasks |> List.map (fun task -> group.Worktree.Branch, task.Id, task.NextActor))
        agent |> shouldEqual [ Some "feature/ready-agent", "ready-agent", Some "agent" ])

[<Test>]
let completedHistoricalTaskNeverAppearsInHandoffFilters () =
    withProject (fun _ project context ->
        let created =
            match createTask
                { ProjectName = "Project1"; TaskId = Some "legacy-completed"
                  Title = "Old complete"; Html = "<h1>Old</h1>"; Dependencies = [] } context with
            | Ok result -> result
            | Error error -> failwith $"Create failed: {error}"
        let json = Path.Combine(project, "docs", "tasks", "legacy-completed.json")
        let state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(json))
        state["status"] <- System.Text.Json.Nodes.JsonValue.Create("completed")
        state["nextActor"] <- System.Text.Json.Nodes.JsonValue.Create("reviewer")
        File.WriteAllText(json, state.ToJsonString())
        match listTasks
            { ProjectName = "Project1"; Status = None; Owner = None
              DependsOn = None; NextActor = Some "reviewer" } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Completed task leaked into reviewer queue: {other}")
        match compareWorktreeTasks { ProjectName = "Project1"; NextActor = Some "reviewer" } context with
        | Ok groups when groups |> List.forall (fun group -> group.Tasks.IsEmpty) -> ()
        | other -> Assert.Fail($"Cross-worktree reviewer queue contains completed task: {other}")
        match listTasks
            { ProjectName = "Project1"; Status = None; Owner = None
              DependsOn = None; NextActor = None } context with
        | Ok [ task ] ->
            task.Id |> shouldEqual "legacy-completed"
            task.Status |> shouldEqual "completed"
            task.NextActor |> shouldEqual (Some "reviewer")
            task.Revision.IsSome |> shouldEqual true
        | other -> Assert.Fail($"Historical task was lost: {other}")
        created.Task.Task.Id |> shouldEqual "legacy-completed")

[<Test>]
let completedTransitionsClearHandoffForEveryTaskMutation () =
    withProject (fun _ _ context ->
        for mode in [ "checkpoint"; "metadata"; "completion" ] do
            let created =
                match createTask
                    { ProjectName = "Project1"; TaskId = Some mode
                      Title = mode; Html = "<h1>" + mode + "</h1>"; Dependencies = [] } context with
                | Ok result -> result
                | Error e -> failwith $"Create failed: {e}"
            let claimed =
                match claimTask
                    { ProjectName = "Project1"; TaskId = mode
                      ExpectedRevision = created.Task.Task.Revision
                      Owner = "agent-6"; AllowReassign = false } context with
                | Ok result -> result
                | Error e -> failwith $"Claim failed: {e}"
            let updated =
                if mode = "checkpoint" then
                    checkpointTask
                        { ProjectName = "Project1"; TaskId = mode
                          ExpectedRevision = claimed.Task.Task.Revision
                          Status = "completed"; Summary = "Accepted"; NextAction = ""
                          Verified = [ "verified" ]; Blocker = None; LastCommit = Some "abc"
                          DescriptionCommit = None; AcknowledgedAssignmentRevision = None
                          ActorId = Some "agent-6" } context
                    |> Result.map (fun value -> value.Task)
                elif mode = "metadata" then
                    updateTask
                        { ProjectName = "Project1"; TaskId = mode
                          ExpectedRevision = claimed.Task.Task.Revision
                          Title = None; Status = Some "completed"; Dependencies = None
                          DescriptionCommit = None; AssignmentInstructions = None
                          AssignmentBaseCommit = None; ExpectedAssignmentRevision = None
                          NextActor = Some "reviewer"; ActorId = Some "reviewer-1" } context
                    |> Result.map (fun value -> value.Task)
                else
                    completeTask
                        { ProjectName = "Project1"; TaskId = mode
                          ExpectedRevision = claimed.Task.Task.Revision
                          Verified = [ "accepted" ]; FinalCommit = Some "final"
                          AcknowledgedAssignmentRevision = None; ActorId = Some "agent-6" } context
                    |> Result.map (fun value -> value.Task)
            match updated with
            | Error error -> Assert.Fail($"Completion via {mode} failed: {error}")
            | Ok details ->
                details.Task.Status |> shouldEqual "completed"
                details.Task.Owner |> shouldEqual (Some "agent-6")
                details.Task.NextActor |> shouldEqual None
        match listTasks
            { ProjectName = "Project1"; Status = None; Owner = None
              DependsOn = None; NextActor = Some "reviewer" } context with
        | Ok [] -> ()
        | other -> Assert.Fail($"Completed tasks in reviewer queue: {other}"))

[<Test>]
let globalAgentAssignmentFindsCurrentWorktreeAfterOldTaskCompleted () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        for name in [ "old"; "next" ] do
            match Client.Worktrees.createWorktree
                { ProjectName = "Project1"; Name = name; Branch = "feature/" + name; BaseRef = "HEAD" } context with
            | Ok _ -> ()
            | Error e -> Assert.Fail($"Cannot create worktree: {e}")
        let make name =
            let selector = "Project1::" + name
            let created =
                match createTask
                    { ProjectName = selector; TaskId = Some name
                      Title = name; Html = "<h1>" + name + "</h1>"; Dependencies = [] } context with
                | Ok task -> task
                | Error e -> failwith $"Create failed: {e}"
            let claimed =
                match claimTask
                    { ProjectName = selector; TaskId = name
                      ExpectedRevision = created.Task.Task.Revision
                      Owner = "Agent 2"; AllowReassign = false } context with
                | Ok task -> task
                | Error e -> failwith $"Claim failed: {e}"
            selector, claimed
        let oldSelector, oldTask = make "old"
        match completeTask
            { ProjectName = oldSelector; TaskId = "old"
              ExpectedRevision = oldTask.Task.Task.Revision
              Verified = [ "integrated" ]; FinalCommit = Some "abc"
              AcknowledgedAssignmentRevision = None; ActorId = Some "Agent 2" } context with
        | Error e -> Assert.Fail($"Complete failed: {e}")
        | Ok _ -> ()
        let nextSelector, nextTask = make "next"
        let assigned =
            match updateTask
                { ProjectName = nextSelector; TaskId = "next"
                  ExpectedRevision = nextTask.Task.Task.Revision
                  Title = None; Status = None; Dependencies = None; DescriptionCommit = None
                  AssignmentInstructions = Some "Implement Task 292"
                  AssignmentBaseCommit = Some "abc123"
                  ExpectedAssignmentRevision = None
                  NextActor = None; ActorId = Some "orchestrator" } context with
            | Ok result -> result
            | Error e -> failwith $"Assign failed: {e}"
        match getAgentAssignment { ProjectName = "Project1::old"; Agent = "Agent 2" } context with
        | Ok inbox ->
            inbox.Worktree.Branch |> shouldEqual (Some "feature/next")
            inbox.Task.Id |> shouldEqual "next"
            inbox.ProjectName |> shouldEqual "Project1::next"
            inbox.Agent |> shouldEqual "Agent 2"
            inbox.Task.Owner |> shouldEqual (Some "Agent 2")
            inbox.Task.Revision |> shouldEqual assigned.Task.Task.Revision
            inbox.AssignmentInstructions |> shouldEqual (Some "Implement Task 292")
            inbox.AssignmentBaseCommit |> shouldEqual (Some "abc123")
            inbox.AssignmentRevision.IsSome |> shouldEqual true
            inbox.Phase |> shouldEqual "Assigned"
        | Error e -> Assert.Fail($"Global assignment lookup failed: {e}"))

[<Test>]
let globalAgentAssignmentRejectsTwoActiveWorktreesAndMissingAgent () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        for name in [ "alpha"; "beta" ] do
            match Client.Worktrees.createWorktree
                { ProjectName = "Project1"; Name = name; Branch = "feature/" + name; BaseRef = "HEAD" } context with
            | Ok _ -> ()
            | Error e -> Assert.Fail($"Cannot create worktree: {e}")
            let selector = "Project1::" + name
            let task =
                match createTask
                    { ProjectName = selector; TaskId = Some name
                      Title = name; Html = "<h1>" + name + "</h1>"; Dependencies = [] } context with
                | Ok task -> task
                | Error e -> failwith $"Create failed: {e}"
            if name = "alpha" then
                match claimTask
                    { ProjectName = selector; TaskId = name
                      ExpectedRevision = task.Task.Task.Revision
                      Owner = "Agent 3"; AllowReassign = false } context with
                | Ok _ -> ()
                | Error e -> Assert.Fail($"Claim failed: {e}")
            else
                // Simulate a legacy conflicting record created before global claim protection.
                let worktree =
                    match Client.Worktrees.listWorktrees { ProjectName = "Project1" } context with
                    | Ok entries -> entries |> List.find (fun entry -> entry.Path.EndsWith(name)) |> fun entry -> entry.Path
                    | Error e -> failwith $"Cannot inspect worktree: {e}"
                let statePath = Path.Combine(worktree, "docs", "tasks", name + ".json")
                let state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))
                let owner = System.Text.Json.Nodes.JsonObject()
                owner["agent"] <- System.Text.Json.Nodes.JsonValue.Create("Agent 3")
                state["owner"] <- owner
                state["status"] <- System.Text.Json.Nodes.JsonValue.Create("active")
                state["nextActor"] <- System.Text.Json.Nodes.JsonValue.Create("agent")
                File.WriteAllText(statePath, state.ToJsonString())
        match getAgentAssignment { ProjectName = "Project1"; Agent = "Agent 3" } context with
        | Error(Client.ValidationError message) ->
            message.Contains("Conflicting active assignments") |> shouldEqual true
            message.Contains("alpha") |> shouldEqual true
            message.Contains("beta") |> shouldEqual true
        | other -> Assert.Fail($"Expected explicit conflicting active assignment, got {other}")
        match getAgentAssignment { ProjectName = "Project1"; Agent = "Agent 4" } context with
        | Error(Client.NotFoundError _) -> ()
        | other -> Assert.Fail($"Expected no assignment for Agent 4, got {other}"))

[<Test>]
let assignTaskCreatesWorktreeAndTracksAcknowledgementWithoutLosingCheckpoint () =
    withProject (fun _ project context ->
        let created =
            match createTask
                { ProjectName = "Project1"; TaskId = Some "292"
                  Title = "Gold pickup"; Html = "<h1>Gold pickup</h1>"; Dependencies = [] } context with
            | Ok value -> value
            | Error e -> failwith $"Create failed: {e}"
        let cmd =
            { ProjectName = "Project1"; Agent = "Agent 2"; TaskId = "292"
              WorktreeName = Some "gold-pickup"; Branch = Some "feature/gold-pickup"
              Instructions = "Implement task 292"; BaseCommit = None
              ExpectedRevision = None; ExpectedAssignmentRevision = None
              ActorId = "orchestrator" }
        let first =
            match assignTask cmd context with
            | Ok value -> value
            | Error error -> failwith $"Assign failed: {error}"
        first.Task.Task.Owner |> shouldEqual (Some "Agent 2")
        first.Task.Task.NextActor |> shouldEqual (Some "agent")
        let selector = "Project1::gold-pickup"
        let firstRevision = first.Task.Task.AssignmentRevision.Value
        match getAgentAssignment { ProjectName = "Project1"; Agent = "Agent 2" } context with
        | Ok inbox ->
            inbox.Task.Id |> shouldEqual "292"
            inbox.ProjectName |> shouldEqual selector
            inbox.Phase |> shouldEqual "Assigned"
        | Error e -> Assert.Fail($"Inbox failed: {e}")
        match acknowledgeTask
            { ProjectName = selector; TaskId = "292"; Agent = "Agent 2"
              AssignmentRevision = firstRevision; ExpectedRevision = first.Task.Task.Revision } context with
        | Error e -> Assert.Fail($"Acknowledgement failed: {e}")
        | Ok ack ->
            let json = System.Text.Json.Nodes.JsonNode.Parse(ack.Task.StateJson.Value)
            json["checkpoint"] = null |> shouldEqual true
        match getAgentAssignment { ProjectName = "Project1"; Agent = "Agent 2" } context with
        | Ok inbox ->
            inbox.Phase |> shouldEqual "Acknowledged"
            inbox.AcknowledgedAssignmentRevision |> shouldEqual (Some firstRevision)
        | Error e -> Assert.Fail($"Acknowledged inbox failed: {e}")
        let received =
            match getTask { ProjectName = selector; TaskId = "292" } context with
            | Ok value -> value
            | Error e -> failwith $"Get acknowledged task failed: {e}"
        match beginTask
            { ProjectName = selector; TaskId = "292"; Agent = "Agent 2"
              AssignmentRevision = firstRevision
              ExpectedRevision = received.Task.Revision } context with
        | Error e -> Assert.Fail($"Begin task failed: {e}")
        | Ok _ -> ()
        match getAgentAssignment { ProjectName = "Project1"; Agent = "Agent 2" } context with
        | Ok inbox -> inbox.Phase |> shouldEqual "Working"
        | Error e -> Assert.Fail($"Working inbox failed: {e}")
        match acknowledgeTask
            { ProjectName = selector; TaskId = "292"; Agent = "Agent 2"
              AssignmentRevision = firstRevision; ExpectedRevision = None } context with
        | Ok _ -> ()
        | Error e -> Assert.Fail($"Repeated receipt failed: {e}")
        match getAgentAssignment { ProjectName = "Project1"; Agent = "Agent 2" } context with
        | Ok inbox -> inbox.Phase |> shouldEqual "Working"
        | Error e -> Assert.Fail($"Repeated receipt regressed work phase: {e}")
        let acknowledged =
            match getTask { ProjectName = selector; TaskId = "292" } context with
            | Ok value -> value
            | Error e -> failwith $"GetTask failed: {e}"
        match acknowledgeTask
            { ProjectName = selector; TaskId = "292"; Agent = "Agent 2"
              AssignmentRevision = "stale"; ExpectedRevision = acknowledged.Task.Revision } context with
        | Error(Client.ValidationError error) ->
            error.Contains("stale") |> shouldEqual true
        | other -> Assert.Fail($"Stale acknowledgment accepted: {other}")
        let checkpointed =
            match checkpointTask
                { ProjectName = selector; TaskId = "292"; ExpectedRevision = acknowledged.Task.Revision
                  Status = "active"; Summary = "Work completed"; NextAction = "Review"
                  Verified = [ "acceptance test" ]; Blocker = None
                  LastCommit = Some "commit-one"; DescriptionCommit = None
                  AcknowledgedAssignmentRevision = Some firstRevision; ActorId = Some "Agent 2" } context with
            | Ok value -> value
            | Error e -> failwith $"Checkpoint failed: {e}"
        let reissued =
            match assignTask
                { cmd with WorktreeName = Some "gold-pickup"; Branch = None
                           Instructions = "Fix remaining issue"
                           ExpectedRevision = checkpointed.Task.Task.Revision
                           ExpectedAssignmentRevision = Some firstRevision } context with
            | Ok value -> value
            | Error e -> failwith $"Reassignment failed: {e}"
        let state = System.Text.Json.Nodes.JsonNode.Parse(reissued.Task.StateJson.Value)
        (state.ToJsonString().Contains("Work completed")) |> shouldEqual true
        reissued.Task.Task.Owner |> shouldEqual (Some "Agent 2")
        (reissued.Task.Task.AssignmentRevision.Value <> firstRevision) |> shouldEqual true
        match getAgentAssignment { ProjectName = "Project1::gold-pickup"; Agent = "Agent 2" } context with
        | Ok inbox ->
            inbox.Phase |> shouldEqual "Assigned"
            inbox.AcknowledgedAssignmentRevision |> shouldEqual None
        | Error e -> Assert.Fail($"Reassigned inbox failed: {e}")
        match getOrchestrationSnapshot { ProjectName = "Project1" } context with
        | Ok snapshot ->
            snapshot.Assignments.Length |> shouldEqual 1
            snapshot.UnacknowledgedTaskCount |> shouldEqual 1
            snapshot.ReviewerTaskCount |> shouldEqual 0
            snapshot.Conflicts.IsEmpty |> shouldEqual true
            snapshot.Worktrees.Length |> shouldEqual 2
        | Error e -> Assert.Fail($"Snapshot failed: {e}")
        created.Task.Task.Id |> shouldEqual "292")

[<Test>]
let globalAssignAndClaimRejectSecondActiveAgentTask () =
    withProject (fun _ _ context ->
        let create id =
            match createTask
                { ProjectName = "Project1"; TaskId = Some id
                  Title = id; Html = "<h1>" + id + "</h1>"; Dependencies = [] } context with
            | Ok result -> result
            | Error e -> failwith $"Create failed: {e}"
        let old = create "301"
        let pending = create "292"
        match claimTask
            { ProjectName = "Project1"; TaskId = "301"
              ExpectedRevision = old.Task.Task.Revision; Owner = "Agent 2"; AllowReassign = false } context with
        | Ok _ -> ()
        | Error e -> Assert.Fail($"Old claim failed: {e}")
        match claimTask
            { ProjectName = "Project1"; TaskId = "292"
              ExpectedRevision = pending.Task.Task.Revision; Owner = "Agent 2"; AllowReassign = false } context with
        | Error(Client.ValidationError error) ->
            error.Contains("Conflicting active global task ownership") |> shouldEqual true
        | other -> Assert.Fail($"Duplicate claim accepted: {other}")
        match assignTask
            { ProjectName = "Project1"; Agent = "Agent 2"; TaskId = "292"
              WorktreeName = None; Branch = None; Instructions = "New work"
              BaseCommit = None; ExpectedRevision = pending.Task.Task.Revision
              ExpectedAssignmentRevision = None; ActorId = "orchestrator" } context with
        | Error(Client.ValidationError error) ->
            error.Contains("Conflicting active global task ownership") |> shouldEqual true
        | other -> Assert.Fail($"Duplicate assignment accepted: {other}")
        match getTask { ProjectName = "Project1"; TaskId = "292" } context with
        | Ok task ->
            task.Task.Owner |> shouldEqual None
            task.Task.Revision |> shouldEqual pending.Task.Task.Revision
        | Error e -> Assert.Fail($"Conflicting claim modified task: {e}"))

[<Test>]
let retireWorktreePreservesTaskOnlyHistoryAndAcceptsPatchEquivalentSource () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, ".gitignore"), "*.cache\n")
        File.WriteAllText(Path.Combine(project, "README.md"), "Initial")
        GitCommandTests.expectGitSuccess context [ "add"; ".gitignore"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        match Client.Worktrees.createWorktree
            { ProjectName = "Project1"; Name = "retirement"
              Branch = "feature/retirement"; BaseRef = "HEAD" } context with
        | Error e -> Assert.Fail($"Could not create worktree: {e}")
        | Ok _ -> ()
        let worktree =
            match Client.Worktrees.listWorktrees { ProjectName = "Project1" } context with
            | Ok entries -> entries |> List.find (fun entry -> entry.Branch = Some "feature/retirement") |> fun entry -> entry.Path
            | Error e -> failwith $"Could not inspect registered worktrees: {e}"
        let task =
            match createTask
                { ProjectName = "Project1::retirement"; TaskId = Some "history"
                  Title = "Task metadata"; Html = "<h1>History</h1>"; Dependencies = [] } context with
            | Ok outcome -> outcome
            | Error e -> failwith $"Create metadata failed: {e}"
        File.WriteAllText(Path.Combine(worktree, "feature.cs"), "// accepted source")
        GitCommandTests.expectGitSuccess context [ "-C"; worktree; "add"; "feature.cs" ]
        GitCommandTests.expectGitSuccess context [ "-C"; worktree; "commit"; "-m"; "Feature source" ]
        let source = GitCommandTests.gitOutput context [ "-C"; worktree; "rev-parse"; "HEAD" ]
        GitCommandTests.expectGitSuccess context [ "cherry-pick"; source ]
        match completeTask
            { ProjectName = "Project1::retirement"; TaskId = "history"
              ExpectedRevision = task.Task.Task.Revision
              Verified = [ "source cherry-picked into main" ]
              FinalCommit = Some source; AcknowledgedAssignmentRevision = None
              ActorId = Some "Agent 1" } context with
        | Ok _ -> ()
        | Error e -> Assert.Fail($"Task completion failed: {e}")
        match getOrchestrationSnapshot { ProjectName = "Project1" } context with
        | Ok snapshot ->
            (snapshot.IntegratedTasks |> List.exists (fun item ->
                item.Task.Id = "history" && item.Phase = "Integrated")) |> shouldEqual true
        | Error e -> Assert.Fail($"Integration snapshot failed: {e}")
        let inspect () =
            match retireWorktree
                { ProjectName = "Project1"; WorktreePath = worktree
                  TargetRef = Some "main"; DeleteBranch = false; DryRun = true } context with
            | Ok value -> value
            | Error e -> failwith $"Retirement inspect failed: {e}"
        let first = inspect()
        first.FullyMerged |> shouldEqual false
        first.SourceEquivalent |> shouldEqual true
        first.TaskOnlyCommitsPreservedOnBranch.IsEmpty |> shouldEqual false
        first.CanRetire |> shouldEqual true
        first.Retired |> shouldEqual false
        File.WriteAllText(Path.Combine(worktree, "important.cache"), "preserve")
        let blocked = inspect()
        blocked.CanRetire |> shouldEqual false
        blocked.HasIgnoredOrUntrackedArtifacts |> shouldEqual true
        File.Delete(Path.Combine(worktree, "important.cache"))
        match retireWorktree
            { ProjectName = "Project1"; WorktreePath = worktree
              TargetRef = Some "main"; DeleteBranch = false; DryRun = false } context with
        | Error e -> Assert.Fail($"Retirement failed: {e}")
        | Ok retired ->
            retired.Retired |> shouldEqual true
            retired.BranchDeleted |> shouldEqual false
        Directory.Exists worktree |> shouldEqual false
        let branchSha = GitCommandTests.gitOutput context [ "rev-parse"; "feature/retirement" ]
        branchSha.Length |> shouldEqual 40
        task.Task.Task.Id |> shouldEqual "history")

[<Test>]
let retireWorktreeBlocksUnintegratedSourceAndActiveTask () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        match Client.Worktrees.createWorktree
            { ProjectName = "Project1"; Name = "unsafe"
              Branch = "feature/unsafe"; BaseRef = "HEAD" } context with
        | Ok _ -> ()
        | Error e -> Assert.Fail($"Could not create worktree: {e}")
        let worktree =
            match Client.Worktrees.listWorktrees { ProjectName = "Project1" } context with
            | Ok entries -> entries |> List.find (fun entry -> entry.Branch = Some "feature/unsafe") |> fun entry -> entry.Path
            | Error e -> failwith $"List failed: {e}"
        let created =
            match createTask
                { ProjectName = "Project1::unsafe"; TaskId = Some "active"
                  Title = "Active"; Html = "<h1>Active</h1>"; Dependencies = [] } context with
            | Ok task -> task
            | Error e -> failwith $"Create failed: {e}"
        match claimTask
            { ProjectName = "Project1::unsafe"; TaskId = "active"
              ExpectedRevision = created.Task.Task.Revision
              Owner = "Agent 1"; AllowReassign = false } context with
        | Ok _ -> ()
        | Error e -> Assert.Fail($"Claim failed: {e}")
        File.WriteAllText(Path.Combine(worktree, "source.cs"), "not integrated")
        GitCommandTests.expectGitSuccess context [ "-C"; worktree; "add"; "source.cs" ]
        GitCommandTests.expectGitSuccess context [ "-C"; worktree; "commit"; "-m"; "Unaccepted feature" ]
        let requested =
            { ProjectName = "Project1"; WorktreePath = worktree
              TargetRef = Some "main"; DeleteBranch = false; DryRun = false }
        match retireWorktree requested context with
        | Error e -> Assert.Fail($"Retirement validation failed: {e}")
        | Ok denied ->
            denied.Retired |> shouldEqual false
            denied.CanRetire |> shouldEqual false
            denied.HasActiveWork |> shouldEqual true
            denied.SourceEquivalent |> shouldEqual false
            Directory.Exists worktree |> shouldEqual true)

[<Test>]
let failedNewWorktreeAssignmentCannotLeaveAnOrphanedCheckout () =
    withProject (fun _ project context ->
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        let oldBase = GitCommandTests.gitOutput context [ "rev-parse"; "HEAD" ]
        let task =
            match createTask
                { ProjectName = "Project1"; TaskId = Some "next"
                  Title = "Next task"; Html = "<h1>Next</h1>"; Dependencies = [] } context with
            | Ok created -> created
            | Error e -> failwith $"Create failed: {e}"
        match assignTask
            { ProjectName = "Project1"; Agent = "Agent 4"; TaskId = "next"
              WorktreeName = Some "wrong-base"; Branch = Some "feature/wrong-base"
              Instructions = "Implement"; BaseCommit = Some oldBase
              ExpectedRevision = None; ExpectedAssignmentRevision = None
              ActorId = "orchestrator" } context with
        | Error(Client.ValidationError message) ->
            message.Contains("absent from requested worktree base") |> shouldEqual true
        | other -> Assert.Fail($"Expected old-base rejection: {other}")
        let branches =
            match Client.Worktrees.listWorktrees { ProjectName = "Project1" } context with
            | Ok entries -> entries
            | Error e -> failwith $"List failed: {e}"
        branches.Length |> shouldEqual 1
        let beforeClaim = task.Task.Task.Revision
        match claimTask
            { ProjectName = "Project1"; TaskId = "next"
              ExpectedRevision = beforeClaim; Owner = "Agent 1"; AllowReassign = false } context with
        | Ok _ -> ()
        | Error e -> Assert.Fail($"Claim failed: {e}")
        match assignTask
            { ProjectName = "Project1"; Agent = "Agent 4"; TaskId = "next"
              WorktreeName = Some "conflicting-base"; Branch = Some "feature/conflicting-base"
              Instructions = "Implement"; BaseCommit = None
              ExpectedRevision = None; ExpectedAssignmentRevision = None
              ActorId = "orchestrator" } context with
        | Error(Client.ValidationError message) ->
            message.Contains("Conflicting active global task ownership") |> shouldEqual true
        | other -> Assert.Fail($"Expected global ownership rejection: {other}")
        let remaining =
            match Client.Worktrees.listWorktrees { ProjectName = "Project1" } context with
            | Ok entries -> entries
            | Error e -> failwith $"List failed: {e}"
        remaining.Length |> shouldEqual 1)
