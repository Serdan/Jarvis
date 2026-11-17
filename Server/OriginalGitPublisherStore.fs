namespace Jarvis.WorkQueues

open System
open System.Collections.Concurrent
open System.IO
open Microsoft.Data.Sqlite

// P1b disposable durable journal. This is NOT a second production authority:
// it requires a marker in a throwaway directory and is deliberately never
// constructed/registered by Server.Program or an original Git client.
[<RequireQualifiedAccess>]
type OriginalGitFixtureCasStatus =
    | Prepared
    | Applied
    | RejectedStale
    | Unknown

type OriginalGitFixtureStoredCas =
    { OperationId: Guid
      EnvelopeSha256: string
      Sequence: int64
      Status: OriginalGitFixtureCasStatus
      ReceiptSha256: string option }

[<RequireQualifiedAccess>]
type OriginalGitFixturePrepare =
    | Fresh of sequence: int64
    | Replay of OriginalGitFixtureStoredCas

type OriginalGitFixtureBarrierRecord =
    { OperationId: Guid
      OperatorDecisionId: Guid
      TargetSetSha256: string
      FenceEpoch: int64
      WitnessSha256: string
      OriginalPendingHighWater: int64
      OriginalSourceEffectHighWater: int64 }

module private OriginalGitFixtureSql =
    let locks = ConcurrentDictionary<string, obj>(StringComparer.Ordinal)

    let openConnection path =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.Cache <- SqliteCacheMode.Shared
        let c = new SqliteConnection(builder.ConnectionString)
        c.Open()
        c

    let command (c: SqliteConnection) (sql: string) =
        let cmd = c.CreateCommand()
        cmd.CommandText <- sql
        cmd

    let parameter (cmd: SqliteCommand) name (value: obj) =
        cmd.Parameters.AddWithValue(name, value) |> ignore

    let optional (s: string option) : obj =
        match s with Some value -> box value | None -> box DBNull.Value

    let optionalInt (n: int64 option) : obj =
        match n with Some value -> box value | None -> box DBNull.Value

    let optionalText (reader: SqliteDataReader) index =
        if reader.IsDBNull index then None else Some(reader.GetString index)

    let execute (c: SqliteConnection) sql =
        use cmd = command c sql
        cmd.ExecuteNonQuery() |> ignore

type OriginalGitPublisherStore(databasePath: string) =
    let path = Path.GetFullPath databasePath
    let folder = Path.GetDirectoryName path
    let marker = Path.Combine(folder, OriginalGitPublisherStore.DisposableMarker)
    let lane = OriginalGitFixtureSql.locks.GetOrAdd(path, fun _ -> obj())

    let readState (c: SqliteConnection) =
        use cmd = OriginalGitFixtureSql.command c
                    "SELECT phase, fence_id, epoch, hold_op FROM publisher_state WHERE singleton=1"
        use reader = cmd.ExecuteReader()
        if not (reader.Read()) then invalidOp "Publisher fixture state row missing"
        let epoch = reader.GetInt64(2)
        let parseGuid index =
            OriginalGitFixtureSql.optionalText reader index
            |> Option.map Guid.Parse
        let phase =
            match reader.GetString 0 with
            | "active" -> OriginalGitFixturePhase.Active
            | "pending" ->
                OriginalGitFixturePhase.FencePending
                    (parseGuid 1 |> Option.defaultWith (fun () -> invalidOp "Fence ID missing"))
            | "fenced" ->
                OriginalGitFixturePhase.Fenced(
                    parseGuid 1 |> Option.defaultWith (fun () -> invalidOp "Fence ID missing"), epoch)
            | "recovery" -> OriginalGitFixturePhase.RecoveryHold(parseGuid 3)
            | unknown -> invalidOp ("Unknown publisher fixture phase: " + unknown)
        { Phase = phase; FenceEpoch = epoch }

    let readCas (c: SqliteConnection) (operationId: Guid) =
        use cmd = OriginalGitFixtureSql.command c
                    "SELECT seq, envelope_sha, status, receipt_sha FROM publisher_ops WHERE op_id=@id"
        OriginalGitFixtureSql.parameter cmd "@id" (box (operationId.ToString("N")))
        use reader = cmd.ExecuteReader()
        if not (reader.Read()) then None
        else
            let status =
                match reader.GetString 2 with
                | "prepared" -> OriginalGitFixtureCasStatus.Prepared
                | "applied" -> OriginalGitFixtureCasStatus.Applied
                | "rejected_stale" -> OriginalGitFixtureCasStatus.RejectedStale
                | "unknown" -> OriginalGitFixtureCasStatus.Unknown
                | other -> invalidOp ("Unrecognized journal status " + other)
            Some
                { OperationId = operationId
                  EnvelopeSha256 = reader.GetString 1
                  Sequence = reader.GetInt64 0
                  Status = status
                  ReceiptSha256 = OriginalGitFixtureSql.optionalText reader 3 }

    do
        if String.IsNullOrWhiteSpace databasePath
           || not (Directory.Exists folder)
           || not (File.Exists marker)
           || File.ReadAllText(marker) <> OriginalGitPublisherStore.DisposableMarkerContent then
            invalidArg (nameof databasePath) "Only a marked disposable P1b publisher fixture is allowed"
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            OriginalGitFixtureSql.execute c "PRAGMA synchronous=FULL;"
            OriginalGitFixtureSql.execute c
                "CREATE TABLE IF NOT EXISTS publisher_state (singleton INTEGER PRIMARY KEY CHECK(singleton=1), phase TEXT NOT NULL, fence_id TEXT, operator_id TEXT, epoch INTEGER NOT NULL, target_sha TEXT, witness_sha TEXT, pending_highwater INTEGER, effect_highwater INTEGER, hold_op TEXT);"
            OriginalGitFixtureSql.execute c
                "CREATE TABLE IF NOT EXISTS publisher_ops (seq INTEGER PRIMARY KEY AUTOINCREMENT, op_id TEXT NOT NULL UNIQUE, envelope_sha TEXT NOT NULL, status TEXT NOT NULL, receipt_sha TEXT);"
            OriginalGitFixtureSql.execute c
                "INSERT OR IGNORE INTO publisher_state(singleton, phase, epoch) VALUES (1, 'active', 0);"
            // Reopening an interrupted publisher is conservative: an earlier
            // Git child may STILL complete after the process crashed. Never
            // retry its UUID or issue a positive barrier on missing receipt.
            use tx = c.BeginTransaction()
            use pending = OriginalGitFixtureSql.command c
                            "SELECT op_id FROM publisher_ops WHERE status='prepared' ORDER BY seq LIMIT 1"
            pending.Transaction <- tx
            let first = pending.ExecuteScalar()
            if not (isNull first) then
                use mark = OriginalGitFixtureSql.command c
                            "UPDATE publisher_ops SET status='unknown' WHERE status='prepared'"
                mark.Transaction <- tx
                mark.ExecuteNonQuery() |> ignore
                use hold = OriginalGitFixtureSql.command c
                            "UPDATE publisher_state SET phase='recovery', hold_op=@id WHERE singleton=1"
                hold.Transaction <- tx
                OriginalGitFixtureSql.parameter hold "@id" first
                hold.ExecuteNonQuery() |> ignore
            tx.Commit())

    static member DisposableMarker = ".jarvis-original-git-publisher-p1b"
    static member DisposableMarkerContent = "disposable-fixture-only-v1\n"
    member _.DatabasePath = path
    member _.Lane = lane

    member _.ReadState() =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            readState c)

    member _.ReadOperation(operationId: Guid) =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            readCas c operationId)

    member _.Prepare(operationId: Guid, envelopeSha256: string) =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            match readCas c operationId with
            | Some old when old.EnvelopeSha256 = envelopeSha256 ->
                Ok(OriginalGitFixturePrepare.Replay old)
            | Some _ -> Error OriginalGitPublisherFailure.ReusedOperationId
            | None ->
                match OriginalGitPublisherState.allowMutation (readState c) with
                | Error error -> Error error
                | Ok () ->
                    use cmd = OriginalGitFixtureSql.command c
                                "INSERT INTO publisher_ops(op_id,envelope_sha,status) VALUES(@id,@sha,'prepared'); SELECT last_insert_rowid();"
                    OriginalGitFixtureSql.parameter cmd "@id" (box (operationId.ToString("N")))
                    OriginalGitFixtureSql.parameter cmd "@sha" (box envelopeSha256)
                    let seq = Convert.ToInt64(cmd.ExecuteScalar())
                    Ok(OriginalGitFixturePrepare.Fresh seq))

    member _.MarkApplied(operationId: Guid, receiptSha256: string) =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            use cmd = OriginalGitFixtureSql.command c
                        "UPDATE publisher_ops SET status='applied', receipt_sha=@sha WHERE op_id=@id AND status='prepared'"
            OriginalGitFixtureSql.parameter cmd "@id" (box (operationId.ToString("N")))
            OriginalGitFixtureSql.parameter cmd "@sha" (box receiptSha256)
            if cmd.ExecuteNonQuery() <> 1 then invalidOp "No unique prepared CAS to confirm")

    member _.MarkRejectedStale(operationId: Guid) =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            use cmd = OriginalGitFixtureSql.command c
                        "UPDATE publisher_ops SET status='rejected_stale' WHERE op_id=@id AND status='prepared'"
            OriginalGitFixtureSql.parameter cmd "@id" (box (operationId.ToString("N")))
            if cmd.ExecuteNonQuery() <> 1 then invalidOp "No unique prepared CAS to reject")

    member _.MarkUnknown(operationId: Guid) =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            use tx = c.BeginTransaction()
            use operation = OriginalGitFixtureSql.command c "UPDATE publisher_ops SET status='unknown' WHERE op_id=@id AND status='prepared'"
            operation.Transaction <- tx
            OriginalGitFixtureSql.parameter operation "@id" (box (operationId.ToString("N")))
            operation.ExecuteNonQuery() |> ignore
            use hold = OriginalGitFixtureSql.command c
                        "UPDATE publisher_state SET phase='recovery', hold_op=@id WHERE singleton=1"
            hold.Transaction <- tx
            OriginalGitFixtureSql.parameter hold "@id" (box (operationId.ToString("N")))
            hold.ExecuteNonQuery() |> ignore
            tx.Commit())

    member _.BeginFence(operationId: Guid, operatorDecisionId: Guid, targetSetSha256: string) =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            if operatorDecisionId = Guid.Empty then
                Error(OriginalGitPublisherFailure.InvalidEnvelope "OperatorDecisionMissing")
            else
                match OriginalGitPublisherState.beginFence operationId (readState c) with
                | Error error -> Error error
                | Ok next ->
                    use existing = OriginalGitFixtureSql.command c
                                    "SELECT fence_id, operator_id, target_sha FROM publisher_state WHERE singleton=1"
                    use reader = existing.ExecuteReader()
                    reader.Read() |> ignore
                    let previousId = OriginalGitFixtureSql.optionalText reader 0
                    let previousOperator = OriginalGitFixtureSql.optionalText reader 1
                    let previousTarget = OriginalGitFixtureSql.optionalText reader 2
                    reader.Close()
                    let sameEnvelope =
                        match previousId, previousOperator, previousTarget with
                        | Some id, Some decision, Some sha ->
                            id = operationId.ToString("N")
                            && decision = operatorDecisionId.ToString("N")
                            && sha = targetSetSha256
                        | None, None, None ->
                            next.Phase = OriginalGitFixturePhase.FencePending operationId
                        | _ -> false
                    if not sameEnvelope then
                        Error OriginalGitPublisherFailure.ReusedOperationId
                    else
                        if next.Phase = OriginalGitFixturePhase.FencePending operationId then
                            use cmd = OriginalGitFixtureSql.command c
                                        "UPDATE publisher_state SET phase='pending', fence_id=@id, operator_id=@operator, target_sha=@sha WHERE singleton=1 AND phase='active'"
                            OriginalGitFixtureSql.parameter cmd "@id" (box (operationId.ToString("N")))
                            OriginalGitFixtureSql.parameter cmd "@operator" (box (operatorDecisionId.ToString("N")))
                            OriginalGitFixtureSql.parameter cmd "@sha" (box targetSetSha256)
                            cmd.ExecuteNonQuery() |> ignore
                        use pending = OriginalGitFixtureSql.command c
                                        "SELECT count(*) FROM publisher_ops WHERE status IN ('prepared','unknown')"
                        if Convert.ToInt32(pending.ExecuteScalar()) > 0 then
                            Error OriginalGitPublisherFailure.UnresolvedOriginalEffect
                        else Ok next)

    member _.ReadBarrier() =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            use cmd = OriginalGitFixtureSql.command c
                        "SELECT fence_id,operator_id,target_sha,epoch,witness_sha,pending_highwater,effect_highwater FROM publisher_state WHERE singleton=1 AND phase='fenced'"
            use reader = cmd.ExecuteReader()
            if not (reader.Read()) then None
            else
                Some { OperationId = Guid.Parse(reader.GetString 0)
                       OperatorDecisionId = Guid.Parse(reader.GetString 1)
                       TargetSetSha256 = reader.GetString 2
                       FenceEpoch = reader.GetInt64 3
                       WitnessSha256 = reader.GetString 4
                       OriginalPendingHighWater = reader.GetInt64 5
                       OriginalSourceEffectHighWater = reader.GetInt64 6 })

    member this.FinishFence(operationId: Guid, targetSetSha256: string, witnessSha256: string,
                         pendingHighWater: int64, effectHighWater: int64) =
        lock lane (fun () ->
            use c = OriginalGitFixtureSql.openConnection path
            let state = readState c
            match OriginalGitPublisherState.finishFence operationId state with
            | Error error -> Error error
            | Ok next when pendingHighWater < 0L || effectHighWater < 0L ->
                Error(OriginalGitPublisherFailure.InvalidEnvelope "NegativeOriginalJournalHighWater")
            | Ok next ->
                use existing = OriginalGitFixtureSql.command c
                                "SELECT operator_id, target_sha FROM publisher_state WHERE singleton=1"
                use reader = existing.ExecuteReader()
                reader.Read() |> ignore
                let storedDecision = OriginalGitFixtureSql.optionalText reader 0
                let persistedTarget = OriginalGitFixtureSql.optionalText reader 1
                reader.Close()
                if storedDecision.IsNone || persistedTarget <> Some targetSetSha256 then
                    Error OriginalGitPublisherFailure.UntrustedAuthority
                else
                    match state.Phase with
                    | OriginalGitFixturePhase.Fenced _ ->
                        match this.ReadBarrier() with
                        | Some previous when previous.WitnessSha256 = witnessSha256 &&
                                             previous.OriginalPendingHighWater = pendingHighWater &&
                                             previous.OriginalSourceEffectHighWater = effectHighWater -> Ok previous
                        | _ -> Error OriginalGitPublisherFailure.ReusedOperationId
                    | _ ->
                        use pending = OriginalGitFixtureSql.command c
                                        "SELECT count(*) FROM publisher_ops WHERE status IN ('prepared','unknown')"
                        if Convert.ToInt32(pending.ExecuteScalar()) > 0 then
                            Error OriginalGitPublisherFailure.UnresolvedOriginalEffect
                        else
                            use cmd = OriginalGitFixtureSql.command c
                                        "UPDATE publisher_state SET phase='fenced', epoch=@epoch, witness_sha=@w, pending_highwater=@p, effect_highwater=@e WHERE singleton=1 AND phase='pending' AND fence_id=@id"
                            OriginalGitFixtureSql.parameter cmd "@epoch" (box next.FenceEpoch)
                            OriginalGitFixtureSql.parameter cmd "@w" (box witnessSha256)
                            OriginalGitFixtureSql.parameter cmd "@p" (box pendingHighWater)
                            OriginalGitFixtureSql.parameter cmd "@e" (box effectHighWater)
                            OriginalGitFixtureSql.parameter cmd "@id" (box (operationId.ToString("N")))
                            if cmd.ExecuteNonQuery() <> 1 then Error OriginalGitPublisherFailure.Fenced
                            else
                                Ok { OperationId = operationId
                                     OperatorDecisionId = Guid.Parse(storedDecision.Value)
                                     TargetSetSha256 = targetSetSha256
                                     FenceEpoch = next.FenceEpoch
                                     WitnessSha256 = witnessSha256
                                     OriginalPendingHighWater = pendingHighWater
                                     OriginalSourceEffectHighWater = effectHighWater })
