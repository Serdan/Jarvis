module AgentViewTests

open System
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
      Instructions = Some ("Implement " + task); BaseCommit = None }

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
          LastUpdatedBy = Some "orchestrator"; AssignmentRevision = Some "revision" }
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
          RetirementInspections = []; RetirementCandidates = []; IntegratedTasks = [] }
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
