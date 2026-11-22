module AutomationRealPortProbe20261122.ProbeTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.NativeHealth
open Client.WorkQueueReactiveWake
open Client.WorkQueueSchedulerHostLoop
open Client.WorkQueueSchedulerHeadBinding
open Client.WorkQueueNativePrivateHostBridge
open Client.NativeSchedulerAccountReservation

[<TestFixture>]
type PortCallability() =
    let ok = function
        | Ok value -> value
        | Error issue -> failwithf "Unexpected canonical Git result: %A" issue

    let git cwd args =
        use child = new Process()
        child.StartInfo.FileName <- "git"
        child.StartInfo.WorkingDirectory <- cwd
        child.StartInfo.RedirectStandardOutput <- true
        child.StartInfo.RedirectStandardError <- true
        for arg in args do child.StartInfo.ArgumentList.Add(arg)
        if not (child.Start()) then failwith "Unable to launch disposable Git command"
        let output = child.StandardOutput.ReadToEnd()
        let errors = child.StandardError.ReadToEnd()
        child.WaitForExit()
        if child.ExitCode <> 0 then failwithf "Disposable Git command failed: %s" errors
        output.Trim()

    let withTwoDisposableGitClones action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-real-port-w23-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let a = Path.Combine(root, "clone-a")
            let b = Path.Combine(root, "clone-b")
            git root ["init"; "--bare"; bare] |> ignore
            git root ["clone"; bare; a] |> ignore
            git a ["config"; "user.name"; "Probe"] |> ignore
            git a ["config"; "user.email"; "probe@example.test"] |> ignore
            File.WriteAllText(Path.Combine(a, "source.txt"), "disposable probe")
            git a ["add"; "source.txt"] |> ignore
            git a ["commit"; "-m"; "disposable baseline"] |> ignore
            git a ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; bare; b] |> ignore
            git b ["config"; "user.name"; "Probe"] |> ignore
            git b ["config"; "user.email"; "probe@example.test"] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> ok |> ignore
            for actor in ["Issuer"; "Owner"; "Intruder"] do
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) actor |> ok |> ignore
            action first second
        finally
            Directory.Delete(root, true)

    [<Test>]
    member _.HealthDefaultsOffAndNeverReadsOrRecovers() =
        let mutable calls = 0
        let dependencies : Dependencies =
            { Enumerate = fun () -> calls <- calls + 1; Error "no roster"
              Current = fun _ -> calls <- calls + 1; Error "no verified Git"
              Activity = fun _ _ -> calls <- calls + 1; Task.FromResult(Error "not authenticated")
              Recover = fun _ _ -> calls <- calls + 1; Error "forbidden" }
        let summary =
            NativeHealth.checkOnce { Enabled = false; InactivitySeconds = 600 } dependencies
            |> fun task -> task.GetAwaiter().GetResult()
        Assert.That(summary.Enabled, Is.False)
        Assert.That(summary.Recoveries, Is.Zero)
        Assert.That(calls, Is.Zero)

    [<Test>]
    member _.HealthMissingCanonicalRosterIsHoldNotRecovered() =
        let mutable recoveries = 0
        let dependencies : Dependencies =
            { Enumerate = fun () -> Error "no approved project binding"
              Current = fun _ -> failwith "unexpected current"
              Activity = fun _ _ -> failwith "unexpected activity"
              Recover = fun _ _ -> recoveries <- recoveries + 1; Error "forbidden" }
        let summary =
            NativeHealth.checkOnce { Enabled = true; InactivitySeconds = 600 } dependencies
            |> fun task -> task.GetAwaiter().GetResult()
        Assert.That(summary.Holds, Is.EqualTo(1))
        Assert.That(summary.Candidates, Is.Zero)
        Assert.That(recoveries, Is.Zero)
        Assert.That(summary.Errors.Head, Does.Contain("Canonical roster unavailable"))

    [<Test>]
    member _.ChallengeRealGitTwoClonesResolveCASAndHistory() =
        withTwoDisposableGitClones (fun first second ->
            let conditions : ChallengeConditionSpec list =
                [{ OwnerKind = ""; OwnerAgentId = "Owner"; SuggestedAgentId = ""
                   Description = "disposable canonical condition" }]
            let created =
                ChallengeWorkflow.create first (Guid.NewGuid()) "Issuer" "Port check" "" conditions
                |> ok
            let before = ChallengeWorkflow.get second "Issuer" created.ChallengeId |> ok
            Assert.That(before.Challenge.Version, Is.EqualTo(1))
            Assert.That(before.Challenge.Status, Is.EqualTo("Pending"))
            let conditionId = before.Challenge.Conditions.Head.ConditionId
            match ChallengeWorkflow.resolve second (Guid.NewGuid()) "Intruder"
                    created.ChallengeId conditionId 1 "fake" "" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Expected ownership denial: %A" other)
            let operationId = Guid.NewGuid()
            let committed =
                ChallengeWorkflow.resolve second operationId "Owner"
                    created.ChallengeId conditionId 1 "evidence:disposable" ""
                |> ok
            Assert.That(committed.AlreadyApplied, Is.False)
            let replay =
                ChallengeWorkflow.resolve first operationId "Owner"
                    created.ChallengeId conditionId 1 "evidence:disposable" ""
                |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            match ChallengeWorkflow.resolve first (Guid.NewGuid()) "Owner"
                    created.ChallengeId conditionId 1 "stale" "" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Expected stale version rejection: %A" other)
            let after = ChallengeWorkflow.get first "Issuer" created.ChallengeId |> ok
            Assert.That(after.Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(after.Challenge.Version, Is.EqualTo(2))
            Assert.That(after.History.Length, Is.EqualTo(2))
            Assert.That(after.Challenge.Conditions.Head.Evidence, Is.EqualTo("evidence:disposable"))
        )

    [<Test>]
    member _.ProductionHeadBindingAndHostRefuseUnapprovedScope() =
        let mutable executions = 0
        let wakePorts : WorkWakePorts =
            { ReadVerified = fun () -> failwith "no unapproved preview"
              Plan = fun _ -> failwith "no plan"
              Execute = fun _ -> executions <- executions + 1; WorkExecutorResult.Applied }
        let coordinator = WorkQueueReactiveWake.Coordinator(wakePorts, enabled = false)
        let unapprovedScope : SchedulerHeadScope =
            { Account = Unchecked.defaultof<_>; Projects = []; Membership = None }
        let observer = Observer(unapprovedScope)
        let ports = WorkQueueSchedulerHostLoop.productionPorts coordinator
                        (fun () -> observer.ReadVerifiedHeads Guid.Empty) ignore
        use defaultOff = new WorkQueueSchedulerHostLoop.Host(ports)
        Assert.That(defaultOff.Start(), Is.False)
        Assert.That(defaultOff.RunOnce [WorkWakeTrigger.DependencyCommitted],
                    Is.EqualTo(SchedulerHostOutcome.Disabled))
        use explicitlyLocal = new WorkQueueSchedulerHostLoop.Host(ports, enabled = true)
        match explicitlyLocal.RunOnce [WorkWakeTrigger.DependencyCommitted] with
        | SchedulerHostOutcome.NeedsAttention("CanonicalHeadsUnavailable", reason) ->
            Assert.That(reason, Does.Contain("HeadObserverDisabled"))
        | other -> Assert.Fail(sprintf "Expected approved-head HOLD: %A" other)
        Assert.That(executions, Is.Zero)

    [<Test>]
    member _.ProductionPrivateHostGateReportsMissingAuthenticatedProviderInterfaces() =
        Assert.That(WorkQueueNativePrivateHostBridge.inspectProduction false,
                    Is.EqualTo(PrivateHostGate.Disabled))
        match WorkQueueNativePrivateHostBridge.inspectProduction true with
        | PrivateHostGate.HostBridgeUnavailable missing ->
            Assert.That(missing.MissingInterfaces.Length, Is.EqualTo(3))
            Assert.That(missing.Code, Is.EqualTo("HostBridgeUnavailable"))
        | other -> Assert.Fail(sprintf "Expected fail-closed host gate: %A" other)
        Assert.That(typeof<NativeDispatchCandidate>.FullName,
                    Does.Contain("NativeDispatchCandidate"))

    [<Test>]
    member _.ServerAuthenticatedActivityMethodIsCompiledButNotAnUnregisteredTestProof() =
        let methodInfo = typeof<Server.Services.HubService>.GetMethod("GetAgentActivity")
        Assert.That(methodInfo, Is.Not.Null)
        let args = methodInfo.GetParameters()
        Assert.That(args.Length, Is.EqualTo(2))
        Assert.That(args[0].ParameterType, Is.EqualTo(typeof<string>))
        Assert.That(args[1].ParameterType, Is.EqualTo(typeof<string>))
