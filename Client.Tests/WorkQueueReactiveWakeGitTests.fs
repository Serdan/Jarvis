module Client.Tests.WorkQueueReactiveWakeGitTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Client.ReviewWorkflow
open Client.WorkQueueReceiptProducer
open Client.WorkQueuePracticalAdmission
open Client.WorkQueueSchedulerHostPreview
open Client.WorkQueueReactiveWake

[<TestFixture>]
type WorkQueueReactiveWakeGitTests() =
    let git directory arguments =
        let start = ProcessStartInfo("git")
        start.WorkingDirectory <- directory
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        for arg in arguments do start.ArgumentList.Add arg
        use proc = Process.Start start
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        if not (proc.WaitForExit(20000)) then
            proc.Kill(true)
            failwithf "Disposable Git operation timed out: %A" arguments
        Assert.That(proc.ExitCode, Is.Zero, "git " + String.concat " " arguments + ": " + error)
        output.Trim()

    let succeed = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected verified Git error: %A" error

    [<Test>]
    member _.BoundReaderUsesFreshApprovedGitHeadsAndNeverEnablesDispatch() =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-reactive-wake-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "client-one")
            let two = Path.Combine(root, "client-two")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; one] |> ignore
            git one ["config"; "user.name"; "Wake fixture"] |> ignore
            git one ["config"; "user.email"; "wake@example.invalid"] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "fixture source")
            git one ["add"; "source.txt"] |> ignore
            git one ["commit"; "-m"; "seed"] |> ignore
            git one ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; two] |> ignore

            let projectId = Guid.NewGuid()
            let projectLedgerId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let accountLedgerId = Guid.NewGuid()
            let project path: LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = projectLedgerId; Epoch = 1 }
            let account path: AccountBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }

            AccountCapacityLedger.initialize (account one) 1 |> succeed |> ignore
            LedgerStore.initialize (project one) true |> succeed |> ignore
            WorkQueueGitLedger.initialize (project one) "fixture" (Guid.NewGuid())
                (Map.ofList ["Worker", set ["implementation"]])
            |> succeed |> ignore
            WorkQueueRoleAuthority.initializeCatalog (project one) "fixture" (Guid.NewGuid())
            |> succeed |> ignore
            let role: RoleConfiguration =
                { RoleId = "Worker"; Version = 1; Enabled = true
                  QueueSubscriptions = set ["implementation"]; Instructions = "fixture" }
            WorkQueueRoleAuthority.configure (project one) "fixture" (Guid.NewGuid()) 0 0 role
            |> succeed |> ignore

            let read = WorkQueueReactiveWake.boundRead (account two) (project two) "Worker"
            let beforeAccount = AccountCapacityLedger.fetch (account two) |> succeed
            let beforeProject = LedgerStore.fetch (project two) |> succeed
            let verified = read ()
            Assert.That(verified.AccountHead, Is.EqualTo(Some beforeAccount.Head))
            Assert.That(verified.ProjectHead, Is.EqualTo(Some beforeProject.Commit))
            Assert.That(verified.Mode, Is.EqualTo("ReadOnlyDryRun"))
            Assert.That(verified.CanDispatch, Is.False)
            Assert.That(verified.WouldLaunch, Is.EqualTo(None))

            let mutable planCalls, executorCalls = 0, 0
            let ports: WorkWakePorts =
                { ReadVerified = read
                  Plan = fun _ -> planCalls <- planCalls + 1; Ok None
                  Execute = fun _ -> executorCalls <- executorCalls + 1; WorkExecutorResult.Applied }
            let coordinator = WorkQueueReactiveWake.Coordinator(ports, enabled = true)
            match coordinator.Wake [WorkWakeTrigger.ManualReconcile] with
            | WorkWakeResult.NeedsAttention("AccountNotActivated", _) -> ()
            | other -> Assert.Fail(sprintf "An unactivated account cannot dispatch: %A" other)
            Assert.That(planCalls, Is.Zero)
            Assert.That(executorCalls, Is.Zero)
            Assert.That(git one ["status"; "--porcelain"], Is.Empty)
            Assert.That(git two ["status"; "--porcelain"], Is.Empty)
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Test>]
    member _.VerifiedTerminalAndReleasedHistoryAllowsSuccessorWakeWithoutBrowserJournal() =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-reactive-successor-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "client-one")
            let two = Path.Combine(root, "client-two")
            git root ["init"; "--bare"; "--initial-branch=main"; remote] |> ignore
            git root ["clone"; remote; one] |> ignore
            git one ["config"; "user.name"; "Wake fixture"] |> ignore
            git one ["config"; "user.email"; "wake@example.invalid"] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "isolated successor proof")
            git one ["add"; "source.txt"] |> ignore
            git one ["commit"; "-m"; "source"] |> ignore
            git one ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; two] |> ignore

            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let project path: LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let account path: AccountBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }

            AccountCapacityLedger.initialize (account one) 1 |> succeed |> ignore
            LedgerStore.initialize (project one) true |> succeed |> ignore
            WorkQueueGitLedger.initialize (project one) "Lead" (Guid.NewGuid())
                (Map.ofList ["Lead", set ["planning"]]) |> succeed |> ignore
            ReviewWorkflow.registerAgent (project one) (Guid.NewGuid()) "Lead" |> succeed |> ignore
            WorkQueueRoleAuthority.initializeCatalog (project one) "Lead" (Guid.NewGuid())
            |> succeed |> ignore
            let role: RoleConfiguration =
                { RoleId = "Lead"; Version = 1; Enabled = true
                  QueueSubscriptions = set ["planning"]; Instructions = "disposable fixture" }
            WorkQueueRoleAuthority.configure (project one) "Lead" (Guid.NewGuid()) 0 0 role
            |> succeed |> ignore

            let produce taskId =
                let original = Guid.NewGuid()
                ReviewWorkflow.createTask (project one) original taskId ("Fixture " + taskId)
                |> succeed |> ignore
                WorkQueueReceiptProducer.produce (project two) "Lead" (Guid.NewGuid()) original
                |> succeed |> ignore
                let snapshot, state = WorkQueueGitLedger.load (project one) |> succeed
                let stage =
                    state.Works
                    |> Map.toSeq |> Seq.map snd
                    |> Seq.find (fun stage -> stage.SubjectId = taskId)
                let _, oid =
                    WorkQueueGitLedger.readOriginAt (project one) snapshot stage.Id
                    |> succeed |> Option.get
                stage.Id, oid

            let request (): AccountAdmissionRequest =
                { Actor = "Reactive Git fixture"; LaunchId = Guid.NewGuid()
                  AdmissionOperationId = Guid.NewGuid(); AbortOperationId = Guid.NewGuid()
                  ReleaseOperationId = Guid.NewGuid(); RoleId = "Lead"; RoleVersion = 1 }

            let mutate change =
                let _, state = WorkQueueGitLedger.load (project two) |> succeed
                WorkQueueGitLedger.executeInertFixture (project one) "Reactive Git fixture"
                    (Guid.NewGuid()) state.Version change |> succeed |> ignore

            let completedWork, completedOrigin = produce "historical"
            let first = request ()
            let priorDemand: PracticalAdmissionDemand =
                { WorkId = completedWork; OriginBlobOid = completedOrigin }
            match WorkQueuePracticalAdmission.admitExplicit true
                    (account one) (project one) first priorDemand with
            | ProjectAdmitted _ -> ()
            | other -> Assert.Fail(sprintf "Verified WQ04 admission failed: %A" other)
            let pastId = first.LaunchId.ToString("D")
            let held = WorkQueueReactiveWake.boundRead (account two) (project two) "Lead" ()
            Assert.That(held.ExistingLaunches |> List.exists (fun launch ->
                launch.LaunchId = pastId && launch.AccountTokenStatus = "Held"), Is.True)
            Assert.That(held.Issues |> List.exists (fun issue ->
                issue.Code = "BrowserJournalUnknown" && issue.Record = pastId), Is.True)

            mutate (DispatchOnce(pastId, DispatchState.Submitted))
            mutate (InitializeAgent(pastId, "disposable-fake-provider"))
            mutate (ClaimNext pastId)
            let terminalOperation = Guid.NewGuid()
            let _, beforeTerminal = WorkQueueGitLedger.load (project one) |> succeed
            WorkQueueGitLedger.executeInertFixture (project two) "Reactive Git fixture"
                terminalOperation beforeTerminal.Version
                (CompleteOrdinary(pastId, completedWork)) |> succeed |> ignore
            let terminal: ProjectTerminalLocator =
                { ProjectId = projectId; LaunchId = first.LaunchId
                  AdmissionOperationId = first.AdmissionOperationId
                  TerminalOperationId = terminalOperation }
            AccountAdmissionAdapter.releaseTerminal (account two) (project two)
                "Reactive Git fixture" first.ReleaseOperationId terminal |> succeed |> ignore
            Assert.That(AccountCapacityLedger.occupied
                (AccountCapacityLedger.fetch (account one) |> succeed).State, Is.Zero)

            let successorWork, _ = produce "successor"
            // The actual account remains LegacyActive by design. Only the
            // read-only test port models the future approved Activated phase,
            // just as WQ11's fake-only planner acceptance tests do.
            let genuineRead = WorkQueueReactiveWake.boundRead (account two) (project two) "Lead"
            let realReport = genuineRead ()
            Assert.That(realReport.AccountMigration, Is.EqualTo(Some "LegacyActive"))
            let past = realReport.ExistingLaunches |> List.find (fun x -> x.LaunchId = pastId)
            Assert.That(past.State, Is.EqualTo("TerminalPendingRelease"))
            Assert.That(past.AccountTokenStatus, Is.EqualTo("Released"))
            Assert.That(realReport.Issues |> List.exists (fun issue ->
                issue.Code = "BrowserJournalUnknown" && issue.Record = pastId), Is.True)
            Assert.That(realReport.WouldLaunch |> Option.map _.WorkId, Is.EqualTo(Some successorWork))

            // A TaskCreated source receipt has no branch commit. Keep the
            // historical launch, released token, and Git heads authoritative,
            // but provide an immutable fixture SHA for the unrelated source
            // gate so this test can isolate reactive wake behavior.
            let fixtureSourceCommit = git one ["rev-parse"; "HEAD"]
            let realPorts = WorkQueueSchedulerHostPreview.realReadPorts (account two) (project two)
            let activatedRead () =
                let testPorts =
                    { realPorts with
                        ReadAccount = fun () ->
                            AccountCapacityLedger.fetch (account two)
                            |> Result.map (fun snapshot ->
                                { snapshot with
                                    State = { snapshot.State with MigrationPhase = "Activated" } })
                            |> Result.mapError (sprintf "%A")
                        ReadOrigin = fun snapshot workId ->
                            realPorts.ReadOrigin snapshot workId
                            |> Result.map (Option.map (fun (origin, oid) ->
                                if workId = successorWork then
                                    { origin with SourceCommit = fixtureSourceCommit }, oid
                                else origin, oid)) }
                WorkQueueSchedulerHostPreview.previewWith testPorts "Lead"
            let stableLaunch, stableBrowser = Guid.NewGuid(), Guid.NewGuid()
            let mutable callbacks = 0
            let ports: WorkWakePorts =
                { ReadVerified = activatedRead
                  Plan = fun report ->
                      match report.WouldLaunch with
                      | None -> Ok None
                      | Some candidate ->
                          Ok(Some
                              { WorkId = candidate.WorkId
                                WorkGeneration = candidate.WorkGeneration
                                RoleId = candidate.RoleId; RoleVersion = candidate.RoleVersion
                                ProjectHead = report.ProjectHead.Value
                                AccountHead = report.AccountHead.Value
                                SourceOperationId = candidate.SourceOperationId.Value
                                SourceRequestHash = candidate.SourceRequestHash.Value
                                LaunchId = stableLaunch.ToString("D")
                                OriginalBrowserOperationId = stableBrowser.ToString("D") })
                  Execute = fun _ -> callbacks <- callbacks + 1; WorkExecutorResult.Applied }
            let coordinator = WorkQueueReactiveWake.Coordinator(ports, enabled = true)
            match coordinator.Wake [WorkWakeTrigger.CapacityReleased] with
            | WorkWakeResult.Reconciled intent ->
                Assert.That(intent.WorkId, Is.EqualTo(successorWork))
            | other -> Assert.Fail(sprintf "Verified settled history blocked successor: %A" other)
            Assert.That(callbacks, Is.EqualTo(1))
            match coordinator.Wake [WorkWakeTrigger.ManualReconcile] with
            | WorkWakeResult.Coalesced _ -> ()
            | other -> Assert.Fail(sprintf "Repeated successor wake was not coalesced: %A" other)
            Assert.That(callbacks, Is.EqualTo(1))
            Assert.That(git one ["status"; "--porcelain"], Is.Empty)
            Assert.That(git two ["status"; "--porcelain"], Is.Empty)
        finally
            try Directory.Delete(root, true) with _ -> ()
