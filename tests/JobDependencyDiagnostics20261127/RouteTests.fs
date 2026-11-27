module JobDependencyDiagnostics20261127.RouteTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework
open Common
open Client
open Client.ConsoleTui
open Client.JobManager
open Client.SignalR.Client

module Durable = Client.BoundedProcess.DurableJobs

[<TestFixture; NonParallelizable>]
type RouteTests() =
    let withRuntime action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-job-diagnostics-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"))
        try
            Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
            Directory.CreateDirectory(Path.Combine(root, "Project2")) |> ignore
            let runtime = Runtime(root, ConsoleTui(), FullTrust)
            action root runtime
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior)
            Directory.Delete(root, true)

    let execute runtime actor command =
        let wire = JsonSerializer.Serialize<AgentCommand>(command)
        executeAuthenticatedCommandWithChallengeWake runtime wire actor None false
            (fun _ _ -> failwith "Read must not commit a wake")
        |> fun t -> t.GetAwaiter().GetResult()

    let query runtime actor project jobId =
        execute runtime actor (GetJobResultCommand { ProjectName = project; JobId = jobId; AfterSequence = Some 0L })

    let result (response: AgentCommandResponse) =
        Assert.That(response.Error.IsNone, Is.True, sprintf "%A" response.Error)
        Assert.That(response.Result.IsSome, Is.True)
        JsonSerializer.Deserialize<JobResult>(response.Result.Value)

    let list runtime actor project =
        let response = execute runtime actor (ListJobsCommand { ProjectName = Some project; IncludeCompleted = true })
        Assert.That(response.Error.IsNone, Is.True, sprintf "%A" response.Error)
        JsonSerializer.Deserialize<ListJobsResult>(response.Result.Value).Jobs

    // Real durable observer reads a disposable persisted request and receipt.
    // These are synthetic test receipts, not live OS-job or restart evidence.
    let createRequest root project actor =
        let jobs = Durable.root root
        let request: Durable.Request =
            { Schema = 1; JobId = Guid.NewGuid().ToString("N"); Project = project; Actor = Some actor
              Executable = "fixture-only"; Args = []; Reason = None; WorkingDirectory = root
              StartedAt = DateTimeOffset.UtcNow; MaxOutputBytes = 1024 }
        Durable.create jobs request, request

    let publish directory (request: Durable.Request) status terminal =
        let receipt: Durable.Receipt =
            { Schema = 1; JobId = request.JobId
              Supervisor = { Pid = Int32.MaxValue; Birth = "fixture-dead-supervisor" }
              Child = None; Started = true; Status = status
              CompletedAt = if terminal then Some(request.StartedAt.AddSeconds 1.) else None
              Events = []; NextSequence = 0L; Truncated = false; CancelError = None }
        File.WriteAllBytes(Path.Combine(directory, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes receipt)

    [<Test>]
    member _.DurableStatusUsesOriginalOwnerAndProject() =
        withRuntime (fun root runtime ->
            let directory, request = createRequest root "Project1" "Fixture Owner"
            publish directory request Running false
            match (query runtime "Fixture Owner" "Project1" request.JobId |> result).Status with
            | RecoveryUnknown _ -> ()
            | other -> failwithf "Dead supervisor is not terminal: %A" other
            Assert.That((query runtime "Fixture Observer" "Project1" request.JobId).Error.IsSome, Is.True)
            Assert.That((query runtime "Fixture Owner" "Project2" request.JobId).Error.IsSome, Is.True)
            publish directory request (Completed 17) true
            let owner = query runtime "Fixture Owner" "Project1" request.JobId |> result
            Assert.That(owner.Status, Is.EqualTo(Completed 17))
            Assert.That(owner.ToolResponse.IsNone, Is.True)
            Assert.That((query runtime "Fixture Observer" "Project1" request.JobId).Error.IsSome, Is.True)
            Assert.That(list runtime "Fixture Observer" "Project1" |> List.exists (fun j -> j.JobId = request.JobId), Is.False)
            Assert.That(list runtime "Fixture Owner" "Project1" |> List.exists (fun j -> j.JobId = request.JobId && j.Kind = JobKind.Process), Is.True)
            let freshRuntime = Runtime(root, ConsoleTui(), FullTrust)
            Assert.That((query freshRuntime "Fixture Owner" "Project1" request.JobId |> result).Status, Is.EqualTo(Completed 17))
            Assert.That((query freshRuntime "Fixture Owner" "Project1" (Guid.NewGuid().ToString("N"))).Error.IsSome, Is.True))

    [<Test>]
    member _.PromotedWrapperDoesNotConferChildOrObserverAuthority() =
        withRuntime (fun root runtime ->
            let directory, request = createRequest root "Project1" "Fixture Owner"
            publish directory request Running false
            let completion = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
            let command = RunCommandCommand { ProjectName = "Project1"; Executable = "fixture-only"; Args = []; Reason = None; WorkingDirectory = None; TimeoutSeconds = None; MaxOutputBytes = None }
            let wire = JsonSerializer.Serialize<AgentCommand>(command)
            // Deterministic injected runner at the existing promotion boundary;
            // it never executes the serialized command or a child process.
            let outer = executeOrPromoteWith runtime wire "Fixture Owner" (TimeSpan.FromMilliseconds 1.) (fun _ _ -> completion.Task)
                        |> fun t -> t.GetAwaiter().GetResult()
            let handle = outer.PendingJob.Value
            try
                Assert.That(handle.Kind, Is.EqualTo(JobKind.ToolOperation))
                Assert.That((query runtime "Fixture Owner" "Project1" handle.JobId |> result).Status, Is.EqualTo(Running))
                Assert.That((query runtime "Fixture Observer" "Project1" handle.JobId).Error.IsSome, Is.True)
                Assert.That((query runtime "Fixture Owner" "Project2" handle.JobId).Error.IsSome, Is.True)
                Assert.That(list runtime "Fixture Owner" "Project1" |> List.exists (fun j -> j.JobId = handle.JobId && j.Kind = JobKind.ToolOperation), Is.True)
            finally
                // An arbitrary inner payload must not be interpreted as a typed
                // process result by the generic wrapper.
                completion.TrySetResult({ Result = Some(JsonSerializer.Serialize {| jobId = request.JobId |})
                                          Error = None; Instructions = []; StateChangeEvidence = StateChangeEvidence.Confirmed []
                                          InboxActivation = None; PendingJob = None }) |> ignore
            let mutable terminal = query runtime "Fixture Owner" "Project1" handle.JobId |> result
            Assert.That(System.Threading.SpinWait.SpinUntil((fun () ->
                terminal <- query runtime "Fixture Owner" "Project1" handle.JobId |> result
                terminal.Status <> Running), TimeSpan.FromSeconds 5.), Is.True)
            Assert.That(terminal.Status, Is.EqualTo(ToolCompleted))
            Assert.That(terminal.ToolResponse.IsSome, Is.True)
            match (query runtime "Fixture Owner" "Project1" request.JobId |> result).Status with
            | RecoveryUnknown _ -> ()
            | other -> failwithf "Outer completion does not prove child terminal: %A" other
            Assert.That((query runtime "Fixture Observer" "Project1" handle.JobId).Error.IsSome, Is.True))
