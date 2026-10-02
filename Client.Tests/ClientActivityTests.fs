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
                  Limit = Some 20 }

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
