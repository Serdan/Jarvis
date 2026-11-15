module Client.SourceEffectJournal

open System
open System.Data
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite
open Client.LedgerStore

// Inert WQ-03 host-side evidence boundary. This is NOT a scheduler or an
// authorization to release source. All legacy/uninstrumented mutation paths
// remain outside coverage; even a terminal row cannot waive that gap.
type SourceEffectKind =
    | ManagedCommand
    | ManagedJob
    | FileEdit
    | GitCommit
    | GitPush
    | GitMerge
    | Retry

[<CLIMutable>]
type SourceEffectScope =
    { ProjectName: string
      ProjectId: Guid
      LedgerId: Guid
      LedgerEpoch: int
      RepositoryId: string
      ApprovedRemoteFingerprint: string
      SourceRef: string
      CheckoutId: string
      WorkId: string
      LaunchUuid: Guid
      ClaimGeneration: int }

type SourceEffectIntent =
    { OriginalOperationId: Guid
      Scope: SourceEffectScope
      Kind: SourceEffectKind
      RequestSha256: string }

type SourceEffectState =
    | Prepared
    | Uncertain of string
    | Terminal of string

type DispatchAdmission =
    | FirstDispatch
    | AlreadyRecorded of SourceEffectState

type SourceEffectRecord =
    { OriginalOperationId: Guid
      Kind: SourceEffectKind
      RequestSha256: string
      State: SourceEffectState }

type SourceEffectPreflight =
    { Provenance: string
      Scope: SourceEffectScope
      Records: SourceEffectRecord list
      MissingOriginalOperations: Guid list
      UninstrumentedRoutes: string list
      AllKnownOperationsTerminal: bool
      RecoveryBlocked: bool
      BlockReasons: string list }

type SourceEffectStore = private { DatabasePath: string }

// fsync the parent of a newly created SQLite database. SQLite uses EXTRA
// synchronous transactions (including rollback-journal unlink directory sync)
// thereafter. A failed sync is an error, never permission to dispatch.
module private DurableDirectory =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openDirectory(string path, int flags)

    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int private syncDescriptor(int fd)

    [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int private closeDescriptor(int fd)

    let sync path =
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let fd = openDirectory(path, 0)
            if fd < 0 then
                raise (IOException("Cannot open source-journal directory for fsync."))
            try
                if syncDescriptor(fd) <> 0 then
                    raise (IOException("Cannot fsync source-journal directory."))
            finally
                closeDescriptor(fd) |> ignore
        elif not (OperatingSystem.IsWindows()) then
            raise (PlatformNotSupportedException("Unknown source-journal durability platform."))

let private kindName = function
    | ManagedCommand -> "ManagedCommand"
    | ManagedJob -> "ManagedJob"
    | FileEdit -> "FileEdit"
    | GitCommit -> "GitCommit"
    | GitPush -> "GitPush"
    | GitMerge -> "GitMerge"
    | Retry -> "Retry"

let private parseKind = function
    | "ManagedCommand" -> ManagedCommand
    | "ManagedJob" -> ManagedJob
    | "FileEdit" -> FileEdit
    | "GitCommit" -> GitCommit
    | "GitPush" -> GitPush
    | "GitMerge" -> GitMerge
    | "Retry" -> Retry
    | other -> invalidOp ("Corrupt source effect kind: " + other)

let private digest (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value)
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let private hex64 (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && value.Length = 64
    && value |> Seq.forall (fun ch ->
        (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))

let private token (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && value.Length <= 200
    && value |> Seq.forall (fun ch ->
        Char.IsLetterOrDigit ch || ch = '-' || ch = '_' || ch = '.')

let private validScope (scope: SourceEffectScope) =
    not (isNull (box scope))
    && token scope.ProjectName
    && scope.ProjectId <> Guid.Empty
    && scope.LedgerId <> Guid.Empty
    && scope.LedgerEpoch >= 0
    && hex64 scope.RepositoryId
    && hex64 scope.ApprovedRemoteFingerprint
    && not (isNull scope.SourceRef)
    && scope.SourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
    && scope.SourceRef.Length < 400
    && not (scope.SourceRef.Contains("..", StringComparison.Ordinal))
    && not (scope.SourceRef.Contains("@{", StringComparison.Ordinal))
    && not (scope.SourceRef.Contains('\\'))
    && not (scope.SourceRef.Contains('\n'))
    && token scope.CheckoutId
    && token scope.WorkId
    && scope.LaunchUuid <> Guid.Empty
    && scope.ClaimGeneration > 0

let private validIntent (intent: SourceEffectIntent) =
    intent.OriginalOperationId <> Guid.Empty
    && validScope intent.Scope
    && hex64 intent.RequestSha256

/// Construct the scope from the independently bound, operator-approved ledger
/// URLs, not from a worker-provided "remote is safe" boolean or URL.
let fromTrustedLedgerBinding (binding: LedgerBinding) projectName repositoryId
    sourceRef checkoutId workId launchUuid claimGeneration =
    { ProjectName = projectName
      ProjectId = binding.ProjectId
      LedgerId = binding.LedgerId
      LedgerEpoch = binding.Epoch
      RepositoryId = repositoryId
      ApprovedRemoteFingerprint =
        digest ("wq03-approved-remote-v1\n" + binding.ApprovedFetchUrl +
                "\n" + binding.ApprovedPushUrl)
      SourceRef = sourceRef
      CheckoutId = checkoutId
      WorkId = workId
      LaunchUuid = launchUuid
      ClaimGeneration = claimGeneration }

let private scopeText (scope: SourceEffectScope) = JsonSerializer.Serialize scope
let private scopeKey scope = digest ("wq03-source-scope-v1\n" + scopeText scope)
let private operationKey (intent: SourceEffectIntent) =
    digest ("wq03-original-effect-v1\n" + scopeText intent.Scope + "\n" +
            kindName intent.Kind + "\n" + intent.RequestSha256)

let private command (connection: SqliteConnection) (transaction: SqliteTransaction option) sql =
    let cmd = connection.CreateCommand()
    cmd.CommandText <- sql
    transaction |> Option.iter (fun tx -> cmd.Transaction <- tx)
    cmd

let private add (cmd: SqliteCommand) key (value: obj) =
    cmd.Parameters.AddWithValue(key, value) |> ignore

let private connect (store: SourceEffectStore) =
    if not (File.Exists store.DatabasePath) then
        invalidOp "Original source-effect journal missing; RecoveryBlocked."
    let cs = SqliteConnectionStringBuilder()
    cs.DataSource <- store.DatabasePath
    cs.Mode <- SqliteOpenMode.ReadWrite
    cs.Pooling <- false
    let connection = new SqliteConnection(cs.ToString())
    connection.Open()
    try
        use pragmas = connection.CreateCommand()
        pragmas.CommandText <-
            "PRAGMA busy_timeout=5000; PRAGMA journal_mode=DELETE; " +
            "PRAGMA synchronous=EXTRA; PRAGMA foreign_keys=ON;"
        pragmas.ExecuteNonQuery() |> ignore
        connection
    with ex ->
        connection.Dispose()
        raise ex

/// Root must be provisioned by the trusted client OUTSIDE any source checkout.
/// The database is shared by independent client processes/clones on the host.
/// Missing/corrupt databases always block; never fall back to a job list.
let openAtHostRoot root : Result<SourceEffectStore, string> =
    try
        if String.IsNullOrWhiteSpace root || not (Path.IsPathFullyQualified root) then
            invalidArg "root" "Source journal requires a trusted absolute host root."
        let path = Path.GetFullPath root
        if not (Directory.Exists path) then
            invalidOp "Source journal root not provisioned by the host."
        if DirectoryInfo(path).LinkTarget <> null then
            invalidOp "Source journal root may not be a symbolic link."
        let dbPath = Path.Combine(path, "source-effects-v1.sqlite")
        if FileInfo(dbPath).LinkTarget <> null then
            invalidOp "Source journal database may not be a symbolic link."
        let firstCreation = not (File.Exists dbPath)
        if firstCreation then
            let cs = SqliteConnectionStringBuilder()
            cs.DataSource <- dbPath
            cs.Mode <- SqliteOpenMode.ReadWriteCreate
            cs.Pooling <- false
            use initial = new SqliteConnection(cs.ToString())
            initial.Open()
            use configure = initial.CreateCommand()
            configure.CommandText <- "PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA;"
            configure.ExecuteNonQuery() |> ignore
            use create = initial.CreateCommand()
            create.CommandText <-
                "CREATE TABLE IF NOT EXISTS effects (" +
                "operation_id TEXT PRIMARY KEY, scope_key TEXT NOT NULL, scope_json TEXT NOT NULL, " +
                "effect_kind TEXT NOT NULL, request_sha TEXT NOT NULL, operation_key TEXT NOT NULL, " +
                "state TEXT NOT NULL, proof TEXT NOT NULL, reason TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS fences (" +
                "scope_key TEXT PRIMARY KEY, scope_json TEXT NOT NULL, " +
                "state TEXT NOT NULL CHECK(state = 'RecoveryBlocked'), reason TEXT NOT NULL);"
            create.ExecuteNonQuery() |> ignore
            initial.Close()
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(dbPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            DurableDirectory.sync path
        let store = { DatabasePath = dbPath }
        use verify = connect store
        use tables = verify.CreateCommand()
        tables.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('effects','fences');"
        if Convert.ToInt32(tables.ExecuteScalar()) <> 2 then
            invalidOp "Incomplete source effect journal schema."
        Ok store
    with ex -> Error ("RecoveryBlocked: cannot open durable source-effect journal: " + ex.Message)

let private transaction (store: SourceEffectStore) work =
    try
        use connection = connect store
        use tx = connection.BeginTransaction(IsolationLevel.Serializable, false)
        let result = work connection tx
        match result with
        | Ok _ -> tx.Commit()
        | Error _ -> tx.Rollback()
        result
    with ex -> Error ("RecoveryBlocked: source-effect journal transaction failed: " + ex.Message)

let private parseState status proof reason =
    match status with
    | "Prepared" -> Prepared
    | "Uncertain" -> Uncertain reason
    | "Terminal" when not (String.IsNullOrWhiteSpace proof) -> Terminal proof
    | _ -> invalidOp "Corrupt source effect state; RecoveryBlocked."

let private queryExisting (connection: SqliteConnection) (tx: SqliteTransaction)
    (operationId: Guid) =
    use q = command connection (Some tx) (
        "SELECT scope_key, effect_kind, request_sha, operation_key, state, proof, reason FROM effects WHERE operation_id=$id;")
    add q "$id" (box (operationId.ToString("D")))
    use reader = q.ExecuteReader()
    if reader.Read() then
        Some(reader.GetString(0), reader.GetString(1), reader.GetString(2),
             reader.GetString(3), parseState (reader.GetString(4)) (reader.GetString(5)) (reader.GetString(6)))
    else None

/// Durable original UUID is committed BEFORE an effect may be dispatched.
/// A replay (even after restart) is never permission to launch it a second time.
let beginBeforeDispatch store intent : Result<DispatchAdmission, string> =
    if not (validIntent intent) then
        Error "RecoveryBlocked: invalid source, ledger, route or caller-stable operation identity."
    else
        transaction store (fun connection tx ->
            let scopeHash = scopeKey intent.Scope
            let requestKey = operationKey intent
            match queryExisting connection tx intent.OriginalOperationId with
            | Some (existingScope, kind, requestSha, key, state) ->
                if existingScope <> scopeHash || kind <> kindName intent.Kind
                    || requestSha <> intent.RequestSha256 || key <> requestKey then
                    Error "RecoveryBlocked: original operation UUID reused with different project, ledger, source or payload."
                else Ok(AlreadyRecorded state)
            | None ->
                use fence = command connection (Some tx) (
                    "INSERT OR IGNORE INTO fences(scope_key,scope_json,state,reason) VALUES($scope,$json,'RecoveryBlocked','Coverage not attested; original source effects may still be in flight.');")
                add fence "$scope" (box scopeHash)
                add fence "$json" (box (scopeText intent.Scope))
                fence.ExecuteNonQuery() |> ignore
                use insert = command connection (Some tx) (
                    "INSERT INTO effects(operation_id,scope_key,scope_json,effect_kind,request_sha,operation_key,state,proof,reason) " +
                    "VALUES($id,$scope,$json,$kind,$request,$key,'Prepared','','Original dispatch outcome not proven terminal.');")
                add insert "$id" (box (intent.OriginalOperationId.ToString("D")))
                add insert "$scope" (box scopeHash)
                add insert "$json" (box (scopeText intent.Scope))
                add insert "$kind" (box (kindName intent.Kind))
                add insert "$request" (box intent.RequestSha256)
                add insert "$key" (box requestKey)
                insert.ExecuteNonQuery() |> ignore
                Ok FirstDispatch)

/// Only the Fresh begin path can execute callback. A callback failure may have
/// applied an effect, so its original UUID remains permanently uncertain until
/// a *trusted* reconciliation is independently implemented.
let dispatchOnce store (intent: SourceEffectIntent) (dispatch: unit -> 'T) : Result<'T, string> =
    match beginBeforeDispatch store intent with
    | Error problem -> Error problem
    | Ok(AlreadyRecorded _) ->
        Error "RecoveryBlocked: original operation already recorded; do not redispatch or mint another UUID."
    | Ok FirstDispatch ->
        try Ok(dispatch())
        with ex ->
            let reason = "Dispatch raised after durable begin: " + ex.GetType().Name
            let _ = transaction store (fun connection tx ->
                use update = command connection (Some tx) (
                    "UPDATE effects SET state='Uncertain',reason=$reason WHERE operation_id=$id AND state='Prepared';")
                add update "$reason" (box reason)
                add update "$id" (box (intent.OriginalOperationId.ToString("D")))
                update.ExecuteNonQuery() |> ignore
                Ok ())
            Error ("RecoveryBlocked: " + reason)

/// Explicitly preserve unknown original remote outcomes; remote absence at a
/// single observation is never accepted as a terminal/retry receipt.
let recordUncertain store (intent: SourceEffectIntent) reason =
    if not (validIntent intent) || String.IsNullOrWhiteSpace reason then
        Error "RecoveryBlocked: invalid uncertain source effect."
    else
        transaction store (fun connection tx ->
            match queryExisting connection tx intent.OriginalOperationId with
            | None -> Error "RecoveryBlocked: no original pre-dispatch journal record."
            | Some (scope, kind, request, key, state)
                when scope = scopeKey intent.Scope && kind = kindName intent.Kind
                     && request = intent.RequestSha256 && key = operationKey intent ->
                match state with
                | Terminal _ -> Error "RecoveryBlocked: immutable terminal observation cannot be rewritten."
                | _ ->
                    use update = command connection (Some tx) (
                        "UPDATE effects SET state='Uncertain',reason=$reason WHERE operation_id=$id;")
                    add update "$reason" (box reason)
                    add update "$id" (box (intent.OriginalOperationId.ToString("D")))
                    update.ExecuteNonQuery() |> ignore
                    Ok ()
            | Some _ -> Error "RecoveryBlocked: uncertain effect identity does not match original UUID.")

/// No public proof-finalization method is provided for a process tree, push,
/// merge or retry: the current host has no authenticated remote/process-tree
/// terminal attestation. Absence in ListJobs or ls-remote is deliberately inert.
/// Only synchronous, host-owned in-process file edits can be terminalized by
/// the same wrapper that performs them, after its returned effect has completed.
let runSynchronousFileEdit store (intent: SourceEffectIntent) (edit: unit -> 'T) : Result<'T, string> =
    if intent.Kind <> FileEdit then
        Error "RecoveryBlocked: synchronous completion is valid only for host-owned FileEdit."
    else
        match dispatchOnce store intent edit with
        | Error error -> Error error
        | Ok result ->
            match transaction store (fun connection tx ->
                match queryExisting connection tx intent.OriginalOperationId with
                | Some (scope, kind, sha, key, Prepared)
                    when scope = scopeKey intent.Scope && kind = "FileEdit"
                         && sha = intent.RequestSha256 && key = operationKey intent ->
                    use update = command connection (Some tx) (
                        "UPDATE effects SET state='Terminal',proof='host-synchronous-file-edit-returned-v1',reason='' WHERE operation_id=$id;")
                    add update "$id" (box (intent.OriginalOperationId.ToString("D")))
                    update.ExecuteNonQuery() |> ignore
                    Ok ()
                | _ -> Error "RecoveryBlocked: original file edit identity/state changed during dispatch.") with
            | Ok () -> Ok result
            | Error error -> Error ("Effect may have completed; " + error)

/// Persist a sticky source fence. There is deliberately no corresponding
/// release operation: canonical project transition and host/WIP policy remain
/// separate, future independently reviewed gates.
let markRecoveryBlocked store scope reason =
    if not (validScope scope) || String.IsNullOrWhiteSpace reason then
        Error "RecoveryBlocked: invalid bound source fence."
    else
        transaction store (fun connection tx ->
            use write = command connection (Some tx) (
                "INSERT INTO fences(scope_key,scope_json,state,reason) VALUES($key,$json,'RecoveryBlocked',$reason) " +
                "ON CONFLICT(scope_key) DO UPDATE SET reason=excluded.reason;")
            add write "$key" (box (scopeKey scope))
            add write "$json" (box (scopeText scope))
            add write "$reason" (box reason)
            write.ExecuteNonQuery() |> ignore
            Ok ())

// Explicitly enumerated missing routes: a complete journal over some IDs
// cannot imply complete coverage of all mutation-capable endpoints.
let uninstrumentedMutationRoutes =
    [ "RunCommand"; "StartJob"; "RunProjectTask"; "WriteFile"; "PatchFile"
      "ImportFile"; "GitCommit"; "GitPush"; "GitMerge"; "Retry"
      "LegacyOrOutOfBandOSGit" ]

/// Read-only inspection: never writes evidence or changes source authority.
/// Never returns "safe to yield": the route/host completeness proof is not yet
/// installed, regardless of a clean checkout, elapsed time or terminal rows.
let inspectForHandoff store scope expectedOriginalIds : SourceEffectPreflight =
    let blocked reasons records missing =
        { Provenance = "Trusted Jarvis client SQLite/EXTRA original-UUID journal; host coverage not attested"
          Scope = scope
          Records = records
          MissingOriginalOperations = missing
          UninstrumentedRoutes = uninstrumentedMutationRoutes
          AllKnownOperationsTerminal =
              not (List.isEmpty records)
              && List.isEmpty missing
              && (records |> List.forall (fun record ->
                  match record.State with Terminal _ -> true | _ -> false))
          RecoveryBlocked = true
          BlockReasons = reasons }
    if not (validScope scope) then
        blocked [ "Invalid project/ledger/source binding." ] [] expectedOriginalIds
    else
        try
            use connection = connect store
            use fence = command connection None (
                "SELECT reason FROM fences WHERE scope_key=$scope AND scope_json=$json;")
            add fence "$scope" (box (scopeKey scope))
            add fence "$json" (box (scopeText scope))
            let fenceReason = fence.ExecuteScalar()
            let missingFence = isNull fenceReason
            let entries =
                expectedOriginalIds
                |> List.distinct
                |> List.choose (fun id ->
                    use query = command connection None (
                        "SELECT scope_key,scope_json,effect_kind,request_sha,state,proof,reason FROM effects WHERE operation_id=$id;")
                    add query "$id" (box (id.ToString("D")))
                    use reader = query.ExecuteReader()
                    if not (reader.Read()) then None
                    elif (reader.GetString(0) <> scopeKey scope ||
                          reader.GetString(1) <> scopeText scope) then None
                    else
                        let item: SourceEffectRecord =
                            { OriginalOperationId = id
                              Kind = parseKind (reader.GetString(2))
                              RequestSha256 = reader.GetString(3)
                              State = parseState (reader.GetString(4)) (reader.GetString(5)) (reader.GetString(6)) }
                        Some item)
            let missing =
                expectedOriginalIds |> List.distinct
                |> List.filter (fun id -> not (entries |> List.exists (fun e -> e.OriginalOperationId = id)))
            let reasons =
                [ if missingFence then "No durable RecoveryBlocked fence for this lineage."
                  else string fenceReason
                  if List.isEmpty expectedOriginalIds then "No complete original operation inventory."
                  if not (List.isEmpty missing) then "One or more original operations absent or wrongly bound."
                  if entries |> List.exists (fun item ->
                      match item.State with Terminal _ -> false | _ -> true) then
                      "Prepared, running or uncertain original effects remain unresolved."
                  "Host source-mutation route completeness and WIP retention are not proven." ]
            blocked reasons entries missing
        with ex ->
            blocked [ "Journal read/provenance failure: " + ex.Message ] [] expectedOriginalIds


// Only a host-owned awaited WriteFile/PatchFile dispatch may produce this
// narrow terminal receipt. A callback error, exception or lost acknowledgement
// is uncertain, never proof of a completed effect. No process/Git/remote
// operation may invoke this as a terminal attestation.
let runAwaitedHostFileEdit store (intent: SourceEffectIntent)
    (edit: unit -> System.Threading.Tasks.Task<Result<'T, 'E>>)
    : System.Threading.Tasks.Task<Result<Result<'T, 'E>, string>> =
    task {
        if intent.Kind <> FileEdit then
            return Error "RecoveryBlocked: awaited terminal proof restricted to host-owned file edits."
        else
            match beginBeforeDispatch store intent with
            | Error reason -> return Error reason
            | Ok(AlreadyRecorded _) ->
                return Error "RecoveryBlocked: original file edit already recorded; no redispatch."
            | Ok FirstDispatch ->
                try
                    let! result = edit()
                    match result with
                    | Error _ ->
                        match recordUncertain store intent "Awaited host file edit returned an error; original outcome may be partial." with
                        | Ok () -> return Ok result
                        | Error reason -> return Error reason
                    | Ok _ ->
                        let completion = transaction store (fun connection tx ->
                            match queryExisting connection tx intent.OriginalOperationId with
                            | Some (scope, kind, sha, key, Prepared)
                                when scope = scopeKey intent.Scope && kind = "FileEdit"
                                     && sha = intent.RequestSha256 && key = operationKey intent ->
                                use update = command connection (Some tx) (
                                    "UPDATE effects SET state='Terminal',proof='host-awaited-file-edit-returned-v1',reason='' WHERE operation_id=$id;")
                                add update "$id" (box (intent.OriginalOperationId.ToString("D")))
                                update.ExecuteNonQuery() |> ignore
                                Ok ()
                            | _ -> Error "RecoveryBlocked: awaited file edit identity/state changed before terminal receipt.")
                        match completion with
                        | Ok () -> return Ok result
                        | Error reason -> return Error ("RecoveryBlocked: effect may have completed; " + reason)
                with ex ->
                    let uncertainty = "Awaited host file edit raised " + ex.GetType().Name + "."
                    match recordUncertain store intent uncertainty with
                    | Ok () -> return Error ("RecoveryBlocked: " + uncertainty)
                    | Error reason -> return Error reason
    }
