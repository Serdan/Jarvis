module Client.Tests.TaskFirstUiSnapshotAdapterTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueOperatorConversations
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Client.ReviewWorkflow
open Client.TaskFirstUiProjection
open Client.TaskFirstUiSnapshotAdapter

let private ok = function
    | Ok value -> value
    | Error reason -> failwithf "Unexpected fixture failure: %A" reason

let private known = function
    | Evidence.Known value -> value
    | Evidence.Unknown reason -> failwithf "Expected verified projection: %s" reason

let private unknown = function
    | Evidence.Unknown _ -> ()
    | Evidence.Known _ -> Assert.Fail("Missing or inconsistent authority must not be displayed as verified empty state")

let private git dir args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- dir
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for arg in args do p.StartInfo.ArgumentList.Add(arg)
    Assert.That(p.Start(), Is.True)
    let output = p.StandardOutput.ReadToEnd()
    let errors = p.StandardError.ReadToEnd()
    Assert.That(p.WaitForExit(30000), Is.True, sprintf "git %A timed out" args)
    Assert.That(p.ExitCode, Is.EqualTo(0), errors)
    output.Trim()

let private makeClones root name =
    let bare = Path.Combine(root, name + "-remote.git")
    let first = Path.Combine(root, name + "-first")
    let second = Path.Combine(root, name + "-second")
    git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
    git root [ "clone"; bare; first ] |> ignore
    File.WriteAllText(Path.Combine(first, "fixture.txt"), "Disposable " + name + " Git fixture")
    git first [ "add"; "fixture.txt" ] |> ignore
    git first [ "-c"; "user.name=WQ09 Fixture"; "-c"; "user.email=wq09@example.test"
                "commit"; "-m"; "disposable fixture" ] |> ignore
    git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
    git root [ "clone"; bare; second ] |> ignore
    bare, first, second

let private withClones action =
    let root = Path.Combine(Path.GetTempPath(), "wq09-readonly-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let projectRemote, p1, p2 = makeClones root "project"
        let pid, lid = Guid.NewGuid(), Guid.NewGuid()
        let project dir : LedgerBinding =
            { RepositoryPath = dir; RemoteName = "origin"
              ApprovedFetchUrl = projectRemote; ApprovedPushUrl = projectRemote
              ProjectId = pid; LedgerId = lid; Epoch = 1 }
        let first, second = project p1, project p2
        LedgerStore.initialize first true |> ok |> ignore
        LedgerStore.fetch second |> ok |> ignore
        WorkQueueGitLedger.initialize first "Fixture" (Guid.NewGuid())
            (Map.ofList [ "Lead", set [ "planning" ] ]) |> ok |> ignore
        WorkQueueRoleAuthority.initializeCatalog second "Fixture" (Guid.NewGuid()) |> ok |> ignore
        let role: RoleConfiguration =
            { RoleId = "Lead"; Version = 1; Instructions = "Read your inbox."
              QueueSubscriptions = set [ "planning" ]; Enabled = true }
        WorkQueueRoleAuthority.configure first "Fixture" (Guid.NewGuid()) 0 0 role |> ok |> ignore

        let accountRemote, a1, a2 = makeClones root "account"
        let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
        let account dir : AccountBinding =
            { RepositoryPath = dir; RemoteName = "origin"
              ApprovedFetchUrl = accountRemote; ApprovedPushUrl = accountRemote
              AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
        let accountFirst, accountSecond = account a1, account a2
        AccountCapacityLedger.initialize accountFirst 2 |> ok |> ignore
        AccountCapacityLedger.fetch accountSecond |> ok |> ignore
        action first second accountFirst accountSecond
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

let private stage projectId id subjectId dependencies sequence =
    { Id = id
      ProjectId = projectId
      SubjectId = subjectId
      Kind = WorkKind.Planning
      QueueId = "planning"
      StageGeneration = 1
      State = WorkState.Available
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.ofList dependencies
      WaivedDependencies = Map.empty
      AvailableAt = 1000L
      AvailableSequence = sequence
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

[<TestFixture>]
type TaskFirstUiSnapshotAdapterTests() =
    [<Test>]
    member _.TwoCloneActualSourcesExposeBlockedFirstTaskTitlePendingLeadAndIndependentOccupancy() =
        withClones (fun first second accountFirst accountSecond ->
            let projectId = first.ProjectId.ToString("D")
            ReviewWorkflow.createTask first (Guid.NewGuid()) "task-A" "Actual canonical task title" |> ok |> ignore
            ReviewWorkflow.createTask second (Guid.NewGuid()) "task-B" "Actual dependent task title" |> ok |> ignore
            WorkQueueGitLedger.executeInertFixtureAt first "Fixture" (Guid.NewGuid()) 0 1000L
                (PublishStage(stage projectId "stage-A" "task-A" [] 1L)) |> ok |> ignore
            WorkQueueGitLedger.executeInertFixtureAt second "Fixture" (Guid.NewGuid()) 1 1001L
                (PublishStage(stage projectId "stage-B" "task-B" [ "stage-A" ] 2L)) |> ok |> ignore
            WorkQueueOperatorConversations.post second "Operator" (Guid.NewGuid()) 0
                "Role" "Lead" "message-role-1" "Please review this dependency" |> ok |> ignore
            let admissionId, launchId = Guid.NewGuid(), Guid.NewGuid()
            AccountCapacityLedger.reserve accountFirst admissionId "Fixture" launchId
                (AccountAdmissionAdapter.projectAuthority first) "Lead" 1L |> ok |> ignore

            let ports = TaskFirstUiSnapshotAdapter.portsForBindings second (Some accountSecond)
            let report = TaskFirstUiSnapshotAdapter.inspectWith ports
            let output = TaskFirstUiSnapshotAdapter.renderReport report |> String.concat "\n"
            let next = known report.Dashboard.Next
            let blocked = known report.Dashboard.Blocked
            Assert.That((next |> List.map _.WorkId), Is.EqualTo(box [ "stage-A" ]))
            Assert.That((blocked |> List.map _.WorkId), Does.Contain("stage-B"))
            Assert.That(blocked.Head.HardBlockers, Does.Contain("stage-A"))
            Assert.That(next.Head.SubjectTitle, Is.EqualTo("Actual canonical task title"))
            Assert.That((known report.Roles).Head.RoleId, Is.EqualTo("Lead"))
            Assert.That((known report.OperatorThreads) |> List.exists (fun t ->
                t.Kind = "Role" && t.SubjectId = "Lead"
                && t.Messages |> List.exists (fun msg ->
                    msg.Id = "message-role-1" && msg.Status = "Pending")), Is.True)
            Assert.That((known report.AccountObserved).Occupied, Is.EqualTo(1))
            Assert.That((known report.AccountObserved).Limit, Is.EqualTo(2))
            unknown report.Dashboard.Capacity
            Assert.That(output, Does.Contain("Please review this dependency"))
            Assert.That(output, Does.Contain("Account-global capacity\n  UNKNOWN"))
            Assert.That(output, Does.Contain("UNKNOWN safe free slots"))
            Assert.That(output, Does.Contain("head "))
            Assert.That(output, Does.Contain("stage-A"))
            Assert.That(output, Does.Not.Contain("1/2 occupied; 1 free")))

    [<Test>]
    member _.UnregisteredLaunchAndUncertainOriginalSendNeverInventSessionUuid() =
        withClones (fun first second accountFirst accountSecond ->
            let projectId = first.ProjectId.ToString("D")
            WorkQueueGitLedger.executeInertFixtureAt first "Fixture" (Guid.NewGuid()) 0 1000L
                (PublishStage(stage projectId "stage-A" "task-A" [] 1L)) |> ok |> ignore
            let admissionId, launchId = Guid.NewGuid(), Guid.NewGuid()
            AccountCapacityLedger.reserve accountFirst admissionId "Fixture" launchId
                (AccountAdmissionAdapter.projectAuthority first) "Lead" 1L |> ok |> ignore
            let launch =
                { Id = launchId.ToString("D")
                  ProjectId = projectId
                  RoleId = "Lead"
                  RoleVersion = 1
                  AccountTokenId = admissionId.ToString("D")
                  State = LaunchState.ReservedUnregistered
                  Lease = None
                  Version = 0
                  Credit = None
                  PrimaryWorkId = None
                  EverClaimed = false
                  AncillaryMessages = Set.empty
                  DispatchCount = 0 }
            WorkQueueGitLedger.executeInertFixtureAt second "Fixture" admissionId 1 1001L
                (AdmitProject launch) |> ok |> ignore
            WorkQueueGitLedger.executeInertFixtureAt first "Fixture" (Guid.NewGuid()) 2 1002L
                (DispatchOnce(launch.Id, DispatchState.OutcomeUnknown)) |> ok |> ignore
            let report =
                TaskFirstUiSnapshotAdapter.inspectWith
                    (TaskFirstUiSnapshotAdapter.portsForBindings second (Some accountSecond))
            let sends = known report.OriginalSends
            Assert.That(sends.Length, Is.EqualTo(1))
            Assert.That(sends.Head.LaunchId, Is.EqualTo(launch.Id))
            Assert.That(Option.isNone sends.Head.RegisteredUuid, Is.True)
            Assert.That(sends.Head.Dispatch, Does.Contain("UNKNOWN"))
            Assert.That(sends.Head.OriginalJournal, Does.Contain("UNKNOWN"))
            // The independent WQ04 role reader should reject a pre-WQ04
            // fixture admission rather than fabricate versioned role authority.
            unknown report.Roles
            unknown report.Dashboard.Capacity
            let output = TaskFirstUiSnapshotAdapter.renderReport report |> String.concat "\n"
            Assert.That(output, Does.Contain(launch.Id))
            Assert.That(output, Does.Contain("UUID: (unregistered/unknown)"))
            Assert.That(output, Does.Not.Contain("UUID: 00000000-0000-0000-0000-000000000000")))

    [<Test>]
    member _.MissingAndMismatchedSourcesStayUnknownWithoutZeroOrReplayedRows() =
        withClones (fun first second _ _ ->
            let projectId = first.ProjectId.ToString("D")
            WorkQueueGitLedger.executeInertFixtureAt first "Fixture" (Guid.NewGuid()) 0 1000L
                (PublishStage(stage projectId "stage-A" "task-A" [] 1L)) |> ok |> ignore
            let real = TaskFirstUiSnapshotAdapter.portsForBindings second None
            let report =
                TaskFirstUiSnapshotAdapter.inspectWith
                    { real with
                        ReadRoles = fun _ -> Error "Role catalog unavailable"
                        ReadOperatorThread = fun _ _ _ -> Error "Operator conversation source missing"
                        ReadTaskTitle = fun _ _ -> Error "Canonical task file not yet available" }
            Assert.That((known report.Dashboard.Next).Length, Is.EqualTo(1))
            Assert.That((known report.Dashboard.Next).Head.SubjectTitle, Is.EqualTo("task-A"))
            unknown report.Roles
            unknown report.OperatorThreads
            unknown report.AccountObserved
            unknown report.Dashboard.Capacity
            Assert.That(report.SourceWarnings |> List.exists (_.Contains("task-A")), Is.True)
            let text = TaskFirstUiSnapshotAdapter.renderReport report |> String.concat "\n"
            Assert.That(text, Does.Contain("UNKNOWN - WQ06 conversation source incomplete"))
            Assert.That(text, Does.Contain("UNKNOWN task title"))
            Assert.That(text, Does.Not.Contain("0/0 occupied"))

            let noProject =
                TaskFirstUiSnapshotAdapter.inspectWith
                    { real with ReadProject = fun () -> Error "Project head mismatch" }
            unknown noProject.ProjectHead
            unknown noProject.Dashboard.Next
            unknown noProject.OriginalSends
            let noProjectText = TaskFirstUiSnapshotAdapter.renderReport noProject |> String.concat "\n"
            Assert.That(noProjectText, Does.Contain("Project head mismatch"))
            Assert.That(noProjectText, Does.Not.Contain("0 unfinished")))

    [<Test>]
    member _.TwoCloneHardCycleShowsExactWitnessPathWithoutClaimingRunnableWork() =
        withClones (fun first second _ _ ->
            let projectId = first.ProjectId.ToString("D")
            WorkQueueGitLedger.executeInertFixtureAt first "Fixture" (Guid.NewGuid()) 0 1000L
                (PublishStage(stage projectId "stage-A" "task-A" [] 1L)) |> ok |> ignore
            WorkQueueGitLedger.executeInertFixtureAt second "Fixture" (Guid.NewGuid()) 1 1001L
                (PublishStage(stage projectId "stage-B" "task-B" [ "stage-A" ] 2L)) |> ok |> ignore
            WorkQueueGitLedger.executeInertFixtureAt first "Fixture" (Guid.NewGuid()) 2 1002L
                (SetHardPrerequisite("stage-A", "stage-B")) |> ok |> ignore
            let headBefore = (LedgerStore.fetch first |> ok).Commit
            Assert.That((LedgerStore.fetch second |> ok).Commit, Is.EqualTo(headBefore))

            let ports = TaskFirstUiSnapshotAdapter.portsForBindings second None
            let report = TaskFirstUiSnapshotAdapter.inspectWith ports
            let cycles = known report.Dashboard.Cycles
            Assert.That(cycles.Length, Is.EqualTo(1))
            Assert.That(cycles.Head.ExactStagePath,
                        Is.EqualTo(box [ "stage-A"; "stage-B"; "stage-A" ]))
            Assert.That(known report.Dashboard.Next, Is.Empty)
            Assert.That((known report.Dashboard.Blocked |> List.map _.WorkId),
                        Is.EquivalentTo([ "stage-A"; "stage-B" ]))
            let rendered = TaskFirstUiSnapshotAdapter.renderReport report |> String.concat "\n"
            Assert.That(rendered, Does.Contain("stage-A -> stage-B -> stage-A"))
            unknown report.Dashboard.Capacity

            // Repeated reads from separate clones must neither mutate Git nor
            // resurrect a runnable stage out of a hard cycle.
            let again = TaskFirstUiSnapshotAdapter.inspectWith
                            (TaskFirstUiSnapshotAdapter.portsForBindings first None)
            Assert.That(again.ProjectHead, Is.EqualTo(report.ProjectHead))
            Assert.That(known again.Dashboard.Next, Is.Empty)
            Assert.That((LedgerStore.fetch second |> ok).Commit, Is.EqualTo(headBefore)))
