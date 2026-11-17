module Client.OriginalGitWriterAdmissionJournal

open System
open System.Data
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Security.Cryptography
open System.Text
open Microsoft.Data.Sqlite

// Host-owned, default-off journal of ORIGINAL Git publication attempts.
// This is a safety primitive, not cutover authorization or a second publisher.
// No "drained" witness is valid unless every actual old writer is instrumented.
type OriginalWriterPermit =
    private
        { DatabasePath: string
          EffectKey: string
          Sequence: int64
          Epoch: int64
          Replay: bool }

type OriginalWriterEffect =
    { Sequence: int64
      EffectKey: string
      Kind: string
      AuthoritySha256: string
      GitRef: string
      Status: string }

type OriginalWriterJournalWitness =
    { Epoch: int64
      AdmissionClosed: bool
      HighWater: int64
      SettledHighWater: int64
      PendingOrUnknown: OriginalWriterEffect list
      AllRecordedEffectsSettled: bool
      // Explicitly cannot be inferred from journal rows themselves.
      RouteCoverageProven: bool }

module OriginalGitWriterAdmissionJournal =
    [<Literal>]
    let JournalEnvironment = "JARVIS_ORIGINAL_GIT_WRITER_ADMISSION_JOURNAL"

    // The environment variable is only a request to use a journal, never the
    // enduring authority to revert an activated host to default-off.  A
    // protected, fixed-location host binding independently pins its identity.
    // Test-only per-execution root avoids touching the actual operator host.
    let private fixtureRoot = AsyncLocal<string>()

    let internal setDisposableBindingRoot path =
        fixtureRoot.Value <- defaultArg path null

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openDirectory(string path, int flags)
    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int private syncDirectory(int fd)
    [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int private closeDirectory(int fd)

    let private digest (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> fun h -> h.ToLowerInvariant()

    let private noRedirects path =
        try
            let rec walk current =
                if String.IsNullOrWhiteSpace current then true
                elif (File.GetAttributes current).HasFlag(FileAttributes.ReparsePoint) then false
                else
                    let parent = Path.GetDirectoryName current
                    isNull parent || current = parent || walk parent
            walk path
        with _ -> false

    let private safeJournalPath (path: string) =
        try
            not (String.IsNullOrWhiteSpace path)
            && Path.IsPathFullyQualified path
            && File.Exists path
            && noRedirects path
            && (OperatingSystem.IsWindows()
                || (File.GetUnixFileMode path
                    &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite
                         ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite)) = UnixFileMode.None)
        with _ -> false

    let private syncParent (path: string) =
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let fd = openDirectory(Path.GetDirectoryName path, 0)
            if fd < 0 then invalidOp "Cannot open original-writer journal parent for fsync."
            try
                if syncDirectory(fd) <> 0 then
                    invalidOp "Cannot fsync original-writer journal parent."
            finally
                closeDirectory fd |> ignore
        elif not (OperatingSystem.IsWindows()) then
            invalidOp "Unknown original-writer journal durability platform."

    let private openDatabase path =
        if not (safeJournalPath path) then
            invalidOp "Original-writer journal binding unavailable or insecure."
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Pooling <- false // Reopen the exact on-disk journal identity; no stale pooled inode.
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.Cache <- SqliteCacheMode.Private
        let connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use pragma = connection.CreateCommand()
        pragma.CommandText <- "PRAGMA busy_timeout=5000; PRAGMA synchronous=EXTRA; PRAGMA foreign_keys=ON;"
        pragma.ExecuteNonQuery() |> ignore
        connection

    let private command (connection: SqliteConnection) (transaction: SqliteTransaction) sql (parameters: (string * obj) list) =
        let cmd = connection.CreateCommand()
        cmd.Transaction <- transaction
        cmd.CommandText <- sql
        for name, value in parameters do
            cmd.Parameters.AddWithValue(name, value) |> ignore
        cmd

    let private readControl (connection: SqliteConnection) (tx: SqliteTransaction) =
        use cmd = command connection tx "SELECT epoch,closed FROM control WHERE id=1" []
        use rows = cmd.ExecuteReader()
        if not (rows.Read()) then invalidOp "Missing original-writer admission epoch."
        let epoch = rows.GetInt64 0
        let closed = rows.GetInt64 1 <> 0L
        if rows.Read() || epoch < 1L then invalidOp "Invalid original-writer admission control."
        epoch, closed

    let private inTransaction path action =
        use db = openDatabase path
        use tx = db.BeginTransaction(IsolationLevel.Serializable, false)
        let outcome = action db tx
        tx.Commit()
        outcome

    // Explicit isolated fixture provision. Production provisioning and
    // ownership attestation must be separately reviewed and authorized.
    let internal provisionDisposableJournal (path: string) =
        if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path)
           || File.Exists path || not (Directory.Exists(Path.GetDirectoryName path))
           || not (noRedirects (Path.GetDirectoryName path)) then
            invalidArg (nameof path) "A new explicit disposable journal path is required."
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Pooling <- false // Disposable provisioning must create the named file, not reuse a pooled handle.
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        use db = new SqliteConnection(builder.ToString())
        db.Open()
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        use cmd = db.CreateCommand()
        cmd.CommandText <- """
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=EXTRA;
            CREATE TABLE control (
                id INTEGER PRIMARY KEY CHECK(id=1),
                epoch INTEGER NOT NULL CHECK(epoch>0),
                closed INTEGER NOT NULL CHECK(closed IN (0,1))
            );
            INSERT INTO control(id,epoch,closed) VALUES (1,1,0);
            CREATE TABLE journal_identity (
                id INTEGER PRIMARY KEY CHECK(id=1),
                journal_id TEXT NOT NULL UNIQUE
            );
            CREATE TABLE effects (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                effect_key TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                authority_sha256 TEXT NOT NULL,
                git_ref TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                status TEXT NOT NULL CHECK(status IN
                    ('Admitted','Attempted','Settled','Rejected','Unknown'))
            );
        """
        cmd.ExecuteNonQuery() |> ignore
        use identity = db.CreateCommand()
        identity.CommandText <- "INSERT INTO journal_identity(id,journal_id) VALUES(1,$id)"
        identity.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")) |> ignore
        identity.ExecuteNonQuery() |> ignore
        db.Close()
        syncParent path

    let private boundRoot () =
        if String.IsNullOrWhiteSpace fixtureRoot.Value then
            let appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            if String.IsNullOrWhiteSpace appData then
                invalidOp "Local application-data binding root is unavailable."
            Path.Combine(appData, "Jarvis", "original-git-writer-admission")
        else Path.Combine(fixtureRoot.Value, "original-writer-host-binding")

    let private protectedFile path =
        try
            File.Exists path && noRedirects path
            && (OperatingSystem.IsWindows()
                || (File.GetUnixFileMode path
                    &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite
                         ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite)) = UnixFileMode.None)
        with _ -> false

    let private journalId path =
        try
            use db = openDatabase path
            use cmd = db.CreateCommand()
            cmd.CommandText <- "SELECT journal_id FROM journal_identity WHERE id=1"
            let value = cmd.ExecuteScalar()
            let id = if isNull value then "" else string value
            let mutable parsed = Guid.Empty
            if Guid.TryParseExact(id, "N", &parsed) && parsed <> Guid.Empty then Some id
            else None
        with _ -> None

    let private createProtected (path: string) (content: string) =
        use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let bytes = Encoding.UTF8.GetBytes(content)
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)

    let private bindingText path identity =
        String.concat "\n" ["OriginalWriterHostBinding/v1"; path; identity]

    let private readHostBinding path seal =
        if not (protectedFile path && protectedFile seal) then
            Error "OriginalWriterJournalUnknown/HostBindingMissingOrInsecure"
        else
            try
                let record = File.ReadAllText path
                let signature = File.ReadAllText seal
                let lines = record.Split('\n')
                if lines.Length <> 3 || lines.[0] <> "OriginalWriterHostBinding/v1"
                   || signature <> digest record || not (safeJournalPath lines.[1])
                   || journalId lines.[1] <> Some lines.[2] then
                    Error "OriginalWriterJournalUnknown/HostBindingIdentityMismatch"
                else Ok lines.[1]
            with _ -> Error "OriginalWriterJournalUnknown/HostBindingUnreadable"

    let private hostRootState (root: string) =
        try
            let attributes = File.GetAttributes root
            if not (attributes.HasFlag(FileAttributes.Directory)) then
                Error "OriginalWriterJournalUnknown/HostBindingRootNotDirectory"
            elif not (noRedirects root) then
                Error "OriginalWriterJournalUnknown/UntrustedHostBindingRoot"
            elif not (OperatingSystem.IsWindows())
                 && (File.GetUnixFileMode root
                     &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite
                          ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead
                          ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)) <> UnixFileMode.None then
                Error "OriginalWriterJournalUnknown/UntrustedHostBindingPermissions"
            else Ok true
        with
        | :? DirectoryNotFoundException | :? FileNotFoundException -> Ok false
        | _ -> Error "OriginalWriterJournalUnknown/HostBindingRootUnreadable"

    // First activation is possible only with a previously provisioned journal.
    // Thereafter a separate pinned host binding prevents environment removal,
    // substitution or journal replacement from silently reopening publication.
    // Missing half of an interrupted/tampered binding is permanently UNKNOWN.
    let internal configuredPath () =
        try
            let requested = Environment.GetEnvironmentVariable JournalEnvironment
            let root = boundRoot()
            let binding = Path.Combine(root, "journal.binding")
            let seal = Path.Combine(root, "journal.binding.sha256")
            match hostRootState root with
            | Error reason -> Error reason
            | Ok rootExists when rootExists && (File.Exists binding || File.Exists seal) ->
                match readHostBinding binding seal with
                | Error reason -> Error reason
                | Ok pinned when String.IsNullOrWhiteSpace requested ->
                    Error "OriginalWriterJournalUnknown/ActivatedHostBindingMissing"
                | Ok pinned when requested <> pinned ->
                    Error "OriginalWriterJournalUnknown/ActivatedHostBindingChanged"
                | Ok pinned -> Ok(Some pinned)
            | Ok true ->
                // The host-specific root is only created on enrollment. Even
                // deletion of BOTH binding files must not revert to default-off.
                Error "OriginalWriterJournalUnknown/ActivatedHostBindingDeleted"
            | Ok false when String.IsNullOrWhiteSpace requested ->
                // Truly never activated: retain existing default-off behavior.
                Ok None
            | Ok false when not (safeJournalPath requested) ->
                Error "OriginalWriterJournalUnknown/UntrustedOrMissingJournal"
            | Ok false ->
                match journalId requested with
                | None -> Error "OriginalWriterJournalUnknown/JournalIdentityUnavailable"
                | Some identity ->
                    // A fixed host-owned anchor, not a dynamically selected
                    // environment-variable path after activation.
                    Directory.CreateDirectory root |> ignore
                    if not (OperatingSystem.IsWindows()) then
                        File.SetUnixFileMode(root,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                    if not (noRedirects root) then
                        Error "OriginalWriterJournalUnknown/UntrustedHostBindingRoot"
                    else
                        let contents = bindingText requested identity
                        createProtected binding contents
                        syncParent binding
                        createProtected seal (digest contents)
                        syncParent seal
                        Ok(Some requested)
        with _ -> Error "OriginalWriterJournalUnknown/HostBindingUnavailable"

    let private validateIntent (operationId: Guid) (kind: string) (authority: string) (gitRef: string) (payload: string) =
        operationId <> Guid.Empty
        && not (String.IsNullOrWhiteSpace kind)
        && kind.Length <= 100
        && not (String.IsNullOrWhiteSpace gitRef)
        && gitRef.Length <= 256
        && not (String.IsNullOrWhiteSpace payload)
        && authority.Length = 64
        && (authority |> Seq.forall (fun c -> Char.IsAsciiHexDigitLower c))

    let internal admit (operationId: Guid) kind authority gitRef payload : Result<OriginalWriterPermit option, string> =
        match configuredPath () with
        | Error reason -> Error reason
        | Ok None -> Ok None // DEFAULT OFF; no new write authority.
        | Ok(Some path) ->
            if not (validateIntent operationId kind authority gitRef payload) then
                Error "OriginalWriterJournalUnknown/InvalidPublicationIdentity"
            else
                try
                    let payloadHash = digest payload
                    let key = digest (String.concat "\n" [operationId.ToString("D"); kind; authority; gitRef])
                    inTransaction path (fun db tx ->
                        let epoch, closed = readControl db tx
                        if closed then Error "OriginalWriterAdmissionClosed"
                        else
                            use read =
                                command db tx
                                    "SELECT sequence,epoch,kind,authority_sha256,git_ref,payload_sha256,status FROM effects WHERE effect_key=$key"
                                    ["$key", box key]
                            use reader = read.ExecuteReader()
                            let prior =
                                if reader.Read() then
                                    Some(reader.GetInt64 0, reader.GetInt64 1, reader.GetString 2,
                                         reader.GetString 3, reader.GetString 4, reader.GetString 5,
                                         reader.GetString 6)
                                else None
                            reader.Close()
                            match prior with
                            | Some(seq, oldEpoch, priorKind, priorAuthority, priorRef, priorHash, "Settled")
                                when oldEpoch=epoch && priorKind=kind
                                     && priorAuthority=authority && priorRef=gitRef
                                     && priorHash=payloadHash ->
                                Ok(Some {DatabasePath=path; EffectKey=key; Sequence=seq; Epoch=epoch; Replay=true})
                            | Some _ -> Error "OriginalWriterJournalUnknown/ExistingEffectRequiresReconciliation"
                            | None ->
                                use insert =
                                    command db tx
                                        """INSERT INTO effects(effect_key,kind,authority_sha256,git_ref,payload_sha256,epoch,status)
                                           VALUES($key,$kind,$authority,$ref,$payload,$epoch,'Admitted')"""
                                        ["$key", box key; "$kind", box kind; "$authority", box authority;
                                         "$ref", box gitRef; "$payload", box payloadHash; "$epoch", box epoch]
                                insert.ExecuteNonQuery() |> ignore
                                use seqCommand = command db tx "SELECT last_insert_rowid()" []
                                let sequence = Convert.ToInt64(seqCommand.ExecuteScalar())
                                Ok(Some {DatabasePath=path; EffectKey=key; Sequence=sequence;
                                         Epoch=epoch; Replay=false}))
                with _ -> Error "OriginalWriterJournalUnknown/AdmissionNotDurable"

    let internal markAttempt permit =
        if permit.Replay then Error "OriginalWriterJournalUnknown/ReplayMayNotRepublish"
        else
            try
                inTransaction permit.DatabasePath (fun db tx ->
                    use cmd =
                        command db tx
                            """UPDATE effects SET status='Attempted'
                               WHERE effect_key=$key AND epoch=$epoch AND status='Admitted'"""
                            ["$key",box permit.EffectKey; "$epoch",box permit.Epoch]
                    if cmd.ExecuteNonQuery()=1 then Ok()
                    else Error "OriginalWriterJournalUnknown/AttemptNotAdmitted")
            with _ -> Error "OriginalWriterJournalUnknown/AttemptNotDurable"

    let internal settle permit verified (publicationAttempted: bool) =
        if permit.Replay then Ok()
        else
            try
                let status =
                    if verified then "Settled"
                    elif publicationAttempted then "Unknown"
                    else "Rejected"
                inTransaction permit.DatabasePath (fun db tx ->
                    use cmd =
                        command db tx
                            """UPDATE effects SET status=$status WHERE effect_key=$key AND epoch=$epoch
                               AND status IN ('Admitted','Attempted')"""
                            ["$key",box permit.EffectKey; "$epoch",box permit.Epoch; "$status",box status]
                    if cmd.ExecuteNonQuery()=1 then Ok()
                    else Error "OriginalWriterJournalUnknown/SettlementConflict")
            with _ -> Error "OriginalWriterJournalUnknown/SettlementNotDurable"

    let internal inspect path =
        try
            inTransaction path (fun db tx ->
                let epoch, closed = readControl db tx
                use maxCmd = command db tx "SELECT COALESCE(MAX(sequence),0) FROM effects" []
                let highWater = Convert.ToInt64(maxCmd.ExecuteScalar())
                use cmd =
                    command db tx
                        """SELECT sequence,effect_key,kind,authority_sha256,git_ref,status
                           FROM effects WHERE status IN ('Admitted','Attempted','Unknown')
                           ORDER BY sequence""" []
                use reader = cmd.ExecuteReader()
                let mutable pending = []
                while reader.Read() do
                    pending <- {Sequence=reader.GetInt64 0; EffectKey=reader.GetString 1;
                                Kind=reader.GetString 2; AuthoritySha256=reader.GetString 3;
                                GitRef=reader.GetString 4; Status=reader.GetString 5} :: pending
                let unresolved = List.rev pending
                let settledHighWater =
                    match unresolved with
                    | [] -> highWater
                    | first :: _ -> max 0L (first.Sequence - 1L)
                Ok {Epoch=epoch; AdmissionClosed=closed; HighWater=highWater;
                    SettledHighWater=settledHighWater;
                    PendingOrUnknown=unresolved;
                    AllRecordedEffectsSettled=List.isEmpty unresolved;
                    RouteCoverageProven=false})
        with _ -> Error "OriginalWriterJournalUnknown/UnverifiableJournal"

    let internal stop path =
        try
            inTransaction path (fun db tx ->
                let epoch, _ = readControl db tx
                use cmd = command db tx "UPDATE control SET closed=1 WHERE id=1 AND epoch=$epoch"
                                    ["$epoch",box epoch]
                if cmd.ExecuteNonQuery() <> 1 then
                    invalidOp "Cannot close original-writer admission epoch.")
            inspect path
        with _ -> Error "OriginalWriterJournalUnknown/StopNotDurable"


    // An irreversible remote publisher must acquire this admission BEFORE
    // invoking Git, and its caller must verify the durable remote effect before
    // reporting success. Failed/uncertain confirmation remains UNKNOWN across
    // process restarts, including when STOP races an already-admitted push.
    // A settled duplicate is never a permit to publish again.
    let internal withPublication operationId kind authority gitRef payload
                                 (publish: unit -> Result<'a, 'error>)
                                 (verified: 'a -> bool)
                                 (failure: string -> 'error) : Result<'a, 'error> =
        match admit operationId kind authority gitRef payload with
        | Error reason -> Error(failure reason)
        | Ok None -> publish() // Default-off preserves existing Git behavior.
        | Ok(Some permit) when permit.Replay ->
            Error(failure "OriginalWriterJournalUnknown/SettledReplayMustUseCanonicalReceipt")
        | Ok(Some permit) ->
            match markAttempt permit with
            | Error reason -> Error(failure reason)
            | Ok () ->
                try
                    match publish() with
                    | Ok result when verified result ->
                        match settle permit true true with
                        | Ok () -> Ok result
                        | Error reason -> Error(failure reason)
                    | Ok _ ->
                        match settle permit false true with
                        | Ok () -> Error(failure "OriginalWriterJournalUnknown/PublicationUnverified")
                        | Error reason -> Error(failure reason)
                    | Error originalError ->
                        match settle permit false true with
                        | Ok () -> Error originalError
                        | Error reason -> Error(failure reason)
                with _ ->
                    settle permit false true |> ignore
                    reraise()

    // Identity is the logical remote authority, not an interchangeable local
    // clone path or a caller-asserted actor name. Scope and ref are both bound.
    let internal authoritySha256 (parts: string list) =
        digest (String.concat "\\n" parts)

    // Immutable source refs and initialization have no caller UUID. Their
    // canonical ref and content deterministically identify one publication.
    let internal deterministicOperationId kind gitRef payload =
        SHA256.HashData(Encoding.UTF8.GetBytes(String.concat "\n" [ kind; gitRef; payload ]))
        |> Array.take 16
        |> Guid
