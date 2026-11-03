module ActivityLogTests

open System
open System.IO
open Client.ActivityLog
open FsUnitTyped
open NUnit.Framework

let private tempDirectory () =
    let path = Path.Combine(Path.GetTempPath(), "jarvis-activity-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(path) |> ignore
    path

let private settings path maxFileBytes =
    { DirectoryPath = path
      RetentionDays = 14
      MaxFileBytes = maxFileBytes }

let private snapshot id status =
    { Version = 1
      ActivityId = id
      Origin = "Worker"
      StartedAt = DateTimeOffset.Parse("2026-10-02T15:00:00+02:00")
      ProjectName = "Wayfold"
      CommandName = "RunCommand"
      Message = "Verify tests"
      Reason = "Verify tests"
      Detail = "dotnet test"
      Status = status
      DurationMs = Nullable()
      Result = null
      Error = null }

[<Test>]
let ``activity log replays latest state for each activity`` () =
    let path = tempDirectory ()

    try
        use store = new Store(settings path (1024L * 1024L))
        store.Append(snapshot "a1" "Running")

        store.Append(
            { snapshot "a1" "Completed" with
                DurationMs = Nullable 42L
                Result = "exit 0" })

        let entries = store.LoadRecent(500)
        entries.Length |> shouldEqual 1
        entries.Head.Status |> shouldEqual "Completed"
        entries.Head.DurationMs.Value |> shouldEqual 42L
        entries.Head.Result |> shouldEqual "exit 0"
    finally
        Directory.Delete(path, true)

[<Test>]
let ``unfinished activity is restored as interrupted`` () =
    let path = tempDirectory ()

    try
        use store = new Store(settings path (1024L * 1024L))
        store.Append(snapshot "a1" "Awaiting permission")

        let entries = store.LoadRecent(500)
        entries.Head.Status |> shouldEqual "Interrupted"
        entries.Head.Error |> shouldEqual "Client stopped before this activity completed."
    finally
        Directory.Delete(path, true)

[<Test>]
let ``activity log rotates when current file reaches size limit`` () =
    let path = tempDirectory ()

    try
        use store = new Store(settings path 1L)
        store.Append(snapshot "a1" "Completed")
        store.Append(snapshot "a2" "Completed")

        Directory.GetFiles(path, "activity-*.jsonl").Length |> shouldEqual 2
    finally
        Directory.Delete(path, true)

[<Test>]
let ``old activity log files are pruned`` () =
    let path = tempDirectory ()

    try
        let oldPath = Path.Combine(path, "activity-2020-01-01.jsonl")
        File.WriteAllText(oldPath, "{}")
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-30.0))

        use _store = new Store(settings path (1024L * 1024L))
        File.Exists(oldPath) |> shouldEqual false
    finally
        Directory.Delete(path, true)


[<Test>]
let ``agent last seen survives reopening the activity store`` () =
    let path = tempDirectory ()
    let timestamp = DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.Zero)
    try
        do
            use store = new Store(settings path (1024L * 1024L))
            store.SaveAgentSeen [ "Wayfold::Agent 8", timestamp ]
        use reopened = new Store(settings path (1024L * 1024L))
        reopened.LoadAgentSeen() |> Map.ofList |> Map.tryFind "Wayfold::Agent 8"
        |> shouldEqual (Some timestamp)
        reopened.SaveAgentSeen [ "Wayfold::Agent 8", timestamp.AddMinutes(2.) ]
        reopened.LoadAgentSeen() |> Map.ofList |> Map.tryFind "Wayfold::Agent 8"
        |> shouldEqual (Some(timestamp.AddMinutes(2.)))
    finally
        Directory.Delete(path, true)


[<Test>]
let ``agent seen is persisted by the TUI on first attributed call`` () =
    let path = tempDirectory ()
    try
        use store = new Store(settings path (1024L * 1024L))
        let tui = Client.ConsoleTui.ConsoleTui(activityLog = store)
        tui.RecordAgentSeen("Wayfold", "Agent 8")
        let recorded = store.LoadAgentSeen() |> Map.ofList
        Assert.That(recorded.ContainsKey "Wayfold::Agent 8", Is.True)
        Assert.That(recorded["Wayfold::Agent 8"], Is.LessThanOrEqualTo(DateTimeOffset.UtcNow))
    finally
        Directory.Delete(path, true)
