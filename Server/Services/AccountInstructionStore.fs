namespace Server.Services

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open Server

/// Exact account-wide AGENTS.md state. "missing" is not the SHA-256 of an empty file.
[<CLIMutable>]
type AccountInstructionSnapshot =
    { Status: string
      Revision: string
      Sha256: string option
      Content: string option
      Problem: string option }

type AccountInstructionUpdateError =
    | RevisionConflict of currentRevision: string
    | InvalidInput of message: string
    | StorageUnavailable of message: string

type private AccountInstructionState =
    | Missing
    | Present of content: string * sha256: string
    | Unavailable of reason: string

/// One server-owned UTF-8 file per authenticated subject; no project ledger or database.
/// All reads use the startup-loaded snapshot. Writes serialize CAS and update memory only
/// after the rename of a fully flushed, private temporary file succeeds.
type AccountInstructionStore(rootDirectory: string) =
    let gate = obj()
    let root =
        if String.IsNullOrWhiteSpace rootDirectory then
            invalidArg "rootDirectory" "A private account instruction directory is required."
        Path.GetFullPath rootDirectory
    let strictUtf8 = UTF8Encoding(false, true)
    let maxBytes = 64 * 1024
    let states = Dictionary<string, AccountInstructionState>(StringComparer.Ordinal)
    let deliveries = Dictionary<struct (string * string * string * string * int64), string>()
    let directoryMode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    let fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    let digest (bytes: byte[]) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun hash -> hash.ToLowerInvariant()

    let validKey (name: string) =
        name.Length = 64
        && name |> Seq.forall (fun ch -> (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))

    let isLink (path: string) =
        try
            (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint
        with :? FileNotFoundException | :? DirectoryNotFoundException -> false

    let ensurePrivateDirectory path =
        if isLink path then invalidOp "Account instruction storage cannot use symbolic links."
        if File.Exists path && not (Directory.Exists path) then
            invalidOp "Account instruction storage directory is not a directory."
        Directory.CreateDirectory path |> ignore
        if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(path, directoryMode)
        if isLink path then invalidOp "Account instruction storage cannot use symbolic links."

    let accountFolder key = Path.Combine(root, key)
    let documentPath key = Path.Combine(accountFolder key, "AGENTS.md")

    let load key =
        let folder = accountFolder key
        let file = documentPath key
        try
            if isLink folder then Unavailable "Account instruction directory is a link."
            elif File.Exists folder && not (Directory.Exists folder) then
                Unavailable "Account instruction directory is invalid."
            elif not (Directory.Exists folder) then Missing
            elif isLink file then Unavailable "Account instruction document is a link."
            elif Directory.Exists file then Unavailable "Account instruction document is a directory."
            elif not (File.Exists file) then Missing
            else
                let info = FileInfo(file)
                if info.Length > int64 maxBytes then
                    Unavailable "Account instruction document exceeds 64 KiB."
                else
                    let bytes = File.ReadAllBytes file
                    if bytes.Length > maxBytes then
                        Unavailable "Account instruction document exceeds 64 KiB."
                    else
                        Present(strictUtf8.GetString(bytes), digest bytes)
        with
        | :? DecoderFallbackException -> Unavailable "Account instruction document is not valid UTF-8."
        | :? UnauthorizedAccessException -> Unavailable "Account instruction document is unreadable."
        | :? IOException -> Unavailable "Account instruction document is unreadable."

    let state key =
        match states.TryGetValue key with
        | true, known -> known
        | _ -> Missing

    let view = function
        | Missing ->
            { Status = "missing"; Revision = "missing"; Sha256 = None
              Content = None; Problem = None }
        | Present(content, hash) ->
            { Status = "present"; Revision = "sha256:" + hash; Sha256 = Some hash
              Content = Some content; Problem = None }
        | Unavailable why ->
            { Status = "unavailable"; Revision = "unavailable"; Sha256 = None
              Content = None; Problem = Some why }

    do
        // Startup fails closed if the root cannot be enumerated. Per-account corruption
        // remains a typed unavailable state, never silently becoming "missing".
        ensurePrivateDirectory root
        for path in Directory.EnumerateFileSystemEntries root do
            let key = Path.GetFileName path
            if validKey key then
                states[key] <- load key

    member _.Read(authenticatedSubject: string) =
        let key = Auth.profileId authenticatedSubject
        lock gate (fun () -> view (state key))

    member _.Update(authenticatedSubject: string, expectedRevision: string, content: string)
        : Result<AccountInstructionSnapshot, AccountInstructionUpdateError> =
        if isNull content then Error(InvalidInput "Content must be a non-null UTF-8 string.")
        else
            let encoded =
                try Ok(strictUtf8.GetBytes content)
                with :? EncoderFallbackException ->
                    Error(InvalidInput "Content contains an invalid UTF-16 surrogate.")
            encoded
            |> Result.bind (fun bytes ->
                if bytes.Length > maxBytes then Error(InvalidInput "Content exceeds 64 KiB.")
                elif String.IsNullOrWhiteSpace expectedRevision then
                    Error(InvalidInput "An exact expected revision from GetAccountInstructions is required.")
                else
                    let key = Auth.profileId authenticatedSubject
                    lock gate (fun () ->
                        let previous = state key |> view
                        if previous.Status = "unavailable" then
                            Error(StorageUnavailable "Existing account instructions are unavailable; the file must be repaired before an update.")
                        elif not (String.Equals(previous.Revision, expectedRevision, StringComparison.Ordinal)) then
                            Error(RevisionConflict previous.Revision)
                        else
                            let folder = accountFolder key
                            let target = documentPath key
                            let pending = Path.Combine(folder, ".AGENTS.md." + Guid.NewGuid().ToString("N") + ".tmp")
                            try
                                ensurePrivateDirectory root
                                ensurePrivateDirectory folder
                                if isLink target || Directory.Exists target then
                                    invalidOp "The account instruction document was replaced by an unsafe filesystem entry."
                                use output =
                                    new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 8192, FileOptions.WriteThrough)
                                if not (OperatingSystem.IsWindows()) then
                                    File.SetUnixFileMode(pending, fileMode)
                                output.Write(bytes, 0, bytes.Length)
                                output.Flush(true)
                                output.Dispose()
                                // Same-directory rename is atomic: a process interruption cannot
                                // leave readers with a partially written AGENTS.md.
                                File.Move(pending, target, true)
                                let updated = Present(content, digest bytes)
                                states[key] <- updated
                                Ok(view updated)
                            with ex ->
                                try if File.Exists pending then File.Delete pending with _ -> ()
                                Error(StorageUnavailable $"Could not durably write account instructions ({ex.GetType().Name}).")))

    /// Suppress duplicate automatic content for the same agent/client generation.
    /// Explicit reads bypass this cache. Unavailable guidance always emits a warning.
    member _.NextDelivery(authenticatedSubject: string, agentId: string,
                          projectName: string, deviceId: string, generation: int64,
                          explicitRead: bool) =
        let accountKey = Auth.profileId authenticatedSubject
        lock gate (fun () ->
            let current = state accountKey |> view
            if explicitRead then Some current
            elif current.Status = "unavailable" then Some current
            elif String.IsNullOrWhiteSpace projectName then None
            // A tool without a stable agent identity cannot safely use the
            // deduplication cache; deliver guidance rather than silently omit it.
            elif String.IsNullOrWhiteSpace agentId then
                if current.Status = "missing" then None else Some current
            else
                let cacheKey = struct (accountKey, agentId, projectName, deviceId, generation)
                let seen =
                    match deliveries.TryGetValue cacheKey with
                    | true, old -> old = current.Revision
                    | _ -> false
                if seen then None
                else
                    // Conservative redelivery is safer than an unbounded
                    // server-global cache keyed by caller-selectable names.
                    if deliveries.Count >= 8192 then deliveries.Clear()
                    deliveries[cacheKey] <- current.Revision
                    if current.Status = "missing" then None else Some current)
