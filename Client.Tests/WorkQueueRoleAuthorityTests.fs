module Client.Tests.WorkQueueRoleAuthorityTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority

[<TestFixture>]
type WorkQueueRoleAuthorityTests() =
    let success = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected role-authority failure: %A" error

    let git directory arguments =
        use command = new Process()
        command.StartInfo.FileName <- "git"
        command.StartInfo.WorkingDirectory <- directory
        command.StartInfo.RedirectStandardOutput <- true
        command.StartInfo.RedirectStandardError <- true
        for argument in arguments do command.StartInfo.ArgumentList.Add(argument)
        Assert.That(command.Start(), Is.True)
        let output = command.StandardOutput.ReadToEnd()
        let errors = command.StandardError.ReadToEnd()
        command.WaitForExit()
        Assert.That(command.ExitCode, Is.EqualTo(0), errors)
        output.Trim()

    let withTwoClones action =
        let root = Path.Combine(Path.GetTempPath(), "wq04-role-authority-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "one")
            let second = Path.Combine(root, "two")
            git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
            git root ["clone"; bare; first] |> ignore
            git first ["config"; "user.name"; "Role fixture"] |> ignore
            git first ["config"; "user.email"; "role-fixture@example.test"] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "local test only")
            git first ["add"; "source.txt"] |> ignore
            git first ["commit"; "-m"; "fixture"] |> ignore
            git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; bare; second] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding directory =
                { RepositoryPath = directory; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> success |> ignore
            LedgerStore.fetch b |> success |> ignore
            // The legacy map is deliberately broader than any versioned role.
            // Its presence MUST NOT authorize new versioned role subscriptions.
            WorkQueueGitLedger.initialize a "fixture" (Guid.NewGuid())
                (Map.ofList ["Lead", set ["planning"; "review"; "role:Lead"]])
            |> success |> ignore
            action a b
            Assert.That(git first ["status"; "--porcelain"], Is.Empty)
            Assert.That(git second ["status"; "--porcelain"], Is.Empty)
        finally
            try Directory.Delete(root, true) with _ -> ()

    let role version enabled queues : RoleConfiguration =
        { RoleId = "Lead"; Version = version; Instructions = "Lead version " + string version
          Enabled = enabled; QueueSubscriptions = set queues }

    let init (binding: LedgerBinding) =
        WorkQueueRoleAuthority.initializeCatalog binding "fixture" (Guid.NewGuid()) |> success |> ignore

    let config (binding: LedgerBinding) expected previous definition =
        WorkQueueRoleAuthority.configure binding "fixture" (Guid.NewGuid()) expected previous definition

    let stage project id queue kind subject =
        { Id = id; ProjectId = project; SubjectId = subject; QueueId = queue
          Kind = kind; StageGeneration = 1; State = WorkState.Available
          Domain = DomainState.Undecided; Gate = GateState.Clear
          ClaimGeneration = 0; Dependencies = Set.empty; WaivedDependencies = Map.empty
          AvailableAt = 0L; AvailableSequence = 0L
          SourceLineage = None; Push = None; IntegrationAuthorized = false }

    let launch project id version =
        { Id = id; ProjectId = project; RoleId = "Lead"; RoleVersion = version
          AccountTokenId = "local-account-token-" + id
          State = LaunchState.ReservedUnregistered
          Lease = None; Version = 0; Credit = None; PrimaryWorkId = None
          EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }

    let mutate (binding: LedgerBinding) mutation =
        let _, before = WorkQueueGitLedger.load binding |> success
        WorkQueueGitLedger.executeInertFixture binding "fixture" (Guid.NewGuid()) before.Version mutation
        |> success |> ignore

    let reject (predicate: LedgerFailure -> bool) (result: Result<'a, LedgerFailure>) =
        match result with
        | Ok _ -> Assert.Fail("Expected WQ04 fail-closed rejection.")
        | Error failure ->
            Assert.That((predicate failure), Is.True, sprintf "Unexpected rejection: %A" failure)

    let isStale = function
        | StaleEntityVersion _ -> true
        | _ -> false

    let isIntegrity = function
        | LedgerIntegrityError _ -> true
        | _ -> false

    [<Test>]
    member _.CatalogIsSeparatelyProvisionedAndNeverImportedFromWq02Map() =
        withTwoClones (fun a b ->
            reject (function InvalidBinding _ -> true | _ -> false) (WorkQueueRoleAuthority.load a)
            init a
            let initial = WorkQueueRoleAuthority.load b |> success
            Assert.That(initial.Catalog.Revision, Is.EqualTo(0))
            Assert.That(initial.Catalog.Current.IsEmpty, Is.True)
            reject (function InvalidBinding _ -> true | _ -> false)
                (WorkQueueRoleAuthority.inspect initial (PlanNewDemand "Lead"))
            config a 0 0 (role 1 true ["planning"]) |> success |> ignore
            let first = WorkQueueRoleAuthority.load b |> success
            Assert.That(first.Catalog.Current.["Lead"], Is.EqualTo(1))
            Assert.That(first.Proof.CatalogRevision, Is.EqualTo(1))
            Assert.That(first.Catalog.Versions.["Lead"].[1].QueueSubscriptions = set ["planning"], Is.True)
            Assert.That(first.Catalog.Versions.["Lead"].[1].QueueSubscriptions.Contains("review"), Is.False)
            Assert.That(first.Proof.Head, Is.Not.Empty)
            Assert.That(first.Proof.ProjectBlobOid, Is.Not.Empty)
            Assert.That(first.Proof.CatalogBlobOid, Is.Not.Empty)
            Assert.That(first.Proof.LedgerId, Is.EqualTo(a.LedgerId.ToString("D")))
            let provisional = WorkQueueRoleAuthority.inspect first ReconcileCredits |> success
            Assert.That(provisional.RequiresCanonicalSourceAndAccountProof, Is.True))

    [<Test>]
    member _.ConcurrentRevisionChangesAndOriginalUuidReplayAreSingleWinner() =
        withTwoClones (fun a b ->
            init a
            config a 0 0 (role 1 true ["planning"]) |> success |> ignore
            let pinned = WorkQueueRoleAuthority.load a |> success
            let v2a = role 2 true ["review"]
            let v2b = role 2 false ["role:Lead"]
            let opA, opB = Guid.NewGuid(), Guid.NewGuid()
            let f1 = Task.Run(fun () ->
                WorkQueueRoleAuthority.configure a "fixture" opA 1 1 v2a)
            let f2 = Task.Run(fun () ->
                WorkQueueRoleAuthority.configure b "fixture" opB 1 1 v2b)
            Task.WaitAll([| f1 :> Task; f2 :> Task |])
            let results = [f1.Result; f2.Result]
            Assert.That(results |> List.filter Result.isOk |> List.length, Is.EqualTo(1),
                sprintf "Two clones cannot both publish revision 2: %A" results)
            Assert.That(results |> List.filter (function Error(StaleEntityVersion _) -> true | _ -> false)
                       |> List.length, Is.EqualTo(1))
            let winnerBinding, winnerOp, winnerDefinition =
                if Result.isOk f1.Result then a, opA, v2a else b, opB, v2b
            let applied =
                WorkQueueRoleAuthority.configure winnerBinding "fixture" winnerOp 1 1 winnerDefinition
                |> success
            Assert.That(applied.AlreadyApplied, Is.True, "Original UUID must replay original commit receipt.")
            reject (function OperationIdReused -> true | _ -> false)
                (WorkQueueRoleAuthority.configure winnerBinding "fixture" winnerOp 1 1
                    { winnerDefinition with Instructions = "different same UUID" })
            reject isStale (WorkQueueRoleAuthority.verifyFresh a pinned.Proof)
            let latest = WorkQueueRoleAuthority.load b |> success
            Assert.That(latest.Catalog.Revision, Is.EqualTo(2))
            Assert.That(latest.Catalog.Versions.["Lead"].[1].QueueSubscriptions = set ["planning"], Is.True)
            Assert.That(latest.Catalog.Versions.["Lead"].[2], Is.EqualTo(winnerDefinition))
            reject isStale (config b 1 1 (role 2 true ["review"])))

    [<Test>]
    member _.PinnedV1ClaimsAndFirstConnectSurviveV2SubscriptionsButNewDemandUsesV2() =
        withTwoClones (fun a b ->
            init a
            config a 0 0 (role 1 true ["planning"; "role:Lead"]) |> success |> ignore
            let id = a.ProjectId.ToString("D")
            mutate a (PublishStage(stage id "planning-a" "planning" WorkKind.Planning "task-a"))
            mutate a (PublishStage(stage id "review-b" "review" WorkKind.FrozenReview "task-b"))
            let oldLaunch = launch id "L-old" 1
            WorkQueueRoleAuthority.inspect (WorkQueueRoleAuthority.load a |> success)
                (AdmitNew oldLaunch) |> success |> ignore
            mutate a (AdmitProject oldLaunch)
            mutate a (DispatchOnce("L-old", DispatchState.Submitted))
            let before = WorkQueueRoleAuthority.load a |> success
            WorkQueueRoleAuthority.inspect before (FirstConnect "L-old") |> success |> ignore
            config b 1 1 (role 2 true ["review"]) |> success |> ignore
            let changed = WorkQueueRoleAuthority.load a |> success
            Assert.That(changed.Catalog.Versions.["Lead"].[1].QueueSubscriptions = set ["planning"; "role:Lead"], Is.True)
            Assert.That(changed.Catalog.Versions.["Lead"].[2].QueueSubscriptions = set ["review"], Is.True)
            reject (function InvalidBinding _ -> true | _ -> false)
                (WorkQueueRoleAuthority.inspect changed (ClaimWork("L-old", "planning-a")))
            WorkQueueRoleAuthority.inspect changed (FirstConnect "L-old") |> success |> ignore
            mutate a (InitializeAgent("L-old", "session-v1"))
            let registered = WorkQueueRoleAuthority.load b |> success
            WorkQueueRoleAuthority.inspect registered (ClaimWork("L-old", "planning-a"))
            |> success |> ignore
            reject (function InvalidBinding _ -> true | _ -> false)
                (WorkQueueRoleAuthority.inspect registered (ClaimWork("L-old", "review-b")))
            WorkQueueRoleAuthority.inspect registered (PlanNewDemand "Lead") |> success |> ignore
            reject isStale (WorkQueueRoleAuthority.inspect registered (AdmitNew (launch id "new-stale" 1)))
            WorkQueueRoleAuthority.inspect registered (AdmitNew (launch id "new-v2" 2))
            |> success |> ignore
            config a 2 2 (role 3 false ["review"]) |> success |> ignore
            let disabled = WorkQueueRoleAuthority.load b |> success
            reject (function InvalidBinding _ -> true | _ -> false)
                (WorkQueueRoleAuthority.inspect disabled (PlanNewDemand "Lead"))
            reject (function InvalidBinding _ -> true | _ -> false)
                (WorkQueueRoleAuthority.inspect disabled (AdmitNew (launch id "new-disabled" 3)))
            WorkQueueRoleAuthority.inspect disabled (ClaimWork("L-old", "planning-a"))
            |> success |> ignore)

    [<Test>]
    member _.RoleMessageRequiresCurrentExplicitSubscriptionAndPendingIndexedWork() =
        withTwoClones (fun a b ->
            init a
            config a 0 0 (role 1 true ["role:Lead"]) |> success |> ignore
            let id = a.ProjectId.ToString("D")
            let msg =
                { Id = "M1"; Subject = Subject.Role "Lead"; Body = "local only"
                  State = MessageState.Pending; HandlerGeneration = 0; WorkId = None }
            let roleWork = stage id "role-work" "role:Lead" (WorkKind.RoleMessage "M1") "Lead"
            mutate a (PostMessage(msg, Some roleWork))
            let v1 = WorkQueueRoleAuthority.load b |> success
            WorkQueueRoleAuthority.inspect v1 (RoleMessageDemand("Lead", "M1"))
            |> success |> ignore
            reject (function InvalidBinding _ -> true | _ -> false)
                (WorkQueueRoleAuthority.inspect v1 (RoleMessageDemand("Lead", "made-up")))
            config b 1 1 (role 2 true ["review"]) |> success |> ignore
            let v2 = WorkQueueRoleAuthority.load a |> success
            reject (function InvalidBinding _ -> true | _ -> false)
                (WorkQueueRoleAuthority.inspect v2 (RoleMessageDemand("Lead", "M1"))))

    [<Test>]
    member _.PinnedAdmissionLosesToAnotherClonesRoleCatalogCas() =
        withTwoClones (fun a b ->
            init a
            config a 0 0 (role 1 true ["planning"]) |> success |> ignore
            let id = a.ProjectId.ToString("D")
            mutate a (PublishStage(stage id "P1" "planning" WorkKind.Planning "task-P1"))
            let pinned = WorkQueueRoleAuthority.load a |> success
            let pending = launch id "late-L" 1
            WorkQueueRoleAuthority.inspect pinned (AdmitNew pending) |> success |> ignore
            config b 1 1 (role 2 true ["review"]) |> success |> ignore
            let transaction =
                { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "WQ04RoleGuardFixture"
                  RequestJson = "{\"schemaVersion\":1,\"kind\":\"prove-cas\"}" }
            let attempted =
                LedgerStore.transact a transaction (fun fresh ->
                    WorkQueueRoleAuthority.inspectAt a fresh pinned.Proof (AdmitNew pending)
                    |> Result.map (fun _ ->
                        [{ Path = "records/work-queue/wq04-test-guard.json"
                           ExpectedBlobOid = None; Content = "{\"guard\":\"local-only\"}" }]))
            reject isStale attempted
            let _, written =
                let snapshot = LedgerStore.fetch a |> success
                LedgerStore.readAt a snapshot "records/work-queue/wq04-test-guard.json" |> success
            Assert.That(written.IsNone, Is.True)
            reject isStale (WorkQueueRoleAuthority.inspectFresh b pinned.Proof (AdmitNew pending))
            let latest = WorkQueueRoleAuthority.load a |> success
            reject isStale (WorkQueueRoleAuthority.inspect latest (AdmitNew pending))
            Assert.That(latest.Catalog.Revision, Is.EqualTo(2)))

    [<Test>]
    member _.MissingHistoricalVersionAndForgedProjectLedgerProofFailClosed() =
        withTwoClones (fun a b ->
            init a
            config a 0 0 (role 1 true ["planning"]) |> success |> ignore
            let original = WorkQueueRoleAuthority.load a |> success
            let forged = { original.Proof with CatalogRevision = original.Proof.CatalogRevision + 1 }
            reject isStale (WorkQueueRoleAuthority.verifyFresh b forged)
            let forgedLedger = { original.Proof with LedgerId = Guid.NewGuid().ToString("D") }
            reject isStale (WorkQueueRoleAuthority.verifyFresh a forgedLedger)
            let snapshot = LedgerStore.fetch a |> success
            let raw, blob =
                LedgerStore.readAt a snapshot WorkQueueRoleAuthority.CatalogPath |> success
            let json = JsonNode.Parse(raw.Value).AsObject()
            json.["Versions"].AsArray().RemoveAt(0)
            let bad = json.ToJsonString()
            let op = Guid.NewGuid()
            let intent =
                { OperationId = op; Actor = "fixture"; Kind = "WQ04CorruptHistoryFixture"
                  RequestJson = "{\"schemaVersion\":1,\"corrupt\":\"history\"}" }
            LedgerStore.transact b intent (fun _ ->
                Ok [{ Path = WorkQueueRoleAuthority.CatalogPath
                      ExpectedBlobOid = blob; Content = bad }])
            |> success |> ignore
            reject isIntegrity (WorkQueueRoleAuthority.load a)
            reject isStale (WorkQueueRoleAuthority.inspectFresh b original.Proof ReconcileCredits))
