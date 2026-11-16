module Client.Tests.WorkDashboardViewTests

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
