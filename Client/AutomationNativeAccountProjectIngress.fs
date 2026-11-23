module Client.AutomationNativeAccountProjectIngress

open System
open System.Text.Json
open System.Text.RegularExpressions
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.NativeSchedulerAccountReservation
open Client.NativeSchedulerRosterPolicy
open Client.ReviewWorkflow

// This file is NOT in Client.fsproj. Only the standalone, signed disposable
// fixture compiles it. No public caller-supplied record grants launch authority.
type NativeIssuerStatus = Disabled of string | Enabled
let productionStatus () =
    Disabled "Authenticated account/provider issuer absent; production ingress remains default OFF."

[<CLIMutable>]
type StartedProjectClaim =
    { OperationId: Guid
      ExpectedProjectHead: string
      AccountHead: string
      OriginalReservationOperationId: Guid
      Candidate: NativeDispatchCandidate
      DeviceId: string
      WindowRegistration: string }

[<CLIMutable>]
type StartedProjectEffect =
    { SchemaVersion: int
      ProjectId: string
      ProjectLedgerId: string
      ProjectEpoch: int
      AccountId: string
      AccountLedgerId: string
      AccountEpoch: int
      AccountHead: string
      OriginalReservationOperationId: string
      ReservationCanonicalKey: string
      Candidate: NativeDispatchCandidate
      ClaimOperationId: string
      DispatchId: string
      EffectOperationId: string
      SourceLeaseSha256: string
      DeviceId: string
      WindowRegistration: string
      Status: string
      ProviderSessionId: string
      TerminalOriginOid: string }

// These are only observations, never issuer capabilities. None can release
// native occupancy or be converted into the authenticated terminal required by B2.
type UntrustedTerminalObservation =
    | Unsupported
    | Unknown
    | AcknowledgmentLost
    | Foreign of string
    | Late
    | Duplicate
    | TabClosed

type TerminalDisposition = Denied of string

let denyUntrustedTerminal (observation: UntrustedTerminalObservation) =
    let reason =
        match observation with
        | Unsupported -> "provider issuer is unimplemented"
        | Unknown -> "unknown provider result cannot prove terminal"
        | AcknowledgmentLost -> "ACK lost is an uncertain external effect"
        | Foreign _ -> "foreign session or project is not an origin proof"
        | Late -> "late callback cannot authenticate an immutable terminal"
        | Duplicate -> "duplicate callback is not a new terminal"
        | TabClosed -> "closing a browser tab does not end the provider session"
    Denied reason

let productionClaim (_: LedgerBinding) (_: StartedProjectClaim) : Result<LedgerWrite, LedgerFailure> =
    Error(InvalidBinding "Native Started pre-send ingress default OFF: authenticated account/provider issuer unavailable.")

let private opts =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                          PropertyNameCaseInsensitive = true)
let private json value = JsonSerializer.Serialize(value, opts)
let private oid (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && Regex.IsMatch(value, "^[0-9a-f]{40}([0-9a-f]{24})?$")
let private bad why = Error(InvalidBinding("NativeStarted(default-off): " + why))
let private stale why = Error(StaleEntityVersion("NativeStarted(default-off): " + why))

let private recordPath (candidate: NativeDispatchCandidate) =
    "records/native-account-started-effect-v1/"
    + NativeSchedulerRosterPolicy.sha256 (
        candidate.ProjectLedgerId + "|" + candidate.Kind + "|"
        + candidate.TaskId + "|" + candidate.AttemptId) + ".json"

let private readCanonical<'T> binding snapshot path : Result<('T * string * string), LedgerFailure> =
    LedgerStore.readAt binding snapshot path
    |> Result.bind (function
        | Some raw, Some blob ->
            try
                let parsed = JsonSerializer.Deserialize<'T>(raw, opts)
                if isNull (box parsed) then bad("Null canonical project entity " + path)
                else Ok(parsed, raw, blob)
            with _ -> bad("Malformed canonical project entity " + path)
        | _ -> stale("Missing canonical project entity " + path))

let private receiptBinds (receipt: AccountOperationReceipt) (record: NativeReservationRecord) =
    try
        if receipt.Kind <> "NativeReserve"
           || receipt.OperationId <> record.OriginalOperationId then false
        else
            use envelope = JsonDocument.Parse(receipt.CanonicalRequestJson)
            use request = JsonDocument.Parse(envelope.RootElement.GetProperty("request").GetString())
            use outcome = JsonDocument.Parse(receipt.OriginalResultJson)
            let original = JsonSerializer.Deserialize<NativeDispatchCandidate>(
                               request.RootElement.GetProperty("candidate").GetRawText(), opts)
            envelope.RootElement.GetProperty("kind").GetString() = "NativeReserve"
            && original = record.Candidate
            && not (String.IsNullOrWhiteSpace(
                request.RootElement.GetProperty("coverageHead").GetString()))
            && outcome.RootElement.GetProperty("status").GetString() = "Reserved"
            && outcome.RootElement.GetProperty("dispatchId").GetString() = record.Candidate.DispatchId
            && outcome.RootElement.GetProperty("canonicalKey").GetString() = record.CanonicalKey
    with _ -> false

let private verifiedReservation root (account: AccountBinding) operatorKey subject
                                (projects: LedgerBinding array)
                                (coverage: NativeDisposableCoverage) (claim: StartedProjectClaim) =
    let accountRead =
        NativeSchedulerAccountReservation.readVerifiedDisposable
            root account operatorKey subject projects coverage
    match accountRead with
    | Error e -> bad (sprintf "Signed disposable account reread denied: %A" e)
    | Ok (snapshot, state) ->
        if snapshot.Head <> claim.AccountHead || coverage.AccountHead <> claim.AccountHead
           || state.AccountId <> account.AccountId.ToString("D")
           || state.LedgerId <> account.LedgerId.ToString("D")
           || state.Epoch <> account.Epoch then
            stale "Account root/head changed, revoked or foreign"
        else
            match state.Records |> Array.tryFind (fun r ->
                    r.OriginalOperationId = claim.OriginalReservationOperationId.ToString("D")) with
            | None -> stale "Original account reservation absent"
            | Some record when
                record.Status <> "Reserved" || record.Generation <> 1L
                || record.Candidate <> claim.Candidate
                || record.Candidate.ProjectId <> claim.Candidate.ProjectId
                || record.Candidate.ProjectLedgerId <> claim.Candidate.ProjectLedgerId
                || record.Candidate.ProjectEpoch <> claim.Candidate.ProjectEpoch
                || record.Candidate.Kind <> "Worker"
                || not (String.IsNullOrEmpty record.ProviderSessionId)
                || not (String.IsNullOrEmpty record.TerminalOperationId) ->
                stale "Original account occupancy/source/role/UUID differs from Started claim"
            | Some record ->
                match AccountCapacityLedger.readOperation account claim.OriginalReservationOperationId with
                | Ok (originSnapshot, Some receipt)
                    when originSnapshot.Head = claim.AccountHead && receiptBinds receipt record ->
                    Ok record
                | _ -> bad "Original native account reservation has no matching immutable CAS receipt"

let private checkStartedProject (binding: LedgerBinding) snapshot
                                (claim: StartedProjectClaim) =
    let c = claim.Candidate
    if c.ProjectId <> binding.ProjectId.ToString("D")
       || c.ProjectLedgerId <> binding.LedgerId.ToString("D")
       || c.ProjectEpoch <> binding.Epoch
       || c.Kind <> "Worker"
       || not (Regex.IsMatch(c.TaskId, "^[A-Za-z0-9_-]{1,160}$"))
       || not (Regex.IsMatch(c.AttemptId, "^[0-9a-f]{32}$"))
       || c.AssignmentGeneration < 1L
       || String.IsNullOrWhiteSpace c.SourceRef
       || not (c.SourceRef.StartsWith("refs/heads/", StringComparison.Ordinal))
       || not (oid c.SourceBaseOid) then
        bad "Foreign/non-Started Worker identity or invalid source ref"
    else
        let taskPath = "tasks/" + c.TaskId + "/task.json"
        let attemptPath = "tasks/" + c.TaskId + "/attempts/" + c.AttemptId + ".json"
        match readCanonical<ReviewTask> binding snapshot taskPath,
              readCanonical<ReviewAttempt> binding snapshot attemptPath with
        | Ok (task, _, _), Ok (attempt, raw, _) ->
            if task.TaskId <> c.TaskId || task.AttemptId <> c.AttemptId
               || task.Lifecycle <> "InProgress" || attempt.TaskId <> c.TaskId
               || attempt.AttemptId <> c.AttemptId || attempt.Phase <> "Started"
               || attempt.OwnerAgentId <> c.OwnerAgentId
               || int64 attempt.AssignmentGeneration <> c.AssignmentGeneration
               || attempt.SourceRepositoryId <> c.SourceRepositoryId
               || attempt.BaseCommit <> c.SourceBaseOid
               || attempt.SourceRef <> c.SourceRef
               || String.IsNullOrWhiteSpace attempt.TargetRef
               || NativeSchedulerRosterPolicy.sha256 raw <> c.SourceLeaseSha256 then
                stale "Canonical Started assignment/source lease is stale or rewritten"
            else Ok ()
        | Error e, _ | _, Error e -> Error e

let private claimValid (claim: StartedProjectClaim) =
    not (isNull (box claim))
    && not (isNull (box claim.Candidate))
    && claim.OperationId <> Guid.Empty
    && claim.OriginalReservationOperationId <> Guid.Empty
    && oid claim.ExpectedProjectHead && oid claim.AccountHead
    && not (String.IsNullOrWhiteSpace claim.DeviceId)
    && not (String.IsNullOrWhiteSpace claim.WindowRegistration)
    && claim.Candidate.DispatchId <> claim.Candidate.EffectOperationId
    && claim.OperationId.ToString("D") <> claim.Candidate.EffectOperationId
    && claim.OperationId.ToString("D") <> claim.Candidate.DispatchId
    && claim.OriginalReservationOperationId <> claim.OperationId

// Exposed only to the separately compiled disposable test harness. NOT linked
// into Client, not a session issuer, and does not invoke any provider effect.
// Account proof is reread both before and during the project head CAS.
let internal claimVerifiedDisposable root (account: AccountBinding) operatorKey subject
                                     (projects: LedgerBinding array)
                                     (coverage: NativeDisposableCoverage) (claim: StartedProjectClaim) =
    if not (claimValid claim) then bad "Invalid original UUID or immutable source/head binding"
    elif isNull projects || isNull (box coverage) then bad "No complete signed disposable roster"
    else
        match projects |> Array.tryFind (fun p ->
            p.ProjectId.ToString("D") = claim.Candidate.ProjectId
            && p.LedgerId.ToString("D") = claim.Candidate.ProjectLedgerId
            && p.Epoch = claim.Candidate.ProjectEpoch) with
        | None -> bad "Canonical project binding absent from original roster"
        | Some project ->
            match verifiedReservation root account operatorKey subject projects coverage claim with
            | Error e -> Error e
            | Ok _ ->
                let c = claim.Candidate
                let intent =
                    { OperationId = claim.OperationId
                      Actor = c.OwnerAgentId
                      Kind = "NativeStartedPreSendClaimDisposable"
                      RequestJson = json claim }
                LedgerStore.transact project intent (fun snapshot ->
                    if snapshot.Commit <> claim.ExpectedProjectHead then
                        stale "Project head changed before original pre-send CAS"
                    else
                        match checkStartedProject project snapshot claim with
                        | Error e -> Error e
                        | Ok () ->
                            match verifiedReservation root account operatorKey subject projects coverage claim with
                            | Error e -> Error e
                            | Ok original ->
                                let path = recordPath c
                                match LedgerStore.readAt project snapshot path with
                                | Error e -> Error e
                                | Ok (Some _, _) -> stale "Original Started attempt already has a project fence"
                                | Ok (None, _) ->
                                    let effect =
                                        { SchemaVersion = 1
                                          ProjectId = c.ProjectId
                                          ProjectLedgerId = c.ProjectLedgerId
                                          ProjectEpoch = c.ProjectEpoch
                                          AccountId = account.AccountId.ToString("D")
                                          AccountLedgerId = account.LedgerId.ToString("D")
                                          AccountEpoch = account.Epoch
                                          AccountHead = claim.AccountHead
                                          OriginalReservationOperationId =
                                              claim.OriginalReservationOperationId.ToString("D")
                                          ReservationCanonicalKey = original.CanonicalKey
                                          Candidate = c
                                          ClaimOperationId = claim.OperationId.ToString("D")
                                          DispatchId = c.DispatchId
                                          EffectOperationId = c.EffectOperationId
                                          SourceLeaseSha256 = c.SourceLeaseSha256
                                          DeviceId = claim.DeviceId
                                          WindowRegistration = claim.WindowRegistration
                                          Status = "HeldBeforeSend"
                                          ProviderSessionId = ""
                                          TerminalOriginOid = "" }
                                    Ok [ { Path = path; ExpectedBlobOid = None
                                           Content = json effect } ])
