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
let toolFailuresPreserveOriginalErrorWithoutRestart () =
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
    | Some(ExecutionFailed message) -> Assert.That(message, Does.Contain("synthetic command fault"))
    | other -> Assert.Fail($"Expected typed execution failure: {other}")
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
                    Assert.That(summary.CompletedAt.IsNone, Is.True,
                                "A cancellation request cannot publish a terminal timestamp.")
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
