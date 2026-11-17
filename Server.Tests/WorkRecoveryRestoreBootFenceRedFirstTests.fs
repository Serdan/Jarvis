module WorkRecoveryRestoreBootFenceRedFirstTests

open System
open System.IO
open NUnit.Framework
open Server
open Jarvis.WorkSnapshots

let private succeed = function
    | Ok value -> value
    | Error reason -> failwithf "Unexpected fixture failure: %A" reason

let private tempFixture run =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-recovery-boot-red-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    try
        let original = Path.Combine(folder, "original.sqlite")
        let target = Path.Combine(folder, "restored.sqlite")
        let authority = WorkSqliteAuthority(original)
        authority.Initialize() |> succeed |> ignore
        run original target authority
    finally
        Directory.Delete(folder, true)

let private admission operationId expectedVersion =
    { OperationId = operationId
      Guards = []
      Records = []
      Capacities =
        [ { AccountId = "cross-project-account"
            ExpectedVersion = expectedVersion
            Maximum = None
            ReservedDelta = 0 } ] }

let private establishAccount operationId =
    { OperationId = operationId
      Guards = []
      Records = []
      Capacities =
        [ { AccountId = "cross-project-account"
            ExpectedVersion = None
            Maximum = Some 2
            ReservedDelta = 0 } ] }

[<Test>]
let restored_backup_requires_persisted_pre_serve_fence_not_a_process_local_receipt () =
    tempFixture (fun original target authority ->
        authority.Commit(establishAccount (Guid.NewGuid())) |> succeed |> ignore
        let backup = WorkSqliteBackup.exportSnapshot original |> succeed
        let restoreReceipt = WorkSqliteBackup.restoreEmpty backup target |> succeed
        Assert.That(restoreReceipt.RequiresRecovery, Is.True)
        // Reopen as a different host process would. The receipt boolean has
        // gone out of scope: durable SQLite authority must independently deny
        // all fresh admissions even without the caller carrying this receipt.
        let reopened = WorkSqliteAuthority(target)
        reopened.Initialize() |> succeed |> ignore
        match reopened.Commit(admission (Guid.NewGuid()) (Some 1L)) with
        | Error _ -> ()
        | Ok _ ->
            Assert.Fail("RED G1b: a restored DB admitted a fresh mutation without a durable RecoveryRequired gate")
    )

[<Test>]
let intact_database_restart_must_not_be_mistaken_for_stale_restore () =
    tempFixture (fun original _ authority ->
        let first = Guid.NewGuid()
        authority.Commit(establishAccount first) |> succeed |> ignore
        let reopened = WorkSqliteAuthority(original)
        reopened.Initialize() |> succeed |> ignore
        let historical = reopened.Commit(establishAccount first) |> succeed
        Assert.That(historical.Replayed, Is.True)
        // An ordinary restart of an intact authoritative database is not a
        // host-loss restore, so it must retain ordinary mutation capability.
        let second = reopened.Commit(admission (Guid.NewGuid()) (Some 1L)) |> succeed
        Assert.That(second.Replayed, Is.False)
    )
