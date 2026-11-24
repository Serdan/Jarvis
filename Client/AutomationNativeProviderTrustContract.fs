module Client.AutomationNativeProviderTrustContract

open System
open System.Text.RegularExpressions
open Client.NativeSchedulerAccountReservation

#if STAGE_B_DISPOSABLE_TEST_ONLY
open Client.NativeProjectEffectFence
#endif

// This file is intentionally absent from Client.fsproj. It neither creates a
// host trust root nor authorizes BrowserBridge/provider calls. Only a standalone
// disposable test project links it to the accepted native account/B1 types.
type HoldReason =
    | MissingObservation
    | InvalidAccountHead
    | ForeignCandidate
    | StaleGeneration
    | NotFreshReservation
    | WorkerNotStarted
    | InvalidAccountRecord
    | StartedReadyMismatch
    | DisposableReceiptNotAuthority
    | ForeignProjectFence
    | InvalidTerminalProof
    | VerifiedOutcomeUnavailable
    | EventRequiresReconciliation
    | UnknownHostEvent

// The record is a structural projection of caller-supplied values, explicitly
// NOT a capability. Its private constructor cannot stand in for an independent
// account ledger fetch, configured roster or project operation origin.
type NonAuthorizingAccountShape =
    private
        { Candidate: NativeDispatchCandidate
          CanonicalKey: string
          AccountHead: string
          ReservationGeneration: int64 }
    member x.Key = x.CanonicalKey
    member x.Generation = x.ReservationGeneration

type AccountObservation =
    { ExpectedCandidate: NativeDispatchCandidate
      Reservation: NativeReservationRecord
      AccountHead: string
      ExpectedGeneration: int64
      CanonicalWorkerPhase: string }

let private matches pattern (value: string) =
    not (String.IsNullOrWhiteSpace value) && Regex.IsMatch(value, pattern)

let private oid value = matches "^[0-9a-f]{40}([0-9a-f]{24})?$" value
let private digest value = matches "^[0-9a-f]{64}$" value
let private guidN value =
    matches "^[0-9a-f]{32}$" value && value <> String.replicate 32 "0"

let private guidD value =
    let mutable parsed = Guid.Empty
    not (String.IsNullOrWhiteSpace value)
    && Guid.TryParse(value, &parsed)
    && parsed <> Guid.Empty
    && value = parsed.ToString("D")

let private candidateShape (c: NativeDispatchCandidate) =
    not (isNull (box c))
    && c.Kind = "Worker"
    && guidD c.DispatchId && guidD c.EffectOperationId
    && c.DispatchId <> c.EffectOperationId
    && guidD c.ProjectId && guidD c.ProjectLedgerId
    && c.ProjectEpoch > 0
    && not (String.IsNullOrWhiteSpace c.OwnerAgentId)
    && matches "^[A-Za-z0-9_-]{1,80}$" c.TaskId
    && guidN c.AttemptId && c.AssignmentGeneration > 0L
    && digest c.SourceRepositoryId && oid c.SourceBaseOid
    && not (String.IsNullOrWhiteSpace c.SourceRef)
    && c.SourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
    && not (c.SourceRef.Contains("..", StringComparison.Ordinal))
    && digest c.SourceLeaseSha256
    && c.ReviewId = "" && c.ReviewVersion = 0L
    && c.ReviewRef = "" && c.ReviewedHeadOid = "" && c.ClaimOperationId = ""

/// Checks untrusted *shape* only. Does not fetch an account HEAD, authorize
/// candidate ownership, verify configured membership, or make any provider call.
let inspectAccount (observation: AccountObservation) : Result<NonAuthorizingAccountShape, HoldReason list> =
    if isNull (box observation) || isNull (box observation.Reservation)
       || isNull (box observation.ExpectedCandidate) then
        Error [ MissingObservation ]
    else
        let record = observation.Reservation
        let failures =
            [ if not (oid observation.AccountHead) then InvalidAccountHead
              if not (candidateShape observation.ExpectedCandidate)
                 || record.Candidate <> observation.ExpectedCandidate then ForeignCandidate
              if record.Generation <> observation.ExpectedGeneration
                 || record.Generation < 1L then StaleGeneration
              if record.Status <> "Reserved" then NotFreshReservation
              if observation.CanonicalWorkerPhase <> "Started" then WorkerNotStarted
              if not (digest record.CanonicalKey)
                 || not (guidD record.OriginalOperationId)
                 || record.ProjectFenceOid <> ""
                 || record.ProviderSessionId <> ""
                 || record.TerminalOperationId <> "" then InvalidAccountRecord ]
        match failures with
        | [] ->
            Ok { Candidate = observation.ExpectedCandidate
                 CanonicalKey = record.CanonicalKey
                 AccountHead = observation.AccountHead
                 ReservationGeneration = record.Generation }
        | errors -> Error errors

#if STAGE_B_DISPOSABLE_TEST_ONLY
type B1Observation =
    { Account: AccountObservation
      FakeClaim: PreSendClaim
      FakeProject: NativeEffectRecord
      CanonicalProjectWorkerPhase: string }

/// The accepted B1 module is intentionally fake and requires Ready where the
/// native account fixture requires Started. Even matching identity/shape MUST
/// remain a HOLD. This function never returns production authorization.
let inspectB1 (observation: B1Observation) : Result<unit, HoldReason list> =
    if isNull (box observation)
       || isNull (box observation.Account)
       || isNull (box observation.Account.Reservation)
       || isNull (box observation.Account.ExpectedCandidate)
       || isNull (box observation.FakeClaim)
       || isNull (box observation.FakeClaim.Candidate)
       || isNull (box observation.FakeClaim.Account)
       || isNull (box observation.FakeProject) then
        Error [ MissingObservation; DisposableReceiptNotAuthority ]
    else
        let claim = observation.FakeClaim
        let project = observation.FakeProject
        let account = observation.Account
        let failures =
            [ yield DisposableReceiptNotAuthority
              match inspectAccount account with
              | Error reasons -> yield! reasons
              | Ok _ -> ()
              if account.CanonicalWorkerPhase = "Started"
                 && observation.CanonicalProjectWorkerPhase = "Ready" then
                  yield StartedReadyMismatch
              if claim.DispatchId.ToString("D") <> account.ExpectedCandidate.DispatchId
                 || claim.EffectId.ToString("D") <> account.ExpectedCandidate.EffectOperationId
                 || project.DispatchId <> claim.DispatchId
                 || project.EffectId <> claim.EffectId
                 || project.ClaimOperationId <> claim.OperationId
                 || project.Candidate <> claim.Candidate
                 || project.Account <> claim.Account
                 || project.ProjectId <> account.ExpectedCandidate.ProjectId
                 || project.ProjectLedgerId <> account.ExpectedCandidate.ProjectLedgerId
                 || project.ProjectEpoch <> account.ExpectedCandidate.ProjectEpoch
                 || claim.Candidate.TaskId <> account.ExpectedCandidate.TaskId
                 || claim.Candidate.AttemptId <> account.ExpectedCandidate.AttemptId
                 || int64 claim.Candidate.AssignmentGeneration <> account.ExpectedCandidate.AssignmentGeneration
                 || claim.Candidate.SourceRepositoryId <> account.ExpectedCandidate.SourceRepositoryId
                 || claim.Candidate.SourceRef <> account.ExpectedCandidate.SourceRef
                 || claim.Candidate.BaseCommit <> account.ExpectedCandidate.SourceBaseOid
                 || claim.Account.OriginalReservationOperationId <> account.Reservation.OriginalOperationId
                 || claim.Account.ProjectId <> account.ExpectedCandidate.ProjectId
                 || claim.Account.Marker <> "STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
                 then yield ForeignProjectFence ]
        Error failures
#endif

type TerminalObservation =
    { Reservation: NativeReservationRecord
      Locator: NativeTerminalLocator
      Proof: NativeTerminalProof }

// A fully matching terminal *shape* is still caller-shaped. It cannot
// authenticate original Git origin, provider session, or a once-only release.
type NonAuthorizingTerminalShape =
    private
        { ProviderSessionId: string
          TerminalOperationId: Guid }
    member x.SessionId = x.ProviderSessionId

let inspectTerminal (observation: TerminalObservation) : Result<NonAuthorizingTerminalShape, HoldReason list> =
    if isNull (box observation) || isNull (box observation.Reservation)
       || isNull (box observation.Reservation.Candidate)
       || isNull (box observation.Locator) || isNull (box observation.Proof) then
        Error [ MissingObservation ]
    else
        let r, l, p = observation.Reservation, observation.Locator, observation.Proof
        if l.DispatchId = Guid.Empty || l.OriginalReservationId = Guid.Empty
           || l.EffectOperationId = Guid.Empty || l.TerminalOperationId = Guid.Empty
           || not (digest l.ReservationRequestSha256)
           || l.ExpectedGeneration <> r.Generation || r.Generation < 1L
           || r.Status <> "Active"
           || r.Candidate.DispatchId <> string l.DispatchId
           || r.OriginalOperationId <> string l.OriginalReservationId
           || r.Candidate.EffectOperationId <> string l.EffectOperationId
           || p.DispatchId <> l.DispatchId
           || p.OriginalReservationId <> l.OriginalReservationId
           || p.EffectOperationId <> l.EffectOperationId
           || p.TerminalOperationId <> l.TerminalOperationId
           || p.ProjectId.ToString("D") <> r.Candidate.ProjectId
           || p.ProjectLedgerId.ToString("D") <> r.Candidate.ProjectLedgerId
           || p.ProjectEpoch <> r.Candidate.ProjectEpoch
           || p.CanonicalKey <> r.CanonicalKey
           || String.IsNullOrWhiteSpace p.ProviderSessionId
           || p.ProviderSessionId <> r.ProviderSessionId
           || not (oid r.ProjectFenceOid)
           || not (oid p.ProjectHead)
           || not (oid p.ProjectTerminalOid)
           || not (oid p.ProviderTerminalOid) then
            Error [ InvalidTerminalProof ]
        else
            Ok { ProviderSessionId = p.ProviderSessionId
                 TerminalOperationId = l.TerminalOperationId }

/// No callback or boolean proof promotes a shape to a release capability.
let authorizeRelease (_: NonAuthorizingTerminalShape) : Result<unit, HoldReason list> =
    Error [ VerifiedOutcomeUnavailable ]

/// Explicit names prevent a lost ACK, tab closure, unknown provider state,
/// revocation, late delivery or losing client from becoming terminal or resend.
let evaluateHostEvent eventName : Result<unit, HoldReason list> =
    match eventName with
    | "LostAck" | "TabClosed" | "Unknown" | "Revoked" | "LateAck"
    | "Duplicate" | "SecondClientCASLoser" ->
        Error [ VerifiedOutcomeUnavailable; EventRequiresReconciliation ]
    | _ -> Error [ VerifiedOutcomeUnavailable; UnknownHostEvent ]
