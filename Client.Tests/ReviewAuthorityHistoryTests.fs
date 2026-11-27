module Client.Tests.ReviewAuthorityHistoryTests

open System
open System.Diagnostics
open System.IO
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open NUnit.Framework

[<TestFixture>]
type ReviewAuthorityHistoryTests() =
    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        if not (proc.Start()) then failwith "Cannot launch Git fixture."
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then failwithf "Git fixture error: %s" stderr
        stdout.Trim()

    let success = function
        | Ok value -> value
        | Error failure -> failwithf "Unexpected ledger result: %A" failure

    let expectStale = function
        | Error(StaleEntityVersion _) | Error(ReviewVersionConflict _) -> ()
        | other -> Assert.Fail(sprintf "Expected stale/version conflict: %A" other)

    let withFixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-review-ledger-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "owner")
            let two = Path.Combine(root, "reviewer")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; one ] |> ignore
            for clone in [ one ] do
                git clone [ "config"; "user.name"; " Fixture" ] |> ignore
                git clone [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "base")
            git one [ "add"; "source.txt" ] |> ignore
            git one [ "commit"; "-m"; "task base" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; two ] |> ignore
            git two [ "config"; "user.name"; " Fixture" ] |> ignore
            git two [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let first = binding one
            let second = binding two
            LedgerStore.initialize first true |> success |> ignore
            for id in [ "Owner"; "Reviewer" ] do
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) id |> success |> ignore
            let baseOid = git one [ "rev-parse"; "HEAD" ]
            let sourceRef = "refs/heads/" + git one [ "branch"; "--show-current" ]
            let newCommit (title: string) (content: string) =
                File.WriteAllText(Path.Combine(one, "source.txt"), content)
                git one [ "add"; "source.txt" ] |> ignore
                git one [ "commit"; "-m"; title ] |> ignore
                git one [ "rev-parse"; "HEAD" ]
            let initTask taskId =
                ReviewWorkflow.createTask first (Guid.NewGuid()) taskId taskId |> success |> ignore
                let attemptId = Guid.NewGuid()
                ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner"
                |> success |> ignore
                ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
                ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Owner" 2 1 baseOid sourceRef
                |> success |> ignore
            action one two first second baseOid sourceRef newCommit initTask
        finally
            for checkout in [ "owner"; "reviewer" ] do
                let pendingConfig =
                    Client.OrchestrationApi.LedgerBindingConfig.pathForRoot(
                        Path.Combine(root, checkout))
                let pendingDir =
                    Path.Combine(Path.GetDirectoryName pendingConfig, "pending",
                                 Path.GetFileNameWithoutExtension pendingConfig)
                if Directory.Exists pendingDir then Directory.Delete(pendingDir, true)
            Directory.Delete(root, true)


    [<TestCase("Working")>]
    [<TestCase("TasklessYield")>]
    [<TestCase("Stopped")>]
    [<TestCase("NeedsWork")>]
    member _.HistoricalTasklessStartDoesNotDisqualifyReviewRoute(finalState: string) =
        withFixture (fun one _ first second baseOid sourceRef _ _ ->
            let taskId = "review-authority-history"
            let id = Guid.NewGuid()
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Review history"
            |> success |> ignore
            ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId "Owner"
                id sourceRef baseOid "Review source" "Independent audit" ""
            |> success |> ignore
            let agent () = (ReviewWorkflow.getAgentInbox second "Reviewer" |> success).Agent
            ReviewWorkflow.setAgentAdministrativeStateAs first "AuthenticatedUser" (Guid.NewGuid())
                "Reviewer" "NeedsWork" (agent ()).Version "Enable taskless activity"
            |> success |> ignore
            let startId = Guid.NewGuid()
            ReviewWorkflow.startTasklessWork first startId "Reviewer" (agent ()).Version "Taskless start"
            |> success |> ignore
            if finalState <> "Working" then
                ReviewWorkflow.yieldTasklessWork first (Guid.NewGuid()) "Reviewer" (agent ()).Version "Yield"
                |> success |> ignore
            if finalState = "Stopped" || finalState = "NeedsWork" then
                ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Reviewer" (agent ()).Version "Operator STOP"
                |> success |> ignore
            if finalState = "NeedsWork" then
                ReviewWorkflow.setAgentAdministrativeStateAs first "AuthenticatedUser" (Guid.NewGuid())
                    "Reviewer" "NeedsWork" (agent ()).Version "Resume ordinary review"
                |> success |> ignore
            let path = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot one
            let context = GitCommandTests.TestContext(Path.GetDirectoryName one)
            try
                let head = (LedgerStore.fetch first |> success).Commit
                Client.OrchestrationApi.LedgerBindingConfig.bindExisting
                    "owner" context first.ProjectId first.LedgerId first.Epoch
                    first.RemoteName first.ApprovedFetchUrl first.ApprovedPushUrl head true
                |> success |> ignore
                let runtime = Client.Runtime(Path.GetDirectoryName one, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
                let command : Common.ReviewWriteCommand =
                    { ProjectName = "owner"; Action = "Claim"; OperationId = Guid.NewGuid().ToString("D")
                      TaskId = taskId; RequestId = id.ToString("D"); ActorId = "Reviewer"
                      ReviewerId = ""; ExpectedVersion = 1; Kind = ""; SourceRef = ""
                      HeadOid = ""; TargetRef = ""; Summary = ""; Verdict = ""
                      DocumentPath = ""; Content = ""; ExpectedBlobOid = "" }
                let run actor cmd =
                    let _, outcome, _ =
                        Client.SignalR.Client.processIncomingCommand runtime
                            (System.Text.Json.JsonSerializer.Serialize<Common.AgentCommand>(Common.ReviewWriteCommand cmd)) actor
                        |> Async.AwaitTask |> Async.RunSynchronously
                    outcome
                // The exact old StartTasklessWork receipt remains in canonical history.
                let _, original = LedgerStore.getOperationStatus second startId |> success
                Assert.That(original.IsSome, Is.True)
                let claimed = run "Reviewer" command
                Assert.That(claimed.IsOk, Is.True, sprintf "%A" claimed)
                let read () = (ReviewWorkflow.getBranchReview second taskId id |> success).Review
                Assert.That((read ()).ReviewerId, Is.EqualTo("Reviewer"))
                Assert.That((read ()).Version, Is.EqualTo(2))
                let reject action actor version =
                    let rejected = run actor { command with Action = action; ActorId = actor
                                                            OperationId = Guid.NewGuid().ToString("D")
                                                            ExpectedVersion = version; Verdict = "Accept" }
                    Assert.That(rejected.IsError, Is.True, sprintf "%A" rejected)
                let claimHead = (LedgerStore.fetch second |> success).Commit
                reject "Claim" "Reviewer" 1
                reject "Decide" "Reviewer" 1
                reject "Decide" "Owner" 2
                Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(claimHead))
                let release = run "Reviewer" { command with Action = "Release"; ExpectedVersion = 2
                                                            OperationId = Guid.NewGuid().ToString("D") }
                Assert.That(release.IsOk, Is.True, sprintf "%A" release)
                Assert.That((read ()).Status, Is.EqualTo("Unclaimed"))
                let reclaimed = run "Reviewer" { command with ExpectedVersion = 3
                                                              OperationId = Guid.NewGuid().ToString("D") }
                Assert.That(reclaimed.IsOk, Is.True, sprintf "%A" reclaimed)
                let decided = run "Reviewer" { command with Action = "Decide"; ExpectedVersion = 4
                                                            Verdict = "Accept"; OperationId = Guid.NewGuid().ToString("D") }
                Assert.That(decided.IsOk, Is.True, sprintf "%A" decided)
                Assert.That((read ()).Status, Is.EqualTo("Accepted"))
                let _, stillOriginal = LedgerStore.getOperationStatus second startId |> success
                Assert.That(stillOriginal, Is.EqualTo(original))
            finally
                if File.Exists path then File.Delete path)
