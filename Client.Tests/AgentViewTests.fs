module AgentViewTests

open System
open System.IO
open Client
open Client.AgentView
open Client.AgentDashboard
open NUnit.Framework
open FsUnitTyped

let private assignment agent project task phase =
    { Agent = agent; Project = project; Selector = project + "::" + task
      TaskId = task; Title = task + " implementation"; Status = "active"
      Phase = phase; NextActor = Some "agent"; Worktree = "/tmp/" + task
      Branch = Some ("feature/" + task); Head = "abc"; Clean = Some true
      AssignmentRevision = Some "rev"; AcknowledgedRevision = None
      Instructions = Some ("Implement " + task); BaseCommit = None
      UpdatedAt = None; LastUpdatedBy = None; CheckpointSummary = None
      CheckpointNextAction = None; CheckpointLastCommit = None
      CheckpointBlocker = None; CompletionFinalCommit = None }

let private snapshot entries conflicts =
    { Assignments = entries; Projects = []; Conflicts = conflicts; Issues = []
      RefreshedAt = DateTimeOffset.UtcNow }

[<Test>]
let rosterGroupsAndMarksConflicts () =
    let data = snapshot
                   [ assignment "Agent 7" "Wayfold" "304" "Working"
                     assignment "Agent 2" "Wayfold" "292" "Assigned"
                     assignment "Agent 7" "Wayfold" "305" "ReadyForReview" ]
                   [ "Wayfold: Agent Agent 7 has multiple active assignments" ]
    let result = rows data
    (result |> List.map _.Agent) |> shouldEqual [ "Agent 7"; "Agent 2" ]
    result.Head.Conflict |> shouldEqual true
    result.Head.Assignments.Length |> shouldEqual 2
    (agentSummary result.Head).Contains("CONFLICT") |> shouldEqual true

[<Test>]
let rosterSearchMatchesAgentProjectTaskWorktreeAndPhase () =
    let data = snapshot [ assignment "Agent 2" "Wayfold" "292" "Working"
                          assignment "Agent 3" "Jarvis" "308" "Assigned" ] []
    let agent query = visibleRows query data |> List.map _.Agent
    agent "agent 2" |> shouldEqual [ "Agent 2" ]
    agent "WAYFOLD" |> shouldEqual [ "Agent 2" ]
    agent "308" |> shouldEqual [ "Agent 3" ]
    agent "tmp/292" |> shouldEqual [ "Agent 2" ]
    agent "working" |> shouldEqual [ "Agent 2" ]
    agent "missing" |> shouldEqual []
    agent "" |> shouldEqual [ "Agent 3"; "Agent 2" ]
    phaseLabel "ReadyForReview" |> shouldEqual "Review"

[<Test>]
let snapshotAdapterPreservesAssignmentIdentityAndInspectionFailures () =
    let tree : Client.Worktrees.WorktreeInfo =
        { Path = "/workspace/Wayfold-gold"; MainPath = "/workspace/Wayfold"
          Head = "abcdef"; Branch = Some "feature/gold"; IsMain = false
          IsDetached = false; IsLocked = false; IsPrunable = false; IsClean = Some true }
    let task : Client.TaskStore.TaskSummary =
        { Id = "292"; Title = "Gold pickup"; Status = "active"
          DescriptionPath = "docs/tasks/292.html"; StatePath = "docs/tasks/292.json"
          HasState = true; Owner = Some "Agent 2"; Dependencies = []
          Revision = Some "taskrev"; Diagnostics = []; NextActor = Some "agent"
          LastUpdatedBy = Some "orchestrator"; AssignmentRevision = Some "revision"
          UpdatedAt = None; CheckpointSummary = None
          CheckpointNextAction = None; CheckpointLastCommit = None
          CheckpointBlocker = None; CompletionFinalCommit = None }
    let assigned : Client.TaskStore.AgentAssignment =
        { Agent = "Agent 2"; ProjectName = "Wayfold::gold"; Worktree = tree
          Task = task; AssignmentInstructions = Some "Fix gold"
          AssignmentBaseCommit = Some "basecommit"; AssignmentRevision = Some "revision"
          AcknowledgedAssignmentRevision = None; Phase = "Assigned" }
    let group : Client.TaskStore.WorktreeTaskGroup =
        { Worktree = tree; Tasks = [task]; Error = None }
    let source : Client.TaskStore.OrchestrationSnapshot =
        { ProjectName = "Wayfold"; Worktrees = [group]; Assignments = [assigned]
          Conflicts = ["Agent 2 duplicate"]; ReviewerTaskCount = 0
          UnacknowledgedTaskCount = 1; InspectionErrors = ["one worktree unreadable"]
          RetirementInspections = []; RetirementCandidates = []; IntegratedTasks = []
          TotalMatches = 1; Offset = 0; Limit = 60; NextOffset = None
          TaskDiagnosticCount = 0; TaskDiagnostics = []; RetirementInspected = false }
    let data = fromSnapshots DateTimeOffset.UtcNow
                    [ "Wayfold", Ok source; "NonGit", Error "not a Git repository" ]
    data.Assignments.Head.Selector |> shouldEqual "Wayfold::gold"
    data.Assignments.Head.Instructions |> shouldEqual (Some "Fix gold")
    data.Assignments.Head.AssignmentRevision |> shouldEqual (Some "revision")
    data.Conflicts |> shouldEqual [ "Wayfold: Agent 2 duplicate" ]
    (data.Projects |> List.find (fun p -> p.Project = "Wayfold")).Unacknowledged |> shouldEqual 1
    (data.Projects |> List.find (fun p -> p.Project = "NonGit")).Error |> shouldEqual (Some "not a Git repository")
    data.Issues.Length |> shouldEqual 2

[<Test>]
let identicalAgentLabelsAcrossDifferentProjectsAreNotConflicts () =
    let data =
        snapshot
            [ assignment "Agent 1" "Wayfold" "282" "Working"
              assignment "Agent 1" "Jarvis" "301" "Assigned" ] []
    let result = rows data
    result.Length |> shouldEqual 2
    (result |> List.map _.Key |> Set.ofList)
    |> shouldEqual (set [ "Wayfold::Agent 1"; "Jarvis::Agent 1" ])
    result |> List.forall (fun row -> not row.Conflict) |> shouldEqual true

[<Test>]
let duplicateTaskClaimFlagsBothAgentsWithinProject () =
    let data =
        snapshot
            [ assignment "Agent 1" "Wayfold" "282" "Working"
              assignment "Agent 7" "Wayfold" "282" "Assigned" ]
            [ "Wayfold: Task 282 is actively claimed in two worktrees" ]
    let result = rows data
    result |> List.forall _.Conflict |> shouldEqual true
    result.Length |> shouldEqual 2

[<Test>]
let completedWorkRemainsVisibleAfterActiveAssignmentDisappears () =
    let tree : Client.Worktrees.WorktreeInfo =
        { Path = "/tmp/Wayfold-finished"; MainPath = "/tmp/Wayfold"
          Head = "abc"; Branch = Some "feature/292"
          IsMain = false; IsDetached = false; IsLocked = false
          IsPrunable = false; IsClean = Some true }
    let completedTask : Client.TaskStore.TaskSummary =
        { Id = "292"; Title = "Gold pickup"; Status = "completed"
          DescriptionPath = "docs/tasks/292.html"; StatePath = "docs/tasks/292.json"
          HasState = true; Owner = Some "Agent 2"; Dependencies = []
          Revision = Some "final"; Diagnostics = []; NextActor = None
          LastUpdatedBy = Some "agent:Agent 2"; AssignmentRevision = Some "rev"
          UpdatedAt = Some(DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero))
          CheckpointSummary = Some "Gold pickup implemented"
          CheckpointNextAction = Some "Reviewer acceptance"
          CheckpointLastCommit = Some "code-sha"
          CheckpointBlocker = None; CompletionFinalCommit = Some "accepted-sha" }
    let source : Client.TaskStore.OrchestrationSnapshot =
        { ProjectName = "Wayfold"
          Worktrees = [ { Worktree = tree; Tasks = [ completedTask ]; Error = None } ]
          Assignments = []
          Conflicts = []
          ReviewerTaskCount = 0
          UnacknowledgedTaskCount = 0
          InspectionErrors = []
          RetirementInspections = []
          RetirementCandidates = []
          IntegratedTasks = []
          TotalMatches = 1; Offset = 0; Limit = 60; NextOffset = None
          TaskDiagnosticCount = 0; TaskDiagnostics = []; RetirementInspected = false }
    let inProgress = { completedTask with Status = "active"; Revision = Some "progress"; NextActor = Some "reviewer" }
    let activeAssignment : Client.TaskStore.AgentAssignment =
        { Agent = "Agent 2"; ProjectName = "Wayfold::Wayfold-finished"
          Worktree = tree; Task = inProgress
          AssignmentInstructions = Some "Implement"; AssignmentBaseCommit = None
          AssignmentRevision = Some "rev"; AcknowledgedAssignmentRevision = Some "rev"
          Phase = "ReadyForReview" }
    let beforeSource = { source with
                            Assignments = [ activeAssignment ]
                            Worktrees = [ { Worktree = tree; Tasks = [ inProgress ]; Error = None } ] }
    let before = fromSnapshots DateTimeOffset.UtcNow [ "Wayfold", Ok beforeSource ]
    let finished = fromSnapshots DateTimeOffset.UtcNow [ "Wayfold", Ok source ]
    let roster = rows finished
    roster.Length |> shouldEqual 1
    roster.Head.Agent |> shouldEqual "Agent 2"
    roster.Head.Assignments.Length |> shouldEqual 1
    roster.Head.Assignments.Head.Phase |> shouldEqual "Completed"
    roster.Head.Assignments.Head.CheckpointSummary |> shouldEqual (Some "Gold pickup implemented")
    roster.Head.Assignments.Head.CompletionFinalCommit |> shouldEqual (Some "accepted-sha")
    (agentSummary roster.Head).Contains("Completed") |> shouldEqual true
    (changedAgents before finished).Contains("Wayfold::Agent 2") |> shouldEqual true
    let acceptedSource = { source with
                               IntegratedTasks = [ { Worktree = tree; Task = completedTask; Phase = "Integrated" } ] }
    let accepted = fromSnapshots DateTimeOffset.UtcNow [ "Wayfold", Ok acceptedSource ]
    (rows accepted).Head.Assignments.Head.Phase |> shouldEqual "Integrated"
    (changedAgents finished accepted).Contains("Wayfold::Agent 2") |> shouldEqual true
    (changedAgents accepted accepted).IsEmpty |> shouldEqual true

[<Test>]
let recentCompletedTasksDoNotCreateFalseActiveConflicts () =
    let today = DateTimeOffset.UtcNow
    let completed =
        { (assignment "Agent 2" "Wayfold" "old" "Completed") with
            Status = "completed"; NextActor = None
            UpdatedAt = Some(today.AddDays(-1.)) }
    let current =
        { (assignment "Agent 2" "Wayfold" "new" "Working") with UpdatedAt = Some today }
    let roster = rows (snapshot [ completed; current ] [])
    roster.Length |> shouldEqual 1
    roster.Head.Conflict |> shouldEqual false
    roster.Head.Assignments.Head.TaskId |> shouldEqual "new"
    (agentSummary roster.Head).Contains("+1 completed") |> shouldEqual true

[<Test>]
let timestampOrCheckpointUpdatesMarkAgentAsChanged () =
    let started = assignment "Agent 1" "Wayfold" "282" "Working"
    let before = snapshot [started] []
    let updated = { started with UpdatedAt = Some DateTimeOffset.UtcNow
                                 CheckpointSummary = Some "New allocation evidence" }
    let after = snapshot [ updated ] []
    (changedAgents before after).Contains("Wayfold::Agent 1") |> shouldEqual true
    (changedAgents after after).IsEmpty |> shouldEqual true

[<Test>]
let agentsDashboardSkipsNonGitProjectsWithoutHidingGitFailures () =
    let root, project = GitCommandTests.createTempProject()
    try
        let context = GitCommandTests.TestContext(root)
        for name in [ "LexicalEffects"; "TEST"; "eyepuzzle" ] do
            Directory.CreateDirectory(Path.Combine(root, name)) |> ignore
        GitCommandTests.initRepo context
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "initial" ]
        let all =
            match Client.ProjectBrowser.listProjects context with
            | Ok projects -> projects |> Seq.map (fun (Client.ProjectName value) -> value) |> Seq.toList
            | Error error -> failwith $"Listing projects failed: {error}"
        for name in [ "LexicalEffects"; "TEST"; "eyepuzzle"; "Project1" ] do
            (List.contains name all) |> shouldEqual true
        let gitOnly =
            match Client.ProjectBrowser.listGitProjects context with
            | Ok projects -> projects |> List.map (fun (Client.ProjectName value) -> value)
            | Error error -> failwith $"Listing Git projects failed: {error}"
        gitOnly |> shouldEqual [ "Project1" ]
        match Client.AgentDashboard.load context with
        | Ok snapshot ->
            (snapshot.Projects |> List.map _.Project) |> shouldEqual [ "Project1" ]
            snapshot.Issues |> shouldEqual []
        | Error error -> Assert.Fail($"Agents dashboard failed: {error}")
        // A directory advertising a Git root must still report real Git
        // validation errors instead of being silently ignored.
        Directory.CreateDirectory(Path.Combine(root, "TEST", ".git")) |> ignore
        match Client.AgentDashboard.load context with
        | Error error -> Assert.Fail($"Dashboard aborted instead of reporting Git failure: {error}")
        | Ok snapshot ->
            (snapshot.Projects |> List.map _.Project |> List.sort) |> shouldEqual [ "Project1"; "TEST" ]
            (snapshot.Issues |> List.exists (fun issue ->
                issue.Contains("TEST") && issue.Contains("Git"))) |> shouldEqual true
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let agentFiltersShareActivityTextAndProjectTerms () =
    let data = snapshot
                   [ assignment "Agent 2" "Wayfold" "292" "Working"
                     assignment "Agent 3" "Jarvis" "308" "Assigned" ] []
    let names query = visibleRows query data |> List.map _.Agent
    names "@Wayfold" |> shouldEqual [ "Agent 2" ]
    names "@Jarvis" |> shouldEqual [ "Agent 3" ]
    names "@Wayfold agent 2" |> shouldEqual [ "Agent 2" ]
    names "working 292" |> shouldEqual [ "Agent 2" ]
    names "agent 2 @Jarvis" |> shouldEqual []
    names "@Wayfold::292" |> shouldEqual [ "Agent 2" ]
    names "@" |> shouldEqual [ "Agent 3"; "Agent 2" ]
    names "@missing" |> shouldEqual []

[<Test>]
let agentProjectAndTextFiltersMatchSameTask () =
    let data = snapshot
                   [ assignment "Agent 2" "Wayfold" "292" "Working"
                     assignment "Agent 2" "Wayfold" "305" "Assigned" ] []
    (visibleRows "@Wayfold::292 292" data).Length |> shouldEqual 1
    (visibleRows "@Wayfold::292 305" data).Length |> shouldEqual 0
    (visibleRows "@Wayfold::305 305" data).Length |> shouldEqual 1
