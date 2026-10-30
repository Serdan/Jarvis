module Client.LedgerStore

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// Canonical Git ledger transport. Every caller requires an explicitly verified local binding.
type LedgerFailure =
    | InvalidBinding of string
    | RemoteUnavailable of string
    | LedgerIntegrityError of string
    | LedgerHistoryUnavailable of string
    | StaleEntityVersion of string
    | OperationIdReused
    | OutcomeUnknown of string
    | LedgerContended
    | GitFailure of string
    | SourceUnavailable of string
    | AgentBusy of string
    | ReviewAccessDenied of string
    | ReviewAlreadyClaimed of string
    | ReviewVersionConflict of string
    | SourceReviewRefUnavailable of string

[<CLIMutable>]
type SourceRepositoryIdentity =
    { RepositoryId: string
      RemoteName: string
      FetchUrl: string
      PushUrl: string }

[<CLIMutable>]
type SourceWorktreeObservation =
    { RepositoryId: string
      SourceRef: string
      SourceHead: string
      SelectedHead: string
      State: string
      WorktreePresent: bool
      SelectedWorktreeDetached: bool
      SelectedWorktreeDirty: bool }

[<CLIMutable>]
type SourceSpecFile =
    { RepositoryId: string
      SourceRef: string
      CommitOid: string
      RelativePath: string
      BlobOid: string
      Html: string }

type LedgerBinding =
    { RepositoryPath: string
      RemoteName: string
      ApprovedFetchUrl: string
      ApprovedPushUrl: string
      ProjectId: Guid
      LedgerId: Guid
      Epoch: int }

[<CLIMutable>]
type LedgerSettings =
    { MaxWorkingPerAgent: int
      AllowDirectSourcePush: bool }

[<CLIMutable>]
type LedgerHeader =
    { SchemaVersion: int
      ProjectId: string
      LedgerId: string
      Epoch: int
      NextSequence: int64
      CreatedAt: string
      Status: string
      Settings: LedgerSettings }

[<CLIMutable>]
type LedgerOperation =
    { SchemaVersion: int
      CanonicalRequestJson: string
      OriginalResultJson: string
      OriginalResultHash: string
      OperationId: string
      RequestHash: string
      Sequence: int64
      Type: string
      Actor: string
      Path: string
      BlobOid: string
      Paths: string array }

type LedgerSnapshot =
    { Commit: string
      Header: LedgerHeader }

type LedgerWrite =
    { Operation: LedgerOperation
      CurrentHead: string
      AlreadyApplied: bool }

type LedgerFileChange =
    { Path: string
      ExpectedBlobOid: string option
      Content: string }

[<CLIMutable>]
type LedgerEntityEffect =
    { Path: string
      EntityKind: string
      ContentSha256: string
      EntityVersion: int option
      TaskId: string option
      AttemptId: string option
      RequestId: string option
      AgentId: string option
      ActionId: string option
      Lifecycle: string option
      State: string option
      Phase: string option
      Status: string option
      AssignmentGeneration: int option
      CheckpointSequence: int64 option
      CheckpointKind: string option
      CurrentReviewRevision: int option
      ClaimedReviewRevision: int option
      ReviewRevision: int option
      AcceptedReviewRevision: int option
      SpecBlobOid: string option
      HeadCommit: string option
      ReviewRef: string option
      VerifiedIntegrationSequence: int64 option }

[<CLIMutable>]
type LedgerRemotePolicy =
    { RemoteKind: string
      Visibility: string
      NonFastForwardRejection: string
      DeletionRejection: string
      SignedCommitRequirement: string
      Warnings: string array }

type LedgerIntent =
    { OperationId: Guid
      Actor: string
      Kind: string
      RequestJson: string }

type LedgerRecordChange =
    { OperationId: Guid
      Actor: string
      Path: string
      ExpectedBlobOid: string option
      Content: string }

module LedgerStore =
    let private ledgerRef = "refs/heads/jarvis-orchestration"
    // Full jitter prevents synchronized clients from repeatedly colliding
    // after an optimistic Git push loses a race.
    let private contentionDelay retries =
        let ceiling = min 800 (50 * (1 <<< min 6 retries))
        RandomNumberGenerator.GetInt32(max 1 (ceiling / 2), ceiling + 1)

    let private encoding = UTF8Encoding(false)
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
    let private oidPattern = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private recordPath = Regex("^records/[A-Za-z0-9_-]+(/[A-Za-z0-9_.-]+)*[.]json$", RegexOptions.Compiled)
    let private ledgerPath = Regex("^(tasks/[A-Za-z0-9_-]+/(task[.]json|spec[.]html|attempts/[a-f0-9]{32}([.]json|/checkpoints/[0-9]+[.]json|/events/[0-9]+[.]json|/assignments/[0-9]+([.]json|[.]ack[.]json))|integrations/[a-f0-9]{32}[.]json|imports/[0-9]+[.]json|lifecycle/[0-9]+[.]json|approvals/non-source/[0-9]+[.]json|orphan-cleanups/[a-f0-9]{32}/r[1-9][0-9]*[.]json|reviews/[a-f0-9]{32}/(request[.]json|notification[.]json|summary[.]md|findings/[a-z0-9][a-z0-9_-]{0,79}[.]md|revisions/[0-9]+[.]json|decisions/[0-9]+[.]json|events/[0-9]+[.]json))|agents/[A-Za-z0-9_-]+[.]json|records/[A-Za-z0-9_-]+(/[A-Za-z0-9_.-]+)*[.]json)$", RegexOptions.Compiled)
    let private remotePattern = Regex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled)

    type private GitOutput =
        { ExitCode: int
          Stdout: string
          Stderr: string }

    let private json value = JsonSerializer.Serialize(value, options)
    let private digest (value: string) =
        SHA256.HashData(encoding.GetBytes value)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    // Stable logical intent hashing must not depend on JSON member order or
    // whitespace. Duplicate (case-insensitive) members are rejected instead
    // of letting different consumers interpret them differently.
    let private canonicalRequestJson (raw: string) =
        use document = JsonDocument.Parse(raw)
        if document.RootElement.ValueKind <> JsonValueKind.Object then
            invalidArg "raw" "Ledger intent must be a JSON object."
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        let rec emit (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                let properties =
                    element.EnumerateObject()
                    |> Seq.sortWith (fun x y -> StringComparer.Ordinal.Compare(x.Name, y.Name))
                    |> Seq.toArray
                let names = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                writer.WriteStartObject()
                for property in properties do
                    if not (names.Add property.Name) then
                        invalidArg "raw" "Duplicate JSON member in ledger intent."
                    writer.WritePropertyName(property.Name)
                    emit property.Value
                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                for item in element.EnumerateArray() do emit item
                writer.WriteEndArray()
            | _ -> element.WriteTo(writer)
        emit document.RootElement
        writer.Flush()
        encoding.GetString(stream.ToArray())

    let private validRequestJson raw =
        try
            canonicalRequestJson raw |> ignore
            true
        with _ -> false

    let private execute (repo: string) (args: string list) (input: string option) (environment: (string * string) list) (preserveStdout: bool) =
        try
            use gitProcess = new Process()
            gitProcess.StartInfo.FileName <- "git"
            gitProcess.StartInfo.WorkingDirectory <- repo
            gitProcess.StartInfo.UseShellExecute <- false
            gitProcess.StartInfo.RedirectStandardOutput <- true
            gitProcess.StartInfo.RedirectStandardError <- true
            gitProcess.StartInfo.RedirectStandardInput <- true
            gitProcess.StartInfo.CreateNoWindow <- true
            gitProcess.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
            // A caller's Git credentials/configuration, not the task payload, select authentication.
            for (key, value) in environment do
                gitProcess.StartInfo.Environment[key] <- value
            args |> List.iter gitProcess.StartInfo.ArgumentList.Add
            if not (gitProcess.Start()) then
                Error(GitFailure "Cannot launch Git.")
            else
                let stdout = gitProcess.StandardOutput.ReadToEndAsync()
                let stderr = gitProcess.StandardError.ReadToEndAsync()
                match input with
                | Some value -> gitProcess.StandardInput.Write(value)
                | None -> ()
                gitProcess.StandardInput.Close()
                if not (gitProcess.WaitForExit(30000)) then
                    gitProcess.Kill(true)
                    Error(RemoteUnavailable "Git command timed out.")
                else
                    let value = stdout.GetAwaiter().GetResult()
                    Ok { ExitCode = gitProcess.ExitCode
                         Stdout = if preserveStdout then value else value.TrimEnd('\n', '\r')
                         Stderr = stderr.GetAwaiter().GetResult() }
        with ex ->
            Error(GitFailure ex.Message)

    let private git repo args = execute repo args None [] false
    let private gitRaw repo args = execute repo args None [] true
    let private gitInput repo args input = execute repo args (Some input) [] false
    let private gitIndex repo index args = execute repo args None [ "GIT_INDEX_FILE", index ] false

    let private requireSuccess label result =
        match result with
        | Error error -> Error error
        | Ok output when output.ExitCode = 0 -> Ok output.Stdout
        | Ok output -> Error(GitFailure $"{label}: {output.Stderr.Trim()}")

    let private run repo args = git repo args |> requireSuccess (String.concat " " args)

    let private embedsCredentials (raw: string) =
        let mutable uri = Unchecked.defaultof<Uri>
        if Uri.TryCreate(raw, UriKind.Absolute, &uri) then
            match uri.Scheme.ToLowerInvariant() with
            | "https" | "http" ->
                not (String.IsNullOrEmpty uri.UserInfo)
                || not (String.IsNullOrEmpty uri.Query)
                || not (String.IsNullOrEmpty uri.Fragment)
            | "ssh" | "git+ssh" ->
                uri.UserInfo.Contains(":")
                || not (String.IsNullOrEmpty uri.Query)
                || not (String.IsNullOrEmpty uri.Fragment)
            | _ -> false
        else false

    let private validate (binding: LedgerBinding) =
        if not (Directory.Exists binding.RepositoryPath) then
            Error(InvalidBinding "Repository path does not exist.")
        elif not (remotePattern.IsMatch binding.RemoteName) || binding.RemoteName.Contains("..") then
            Error(InvalidBinding "Invalid remote name.")
        elif embedsCredentials binding.ApprovedFetchUrl
             || embedsCredentials binding.ApprovedPushUrl then
            Error(InvalidBinding "Canonical remote URLs must not embed credentials or secret-bearing query parameters.")
        elif String.IsNullOrWhiteSpace binding.ApprovedFetchUrl
             || String.IsNullOrWhiteSpace binding.ApprovedPushUrl
             || binding.ProjectId = Guid.Empty || binding.LedgerId = Guid.Empty || binding.Epoch < 1 then
            Error(InvalidBinding "Canonical remote and ledger identity must be explicitly pinned.")
        else
            match run binding.RepositoryPath [ "remote"; "get-url"; binding.RemoteName ],
                  run binding.RepositoryPath [ "remote"; "get-url"; "--push"; binding.RemoteName ] with
            | Ok fetchUrl, Ok pushUrl when fetchUrl = binding.ApprovedFetchUrl && pushUrl = binding.ApprovedPushUrl ->
                Ok ()
            | Ok _, Ok _ -> Error(InvalidBinding "Remote fetch or push endpoint differs from approved binding.")
            | _ -> Error(InvalidBinding "Approved remote is not configured.")

    // Git's transport protocol cannot report hosting-provider branch rules
    // or repository visibility. For local bare remotes we can check actual
    // receive configuration; for hosted remotes we return explicit Unknown.
    let inspectRemotePolicy (binding: LedgerBinding) =
        match validate binding with
        | Error error -> Error error
        | Ok () ->
            let path =
                if Path.IsPathFullyQualified binding.ApprovedPushUrl then
                    Some binding.ApprovedPushUrl
                else
                    let mutable url = Unchecked.defaultof<Uri>
                    if Uri.TryCreate(binding.ApprovedPushUrl, UriKind.Absolute, &url)
                       && url.Scheme = Uri.UriSchemeFile then Some url.LocalPath
                    else None
            let unknown =
                { RemoteKind = "HostedOrUnknown"
                  Visibility = "Unknown"
                  NonFastForwardRejection = "Unknown"
                  DeletionRejection = "Unknown"
                  SignedCommitRequirement = "Unknown"
                  Warnings = [| "Provider visibility, branch protection and commit signing cannot be verified via Git transport. Confirm them with your host before use." |] }
            match path with
            | None -> Ok unknown
            | Some repo when not (Directory.Exists repo) -> Ok unknown
            | Some repo ->
                match git binding.RepositoryPath
                        [ "--git-dir=" + repo; "rev-parse"; "--is-bare-repository" ] with
                | Ok output when output.ExitCode = 0 && output.Stdout = "true" ->
                    let setting name =
                        match git binding.RepositoryPath
                                [ "--git-dir=" + repo; "config"; "--bool"; "--get"; name ] with
                        | Ok output when output.ExitCode = 0 && output.Stdout = "true" -> "Enforced"
                        | Ok output when output.ExitCode = 0 && output.Stdout = "false" -> "NotEnforced"
                        | Ok output when output.ExitCode = 1 -> "NotConfigured"
                        | _ -> "Unknown"
                    let nonFast = setting "receive.denyNonFastForwards"
                    let deletion = setting "receive.denyDeletes"
                    let warnings =
                        [| if nonFast <> "Enforced" then
                               "Local bare remote does not enforce rejecting non-fast-forward pushes."
                           if deletion <> "Enforced" then
                               "Local bare remote does not enforce rejecting branch deletion."
                           "Git cannot independently verify remote repository visibility or signed-commit policy." |]
                    Ok
                        { RemoteKind = "LocalBareGit"
                          Visibility = "Unknown"
                          NonFastForwardRejection = nonFast
                          DeletionRejection = deletion
                          SignedCommitRequirement = "Unknown"
                          Warnings = warnings }
                | _ -> Ok unknown

    let private cacheRef (binding: LedgerBinding) =
        $"refs/jarvis/ledger-cache/{binding.ProjectId:N}"

    let private validateIdentity (binding: LedgerBinding) (header: LedgerHeader) =
        if isNull (box header) || header.SchemaVersion <> 2
           || header.ProjectId <> string binding.ProjectId
           || header.LedgerId <> string binding.LedgerId
           || header.Epoch <> binding.Epoch
           || header.NextSequence < 1L
           || header.Status <> "active"
           || isNull (box header.Settings) then
            Error(LedgerIntegrityError "Ledger header identity, epoch, schema or status mismatch.")
        else
            Ok ()

    let private showBlob repo (commit: string) path =
        if not (oidPattern.IsMatch commit) then
            Error(LedgerHistoryUnavailable "Invalid snapshot commit OID.")
        else
            gitRaw repo [ "show"; $"{commit}:{path}" ] |> requireSuccess "Show exact Git blob"

    let private readHeader binding commit =
        match showBlob binding.RepositoryPath commit "ledger.json" with
        | Error _ -> Error(LedgerHistoryUnavailable "Ledger root metadata missing.")
        | Ok text ->
            try
                let header = JsonSerializer.Deserialize<LedgerHeader>(text, options)
                match validateIdentity binding header with
                | Error error -> Error error
                | Ok () -> Ok header
            with ex ->
                Error(LedgerIntegrityError $"Invalid ledger.json: {ex.Message}")

    let private getRef repo gitRef =
        match git repo [ "show-ref"; "--verify"; "--hash"; gitRef ] with
        | Ok result when result.ExitCode = 0 && oidPattern.IsMatch result.Stdout -> Ok(Some result.Stdout)
        | Ok result when result.ExitCode = 1 || (result.ExitCode = 128 && result.Stderr.Contains("not a valid ref")) -> Ok None
        | Ok result -> Error(GitFailure $"Cannot inspect ref: {result.Stderr}")
        | Error error -> Error error

    let private isAncestor repo oldHead newHead =
        match git repo [ "merge-base"; "--is-ancestor"; oldHead; newHead ] with
        | Ok result when result.ExitCode = 0 -> Ok true
        | Ok result when result.ExitCode = 1 -> Ok false
        | Ok _ -> Error(LedgerHistoryUnavailable "Cannot inspect trusted Git history.")
        | Error error -> Error error

    let private remoteExists binding =
        match git binding.RepositoryPath [ "ls-remote"; "--exit-code"; binding.RemoteName; ledgerRef ] with
        | Ok result when result.ExitCode = 0 -> Ok true
        | Ok result when result.ExitCode = 2 -> Ok false
        | Ok result -> Error(RemoteUnavailable result.Stderr)
        | Error error -> Error error

    // All authoritative reads fetch a fresh remote head; local refs are only GC/trust anchors.
    let fetch (binding: LedgerBinding) : Result<LedgerSnapshot, LedgerFailure> =
        match validate binding with
        | Error error -> Error error
        | Ok () ->
            let repo = binding.RepositoryPath
            let stage = $"refs/jarvis/fetch/{Guid.NewGuid():N}"
            try
                match git repo [ "fetch"; "--no-tags"; binding.RemoteName; $"{ledgerRef}:{stage}" ] with
                | Error error -> Error error
                | Ok fetched when fetched.ExitCode <> 0 ->
                    match remoteExists binding with
                    | Ok false -> Error(LedgerIntegrityError "Previously configured ledger branch is missing.")
                    | _ -> Error(RemoteUnavailable fetched.Stderr)
                | Ok _ ->
                    match run repo [ "rev-parse"; "--verify"; $"{stage}^{{commit}}" ] with
                    | Error _ -> Error(LedgerHistoryUnavailable "Fetched ledger commit is missing.")
                    | Ok head ->
                        match getRef repo (cacheRef binding) with
                        | Error error -> Error error
                        | Ok trusted ->
                            let ancestry =
                                match trusted with
                                | None -> Ok true
                                | Some previous -> isAncestor repo previous head
                            match ancestry with
                            | Error error -> Error error
                            | Ok false -> Error(LedgerIntegrityError "Canonical ledger ref is not descended from trusted history.")
                            | Ok true ->
                                match readHeader binding head with
                                | Error error -> Error error
                                | Ok header ->
                                    match run repo [ "update-ref"; (cacheRef binding); head ] with
                                    | Error error -> Error error
                                    | Ok _ -> Ok { Commit = head; Header = header }
            finally
                git repo [ "update-ref"; "-d"; stage ] |> ignore

    let verifyTrustedHead (binding: LedgerBinding) (expectedHead: string) (observedHead: string) =
        if String.IsNullOrWhiteSpace expectedHead
           || not (oidPattern.IsMatch expectedHead)
           || not (oidPattern.IsMatch observedHead) then
            Error(InvalidBinding "An exact trusted ledger commit OID is required.")
        else
            match isAncestor binding.RepositoryPath expectedHead observedHead with
            | Ok true -> Ok ()
            | Ok false ->
                Error(LedgerIntegrityError "Canonical ledger head is not a descendant of the configured trust anchor.")
            | Error _ ->
                Error(LedgerHistoryUnavailable "Configured trusted ledger commit is missing from fetched ancestry.")

    let readRecord binding (path: string) =
        if not (recordPath.IsMatch path) || path.Contains("..") then
            Error(InvalidBinding "Only safe records/*.json paths are accepted by the prototype.")
        else
            match fetch binding with
            | Error error -> Error error
            | Ok snapshot ->
                match git binding.RepositoryPath [ "ls-tree"; snapshot.Commit; "--"; path ] with
                | Error error -> Error error
                | Ok listing when listing.ExitCode <> 0 -> Error(LedgerHistoryUnavailable listing.Stderr)
                | Ok listing when listing.Stdout = "" -> Ok(snapshot, None)
                | Ok _ ->
                    showBlob binding.RepositoryPath snapshot.Commit path
                    |> Result.map (fun content -> snapshot, Some content)

    let private blobAt repo (commit: string) (path: string) =
        match git repo [ "ls-tree"; commit; "--"; path ] with
        | Error error -> Error error
        | Ok listing when listing.ExitCode <> 0 -> Error(LedgerHistoryUnavailable listing.Stderr)
        | Ok listing when listing.Stdout = "" -> Ok None
        | Ok listing ->
            let matchOid = Regex.Match(listing.Stdout, "^100644 blob ([0-9a-f]{40}(?:[0-9a-f]{24})?)\t")
            if matchOid.Success then Ok(Some matchOid.Groups[1].Value)
            else Error(LedgerIntegrityError "Unexpected ledger object type.")

    // Compute a prospective canonical spec blob OID without changing the
    // checkout or writing an object. The transaction writes/validates it later.
    let computeBlobOid (binding: LedgerBinding) (content: string) =
        if isNull content || Encoding.UTF8.GetByteCount(content) > 512 * 1024 then
            Error(InvalidBinding "Spec blob is missing or exceeds the canonical size cap.")
        else
            match validate binding with
            | Error error -> Error error
            | Ok () ->
                gitInput binding.RepositoryPath [ "hash-object"; "--stdin" ] content
                |> requireSuccess "hash-object"
                |> Result.bind (fun oid ->
                    if oidPattern.IsMatch oid then Ok oid
                    else Error(LedgerIntegrityError "Git produced an invalid spec blob hash."))

    let private hashBlob repo content =
        gitInput repo [ "hash-object"; "-w"; "--stdin" ] content
        |> requireSuccess "hash-object"

    let private writeTree repo parent (files: (string * string) list) =
        let index = Path.Combine(Path.GetTempPath(), $"jarvis-ledger-{Guid.NewGuid():N}.index")
        try
            let initial =
                match parent with
                | Some commit -> gitIndex repo index [ "read-tree"; commit ]
                | None -> gitIndex repo index [ "read-tree"; "--empty" ]
            match requireSuccess "read-tree" initial with
            | Error error -> Error error
            | Ok _ ->
                let mutable failed = None
                for (path, content) in files do
                    if failed.IsNone then
                        match hashBlob repo content with
                        | Error error -> failed <- Some error
                        | Ok blobOid ->
                            let updated = gitIndex repo index [ "update-index"; "--add"; "--cacheinfo"; $"100644,{blobOid},{path}" ]
                            match requireSuccess "update-index" updated with
                            | Error error -> failed <- Some error
                            | Ok _ -> ()
                match failed with
                | Some error -> Error error
                | None ->
                    gitIndex repo index [ "write-tree" ]
                    |> requireSuccess "write-tree"
        finally
            if File.Exists index then File.Delete index

    let private commitTree repo tree parent message =
        let args = [ "commit-tree"; tree; "-m"; message ]
        let args =
            match parent with
            | Some sha -> args @ [ "-p"; sha ]
            | None -> args
        run repo args

    let private opPath (id: Guid) =
        let key = id.ToString("N")
        $"ops/{key.Substring(0, 2)}/{key}.json"

    let private readOperation binding head id =
        let path = opPath id
        match blobAt binding.RepositoryPath head path with
        | Error error -> Error error
        | Ok None -> Ok None
        | Ok(Some _) ->
            match showBlob binding.RepositoryPath head path with
            | Error error -> Error error
            | Ok content ->
                try
                    let operation = JsonSerializer.Deserialize<LedgerOperation>(content, options)
                    if isNull (box operation) || operation.OperationId <> id.ToString("D")
                       || operation.SchemaVersion <> 2
                       || String.IsNullOrWhiteSpace operation.CanonicalRequestJson
                       || String.IsNullOrWhiteSpace operation.OriginalResultJson
                       || digest operation.CanonicalRequestJson <> operation.RequestHash
                       || digest operation.OriginalResultJson <> operation.OriginalResultHash then
                        Error(LedgerIntegrityError "Corrupt, incomplete or unsupported operation record.")
                    else
                        Ok(Some operation)
                with ex ->
                    Error(LedgerIntegrityError $"Malformed operation record: {ex.Message}")

    let getOperationAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (operationId: Guid) =
        if operationId = Guid.Empty then Error(InvalidBinding "Valid operation UUID required.")
        else readOperation binding snapshot.Commit operationId

    // Originating commit cannot be embedded in its own tree. Recover it by
    // tracing the addition of this immutable operation file through the
    // verified ancestry, then require the original blob and commit trailers.
    // A rewritten operation file is never treated as the same historical fact.
    let getOperationOriginAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                             (operation: LedgerOperation) =
        match Guid.TryParse operation.OperationId with
        | false, _ -> Error(LedgerIntegrityError "Operation has an invalid UUID.")
        | true, id ->
            let path = opPath id
            match blobAt binding.RepositoryPath snapshot.Commit path with
            | Error error -> Error error
            | Ok None -> Error(LedgerIntegrityError "Operation record missing at verified snapshot.")
            | Ok(Some currentOid) ->
                match run binding.RepositoryPath
                        [ "log"; "--first-parent"; "--diff-filter=A"; "--format=%H"
                          snapshot.Commit; "--"; path ] with
                | Error error -> Error error
                | Ok history ->
                    let origins = history.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    if origins.Length <> 1 || not (oidPattern.IsMatch origins[0]) then
                        Error(LedgerIntegrityError "Operation has no unique originating commit.")
                    else
                        let origin = origins[0]
                        match blobAt binding.RepositoryPath origin path with
                        | Error error -> Error error
                        | Ok(Some addedOid) when addedOid = currentOid ->
                            match run binding.RepositoryPath [ "show"; "-s"; "--format=%B"; origin ] with
                            | Error error -> Error error
                            | Ok message ->
                                if message.Contains($"Jarvis-Operation-Id: {id:D}", StringComparison.Ordinal)
                                   && message.Contains(
                                       $"Jarvis-Request-Hash: {operation.RequestHash}",
                                       StringComparison.Ordinal) then Ok origin
                                else Error(LedgerIntegrityError
                                    "Originating commit lacks matching operation evidence trailers.")
                        | _ -> Error(LedgerIntegrityError
                            "An operation record changed after its originating commit.")

    let getOperationStatus (binding: LedgerBinding) (operationId: Guid) =
        if operationId = Guid.Empty then Error(InvalidBinding "Valid operation UUID required.")
        else
            match fetch binding with
            | Error error -> Error error
            | Ok snapshot ->
                getOperationAt binding snapshot operationId
                |> Result.map (fun record -> snapshot, record)

    // A stable logical write receipt lives in the same canonical tree as the
    // mutation. It deliberately does not contain the moving remote HEAD.
    let private receipt operationId sequence kind changes =
        json
            {| schemaVersion = 1
               operationId = operationId
               sequence = sequence
               kind = kind
               changes = changes |}

    let private effect (change: LedgerFileChange) =
        let path = change.Path
        let isSpec = path.EndsWith("/spec.html", StringComparison.Ordinal)
        let entityKind =
            if isSpec then "TaskSpec"
            elif path.EndsWith("/task.json", StringComparison.Ordinal) then "Task"
            elif path.StartsWith("agents/", StringComparison.Ordinal) then "Agent"
            elif path.Contains("/checkpoints/", StringComparison.Ordinal) then "Checkpoint"
            elif path.EndsWith(".ack.json", StringComparison.Ordinal)
                 && path.Contains("/assignments/", StringComparison.Ordinal) then "AssignmentReceipt"
            elif path.Contains("/assignments/", StringComparison.Ordinal) then "AssignmentIssue"
            elif path.Contains("/integrations/", StringComparison.Ordinal) then "Integration"
            elif path.Contains("/revisions/", StringComparison.Ordinal) then "ReviewRevision"
            elif path.Contains("/decisions/", StringComparison.Ordinal) then "ReviewDecisionEvidence"
            elif path.EndsWith("/request.json", StringComparison.Ordinal) then "ReviewRequest"
            elif path.EndsWith("/notification.json", StringComparison.Ordinal) then "ReviewNotification"
            elif path.Contains("/attempts/", StringComparison.Ordinal) then "Attempt"
            else "Record"
        let source =
            if isSpec then None else
                try
                    let doc = JsonDocument.Parse(change.Content)
                    if doc.RootElement.ValueKind = JsonValueKind.Object then Some doc
                    else
                        doc.Dispose()
                        None
                with _ -> None
        try
            let find (name: string) =
                source |> Option.bind (fun doc ->
                    let mutable found = Unchecked.defaultof<JsonElement>
                    if doc.RootElement.TryGetProperty(name, &found) then Some found else None)
            let str name =
                find name |> Option.bind (fun value ->
                    if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)
            let number name =
                find name |> Option.bind (fun value ->
                    let mutable n = 0
                    if value.ValueKind = JsonValueKind.Number && value.TryGetInt32(&n) then Some n else None)
            let longNumber name =
                find name |> Option.bind (fun value ->
                    let mutable n = 0L
                    if value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&n) then Some n else None)
            { Path = path; EntityKind = entityKind
              ContentSha256 = digest change.Content
              EntityVersion = number "version"
              TaskId = str "taskId"; AttemptId = str "attemptId"
              RequestId = str "requestId"; AgentId = str "agentId"; ActionId = str "actionId"
              Lifecycle = str "lifecycle"; State = str "state"
              Phase = str "phase"; Status = str "status"
              AssignmentGeneration = number "assignmentGeneration"
              CheckpointSequence = if entityKind = "Checkpoint" then longNumber "sequence" else None
              CheckpointKind = if entityKind = "Checkpoint" then str "kind" else None
              CurrentReviewRevision =
                  match number "currentRevision" with
                  | Some revision -> Some revision
                  | None -> number "revision"
              ClaimedReviewRevision = number "claimedRevision"
              ReviewRevision = number "reviewRevision"
              AcceptedReviewRevision = number "acceptedReviewRevision"
              SpecBlobOid = str "specBlobOid"
              HeadCommit = str "headCommit"; ReviewRef = str "reviewRef"
              VerifiedIntegrationSequence = longNumber "verifiedSequence" }
        finally
            source |> Option.iter (fun doc -> doc.Dispose())

    let private domainReceipt operationId sequence kind (changes: LedgerFileChange list) =
        json
            {| schemaVersion = 2
               operationId = operationId
               sequence = sequence
               kind = kind
               changes =
                   changes |> List.map (fun change ->
                       {| path = change.Path
                          beforeOid = change.ExpectedBlobOid
                          contentSha256 = digest change.Content |}) |> List.toArray
               entityResults = changes |> List.map effect |> List.toArray |}

    let private recordInput (binding: LedgerBinding) (change: LedgerRecordChange) =
        json
            {| schemaVersion = 2
               projectId = string binding.ProjectId
               operationId = change.OperationId.ToString("D")
               operationType = "PutRecord"
               actor = change.Actor
               path = change.Path
               expectedBlob = (change.ExpectedBlobOid |> Option.defaultValue "")
               contentSha256 = digest change.Content |}

    let private recordHash binding change = digest (recordInput binding change)

    let private checkDuplicate binding head change requestHash =
        match readOperation binding head change.OperationId with
        | Error error -> Error error
        | Ok(Some operation) when operation.RequestHash = requestHash ->
            Ok(Some { Operation = operation; CurrentHead = head; AlreadyApplied = true })
        | Ok(Some _) -> Error OperationIdReused
        | Ok None -> Ok None

    // Initialization requires an explicit, caller-confirmed privacy/remote approval.
    // This is not exposed as a live MCP tool until full binding and permission UX exists.
    let initialize (binding: LedgerBinding) approved =
        if not approved then
            Error(InvalidBinding "Initial ledger publication requires explicit user consent.")
        else
            match validate binding with
            | Error error -> Error error
            | Ok () ->
                match remoteExists binding with
                | Error error -> Error error
                | Ok true -> Error(LedgerIntegrityError "Ledger ref already exists; refusing to reinitialize.")
                | Ok false ->
                    let header =
                        { SchemaVersion = 2
                          ProjectId = string binding.ProjectId
                          LedgerId = string binding.LedgerId
                          Epoch = binding.Epoch
                          NextSequence = 1L
                          CreatedAt = DateTimeOffset.UtcNow.ToString("O")
                          Status = "active"
                          Settings = { MaxWorkingPerAgent = 1; AllowDirectSourcePush = true } }
                    let repo = binding.RepositoryPath
                    match writeTree repo None [ "ledger.json", json header ] with
                    | Error error -> Error error
                    | Ok tree ->
                        match commitTree repo tree None "Initialize Jarvis orchestration ledger" with
                        | Error error -> Error error
                        | Ok commit ->
                            match git repo [ "push"; "--porcelain"; binding.RemoteName; $"{commit}:{ledgerRef}" ] with
                            | Error error -> Error error
                            | Ok pushed when pushed.ExitCode <> 0 ->
                                Error(OutcomeUnknown $"Ledger initialization push was not confirmed: {pushed.Stderr}")
                            | Ok _ ->
                                match fetch binding with
                                | Error error -> Error error
                                | Ok snapshot ->
                                    match isAncestor repo commit snapshot.Commit with
                                    | Ok true -> Ok snapshot
                                    | Ok false -> Error(OutcomeUnknown "Remote did not retain initialization commit.")
                                    | Error error -> Error error

    // V2 prototype's generic record mutation. Task transitions are intentionally not yet wired.
    let putRecord (binding: LedgerBinding) (change: LedgerRecordChange) =
        if change.OperationId = Guid.Empty || String.IsNullOrWhiteSpace change.Actor
           || not (recordPath.IsMatch change.Path) || change.Path.Contains("..")
           || isNull change.Content || encoding.GetByteCount(change.Content) > 64 * 1024
           || (change.ExpectedBlobOid |> Option.exists (oidPattern.IsMatch >> not)) then
            Error(InvalidBinding "Invalid idempotency key, actor, path, expected blob or record size.")
        else
            let repo = binding.RepositoryPath
            let requestHash = recordHash binding change
            let rec attempt retries =
                match fetch binding with
                | Error error -> Error error
                | Ok snapshot ->
                    let head = snapshot.Commit
                    match checkDuplicate binding head change requestHash with
                    | Error error -> Error error
                    | Ok(Some result) -> Ok result
                    | Ok None ->
                        match blobAt repo head change.Path with
                        | Error error -> Error error
                        | Ok present when present <> change.ExpectedBlobOid ->
                            Error(StaleEntityVersion $"Record {change.Path} changed since the requested version.")
                        | Ok _ ->
                            let blob = hashBlob repo change.Content
                            match blob with
                            | Error error -> Error error
                            | Ok contentOid ->
                                let sequence = snapshot.Header.NextSequence
                                let originalResult =
                                    receipt (change.OperationId.ToString("D")) sequence "PutRecord"
                                        [| {| path = change.Path
                                              beforeOid = change.ExpectedBlobOid
                                              contentSha256 = digest change.Content
                                              committedBlobOid = contentOid |} |]
                                let operation =
                                    { SchemaVersion = 2
                                      CanonicalRequestJson = recordInput binding change
                                      OriginalResultJson = originalResult
                                      OriginalResultHash = digest originalResult
                                      OperationId = change.OperationId.ToString("D")
                                      RequestHash = requestHash
                                      Sequence = sequence
                                      Type = "PutRecord"
                                      Actor = change.Actor
                                      Path = change.Path
                                      BlobOid = contentOid
                                      Paths = [| change.Path |] }
                                let nextHeader = { snapshot.Header with NextSequence = sequence + 1L }
                                match writeTree repo (Some head)
                                        [ "ledger.json", json nextHeader
                                          change.Path, change.Content
                                          opPath change.OperationId, json operation ] with
                                | Error error -> Error error
                                | Ok tree ->
                                    match commitTree repo tree (Some head)
                                            $"Jarvis PutRecord {change.Path}\n\nJarvis-Operation-Id: {change.OperationId:D}\nJarvis-Request-Hash: {requestHash}" with
                                    | Error error -> Error error
                                    | Ok commit ->
                                        let pushed = git repo [ "push"; "--porcelain"; binding.RemoteName; $"{commit}:{ledgerRef}" ]
                                        // Always inspect the remote again. A failed command may have already published.
                                        match fetch binding with
                                        | Error _ -> Error(OutcomeUnknown $"Cannot verify operation {change.OperationId:D}.")
                                        | Ok current ->
                                            match checkDuplicate binding current.Commit change requestHash with
                                            | Error error -> Error error
                                            | Ok(Some existing) ->
                                                match isAncestor repo commit current.Commit with
                                                | Ok true when (match pushed with | Ok output -> output.ExitCode = 0 | _ -> false) ->
                                                    Ok { existing with AlreadyApplied = false }
                                                | _ -> Ok existing
                                            | Ok None ->
                                                match pushed with
                                                | Error _ -> Error(OutcomeUnknown $"Push outcome unknown for {change.OperationId:D}.")
                                                | Ok output when output.ExitCode <> 0 && current.Commit = head ->
                                                    Error(GitFailure $"Ledger push rejected without remote advance: {output.Stderr}")
                                                | Ok _ when retries >= 7 -> Error LedgerContended
                                                | _ ->
                                                    System.Threading.Thread.Sleep(contentionDelay retries)
                                                    attempt (retries + 1)
            attempt 0

    // Pinned reads used by domain transitions. Never fetch a newer head mid-transaction.
    let readAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (path: string) =
        if not (ledgerPath.IsMatch path) || path.Contains("..") then
            Error(InvalidBinding "Unsafe ledger entity path.")
        else
            match blobAt binding.RepositoryPath snapshot.Commit path with
            | Error error -> Error error
            | Ok None -> Ok(None, None)
            | Ok(Some oid) ->
                showBlob binding.RepositoryPath snapshot.Commit path
                |> Result.map (fun content -> Some content, Some oid)

    let listAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (prefix: string) =
        if prefix <> "tasks" && prefix <> "agents" then
            Error(InvalidBinding "Unsupported ledger enumeration prefix.")
        else
            run binding.RepositoryPath [ "ls-tree"; "-r"; "--name-only"; snapshot.Commit; "--"; prefix ]
            |> Result.map (fun value ->
                value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.filter (fun path -> ledgerPath.IsMatch path && not (path.Contains("..")))
                |> Array.toList)

    let verifySourceCommit (binding: LedgerBinding) (sourceRef: string) (commit: string) =
        if not (sourceRef.StartsWith("refs/heads/", StringComparison.Ordinal))
           || not (Regex.IsMatch(sourceRef, "^refs/heads/[A-Za-z0-9][A-Za-z0-9_./-]*$"))
           || sourceRef.Contains("..") || sourceRef.EndsWith("/")
           || not (oidPattern.IsMatch commit) then
            Error(InvalidBinding "Invalid fully qualified source ref or Git commit OID.")
        else
            let repo = binding.RepositoryPath
            match git repo [ "rev-parse"; "--verify"; $"{sourceRef}^{{commit}}" ] with
            | Error error -> Error error
            | Ok resolved when resolved.ExitCode <> 0 ->
                Error(InvalidBinding "The source branch is unavailable in this repository.")
            | Ok _ ->
                match git repo [ "cat-file"; "-t"; commit ] with
                | Ok result when result.ExitCode = 0 && result.Stdout = "commit" ->
                    match isAncestor repo commit sourceRef with
                    | Ok true -> Ok ()
                    | Ok false -> Error(InvalidBinding "Submitted commit is not in the specified source branch.")
                    | Error error -> Error error
                | _ -> Error(InvalidBinding "Submitted OID is not a Git commit.")

    // A branch is source context, not a particular checkout. Reading it must
    // not depend on whether some unrelated worktree is dirty or detached.
    let sourceBranchHead (binding: LedgerBinding) sourceRef =
        if isNull sourceRef
           || not (Regex.IsMatch(sourceRef, "^refs/heads/[A-Za-z0-9][A-Za-z0-9_./-]*$"))
           || sourceRef.Contains("..") || sourceRef.EndsWith("/") then
            Error(InvalidBinding "Invalid source branch ref.")
        else
            match git binding.RepositoryPath [ "rev-parse"; "--verify"; $"{sourceRef}^{{commit}}" ] with
            | Ok output when output.ExitCode = 0 && oidPattern.IsMatch output.Stdout ->
                Ok output.Stdout
            | Ok _ -> Error(SourceUnavailable "Source branch does not exist.")
            | Error error -> Error error

    let verifySourceObject (binding: LedgerBinding) (commit: string) =
        if isNull commit || not (oidPattern.IsMatch commit) then
            Error(InvalidBinding "Invalid source commit OID.")
        else
            match git binding.RepositoryPath [ "cat-file"; "-t"; commit ] with
            | Ok result when result.ExitCode = 0 && result.Stdout = "commit" -> Ok ()
            | _ -> Error(SourceUnavailable $"Source commit {commit} is not available on this device.")


    // Source identity is independent of the orphan ledger project UUID and
    // of any workstation path. Both approved Git endpoints are part of the
    // fingerprint: forks and independently configured push destinations do
    // not silently become equivalent repositories.
    let sourceRepositoryIdentity (binding: LedgerBinding) =
        match validate binding with
        | Error error -> Error error
        | Ok () ->
            let repo = binding.RepositoryPath
            match run repo [ "rev-parse"; "--show-toplevel" ],
                  run repo [ "worktree"; "list"; "--porcelain" ] with
            | Ok selectedRoot, Ok registered ->
                let selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedRoot))
                let approved =
                    registered.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.exists (fun line ->
                        line.StartsWith("worktree ", StringComparison.Ordinal)
                        && String.Equals(
                            Path.TrimEndingDirectorySeparator(Path.GetFullPath(line.Substring(9))),
                            selected, StringComparison.Ordinal))
                if not approved then
                    Error(InvalidBinding "Selected source checkout is not a registered Git worktree.")
                else
                    let fingerprint = digest ("jarvis-source-repository-v1\n"
                                                + binding.ApprovedFetchUrl + "\n"
                                                + binding.ApprovedPushUrl)
                    Ok { RepositoryId = fingerprint
                         RemoteName = binding.RemoteName
                         FetchUrl = binding.ApprovedFetchUrl
                         PushUrl = binding.ApprovedPushUrl }
            | _ -> Error(SourceUnavailable "Cannot verify registered source checkout.")

    // The selected checkout must be a registered Git worktree of the
    // already approved project; never allow an arbitrary caller-chosen root.
    // Paths are scoped under that checkout and every existing component is
    // inspected for symlinks before reading or writing.
    let private checkedSourcePath (binding: LedgerBinding) (relative: string) =
        match sourceRepositoryIdentity binding with
        | Error error -> Error error
        | Ok identity ->
            if String.IsNullOrWhiteSpace relative || relative.Length > 400
               || Path.IsPathRooted(relative) || relative.Contains('\\')
               || relative.Contains(':') || relative.Contains('\000')
               || relative.Split('/').Length > 20
               || relative.Split('/') |> Array.exists (fun part ->
                    part = "" || part = "." || part = ".." || part = ".git"
                    || part.StartsWith(".", StringComparison.Ordinal)
                    || part |> Seq.exists Char.IsControl)
               || not (relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) then
                Error(InvalidBinding "Unsafe source HTML path.")
            else
                let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binding.RepositoryPath))
                let path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
                if not (path.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)) then
                    Error(InvalidBinding "Source path escapes registered checkout.")
                else
                    let mutable current = root
                    let mutable unsafePath = false
                    for part in relative.Split('/') do
                        current <- Path.Combine(current, part)
                        if File.Exists(current) || Directory.Exists(current) then
                            let attributes = File.GetAttributes(current)
                            if attributes.HasFlag(FileAttributes.ReparsePoint) then unsafePath <- true
                    if unsafePath then Error(InvalidBinding "Source path uses a symlink or reparse point.")
                    else Ok(identity, root, path)

    // Source drafts may be tracked or untracked. The blob OID denotes the
    // exact worktree bytes, while HEAD and branch denote import provenance.
    let readRegisteredSourceSpec (binding: LedgerBinding) relative =
        match checkedSourcePath binding relative with
        | Error error -> Error error
        | Ok(identity, root, path) ->
            try
                if not (File.Exists(path)) then
                    Error(SourceUnavailable "Source HTML file does not exist.")
                elif (FileInfo(path)).Length > 512L * 1024L then
                    Error(InvalidBinding "Source HTML exceeds 512 KiB.")
                else
                    match run root [ "symbolic-ref"; "-q"; "HEAD" ],
                          run root [ "rev-parse"; "HEAD" ] with
                    | Ok sourceRef, Ok commit when
                        sourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                        && oidPattern.IsMatch commit ->
                        let bytes = File.ReadAllBytes(path)
                        if bytes.Length > 512 * 1024 then
                            Error(InvalidBinding "Source HTML changed or exceeds 512 KiB.")
                        else
                            let html = UTF8Encoding(false, true).GetString(bytes)
                            match computeBlobOid binding html with
                            | Error error -> Error error
                            | Ok blob ->
                                Ok { RepositoryId = identity.RepositoryId; SourceRef = sourceRef
                                     CommitOid = commit; RelativePath = relative; BlobOid = blob
                                     Html = html }
                    | _ -> Error(SourceUnavailable "Source worktree must have a checked-out branch and commit.")
            with
            | :? DecoderFallbackException ->
                Error(InvalidBinding "Source HTML is not valid UTF-8.")
            | :? System.IO.IOException as ex ->
                Error(SourceUnavailable $"Source HTML unavailable: {ex.Message}")

    // Called only after ledger/snapshot authority has been established.
    // The pinned OID must actually occur at this task spec path in canonical
    // Git ancestry; arbitrary local Git blobs or source-branch blobs are not
    // eligible for historical task diff or paging.
    let readHistoricalTaskSpec (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                               taskId (blobOid: string) =
        if String.IsNullOrWhiteSpace taskId
           || not (Regex.IsMatch(taskId, "^[A-Za-z0-9_-]{1,80}$"))
           || isNull blobOid || not (oidPattern.IsMatch blobOid) then
            Error(InvalidBinding "Invalid task/spec history locator.")
        else
            let path = $"tasks/{taskId}/spec.html"
            match run binding.RepositoryPath
                    [ "log"; "--format=%H"; snapshot.Commit; "--"; path ] with
            | Error error -> Error error
            | Ok history ->
                let commits = history.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                let mutable found = false
                let mutable failure = None
                for commit in commits do
                    if not found && failure.IsNone then
                        match blobAt binding.RepositoryPath commit path with
                        | Error error -> failure <- Some error
                        | Ok oid when oid = Some blobOid -> found <- true
                        | Ok _ -> ()
                match failure with
                | Some error -> Error error
                | None when not found ->
                    Error(InvalidBinding "Blob OID does not belong to this task's canonical spec history.")
                | None ->
                    match gitRaw binding.RepositoryPath [ "cat-file"; "blob"; blobOid ] with
                    | Ok data when data.ExitCode = 0 ->
                        try
                            let content = data.Stdout
                            if encoding.GetByteCount(content) > 512 * 1024 then
                                Error(LedgerIntegrityError "Historical task specification exceeds canonical limit.")
                            else Ok content
                        with ex -> Error(LedgerIntegrityError ex.Message)
                    | _ -> Error(LedgerHistoryUnavailable "Historical task spec blob is missing.")

    let verifiedHistoricalSnapshot (binding: LedgerBinding) (commit: string) =
        if isNull commit || not (oidPattern.IsMatch commit) then
            Error(InvalidBinding "Invalid snapshot locator.")
        else
            match fetch binding with
            | Error error -> Error error
            | Ok current ->
                match git binding.RepositoryPath [ "merge-base"; "--is-ancestor"; commit; current.Commit ] with
                | Ok output when output.ExitCode = 0 ->
                    readHeader binding commit
                    |> Result.map (fun header -> { Commit = commit; Header = header })
                | _ -> Error(LedgerHistoryUnavailable "Pinned snapshot is unavailable or no longer trusted canonical ancestry.")

    let diffHistoricalTaskSpecs (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                 taskId beforeOid afterOid =
        match readHistoricalTaskSpec binding snapshot taskId beforeOid,
              readHistoricalTaskSpec binding snapshot taskId afterOid with
        | Error error, _ | _, Error error -> Error error
        | Ok _, Ok _ ->
            match gitRaw binding.RepositoryPath
                    [ "diff"; "--no-ext-diff"; "--no-color"; "--unified=3"; beforeOid; afterOid ] with
            | Ok diff when diff.ExitCode = 0 ->
                let limit = 128 * 1024
                let bytes = encoding.GetBytes(diff.Stdout)
                if bytes.Length <= limit then Ok(diff.Stdout, false)
                else
                    let mutable count = limit
                    while count > 0 && count < bytes.Length && (bytes[count] &&& 0xC0uy) = 0x80uy do
                        count <- count - 1
                    Ok(encoding.GetString(bytes, 0, count), true)
            | _ -> Error(LedgerHistoryUnavailable "Unable to diff pinned historical spec blobs.")

    // Exports are create-only, read-only authority snapshots. Validate all
    // preconditions before the temp write and perform a no-overwrite move;
    // failures never rewrite a pre-existing source file or canonical ledger.
    let exportRegisteredSpec (binding: LedgerBinding) relative taskId
                             snapshotOid blobOid (operationId: Guid) html =
        match checkedSourcePath binding relative with
        | Error error -> Error error
        | Ok(_, _, destination) ->
            let prefix =
                $"<!-- NON-AUTHORITATIVE JARVIS V2 EXPORT; operation={operationId:D}; task={taskId}; ledger-snapshot="
            let suffix =
                $"; spec-blob={blobOid}; editing this copy does not change the canonical ledger. -->\n"
            let replay () =
                try
                    let existing = File.ReadAllText(destination, UTF8Encoding(false, true))
                    if existing.StartsWith(prefix, StringComparison.Ordinal) then
                        let separator = existing.IndexOf(suffix, prefix.Length, StringComparison.Ordinal)
                        if separator > prefix.Length
                           && oidPattern.IsMatch(existing.Substring(prefix.Length, separator - prefix.Length))
                           && existing.Substring(separator + suffix.Length) = html then
                            // The operation is a replay of the exact same
                            // content, not an overwrite of someone else's file.
                            Ok(true)
                        else
                            Error(StaleEntityVersion "Export destination already exists with other content.")
                    else Error(StaleEntityVersion "Export destination already exists with other content.")
                with ex ->
                    Error(SourceUnavailable $"Existing export cannot be verified: {ex.Message}")
            if File.Exists destination then replay ()
            elif Directory.Exists destination then
                Error(StaleEntityVersion "Export destination is an existing directory.")
            elif not (Directory.Exists(Path.GetDirectoryName destination)) then
                Error(InvalidBinding "Export directory must already exist.")
            else
                let temp = Path.Combine(Path.GetDirectoryName destination,
                                        $".jarvis-spec-{Guid.NewGuid():N}.tmp")
                try
                    try
                        let heading = prefix + snapshotOid + suffix
                        File.WriteAllText(temp, heading + html, UTF8Encoding(false))
                        File.Move(temp, destination, false)
                        Ok(false)
                    with
                    | :? System.IO.IOException when File.Exists destination -> replay ()
                    | ex -> Error(SourceUnavailable $"Export was not published: {ex.Message}")
                finally
                    if File.Exists temp then File.Delete temp

    // This is a read-only local observation, never authoritative ownership.
    // Branches can survive checkout deletion; detached/dirty checkouts are
    // reported rather than guessed to be an assigned worker's source.
    let inspectSourceWorktree (binding: LedgerBinding) sourceRef =
        if isNull sourceRef
           || not (Regex.IsMatch(sourceRef, "^refs/heads/[A-Za-z0-9][A-Za-z0-9_./-]*$"))
           || sourceRef.Contains("..") || sourceRef.EndsWith("/") then
            Error(InvalidBinding "Fully qualified source branch required.")
        else
            match sourceRepositoryIdentity binding with
            | Error error -> Error error
            | Ok identity ->
                let repo = binding.RepositoryPath
                match run repo [ "worktree"; "list"; "--porcelain" ],
                      run repo [ "rev-parse"; "--show-toplevel" ],
                      git repo [ "show-ref"; "--verify"; "--hash"; sourceRef ],
                      run repo [ "rev-parse"; "HEAD" ],
                      run repo [ "status"; "--porcelain" ] with
                | Ok trees, Ok selectedRoot, Ok source, Ok selectedHead, Ok status ->
                    let selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedRoot))
                    let blocks = trees.Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
                    let parsed =
                        blocks |> Array.choose (fun block ->
                            let lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            match lines |> Array.tryFind (fun l -> l.StartsWith("worktree ", StringComparison.Ordinal)) with
                            | None -> None
                            | Some path ->
                                let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Substring(9)))
                                // Git can keep a prunable registration after a checkout
                                // directory was moved or deleted outside git worktree.
                                // Such a registration is not evidence of an extant worktree.
                                if not (Directory.Exists(root))
                                   || (lines |> Array.exists (fun l ->
                                       l.StartsWith("prunable ", StringComparison.Ordinal))) then
                                    None
                                else
                                    let branch =
                                        lines |> Array.tryFind (fun l -> l.StartsWith("branch ", StringComparison.Ordinal))
                                        |> Option.map (fun l -> l.Substring(7))
                                    Some(root, branch))
                    let selectedBranch =
                        parsed |> Array.tryPick (fun (root, branch) ->
                            if root = selected then Some branch else None)
                    let checkedOut = parsed |> Array.exists (fun (_, branch) -> branch = Some sourceRef)
                    let branchPresent = source.ExitCode = 0
                    let detached = selectedBranch = Some None
                    let dirty = not (String.IsNullOrEmpty status)
                    let state =
                        if not branchPresent then "SourceUnavailable"
                        elif detached && branchPresent && selectedHead <> source.Stdout then "StaleDetachedCheckout"
                        elif detached && not checkedOut then "DetachedCheckout"
                        elif dirty && selectedBranch = Some(Some sourceRef) then "Uncommitted"
                        elif checkedOut then "CheckedOut"
                        else "AvailableNoWorktree"
                    Ok { RepositoryId = identity.RepositoryId
                         SourceRef = sourceRef
                         SourceHead = if branchPresent then source.Stdout else null
                         SelectedHead = selectedHead
                         State = state
                         WorktreePresent = checkedOut
                         SelectedWorktreeDetached = detached
                         SelectedWorktreeDirty = dirty }
                | _ -> Error(SourceUnavailable "Cannot inspect registered source worktrees.")

    let verifyReviewDiff (binding: LedgerBinding) (baseOid: string) (headOid: string) =
        if isNull baseOid || isNull headOid
           || not (oidPattern.IsMatch baseOid) || not (oidPattern.IsMatch headOid) then
            Error(InvalidBinding "Invalid review base/head Git object ID.")
        else
            match verifySourceObject binding baseOid, verifySourceObject binding headOid with
            | Error error, _ | _, Error error -> Error error
            | Ok (), Ok () ->
                match isAncestor binding.RepositoryPath baseOid headOid with
                | Error error -> Error error
                | Ok false -> Error(InvalidBinding "Review base must be an ancestor of submitted head.")
                | Ok true ->
                    match git binding.RepositoryPath [ "diff"; "--quiet"; baseOid; headOid ] with
                    | Ok output when output.ExitCode = 1 -> Ok ()
                    | Ok output when output.ExitCode = 0 -> Error(InvalidBinding "Code review has no changes relative to its merge-base.")
                    | Ok output -> Error(GitFailure $"Could not inspect review delta: {output.Stderr}")
                    | Error error -> Error error

    let private safeReviewRef (gitRef: string) =
        not (isNull gitRef)
        && Regex.IsMatch(gitRef, "^refs/heads/jarvis-review/[A-Za-z0-9_-]+/[a-f0-9]{32}/r[1-9][0-9]*$")
        && not (gitRef.Contains(".."))

    let private remoteReviewRef (binding: LedgerBinding) gitRef =
        match git binding.RepositoryPath [ "ls-remote"; "--refs"; binding.RemoteName; gitRef ] with
        | Error error -> Error error
        | Ok response when response.ExitCode <> 0 -> Error(RemoteUnavailable response.Stderr)
        | Ok response when response.Stdout = "" -> Ok None
        | Ok response ->
            let fields = response.Stdout.Split([|'\t'|], StringSplitOptions.RemoveEmptyEntries)
            if fields.Length <> 2 || fields[1] <> gitRef || not (oidPattern.IsMatch fields[0]) then
                Error(LedgerIntegrityError "Ambiguous or malformed remote review ref.")
            else Ok(Some fields[0])

    // The source operation is separate from the canonical ledger commit.
    // The absent-ref lease enforces create-only publication, even for fast-forward related heads.
    let publishReviewRef (binding: LedgerBinding) gitRef headOid =
        if not (safeReviewRef gitRef) || isNull headOid || not (oidPattern.IsMatch headOid) then
            Error(InvalidBinding "Invalid review source ref or head OID.")
        else
            match validate binding with
            | Error error -> Error error
            | Ok () ->
                match verifySourceObject binding headOid with
                | Error error -> Error error
                | Ok () ->
                    match remoteReviewRef binding gitRef with
                    | Error error -> Error error
                    | Ok(Some current) when current = headOid -> Ok ()
                    | Ok(Some _) -> Error(SourceReviewRefUnavailable "Existing immutable review ref points to another commit.")
                    | Ok None ->
                        let result =
                            git binding.RepositoryPath
                                [ "push"; "--porcelain"; $"--force-with-lease={gitRef}:"; binding.RemoteName; $"{headOid}:{gitRef}" ]
                        match remoteReviewRef binding gitRef with
                        | Ok(Some observed) when observed = headOid -> Ok ()
                        | Ok(Some _) -> Error(SourceReviewRefUnavailable "Immutable review ref changed or was claimed elsewhere.")
                        | Ok None ->
                            match result with
                            | Ok output when output.ExitCode <> 0 -> Error(GitFailure $"Source publication rejected: {output.Stderr}")
                            | _ -> Error(OutcomeUnknown $"Cannot confirm source publication at {gitRef}.")
                        | Error _ -> Error(OutcomeUnknown $"Cannot verify source publication at {gitRef}.")

    // Inspect the source remote without altering a local branch/worktree.
    // Only parser-validated review refs in one task namespace are returned.
    let listRemoteReviewRefs (binding: LedgerBinding) taskId =
        if isNull taskId || not (Regex.IsMatch(taskId, "^[A-Za-z0-9_-]{1,80}$")) then
            Error(InvalidBinding "Invalid orphan-review task ID.")
        else
            match validate binding with
            | Error error -> Error error
            | Ok () ->
                let pattern = $"refs/heads/jarvis-review/{taskId}/*"
                match git binding.RepositoryPath
                        [ "ls-remote"; "--refs"; binding.RemoteName; pattern ] with
                | Error error -> Error error
                | Ok response when response.ExitCode <> 0 ->
                    Error(RemoteUnavailable response.Stderr)
                | Ok response ->
                    let lines = response.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    let mutable entries = []
                    let mutable invalid = false
                    for line in lines do
                        let parts = line.Split('\t')
                        if parts.Length <> 2 || not (oidPattern.IsMatch parts[0])
                           || not (safeReviewRef parts[1])
                           || not (parts[1].StartsWith($"refs/heads/jarvis-review/{taskId}/", StringComparison.Ordinal)) then
                            invalid <- true
                        else entries <- (parts[1], parts[0]) :: entries
                    if invalid then Error(LedgerIntegrityError "Malformed or out-of-scope source review ref.")
                    else Ok(entries |> List.sortBy fst)

    let inspectRemoteReviewRef (binding: LedgerBinding) gitRef =
        if not (safeReviewRef gitRef) then Error(InvalidBinding "Invalid immutable review ref.")
        else
            match validate binding with
            | Error error -> Error error
            | Ok () -> remoteReviewRef binding gitRef

    // CAS source deletion is intentionally separate from ledger reservation.
    // There is no force or 'delete all' operation; an unexpected OID is retained.
    let deleteReservedReviewRef (binding: LedgerBinding) gitRef expectedOid =
        if not (safeReviewRef gitRef)
           || isNull expectedOid || not (oidPattern.IsMatch expectedOid) then
            Error(InvalidBinding "Deletion requires exact review ref and expected SHA.")
        else
            inspectRemoteReviewRef binding gitRef
            |> Result.bind (function
                | None -> Ok "AlreadyAbsent"
                | Some found when found <> expectedOid ->
                    Error(SourceReviewRefUnavailable "Remote review ref no longer matches reserved orphan SHA.")
                | Some _ ->
                    let push = git binding.RepositoryPath
                                    [ "push"; "--porcelain"
                                      $"--force-with-lease={gitRef}:{expectedOid}"
                                      binding.RemoteName; $":{gitRef}" ]
                    inspectRemoteReviewRef binding gitRef
                    |> Result.bind (function
                        | None -> Ok "Deleted"
                        | Some found when found <> expectedOid ->
                            Error(SourceReviewRefUnavailable "Review ref changed during guarded deletion.")
                        | Some _ ->
                            match push with
                            | Ok response when response.ExitCode <> 0 ->
                                Error(GitFailure $"Guarded review deletion rejected: {response.Stderr}")
                            | _ ->
                                Error(OutcomeUnknown "Cannot confirm guarded review ref deletion.")))

    let fetchReviewRef (binding: LedgerBinding) gitRef headOid =
        if not (safeReviewRef gitRef) || isNull headOid || not (oidPattern.IsMatch headOid) then
            Error(InvalidBinding "Invalid review source locator.")
        else
            match validate binding with
            | Error error -> Error error
            | Ok () ->
                match remoteReviewRef binding gitRef with
                | Error error -> Error error
                | Ok None -> Error(SourceReviewRefUnavailable "Pinned source review ref is missing.")
                | Ok(Some actual) when actual <> headOid ->
                    Error(SourceReviewRefUnavailable "Pinned source review ref no longer matches its submitted commit.")
                | Ok(Some _) ->
                    let stage = $"refs/jarvis/source-reviews/{Guid.NewGuid():N}"
                    try
                        match git binding.RepositoryPath [ "fetch"; "--no-tags"; binding.RemoteName; $"{gitRef}:{stage}" ] with
                        | Error error -> Error error
                        | Ok output when output.ExitCode <> 0 -> Error(SourceReviewRefUnavailable output.Stderr)
                        | Ok _ ->
                            match run binding.RepositoryPath [ "rev-parse"; stage ] with
                            | Ok fetched when fetched = headOid -> Ok ()
                            | Ok _ -> Error(SourceReviewRefUnavailable "Remote source changed while fetching.")
                            | Error error -> Error error
                    finally
                        git binding.RepositoryPath [ "update-ref"; "-d"; stage ] |> ignore



    // Read-only source-remote verification. A temporary namespace is used so a
    // local worktree, source branch and remote-tracking refs are never changed.
    let private validTargetRef (targetRef: string) =
        not (isNull targetRef)
        && Regex.IsMatch(targetRef, "^refs/heads/[A-Za-z0-9][A-Za-z0-9_./-]*$")
        && not (targetRef.Contains(".."))
        && not (targetRef.EndsWith("/"))
        && targetRef <> "refs/heads/jarvis-orchestration"
        && not (targetRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal))

    let readRemoteTarget (binding: LedgerBinding) targetRef =
        if not (validTargetRef targetRef) then
            Error(InvalidBinding "Integration target must be a normal fully qualified source branch.")
        else
            match validate binding with
            | Error error -> Error error
            | Ok () ->
                match git binding.RepositoryPath
                        [ "ls-remote"; "--refs"; binding.RemoteName; targetRef ] with
                | Error error -> Error error
                | Ok result when result.ExitCode <> 0 ->
                    Error(RemoteUnavailable $"Cannot inspect remote integration target: {result.Stderr}")
                | Ok result when result.Stdout = "" ->
                    Error(SourceUnavailable $"Remote integration target is missing: {targetRef}")
                | Ok result ->
                    let lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    if lines.Length <> 1 then
                        Error(LedgerIntegrityError "Remote integration target is ambiguous.")
                    else
                        let values = lines[0].Split('\t')
                        if values.Length <> 2 || values[1] <> targetRef
                           || not (oidPattern.IsMatch values[0]) then
                            Error(LedgerIntegrityError "Remote returned a malformed target reference.")
                        else
                            let advertised = values[0]
                            let stage = $"refs/jarvis/integration-verify/{Guid.NewGuid():N}"
                            try
                                match git binding.RepositoryPath
                                        [ "fetch"; "--no-tags"; binding.RemoteName; $"{targetRef}:{stage}" ] with
                                | Error error -> Error error
                                | Ok fetched when fetched.ExitCode <> 0 ->
                                    Error(RemoteUnavailable $"Cannot fetch remote target: {fetched.Stderr}")
                                | Ok _ ->
                                    match run binding.RepositoryPath [ "rev-parse"; stage ] with
                                    | Ok actual when actual = advertised ->
                                        match verifySourceObject binding actual with
                                        | Ok () -> Ok actual
                                        | Error error -> Error error
                                    | Ok _ ->
                                        Error(SourceUnavailable "Integration target moved during remote verification.")
                                    | Error error -> Error error
                            finally
                                git binding.RepositoryPath [ "update-ref"; "-d"; stage ] |> ignore

    // Use ordinary Git merge-base for each review. A previously integrated
    // slice does not remain part of the next review merely because the task
    // itself is still in progress.
    let reviewMergeBase (binding: LedgerBinding) targetRef headOid =
        readRemoteTarget binding targetRef
        |> Result.bind (fun targetHead ->
            verifySourceObject binding headOid
            |> Result.bind (fun () ->
                match git binding.RepositoryPath [ "merge-base"; targetHead; headOid ] with
                | Ok output when output.ExitCode = 0 && oidPattern.IsMatch output.Stdout ->
                    Ok output.Stdout
                | Ok output ->
                    Error(SourceUnavailable $"Cannot find a review merge-base: {output.Stderr}")
                | Error error -> Error error))

    // True when a commit is contained in the observed merge result, whether
    // the integration used a fast-forward or an ordinary merge commit.
    let sourceContainsCommit (binding: LedgerBinding) ancestor descendant =
        verifySourceObject binding ancestor
        |> Result.bind (fun () -> verifySourceObject binding descendant)
        |> Result.bind (fun () -> isAncestor binding.RepositoryPath ancestor descendant)

    let verifySourceAncestry (binding: LedgerBinding) earlier later =
        match verifySourceObject binding earlier with
        | Error error -> Error error
        | Ok () ->
            match verifySourceObject binding later with
            | Error error -> Error error
            | Ok () ->
                match isAncestor binding.RepositoryPath earlier later with
                | Ok true -> Ok ()
                | Ok false -> Error(InvalidBinding "Source commit does not descend from the expected base.")
                | Error error -> Error error

    let private intentInput (binding: LedgerBinding) (intent: LedgerIntent) =
        json
            {| schemaVersion = 2
               projectId = string binding.ProjectId
               operationId = intent.OperationId.ToString("D")
               actor = intent.Actor
               kind = intent.Kind
               request = canonicalRequestJson intent.RequestJson |}

    let private intentHash binding intent = digest (intentInput binding intent)


    let findApplied (binding: LedgerBinding) (intent: LedgerIntent) =
        if intent.OperationId = Guid.Empty || not (validRequestJson intent.RequestJson) then
            Error(InvalidBinding "Missing operation ID or invalid canonical JSON intent.")
        else
            match fetch binding with
            | Error error -> Error error
            | Ok snapshot ->
                match readOperation binding snapshot.Commit intent.OperationId with
                | Error error -> Error error
                | Ok None -> Ok None
                | Ok(Some applied) when applied.RequestHash <> intentHash binding intent ->
                    Error OperationIdReused
                | Ok(Some applied) ->
                    Ok(Some
                        { Operation = applied; CurrentHead = snapshot.Commit; AlreadyApplied = true })

    // A transaction updates every task/attempt/agent/review file and its operation
    // record in one ledger tree. Its planner is re-run against each new remote tip.
    let transact (binding: LedgerBinding) (intent: LedgerIntent)
                 (plan: LedgerSnapshot -> Result<LedgerFileChange list, LedgerFailure>) =
        if intent.OperationId = Guid.Empty || String.IsNullOrWhiteSpace intent.Actor
           || String.IsNullOrWhiteSpace intent.Kind || String.IsNullOrWhiteSpace intent.RequestJson
           || encoding.GetByteCount(intent.RequestJson) > 1024 * 1024
           || not (validRequestJson intent.RequestJson) then
            Error(InvalidBinding "Invalid transaction intent or size.")
        else
            let requestHash = intentHash binding intent
            let repo = binding.RepositoryPath
            let rec attempt retry =
                match fetch binding with
                | Error error -> Error error
                | Ok snapshot ->
                    let head = snapshot.Commit
                    match readOperation binding head intent.OperationId with
                    | Error error -> Error error
                    | Ok(Some applied) when applied.RequestHash = requestHash ->
                        Ok { Operation = applied; CurrentHead = head; AlreadyApplied = true }
                    | Ok(Some _) -> Error OperationIdReused
                    | Ok None ->
                        match plan snapshot with
                        | Error error -> Error error
                        | Ok changes ->
                            let paths = changes |> List.map (fun change -> change.Path)
                            if List.isEmpty changes
                               || List.length paths <> (paths |> List.distinct |> List.length)
                               || changes |> List.exists (fun change ->
                                   not (ledgerPath.IsMatch change.Path) || change.Path.Contains("..")
                                   || isNull change.Content
                                   || encoding.GetByteCount(change.Content) >
                                       (if change.Path.EndsWith("/spec.html", StringComparison.Ordinal) || change.Path.EndsWith(".md", StringComparison.Ordinal) then 512 * 1024 else 64 * 1024)
                                   || (change.ExpectedBlobOid |> Option.exists (oidPattern.IsMatch >> not))) then
                                Error(InvalidBinding "Invalid or duplicate entity paths in ledger transaction.")
                            else
                                let validationFailure =
                                    changes
                                    |> List.tryPick (fun change ->
                                        match blobAt repo head change.Path with
                                        | Error error -> Some error
                                        | Ok current when current <> change.ExpectedBlobOid ->
                                            Some(StaleEntityVersion $"Ledger entity changed: {change.Path}")
                                        | _ -> None)
                                match validationFailure with
                                | Some error -> Error error
                                | None ->
                                    let originalResult =
                                        domainReceipt (intent.OperationId.ToString("D"))
                                            snapshot.Header.NextSequence intent.Kind changes
                                    let operation =
                                        { SchemaVersion = 2
                                          CanonicalRequestJson = intentInput binding intent
                                          OriginalResultJson = originalResult
                                          OriginalResultHash = digest originalResult
                                          OperationId = intent.OperationId.ToString("D")
                                          RequestHash = requestHash
                                          Sequence = snapshot.Header.NextSequence
                                          Type = intent.Kind
                                          Actor = intent.Actor
                                          Path = List.head paths
                                          BlobOid = ""
                                          Paths = paths |> List.toArray }
                                    let header = { snapshot.Header with NextSequence = snapshot.Header.NextSequence + 1L }
                                    let files =
                                        [ "ledger.json", json header
                                          opPath intent.OperationId, json operation ]
                                        @ (changes |> List.map (fun change -> change.Path, change.Content))
                                    match writeTree repo (Some head) files with
                                    | Error error -> Error error
                                    | Ok tree ->
                                        match commitTree repo tree (Some head)
                                                $"Jarvis {intent.Kind}\n\nJarvis-Operation-Id: {intent.OperationId:D}\nJarvis-Request-Hash: {requestHash}" with
                                        | Error error -> Error error
                                        | Ok proposed ->
                                            let pushed = git repo [ "push"; "--porcelain"; binding.RemoteName; $"{proposed}:{ledgerRef}" ]
                                            match fetch binding with
                                            | Error _ -> Error(OutcomeUnknown $"Cannot confirm operation {intent.OperationId:D}.")
                                            | Ok observed ->
                                                match readOperation binding observed.Commit intent.OperationId with
                                                | Error error -> Error error
                                                | Ok(Some applied) when applied.RequestHash <> requestHash ->
                                                    Error OperationIdReused
                                                | Ok(Some applied) ->
                                                    match isAncestor repo proposed observed.Commit with
                                                    | Ok true ->
                                                        Ok { Operation = applied
                                                             CurrentHead = observed.Commit
                                                             AlreadyApplied = false }
                                                    | Ok false ->
                                                        Ok { Operation = applied
                                                             CurrentHead = observed.Commit
                                                             AlreadyApplied = true }
                                                    | Error error -> Error error
                                                | Ok None ->
                                                    match pushed with
                                                    | Error _ -> Error(OutcomeUnknown $"Push outcome uncertain: {intent.OperationId:D}.")
                                                    | Ok output when output.ExitCode <> 0 && observed.Commit = head ->
                                                        Error(GitFailure $"Ledger push failed: {output.Stderr}")
                                                    | Ok _ when retry >= 7 -> Error LedgerContended
                                                    | _ ->
                                                        System.Threading.Thread.Sleep(contentionDelay retry)
                                                        attempt (retry + 1)
            attempt 0
