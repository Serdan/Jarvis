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

    // This commitment is stored independently of SQLite BEFORE the admitting
    // transaction commits and before any irreversible Git publication can begin.
    // Length-prefix fields to make the effect-identity encoding unambiguous.
    let private emptyHistory = digest "OriginalWriterAdmissionHistory/v1"

    let private extendHistory previous sequence key kind authority gitRef payloadHash epoch =
        [ previous; string sequence; key; kind; authority; gitRef; payloadHash; string epoch ]
        |> List.map (fun value -> string (Encoding.UTF8.GetByteCount value) + ":" + value)
        |> String.concat ""
        |> digest

    let private isLowerSha256 (value: string) =
        value.Length = 64 && (value |> Seq.forall Char.IsAsciiHexDigitLower)

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
                admission_sha256 TEXT NOT NULL,
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

    // This host-owned authority is deliberately OUTSIDE the binding directory.
    // Removal of the whole binding directory cannot turn an activated host
    // back into a never-activated, default-off original Git publisher.
    let private hostStateFile (root: string) =
        Path.Combine(Path.GetDirectoryName root, "original-git-writer-host-state")

    let private hostStateText path identity stopped epoch highWater history =
        String.concat "\n"
            [ "OriginalWriterHostState/v2"; path; identity
              (if stopped then "Stopped" else "Open")
              string epoch; string highWater; history ]

    let private readHostState stateFile =
        if not (File.Exists stateFile) then Ok None
        elif not (protectedFile stateFile) then
            Error "OriginalWriterJournalUnknown/HostStateMissingOrInsecure"
        else
            try
                let fields = File.ReadAllText(stateFile).Split('\n')
                if fields.Length <> 7 || fields.[0] <> "OriginalWriterHostState/v2"
                   || not (Path.IsPathFullyQualified fields.[1])
                   || (fields.[3] <> "Open" && fields.[3] <> "Stopped") then
                    Error "OriginalWriterJournalUnknown/HostStateMalformed"
                else
                    let epochOk, epoch = Int64.TryParse fields.[4]
                    let highWaterOk, highWater = Int64.TryParse fields.[5]
                    if not epochOk || not highWaterOk || epoch < 1L || highWater < 0L
                       || not (isLowerSha256 fields.[6]) then
                        Error "OriginalWriterJournalUnknown/HostStateWatermarkInvalid"
                    else
                        Ok(Some(fields.[1], fields.[2], fields.[3] = "Stopped",
                                epoch, highWater, fields.[6]))
            with _ -> Error "OriginalWriterJournalUnknown/HostStateUnreadable"

    let private requireBoundState root path identity =
        match readHostState (hostStateFile root) with
        | Ok(Some(pinned, journalIdentity, stopped, epoch, highWater, history))
            when pinned = path && journalIdentity = identity ->
            Ok(stopped, epoch, highWater, history)
        | Ok _ -> Error "OriginalWriterJournalUnknown/HostStateMissingOrChanged"
        | Error reason -> Error reason

    let private updateHostStateAtomically stateFile content =
        // The old record remains intact until a same-filesystem durable rename.
        // Crash after this STOP rename but before closing SQLite must deny
        // admission and require Recovery, never re-open the old Git writer.
        let temporary = stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            createProtected temporary content
            File.Move(temporary, stateFile, true)
            syncParent stateFile
        finally
            if File.Exists temporary then File.Delete temporary

    let private readJournalIdentity (db: SqliteConnection) (tx: SqliteTransaction) =
        use cmd = command db tx "SELECT journal_id FROM journal_identity WHERE id=1" []
        let value = cmd.ExecuteScalar()
        if isNull value then invalidOp "OriginalWriterJournalUnknown/JournalIdentityUnavailable"
        string value

    // Admission can check the latest committed row in O(1); a stale snapshot
    // from before an admission has a different tail from the monotonic host
    // authority. STOP and inspection also independently verify the entire
    // identity chain, rejecting deletions or modified earlier rows.
    let private readHistoryTail (db: SqliteConnection) (tx: SqliteTransaction) =
        use cmd =
            command db tx
                "SELECT sequence,admission_sha256 FROM effects ORDER BY sequence DESC LIMIT 1" []
        use reader = cmd.ExecuteReader()
        if reader.Read() then reader.GetInt64 0, reader.GetString 1
        else 0L, emptyHistory

    let private readHistorySummary (db: SqliteConnection) (tx: SqliteTransaction) =
        use cmd =
            command db tx
                """SELECT sequence,effect_key,kind,authority_sha256,git_ref,
                          payload_sha256,epoch,admission_sha256
                   FROM effects ORDER BY sequence""" []
        use reader = cmd.ExecuteReader()
        let mutable previousSequence = 0L
        let mutable previousHash = emptyHistory
        while reader.Read() do
            let sequence = reader.GetInt64 0
            if sequence <= previousSequence then
                invalidOp "OriginalWriterJournalUnknown/AdmissionSequenceInvalid"
            let nextHash =
                extendHistory previousHash sequence
                    (reader.GetString 1) (reader.GetString 2) (reader.GetString 3)
                    (reader.GetString 4) (reader.GetString 5) (reader.GetInt64 6)
            if reader.GetString 7 <> nextHash then
                invalidOp "OriginalWriterJournalUnknown/AdmissionHistoryCorrupt"
            previousSequence <- sequence
            previousHash <- nextHash
        previousSequence, previousHash

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
            let state = hostStateFile root
            match hostRootState root with
            | Error reason -> Error reason
            | Ok rootExists when rootExists && (File.Exists binding || File.Exists seal) ->
                match readHostBinding binding seal with
                | Error reason -> Error reason
                | Ok pinned ->
                    match journalId pinned with
                    | None -> Error "OriginalWriterJournalUnknown/JournalIdentityUnavailable"
                    | Some identity ->
                        match requireBoundState root pinned identity with
                        | Error reason -> Error reason
                        | Ok(_, epoch, _, _) when epoch <> 1L ->
                            Error "OriginalWriterJournalUnknown/HostStateEpochMismatch"
                        | Ok _ when String.IsNullOrWhiteSpace requested ->
                            Error "OriginalWriterJournalUnknown/ActivatedHostBindingMissing"
                        | Ok _ when requested <> pinned ->
                            Error "OriginalWriterJournalUnknown/ActivatedHostBindingChanged"
                        | Ok _ -> Ok(Some pinned)
            | Ok true ->
                // Root exists but its original binding was removed or incomplete.
                Error "OriginalWriterJournalUnknown/ActivatedHostBindingDeleted"
            | Ok false when File.Exists state || Directory.Exists state ->
                // Host authority lives outside the removed binding directory.
                // Neither root loss nor environment loss grants direct-push fallback.
                Error "OriginalWriterJournalUnknown/ActivatedHostBindingRootDeleted"
            | Ok false when String.IsNullOrWhiteSpace requested ->
                // Genuine preactivation only; absence of all local authority after
                // host storage loss is unprovable and requires operator Recovery.
                Ok None
            | Ok false when not (safeJournalPath requested) ->
                Error "OriginalWriterJournalUnknown/UntrustedOrMissingJournal"
            | Ok false ->
                match journalId requested with
                | None -> Error "OriginalWriterJournalUnknown/JournalIdentityUnavailable"
                | Some identity ->
                    // First persist independent host authority; interrupted
                    // enrollment then fails closed instead of returning default-off.
                    let parent = Path.GetDirectoryName root
                    Directory.CreateDirectory parent |> ignore
                    if not (noRedirects parent) then
                        Error "OriginalWriterJournalUnknown/UntrustedHostAuthorityParent"
                    else
                        createProtected state (hostStateText requested identity false 1L 0L emptyHistory)
                        syncParent state
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
                        // SQLite serializes admission with STOP, while the sibling
                        // host authority prevents a restored journal from forgetting
                        // previously admitted irreversible or UNKNOWN effects.
                        let existingHighWater, existingHash = readHistoryTail db tx
                        let identity = readJournalIdentity db tx
                        match requireBoundState (boundRoot()) path identity with
                        | Ok(false, authorityEpoch, authorityHighWater, authorityHash)
                            when authorityEpoch = epoch && authorityHighWater = existingHighWater
                                 && authorityHash = existingHash -> ()
                        | Ok(true, _, _, _) -> invalidOp "OriginalWriterAdmissionClosed"
                        | Ok _ -> invalidOp "OriginalWriterJournalUnknown/AdmissionHistoryRollback"
                        | Error reason -> invalidOp reason
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
                                        """INSERT INTO effects(effect_key,kind,authority_sha256,git_ref,payload_sha256,epoch,status,admission_sha256)
                                           VALUES($key,$kind,$authority,$ref,$payload,$epoch,'Admitted',$history)"""
                                        ["$key", box key; "$kind", box kind; "$authority", box authority;
                                         "$ref", box gitRef; "$payload", box payloadHash; "$epoch", box epoch;
                                         "$history", box emptyHistory]
                                insert.ExecuteNonQuery() |> ignore
                                use seqCommand = command db tx "SELECT last_insert_rowid()" []
                                let sequence = Convert.ToInt64(seqCommand.ExecuteScalar())
                                let newHash =
                                    extendHistory existingHash sequence key kind authority gitRef payloadHash epoch
                                use recordCommitment =
                                    command db tx
                                        "UPDATE effects SET admission_sha256=$history WHERE sequence=$sequence"
                                        ["$history", box newHash; "$sequence", box sequence]
                                if recordCommitment.ExecuteNonQuery() <> 1 then
                                    invalidOp "OriginalWriterJournalUnknown/AdmissionCommitmentNotDurable"
                                // CRITICAL ORDER: durable host commitment first; SQLite
                                // commit second; remote publication only after both.
                                // A crash between the two leaves host ahead and
                                // requires Recovery instead of fabricating clean STOP.
                                updateHostStateAtomically (hostStateFile (boundRoot()))
                                    (hostStateText path identity false epoch sequence newHash)
                                Ok(Some {DatabasePath=path; EffectKey=key; Sequence=sequence;
                                         Epoch=epoch; Replay=false}))
                with
                | :? InvalidOperationException as ex when ex.Message.StartsWith("OriginalWriter") ->
                    Error ex.Message
                | _ -> Error "OriginalWriterJournalUnknown/AdmissionNotDurable"

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
                let highWater, history = readHistorySummary db tx
                // A witness from restored SQLite cannot override independently
                // durable host STOP authority or its settled-effect high-water.
                match readHostState (hostStateFile (boundRoot())) with
                | Error reason -> invalidOp reason
                | Ok(Some(pinned, identity, stopped, authorityEpoch, authorityHighWater, authorityHistory))
                    when pinned = path ->
                    if identity <> readJournalIdentity db tx
                       || authorityEpoch <> epoch || stopped <> closed
                       || highWater <> authorityHighWater || history <> authorityHistory then
                        invalidOp "OriginalWriterJournalUnknown/AdmissionHistoryRollback"
                | Ok None ->
                    match hostRootState (boundRoot()) with
                    | Ok true ->
                        invalidOp "OriginalWriterJournalUnknown/HostStateMissingOrChanged"
                    | Error reason -> invalidOp reason
                    | Ok false -> () // Unactivated disposable journal.
                | _ -> () // Another isolated unactivated journal on this host.
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
                let epoch, alreadyClosed = readControl db tx
                let identity = readJournalIdentity db tx
                let highWater, history = readHistorySummary db tx
                match requireBoundState (boundRoot()) path identity with
                | Ok(false, authorityEpoch, authorityHighWater, authorityHistory)
                    when not alreadyClosed && authorityEpoch = epoch
                         && highWater = authorityHighWater && history = authorityHistory ->
                    // STOP is durable BEFORE updating the journal control row.
                    // A crash between host STOP and SQLite close requires Recovery.
                    updateHostStateAtomically (hostStateFile (boundRoot()))
                        (hostStateText path identity true epoch highWater history)
                | Ok(true, authorityEpoch, authorityHighWater, authorityHistory)
                    when alreadyClosed && authorityEpoch = epoch
                         && highWater = authorityHighWater && history = authorityHistory ->
                    () // Idempotent already-stopped journal with verified identity history.
                | _ -> invalidOp "OriginalWriterJournalUnknown/StopAuthorityMismatch"
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
    // A caller can separately prove that every attempted publication was
    // rejected without an external effect; that outcome settles as Rejected,
    // not Unknown. A changed remote head alone is not that proof.
    // A settled duplicate is never a permit to publish again.
    let internal withPublicationClassified operationId kind authority gitRef payload
                                 (publish: unit -> Result<'a, 'error>)
                                 (verified: 'a -> bool)
                                 (provenRejected: 'a -> bool)
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
                    | Ok result when provenRejected result ->
                        match settle permit false false with
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

    let internal withPublication operationId kind authority gitRef payload
                                 (publish: unit -> Result<'a, 'error>)
                                 (verified: 'a -> bool)
                                 (failure: string -> 'error) : Result<'a, 'error> =
        withPublicationClassified operationId kind authority gitRef payload
            publish verified (fun _ -> false) failure

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
