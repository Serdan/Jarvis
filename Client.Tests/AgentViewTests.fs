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
      ProjectLabel = project
      ProjectAliases = [project]
      State = state
      StateEnteredAt = Some(DateTimeOffset(2026,10,30,4,36,32,TimeSpan.Zero))
      StateReason = Some "Review commits then resume"
      Focus = task
      RoleIds = []
      Activity = None
      ReviewContext = None
      PendingNotifications = 0
      LatestNotification = None
      ActivityKeys = [project + "::" + agent] }

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
    result |> List.map _.State |> shouldEqual ["Ready"; "Working"; "NeedsWork"; "Parked"]
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
    lookup "Parked" |> shouldEqual []

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
    |> shouldEqual ["Jarvis::Agent 3"; "Wayfold::Agent 3"]

[<Test>]
let agentOrderingUsesProjectThenAgentRatherThanState () =
    let agents =
        [ record "AAA" "Idle" "Parked" None
          record "ZZZ" "Ready" "Ready" (Some focused)
          record "MMM" "Working" "Working" (Some focused)
          record "CCC" "Unassigned" "NeedsWork" None ]
    rows (snapshot agents) |> List.map _.State
    |> shouldEqual ["Parked"; "NeedsWork"; "Working"; "Ready"]

[<Test>]
let relativeStateAgeUsesTimestampNotLastRefreshTime () =
    let now = DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)
    let agent = { record "Jarvis" "Agent" "Ready" None with StateEnteredAt = Some(now.AddMinutes(-14.)) }
    stateAge now agent |> shouldEqual "14m"
    stateAge now { agent with StateEnteredAt = None } |> shouldEqual "—"

[<Test>]
let unreadNotificationsDoNotReorderAgentsButTriggerRefresh () =
    let quiet = record "Wayfold" "Agent 2" "Ready" None
    let notified =
        { record "Wayfold" "Agent 7" "Parked" None with
            PendingNotifications = 2
            LatestNotification = Some "Agent 3 #304: Agree on the interface" }
    let before = snapshot [ quiet; { notified with PendingNotifications = 0; LatestNotification = None } ]
    let after = snapshot [ quiet; notified ]
    rows after |> List.map _.Agent |> shouldEqual ["Agent 2"; "Agent 7"]
    changedAgents before after |> shouldEqual (set ["Wayfold::Agent 7"])
    changedAgents after after |> shouldEqual Set.empty

[<Test>]
let agentStatesHaveDistinctReadableColors () =
    let states =
        [ "Working", ConsoleColor.Green
          "Ready", ConsoleColor.Yellow
          "Assigned", ConsoleColor.Cyan
          "NeedsWork", ConsoleColor.Magenta
          "Completed", ConsoleColor.Blue
          "Parked", ConsoleColor.DarkGray
          "Unexpected", ConsoleColor.Gray ]
    for state, expected in states do
        stateColor state |> shouldEqual expected
    displayState "NeedsWork" |> shouldEqual "Waiting"


[<Test>]
let numericAgentSortingIsStableAcrossStateAndNotificationChanges () =
    let names = ["Agent 10"; "Agent 2"; "Lead"; "Agent 1"; "Agent 11"]
    let agents = names |> List.map (fun name -> record "Wayfold" name "Working" None)
    let expected = ["Agent 1"; "Agent 2"; "Agent 10"; "Agent 11"; "Lead"]
    rows (snapshot agents) |> List.map _.Agent |> shouldEqual expected
    let modified =
        agents |> List.map (fun agent ->
            { agent with State = "Ready"; PendingNotifications = 3 })
    rows (snapshot modified) |> List.map _.Agent |> shouldEqual expected


[<Test>]
let parkedAgentsAreHiddenFromTheTuiButRemainInTheLedgerSnapshot () =
    let parked = record "Wayfold" "Orchestrator" "Parked" None
    let active = record "Wayfold" "Lead" "Working" None
    let data = snapshot [ parked; active ]
    rows data |> List.map _.Agent |> shouldEqual ["Lead"; "Orchestrator"]
    visibleRows "" data |> List.map _.Agent |> shouldEqual ["Lead"]
    visibleRows "orchestrator" data |> shouldEqual []
    visibleRows "Parked" data |> shouldEqual []
    let resumed = { parked with State = "Ready" }
    visibleRows "" (snapshot [ resumed; active ]) |> List.map _.Agent
    |> shouldEqual ["Lead"; "Orchestrator"]


[<Test>]
let agentTableColumnsFollowVisibleContentInsteadOfTerminalWidth () =
    let wayfold =
        [| record "Wayfold" "Agent 1" "Working" None
           record "Wayfold" "Agent 10" "Ready" None |]
    columnWidths 120 wayfold |> shouldEqual (8, 8)
    columnWidths 80 wayfold |> shouldEqual (8, 8)
    let mixed = Array.append wayfold [| record "Longer Project" "GUI Agent" "Ready" None |]
    columnWidths 120 mixed |> shouldEqual (15, 9)
    columnWidths 55 mixed |> shouldEqual (5, 5)

[<Test>]
let narrowAgentTablesKeepColumnsWithinAvailableWidth () =
    let agents = [| record "A very long project name" "A very long agent name" "Working" None |]
    for width in [20; 30; 40; 50; 120] do
        let project, agent = columnWidths width agents
        Assert.That(project, Is.GreaterThanOrEqualTo(0))
        Assert.That(agent, Is.GreaterThanOrEqualTo(0))
        Assert.That(project + agent, Is.LessThanOrEqualTo(max 0 (width - 27 - 8 - 10)))


[<Test>]
let lastSeenUsesExplicitAgentKeyOnly () =
    let agent = record "Wayfold" "Agent 8" "Working" None
    let timestamp = DateTimeOffset(2026, 11, 3, 16, 0, 0, TimeSpan.Zero)
    let seen = System.Collections.Generic.Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
    seen["Wayfold::Agent 8"] <- timestamp
    seen["Wayfold::Agent 80"] <- timestamp.AddMinutes(10.)
    seen["Jarvis::Agent 8"] <- timestamp.AddMinutes(20.)
    lastSeenAt agent seen |> shouldEqual (Some timestamp)
    lastSeenAt agent (System.Collections.Generic.Dictionary<string, DateTimeOffset>()) |> shouldEqual None

[<Test>]
let lastSeenDisplaysRelativeAgeWithoutInferringPresence () =
    let now = DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.Zero)
    relativeAge now None |> shouldEqual "—"
    relativeAge now (Some(now.AddMinutes(-9.))) |> shouldEqual "9m"
    relativeAge now (Some(now.AddHours(-2.))) |> shouldEqual "2h"


[<Test>]
let activityProjectNormalizesRegisteredWorktreeNames () =
    let selected raw =
        ProjectPaths.ProjectTarget.parse raw
        |> function Ok target -> activityProject target | Error error -> failwithf "%A" error
    selected "Wayfold" |> shouldEqual "Wayfold"
    selected "Wayfold::gui-design" |> shouldEqual "Wayfold"
    selected "Jarvis::agent-id-implementation" |> shouldEqual "Jarvis"


[<Test>]
let activityProjectRejectsInvalidCheckoutSelector () =
    match ProjectPaths.ProjectTarget.parse "Wayfold::bad::selector" with
    | Error(Client.ValidationError _) -> ()
    | other -> Assert.Fail(sprintf "Expected typed validation: %A" other)

[<Test>]
let projectCountersUseCanonicalRowsIncludingParkedAndUnread () =
    let agents =
        [ { record "Wayfold" "A" "Working" None with PendingNotifications = 2 }
          { record "Wayfold" "B" "Ready" None with PendingNotifications = 1 }
          record "Wayfold" "C" "NeedsWork" None
          record "Wayfold" "D" "Parked" None ]
    let totals = projectCounts agents 3
    Assert.That(totals.Tracked, Is.EqualTo(4))
    Assert.That(totals.PendingReviews, Is.EqualTo(3))
    Assert.That(totals.Unread, Is.EqualTo(3))
    Assert.That(totals.Working, Is.EqualTo(1))
    Assert.That(totals.Ready, Is.EqualTo(1))
    Assert.That(totals.Waiting, Is.EqualTo(1))
    Assert.That(totals.Parked, Is.EqualTo(1))

[<Test>]
let projectStatusTotalsStayWholeWhenOnlyAgentRowsAreTextFiltered () =
    let a = { Project = "Wayfold"; Aliases = ["Wayfold"]; Identity = "pid:11111111:1"; Counts = Some (projectCounts [ record "Wayfold" "Agent" "Working" None ] 2); Status = Fresh; Error = None }
    let b = { Project = "Jarvis"; Aliases = ["Jarvis"]; Identity = "pid:22222222:1"; Counts = Some (projectCounts [ record "Jarvis" "Lead" "Parked" None ] 0); Status = Fresh; Error = None }
    let data = { snapshot [] with Projects = [b; a] }
    visibleProjects "Agent" data |> List.map _.Project |> shouldEqual ["Jarvis"; "Wayfold"]
    visibleProjects "#way -- Agent" data |> List.map _.Project |> shouldEqual ["Wayfold"]
    visibleProjects "#jar" data |> List.map _.Project |> shouldEqual ["Jarvis"]
    visibleProjects "#missing" data |> shouldEqual []
    totalCounts data |> fun total ->
        Assert.That(total.Tracked, Is.EqualTo(2))
        Assert.That(total.Working, Is.EqualTo(1))
        Assert.That(total.Parked, Is.EqualTo(1))

[<Test>]
let distinctLedgersWithSameDisplayNameHaveDistinctProjectLabels () =
    let one = { Project = "Wayfold"; Aliases = ["Wayfold"]; Identity = "pid:abc0123400000000:1"; Counts = Some (projectCounts [] 0); Status = Fresh; Error = None }
    let two = { one with Identity = "pid:def0567800000000:1" }
    let data = { snapshot [] with Projects = [two; one] }
    projectDisplayName data one |> shouldEqual "Wayfold [abc01234]"
    projectDisplayName data two |> shouldEqual "Wayfold [def05678]"
    visibleProjects "" data |> List.map _.Identity |> shouldEqual [one.Identity; two.Identity]

[<Test>]
let projectStatusLinesBoundWidthsAndKeepUnavailableSeparateFromZero () =
    let empty = { Project = "Project With A Very Very Very Long Name"; Aliases = ["Project With A Very Very Very Long Name"]; Identity = "pid:a:1"; Counts = Some (projectCounts [] 0); Status = Fresh; Error = None }
    let offline = { empty with Counts = None; Status = Unavailable; Error = Some "Network unavailable" }
    for width in [1; 15; 25; 40; 65; 120] do
        let name = projectDisplayName (snapshot []) empty
        let line = projectStatusLine width name empty
        Assert.That(line.Length, Is.LessThanOrEqualTo(width))
        Assert.That((projectStatusLine width name offline).Length, Is.LessThanOrEqualTo(width))
    Assert.That(projectStatusLine 120 empty.Project empty, Does.Contain("Tracked 0"))
    Assert.That(projectStatusLine 120 offline.Project offline, Does.Contain("UNAVAILABLE (counts unknown)"))
    let compactLine = projectStatusLine 60 empty.Project empty
    for token in ["T0"; "Rev0"; "U0"; "W0"; "Rd0"; "Wait0"; "P0"] do
        Assert.That(compactLine, Does.Contain(token))

[<Test>]
let partialProjectTotalsNeverTreatUnavailableAsZero () =
    let online = { Project = "Wayfold"; Aliases = ["Wayfold"]; Identity = "pid:1:1"; Counts = Some (projectCounts [ record "Wayfold" "Worker" "Ready" None ] 2); Status = Fresh; Error = None }
    let missing = { Project = "Jarvis"; Aliases = ["Jarvis"]; Identity = "pid:2:1"; Counts = None; Status = Unavailable; Error = Some "Cannot fetch ledger" }
    let data = { snapshot [] with Projects = [online; missing] }
    let totals = totalCounts data
    Assert.That(totals.Tracked, Is.EqualTo(1))
    Assert.That(totals.PendingReviews, Is.EqualTo(2))
    Assert.That(missing.Counts.IsNone, Is.True)

[<Test>]
let sameNamedLedgersWithCollidingShortIdentifiersGetUniqueLabels () =
    let a = { Project = "Wayfold"; Aliases = ["Wayfold"]; Identity = "pid:abc01234a:1"; Counts = Some (projectCounts [] 0); Status = Fresh; Error = None }
    let b = { a with Identity = "pid:abc01234b:1" }
    let view = { snapshot [] with Projects = [a; b] }
    projectDisplayName view a |> shouldEqual "Wayfold [abc01234a]"
    projectDisplayName view b |> shouldEqual "Wayfold [abc01234b]"

[<Test>]
let narrowProjectStatusWrapsAllCountersWithoutDroppingTheProjectLabel () =
    let entry =
        { Project = "Wayfold"; Aliases = ["Wayfold"]; Identity = "project:ledger:1"
          Counts = Some (projectCounts [record "Wayfold" "A" "Working" None] 2)
          Status = Fresh; Error = None }
    for width in [12; 24; 40; 54] do
        let lines = projectStatusLines width entry.Project entry
        Assert.That(lines.Length, Is.GreaterThan(1))
        Assert.That(lines.Head, Does.StartWith("#"))
        for line in lines do Assert.That(line.Length, Is.LessThanOrEqualTo(width))
        let joined = String.concat " " lines
        for token in ["T1"; "Rev2"; "U0"; "W1"; "Rd0"; "Wait0"; "P0"] do
            Assert.That(joined, Does.Contain(token))
    let unavailable = { entry with Counts = None; Status = Unavailable; Error = Some "Remote unreachable" }
    let offlineLines = projectStatusLines 24 unavailable.Project unavailable
    Assert.That(offlineLines.Head, Does.StartWith("#Wayfold"))
    Assert.That(String.concat " " offlineLines, Does.Contain("UNAVAILABLE"))
    Assert.That(String.concat " " offlineLines, Does.Contain("counts unknown"))

[<Test>]
let narrowDistinctProjectLabelsKeepTheirLedgerSuffixes () =
    let prefix = "A Project With A Much Longer Name Than The Terminal"
    let a = { Project = prefix; Aliases = [prefix]; Identity = "project:a1234567:1"; Counts = Some (projectCounts [] 0); Status = Fresh; Error = None }
    let b = { a with Identity = "project:b1234567:1" }
    let view = { snapshot [] with Projects = [a; b] }
    let one = (projectStatusLines 26 (projectDisplayName view a) a).Head
    let two = (projectStatusLines 26 (projectDisplayName view b) b).Head
    Assert.That(one, Does.Contain("[a1234567]"))
    Assert.That(two, Does.Contain("[b1234567]"))
    Assert.That(one, Is.Not.EqualTo(two))

[<Test>]
let projectAndAgentSelectorsAreIndependentAndConjunctive () =
    let data = snapshot [ record "Wayfold" "Worker 1" "Ready" None;
          record "Wayfold" "Worker 2" "Working" None;
          record "Wayfold" "Worker 10" "Ready" None;
          record "Wayfold" "GUI Agent" "Ready" None;
          record "Jarvis" "Worker 1" "Working" None;
          record "Projekt Loke" "GUI Agent" "Ready" None ]
    let selected filter =
        visibleRows filter data
        |> List.map (fun row -> row.Project, row.Agent)
        |> Set.ofList
    let one = Set.singleton ("Wayfold", "Worker 1")
    for filter in [ "#Wayfold@Worker 1"; "@Worker 1#Wayfold"; "@worker 1 #WAYFOLD"; "#wayfold @WORKER 1" ] do
        Assert.That(selected filter = one, Is.True, filter)
    Assert.That(selected "#Wayfold@Worker" = set [ ("Wayfold", "Worker 1"); ("Wayfold", "Worker 2"); ("Wayfold", "Worker 10") ], Is.True)
    Assert.That(selected "@Worker 1" = set [("Jarvis", "Worker 1"); ("Wayfold", "Worker 1")], Is.True)
    Assert.That(selected "#wayfold@GUI Agent" = Set.singleton ("Wayfold", "GUI Agent"), Is.True)
    Assert.That(selected "@GUI Agent#projekt loke" = Set.singleton ("Projekt Loke", "GUI Agent"), Is.True)
    Assert.That(selected "#missing@Worker 1", Is.Empty)
    Assert.That(selected "#Wayfold@" = selected "#Wayfold", Is.True)
    Assert.That(selected "@Worker 1 -- Review" = set [("Jarvis", "Worker 1"); ("Wayfold", "Worker 1")], Is.True)
    Assert.That(selected "#Wayfold -- Ready" = set [("Wayfold", "Worker 1"); ("Wayfold", "Worker 10"); ("Wayfold", "GUI Agent")], Is.True)

[<Test>]
let completionOnlyOffersAgentsFromSelectedProject () =
    let data = snapshot [ record "Wayfold" "Worker 1" "Ready" None;
          record "Wayfold" "Worker 2" "Working" None;
          record "Jarvis" "Worker 3" "Ready" None;
          record "Jarvis" "GUI Agent" "Ready" None ]
    let completed = completeFilter "#Wayfold@Wo" data
    Assert.That(completed, Is.EqualTo("#Wayfold@Worker "))
    Assert.That(completeFilter completed data, Is.EqualTo(completed))
    Assert.That(completeFilter (completed + "1") data, Is.EqualTo("#Wayfold@Worker 1"))
    Assert.That(completeFilter "#Jarvis@Wo" data, Is.EqualTo("#Jarvis@Worker 3"))
    Assert.That(completeFilter "@Wo#Way" data, Is.EqualTo("@Wo#Wayfold"))
    Assert.That(completeFilter "@Gu" data, Is.EqualTo("@GuI Agent"))
    Assert.That(completeFilter "@Gu" (snapshot []), Is.EqualTo("@Gu"))
    Assert.That(completeFilter "#W" (snapshot []), Is.EqualTo("#W"))

[<Test>]
let sameNamedAgentsAcrossProjectsKeepProjectSpecificFiltering () =
    let data = snapshot [ { (record "Wayfold" "Worker 1" "Ready" None) with ProjectAliases = ["Wayfold"; "W"] };
          { (record "Jarvis" "Worker 1" "Working" None) with ProjectAliases = ["Jarvis"; "J"] } ]
    let selected filter = visibleRows filter data |> List.map _.Project
    Assert.That(selected "#W@Worker 1" = ["Wayfold"], Is.True)
    Assert.That(selected "@Worker 1#J" = ["Jarvis"], Is.True)
    Assert.That(selected "#W@Worker 2", Is.Empty)

[<Test>]
let partialWorkerSelectorIncludesBareAndNumberedNamesWithoutRosterDependentModeSwitch () =
    let numbered =
        [ record "Wayfold" "Worker 1" "Ready" None;
          record "Wayfold" "Worker 2" "Ready" None;
          record "Wayfold" "Worker 10" "Working" None;
          record "Jarvis" "Worker 3" "Ready" None ]
    let bare = record "Wayfold" "Worker" "Ready" None
    let selected roster filter =
        visibleRows filter (snapshot roster)
        |> List.map (fun row -> row.Project, row.Agent)
        |> Set.ofList
    let numberedResult =
        set [ ("Wayfold", "Worker 1"); ("Wayfold", "Worker 2"); ("Wayfold", "Worker 10") ]
    Assert.That(selected numbered "#Wayfold@Worker" = numberedResult, Is.True)
    Assert.That(selected (bare :: numbered) "#Wayfold@Worker" =
                    Set.add ("Wayfold", "Worker") numberedResult, Is.True)
    Assert.That(selected (bare :: numbered) "#wayfold@WORK" =
                    Set.add ("Wayfold", "Worker") numberedResult, Is.True)
    // Numeric tokens have stable boundaries regardless of an exact bare agent.
    for roster in [ numbered; bare :: numbered ] do
        Assert.That(selected roster "#Wayfold@Worker 1" =
                        Set.singleton ("Wayfold", "Worker 1"), Is.True)
        Assert.That(selected roster "@Worker 10#Wayfold" =
                        Set.singleton ("Wayfold", "Worker 10"), Is.True)
        Assert.That(selected roster "#Jarvis@Worker" =
                        Set.singleton ("Jarvis", "Worker 3"), Is.True)

[<Test>]
let completionWithExactBareWorkerNameRetainsAmbiguityAndDoesNotChangeFilterSemantics () =
    let numbered =
        [ record "Wayfold" "Worker 1" "Ready" None;
          record "Wayfold" "Worker 2" "Working" None;
          record "Jarvis" "Worker 3" "Ready" None ]
    let withBare = snapshot (record "Wayfold" "Worker" "Ready" None :: numbered)
    let withoutBare = snapshot numbered
    let query = "#Wayfold@Wo"
    Assert.That(completeFilter query withBare, Is.EqualTo("#Wayfold@Worker"))
    Assert.That(completeFilter "#Wayfold@Worker" withBare, Is.EqualTo("#Wayfold@Worker"))
    Assert.That(completeFilter query withoutBare, Is.EqualTo("#Wayfold@Worker "))
    Assert.That(completeFilter "#Wayfold@Worker " withoutBare, Is.EqualTo("#Wayfold@Worker "))
    Assert.That(completeFilter "#Wayfold@Worker 1" withBare, Is.EqualTo("#Wayfold@Worker 1"))
    Assert.That(completeFilter "#Jarvis@Wo" withBare, Is.EqualTo("#Jarvis@Worker 3"))

[<Test>]
let missingNumericAndMultiwordAgentSelectorsNeverTurnIntoTaskText () =
    let taskWithOne = { focused with Title = "Sprint 1 follow-up; GUI Agent coverage" }
    let wayfoldWorker2 = record "Wayfold" "Worker 2" "Ready" (Some taskWithOne)
    let jarvisWorker1 = record "Jarvis" "Worker 1" "Ready" (Some taskWithOne)
    let projectWorker1 = record "Wayfold" "Worker 1" "Ready" (Some taskWithOne)
    let selected roster filter =
        visibleRows filter (snapshot roster) |> List.map _.Key |> Set.ofList
    let worker2 = Set.singleton "Wayfold::Worker 2"
    for roster in [[wayfoldWorker2]; [wayfoldWorker2; jarvisWorker1]] do
        // No matching Wayfold identity, even though the task title contains 1.
        Assert.That(selected roster "#Wayfold@Worker 1", Is.Empty)
        Assert.That(selected roster "#Wayfold@GUI Agent", Is.Empty)
        Assert.That(selected roster "@Worker 1#Wayfold", Is.Empty)
        Assert.That(selected roster "#Wayfold@Worker 2" = worker2, Is.True)
        Assert.That(selected roster "#Wayfold@Worker" = worker2, Is.True)
        Assert.That(selected roster "#Wayfold@Worker 2 -- Sprint" = worker2, Is.True)
        Assert.That(selected roster "#Wayfold@Worker 2 -- absent", Is.Empty)
    let withSameProject = [wayfoldWorker2; projectWorker1]
    Assert.That(selected withSameProject "#Wayfold@Worker 1" =
                    Set.singleton "Wayfold::Worker 1", Is.True)
    Assert.That(selected withSameProject "#Wayfold@Worker 10", Is.Empty)
