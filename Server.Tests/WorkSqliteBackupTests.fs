module WorkSqliteBackupTests

open System
open System.IO
open System.Text
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkSnapshots
open Server

let private ok = function
    | Ok value -> value
    | Error reason -> failwithf "Unexpected failure: %A" reason

let private fail = function
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Unsafe backup/restore unexpectedly succeeded")

let private restored result =
    let receipt = result |> ok
    Assert.That(receipt.RequiresRecovery, Is.True,
                "Restore must never imply missing external effects are safe")
    receipt.CommittedSequence

let private fixture (test: WorkSqliteAuthority -> string -> string -> unit) =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-f1-backup-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    try
        let original = Path.Combine(folder, "source.sqlite")
        let restored = Path.Combine(folder, "restored.sqlite")
        let authority = WorkSqliteAuthority(original)
        authority.Initialize() |> ok |> ignore
        test authority original restored
    finally
        Directory.Delete(folder, true)

let private makeValue body =
    { BodyJson = body
      GraphVersion = 4L
      StageGeneration = 2L
      ClaimGeneration = 1L
      StopGeneration = 3L
      RoleVersion = 6L
      SourceSha = Some(String.replicate 40 "a")
      ReviewSha = Some(String.replicate 40 "b")
      IntegrationSha = Some(String.replicate 40 "c")
      ExternalEffect = WorkSqliteEffect.Unknown }

let private mutation project kind id version body =
    { Key = { ProjectId = project; Kind = kind; EntityId = id }
      ExpectedVersion = version
      Value = body }

let private transact rows capacities =
    { OperationId = Guid.NewGuid()
      Guards = []
      Records = rows
      Capacities = capacities }

let private updateCapacity account version maximum delta =
    { AccountId = account; ExpectedVersion = version
      Maximum = maximum; ReservedDelta = delta }

let private seed (authority: WorkSqliteAuthority) =
    let first =
        transact
            [ mutation "project-alpha" WorkSqliteRecordKind.Stage "stage-a" None
                (makeValue """{"state":"Available"}""")
              mutation "project-beta" WorkSqliteRecordKind.ExternalEffect "effect-b" None
                (makeValue """{"status":"Unknown"}""") ]
            [ updateCapacity "global" None (Some 3) 0 ]
    authority.Commit(first) |> ok |> ignore
    let second =
        transact
            [ mutation "project-alpha" WorkSqliteRecordKind.Stage "stage-a" (Some 1L)
                (makeValue """{"state":"Claimed"}""") ]
            [ updateCapacity "global" (Some 1L) None 1 ]
    authority.Commit(second) |> ok |> ignore
    first, second

[<Test>]
let empty_sqlite_snapshot_can_restore_and_preserve_zero_sequence () =
    fixture (fun authority original target ->
        let exported = WorkSqliteBackup.exportSnapshot original |> ok
        Assert.That(exported.Files.Keys |> Set.ofSeq,
                    Is.EqualTo(box (set [ "authority.json"; "manifest.json" ])))
        let replay = WorkSqliteBackup.restoreEmpty exported target |> restored
        Assert.That(replay, Is.EqualTo 0L)
        Assert.That((WorkSqliteAuthority(target)).CurrentSequence() |> ok, Is.EqualTo 0L)
        let again = WorkSqliteBackup.exportSnapshot target |> ok
        Assert.That(again.Files, Is.EqualTo(box exported.Files))
    )

[<Test>]
let two_project_account_capacity_receipts_and_source_fences_roundtrip_exactly () =
    fixture (fun authority original target ->
        let first, second = seed authority
        let bundle = WorkSqliteBackup.exportSnapshot original |> ok
        let another = WorkSqliteBackup.exportSnapshot original |> ok
        Assert.That(bundle.Files, Is.EqualTo(box another.Files))
        let text = Encoding.UTF8.GetString(bundle.Files.["authority.json"])
        Assert.That(text, Does.Contain("project-alpha"))
        Assert.That(text, Does.Contain("project-beta"))
        Assert.That(text, Does.Contain("unknown"))
        Assert.That(text, Does.Contain(String.replicate 40 "a"))
        Assert.That(WorkSqliteBackup.restoreEmpty bundle target |> restored, Is.EqualTo 2L)
        let restored = WorkSqliteAuthority(target)
        let stage = { ProjectId = "project-alpha"; Kind = WorkSqliteRecordKind.Stage; EntityId = "stage-a" }
        let record = restored.ReadRecord stage |> ok |> Option.get
        Assert.That(record.Version, Is.EqualTo 2L)
        Assert.That(record.Value.StopGeneration, Is.EqualTo 3L)
        Assert.That(record.Value.SourceSha, Is.EqualTo(Some(String.replicate 40 "a")))
        Assert.That(record.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Unknown)
        Assert.That((restored.ReadCapacity "global" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        Assert.That(restored.CurrentSequence() |> ok, Is.EqualTo 2L)
        let replay = restored.Commit(first) |> ok
        Assert.That(replay.Replayed, Is.True)
        Assert.That(replay.Sequence, Is.EqualTo 1L)
        let replay2 = restored.Commit(second) |> ok
        Assert.That(replay2.Replayed, Is.True)
        Assert.That(replay2.Sequence, Is.EqualTo 2L)
        Assert.That(WorkSqliteBackup.exportSnapshot target |> ok |> _.Files,
                    Is.EqualTo(box bundle.Files))
        // Non-destructive: target must be empty even when the bytes are identical.
        fail (WorkSqliteBackup.restoreEmpty bundle target)
    )

[<Test>]
let rejecting_corrupt_missing_extra_and_rehashed_but_surprising_content () =
    fixture (fun authority original target ->
        seed authority |> ignore
        let bundle = WorkSqliteBackup.exportSnapshot original |> ok
        let changed = Array.copy bundle.Files.["authority.json"]
        changed.[changed.Length / 2] <- changed.[changed.Length / 2] ^^^ 1uy
        fail (WorkSqliteBackup.restoreEmpty
                { Files = bundle.Files.Add("authority.json", changed) } target)
        fail (WorkSqliteBackup.restoreEmpty
                { Files = bundle.Files.Remove("authority.json") } target)
        fail (WorkSqliteBackup.restoreEmpty
                { Files = bundle.Files.Add("surprise.json", [| 0uy |]) } target)
        let text = Encoding.UTF8.GetString(bundle.Files.["authority.json"])
        let malicious = text.Replace("Claimed", "Hacked", StringComparison.Ordinal)
        // Even if attackers repair the checksum, noncanonical or invalid
        // record bodies are rejected by the typed projection.
        let modified = Encoding.UTF8.GetBytes(malicious)
        let sha = SHA256.HashData(modified) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()
        let oldHash = SHA256.HashData(bundle.Files.["authority.json"]) |> Convert.ToHexString
                      |> fun s -> s.ToLowerInvariant()
        let rewrittenManifest =
            Encoding.UTF8.GetString(bundle.Files.["manifest.json"]).Replace(oldHash, sha)
            |> Encoding.UTF8.GetBytes
        fail (WorkSqliteBackup.restoreEmpty
                { Files = bundle.Files.Add("authority.json", modified)
                                       .Add("manifest.json", rewrittenManifest) } target)
        Assert.That(File.Exists target, Is.False)
    )

[<Test>]
let arbitrary_body_fields_cannot_copy_plaintext_credentials_or_manual_grants () =
    fixture (fun authority original _ ->
        let token = "Bearer secret-scheduled-credential"
        let bad = transact [ mutation "project-one" WorkSqliteRecordKind.Stage "stage" None
                                (makeValue ($"""{{"state":"Available","label":"{token}"}}""")) ] []
        authority.Commit(bad) |> ok |> ignore
        fail (WorkSqliteBackup.exportSnapshot original)
        // Generic B accepts arbitrary JSON; export must not copy it even
        // through an otherwise allowlisted identifier-looking field.
        let another = transact [ mutation "project-one" WorkSqliteRecordKind.Role "manual-grant" None
                                  (makeValue """{"grant":"manual"}""") ] []
        authority.Commit(another) |> ok |> ignore
        fail (WorkSqliteBackup.exportSnapshot original)
    )

[<Test>]
let concurrent_writes_never_produce_torn_two_project_account_snapshots () =
    fixture (fun authority original target ->
        seed authority |> ignore
        use gate = new ManualResetEventSlim(false)
        let changes = Task.Run(fun () ->
            gate.Wait()
            for next in 2 .. 8 do
                let current = int64 next
                let change =
                    transact
                        [ mutation "project-alpha" WorkSqliteRecordKind.Stage "stage-a" (Some current)
                            (makeValue """{"state":"Claimed"}""")
                          mutation "project-beta" WorkSqliteRecordKind.ExternalEffect "effect-b" (Some (current-1L))
                            (makeValue """{"status":"Unknown"}""") ]
                        [ updateCapacity "global" (Some current) None 0 ]
                authority.Commit(change) |> ok |> ignore)
        gate.Set()
        for _ in 1 .. 8 do
            let bundle = WorkSqliteBackup.exportSnapshot original |> ok
            let transient = Path.Combine(Path.GetDirectoryName target, Guid.NewGuid().ToString("N") + ".sqlite")
            WorkSqliteBackup.restoreEmpty bundle transient |> restored |> ignore
            let db = WorkSqliteAuthority(transient)
            let a =
                db.ReadRecord { ProjectId = "project-alpha"; Kind = WorkSqliteRecordKind.Stage; EntityId = "stage-a" }
                |> ok |> Option.get
            let b =
                db.ReadRecord { ProjectId = "project-beta"; Kind = WorkSqliteRecordKind.ExternalEffect; EntityId = "effect-b" }
                |> ok |> Option.get
            let capacity = db.ReadCapacity "global" |> ok |> Option.get
            // First seed commits project A v2, B v1, capacity v2.
            // Each later atomic transaction advances all three.
            Assert.That(a.Version, Is.EqualTo(b.Version + 1L))
            Assert.That(capacity.Version, Is.EqualTo(a.Version))
            File.Delete transient
        changes.GetAwaiter().GetResult()
    )


type private FlakyGitSink() =
    let mutable online = false
    let mutable calls = 0
    member _.GoOnline() = online <- true
    member _.Calls = calls
    interface IWorkBackupGitSink with
        member _.PublishAsync(_sequence, _bundle, _cancellationToken) =
            calls <- calls + 1
            if online then Task.CompletedTask
            else Task.FromException(InvalidOperationException("Simulated Git outage"))

[<Test>]
let outbox_does_not_call_git_on_capture_and_retries_outage_without_losing_receipts () =
    fixture (fun authority original target ->
        seed authority |> ignore
        let sink = FlakyGitSink()
        let outbox = WorkBackupOutbox(original, target + ".spool", sink)
        let sequence = outbox.Capture() |> ok
        Assert.That(sequence, Is.EqualTo 2L)
        // Repeated unchanged captures are byte-identical, not duplicate jobs.
        Assert.That(outbox.Capture() |> ok, Is.EqualTo 2L)
        Assert.That(sink.Calls, Is.Zero)
        Assert.That(outbox.PendingSequences() |> ok, Is.EqualTo(box [ 2L ]))
        fail (outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult())
        Assert.That(sink.Calls, Is.EqualTo 1)
        Assert.That(outbox.PendingSequences() |> ok, Is.EqualTo(box [ 2L ]))
        Assert.That(authority.CurrentSequence() |> ok, Is.EqualTo 2L)
        sink.GoOnline()
        let published = outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult() |> ok
        Assert.That(published, Is.EqualTo(box [ 2L ]))
        Assert.That(sink.Calls, Is.EqualTo 2)
        Assert.That(outbox.PendingSequences() |> ok, Is.Empty)
        Assert.That(authority.CurrentSequence() |> ok, Is.EqualTo 2L)
    )

[<Test>]
let outbox_refuses_torn_spool_before_contacting_git () =
    fixture (fun authority original target ->
        seed authority |> ignore
        let sink = FlakyGitSink()
        let spool = target + ".spool"
        let outbox = WorkBackupOutbox(original, spool, sink)
        outbox.Capture() |> ok |> ignore
        let pending = Path.Combine(spool, "s-00000000000000000002")
        let payload = Path.Combine(pending, "authority.json")
        let bytes = File.ReadAllBytes payload
        bytes.[bytes.Length / 2] <- bytes.[bytes.Length / 2] ^^^ 1uy
        File.WriteAllBytes(payload, bytes)
        fail (outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult())
        Assert.That(sink.Calls, Is.Zero)
        Assert.That(outbox.PendingSequences() |> ok, Is.EqualTo(box [ 2L ]))
    )

let private runLocalGit directory args =
    let info = Diagnostics.ProcessStartInfo()
    info.FileName <- "git"
    info.WorkingDirectory <- directory
    info.UseShellExecute <- false
    info.RedirectStandardError <- true
    info.RedirectStandardOutput <- true
    for arg in args do info.ArgumentList.Add(arg)
    use proc = new Diagnostics.Process()
    proc.StartInfo <- info
    if not (proc.Start()) then failwith "git missing"
    let output = proc.StandardOutput.ReadToEnd()
    let err = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwithf "Local fake Git failure: %A" args
    output.Trim()

[<Test>]
let dedicated_git_checkout_publishes_only_backup_artifacts_and_replays_idempotently () =
    fixture (fun authority original target ->
        seed authority |> ignore
        let parent = Path.GetDirectoryName(target)
        let bare = Path.Combine(parent, "bare.git")
        let checkout = Path.Combine(parent, "backup-checkout")
        Directory.CreateDirectory checkout |> ignore
        runLocalGit parent [ "init"; "--bare"; bare ] |> ignore
        runLocalGit checkout [ "init"; "-b"; "backups" ] |> ignore
        runLocalGit checkout [ "-c"; "user.name=Test"; "-c"; "user.email=test@localhost";
                                "commit"; "--allow-empty"; "-m"; "initial" ] |> ignore
        runLocalGit checkout [ "remote"; "add"; "origin"; bare ] |> ignore
        let sink = WorkBackupGitCheckoutSink(checkout, "origin", "backups")
        let outbox = WorkBackupOutbox(original, target + ".spool", sink)
        outbox.Capture() |> ok |> ignore
        let pushed = outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult() |> ok
        Assert.That(pushed, Is.EqualTo(box [ 2L ]))
        let hash1 = runLocalGit checkout [ "rev-parse"; "HEAD" ]
        let remoteHash = runLocalGit parent [ "--git-dir"; bare; "rev-parse"; "refs/heads/backups" ]
        Assert.That(hash1, Is.EqualTo remoteHash)
        let manifest =
            runLocalGit checkout [ "show"; "HEAD:snapshots/s-00000000000000000002/manifest.json" ]
        Assert.That(manifest, Does.Contain("\"committedSequence\": 2"))
        // A repeated export/push creates no duplicate backup commit.
        outbox.Capture() |> ok |> ignore
        outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult() |> ok |> ignore
        Assert.That(runLocalGit checkout [ "rev-parse"; "HEAD" ], Is.EqualTo hash1)
        Assert.That(authority.CurrentSequence() |> ok, Is.EqualTo 2L)
    )


// A crash after the files were materialized (or partially staged) but before
// commit must be retryable without ever adopting an unrelated dirty checkout.
[<Test>]
let git_sink_recovers_exact_precommit_files_but_refuses_unrelated_dirty_changes () =
    fixture (fun authority original target ->
        seed authority |> ignore
        let parent = Path.GetDirectoryName(target)
        let bare = Path.Combine(parent, "retry-bare.git")
        let checkout = Path.Combine(parent, "retry-checkout")
        Directory.CreateDirectory checkout |> ignore
        runLocalGit parent [ "init"; "--bare"; bare ] |> ignore
        runLocalGit checkout [ "init"; "-b"; "backups" ] |> ignore
        runLocalGit checkout [ "-c"; "user.name=Test"; "-c"; "user.email=test@localhost";
                               "commit"; "--allow-empty"; "-m"; "initial" ] |> ignore
        runLocalGit checkout [ "remote"; "add"; "origin"; bare ] |> ignore

        let sink = WorkBackupGitCheckoutSink(checkout, "origin", "backups")
        let outbox = WorkBackupOutbox(original, target + ".spool", sink)
        outbox.Capture() |> ok |> ignore
        let bundle = WorkSqliteBackup.exportSnapshot original |> ok
        let path = "snapshots/s-00000000000000000002"
        let destination = Path.Combine(checkout, "snapshots", "s-00000000000000000002")
        Directory.CreateDirectory destination |> ignore
        for KeyValue(name, bytes) in bundle.Files do
            File.WriteAllBytes(Path.Combine(destination, name), bytes)
        // Simulate a crash with one file already added and the other untracked.
        runLocalGit checkout [ "add"; "--"; path + "/authority.json" ] |> ignore

        let unrelated = Path.Combine(checkout, "private-unrelated.txt")
        File.WriteAllText(unrelated, "must not be included in Git backup")
        fail (outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult())
        Assert.That(outbox.PendingSequences() |> ok, Is.EqualTo(box [ 2L ]))
        File.Delete unrelated

        let published = outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult() |> ok
        Assert.That(published, Is.EqualTo(box [ 2L ]))
        let changed = runLocalGit checkout [ "show"; "--format="; "--name-only"; "HEAD" ]
        Assert.That(changed, Does.Contain(path + "/authority.json"))
        Assert.That(changed, Does.Contain(path + "/manifest.json"))
        Assert.That(changed, Does.Not.Contain("private-unrelated.txt"))
        Assert.That(runLocalGit checkout [ "status"; "--porcelain" ], Is.Empty)
        let localHead = runLocalGit checkout [ "rev-parse"; "HEAD" ]
        let remoteHead = runLocalGit parent [ "--git-dir"; bare; "rev-parse"; "refs/heads/backups" ]
        Assert.That(remoteHead, Is.EqualTo localHead)
    )

[<Test>]
let git_sink_retries_push_failure_without_recommitting_local_snapshot () =
    fixture (fun authority original target ->
        seed authority |> ignore
        let parent = Path.GetDirectoryName(target)
        let bare = Path.Combine(parent, "late-remote.git")
        let unreachable = Path.Combine(parent, "missing-remote.git")
        let checkout = Path.Combine(parent, "push-retry-checkout")
        Directory.CreateDirectory checkout |> ignore
        runLocalGit parent [ "init"; "--bare"; bare ] |> ignore
        runLocalGit checkout [ "init"; "-b"; "backups" ] |> ignore
        runLocalGit checkout [ "-c"; "user.name=Test"; "-c"; "user.email=test@localhost";
                               "commit"; "--allow-empty"; "-m"; "initial" ] |> ignore
        runLocalGit checkout [ "remote"; "add"; "origin"; unreachable ] |> ignore

        let sink = WorkBackupGitCheckoutSink(checkout, "origin", "backups")
        let outbox = WorkBackupOutbox(original, target + ".spool", sink)
        outbox.Capture() |> ok |> ignore
        fail (outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult())
        Assert.That(outbox.PendingSequences() |> ok, Is.EqualTo(box [ 2L ]))
        let committed = runLocalGit checkout [ "rev-parse"; "HEAD" ]
        Assert.That(runLocalGit checkout [ "status"; "--porcelain" ], Is.Empty)

        runLocalGit checkout [ "remote"; "set-url"; "origin"; bare ] |> ignore
        let published = outbox.PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult() |> ok
        Assert.That(published, Is.EqualTo(box [ 2L ]))
        Assert.That(outbox.PendingSequences() |> ok, Is.Empty)
        Assert.That(runLocalGit checkout [ "rev-parse"; "HEAD" ], Is.EqualTo committed)
        let remoteHead = runLocalGit parent [ "--git-dir"; bare; "rev-parse"; "refs/heads/backups" ]
        Assert.That(remoteHead, Is.EqualTo committed)
    )

[<Test>]
let deleting_highest_sequence_rows_does_not_rewind_restore_audit_high_water () =
    fixture (fun authority original target ->
        let first, second = seed authority
        let third =
            transact [ mutation "project-alpha" WorkSqliteRecordKind.Role "role-c" None
                           (makeValue """{"state":"Ready"}""") ] []
        authority.Commit(third) |> ok |> ignore
        // A physical compaction can remove all rows while durable receipts
        // and audit still witness the authoritative high-water sequence.
        use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={original};Pooling=False")
        conn.Open()
        use delete = conn.CreateCommand()
        delete.CommandText <- "DELETE FROM work_records;"
        Assert.That(delete.ExecuteNonQuery(), Is.EqualTo 3)
        conn.Close()
        let bundle = WorkSqliteBackup.exportSnapshot original |> ok
        let result = WorkSqliteBackup.restoreEmpty bundle target |> restored
        Assert.That(result, Is.EqualTo 3L)
        let copied = WorkSqliteAuthority(target)
        Assert.That(copied.CurrentSequence() |> ok, Is.EqualTo 3L)
        Assert.That(copied.Commit(first) |> ok |> _.Replayed, Is.True)
        Assert.That(copied.Commit(second) |> ok |> _.Replayed, Is.True)
        Assert.That(copied.Commit(third) |> ok |> _.Replayed, Is.True)
    )

[<Test>]
let linux_directory_durability_barrier_fails_closed_for_nondirectories () =
    if not (OperatingSystem.IsLinux()) then
        Assert.Ignore("Linux directory-fsync syscall contract test")
    fixture (fun _ original target ->
        let folder = Path.GetDirectoryName original
        // A successful backup acknowledgement depends on this actual
        // filesystem-level barrier, not a FileStream.Flush-only surrogate.
        WorkBackupDurability.syncDirectory folder
        Assert.Throws<IOException>(
            TestDelegate(fun () -> WorkBackupDurability.syncDirectory original))
        |> ignore
        Assert.Throws<IOException>(
            TestDelegate(fun () -> WorkBackupDurability.syncDirectory (target + ".missing")))
        |> ignore
    )

[<Test>]
let outbox_refuses_missing_spool_parent_instead_of_claiming_a_durable_capture () =
    fixture (fun authority original target ->
        seed authority |> ignore
        let spool = Path.Combine(target + ".absent-parent", "spool")
        let outbox = WorkBackupOutbox(original, spool, FlakyGitSink())
        fail (outbox.Capture())
        Assert.That(Directory.Exists spool, Is.False)
        Assert.That(authority.CurrentSequence() |> ok, Is.EqualTo 2L)
    )
