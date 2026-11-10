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
                    (Some "operation-one") (fun () ->
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
                None (fun () -> Task.FromResult response) with
        | Ok job -> job
        | Error error -> failwith $"Unable to register quick tool: {error}"
    match (awaitToolOperation registered (TimeSpan.FromSeconds 2.)).GetAwaiter().GetResult() with
    | Choice1Of2 actual -> Assert.That(actual, Is.EqualTo(response))
    | Choice2Of2 _ -> Assert.Fail("A completed fast operation must not return a job handle.")
    let handle, _ = registered
    Assert.That(isToolOperationJob handle.JobId, Is.False,
                "A quick call is discarded rather than cluttering list_jobs.")
