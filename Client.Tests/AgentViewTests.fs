module AgentViewTests

open System
open Client
open Client.AgentView
open NUnit.Framework
open FsUnitTyped

let private record project agent state task =
    { Key = project + "::" + agent
      Agent = agent
      Project = project
      State = state
      StateEnteredAt = Some(DateTimeOffset(2026,10,30,4,36,32,TimeSpan.Zero))
      StateReason = Some "Review commits then resume"
      Focus = task }

let private focused =
    { TaskId = "320"
      Title = "Eliminate audio allocations"
      Lifecycle = "InProgress"
      Phase = "Paused"
      SourceRef = Some "refs/heads/perf/320-audio-commit-alloc"
      BaseCommit = Some "abcdef"
      AssignmentGeneration = 1 }

let private snapshot agents =
    { Agents = agents
      Projects = []
      Issues = []
      RefreshedAt = DateTimeOffset.UtcNow }

[<Test>]
let canonicalReadyStateIsNotInferredFromPausedAttempt () =
    let agent = record "Wayfold" "Agent 3" "Ready" (Some focused)
    let output = rows (snapshot [agent])
    output.Head.State |> shouldEqual "Ready"
    agentSummary output.Head |> shouldEqual "#320 - Ready"
    output.Head.Focus.Value.Phase |> shouldEqual "Paused"
    output.Head.StateEnteredAt |> shouldEqual agent.StateEnteredAt

[<Test>]
let allFourDerivedStatesAndUnassignedAgentsAreRepresented () =
    let agents =
        ["Ready"; "Working"; "NeedsWork"; "Parked"]
        |> List.mapi (fun index state ->
            record "Wayfold" ("Agent " + string index) state
                (if state = "Parked" || state = "NeedsWork" then None else Some focused))
    let result = rows (snapshot agents)
    result |> List.map _.State |> shouldEqual ["Ready"; "NeedsWork"; "Working"; "Parked"]
    agentSummary result[3] |> shouldEqual "Parked"

[<Test>]
let canonicalAgentSearchUsesProjectStatusTaskAndSourceRef () =
    let a = record "Wayfold" "Agent 3" "Ready" (Some focused)
    let b = record "Jarvis" "Agent 8" "Parked" None
    let data = snapshot [a;b]
    let lookup filter = visibleRows filter data |> List.map _.Agent
    lookup "agent 3" |> shouldEqual ["Agent 3"]
    lookup "wayfold" |> shouldEqual ["Agent 3"]
    lookup "320" |> shouldEqual ["Agent 3"]
    lookup "audio-commit-alloc" |> shouldEqual ["Agent 3"]
    lookup "Ready" |> shouldEqual ["Agent 3"]
    lookup "Parked" |> shouldEqual ["Agent 8"]

[<Test>]
let canonicalStateTransitionsAndReasonChangeMarkAgentAsChanged () =
    let ready = record "Wayfold" "Agent 3" "Ready" (Some focused)
    let before = snapshot [ready]
    let working = { ready with State = "Working"; StateReason = Some "Resumed" }
    let changed = snapshot [working]
    changedAgents before changed |> shouldEqual (set ["Wayfold::Agent 3"])
    changedAgents changed changed |> shouldEqual Set.empty

[<Test>]
let agentNamesAreScopedToTheirCanonicalProjects () =
    let a = record "Wayfold" "Agent 3" "Ready" None
    let b = record "Jarvis" "Agent 3" "Parked" None
    rows (snapshot [a;b]) |> List.map _.Key
    |> shouldEqual ["Wayfold::Agent 3"; "Jarvis::Agent 3"]

[<Test>]
let agentOrderingPrioritizesActionableWorkOverAlphabeticalProjectName () =
    let agents =
        [ record "AAA" "Idle" "Parked" None
          record "ZZZ" "Ready" "Ready" (Some focused)
          record "MMM" "Working" "Working" (Some focused)
          record "CCC" "Unassigned" "NeedsWork" None ]
    rows (snapshot agents) |> List.map _.State
    |> shouldEqual ["Ready"; "NeedsWork"; "Working"; "Parked"]

[<Test>]
let relativeStateAgeUsesTimestampNotLastRefreshTime () =
    let now = DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)
    let agent = { record "Jarvis" "Agent" "Ready" None with StateEnteredAt = Some(now.AddMinutes(-14.)) }
    stateAge now agent |> shouldEqual "14m"
    stateAge now { agent with StateEnteredAt = None } |> shouldEqual "—"
