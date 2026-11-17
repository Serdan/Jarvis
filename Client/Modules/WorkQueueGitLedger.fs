module Client.WorkQueueGitLedger

open System
open System.Collections
open System.Diagnostics
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore

// WQ-02: isolated, non-activated canonical project Git CAS implementation.
// The existing task/orchestration handlers are intentionally not redirected here.
// Account-global admission, production clock attestation and migration are separate gates.

module internal CanonicalCodec =
    let private publicStatic = BindingFlags.Public ||| BindingFlags.Static
    let private mapOfSeq =
        typeof<Map<string, int>>.Assembly.GetType("Microsoft.FSharp.Collections.MapModule", true).GetMethods(publicStatic)
        |> Array.find (fun methodInfo ->
            methodInfo.Name = "OfSeq" && methodInfo.IsGenericMethodDefinition
            && methodInfo.GetGenericArguments().Length = 2)
    let private setOfSeq =
        typeof<Set<string>>.Assembly.GetType("Microsoft.FSharp.Collections.SetModule", true).GetMethods(publicStatic)
        |> Array.find (fun methodInfo ->
            methodInfo.Name = "OfSeq" && methodInfo.IsGenericMethodDefinition
            && methodInfo.GetGenericArguments().Length = 1)

    let rec private encodeType (shape: Type) (value: obj) : JsonNode =
        if shape = typeof<string> then JsonValue.Create(value :?> string) :> JsonNode
        elif shape = typeof<int> then JsonValue.Create(unbox<int> value) :> JsonNode
        elif shape = typeof<int64> then JsonValue.Create(unbox<int64> value) :> JsonNode
        elif shape = typeof<bool> then JsonValue.Create(unbox<bool> value) :> JsonNode
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Map<_,_>> then
            let arguments = shape.GetGenericArguments()
            if arguments.[0] <> typeof<string> then invalidOp "Only string-keyed canonical maps are supported."
            let result = JsonObject()
            let entries =
                (value :?> IEnumerable)
                |> Seq.cast<obj>
                |> Seq.map (fun entry ->
                    let entryType = entry.GetType()
                    entryType.GetProperty("Key").GetValue(entry) :?> string,
                    entryType.GetProperty("Value").GetValue(entry))
                |> Seq.sortWith (fun (a, _) (b, _) -> StringComparer.Ordinal.Compare(a, b))
            for key, item in entries do result.Add(key, encodeType arguments.[1] item)
            result :> JsonNode
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Set<_>> then
            let elementType = shape.GetGenericArguments().[0]
            let result = JsonArray()
            for item in value :?> IEnumerable do result.Add(encodeType elementType item)
            result :> JsonNode
        elif FSharpType.IsRecord shape then
            let result = JsonObject()
            let fields = FSharpType.GetRecordFields shape
            let values = FSharpValue.GetRecordFields value
            for field, fieldValue in Array.zip fields values |> Array.sortBy (fun (f, _) -> f.Name) do
                result.Add(field.Name, encodeType field.PropertyType fieldValue)
            result :> JsonNode
        elif FSharpType.IsUnion shape then
            let case, values = FSharpValue.GetUnionFields(value, shape)
            let result = JsonObject()
            result.Add("case", JsonValue.Create(case.Name))
            let elements = JsonArray()
            for field, item in Array.zip (case.GetFields()) values do
                elements.Add(encodeType field.PropertyType item)
            result.Add("fields", elements)
            result :> JsonNode
        else invalidOp ("Unsupported canonical contract type: " + shape.FullName)

    let rec private decodeType (shape: Type) (node: JsonNode) : obj =
        if isNull node then invalidOp "Unexpected null in canonical work record."
        elif shape = typeof<string> then box (node.GetValue<string>())
        elif shape = typeof<int> then box (node.GetValue<int>())
        elif shape = typeof<int64> then box (node.GetValue<int64>())
        elif shape = typeof<bool> then box (node.GetValue<bool>())
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Map<_,_>> then
            let arguments = shape.GetGenericArguments()
            if arguments.[0] <> typeof<string> then invalidOp "Unsupported canonical map key."
            let tupleType = FSharpType.MakeTupleType arguments
            let entries = node.AsObject() |> Seq.toArray
            let values = Array.CreateInstance(tupleType, entries.Length)
            for index = 0 to entries.Length - 1 do
                let entry = entries.[index]
                values.SetValue(
                    FSharpValue.MakeTuple(
                        [| box entry.Key; decodeType arguments.[1] entry.Value |], tupleType), index)
            mapOfSeq.MakeGenericMethod(arguments).Invoke(null, [| values |])
        elif shape.IsGenericType && shape.GetGenericTypeDefinition() = typedefof<Set<_>> then
            let elementType = shape.GetGenericArguments().[0]
            let elements = node.AsArray()
            let values = Array.CreateInstance(elementType, elements.Count)
            for index = 0 to elements.Count - 1 do
                values.SetValue(decodeType elementType elements.[index], index)
            setOfSeq.MakeGenericMethod([| elementType |]).Invoke(null, [| values |])
        elif FSharpType.IsRecord shape then
            let source = node.AsObject()
            let fields = FSharpType.GetRecordFields shape
            if source.Count <> fields.Length then invalidOp "Canonical record field count mismatch."
            let values =
                fields |> Array.map (fun field ->
                    if not (source.ContainsKey(field.Name)) then
                        invalidOp ("Missing canonical field " + field.Name)
                    decodeType field.PropertyType source.[field.Name])
            FSharpValue.MakeRecord(shape, values)
        elif FSharpType.IsUnion shape then
            let source = node.AsObject()
            if source.Count <> 2 then invalidOp "Canonical union shape mismatch."
            let tag = source.["case"].GetValue<string>()
            let unionCase =
                FSharpType.GetUnionCases shape
                |> Array.tryFind (fun candidate -> candidate.Name = tag)
                |> Option.defaultWith (fun () -> invalidOp ("Unknown canonical union case " + tag))
            let fields = unionCase.GetFields()
            let items = source.["fields"].AsArray()
            if items.Count <> fields.Length then invalidOp "Canonical union field count mismatch."
            let values =
                fields |> Array.mapi (fun index field -> decodeType field.PropertyType items.[index])
            FSharpValue.MakeUnion(unionCase, values)
        else invalidOp ("Unsupported canonical contract type: " + shape.FullName)

    let encode<'T> (value: 'T) =
        encodeType typeof<'T> (box value) |> fun node -> node.ToJsonString()

    let decode<'T> (raw: string) =
        let node = JsonNode.Parse(raw)
        decodeType typeof<'T> node :?> 'T

    let digest (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(text))
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

type ProjectEnvelope =
    { SchemaVersion: int
      ProjectId: string
      StateHash: string
      State: Project }

// Immutable project-CAS witnesses, not caller assertions.
type ProjectAdmissionRecord =
    { SchemaVersion: int
      ProjectId: string
      LaunchId: string
      AccountTokenId: string
      AdmissionOperationId: string
      Kind: string
      AbortOperationId: string }

type ProjectTerminalRecord =
    { SchemaVersion: int
      ProjectId: string
      LaunchId: string
      AccountTokenId: string
      AdmissionOperationId: string
      TerminalOperationId: string
      TerminalKind: string
      LaunchGeneration: int64 }

// Immutable verifier is stored in the SAME Git CAS as a successful admission;
// the launch state supplies the lifecycle. The preimage never enters Git.
type WorkCredentialVerifier =
    { SchemaVersion: int
      ProjectId: string
      LaunchId: string
      AccountTokenId: string
      SecretSha256: string }

// Existing WQ02 verifier records use this same SHA-256 shape. Only new
// issuance changes to UUIDv4; already-delivered Base64URL credentials remain
// valid against their original verifier (never as an identity fallback).
module WorkCredentialFormat =
    let isCanonicalUuidV4 (value: string) =
        match Guid.TryParseExact(value, "D") with
        | true, parsed ->
            parsed <> Guid.Empty
            && String.Equals(value, parsed.ToString("D"), StringComparison.Ordinal)
            && value.[14] = '4'
            && (value.[19] = '8' || value.[19] = '9'
                || value.[19] = 'a' || value.[19] = 'b')
        | _ -> false

    let isSupportedBearer (value: string) =
        isCanonicalUuidV4 value
        || (not (isNull value) && value.Length = 43
            && Regex.IsMatch(value, "^[A-Za-z0-9_-]{43}$"))

[<Sealed>]
type IssuedWorkCredential private (launchId: string, secret: string) =
    member _.LaunchId = launchId
    member _.VerifierSha256 = CanonicalCodec.digest secret
    // The trusted launcher supplies this value explicitly to the model at
    // bootstrap; the model includes it in every Work execution tool call.
    member _.RevealForExplicitToolArgument() = secret
    // Original disposable fake-provider compatibility (not implicit injection).
    member _.RevealForPrivateDelivery() = secret
    override _.ToString() = "IssuedWorkCredential(<redacted>)"
    static member Issue(launchId: string) =
        match Guid.TryParse launchId with
        | true, id when id <> Guid.Empty ->
            // .NET 11 Guid.NewGuid uses the platform CSPRNG for UUIDv4.
            // Never reuse the distinct public launch ID as a bearer.
            let rec fresh () =
                let candidate = Guid.NewGuid().ToString("D")
                if String.Equals(candidate, launchId, StringComparison.OrdinalIgnoreCase)
                then fresh () else candidate
            let secret = fresh ()
            IssuedWorkCredential(launchId, secret)
        | _ -> invalidArg (nameof launchId) "Expected nonempty launch UUID."

type WorkMutation =
    | PublishStage of WorkStage
    | ProduceEvent of QueueProducerEvent
    | PostMessage of SubjectMessage * WorkStage option
    | AdmitProject of Launch
    | AdmitCredentialProject of launch: Launch * verifierSha256: string
    | AbortUnboundProjectAdmission of Launch * originalAdmissionOperationId: string
    | DispatchOnce of launchId: string * outcome: DispatchState
    | InitializeAgent of launchId: string * sessionKey: string
    | InitializeCredential of launchId: string
    | Heartbeat of launchId: string * leaseGeneration: int
    | StopStalled of launchId: string * leaseGeneration: int * stalledAfter: int64
    | ClaimNext of launchId: string
    | ClaimVerifiedWork of launchId: string * workId: string * expectedStageGeneration: int * resultingClaimGeneration: int
    // FrozenReview domain finalization requires a trusted, same-CAS original
    // WQ05 Accepted-review witness; this variant cannot run without it.
    | FinalizeVerifiedReview of launchId: string * workId: string * expectedClaimGeneration: int * immutableReceipt: string
    | CompleteOrdinary of launchId: string * workId: string
    | CompleteMessage of launchId: string * workId: string * messageId: string * handlerGeneration: int * replyId: string
    | ClaimAncillary of launchId: string * messageId: string
    | CompleteAncillary of launchId: string * messageId: string * handlerGeneration: int * replyId: string
    | AssignPriority of workId: string * priority: int * reason: string
    | SetHardPrerequisite of workId: string * prerequisite: string
    | RemoveHardPrerequisite of workId: string * prerequisite: string
    | CancelUnclaimed of workId: string * reason: string
    | TerminalizeNoClaim of launchId: string
    | YieldReadOnly of launchId: string * workId: string * expectedClaimGeneration: int
    | CheckpointReadOnly of launchId: string * workId: string * expectedClaimGeneration: int * summarySha256: string
    | CompleteVerifiedReadOnly of launchId: string * workId: string * expectedClaimGeneration: int * terminalEvidenceSha256: string
    | YieldSource of SourceCheckpoint
    | MarkSourceRecoveryBlocked of launchId: string * workId: string * expectedClaimGeneration: int * reason: string
    | ResolveReadOnlyGate of launchId: string * workId: string * expectedClaimGeneration: int
    | ResolveAncillaryGate of launchId: string * messageId: string * workId: string * expectedLaunchVersion: int * expectedClaimGeneration: int * expectedGraphVersion: int
    // Integration, account and live source-job journal transitions remain
    // unavailable: a production cutover requires a separate authorized bridge.

// Work-native actions never accept a caller-controlled agent ID or role.
// The credential selects the only effective launch before a mutation is made.
type CredentialWorkAction =
    | FirstConnect
    | ClaimPrimary
    | HeartbeatLease of leaseGeneration: int
    | Checkpoint of workId: string * claimGeneration: int * summarySha256: string
    | CompleteReadOnly of workId: string * claimGeneration: int * evidenceSha256: string
    | FinishOrdinary of workId: string
    | ClaimMessage of messageId: string
    | FinishMessage of workId: string * messageId: string * handlerGeneration: int * replyId: string

// Immutable, inspectable provenance in the same project Git CAS as the work stage.
// This is an observation, not a global permission or a scheduler launch token.
type CanonicalWorkOrigin =
    { SchemaVersion: int
      ProjectId: string
      WorkId: string
      SourceHead: string
      SourceHighWater: int64
      SourceOperationId: string
      SourceOperationSequence: int64
      SourceOperationKind: string
      SourceOperationActor: string
      SourceRequestHash: string
      SourceOriginHead: string
      SourceEntityPath: string
      SourceEntityBlobOid: string
      SourceEntitySha256: string
      SourceSelector: string
      SourceRef: string
      SourceCommit: string
      RoleId: string
      RoleVersion: int
      AccountCreditStatus: string
      Ambiguity: string }

module WorkQueueGitLedger =
    // Test-only seam at the publication/acknowledgement boundary. The real
    // project CAS and immutable witnesses commit first; the caller then loses
    // its reply. No production scheduler or Git transport is altered.
    let private dropPublishedAck = System.Threading.AsyncLocal<(Guid -> bool) option>()

    let internal withTestPublishedAckLoss predicate operation =
        let previous = dropPublishedAck.Value
        dropPublishedAck.Value <- Some predicate
        try operation ()
        finally dropPublishedAck.Value <- previous

    [<Literal>]
    let RecordPath = "records/work-queue/project.json"

    let admissionPath launchId =
        "records/work-queue/admissions/" + CanonicalCodec.digest launchId + ".json"

    let credentialPath launchId =
        "records/work-queue/credentials/" + CanonicalCodec.digest launchId + ".json"

    let terminalPath launchId =
        "records/work-queue/terminals/" + CanonicalCodec.digest launchId + ".json"

    let private readCanonical<'T> binding snapshot path =
        LedgerStore.readAt binding snapshot path
        |> Result.bind (function
            | None, None -> Ok None
            | Some raw, Some oid ->
                try
                    let decoded = CanonicalCodec.decode<'T> raw
                    if CanonicalCodec.encode decoded <> raw then
                        Error(LedgerIntegrityError "Noncanonical WQ-07 witness.")
                    else Ok(Some(decoded, oid))
                with ex -> Error(LedgerIntegrityError ("Malformed WQ-07 witness: " + ex.Message))
            | _ -> Error(LedgerIntegrityError "Incomplete WQ-07 witness object."))

    let originPath workId =
        "records/work-queue/origins/" + CanonicalCodec.digest workId + ".json"

    let readOriginAt binding snapshot workId =
        readCanonical<CanonicalWorkOrigin> binding snapshot (originPath workId)

    let readAdmissionAt binding snapshot launchId =
        readCanonical<ProjectAdmissionRecord> binding snapshot (admissionPath launchId)

    let readCredentialAt binding snapshot launchId =
        readCanonical<WorkCredentialVerifier> binding snapshot (credentialPath launchId)

    let readTerminalAt binding snapshot launchId =
        readCanonical<ProjectTerminalRecord> binding snapshot (terminalPath launchId)

    let private failure (reason: Rejection) =
        match reason with
        | Rejection.CasConflict -> StaleEntityVersion "Work ledger project version changed."
        | Rejection.OperationIdReused -> OperationIdReused
        | Rejection.IncompleteCoverage reason -> LedgerIntegrityError ("Incomplete work ledger: " + reason)
        | other -> InvalidBinding ("Rejected WQ-02 transition: " + string other)

    let private envelope binding project =
        { SchemaVersion = 1
          ProjectId = (binding: LedgerBinding).ProjectId.ToString("D")
          StateHash = CanonicalCodec.digest (CanonicalCodec.encode project)
          State = project }

    let private content binding project =
        CanonicalCodec.encode (envelope binding project)

    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    // WQ-03 is still an isolated source-repository adapter. The approved
    // URLs in the trusted project binding, never caller-supplied URLs, define
    // the repository identity and the authoritative remote used for proof.
    let sourceRepositoryId (binding: LedgerBinding) =
        CanonicalCodec.digest (
            "wq03-source-git-v1\n" + binding.ApprovedFetchUrl + "\n" + binding.ApprovedPushUrl)

    // Successor role executions read the canonical immutable source context,
    // never the prior provider conversation or prior mutable checkout.
    let latestSourceHandoff (project: Project) workId =
        project.Receipts
        |> Map.toSeq
        |> Seq.choose (fun (_, payload) ->
            match payload with
            | ProjectOperationPayload.SourceHandoff checkpoint
                when checkpoint.WorkId = workId -> Some checkpoint
            | _ -> None)
        |> Seq.sortByDescending (fun checkpoint ->
            checkpoint.LineageGeneration, checkpoint.ClaimGeneration)
        |> Seq.tryHead

    let private sourceReject reason = InvalidBinding ("WQ-03 source handoff: " + reason)

    let private git (directory: string) (arguments: string list) =
        try
            use runner = new Process()
            runner.StartInfo.FileName <- "git"
            runner.StartInfo.WorkingDirectory <- directory
            runner.StartInfo.UseShellExecute <- false
            runner.StartInfo.RedirectStandardOutput <- true
            runner.StartInfo.RedirectStandardError <- true
            runner.StartInfo.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
            for argument in arguments do
                runner.StartInfo.ArgumentList.Add(argument)
            if not (runner.Start()) then Error(sourceReject "could not start Git verifier")
            else
                let standardOutput = runner.StandardOutput.ReadToEndAsync()
                let standardError = runner.StandardError.ReadToEndAsync()
                if not (runner.WaitForExit(20000)) then
                    runner.Kill(true)
                    runner.WaitForExit()
                    Error(sourceReject "Git verifier timed out; remote outcome is unknown")
                else
                    standardError.GetAwaiter().GetResult() |> ignore
                    Ok(runner.ExitCode, standardOutput.GetAwaiter().GetResult().Trim())
        with _ -> Error(sourceReject "Git verifier failed; source outcome is unknown")

    let private successfulGit binding args description =
        git (binding: LedgerBinding).RepositoryPath args
        |> Result.bind (function
            | 0, output -> Ok output
            | _ -> Error(sourceReject description))

    let private validSourceRefPart (part: string) =
        nonBlank part
        && Regex.IsMatch(part, "^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$")
        && not (part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))

    let private validGitSha (sha: string) =
        nonBlank sha && Regex.IsMatch(sha, "^([a-f0-9]{40}|[a-f0-9]{64})$")

    // Use ls-remote against the approved URL, not local origin-tracking refs:
    // another clone or a lost push reply must yield the same independent fact.
    let private verifyRemoteTip binding refName expectedSha =
        successfulGit binding
            [ "ls-remote"; "--refs"; (binding: LedgerBinding).ApprovedFetchUrl; refName ]
            "cannot independently read the approved source remote"
        |> Result.bind (fun output ->
            match output.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) with
            | [| line |] ->
                match line.Split('\t') with
                | [| sha; exactRef |] when exactRef = refName && sha = expectedSha -> Ok()
                | [| sha; exactRef |] when exactRef = refName ->
                    Error(sourceReject (sprintf
                        "remote ref %s points to %s, expected %s; inspect/fetch before retrying a non-force push or recording recovery"
                        refName sha expectedSha))
                | _ -> Error(sourceReject ("unexpected remote ref response for " + refName))
            | [||] -> Error(sourceReject (sprintf
                "remote ref %s is absent (expected %s); original push may be pending or failed: inspect its operation before retry"
                refName expectedSha))
            | _ -> Error(sourceReject ("ambiguous remote response for " + refName)))

    // First-generation stages must have a trustworthy source binding and a
    // real pinned base reachable on the approved target, before any claim.
    let private verifyInitialSource binding (lineage: SourceLineage) =
        let approvedSourceCheckout =
            match LedgerStore.sourceRepositoryIdentity binding with
            | Ok identity ->
                identity.FetchUrl = (binding: LedgerBinding).ApprovedFetchUrl
                && identity.PushUrl = binding.ApprovedPushUrl
            | Error _ -> false
        if not approvedSourceCheckout
           || lineage.RepositoryId <> sourceRepositoryId binding
           || not (validGitSha lineage.PinnedBaseCommit)
           || not (lineage.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal)) then
            Error(sourceReject "source identity, target or pinned base is invalid")
        else
            successfulGit binding [ "check-ref-format"; lineage.TargetRef ]
                "invalid source target ref"
            |> Result.bind (fun _ ->
                successfulGit binding
                    [ "ls-remote"; "--refs"; (binding: LedgerBinding).ApprovedFetchUrl; lineage.TargetRef ]
                    "cannot independently read approved source target")
            |> Result.bind (fun output ->
                match output.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) with
                | [| line |] ->
                    match line.Split('\t') with
                    | [| sha; reference |] when reference = lineage.TargetRef && validGitSha sha ->
                        // Only Git object fetch: do not modify HEAD or any branch.
                        successfulGit binding
                            [ "fetch"; "--no-tags"; "--no-write-fetch-head";
                              (binding: LedgerBinding).ApprovedFetchUrl; lineage.TargetRef ]
                            "approved source target cannot be fetched"
                        |> Result.bind (fun _ ->
                            successfulGit binding
                                [ "cat-file"; "-e"; lineage.PinnedBaseCommit + "^{commit}" ]
                                "pinned base is not a local commit")
                        |> Result.bind (fun _ ->
                            successfulGit binding
                                [ "cat-file"; "-e"; sha + "^{commit}" ]
                                "approved target SHA is not a commit")
                        |> Result.bind (fun _ ->
                            successfulGit binding
                                [ "merge-base"; "--is-ancestor"; lineage.PinnedBaseCommit; sha ]
                                "pinned source base is not reachable from approved target"
                            |> Result.map ignore)
                    | _ -> Error(sourceReject "approved target ref identity does not match")
                | _ -> Error(sourceReject "approved target ref missing or ambiguous"))

    let private verifySourceContinuation binding (lineage: SourceLineage) =
        verifyInitialSource binding lineage
        |> Result.bind (fun () ->
            match lineage.ReachableRef, lineage.ContinuationSha with
            | None, None -> Ok()
            | Some remoteRef, Some sha when validGitSha sha ->
                verifyRemoteTip binding remoteRef sha
            | _ -> Error(sourceReject "source continuation lacks complete approved-remote provenance"))

    // ClaimNext is immediately actionable. Require a clean, detached, linked
    // worktree already pinned to this exact launch/claim generation; never
    // allow a stale clone or the predecessor's mutable checkout to claim.
    let internal verifySourceClaimCheckout binding launchId (work: WorkStage) =
        match work.SourceLineage with
        | None -> Error(sourceReject "source claim has no lineage")
        | Some lineage ->
            let expectedSha = lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit
            let expectedName = sprintf "wq03-%s-%s-g%d" work.Id launchId work.ClaimGeneration
            if not (validSourceRefPart work.Id && validSourceRefPart launchId) then
                Error(sourceReject "invalid source claim worktree identity")
            else
                verifySourceContinuation binding lineage
                |> Result.bind (fun () ->
                    successfulGit binding [ "rev-parse"; "--show-toplevel" ]
                        "cannot resolve source worktree")
                |> Result.bind (fun root ->
                    let selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
                    let configured =
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath((binding: LedgerBinding).RepositoryPath))
                    if selected <> configured || Path.GetFileName(selected) <> expectedName then
                        Error(sourceReject "source claim requires its generation-scoped worktree")
                    else Ok selected)
                |> Result.bind (fun selected ->
                    successfulGit binding [ "rev-parse"; "--absolute-git-dir" ]
                        "cannot verify linked source worktree"
                    |> Result.bind (fun gitDir ->
                        successfulGit binding [ "rev-parse"; "--git-common-dir" ]
                            "cannot verify common source object store"
                        |> Result.bind (fun common ->
                            let gitPath = Path.GetFullPath(gitDir)
                            let commonPath = Path.GetFullPath(
                                if Path.IsPathRooted(common) then common
                                else Path.Combine(selected, common))
                            if gitPath = commonPath then
                                Error(sourceReject "source claim must use an isolated linked worktree")
                            else Ok())))
                |> Result.bind (fun () ->
                    successfulGit binding [ "worktree"; "list"; "--porcelain" ]
                        "cannot inspect source worktree registration"
                    |> Result.bind (fun output ->
                        let registered =
                            output.Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
                            |> Array.exists (fun block ->
                                let lines = block.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                                lines |> Array.contains ("worktree " + (binding: LedgerBinding).RepositoryPath)
                                && lines |> Array.contains ("HEAD " + expectedSha)
                                && lines |> Array.contains "detached"
                                && not (lines |> Array.exists (fun line -> line.StartsWith("prunable ", StringComparison.Ordinal))))
                        if registered then Ok()
                        else Error(sourceReject "generation checkout is not pinned and registered")))
                |> Result.bind (fun () ->
                    successfulGit binding [ "rev-parse"; "HEAD" ] "source checkout HEAD missing"
                    |> Result.bind (fun head ->
                        if head = expectedSha then Ok()
                        else Error(sourceReject "source checkout HEAD differs from pinned generation")))
                |> Result.bind (fun () ->
                    successfulGit binding
                        [ "status"; "--porcelain"; "--untracked-files=all"; "--ignore-submodules=none" ]
                        "source checkout status unknown"
                    |> Result.bind (fun dirty ->
                        if dirty = "" then Ok()
                        else Error(sourceReject "dirty generation checkout; RecoveryBlocked required")))

    // Preserve what the original push reported without confusing its reply
    // with the observed remote state. For a lost acknowledgement, a fresh
    // ls-remote can establish that the expected continuation is currently
    // reachable; the unknown dispatch outcome remains visible to successors.
    // This inert adapter does not claim that host refs are immutable or jobs
    // have globally terminated. Unresolved work is recorded as RecoveryBlocked.
    let private validSourcePushObservation (checkpoint: SourceCheckpoint) =
        nonBlank checkpoint.PushOperationId
        && checkpoint.PushOperationId.Length <= 256
        && nonBlank checkpoint.PushCommand
        && checkpoint.PushCommand.Length <= 2048
        && validGitSha checkpoint.ObservedRemoteSha
        && checkpoint.ObservedRemoteSha = checkpoint.ContinuationSha
        && nonBlank checkpoint.RecoveryNote
        && checkpoint.RecoveryNote.Length <= 8192
        && (match checkpoint.PushOutcome with
            | PushOutcome.Unknown -> true
            | PushOutcome.Observed sha -> sha = checkpoint.ObservedRemoteSha
            | PushOutcome.DefinitiveNoEffect -> false)

    // No caller-controlled "reachability verified" boolean is accepted.
    // The source worktree must contain the committed WIP, be entirely clean,
    // and show ancestry from both the pinned base and previous continuation.
    // Mutating process-journal coverage remains a separate cutover gate.
    let private verifySourceHandoff binding (work: WorkStage) (checkpoint: SourceCheckpoint) =
        match work.SourceLineage with
        | None -> Error(sourceReject "source-writing stage has no lineage")
        | Some lineage ->
            let expectedRef =
                sprintf "refs/heads/jarvis-wip/%s/%s-g%d"
                    checkpoint.WorkId checkpoint.LaunchId checkpoint.ClaimGeneration
            if not (validSourceRefPart checkpoint.WorkId
                    && validSourceRefPart checkpoint.LaunchId)
               || checkpoint.ContinuationRef <> expectedRef
               || not (validGitSha checkpoint.ContinuationSha)
               || not (validGitSha lineage.PinnedBaseCommit)
               || checkpoint.RepositoryId <> sourceRepositoryId binding
               || not (validSourcePushObservation checkpoint)
               || checkpoint.Summary.Length > 8192
               || checkpoint.NextAction.Length > 8192
               || checkpoint.TestEvidence.Length > 8192 then
                Error(sourceReject "invalid source identity, generation-scoped ref or checkpoint fields")
            else
                verifySourceContinuation binding lineage
                |> Result.bind (fun () ->
                    successfulGit binding [ "rev-parse"; "--verify"; "HEAD" ] "missing source HEAD"
                    |> Result.bind (fun head ->
                        if head <> checkpoint.ContinuationSha then
                            Error(sourceReject "source worktree HEAD does not match WIP commit")
                        else Ok()))
                |> Result.bind (fun () ->
                    successfulGit binding
                        [ "status"; "--porcelain"; "--untracked-files=all"; "--ignore-submodules=none" ]
                        "cannot inspect source worktree cleanliness"
                    |> Result.bind (fun dirty ->
                        if dirty = "" then Ok()
                        else Error(sourceReject "dirty or untracked source files; RecoveryBlocked required")))
                |> Result.bind (fun () ->
                    successfulGit binding
                        [ "cat-file"; "-e"; checkpoint.ContinuationSha + "^{commit}" ]
                        "WIP SHA is not a local commit"
                    |> Result.map ignore)
                |> Result.bind (fun () ->
                    let predecessor =
                        lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit
                    successfulGit binding
                        [ "merge-base"; "--is-ancestor"; predecessor; checkpoint.ContinuationSha ]
                        "WIP is not descended from the previous immutable source"
                    |> Result.map ignore)
                |> Result.bind (fun () ->
                    successfulGit binding
                        [ "merge-base"; "--is-ancestor"; lineage.PinnedBaseCommit; checkpoint.ContinuationSha ]
                        "WIP is not descended from the pinned source base"
                    |> Result.map ignore)
                |> Result.bind (fun () ->
                    verifyRemoteTip binding checkpoint.ContinuationRef checkpoint.ContinuationSha)

    let private validStagePayload (stage: WorkStage) =
        nonBlank stage.Id
        && nonBlank stage.ProjectId
        && nonBlank stage.SubjectId
        && nonBlank stage.QueueId
        && (match stage.Kind with WorkKind.RoleMessage id -> nonBlank id | _ -> true)
        && (stage.Dependencies |> Set.forall nonBlank)
        && (stage.WaivedDependencies |> Map.forall (fun id receipt -> nonBlank id && nonBlank receipt))
        && (stage.SourceLineage
            |> Option.forall (fun lineage ->
                nonBlank lineage.RepositoryId
                && nonBlank lineage.TaskId
                && nonBlank lineage.TargetRef
                && nonBlank lineage.LineageId
                && nonBlank lineage.PinnedBaseCommit
                && lineage.Generation > 0
                && (lineage.ReachableRef |> Option.forall nonBlank)
                && (lineage.ContinuationSha |> Option.forall nonBlank)))

    let private validMessagePayload (message: SubjectMessage) =
        nonBlank message.Id
        && nonBlank message.Body
        && (match message.Subject with
            | Subject.Task id | Subject.Role id -> nonBlank id)
        && (message.WorkId |> Option.forall nonBlank)
        && (match message.State with MessageState.Handled reply -> nonBlank reply | _ -> true)

    // The state is an authoritative *single-tree* projection of its own WQ-02
    // events; these checks do not replace verification of external producer
    // journals or account-global authority at production cutover.
    let private structurallyCoherent (binding: LedgerBinding) (state: Project) =
        let projectId = binding.ProjectId.ToString("D")
        let workOwner id (work: WorkStage) =
            match work.State with
            | WorkState.Claimed launchId ->
                state.Launches.TryFind launchId
                |> Option.exists (fun launch ->
                    launch.PrimaryWorkId = Some id
                    || (match work.Kind with
                        | WorkKind.RoleMessage messageId ->
                            launch.AncillaryMessages.Contains messageId
                        | _ -> false))
            | _ -> true
        let validMessage id (message: SubjectMessage) =
            let subjectWork =
                match message.Subject, message.WorkId with
                | Subject.Task _, None -> true
                | Subject.Role roleId, Some workId ->
                    state.Works.TryFind workId
                    |> Option.exists (fun work ->
                        work.Kind = WorkKind.RoleMessage id
                        && work.QueueId = "role:" + roleId
                        && work.SubjectId = roleId
                        && work.StageGeneration > 0
                        && work.SourceLineage.IsNone && work.Push.IsNone)
                | _ -> false
            let handler =
                match message.State with
                | MessageState.Pending | MessageState.Handled _ -> true
                | MessageState.ClaimedPrimary launchId ->
                    message.WorkId
                    |> Option.exists (fun workId ->
                        state.Launches.TryFind launchId
                        |> Option.exists (fun launch ->
                            launch.PrimaryWorkId = Some workId
                            && state.Works.[workId].State = WorkState.Claimed launchId))
                | MessageState.ClaimedAncillary launchId ->
                    state.Launches.TryFind launchId
                    |> Option.exists (fun launch -> launch.AncillaryMessages.Contains id)
            validMessagePayload message && subjectWork && handler && message.HandlerGeneration >= 0
        let validLaunchPhase (launch: Launch) =
            match launch.State with
            | LaunchState.ReservedUnregistered ->
                launch.Version = 0
                && launch.DispatchCount = 0
                && launch.Lease.IsNone
                && not launch.EverClaimed
                && launch.PrimaryWorkId.IsNone
            | LaunchState.SubmittedUnregistered _ ->
                launch.Version >= 1
                && launch.DispatchCount = 1
                && launch.Lease.IsNone
                && not launch.EverClaimed
                && launch.PrimaryWorkId.IsNone
            | LaunchState.Registered (agentUuid, sessionKey) ->
                launch.Version >= 2
                && launch.DispatchCount = 1
                && not (String.IsNullOrWhiteSpace agentUuid)
                && not (String.IsNullOrWhiteSpace sessionKey)
                && (launch.Lease
                    |> Option.exists (fun lease -> lease.Generation > 0 && lease.LastSeenAt >= 0L))
            | LaunchState.DemandInvalidatedPendingTerminal prior ->
                match prior with
                | LaunchState.ReservedUnregistered
                | LaunchState.SubmittedUnregistered _
                | LaunchState.Registered _ -> true
                | _ -> false
            | LaunchState.TerminalPendingRelease receipt ->
                not (String.IsNullOrWhiteSpace receipt)
        let validRecordedHandoff operationId (checkpoint: SourceCheckpoint) =
            nonBlank checkpoint.WorkId
            && validSourcePushObservation checkpoint
            && validSourceRefPart checkpoint.WorkId
            && validSourceRefPart checkpoint.LaunchId
            && nonBlank checkpoint.AgentUuid
            && nonBlank checkpoint.Summary && checkpoint.Summary.Length <= 8192
            && nonBlank checkpoint.NextAction && checkpoint.NextAction.Length <= 8192
            && nonBlank checkpoint.TestEvidence && checkpoint.TestEvidence.Length <= 8192
            && validGitSha checkpoint.ContinuationSha
            && checkpoint.ContinuationRef =
                sprintf "refs/heads/jarvis-wip/%s/%s-g%d"
                    checkpoint.WorkId checkpoint.LaunchId checkpoint.ClaimGeneration
            && checkpoint.ClaimGeneration > 0
            && checkpoint.LineageGeneration > 0
            && (state.Works.TryFind checkpoint.WorkId
                |> Option.exists (fun work ->
                    work.Kind = WorkKind.Implementation
                    && work.StageGeneration = checkpoint.StageGeneration
                    && work.ClaimGeneration > checkpoint.ClaimGeneration
                    && (work.SourceLineage
                        |> Option.exists (fun lineage ->
                            lineage.RepositoryId = checkpoint.RepositoryId
                            && lineage.TaskId = checkpoint.TaskId
                            && lineage.TargetRef = checkpoint.TargetRef
                            && lineage.LineageId = checkpoint.LineageId
                            && lineage.PinnedBaseCommit = checkpoint.PinnedBaseCommit
                            && lineage.Generation > checkpoint.LineageGeneration))))
            && (state.Launches.TryFind checkpoint.LaunchId
                |> Option.exists (fun launch ->
                    launch.EverClaimed
                    && launch.PrimaryWorkId = Some checkpoint.WorkId
                    && launch.State = LaunchState.TerminalPendingRelease("source-yield:" + operationId)))
        state.Version >= 0
        && state.Version = state.Receipts.Count
        && state.Receipts |> Map.forall (fun operationId payload ->
            match payload with
            | ProjectOperationPayload.SourceHandoff checkpoint -> validRecordedHandoff operationId checkpoint
            | ProjectOperationPayload.AssignPriority decision ->
                state.Works.ContainsKey decision.WorkId
                && decision.ProjectVersion > 0
                && decision.ProjectVersion <= state.Version
                && not (String.IsNullOrWhiteSpace decision.Reason)
                && decision.Reason.Length <= 1024
                && decision.Reason = decision.Reason.Trim()
            | _ -> true)
        && state.GraphVersion >= 0
        && state.NextAvailability >= 0L
        && state.Works |> Map.forall (fun id work ->
            id = work.Id && work.ProjectId = projectId && validStagePayload work
            && work.StageGeneration > 0 && work.ClaimGeneration >= 0
            && work.AvailableSequence >= 0L
            && work.AvailableSequence <= state.NextAvailability
            && work.Dependencies |> Set.forall state.Works.ContainsKey
            // A source continuation must have been published by a real
            // versioned SourceHandoff, not smuggled in through PublishStage.
            && (work.SourceLineage
                |> Option.forall (fun lineage ->
                    match lineage.ReachableRef, lineage.ContinuationSha with
                    | None, None -> true
                    | Some reference, Some sha ->
                        state.Receipts
                        |> Map.exists (fun _ receipt ->
                            match receipt with
                            | ProjectOperationPayload.SourceHandoff proof ->
                                proof.WorkId = work.Id
                                && proof.ContinuationRef = reference
                                && proof.ContinuationSha = sha
                                && proof.RepositoryId = lineage.RepositoryId
                                && proof.LineageGeneration < lineage.Generation
                            | _ -> false)
                    | _ -> false))
            && (match work.Gate with
                | GateState.CompositionHold (unitId, specVersion) ->
                    nonBlank unitId && specVersion > 0
                    && work.Kind = WorkKind.Planning
                    && work.State = WorkState.Blocked
                    && work.SourceLineage.IsNone && work.Push.IsNone
                | GateState.SourceRecoveryBlocked reason ->
                    nonBlank reason && work.SourceLineage.IsSome && work.State <> WorkState.Available
                | _ -> true)
            // This frozen adapter has no authority-backed waiver mutation.
            && work.WaivedDependencies.IsEmpty
            && workOwner id work)
        && state.Messages |> Map.forall (fun id message ->
            id = message.Id && validMessage id message)
        && state.Launches |> Map.forall (fun id launch ->
            id = launch.Id && launch.ProjectId = projectId
            && launch.Version >= 0 && launch.DispatchCount >= 0
            && launch.RoleVersion > 0
            && not (String.IsNullOrWhiteSpace launch.RoleId)
            && not (String.IsNullOrWhiteSpace launch.AccountTokenId)
            && validLaunchPhase launch
            && (launch.PrimaryWorkId
                |> Option.forall state.Works.ContainsKey)
            && (launch.AncillaryMessages
                |> Set.forall (fun messageId ->
                    state.Messages.TryFind messageId
                    |> Option.exists (fun message ->
                        message.State = MessageState.ClaimedAncillary launch.Id))))

    // Check the exact proposed Git bytes against their decoded projection
    // before granting the CAS writer permission to publish them.
    let internal validatedContent binding (state: Project) =
        try
            if not (structurallyCoherent binding state) then
                Error(InvalidBinding "WQ-02 canonical candidate is structurally incomplete.")
            else
                let raw = content binding state
                let decoded = CanonicalCodec.decode<ProjectEnvelope> raw
                if decoded.SchemaVersion <> 1
                   || decoded.ProjectId <> (binding: LedgerBinding).ProjectId.ToString("D")
                   || decoded.State <> state
                   || decoded.StateHash <> CanonicalCodec.digest (CanonicalCodec.encode decoded.State)
                   || CanonicalCodec.encode decoded <> raw
                   || not (structurallyCoherent binding decoded.State) then
                    Error(InvalidBinding "WQ-02 canonical candidate failed roundtrip validation.")
                else Ok raw
        with ex ->
            Error(InvalidBinding ("WQ-02 malformed canonical candidate: " + ex.Message))

    let private validateMutationPayload mutation =
        try
            let raw = CanonicalCodec.encode mutation
            let decoded = CanonicalCodec.decode<WorkMutation> raw
            if decoded <> mutation || CanonicalCodec.encode decoded <> raw then
                Error(InvalidBinding "WQ-02 mutation payload failed canonical roundtrip.")
            else Ok()
        with ex ->
            Error(InvalidBinding ("WQ-02 malformed mutation payload: " + ex.Message))

    let private witnessNewStage (state: Project) now (stage: WorkStage) =
        // Caller-supplied AvailableAt/Sequence are never scheduling authority.
        // A hard-edge-blocked stage receives its FIFO position only on wake.
        let unmet =
            stage.Dependencies
            |> Set.exists (fun prereq ->
                not (stage.WaivedDependencies.ContainsKey prereq
                    || Contracts.isSatisfied state prereq))
        if stage.State = WorkState.Available && not unmet then
            { stage with
                AvailableAt = now
                AvailableSequence = state.NextAvailability + 1L }
        else
            { stage with AvailableAt = 0L; AvailableSequence = 0L }

    let private validInitialStage (stage: WorkStage) =
        validStagePayload stage
        // New implementation stages cannot fabricate a predecessor WIP.
        // Only a canonical SourceHandoff transition advances continuation.
        && (match stage.Kind, stage.SourceLineage with
            | WorkKind.Implementation, Some lineage ->
                lineage.ReachableRef.IsNone && lineage.ContinuationSha.IsNone
            | WorkKind.Implementation, None -> false
            | _ -> true)
        && (stage.State = WorkState.Available || stage.State = WorkState.Blocked)
        && stage.StageGeneration > 0
        && stage.ClaimGeneration = 0
        && stage.Gate = GateState.Clear
        && stage.Domain = DomainState.Undecided
        && stage.Push.IsNone
        && not stage.IntegrationAuthorized
        && stage.WaivedDependencies.IsEmpty

    let private validInitialLaunch (binding: LedgerBinding) (launch: Launch) =
        launch.ProjectId = binding.ProjectId.ToString("D")
        && not (String.IsNullOrWhiteSpace launch.Id)
        && not (String.IsNullOrWhiteSpace launch.RoleId)
        && launch.RoleVersion > 0
        && not (String.IsNullOrWhiteSpace launch.AccountTokenId)
        && launch.State = LaunchState.ReservedUnregistered
        && launch.Lease.IsNone
        && launch.Version = 0
        && launch.Credit.IsNone
        && launch.PrimaryWorkId.IsNone
        && not launch.EverClaimed
        && launch.AncillaryMessages.IsEmpty
        && launch.DispatchCount = 0

    let private readPinned (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
        LedgerStore.readAt binding snapshot RecordPath
        |> Result.bind (fun (raw, oid) ->
            match raw with
            | None -> Ok(None, oid)
            | Some json ->
                try
                    let persisted = CanonicalCodec.decode<ProjectEnvelope> json
                    if persisted.SchemaVersion <> 1
                       || persisted.ProjectId <> binding.ProjectId.ToString("D")
                       || persisted.StateHash <> CanonicalCodec.digest (CanonicalCodec.encode persisted.State)
                       || json <> CanonicalCodec.encode persisted
                       || not (structurallyCoherent binding persisted.State) then
                        Error(LedgerIntegrityError "Work ledger schema, identity, projection or state hash mismatch.")
                    else Ok(Some persisted.State, oid)
                with ex ->
                    Error(LedgerIntegrityError ("Malformed canonical work ledger: " + ex.Message)))

    let loadAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
        readPinned binding snapshot
        |> Result.bind (fun (state, _) ->
            match state with
            | None -> Error(InvalidBinding "WQ-02 ledger is not initialized; no migration is permitted.")
            | Some state -> Ok state)

    let load (binding: LedgerBinding) =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            loadAt binding snapshot |> Result.map (fun state -> snapshot, state))

    // Explicit initialization is for disposable WQ-02 ledgers. It is not wired
    // to the live orchestration branch or any deployment path.
    let initialize (binding: LedgerBinding) actor operationId subscriptions =
        let state = { Contracts.emptyProject subscriptions with Migration = MigrationEpoch.Frozen }
        let request = JsonObject()
        request.Add("schemaVersion", JsonValue.Create(1))
        request.Add("command", JsonValue.Create("InitializeWQ02"))
        request.Add("subscriptions", JsonNode.Parse(CanonicalCodec.encode subscriptions))
        let intent =
            { OperationId = operationId; Actor = actor; Kind = "WQ02.Initialize"
              RequestJson = request.ToJsonString() }
        LedgerStore.transact binding intent (fun snapshot ->
            readPinned binding snapshot
            |> Result.bind (fun (previous, oid) ->
                match previous with
                | Some _ -> Error(InvalidBinding "WQ-02 ledger already exists.")
                | None ->
                    validatedContent binding state
                    |> Result.map (fun raw ->
                        [ { Path = RecordPath; ExpectedBlobOid = oid
                            Content = raw } ])))

    // All state and the complete original semantic command become a single
    // versioned project Git tree alongside LedgerStore's immutable operation receipt.
    // A competing clone must replay its planner against the winning remote head.
    // Clock time is transition context, not semantic request identity:
    // delayed retries of the same UUID must recover the original receipt.
    // The winning transition retains its witness in persisted project state.
    // Admission and terminal witnesses are published with the authoritative
    // project-state transition and its immutable LedgerStore operation receipt.
    // A different CAS winner cannot later forge the original launch decision.
    let private witnessChanges (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                               (before: Project) (after: Project)
                               (mutation: WorkMutation) (operationId: Guid) =
        let op = operationId.ToString("D")
        let project = binding.ProjectId.ToString("D")
        let decision =
            match mutation with
            | AdmitProject launch -> Some(launch, "AdmissionWon", op, "")
            | AdmitCredentialProject (launch, _) -> Some(launch, "AdmissionWon", op, "")
            | AbortUnboundProjectAdmission (launch, original) ->
                Some(launch, "AbortOnlyUnbound", original, op)
            | _ -> None
        let admissionChanges =
            match decision with
            | None -> Ok []
            | Some(launch, kind, original, abort) ->
                let record =
                    { SchemaVersion = 1; ProjectId = project; LaunchId = launch.Id
                      AccountTokenId = launch.AccountTokenId
                      AdmissionOperationId = original; Kind = kind
                      AbortOperationId = abort }
                readAdmissionAt binding snapshot launch.Id
                |> Result.bind (function
                    | Some _ -> Error(LedgerIntegrityError "Launch decision path already exists.")
                    | None ->
                        Ok [ { Path = admissionPath launch.Id; ExpectedBlobOid = None
                               Content = CanonicalCodec.encode record } ])
        let credentialChanges =
            match mutation with
            | AdmitCredentialProject (launch, digest) ->
                if digest.Length <> 64 ||
                   not (digest |> Seq.forall (fun c ->
                       (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) then
                    Error(InvalidBinding "Work credential verifier must be a lowercase SHA-256 digest.")
                else
                    readCredentialAt binding snapshot launch.Id
                    |> Result.bind (function
                        | Some _ -> Error(LedgerIntegrityError "Work credential verifier already exists.")
                        | None ->
                            let record =
                                { SchemaVersion = 1; ProjectId = project
                                  LaunchId = launch.Id; AccountTokenId = launch.AccountTokenId
                                  SecretSha256 = digest }
                            Ok [ { Path = credentialPath launch.Id; ExpectedBlobOid = None
                                   Content = CanonicalCodec.encode record } ])
            | _ -> Ok []
        let terminals =
            after.Launches
            |> Map.toList
            |> List.choose (fun (id, launch) ->
                match before.Launches.TryFind id, launch.State with
                | Some old, LaunchState.TerminalPendingRelease marker
                    when (match old.State with LaunchState.TerminalPendingRelease _ -> false | _ -> true)
                         && marker.EndsWith(":" + op, StringComparison.Ordinal)
                         && launch.Version > old.Version -> Some launch
                | _ -> None)
        let rec terminalChanges accumulated (remaining: Launch list) =
            match remaining with
            | [] -> Ok(List.rev accumulated)
            | launch :: rest ->
                readAdmissionAt binding snapshot launch.Id
                |> Result.bind (function
                    | Some(record, _) when record.SchemaVersion = 1
                                          && record.ProjectId = project
                                          && record.LaunchId = launch.Id
                                          && record.AccountTokenId = launch.AccountTokenId
                                          && record.Kind = "AdmissionWon"
                                          && record.AbortOperationId = ""
                                          && not (String.IsNullOrWhiteSpace record.AdmissionOperationId) ->
                        readTerminalAt binding snapshot launch.Id
                        |> Result.bind (function
                            | Some _ -> Error(LedgerIntegrityError "Terminal witness already exists.")
                            | None ->
                                let terminal =
                                    { SchemaVersion = 1; ProjectId = project; LaunchId = launch.Id
                                      AccountTokenId = launch.AccountTokenId
                                      AdmissionOperationId = record.AdmissionOperationId
                                      TerminalOperationId = op; TerminalKind = "LaunchTerminal"
                                      LaunchGeneration = int64 launch.Version }
                                terminalChanges
                                    ({ Path = terminalPath launch.Id; ExpectedBlobOid = None
                                       Content = CanonicalCodec.encode terminal } :: accumulated) rest)
                    | _ -> Error(LedgerIntegrityError "Terminal launch lacks its original project admission decision."))
        admissionChanges
        |> Result.bind (fun admissions ->
            credentialChanges
            |> Result.bind (fun credentials ->
                terminalChanges [] terminals |> Result.map (fun terminal -> admissions @ credentials @ terminal)))

    /// Positive WQ-04 project-only stage producer. The typed source event has
    /// already been verified against its original Git operation by the higher
    /// level binder. Recheck its complete receipt, original entity and the
    /// *same* pinned head in the winning project Git transaction. This commits
    /// the immutable origin and next work atomically, without reserving a token
    /// or activating any scheduler. Conflicts and uncertain pushes remain
    /// visible to the caller for ordinary agent reconciliation.
    let internal publishCanonicalEvent (binding: LedgerBinding) (actor: string) (operationId: Guid)
                                       (expectedVersion: int) (now: int64) (origin: CanonicalWorkOrigin)
                                       (event: QueueProducerEvent)
                                       (compositionGate: LedgerSnapshot -> WorkStage -> Result<GateState option * LedgerFileChange list, LedgerFailure>) =
        let request = JsonObject()
        request.Add("schemaVersion", JsonValue.Create(1))
        request.Add("expectedProjectVersion", JsonValue.Create(expectedVersion))
        request.Add("event", JsonNode.Parse(CanonicalCodec.encode event))
        request.Add("origin", JsonNode.Parse(CanonicalCodec.encode origin))
        let intent =
            { OperationId = operationId; Actor = actor; Kind = "WQ04.PracticalProducer"
              RequestJson = request.ToJsonString() }
        LedgerStore.transact binding intent (fun snapshot -> result {
            if origin.SchemaVersion <> 1
               || origin.ProjectId <> binding.ProjectId.ToString("D")
               || event.ProjectId <> origin.ProjectId
               || origin.WorkId <> WorkQueueProducers.workId event
               || String.IsNullOrWhiteSpace origin.SourceOperationId
               || String.IsNullOrWhiteSpace origin.SourceRequestHash
               || String.IsNullOrWhiteSpace origin.SourceEntityPath
               || String.IsNullOrWhiteSpace origin.SourceSelector
               || origin.AccountCreditStatus <> "NotReserved"
               || origin.RoleId <> "" || origin.RoleVersion <> 0 then
                return! Error(InvalidBinding "WQ04 producer identity or project-only scope is invalid.")
            if origin.SourceHead <> snapshot.Commit
               || origin.SourceHighWater <> snapshot.Header.NextSequence - 1L then
                return! Error(StaleEntityVersion
                    (sprintf "WQ04 source head/high-water changed. Expected %s / %d; observed %s / %d. Refresh before retry."
                        origin.SourceHead origin.SourceHighWater snapshot.Commit (snapshot.Header.NextSequence - 1L)))
            let! project, oid = readPinned binding snapshot
            let! state =
                match project with
                | Some value -> Ok value
                | None -> Error(InvalidBinding "WQ04 project has not been initialized.")
            if state.Version <> expectedVersion then
                return! Error(StaleEntityVersion
                    (sprintf "WQ04 project version conflict: expected %d, observed %d." expectedVersion state.Version))
            let! sourceOperationId =
                match Guid.TryParse origin.SourceOperationId with
                | true, id when id <> Guid.Empty -> Ok id
                | _ -> Error(InvalidBinding "WQ04 original operation UUID is missing.")
            let! maybeOperation = LedgerStore.getOperationAt binding snapshot sourceOperationId
            let! original =
                match maybeOperation with
                | Some value -> Ok value
                | None -> Error(LedgerIntegrityError "WQ04 original source operation is absent at this head.")
            if original.OperationId <> origin.SourceOperationId
               || original.Sequence <> origin.SourceOperationSequence
               || original.Type <> origin.SourceOperationKind
               || original.Actor <> origin.SourceOperationActor
               || original.RequestHash <> origin.SourceRequestHash then
                return! Error(LedgerIntegrityError "WQ04 original operation identity/request hash changed.")
            let! sourceHead = LedgerStore.getOperationOriginAt binding snapshot original
            if sourceHead <> origin.SourceOriginHead then
                return! Error(LedgerIntegrityError "WQ04 original operation commit changed.")
            let! sourceSnapshot = LedgerStore.verifiedHistoricalSnapshot binding sourceHead
            let! originalRaw, originalOid = LedgerStore.readAt binding sourceSnapshot origin.SourceEntityPath
            match originalRaw, originalOid with
            | Some raw, Some blobOid when blobOid = origin.SourceEntityBlobOid
                                         && CanonicalCodec.digest raw = origin.SourceEntitySha256 -> ()
            | _ -> return! Error(LedgerIntegrityError "WQ04 original source entity/receipt differs from its historical commit.")
            let! existing = readOriginAt binding snapshot origin.WorkId
            if existing.IsSome then
                return! Error(StaleEntityVersion
                    ("WQ04 stage already has a canonical origin: " + originPath origin.WorkId))
            let! projected = WorkQueueProducers.projectEvent state event |> Result.mapError failure
            let! stage =
                match projected with
                | ProducedProjection.Work work -> Ok work
                | _ -> Error(InvalidBinding "WQ04 practical producer currently accepts task/review/integration work only.")
            if not (validInitialStage stage) then
                return! Error(InvalidBinding "WQ04 malformed canonical stage.")
            // Decide the canonical composition hold inside this very same
            // original WQ04 Git CAS. No separately visible Available window.
            let! graphHold, compositionChanges = compositionGate snapshot stage
            if graphHold |> Option.exists (function GateState.CompositionHold _ -> false | _ -> true) then
                return! Error(InvalidBinding "WQ04 composition planner supplied an unsupported gate.")
            let staged = witnessNewStage state now stage
            let! next = Contracts.publishStage expectedVersion (operationId.ToString("D")) staged state
                        |> Result.mapError failure
            // The ordinary publish validator permits Blocked only for WQ02
            // dependencies. A pinned graph prerequisite is a separate,
            // verified source of blocking, so apply it before committing bytes.
            let committed =
                match graphHold with
                | Some (GateState.CompositionHold _ as hold) ->
                    { next with
                        Works = next.Works.Add(stage.Id,
                            { next.Works.[stage.Id] with
                                State = WorkState.Blocked
                                Gate = hold
                                AvailableAt = 0L
                                AvailableSequence = 0L })
                        NextAvailability = state.NextAvailability }
                | None -> next
                | Some _ ->
                    // Only the original, immutable composition hold is
                    // accepted: never synthesize other WQ02 gate authority.
                    next
            let! stateContent = validatedContent binding committed
            return
                [ { Path = RecordPath; ExpectedBlobOid = oid; Content = stateContent }
                  { Path = originPath origin.WorkId; ExpectedBlobOid = None
                    Content = CanonicalCodec.encode origin } ] @ compositionChanges
        })

    // Schema-1 WQ-02 projects have no canonical source/event/index or pinned
    // versioned role provenance. They are inert CAS prototypes only. An arbitrary
    // stage ID or a WQ-02 PublishStage receipt is never Scheduler authority.
    // Fixture-only execution below preserves historical frozen WQ-02 tests.
    let private requiresCanonicalAuthority = function
        | PostMessage ({ Subject = Subject.Task _ }, None)
        | AbortUnboundProjectAdmission _
        | StopStalled _
        | CancelUnclaimed _
        | MarkSourceRecoveryBlocked _
        | AssignPriority _
        | ProduceEvent _ -> false // ProduceEvent has its own unconditional hold.
        | _ -> true

    // Opt-in disposable Work lifecycle checks run inside the winning project
    // Git CAS, against both the original and resulting canonical stages.
    let private change inertFixture
            (verifiedLifecycle: (LedgerSnapshot -> Project -> Project -> Result<unit, LedgerFailure>) option)
            (binding: LedgerBinding) (actor: string) (operationId: Guid) (expectedVersion: int) (now: int64) (mutation: WorkMutation) =
        let request = JsonObject()
        request.Add("schemaVersion", JsonValue.Create(1))
        request.Add("expectedProjectVersion", JsonValue.Create(expectedVersion))
        request.Add("mutation", JsonNode.Parse(CanonicalCodec.encode mutation))
        let intent =
            { OperationId = operationId; Actor = actor; Kind = "WQ02.Mutate"
              RequestJson = request.ToJsonString() }
        LedgerStore.transact binding intent (fun snapshot ->
            readPinned binding snapshot
            |> Result.bind (fun (previous, oid) ->
                match previous with
                | None -> Error(InvalidBinding "WQ-02 ledger is not initialized.")
                | Some state when state.Version <> expectedVersion ->
                    Error(StaleEntityVersion "Stale WQ-02 project version.")
                | Some state ->
                    let op = operationId.ToString("D")
                    let transition =
                        match mutation with
                        | _ when inertFixture && state.Migration <> MigrationEpoch.Frozen ->
                            Error(Rejection.IncompleteCoverage
                                "WQ-02 fixture execution requires a durable Frozen project epoch")
                        | PublishStage _ when not inertFixture ->
                            Error(Rejection.IncompleteCoverage
                                "WQ-04 stages require canonical producer authority, regardless of ID or kind")
                        | PostMessage ({ Subject = Subject.Role _ }, _) when not inertFixture ->
                            Error(Rejection.IncompleteCoverage
                                "WQ-04 RoleMessage requires canonical message origin and pinned role authority")
                        | _ when not inertFixture && requiresCanonicalAuthority mutation ->
                            Error(Rejection.IncompleteCoverage
                                "WQ-04 canonical source/index and versioned role admission proof unavailable")
                        | PublishStage stage ->
                            if stage.Id.StartsWith("wq04-", StringComparison.Ordinal) then
                                Error(Rejection.IncompleteCoverage
                                    "WQ-04 stages require canonical producer authority, not PublishStage")
                            elif not (validInitialStage stage) then
                                Error(Rejection.InvalidState "invalid published stage generation")
                            else
                                Contracts.publishStage expectedVersion op (witnessNewStage state now stage) state
                        | ProduceEvent _ ->
                            // The caller supplies the purported event, SourceId,
                            // verdict and high-water. No pinned canonical
                            // task/review/challenge/message resolver exists here.
                            // A successful project CAS would not authenticate it.
                            Error(Rejection.IncompleteCoverage
                                "WQ-04 canonical event/index authority missing; recovery blocked")
                        | PostMessage (message, roleWork) ->
                            if roleWork |> Option.exists (fun stage ->
                                stage.Id.StartsWith("wq04-", StringComparison.Ordinal)) then
                                Error(Rejection.IncompleteCoverage "WQ-04 role message requires canonical source authority")
                            elif not (validMessagePayload message)
                               || (roleWork |> Option.exists (validInitialStage >> not)) then
                                Error(Rejection.InvalidState "invalid message or role stage payload")
                            else
                                Contracts.postMessage expectedVersion op message
                                    (roleWork |> Option.map (witnessNewStage state now)) state
                        | AdmitProject launch
                        | AdmitCredentialProject (launch, _) ->
                            // Account reservation is proven by the separate bridge before
                            // invoking this inert project-CAS adapter.
                            if not (validInitialLaunch binding launch)
                               || (state.Launches
                                   |> Map.exists (fun _ existing ->
                                       existing.AccountTokenId = launch.AccountTokenId)) then
                                Error(Rejection.InvalidState "invalid unverified launch admission")
                            else
                                Contracts.admitProject expectedVersion op launch state
                        | AbortUnboundProjectAdmission (launch, originalAdmission) ->
                            // C01 competes with original admission on launch-ID absence.
                            // The bridge must independently prove the account token.
                            match Guid.TryParse originalAdmission with
                            | true, id when id <> Guid.Empty && id <> operationId
                                            && launch.AccountTokenId = originalAdmission
                                            && validInitialLaunch binding launch
                                            && not (state.Receipts.ContainsKey originalAdmission) ->
                                Contracts.abortUnboundAdmission expectedVersion op launch state
                            | _ -> Error Rejection.MissingProof
                        | DispatchOnce (launch, outcome) -> Contracts.dispatchOnce expectedVersion op launch outcome state
                        | InitializeAgent (launch, sessionKey) ->
                            // Old Agent transport has no bearer: it cannot register a
                            // launch whose immutable Work verifier is present.
                            match readCredentialAt binding snapshot launch with
                            | Ok None ->
                                Contracts.initializeAgent expectedVersion op launch sessionKey (Guid.NewGuid().ToString("D")) now state
                            | _ -> Error Rejection.MissingProof
                        | InitializeCredential launch ->
                            if verifiedLifecycle.IsNone then Error Rejection.MissingProof
                            else Contracts.initializeCredential expectedVersion op launch now state
                        | Heartbeat (launch, generation) -> Contracts.heartbeat expectedVersion op launch generation now state
                        | StopStalled (launch, generation, timeout) ->
                            Contracts.markStalled expectedVersion op launch generation now timeout state
                        | ClaimNext launch ->
                            Contracts.claimNext expectedVersion op launch state
                        | ClaimVerifiedWork (launch, work, stageGeneration, claimGeneration) ->
                            match state.Works.TryFind work, state.Launches.TryFind launch with
                            | Some stage, Some owner
                                when stage.StageGeneration = stageGeneration
                                     && stage.ClaimGeneration + 1 = claimGeneration
                                     && stage.State = WorkState.Available
                                     && owner.Credit = Some work
                                     && not owner.EverClaimed ->
                                Contracts.claimNext expectedVersion op launch state
                            | _ -> Error(Rejection.InvalidState "stale selected Work/stage/claim or credit")
                        | FinalizeVerifiedReview (launch, work, claimGeneration, receipt) ->
                            match state.Works.TryFind work, state.Launches.TryFind launch with
                            | Some stage, Some owner
                                when verifiedLifecycle.IsSome
                                     && stage.Kind = WorkKind.FrozenReview
                                     && stage.State = WorkState.Claimed launch
                                     && stage.ClaimGeneration = claimGeneration
                                     && owner.PrimaryWorkId = Some work
                                     && not (String.IsNullOrWhiteSpace receipt) ->
                                Contracts.decideReview expectedVersion op work receipt state
                            | _ -> Error(Rejection.MissingProof)
                        | CompleteOrdinary (launch, work) ->
                            match state.Works.TryFind work with
                            | Some stage when stage.Kind = WorkKind.Implementation
                                              || stage.Kind = WorkKind.Integration ->
                                // Remote source ref/SHA and mutating-job proofs have no
                                // verified completion transition in this foundation.
                                Error Rejection.MissingProof
                            | _ -> Contracts.completeOrdinary expectedVersion op launch work now state
                        | CompleteMessage (launch, work, message, generation, reply) ->
                            Contracts.completeMessageWork expectedVersion op launch work message generation reply now state
                        | ClaimAncillary (launch, message) ->
                            Contracts.claimAncillaryMessage expectedVersion op launch message state
                        | CompleteAncillary (launch, message, generation, reply) ->
                            Contracts.completeAncillaryMessage expectedVersion op launch message generation reply now state
                        | AssignPriority (work, priority, reason) ->
                            Contracts.assignPriority expectedVersion op work priority reason state
                        | SetHardPrerequisite (work, prerequisite) ->
                            Contracts.setHardPrerequisite expectedVersion op work prerequisite now state
                        | RemoveHardPrerequisite (work, prerequisite) ->
                            Contracts.removeHardPrerequisite expectedVersion op work prerequisite now state
                        | CancelUnclaimed (work, reason) ->
                            Contracts.cancelUnclaimedStage expectedVersion op work reason now state
                        | TerminalizeNoClaim _ ->
                            // A live or stopped lease is not authenticated session-terminal
                            // and settled dispatch-journal proof of release eligibility.
                            Error Rejection.MissingProof
                        | YieldReadOnly (launch, work, claimGeneration) ->
                            Contracts.yieldReadOnly expectedVersion op launch work claimGeneration now state
                        | YieldSource checkpoint ->
                            Contracts.yieldSource expectedVersion op checkpoint now state
                        | MarkSourceRecoveryBlocked (launch, work, claimGeneration, reason) ->
                            Contracts.markSourceRecoveryBlocked expectedVersion op launch work claimGeneration reason state
                        | CheckpointReadOnly (launch, work, claimGeneration, summarySha256) ->
                            match state.Launches.TryFind launch, state.Works.TryFind work with
                            | Some owner, Some claimed
                                when owner.PrimaryWorkId = Some work
                                     && (match owner.State with LaunchState.Registered _ -> true | _ -> false)
                                     && claimed.State = WorkState.Claimed launch
                                     && claimed.ClaimGeneration = claimGeneration
                                     && claimed.Kind <> WorkKind.Implementation
                                     && claimed.Kind <> WorkKind.Integration
                                     && (match claimed.Kind with WorkKind.RoleMessage _ -> false | _ -> true)
                                     && claimed.Domain = DomainState.Undecided
                                     && claimed.SourceLineage.IsNone
                                     && claimed.Push.IsNone
                                     && not claimed.IntegrationAuthorized
                                     && not (isNull summarySha256)
                                     && Regex.IsMatch(summarySha256, "^[a-f0-9]{64}$") ->
                                let evidence = "checkpoint-readonly|" + launch + "|" + work +
                                               "|" + string claimGeneration + "|" + summarySha256
                                Ok { state with
                                         Version = state.Version + 1
                                         Receipts = state.Receipts.Add(op, ProjectOperationPayload.OtherMutation evidence) }
                            | _ -> Error(Rejection.InvalidState "stale/read-write checkpoint generation")
                        | CompleteVerifiedReadOnly (launch, work, claimGeneration, evidenceSha) ->
                            match state.Launches.TryFind launch, state.Works.TryFind work with
                            | Some owner, Some claimed
                                when owner.PrimaryWorkId = Some work
                                     && (match owner.State with LaunchState.Registered _ -> true | _ -> false)
                                     && claimed.State = WorkState.Claimed launch
                                     && claimed.ClaimGeneration = claimGeneration
                                     && claimed.Kind = WorkKind.Planning
                                     && claimed.Domain = DomainState.Undecided
                                     && claimed.SourceLineage.IsNone
                                     && claimed.Push.IsNone
                                     && not claimed.IntegrationAuthorized
                                     && not (isNull evidenceSha)
                                     && Regex.IsMatch(evidenceSha, "^[a-f0-9]{64}$") ->
                                Contracts.completeOrdinary expectedVersion op launch work now state
                            | _ -> Error(Rejection.InvalidState
                                           "read-only completion requires matching claimed generation and terminal evidence")
                        | ResolveReadOnlyGate (launch, work, claimGeneration) ->
                            match state.Works.TryFind work with
                            | Some claimed when claimed.State = WorkState.Claimed launch
                                                && claimed.ClaimGeneration = claimGeneration
                                                && claimed.SourceLineage.IsNone
                                                && claimed.Push.IsNone
                                                && claimed.Kind <> WorkKind.Implementation
                                                && claimed.Kind <> WorkKind.Integration ->
                                Contracts.resolveDependencyGate expectedVersion op launch work
                                    (SourceHandoffProof.ReadOnly ("wq02:" + op)) now state
                            | _ -> Error(Rejection.InvalidState "stale or source-writing dependency gate")
                        | ResolveAncillaryGate (launch, message, work, launchVersion, claimGeneration, gateVersion) ->
                            Contracts.resolveAncillaryDependencyGate expectedVersion op launch message work launchVersion claimGeneration gateVersion now state
                    transition
                    |> Result.mapError failure
                    |> Result.bind (fun changed ->
                        let lifecycleProof =
                            match verifiedLifecycle with
                            | Some verify -> verify snapshot state changed
                            | None -> Ok ()
                        // Verify against the *approved remote* inside the
                        // original Git-ledger transaction before publication.
                        // Receipt replay bypasses this planner on another
                        // clone with a different local source checkout.
                        let sourceProof =
                            match mutation with
                            | PublishStage stage ->
                                match stage.Kind, stage.SourceLineage with
                                | WorkKind.Implementation, Some lineage -> verifyInitialSource binding lineage
                                | _ -> Ok()
                            | ProduceEvent _ ->
                                // A source lineage check is not event authority.
                                // This second guard must not be bypassed if
                                // the transition branch changes in future.
                                Error(sourceReject "WQ-04 canonical source authority unavailable")
                            | YieldSource checkpoint ->
                                match state.Works.TryFind checkpoint.WorkId with
                                | Some prior ->
                                    verifySourceHandoff binding prior checkpoint
                                | None -> Error(sourceReject "missing source stage")
                            | ClaimNext launchId
                            | ClaimVerifiedWork (launchId, _, _, _) ->
                                match changed.Launches.TryFind launchId with
                                | Some launch ->
                                    match launch.PrimaryWorkId with
                                    | Some workId ->
                                        match changed.Works.TryFind workId with
                                        | Some work ->
                                            match work.Kind, work.SourceLineage with
                                            | WorkKind.Implementation, Some _ ->
                                                verifySourceClaimCheckout binding launchId work
                                            | WorkKind.Implementation, None ->
                                                Error(sourceReject "source claim lacks lineage")
                                            | _ -> Ok()
                                        | None -> Error(sourceReject "claimed work disappeared")
                                    | None -> Error(sourceReject "claim has no primary work")
                                | None -> Error(sourceReject "claimed launch disappeared")
                            | _ -> Ok()
                        lifecycleProof
                        |> Result.bind (fun () -> sourceProof)
                        |> Result.bind (fun () -> validatedContent binding changed)
                        |> Result.bind (fun raw ->
                            witnessChanges binding snapshot state changed mutation operationId
                            |> Result.map (fun evidence ->
                                { Path = RecordPath; ExpectedBlobOid = oid
                                  Content = raw } :: evidence)))))

    // The timestamp is captured for the winning transition's persisted state,
    // but is excluded from the original command's stable semantic identity.
    // Server clock authority must be added before live cutover.
    let execute binding actor operationId expectedVersion mutation =
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let result =
            validateMutationPayload mutation
            |> Result.bind (fun () -> change false None binding actor operationId expectedVersion now mutation)
        match result, dropPublishedAck.Value with
        | Ok _, Some shouldDrop when shouldDrop operationId ->
            Error(OutcomeUnknown $"Injected lost WQ-02 post-publication acknowledgement {operationId:D}.")
        | _ -> result

    // OAuth-authenticated operator endpoint only. The original WQ02 project
    // Git CAS is both the priority state and the durable audit receipt.
    let setPriority binding actor operationId expectedVersion workId priority reason =
        execute binding actor operationId expectedVersion
            (AssignPriority(workId, priority, reason))

    // Default-off WQ04/WQ07 admission boundary. The only transition this seam
    // can publish is a project launch with both immutable original WQ02 and
    // versioned-role witnesses in the SAME CAS. The caller must reverify pinned
    // source, role and account evidence inside this planner. Normal WQ02
    // execute remains fail-closed for production AdmitProject.
    let private executePinnedRoleAdmissionCore credentialVerifier
             (binding: LedgerBinding) (actor: string) (operationId: Guid)
             (expectedVersion: int) (launch: Launch) (expectedWorkId: string)
             (validateAt: LedgerSnapshot -> Project -> Result<string, LedgerFailure>) =
        let mutation =
            match credentialVerifier with
            | Some digest -> AdmitCredentialProject(launch, digest)
            | None -> AdmitProject launch
        let request = JsonObject()
        request.Add("schemaVersion", JsonValue.Create(1))
        request.Add("expectedProjectVersion", JsonValue.Create(expectedVersion))
        request.Add("mutation", JsonNode.Parse(CanonicalCodec.encode mutation))
        let intent =
            { OperationId = operationId; Actor = actor; Kind = "WQ02.Mutate"
              RequestJson = request.ToJsonString() }
        let result =
            validateMutationPayload mutation
            |> Result.bind (fun () ->
                LedgerStore.transact binding intent (fun snapshot -> result {
                    let! previous, oid = readPinned binding snapshot
                    let! state =
                        match previous with
                        | Some value -> Ok value
                        | None -> Error(InvalidBinding "WQ02 project is not initialized.")
                    if state.Version <> expectedVersion then
                        return! Error(StaleEntityVersion "Stale WQ02 project version.")
                    if not (validInitialLaunch binding launch)
                       || (state.Launches |> Map.exists (fun _ item ->
                           item.AccountTokenId = launch.AccountTokenId)) then
                        return! Error(InvalidBinding "Invalid or duplicate WQ04 launch identity.")
                    if String.IsNullOrWhiteSpace expectedWorkId then
                        return! Error(InvalidBinding "WQ04 selected work identity is absent.")
                    let! roleWitnessContent = validateAt snapshot state
                    if String.IsNullOrWhiteSpace roleWitnessContent then
                        return! Error(LedgerIntegrityError "Missing WQ04 original role-admission witness.")
                    let! next =
                        Contracts.admitProject expectedVersion (operationId.ToString("D")) launch state
                        |> Result.mapError failure
                    match next.Launches.TryFind launch.Id with
                    | Some admitted when admitted.Credit = Some expectedWorkId -> ()
                    | _ ->
                        return! Error(StaleEntityVersion
                            "WQ02 actual credit differs from the WQ04 pinned source/work selection.")
                    let rolePath = (admissionPath launch.Id).Replace(
                                        "/admissions/", "/role-admissions/", StringComparison.Ordinal)
                    let! prior, priorOid = LedgerStore.readAt binding snapshot rolePath
                    if prior.IsSome || priorOid.IsSome then
                        return! Error(LedgerIntegrityError "Role-admission path already exists.")
                    let! content = validatedContent binding next
                    let! admission = witnessChanges binding snapshot state next mutation operationId
                    if admission.Length <> (if credentialVerifier.IsSome then 2 else 1) then
                        return! Error(LedgerIntegrityError "Original project admission witness is incomplete.")
                    return
                        { Path = RecordPath; ExpectedBlobOid = oid; Content = content }
                        :: (admission @
                            [ { Path = rolePath; ExpectedBlobOid = None; Content = roleWitnessContent } ])
                }))
        match result, dropPublishedAck.Value with
        | Ok _, Some shouldDrop when shouldDrop operationId ->
            Error(OutcomeUnknown $"Injected lost WQ04 admission acknowledgement {operationId:D}.")
        | _ -> result

    // Preserve the historic, explicitly legacy admission contract unchanged.
    let internal executePinnedRoleAdmission binding actor operationId expectedVersion
            launch expectedWorkId validateAt =
        executePinnedRoleAdmissionCore None binding actor operationId expectedVersion
            launch expectedWorkId validateAt

    // An approved launcher issues once, keeps the bearer only in volatile
    // execution-delivery state, and binds its verifier to the WQ04/WQ07
    // admission decision atomically. A lost original send is reconciled via
    // the original intent; the secret is NEVER reconstructed from the hash.
    let internal executePinnedRoleCredentialAdmission binding actor operationId expectedVersion
            (launch: Launch) expectedWorkId
            (credential: IssuedWorkCredential)
            (validateAt: LedgerSnapshot -> Project -> Result<string, LedgerFailure>) =
        if isNull (box credential) || credential.LaunchId <> launch.Id then
            Error(InvalidBinding "Issued Work credential does not match the reserved launch.")
        else
            executePinnedRoleAdmissionCore (Some credential.VerifierSha256) binding actor
                operationId expectedVersion launch expectedWorkId validateAt

    // Deterministic tests can replay adversarial interleavings with virtual time.
    let internal executeAt binding actor operationId expectedVersion now mutation =
        validateMutationPayload mutation
        |> Result.bind (fun () -> change false None binding actor operationId expectedVersion now mutation)

    // Only the explicitly named test seam can simulate the accepted WQ-02
    // state machine. Its mutations are restricted to durable Frozen envelopes.
    // Neither this internal method nor its receipts grant canonical origin.
    let internal executeInertFixtureAt binding actor operationId expectedVersion now mutation =
        validateMutationPayload mutation
        |> Result.bind (fun () -> change true None binding actor operationId expectedVersion now mutation)

    // This seam is for explicit disposable Frozen Work fixtures only. A trusted
    // caller must validate WQ03 original/execution continuity, WQ04 role/source
    // and WQ07 held credit in the CAS planner, including the resulting chosen
    // Work. No live dispatch or public MCP command calls this function.
    let internal executePinnedDisposableLifecycle binding actor operationId expectedVersion
            now mutation (verifyAtCas: LedgerSnapshot -> Project -> Project -> Result<unit, LedgerFailure>) =
        let supported =
            match mutation with
            | ClaimVerifiedWork _ | CheckpointReadOnly _ | CompleteVerifiedReadOnly _
            | FinalizeVerifiedReview _ -> true
            | _ -> false
        if not supported || isNull (box verifyAtCas) then
            Error(InvalidBinding
                "Disposable Work CAS accepts only an evidence-verified claim, read-only checkpoint or completion.")
        else
            validateMutationPayload mutation
            |> Result.bind (fun () ->
                change true (Some verifyAtCas) binding actor operationId expectedVersion now mutation)

    let internal executeInertFixture binding actor operationId expectedVersion mutation =
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let result = executeInertFixtureAt binding actor operationId expectedVersion now mutation
        match result, dropPublishedAck.Value with
        | Ok _, Some shouldDrop when shouldDrop operationId ->
            Error(OutcomeUnknown $"Injected lost WQ-02 post-publication acknowledgement {operationId:D}.")
        | _ -> result


    // The only Work-native fixture mutation entrypoint. Credentials are
    // ephemeral arguments, NOT part of WorkMutation, operation receipts or logs.
    // This logic can be hosted by the approved relay without server-local state.
    let private credentialMutation launchId action =
        match action with
        | CredentialWorkAction.FirstConnect -> InitializeCredential launchId
        | CredentialWorkAction.ClaimPrimary -> ClaimNext launchId
        | CredentialWorkAction.HeartbeatLease generation -> Heartbeat(launchId, generation)
        | CredentialWorkAction.Checkpoint (workId, generation, digest) ->
            CheckpointReadOnly(launchId, workId, generation, digest)
        | CredentialWorkAction.CompleteReadOnly (workId, generation, digest) ->
            CompleteVerifiedReadOnly(launchId, workId, generation, digest)
        | CredentialWorkAction.FinishOrdinary workId -> CompleteOrdinary(launchId, workId)
        | CredentialWorkAction.ClaimMessage messageId -> ClaimAncillary(launchId, messageId)
        | CredentialWorkAction.FinishMessage (workId, messageId, generation, replyId) ->
            CompleteMessage(launchId, workId, messageId, generation, replyId)

    let internal verifyBearerAt (binding: LedgerBinding) snapshot (state: Project)
                               launchId (bearer: string) (operationId: Guid)
                               (action: CredentialWorkAction) =
        let denied = InvalidBinding "Work execution credential or launch lifecycle rejected."
        let validEncoding = WorkCredentialFormat.isSupportedBearer bearer
        // Hash the candidate independently of the public launch label;
        // compare fixed-size digests in constant time.
        if not validEncoding then Error denied
        else
            let presented = SHA256.HashData(Encoding.UTF8.GetBytes bearer)
            readCredentialAt binding snapshot launchId
            |> Result.bind (function
                | Some (verifier, _) ->
                    match state.Launches.TryFind launchId with
                    | Some launch when verifier.SchemaVersion = 1
                                       && verifier.ProjectId = binding.ProjectId.ToString("D")
                                       && verifier.LaunchId = launchId
                                       && verifier.AccountTokenId = launch.AccountTokenId
                                       && launch.ProjectId = verifier.ProjectId
                                       && Regex.IsMatch(verifier.SecretSha256, "^[a-f0-9]{64}$") ->
                        let known = Convert.FromHexString(verifier.SecretSha256)
                        let authenticated = CryptographicOperations.FixedTimeEquals(presented, known)
                        let marker = "work-credential-v1:" + launchId
                        let registered =
                            match launch.State, launch.Lease with
                            | LaunchState.Registered (id, value), Some lease
                                when id = launchId && value = marker && not lease.StopRequested -> true
                            | _ -> false
                        let eligible =
                            match action, launch.State with
                            | CredentialWorkAction.FirstConnect, LaunchState.SubmittedUnregistered DispatchState.Submitted
                            | CredentialWorkAction.FirstConnect, LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown -> true
                            | _, _ -> registered
                        // An exact previously committed operation can be replayed
                        // after terminal/STOP, but no new operation may mutate.
                        let replay = state.Receipts.ContainsKey(operationId.ToString("D"))
                        if authenticated && (eligible || replay) then Ok ()
                        else Error denied
                    | _ -> Error denied
                | None -> Error denied)

    // Fixture gate deliberately remains disconnected from public Agent tools.
    // Both the preflight and the winning Git CAS re-read the same persisted
    // verifier: stale snapshots, restarted relays and two clones fail closed.
    let internal executeCredentialFixtureAt (binding: LedgerBinding) actor
            (operationId: Guid) expectedVersion now launchId bearer action =
        let mutation = credentialMutation launchId action
        load binding
        |> Result.bind (fun (head, state) ->
            verifyBearerAt binding head state launchId bearer operationId action)
        |> Result.bind (fun () ->
            let verifyWinningCas snapshot before _ =
                verifyBearerAt binding snapshot before launchId bearer operationId action
            validateMutationPayload mutation
            |> Result.bind (fun () ->
                change true (Some verifyWinningCas) binding actor operationId expectedVersion now mutation))
