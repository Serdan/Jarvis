module JobManagerTests

open System
open System.IO
open System.Text
open System.Threading
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

[<Test>]
let ``job lifecycle starts lists reads and cancels`` () =
    let root = createTempProject ()
    try
        let context = TestContext root
        let start =
            { ProjectName = "Project1"
              Executable = "dotnet"
              Args = [ "--info" ]
              WorkingDirectory = None
              MaxOutputBytes = Some 8192 }

        let jobId =
            match startJob start context with
            | Ok result -> result.JobId
            | Error error -> failwith $"Expected StartJob Ok, got {error}"

        match listJobs { ProjectName = Some "Project1"; IncludeCompleted = true } context with
        | Ok result -> result.Jobs |> List.exists (fun job -> job.JobId = jobId) |> shouldEqual true
        | Error error -> Assert.Fail($"Expected ListJobs Ok, got {error}")

        Thread.Sleep 500

        match getJobResult { JobId = jobId; AfterSequence = Some 0L } context with
        | Ok result ->
            result.JobId |> shouldEqual jobId
            result.NextSequence >= 0L |> shouldEqual true
        | Error error -> Assert.Fail($"Expected GetJobResult Ok, got {error}")

        cancelJob { JobId = jobId } context |> shouldEqual (Ok())
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
              WorkingDirectory = None
              MaxOutputBytes = Some 64 }

        let jobId =
            match startJob start context with
            | Ok result -> result.JobId
            | Error error -> failwith $"Expected StartJob Ok, got {error}"

        Thread.Sleep 750

        match getJobResult { JobId = jobId; AfterSequence = Some 0L } context with
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
              WorkingDirectory = None
              MaxOutputBytes = Some 4096 }

        let jobId =
            match startJob start context with
            | Ok result -> result.JobId
            | Error error -> failwith $"Expected StartJob Ok, got {error}"

        let rec waitForCompletion remaining =
            match getJobResult { JobId = jobId; AfterSequence = Some 0L } context with
            | Error error -> failwith $"Expected GetJobResult Ok, got {error}"
            | Ok result when result.Status <> Running -> result
            | Ok _ when remaining <= 0 -> failwith "Job did not complete in time."
            | Ok _ ->
                Thread.Sleep 50
                waitForCompletion (remaining - 1)

        let first = waitForCompletion 100
        first.Events.IsEmpty |> shouldEqual false
        first.Events |> List.exists (fun event -> event.Stream = JobOutputStream.StdOut) |> shouldEqual true
        first.Events |> List.exists (fun event -> event.Stream = JobOutputStream.StdErr) |> shouldEqual true

        first.Events
        |> List.map (fun event -> event.Sequence)
        |> List.pairwise
        |> List.forall (fun (before, after) -> before < after)
        |> shouldEqual true

        match getJobResult { JobId = jobId; AfterSequence = Some first.NextSequence } context with
        | Error error -> Assert.Fail($"Expected second GetJobResult Ok, got {error}")
        | Ok second ->
            second.Events |> shouldEqual []
            second.NextSequence |> shouldEqual first.NextSequence
    finally
        Directory.Delete(root, true)
