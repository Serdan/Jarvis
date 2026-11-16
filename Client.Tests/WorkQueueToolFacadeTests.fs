module Client.Tests.WorkQueueToolFacadeTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueToolFacade

[<TestFixture>]
type WorkQueueToolFacadeTests() =
    let success = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected Work ledger outcome: %A" error

    let git path args =
        use command = new Process()
        command.StartInfo.FileName <- "git"
        command.StartInfo.WorkingDirectory <- path
        command.StartInfo.RedirectStandardOutput <- true
        command.StartInfo.RedirectStandardError <- true
        for arg in args do command.StartInfo.ArgumentList.Add arg
        Assert.That(command.Start(), Is.True)
        let output = command.StandardOutput.ReadToEnd()
        let error = command.StandardError.ReadToEnd()
        command.WaitForExit()
        Assert.That(command.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let withClones work =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-work-facade-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; first ] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "inert Work fixture")
            git first [ "add"; "source.txt" ] |> ignore
            git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                        "commit"; "-m"; "fixture" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; remote; second ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> success |> ignore
            LedgerStore.fetch b |> success |> ignore
            work a b
        finally
            try Directory.Delete(root, true) with _ -> ()

    let stage projectId id dependencies =
        { Id = id; ProjectId = projectId; SubjectId = "task-" + id
          Kind = WorkKind.Planning; QueueId = "planning"; StageGeneration = 1
          State = WorkState.Available; Domain = DomainState.Undecided
          Gate = GateState.Clear; ClaimGeneration = 0
          Dependencies = Set.ofList dependencies; WaivedDependencies = Map.empty
          AvailableAt = 1000L; AvailableSequence = 1L
          SourceLineage = None; Push = None; IntegrationAuthorized = false }

    let launch projectId =
        { Id = "L1"; ProjectId = projectId; RoleId = "Lead"; RoleVersion = 1
          AccountTokenId = "account-token-L1"; State = LaunchState.ReservedUnregistered
          Lease = None; Version = 0; Credit = None; PrimaryWorkId = None
          EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }

    let actor = "operator:" + String.replicate 64 "a"
    let nextWork binding =
        WorkQueueToolFacade.readWithBinding binding "List" "Available" "" |> success

    [<Test>]
    member _.PinnedTwoCloneViewsClassifyReadyVersusBlockedWithoutFalseClaimPermit() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Lead", Set.singleton "planning" ]) |> success |> ignore
            let projectId = a.ProjectId.ToString("D")
            WorkQueueGitLedger.executeInertFixtureAt a "Fixture" (Guid.NewGuid()) 0 1000L
                (PublishStage (stage projectId "A" [])) |> success |> ignore
            WorkQueueGitLedger.executeInertFixtureAt b "Fixture" (Guid.NewGuid()) 1 1001L
                (PublishStage (stage projectId "B" ["A"])) |> success |> ignore
            let aView = WorkQueueToolFacade.readWithBinding a "List" "All" "" |> success
            let bView = WorkQueueToolFacade.readWithBinding b "List" "All" "" |> success
            Assert.That(aView.VerifiedGitHead, Is.EqualTo(bView.VerifiedGitHead))
            Assert.That(aView.ProjectVersion, Is.EqualTo(2))
            Assert.That(aView.Coverage, Is.EqualTo("Verified"))
            let ready = aView.Work |> List.find (fun item -> item.WorkId = "A")
            Assert.That(ready.Category, Is.EqualTo("Available"))
            let blocked = bView.Work |> List.find (fun item -> item.WorkId = "B")
            Assert.That(blocked.Category, Is.EqualTo("Blocked"))
            Assert.That(blocked.Dependencies.Length, Is.EqualTo(1))
            Assert.That(blocked.Dependencies.Head.WorkId, Is.EqualTo("A"))
            Assert.That(blocked.Dependencies.Head.StageGeneration, Is.EqualTo(Some 1))
            Assert.That(blocked.Dependencies.Head.Satisfied, Is.False)
            Assert.That(blocked.Dependencies.Head.Reason, Does.Contain("graph v"))
            Assert.That(aView.RoleAuthority, Does.StartWith("UNKNOWN"))
            Assert.That(aView.ReadOnly, Is.True)
            Assert.That(aView.Work |> List.forall (fun item -> not item.ClaimEnabled), Is.True)
            Assert.That((nextWork b).Work |> List.map (fun item -> item.WorkId) = ["A"], Is.True)
            let before = (LedgerStore.fetch a |> success).Commit
            let op = Guid.NewGuid().ToString("D")
            let rejected =
                WorkQueueToolFacade.transitionWithBinding b "ClaimNext" actor op
                    "L1" "" 2 0 "" |> success
            Assert.That(rejected.Applied, Is.False)
            Assert.That(rejected.Status, Is.EqualTo("RejectedMissingBinding"))
            Assert.That(rejected.MissingBinding, Does.Contain("same-CAS"))
            Assert.That(rejected.OperationId, Is.EqualTo(op))
            let replay =
                WorkQueueToolFacade.transitionWithBinding a "ClaimNext" actor op
                    "L1" "" 2 0 "" |> success
            Assert.That(replay.Applied, Is.False)
            Assert.That(replay.OperationId, Is.EqualTo(op))
            match WorkQueueToolFacade.transitionWithBinding b "ClaimNext" actor
                    (Guid.NewGuid().ToString("D")) "L1" "" 1 0 "" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale Work project version must reject: %A" other)
            Assert.That((LedgerStore.fetch b |> success).Commit, Is.EqualTo(before)))

    [<Test>]
    member _.TwoCloneFrozenWq02ClaimCASShowsSingleWinnerAndOriginalReplayOnWorkView() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Lead", Set.singleton "planning" ]) |> success |> ignore
            let projectId = a.ProjectId.ToString("D")
            let write client version now mutation =
                WorkQueueGitLedger.executeInertFixtureAt client "Fixture"
                    (Guid.NewGuid()) version now mutation |> success |> ignore
            write a 0 1000L (PublishStage (stage projectId "P" []))
            write b 1 1001L (AdmitProject (launch projectId))
            write a 2 1002L (DispatchOnce ("L1", DispatchState.Submitted))
            write b 3 1003L (InitializeAgent ("L1", "ephemeral-session"))
            let original = Guid.NewGuid()
            let winning =
                WorkQueueGitLedger.executeInertFixtureAt a "Fixture" original 4 1004L
                    (ClaimNext "L1") |> success
            match WorkQueueGitLedger.executeInertFixtureAt b "Fixture" (Guid.NewGuid()) 4 1004L
                    (ClaimNext "L1") with
            | Error (StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Concurrent second claimant must lose CAS: %A" other)
            let replay =
                WorkQueueGitLedger.executeInertFixtureAt b "Fixture" original 4 1004L
                    (ClaimNext "L1") |> success
            Assert.That(replay.Operation.OriginalResultHash, Is.EqualTo(winning.Operation.OriginalResultHash))
            Assert.That(replay.AlreadyApplied, Is.True)
            let active = WorkQueueToolFacade.readWithBinding b "List" "Active" "" |> success
            Assert.That(active.Work.Length, Is.EqualTo(1))
            let row = active.Work.Head
            Assert.That(row.WorkId, Is.EqualTo("P"))
            Assert.That(row.ClaimLaunchId, Is.EqualTo(Some "L1"))
            Assert.That(row.SessionUuid.IsSome, Is.True)
            Assert.That(row.Liveness, Does.Contain("UNKNOWN"))
            Assert.That(row.ClaimEnabled, Is.False)
            Assert.That(row.Domain, Is.EqualTo("Undecided")))

    [<Test>]
    member _.AvailableWorkPreservesCanonicalBlockingFirstPriorityAcrossFifo() =
        let id = "P"
        let upstream =
            { stage id "upstream" [] with AvailableAt = 9000L; AvailableSequence = 3L }
        let later =
            { stage id "later" [] with AvailableAt = 100L; AvailableSequence = 1L }
        let dependent =
            { stage id "dependent" ["upstream"] with
                State = WorkState.Blocked; AvailableAt = 150L; AvailableSequence = 2L }
        let project =
            { Contracts.emptyProject (Map.ofList ["Lead", Set.singleton "planning"]) with
                Works = Map.ofList ["upstream", upstream; "later", later; "dependent", dependent] }
        let rows = (WorkQueueToolFacade.inspectAt id "head" project (Error "no roles")).Work
        let available = rows |> List.filter (fun item -> item.Category = "Available")
        Assert.That(available |> List.map (fun row -> row.WorkId) = ["upstream"; "later"], Is.True)
        let held = rows |> List.find (fun item -> item.WorkId = "dependent")
        Assert.That(held.Category, Is.EqualTo("Blocked"))

    [<Test>]
    member _.IncompleteCoverageIsUnknownRatherThanAvailableAndMissingDependenciesAreVersioned() =
        let id = "P"
        let ready = stage id "A" ["missing"]
        let state =
            { Contracts.emptyProject Map.empty with
                QueueCovered = false; GraphVersion = 7
                Works = Map.ofList ["A", ready] }
        let observed = WorkQueueToolFacade.inspectAt id "head" state (Error "no pinned roles")
        Assert.That(observed.Coverage, Does.StartWith("UNKNOWN"))
        Assert.That(observed.Work.Head.Category, Is.EqualTo("Unknown"))
        Assert.That(observed.Work.Head.Dependencies.Head.Reason, Does.Contain("graph v7"))
        Assert.That(observed.Work.Head.ClaimEnabled, Is.False)

    [<Test>]
    member _.UnboundCheckpointAndCompleteRequireOriginalUuidAndEvidenceButDoNotMutate() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid()) Map.empty
            |> success |> ignore
            let before = (LedgerStore.fetch a |> success).Commit
            let sha = String.replicate 64 "b"
            let op = Guid.NewGuid().ToString("D")
            for action in ["Checkpoint"; "Complete"] do
                let refused =
                    WorkQueueToolFacade.transitionWithBinding b action actor op
                        "L1" "W1" 0 1 sha |> success
                Assert.That(refused.Status, Is.EqualTo("RejectedMissingBinding"))
                Assert.That(refused.MissingBinding, Does.Contain("journal"))
            Assert.That((LedgerStore.fetch a |> success).Commit, Is.EqualTo(before))
            match WorkQueueToolFacade.transitionWithBinding b "Complete" actor "not-a-uuid"
                      "L1" "W1" 0 1 sha with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Invalid operation UUID must fail: %A" other))
