module Client.NativeSchedulerAccountReservation

open System
open System.Text.Json
open System.Text.RegularExpressions
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.ReviewWorkflow

// Default-off WQ10 disposable implementation. No production account/operator
// trust root, authenticated provider-session ingress or browser send hook exists.
[<CLIMutable>]
type NativeDispatchCandidate =
    { DispatchId: string
      EffectOperationId: string
      Kind: string
      ProjectId: string
      ProjectLedgerId: string
      ProjectEpoch: int
      OwnerAgentId: string
      TaskId: string
      AttemptId: string
      AssignmentGeneration: int64
      SourceRepositoryId: string
      SourceBaseOid: string
      SourceRef: string
      SourceLeaseSha256: string
      ReviewId: string
      ReviewVersion: int64
      ReviewedHeadOid: string
      ReviewRef: string
      ClaimOperationId: string }

[<CLIMutable>]
type NativeReservationRecord =
    { CanonicalKey: string
      OriginalOperationId: string
      Candidate: NativeDispatchCandidate
      Status: string
      Generation: int64
      ProjectFenceOid: string
      ProviderSessionId: string
      TerminalOperationId: string }

[<CLIMutable>]
type NativeReservationState =
    { SchemaVersion: int
      AccountId: string
      LedgerId: string
      Epoch: int
      Records: NativeReservationRecord array }

// Only an independently marked private disposable authority may accept this
// synthetic completeness witness. Never expose this as production session proof.
type NativeDisposableCoverage =
    { AccountHead: string
      ProjectHeads: NativeVerifiedProjectHead array
      ObservedAtUtc: DateTimeOffset
      Complete: bool }

type NativeTerminalLocator =
    { DispatchId: Guid
      OriginalReservationId: Guid
      EffectOperationId: Guid
      TerminalOperationId: Guid }

type NativeTerminalProof =
    { DispatchId: Guid
      OriginalReservationId: Guid
      EffectOperationId: Guid
      TerminalOperationId: Guid
      ProjectId: Guid
      ProjectLedgerId: Guid
      ProjectEpoch: int
      CanonicalKey: string
      ProviderSessionId: string
      ProjectHead: string
      ProjectTerminalOid: string
      ProviderTerminalOid: string }

type INativeDisposableTerminalVerifier =
    abstract member VerifyCurrentTerminal: NativeTerminalLocator -> Result<NativeTerminalProof,string>

module NativeSchedulerAccountReservation =
    [<Literal>]
    let private Path = "native/scheduler-reservations-v1.json"

    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let private json value = JsonSerializer.Serialize(value, options)
    let private digest = NativeSchedulerRosterPolicy.sha256
    let private validGuid (value: string) =
        let mutable g = Guid.Empty
        not (String.IsNullOrWhiteSpace value) && Guid.TryParse(value, &g)
        && g <> Guid.Empty && value = g.ToString("D")
    let private validGuidN (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{32}$")
        && value <> String.replicate 32 "0"
    let private validDigest (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$")
    let private validOid (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{40}([0-9a-f]{24})?$")
    let private bad text = Error(AccountPolicyNotAuthoritative text)
    let private occupied (r: NativeReservationRecord) = r.Status <> "Released"
    let private kinds = [ "Worker"; "Reviewer" ]

    let private key (b: AccountBinding) (c: NativeDispatchCandidate) =
        if c.Kind = "Worker" then
            digest (json {| accountId = string b.AccountId; projectId = c.ProjectId
                            projectLedgerId = c.ProjectLedgerId; epoch = c.ProjectEpoch
                            kind = c.Kind; taskId = c.TaskId; attemptId = c.AttemptId
                            generation = c.AssignmentGeneration |})
        else
            digest (json {| accountId = string b.AccountId; projectId = c.ProjectId
                            projectLedgerId = c.ProjectLedgerId; epoch = c.ProjectEpoch
                            kind = c.Kind; reviewId = c.ReviewId; version = c.ReviewVersion |})

    let private candidateValid (b: AccountBinding) (p: NativeRosterPolicy)
                               (c: NativeDispatchCandidate) =
        if isNull (box c) || not (validGuid c.DispatchId) || not (validGuid c.EffectOperationId)
           || not (List.contains c.Kind kinds)
           || not (validGuid c.ProjectId) || not (validGuid c.ProjectLedgerId)
           || c.ProjectEpoch < 1 || String.IsNullOrWhiteSpace c.OwnerAgentId then
            false
        else
            let memberProject =
                p.Projects |> Array.tryFind (fun project -> project.ProjectId = c.ProjectId)
            match memberProject with
            | None -> false
            | Some project ->
                project.LedgerId = c.ProjectLedgerId && project.Epoch = c.ProjectEpoch
                && (if c.Kind = "Worker" then
                        not (String.IsNullOrWhiteSpace c.TaskId)
                        && validGuidN c.AttemptId && c.AssignmentGeneration > 0L
                        && validDigest c.SourceRepositoryId
                        && validOid c.SourceBaseOid
                        && not (String.IsNullOrEmpty c.SourceRef)
                        && c.SourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                        && validDigest c.SourceLeaseSha256
                        && c.ReviewVersion = 0L
                        && String.IsNullOrEmpty c.ReviewedHeadOid
                        && String.IsNullOrEmpty c.ReviewId
                        && String.IsNullOrEmpty c.ReviewRef
                        && String.IsNullOrEmpty c.ClaimOperationId
                    else
                        validGuidN c.ReviewId
                        && c.ReviewVersion > 0L
                        && c.ReviewVersion <= int64 Int32.MaxValue
                        && validOid c.ReviewedHeadOid
                        && not (String.IsNullOrEmpty c.ReviewRef)
                        && c.ReviewRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal)
                        && validGuid c.ClaimOperationId
                        && c.AssignmentGeneration = 0L
                        && String.IsNullOrEmpty c.TaskId
                        && String.IsNullOrEmpty c.AttemptId)

    let private stateValid (b: AccountBinding) (s: NativeReservationState) =
        not (isNull (box s)) && s.SchemaVersion = 1
        && s.AccountId = string b.AccountId && s.LedgerId = string b.LedgerId
        && s.Epoch = b.Epoch && not (isNull s.Records)
        && s.Records |> Array.forall (fun r ->
            not (isNull (box r)) && not (isNull (box r.Candidate))
            && validDigest r.CanonicalKey
            && validGuid r.OriginalOperationId && r.Generation > 0L
            && validGuid r.Candidate.DispatchId && validGuid r.Candidate.EffectOperationId
            && r.CanonicalKey = key b r.Candidate
            && (r.Candidate.Kind <> "Reviewer"
                || (r.Candidate.ReviewVersion > 0L
                    && r.Candidate.ReviewVersion <= int64 Int32.MaxValue))
            && List.contains r.Candidate.Kind kinds
            && List.contains r.Status
                 [ "Reserved"; "Dispatching"; "Submitted"; "Uncertain"; "Active"; "Unknown"; "Released" ]
            && (r.Status <> "Released" || validGuid r.TerminalOperationId))
        && (s.Records |> Array.distinctBy _.CanonicalKey |> Array.length) = s.Records.Length
        && (s.Records |> Array.distinctBy _.OriginalOperationId |> Array.length) = s.Records.Length
        && (s.Records |> Array.distinctBy (fun r -> r.Candidate.DispatchId) |> Array.length) = s.Records.Length
        && (s.Records |> Array.distinctBy (fun r -> r.Candidate.EffectOperationId) |> Array.length) = s.Records.Length
        && (s.Records |> Array.filter (fun r -> r.Status = "Released")
                      |> Array.distinctBy _.TerminalOperationId |> Array.length) =
           (s.Records |> Array.filter (fun r -> r.Status = "Released") |> Array.length)

    let private stateAt b (snapshot: AccountSnapshot) =
        AccountCapacityLedger.readNativeReservationDocument b snapshot.Head
        |> Result.bind (function
            | None ->
                Ok { SchemaVersion = 1; AccountId = string b.AccountId
                     LedgerId = string b.LedgerId; Epoch = b.Epoch; Records = [||] }
            | Some raw ->
                try
                    let parsed = JsonSerializer.Deserialize<NativeReservationState>(raw, options)
                    if stateValid b parsed then Ok parsed
                    else Error(AccountIntegrityError "Native reservation identity, status or uniqueness invariant failed.")
                with _ -> Error(AccountIntegrityError "Native reservation document cannot be parsed."))

    let private verifyCurrent b root operatorKey subject projects (coverage: NativeDisposableCoverage)
                              (snapshot: AccountSnapshot) =
        let policy =
            AccountCapacityLedger.readVerifiedDisposableNativeRosterPolicy
                root b operatorKey subject projects
        if policy.Status <> "PolicyVerified" then
            bad ("No authenticated complete native roster: " + String.concat "; " policy.HoldReasons)
        elif isNull (box coverage) || not coverage.Complete
             || coverage.AccountHead <> snapshot.Head
             || policy.VerifiedAccountHead <> Some snapshot.Head
             || isNull coverage.ProjectHeads
             || Set.ofArray coverage.ProjectHeads <> Set.ofArray policy.VerifiedProjectHeads
             || coverage.ObservedAtUtc > DateTimeOffset.UtcNow.AddSeconds(2.0)
             || coverage.ObservedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-60.0) then
            bad "Missing, stale, incomplete or mismatched global native session/project coverage."
        else
            match policy.Policy with
            | Some p when p.MembershipComplete -> Ok p
            | _ -> bad "Verified complete native operator policy is missing."

    // Never promote caller-shaped IDs to scheduler authority. The same verified
    // project head used by the authenticated full-roster observation must contain
    // the actual attempt/source lease or immutable claimed review.
    let private projectAt (projects: LedgerBinding array)
                          (coverage: NativeDisposableCoverage) projectId =
        match projects |> Array.tryFind (fun b -> string b.ProjectId = projectId),
              coverage.ProjectHeads |> Array.tryFind (fun h -> h.ProjectId = projectId) with
        | Some binding, Some witness when
            witness.LedgerId = string binding.LedgerId && witness.Epoch = binding.Epoch ->
            match LedgerStore.fetch binding with
            | Ok snapshot when snapshot.Commit = witness.VerifiedHead ->
                Ok(binding, snapshot)
            | _ -> bad "Canonical candidate/project terminal head is stale or unverified."
        | _ -> bad "Canonical candidate has no verified project authority."

    let private requiredProject<'T> binding snapshot path =
        match LedgerStore.readAt binding snapshot path with
        | Ok(Some raw, Some oid) ->
            try
                let value = JsonSerializer.Deserialize<'T>(raw, options)
                if isNull (box value) then bad "Canonical project record is null."
                else Ok(value, raw, oid)
            with _ -> bad "Canonical project record cannot be parsed."
        | _ -> bad "Canonical project record is missing or unreadable."

    let private claimOperationMatches (operation: LedgerOperation)
                                      (c: NativeDispatchCandidate) taskId version path =
        try
            use envelope = JsonDocument.Parse(operation.CanonicalRequestJson)
            let root = envelope.RootElement
            use request = JsonDocument.Parse(root.GetProperty("request").GetString())
            let r = request.RootElement
            operation.Type = "ClaimBranchReview"
            && operation.Actor = c.OwnerAgentId
            && operation.OperationId = c.ClaimOperationId
            && operation.Paths |> Array.contains path
            && root.GetProperty("projectId").GetString() = c.ProjectId
            && root.GetProperty("kind").GetString() = "ClaimBranchReview"
            && root.GetProperty("actor").GetString() = c.OwnerAgentId
            && root.GetProperty("operationId").GetString() = c.ClaimOperationId
            && r.GetProperty("taskId").GetString() = taskId
            && r.GetProperty("reviewId").GetString() = c.ReviewId
            && r.GetProperty("reviewerId").GetString() = c.OwnerAgentId
            && r.GetProperty("expectedVersion").GetInt32() = version - 1
            && (r.EnumerateObject() |> Seq.length) = 4
        with _ -> false

    let private candidateFromVerifiedProject projects coverage (c: NativeDispatchCandidate) =
        projectAt projects coverage c.ProjectId
        |> Result.bind (fun (binding, snapshot) ->
            if c.Kind = "Worker" then
                if not (Regex.IsMatch(c.TaskId, "^[A-Za-z0-9_-]{1,80}$")) then
                    bad "Unsafe canonical Worker task identity."
                else
                    let path = $"tasks/{c.TaskId}/attempts/{c.AttemptId}.json"
                    requiredProject<ReviewTask> binding snapshot $"tasks/{c.TaskId}/task.json"
                    |> Result.bind (fun (task, _, _) ->
                        requiredProject<ReviewAttempt> binding snapshot path
                        |> Result.bind (fun (attempt, raw, _) ->
                            if task.TaskId <> c.TaskId || task.Lifecycle <> "InProgress"
                               || task.AttemptId <> c.AttemptId
                               || attempt.TaskId <> c.TaskId || attempt.AttemptId <> c.AttemptId
                               || attempt.OwnerAgentId <> c.OwnerAgentId
                               || attempt.Phase <> "Started"
                               || int64 attempt.AssignmentGeneration <> c.AssignmentGeneration
                               || attempt.SourceRepositoryId <> c.SourceRepositoryId
                               || attempt.BaseCommit <> c.SourceBaseOid
                               || attempt.SourceRef <> c.SourceRef
                               || String.IsNullOrWhiteSpace attempt.TargetRef
                               || not (validOid attempt.SpecBlobOid)
                               || c.SourceLeaseSha256 <> digest raw then
                                bad "Worker source lease is absent, forged, stale or not issued on this project head."
                            else Ok ()))
            else
                let bits = c.ReviewRef.Split('/')
                if bits.Length <> 6 || bits[0] <> "refs" || bits[1] <> "heads"
                   || bits[2] <> "jarvis-review"
                   || bits[4] <> c.ReviewId
                   || not (Regex.IsMatch(bits[3], "^[A-Za-z0-9_-]{1,80}$"))
                   || not (Regex.IsMatch(bits[5], "^r[1-9][0-9]*$"))
                   || not (String.IsNullOrEmpty c.SourceRepositoryId)
                   || not (String.IsNullOrEmpty c.SourceBaseOid)
                   || not (String.IsNullOrEmpty c.SourceRef)
                   || not (String.IsNullOrEmpty c.SourceLeaseSha256) then
                    bad "Reviewer source must derive from one immutable canonical review claim."
                else
                    let taskId = bits[3]
                    let path = $"tasks/{taskId}/reviews/{c.ReviewId}/review.json"
                    requiredProject<BranchReview> binding snapshot path
                    |> Result.bind (fun (review, _, _) ->
                        if review.ReviewId <> c.ReviewId
                           || review.TaskId <> taskId
                           || review.Status <> "Claimed"
                           || review.ReviewerId <> c.OwnerAgentId
                           || int64 review.Version <> c.ReviewVersion
                           || review.SourceVersion.ToString() <> bits[5].Substring(1)
                           || review.ReviewRef <> c.ReviewRef
                           || review.ReviewedHeadOid <> c.ReviewedHeadOid then
                            bad "Reviewer claim/version/frozen source differs from canonical Git record."
                        else
                            match LedgerStore.getOperationAt binding snapshot (Guid.Parse c.ClaimOperationId) with
                            | Ok(Some op) when claimOperationMatches op c taskId review.Version path ->
                                match LedgerStore.getOperationOriginAt binding snapshot op with
                                | Ok _ ->
                                    LedgerStore.fetchReviewRef binding c.ReviewRef c.ReviewedHeadOid
                                    |> Result.mapError (fun _ ->
                                        AccountPolicyNotAuthoritative "Frozen review source/ref no longer authenticates.")
                                | _ -> bad "Reviewer claim operation has no immutable verified origin."
                            | _ -> bad "Reviewer claim operation is absent, stale or mismatched."))

    // A disposable verifier's Ok is not an issuer. The original send fence,
    // terminal operation and provider-session evidence must be on the same
    // authenticated project lineage that supplied the account roster.
    let private terminalEvidenceMatches (raw: string) (record: NativeReservationRecord)
                                        (locator: NativeTerminalLocator) =
        try
            use document = JsonDocument.Parse(raw)
            let r = document.RootElement
            r.ValueKind = JsonValueKind.Object
            && (r.EnumerateObject() |> Seq.length) = 9
            && r.GetProperty("schemaVersion").GetInt32() = 1
            && r.GetProperty("state").GetString() = "Terminated"
            && r.GetProperty("dispatchId").GetString() = string locator.DispatchId
            && r.GetProperty("originalReservationId").GetString() = string locator.OriginalReservationId
            && r.GetProperty("effectOperationId").GetString() = string locator.EffectOperationId
            && r.GetProperty("terminalOperationId").GetString() = string locator.TerminalOperationId
            && r.GetProperty("canonicalKey").GetString() = record.CanonicalKey
            && r.GetProperty("projectFenceOid").GetString() = record.ProjectFenceOid
            && r.GetProperty("providerSessionId").GetString() = record.ProviderSessionId
        with _ -> false

    let private terminalFromVerifiedProject projects coverage
                                         (record: NativeReservationRecord)
                                         (locator: NativeTerminalLocator)
                                         (proof: NativeTerminalProof) =
        projectAt projects coverage record.Candidate.ProjectId
        |> Result.bind (fun (binding, snapshot) ->
            if proof.ProjectHead <> snapshot.Commit
               || not (validOid record.ProjectFenceOid)
               || String.IsNullOrWhiteSpace record.ProviderSessionId
               || proof.ProviderSessionId <> record.ProviderSessionId then
                bad "Terminal head, original project fence or provider session is unbound."
            else
                let id = locator.TerminalOperationId.ToString("N")
                let path = $"records/native-scheduler/terminals/{id}.json"
                match LedgerStore.getOperationAt binding snapshot locator.TerminalOperationId with
                | Ok(Some operation) when operation.Type = "NativeProviderSessionTerminal"
                                           && operation.OperationId = string locator.TerminalOperationId
                                           && (operation.Paths |> Array.contains path) ->
                    match LedgerStore.getOperationOriginAt binding snapshot operation,
                          LedgerStore.readAt binding snapshot path with
                    | Ok origin, Ok(Some raw, Some oid) when
                        proof.ProjectTerminalOid = origin
                        && proof.ProviderTerminalOid = oid
                        && terminalEvidenceMatches raw record locator ->
                        try
                            use doc = JsonDocument.Parse(operation.CanonicalRequestJson)
                            let envelope = doc.RootElement
                            use request = JsonDocument.Parse(envelope.GetProperty("request").GetString())
                            if envelope.GetProperty("kind").GetString() <> "NativeProviderSessionTerminal"
                               || envelope.GetProperty("projectId").GetString() <> record.Candidate.ProjectId
                               || envelope.GetProperty("operationId").GetString() <> string locator.TerminalOperationId
                               || not (terminalEvidenceMatches (request.RootElement.GetRawText()) record locator) then
                                bad "Project terminal intent does not bind the original session/effect fence."
                            else
                                match LedgerStore.verifySourceAncestry binding record.ProjectFenceOid origin,
                                      LedgerStore.verifySourceAncestry binding origin snapshot.Commit with
                                | Ok (), Ok () -> Ok ()
                                | _ -> bad "Terminal operation is outside the verified original project lineage."
                        with _ -> bad "Project terminal evidence is malformed."
                    | _ -> bad "Project/provider terminal OIDs are not source-bound to the verified head."
                | _ -> bad "Immutable provider-session terminal operation is missing from the project head.")

    let private safePhase (snapshot: AccountSnapshot) =
        if snapshot.State.MigrationPhase <> "LegacyActive" then
            Error(AccountMigrationWritesFenced snapshot.State.MigrationPhase)
        elif snapshot.State.Tokens |> Array.exists (fun token -> token.Status = "Held") then
            bad "WQ07 Held token exists; native and legacy occupancy may not coexist."
        else Ok ()

    // A default-off, non-launching public entrypoint prevents a caller-provided
    // OAuth subject, signature, or coverage boolean from becoming authority.
    let reserve (_: AccountBinding) (_: NativeDispatchCandidate) =
        bad "Native host/session trust root not provisioned; production reservation disabled."

    let read (_: AccountBinding) =
        bad "Native account occupancy is not authoritative without a trusted host/session root."

    let internal readVerifiedDisposable root b operatorKey subject projects coverage =
        AccountCapacityLedger.fetch b
        |> Result.bind (fun snapshot ->
            verifyCurrent b root operatorKey subject projects coverage snapshot
            |> Result.bind (fun _ -> stateAt b snapshot |> Result.map (fun native -> snapshot, native)))

    let internal reserveVerifiedDisposable root (b: AccountBinding) operatorKey subject projects
                                           coverage operationId actor candidate =
        // An absent caller observation is a hold, never a null dereference.
        let observedHead = if isNull (box coverage) then "" else coverage.AccountHead
        let request = json {| candidate = candidate; coverageHead = observedHead |}
        AccountCapacityLedger.transactFiles b operationId actor "NativeReserve" request None
            (fun snapshot ->
                verifyCurrent b root operatorKey subject projects coverage snapshot
                |> Result.bind (fun policy ->
                    safePhase snapshot |> Result.bind (fun () ->
                        if not (candidateValid b policy candidate) then
                            bad "Native candidate lacks exact worker source lease, reviewer claim or signed roster membership."
                        else
                            candidateFromVerifiedProject projects coverage candidate
                            |> Result.bind (fun () ->
                            stateAt b snapshot |> Result.bind (fun state ->
                                let candidateKey = key b candidate
                                if state.Records |> Array.exists (fun r -> r.CanonicalKey = candidateKey) then
                                    bad "Canonical attempt/review already holds a native reservation under another UUID."
                                elif state.Records |> Array.exists (fun r ->
                                     r.Candidate.DispatchId = candidate.DispatchId
                                     || r.Candidate.EffectOperationId = candidate.EffectOperationId) then
                                    bad "Original dispatch/effect identity is already bound to another candidate."
                                else
                                    let accountLimit =
                                        if candidate.Kind = "Worker" then policy.AccountLimits.Worker
                                        else policy.AccountLimits.Reviewer
                                    let project = policy.Projects |> Array.find (fun p -> p.ProjectId = candidate.ProjectId)
                                    let projectLimit =
                                        if candidate.Kind = "Worker" then project.Limits.Worker else project.Limits.Reviewer
                                    let count predicate =
                                        state.Records |> Array.filter (fun r -> occupied r && predicate r) |> Array.length
                                    let accountUsed = count (fun r -> r.Candidate.Kind = candidate.Kind)
                                    let local = count (fun r ->
                                        r.Candidate.Kind = candidate.Kind && r.Candidate.ProjectId = candidate.ProjectId)
                                    if accountUsed >= accountLimit then
                                        Error(AccountCapacityFull(accountUsed, accountLimit))
                                    elif local >= projectLimit then
                                        Error(AccountCapacityFull(local, projectLimit))
                                    else
                                        let record =
                                            { CanonicalKey = candidateKey
                                              OriginalOperationId = string operationId
                                              Candidate = candidate; Status = "Reserved"
                                              Generation = 1L; ProjectFenceOid = ""
                                              ProviderSessionId = ""; TerminalOperationId = "" }
                                        let next = { state with Records = Array.append state.Records [| record |] }
                                        let raw = json next
                                        Ok(snapshot.State,
                                           json {| status = "Reserved"; dispatchId = candidate.DispatchId
                                                   canonicalKey = candidateKey; nativeStateSha256 = digest raw |},
                                           [ Path, raw ]))))))

    // The only transition path is test-fixture-scoped. The caller must present
    // the original effect ID; this is state accounting, NOT permission to send.
    // Durable native project fence/provider verification remains a separate task.
    let internal transitionVerifiedDisposable root (b: AccountBinding) operatorKey subject projects
                                              coverage operationId actor dispatchId originalEffectId
                                              expectedGeneration toStatus projectFenceOid providerSessionId =
        let request =
            json {| dispatchId = string dispatchId; originalEffectId = string originalEffectId
                    expectedGeneration = expectedGeneration; toStatus = toStatus
                    projectFenceOid = projectFenceOid; providerSessionId = providerSessionId |}
        AccountCapacityLedger.transactFiles b operationId actor "NativeTransition" request None
            (fun snapshot ->
                verifyCurrent b root operatorKey subject projects coverage snapshot
                |> Result.bind (fun _ ->
                    safePhase snapshot |> Result.bind (fun () ->
                        stateAt b snapshot |> Result.bind (fun state ->
                            match state.Records |> Array.tryFind (fun r -> r.Candidate.DispatchId = string dispatchId) with
                            | None -> bad "Native dispatch missing."
                            | Some record when record.Candidate.EffectOperationId <> string originalEffectId
                                               || record.Generation <> expectedGeneration ->
                                bad "Original effect identity or reservation generation mismatch."
                            | Some record ->
                                let allowed =
                                    match record.Status, toStatus with
                                    | "Reserved", "Dispatching"
                                    | "Dispatching", "Submitted"
                                    | "Dispatching", "Uncertain"
                                    | "Submitted", "Uncertain"
                                    | "Submitted", "Active"
                                    | "Submitted", "Unknown"
                                    | "Uncertain", "Active"
                                    | "Uncertain", "Unknown"
                                    | "Active", "Unknown"
                                    | "Unknown", "Active" -> true
                                    | _ -> false
                                if not allowed then bad "Native status transition is not monotonic."
                                elif not (validOid projectFenceOid)
                                     || (record.ProjectFenceOid <> "" && record.ProjectFenceOid <> projectFenceOid)
                                     || (List.contains toStatus [ "Active"; "Unknown" ]
                                         && String.IsNullOrWhiteSpace providerSessionId)
                                     || (record.ProviderSessionId <> "" && record.ProviderSessionId <> providerSessionId) then
                                    bad "Missing original project-send fence or trusted session identity."
                                else
                                    let nextRecord =
                                        { record with Status = toStatus; Generation = record.Generation + 1L
                                                      ProjectFenceOid = projectFenceOid
                                                      ProviderSessionId = providerSessionId }
                                    let records =
                                        state.Records |> Array.map (fun r ->
                                            if r.CanonicalKey = record.CanonicalKey then nextRecord else r)
                                    let next = { state with Records = records }
                                    let raw = json next
                                    Ok(snapshot.State,
                                       json {| status = toStatus; dispatchId = record.Candidate.DispatchId
                                               generation = nextRecord.Generation; nativeStateSha256 = digest raw |},
                                       [ Path, raw ])))))

    let internal releaseVerifiedDisposable root (b: AccountBinding) operatorKey subject projects
                                           coverage (verifier: INativeDisposableTerminalVerifier)
                                           operationId actor (locator: NativeTerminalLocator) =
        // Reject invalid IDs before entering any account-head CAS transaction.
        if isNull (box locator) || locator.DispatchId = Guid.Empty
           || locator.OriginalReservationId = Guid.Empty
           || locator.EffectOperationId = Guid.Empty
           || locator.TerminalOperationId = Guid.Empty then
            bad "Native terminal requires nonempty original and terminal effect UUIDs before CAS."
        else
            let request =
                json {| dispatchId = string locator.DispatchId
                        originalReservationId = string locator.OriginalReservationId
                        effectOperationId = string locator.EffectOperationId
                        terminalOperationId = string locator.TerminalOperationId |}
            AccountCapacityLedger.transactFiles b operationId actor "NativeTerminalRelease" request None
                (fun snapshot ->
                    verifyCurrent b root operatorKey subject projects coverage snapshot
                    |> Result.bind (fun _ ->
                        safePhase snapshot
                        |> Result.bind (fun () ->
                            stateAt b snapshot
                            |> Result.bind (fun state ->
                                match state.Records |> Array.tryFind (fun r ->
                                    r.Candidate.DispatchId = string locator.DispatchId) with
                                | None -> bad "Original native reservation not found."
                                | Some record when record.Status = "Released" ->
                                    bad "Native slot already released; replay original release UUID."
                                | Some record when record.OriginalOperationId <> string locator.OriginalReservationId
                                                   || record.Candidate.EffectOperationId <> string locator.EffectOperationId ->
                                    bad "Native terminal does not bind original reservation/effect."
                                | Some _ when isNull (box verifier) ->
                                    bad "Native terminal requires an independent verifier."
                                | Some record ->
                                    match verifier.VerifyCurrentTerminal locator with
                                    | Error message -> bad ("Terminal verifier refused release: " + message)
                                    | Ok proof when proof.DispatchId <> locator.DispatchId
                                                    || proof.OriginalReservationId <> locator.OriginalReservationId
                                                    || proof.EffectOperationId <> locator.EffectOperationId
                                                    || proof.TerminalOperationId <> locator.TerminalOperationId
                                                    || proof.ProjectId.ToString("D") <> record.Candidate.ProjectId
                                                    || proof.ProjectLedgerId.ToString("D") <> record.Candidate.ProjectLedgerId
                                                    || proof.ProjectEpoch <> record.Candidate.ProjectEpoch
                                                    || proof.CanonicalKey <> record.CanonicalKey
                                                    || not (validOid proof.ProjectHead)
                                                    || not (validOid proof.ProjectTerminalOid)
                                                    || not (validOid proof.ProviderTerminalOid)
                                                    || not (validOid record.ProjectFenceOid)
                                                    || String.IsNullOrWhiteSpace record.ProviderSessionId
                                                    || record.ProviderSessionId <> proof.ProviderSessionId ->
                                        bad "Foreign, stale, incomplete or mismatched project/provider terminal proof."
                                    | Ok proof ->
                                        terminalFromVerifiedProject projects coverage record locator proof
                                        |> Result.bind (fun () ->
                                            if state.Records |> Array.exists (fun r ->
                                                r.Status = "Released"
                                                && r.TerminalOperationId = string locator.TerminalOperationId) then
                                                bad "Terminal operation UUID already released another reservation."
                                            else
                                                let nextRecord =
                                                    { record with Status = "Released"
                                                                  Generation = record.Generation + 1L
                                                                  TerminalOperationId = string locator.TerminalOperationId }
                                                let records =
                                                    state.Records |> Array.map (fun r ->
                                                        if r.CanonicalKey = record.CanonicalKey then nextRecord else r)
                                                let next = { state with Records = records }
                                                let raw = json next
                                                Ok(snapshot.State,
                                                   json {| status = "Released"
                                                           dispatchId = record.Candidate.DispatchId
                                                           generation = nextRecord.Generation
                                                           nativeStateSha256 = digest raw |},
                                                   [ Path, raw ]))))))
