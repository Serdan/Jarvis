module OriginalBrowserJournalTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Microsoft.Data.Sqlite
open Jarvis.WorkQueues
open Client.OriginalBrowserJournal
open Client.WorkQueueSchedulerHostPreview

let private success (result: Result<'a,string>) =
    match result with
    | Ok value -> value
    | Error error -> failwith error

let private rejected expected (result: Result<'a,string>) =
    match result with
    | Error error -> Assert.That(error, Does.Contain expected)
    | Ok _ -> Assert.Fail("Expected fail-closed journal rejection.")

let private withStore action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-wq03-original-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        OriginalBrowserJournal.provisionTrustedHostRoot root |> success |> ignore
        action root (OriginalBrowserJournal.openExisting root |> success)
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private intent operationId launchId =
    { OperationId = operationId
      LaunchId = launchId
      ProjectId = "project-123"
      WorkId = "work-321"
      WorkGeneration = 3
      RoleId = "Worker"
      RoleVersion = 5
      RequestFingerprint = String.replicate 64 "a"
      Provenance =
        { AccountHead = String.replicate 40 "b"
          ProjectHead = String.replicate 40 "c"
          SourceRef = Some "refs/heads/implementation"
          SourceCommit = Some(String.replicate 40 "d") } }

let private originalId () =
    // Preserve the real WQ08 ID format, not a newly invented opaque GUID.
    "wq08:dispatch:" + Guid.NewGuid().ToString("N") + String.replicate 32 "f"

[<Test>]
let marker_is_committed_before_fake_callback_and_prevents_second_send () =
    withStore (fun root store ->
        let original = intent (originalId ()) (Guid.NewGuid().ToString("D"))
        let mutable fakeCalls = 0
        let fakeProvider () =
            fakeCalls <- fakeCalls + 1
            let before = OriginalBrowserJournal.findOriginal store original.OperationId |> success
            Assert.That(before.IsSome, Is.True)
            Assert.That(before.Value.Intent, Is.EqualTo(original))
            Assert.That(before.Value.Outcome.IsNone, Is.True)
            OriginalBrowserOutcome.Accepted "fake-provider-receipt-1"
        let attempt () =
            match OriginalBrowserJournal.tryMarkOriginal store original |> success with
            | OriginalMarkResult.Marked ->
                let outcome = fakeProvider ()
                OriginalBrowserJournal.recordOriginalOutcome store original outcome |> success
                outcome
            | OriginalMarkResult.Existing previous ->
                previous.Outcome |> Option.defaultValue (OriginalBrowserOutcome.Unknown "Unsettled original")
        Assert.That(attempt (), Is.EqualTo(OriginalBrowserOutcome.Accepted "fake-provider-receipt-1"))
        let restarted = OriginalBrowserJournal.openExisting root |> success
        Assert.That(attempt (), Is.EqualTo(OriginalBrowserOutcome.Accepted "fake-provider-receipt-1"))
        Assert.That(fakeCalls, Is.EqualTo 1)
        let originalByLaunch = OriginalBrowserJournal.findByLaunch restarted original.LaunchId |> success
        Assert.That(originalByLaunch.IsSome, Is.True)
        Assert.That(originalByLaunch.Value.Intent.OperationId, Is.EqualTo original.OperationId)
        Assert.That(originalByLaunch.Value.Outcome, Is.EqualTo(Some(OriginalBrowserOutcome.Accepted "fake-provider-receipt-1")))
        let parsed, guid = Guid.TryParse(OriginalBrowserJournal.operationUuid original.OperationId)
        Assert.That(parsed && guid <> Guid.Empty, Is.True)
        Assert.That(OriginalBrowserJournal.originalIntentSha256 original, Has.Length.EqualTo 64))

[<Test>]
let unknown_response_and_restart_never_justify_second_send () =
    withStore (fun root store ->
        let original = intent (originalId ()) (Guid.NewGuid().ToString("D"))
        Assert.That(OriginalBrowserJournal.tryMarkOriginal store original |> success, Is.EqualTo OriginalMarkResult.Marked)
        // Simulate termination between a browser invocation and recording its response.
        let restarted = OriginalBrowserJournal.openExisting root |> success
        let existing = OriginalBrowserJournal.tryMarkOriginal restarted original |> success
        match existing with
        | OriginalMarkResult.Marked -> Assert.Fail("A second send was authorized after restart.")
        | OriginalMarkResult.Existing record ->
            Assert.That(record.Intent.OperationId, Is.EqualTo original.OperationId)
            Assert.That(record.Outcome.IsNone, Is.True)
        OriginalBrowserJournal.recordOriginalOutcome restarted original
            (OriginalBrowserOutcome.Unknown "Provider response lost") |> success
        let reloaded = OriginalBrowserJournal.openExisting root |> success
        let record = OriginalBrowserJournal.findOriginal reloaded original.OperationId |> success
        Assert.That(record.Value.Outcome, Is.EqualTo(Some(OriginalBrowserOutcome.Unknown "Provider response lost")))
        rejected "immutable" (OriginalBrowserJournal.recordOriginalOutcome reloaded original
            (OriginalBrowserOutcome.Accepted "unverified-later-claim")))

[<Test>]
let competing_marks_and_foreign_identity_conflicts_are_fail_closed () =
    withStore (fun _ store ->
        let original = intent (originalId ()) (Guid.NewGuid().ToString("D"))
        let tasks = [| Task.Run(fun () -> OriginalBrowserJournal.tryMarkOriginal store original)
                       Task.Run(fun () -> OriginalBrowserJournal.tryMarkOriginal store original) |]
        Task.WaitAll(tasks |> Array.map (fun task -> task :> Task))
        let outcomes = tasks |> Array.map (fun task -> success task.Result)
        Assert.That(outcomes |> Array.filter ((=) OriginalMarkResult.Marked) |> Array.length, Is.EqualTo 1)
        Assert.That(outcomes |> Array.filter (function OriginalMarkResult.Existing _ -> true | _ -> false) |> Array.length, Is.EqualTo 1)
        rejected "IdentityConflict" (OriginalBrowserJournal.tryMarkOriginal store { original with ProjectId = "foreign-project" })
        rejected "IdentityConflict" (OriginalBrowserJournal.tryMarkOriginal store
            { original with OperationId = originalId () })
        rejected "IdentityConflict" (OriginalBrowserJournal.recordOriginalOutcome store
            { original with RoleVersion = 6 } (OriginalBrowserOutcome.Accepted "no-valid-receipt"))
        rejected "marker is missing" (OriginalBrowserJournal.recordOriginalOutcome store
            (intent (originalId ()) (Guid.NewGuid().ToString("D"))) (OriginalBrowserOutcome.Accepted "false"))
        rejected "nonempty" (OriginalBrowserJournal.recordOriginalOutcome store original
            (OriginalBrowserOutcome.Accepted "")))

[<Test>]
let missing_store_corruption_and_tampered_intent_are_not_no_effect () =
    withStore (fun root store ->
        let original = intent (originalId ()) (Guid.NewGuid().ToString("D"))
        OriginalBrowserJournal.tryMarkOriginal store original |> success |> ignore
        let db = Path.Combine(root, "original-browser-v1.sqlite")
        use connection = new SqliteConnection("Data Source=" + db + ";Pooling=False")
        connection.Open()
        use update = connection.CreateCommand()
        update.CommandText <- "UPDATE originals SET intent_sha='forged' WHERE operation_id=$id;"
        update.Parameters.AddWithValue("$id", original.OperationId) |> ignore
        Assert.That(update.ExecuteNonQuery(), Is.EqualTo 1)
        connection.Close()
        rejected "inventory diverged" (OriginalBrowserJournal.findOriginal store original.OperationId)
        File.WriteAllText(db, "not a sqlite database")
        rejected "RecoveryBlocked" (OriginalBrowserJournal.openExisting root)
        File.Delete db
        rejected "missing" (OriginalBrowserJournal.openExisting root)
        rejected "missing" (OriginalBrowserJournal.findByLaunch store original.LaunchId))

[<Test>]
let removed_sqlite_original_cannot_be_replayed_after_rollback () =
    withStore (fun root store ->
        let original = intent (originalId ()) (Guid.NewGuid().ToString("D"))
        Assert.That(OriginalBrowserJournal.tryMarkOriginal store original |> success, Is.EqualTo OriginalMarkResult.Marked)
        let db = Path.Combine(root, "original-browser-v1.sqlite")
        use connection = new SqliteConnection("Data Source=" + db + ";Pooling=False")
        connection.Open()
        use delete = connection.CreateCommand()
        delete.CommandText <- "DELETE FROM originals WHERE operation_id=$id;"
        delete.Parameters.AddWithValue("$id", original.OperationId) |> ignore
        Assert.That(delete.ExecuteNonQuery(), Is.EqualTo 1)
        connection.Close()
        rejected "inventory diverged" (OriginalBrowserJournal.findOriginal store original.OperationId)
        rejected "inventory diverged" (OriginalBrowserJournal.tryMarkOriginal store original)
        rejected "inventory diverged" (OriginalBrowserJournal.openExisting root))

[<Test>]
let original_read_adapter_is_explicitly_opted_in_and_read_only () =
    withStore (fun _ store ->
        let original = intent (originalId ()) (Guid.NewGuid().ToString("D"))
        OriginalBrowserJournal.tryMarkOriginal store original |> success |> ignore
        OriginalBrowserJournal.recordOriginalOutcome store original
            (OriginalBrowserOutcome.DefinitiveNoEffect "Provider rejected before submission") |> success
        let account = Unchecked.defaultof<Client.AccountCapacityLedger.AccountBinding>
        let project = Unchecked.defaultof<Client.LedgerStore.LedgerBinding>
        let defaultOff = WorkQueueSchedulerHostPreview.realReadPorts account project
        rejected "not provisioned" (defaultOff.ReadOriginal original.LaunchId)
        let bound = WorkQueueSchedulerHostPreview.realReadPortsWithOriginal account project (Some store)
        let result = bound.ReadOriginal original.LaunchId |> success
        Assert.That(result.Value.Intent.OperationId, Is.EqualTo original.OperationId)
        Assert.That(result.Value.Outcome,
                    Is.EqualTo(Some(OriginalBrowserOutcome.DefinitiveNoEffect "Provider rejected before submission")))
        match bound.ReadOriginal "not-a-launch" with
        | Ok None -> ()
        | other -> Assert.Fail(sprintf "Expected no original for unrelated launch: %A" other))

[<Test>]
let independent_dotnet_process_observes_exact_original_marker_and_receipt () =
    withStore (fun root store ->
        let original = intent (originalId ()) (Guid.NewGuid().ToString("D"))
        OriginalBrowserJournal.tryMarkOriginal store original |> success |> ignore
        let script = Path.Combine(root, "read-probe.fsx")
        let assembly = typeof<OriginalBrowserJournalStore>.Assembly.Location
        let source =
            "#r @\"" + assembly + "\"\n" +
            "open Client.OriginalBrowserJournal\n" +
            "open Jarvis.WorkQueues\n" +
            "let root = System.Environment.GetEnvironmentVariable(\"WQ03_ROOT\")\n" +
            "let operation = System.Environment.GetEnvironmentVariable(\"WQ03_OPERATION\")\n" +
            "let expected = System.Environment.GetEnvironmentVariable(\"WQ03_EXPECTED\")\n" +
            "match OriginalBrowserJournal.openExisting root with\n" +
            "| Error message -> failwith message\n" +
            "| Ok store ->\n" +
            "    match OriginalBrowserJournal.findOriginal store operation with\n" +
            "    | Ok (Some entry) when entry.Intent.OperationId = operation &&\n" +
            "         (match expected, entry.Outcome with\n" +
            "          | \"pending\", None -> true\n" +
            "          | \"accepted\", Some (OriginalBrowserOutcome.Accepted \"receipt-77\") -> true\n" +
            "          | _ -> false) ->\n" +
            "        match OriginalBrowserJournal.tryMarkOriginal store entry.Intent with\n" +
            "        | Ok (OriginalMarkResult.Existing previous) when previous.Intent = entry.Intent -> printfn \"verified\"\n" +
            "        | other -> failwithf \"Original was wrongly re-marked: %A\" other\n" +
            "    | other -> failwithf \"Wrong original: %A\" other\n"
        File.WriteAllText(script, source)
        let runProbe expected =
            let info = ProcessStartInfo("dotnet")
            info.ArgumentList.Add "fsi"
            info.ArgumentList.Add "--exec"
            info.ArgumentList.Add script
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.Environment["WQ03_ROOT"] <- root
            info.Environment["WQ03_OPERATION"] <- original.OperationId
            info.Environment["WQ03_EXPECTED"] <- expected
            use child = Process.Start(info)
            let stdout = child.StandardOutput.ReadToEnd()
            let stderr = child.StandardError.ReadToEnd()
            if not (child.WaitForExit 60000) then
                child.Kill(true)
                Assert.Fail("Independent original browser journal probe timed out.")
            Assert.That(child.ExitCode, Is.Zero, stdout + "\n" + stderr)
            Assert.That(stdout, Does.Contain "verified")
        runProbe "pending"
        OriginalBrowserJournal.recordOriginalOutcome store original
            (OriginalBrowserOutcome.Accepted "receipt-77") |> success
        runProbe "accepted")
