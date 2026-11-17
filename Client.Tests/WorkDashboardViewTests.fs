module Client.Tests.WorkDashboardViewTests

open System
open System.Threading
open Client.ConsoleTui
open Client.WorkDashboardView
open NUnit.Framework

let private sha = String.replicate 40 "a"

let private dependency =
    { WorkId = "prerequisite"
      StageGeneration = Some 7
      ClaimGeneration = Some 4
      State = "Blocked"
      Satisfied = false
      WaiverReceipt = None
      Reason = "Hard dependency remains unsatisfied at graph v12" }

let private row id category =
    { WorkId = id
      SubjectId = "task-" + id
      SubjectTitle = Some("Title " + id)
      QueueId = "implementation"
      Priority = 0
      Kind = "Implementation"
      State = category
      Category = category
      StageGeneration = 3
      ClaimGeneration = 2
      GraphVersion = 12
      Gate = "Clear"
      Domain = "Undecided"
      ClaimLaunchId = None
      SessionUuid = None
      Liveness = "Not claimed"
      Dependencies = []
      Reasons = []
      ClaimEnabled = false }

let private source name projectId coverage work =
    { ProjectName = name
      Snapshot =
        Ok { ProjectId = projectId
             VerifiedGitHead = sha
             ProjectVersion = 9
             GraphVersion = 12
             Coverage = coverage
             RoleAuthority = "UNKNOWN: pinned WQ04 role view not supplied"
             ClaimBinding = "UNKNOWN: same-CAS WQ04/WQ03 binding missing; default-off"
             Work = work
             ReadOnly = true } }

[<Test>]
let twoProjectWorkRowsAreStableAndWorkIsTheIdentity () =
    let a = source "Zulu" "proj-z" "Verified"
                [ row "same" "Terminal"; row "later" "Blocked" ]
    let b = source "Alpha" "proj-a" "Verified"
                [ row "same" "Blocked"; row "first" "Available" ]
    let actual = allRows [ a; b ]
    Assert.That(actual |> List.map _.WorkId = [ "first"; "same"; "later"; "same" ], Is.True)
    Assert.That(actual |> List.map _.Project = [ "Alpha"; "Alpha"; "Zulu"; "Zulu" ], Is.True)
    Assert.That((actual |> List.map _.Key |> Set.ofList).Count, Is.EqualTo(4))
    Assert.That(actual |> List.forall (fun item -> item.Details |> List.exists (fun x -> x.Contains("not owner"))),
                Is.True)

[<Test>]
let availableIsNotMisrepresentedAsDispatchReadyWhenClaimPortMissing () =
    let item = allRows [ source "One" "proj-one" "Verified" [ row "w1" "Available" ] ] |> List.head
    Assert.That(item.Status, Does.StartWith("NotReady"))
    Assert.That(item.Details |> String.concat " ", Does.Contain("Account admission: UNKNOWN"))
    Assert.That(item.Details |> String.concat " ", Does.Contain("Original effect: UNKNOWN"))
    Assert.That(item.Details |> String.concat " ", Does.Contain("Role authority: UNKNOWN"))
    Assert.That(item.Details |> String.concat " ", Does.Contain("Claim authority: UNKNOWN"))

[<Test>]
let blockerReasonsIncludeDependencyAndClaimVersions () =
    let blocked = { row "held" "Blocked" with Dependencies = [ dependency ]; Reasons = [ "Gate hold" ] }
    let item = allRows [ source "One" "proj-one" "Verified" [ blocked ] ] |> List.head
    let details = String.concat "\n" item.Details
    Assert.That(item.Status, Is.EqualTo("Blocked"))
    Assert.That(details, Does.Contain("stage v7 / claim v4"))
    Assert.That(details, Does.Contain("Hard dependency"))
    Assert.That(details, Does.Contain("Gate hold"))

[<Test>]
let missingAndPartialGitCoverageNeverLooksFresh () =
    let partial = source "Partial" "proj-p" "UNKNOWN: graph index incomplete" [ row "ready" "Available" ]
    let noBinding = { ProjectName = "Offline"; Snapshot = Error "No explicit Git trust binding" }
    let actual = allRows [ partial; noBinding ]
    Assert.That(actual |> List.exists (fun r -> r.Project = "Offline" && r.Status.Contains("NotReady")), Is.True)
    Assert.That(actual |> List.exists (fun r -> r.Project = "Partial" && r.Status.Contains("NotReady")), Is.True)
    Assert.That(actual |> List.forall (fun r -> r.Status <> "Available (observation)"), Is.True)

[<Test>]
let uncertainClaimDoesNotBecomeActiveAndTerminalRemainsVisible () =
    let claimed =
        { row "claim" "Active" with
            State = "Claimed"
            ClaimLaunchId = Some "launch-1"
            SessionUuid = Some "session-1"
            Liveness = "UNKNOWN: original session journal missing" }
    let actual =
        allRows [ source "One" "proj-one" "Verified" [ claimed; row "done" "Terminal" ] ]
    let active = actual |> List.find (fun r -> r.WorkId = "claim")
    Assert.That(active.Status, Does.Contain("UNKNOWN"))
    Assert.That(active.Details |> String.concat " ", Does.Contain("Claim launch (not owner): launch-1"))
    Assert.That(active.Details |> String.concat " ", Does.Contain("Session UUID (not owner): session-1"))
    Assert.That(actual |> List.find (fun r -> r.WorkId = "done") |> _.Status, Is.EqualTo("Terminal"))

[<Test>]
let filterAndSelectionSurviveSnapshotReorderingButNotMissingRows () =
    let initial = source "Alpha" "id" "Verified" [ row "w1" "Available"; row "w2" "Blocked" ]
    let selected = normalizeSelection (Some "id::w2") (visibleRows "" [ initial ])
    let reordered = source "Alpha" "id" "Verified" [ row "w2" "Blocked"; row "w1" "Available" ]
    Assert.That(normalizeSelection selected (visibleRows "" [ reordered ]), Is.EqualTo(selected))
    Assert.That(moveSelection selected -1 (visibleRows "" [ reordered ]), Is.EqualTo(Some "id::w1"))
    let filtered = visibleRows "w2" [ reordered ]
    Assert.That(filtered |> List.map _.WorkId = [ "w2" ], Is.True)
    Assert.That(normalizeSelection (Some "id::missing") filtered, Is.EqualTo(Some "id::w2"))

[<Test>]
let narrowTerminalTruncatesAndSanitizesUntrustedSnapshotStrings () =
    let hostile =
        { row "w1" "Unknown" with
            SubjectTitle = Some "Nasty\u001b[31m\nsubject"
            Reasons = [ "gate\u001b[2J\rrecovery" ] }
    let item = allRows [ source "One" "id" "Verified" [ hostile ] ] |> List.head
    for width in [ 1; 8; 18; 40 ] do
        let line = displayLine width (Some item.Key) item
        Assert.That(line.Length, Is.LessThanOrEqualTo(width))
        Assert.That(line.Contains("\u001b"), Is.False)
        Assert.That(line.Contains("\n"), Is.False)
        for detail in detailLines width item do
            Assert.That(detail.Length, Is.LessThanOrEqualTo(width))
            Assert.That(detail.Contains("\u001b"), Is.False)
            Assert.That(detail.Contains("\r"), Is.False)

[<Test>]
let claimedRowsRequireExplicitPositiveBoundSessionLiveness () =
    // Work.Active is a claim category, never itself a positive session signal.
    let cases =
        [ "Not claimed", None, None, false
          "Running", None, None, false
          "Running", Some "launch", None, false
          "Running", Some " ", Some "session", false
          "Stopped", Some "launch", Some "session", false
          "Timed out", Some "launch", Some "session", false
          "Unbound", Some "launch", Some "session", false
          "", Some "launch", Some "session", false
          "UNKNOWN: stale", Some "launch", Some "session", false
          "Running (stale)", Some "launch", Some "session", false
          "Running", Some "launch", Some "session", true
          "Live (verified)", Some "launch", Some "session", true ]
    for liveness, launch, session, expectedLive in cases do
        let claimed =
            { row "claim" "Active" with
                State = "Claimed"
                Liveness = liveness
                ClaimLaunchId = launch
                SessionUuid = session }
        let actual = allRows [ source "P" "project" "Verified" [ claimed ] ] |> List.head
        let expected = if expectedLive then "Active (observed)" else "Claimed; session UNKNOWN"
        Assert.That(actual.Status, Is.EqualTo(expected), sprintf "liveness=%A launch=%A session=%A" liveness launch session)

[<Test>]
let listKeepsCriticalReadinessVisibleBeforeLongIdentityAtFortyAndEightyColumns () =
    let longId = String.replicate 42 "w"
    let longProject = String.replicate 50 "Project"
    let longSubject = String.replicate 5 "A descriptive subject "
    let claimed =
        { row longId "Active" with
            SubjectTitle = Some longSubject
            Liveness = "Stopped"
            ClaimLaunchId = Some "launch"
            SessionUuid = Some "session" }
    let partial = source longProject "project-partial" "UNKNOWN: missing graph" [ row longId "Available" ]
    let unbound = { ProjectName = longProject + "-offline"; Snapshot = Error "Unbound trust" }
    let ready = source "Long" "project-long" "Verified" [ claimed ]
    let rows = allRows [ ready; partial; unbound ]
    for width in [ 40; 80 ] do
        for item in rows do
            let line = displayLine width (Some item.Key) item
            Assert.That(line.Length, Is.LessThanOrEqualTo(width))
            Assert.That(line, Does.Contain("UNKNOWN"),
                        sprintf "warning truncated at %d columns on %A" width item.Key)

let private press (ui: ConsoleTui) ch (keyCode: ConsoleKey) =
    ui.DispatchWorkKeyForFixture(ConsoleKeyInfo(ch, keyCode, false, false, false))

let private untilWork (ui: ConsoleTui) predicate =
    Assert.That(
        SpinWait.SpinUntil(
            (fun () -> predicate (ui.InspectWorkForFixture(80, 26))),
            4000),
        Is.True, "The bounded terminal fixture did not reach the expected state.")

[<Test>]
let actualTerminalWorkModeNavigationFilterAndLegacySwitchAreReachable () =
    let ui = new ConsoleTui()
    let loaded = source "Alpha" "project-alpha" "Verified"
                        [ row "w1" "Available"; row "w2" "Blocked" ]
    ui.SetWorkAccess({ Load = fun () -> Ok [ loaded ] })
    Assert.That((ui.InspectWorkForFixture(40, 24)).Mode, Is.EqualTo("ActivityMode"))
    Assert.That(press ui 'w' ConsoleKey.W, Is.True)
    untilWork ui (fun state -> state.ProjectCount = 1 && not state.Refreshing)
    let initial = ui.InspectWorkForFixture(40, 24)
    Assert.That(initial.Mode, Is.EqualTo("WorkMode"))
    Assert.That(initial.Selected, Is.EqualTo(Some "project-alpha::w1"))
    Assert.That(initial.Lines |> List.exists (fun line -> line.Contains("NotReady")), Is.True)

    Assert.That(press ui '\000' ConsoleKey.DownArrow, Is.True)
    Assert.That((ui.InspectWorkForFixture(40, 24)).Selected, Is.EqualTo(Some "project-alpha::w2"))
    Assert.That(press ui '/' ConsoleKey.Oem2, Is.True)
    Assert.That((ui.InspectWorkForFixture(40, 24)).Mode, Is.EqualTo("WorkFilterMode"))
    Assert.That(press ui 'w' ConsoleKey.W, Is.True)
    Assert.That(press ui '2' ConsoleKey.D2, Is.True)
    Assert.That(press ui '\000' ConsoleKey.Enter, Is.True)
    let filtered = ui.InspectWorkForFixture(40, 24)
    Assert.That(filtered.Mode, Is.EqualTo("WorkMode"))
    Assert.That(filtered.Filter, Is.EqualTo("w2"))
    Assert.That(filtered.Selected, Is.EqualTo(Some "project-alpha::w2"))
    Assert.That(press ui '/' ConsoleKey.Oem2, Is.True)
    Assert.That(press ui 'x' ConsoleKey.X, Is.True)
    Assert.That((ui.InspectWorkForFixture(40, 24)).Selected, Is.EqualTo(None))
    Assert.That(press ui '\000' ConsoleKey.Escape, Is.True)
    Assert.That((ui.InspectWorkForFixture(40, 24)).Selected, Is.EqualTo(Some "project-alpha::w2"))
    Assert.That(press ui 'a' ConsoleKey.A, Is.True)
    Assert.That((ui.InspectWorkForFixture(40, 24)).Mode, Is.EqualTo("AgentsMode"))
    // Legacy mode remains separate and its non-Work shortcuts are not intercepted.
    Assert.That(press ui '#' ConsoleKey.D3, Is.False)
    Assert.That(press ui 'w' ConsoleKey.W, Is.True)
    untilWork ui (fun state -> state.ProjectCount = 1 && not state.Refreshing)
    Assert.That((ui.InspectWorkForFixture(80, 24)).Mode, Is.EqualTo("WorkMode"))
    Assert.That(press ui '\000' ConsoleKey.Escape, Is.True)
    Assert.That((ui.InspectWorkForFixture(80, 24)).Mode, Is.EqualTo("ActivityMode"))

[<Test>]
let terminalRefreshClearsReadyRowsAndRejectedLateProviderCannotRepaintThem () =
    let ui = new ConsoleTui()
    use staleEntered = new ManualResetEventSlim(false)
    use releaseStale = new ManualResetEventSlim(false)
    let calls = ref 0
    let first = source "First" "project-first" "Verified" [ row "ready" "Available" ]
    let stale = source "STALE" "project-stale" "Verified" [ row "stale-ready" "Available" ]
    let replacement = source "Replacement" "project-replacement" "Verified" [ row "blocked" "Blocked" ]
    ui.SetWorkAccess(
        { Load = fun () ->
            calls.Value <- calls.Value + 1
            if calls.Value = 1 then Ok [ first ]
            else
                staleEntered.Set()
                if releaseStale.Wait(4000) then Ok [ stale ]
                else Error "stale fixture timed out" })
    try
        Assert.That(press ui 'w' ConsoleKey.W, Is.True)
        untilWork ui (fun state -> state.ProjectCount = 1 && not state.Refreshing)
        Assert.That((ui.InspectWorkForFixture(80, 26)).Lines |> List.exists (fun line -> line.Contains("ready")), Is.True)

        Assert.That(press ui 'r' ConsoleKey.R, Is.True)
        Assert.That(staleEntered.Wait(2000), Is.True)
        let refreshing = ui.InspectWorkForFixture(80, 26)
        Assert.That(refreshing.Refreshing, Is.True)
        Assert.That(refreshing.ProjectCount, Is.EqualTo(0))
        Assert.That(refreshing.Lines |> List.exists (fun line -> line.Contains("prior observations suppressed")), Is.True)
        Assert.That(refreshing.Lines |> List.exists (fun line -> line.Contains("stale-ready")), Is.False)

        // Replace the provider while its old Load still executes.
        ui.SetWorkAccess({ Load = fun () -> Ok [ replacement ] })
        untilWork ui (fun state -> state.ProjectCount = 1 && not state.Refreshing)
        releaseStale.Set()
        Thread.Sleep(70)
        let current = ui.InspectWorkForFixture(80, 26)
        Assert.That(current.Lines |> List.exists (fun line -> line.Contains("blocked")), Is.True)
        Assert.That(current.Lines |> List.exists (fun line -> line.Contains("stale-ready")), Is.False)
        Assert.That(current.Selected, Is.EqualTo(Some "project-replacement::blocked"))
    finally releaseStale.Set()

[<Test>]
let terminalFailedRefreshSuppressesLastGoodGitDataAndShowsUnknown () =
    let ui = new ConsoleTui()
    ui.SetWorkAccess({ Load = fun () ->
        Ok [ source "Alpha" "project-alpha" "Verified" [ row "ready" "Available" ] ] })
    ui.ShowWorkView()
    untilWork ui (fun state -> state.ProjectCount = 1 && not state.Refreshing)
    ui.SetWorkAccess({ Load = fun () -> Error "Git ledger unbound" })
    untilWork ui (fun state -> state.Error.IsSome && not state.Refreshing)
    let broken = ui.InspectWorkForFixture(40, 24)
    Assert.That(broken.ProjectCount, Is.EqualTo(0))
    Assert.That(broken.Error, Is.EqualTo(Some "Git ledger unbound"))
    Assert.That(broken.Lines |> List.exists (fun line -> line.Contains("UNKNOWN")), Is.True)
    Assert.That(broken.Lines |> List.exists (fun line -> line.Contains("ready")), Is.False)


[<Test>]
let leadPriorityDisplayedInWorkDetailsWithoutGrantingClaimAuthority () =
    let prioritized = { row "priority" "Available" with Priority = 21 }
    let displayed = allRows [source "One" "proj-one" "Verified" [prioritized]] |> List.head
    Assert.That(displayed.Details |> String.concat "\n", Does.Contain("Lead priority: 21"))
    Assert.That(displayed.Status, Does.StartWith("NotReady"))
