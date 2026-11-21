module Client.AccountCapacityLedger

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Client.OriginalGitWriterAdmissionJournal
open Client.NativeSchedulerRosterPolicy
open Client.LedgerStore

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
    | AccountPolicyNotAuthoritative of string

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

    // A competing remote head alone is not proof this push did not happen.
    // Only Git's explicit porcelain non-fast-forward rejection permits replan.
    let private provenNonFastForwardRejection (output: GitOutput) =
        output.Code <> 0
        && output.Out.Contains("[rejected]", StringComparison.OrdinalIgnoreCase)
        && (output.Out.Contains("(fetch first)", StringComparison.OrdinalIgnoreCase)
            || output.Out.Contains("(non-fast-forward)", StringComparison.OrdinalIgnoreCase)
            || output.Out.Contains("(stale info)", StringComparison.OrdinalIgnoreCase))

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
                      [ // Local Git replace refs are never authenticated remote history.
                        "GIT_NO_REPLACE_OBJECTS", "1"
                        "GIT_TERMINAL_PROMPT", "0"
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
                                let publishAndConfirm () =
                                    // Empty expected remote ref: no overwrite, including an init race.
                                    git b [ "push"; "--porcelain"; $"--force-with-lease={AccountRef}:";
                                            b.RemoteName; $"{proposed}:{AccountRef}" ] |> ignore
                                    // Re-read even on a lost acknowledgment; a competing initializer
                                    // with different identity will fail pinned-identity validation.
                                    match fetch b with
                                    | Ok snapshot -> Ok snapshot
                                    | Error(AccountIntegrityError message) -> Error(AccountIntegrityError message)
                                    | Error(InvalidAccountBinding message) -> Error(InvalidAccountBinding message)
                                    | Error _ -> Error(AccountOutcomeUnknown Guid.Empty)
                                OriginalGitWriterAdmissionJournal.withPublication
                                    (OriginalGitWriterAdmissionJournal.deterministicOperationId
                                        "AccountInitialize" AccountRef
                                        (b.AccountId.ToString("D") + ":" + b.LedgerId.ToString("D") + ":" + string initialCapacity))
                                    "AccountInitialize"
                                    (OriginalGitWriterAdmissionJournal.authoritySha256
                                        [ "account"; b.AccountId.ToString("D"); b.LedgerId.ToString("D")
                                          string b.Epoch; b.ApprovedFetchUrl; b.ApprovedPushUrl ])
                                    AccountRef proposed publishAndConfirm
                                    (fun snapshot ->
                                        match isAncestor b proposed snapshot.Head with
                                        | Ok true -> true
                                        | _ -> false)
                                    (fun _ -> AccountOutcomeUnknown Guid.Empty)))))

    let private transactFiles (b: AccountBinding) operationId actor kind requestJson expectedHead
                         (plan: AccountSnapshot -> Result<AccountCapacityState * string * (string * string) list, AccountFailure>) =
        if operationId = Guid.Empty || String.IsNullOrWhiteSpace actor
           || String.IsNullOrWhiteSpace kind then
            Error(InvalidAccountBinding "Stable operation ID, actor and kind required")
        else
            try
                let request = canonical requestJson
                let fullRequest =
                    canonical (json {| schemaVersion = 1; accountId = b.AccountId.ToString("D"); actor = actor; kind = kind; request = request |})
                let requestHash = hash fullRequest
                let mutable journalEnabled = false
                let mutable unresolvedRemoteEffect = false
                let rec attempt retry admitted =
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
                                 | _ -> plan snapshot)
                                |> Result.bind (fun (updated, resultJson, extraFiles) ->
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
                                        ([ "state.json", json next; receiptPath operationId, json receipt ]
                                         @ extraFiles)
                                    |> Result.bind (fun tree ->
                                        commitTree b tree (Some snapshot.Head)
                                            $"Account {kind}\n\nOperation-Id: {operationId:D}\nRequest-SHA256: {requestHash}"
                                        |> Result.bind (fun proposed ->
                                            // A lease requires *this* observed parent: stale writers cannot
                                            // push an incompatible capacity calculation.
                                            let hooks = gitInterposition.Value
                                            if not (isNull (box hooks)) then
                                                hooks.BeforeLeasePush operationId snapshot.Head
                                            // A single durable permit spans only CAS retries for which
                                            // Git independently proved the proposed push was rejected.
                                            // A missing/late acknowledgment never authorizes reentry.
                                            let publishAndConfirm () =
                                                unresolvedRemoteEffect <- true
                                                let pushed =
                                                    git b [ "push"; "--porcelain";
                                                            $"--force-with-lease={AccountRef}:{snapshot.Head}";
                                                            b.RemoteName; $"{proposed}:{AccountRef}" ]
                                                match fetch b with
                                                | Error _ -> Error(AccountOutcomeUnknown operationId)
                                                | Ok current ->
                                                    match readReceipt b current.Head operationId with
                                                    | Error error -> Error error
                                                    | Ok(Some observed) when observed.CanonicalRequestJson <> fullRequest
                                                                           || observed.RequestSha256 <> requestHash ->
                                                        Error AccountOperationIdReused
                                                    | Ok(Some observed) ->
                                                        isAncestor b proposed current.Head
                                                        |> Result.map (fun ourCommit ->
                                                            unresolvedRemoteEffect <- false
                                                            { Receipt = observed; CurrentHead = current.Head
                                                              AlreadyApplied = not ourCommit })
                                                    | Ok None when journalEnabled && current.Head <> snapshot.Head
                                                                   && (match pushed with
                                                                       | Ok output -> provenNonFastForwardRejection output
                                                                       | Error _ -> false) ->
                                                        unresolvedRemoteEffect <- false
                                                        if retry >= 8 then Error AccountContended
                                                        else attempt (retry + 1) true
                                                    | Ok None when not journalEnabled && current.Head <> snapshot.Head
                                                                    && retry < 8 ->
                                                        // Exact preactivation behavior: bounded replan
                                                        // even when the earlier transport was uncertain.
                                                        attempt (retry + 1) true
                                                    | Ok None when not journalEnabled && retry >= 8 ->
                                                        Error AccountContended
                                                    | Ok None ->
                                                        // A changed head or failed remote read is never
                                                        // independent proof that our push did not publish.
                                                        Error(AccountOutcomeUnknown operationId)
                                            if admitted then publishAndConfirm()
                                            else
                                                let publication enabled =
                                                    journalEnabled <- enabled
                                                    match publishAndConfirm () with
                                                    | Ok applied -> Ok(Choice1Of2 applied)
                                                    | Error error when not unresolvedRemoteEffect ->
                                                        Ok(Choice2Of2 error)
                                                    | Error error -> Error error
                                                match OriginalGitWriterAdmissionJournal.withPublicationClassified
                                                    operationId kind
                                                    (OriginalGitWriterAdmissionJournal.authoritySha256
                                                        [ "account"; b.AccountId.ToString("D"); b.LedgerId.ToString("D")
                                                          string b.Epoch; b.ApprovedFetchUrl; b.ApprovedPushUrl ])
                                                    AccountRef requestHash publication
                                                    (function Choice1Of2 _ -> true | _ -> false)
                                                    (function Choice2Of2 _ -> true | _ -> false)
                                                    InvalidAccountBinding with
                                                | Ok(Choice1Of2 applied) -> Ok applied
                                                | Ok(Choice2Of2 error) -> Error error
                                                | Error error -> Error error)))))
                attempt 0 false
            with :? ArgumentException as ex ->
                Error(InvalidAccountBinding $"Invalid operation payload: {ex.Message}")
               | :? JsonException as ex ->
                Error(InvalidAccountBinding $"Invalid operation JSON: {ex.Message}")

    // Original WQ07 callers retain exactly their state v1 schema, transaction
    // semantics, receipts and publication journal. Native policy adds only a
    // namespaced file in the SAME account-head CAS.
    let private transact b operationId actor kind requestJson expectedHead plan =
        transactFiles b operationId actor kind requestJson expectedHead (fun snapshot ->
            plan snapshot.State |> Result.map (fun (updated, result) -> updated, result, []))

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
                && not (File.Exists(Path.Combine(b.RepositoryPath, ".git", "info", "grafts")))
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


    // Native Scheduler *policy only*. There are deliberately no production
    // approval trust anchors, writer entrypoints, reservations or launch paths.
    // The internal signer port is restricted to independently signed disposable
    // account/project bare-Git fixtures. A real authenticated host must supply
    // its own operator-owned root/subject/epoch/roster proof before activation.
    let private nativeRootMatches (b: AccountBinding) (approval: NativeSignedRosterApproval)
                                  expectedSubject =
        let p = approval.Policy
        p.AccountId = b.AccountId.ToString("D")
        && p.AccountLedgerId = b.LedgerId.ToString("D")
        && p.AccountEpoch = b.Epoch
        && p.AuthenticatedSubject = expectedSubject
        && p.AccountAuthoritySha256 =
            NativeSchedulerRosterPolicy.accountAuthoritySha256
                p.AccountId p.AccountLedgerId p.AccountEpoch
                b.ApprovedFetchUrl b.ApprovedPushUrl

    let private readNativeApproval b head =
        let path = NativeSchedulerRosterPolicy.PolicyPath
        command b [ "ls-tree"; head; "--"; path ]
        |> Result.bind (fun listing ->
            if String.IsNullOrEmpty listing then Ok None
            elif not (Regex.IsMatch(listing,
                      "^100644 blob [0-9a-f]{40}([0-9a-f]{24})?\\t" + Regex.Escape(path) + "$")) then
                Error(AccountIntegrityError "Native policy path is not a regular Git blob.")
            else
                command b [ "show"; $"{head}:{path}" ]
                |> Result.bind (fun raw ->
                    try
                        let approved =
                            JsonSerializer.Deserialize<NativeSignedRosterApproval>(raw, options)
                        if isNull (box approved) then
                            Error(AccountIntegrityError "Missing native approval document.")
                        else Ok(Some approved)
                    with _ ->
                        Error(AccountIntegrityError "Unreadable native roster approval document.")))

    // The only source of a native policy change is its WQ07-style immutable
    // account operation, published at that same account ref and same CAS head.
    // A later forged path rewrite or rollback cannot inherit an old signature.
    let private verifyNativePolicyOrigin (b: AccountBinding) snapshot
                                         (approval: NativeSignedRosterApproval) =
        let opId = Guid.Parse approval.OperationId
        let expectedRequest =
            canonical (json {| schemaVersion = 1; accountId = b.AccountId.ToString("D")
                               actor = approval.Actor; kind = "SetNativeSchedulerRosterPolicy"
                               request = canonical (json approval) |})
        let expectedResult =
            canonical (json {| version = approval.Policy.Version
                               previousVersion = approval.PreviousVersion
                               policySha256 = hash (json approval) |})
        readReceipt b snapshot.Head opId
        |> Result.bind (function
            | Some receipt when receipt.Kind = "SetNativeSchedulerRosterPolicy"
                                && receipt.Actor = approval.Actor
                                && receipt.CanonicalRequestJson = expectedRequest
                                && receipt.RequestSha256 = hash expectedRequest
                                && receipt.OriginalResultJson = expectedResult
                                && receipt.StateVersion <= snapshot.State.StateVersion ->
                command b [ "log"; "--first-parent"; "--diff-filter=A"; "--format=%H";
                            snapshot.Head; "--"; receiptPath opId ]
                |> Result.bind (fun origin ->
                    command b [ "log"; "--first-parent"; "-1"; "--format=%H";
                                snapshot.Head; "--"; NativeSchedulerRosterPolicy.PolicyPath ]
                    |> Result.bind (fun latestChange ->
                        if not (validOid origin) || origin <> latestChange then
                            Error(AccountIntegrityError
                                "Native policy was rewritten independently of its immutable CAS receipt.")
                        else
                            // A valid operator signature for head A must never
                            // be transplanted by a Git-only actor onto parent D.
                            // Git commit messages/receipts are not operator signatures.
                            command b [ "rev-parse"; $"{origin}^" ]
                            |> Result.bind (fun parent ->
                                if parent <> approval.ExpectedAccountHead then
                                    Error(AccountIntegrityError
                                        "Native signed approval was transplanted onto an unauthorized predecessor.")
                                else
                                    command b [ "diff-tree"; "--no-commit-id"; "--name-only"; "-r"; origin ]
                                    |> Result.bind (fun changed ->
                                        let paths =
                                            changed.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                        let expected =
                                            [| "state.json"; receiptPath opId
                                               NativeSchedulerRosterPolicy.PolicyPath |]
                                        if paths.Length <> expected.Length
                                           || Set.ofArray paths <> Set.ofArray expected then
                                            Error(AccountIntegrityError
                                                "Native account policy CAS commit modified unexpected paths.")
                                        else
                                            readState b parent
                                            |> Result.bind (fun before ->
                                                readState b origin
                                                |> Result.bind (fun after ->
                                                    if receipt.Sequence <> before.NextSequence
                                                       || receipt.StateVersion <> after.StateVersion
                                                       || after.StateVersion <> before.StateVersion + 1L
                                                       || after.NextSequence <> before.NextSequence + 1L
                                                       || after.ConfigVersion <> before.ConfigVersion
                                                       || after.MaxConcurrentAgents <> before.MaxConcurrentAgents
                                                       || after.MigrationPhase <> before.MigrationPhase
                                                       || after.Tokens <> before.Tokens then
                                                        Error(AccountIntegrityError
                                                            "Native policy CAS changed WQ07 state beyond its sequence increment.")
                                                    else Ok ()))))))
            | _ ->
                Error(AccountIntegrityError
                    "Native policy has no matching immutable account-head operation and payload."))

    // A clone-local replace ref, graft or alternate cannot authenticate a
    // project ledger header. Check the exact approved remote in an empty private
    // bare repository, not through LedgerStore's shared project-clone reader.
    let private verifiedNativeProjectRemoteHead (b: LedgerBinding) =
        try
            let scratch = Directory.CreateTempSubdirectory("jarvis-native-project-proof-").FullName
            try
                let repo = Path.Combine(scratch, "verified.git")
                let emptyGlobal = Path.Combine(scratch, "empty-gitconfig")
                let emptyTemplate = Path.Combine(scratch, "empty-template")
                File.WriteAllText(emptyGlobal, "")
                Directory.CreateDirectory(emptyTemplate) |> ignore
                let git args =
                    BoundedProcess.run
                        { Executable = "git"
                          Args = args
                          WorkingDirectory = scratch
                          Timeout = TimeSpan.FromSeconds 30.
                          MaxOutputBytes = Some (256 * 1024)
                          StandardInput = BoundedProcess.ClosedInput
                          ExtraEnvironment =
                            [ "GIT_NO_REPLACE_OBJECTS", "1"
                              "GIT_CONFIG_NOSYSTEM", "1"
                              "GIT_CONFIG_GLOBAL", emptyGlobal
                              "GIT_TEMPLATE_DIR", emptyTemplate
                              "GIT_TERMINAL_PROMPT", "0" ]
                          ConfigureStart = Some(fun start ->
                              // This includes GIT_CONFIG_PARAMETERS/COUNT/KEY_n,
                              // GIT_DIR, GIT_OBJECT_DIRECTORY and alternates.
                              let inherited =
                                  start.Environment.Keys
                                  |> Seq.cast<string>
                                  |> Seq.filter (fun key ->
                                      key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                                  |> Seq.toArray
                              for key in inherited do
                                  start.Environment.Remove(key) |> ignore) }
                    |> function
                        | Ok output when output.ExitCode = 0 && not output.TimedOut ->
                            output.StdOut.Trim()
                        | _ -> invalidOp "Isolated project Git proof command failed."
                let gitRepo args = git (("--git-dir=" + repo) :: args)
                if String.IsNullOrWhiteSpace b.ApprovedFetchUrl
                   || b.ApprovedFetchUrl.StartsWith("-", StringComparison.Ordinal) then
                    invalidOp "No unambiguous approved project fetch authority."
                git [ "init"; "--bare"; "--initial-branch=main"; repo ] |> ignore
                let cleanMetadata () =
                    not ([ "info/grafts"; "objects/info/alternates"
                           "objects/info/http-alternates"; "shallow" ]
                         |> List.exists (fun path -> File.Exists(Path.Combine(repo, path))))
                    && not (Directory.Exists(Path.Combine(repo, "refs", "replace")))
                if not (cleanMetadata ()) then
                    invalidOp "Project proof repository has local history redirects."
                let advertisedHead () =
                    let record =
                        gitRepo [ "ls-remote"; "--refs"; "--exit-code"
                                  b.ApprovedFetchUrl
                                  NativeSchedulerRosterPolicy.ProjectSourceRef ]
                    let fields = record.Split('\t')
                    if fields.Length <> 2 || not (validOid fields[0])
                       || fields[1] <> NativeSchedulerRosterPolicy.ProjectSourceRef then
                        invalidOp "Approved remote does not advertise one exact project ref."
                    fields[0]
                let before = advertisedHead ()
                gitRepo [ "fetch"; "--no-tags"; "--no-write-fetch-head"; "--refmap="
                          b.ApprovedFetchUrl
                          NativeSchedulerRosterPolicy.ProjectSourceRef + ":refs/heads/native-roster-proof" ]
                |> ignore
                let head = gitRepo [ "rev-parse"; "--verify"; "refs/heads/native-roster-proof^{commit}" ]
                if not (validOid head) || head <> before || not (cleanMetadata ()) then
                    invalidOp "Fetched project head or local Git metadata changed."
                // Only a raw blob fetched from the real approved ref is proof.
                let rawHeader = gitRepo [ "show"; head + ":ledger.json" ]
                let header = JsonSerializer.Deserialize<LedgerHeader>(rawHeader, options)
                if isNull (box header) || header.SchemaVersion <> 2
                   || header.ProjectId <> b.ProjectId.ToString("D")
                   || header.LedgerId <> b.LedgerId.ToString("D")
                   || header.Epoch <> b.Epoch || header.NextSequence < 1L
                   || header.Status <> "active" || isNull (box header.Settings)
                   || advertisedHead () <> head || not (cleanMetadata ()) then
                    invalidOp "Approved remote has a foreign or changed ledger header."
                Ok head
            finally
                try Directory.Delete(scratch, true) with _ -> ()
        with _ ->
            Error(AccountPolicyNotAuthoritative
                "Exact project remote/ref/ledger identity cannot be independently authenticated.")

    let private verifyNativeProjects (policy: NativeRosterPolicy)
                                     (bindings: LedgerBinding array) =
        if isNull bindings || bindings.Length <> policy.Projects.Length
           || bindings |> Array.exists (fun b -> isNull (box b))
           || (bindings |> Array.map _.ProjectId |> Array.distinct |> Array.length) <> bindings.Length then
            Error(AccountPolicyNotAuthoritative
                "Trusted complete project bindings are absent, duplicate or only a subset.")
        else
            policy.Projects
            |> Array.fold (fun state project ->
                state |> Result.bind (fun heads ->
                    match bindings |> Array.tryFind (fun b -> b.ProjectId.ToString("D") = project.ProjectId) with
                    | None -> Error(AccountPolicyNotAuthoritative "Unrecognized/foreign approved project.")
                    | Some b ->
                        let authority =
                            { ProjectId = b.ProjectId; LedgerId = b.LedgerId; Epoch = b.Epoch
                              ApprovedFetchUrl = b.ApprovedFetchUrl; ApprovedPushUrl = b.ApprovedPushUrl }
                        if project.LedgerId <> b.LedgerId.ToString("D")
                           || project.Epoch <> b.Epoch
                           || project.SourceRef <> NativeSchedulerRosterPolicy.ProjectSourceRef
                           || project.RemoteAuthoritySha256 <> projectAuthoritySha256 authority then
                            Error(AccountPolicyNotAuthoritative
                                "Project source, ledger epoch or remote mapping does not match its approved identity.")
                        else
                            // Keep the existing project trusted-cache semantics, but
                            // do not let its clone-local Git metadata prove identity.
                            match verifiedNativeProjectRemoteHead b with
                            | Error error -> Error error
                            | Ok genuineHead ->
                                match LedgerStore.fetch b with
                                | Ok snapshot when snapshot.Commit = genuineHead
                                                   && snapshot.Header.ProjectId = project.ProjectId
                                                   && snapshot.Header.LedgerId = project.LedgerId
                                                   && snapshot.Header.Epoch = project.Epoch ->
                                    Ok(Array.append heads
                                        [| { ProjectId = project.ProjectId; LedgerId = project.LedgerId
                                             Epoch = project.Epoch; VerifiedHead = genuineHead } |])
                                | _ -> Error(AccountPolicyNotAuthoritative
                                           "Project local trust cache disagrees with authenticated remote head.")))
                (Ok [||])

    let private verifiedNativeAt b operatorKey expectedSubject bindings snapshot =
        readNativeApproval b snapshot.Head
        |> Result.bind (function
            | None -> Error(AccountPolicyNotAuthoritative "Complete signed account project roster is absent.")
            | Some approval ->
                NativeSchedulerRosterPolicy.verifySignedApproval operatorKey approval
                |> Result.mapError AccountPolicyNotAuthoritative
                |> Result.bind (fun () ->
                    if not (nativeRootMatches b approval expectedSubject) then
                        Error(AccountPolicyNotAuthoritative
                            "Authenticated subject/account/ledger/epoch/remote approval differs.")
                    elif snapshot.State.MigrationPhase <> "LegacyActive" then
                        Error(AccountPolicyNotAuthoritative
                            "WQ07 migration fence is not in the inert LegacyActive phase.")
                    else
                        verifyNativePolicyOrigin b snapshot approval
                        |> Result.bind (fun () ->
                            verifyNativeProjects approval.Policy bindings
                            |> Result.map (fun heads -> approval, heads))))

    // Host approval is intentionally DISABLED. No signed fixture capability is
    // exposed through this public entrypoint and neither a caller-authored
    // subject, "approved" flag, nor dashboard membership can turn it on.
    let setNativeRosterPolicy (_: AccountBinding) (_: NativeSignedRosterApproval) =
        Error(AccountPolicyNotAuthoritative
            "Production host OAuth subject/operator approval trust registry is not provisioned; writer disabled.")

    let readNativeRosterPolicy (b: AccountBinding) =
        match fetch b with
        | Error failure ->
            NativeSchedulerRosterPolicy.hold
                $"No verified account head: {failure}" None
        | Ok snapshot ->
            NativeSchedulerRosterPolicy.hold
                "Production host OAuth subject/operator approval trust registry is not provisioned; signed roster cannot be authorized."
                (Some snapshot.Head)

    // Test-only authenticated route. The account sink cannot be a production
    // checkout or remote: it must satisfy WQ10's private, marked /tmp bare-Git
    // topology with no symlink/alternates/credential redirect. The disposable
    // operator key signs the exact host subject, account, *complete* membership,
    // version, predecessor head and operation UUID. All projects are reread
    // against their independently pinned Git remotes before any account write.
    let internal setVerifiedDisposableNativeRosterPolicy disposableRoot
                         (b: AccountBinding) (operatorKey: byte array)
                         expectedSubject (projects: LedgerBinding array)
                         (approval: NativeSignedRosterApproval) =
        if not (approvedDisposableAccountSink disposableRoot b) then
            Error(AccountPolicyNotAuthoritative
                "Native policy test writer requires an isolated, marked, private disposable account authority.")
        else
            NativeSchedulerRosterPolicy.verifySignedApproval operatorKey approval
            |> Result.mapError AccountPolicyNotAuthoritative
            |> Result.bind (fun () ->
                if not (nativeRootMatches b approval expectedSubject) then
                    Error(AccountPolicyNotAuthoritative
                        "Signed approval does not bind authenticated subject and account authority.")
                else
                    verifyNativeProjects approval.Policy projects
                    |> Result.bind (fun _ ->
                        let opId = Guid.Parse approval.OperationId
                        transactFiles b opId approval.Actor "SetNativeSchedulerRosterPolicy"
                            (json approval) (Some approval.ExpectedAccountHead) (fun snapshot ->
                                if snapshot.State.MigrationPhase <> "LegacyActive" then
                                    Error(AccountMigrationWritesFenced snapshot.State.MigrationPhase)
                                else
                                    readNativeApproval b snapshot.Head
                                    |> Result.bind (fun previous ->
                                        let previousVersion =
                                            match previous with
                                            | None -> Ok 0L
                                            | Some old ->
                                                NativeSchedulerRosterPolicy.verifySignedApproval operatorKey old
                                                |> Result.mapError AccountPolicyNotAuthoritative
                                                |> Result.bind (fun () ->
                                                    if not (nativeRootMatches b old expectedSubject) then
                                                        Error(AccountPolicyNotAuthoritative
                                                            "Existing policy is signed for a different root.")
                                                    else
                                                        verifyNativePolicyOrigin b snapshot old
                                                        |> Result.map (fun () -> old.Policy.Version))
                                        previousVersion
                                        |> Result.bind (fun currentVersion ->
                                            if currentVersion <> approval.PreviousVersion then
                                                Error(AccountPolicyNotAuthoritative
                                                    "Signed predecessor policy version differs from account head.")
                                            else
                                                Ok(snapshot.State,
                                                   json {| version = approval.Policy.Version
                                                           previousVersion = approval.PreviousVersion
                                                           policySha256 = hash (json approval) |},
                                                   [ NativeSchedulerRosterPolicy.PolicyPath, json approval ]))))))

    let internal readVerifiedDisposableNativeRosterPolicy disposableRoot (b: AccountBinding)
                         (operatorKey: byte array) expectedSubject (projects: LedgerBinding array) =
        if not (approvedDisposableAccountSink disposableRoot b) then
            NativeSchedulerRosterPolicy.hold
                "Account is not an independently marked disposable approval authority." None
        else
            match fetch b with
            | Error failure ->
                NativeSchedulerRosterPolicy.hold $"Account head unverified: {failure}" None
            | Ok snapshot ->
                match verifiedNativeAt b operatorKey expectedSubject projects snapshot with
                | Ok (approved, heads) ->
                    // The account may have moved while separate project
                    // remotes were checked. Never present such a stale head
                    // as an authoritative policy projection.
                    match fetch b with
                    | Ok refreshed when refreshed.Head = snapshot.Head ->
                        NativeSchedulerRosterPolicy.verified snapshot.Head heads approved.Policy
                    | Ok _ ->
                        NativeSchedulerRosterPolicy.hold
                            "Account head changed during cross-project policy verification." (Some snapshot.Head)
                    | Error error ->
                        NativeSchedulerRosterPolicy.hold
                            $"Unable to confirm account head after project verification: {error}"
                            (Some snapshot.Head)
                | Error failure ->
                    NativeSchedulerRosterPolicy.hold $"{failure}" (Some snapshot.Head)
