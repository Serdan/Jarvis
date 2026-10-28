module Client.LedgerStore

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// V2 canonical Git transport. Experimental MCP callers require an explicit local binding; V1 remains separate.
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
    { OperationId: string
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
    let private encoding = UTF8Encoding(false)
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
    let private oidPattern = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled)
    let private recordPath = Regex("^records/[A-Za-z0-9_-]+(/[A-Za-z0-9_.-]+)*[.]json$", RegexOptions.Compiled)
    let private ledgerPath = Regex("^(tasks/[A-Za-z0-9_-]+/(task[.]json|spec[.]html|attempts/[a-f0-9]{32}([.]json|/checkpoints/[0-9]+[.]json)|reviews/[a-f0-9]{32}/(request[.]json|revisions/[0-9]+[.]json))|agents/[A-Za-z0-9_-]+[.]json|records/[A-Za-z0-9_-]+(/[A-Za-z0-9_.-]+)*[.]json)$", RegexOptions.Compiled)
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

    let private execute (repo: string) (args: string list) (input: string option) (environment: (string * string) list) =
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
                    Ok
                        { ExitCode = gitProcess.ExitCode
                          Stdout = stdout.GetAwaiter().GetResult().TrimEnd('\n', '\r')
                          Stderr = stderr.GetAwaiter().GetResult() }
        with ex ->
            Error(GitFailure ex.Message)

    let private git repo args = execute repo args None []
    let private gitInput repo args input = execute repo args (Some input) []
    let private gitIndex repo index args = execute repo args None [ "GIT_INDEX_FILE", index ]

    let private requireSuccess label result =
        match result with
        | Error error -> Error error
        | Ok output when output.ExitCode = 0 -> Ok output.Stdout
        | Ok output -> Error(GitFailure $"{label}: {output.Stderr.Trim()}")

    let private run repo args = git repo args |> requireSuccess (String.concat " " args)

    let private validate (binding: LedgerBinding) =
        if not (Directory.Exists binding.RepositoryPath) then
            Error(InvalidBinding "Repository path does not exist.")
        elif not (remotePattern.IsMatch binding.RemoteName) || binding.RemoteName.Contains("..") then
            Error(InvalidBinding "Invalid remote name.")
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
            run repo [ "show"; $"{commit}:{path}" ]

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
                    if isNull (box operation) || operation.OperationId <> id.ToString("D") then
                        Error(LedgerIntegrityError "Corrupt deduplication record.")
                    else
                        Ok(Some operation)
                with ex ->
                    Error(LedgerIntegrityError $"Malformed operation record: {ex.Message}")

    let private recordHash (binding: LedgerBinding) (change: LedgerRecordChange) =
        json
            {| schemaVersion = 2
               projectId = string binding.ProjectId
               operationId = change.OperationId.ToString("D")
               operationType = "PutRecord"
               actor = change.Actor
               path = change.Path
               expectedBlob = (change.ExpectedBlobOid |> Option.defaultValue "")
               contentSha256 = digest change.Content |}
        |> digest

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
                                let operation =
                                    { OperationId = change.OperationId.ToString("D")
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
                                                    System.Threading.Thread.Sleep(min 800 (50 * (1 <<< retries)))
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

    let verifySourceObject (binding: LedgerBinding) (commit: string) =
        if isNull commit || not (oidPattern.IsMatch commit) then
            Error(InvalidBinding "Invalid source commit OID.")
        else
            match git binding.RepositoryPath [ "cat-file"; "-t"; commit ] with
            | Ok result when result.ExitCode = 0 && result.Stdout = "commit" -> Ok ()
            | _ -> Error(SourceUnavailable $"Source commit {commit} is not available on this device.")


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
                    | Ok output when output.ExitCode = 0 -> Error(InvalidBinding "Code review has an empty cumulative diff.")
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


    let private intentHash (binding: LedgerBinding) (intent: LedgerIntent) =
        json
            {| schemaVersion = 2
               projectId = string binding.ProjectId
               operationId = intent.OperationId.ToString("D")
               actor = intent.Actor
               kind = intent.Kind
               request = intent.RequestJson |}
        |> digest

    let findApplied (binding: LedgerBinding) (intent: LedgerIntent) =
        if intent.OperationId = Guid.Empty then Error(InvalidBinding "Missing transaction operation ID.")
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
           || encoding.GetByteCount(intent.RequestJson) > 16 * 1024 then
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
                                   || isNull change.Content || encoding.GetByteCount(change.Content) > 64 * 1024
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
                                    let operation =
                                        { OperationId = intent.OperationId.ToString("D")
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
                                                        System.Threading.Thread.Sleep(min 800 (50 * (1 <<< retry)))
                                                        attempt (retry + 1)
            attempt 0
