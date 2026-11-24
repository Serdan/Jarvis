module Client.NativeSchedulerAccountReservation

open System
open System.Text.Json
open System.Text.RegularExpressions
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.ReviewWorkflow
open FsToolkit.ErrorHandling

// Account-side state machine under the existing authenticated Jarvis client.
// This is state accounting, not a provider send or activation path.
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

// A head-pinned observation; every use rereads the complete configured roster.
// This is not a provider-session completeness or terminal proof.
type NativeAccountCoverage =
    { AccountHead: string
      ProjectHeads: NativeVerifiedProjectHead array
      ObservedAtUtc: DateTimeOffset }

type NativeOriginalReservationLocator =
    { ReservationOperationId: Guid
      ReservationRequestSha256: string
      Candidate: NativeDispatchCandidate
      ExpectedGeneration: int64
      ExpectedProjectFenceOid: string
      ExpectedProviderSessionId: string }

type NativeOriginalReservationContext =
    { AccountHead: string
      ReservationOrigin: string
      ReservationParent: string
      ReservationReceipt: AccountOperationReceipt
      CurrentReservation: NativeReservationRecord
      VerifiedProjectHeads: NativeVerifiedProjectHead array }

type NativeTerminalLocator =
    { DispatchId: Guid
      OriginalReservationId: Guid
      ReservationRequestSha256: string
      ExpectedGeneration: int64
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

type INativeTerminalVerifier =
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

    let private verifyCurrent b projects (coverage: NativeAccountCoverage)
                              (snapshot: AccountSnapshot) =
        let policy =
            AccountCapacityLedger.readNativeRosterPolicy b projects
        if policy.Status <> "PolicyVerified" then
            bad ("No verified complete configured native roster: " + String.concat "; " policy.HoldReasons)
        elif isNull (box coverage)
             || coverage.AccountHead <> snapshot.Head
             || policy.VerifiedAccountHead <> Some snapshot.Head
             || isNull coverage.ProjectHeads
             || coverage.ProjectHeads.Length <> policy.VerifiedProjectHeads.Length
             || Set.ofArray coverage.ProjectHeads <> Set.ofArray policy.VerifiedProjectHeads
             || coverage.ObservedAtUtc > DateTimeOffset.UtcNow.AddSeconds(2.0)
             || coverage.ObservedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-60.0) then
            bad "Missing, stale, incomplete or mismatched account/project head observation."
        else
            match policy.Policy with
            | Some p when p.MembershipComplete -> Ok p
            | _ -> bad "Verified complete native configuration is missing."

    // Never promote caller-shaped IDs to scheduler authority. The same verified
    // project head used by the verified configured roster observation must contain
    // the actual attempt/source lease or immutable claimed review.
    let private projectAt (projects: LedgerBinding array)
                          (coverage: NativeAccountCoverage) projectId =
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

    // One read-only canonical reviewer-claim verifier. The host and account
    // reservation use the same rule against their exact verified project head.
    // No project writer or new authentication mechanism lives here.
    let internal verifyReviewerClaim (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                     (c: NativeDispatchCandidate) =
        if isNull (box binding) || isNull (box snapshot) || isNull (box snapshot.Header)
           || isNull (box c) || c.Kind <> "Reviewer"
           || c.ProjectId <> string binding.ProjectId
           || c.ProjectLedgerId <> string binding.LedgerId || c.ProjectEpoch <> binding.Epoch
           || snapshot.Header.ProjectId <> c.ProjectId
           || snapshot.Header.LedgerId <> c.ProjectLedgerId || snapshot.Header.Epoch <> c.ProjectEpoch
           || snapshot.Header.Status <> "active" || not (validOid snapshot.Commit)
           || String.IsNullOrWhiteSpace c.OwnerAgentId || not (validGuidN c.ReviewId)
           || c.ReviewVersion < 2L || c.ReviewVersion > int64 Int32.MaxValue
           || not (validOid c.ReviewedHeadOid) || not (validGuid c.ClaimOperationId)
           || String.IsNullOrEmpty c.ReviewRef || c.AssignmentGeneration <> 0L
           || not (String.IsNullOrEmpty c.TaskId) || not (String.IsNullOrEmpty c.AttemptId) then
            bad "Reviewer candidate lacks exact project, claim, source or role identity."
        else
            match Client.RoleInstructions.RoleInstructions.resolveAt binding snapshot c.OwnerAgentId with
            | Ok roles when roles.RoleIds |> List.contains "Reviewer" ->
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
                            | _ -> bad "Reviewer claim operation is absent, stale or mismatched.")
            | _ -> bad "Claimed reviewer is not a registered member of the canonical Reviewer role."

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
            else verifyReviewerClaim binding snapshot c)

    // A terminal verifier's Ok is not an issuer. The original send fence,
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

    // These are internal operations of the already authenticated client.
    // The runtime supplies the actual configured bindings; every read and
    // write rechecks authoritative Git state instead of trusting the caller's
    // observation as a permission.
    let internal readVerified b projects coverage =
        AccountCapacityLedger.fetch b
        |> Result.bind (fun snapshot ->
            verifyCurrent b projects coverage snapshot
            |> Result.bind (fun _ -> stateAt b snapshot |> Result.map (fun native -> snapshot, native)))

    let private originalProjectPresent (coverage: NativeAccountCoverage) (record: NativeReservationRecord) =
        coverage.ProjectHeads
        |> Array.filter (fun head ->
            head.ProjectId = record.Candidate.ProjectId
            && head.LedgerId = record.Candidate.ProjectLedgerId
            && head.Epoch = record.Candidate.ProjectEpoch)
        |> Array.length = 1

    let private verifiedOriginalReceipt b snapshot (record: NativeReservationRecord)
                                        operationId requestSha256 =
        result {
            let! receipt, origin, parent, previous, raw =
                AccountCapacityLedger.readNativeReserveOrigin b snapshot operationId
            let! checkedAppend =
                try
                    use envelope = JsonDocument.Parse(receipt.CanonicalRequestJson)
                    let e = envelope.RootElement
                    use request = JsonDocument.Parse(e.GetProperty("request").GetString())
                    let req = request.RootElement
                    let candidate = req.GetProperty("candidate").Deserialize<NativeDispatchCandidate>(options)
                    use outcome = JsonDocument.Parse(receipt.OriginalResultJson)
                    let res = outcome.RootElement
                    let original = JsonSerializer.Deserialize<NativeReservationState>(raw, options)
                    let prior =
                        match previous with
                        | None -> Ok [||]
                        | Some text ->
                            let parsed = JsonSerializer.Deserialize<NativeReservationState>(text, options)
                            if stateValid b parsed then Ok parsed.Records
                            else bad "Original reservation predecessor is invalid."
                    prior |> Result.bind (fun previousRecords ->
                        if not (stateValid b original)
                           || receipt.OperationId <> string operationId
                           || receipt.RequestSha256 <> requestSha256
                           || e.GetProperty("accountId").GetString() <> string b.AccountId
                           || e.GetProperty("kind").GetString() <> "NativeReserve"
                           || e.GetProperty("actor").GetString() <> receipt.Actor
                           || req.GetProperty("coverageHead").GetString() <> parent
                           || candidate <> record.Candidate
                           || res.GetProperty("status").GetString() <> "Reserved"
                           || res.GetProperty("dispatchId").GetString() <> record.Candidate.DispatchId
                           || res.GetProperty("canonicalKey").GetString() <> record.CanonicalKey
                           || res.GetProperty("nativeStateSha256").GetString() <> digest raw then
                            bad "Original receipt, account/project lineage or candidate differs."
                        else
                            let initial =
                                { record with Status = "Reserved"; Generation = 1L
                                              ProjectFenceOid = ""; ProviderSessionId = ""; TerminalOperationId = "" }
                            if original.Records <> Array.append previousRecords [| initial |] then
                                bad "Original receipt did not create the exact initial reservation."
                            else Ok ())
                with _ -> bad "Malformed original native reserve payload."
            return receipt, origin, parent
        }

    let private originalContext b snapshot state coverage (locator: NativeOriginalReservationLocator) =
        if isNull (box locator) || isNull (box locator.Candidate)
           || locator.ReservationOperationId = Guid.Empty || not (validDigest locator.ReservationRequestSha256)
           || locator.ExpectedGeneration < 1L then
            bad "Exact original reservation locator required."
        else
            match state.Records |> Array.tryFind (fun r ->
                r.OriginalOperationId = string locator.ReservationOperationId) with
            | Some record when record.Candidate = locator.Candidate
                               && record.Generation = locator.ExpectedGeneration
                               && record.ProjectFenceOid = locator.ExpectedProjectFenceOid
                               && record.ProviderSessionId = locator.ExpectedProviderSessionId ->
                if not (originalProjectPresent coverage record) then
                    bad "Original reservation project/ledger/epoch is absent from the verified current project heads."
                else
                    verifiedOriginalReceipt b snapshot record locator.ReservationOperationId locator.ReservationRequestSha256
                    |> Result.map (fun (receipt, origin, parent) ->
                        { AccountHead = snapshot.Head
                          ReservationOrigin = origin; ReservationParent = parent
                          ReservationReceipt = receipt; CurrentReservation = record
                          VerifiedProjectHeads = coverage.ProjectHeads })
            | _ -> bad "Current reservation identity, generation, project fence or session differs."

    // Exact original account evidence plus the original project tuple at a
    // current verified head. A legitimate roster removal can make this read
    // unavailable, but cannot erase the original reservation or free capacity.
    let internal readOriginal b projects coverage locator =
        readVerified b projects coverage
        |> Result.bind (fun (snapshot, state) ->
            originalContext b snapshot state coverage locator
            |> Result.bind (fun context ->
                readVerified b projects coverage
                |> Result.bind (fun (current, currentState) ->
                    if current.Head <> snapshot.Head || currentState <> state then
                        bad "Account state changed during original reservation verification."
                    else Ok context)))

    let internal reserve (b: AccountBinding) projects
                         (coverage: NativeAccountCoverage) operationId actor candidate =
        // An absent caller observation is a hold, never a null dereference.
        let observedHead = if isNull (box coverage) then "" else coverage.AccountHead
        let request = json {| candidate = candidate; coverageHead = observedHead |}
        AccountCapacityLedger.transactFiles b operationId actor "NativeReserve" request None
            (fun snapshot ->
                verifyCurrent b projects coverage snapshot
                |> Result.bind (fun policy ->
                    safePhase snapshot |> Result.bind (fun () ->
                        if not (candidateValid b policy candidate) then
                            bad "Native candidate lacks exact worker source lease, reviewer claim or configured roster membership."
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

    // Internal state accounting only: the original reserve receipt, exact
    // candidate and generation must still match, even after roster succession.
    let internal transition (b: AccountBinding) projects coverage operationId actor
                            (locator: NativeOriginalReservationLocator)
                            toStatus projectFenceOid providerSessionId =
        if isNull (box locator) then bad "Exact original reservation locator required."
        else
            let request = json {| original = locator; toStatus = toStatus
                                  projectFenceOid = projectFenceOid; providerSessionId = providerSessionId |}
            AccountCapacityLedger.transactFiles b operationId actor "NativeTransition" request None
                (fun snapshot -> result {
                    let! _ = verifyCurrent b projects coverage snapshot
                    do! safePhase snapshot
                    let! state = stateAt b snapshot
                    let! context = originalContext b snapshot state coverage locator
                    let record = context.CurrentReservation
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
                    if not allowed then
                        return! bad "Native status transition is not monotonic."
                    if not (validOid projectFenceOid)
                       || (record.ProjectFenceOid <> "" && record.ProjectFenceOid <> projectFenceOid)
                       || (List.contains toStatus [ "Active"; "Unknown" ]
                           && String.IsNullOrWhiteSpace providerSessionId)
                       || (record.ProviderSessionId <> "" && record.ProviderSessionId <> providerSessionId) then
                        return! bad "Missing original project-send fence or trusted session identity."
                    let nextRecord =
                        { record with Status = toStatus; Generation = record.Generation + 1L
                                      ProjectFenceOid = projectFenceOid; ProviderSessionId = providerSessionId }
                    let records =
                        state.Records |> Array.map (fun r ->
                            if r.CanonicalKey = record.CanonicalKey then nextRecord else r)
                    let next = { state with Records = records }
                    let raw = json next
                    return snapshot.State,
                           json {| status = toStatus; dispatchId = record.Candidate.DispatchId
                                   generation = nextRecord.Generation; nativeStateSha256 = digest raw |},
                           [ Path, raw ]
                })

    let internal release (b: AccountBinding) projects coverage (verifier: INativeTerminalVerifier)
                         operationId actor (locator: NativeTerminalLocator) =
        if isNull (box locator) || locator.DispatchId = Guid.Empty
           || locator.OriginalReservationId = Guid.Empty
           || not (validDigest locator.ReservationRequestSha256) || locator.ExpectedGeneration < 1L
           || locator.EffectOperationId = Guid.Empty || locator.TerminalOperationId = Guid.Empty then
            bad "Exact original and terminal UUIDs, receipt and generation required before CAS."
        else
            let request =
                json {| dispatchId = string locator.DispatchId
                        originalReservationId = string locator.OriginalReservationId
                        reservationRequestSha256 = locator.ReservationRequestSha256
                        expectedGeneration = locator.ExpectedGeneration
                        effectOperationId = string locator.EffectOperationId
                        terminalOperationId = string locator.TerminalOperationId |}
            AccountCapacityLedger.transactFiles b operationId actor "NativeTerminalRelease" request None
                (fun snapshot -> result {
                    let! _ = verifyCurrent b projects coverage snapshot
                    do! safePhase snapshot
                    let! state = stateAt b snapshot
                    let! record =
                        match state.Records |> Array.tryFind (fun r ->
                            r.Candidate.DispatchId = string locator.DispatchId) with
                        | Some r when r.Status <> "Released"
                                      && r.OriginalOperationId = string locator.OriginalReservationId
                                      && r.Candidate.EffectOperationId = string locator.EffectOperationId
                                      && r.Generation = locator.ExpectedGeneration -> Ok r
                        | _ -> bad "Original reservation, effect, generation or occupied status differs."
                    if not (originalProjectPresent coverage record) then
                        return! bad "Original reservation project/ledger/epoch is not currently verified."
                    let! _ = verifiedOriginalReceipt b snapshot record locator.OriginalReservationId locator.ReservationRequestSha256
                    if isNull (box verifier) then
                        return! bad "Native terminal requires actual verified evidence."
                    let! proof = verifier.VerifyCurrentTerminal locator
                                 |> Result.mapError (fun message ->
                                     AccountPolicyNotAuthoritative ("Terminal verifier refused release: " + message))
                    if isNull (box proof) || proof.DispatchId <> locator.DispatchId
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
                       || record.ProviderSessionId <> proof.ProviderSessionId then
                        return! bad "Foreign, stale, incomplete or mismatched project/provider terminal proof."
                    do! terminalFromVerifiedProject projects coverage record locator proof
                    if state.Records |> Array.exists (fun r ->
                        r.Status = "Released" && r.TerminalOperationId = string locator.TerminalOperationId) then
                        return! bad "Terminal operation UUID already released another reservation."
                    let nextRecord =
                        { record with Status = "Released"
                                      Generation = record.Generation + 1L
                                      TerminalOperationId = string locator.TerminalOperationId }
                    let records =
                        state.Records |> Array.map (fun r ->
                            if r.CanonicalKey = record.CanonicalKey then nextRecord else r)
                    let next = { state with Records = records }
                    let raw = json next
                    return snapshot.State,
                           json {| status = "Released"; dispatchId = record.Candidate.DispatchId
                                   generation = nextRecord.Generation; nativeStateSha256 = digest raw |},
                           [ Path, raw ]
                })
