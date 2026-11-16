module Client.Tests.WorkQueueWorkReplacementVerticalSliceTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.WorkQueueProducers
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.WorkQueueReceiptProducer
open Client.WorkQueueRoleAuthority
open Client.WorkCompositionGitLedger
open Client.WorkQueueToolFacade
open Client.WorkQueueCrossProjectHostPreview
open Client.WorkQueueReactiveWake
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter

// WQ11 integration boundary. All writes target disposable Git ledgers.
// Neither tests nor callbacks enable the actual Scheduler, browser or provider.
let private succeed = function
    | Ok value -> value
    | Error error -> failwithf "WQ11 verified fixture error: %A" error

let private git directory arguments =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for arg in arguments do p.StartInfo.ArgumentList.Add arg
    Assert.That(p.Start(), Is.True)
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    if not (p.WaitForExit 30000) then
        p.Kill(true)
        Assert.Fail(sprintf "Disposable git timed out: %A" arguments)
    Assert.That(p.ExitCode, Is.Zero, sprintf "git %A: %s" arguments error)
    output.Trim()

let private withAuthorities test =
    let root = Path.Combine(Path.GetTempPath(), "wq11-work-vertical-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let checkouts = ResizeArray<string>()
    try
        let clones name =
            let remote = Path.Combine(root, name + ".git")
            let first = Path.Combine(root, name + "-first")
            let second = Path.Combine(root, name + "-second")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; first] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "WQ11 disposable " + name)
            git first ["add"; "source.txt"] |> ignore
            git first ["-c"; "user.name=Fixture"; "-c"; "user.email=wq11@example.test"
                       "commit"; "-m"; "source"] |> ignore
            git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; second] |> ignore
            checkouts.Add first
            checkouts.Add second
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            binding first, binding second
        let a1, a2 = clones "project-a"
        let b1, b2 = clones "project-b"
        let accountProject1, accountProject2 = clones "account"
        let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
        let account (source: LedgerBinding) : AccountBinding =
            { RepositoryPath = source.RepositoryPath; RemoteName = source.RemoteName
              ApprovedFetchUrl = source.ApprovedFetchUrl; ApprovedPushUrl = source.ApprovedPushUrl
              AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
        let account1, account2 = account accountProject1, account accountProject2
        for first, second in [a1, a2; b1, b2] do
            LedgerStore.initialize first true |> succeed |> ignore
            LedgerStore.fetch second |> succeed |> ignore
            WorkQueueGitLedger.initialize first "Fixture" (Guid.NewGuid())
                (Map.ofList ["Lead", set ["planning"]]) |> succeed |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Lead" |> succeed |> ignore
            WorkQueueRoleAuthority.initializeCatalog first "Lead" (Guid.NewGuid()) |> succeed |> ignore
            let role: RoleConfiguration =
                { RoleId = "Lead"; Version = 1; Enabled = true
                  Instructions = "WQ11 fake-only fixture"; QueueSubscriptions = set ["planning"] }
            WorkQueueRoleAuthority.configure first "Lead" (Guid.NewGuid()) 0 0 role
            |> succeed |> ignore
        AccountCapacityLedger.initialize account1 1 |> succeed |> ignore
        AccountCapacityLedger.fetch account2 |> succeed |> ignore
        test (a1,a2) (b1,b2) (account1,account2)
        for checkout in checkouts do
            Assert.That(git checkout ["status"; "--porcelain"], Is.Empty,
                        "Disposable Git fixture modified its source checkout")
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private produce source receiver taskId =
    let creationOperation = Guid.NewGuid()
    ReviewWorkflow.createTask source creationOperation taskId ("WQ11 " + taskId)
    |> succeed |> ignore
    WorkQueueReceiptProducer.produce receiver "Lead" (Guid.NewGuid()) creationOperation
    |> succeed |> ignore
    let snapshot, state = WorkQueueGitLedger.load receiver |> succeed
    let stage =
        state.Works |> Map.toSeq |> Seq.map snd
        |> Seq.filter (fun work -> work.SubjectId = taskId)
        |> Seq.exactlyOne
    Assert.That((WorkQueueGitLedger.readOriginAt receiver snapshot stage.Id |> succeed).IsSome, Is.True)
    stage

let private spec title task =
    { Title = title; Goal = "Accept " + title; Scope = "WQ11 verified work"
      AcceptanceCriteria = set ["Pinned review and integration receipt"]
      Constraints = Set.empty; LinkedTaskId = task }

[<TestFixture>]
type WorkQueueWorkReplacementVerticalSliceTests() =
    [<Test>]
    member _.PinnedCompositionAndWq02ViewExposeMissingWorkToSchedulerBindings() =
        withAuthorities (fun (a1,a2) (b1,b2) (account1,account2) ->
            let aStage = produce a1 a2 "project-a-task"
            let bStage = produce b1 b2 "dependent-task"
            // WQ04 sees B as blocking demand because a later stage requires it.
            let dependent: WorkStage =
                { Id = "after-" + Guid.NewGuid().ToString("N")
                  ProjectId = b1.ProjectId.ToString("D")
                  SubjectId = "later-review"
                  Kind = WorkKind.FrozenReview; QueueId = "review"
                  StageGeneration = 1; State = WorkState.Blocked
                  Domain = DomainState.Undecided; Gate = GateState.Clear
                  ClaimGeneration = 0; Dependencies = Set.singleton bStage.Id
                  WaivedDependencies = Map.empty
                  AvailableAt = bStage.AvailableAt + 1L
                  AvailableSequence = bStage.AvailableSequence + 1L
                  SourceLineage = None; Push = None; IntegrationAuthorized = false }
            let _, queue = WorkQueueGitLedger.load b1 |> succeed
            WorkQueueGitLedger.executeInertFixture b1 "Fixture" (Guid.NewGuid()) queue.Version
                (PublishStage dependent) |> succeed |> ignore

            // Separate authoritative composition graph: dependency and parent
            // existence are independently read on the other approved client.
            let changes =
                [ WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None)
                  WorkCompositionMutation.Create("foundation", WorkUnitKind.System, Some "project", spec "Foundation" None)
                  WorkCompositionMutation.Create("delivery", WorkUnitKind.System, Some "project", spec "Delivery" None)
                  WorkCompositionMutation.Create("prerequisite", WorkUnitKind.Feature, Some "foundation", spec "Prerequisite" None)
                  WorkCompositionMutation.Create("dependent", WorkUnitKind.Feature, Some "delivery", spec "Dependent" (Some "dependent-task"))
                  WorkCompositionMutation.Require("dependent", "prerequisite") ]
            changes |> List.iteri (fun version mutation ->
                WorkCompositionGitLedger.execute b1 "Fixture" (Guid.NewGuid()) version mutation
                |> succeed |> ignore)
            let headB, graph = WorkCompositionGitLedger.load b2 |> succeed
            Assert.That(graph.Version, Is.EqualTo(6))
            Assert.That(graph.Units.["dependent"].HardPrerequisites = Set.singleton "prerequisite", Is.True)
            Assert.That(graph.Units.["dependent"].ParentId, Is.EqualTo(Some "delivery"))
            let views = WorkCompositionGitLedger.loadOverviews b2 |> succeed
            let workUnit = views |> List.find (fun view -> view.Id = "dependent")
            Assert.That(workUnit.Phase, Is.EqualTo(WorkUnitPhase.Blocked))
            Assert.That(workUnit.UnmetPrerequisites, Does.Contain("prerequisite"))

            let workView = WorkQueueToolFacade.readWithBinding b2 "Get" "All" bStage.Id |> succeed
            Assert.That(workView.VerifiedGitHead, Is.EqualTo(headB.Commit))
            Assert.That(workView.ReadOnly, Is.True)
            Assert.That(workView.RoleAuthority, Does.StartWith("Pinned WQ04"))
            Assert.That(workView.Work.Head.Category, Is.EqualTo("Available"),
                        "WQ02 queue availability must not be misreported as composition acceptance")
            Assert.That(workView.Work.Head.ClaimEnabled, Is.False)
            Assert.That(workView.ClaimBinding, Does.Contain("not installed"))
            let actor = "operator:" + String.replicate 64 "a"
            let launch = Guid.NewGuid().ToString("D")
            let operation = Guid.NewGuid().ToString("D")
            let rejected =
                WorkQueueToolFacade.transitionWithBinding b1 "ClaimNext" actor operation
                    launch bStage.Id workView.ProjectVersion 0 "" |> succeed
            Assert.That(rejected.Status, Is.EqualTo("RejectedMissingBinding"))
            Assert.That(rejected.Applied, Is.False)
            Assert.That((LedgerStore.fetch b2 |> succeed).Commit, Is.EqualTo(headB.Commit))

            let port label binding : CrossProjectHostProjectPorts =
                { Label = label
                  ProjectAuthoritySha256 =
                    AccountAdmissionAdapter.projectAuthority binding
                    |> AccountCapacityLedger.projectAuthoritySha256
                  ReadProject = fun () ->
                    let head = LedgerStore.fetch binding |> succeed
                    WorkQueueRoleAuthority.loadAt binding head
                    |> Result.map (fun verified -> head, verified)
                    |> Result.mapError (sprintf "%A")
                  ReadOrigin = fun head workId ->
                    WorkQueueGitLedger.readOriginAt binding head workId
                    |> Result.mapError (sprintf "%A")
                  ReadOriginal = fun _ -> Ok None }
            let ports: CrossProjectHostPorts =
                { CompleteProjectSet = true
                  ReadAccount = fun () ->
                    AccountCapacityLedger.fetch account2 |> Result.mapError (sprintf "%A")
                  Projects = [port "A" a2; port "B" b2] }
            let before = WorkQueueCrossProjectHostPreview.previewWith ports
            let replay = WorkQueueCrossProjectHostPreview.previewWith ports
            Assert.That(before.Mode, Is.EqualTo("RealCrossProjectReadOnlyDefaultOff"))
            Assert.That(before.CanDispatch, Is.False)
            Assert.That(before.MaxConcurrentAgents, Is.EqualTo(Some 1))
            Assert.That(before.Projects.Length, Is.EqualTo(2))
            Assert.That(before.Projects |> List.forall (fun p -> p.Head.IsSome), Is.True)
            Assert.That(replay.SimulatedWinner, Is.EqualTo(before.SimulatedWinner))
            Assert.That(before.Issues |> List.exists (fun issue -> issue.Code = "NoDurablePreSendIntent"), Is.True)
            Assert.That((AccountCapacityLedger.fetch account1 |> succeed).State.Tokens, Is.Empty)
            let pinnedB = WorkQueueRoleAuthority.loadAt b2 headB |> succeed
            let demandB = selectRoleDemandPractical pinnedB.Catalog pinnedB.Project "Lead" |> succeed
            Assert.That(demandB.Chosen |> Option.map (fun chosen -> chosen.WorkId),
                        Is.EqualTo(Some bStage.Id))
            Assert.That(demandB.Chosen |> Option.map (fun chosen -> chosen.BlocksUnfinishedStage),
                        Is.EqualTo(Some true))
            // Verified WQ04 CreateTask origin is sufficient for planning-only
            // projection, without inventing a source-writing branch or IDs.
            Assert.That(before.ProjectSetComplete, Is.True)
            Assert.That(before.Issues |> List.exists (fun issue -> issue.Code = "SourceLineageUnknown"),
                        Is.False, sprintf "Genuine planning origin was rejected: %A" before.Issues)
            Assert.That(before.SimulatedWinner |> Option.map _.WorkId, Is.EqualTo(Some bStage.Id))
            Assert.That((WorkQueueToolFacade.readWithBinding a1 "List" "Available" "" |> succeed).Work
                        |> List.exists (fun work -> work.WorkId = aStage.Id), Is.True)

            // The composition CAS cannot accept an arbitrary evidence string.
            // No production verifier currently joins task/review/integration
            // receipts to a composed unit's spec/graph version.
            match WorkCompositionGitLedger.execute b2 "Fixture" (Guid.NewGuid()) graph.Version
                      (WorkCompositionMutation.Accept("prerequisite", 1, "caller-claimed-review")) with
            | Error(InvalidBinding reason) ->
                Assert.That(reason, Does.Contain("independently verified canonical evidence"))
            | other -> Assert.Fail(sprintf "Unverified acceptance bypassed composition CAS: %A" other)
            let _, unchanged = WorkCompositionGitLedger.load b1 |> succeed
            Assert.That(unchanged.Version, Is.EqualTo(graph.Version))
            Assert.That((WorkCompositionGitLedger.loadOverviews b1 |> succeed
                         |> List.find (fun view -> view.Id = "dependent")).Phase,
                        Is.EqualTo(WorkUnitPhase.Blocked))

            // An injected wake observes the real, unmigrated account. Signals
            // are not authority, and a production executor is never invoked.
            let mutable plannerCalls, executorCalls = 0, 0
            let wakePorts: WorkWakePorts =
                { ReadVerified = WorkQueueReactiveWake.boundRead account2 b2 "Lead"
                  Plan = fun _ -> plannerCalls <- plannerCalls + 1; Ok None
                  Execute = fun _ -> executorCalls <- executorCalls + 1; WorkExecutorResult.Rejected "must not run" }
            let defaultOff = WorkQueueReactiveWake.Coordinator(wakePorts)
            Assert.That(defaultOff.Wake [WorkWakeTrigger.IntegrationCommitted], Is.EqualTo(WorkWakeResult.Disabled))
            let explicitlyEnabledFixture = WorkQueueReactiveWake.Coordinator(wakePorts, enabled = true)
            match explicitlyEnabledFixture.Wake [WorkWakeTrigger.DependencyCommitted; WorkWakeTrigger.ReviewAccepted] with
            | WorkWakeResult.NeedsAttention _ -> ()
            | other -> Assert.Fail(sprintf "Unmigrated composition must not wake a sender: %A" other)
            Assert.That(plannerCalls, Is.Zero)
            Assert.That(executorCalls, Is.Zero)
            Assert.That((AccountCapacityLedger.fetch account2 |> succeed).State.MigrationPhase, Is.EqualTo("LegacyActive")))
