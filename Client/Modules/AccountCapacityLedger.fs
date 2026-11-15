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

[<CLIMutable>]
type AccountTerminalProof =
    { ProjectId: string
      LaunchId: string
      AdmissionOperationId: string
      ProjectLedgerId: string
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
                  ConfigureStart = None }
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
                       || t.Status <> "Held" && t.Status <> "Released"
                       || (t.Status = "Held" && not (isNull (box t.ReleaseProof)))
                       || (t.Status = "Released" && isNull (box t.ReleaseProof)))
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
        let index = Path.Combine(Path.GetTempPath(), $"jarvis-account-{Guid.NewGuid():N}.index")
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

    let private transact (b: AccountBinding) operationId actor kind requestJson
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
                                plan snapshot.State |> Result.bind (fun (updated, resultJson) ->
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

    let reserve (b: AccountBinding) operationId actor launchId projectId roleId roleConfigVersion =
        if launchId = Guid.Empty || projectId = Guid.Empty
           || String.IsNullOrWhiteSpace roleId || roleConfigVersion < 1L then
            Error(InvalidAccountBinding "Launch, project and versioned role are required")
        else
            let request =
                json {| launchId = string launchId; projectId = string projectId
                        roleId = roleId; roleConfigVersion = roleConfigVersion
                        admissionEpoch = b.Epoch |}
            transact b operationId actor "ReserveGlobalAgentSlot" request (fun state ->
                if state.Tokens |> Array.exists (fun t -> t.LaunchId = string launchId) then
                    Error AccountLaunchExists
                elif occupied state >= state.MaxConcurrentAgents then
                    Error(AccountCapacityFull(occupied state, state.MaxConcurrentAgents))
                else
                    let token =
                        { LaunchId = string launchId; ProjectId = string projectId
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
            transact b operationId actor "SetGlobalAgentCapacity" request (fun state ->
                if state.ConfigVersion <> expectedConfigVersion then
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
            transact b operationId actor "ReleaseGlobalAgentSlot" request (fun state ->
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
                                    String.IsNullOrWhiteSpace proof.ProjectLedgerId ||
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
