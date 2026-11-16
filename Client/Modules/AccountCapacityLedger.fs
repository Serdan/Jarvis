module Client.AccountCapacityLedger

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// Account-wide admission is a separate Git authority from every project work ledger.
// These primitives intentionally have no Scheduler, browser, or migration activation hook.
type AccountFailure =
    | InvalidAccountBinding of string
    | AccountRemoteUnavailable of string
    | AccountIntegrityError of string
    | AccountGitError of string
    | AccountOutcomeUnknown of Guid
    | AccountContended
    | AccountOperationIdReused
    | AccountCapacityFull of occupied: int * limit: int
    | AccountResizeBelowOccupancy of occupied: int
    | AccountConfigVersionChanged of actual: int64
    | AccountHeadChanged of expected: string * actual: string
    | AccountStateVersionChanged of actual: int64
    | AccountMigrationNotFrozen of actual: string
    | AccountMigrationOutstandingSlots of count: int
    | AccountMigrationWritesFenced of phase: string
    | AccountLaunchExists
    | AccountLaunchMissing
    | AccountLaunchAlreadyReleased
    | AccountTerminalProofRejected of string

type AccountBinding =
    { RepositoryPath: string
      RemoteName: string
      ApprovedFetchUrl: string
      ApprovedPushUrl: string
      AccountId: Guid
      LedgerId: Guid
      Epoch: int }

// Immutable, independently configured authority for one project Git ledger.
// Clone-local repository paths and remote aliases are not identity.
type ProjectLedgerAuthority =
    { ProjectId: Guid
      LedgerId: Guid
      Epoch: int
      ApprovedFetchUrl: string
      ApprovedPushUrl: string }

[<CLIMutable>]
type AccountTerminalProof =
    { ProjectId: string
      LaunchId: string
      AdmissionOperationId: string
      ProjectLedgerId: string
      ProjectLedgerEpoch: int
      ProjectAuthoritySha256: string
      ProjectHead: string
      TerminalOperationId: string
      TerminalBlobOid: string
      TerminalKind: string
      LaunchGeneration: int64 }

// The locator is not proof. Only the integration adapter may independently read
// the WQ-02 canonical project ledger and return a verified, currently terminal
// launch receipt. In particular, AbortOnly must prove the original admission
// never won the competing project CAS. A caller-supplied boolean is insufficient.
type ProjectTerminalLocator =
    { ProjectId: Guid
      LaunchId: Guid
      AdmissionOperationId: Guid
      TerminalOperationId: Guid }

type IProjectTerminalReceiptVerifier =
    abstract member VerifyCurrentTerminal:
        ProjectTerminalLocator -> Result<AccountTerminalProof, string>

[<CLIMutable>]
type AccountToken =
    { LaunchId: string
      ProjectId: string
      ProjectLedgerId: string
      ProjectLedgerEpoch: int
      ProjectAuthoritySha256: string
      RoleId: string
      RoleConfigVersion: int64
      AdmissionOperationId: string
      Status: string
      ReleaseProof: AccountTerminalProof }

[<CLIMutable>]
type AccountCapacityState =
    { SchemaVersion: int
      AccountId: string
      LedgerId: string
      Epoch: int
      MigrationPhase: string
      StateVersion: int64
      ConfigVersion: int64
      MaxConcurrentAgents: int
      NextSequence: int64
      Tokens: AccountToken array }

[<CLIMutable>]
type AccountOperationReceipt =
    { SchemaVersion: int
      OperationId: string
      Kind: string
      Actor: string
      CanonicalRequestJson: string
      RequestSha256: string
      OriginalResultJson: string
      ResultSha256: string
      Sequence: int64
      StateVersion: int64 }

type AccountSnapshot =
    { Head: string
      State: AccountCapacityState }

type AccountWrite =
    { Receipt: AccountOperationReceipt
      CurrentHead: string
      AlreadyApplied: bool }

// The original WQ07 operation receipt is the immutable Git provenance for
// this payload. Source-bound participants and three independently signed
// witnesses are carried by the canonical request/result, not a mutable
// sidecar or a project Prepared/dispatch authorization.
[<CLIMutable>]
type AccountMigrationParticipant =
    { ProjectId: string
      LedgerId: string
      LedgerEpoch: int
      AuthoritySha256: string
      FrozenHead: string
      ProjectVersion: int
      SourceHighWater: int64 }

[<CLIMutable>]
type AccountMigrationSignedWitness =
    { EvidenceSha256: string
      SignatureBase64: string
      SignerPublicKeySha256: string }

// This is a claim about the ORIGINAL legacy source/journal history, not
// the new canonical project ledger NextSequence. The trusted old-writer-fence
// signer separately witnesses exact journal identities and high-waters.
[<CLIMutable>]
type AccountMigrationOriginalSourceCheckpoint =
    { ProjectId: string
      FrozenProjectHead: string
      OriginalJournalId: string
      OriginalJournalHighWater: int64
      OriginalSourceEffectHighWater: int64 }

[<CLIMutable>]
type AccountMigrationPreparingReceipt =
    { SchemaVersion: int
      OriginalOperationId: string
      Actor: string
      AccountId: string
      LedgerId: string
      Epoch: int
      PreviousAccountHead: string
      PreviousStateVersion: int64
      ParticipantRoster: AccountMigrationParticipant array
      ParticipantRosterSha256: string
      ReviewedReportSha256: string
      OriginalSourceCheckpoints: AccountMigrationOriginalSourceCheckpoint array
      OriginalSourceCoverageSha256: string
      OriginalSourceCoverageWitness: AccountMigrationSignedWitness
      MinConnectedClientProtocol: int
      StatementSha256: string
      ClientProtocolWitness: AccountMigrationSignedWitness
      OldWriterFenceWitness: AccountMigrationSignedWitness
      OperatorApprovalWitness: AccountMigrationSignedWitness
      AccountStatus: string
      ProjectStatus: string }

// Controlled, dynamically scoped Git transport test seam. No production caller
// binds it; it permits deterministic post-publication uncertainty and stale
// snapshot interleavings against disposable remote repositories.
[<RequireQualifiedAccess>]
type AccountGitTestFault =
    | PassThrough
    | FailBeforeExecution
    | ExecuteThenLoseAcknowledgment

type AccountGitTestInterposition =
    { BeforeLeasePush: Guid -> string -> unit
      GitCommand: string list -> AccountGitTestFault }

module AccountCapacityLedger =
    // A private construction token prevents the low-level CAS from accepting
    // merely shape-correct, unauthenticated receipt records. The host verifier
    // must cryptographically authenticate all signatures before minting it.
    type internal AuthenticatedDisposablePreparingReceipt =
        private { SignedReceipt: AccountMigrationPreparingReceipt }

    let private preparingFrame (fields: string list) =
        fields
        |> List.map (fun value -> string (Encoding.UTF8.GetByteCount value) + ":" + value)
        |> String.concat ""

    let private preparingWitnessMessage role statementSha evidenceSha =
        Encoding.UTF8.GetBytes(
            preparingFrame [ "jarvis-account-preparing-attestation-v1"; role; statementSha; evidenceSha ])

    let private preparingWitnessValid (publicKey: byte array) role statementSha expectedEvidence
                                      (witness: AccountMigrationSignedWitness) =
        if isNull publicKey || publicKey.Length = 0 || isNull (box witness)
           || witness.EvidenceSha256 <> expectedEvidence
           || not (Regex.IsMatch(defaultArg (Option.ofObj expectedEvidence) "", "^[0-9a-f]{64}$"))
           || witness.SignerPublicKeySha256 <>
               Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant() then false
        else
            try
                use verifier = ECDsa.Create()
                let mutable read = 0
                verifier.ImportSubjectPublicKeyInfo(publicKey.AsSpan(), &read)
                read = publicKey.Length
                && verifier.VerifyData(
                    preparingWitnessMessage role statementSha expectedEvidence,
                    Convert.FromBase64String(witness.SignatureBase64),
                    HashAlgorithmName.SHA256)
            with _ -> false

    // Internal assembly boundary: the publicly callable account Preparing API
    // has NO argument through which an untrusted caller can nominate these keys.
    // Until a real host/operator-owned trust registry is provisioned, that
    // public API returns a typed Blocked outcome and never invokes this port.
    let internal authenticateDisposablePreparingReceipt (clientKey: byte array)
                                                         (fenceKey: byte array)
                                                         (operatorKey: byte array)
                                                         (receipt: AccountMigrationPreparingReceipt) =
        let keys = [ clientKey; fenceKey; operatorKey ]
        let distinctKeys =
            keys |> List.forall (fun k -> not (isNull k) && k.Length > 0)
            && (keys |> List.map (fun k -> Convert.ToHexString(SHA256.HashData k))
                     |> List.distinct |> List.length) = 3
        if isNull (box receipt) || not distinctKeys then
            Error(InvalidAccountBinding "Independent Preparing trust anchors are unavailable.")
        elif not (
            preparingWitnessValid clientKey "client-protocol" receipt.StatementSha256
                receipt.ClientProtocolWitness.EvidenceSha256 receipt.ClientProtocolWitness
            && preparingWitnessValid fenceKey "old-writer-fence" receipt.StatementSha256
                receipt.OldWriterFenceWitness.EvidenceSha256 receipt.OldWriterFenceWitness
            && preparingWitnessValid fenceKey "original-journal-coverage" receipt.StatementSha256
                receipt.OriginalSourceCoverageSha256 receipt.OriginalSourceCoverageWitness
            && preparingWitnessValid operatorKey "operator-approval" receipt.StatementSha256
                receipt.OperatorApprovalWitness.EvidenceSha256 receipt.OperatorApprovalWitness) then
            Error(InvalidAccountBinding "Preparing receipts must have independent, verified signatures.")
        else
            Ok { SignedReceipt = receipt }

    [<Literal>]
    let AccountRef = "refs/heads/jarvis-account-admission"

    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                              PropertyNameCaseInsensitive = true)
    let private shaPattern = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private remotePattern = Regex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled)
    let private enc = UTF8Encoding(false)
    let private json value = JsonSerializer.Serialize(value, options)
    let private hash (value: string) =
        Convert.ToHexString(SHA256.HashData(enc.GetBytes(value))).ToLowerInvariant()

    // Bind the exact trusted source/push authority while never persisting Git
    // remote URLs (which may contain credentials) to the account Git ledger.
    let projectAuthoritySha256 (project: ProjectLedgerAuthority) =
        hash (json {| projectId = string project.ProjectId
                      ledgerId = string project.LedgerId; epoch = project.Epoch
                      approvedFetchUrl = project.ApprovedFetchUrl
                      approvedPushUrl = project.ApprovedPushUrl |})
    let private receiptPath (id: Guid) = $"operations/{id:N}.json"
    let private trustedRef (b: AccountBinding) = $"refs/jarvis/account-trusted/{b.LedgerId:N}"
    let private validOid (v: string) = not (isNull v) && shaPattern.IsMatch v

    // JSON objects are canonicalized recursively. Typed requests contain every
    // semantic argument, not merely a client-supplied operation label.
    let private canonical (value: string) =
        use doc = JsonDocument.Parse(value)
        if doc.RootElement.ValueKind <> JsonValueKind.Object then
            invalidArg "value" "Expected a JSON object"
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        let rec emit (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                let properties =
                    element.EnumerateObject()
                    |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.Name, b.Name))
                    |> Seq.toArray
                let names = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                writer.WriteStartObject()
                for property in properties do
                    if not (names.Add property.Name) then
                        invalidArg "value" "Duplicate JSON member"
                    writer.WritePropertyName(property.Name)
                    emit property.Value
                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                for child in element.EnumerateArray() do emit child
                writer.WriteEndArray()
            | _ -> element.WriteTo(writer)
        emit doc.RootElement
        writer.Flush()
        enc.GetString(stream.ToArray())

    type private GitOutput = { Code: int; Out: string; Err: string }

    let private gitInterposition = System.Threading.AsyncLocal<AccountGitTestInterposition>()

    let withTestInterposition (hooks: AccountGitTestInterposition) operation =
        let previous = gitInterposition.Value
        gitInterposition.Value <- hooks
        try operation ()
        finally gitInterposition.Value <- previous

    let private gitWith (b: AccountBinding) args input environment =
        let fault =
            let hooks = gitInterposition.Value
            if isNull (box hooks) then AccountGitTestFault.PassThrough
            else hooks.GitCommand args
        if fault = AccountGitTestFault.FailBeforeExecution then
            Error(AccountRemoteUnavailable "Injected unavailable Git transport")
        else
            BoundedProcess.run
                { Executable = "git"
                  Args = args
                  WorkingDirectory = b.RepositoryPath
                  Timeout = TimeSpan.FromSeconds 30.
                  MaxOutputBytes = None
                  StandardInput = input |> Option.map BoundedProcess.TextInput
                                        |> Option.defaultValue BoundedProcess.ClosedInput
                  ExtraEnvironment =
                      [ "GIT_TERMINAL_PROMPT", "0"
                        "GIT_AUTHOR_NAME", "Jarvis Account Admission"
                        "GIT_AUTHOR_EMAIL", "jarvis-account@localhost"
                        "GIT_COMMITTER_NAME", "Jarvis Account Admission"
                        "GIT_COMMITTER_EMAIL", "jarvis-account@localhost" ] @ environment
                  ConfigureStart = Some(fun start ->
                      // Never inherit metadata redirects into the approved
                      // WQ07 repository. The explicit isolated index passed
                      // through ExtraEnvironment is applied after this hook.
                      for key in
                          [ "GIT_DIR"; "GIT_WORK_TREE"; "GIT_COMMON_DIR"
                            "GIT_OBJECT_DIRECTORY"; "GIT_ALTERNATE_OBJECT_DIRECTORIES"
                            "GIT_INDEX_FILE"; "GIT_NAMESPACE" ] do
                          start.Environment.Remove(key) |> ignore) }
            |> function
                | Ok output when not output.TimedOut ->
                    Ok { Code = output.ExitCode; Out = output.StdOut.TrimEnd('\n', '\r'); Err = output.StdErr }
                | Ok _ -> Error(AccountRemoteUnavailable "Git operation timed out; remote outcome may be unknown")
                | Error failure -> Error(AccountGitError(string failure))
            |> fun outcome ->
                if fault = AccountGitTestFault.ExecuteThenLoseAcknowledgment then
                    Error(AccountRemoteUnavailable "Injected lost Git acknowledgment")
                else outcome

    let private git b args = gitWith b args None []
    let private command b args =
        git b args
        |> Result.bind (fun x ->
            if x.Code = 0 then Ok x.Out
            else
                let joined = String.concat " " args
                Error(AccountGitError $"{joined}: {x.Err}"))
    let private gitInput b args input = gitWith b args (Some input) [] |> Result.bind (fun x ->
        if x.Code = 0 then Ok x.Out else Error(AccountGitError x.Err))
    let private gitIndex b path args =
        gitWith b args None [ "GIT_INDEX_FILE", path ]
        |> Result.bind (fun x ->
            if x.Code = 0 then Ok x.Out else Error(AccountGitError x.Err))

    let private embeddedSecrets (value: string) =
        let mutable uri = Unchecked.defaultof<Uri>
        Uri.TryCreate(value, UriKind.Absolute, &uri)
        && (uri.Scheme = "https" || uri.Scheme = "http" || uri.Scheme = "ssh")
        && (not (String.IsNullOrEmpty uri.Query)
            || not (String.IsNullOrEmpty uri.Fragment)
            || (uri.Scheme <> "ssh" && not (String.IsNullOrEmpty uri.UserInfo))
            || (uri.Scheme = "ssh" && uri.UserInfo.Contains(':')))

    let private validate (b: AccountBinding) =
        if not (Directory.Exists b.RepositoryPath)
           || b.AccountId = Guid.Empty || b.LedgerId = Guid.Empty || b.Epoch < 1
           || String.IsNullOrWhiteSpace b.ApprovedFetchUrl
           || String.IsNullOrWhiteSpace b.ApprovedPushUrl
           || not (remotePattern.IsMatch b.RemoteName)
           || b.RemoteName.Contains("..")
           || embeddedSecrets b.ApprovedFetchUrl || embeddedSecrets b.ApprovedPushUrl then
            Error(InvalidAccountBinding "Explicit, non-secret account identity and remote endpoints are required")
        else
            match command b [ "remote"; "get-url"; b.RemoteName ],
                  command b [ "remote"; "get-url"; "--push"; b.RemoteName ] with
            | Ok fetchUrl, Ok pushUrl when fetchUrl = b.ApprovedFetchUrl && pushUrl = b.ApprovedPushUrl -> Ok ()
            | _ -> Error(InvalidAccountBinding "Remote fetch/push endpoint differs from the account trust binding")

    let private refValue b name =
        git b [ "show-ref"; "--verify"; "--hash"; name ]
        |> Result.bind (fun x ->
            if x.Code = 0 && validOid x.Out then Ok(Some x.Out)
            elif x.Code = 1 then Ok None
            elif x.Code = 128 && x.Err.Contains("not a valid ref", StringComparison.Ordinal) then
                Ok None
            else Error(AccountGitError $"Unable to inspect Git ref {name}: {x.Err}"))

    let private isAncestor b older newer =
        git b [ "merge-base"; "--is-ancestor"; older; newer ]
        |> Result.bind (fun x ->
            if x.Code = 0 then Ok true
            elif x.Code = 1 then Ok false
            else Error(AccountIntegrityError "Cannot verify trusted account Git ancestry"))

    let private readState b head =
        command b [ "show"; $"{head}:state.json" ]
        |> Result.bind (fun raw ->
            try
                let s = JsonSerializer.Deserialize<AccountCapacityState>(raw, options)
                if isNull (box s)
                   || s.SchemaVersion <> 1 || s.AccountId <> string b.AccountId
                   || s.LedgerId <> string b.LedgerId || s.Epoch <> b.Epoch
                   || s.ConfigVersion < 1L || s.StateVersion < 1L
                   || s.NextSequence < 1L || s.MaxConcurrentAgents < 1
                   || s.MigrationPhase <> "LegacyActive" && s.MigrationPhase <> "Frozen"
                      && s.MigrationPhase <> "Preparing" && s.MigrationPhase <> "Validated"
                      && s.MigrationPhase <> "Activated" && s.MigrationPhase <> "Aborted"
                   || isNull s.Tokens
                   || s.Tokens |> Array.exists (fun t ->
                       isNull (box t) || String.IsNullOrWhiteSpace t.LaunchId
                       || String.IsNullOrWhiteSpace t.ProjectId || String.IsNullOrWhiteSpace t.RoleId
                       || String.IsNullOrWhiteSpace t.ProjectLedgerId
                       || t.ProjectLedgerId = string Guid.Empty || t.ProjectLedgerEpoch < 1
                       || isNull t.ProjectAuthoritySha256
                       || not (Regex.IsMatch(t.ProjectAuthoritySha256, "^[0-9a-f]{64}$"))
                       || t.Status <> "Held" && t.Status <> "Released"
                       || (t.Status = "Held" && not (isNull (box t.ReleaseProof)))
                       || (t.Status = "Released" &&
                           (isNull (box t.ReleaseProof)
                            || t.ReleaseProof.ProjectId <> t.ProjectId
                            || t.ReleaseProof.ProjectLedgerId <> t.ProjectLedgerId
                            || t.ReleaseProof.ProjectLedgerEpoch <> t.ProjectLedgerEpoch
                            || t.ReleaseProof.ProjectAuthoritySha256 <> t.ProjectAuthoritySha256)))
                   || (s.Tokens |> Array.map _.LaunchId |> Array.distinct |> Array.length) <> s.Tokens.Length
                   || (s.Tokens |> Array.filter (fun t -> t.Status = "Held") |> Array.length) > s.MaxConcurrentAgents then
                    Error(AccountIntegrityError "Account ledger identity, epoch, configuration, or token invariants failed")
                else Ok s
            with ex -> Error(AccountIntegrityError $"Invalid account state: {ex.Message}"))

    let private remoteExists b =
        git b [ "ls-remote"; "--exit-code"; b.RemoteName; AccountRef ]
        |> Result.bind (fun x ->
            if x.Code = 0 then Ok true
            elif x.Code = 2 then Ok false
            else Error(AccountRemoteUnavailable x.Err))

    let rec private fetchRetry attempts (b: AccountBinding) =
        match validate b with
        | Error e -> Error e
        | Ok () ->
            let stage = $"refs/jarvis/account-fetch/{Guid.NewGuid():N}"
            try
                match git b [ "fetch"; "--no-tags"; "--no-write-fetch-head"; "--refmap=";
                              b.RemoteName; $"{AccountRef}:{stage}" ] with
                | Error e -> Error e
                | Ok x when x.Code <> 0 ->
                    match remoteExists b with
                    | Ok false -> Error(AccountIntegrityError "Account admission branch missing")
                    | _ -> Error(AccountRemoteUnavailable x.Err)
                | Ok _ ->
                    command b [ "rev-parse"; "--verify"; $"{stage}^{{commit}}" ]
                    |> Result.bind (fun head ->
                        refValue b (trustedRef b)
                        |> Result.bind (fun previous ->
                            let ancestry =
                                match previous with
                                | None -> Ok true
                                | Some old -> isAncestor b old head
                            ancestry |> Result.bind (fun follows ->
                                if not follows then
                                    if attempts > 0 then fetchRetry (attempts - 1) b
                                    else Error(AccountIntegrityError "Account remote rewound from trusted history")
                                else
                                    readState b head |> Result.bind (fun state ->
                                        let old = previous |> Option.defaultValue (String.replicate 40 "0")
                                        git b [ "update-ref"; trustedRef b; head; old ]
                                        |> Result.bind (fun update ->
                                            if update.Code = 0 then Ok { Head = head; State = state }
                                            else
                                                match refValue b (trustedRef b) with
                                                | Ok(Some now) when now = head -> Ok { Head = head; State = state }
                                                | Ok _ when attempts > 0 -> fetchRetry (attempts - 1) b
                                                | _ -> Error(AccountContended))))))
            finally
                git b [ "update-ref"; "-d"; stage ] |> ignore

    // Every authoritative read goes to remote, never to the source worktree or stale clone.
    let fetch binding = fetchRetry 8 binding

    let private writeTree b parent (files: (string * string) list) =
        let index = Path.Combine(b.RepositoryPath, ".git", $"jarvis-account-{Guid.NewGuid():N}.index")
        try
            let first =
                match parent with
                | Some p -> gitIndex b index [ "read-tree"; p ]
                | None -> gitIndex b index [ "read-tree"; "--empty" ]
            first |> Result.bind (fun _ ->
                let rec add = function
                    | [] -> gitIndex b index [ "write-tree" ]
                    | (path, text) :: rest ->
                        gitInput b [ "hash-object"; "-w"; "--stdin" ] text
                        |> Result.bind (fun blob ->
                            gitIndex b index [ "update-index"; "--add"; "--cacheinfo";
                                               $"100644,{blob},{path}" ]
                            |> Result.bind (fun _ -> add rest))
                add files)
        finally
            if File.Exists index then File.Delete index

    let private commitTree b tree parent message =
        command b
            ([ "commit-tree"; tree; "-m"; message ] @
             (parent |> Option.map (fun p -> [ "-p"; p ]) |> Option.defaultValue []))

    let private readReceipt b head opId =
        let path = receiptPath opId
        command b [ "ls-tree"; head; "--"; path ]
        |> Result.bind (fun listing ->
            if String.IsNullOrEmpty listing then Ok None
            elif not (Regex.IsMatch(listing,
                         "^100644 blob [0-9a-f]{40}([0-9a-f]{24})?\\t" + Regex.Escape(path) + "$")) then
                Error(AccountIntegrityError "Account operation path has an unexpected Git object")
            else
                command b [ "show"; $"{head}:{path}" ]
                |> Result.bind (fun raw ->
                    try
                        let r = JsonSerializer.Deserialize<AccountOperationReceipt>(raw, options)
                        if isNull (box r) || r.SchemaVersion <> 1
                           || r.OperationId <> string opId || r.Sequence < 1L
                           || r.StateVersion < 1L || r.CanonicalRequestJson = null
                           || r.OriginalResultJson = null
                           || hash r.CanonicalRequestJson <> r.RequestSha256
                           || hash r.OriginalResultJson <> r.ResultSha256 then
                            Error(AccountIntegrityError "Invalid immutable account operation receipt")
                        else
                            // A descendant cannot silently replace an operation
                            // with a newly self-consistent digest and replay it.
                            command b [ "log"; "--first-parent"; "--diff-filter=A"; "--format=%H";
                                        head; "--"; path ]
                            |> Result.bind (fun history ->
                                let origins = history.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                if origins.Length <> 1 || not (validOid origins[0]) then
                                    Error(AccountIntegrityError "Account receipt has no unique historical origin")
                                else
                                    command b [ "ls-tree"; origins[0]; "--"; path ]
                                    |> Result.bind (fun original ->
                                        if original <> listing then
                                            Error(AccountIntegrityError "Immutable account operation record was rewritten")
                                        else
                                            command b [ "show"; "-s"; "--format=%B"; origins[0] ]
                                            |> Result.bind (fun message ->
                                                if message.Contains($"Operation-Id: {opId:D}", StringComparison.Ordinal)
                                                   && message.Contains(
                                                       $"Request-SHA256: {r.RequestSha256}", StringComparison.Ordinal) then
                                                    Ok(Some r)
                                                else
                                                    Error(AccountIntegrityError "Account origin trailers mismatch"))))
                    with ex ->
                        Error(AccountIntegrityError $"Unreadable account receipt: {ex.Message}")))

    let readOperation b opId =
        fetch b |> Result.bind (fun s ->
            readReceipt b s.Head opId |> Result.map (fun receipt -> s, receipt))

    let initialize (b: AccountBinding) initialCapacity =
        if initialCapacity < 1 then
            Error(InvalidAccountBinding "Initial global capacity must be positive")
        else
            validate b |> Result.bind (fun () ->
                remoteExists b |> Result.bind (fun exists ->
                    if exists then fetch b
                    else
                        let state =
                            { SchemaVersion = 1; AccountId = string b.AccountId
                              LedgerId = string b.LedgerId; Epoch = b.Epoch
                              MigrationPhase = "LegacyActive"
                              StateVersion = 1L; ConfigVersion = 1L
                              MaxConcurrentAgents = initialCapacity
                              NextSequence = 1L; Tokens = [||] }
                        writeTree b None [ "state.json", json state ]
                        |> Result.bind (fun tree ->
                            commitTree b tree None "Initialize separate account admission ledger"
                            |> Result.bind (fun proposed ->
                                // Empty expected remote ref: no overwrite, including an init race.
                                git b [ "push"; "--porcelain"; $"--force-with-lease={AccountRef}:";
                                        b.RemoteName; $"{proposed}:{AccountRef}" ] |> ignore
                                // Re-read even on a lost acknowledgment; a competing initializer
                                // with different identity will fail pinned-identity validation.
                                match fetch b with
                                | Ok snapshot -> Ok snapshot
                                | Error(AccountIntegrityError message) -> Error(AccountIntegrityError message)
                                | Error(InvalidAccountBinding message) -> Error(InvalidAccountBinding message)
                                | Error _ -> Error(AccountOutcomeUnknown Guid.Empty)))))

    let private transact (b: AccountBinding) operationId actor kind requestJson expectedHead
                         (plan: AccountCapacityState -> Result<AccountCapacityState * string, AccountFailure>) =
        if operationId = Guid.Empty || String.IsNullOrWhiteSpace actor
           || String.IsNullOrWhiteSpace kind then
            Error(InvalidAccountBinding "Stable operation ID, actor and kind required")
        else
            try
                let request = canonical requestJson
                let fullRequest =
                    canonical (json {| schemaVersion = 1; accountId = b.AccountId.ToString("D"); actor = actor; kind = kind; request = request |})
                let requestHash = hash fullRequest
                let rec attempt retry =
                    fetch b |> Result.bind (fun snapshot ->
                        readReceipt b snapshot.Head operationId |> Result.bind (function
                            | Some old when old.CanonicalRequestJson = fullRequest
                                            && old.RequestSha256 = requestHash ->
                                Ok { Receipt = old; CurrentHead = snapshot.Head; AlreadyApplied = true }
                            | Some _ -> Error AccountOperationIdReused
                            | None ->
                                (match expectedHead with
                                 | Some expected when snapshot.Head <> expected ->
                                     Error(AccountHeadChanged(expected, snapshot.Head))
                                 | _ -> plan snapshot.State)
                                |> Result.bind (fun (updated, resultJson) ->
                                    let next =
                                        { updated with StateVersion = snapshot.State.StateVersion + 1L
                                                       NextSequence = snapshot.State.NextSequence + 1L }
                                    let result = canonical resultJson
                                    let receipt =
                                        { SchemaVersion = 1
                                          OperationId = string operationId
                                          Actor = actor; Kind = kind
                                          CanonicalRequestJson = fullRequest
                                          RequestSha256 = requestHash
                                          OriginalResultJson = result
                                          ResultSha256 = hash result
                                          Sequence = snapshot.State.NextSequence
                                          StateVersion = next.StateVersion }
                                    writeTree b (Some snapshot.Head)
                                        [ "state.json", json next; receiptPath operationId, json receipt ]
                                    |> Result.bind (fun tree ->
                                        commitTree b tree (Some snapshot.Head)
                                            $"Account {kind}\n\nOperation-Id: {operationId:D}\nRequest-SHA256: {requestHash}"
                                        |> Result.bind (fun proposed ->
                                            // A lease requires *this* observed parent: stale writers cannot
                                            // push an incompatible capacity calculation.
                                            let hooks = gitInterposition.Value
                                            if not (isNull (box hooks)) then
                                                hooks.BeforeLeasePush operationId snapshot.Head
                                            let pushed =
                                                git b [ "push"; "--porcelain";
                                                        $"--force-with-lease={AccountRef}:{snapshot.Head}";
                                                        b.RemoteName; $"{proposed}:{AccountRef}" ]
                                            match fetch b with
                                            | Error _ -> Error(AccountOutcomeUnknown operationId)
                                            | Ok current ->
                                                match readReceipt b current.Head operationId with
                                                | Error e -> Error e
                                                | Ok(Some observed) when observed.CanonicalRequestJson <> fullRequest ->
                                                    Error AccountOperationIdReused
                                                | Ok(Some observed) ->
                                                    isAncestor b proposed current.Head
                                                    |> Result.map (fun ourCommit ->
                                                        { Receipt = observed; CurrentHead = current.Head
                                                          AlreadyApplied = not ourCommit })
                                                | Ok None when current.Head <> snapshot.Head && retry < 8 ->
                                                    attempt (retry + 1)
                                                | Ok None when retry >= 8 -> Error AccountContended
                                                | Ok None ->
                                                    // Transport failure or dropped reply does not prove
                                                    // that this Git push cannot still be applied.
                                                    match pushed with
                                                    | Error _ -> Error(AccountOutcomeUnknown operationId)
                                                    | Ok _ -> Error(AccountOutcomeUnknown operationId))))))
                attempt 0
            with :? ArgumentException as ex ->
                Error(InvalidAccountBinding $"Invalid operation payload: {ex.Message}")
               | :? JsonException as ex ->
                Error(InvalidAccountBinding $"Invalid operation JSON: {ex.Message}")

    let occupied (s: AccountCapacityState) =
        s.Tokens |> Array.sumBy (fun t -> if t.Status = "Held" then 1 else 0)

    // Defense in depth at the *actual Git writer*, independently of the
    // higher-level assessor. No direct internal caller can use a production,
    // unmarked, redirected, or ordinary account clone as a Preparing target.
    let private approvedDisposableAccountSink (rootRaw: string) (b: AccountBinding) =
        try
            let explicitPath (raw: string) =
                not (String.IsNullOrWhiteSpace raw)
                && Path.IsPathFullyQualified raw
                && (raw.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.forall ((<>) ".."))
            let rec noReparseAncestors (path: string) =
                let attrs = File.GetAttributes path
                not (attrs.HasFlag FileAttributes.ReparsePoint)
                && (let parent = Path.GetDirectoryName path
                    isNull parent || parent = path || noReparseAncestors parent)
            let rec noMetadataRedirects directory =
                Directory.Exists directory && noReparseAncestors directory
                && (Directory.EnumerateFileSystemEntries directory
                    |> Seq.forall (fun item ->
                        let attrs = File.GetAttributes item
                        not (attrs.HasFlag FileAttributes.ReparsePoint)
                        && (not (attrs.HasFlag FileAttributes.Directory)
                            || noMetadataRedirects item)))
            if not (explicitPath rootRaw) || not (explicitPath b.RepositoryPath)
               || not (explicitPath b.ApprovedFetchUrl)
               || not (explicitPath b.ApprovedPushUrl) then false
            else
                let root = Path.GetFullPath rootRaw
                let temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                let inside candidate =
                    let full = Path.GetFullPath candidate
                    full.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && Directory.Exists full && noReparseAncestors full
                let scope = Path.GetDirectoryName b.RepositoryPath
                let marker = Path.Combine(root, ".wq10-disposable-fixture")
                let bare authority =
                    inside authority && noMetadataRedirects authority
                    && File.Exists(Path.Combine(authority, "HEAD"))
                    && File.Exists(Path.Combine(authority, "config"))
                    && Directory.Exists(Path.Combine(authority, "refs"))
                    && Directory.Exists(Path.Combine(authority, "objects"))
                    && not (File.Exists(Path.Combine(authority, "objects", "info", "alternates")))
                let permissionsOk =
                    if OperatingSystem.IsWindows() then true
                    else
                        let rootMode = File.GetUnixFileMode root
                        let scopeMode = File.GetUnixFileMode scope
                        let unsafeRoot = UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite
                        let unsafeScope =
                            UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute
                            ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute
                        (rootMode &&& unsafeRoot) = UnixFileMode.None
                        && (scopeMode &&& unsafeScope) = UnixFileMode.None
                root.StartsWith(temp + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && Directory.Exists root && noReparseAncestors root
                && File.Exists marker && noReparseAncestors marker
                && File.ReadAllText(marker) = "WQ10_DISPOSABLE_FIXTURE_V1"
                && inside scope && inside b.RepositoryPath
                && Path.GetFileName(scope).StartsWith("wq10-private-", StringComparison.Ordinal)
                && Path.GetFileName b.RepositoryPath = "account"
                && permissionsOk
                && noMetadataRedirects(Path.Combine(b.RepositoryPath, ".git"))
                && not (File.Exists(Path.Combine(b.RepositoryPath, ".git", "objects", "info", "alternates")))
                && not (File.Exists(Path.Combine(b.RepositoryPath, ".git", "commondir")))
                && bare b.ApprovedFetchUrl && bare b.ApprovedPushUrl
        with _ -> false

    // Assembly-internal on purpose. Only the WQ10 verified disposable port
    // may use this CAS; no public unchecked Preparing API or approval boolean.
    // The original request is stable through retries and pins its previous
    // head so a loser cannot silently re-plan against the winning epoch.
    let internal prepareAccountForVerifiedDisposable disposableRoot (b: AccountBinding)
                                                      (authenticated: AuthenticatedDisposablePreparingReceipt) =
        let receipt =
            if isNull (box authenticated) then Unchecked.defaultof<AccountMigrationPreparingReceipt>
            else authenticated.SignedReceipt
        let sha256 = Regex("^[0-9a-f]{64}$", RegexOptions.Compiled)
        let validOriginalJournalId (raw: string) =
            let mutable parsed = Guid.Empty
            Guid.TryParse(raw, &parsed) && parsed <> Guid.Empty
        let validWitness (w: AccountMigrationSignedWitness) =
            not (isNull (box w))
            && sha256.IsMatch(defaultArg (Option.ofObj w.EvidenceSha256) "")
            && sha256.IsMatch(defaultArg (Option.ofObj w.SignerPublicKeySha256) "")
            && not (String.IsNullOrWhiteSpace w.SignatureBase64)
        let mutable operationId = Guid.Empty
        if not (approvedDisposableAccountSink disposableRoot b)
           || isNull (box receipt)
           || not (Guid.TryParse(receipt.OriginalOperationId, &operationId))
           || operationId = Guid.Empty
           || String.IsNullOrWhiteSpace receipt.Actor
           || receipt.SchemaVersion <> 1
           || receipt.AccountId <> string b.AccountId
           || receipt.LedgerId <> string b.LedgerId
           || receipt.Epoch <> b.Epoch
           || not (validOid receipt.PreviousAccountHead)
           || receipt.PreviousStateVersion < 1L
           || receipt.MinConnectedClientProtocol < 1
           || isNull receipt.ParticipantRoster
           || receipt.ParticipantRoster.Length <> 2
           || ((receipt.ParticipantRoster |> Array.map _.ProjectId |> Array.distinct |> Array.length) <> 2)
           || (receipt.ParticipantRoster |> Array.exists (fun p ->
                isNull (box p) || String.IsNullOrWhiteSpace p.ProjectId
                || String.IsNullOrWhiteSpace p.LedgerId || p.LedgerEpoch < 1
                || not (sha256.IsMatch(defaultArg (Option.ofObj p.AuthoritySha256) ""))
                || not (validOid p.FrozenHead) || p.ProjectVersion < 0 || p.SourceHighWater < 0L))
           || not (sha256.IsMatch(defaultArg (Option.ofObj receipt.ParticipantRosterSha256) ""))
           || not (sha256.IsMatch(defaultArg (Option.ofObj receipt.ReviewedReportSha256) ""))
           || isNull receipt.OriginalSourceCheckpoints
           || receipt.OriginalSourceCheckpoints.Length <> 2
           || (receipt.OriginalSourceCheckpoints |> Array.exists (fun checkpoint ->
               isNull (box checkpoint) || String.IsNullOrWhiteSpace checkpoint.ProjectId
               || not (validOid checkpoint.FrozenProjectHead)
               || not (validOriginalJournalId checkpoint.OriginalJournalId)
               || checkpoint.OriginalJournalHighWater < 0L
               || checkpoint.OriginalSourceEffectHighWater < 0L))
           || ((receipt.OriginalSourceCheckpoints |> Array.map (fun checkpoint ->
               checkpoint.ProjectId, checkpoint.FrozenProjectHead) |> Set.ofArray) <>
               (receipt.ParticipantRoster |> Array.map (fun participant ->
                   participant.ProjectId, participant.FrozenHead) |> Set.ofArray))
           || not (sha256.IsMatch(defaultArg (Option.ofObj receipt.OriginalSourceCoverageSha256) ""))
           || not (validWitness receipt.OriginalSourceCoverageWitness)
           || not (sha256.IsMatch(defaultArg (Option.ofObj receipt.StatementSha256) ""))
           || not (validWitness receipt.ClientProtocolWitness)
           || not (validWitness receipt.OldWriterFenceWitness)
           || not (validWitness receipt.OperatorApprovalWitness)
           || receipt.AccountStatus <> "AccountPreparing"
           || receipt.ProjectStatus <> "ProjectPreparedNotEstablished" then
            Error(InvalidAccountBinding "An exact source-bound Preparing receipt is required")
        else
            transact b operationId receipt.Actor "AccountMigrationPreparing"
                (json receipt) (Some receipt.PreviousAccountHead) (fun state ->
                    if state.StateVersion <> receipt.PreviousStateVersion then
                        Error(AccountStateVersionChanged state.StateVersion)
                    elif state.MigrationPhase <> "Frozen" then
                        Error(AccountMigrationNotFrozen state.MigrationPhase)
                    elif occupied state <> 0 then
                        Error(AccountMigrationOutstandingSlots(occupied state))
                    else
                        Ok({ state with MigrationPhase = "Preparing" }, json receipt))

    let reserve (b: AccountBinding) operationId actor launchId (project: ProjectLedgerAuthority) roleId roleConfigVersion =
        if isNull (box project) || launchId = Guid.Empty
           || project.ProjectId = Guid.Empty || project.LedgerId = Guid.Empty
           || project.Epoch < 1 || String.IsNullOrWhiteSpace project.ApprovedFetchUrl
           || String.IsNullOrWhiteSpace project.ApprovedPushUrl
           || String.IsNullOrWhiteSpace roleId || roleConfigVersion < 1L then
            Error(InvalidAccountBinding "Launch, trusted project ledger authority and versioned role are required")
        else
            let authority = projectAuthoritySha256 project
            let request =
                json {| launchId = string launchId; projectId = string project.ProjectId
                        projectLedgerId = string project.LedgerId; projectLedgerEpoch = project.Epoch
                        projectAuthoritySha256 = authority
                        roleId = roleId; roleConfigVersion = roleConfigVersion
                        admissionEpoch = b.Epoch |}
            transact b operationId actor "ReserveGlobalAgentSlot" request None (fun state ->
                if state.MigrationPhase <> "LegacyActive" && state.MigrationPhase <> "Activated" then
                    Error(AccountMigrationWritesFenced state.MigrationPhase)
                elif state.Tokens |> Array.exists (fun t -> t.LaunchId = string launchId) then
                    Error AccountLaunchExists
                elif occupied state >= state.MaxConcurrentAgents then
                    Error(AccountCapacityFull(occupied state, state.MaxConcurrentAgents))
                else
                    let token =
                        { LaunchId = string launchId; ProjectId = string project.ProjectId
                          ProjectLedgerId = string project.LedgerId
                          ProjectLedgerEpoch = project.Epoch
                          ProjectAuthoritySha256 = authority
                          RoleId = roleId; RoleConfigVersion = roleConfigVersion
                          AdmissionOperationId = string operationId; Status = "Held"
                          ReleaseProof = Unchecked.defaultof<AccountTerminalProof> }
                    Ok({ state with Tokens = Array.append state.Tokens [| token |] },
                       json {| launchId = string launchId; status = "Held"
                               occupied = occupied state + 1; limit = state.MaxConcurrentAgents |}))

    let setCapacity (b: AccountBinding) operationId actor expectedConfigVersion newLimit =
        if newLimit < 1 || expectedConfigVersion < 1L then
            Error(InvalidAccountBinding "Positive capacity and expected config version required")
        else
            let request = json {| expectedConfigVersion = expectedConfigVersion; newLimit = newLimit |}
            transact b operationId actor "SetGlobalAgentCapacity" request None (fun state ->
                if state.MigrationPhase <> "LegacyActive" && state.MigrationPhase <> "Activated" then
                    Error(AccountMigrationWritesFenced state.MigrationPhase)
                elif state.ConfigVersion <> expectedConfigVersion then
                    Error(AccountConfigVersionChanged state.ConfigVersion)
                elif newLimit < occupied state then
                    Error(AccountResizeBelowOccupancy(occupied state))
                else
                    Ok({ state with ConfigVersion = state.ConfigVersion + 1L
                                    MaxConcurrentAgents = newLimit },
                       json {| configVersion = state.ConfigVersion + 1L
                               limit = newLimit; occupied = occupied state |}))

    let release (b: AccountBinding) (verifier: IProjectTerminalReceiptVerifier)
                operationId actor (locator: ProjectTerminalLocator) =
        if isNull (box verifier) || locator.LaunchId = Guid.Empty
           || locator.ProjectId = Guid.Empty
           || locator.AdmissionOperationId = Guid.Empty
           || locator.TerminalOperationId = Guid.Empty then
            Error(InvalidAccountBinding "Release needs a proof verifier and exact project/admission/terminal IDs")
        else
            let request =
                json {| launchId = string locator.LaunchId; projectId = string locator.ProjectId
                        admissionOperationId = string locator.AdmissionOperationId
                        terminalOperationId = string locator.TerminalOperationId |}
            transact b operationId actor "ReleaseGlobalAgentSlot" request None (fun state ->
                match state.Tokens |> Array.tryFind (fun t -> t.LaunchId = string locator.LaunchId) with
                | None -> Error AccountLaunchMissing
                | Some token when token.Status = "Released" -> Error AccountLaunchAlreadyReleased
                | Some token when token.ProjectId <> string locator.ProjectId
                                  || token.AdmissionOperationId <> string locator.AdmissionOperationId ->
                    Error(AccountTerminalProofRejected "Token owner or original admission does not match")
                | Some token ->
                    match verifier.VerifyCurrentTerminal locator with
                    | Error message -> Error(AccountTerminalProofRejected message)
                    | Ok proof when isNull (box proof) ||
                                    proof.ProjectId <> token.ProjectId ||
                                    proof.LaunchId <> token.LaunchId ||
                                    proof.AdmissionOperationId <> token.AdmissionOperationId ||
                                    proof.TerminalOperationId <> string locator.TerminalOperationId ||
                                    proof.ProjectLedgerId <> token.ProjectLedgerId ||
                                    proof.ProjectLedgerEpoch <> token.ProjectLedgerEpoch ||
                                    proof.ProjectAuthoritySha256 <> token.ProjectAuthoritySha256 ||
                                    proof.LaunchGeneration < 1L ||
                                    not (validOid proof.ProjectHead) ||
                                    not (validOid proof.TerminalBlobOid) ||
                                    (proof.TerminalKind <> "LaunchTerminal" &&
                                     proof.TerminalKind <> "AbortOnlyUnbound") ->
                        Error(AccountTerminalProofRejected "Verified project terminal identity, head or kind mismatch")
                    | Ok proof ->
                        let tokens =
                            state.Tokens |> Array.map (fun t ->
                                if t.LaunchId = token.LaunchId then
                                    { t with Status = "Released"; ReleaseProof = proof }
                                else t)
                        Ok({ state with Tokens = tokens },
                           json {| launchId = token.LaunchId; status = "Released"
                                   terminalOperationId = proof.TerminalOperationId
                                   occupied = occupied state - 1 |}))
