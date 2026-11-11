module ClientActivityTests

open System
open System.IO
open System.Text.Json
open Client
open Common
open FsUnitTyped
open NUnit.Framework

let private sampleRunCommand projectName reason =
    RunCommandCommand
        { ProjectName = projectName
          Executable = "dotnet"
          Args = [ "test" ]
          Reason = Some reason
          WorkingDirectory = None
          TimeoutSeconds = None
          MaxOutputBytes = None }

[<Test>]
let ``client activity snapshot filters by project and preserves command details`` () =
    let tui = ConsoleTui.ConsoleTui()

    let wayfoldId = tui.StartActivity(sampleRunCommand "Wayfold" "Verify Wayfold")
    tui.CompleteActivity(wayfoldId, 42L, Some "exit 0")

    let jarvisId = tui.StartActivity(sampleRunCommand "Jarvis" "Verify Jarvis")
    tui.CompleteActivity(jarvisId, 17L, Some "exit 0")

    let snapshot = tui.GetActivitySnapshot(Some "wayfold", Some 20)

    snapshot.Entries.Length |> shouldEqual 1
    let entry = snapshot.Entries.Head
    entry.ProjectName |> shouldEqual (Some "Wayfold")
    entry.CommandName |> shouldEqual (Some "RunCommand")
    entry.Reason |> shouldEqual (Some "Verify Wayfold")
    entry.Detail |> shouldEqual (Some "dotnet test")
    entry.Status |> shouldEqual "Completed"
    entry.DurationMs |> shouldEqual (Some 42L)
    entry.Result |> shouldEqual (Some "exit 0")

[<Test>]
let ``GetClientActivity does not add itself to client activity`` () =
    task {
        let tui = ConsoleTui.ConsoleTui()
        let activityId = tui.StartActivity(sampleRunCommand "Wayfold" "Existing activity")
        tui.CompleteActivity(activityId, 5L, Some "exit 0")

        let runtime = Runtime(".", tui, PartialTrust)
        let command =
            GetClientActivityCommand
                { ProjectName = None
                  Limit = Some 20
                  IncludeObservers = None }

        let! response = Client.SignalR.Client.receiveCommand runtime command

        match response with
        | Error error -> failwith $"GetClientActivity failed: {error}"
        | Ok payload ->
            let result = JsonSerializer.Deserialize<GetClientActivityResult>(payload)
            result.Entries.Length |> shouldEqual 1
            result.Entries.Head.Reason |> shouldEqual (Some "Existing activity")
    }

[<Test>]
let ``persisted activity is restored into client snapshots`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "jarvis-client-activity-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(path) |> ignore

    let settings: ActivityLog.Settings =
        { DirectoryPath = path
          RetentionDays = 14
          MaxFileBytes = 1024L * 1024L }

    try
        let writeActivity () =
            use store = new ActivityLog.Store(settings)
            let tui = ConsoleTui.ConsoleTui(activityLog = store)
            let activityId = tui.StartActivity(sampleRunCommand "Wayfold" "Persisted activity")
            tui.CompleteActivity(activityId, 23L, Some "exit 0")

        writeActivity ()

        use replayStore = new ActivityLog.Store(settings)
        let replayTui = ConsoleTui.ConsoleTui(activityLog = replayStore)
        let snapshot = replayTui.GetActivitySnapshot(Some "wayfold", Some 20)

        snapshot.Entries.Length |> shouldEqual 1
        let entry = snapshot.Entries.Head
        entry.ProjectName |> shouldEqual (Some "Wayfold")
        entry.CommandName |> shouldEqual (Some "RunCommand")
        entry.Reason |> shouldEqual (Some "Persisted activity")
        entry.Detail |> shouldEqual (Some "dotnet test")
        entry.Status |> shouldEqual "Completed"
        entry.DurationMs |> shouldEqual (Some 23L)
        entry.Result |> shouldEqual (Some "exit 0")
    finally
        Directory.Delete(path, true)

[<Test>]
let ``Message records one informational project activity without a command wrapper`` () =
    task {
        let tui = ConsoleTui.ConsoleTui()
        let runtime = Runtime(".", tui, NoTrust)
        let command =
            MessageCommand
                { ProjectName = Some "Wayfold"
                  Message = "Done with this turn" }

        let! response = Client.SignalR.Client.receiveCommand runtime command

        match response with
        | Error error -> failwith $"Message failed: {error}"
        | Ok _ -> ()

        let snapshot = tui.GetActivitySnapshot(Some "wayfold", Some 20)
        snapshot.Entries.Length |> shouldEqual 1
        let entry = snapshot.Entries.Head
        entry.ProjectName |> shouldEqual (Some "Wayfold")
        entry.CommandName |> shouldEqual None
        entry.Message |> shouldEqual "Done with this turn"
        entry.Status |> shouldEqual "Info"
    }

[<Test>]
let ``Message rejects blank content without adding activity`` () =
    task {
        let tui = ConsoleTui.ConsoleTui()
        let runtime = Runtime(".", tui, NoTrust)
        let command =
            MessageCommand
                { ProjectName = Some "Jarvis"
                  Message = "   " }

        let! response = Client.SignalR.Client.receiveCommand runtime command

        match response with
        | Error(ValidationError _) -> ()
        | Error error -> Assert.Fail($"Expected validation error, got {error}")
        | Ok _ -> Assert.Fail("Expected blank Message to fail.")

        tui.GetActivitySnapshot(None, Some 20).Entries |> shouldEqual []
    }

[<Test>]
let ``observer diagnostics stay available but are excluded by default`` () =
    let tui = ConsoleTui.ConsoleTui()
    let observerId = tui.StartActivity(GetGitStatusCommand { ProjectName = "Wayfold" })
    tui.CompleteActivity(observerId, 2L, None)
    let workerId = tui.StartActivity(sampleRunCommand "Wayfold" "Build")
    tui.CompleteActivity(workerId, 18L, None)
    let work = tui.GetActivitySnapshot(Some "Wayfold", Some 20)
    work.Entries.Length |> shouldEqual 1
    work.Entries.Head.Origin |> shouldEqual "Worker"
    let all = tui.GetActivitySnapshot(Some "Wayfold", Some 20, includeObservers = true)
    all.Entries.Length |> shouldEqual 2
    all.Entries.Head.Origin |> shouldEqual "Observer"
    all.Entries.Tail.Head.Origin |> shouldEqual "Worker"

[<Test>]
let ``GetClientActivity respects explicit observer inclusion`` () =
    task {
        let tui = ConsoleTui.ConsoleTui()
        let id = tui.StartActivity(GetGitStatusCommand { ProjectName = "Wayfold" })
        tui.CompleteActivity(id, 3L, None)
        let rt = Runtime(".", tui, PartialTrust)

        for includeObservers, expectedCount in [ false, 0; true, 1 ] do
            let command =
                GetClientActivityCommand {
                    ProjectName = Some "Wayfold"
                    Limit = Some 10
                    IncludeObservers = Some includeObservers }
            let! response = Client.SignalR.Client.receiveCommand rt command
            match response with
            | Error error -> Assert.Fail($"Activity query failed: {error}")
            | Ok serialized ->
                let result = JsonSerializer.Deserialize<GetClientActivityResult>(serialized)
                result.Entries.Length |> shouldEqual expectedCount
                if includeObservers then result.Entries.Head.Origin |> shouldEqual "Observer"
    }

[<Test>]
let ``client activity snapshots keep original worktree selector for API consumers`` () =
    let tui = ConsoleTui.ConsoleTui()
    let id = tui.StartActivity(sampleRunCommand "Wayfold::silver-ring-trade" "Verify trade totals")
    tui.CompleteActivity(id, 42L, Some "exit 0")
    let entries = tui.GetActivitySnapshot(Some "Wayfold::silver-ring-trade", Some 20).Entries
    entries.Length |> shouldEqual 1
    entries.Head.ProjectName |> shouldEqual (Some "Wayfold::silver-ring-trade")

[<Test>]
let ``live hidden worktree activity remains stored under its original selector`` () =
    let tui = ConsoleTui.ConsoleTui()
    let id = tui.StartActivity(sampleRunCommand ".Wayfold-worktrees/silver-ring-trade" "Verify trade totals")
    tui.CompleteActivity(id, 9L, Some "exit 0")
    let entries = tui.GetActivitySnapshot(Some ".Wayfold-worktrees/silver-ring-trade", Some 20).Entries
    entries.Length |> shouldEqual 1
    entries.Head.ProjectName |> shouldEqual (Some ".Wayfold-worktrees/silver-ring-trade")


[<Test>]
let ``sessionless MCP receive updates Seen for successful and failed agent commands`` () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-seen-incoming-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
        let logPath = Path.Combine(root, "activity")
        Directory.CreateDirectory logPath |> ignore
        let storeSettings : Client.ActivityLog.Settings =
            { DirectoryPath = logPath
              RetentionDays = 14
              MaxFileBytes = 1024L * 1024L }
        try
            use store = new Client.ActivityLog.Store(storeSettings)
            let tui = ConsoleTui.ConsoleTui(activityLog = store)
            let runtime = Runtime(root, tui, PartialTrust)

            let successful = ListDirectoryCommand { ProjectName = "Project1"; FolderPath = "" }
            let! _, success, _ =
                Client.SignalR.Client.processIncomingCommand runtime (JsonSerializer.Serialize<AgentCommand>(successful)) "Gemini Agent"
            match success with
            | Ok _ -> ()
            | Error error -> Assert.Fail(sprintf "Expected list directory success, got %A" error)
            let initial = store.LoadAgentSeen() |> Map.ofList
            Assert.That(initial.ContainsKey "Project1::Gemini Agent", Is.True)
            Assert.That(initial.ContainsKey "Project1::ChatGPT Agent", Is.False)

            let failed =
                ReadFileCommand
                    { ProjectName = "Project1"; FilePath = "does-not-exist.txt"
                      StartLine = None; EndLine = None; IncludeLineNumbers = None }
            let! _, failure, _ =
                Client.SignalR.Client.processIncomingCommand runtime (JsonSerializer.Serialize<AgentCommand>(failed)) "Other Agent"
            match failure with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Missing file should fail")
            let updated = store.LoadAgentSeen() |> Map.ofList
            Assert.That(updated.ContainsKey "Project1::Other Agent", Is.True)

            // A failed agent action also counts as observed agent activity,
            // despite the absence of provider session metadata or a bound ledger.
            let resume : TaskWriteCommand =
                { ProjectName = "Project1"; Action = "CheckpointAttempt"
                  OperationId = Guid.NewGuid().ToString("D")
                  TaskId = "missing-task"; AttemptId = ""; ActorId = "Resume Agent"; OwnerId = ""
                  Title = ""; Html = ""; ExpectedVersion = 0
                  ExpectedAgentVersion = 0; ExpectedTaskVersion = 0
                  AssignmentGeneration = 0; Summary = ""; NextAction = ""; Reason = ""
                  BaseOid = ""; TargetRef = ""; SourceRef = ""; AllowDetached = false
                  AllowUncommitted = false; DecisionSequence = 0L; Evidence = ""
                  SessionId = "" }
            let! _, resumeResult, _ =
                Client.SignalR.Client.processIncomingCommand runtime (JsonSerializer.Serialize<AgentCommand>(TaskWriteCommand resume)) "Resume Agent"
            match resumeResult with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Checkpoint without a bound ledger should fail")
            let observed = store.LoadAgentSeen() |> Map.ofList
            Assert.That(observed.ContainsKey "Project1::Resume Agent", Is.True)
        finally
            Directory.Delete(root, true)
    }

[<Test>]
let incoming_activity_commands_accept_literal_history_labels () =
    task {
        let label = ".Wayfold-worktrees/silver-ring-trade"
        let tui = ConsoleTui.ConsoleTui()
        let rt = Runtime(".", tui, NoTrust)
        let run (command: AgentCommand) =
            let payload = JsonSerializer.Serialize<AgentCommand>(command)
            Client.SignalR.Client.processIncomingCommand rt payload "Agent 2"
        let! _, messageResult, _ =
            run (MessageCommand { ProjectName = Some label; Message = "handoff" })
        Assert.That(messageResult.IsOk, Is.True)
        let! _, activityResult, _ =
            run (GetClientActivityCommand
                    { ProjectName = Some label; Limit = Some 20; IncludeObservers = None })
        match activityResult with
        | Error error -> Assert.Fail(sprintf "Literal label lookup failed: %A" error)
        | Ok payload ->
            let snapshot = JsonSerializer.Deserialize<GetClientActivityResult>(payload)
            Assert.That(snapshot.Entries.Length, Is.EqualTo(1))
            Assert.That(snapshot.Entries.Head.ProjectName, Is.EqualTo(Some label))
            Assert.That(snapshot.Entries.Head.Message, Is.EqualTo("handoff"))
        let arbitrary = "NotARepository::not-a-registered-worktree"
        let! _, arbitraryResult, _ =
            run (MessageCommand { ProjectName = Some arbitrary; Message = "literal label" })
        Assert.That(arbitraryResult.IsOk, Is.True)
    }

[<Test>]
let jobControlReadsDoNotImplicitlyResumeAReadyAgent () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-job-control-seen-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Project1")) |> ignore
        let logPath = Path.Combine(root, "activity")
        Directory.CreateDirectory logPath |> ignore
        let settings : Client.ActivityLog.Settings =
            { DirectoryPath = logPath; RetentionDays = 14; MaxFileBytes = 1024L * 1024L }
        try
            use store = new Client.ActivityLog.Store(settings)
            let rt = Runtime(root, ConsoleTui.ConsoleTui(activityLog = store), FullTrust)
            let commands : AgentCommand list =
                [ ListJobsCommand { ProjectName = Some "Project1"; IncludeCompleted = true }
                  GetJobResultCommand { ProjectName = "Project1"; JobId = "missing-job"; AfterSequence = None }
                  CancelJobCommand { ProjectName = "Project1"; JobId = "missing-job" } ]
            for command in commands do
                let! _, _, response =
                    Client.SignalR.Client.processIncomingCommand rt (JsonSerializer.Serialize<AgentCommand>(command)) "Ready Agent"
                Assert.That(response.AgentStateChanges, Is.Empty)
                Assert.That(response.InboxActivation.IsNone, Is.True)
            let seen = store.LoadAgentSeen() |> Map.ofList
            Assert.That(seen.ContainsKey("Project1::Ready Agent"), Is.False,
                        "Status polling and job controls must not count as progress or reactivate a Ready assignment.")
        finally
            Directory.Delete(root, true)
    }
