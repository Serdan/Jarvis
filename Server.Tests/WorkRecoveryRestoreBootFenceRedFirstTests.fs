module WorkRecoveryRestoreBootFenceRedFirstTests

open System
open System.IO
open NUnit.Framework
open Server
open Jarvis.WorkSnapshots

// G1b RED-FIRST contract: only disposable, private SQLite files.
// No host, Scheduler dispatch, credential, manual grant or restore cutover.
// These tests intentionally exercise the missing persistent pre-serve fence;
// the existing F1 RequiresRecovery receipt alone cannot enforce the assertions.

let private ok = function
    | Ok value -> value
    | Error error -> Assert.Fail(sprintf "Unexpected fixture failure: %A" error); Unchecked.defaultof<_>

let private key project kind id =
    { ProjectId = project; Kind = kind; EntityId = id }

let private value body effect source =
    { BodyJson = body
      GraphVersion = 1L
      StageGeneration = 1L
      ClaimGeneration = 0L
      StopGeneration = 0L
      RoleVersion = 1L
      SourceSha = source
      ReviewSha = None
      IntegrationSha = None
      ExternalEffect = effect }

let private newRow project kind id body effect source =
    { Key = key project kind id
      ExpectedVersion = None
      Value = value body effect source }

let private initialCommand () =
    { OperationId = Guid.NewGuid()
      Guards = []
      Records =
        [ newRow "alpha" WorkSqliteRecordKind.Project "authority"
            """{"state":"Open"}""" WorkSqliteEffect.NoneRecorded None
          newRow "alpha" WorkSqliteRecordKind.Stage "stage-alpha"
            """{"state":"Available"}""" WorkSqliteEffect.Prepared (Some(String.replicate 40 "a"))
          newRow "alpha" WorkSqliteRecordKind.Launch "launch-alpha"
            """{"state":"Prepared"}""" WorkSqliteEffect.Prepared (Some(String.replicate 40 "a"))
          newRow "alpha" WorkSqliteRecordKind.ExternalEffect "effect-at-n"
            """{"status":"Unknown"}""" WorkSqliteEffect.Unknown (Some(String.replicate 40 "a"))
          newRow "beta" WorkSqliteRecordKind.Project "authority"
            """{"state":"Open"}""" WorkSqliteEffect.NoneRecorded None
          newRow "beta" WorkSqliteRecordKind.Stage "stage-beta"
            """{"state":"Available"}""" WorkSqliteEffect.NoneRecorded (Some(String.replicate 40 "b"))
          newRow "beta" WorkSqliteRecordKind.Role "role-beta"
            """{"state":"Ready"}""" WorkSqliteEffect.NoneRecorded None ]
      Capacities =
        [ { AccountId = "shared-account"
            ExpectedVersion = None
            Maximum = Some 3
            ReservedDelta = 1 } ] }

let private getRow (store: WorkSqliteAuthority) project kind id =
    store.ReadRecord (key project kind id) |> ok |> Option.get

let private admissionCommand (store: WorkSqliteAuthority) project =
    let p = getRow store project WorkSqliteRecordKind.Project "authority"
    let stage = getRow store project WorkSqliteRecordKind.Stage ("stage-" + project)
    let account = store.ReadCapacity "shared-account" |> ok |> Option.get
    { OperationId = Guid.NewGuid()
      Guards =
        [ { Key = p.Key
            ExpectedVersion = p.Version
            GraphVersion = Some p.Value.GraphVersion
            StageGeneration = None
            ClaimGeneration = None
            StopGeneration = Some p.Value.StopGeneration
            RoleVersion = None
            SourceSha = p.Value.SourceSha } ]
      Records =
        [ { Key = stage.Key
            ExpectedVersion = Some stage.Version
            Value = { stage.Value with BodyJson = """{"state":"Claimed"}""" } } ]
      Capacities =
        [ { AccountId = account.AccountId
            ExpectedVersion = Some account.Version
            Maximum = None
            ReservedDelta = 1 } ] }

let private assertRecoveryDenied (store: WorkSqliteAuthority) command =
    let priorSequence = store.CurrentSequence() |> ok
    match store.Commit command with
    | Error _ -> ()
    | Ok accepted ->
        Assert.Fail(
            sprintf "RED G1b: pre-serve Recovery lock absent; ordinary admission was accepted at sequence %d, replay=%b"
                accepted.Sequence accepted.Replayed)
    Assert.That(store.CurrentSequence() |> ok, Is.EqualTo priorSequence,
                "A rejected pre-serve admission must not consume an audit/receipt sequence")

let private withStaleRestore f =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-g1b-red-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    try
        let sourcePath = Path.Combine(folder, "source.sqlite")
        let targetPath = Path.Combine(folder, "restored.sqlite")
        let source = WorkSqliteAuthority(sourcePath)
        source.Initialize() |> ok |> ignore
        let originalCommand = initialCommand ()
        let n = (source.Commit originalCommand |> ok).Sequence
        let bundle = WorkSqliteBackup.exportSnapshot sourcePath |> ok
        Assert.That(WorkSqliteBackup.validateSnapshot bundle |> ok, Is.EqualTo n)
        // The source moves to N+1 *after* the immutable backup: its additional
        // prepared/unknown side effect is not evidence that exists inside N.
        let newer =
            { OperationId = Guid.NewGuid()
              Guards = []
              Records =
                [ newRow "alpha" WorkSqliteRecordKind.ExternalEffect "effect-after-n"
                    """{"status":"Unknown"}""" WorkSqliteEffect.Unknown (Some(String.replicate 40 "a")) ]
              Capacities = [] }
        let next = source.Commit newer |> ok
        Assert.That(next.Sequence, Is.EqualTo(n + 1L))
        let receipt = WorkSqliteBackup.restoreEmpty bundle targetPath |> ok
        Assert.That(receipt.RequiresRecovery, Is.True)
        Assert.That(receipt.CommittedSequence, Is.EqualTo n)
        // Fresh instance simulates a process restart; no in-memory receipt
        // may stand in for an authority lock in the sealed SQLite bytes.
        let reopened = WorkSqliteAuthority(targetPath)
        Assert.That(reopened.CurrentSequence() |> ok, Is.EqualTo n)
        f source reopened originalCommand n
    finally
        Directory.Delete(folder, true)

[<Test>]
let sealed_stale_restore_must_lock_fresh_admission_before_recovery_seeding () =
    withStaleRestore (fun source restored _ n ->
        Assert.That(source.CurrentSequence() |> ok, Is.EqualTo(n + 1L))
        Assert.That((restored.ReadRecord (key "alpha" WorkSqliteRecordKind.ExternalEffect "effect-after-n") |> ok).IsNone,
                    Is.True, "N has no evidence about the real N+1 effect")
        let prepared = getRow restored "alpha" WorkSqliteRecordKind.Launch "launch-alpha"
        Assert.That(prepared.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Prepared)
        let unknown = getRow restored "alpha" WorkSqliteRecordKind.ExternalEffect "effect-at-n"
        Assert.That(unknown.Value.ExternalEffect, Is.EqualTo WorkSqliteEffect.Unknown)
        assertRecoveryDenied restored (admissionCommand restored "alpha"))

[<Test>]
let stale_restore_must_fence_other_project_on_same_account_capacity () =
    withStaleRestore (fun _ restored _ _ ->
        // Uncertainty in alpha must not leave beta runnable through the same
        // global account capacity before a verified, complete reconciliation.
        let before = restored.ReadCapacity "shared-account" |> ok |> Option.get
        assertRecoveryDenied restored (admissionCommand restored "beta")
        let after = restored.ReadCapacity "shared-account" |> ok |> Option.get
        Assert.That(after.Version, Is.EqualTo before.Version)
        Assert.That(after.Reserved, Is.EqualTo before.Reserved))

[<Test>]
let historical_receipt_and_same_uuid_replay_do_not_authorize_new_dispatch () =
    withStaleRestore (fun _ restored original _ ->
        let old = restored.ReadReceipt original.OperationId |> ok |> Option.get
        Assert.That(old.Replayed, Is.True,
                    "A persisted receipt is historical evidence, not a fresh dispatch authorization")
        let replay = restored.Commit original |> ok
        Assert.That(replay.Replayed, Is.True,
                    "Historical receipt is retained for forensics, not consumed as a fresh admission")
        Assert.That(replay.Sequence, Is.EqualTo old.Sequence)
        assertRecoveryDenied restored (admissionCommand restored "beta"))

[<Test>]
let even_empty_backup_requires_a_durable_pre_serve_recovery_gate () =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-g1b-empty-red-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    try
        let original = Path.Combine(folder, "empty.sqlite")
        let destination = Path.Combine(folder, "restored.sqlite")
        let source = WorkSqliteAuthority(original)
        source.Initialize() |> ok |> ignore
        let bundle = WorkSqliteBackup.exportSnapshot original |> ok
        let receipt = WorkSqliteBackup.restoreEmpty bundle destination |> ok
        Assert.That(receipt.RequiresRecovery, Is.True)
        let restored = WorkSqliteAuthority(destination)
        let create =
            { OperationId = Guid.NewGuid()
              Guards = []
              Records = [ newRow "same-name" WorkSqliteRecordKind.Project "authority"
                              """{"state":"Open"}""" WorkSqliteEffect.NoneRecorded None ]
              Capacities = [ { AccountId = "shared-account"; ExpectedVersion = None
                               Maximum = Some 1; ReservedDelta = 0 } ] }
        // A zero-row snapshot does not prove that no newer source, launch,
        // same-name project on another device, or external action exists.
        assertRecoveryDenied restored create
    finally
        Directory.Delete(folder, true)

[<Test>]
let ordinary_intact_sqlite_restart_is_not_a_stale_restore () =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-g1b-intact-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    try
        let path = Path.Combine(folder, "intact.sqlite")
        let first = WorkSqliteAuthority(path)
        first.Initialize() |> ok |> ignore
        first.Commit(initialCommand ()) |> ok |> ignore
        // Ordinary restart on the same durable DB must not invent a Recovery
        // barrier. Only a restored/quarantined authority has that obligation.
        let reopened = WorkSqliteAuthority(path)
        let command = admissionCommand reopened "beta"
        let accepted = reopened.Commit command |> ok
        Assert.That(accepted.Replayed, Is.False)
        Assert.That((reopened.ReadCapacity "shared-account" |> ok |> Option.get).Reserved,
                    Is.EqualTo 2)
    finally
        Directory.Delete(folder, true)
