module Client.Tests.WorkQueueDashboardHostTests

open System
open System.Collections.Generic
open System.Threading
open Client.ConsoleTui
open Client.LedgerStore
open Client.WorkQueueToolFacade
open Client.WorkQueueDashboardHost
open Client.WorkDashboardView
open NUnit.Framework

let private oldHead = String.replicate 40 "a"
let private newHead = String.replicate 40 "b"
let private afterHead = String.replicate 40 "c"

let private binding (id: string) (tag: string) : LedgerBinding =
    { RepositoryPath = "/approved/" + tag
      RemoteName = "origin"
      ApprovedFetchUrl = "https://private.invalid/" + tag
      ApprovedPushUrl = "https://private.invalid/" + tag
      ProjectId = Guid.Parse id
      LedgerId = Guid.NewGuid()
      Epoch = 1 }

let private work (id: string) (category: string) (dependencies: WorkDependencyReason list) : WorkToolRow =
    { WorkId = id; SubjectId = "task-" + id
      QueueId = "implementation"; Priority = 0; Kind = "Implementation"
      State = category; Category = category
      StageGeneration = 3; ClaimGeneration = 2; GraphVersion = 12
      Gate = "Clear"; Domain = "Undecided"
      ClaimLaunchId = None; SessionUuid = None; Liveness = "Not claimed"
      Dependencies = dependencies; Reasons = []; ClaimEnabled = false }

let private dependency : WorkDependencyReason =
    { WorkId = "ready"; StageGeneration = Some 3
      ClaimGeneration = Some 2; State = "Available"
      Satisfied = false; WaiverReceipt = None
      Reason = "Canonical dependency unmet at graph v12" }

let private snapshot (binding: LedgerBinding) (work: WorkToolRow list) : WorkToolSnapshot =
    { ProjectId = binding.ProjectId.ToString("D")
      VerifiedGitHead = newHead; ProjectVersion = 9; GraphVersion = 12
      Coverage = "Verified"
      RoleAuthority = "Pinned WQ04 catalog revision 1 at " + newHead + " (observation only)"
      ClaimBinding = WorkQueueToolFacade.MissingClaimBinding
      Work = work; ReadOnly = true }

let private alpha = binding "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" "alpha"
let private beta = binding "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" "beta"

let private makePorts () =
    let anchors = Dictionary<string, string>(StringComparer.Ordinal)
    anchors["Alpha"] <- oldHead
    anchors["Zulu"] <- oldHead
    let bindings = Map [ "Alpha", alpha; "Zulu", beta ]
    let snapshots =
        Map [ "Alpha", snapshot alpha [ work "ready" "Available" []; work "held" "Blocked" [ dependency ] ]
              "Zulu", snapshot beta [ work "uncertain" "Unknown" [] ] ]
    let mutable failRead = false
    let mutable advanceTrustAfterRead = false
    let ports =
        { ListGitProjects = fun () -> Ok [ "Zulu"; "Alpha" ]
          InspectLocal = fun name ->
              let head = anchors[name]
              Ok (bindings[name], head)
          ReadPinned = fun name ->
              if failRead && name = "Alpha" then
                  Error "https://user:secret@private.invalid/approved/alpha"
              else
                  anchors[name] <- if advanceTrustAfterRead && name = "Alpha" then afterHead else newHead
                  Ok (bindings[name], snapshots[name]) }
    ports,
    (fun () -> failRead <- true),
    (fun () -> advanceTrustAfterRead <- true)

[<Test>]
let productionOptInDefaultsOffAndDoesNotEvaluateProvider () =
    let ui = ConsoleTui()
    installIfEnabled false ui (fun () -> failwith "disabled provider must not run")
    ui.ShowWorkView()
    let frame = ui.InspectWorkForFixture(100, 28)
    Assert.That(frame.ProjectCount, Is.EqualTo(0))
    Assert.That(frame.Lines |> List.exists (fun x ->
        x.Contains("no Work Git-snapshot provider is bound")), Is.True)

[<Test>]
let twoVerifiedProjectsRetainExactWorkDependenciesAndUnknownAccountMembership () =
    let ports, _, _ = makePorts()
    let sources = (createAccessWithPorts ports).Load() |> function
        | Ok value -> value
        | Error reason -> failwith reason
    let actualNames = sources |> List.map (fun source -> source.ProjectName)
    let expectedNames = [ "[Account membership UNKNOWN]"; "Alpha"; "Zulu" ]
    Assert.That((actualNames = expectedNames), Is.True)
    Assert.That(sources.Head.Snapshot.IsError, Is.True)
    for source in sources.Tail do
        let snap = source.Snapshot |> function
            | Ok value -> value
            | Error reason -> failwith reason
        Assert.That(snap.VerifiedGitHead, Is.EqualTo(newHead))
        Assert.That(snap.ReadOnly, Is.True)
        Assert.That(snap.Work |> List.forall (fun row ->
            not row.ClaimEnabled && row.SubjectTitle.IsNone), Is.True)
    let rows = allRows sources
    let ready = rows |> List.find (fun row -> row.WorkId = "ready")
    let held = rows |> List.find (fun row -> row.WorkId = "held")
    let uncertain = rows |> List.find (fun row -> row.WorkId = "uncertain")
    Assert.That(ready.Status, Is.EqualTo("NotReady (queue available)"))
    Assert.That(held.Status, Is.EqualTo("Blocked"))
    Assert.That(uncertain.Status, Is.EqualTo("UNKNOWN"))
    Assert.That(held.Details |> String.concat " ", Does.Contain("stage v3 / claim v2"))
    Assert.That(rows |> List.exists (fun row ->
        row.Project.Contains("Account membership UNKNOWN") && row.Status.Contains("NotReady")), Is.True)

[<Test>]
let staleTrustHeadCannotLeakPreviouslyVerifiedAvailableRow () =
    let ports, _, advance = makePorts()
    let first = (createAccessWithPorts ports).Load() |> Result.defaultWith failwith
    Assert.That(allRows first |> List.exists (fun row -> row.WorkId = "ready"), Is.True)
    advance ()
    let after = (createAccessWithPorts ports).Load() |> Result.defaultWith failwith
    Assert.That(allRows after |> List.exists (fun row -> row.WorkId = "ready"), Is.False)
    let alphaRow = allRows after |> List.find (fun row -> row.Project = "Alpha")
    Assert.That(alphaRow.Status, Does.Contain("NotReady"))
    Assert.That(alphaRow.Details |> String.concat " ", Does.Contain("trust head"))
    Assert.That(allRows after |> List.exists (fun row -> row.WorkId = "uncertain"), Is.True)

[<Test>]
let refreshOnRealTuiClearsOldWorkAndHidesSensitiveFailureDetails () =
    let ports, fail, _ = makePorts()
    let ui = ConsoleTui()
    installIfEnabled true ui (fun () -> createAccessWithPorts ports)
    ui.ShowWorkView()
    let snapshot () = ui.InspectWorkForFixture(120, 40)
    Assert.That(SpinWait.SpinUntil((fun () ->
        let view = snapshot ()
        not view.Refreshing && (view.Lines |> List.exists (fun x -> x.Contains("ready")))), 4000),
        Is.True)
    fail ()
    let pressed = ui.DispatchWorkKeyForFixture(ConsoleKeyInfo('r', ConsoleKey.R, false, false, false))
    Assert.That(pressed, Is.True)
    Assert.That(SpinWait.SpinUntil((fun () ->
        let view = snapshot ()
        not view.Refreshing && (view.Lines |> List.exists (fun x -> x.Contains("UNKNOWN")))), 4000),
        Is.True)
    let refreshed = snapshot ()
    Assert.That(refreshed.Lines |> List.exists (fun x -> x.Contains("ready")), Is.False)
    Assert.That(refreshed.Lines |> List.exists (fun x -> x.Contains("secret")), Is.False)
    Assert.That(refreshed.Lines |> List.exists (fun x -> x.Contains("private.invalid")), Is.False)
    Assert.That(refreshed.Lines |> List.exists (fun x -> x.Contains("uncertain")), Is.True)

[<Test>]
let duplicateCanonicalProjectIdentityNeverProducesWorkRows () =
    let ports, _, _ = makePorts()
    let colliding =
        { ports with
            InspectLocal = fun _ -> Ok (alpha, oldHead)
            ReadPinned = fun _ -> Ok (alpha, snapshot alpha [ work "must-not-appear" "Available" [] ]) }
    let sources = loadWithPorts colliding |> Result.defaultWith failwith
    Assert.That(sources |> List.forall (fun row -> row.Snapshot.IsError), Is.True)
    Assert.That(allRows sources |> List.exists (fun row -> row.WorkId = "must-not-appear"), Is.False)

[<Test>]
let unboundProjectAndChangedBindingAreProjectLocalUnknown () =
    let ports, _, _ = makePorts()
    let missing =
        { ports with
            InspectLocal = (fun name ->
                if name = "Zulu" then Error "local trust file absent"
                else ports.InspectLocal name) }
    let sources = loadWithPorts missing |> Result.defaultWith failwith
    Assert.That((sources |> List.find (fun row -> row.ProjectName = "Alpha")).Snapshot.IsOk, Is.True)
    Assert.That((sources |> List.find (fun row -> row.ProjectName = "Zulu")).Snapshot.IsError, Is.True)
    let swapped =
        { ports with
            ReadPinned = (fun name ->
                if name = "Alpha" then Ok (beta, snapshot beta [ work "wrong-source" "Available" [] ])
                else ports.ReadPinned name) }
    let swappedSources = loadWithPorts swapped |> Result.defaultWith failwith
    Assert.That(allRows swappedSources |> List.exists (fun row -> row.WorkId = "wrong-source"), Is.False)
