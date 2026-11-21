module Client.Tests.NativeHealthTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Client
open Client.LedgerStore
open Client.NativeHealth
open Client.NativeHealth.NativeHealth
open Client.ReviewWorkflow
open Common.SignalR
open NUnit.Framework

[<TestFixture>]
type NativeHealthTests() =
    let serverNow = DateTimeOffset(2026, 11, 21, 14, 0, 0, TimeSpan.Zero)
    let candidate =
        { ProjectName = "Fixture"
          ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111")
          LedgerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
          Epoch = 1
          ApprovedFetchUrl = "test://ledger"
          ApprovedPushUrl = "test://ledger"
          GitHead = "aaa"
          AgentId = "Worker 4"
          AgentVersion = 3
          TaskId = "native-health-test"
          TaskVersion = 2
          AttemptId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
          AttemptVersion = 4
          Generation = 1
          SpecBlobOid = "sha"
          SourceRef = "refs/heads/owned" }

    let snapshot elapsed =
        { ServerNowUtc = serverNow
          LastSeenAtUtc = Some(serverNow - TimeSpan.FromSeconds(float elapsed)) }

    let fake (roster: Candidate list)
             (readings: Result<AgentActivitySnapshot, string> list)
             (current: Candidate -> Result<Candidate option, string>) =
        let queue = Queue<Result<AgentActivitySnapshot,string>>(readings)
        let calls = ResizeArray<Guid>()
        let mutable reads = 0
        let mutable enumerations = 0
        let dependencies : Dependencies =
            { Enumerate = (fun () -> enumerations <- enumerations + 1; Ok roster)
              Current = current
              Activity = (fun _ _ ->
                  reads <- reads + 1
                  Task.FromResult(if queue.Count > 0 then queue.Dequeue() else Error "Unscripted activity read"))
              Recover = (fun _ id -> calls.Add id; Ok ()) }
        dependencies, calls, (fun () -> reads), (fun () -> enumerations)

    let enabled =
        { Enabled = true
          InactivitySeconds = 600 }

    let run settings dependencies =
        let result = NativeHealth.checkOnce settings dependencies
        result.GetAwaiter().GetResult()

    let git cwd (args: string list) =
        use child = new Process()
        child.StartInfo.FileName <- "git"
        child.StartInfo.WorkingDirectory <- cwd
        child.StartInfo.RedirectStandardOutput <- true
        child.StartInfo.RedirectStandardError <- true
        args |> List.iter child.StartInfo.ArgumentList.Add
        if not (child.Start()) then failwith "Cannot start git fixture."
        let output = child.StandardOutput.ReadToEnd()
        let errors = child.StandardError.ReadToEnd()
        child.WaitForExit()
        if child.ExitCode <> 0 then failwithf "Git fixture failed: %s" errors
        output.Trim()

    let success = function
        | Ok value -> value
        | Error issue -> failwithf "Unexpected canonical Git result: %A" issue

    let withGitFixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-native-health-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let firstPath = Path.Combine(root, "one")
            let secondPath = Path.Combine(root, "two")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; firstPath ] |> ignore
            git firstPath [ "config"; "user.name"; "Health Fixture" ] |> ignore
            git firstPath [ "config"; "user.email"; "health@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(firstPath, "source.txt"), "health test")
            git firstPath [ "add"; "source.txt" ] |> ignore
            git firstPath [ "commit"; "-m"; "fixture" ] |> ignore
            git firstPath [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; secondPath ] |> ignore
            git secondPath [ "config"; "user.name"; "Health Fixture" ] |> ignore
            git secondPath [ "config"; "user.email"; "health@example.test" ] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = bare
                  ApprovedPushUrl = bare
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let first, second = binding firstPath, binding secondPath
            LedgerStore.initialize first true |> success |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Owner" |> success |> ignore
            let taskId = "native-health-cas"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId
                "Unchanged source" "<h1>Original approved spec</h1>" |> success |> ignore
            let attemptId = Guid.NewGuid()
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner"
            |> success |> ignore
            ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Owner" 0 0 0
            |> success |> ignore
            let sourceHead = git firstPath [ "rev-parse"; "HEAD" ]
            let sourceRef = "refs/heads/" + git firstPath [ "branch"; "--show-current" ]
            let _, _, started = ReviewWorkflow.getTask first taskId |> success
            ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Owner"
                started.Value.Version started.Value.AssignmentGeneration sourceHead sourceRef
            |> success |> ignore
            action first second taskId
        finally
            for subdir in [ "one"; "two" ] do
                let path = Path.Combine(root, subdir)
                let bindingPath = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot path
                let journal =
                    Path.Combine(Path.GetDirectoryName(bindingPath), "pending",
                                 Path.GetFileNameWithoutExtension(bindingPath))
                if Directory.Exists(journal) then Directory.Delete(journal, true)
            Directory.Delete(root, true)

    [<TestCase(599, 0)>]
    [<TestCase(600, 1)>]
    [<TestCase(601, 1)>]
    member _.ServerTimeBoundaryIsInclusive(ageSeconds: int, expectedRecoveries: int) =
        let deps, calls, reads, _ =
            fake [ candidate ] [ Ok(snapshot ageSeconds); Ok(snapshot ageSeconds) ] (fun c -> Ok(Some c))
        let summary = run enabled deps
        Assert.That(summary.Recoveries, Is.EqualTo(expectedRecoveries))
        Assert.That(calls.Count, Is.EqualTo(expectedRecoveries))
        Assert.That(reads(), Is.EqualTo(if ageSeconds < 600 then 1 else 2))
        Assert.That(summary.Errors, Is.Empty)

    [<Test>]
    member _.NewActivityAtFreshServerRecheckSuppressesRecovery() =
        let deps, calls, reads, _ =
            fake [ candidate ] [ Ok(snapshot 700); Ok(snapshot 12) ] (fun c -> Ok(Some c))
        let result = run enabled deps
        Assert.That(result.Recoveries, Is.EqualTo(0))
        Assert.That(result.Holds, Is.EqualTo(1))
        Assert.That(reads(), Is.EqualTo(2))
        Assert.That(calls, Is.Empty)

    [<Test>]
    member _.UnavailableAndUninitializedServerActivityAlwaysHold() =
        for observed in [
            Error "ActivityProofUnavailable"
            Ok { ServerNowUtc = serverNow; LastSeenAtUtc = None }
            Ok { ServerNowUtc = DateTimeOffset.MinValue; LastSeenAtUtc = Some serverNow }
            Ok { ServerNowUtc = serverNow; LastSeenAtUtc = Some(serverNow.AddSeconds(1.)) }
            Ok { ServerNowUtc = serverNow; LastSeenAtUtc = Some DateTimeOffset.MinValue }
        ] do
            let deps, calls, _, _ = fake [ candidate ] [ observed ] (fun c -> Ok(Some c))
            let outcome = run enabled deps
            Assert.That(outcome.Recoveries, Is.EqualTo(0))
            Assert.That(calls, Is.Empty)
            Assert.That(outcome.Holds, Is.EqualTo(1))

    [<Test>]
    member _.ServerClockRatherThanMachineClockDecidesEligibility() =
        // The server dates can be arbitrarily far from the actual host clock.
        let past = DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
        let seen = { ServerNowUtc = past; LastSeenAtUtc = Some(past.AddSeconds(-600.)) }
        let deps, calls, _, _ = fake [ candidate ] [ Ok seen; Ok seen ] (fun c -> Ok(Some c))
        Assert.That((run enabled deps).Recoveries, Is.EqualTo(1))
        Assert.That(calls.Count, Is.EqualTo(1))

    [<Test>]
    member _.FreshServerReadFailureSuppressesRecovery() =
        let deps, calls, reads, _ =
            fake [ candidate ] [ Ok(snapshot 800); Error "server request timed out" ] (fun c -> Ok(Some c))
        let outcome = run enabled deps
        Assert.That(outcome.Recoveries, Is.Zero)
        Assert.That(outcome.Holds, Is.EqualTo(1))
        Assert.That(reads(), Is.EqualTo(2))
        Assert.That(calls, Is.Empty)

    [<Test>]
    member _.ServerRestartWithoutAttributionCannotEstablishInactivity() =
        let restarted = { ServerNowUtc = serverNow; LastSeenAtUtc = None }
        let deps, calls, reads, _ = fake [ candidate ] [ Ok restarted ] (fun c -> Ok(Some c))
        Assert.That((run enabled deps).Recoveries, Is.Zero)
        Assert.That(reads(), Is.EqualTo(1))
        Assert.That(calls, Is.Empty)

    [<Test>]
    member _.ClockRegressingBetweenAuthenticatedReadsHolds() =
        let regressed = { ServerNowUtc = serverNow.AddSeconds(-1.); LastSeenAtUtc = (snapshot 650).LastSeenAtUtc }
        let deps, calls, _, _ =
            fake [ candidate ] [ Ok(snapshot 650); Ok regressed ] (fun c -> Ok(Some c))
        Assert.That((run enabled deps).Recoveries, Is.Zero)
        Assert.That(calls, Is.Empty)

    [<Test>]
    member _.DisabledSettingsPerformNoEnumerationActivityOrMutation() =
        let deps, calls, reads, scans =
            fake [ candidate ] [] (fun c -> Ok(Some c))
        let outcome = run { enabled with Enabled = false } deps
        Assert.That(outcome.Candidates, Is.Zero)
        Assert.That(scans(), Is.Zero)
        Assert.That(reads(), Is.Zero)
        Assert.That(calls, Is.Empty)

    [<Test>]
    member _.InvalidTimeoutHoldsWithoutReadingGit() =
        let deps, calls, reads, scans = fake [ candidate ] [] (fun c -> Ok(Some c))
        let outcome = run { enabled with InactivitySeconds = 0 } deps
        Assert.That(outcome.Holds, Is.EqualTo(1))
        Assert.That(scans(), Is.Zero)
        Assert.That(reads(), Is.Zero)
        Assert.That(calls, Is.Empty)

    [<Test>]
    member _.StaleReadyParkedBlockedOrReassignedCandidateIsNotRecovered() =
        // A canonical recheck is the authority for state/owner/generation, not
        // a cached presentation state or a task title.
        for current in [
            Ok None
            Ok(Some { candidate with AgentVersion = candidate.AgentVersion + 1 })
            Ok(Some { candidate with Generation = 2 })
            Ok(Some { candidate with AttemptId = "different" })
            Ok(Some { candidate with SpecBlobOid = "different" })
            Ok(Some { candidate with SourceRef = "different" })
            Ok(Some { candidate with ApprovedFetchUrl = "foreign" })
        ] do
            let deps, calls, reads, _ = fake [ candidate ] [ Ok(snapshot 900) ] (fun _ -> current)
            Assert.That((run enabled deps).Recoveries, Is.Zero)
            Assert.That(calls, Is.Empty)
            Assert.That(reads(), Is.EqualTo(1))

    [<Test>]
    member _.CanonicalGitFailureSuppressesRecovery() =
        let deps, calls, _, _ =
            fake [ candidate ] [ Ok(snapshot 900) ] (fun _ -> Error "RemoteUnavailable")
        let outcome = run enabled deps
        Assert.That(outcome.Recoveries, Is.Zero)
        Assert.That(outcome.Errors.Length, Is.EqualTo(1))
        Assert.That(calls, Is.Empty)

    [<Test>]
    member _.RecoveryOperationUuidIsStableWithinGenerationButUniqueAcrossGenerations() =
        let id = NativeHealth.recoveryOperationId candidate
        Assert.That(id, Is.Not.EqualTo(Guid.Empty))
        Assert.That(NativeHealth.recoveryOperationId candidate, Is.EqualTo(id))
        Assert.That(NativeHealth.recoveryOperationId { candidate with Generation = 2 }, Is.Not.EqualTo(id))
        Assert.That(NativeHealth.recoveryOperationId { candidate with AttemptVersion = 5 }, Is.Not.EqualTo(id))
        Assert.That(NativeHealth.recoveryOperationId { candidate with AgentVersion = 4 }, Is.Not.EqualTo(id))
        Assert.That(NativeHealth.recoveryOperationId { candidate with ProjectId = Guid.NewGuid() }, Is.Not.EqualTo(id))

    [<Test>]
    member _.TwoGitClientsHaveOneCasWinnerAndPreserveTheSameAttemptAndSpec() =
        withGitFixture (fun first second taskId ->
            let snapshot = LedgerStore.fetch first |> success
            let agent = ReviewWorkflow.getAgentWorkingTurnAt first snapshot "Owner" |> success
            let task, attemptValue = ReviewWorkflow.getTaskAt first snapshot taskId |> success
            let attempt = attemptValue.Value
            Assert.That(agent.State, Is.EqualTo("Working"))
            Assert.That(attempt.Phase, Is.EqualTo("Started"))
            let derived = NativeHealth.candidateAt "Fixture" first snapshot "Owner" |> success
            Assert.That(derived.IsSome, Is.True)
            Assert.That(derived.Value.AttemptId, Is.EqualTo(attempt.AttemptId))
            let candidate =
                { candidate with TaskId = taskId; TaskVersion = task.Version
                                 AgentId = "Owner"; AgentVersion = agent.Version
                                 AttemptId = attempt.AttemptId
                                 AttemptVersion = attempt.Version
                                 Generation = attempt.AssignmentGeneration
                                 SpecBlobOid = attempt.SpecBlobOid
                                 SourceRef = attempt.SourceRef
                                 ProjectId = first.ProjectId; LedgerId = first.LedgerId
                                 ApprovedFetchUrl = first.ApprovedFetchUrl
                                 ApprovedPushUrl = first.ApprovedPushUrl
                                 GitHead = snapshot.Commit }
            let winningId = NativeHealth.recoveryOperationId candidate
            let competingId = Guid.NewGuid()
            let recover binding operationId =
                ReviewWorkflow.recoverAgentAssignmentAs binding "AuthenticatedUser"
                    operationId taskId "Owner"
                    attempt.Version agent.Version attempt.AssignmentGeneration
                    "Native Health authoritative server inactivity"
            let firstResult = recover first winningId |> success
            Assert.That(firstResult.AlreadyApplied, Is.False)
            match recover second competingId with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Second Git client must lose CAS: %A" other)
            let replay = recover second winningId |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            let _, receipt = LedgerStore.getOperationStatus second winningId |> success
            Assert.That(receipt.IsSome, Is.True)
            Assert.That(receipt.Value.Type, Is.EqualTo("RecoverAgentAssignment"))
            let _, afterTask, afterAttemptValue = ReviewWorkflow.getTask second taskId |> success
            let after = afterAttemptValue.Value
            Assert.That(afterTask.TaskId, Is.EqualTo(task.TaskId))
            Assert.That(after.AttemptId, Is.EqualTo(attempt.AttemptId))
            Assert.That(after.SpecBlobOid, Is.EqualTo(attempt.SpecBlobOid))
            Assert.That(after.SourceRef, Is.EqualTo(attempt.SourceRef))
            Assert.That(after.BaseCommit, Is.EqualTo(attempt.BaseCommit))
            Assert.That(after.TargetRef, Is.EqualTo(attempt.TargetRef))
            Assert.That(after.SourceRepositoryId, Is.EqualTo(attempt.SourceRepositoryId))
            let readySnapshot = LedgerStore.fetch second |> success
            Assert.That((NativeHealth.candidateAt "Fixture" second readySnapshot "Owner" |> success).IsNone, Is.True)
            Assert.That(after.AssignmentGeneration, Is.EqualTo(attempt.AssignmentGeneration + 1))
            Assert.That(after.Phase, Is.EqualTo("Queued"))
            let updatedAgent = ReviewWorkflow.getAgentWorkingTurn second "Owner" |> success
            Assert.That(updatedAgent.State, Is.EqualTo("Ready"))
            match ReviewWorkflow.checkpointAttempt second (Guid.NewGuid()) taskId "Owner"
                    attempt.Version agent.Version attempt.AssignmentGeneration
                    "Old generation cannot checkpoint" "No progress" null "" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old assignment must not checkpoint: %A" other))
