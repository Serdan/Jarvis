module Client.Tests.LedgerMutationStagePerfTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Client.LedgerStore
open Client.ReviewWorkflow
open NUnit.Framework

// Perf 3 owns this standalone fixture; Perf 1's general-purpose harness and
// Perf 2's TaskOverview batch tests are deliberately not reused or modified.
let private git directory args =
    use child = new Process()
    child.StartInfo.FileName <- "git"
    child.StartInfo.WorkingDirectory <- directory
    child.StartInfo.UseShellExecute <- false
    child.StartInfo.RedirectStandardOutput <- true
    child.StartInfo.RedirectStandardError <- true
    for arg in args do child.StartInfo.ArgumentList.Add(arg)
    Assert.That(child.Start(), Is.True, "Cannot start disposable Git fixture")
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    Assert.That(child.WaitForExit(30000), Is.True, "Fixture Git child did not exit")
    Assert.That(child.ExitCode, Is.EqualTo(0), sprintf "git %A: %s" args error)
    output.Trim()

let private good = function
    | Ok value -> value
    | Error failure -> failwithf "Unexpected disposable-ledger failure: %A" failure

let private withDisposableLedger action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-perf3-ledger-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let bare = Path.Combine(root, "remote.git")
        let a = Path.Combine(root, "first")
        let b = Path.Combine(root, "second")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; a ] |> ignore
        git a [ "config"; "user.name"; "Perf3 Fixture" ] |> ignore
        git a [ "config"; "user.email"; "perf3@example.test" ] |> ignore
        File.WriteAllText(Path.Combine(a, "source.txt"), "Perf 3 disposable source")
        git a [ "add"; "source.txt" ] |> ignore
        git a [ "commit"; "-m"; "disposable source" ] |> ignore
        git a [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; bare; b ] |> ignore
        git b [ "config"; "user.name"; "Perf3 Fixture" ] |> ignore
        git b [ "config"; "user.email"; "perf3@example.test" ] |> ignore
        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let binding path : LedgerBinding =
            { RepositoryPath = path
              RemoteName = "origin"
              ApprovedFetchUrl = bare
              ApprovedPushUrl = bare
              ProjectId = projectId
              LedgerId = ledgerId
              Epoch = 1 }
        let first, second = binding a, binding b
        LedgerStore.initialize first true |> good |> ignore
        LedgerStore.fetch second |> good |> ignore
        action first second
    finally
        // Only local disposable fixture/journal state is removed.
        for checkout in [ "first"; "second" ] do
            let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot(Path.Combine(root, checkout))
            let journal =
                Path.Combine(Path.GetDirectoryName(config), "pending", Path.GetFileNameWithoutExtension(config))
            if Directory.Exists(journal) then Directory.Delete(journal, true)
        if Directory.Exists(root) then Directory.Delete(root, true)

let private starts (m: LedgerStore.MutationStageMeasurement) =
    m.Phases |> List.sumBy (fun p -> p.ProcessStarts)
let private fetches (m: LedgerStore.MutationStageMeasurement) =
    m.Phases |> List.sumBy (fun p -> p.RemoteFetches)
let private pushes (m: LedgerStore.MutationStageMeasurement) =
    m.Phases |> List.sumBy (fun p -> p.RemotePushes)
let private commits (m: LedgerStore.MutationStageMeasurement) =
    m.Phases |> List.sumBy (fun p -> p.CommitTrees)
let private phase name (m: LedgerStore.MutationStageMeasurement) =
    m.Phases |> List.tryFind (fun p -> p.Phase = name)
let private measured label action =
    let outcome, metrics = LedgerStore.measureMutationStages action
    outcome |> good |> ignore
    TestContext.Progress.WriteLine(
        sprintf "Perf3 %s: %.2fms, processes=%d fetch=%d push=%d commit-tree=%d"
            label metrics.TotalElapsedMilliseconds (starts metrics)
            (fetches metrics) (pushes metrics) (commits metrics))
    metrics

[<TestFixture; NonParallelizable>]
type LedgerMutationStagePerfTests() =

    [<Test>]
    member _.FourRoutineWritesHaveBoundedGitWorkAndSourceAttributedPhases() =
        withDisposableLedger (fun first _ ->
            let taskId = "perf3-task"
            let create = measured "CreateTask" (fun () ->
                ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Perf3 disposable task")
            let register = measured "RegisterAgent" (fun () ->
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Perf3Owner")
            let assign = measured "AssignTask" (fun () ->
                ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Perf3Owner")
            ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Perf3Owner" 0 0 0
            |> good |> ignore
            let checkpoint = measured "CheckpointAttempt" (fun () ->
                ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) taskId "Perf3Owner"
                    0 0 0 "measured progress" "continue fixture" "" "")
            for name, metric in
                [ "CreateTask", create; "RegisterAgent", register
                  "AssignTask", assign; "CheckpointAttempt", checkpoint ] do
                Assert.That(fetches metric, Is.EqualTo(2), name + " unexpected remote fetch")
                Assert.That(pushes metric, Is.EqualTo(1), name + " one publication")
                Assert.That(commits metric, Is.EqualTo(1), name + " one commit-tree")
                let maxProcessStarts =
                    match name with
                    | "CreateTask" -> 38 | "RegisterAgent" -> 35
                    | "AssignTask" -> 47 | _ -> 44
                Assert.That(starts metric, Is.LessThanOrEqualTo(maxProcessStarts),
                            name + " redundant Git process regression")
                for stage in
                    [ "prepublication-fetch"; "local-preparation"
                      "publication-push"; "confirmation-fetch" ] do
                    Assert.That((phase stage metric).IsSome, Is.True, name + " missing " + stage)
                let totalPhases = metric.Phases |> List.sumBy (fun p -> p.ElapsedMilliseconds)
                Assert.That(totalPhases,
                    Is.LessThanOrEqualTo(metric.TotalElapsedMilliseconds + 1.),
                    name + " nested timings double-counted")
            // This bound catches a second remote fetch per phase even if wall
            // time is fast. The 30+ sample distribution is non-gating below.
            TestContext.Progress.WriteLine("Perf3: all four routine write paths measured against disposable local bare Git"))

    [<Test>]
    member _.SlowFailedFetchHasNoPublicationAndNoReplayableExternalEffect() =
        withDisposableLedger (fun first _ ->
            let inject phase args =
                if phase = "prepublication-fetch" && List.tryHead args = Some "fetch" then
                    Thread.Sleep(25)
                    Some ({ FaultExitCode = 1
                            FaultStderr = "injected timeout"
                            FaultTimedOut = true } : LedgerStore.GitTransportFault)
                else None
            let result, metric =
                LedgerStore.measureMutationStages (fun () ->
                    LedgerStore.withGitTransportFaults inject (fun () ->
                        ReviewWorkflow.createTask first (Guid.NewGuid()) "perf3-failed-fetch" "Never published"))
            match result with
            | Error(RemoteUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Expected fail-closed remote timeout: %A" other)
            Assert.That(fetches metric, Is.GreaterThanOrEqualTo(1))
            Assert.That(pushes metric, Is.Zero, "A prepublication read error cannot push")
            Assert.That(commits metric, Is.Zero, "A prepublication read error cannot commit")
            Assert.That((phase "publication-push" metric).IsNone, Is.True))

    [<Test>]
    member _.FailedBatchedIndexUpdateDoesNotCreateCommitOrPublish() =
        withDisposableLedger (fun first _ ->
            let inject phase args =
                if phase = "local-preparation" && List.tryHead args = Some "update-index" then
                    Some ({ FaultExitCode = 42
                            FaultStderr = "injected index-info failure"
                            FaultTimedOut = false } : LedgerStore.GitTransportFault)
                else None
            let operationId = Guid.NewGuid()
            let result, metric =
                LedgerStore.measureMutationStages (fun () ->
                    LedgerStore.withGitTransportFaults inject (fun () ->
                        ReviewWorkflow.createTask first operationId
                            "perf3-batch-index-failure" "Must remain absent"))
            match result with
            | Error(GitFailure _) -> ()
            | other -> Assert.Fail(sprintf "Expected failure before publication: %A" other)
            Assert.That(fetches metric, Is.EqualTo(1))
            Assert.That(commits metric, Is.Zero, "Cannot commit a failed index")
            Assert.That(pushes metric, Is.Zero, "Cannot publish a failed index")
            let _, receipt = LedgerStore.getOperationStatus first operationId |> good
            Assert.That(receipt.IsNone, Is.True, "Original UUID has no remote receipt")
            match ReviewWorkflow.getTask first "perf3-batch-index-failure" with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Failed index task unexpectedly exists"))


    [<Test>]
    member _.ConcurrentIndependentClientsPublishWithoutSilentLostUpdate() =
        withDisposableLedger (fun first second ->
            use gate = new Barrier(2)
            let start binding suffix =
                Task.Run(fun () ->
                    gate.SignalAndWait()
                    LedgerStore.measureMutationStages (fun () ->
                        ReviewWorkflow.createTask binding (Guid.NewGuid())
                            ("perf3-concurrent-" + suffix) ("Concurrent task " + suffix)))
            let left, right = start first "left", start second "right"
            Task.WaitAll([| left :> Task; right :> Task |])
            for outcome, metrics in [ left.Result; right.Result ] do
                match outcome with
                | Ok _ -> Assert.That(pushes metrics, Is.GreaterThanOrEqualTo(1))
                | Error(OutcomeUnknown _) ->
                    // Publication might have occurred. Do not manufacture a
                    // retry UUID or infer absence from a changed remote head.
                    TestContext.Progress.WriteLine("Perf3: concurrent publication uncertain; reconcile original UUID")
                | Error error -> Assert.Fail(sprintf "Concurrent writer failed unexpectedly: %A" error)
                Assert.That(pushes metrics, Is.LessThanOrEqualTo(8), "Bounded proven-rejected CAS retry")
            let _, l, _ = ReviewWorkflow.getTask first "perf3-concurrent-left" |> good
            let _, r, _ = ReviewWorkflow.getTask first "perf3-concurrent-right" |> good
            Assert.That(l.TaskId, Is.EqualTo("perf3-concurrent-left"))
            Assert.That(r.TaskId, Is.EqualTo("perf3-concurrent-right")))

    [<Test; Explicit("30+ non-gating distribution: run deliberately on same machine for before/after comparison")>]
    member _.ThirtySamplesPerMutationProduceMachineLocalStageDistributions() =
        withDisposableLedger (fun first _ ->
            let samples = System.Collections.Generic.Dictionary<string, ResizeArray<LedgerStore.MutationStageMeasurement>>()
            let allocations = System.Collections.Generic.Dictionary<string, ResizeArray<int64>>()
            for name in [ "CreateTask"; "RegisterAgent"; "AssignTask"; "CheckpointAttempt" ] do
                samples.Add(name, ResizeArray())
                allocations.Add(name, ResizeArray())
            for i in 1..30 do
                let id = sprintf "perf3-sample-%02d" i
                let owner = sprintf "Perf3Owner%02d" i
                let collect name command =
                    let allocatedBefore = GC.GetAllocatedBytesForCurrentThread()
                    let _, metric = LedgerStore.measureMutationStages (fun () -> command () |> good)
                    samples.[name].Add(metric)
                    allocations.[name].Add(max 0L (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore))
                collect "CreateTask" (fun () ->
                    ReviewWorkflow.createTask first (Guid.NewGuid()) id ("Sample " + id))
                collect "RegisterAgent" (fun () ->
                    ReviewWorkflow.registerAgent first (Guid.NewGuid()) owner)
                collect "AssignTask" (fun () ->
                    ReviewWorkflow.assignTask first (Guid.NewGuid()) id (Guid.NewGuid()) owner)
                ReviewWorkflow.resumeAgent first (Guid.NewGuid()) id owner 0 0 0
                |> good |> ignore
                collect "CheckpointAttempt" (fun () ->
                    ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) id owner
                        0 0 0 "sample progress" "continue" "" "")
            for KeyValue(name, values) in samples do
                let values = values |> Seq.toArray
                Assert.That(values.Length, Is.EqualTo(30))
                let ordered = values |> Array.map _.TotalElapsedMilliseconds |> Array.sort
                let pct p = ordered.[max 0 (int (ceil (float values.Length * p)) - 1)]
                let average f = values |> Array.averageBy f
                let stdoutBytes (metric: LedgerStore.MutationStageMeasurement) =
                    metric.Phases |> List.sumBy _.StandardOutputBytes
                let allocationsLocalThread = allocations.[name] |> Seq.averageBy float
                TestContext.Progress.WriteLine(
                    sprintf "Perf3 %-18s n=30 ms cold=%.2f p50=%.2f p95=%.2f p99=%.2f processes=%.1f fetch=%.1f push=%.1f commit=%.1f stdoutBytes=%.1f allocCurrentThreadBytes=%.1f"
                        name values.[0].TotalElapsedMilliseconds (pct 0.50) (pct 0.95) (pct 0.99)
                        (average (starts >> float)) (average (fetches >> float))
                        (average (pushes >> float)) (average (commits >> float))
                        (average (stdoutBytes >> float)) allocationsLocalThread)
                for stage in [ "prepublication-fetch"; "local-preparation"; "publication-push"; "confirmation-fetch" ] do
                    let stageMs =
                        values
                        |> Array.map (fun v -> phase stage v |> Option.map _.ElapsedMilliseconds |> Option.defaultValue 0.)
                        |> Array.sort
                    TestContext.Progress.WriteLine(sprintf "Perf3 %-18s phase=%-22s p50=%.2f p95=%.2f p99=%.2f"
                        name stage stageMs.[14] stageMs.[28] stageMs.[29]))
