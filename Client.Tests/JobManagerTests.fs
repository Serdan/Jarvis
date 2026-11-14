module JobManagerTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Client
open Client.IO
open Client.JobManager
open Client.SignalR
open Common
open NUnit.Framework
open FsUnitTyped

let createTempProject () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-jobs-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
    root

type TestContext(root: string) =
    interface ProjectIO with
        member _.Project = { Root = ProjectDirectory root; SpecialFiles = []; FolderFilters = [] }
    interface FileIO with
        member _.File = FileOperations.impl
        member _.EditText(path, options, edit) =
            AtomicTextFiles.edit path options edit

[<Test>]
let ``job lifecycle starts lists reads and cancels`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let start =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "--info" ]
              Reason = Some "Inspect dotnet runtime"
              WorkingDirectory = None
              MaxOutputBytes = Some 8192 }

        let jobId =
            match startJob start context with
            | Ok result -> result.JobId
            | Error error -> failwith $"Expected StartJob Ok, got {error}"

        // StartJob persists an immutable intent before native execution and
        // a process ID/birth observation before returning its job handle.
        let journal =
            ProcessOwnershipJournal.inspectAt
                (ProcessOwnershipJournal.defaultDirectory()) jobId
            |> function
                | Ok value -> value
                | Error message -> failwithf "Missing durable process evidence: %s" message
        Assert.That(journal.Intent.JobId, Is.EqualTo(jobId))
        Assert.That(journal.Intent.DeviceId, Is.Not.Empty)
        Assert.That(journal.Intent.ClientIncarnationId, Is.Not.Empty)
        Assert.That(journal.Launch.IsSome, Is.True)
        Assert.That(journal.Launch.Value.ProcessId, Is.GreaterThan(0))

        let read : GetProcessOwnershipEvidenceCommand = { ProjectName = "Project1"; JobId = jobId }
        match getProcessOwnershipEvidence read context with
        | Error error -> Assert.Fail($"Authenticated process evidence read failed: {error}")
        | Ok evidence ->
            evidence.JobId |> shouldEqual jobId
            evidence.ProjectId |> shouldEqual "Project1"
            evidence.DeviceId |> shouldEqual journal.Intent.DeviceId
            evidence.ClientIncarnationId |> shouldEqual journal.Intent.ClientIncarnationId
            evidence.Stage |> shouldEqual "Launched"
            evidence.ProcessId |> shouldEqual (Some journal.Launch.Value.ProcessId)
            evidence.ProcessTreeStatus |> shouldEqual "Unverified"
            evidence.IntentSha256 |> shouldEqual (Some journal.Launch.Value.IntentSha256)
            let serialized = System.Text.Json.JsonSerializer.Serialize(evidence)
            Assert.That(serialized.Contains("\"ProcessTreeStatus\":\"Unverified\""), Is.True)

        // A valid UUID cannot be read through another project route.
        match getProcessOwnershipEvidence
                  ({ ProjectName = "OtherProject"; JobId = jobId }: GetProcessOwnershipEvidenceCommand) context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Another project was permitted to read process identity.")

        // The caller's project selector cannot override the resolved route.
        let target =
            ProjectPaths.ProjectTarget.parse "OtherProject"
            |> Result.map ProjectPaths.ProjectTarget.projectId
            |> Result.defaultWith (fun e -> failwithf "Invalid test project: %A" e)
        match getProcessOwnershipEvidenceForProject (Some target) read context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Mismatched authenticated project target was accepted.")

        match listJobs { ProjectName = Some "Project1"; IncludeCompleted = true } context with
        | Ok result ->
            let job = result.Jobs |> List.find (fun job -> job.JobId = jobId)
            job.Reason |> shouldEqual (Some "Inspect dotnet runtime")
        | Error error -> Assert.Fail($"Expected ListJobs Ok, got {error}")

        Thread.Sleep 500

        match getJobResult { ProjectName = "Project1"; JobId = jobId; AfterSequence = Some 0L } context with
        | Ok result ->
            result.JobId |> shouldEqual jobId
            result.NextSequence >= 0L |> shouldEqual true
        | Error error -> Assert.Fail($"Expected GetJobResult Ok, got {error}")

        cancelJob { ProjectName = "Project1"; JobId = jobId } context |> shouldEqual (Ok())
    finally
        Directory.Delete(root, true)

[<Test>]
let ``job output is bounded by max output bytes`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let start =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "--info" ]
              Reason = None
              WorkingDirectory = None
              MaxOutputBytes = Some 64 }

        let jobId =
            match startJob start context with
            | Ok result -> result.JobId
            | Error error -> failwith $"Expected StartJob Ok, got {error}"

        Thread.Sleep 750

        match getJobResult { ProjectName = "Project1"; JobId = jobId; AfterSequence = Some 0L } context with
        | Ok result ->
            let retainedBytes =
                result.Events
                |> List.sumBy (fun event -> Encoding.UTF8.GetByteCount event.Text)

            retainedBytes <= 64 |> shouldEqual true
            result.Truncated |> shouldEqual true
        | Error error -> Assert.Fail($"Expected GetJobResult Ok, got {error}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``failed job start does not leave a ghost job`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let executable = "jarvis-command-that-does-not-exist"
        let start =
            { ProjectName = "Project1"
              Executable = executable
              Args = []
              Reason = None
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

        match startJob start context with
        | Error _ -> ()
        | Ok result -> Assert.Fail($"Expected failed start, got job {result.JobId}")

        match listJobs { ProjectName = Some "Project1"; IncludeCompleted = true } context with
        | Ok result ->
            result.Jobs
            |> List.exists (fun job -> job.Executable = executable)
            |> shouldEqual false
        | Error error -> Assert.Fail($"Expected ListJobs Ok, got {error}")
    finally
        Directory.Delete(root, true)

[<Test>]
let ``job output polling is ordered and incremental across streams`` () =
    let root = createTempProject ()

    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(
            Path.Combine(project, "events.fsx"),
            "printfn \"out-1\"\neprintfn \"err-1\"\nprintfn \"out-2\"\neprintfn \"err-2\""
        )

        let context = TestContext root
        let start =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "events.fsx" ]
              Reason = None
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

        let jobId =
            match startJob start context with
            | Ok result -> result.JobId
            | Error error -> failwith $"Expected StartJob Ok, got {error}"

        // dotnet fsi startup can exceed five seconds on heavily loaded dev hosts.
        // Keep polling incrementally but use a measured deadline, not a fixed 100 iterations.
        let deadline = Stopwatch.StartNew()
        let rec waitForCompletion () =
            match getJobResult { ProjectName = "Project1"; JobId = jobId; AfterSequence = Some 0L } context with
            | Error error -> failwith $"Expected GetJobResult Ok, got {error}"
            | Ok result when result.Status <> Running -> result
            | Ok _ when deadline.Elapsed >= TimeSpan.FromSeconds 20.0 -> failwith "Job did not complete in 20 seconds."
            | Ok _ ->
                Thread.Sleep 50
                waitForCompletion ()

        let first = waitForCompletion ()
        first.Events.IsEmpty |> shouldEqual false
        first.Events |> List.exists (fun event -> event.Stream = JobOutputStream.StdOut) |> shouldEqual true
        first.Events |> List.exists (fun event -> event.Stream = JobOutputStream.StdErr) |> shouldEqual true

        first.Events
        |> List.map (fun event -> event.Sequence)
        |> List.pairwise
        |> List.forall (fun (before, after) -> before < after)
        |> shouldEqual true

        match getJobResult { ProjectName = "Project1"; JobId = jobId; AfterSequence = Some first.NextSequence } context with
        | Error error -> Assert.Fail($"Expected second GetJobResult Ok, got {error}")
        | Ok second ->
            second.Events |> shouldEqual []
            second.NextSequence |> shouldEqual first.NextSequence
    finally
        Directory.Delete(root, true)

[<Test; NonParallelizable>]
let ``startJob strips sensitive inherited environment`` () =
    let root = createTempProject ()
    let variable = "JARVIS_TEST_JOB_SECRET_TOKEN"
    let original = Environment.GetEnvironmentVariable variable

    try
        Environment.SetEnvironmentVariable(variable, "hidden-job-value")
        ProcessEnvironment.configureAllowedEnvironmentVariables []

        let project = Path.Combine(root, "Project1")
        File.WriteAllText(
            Path.Combine(project, "environment.fsx"),
            """open System
printfn "%s" (Environment.GetEnvironmentVariable("JARVIS_TEST_JOB_SECRET_TOKEN") |> Option.ofObj |> Option.defaultValue "missing")"""
        )

        let context = TestContext root
        let start =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "environment.fsx" ]
              Reason = None
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

        let jobId =
            match startJob start context with
            | Ok result -> result.JobId
            | Error error -> failwith $"Expected StartJob Ok, got {error}"

        // dotnet fsi startup varies across hosts; use the same measured deadline
        // as the neighboring cross-stream job test, not five seconds of polling.
        let deadline = Stopwatch.StartNew()
        let rec waitForCompletion () =
            match getJobResult { ProjectName = "Project1"; JobId = jobId; AfterSequence = Some 0L } context with
            | Error error -> failwith $"Expected GetJobResult Ok, got {error}"
            | Ok result when result.Status <> Running -> result
            | Ok _ when deadline.Elapsed >= TimeSpan.FromSeconds 20.0 -> failwith "Job did not complete in 20 seconds."
            | Ok _ ->
                Thread.Sleep 50
                waitForCompletion ()

        let result = waitForCompletion ()
        let output = result.Events |> List.map (fun event -> event.Text) |> String.concat ""

        output.Contains("missing") |> shouldEqual true
        output.Contains("hidden-job-value") |> shouldEqual false
    finally
        ProcessEnvironment.configureAllowedEnvironmentVariables []
        Environment.SetEnvironmentVariable(variable, original)
        Directory.Delete(root, true)


[<Test>]
let ``job read and cancellation reject a mismatched project without stopping the job`` () =
    let root = createTempProject ()
    try
        Directory.CreateDirectory(Path.Combine(root, "JobScope")) |> ignore
        let context = TestContext root
        let command =
            { ProjectName = "JobScope"
              Executable = "dotnet"
              Args = [ "--version" ]
              Reason = None
              WorkingDirectory = None
              MaxOutputBytes = Some 1024 }
        let jobId =
            match startJob command context with
            | Ok result -> result.JobId
            | Error error -> failwithf "Unexpected job start failure: %A" error

        match getJobResult { ProjectName = "OtherProject"; JobId = jobId; AfterSequence = None } context with
        | Error(Client.ValidationError _) -> ()
        | other -> Assert.Fail(sprintf "Expected cross-project read rejection, got %A" other)
        match cancelJob { ProjectName = "OtherProject"; JobId = jobId } context with
        | Error(Client.ValidationError _) -> ()
        | other -> Assert.Fail(sprintf "Expected cross-project cancel rejection, got %A" other)
        match getJobResult { ProjectName = "JobScope"; JobId = jobId; AfterSequence = None } context with
        | Ok _ -> ()
        | other -> Assert.Fail(sprintf "Correct project should still read job, got %A" other)
        cancelJob { ProjectName = "JobScope"; JobId = jobId } context |> shouldEqual (Ok())
    finally
        Directory.Delete(root, true)

[<Test>]
let ``worktree jobs remain accessible through their logical project name`` () =
    let root = createTempProject ()
    try
        let main = Path.Combine(root, "Project1")
        let worktree = Path.Combine(root, "job-worktree")
        let git args =
            use proc = new Process()
            proc.StartInfo.FileName <- "git"
            proc.StartInfo.WorkingDirectory <- main
            proc.StartInfo.RedirectStandardOutput <- true
            proc.StartInfo.RedirectStandardError <- true
            args |> List.iter proc.StartInfo.ArgumentList.Add
            Assert.That(proc.Start(), Is.True)
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()
            Assert.That(proc.ExitCode, Is.EqualTo(0), stderr)
            stdout.Trim()
        git [ "init" ] |> ignore
        File.WriteAllText(Path.Combine(main, "fixture.txt"), "fixture")
        git [ "add"; "fixture.txt" ] |> ignore
        git [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "fixture" ] |> ignore
        git [ "worktree"; "add"; "-b"; "job-fixture"; worktree ] |> ignore
        let context = TestContext root
        let jobId =
            match startJob
                    { ProjectName = "Project1::job-worktree"
                      Executable = "dotnet"
                      Args = [ "--info" ]
                      Reason = None
                      WorkingDirectory = None
                      MaxOutputBytes = Some 4096 } context with
            | Ok started -> started.JobId
            | Error error -> failwithf "StartJob failed: %A" error
        match listJobs { ProjectName = Some "Project1"; IncludeCompleted = true } context with
        | Ok listed ->
            let item = listed.Jobs |> List.find (fun job -> job.JobId = jobId)
            Assert.That(item.ProjectName, Is.EqualTo("Project1"))
            Assert.That(item.WorkingDirectory, Is.EqualTo(Some worktree))
        | Error error -> Assert.Fail(sprintf "ListJobs failed: %A" error)
        match getJobResult { ProjectName = "Project1"; JobId = jobId; AfterSequence = Some 0L } context with
        | Ok result -> Assert.That(result.JobId, Is.EqualTo(jobId))
        | Error error -> Assert.Fail(sprintf "GetJobResult failed: %A" error)
        cancelJob { ProjectName = "Project1"; JobId = jobId } context |> shouldEqual (Ok())
    finally
        Directory.Delete(root, true)


[<Test>]
let invalidProjectSelectorsCannotPassJobIdentityChecks () =
    let root = createTempProject()
    try
        let context = TestContext root
        let started =
            startJob
                { ProjectName = "Project1"; Executable = "dotnet"
                  Args = [ "--info" ]; Reason = None; WorkingDirectory = None
                  MaxOutputBytes = Some 2048 } context
        let id =
            match started with
            | Ok job -> job.JobId
            | Error error -> failwithf "Could not start test job: %A" error
        match listJobs { ProjectName = Some "Project1::bad::selector"; IncludeCompleted = true } context with
        | Error(Client.ValidationError _) -> ()
        | other -> Assert.Fail(sprintf "Expected invalid list scope, got: %A" other)
        match getJobResult { ProjectName = "Project1::bad::selector"; JobId = id; AfterSequence = None } context with
        | Error(Client.ValidationError _) -> ()
        | other -> Assert.Fail(sprintf "Expected invalid read scope, got: %A" other)
        match cancelJob { ProjectName = "Project1::bad::selector"; JobId = id } context with
        | Error(Client.ValidationError _) -> ()
        | other -> Assert.Fail(sprintf "Expected invalid cancel scope, got: %A" other)
        cancelJob { ProjectName = "Project1"; JobId = id } context |> shouldEqual (Ok())
    finally
        Directory.Delete(root, true)


let private jobProject =
    match ProjectPaths.ProjectTarget.parse "Project1" with
    | Ok target -> Some(ProjectPaths.ProjectTarget.projectId target)
    | Error error -> failwith $"Cannot parse test project: {error}"

let private completedToolResponse payload : AgentCommandResponse =
    { Result = Some payload
      Error = None
      Instructions = []
      AgentStateChanges = [ { ProjectName = "Project1"; AgentId = "Worker 1" } ]
      InboxActivation = None
      PendingJob = None }

[<Test>]
let autoPromotedToolRetainsIdenticalTypedOutcomeAndNeverExecutesTwice () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let blocked = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        let count = ref 0
        let registered =
            match startToolOperation jobProject (Some "Project1") "Worker 1" "ReadFile"
                    (Some "operation-one") "read-file-blocked-payload" (fun () ->
                        count.Value <- count.Value + 1
                        blocked.Task) with
            | Ok job -> job
            | Error error -> failwith $"Unable to register blocked tool: {error}"
        let stopwatch = Stopwatch.StartNew()
        let first = (awaitToolOperation registered (TimeSpan.FromMilliseconds 30.)).GetAwaiter().GetResult()
        stopwatch.Stop()
        let handle =
            match first with
            | Choice2Of2 handle -> handle
            | Choice1Of2 _ -> failwith "Blocked tool must return a running handle."
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(500L))
        Assert.That(handle.Status, Is.EqualTo("Running"))
        Assert.That(handle.OperationId, Is.EqualTo(Some "operation-one"))
        let cmd = { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None }
        match getJobResultForProjectAsAgent jobProject (Some "Worker 1") cmd context with
        | Ok value ->
            Assert.That(value.Status, Is.EqualTo(JobStatus.Running))
            Assert.That(value.ToolResponse.IsNone, Is.True)
        | Error error -> Assert.Fail($"Expected running tool: {error}")
        let original = completedToolResponse """{"result":"complete"}"""
        blocked.SetResult original
        let _, completion = registered
        let observed = completion.WaitAsync(TimeSpan.FromSeconds 4.).GetAwaiter().GetResult()
        Assert.That(observed, Is.EqualTo(original))
        for _ in 1 .. 3 do
            match getJobResultForProjectAsAgent jobProject (Some "Worker 1") cmd context with
            | Ok value ->
                Assert.That(value.Status, Is.EqualTo(JobStatus.ToolCompleted))
                Assert.That(value.ToolResponse, Is.EqualTo(Some original))
                Assert.That(value.OperationId, Is.EqualTo(Some "operation-one"))
            | Error error -> Assert.Fail($"Expected complete tool: {error}")
        Assert.That(count.Value, Is.EqualTo(1),
                    "Polling must not reexecute the original operation or its ledger mutations.")
        match getJobResultForProjectAsAgent jobProject (Some "OtherAgent") cmd context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Wrong agent must not read a tool operation job.")
        let otherProject =
            match ProjectPaths.ProjectTarget.parse "OtherProject" with
            | Ok target -> Some(ProjectPaths.ProjectTarget.projectId target)
            | Error _ -> None
        match getJobResultForProjectAsAgent otherProject (Some "Worker 1") cmd context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Wrong project must not read a tool operation job.")
    finally
        Directory.Delete(root, true)

[<Test>]
let quickToolOperationReturnsOriginalResponseWithoutJobPromotion () =
    let response = completedToolResponse """{"quick":true}"""
    let registered =
        match startToolOperation jobProject (Some "Project1") "Worker 1" "ReadFile"
                None "read-file-quick-payload" (fun () -> Task.FromResult response) with
        | Ok job -> job
        | Error error -> failwith $"Unable to register quick tool: {error}"
    match (awaitToolOperation registered (TimeSpan.FromSeconds 2.)).GetAwaiter().GetResult() with
    | Choice1Of2 actual -> Assert.That(actual, Is.EqualTo(response))
    | Choice2Of2 _ -> Assert.Fail("A completed fast operation must not return a job handle.")
    let handle, _ = registered
    Assert.That(isToolOperationJob handle.JobId, Is.False,
                "A quick call is discarded rather than cluttering list_jobs.")

[<Test>]
let boundedSignalRIngressDoesNotHeadOfLineBlockCheapRequests () =
    let errors = System.Collections.Concurrent.ConcurrentQueue<exn>()
    let ingress = BoundedResponseIngress(2, fun ex -> errors.Enqueue ex)
    let slow = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let first =
        match ingress.TryDispatch("slow", fun () -> slow.Task :> Task) with
        | Ok completion -> completion
        | Error error -> failwith error
    let quick = System.Diagnostics.Stopwatch.StartNew()
    let second =
        match ingress.TryDispatch("poll", fun () -> Task.CompletedTask) with
        | Ok completion -> completion
        | Error error -> failwith error
    second.WaitAsync(TimeSpan.FromSeconds(2.)).GetAwaiter().GetResult()
    quick.Stop()
    Assert.That(quick.ElapsedMilliseconds, Is.LessThan(500L),
                "Cheap polling must complete while the slow dispatch remains unresolved.")
    Assert.That(first.IsCompleted, Is.False)
    Assert.That(errors.IsEmpty, Is.True)
    slow.SetResult(())
    first.WaitAsync(TimeSpan.FromSeconds(2.)).GetAwaiter().GetResult()
    Assert.That(ingress.ActiveCount, Is.EqualTo(0))

[<Test>]
let boundedSignalRIngressRejectsSaturationAndDuplicateCorrelations () =
    let ingress = BoundedResponseIngress(2, fun _ -> ())
    let slowA = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let slowB = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let a =
        match ingress.TryDispatch("A", fun () -> slowA.Task :> Task) with
        | Ok completion -> completion
        | Error error -> failwith error
    let b =
        match ingress.TryDispatch("B", fun () -> slowB.Task :> Task) with
        | Ok completion -> completion
        | Error error -> failwith error
    Assert.That(ingress.ActiveCount, Is.EqualTo(2))
    match ingress.TryDispatch("A", fun () -> Task.CompletedTask) with
    | Error error -> Assert.That(error, Does.Contain("Duplicate"))
    | Ok _ -> Assert.Fail("A correlation cannot be executed twice.")
    match ingress.TryDispatch("C", fun () -> Task.CompletedTask) with
    | Error error -> Assert.That(error, Does.Contain("capacity"))
    | Ok _ -> Assert.Fail("Bounded ingress must reject saturation.")
    slowA.SetResult(())
    slowB.SetResult(())
    Task.WhenAll([|a; b|]).WaitAsync(TimeSpan.FromSeconds(2.)).GetAwaiter().GetResult()
    Assert.That(ingress.ActiveCount, Is.EqualTo(0))
    match ingress.TryDispatch("C", fun () -> Task.CompletedTask) with
    | Ok task -> task.WaitAsync(TimeSpan.FromSeconds(2.)).GetAwaiter().GetResult()
    | Error error -> Assert.Fail("Capacity was not returned after completion: " + error)


[<Test>]
let toolOperationRegistryBoundsRunningAndQueuedJobs () =
    let root = createTempProject ()
    let context = TestContext root
    let waiting = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
    let entered = System.Collections.Concurrent.ConcurrentQueue<int>()
    let entries = ResizeArray<int * ToolJobHandle * Task<AgentCommandResponse>>()
    let actor = Some "Worker 1"
    let invoke index =
        startToolOperation jobProject (Some "Project1") "Worker 1" "BlockedRead"
            (Some $"test-operation-{index}") $"blocked-payload-{index}" (fun () ->
                entered.Enqueue index
                waiting.Task)
    try
        for index in 1 .. 32 do
            match invoke index with
            | Ok (handle, completion) -> entries.Add(index, handle, completion)
            | Error error -> Assert.Fail($"Rejected admitted job {index}: {error}")
        Assert.That(
            SpinWait.SpinUntil((fun () -> entered.Count >= 8), TimeSpan.FromSeconds(8.)),
            Is.True, "Eight bounded worker slots should become occupied.")
        match invoke 33 with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("33rd running/queued operation bypassed capacity limit.")
        let queued =
            entries
            |> Seq.find (fun (index, _, _) -> not (entered.ToArray() |> Array.contains index))
        let canceledIndex, canceledHandle, canceledCompletion = queued
        let cmd = { ProjectName = "Project1"; JobId = canceledHandle.JobId }
        match cancelJobForProjectAsAgent jobProject actor cmd context with
        | Ok() -> ()
        | Error error -> Assert.Fail($"Unable to cancel a not-yet-started job: {error}")
        Assert.That(canceledCompletion.IsCompletedSuccessfully, Is.True)
        Assert.That(canceledCompletion.Result.Error.IsSome, Is.True)
        match invoke 34 with
        | Ok (handle, completion) -> entries.Add(34, handle, completion)
        | Error error -> Assert.Fail($"Canceled queued entry did not free capacity: {error}")
        // Mark a job exposed and verify listing/lookup is cheap while all
        // worker slots remain blocked by the original operations.
        let _, exposed, _ = entries[0]
        promoteToolOperation exposed.JobId
        let lookup = Stopwatch.StartNew()
        let result =
            getJobResultForProjectAsAgent jobProject actor
                { ProjectName = "Project1"; JobId = exposed.JobId; AfterSequence = None } context
        let visible =
            listJobsForProjectAsAgent jobProject actor
                { ProjectName = Some "Project1"; IncludeCompleted = true } context
        lookup.Stop()
        Assert.That(lookup.ElapsedMilliseconds, Is.LessThan(300L),
                    "Job polling/listing must not queue behind full worker slots.")
        match result with
        | Ok value -> Assert.That(value.Status, Is.EqualTo(JobStatus.Running))
        | Error error -> Assert.Fail($"Polling blocked job unexpectedly failed: {error}")
        match visible with
        | Ok list -> Assert.That(list.Jobs |> List.exists (fun j -> j.JobId = exposed.JobId), Is.True)
        | Error error -> Assert.Fail($"Job listing failed: {error}")
        Assert.That(entered.ToArray() |> Array.contains canceledIndex, Is.False)
        match cancelJobForProjectAsAgent jobProject actor
                  { ProjectName = "Project1"; JobId = exposed.JobId } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Executing tool operations must not be canceled after side effects may have begun.")
    finally
        waiting.TrySetResult(completedToolResponse """{"ok":true}""") |> ignore
        for _, _, completion in entries do
            completion.WaitAsync(TimeSpan.FromSeconds(10.)).GetAwaiter().GetResult() |> ignore
        Directory.Delete(root, true)


[<Test>]
let stableOperationIdDoesNotDispatchOriginalCommandTwice () =
    let operationId = "dedup-" + Guid.NewGuid().ToString("N")
    let original = completedToolResponse """{"finished":true}"""
    let executed = ref 0
    let work () =
        executed.Value <- executed.Value + 1
        Task.FromResult original
    let first =
        match startToolOperation jobProject (Some "Project1") "Worker 1" "LedgerWrite"
                  (Some operationId) "identical-request-hash" work with
        | Ok job -> job
        | Error error -> failwith $"Unable to start original: {error}"
    let second =
        match startToolOperation jobProject (Some "Project1") "Worker 1" "LedgerWrite"
                  (Some operationId) "identical-request-hash" work with
        | Ok job -> job
        | Error error -> failwith $"Unable to recover original: {error}"
    Assert.That(fst first, Is.EqualTo(fst second))
    let originalResponse =
        (snd first).WaitAsync(TimeSpan.FromSeconds(3.)).GetAwaiter().GetResult()
    Assert.That(originalResponse, Is.EqualTo(original))
    Assert.That((snd second).Result, Is.EqualTo(original))
    Assert.That(executed.Value, Is.EqualTo(1))
    match startToolOperation jobProject (Some "Project1") "Worker 1" "LedgerWrite"
              (Some operationId) "different-request-hash" work with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Reusing an operation ID with changed arguments must be rejected.")
    let replay =
        match startToolOperation jobProject (Some "Project1") "Worker 1" "LedgerWrite"
                  (Some operationId) "identical-request-hash" work with
        | Ok job -> job
        | Error error -> failwith $"Unable to replay completed original: {error}"
    match (awaitToolOperation replay (TimeSpan.FromSeconds(1.))).GetAwaiter().GetResult() with
    | Choice1Of2 response -> Assert.That(response, Is.EqualTo(original))
    | Choice2Of2 _ -> Assert.Fail("Completed idempotent retry must replay the original result.")
    Assert.That(executed.Value, Is.EqualTo(1))


[<Test>]
let challengeWriteUsesStableAutoJobAndRecoveryIdentity () =
    let operationId = Guid.NewGuid().ToString("D")
    let request : AgentCommand =
        ChallengeWriteCommand
            { ProjectName = "Project1"; ActorId = "Worker 1"
              OperationId = operationId; Action = "Cancel"
              ChallengeId = Guid.NewGuid().ToString("N"); ConditionId = ""
              ExpectedVersion = 1; Title = ""; TaskId = ""; AttemptId = ""
              DependencyChallengeIds = []; ExpectedTaskVersion = 0
              ExpectedAttemptVersion = 0; ExpectedAgentVersion = 0
              AssignmentGeneration = 0; Conditions = []
              Evidence = ""; Reason = "test"; Outcome = ""
              NewOwnerAgentId = ""; Subscribe = false }
    Assert.That(AgentCommandInfo.operationId request, Is.EqualTo(Some operationId))
    Assert.That(AgentCommandInfo.recoveryOperationId request, Is.EqualTo(Some operationId))
    let recovery = CommandDispatchRecovery.unexpectedResponse (
        System.Text.Json.JsonSerializer.Serialize request)
    match recovery.Error with
    | Some(UncertainOutcome info) ->
        Assert.That(info.OperationId, Is.EqualTo(Some operationId))
    | other -> Assert.Fail($"Challenge recovery lost original UUID: {other}")
    let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let release = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable calls = 0
    let execute () : Task<AgentCommandResponse> = task {
        calls <- calls + 1
        entered.TrySetResult(()) |> ignore
        return! release.Task
    }
    try
        let start () =
            startToolOperation jobProject (Some "Project1") "Worker 1"
                "ChallengeWrite" (AgentCommandInfo.operationId request) operationId execute
        let first =
            match start () with
            | Ok operation -> operation
            | Error issue -> failwith $"Could not register Challenge write: {issue}"
        entered.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        let retry =
            match start () with
            | Ok operation -> operation
            | Error issue -> failwith $"Could not recover Challenge write: {issue}"
        Assert.That(fst retry, Is.EqualTo(fst first),
                    "The same pending Challenge operation must resolve to one job.")
        match (awaitToolOperation first (TimeSpan.FromMilliseconds 5.)).GetAwaiter().GetResult() with
        | Choice2Of2 handle ->
            Assert.That(handle.OperationId, Is.EqualTo(Some operationId))
            Assert.That(handle.JobId, Is.EqualTo((fst retry).JobId))
        | _ -> Assert.Fail("Blocked Challenge write should return a recoverable job handle.")
        release.SetResult(completedToolResponse """{"ok":true}""")
        let terminal = (snd first).WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        Assert.That(terminal.Error.IsNone, Is.True)
        Assert.That(calls, Is.EqualTo(1),
                    "The same Challenge write must not execute twice while pending.")
    finally
        release.TrySetResult(completedToolResponse "{}") |> ignore

[<Test>]
let toolFailuresRemainUncertainWithOriginalOperationWithoutRestart () =
    let operationId = "failure-" + Guid.NewGuid().ToString("N")
    let calls = ref 0
    let registered =
        match startToolOperation jobProject (Some "Project1") "Worker 1" "ReadFile"
                (Some operationId) "throwing-command" (fun () ->
                    calls.Value <- calls.Value + 1
                    Task.FromException<AgentCommandResponse>(InvalidOperationException("synthetic command fault"))) with
        | Ok job -> job
        | Error error -> failwith $"Could not register faulting job: {error}"
    let first = (awaitToolOperation registered (TimeSpan.FromSeconds 2.)).GetAwaiter().GetResult()
    let outcome =
        match first with
        | Choice1Of2 response -> response
        | Choice2Of2 _ -> failwith "Faulting task should finish before the deadline."
    Assert.That(outcome.Error.IsSome, Is.True)
    match outcome.Error with
    | Some(UncertainOutcome info) ->
        Assert.That(info.Message, Does.Contain("synthetic command fault"))
        Assert.That(info.OperationId, Is.EqualTo(Some operationId))
    | other -> Assert.Fail($"Expected typed uncertain outcome after execution began: {other}")
    Assert.That(AgentStateEvidence.isUnknown outcome, Is.True)
    let handle, _ = registered
    let root = createTempProject ()
    try
        let context = TestContext root
        let result = getJobResultForProjectAsAgent jobProject (Some "Worker 1")
                         { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None } context
        match result with
        | Ok job ->
            match job.Status with
            | ToolFailed message -> Assert.That(message, Does.Contain("synthetic command fault"))
            | other -> Assert.Fail($"Incorrect terminal status: {other}")
            Assert.That(job.ToolResponse, Is.EqualTo(Some outcome))
        | Error error -> Assert.Fail($"Failed to retrieve faulting job: {error}")
    finally
        Directory.Delete(root, true)
    Assert.That(calls.Value, Is.EqualTo(1))

[<Test>]
let evictedToolJobsReturnExplicitExpiryAndBlockUnsafeRetry () =
    let originalId = "evict-" + Guid.NewGuid().ToString("N")
    let response = completedToolResponse """{"ok":true}"""
    let start op =
        match startToolOperation jobProject (Some "Project1") "Worker 1" "LedgerWrite"
                (Some op) op (fun () -> Task.FromResult response) with
        | Ok job -> job
        | Error error -> failwith $"Cannot start retention fixture: {error}"
    let first = start originalId
    (snd first).WaitAsync(TimeSpan.FromSeconds 4.) |> fun task -> task.GetAwaiter().GetResult() |> ignore
    for index in 1 .. 120 do
        let other = start ("evict-fixture-" + Guid.NewGuid().ToString("N") + "-" + string index)
        (snd other).WaitAsync(TimeSpan.FromSeconds 4.) |> fun task -> task.GetAwaiter().GetResult() |> ignore
    let root = createTempProject ()
    try
        let context = TestContext root
        let read = getJobResultForProjectAsAgent jobProject (Some "Worker 1")
                       { ProjectName = "Project1"; JobId = (fst first).JobId; AfterSequence = None } context
        match read with
        | Error(NotFoundError reason) ->
            Assert.That(reason, Does.Contain("expired"))
            Assert.That(reason, Does.Contain("operationId"))
        | other -> Assert.Fail($"Expected explicit expiry after bounded eviction: {other}")
        match startToolOperation jobProject (Some "Project1") "Worker 1" "LedgerWrite"
                  (Some originalId) originalId (fun () -> Task.FromResult response) with
        | Error(NotFoundError reason) -> Assert.That(reason, Does.Contain("Reconcile"))
        | other -> Assert.Fail($"Expired operation ID must not replay a write: {other}")
    finally
        Directory.Delete(root, true)

[<Test>]
let missingToolJobAfterClientRestartExplainsReconciliation () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let unknown = Guid.NewGuid().ToString("N")
        match getJobResultForProjectAsAgent jobProject (Some "Worker 1")
                  { ProjectName = "Project1"; JobId = unknown; AfterSequence = None } context with
        | Error(NotFoundError message) ->
            Assert.That(message, Does.Contain("restarted"))
            Assert.That(message, Does.Contain("operationId"))
        | other -> Assert.Fail($"Unknown client-owned job must explain restart/expiry: {other}")
    finally
        Directory.Delete(root, true)


let private notificationFixture (sourceChanges: AgentStateChange list)
                                (sourceReceipt: AgentInboxActivation option) =
    let original : TaskWriteCommand =
        { ProjectName = "Project1"; Action = "NotifyAgent"
          OperationId = Guid.NewGuid().ToString("D")
          TaskId = ""; AttemptId = ""; ActorId = "Worker 1"; OwnerId = ""
          Title = ""; Html = ""; ExpectedVersion = 0; ExpectedAgentVersion = 0
          ExpectedTaskVersion = 0; AssignmentGeneration = 0; Summary = ""
          NextAction = ""; Reason = ""; BaseOid = ""; TargetRef = ""
          SourceRef = "Jarvis"; AllowDetached = false; AllowUncommitted = false
          DecisionSequence = 0L; Evidence = ""; SessionId = "" }
    { Notification = original
      SourceStateChanges = sourceChanges
      SourceInboxActivation = sourceReceipt }

let private activationFixture () : AgentInboxActivation =
    { ProjectName = "Jarvis"; AgentId = "Worker 1"
      ActivatedByThisCall = true; BeforeState = Some "Ready"
      AfterState = Some "Working"; CommittedHead = Some(String('a', 40))
      OperationId = Some(Guid.NewGuid().ToString("D")) }

[<Test>]
let forwardedTransportRecoveryRetainsKnownSourceAndOriginalOperationId () =
    let source = [ { ProjectName = "Jarvis"; AgentId = "Worker 1" } ]
    let receipt = activationFixture ()
    let envelope = notificationFixture source (Some receipt)
    let request : AgentCommand = CrossProjectNotificationCommand envelope
    let operationId = envelope.Notification.OperationId
    Assert.That(AgentCommandInfo.recoveryOperationId request,
                Is.EqualTo(Some operationId))
    let response = CommandDispatchRecovery.unexpectedResponse (
        System.Text.Json.JsonSerializer.Serialize request)
    Assert.That(AgentStateEvidence.isUnknown response, Is.True)
    match response.Error with
    | Some(UncertainOutcome info) ->
        Assert.That(info.OperationId, Is.EqualTo(Some operationId))
    | other -> Assert.Fail($"Forwarded recovery lost original write ID: {other}")
    Assert.That(response.AgentStateChanges, Is.EqualTo(source :> obj))
    Assert.That(response.InboxActivation, Is.EqualTo(Some receipt))
    let restored =
        System.Text.Json.JsonSerializer.Deserialize<AgentCommandResponse>(
            System.Text.Json.JsonSerializer.Serialize response)
    Assert.That(AgentStateEvidence.isUnknown restored, Is.True)
    Assert.That(restored.AgentStateChanges, Is.EqualTo(source :> obj))
    Assert.That(restored.InboxActivation, Is.EqualTo(Some receipt))

[<Test>]
let forwardedNotificationJobKeepsSourceAndDestinationEvidenceThroughTerminalPolling () =
    let root = createTempProject ()
    let context = TestContext root
    try
        for sourceChanged in [false; true] do
            for outcomeIsFailure in [false; true] do
                let source = if sourceChanged then
                                 [ { ProjectName = "Jarvis"; AgentId = "Worker 1" } ]
                             else []
                let receipt = if sourceChanged then Some(activationFixture()) else None
                let envelope = notificationFixture source receipt
                let merge = Client.SignalR.Client.combineCrossProjectNotification envelope
                let deferred =
                    TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
                let calls = ref 0
                let registered =
                    match startToolOperationWithCancelEvidence jobProject (Some "Project1") "Worker 1"
                            "TaskWrite" (Some envelope.Notification.OperationId)
                            envelope.Notification.OperationId
                            (fun () ->
                                task {
                                    calls.Value <- calls.Value + 1
                                    let! destination = deferred.Task
                                    return merge destination
                                }) merge with
                    | Ok job -> job
                    | Error error -> failwith $"Cannot admit forwarded notification: {error}"
                let pending = (awaitToolOperation registered (TimeSpan.FromMilliseconds 20.)).GetAwaiter().GetResult()
                let handle =
                    match pending with
                    | Choice2Of2 value -> value
                    | _ -> failwith "Unfinished destination must return pending handle."
                let cmd = { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None }
                match getJobResultForProjectAsAgent jobProject (Some "Worker 1") cmd context with
                | Ok value ->
                    Assert.That(value.Status, Is.EqualTo(JobStatus.Running))
                    Assert.That(value.ToolResponse.IsNone, Is.True)
                | Error issue -> Assert.Fail($"Unable to poll pending destination: {issue}")
                let destination =
                    { Result = if outcomeIsFailure then None else Some """{"notified":true}"""
                      Error = if outcomeIsFailure then Some(Conflict "destination failed") else None
                      Instructions = []
                      AgentStateChanges = [ { ProjectName = "Project1"; AgentId = "Receiver" } ]
                      InboxActivation = None; PendingJob = None }
                deferred.SetResult destination
                let _, completion = registered
                let terminal = completion.WaitAsync(TimeSpan.FromSeconds 4.).GetAwaiter().GetResult()
                Assert.That(terminal.AgentStateChanges.Length, Is.EqualTo(if sourceChanged then 2 else 1))
                Assert.That(terminal.InboxActivation, Is.EqualTo(receipt))
                Assert.That(terminal.Error.IsSome, Is.EqualTo(outcomeIsFailure))
                for _ in 1 .. 3 do
                    match getJobResultForProjectAsAgent jobProject (Some "Worker 1") cmd context with
                    | Ok value ->
                        Assert.That(value.ToolResponse, Is.EqualTo(Some terminal))
                        Assert.That(value.Status,
                            Is.EqualTo(if outcomeIsFailure then ToolFailed "Conflict \"destination failed\""
                                       else ToolCompleted))
                    | Error issue -> Assert.Fail($"Unable to poll finished destination: {issue}")
                let replay =
                    startToolOperationWithCancelEvidence jobProject (Some "Project1") "Worker 1"
                        "TaskWrite" (Some envelope.Notification.OperationId) envelope.Notification.OperationId
                        (fun () ->
                            calls.Value <- calls.Value + 1
                            Task.FromResult(merge destination)) merge
                match replay with
                | Ok (same, _) -> Assert.That(same.JobId, Is.EqualTo(handle.JobId))
                | Error issue -> Assert.Fail($"Stable operation ID could not recover original: {issue}")
                Assert.That(calls.Value, Is.EqualTo(1),
                    "Polling and retrying a forwarded notification must not execute a duplicate destination mutation.")
    finally
        Directory.Delete(root, true)

[<Test>]
let forwardedNotificationFaultAfterMutationRetainsUnknownStateAndOriginalOperation () =
    let root = createTempProject ()
    let context = TestContext root
    let source = [ { ProjectName = "Jarvis"; AgentId = "Worker 1" } ]
    let receipt = Some(activationFixture())
    let envelope = notificationFixture source receipt
    let merge = Client.SignalR.Client.combineCrossProjectNotification envelope
    let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable sideEffects = 0
    let execute () : Task<AgentCommandResponse> = task {
        sideEffects <- sideEffects + 1
        entered.TrySetResult(()) |> ignore
        do! release.Task
        return raise (InvalidOperationException("fault after destination write"))
    }
    try
        let registered =
            match startToolOperationWithCancelEvidence jobProject (Some "Project1") "Worker 1"
                    "TaskWrite" (Some envelope.Notification.OperationId)
                    envelope.Notification.OperationId execute merge with
            | Ok operation -> operation
            | Error error -> failwith $"Cannot start one-shot mutation fixture: {error}"
        let handle, completion = registered
        entered.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        match (awaitToolOperation registered (TimeSpan.FromMilliseconds 5.)).GetAwaiter().GetResult() with
        | Choice2Of2 pending -> Assert.That(pending.JobId, Is.EqualTo(handle.JobId))
        | other -> Assert.Fail($"Gated, executing command must return pending handle: {other}")
        let cmd = { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None }
        match getJobResultForProjectAsAgent jobProject (Some "Worker 1") cmd context with
        | Ok pending ->
            Assert.That(pending.Status, Is.EqualTo(JobStatus.Running))
            Assert.That(pending.ToolResponse.IsNone, Is.True, "Pending effects must remain unknown.")
        | Error issue -> Assert.Fail($"Cannot poll pending mutation: {issue}")
        release.SetResult(())
        let terminal = completion.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        Assert.That(sideEffects, Is.EqualTo(1))
        match terminal.Error with
        | Some(UncertainOutcome info) ->
            Assert.That(info.OperationId, Is.EqualTo(Some envelope.Notification.OperationId))
            Assert.That(info.Message, Does.Contain("fault after destination write"))
        | other -> Assert.Fail($"Post-execution exception must be uncertain: {other}")
        Assert.That(AgentStateEvidence.isUnknown terminal, Is.True)
        Assert.That(terminal.AgentStateChanges, Is.EqualTo(source :> obj))
        Assert.That(terminal.InboxActivation, Is.EqualTo(receipt))
        let roundtrip =
            System.Text.Json.JsonSerializer.Deserialize<AgentCommandResponse>(
                System.Text.Json.JsonSerializer.Serialize terminal)
        Assert.That(AgentStateEvidence.isUnknown roundtrip, Is.True)
        for _ in 1 .. 2 do
            match getJobResultForProjectAsAgent jobProject (Some "Worker 1") cmd context with
            | Ok stored ->
                match stored.Status with
                | ToolFailed reason -> Assert.That(reason, Does.Contain("fault after destination write"))
                | other -> Assert.Fail($"Unexpected terminal status: {other}")
                Assert.That(stored.ToolResponse, Is.EqualTo(Some terminal))
            | Error issue -> Assert.Fail($"Cannot recover uncertain job: {issue}")
        match startToolOperationWithCancelEvidence jobProject (Some "Project1") "Worker 1"
                "TaskWrite" (Some envelope.Notification.OperationId)
                envelope.Notification.OperationId execute merge with
        | Ok (same, _) -> Assert.That(same.JobId, Is.EqualTo(handle.JobId))
        | Error issue -> Assert.Fail($"Original operation ID must recover its result: {issue}")
        Assert.That(sideEffects, Is.EqualTo(1), "An uncertain mutation must not be executed twice.")
    finally
        release.TrySetResult(()) |> ignore
        Directory.Delete(root, true)

[<Test>]
let cancelingQueuedForwardedNotificationPreservesCommittedSourceActivation () =
    let root = createTempProject ()
    let context = TestContext root
    let wait = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
    let entered = System.Collections.Concurrent.ConcurrentQueue<int>()
    let blockers = ResizeArray<ToolJobHandle * Task<AgentCommandResponse>>()
    try
        for index in 1 .. 8 do
            match startToolOperation jobProject (Some "Project1") "Worker 1" "BlockingRead"
                    (Some ("cancel-evidence-blocker-" + Guid.NewGuid().ToString("N")))
                    (string index) (fun () ->
                        entered.Enqueue index
                        wait.Task) with
            | Ok item -> blockers.Add(item)
            | Error issue -> Assert.Fail($"Unable to fill worker slots: {issue}")
        Assert.That(SpinWait.SpinUntil((fun () -> entered.Count = 8), TimeSpan.FromSeconds(5.)), Is.True)
        let source = [ { ProjectName = "Jarvis"; AgentId = "Worker 1" } ]
        let receipt = Some(activationFixture())
        let envelope = notificationFixture source receipt
        let merge = Client.SignalR.Client.combineCrossProjectNotification envelope
        let mutable dispatched = 0
        let queued =
            match startToolOperationWithCancelEvidence jobProject (Some "Project1") "Worker 1"
                    "TaskWrite" (Some envelope.Notification.OperationId)
                    envelope.Notification.OperationId
                    (fun () ->
                        dispatched <- dispatched + 1
                        Task.FromResult(merge (completedToolResponse "{}"))) merge with
            | Ok job -> job
            | Error issue -> failwith $"Cannot queue notification: {issue}"
        let handle, completion = queued
        promoteToolOperation handle.JobId
        match cancelJobForProjectAsAgent jobProject (Some "Worker 1")
                  { ProjectName = "Project1"; JobId = handle.JobId } context with
        | Ok () -> ()
        | Error issue -> Assert.Fail($"Cannot cancel queued notification: {issue}")
        let canceled = completion.WaitAsync(TimeSpan.FromSeconds(3.)).GetAwaiter().GetResult()
        Assert.That(canceled.Error.IsSome, Is.True)
        Assert.That(canceled.AgentStateChanges, Is.EqualTo(source :> obj))
        Assert.That(canceled.InboxActivation, Is.EqualTo(receipt))
        Assert.That(dispatched, Is.EqualTo(0))
        for _ in 1 .. 2 do
            match getJobResultForProjectAsAgent jobProject (Some "Worker 1")
                      { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None } context with
            | Ok job ->
                Assert.That(job.Status, Is.EqualTo(JobStatus.Canceled))
                Assert.That(job.ToolResponse, Is.EqualTo(Some canceled))
            | Error issue -> Assert.Fail($"Cannot recover canceled job: {issue}")
    finally
        wait.TrySetResult(completedToolResponse "{}") |> ignore
        for _, completion in blockers do
            completion.WaitAsync(TimeSpan.FromSeconds(6.)).GetAwaiter().GetResult() |> ignore
        Directory.Delete(root, true)

[<Test>]
let ``CancelJob records process cancellation only after exit callback`` () =
    let root = createTempProject()
    try
        let project = Path.Combine(root, "Project1")
        File.WriteAllText(
            Path.Combine(project, "cancellation.fsx"),
            """open System
open System.Threading
printfn "job-is-running"
Console.Out.Flush()
Thread.Sleep(TimeSpan.FromSeconds(45.0))
            """)
        let context = TestContext root
        let start =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "cancellation.fsx" ]
              Reason = Some "Verify cancellation terminal transition"
              WorkingDirectory = None
              MaxOutputBytes = Some 8192 }
        let jobId =
            match startJob start context with
            | Ok value -> value.JobId
            | Error error -> failwithf "Cannot start cancellation fixture: %A" error
        let inspect () =
            getJobResult
                { ProjectName = "Project1"; JobId = jobId; AfterSequence = Some 0L }
                context
            |> function
               | Ok value -> value
               | Error error -> failwithf "Cannot observe cancellation fixture: %A" error
        let stopwatch = Stopwatch.StartNew()
        let rec awaitRunning () =
            let result = inspect()
            if result.Events |> List.exists (fun e -> e.Text.Contains("job-is-running")) then
                Assert.That(result.Status, Is.EqualTo(Running))
            elif stopwatch.Elapsed > TimeSpan.FromSeconds(30.0) then
                Assert.Fail("Cancellation fixture did not reach running phase.")
            else
                Thread.Sleep 30
                awaitRunning ()
        try
            awaitRunning ()
            cancelJob { ProjectName = "Project1"; JobId = jobId } context
            |> shouldEqual (Ok ())
            // Duplicate cancel requests are safe even when an Exited
            // callback races the first request.
            cancelJob { ProjectName = "Project1"; JobId = jobId } context
            |> shouldEqual (Ok ())
            let rec awaitTerminal () =
                let result = inspect()
                match result.Status with
                | Running when stopwatch.Elapsed < TimeSpan.FromSeconds(35.0) ->
                    let summary =
                        listJobs
                            { ProjectName = Some "Project1"; IncludeCompleted = true }
                            context
                        |> function
                           | Ok jobs -> jobs.Jobs |> List.find (fun j -> j.JobId = jobId)
                           | Error error -> failwithf "Cannot inspect active job: %A" error
                    // The first GetJobResult and this ListJobs snapshot are
                    // separate observations: Exited may run between them.
                    // Check the status and timestamp from ONE atomic summary.
                    match summary.Status with
                    | Running ->
                        Assert.That(summary.CompletedAt.IsNone, Is.True,
                                    "A cancellation request cannot publish a terminal timestamp.")
                    | Canceled ->
                        Assert.That(summary.CompletedAt.IsSome, Is.True)
                    | other -> Assert.Fail($"Unexpected cancellation status: {other}")
                    Thread.Sleep 25
                    awaitTerminal ()
                | Canceled ->
                    let summary =
                        listJobs
                            { ProjectName = Some "Project1"; IncludeCompleted = true }
                            context
                        |> function
                           | Ok jobs -> jobs.Jobs |> List.find (fun j -> j.JobId = jobId)
                           | Error error -> failwithf "Cannot inspect canceled job: %A" error
                    Assert.That(summary.CompletedAt.IsSome, Is.True,
                                "Canceled must be an observed process exit, not just a kill request.")
                | other ->
                    Assert.Fail($"Cancellation did not resolve to an observed parent exit: {other}")
            awaitTerminal ()
        finally
            cancelJob { ProjectName = "Project1"; JobId = jobId } context
            |> ignore
    finally
        Directory.Delete(root, true)

let private childIsExecuting pid =
    try
        use proc = Process.GetProcessById pid
        if proc.HasExited then false
        elif OperatingSystem.IsLinux() then
            // A reparented zombie may exist in /proc briefly, but it cannot
            // execute more work and must not make this test nondeterministic.
            let stat = File.ReadAllText($"/proc/{pid}/stat")
            let state = stat[stat.LastIndexOf(')') + 2]
            state <> 'Z' && state <> 'X'
        else true
    with
    | :? ArgumentException
    | :? InvalidOperationException
    | :? FileNotFoundException
    | :? IOException -> false

let private killTestChild pid =
    try
        use proc = Process.GetProcessById pid
        if not proc.HasExited then proc.Kill(entireProcessTree = true)
    with _ -> ()

let private verifyStartJobDescendantContainment holdLeader =
    let root = createTempProject()
    let project = Path.Combine(root, "Project1")
    let marker = Path.Combine(project, "spawned-child.pid")
    let releaseMarker = Path.Combine(project, "allow-parent-exit")
    let childScript = Path.Combine(project, "spawned-child.fsx")
    let parentScript = Path.Combine(project, "spawning-parent.fsx")
    let context = TestContext root
    let mutable jobId = None
    try
        File.WriteAllText(childScript, $"""open System
open System.IO
open System.Threading
File.WriteAllText(@"{marker}", string Environment.ProcessId)
Thread.Sleep 30000
        """)
        let continuation =
            if holdLeader then "Thread.Sleep 30000"
            else $"while not (File.Exists(@\"{releaseMarker}\")) do Thread.Sleep 25"
        File.WriteAllText(parentScript, $"""open System
open System.Diagnostics
open System.IO
open System.Threading
let child = ProcessStartInfo("dotnet")
child.UseShellExecute <- false
child.ArgumentList.Add("fsi")
child.ArgumentList.Add("--exec")
child.ArgumentList.Add(@"{childScript}")
// Don't keep the outer job's redirected pipes open in the descendant.
child.RedirectStandardOutput <- true
child.RedirectStandardError <- true
use process = Process.Start(child)
let limit = DateTime.UtcNow.AddSeconds 15.0
while not (File.Exists(@"{marker}")) && DateTime.UtcNow < limit do
    Thread.Sleep 25
if not (File.Exists(@"{marker}")) then failwith "Child failed to start."
printfn "spawning parent active"
Console.Out.Flush()
{continuation}
        """)
        let start =
            { ProjectName = "Project1"; Executable = "dotnet"
              Args = [ "fsi"; "--exec"; "spawning-parent.fsx" ]
              Reason = Some "Verify isolated StartJob descendants"
              WorkingDirectory = None; MaxOutputBytes = Some 4096 }
        let launched =
            match startJob start context with
            | Ok result -> result
            | Error issue -> failwithf "StartJob failed: %A" issue
        jobId <- Some launched.JobId
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        while not (File.Exists marker) && DateTime.UtcNow < deadline do
            Thread.Sleep 30
        Assert.That(File.Exists marker, Is.True, "Child launch marker is required.")
        let pid = int (File.ReadAllText marker)
        Assert.That(childIsExecuting pid, Is.True,
                    "The child must be genuinely alive before group cleanup.")
        if holdLeader then
            cancelJob { ProjectName = "Project1"; JobId = launched.JobId } context
            |> shouldEqual (Ok ())
        else
            File.WriteAllText(releaseMarker, "exit")
        let rec awaitTerminal () =
            let response =
                getJobResult
                    { ProjectName = "Project1"; JobId = launched.JobId
                      AfterSequence = Some 0L } context
                |> function Ok value -> value
                          | Error issue -> failwithf "GetJobResult failed: %A" issue
            if response.Status = Running && DateTime.UtcNow < deadline then
                Thread.Sleep 30
                awaitTerminal ()
            else response.Status
        let status = awaitTerminal()
        if holdLeader then status |> shouldEqual Canceled
        else status |> shouldEqual (Completed 0)
        let cleanupDeadline = DateTime.UtcNow.AddSeconds 4.0
        while childIsExecuting pid && DateTime.UtcNow < cleanupDeadline do
            Thread.Sleep 30
        Assert.That(childIsExecuting pid, Is.False,
                    "StartJob must tear down descendants, even after the leader exits.")
    finally
        jobId |> Option.iter (fun id ->
            cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore)
        if File.Exists marker then
            match Int32.TryParse(File.ReadAllText marker) with
            | true, pid -> killTestChild pid
            | _ -> ()
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let startJobCleansUpProcessGroupAfterParentExit () =
    verifyStartJobDescendantContainment false

[<Test>]
let cancelJobKillsSupervisedDescendants () =
    verifyStartJobDescendantContainment true

[<Test>]
let rapidConcurrentStartJobsDoNotLoseLaunchOrExitHandoff () =
    let root = createTempProject()
    let context = TestContext root
    let mutable started = Array.empty<string>
    try
        // Short-lived command completion can race Process.Start, the Windows
        // readiness-gate assignment and async stdout reader initialization.
        // Exercise multiple launchers so an Exited event must not dispose a
        // shared supervisor before its launcher has transferred ownership.
        let start =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "--version" ]
              Reason = Some "Exercise StartJob launch and exit handoff"
              WorkingDirectory = None
              MaxOutputBytes = Some 1024 }
        let concurrent =
            [| for _ in 1 .. 24 ->
                Task.Run(fun () ->
                    match startJob start context with
                    | Ok result -> result.JobId
                    | Error error -> failwithf "A fast StartJob launch was lost: %A" error) |]
        Task.WaitAll(concurrent |> Array.map (fun task -> task :> Task))
        started <- concurrent |> Array.map (fun task -> task.Result)
        let deadline = Stopwatch.StartNew()
        let getStatus jobId =
            match getJobResult
                { ProjectName = "Project1"; JobId = jobId; AfterSequence = None } context with
            | Ok result -> result.Status
            | Error error -> failwithf "Fast StartJob handle disappeared: %A" error
        while started |> Array.exists (fun jobId -> getStatus jobId = Running)
              && deadline.Elapsed < TimeSpan.FromSeconds(30.0) do
            Thread.Sleep 25
        for jobId in started do
            Assert.That(getStatus jobId, Is.EqualTo(Completed 0),
                        "The launch/exit handoff must preserve every fast job's terminal result.")
            cancelJob { ProjectName = "Project1"; JobId = jobId } context
            |> shouldEqual (Ok ())
            Assert.That(getStatus jobId, Is.EqualTo(Completed 0),
                        "Canceling after observed exit cannot rewrite Completed as Canceled.")
    finally
        for jobId in started do
            cancelJob { ProjectName = "Project1"; JobId = jobId } context |> ignore
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let cancellationRacingShortJobExitIsIdempotent () =
    let root = createTempProject()
    let context = TestContext root
    let mutable started = Array.empty<string>
    try
        let command =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "--version" ]
              Reason = Some "Exercise cancellation vs observed exit"
              WorkingDirectory = None
              MaxOutputBytes = Some 1024 }
        let cancellationTasks =
            [| for _ in 1 .. 12 ->
                Task.Run(fun () ->
                    let jobId =
                        match startJob command context with
                        | Ok result -> result.JobId
                        | Error error -> failwithf "Fast job failed to start: %A" error
                    let first =
                        cancelJob { ProjectName = "Project1"; JobId = jobId } context
                    let second =
                        cancelJob { ProjectName = "Project1"; JobId = jobId } context
                    jobId, first, second) |]
        Task.WaitAll(cancellationTasks |> Array.map (fun task -> task :> Task))
        let results = cancellationTasks |> Array.map (fun task -> task.Result)
        started <- results |> Array.map (fun (jobId, _, _) -> jobId)
        for _, first, second in results do
            first |> shouldEqual (Ok ())
            second |> shouldEqual (Ok ())
        let timer = Stopwatch.StartNew()
        let status id =
            match getJobResult
                { ProjectName = "Project1"; JobId = id; AfterSequence = None } context with
            | Ok result -> result.Status
            | Error error -> failwithf "Unexpected lost job: %A" error
        while (started |> Array.exists (fun jobId -> status jobId = Running))
              && timer.Elapsed < TimeSpan.FromSeconds(30.0) do
            Thread.Sleep 25
        for id in started do
            match status id with
            | Completed 0 | Canceled -> ()
            | unexpected -> Assert.Fail($"A racing cancellation must observe parent exit: {unexpected}")
    finally
        for id in started do
            cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let processOwnershipJournalSurvivesRestartStyleInspectionAndRejectsReusedStages () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-process-proof-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let id = Guid.NewGuid().ToString("N")
        let intent = ProcessOwnershipJournal.beginLaunchAt root id "project-canonical" "device-verified"
        let before = ProcessOwnershipJournal.inspectAt root id
        match before with
        | Ok evidence ->
            evidence.Intent |> shouldEqual intent
            evidence.Launch.IsNone |> shouldEqual true
        | Error error -> Assert.Fail(error)

        // An unknown spawn outcome after a client crash remains unresolved.
        let replayRejected =
            try
                ProcessOwnershipJournal.beginLaunchAt root id "other-project" "device-verified" |> ignore
                false
            with :? IOException -> true
        replayRejected |> shouldEqual true

        use child = new Process()
        child.StartInfo.FileName <- "dotnet"
        child.StartInfo.ArgumentList.Add("--version")
        child.StartInfo.UseShellExecute <- false
        child.StartInfo.RedirectStandardOutput <- true
        child.StartInfo.RedirectStandardError <- true
        try
            Assert.That(child.Start(), Is.True)
            let recorded = ProcessOwnershipJournal.recordLaunchedAt root intent child
            Assert.That(recorded.ProcessId, Is.EqualTo(child.Id))
            Assert.That(recorded.JobId, Is.EqualTo(id))
            Assert.That(recorded.IntentSha256, Has.Length.EqualTo(64))
            match ProcessOwnershipJournal.inspectAt root id with
            | Ok recovered ->
                recovered.Intent |> shouldEqual intent
                recovered.Launch |> shouldEqual (Some recorded)
            | Error error -> Assert.Fail(error)
            child.WaitForExit(10000) |> ignore
            let secondLaunchRejected =
                try
                    ProcessOwnershipJournal.recordLaunchedAt root intent child |> ignore
                    false
                with :? IOException -> true
            secondLaunchRejected |> shouldEqual true
        finally
            try if not child.HasExited then child.Kill(entireProcessTree = true)
            with _ -> ()
    finally
        Directory.Delete(root, true)

[<Test>]
let processOwnershipJournalRejectsCorruptionAndSymlinkStage () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-process-corrupt-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let id = Guid.NewGuid().ToString("N")
        let _intent = ProcessOwnershipJournal.beginLaunchAt root id "project" "device"
        let intentPath = Path.Combine(root, id + ".intent.json")
        let original = File.ReadAllText(intentPath)
        File.WriteAllText(intentPath, "{")
        match ProcessOwnershipJournal.inspectAt root id with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Torn process ownership intent was accepted.")
        File.WriteAllText(intentPath, original)
        let launchedPath = Path.Combine(root, id + ".launched.json")
        File.WriteAllText(launchedPath, "{\"SchemaVersion\":1,\"JobId\":\"invalid\"}")
        match ProcessOwnershipJournal.inspectAt root id with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Mismatched launched PID/intent evidence was accepted.")
        File.Delete(launchedPath)
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.CreateSymbolicLink(launchedPath, intentPath) |> ignore
            match ProcessOwnershipJournal.inspectAt root id with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Symbolic-link evidence stage was accepted.")
    finally
        Directory.Delete(root, true)

[<Test>]
let processOwnershipEvidenceWireReadRejectsForeignDeviceAndPreservesUncertainty () =
    let root = createTempProject()
    let directory = ProcessOwnershipJournal.defaultDirectory()
    let foreignId = Guid.NewGuid().ToString("N")
    let unresolvedId = Guid.NewGuid().ToString("N")
    try
        // These are test-only immutable stages, removed by the test fixture.
        // No OS process was started; neither stage can imply termination.
        ProcessOwnershipJournal.beginLaunchAt directory foreignId "Project1" "foreign-device"
        |> ignore
        ProcessOwnershipJournal.beginLaunchAt directory unresolvedId "Project1" (DeviceIdentity.getOrCreate())
        |> ignore
        let context = TestContext root
        let query jobId : GetProcessOwnershipEvidenceCommand =
            { ProjectName = "Project1"; JobId = jobId }

        match getProcessOwnershipEvidence (query foreignId) context with
        | Error(ContextError reason) ->
            Assert.That(reason, Does.Contain("device mismatch"))
        | other -> Assert.Fail($"Foreign-device process identity leaked: {other}")

        match getProcessOwnershipEvidence (query unresolvedId) context with
        | Ok evidence ->
            evidence.JobId |> shouldEqual unresolvedId
            evidence.Stage |> shouldEqual "IntentOnly"
            evidence.ProcessId |> shouldEqual None
            evidence.ProcessStartUtc |> shouldEqual None
            evidence.IntentSha256 |> shouldEqual None
            evidence.ProcessTreeStatus |> shouldEqual "Unverified"
        | Error error -> Assert.Fail($"Missing launched stage was incorrectly rejected: {error}")

        for bad in [ Guid.NewGuid().ToString("N"); "not-a-job-uuid" ] do
            match getProcessOwnershipEvidence (query bad) context with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Missing or malformed process identity was treated as available.")

        match getProcessOwnershipEvidenceForProject None (query unresolvedId) context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Unscoped local process evidence was disclosed.")
    finally
        // Only our unique test fixture UUID files; no production journal
        // retention or cleanup policy is implied.
        for id in [ foreignId; unresolvedId ] do
            let file = Path.Combine(directory, id + ".intent.json")
            if File.Exists file then File.Delete file
        Directory.Delete(root, true)


[<Test>]
let linuxNativeJobScopeParsesOnlyExplicitKernelPopulation () =
    Assert.That(LinuxJobScope.inspectEvents "populated 0\nfrozen 0\n",
                Is.EqualTo(LinuxJobScope.Empty))
    Assert.That(LinuxJobScope.inspectEvents "populated 1\nfrozen 0\n",
                Is.EqualTo(LinuxJobScope.Occupied))
    for invalid in [ ""; "frozen 0"; "populated 2"; "populated -1";
                     "populated 0\npopulated 0"; "populated 0\npopulated 1";
                     "populated"; "populated 0 1";
                     "populated 0\npopulated 1 extra";
                     "populated 0\npopulated";
                     "populated 1\npopulated -1";
                     "populated 0\npopulated\t1 extra" ] do
        match LinuxJobScope.inspectEvents invalid with
        | LinuxJobScope.Unverified _ -> ()
        | result -> Assert.Fail($"Malformed kernel scope occupancy was accepted: {result}")

    let forged : LinuxJobScope.Identity =
        { JobId = Guid.NewGuid().ToString("N")
          Directory = Path.GetTempPath()
          BootId = Guid.NewGuid().ToString("D") }
    match LinuxJobScope.inspect forged with
    | LinuxJobScope.Unverified _ -> ()
    | result -> Assert.Fail($"A non-cgroup path was treated as kernel-owned: {result}")
    match LinuxJobScope.tryKill forged with
    | Error _ -> ()
    | Ok () -> Assert.Fail("A forged kernel scope was accepted for termination.")


[<Test; NonParallelizable>]
let linuxNativeStartJobScopeAttachesBeforeExecAndRemainsDiagnostic () =
    if not (OperatingSystem.IsLinux()) then Assert.Ignore("Linux cgroup-v2 only.")
    let prior = Environment.GetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2")
    let root = createTempProject()
    let mutable jobId : string option = None
    let mutable nativeDirectory : string option = None
    try
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", "1")
        // On hosts without delegated cgroup creation the feature must refuse
        // admission; do not report a false native reaping guarantee.
        let probeId = Guid.NewGuid().ToString("N")
        let probe =
            try LinuxJobScope.create probeId
            with
            | :? UnauthorizedAccessException
            | :? IOException
            | :? InvalidOperationException ->
                Assert.Ignore("Kernel cgroup-v2 delegation unavailable.")
                None
        probe |> Option.iter (fun scope -> Directory.Delete scope.Directory)
        let context = TestContext root
        let command =
            { ProjectName = "Project1"; Executable = "dotnet"
              Args = [ "--version" ]; Reason = Some "Native scope admission test"
              WorkingDirectory = None; MaxOutputBytes = Some 2048 }
        let started =
            match startJob command context with
            | Ok result -> result
            | Error error -> failwithf "Opt-in native StartJob did not start: %A" error
        jobId <- Some started.JobId
        let recovered =
            ProcessOwnershipJournal.inspectAt
                (ProcessOwnershipJournal.defaultDirectory()) started.JobId
            |> function
               | Ok evidence -> evidence
               | Error problem -> failwithf "Native scope journal unreadable: %s" problem
        let stage =
            recovered.NativeScope
            |> Option.defaultWith (fun () -> failwith "Native scope stage was not durably recorded.")
        nativeDirectory <- Some stage.LinuxCgroupPath
        Assert.That(stage.JobId, Is.EqualTo(started.JobId))
        Assert.That(recovered.Launch.IsSome, Is.True)
        let identity : LinuxJobScope.Identity =
            { JobId = started.JobId
              Directory = stage.LinuxCgroupPath; BootId = stage.LinuxBootId }
        let deadline = DateTimeOffset.UtcNow.AddSeconds(15.)
        let mutable population = LinuxJobScope.inspect identity
        while population <> LinuxJobScope.Empty && DateTimeOffset.UtcNow < deadline do
            Thread.Sleep 50
            population <- LinuxJobScope.inspect identity
        Assert.That(population, Is.EqualTo(LinuxJobScope.Empty),
            "The cgroup must be empty after the OS reaps the launch helper and descendants.")
        match getProcessOwnershipEvidence
                  { ProjectName = "Project1"; JobId = started.JobId } context with
        | Ok evidence ->
            Assert.That(evidence.ProcessTreeStatus, Is.EqualTo("Unverified"),
                "Even an empty kernel cgroup cannot authorize arbitrary descendant-escape proofs.")
        | Error error -> Assert.Fail($"Native process evidence could not be read: {error}")
    finally
        jobId |> Option.iter (fun id ->
            try cancelJob { ProjectName = "Project1"; JobId = id } (TestContext root) |> ignore
            with _ -> ())
        nativeDirectory |> Option.iter (fun path ->
            try Directory.Delete path with _ -> ())
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", prior)
        if Directory.Exists root then Directory.Delete(root, true)


[<Test; NonParallelizable>]
let linuxNativeStartJobScopeRetainsAndKillsDescendants () =
    if not (OperatingSystem.IsLinux()) then Assert.Ignore("Linux cgroup-v2 only.")
    if not (File.Exists "/usr/bin/python3") then
        Assert.Ignore("Python3 is required for this native containment fixture.")
    let prior = Environment.GetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2")
    let root = createTempProject()
    let context = TestContext root
    let marker = Path.Combine(root, "Project1", "native-child.pid")
    let mutable jobId : string option = None
    let mutable nativeDirectory : string option = None
    try
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", "1")
        let probe =
            try LinuxJobScope.create (Guid.NewGuid().ToString("N"))
            with
            | :? UnauthorizedAccessException
            | :? IOException
            | :? InvalidOperationException ->
                Assert.Ignore("Kernel cgroup-v2 delegation unavailable.")
                None
        probe |> Option.iter (fun scope -> Directory.Delete scope.Directory)
        let python =
            "import subprocess,time,pathlib; " +
            "p=subprocess.Popen(['/usr/bin/sleep','30']); " +
            "pathlib.Path('native-child.pid').write_text(str(p.pid)); " +
            "time.sleep(30)"
        let cmd =
            { ProjectName = "Project1"; Executable = "/usr/bin/python3"
              Args = [ "-c"; python ]; Reason = Some "Verify native descendant containment"
              WorkingDirectory = None; MaxOutputBytes = Some 2048 }
        let started =
            match startJob cmd context with
            | Ok result -> result
            | Error error -> failwithf "Scoped StartJob launch failed: %A" error
        jobId <- Some started.JobId
        let journal =
            ProcessOwnershipJournal.inspectAt
                (ProcessOwnershipJournal.defaultDirectory()) started.JobId
            |> function
               | Ok evidence -> evidence
               | Error failure -> failwithf "Could not read native scope journal: %s" failure
        let stage =
            journal.NativeScope
            |> Option.defaultWith (fun () -> failwith "Native scope stage missing.")
        nativeDirectory <- Some stage.LinuxCgroupPath
        let identity : LinuxJobScope.Identity =
            { JobId = started.JobId; Directory = stage.LinuxCgroupPath
              BootId = stage.LinuxBootId }
        let deadline = DateTimeOffset.UtcNow.AddSeconds(15.)
        while not (File.Exists marker) && DateTimeOffset.UtcNow < deadline do
            Thread.Sleep 40
        Assert.That(File.Exists marker, Is.True, "Child process never started.")
        let childPid = Int32.Parse(File.ReadAllText(marker).Trim())
        let members = File.ReadAllLines(Path.Combine(stage.LinuxCgroupPath, "cgroup.procs"))
        Assert.That(members, Does.Contain(string childPid),
            "The real child must inherit the exact pre-exec kernel scope.")
        Assert.That(LinuxJobScope.inspect identity, Is.EqualTo(LinuxJobScope.Occupied))
        match cancelJob { ProjectName = "Project1"; JobId = started.JobId } context with
        | Ok _ -> ()
        | Error issue -> Assert.Fail($"Unable to cancel scoped job: {issue}")
        let cleanupDeadline = DateTimeOffset.UtcNow.AddSeconds(15.)
        let mutable state = LinuxJobScope.inspect identity
        while state <> LinuxJobScope.Empty && DateTimeOffset.UtcNow < cleanupDeadline do
            Thread.Sleep 40
            state <- LinuxJobScope.inspect identity
        Assert.That(state, Is.EqualTo(LinuxJobScope.Empty),
            "Native cgroup.kill must reap the scoped descendants even if their parent was canceled.")
        let evidence =
            getProcessOwnershipEvidence
                { ProjectName = "Project1"; JobId = started.JobId } context
        match evidence with
        | Ok read -> Assert.That(read.ProcessTreeStatus, Is.EqualTo("Unverified"))
        | Error failure -> Assert.Fail($"Historical read-only identity lookup failed: {failure}")
    finally
        jobId |> Option.iter (fun id ->
            try cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore
            with _ -> ())
        nativeDirectory |> Option.iter (fun path ->
            try Directory.Delete path with _ -> ())
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", prior)
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let nativeScopeJournalRejectsForgedDigestAndMissingKernelScope () =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-native-scope-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let jobId = Guid.NewGuid().ToString("N")
        let intent = ProcessOwnershipJournal.beginLaunchAt root jobId "project" "device"
        let forged : LinuxJobScope.Identity =
            { JobId = jobId; Directory = Path.GetTempPath()
              BootId = Guid.NewGuid().ToString("D") }
        Assert.Throws<IOException>(fun () ->
            ProcessOwnershipJournal.recordNativeScopeAt root intent forged |> ignore)
        |> ignore
        let badStage = Path.Combine(root, jobId + ".native-scope.json")
        let badContents =
            System.Text.Json.JsonSerializer.Serialize(
                {| SchemaVersion = 1; JobId = jobId
                   IntentSha256 = String.replicate 64 "0"
                   LinuxCgroupPath = "/sys/fs/cgroup/test"; LinuxBootId = Guid.NewGuid().ToString("D")
                   RecordedUtc = DateTimeOffset.UtcNow.ToString("O") |})
        File.WriteAllText(badStage, badContents)
        match ProcessOwnershipJournal.inspectAt root jobId with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("A forged native scope digest was accepted.")
    finally
        Directory.Delete(root, true)


let private nativeScopedCleanupFailureIsVisibleAndRetryable inheritPipes =
    if not (OperatingSystem.IsLinux()) then Assert.Ignore("Linux cgroup v2 only.")
    if not (File.Exists "/usr/bin/python3") || not (File.Exists "/usr/bin/sleep") then
        Assert.Ignore("Native Linux Python and sleep fixtures are required.")
    let previous = Environment.GetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2")
    let root = createTempProject()
    let context = TestContext root
    let marker = Path.Combine(root, "Project1", "cleanup-child.pid")
    let mutable jobId: string option = None
    let mutable scope: LinuxJobScope.Identity option = None
    let mutable killPath: string option = None
    let mutable originalKillMode: UnixFileMode option = None
    try
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", "1")
        let probe =
            try LinuxJobScope.create(Guid.NewGuid().ToString("N"))
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? InvalidOperationException ->
                Assert.Ignore("Writable cgroup-v2 delegation unavailable.")
                None
        probe |> Option.iter (fun item -> Directory.Delete item.Directory)
        let spawned =
            if inheritPipes then
                "child=subprocess.Popen(['/usr/bin/sleep','25']); "
            else
                "child=subprocess.Popen(['/usr/bin/sleep','25'], " +
                "stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, " +
                "stderr=subprocess.DEVNULL); "
        let code =
            "import subprocess,time,pathlib; " + spawned +
            "pathlib.Path('cleanup-child.pid').write_text(str(child.pid)); " +
            "time.sleep(4)"
        let start =
            { ProjectName = "Project1"; Executable = "/usr/bin/python3"
              Args = [ "-c"; code ]
              Reason = Some "Exercise retryable native cleanup failure"
              WorkingDirectory = None; MaxOutputBytes = Some 4096 }
        let started =
            match startJob start context with
            | Ok result -> result
            | Error issue -> failwithf "Native StartJob failed: %A" issue
        jobId <- Some started.JobId
        let journal =
            ProcessOwnershipJournal.inspectAt
                (ProcessOwnershipJournal.defaultDirectory()) started.JobId
            |> function
               | Ok evidence -> evidence
               | Error failure -> failwithf "Missing native launch journal: %s" failure
        let native =
            journal.NativeScope
            |> Option.defaultWith (fun () -> failwith "Native scope stage missing.")
        let identity: LinuxJobScope.Identity =
            { JobId = started.JobId; Directory = native.LinuxCgroupPath
              BootId = native.LinuxBootId }
        scope <- Some identity
        let deadline = DateTimeOffset.UtcNow.AddSeconds 15.
        while not (File.Exists marker) && DateTimeOffset.UtcNow < deadline do
            Thread.Sleep 30
        Assert.That(File.Exists marker, Is.True, "Test child was never spawned.")
        Assert.That(LinuxJobScope.inspect identity, Is.EqualTo(LinuxJobScope.Occupied))

        let nativeKill = Path.Combine(identity.Directory, "cgroup.kill")
        killPath <- Some nativeKill
        let permission = File.GetUnixFileMode(nativeKill)
        originalKillMode <- Some permission
        File.SetUnixFileMode(nativeKill, UnixFileMode.UserRead)

        let mutable status = JobStatus.Running
        let finishDeadline = DateTimeOffset.UtcNow.AddSeconds 15.
        let getStatus () =
            match getJobResult
                    { ProjectName = "Project1"; JobId = started.JobId; AfterSequence = None }
                    context with
            | Ok result -> result.Status
            | Error issue -> failwithf "Native scoped job disappeared: %A" issue
        while (match status with CleanupUnverified _ -> false | _ -> true)
              && DateTimeOffset.UtcNow < finishDeadline do
            Thread.Sleep 40
            status <- getStatus ()
        match status with
        | CleanupUnverified message ->
            Assert.That(message, Does.Contain("Native scope termination unverified"))
        | other -> Assert.Fail($"Failed kernel cleanup was hidden as {other}.")
        // A retained child with inherited pipes used to strand Exited forever.
        Assert.That(LinuxJobScope.inspect identity, Is.EqualTo(LinuxJobScope.Occupied))
        match cancelJob { ProjectName = "Project1"; JobId = started.JobId } context with
        | Error _ -> ()
        | Ok () -> Assert.Fail("Failed kernel cleanup retry falsely returned success.")
        match getStatus () with
        | CleanupUnverified _ -> ()
        | other -> Assert.Fail($"Failed retry erased unresolved cleanup evidence: {other}.")

        File.SetUnixFileMode(nativeKill, permission)
        originalKillMode <- None
        match cancelJob { ProjectName = "Project1"; JobId = started.JobId } context with
        | Ok () -> ()
        | Error issue -> Assert.Fail($"Exact native cleanup retry failed: {issue}")
        match getStatus () with
        | Completed 0 -> ()
        | other -> Assert.Fail($"Successful retry did not restore exited-parent result: {other}")
        let emptyDeadline = DateTimeOffset.UtcNow.AddSeconds 10.
        while LinuxJobScope.inspect identity <> LinuxJobScope.Empty
              && DateTimeOffset.UtcNow < emptyDeadline do
            Thread.Sleep 50
        Assert.That(LinuxJobScope.inspect identity, Is.EqualTo(LinuxJobScope.Empty))
    finally
        match killPath, originalKillMode with
        | Some path, Some mode ->
            try File.SetUnixFileMode(path, mode) with _ -> ()
        | _ -> ()
        scope |> Option.iter (fun value ->
            try LinuxJobScope.tryKill value |> ignore with _ -> ()
            // Remove only the unique test scope, never a production journal.
            try Directory.Delete(value.Directory) with _ -> ())
        jobId |> Option.iter (fun id ->
            try cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore
            with _ -> ())
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", previous)
        if Directory.Exists root then Directory.Delete(root, true)


[<Test; NonParallelizable>]
let nativeScopedCleanupFailureWithInheritedPipesIsVisibleAndRetryable () =
    nativeScopedCleanupFailureIsVisibleAndRetryable true


[<Test; NonParallelizable>]
let nativeScopedCleanupFailureWithoutInheritedPipesIsVisibleAndRetryable () =
    nativeScopedCleanupFailureIsVisibleAndRetryable false


[<Test; NonParallelizable>]
let nativeScopedCancelCanRetryFailedKernelKillBeforeParentExit () =
    if not (OperatingSystem.IsLinux()) then Assert.Ignore("Linux cgroup-v2 only.")
    let previous = Environment.GetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2")
    let root = createTempProject()
    let context = TestContext root
    let mutable jobId: string option = None
    let mutable scope: LinuxJobScope.Identity option = None
    let mutable nativeKill: string option = None
    let mutable priorMode: UnixFileMode option = None
    try
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", "1")
        let probe =
            try LinuxJobScope.create(Guid.NewGuid().ToString("N"))
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? InvalidOperationException ->
                Assert.Ignore("Writable cgroup v2 delegation unavailable.")
                None
        probe |> Option.iter (fun item -> Directory.Delete item.Directory)
        let command =
            { ProjectName = "Project1"; Executable = "/usr/bin/python3"
              Args = [ "-c"; "import time; time.sleep(25)" ]
              Reason = Some "Retry native kernel kill before leader exits"
              WorkingDirectory = None; MaxOutputBytes = Some 1024 }
        let started =
            match startJob command context with
            | Ok result -> result
            | Error issue -> failwithf "Native StartJob failed: %A" issue
        jobId <- Some started.JobId
        let journal =
            ProcessOwnershipJournal.inspectAt
                (ProcessOwnershipJournal.defaultDirectory()) started.JobId
            |> function
               | Ok evidence -> evidence
               | Error failure -> failwithf "Missing native journal: %s" failure
        let native =
            journal.NativeScope
            |> Option.defaultWith (fun () -> failwith "Missing native scope identity.")
        let identity: LinuxJobScope.Identity =
            { JobId = started.JobId; Directory = native.LinuxCgroupPath
              BootId = native.LinuxBootId }
        scope <- Some identity
        let path = Path.Combine(identity.Directory, "cgroup.kill")
        nativeKill <- Some path
        let mode = File.GetUnixFileMode path
        priorMode <- Some mode
        File.SetUnixFileMode(path, UnixFileMode.UserRead)
        match cancelJob { ProjectName = "Project1"; JobId = started.JobId } context with
        | Error _ -> ()
        | Ok () -> Assert.Fail("A blocked native kill returned a successful cancellation.")
        let getStatus () =
            match getJobResult
                    { ProjectName = "Project1"; JobId = started.JobId; AfterSequence = None }
                    context with
            | Ok result -> result.Status
            | Error error -> failwithf "Cleanup evidence disappeared: %A" error
        match getStatus () with
        | CleanupUnverified _ -> ()
        | status -> Assert.Fail($"Failed native cancellation was hidden: {status}")
        File.SetUnixFileMode(path, mode)
        priorMode <- None
        match cancelJob { ProjectName = "Project1"; JobId = started.JobId } context with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Native scope cancellation retry failed: {error}")
        let deadline = DateTimeOffset.UtcNow.AddSeconds 15.
        let mutable status = getStatus ()
        while (match status with Canceled -> false | _ -> true)
              && DateTimeOffset.UtcNow < deadline do
            Thread.Sleep 40
            status <- getStatus ()
        Assert.That(status, Is.EqualTo(Canceled))
        Assert.That(LinuxJobScope.inspect identity, Is.EqualTo(LinuxJobScope.Empty))
    finally
        match nativeKill, priorMode with
        | Some path, Some mode -> try File.SetUnixFileMode(path, mode) with _ -> ()
        | _ -> ()
        scope |> Option.iter (fun item ->
            try LinuxJobScope.tryKill item |> ignore with _ -> ()
            try Directory.Delete(item.Directory) with _ -> ())
        jobId |> Option.iter (fun id ->
            try cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore
            with _ -> ())
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", previous)
        if Directory.Exists root then Directory.Delete(root, true)


[<Test; NonParallelizable>]
let nativeScopedTerminalIncludesBothStreamTails () =
    if not (OperatingSystem.IsLinux()) then Assert.Ignore("Linux cgroup v2 only.")
    if not (File.Exists "/usr/bin/python3") then
        Assert.Ignore("Python3 is required for native output-drain coverage.")
    let previous = Environment.GetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2")
    let root = createTempProject()
    let context = TestContext root
    let mutable jobId: string option = None
    let mutable nativeDirectory: string option = None
    try
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", "1")
        let probe =
            try LinuxJobScope.create(Guid.NewGuid().ToString("N"))
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? InvalidOperationException ->
                Assert.Ignore("Writable cgroup-v2 delegation unavailable.")
                None
        probe |> Option.iter (fun scope -> Directory.Delete scope.Directory)
        let python =
            "import sys; print('stdout-tail', flush=True); " +
            "print('stderr-tail', file=sys.stderr, flush=True)"
        let started =
            match startJob
                    { ProjectName = "Project1"; Executable = "/usr/bin/python3"
                      Args = [ "-c"; python ]; Reason = Some "Verify bounded native output drain"
                      WorkingDirectory = None; MaxOutputBytes = Some 4096 } context with
            | Ok value -> value
            | Error failure -> failwithf "Unable to start native output fixture: %A" failure
        jobId <- Some started.JobId
        let journal =
            ProcessOwnershipJournal.inspectAt
                (ProcessOwnershipJournal.defaultDirectory()) started.JobId
            |> function
               | Ok evidence -> evidence
               | Error failure -> failwithf "Missing native output fixture journal: %s" failure
        nativeDirectory <- journal.NativeScope |> Option.map (fun scope -> scope.LinuxCgroupPath)
        let read () =
            match getJobResult
                    { ProjectName = "Project1"; JobId = started.JobId; AfterSequence = None }
                    context with
            | Ok result -> result
            | Error failure -> failwithf "Native output fixture result unavailable: %A" failure
        let deadline = DateTimeOffset.UtcNow.AddSeconds 15.
        let mutable result = read()
        while result.Status = Running && DateTimeOffset.UtcNow < deadline do
            Thread.Sleep 25
            result <- read()
        Assert.That(result.Status, Is.EqualTo(Completed 0))
        Assert.That(result.Truncated, Is.False,
            "A naturally closed native job must not lose trailing buffered output.")
        Assert.That(
            result.Events |> List.exists (fun item ->
                item.Stream = JobOutputStream.StdOut && item.Text.Contains("stdout-tail")),
            Is.True, "Terminal stdout omitted the last complete callback.")
        Assert.That(
            result.Events |> List.exists (fun item ->
                item.Stream = JobOutputStream.StdErr && item.Text.Contains("stderr-tail")),
            Is.True, "Terminal stderr omitted the last complete callback.")
    finally
        jobId |> Option.iter (fun id ->
            try cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore
            with _ -> ())
        nativeDirectory |> Option.iter (fun path ->
            try Directory.Delete path with _ -> ())
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", previous)
        if Directory.Exists root then Directory.Delete(root, true)


[<Test; NonParallelizable>]
let nativeScopedEscapedDescendantCannotMakeTerminalOutputLookComplete () =
    if not (OperatingSystem.IsLinux()) then Assert.Ignore("Linux cgroup-v2 only.")
    if not (File.Exists "/usr/bin/python3") then
        Assert.Ignore("Python3 is required for the escaped-pipe fixture.")
    let previous = Environment.GetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2")
    let root = createTempProject()
    let context = TestContext root
    let project = Path.Combine(root, "Project1")
    let escapedMarker = Path.Combine(project, "escaped-pipe.pid")
    let failedMarker = Path.Combine(project, "escaped-pipe.error")
    let mutable jobId: string option = None
    let mutable scopeDirectory: string option = None
    try
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", "1")
        let probe =
            try LinuxJobScope.create(Guid.NewGuid().ToString("N"))
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? InvalidOperationException ->
                Assert.Ignore("Writable cgroup-v2 delegation unavailable.")
                None
        probe |> Option.iter (fun scope -> Directory.Delete scope.Directory)
        // The exact same-UID migration already observed on this host.
        // The child deliberately inherits BOTH redirected streams, escapes
        // the delegated scope, and self-terminates after eight seconds.
        // Never kill a historical PID or an unrelated system process.
        let python = """
import os, pathlib, sys, time
from pathlib import Path
if os.fork() == 0:
    try:
        membership = next(line for line in Path('/proc/self/cgroup').read_text().splitlines() if line.startswith('0::'))
        relative = Path(membership.partition('::')[2].lstrip('/'))
        parent_procs = Path('/sys/fs/cgroup') / relative.parent / 'cgroup.procs'
        parent_procs.write_text(str(os.getpid()))
        Path('escaped-pipe.pid').write_text(str(os.getpid()))
        print('escaped-prefix', flush=True)
        time.sleep(8)
        print('escaped-late-tail', flush=True)
    except Exception as exc:
        Path('escaped-pipe.error').write_text(repr(exc))
    finally:
        os._exit(0)
else:
    for _ in range(200):
        if Path('escaped-pipe.pid').exists() or Path('escaped-pipe.error').exists():
            break
        time.sleep(0.01)
    print('leader-output-tail', flush=True)
"""
        let started =
            match startJob
                    { ProjectName = "Project1"; Executable = "/usr/bin/python3"
                      Args = [ "-c"; python ]
                      Reason = Some "Negative-space native escaped-pipe output proof"
                      WorkingDirectory = None; MaxOutputBytes = Some 4096 } context with
            | Ok result -> result
            | Error issue -> failwithf "Native escaped-pipe job failed to launch: %A" issue
        jobId <- Some started.JobId
        let journal =
            ProcessOwnershipJournal.inspectAt
                (ProcessOwnershipJournal.defaultDirectory()) started.JobId
            |> function
               | Ok evidence -> evidence
               | Error failure -> failwithf "Native escaped-pipe journal missing: %s" failure
        scopeDirectory <- journal.NativeScope |> Option.map (fun scope -> scope.LinuxCgroupPath)

        let admitted = DateTimeOffset.UtcNow.AddSeconds(10.)
        while not (File.Exists escapedMarker || File.Exists failedMarker)
              && DateTimeOffset.UtcNow < admitted do
            Thread.Sleep 20
        if File.Exists failedMarker then
            Assert.Ignore("Host denies cgroup migration: " + File.ReadAllText failedMarker)
        Assert.That(File.Exists escapedMarker, Is.True,
            "The descendant did not move to the writable parent cgroup.")

        let read () =
            match getJobResult
                    { ProjectName = "Project1"; JobId = started.JobId; AfterSequence = None }
                    context with
            | Ok result -> result
            | Error issue -> failwithf "Native escaped-pipe job disappeared: %A" issue

        // A wait on inherited pipes would reach the child lifetime, not the
        // bounded terminal deadline. The known escaping child is still alive.
        let terminalClock = Stopwatch.StartNew()
        let terminalDeadline = DateTimeOffset.UtcNow.AddSeconds(5.)
        let mutable result = read()
        while result.Status = Running && DateTimeOffset.UtcNow < terminalDeadline do
            Thread.Sleep 25
            result <- read()
        // A blocking getJobResult can return terminal after the escaped child
        // exits; merely exiting the polling loop would hide that regression.
        Assert.That(terminalClock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5.)),
            "Native terminal observation exceeded the five-second bound.")
        Assert.That(result.Status, Is.EqualTo(Completed 0),
            "Successful native kernel-scope kill must complete boundedly.")
        Assert.That(result.Truncated, Is.True,
            "Escaped descendants holding redirected pipes must leave output unverified.")
        let escapedPid = Int32.Parse(File.ReadAllText escapedMarker)
        Assert.That(Directory.Exists($"/proc/{escapedPid}"), Is.True,
            "The escaped descendant must still be alive when terminal is reported.")
        let nativeScope =
            journal.NativeScope
            |> Option.defaultWith (fun () -> failwith "Native kernel scope missing.")
        let nativeIdentity : LinuxJobScope.Identity =
            { JobId = started.JobId; Directory = nativeScope.LinuxCgroupPath
              BootId = nativeScope.LinuxBootId }
        Assert.That(LinuxJobScope.inspect nativeIdentity, Is.EqualTo(LinuxJobScope.Empty),
            "An empty original cgroup does not prove the escaped descendant has exited.")
        Assert.That(
            result.Events |> List.exists (fun item -> item.Text.Contains("leader-output-tail")),
            Is.True, "Already observed leader output disappeared at termination.")
    finally
        // The escaped child is deliberately self-bounded. Its PID is only
        // observed here; never send a signal to an unverified identity.
        if File.Exists escapedMarker then
            try
                let childPid = Int32.Parse(File.ReadAllText escapedMarker)
                let settle = DateTimeOffset.UtcNow.AddSeconds(12.)
                while Directory.Exists($"/proc/{childPid}")
                      && DateTimeOffset.UtcNow < settle do
                    Thread.Sleep 50
            with _ -> ()
        jobId |> Option.iter (fun id ->
            try cancelJob { ProjectName = "Project1"; JobId = id } context |> ignore
            with _ -> ())
        scopeDirectory |> Option.iter (fun path ->
            try Directory.Delete path with _ -> ())
        Environment.SetEnvironmentVariable("JARVIS_STARTJOB_CGROUP_V2", previous)
        if Directory.Exists root then Directory.Delete(root, true)
