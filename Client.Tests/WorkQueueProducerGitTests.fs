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

    let rejectProducerRoute = function
        | Error(LedgerIntegrityError reason)
            when reason.Contains("WQ-04 canonical event/index authority missing", StringComparison.Ordinal) -> ()
        | other ->
            Assert.Fail(sprintf "Unverified WQ-04 event must be RecoveryBlocked/Incomplete: %A" other)

    let rejectRoleDemand = function
        | Error(Rejection.IncompleteCoverage reason)
            when reason.Contains("pinned role admission proof unavailable", StringComparison.Ordinal) -> ()
        | other ->
            Assert.Fail(sprintf "Unbound versioned role demand must fail closed: %A" other)

    let coverage (p: Project) revision =
        { ProjectVersion = p.Version
          GraphVersion = p.GraphVersion
          // Deliberately fabricated, but perfectly equal, caller counters.
          ProducerHighWater = 0L; QueueIndexedThrough = 0L
          MessageHighWater = 0L; MessageIndexedThrough = 0L
          SourceHighWater = 0L; SourceIndexedThrough = 0L
          RoleCatalogRevision = revision
          ProjectHeadVerified = true; CrossProjectCovered = true }

    [<Test>]
    member _.ForgedReviewAcceptedIntegrationAndChallengeCannotCrossProjectCAS() =
        withTwoClones (fun a b project ->
            let fakeReview = event project "invented-review-source" (QueueProducerOrigin.FrozenReviewRequested("T", "not-a-real-review", String.replicate 40 "a", "refs/heads/unverified")) []
            let unacceptedIntegration = event project "accepted-assertion" (QueueProducerOrigin.AcceptedIntegration("T", "undecided-review", String.replicate 40 "b", "refs/heads/main")) []
            let mismatchedSource = event project "unverified-accepted-source" (QueueProducerOrigin.AcceptedIntegration("T", "accepted-elsewhere", String.replicate 40 "c", "refs/heads/not-the-target")) []
            let replayedGeneration = { fakeReview with StageGeneration = 99 }
            let work =
                [ fakeReview; unacceptedIntegration; mismatchedSource; replayedGeneration
                  event project "task-not-in-source-ledger" (QueueProducerOrigin.TaskRequested "T") []
                  event project "missing-challenge" (QueueProducerOrigin.ChallengeFollowUp("T", "C")) []
                  event project "role-msg" (QueueProducerOrigin.OperatorMessage(message "M1" (Subject.Role "Lead"))) []
                  event project "task-msg" (QueueProducerOrigin.OperatorMessage(message "M2" (Subject.Task "T"))) [] ]
            let _, beforeA = WorkQueueGitLedger.load a |> success
            let _, beforeB = WorkQueueGitLedger.load b |> success
            for source in work do
                rejectProducerRoute
                    (execute a "Lead" (Guid.NewGuid()) beforeA.Version 10L (ProduceEvent source))
                rejectProducerRoute
                    (execute b "Lead" (Guid.NewGuid()) beforeB.Version 11L (ProduceEvent source))
            let _, afterA = WorkQueueGitLedger.load a |> success
            let _, afterB = WorkQueueGitLedger.load b |> success
            Assert.That(afterA.Version, Is.EqualTo(beforeA.Version))
            Assert.That(afterB.Version, Is.EqualTo(beforeB.Version))
            Assert.That(afterA.Works, Is.Empty)
            Assert.That(afterB.Works, Is.Empty)
            Assert.That(afterA.Messages, Is.Empty))

    [<Test>]
    member _.EqualFabricatedWatermarksAndRoleVersionDriftCannotSelectLaunchDemand() =
        withTwoClones (fun a b project ->
            let v1 =
                { RoleId = "Lead"; Version = 1; Instructions = "v1"; Enabled = true
                  QueueSubscriptions = set [ "planning"; "role:Lead" ] }
            let initial = configureRole emptyRoleCatalog.Revision 0 "config-v1" v1 emptyRoleCatalog |> success
            let v2 = { v1 with Version = 2; Instructions = "v2"; QueueSubscriptions = set [ "review" ] }
            let changed = configureRole initial.Revision 1 "config-v2" v2 initial |> success
            Assert.That(changed.Versions.["Lead"].[1].QueueSubscriptions = v1.QueueSubscriptions,
                        Is.True, "v1 remains pinned in pure history")
            let _, pa = WorkQueueGitLedger.load a |> success
            let _, pb = WorkQueueGitLedger.load b |> success
            // Both a version matching the legacy map and a changed v2 with
            // no durable project role catalog must fail closed. The flags and
            // counters are equal but say nothing about missing source events.
            rejectRoleDemand (selectRoleDemand (coverage pa initial.Revision) initial pa "Lead")
            rejectRoleDemand (selectRoleDemand (coverage pb changed.Revision) changed pb "Lead")
            let disabled = configureRole changed.Revision 2 "config-v3" { v2 with Version = 3; Instructions = "disabled"; Enabled = false } changed |> success
            rejectRoleDemand (selectRoleDemand (coverage pb disabled.Revision) disabled pb "Lead")
            let missing = { changed with Versions = changed.Versions.Add("Lead", Map.ofList [ 2, v2 ]) }
            rejectRoleDemand (selectRoleDemand (coverage pa missing.Revision) missing pa "Lead")
            Assert.That(pa.Version, Is.EqualTo(pb.Version)))

    [<Test>]
    member _.RacedSourceEventsAndChangedOperationReplayStayUnpublished() =
        withTwoClones (fun a b project ->
            let proposed = event project "task-id-1" (QueueProducerOrigin.TaskRequested "T") []
            let edited = { proposed with SourceId = "different-source-for-same-task" }
            let originalOperation = Guid.NewGuid()
            let _, first = WorkQueueGitLedger.load a |> success
            let _, second = WorkQueueGitLedger.load b |> success
            rejectProducerRoute (execute a "Lead" originalOperation first.Version 100L (ProduceEvent proposed))
            rejectProducerRoute (execute b "Lead" originalOperation second.Version 101L (ProduceEvent edited))
            rejectProducerRoute (execute a "Lead" originalOperation first.Version 102L (ProduceEvent edited))
            rejectProducerRoute (execute b "Lead" (Guid.NewGuid()) second.Version 103L (ProduceEvent proposed))
            let _, after = WorkQueueGitLedger.load a |> success
            Assert.That(after.Version, Is.EqualTo(first.Version))
            Assert.That(after.Receipts, Is.Empty)
            Assert.That(after.Works, Is.Empty)
            // Unrelated WQ-02 task attention still uses legitimate CAS:
            // the second clone cannot overwrite its winning sibling.
            execute a "Lead" (Guid.NewGuid()) after.Version 104L
                (PostMessage(message "cas-winner" (Subject.Task "T"), None))
            |> success |> ignore
            match execute b "Lead" (Guid.NewGuid()) second.Version 105L
                      (PostMessage(message "cas-loser" (Subject.Task "T"), None)) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Two-clone stale mutation must lose: %A" other)
            let _, final = WorkQueueGitLedger.load b |> success
            Assert.That(final.Messages.ContainsKey "cas-winner", Is.True)
            Assert.That(final.Messages.ContainsKey "cas-loser", Is.False)
            Assert.That(final.Works, Is.Empty))

    [<Test>]
    member _.PolicySuccessorIdentityCannotBeSplitByCallerLabelsOrRacingClones() =
        withTwoClones (fun a b project ->
            let origin =
                QueueProducerOrigin.IntegrationPolicyResolved("T", "prior-integration-stage", "receipt-one")
            let first = event project "caller-1" origin [ "prior-integration-stage" ]
            let changed = { first with
                                SourceId = "caller-2"
                                Origin = QueueProducerOrigin.IntegrationPolicyResolved(
                                    "T", "prior-integration-stage", "receipt-two")
                                StageGeneration = 77 }
            Assert.That(workId first, Is.EqualTo(workId changed))
            let _, pa = WorkQueueGitLedger.load a |> success
            let _, pb = WorkQueueGitLedger.load b |> success
            rejectProducerRoute (execute a "Lead" (Guid.NewGuid()) pa.Version 200L (ProduceEvent first))
            rejectProducerRoute (execute b "Lead" (Guid.NewGuid()) pb.Version 201L (ProduceEvent changed))
            let _, final = WorkQueueGitLedger.load a |> success
            Assert.That(final.Works, Is.Empty)
            Assert.That(final.Version, Is.EqualTo(pa.Version)))

    [<Test>]
    member _.LegacyPublicationPathsCannotSmuggleUnverifiedWq04Work() =
        withTwoClones (fun a b project ->
            let event = event project "forged-task" (QueueProducerOrigin.TaskRequested "T") []
            let fixture =
                match projectEvent (Contracts.emptyProject Map.empty) event with
                | Ok(ProducedProjection.Work stage) -> stage
                | other -> failwithf "Cannot construct WQ-04 fixture: %A" other
            let _, pA = WorkQueueGitLedger.load a |> success
            let _, pB = WorkQueueGitLedger.load b |> success
            let rejectLegacy = function
                | Error(LedgerIntegrityError reason)
                    when reason.Contains("WQ-04 stages require canonical producer authority", StringComparison.Ordinal) -> ()
                | other -> Assert.Fail(sprintf "Legacy WQ-04 publication bypassed gate: %A" other)
            rejectLegacy (execute a "Lead" (Guid.NewGuid()) pA.Version 1L (PublishStage fixture))
            rejectLegacy (execute b "Lead" (Guid.NewGuid()) pB.Version 2L (PublishStage fixture))
            let _, fresh = WorkQueueGitLedger.load a |> success
            Assert.That(fresh.Version, Is.EqualTo(pA.Version))
            Assert.That(fresh.Works, Is.Empty))
