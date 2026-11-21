module Client.NativeSchedulerAccountReservation

open System
open System.Text.Json
open System.Text.RegularExpressions
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy

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
                        && validGuid c.AttemptId && c.AssignmentGeneration > 0L
                        && validDigest c.SourceRepositoryId
                        && validOid c.SourceBaseOid
                        && c.SourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                        && validDigest c.SourceLeaseSha256
                        && String.IsNullOrEmpty c.ReviewId
                        && String.IsNullOrEmpty c.ReviewRef
                        && String.IsNullOrEmpty c.ClaimOperationId
                    else
                        not (String.IsNullOrWhiteSpace c.ReviewId)
                        && c.ReviewVersion > 0L
                        && validOid c.ReviewedHeadOid
                        && c.ReviewRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal)
                        && validGuid c.ClaimOperationId
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
            && List.contains r.Candidate.Kind kinds
            && List.contains r.Status
                 [ "Reserved"; "Dispatching"; "Submitted"; "Uncertain"; "Active"; "Unknown"; "Released" ]
            && (r.Status <> "Released" || validGuid r.TerminalOperationId))
        && (s.Records |> Array.distinctBy _.CanonicalKey |> Array.length) = s.Records.Length
        && (s.Records |> Array.distinctBy _.OriginalOperationId |> Array.length) = s.Records.Length
        && (s.Records |> Array.distinctBy (fun r -> r.Candidate.DispatchId) |> Array.length) = s.Records.Length
        && (s.Records |> Array.distinctBy (fun r -> r.Candidate.EffectOperationId) |> Array.length) = s.Records.Length

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
                                           [ Path, raw ])))))

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
        let request =
            json {| dispatchId = string locator.DispatchId
                    originalReservationId = string locator.OriginalReservationId
                    effectOperationId = string locator.EffectOperationId
                    terminalOperationId = string locator.TerminalOperationId |}
        AccountCapacityLedger.transactFiles b operationId actor "NativeTerminalRelease" request None
            (fun snapshot ->
                verifyCurrent b root operatorKey subject projects coverage snapshot
                |> Result.bind (fun _ ->
                    safePhase snapshot |> Result.bind (fun () ->
                        stateAt b snapshot |> Result.bind (fun state ->
                            match state.Records |> Array.tryFind (fun r ->
                                r.Candidate.DispatchId = string locator.DispatchId) with
                            | None -> bad "Original native reservation not found."
                            | Some record when record.Status = "Released" ->
                                bad "Native slot already released; use original release operation UUID to replay."
                            | Some record when record.OriginalOperationId <> string locator.OriginalReservationId
                                               || record.Candidate.EffectOperationId <> string locator.EffectOperationId ->
                                bad "Native terminal does not bind original reservation/effect."
                            | Some record when isNull (box verifier) ->
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
                                                || (record.ProviderSessionId <> ""
                                                    && record.ProviderSessionId <> proof.ProviderSessionId) ->
                                    bad "Foreign, stale, incomplete or mismatched project/provider terminal proof."
                                | Ok _ ->
                                    let nextRecord =
                                        { record with Status = "Released"; Generation = record.Generation + 1L
                                                      TerminalOperationId = string locator.TerminalOperationId }
                                    let records =
                                        state.Records |> Array.map (fun r ->
                                            if r.CanonicalKey = record.CanonicalKey then nextRecord else r)
                                    let next = { state with Records = records }
                                    let raw = json next
                                    Ok(snapshot.State,
                                       json {| status = "Released"; dispatchId = record.Candidate.DispatchId
                                               generation = nextRecord.Generation; nativeStateSha256 = digest raw |},
                                       [ Path, raw ])))))
