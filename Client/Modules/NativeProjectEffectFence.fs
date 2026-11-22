#if STAGE_B_DISPOSABLE_TEST_ONLY
module Client.NativeProjectEffectFence

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Client.LedgerStore
open FsToolkit.ErrorHandling

// B1 deliberately cannot be built into the production Client assembly.
// The only caller is a disposable .NET 11 Release harness with this compile constant.
[<CLIMutable>]
type FakeAccountReservationReceipt =
    { Marker: string
      AccountLedgerId: string
      AccountEpoch: int
      AccountHead: string
      OriginalReservationOperationId: string
      ReservationId: string
      ProjectId: string
      Kind: string }

[<CLIMutable>]
type NativeCandidate =
    { Kind: string
      TaskId: string
      AttemptId: string
      ReviewId: string
      OwnerAgentId: string
      AssignmentGeneration: int
      ReviewVersion: int
      SourceRepositoryId: string
      SourceRef: string
      BaseCommit: string
      TargetRef: string
      ReviewRef: string
      ReviewedHeadOid: string }

[<CLIMutable>]
type PreSendClaim =
    { OperationId: Guid
      ExpectedProjectHead: string
      Account: FakeAccountReservationReceipt
      Candidate: NativeCandidate
      DispatchId: Guid
      EffectId: Guid
      DeviceId: string
      WindowRegistration: string }

[<CLIMutable>]
type NativeEffectRecord =
    { SchemaVersion: int
      ProjectId: string
      ProjectLedgerId: string
      ProjectEpoch: int
      Account: FakeAccountReservationReceipt
      Candidate: NativeCandidate
      ClaimOperationId: Guid
      DispatchId: Guid
      EffectId: Guid
      DeviceId: string
      WindowRegistration: string
      Status: string
      RequestSha256: string
      StartOperationId: Guid
      TerminalOperationId: Guid
      ProviderSessionId: string }

[<CLIMutable>]
type FakeProviderTerminal =
    { Marker: string
      EffectId: Guid
      ProviderSessionId: string
      Status: string }

[<CLIMutable>]
type CanonicalTask =
    { TaskId: string
      Lifecycle: string
      AttemptId: string }

[<CLIMutable>]
type CanonicalAttempt =
    { TaskId: string
      AttemptId: string
      OwnerAgentId: string
      Phase: string
      AssignmentGeneration: int
      SourceRepositoryId: string
      SourceRef: string
      BaseCommit: string
      TargetRef: string }

[<CLIMutable>]
type CanonicalReview =
    { TaskId: string
      ReviewId: string
      ReviewerId: string
      Version: int
      Status: string
      ReviewRef: string
      ReviewedHeadOid: string
      TaskRef: string }

let private opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
let private oid = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$")
let private safeTask = Regex("^[A-Za-z0-9_-]{1,160}$")
let private reviewId = Regex("^[0-9a-f]{32}$")
let private fullRef = Regex("^refs/heads/[A-Za-z0-9][A-Za-z0-9_./-]*$")
let private notBlank (value: string) = not (String.IsNullOrWhiteSpace value)
let private sha256 (value: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()
let private serialize value = JsonSerializer.Serialize(value, opts)
let private reject why = Error(InvalidBinding("NativeStageB(default-off): " + why))
let private stale why = Error(StaleEntityVersion("NativeStageB(default-off): " + why))

let private recordPath (c: NativeCandidate) =
    let key =
        if c.Kind = "Worker" then "Worker|" + c.TaskId + "|" + c.AttemptId
        else "Reviewer|" + c.TaskId + "|" + c.ReviewId
    "records/native-project-effect-v1/" + sha256 key + ".json"

let private read<'T> binding snapshot path : Result<('T * string), LedgerFailure> =
    result {
        let! content, blob = LedgerStore.readAt binding snapshot path
        match content, blob with
        | Some content, Some blob ->
            try
                let value = JsonSerializer.Deserialize<'T>(content, opts)
                if isNull (box value) then return! reject ("Invalid canonical entity " + path)
                return value, blob
            with error -> return! reject ("Invalid canonical JSON " + path + ": " + error.GetType().Name + " " + error.Message)
        | _ -> return! stale ("Missing canonical entity " + path)
    }

let private checkCandidate binding snapshot (c: NativeCandidate) = result {
    if not (safeTask.IsMatch c.TaskId) || notBlank c.OwnerAgentId = false
       || not (fullRef.IsMatch c.SourceRef) || c.SourceRef.Contains("..")
       || not (oid.IsMatch c.ReviewedHeadOid || oid.IsMatch c.BaseCommit)
       || not (fullRef.IsMatch c.TargetRef) then
        return! reject "Candidate path/ref/identity invalid"
    if c.Kind = "Worker" then
        if not (reviewId.IsMatch c.AttemptId) || c.AssignmentGeneration < 1
           || notBlank c.SourceRepositoryId = false || not (oid.IsMatch c.BaseCommit) then
            return! reject "Worker source lease invalid"
        let! task, _ = read<CanonicalTask> binding snapshot ("tasks/" + c.TaskId + "/task.json")
        let! attempt, _ = read<CanonicalAttempt> binding snapshot
                            ("tasks/" + c.TaskId + "/attempts/" + c.AttemptId + ".json")
        if task.TaskId <> c.TaskId || task.AttemptId <> c.AttemptId
           || task.Lifecycle <> "InProgress" || attempt.TaskId <> c.TaskId
           || attempt.AttemptId <> c.AttemptId || attempt.Phase <> "Ready"
           || attempt.OwnerAgentId <> c.OwnerAgentId
           || attempt.AssignmentGeneration <> c.AssignmentGeneration
           || attempt.SourceRepositoryId <> c.SourceRepositoryId
           || attempt.SourceRef <> c.SourceRef || attempt.BaseCommit <> c.BaseCommit
           || attempt.TargetRef <> c.TargetRef then
            return! stale "Worker canonical attempt or source lease changed"
        do! LedgerStore.verifySourceCommit binding c.SourceRef c.BaseCommit
    elif c.Kind = "Reviewer" then
        if not (reviewId.IsMatch c.ReviewId) || c.ReviewVersion < 1
           || not (oid.IsMatch c.ReviewedHeadOid) || not (fullRef.IsMatch c.ReviewRef) then
            return! reject "Reviewer frozen claim invalid"
        let! review, _ = read<CanonicalReview> binding snapshot
                            ("tasks/" + c.TaskId + "/reviews/" + c.ReviewId + "/review.json")
        if review.TaskId <> c.TaskId || review.ReviewId <> c.ReviewId
           || review.Version <> c.ReviewVersion || review.Status <> "Claimed"
           || review.ReviewerId <> c.OwnerAgentId || review.ReviewRef <> c.ReviewRef
           || review.ReviewedHeadOid <> c.ReviewedHeadOid
           || review.TaskRef <> c.SourceRef then
            return! stale "Reviewer frozen claim/version/ref changed"
        do! LedgerStore.fetchReviewRef binding c.ReviewRef c.ReviewedHeadOid
    else return! reject "Unknown candidate kind"
}

let private checkAccount (binding: LedgerBinding) (r: FakeAccountReservationReceipt) (c: NativeCandidate) =
    r.Marker = "STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
    && notBlank r.AccountLedgerId && r.AccountEpoch > 0
    && oid.IsMatch r.AccountHead
    && (match Guid.TryParse r.OriginalReservationOperationId with
        | true, id -> id <> Guid.Empty
        | _ -> false)
    && notBlank r.ReservationId
    && r.ProjectId = binding.ProjectId.ToString("D")
    && r.Kind = c.Kind

let private validateClaim (binding: LedgerBinding) (claim: PreSendClaim) =
    claim.OperationId <> Guid.Empty && claim.DispatchId <> Guid.Empty
    && claim.EffectId <> Guid.Empty && claim.OperationId <> claim.DispatchId
    && claim.OperationId <> claim.EffectId && claim.DispatchId <> claim.EffectId
    && oid.IsMatch claim.ExpectedProjectHead
    && notBlank claim.DeviceId && notBlank claim.WindowRegistration
    && checkAccount binding claim.Account claim.Candidate

let private makeRecord (binding: LedgerBinding) (claim: PreSendClaim) : NativeEffectRecord =
    { SchemaVersion = 1
      ProjectId = binding.ProjectId.ToString("D")
      ProjectLedgerId = binding.LedgerId.ToString("D")
      ProjectEpoch = binding.Epoch
      Account = claim.Account
      Candidate = claim.Candidate
      ClaimOperationId = claim.OperationId
      DispatchId = claim.DispatchId
      EffectId = claim.EffectId
      DeviceId = claim.DeviceId
      WindowRegistration = claim.WindowRegistration
      Status = "Claimed"
      RequestSha256 = sha256 (serialize claim)
      StartOperationId = Guid.Empty
      TerminalOperationId = Guid.Empty
      ProviderSessionId = "" }

let claim (binding: LedgerBinding) (claim: PreSendClaim) =
    if not (validateClaim binding claim) then reject "Invalid candidate/account fake receipt or effect UUID"
    else
        let c = claim.Candidate
        let path = recordPath c
        let intent = { OperationId = claim.OperationId; Actor = c.OwnerAgentId
                       Kind = "NativeStageBClaimTestOnly"; RequestJson = serialize claim }
        LedgerStore.transact binding intent (fun snapshot -> result {
            if snapshot.Commit <> claim.ExpectedProjectHead then
                return! stale "Project head changed before pre-send CAS"
            do! checkCandidate binding snapshot c
            let! existing, _ = LedgerStore.readAt binding snapshot path
            if existing.IsSome then return! stale "Candidate already fenced by an original effect"
            return [ { Path = path; ExpectedBlobOid = None
                       Content = serialize (makeRecord binding claim) } ]
        })

let private update
    (binding: LedgerBinding)
    (candidate: NativeCandidate)
    (effectId: Guid)
    (operationId: Guid)
    (kind: string) (expectedStatus: string) (nextStatus: string)
    (extraValidate: NativeEffectRecord -> Result<unit, LedgerFailure>)
    (modify: NativeEffectRecord -> NativeEffectRecord) =
    if operationId = Guid.Empty || effectId = Guid.Empty then reject "Empty transition UUID"
    else
        let path = recordPath candidate
        let intent = { OperationId = operationId; Actor = candidate.OwnerAgentId
                       Kind = kind
                       RequestJson = serialize {| candidate = candidate; effectId = effectId; expectedStatus = expectedStatus; nextStatus = nextStatus |} }
        LedgerStore.transact binding intent (fun snapshot -> result {
            let! original, beforeOid = read<NativeEffectRecord> binding snapshot path
            if original.Candidate <> candidate || original.EffectId <> effectId
               || original.ProjectId <> binding.ProjectId.ToString("D")
               || original.ProjectLedgerId <> binding.LedgerId.ToString("D")
               || original.ProjectEpoch <> binding.Epoch
               || original.Status <> expectedStatus
               || operationId = original.ClaimOperationId
               || operationId = original.DispatchId || operationId = original.EffectId then
                return! stale "Original effect/claim already advanced or mismatched"
            do! extraValidate original
            let amended = modify original
            return [ { Path = path; ExpectedBlobOid = Some beforeOid
                       Content = serialize { amended with Status = nextStatus } } ]
        })

/// Durable marker MUST be committed and confirmed before any fake-browser invocation.
/// A lost marker ACK is never permission to invoke the effect.
let beginFakeEffect (binding: LedgerBinding) (candidate: NativeCandidate) (effectId: Guid) (operationId: Guid) =
    update binding candidate effectId operationId "NativeStageBBeginEffectTestOnly" "Claimed" "Dispatching"
        (fun _ -> Ok ())
        (fun record -> { record with StartOperationId = operationId })

/// Fake-provider evidence is intentionally incapable of authenticating a real session.
/// It only proves the project-side monotonic terminal record using disposable fixtures.
let recordFakeTerminal (binding: LedgerBinding) (candidate: NativeCandidate) (effectId: Guid) (operationId: Guid) (proof: FakeProviderTerminal) =
    update binding candidate effectId operationId "NativeStageBTerminalTestOnly" "Dispatching" "VerifiedFakeTerminal"
        (fun r ->
            if proof.Marker = "STAGE_B_DISPOSABLE_FAKE_PROVIDER/v1"
               && proof.Status = "Terminal" && proof.EffectId = r.EffectId
               && notBlank proof.ProviderSessionId && r.StartOperationId <> Guid.Empty then Ok ()
            else reject "Unknown/foreign/forged provider terminal")
        (fun r -> { r with TerminalOperationId = operationId; ProviderSessionId = proof.ProviderSessionId })

let observe (binding: LedgerBinding) (candidate: NativeCandidate) =
    result {
        let! snapshot = LedgerStore.fetch binding
        let! record, _ = read<NativeEffectRecord> binding snapshot (recordPath candidate)
        if record.ProjectId <> binding.ProjectId.ToString("D")
           || record.ProjectLedgerId <> binding.LedgerId.ToString("D")
           || record.ProjectEpoch <> binding.Epoch
           || record.Candidate <> candidate then
            return! reject "Foreign project record"
        return snapshot.Commit, record
    }
#endif
