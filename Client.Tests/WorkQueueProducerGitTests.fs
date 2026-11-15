module Client.Tests.WorkQueueProducerGitTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
module Producer = Jarvis.WorkQueues.WorkQueueProducers
let workId = Producer.workId
let projectEvent = Producer.projectEvent
let emptyRoleCatalog = Producer.emptyRoleCatalog
let configureRole = Producer.configureRole
let selectRoleDemand = Producer.selectRoleDemand
open Client.LedgerStore
open Client.WorkQueueGitLedger

[<TestFixture>]
type WorkQueueProducerGitTests() =
    let success = function
        | Ok value -> value
        | Error failure -> failwithf "Unexpected two-clone CAS failure: %A" failure

    let git directory args =
        use command = new Process()
        command.StartInfo.FileName <- "git"
        command.StartInfo.WorkingDirectory <- directory
        command.StartInfo.RedirectStandardOutput <- true
        command.StartInfo.RedirectStandardError <- true
        for argument in args do command.StartInfo.ArgumentList.Add(argument)
        Assert.That(command.Start(), Is.True)
        let output = command.StandardOutput.ReadToEnd()
        let failure = command.StandardError.ReadToEnd()
        command.WaitForExit()
        Assert.That(command.ExitCode, Is.EqualTo(0), failure)
        output.Trim()

    let withTwoClones work =
        let root = Path.Combine(Path.GetTempPath(), "wq04-producer-cas-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
            git root [ "clone"; bare; first ] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "disposable only")
            git first [ "add"; "source.txt" ] |> ignore
            git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                        "commit"; "-m"; "fixture" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; second ] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = bare
                  ApprovedPushUrl = bare
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> success |> ignore
            LedgerStore.fetch b |> success |> ignore
            WorkQueueGitLedger.initialize a "Lead" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning"; "role:Lead" ]
                              "Reviewer", set [ "review" ] ])
            |> success |> ignore
            work a b (projectId.ToString("D"))
        finally
            try Directory.Delete(root, true) with _ -> ()

    let event project source origin dependencies =
        { ProjectId = project
          SourceId = source
          StageGeneration = 1
          Origin = origin
          HardPrerequisites = set dependencies }

    let message id subject =
        { Id = id; Subject = subject; Body = "Operator message"
          State = MessageState.Pending; HandlerGeneration = 0; WorkId = None }

    let execute binding actor op version at mutation =
        WorkQueueGitLedger.executeAt binding actor op version at mutation

    [<Test>]
    member _.ConcurrentProducerSnapshotsUseWinningGitCASAndDeterministicBlockingFirstFIFO() =
        withTwoClones (fun a b project ->
            let _, beforeA = WorkQueueGitLedger.load a |> success
            let _, beforeB = WorkQueueGitLedger.load b |> success
            Assert.That(beforeA.Version, Is.EqualTo(beforeB.Version))
            let normal = event project "task-normal" (QueueProducerOrigin.TaskRequested "normal") []
            let review = event project "review-needed" (QueueProducerOrigin.FrozenReviewRequested(
                            "dependent", "review-1", String.replicate 40 "a", "refs/heads/frozen")) []
            execute a "Lead" (Guid.NewGuid()) beforeA.Version 1000L
                (ProduceEvent normal) |> success |> ignore
            match execute b "Lead" (Guid.NewGuid()) beforeB.Version 1001L
                     (ProduceEvent review) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale second clone must lose CAS: %A" other)
            let _, fresh = WorkQueueGitLedger.load b |> success
            execute b "Lead" (Guid.NewGuid()) fresh.Version 1001L
                (ProduceEvent review) |> success |> ignore
            let _, both = WorkQueueGitLedger.load a |> success
            let dependent = event project "task-after-review" (QueueProducerOrigin.TaskRequested "dependent")
                                    [ workId review ]
            execute a "Lead" (Guid.NewGuid()) both.Version 1002L
                (ProduceEvent dependent) |> success |> ignore
            let _, final = WorkQueueGitLedger.load b |> success
            Assert.That(final.Works.[workId normal].AvailableAt, Is.EqualTo(1000L))
            Assert.That(final.Works.[workId review].AvailableAt, Is.EqualTo(1001L))
            Assert.That(final.Works.[workId dependent].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(final.Works.[workId dependent].AvailableSequence, Is.EqualTo(0L))
            let ranked = Contracts.orderAvailable final |> success |> List.map (fun x -> x.Id)
            Assert.That(ranked, Is.EqualTo(box [ workId review; workId normal ]))
            Assert.That(final.Works.Count, Is.EqualTo(3)))

    [<Test>]
    member _.RoleMessageIsExactlyOneLaunchDemandAndTaskMessageRemainsPassiveAcrossTwoClones() =
        withTwoClones (fun a b project ->
            let roleEvent = event project "operator-role-one" (QueueProducerOrigin.OperatorMessage(message "M1" (Subject.Role "Lead"))) []
            let roleDuplicate = event project "operator-role-duplicate" (QueueProducerOrigin.OperatorMessage(message "M1" (Subject.Role "Lead"))) []
            let taskEvent = event project "operator-task-two" (QueueProducerOrigin.OperatorMessage(message "M2" (Subject.Task "T"))) []
            let _, priorA = WorkQueueGitLedger.load a |> success
            let _, priorB = WorkQueueGitLedger.load b |> success
            execute a "Lead" (Guid.NewGuid()) priorA.Version 200L (ProduceEvent roleEvent)
            |> success |> ignore
            match execute b "Lead" (Guid.NewGuid()) priorB.Version 201L (ProduceEvent roleDuplicate) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Concurrent duplicate must lose CAS: %A" other)
            let _, latest = WorkQueueGitLedger.load b |> success
            match execute b "Lead" (Guid.NewGuid()) latest.Version 202L (ProduceEvent roleDuplicate) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Second stage for one Role message must be rejected: %A" other)
            let _, again = WorkQueueGitLedger.load a |> success
            execute a "Lead" (Guid.NewGuid()) again.Version 203L (ProduceEvent taskEvent)
            |> success |> ignore
            let _, final = WorkQueueGitLedger.load b |> success
            Assert.That(final.Works.Count, Is.EqualTo(1))
            Assert.That(final.Works.ContainsKey(workId roleEvent), Is.True)
            Assert.That(final.Messages.["M1"].WorkId, Is.EqualTo(Some(workId roleEvent)))
            Assert.That(final.Messages.["M2"].WorkId.IsNone, Is.True)
            let catalog = configureRole emptyRoleCatalog.Revision 0 "lead-config"
                                { RoleId = "Lead"; Version = 1; Instructions = "Lead"
                                  QueueSubscriptions = set [ "planning"; "role:Lead" ]; Enabled = true }
                                emptyRoleCatalog |> success
            let proof =
                { ProjectVersion = final.Version; GraphVersion = final.GraphVersion
                  ProducerHighWater = 3L; QueueIndexedThrough = 3L
                  MessageHighWater = 2L; MessageIndexedThrough = 2L
                  SourceHighWater = 0L; SourceIndexedThrough = 0L
                  RoleCatalogRevision = catalog.Revision
                  ProjectHeadVerified = true; CrossProjectCovered = true }
            let demand = selectRoleDemand proof catalog final "Lead" |> success
            Assert.That(demand.Chosen.Value.WorkId, Is.EqualTo(workId roleEvent))
            let incomplete = { proof with MessageIndexedThrough = 1L }
            match selectRoleDemand incomplete catalog final "Lead" with
            | Error(Rejection.IncompleteCoverage _) -> ()
            | other -> Assert.Fail(sprintf "Missing message high-water must fail closed: %A" other))

    [<Test>]
    member _.ConcurrentHardEdgeRemovalClearsSccAndWakesWithFreshFifoWithoutStaleBypass() =
        withTwoClones (fun a b project ->
            let x = event project "task-X" (QueueProducerOrigin.TaskRequested "X") []
            let y = event project "task-Y" (QueueProducerOrigin.TaskRequested "Y") []
            let publish binding e time =
                let _, p = WorkQueueGitLedger.load binding |> success
                execute binding "Lead" (Guid.NewGuid()) p.Version time (ProduceEvent e)
                |> success |> ignore
            publish a x 10L
            publish b y 20L
            let _, baseline = WorkQueueGitLedger.load a |> success
            execute a "Lead" (Guid.NewGuid()) baseline.Version 30L
                (SetHardPrerequisite(workId x, workId y)) |> success |> ignore
            let _, stale = WorkQueueGitLedger.load b |> success
            execute b "Lead" (Guid.NewGuid()) stale.Version 31L
                (SetHardPrerequisite(workId y, workId x)) |> success |> ignore
            let _, cyclic = WorkQueueGitLedger.load a |> success
            let diagnostics = WorkQueueDependencyDiagnostics.analyze cyclic.GraphVersion true cyclic.Works |> success
            Assert.That(diagnostics.CycleWarnings.Length, Is.EqualTo(1))
            Assert.That(Contracts.orderAvailable cyclic |> success, Is.Empty)
            match execute a "Lead" (Guid.NewGuid()) baseline.Version 32L
                       (RemoveHardPrerequisite(workId y, workId x)) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale edge removal must lose CAS: %A" other)
            let _, latest = WorkQueueGitLedger.load a |> success
            execute a "Lead" (Guid.NewGuid()) latest.Version 33L
                (RemoveHardPrerequisite(workId y, workId x)) |> success |> ignore
            let _, repaired = WorkQueueGitLedger.load b |> success
            let warnings = WorkQueueDependencyDiagnostics.analyze repaired.GraphVersion true repaired.Works |> success
            Assert.That(warnings.CycleWarnings, Is.Empty)
            Assert.That(repaired.Works.[workId y].State, Is.EqualTo(WorkState.Available))
            Assert.That(repaired.Works.[workId y].AvailableAt, Is.EqualTo(33L))
            Assert.That(repaired.Works.[workId y].AvailableSequence,
                        Is.GreaterThan(cyclic.NextAvailability))
            Assert.That(repaired.Works.[workId x].State, Is.EqualTo(WorkState.Blocked)))
