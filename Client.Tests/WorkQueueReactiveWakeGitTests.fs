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
