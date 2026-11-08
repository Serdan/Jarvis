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


[<System.Runtime.InteropServices.DllImport("libc", EntryPoint = "umask")>]
extern uint32 private setProcessUmask(uint32 mask)

let private privateDir = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
let private privateFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
let private unix () = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
let private todayLog path = Path.Combine(path, "activity-" + DateTimeOffset.Now.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture) + ".jsonl")

[<Test; NonParallelizable>]
let activity_storage_creation_is_private_under_permissive_umask () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "private")
        let previous = setProcessUmask 0u
        try
            use store = new Store(settings path (1024L * 1024L))
            store.Append(snapshot "one" "Completed")
            store.SaveAgentSeen [ "Wayfold::Agent 8", DateTimeOffset.UtcNow ]
            Assert.That(File.GetUnixFileMode(path), Is.EqualTo(privateDir))
            Assert.That(File.GetUnixFileMode(todayLog path), Is.EqualTo(privateFile))
            Assert.That(File.GetUnixFileMode(Path.Combine(path, "agent-seen.json")), Is.EqualTo(privateFile))
            Assert.That(Directory.GetFiles(path, "*.tmp").Length, Is.Zero)
        finally
            setProcessUmask previous |> ignore
            Directory.Delete(root, true)

[<Test>]
let legacy_loose_permissions_are_repaired_without_losing_history () =
    if unix() then
        let path = tempDirectory()
        try
            let file = todayLog path
            let seen = Path.Combine(path, "agent-seen.json")
            let stamp = DateTimeOffset.UtcNow
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(snapshot "existing" "Completed") + "\n")
            File.WriteAllText(seen, System.Text.Json.JsonSerializer.Serialize(dict [ "Wayfold::Agent 8", stamp ]))
            File.SetUnixFileMode(path, privateDir ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
                                                ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
            File.SetUnixFileMode(file, privateFile ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
            File.SetUnixFileMode(seen, privateFile ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
            use store = new Store(settings path (1024L * 1024L))
            Assert.That(File.GetUnixFileMode(path), Is.EqualTo(privateDir))
            Assert.That(File.GetUnixFileMode(file), Is.EqualTo(privateFile))
            Assert.That(File.GetUnixFileMode(seen), Is.EqualTo(privateFile))
            Assert.That(store.LoadRecent(20) |> List.map _.ActivityId, Is.EqualTo(box [ "existing" ]))
            Assert.That(store.LoadAgentSeen() |> Map.ofList |> Map.find "Wayfold::Agent 8", Is.EqualTo(stamp))
        finally
            Directory.Delete(path, true)

[<Test>]
let symbolic_link_to_activity_directory_is_rejected () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "activity")
        let outside = Path.Combine(root, "outside")
        Directory.CreateDirectory(outside) |> ignore
        Directory.CreateSymbolicLink(path, outside) |> ignore
        try
            Assert.Throws<UnauthorizedAccessException>(TestDelegate(fun () ->
                use _store = new Store(settings path 4096L)
                ())) |> ignore
            Assert.That(Directory.Exists(outside), Is.True)
        finally
            Directory.Delete(path)
            Directory.Delete(root, true)

[<Test>]
let existing_log_symlinks_are_rejected_including_dangling_links () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "activity")
        Directory.CreateDirectory(path) |> ignore
        let dest = Path.Combine(root, "nonexistent-private-target.jsonl")
        let log = todayLog path
        File.CreateSymbolicLink(log, dest) |> ignore
        try
            Assert.That(FileInfo(log).LinkTarget, Is.Not.Null)
            Assert.Throws<UnauthorizedAccessException>(TestDelegate(fun () ->
                use _store = new Store(settings path 4096L)
                ())) |> ignore
            Assert.That(File.Exists(dest), Is.False)
        finally
            Directory.Delete(root, true)

[<Test>]
let unsafe_agent_seen_symlink_cannot_be_read_or_replaced () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "activity")
        let outside = Path.Combine(root, "outside.txt")
        let stamp = DateTimeOffset.UtcNow
        File.WriteAllText(outside, "outside must remain intact")
        try
            use store = new Store(settings path 4096L)
            File.CreateSymbolicLink(Path.Combine(path, "agent-seen.json"), outside) |> ignore
            Assert.Throws<UnauthorizedAccessException>(TestDelegate(fun () ->
                store.LoadAgentSeen() |> ignore)) |> ignore
            Assert.Throws<IOException>(TestDelegate(fun () ->
                store.SaveAgentSeen [ "Wayfold::Agent 8", stamp ])) |> ignore
            Assert.That(File.ReadAllText(outside), Is.EqualTo("outside must remain intact"))
            Assert.That(Directory.GetFiles(path, "*.tmp").Length, Is.Zero)
            Assert.That(store.LastPersistenceError.IsSome, Is.True)
        finally
            Directory.Delete(root, true)

[<Test>]
let append_to_substituted_symlink_is_reported_without_touching_target () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "activity")
        let outside = Path.Combine(root, "outside.txt")
        File.WriteAllText(outside, "unchanged")
        try
            use store = new Store(settings path 4096L)
            File.CreateSymbolicLink(todayLog path, outside) |> ignore
            store.Append(snapshot "blocked" "Running")
            Assert.That(File.ReadAllText(outside), Is.EqualTo("unchanged"))
            Assert.That(store.LastPersistenceError.IsSome, Is.True)
        finally
            Directory.Delete(root, true)


[<Test>]
let reverse_replay_handles_rotated_segments_in_chronological_order () =
    let path = tempDirectory()
    try
        use store = new Store(settings path 1L)
        store.Append(snapshot "one" "Completed")
        store.Append(snapshot "two" "Running")
        store.Append(snapshot "three" "Completed")
        Assert.That(Directory.GetFiles(path, "activity-*.jsonl").Length, Is.EqualTo(3))
        let recent = store.LoadRecent(2)
        Assert.That(recent |> List.map _.ActivityId, Is.EqualTo(box [ "two"; "three" ]))
        Assert.That(recent.Head.Status, Is.EqualTo("Interrupted"))
    finally
        Directory.Delete(path, true)

[<Test>]
let reverse_replay_picks_newest_duplicate_activity_state_across_rotations () =
    let path = tempDirectory()
    try
        use store = new Store(settings path 1L)
        store.Append(snapshot "one" "Running")
        store.Append(snapshot "two" "Running")
        store.Append({ snapshot "one" "Completed" with Result = "exit 0" })
        store.Append(snapshot "three" "Failed")
        let recent = store.LoadRecent(3)
        Assert.That(recent |> List.map _.ActivityId, Is.EqualTo(box [ "two"; "one"; "three" ]))
        let first = recent |> List.find (fun e -> e.ActivityId = "one")
        let second = recent |> List.find (fun e -> e.ActivityId = "two")
        Assert.That(first.Status, Is.EqualTo("Completed"))
        Assert.That(second.Status, Is.EqualTo("Interrupted"))
    finally
        Directory.Delete(path, true)

[<Test>]
let large_historical_prefix_is_not_read_when_distinct_tail_is_available () =
    let path = tempDirectory()
    try
        let filename = todayLog path
        do
            use output = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            // Sparse prefix: reading forward would scan 256 MiB and
            // materialize an enormous malformed first JSON line.
            output.SetLength(256L * 1024L * 1024L)
            output.Position <- output.Length
            for i in 0 .. 79 do
                let row = System.Text.Json.JsonSerializer.Serialize(snapshot $"latest-{i:D3}" "Completed")
                let bytes = System.Text.Encoding.UTF8.GetBytes(row + "\n")
                output.Write(bytes)
        use store = new Store(settings path (512L * 1024L * 1024L))
        let recent = store.LoadRecent(20)
        Assert.That(recent |> List.map _.ActivityId,
                    Is.EqualTo(box [ for i in 60 .. 79 -> $"latest-{i:D3}" ]))
        Assert.That(store.LastReplayBytesScanned, Is.LessThan(256L * 1024L))
    finally
        Directory.Delete(path, true)

[<Test>]
let huge_malformed_trailing_record_does_not_prevent_replay () =
    let path = tempDirectory()
    try
        let filename = todayLog path
        let json = System.Text.Json.JsonSerializer.Serialize(snapshot "valid" "Running")
        File.WriteAllText(filename, json + "\n" + String('x', 600 * 1024) + "\n")
        use store = new Store(settings path (1024L * 1024L))
        let recent = store.LoadRecent(1)
        Assert.That(recent |> List.map _.ActivityId, Is.EqualTo(box [ "valid" ]))
        Assert.That(recent.Head.Status, Is.EqualTo("Interrupted"))
        Assert.That(store.LastReplayBytesScanned, Is.LessThan(1024L * 1024L))
    finally
        Directory.Delete(path, true)

[<Test>]
let reverse_replay_bounds_work_with_too_few_distinct_activities () =
    let path = tempDirectory()
    try
        let filename = todayLog path
        do
            use output = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            output.SetLength(64L * 1024L * 1024L)
            output.Position <- output.Length
            output.WriteByte(10uy)
            let json = System.Text.Json.JsonSerializer.Serialize(snapshot "single" "Completed")
            let bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n")
            output.Write(bytes)
        use store = new Store(settings path (128L * 1024L * 1024L))
        let recent = store.LoadRecent(20)
        Assert.That(recent |> List.map _.ActivityId, Is.EqualTo(box [ "single" ]))
        Assert.That(store.LastReplayBytesScanned, Is.LessThanOrEqualTo(32L * 1024L * 1024L))
    finally
        Directory.Delete(path, true)


[<Test>]
let replay_preserves_start_chronology_when_older_operation_finishes_later () =
    let path = tempDirectory()
    try
        let start = DateTimeOffset.UtcNow.AddMinutes(-10.)
        use store = new Store(settings path 1L)
        store.Append({ snapshot "old" "Running" with StartedAt = start })
        store.Append({ snapshot "new" "Running" with StartedAt = start.AddMinutes(1.) })
        store.Append({ snapshot "old" "Completed" with StartedAt = start; Result = "exit 0" })
        let entries = store.LoadRecent(2)
        Assert.That(entries |> List.map _.ActivityId, Is.EqualTo(box [ "old"; "new" ]))
        Assert.That(entries.Head.Status, Is.EqualTo("Completed"))
        Assert.That(entries.Tail.Head.Status, Is.EqualTo("Interrupted"))
    finally Directory.Delete(path, true)

[<Test>]
let tui_retries_agent_seen_after_failed_atomic_snapshot () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "activity")
        let target = Path.Combine(root, "external")
        File.WriteAllText(target, "private")
        try
            use store = new Store(settings path 4096L)
            let tui = Client.ConsoleTui.ConsoleTui(activityLog = store)
            let seen = Path.Combine(path, "agent-seen.json")
            File.CreateSymbolicLink(seen, target) |> ignore
            tui.RecordAgentSeen("Wayfold", "Agent 8")
            Assert.That(File.ReadAllText(target), Is.EqualTo("private"))
            File.Delete(seen)
            tui.RecordAgentSeen("Wayfold", "Agent 8")
            Assert.That(store.LoadAgentSeen() |> Map.ofList |> Map.containsKey "Wayfold::Agent 8", Is.True)
        finally Directory.Delete(root, true)

[<Test>]
let agent_seen_load_refuses_unbounded_json_state () =
    let path = tempDirectory()
    try
        let seen = Path.Combine(path, "agent-seen.json")
        File.WriteAllBytes(seen, Array.zeroCreate<byte> (4 * 1024 * 1024 + 1))
        use store = new Store(settings path 4096L)
        Assert.Throws<InvalidDataException>(TestDelegate(fun () ->
            store.LoadAgentSeen() |> ignore)) |> ignore
        Assert.That(store.LastPersistenceError.IsSome, Is.True)
    finally Directory.Delete(path, true)


[<Test>]
let malformed_legacy_log_filename_is_secured_but_not_replayed () =
    if unix() then
        let path = tempDirectory()
        try
            let malformed = Path.Combine(path, "activity-legacy-unknown.jsonl")
            let current = todayLog path
            File.WriteAllText(malformed, "{\"secret\":\"legacy\"}\n")
            File.SetUnixFileMode(malformed, privateFile ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
            File.WriteAllText(current, System.Text.Json.JsonSerializer.Serialize(snapshot "current" "Completed") + "\n")
            use store = new Store(settings path 4096L)
            Assert.That(File.GetUnixFileMode(malformed), Is.EqualTo(privateFile))
            Assert.That(store.LoadRecent(20) |> List.map _.ActivityId, Is.EqualTo(box [ "current" ]))
        finally Directory.Delete(path, true)

[<Test>]
let malformed_legacy_log_symlink_is_rejected_before_filename_filter () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "activity")
        Directory.CreateDirectory(path) |> ignore
        let outside = Path.Combine(root, "private")
        File.WriteAllText(outside, "unchanged")
        File.CreateSymbolicLink(Path.Combine(path, "activity-legacy-unknown.jsonl"), outside) |> ignore
        try
            Assert.Throws<UnauthorizedAccessException>(TestDelegate(fun () ->
                use _store = new Store(settings path 4096L)
                ())) |> ignore
            Assert.That(File.ReadAllText(outside), Is.EqualTo("unchanged"))
        finally Directory.Delete(root, true)


[<Test>]
let directory_replaced_with_symlink_after_store_creation_is_rejected_on_load () =
    if unix() then
        let root = tempDirectory()
        let path = Path.Combine(root, "activity")
        let outside = Path.Combine(root, "outside")
        Directory.CreateDirectory(outside) |> ignore
        try
            use store = new Store(settings path 4096L)
            Directory.Delete(path)
            Directory.CreateSymbolicLink(path, outside) |> ignore
            Assert.Throws<UnauthorizedAccessException>(TestDelegate(fun () ->
                store.LoadRecent(20) |> ignore)) |> ignore
            Assert.Throws<UnauthorizedAccessException>(TestDelegate(fun () ->
                store.LoadAgentSeen() |> ignore)) |> ignore
            Assert.That(Directory.Exists(outside), Is.True)
        finally
            if DirectoryInfo(path).LinkTarget <> null then Directory.Delete(path)
            Directory.Delete(root, true)


[<Test>]
let legacy_utf8_bom_and_crlf_activity_file_is_replayed () =
    let path = tempDirectory()
    try
        let filename = todayLog path
        let json = System.Text.Json.JsonSerializer.Serialize(snapshot "legacy-bom" "Completed")
        File.WriteAllBytes(filename,
            Array.concat [
                System.Text.UTF8Encoding(true).GetPreamble()
                System.Text.Encoding.UTF8.GetBytes(json + "\r\n")
            ])
        use store = new Store(settings path (1024L * 1024L))
        let entries = store.LoadRecent(20)
        Assert.That(entries |> List.map _.ActivityId, Is.EqualTo(box [ "legacy-bom" ]))
        Assert.That(entries.Head.Status, Is.EqualTo("Completed"))
    finally Directory.Delete(path, true)

[<Test>]
let reverse_reader_handles_multibyte_text_across_block_boundaries () =
    let path = tempDirectory()
    try
        let detail = String('ø', 10_000) + " 🐈"
        let record = { snapshot "unicode-block" "Completed" with Detail = detail }
        File.WriteAllText(todayLog path,
            System.Text.Json.JsonSerializer.Serialize(record) + "\n",
            System.Text.UTF8Encoding(false))
        use store = new Store(settings path (1024L * 1024L))
        let entries = store.LoadRecent(1)
        Assert.That(entries.Length, Is.EqualTo(1))
        Assert.That(entries.Head.Detail, Is.EqualTo(detail))
    finally Directory.Delete(path, true)


[<Test>]
let recent_selection_includes_newly_finished_long_running_activity () =
    let path = tempDirectory()
    try
        let started = DateTimeOffset.UtcNow.AddHours(-2.)
        use store = new Store(settings path 1L)
        store.Append({ snapshot "long-running" "Running" with StartedAt = started })
        store.Append({ snapshot "middle" "Completed" with StartedAt = started.AddMinutes(30.) })
        store.Append({ snapshot "new" "Completed" with StartedAt = started.AddMinutes(60.) })
        store.Append({ snapshot "long-running" "Completed" with StartedAt = started; Result = "finished now" })
        let entries = store.LoadRecent(2)
        // Recently completed work remains visible after restart, even if
        // another activity started more recently. Visible rows remain in
        // original start-time order rather than completion order.
        Assert.That(entries |> List.map _.ActivityId, Is.EqualTo(box [ "long-running"; "new" ]))
        Assert.That(entries.Head.Result, Is.EqualTo("finished now"))
    finally Directory.Delete(path, true)
