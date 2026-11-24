module Client.AutomationNativeAccountProjectIngress

open System
open System.Text.Json
open System.Text.RegularExpressions
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.NativeSchedulerAccountReservation
open Client.NativeSchedulerRosterPolicy
open Client.ReviewWorkflow

// Internal project ingress under the existing authenticated client bindings.
// No provider send or scheduler activation is performed here.
// A claimed review is not a Started implementation attempt. Keep its
// request type distinct and never derive a reviewer fence from empty task
// and attempt fields.
[<CLIMutable>]
type ReviewerProjectClaim =
    { OperationId: Guid
      ExpectedProjectHead: string
      AccountHead: string
      OriginalReservationOperationId: Guid
      ReviewCandidate: NativeDispatchCandidate
      DeviceId: string
      WindowRegistration: string }

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

let private opts =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                          PropertyNameCaseInsensitive = true)
let private json value = JsonSerializer.Serialize(value, opts)
let private oid (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && Regex.IsMatch(value, "^[0-9a-f]{40}([0-9a-f]{24})?$")
let private bad why = Error(InvalidBinding("NativeStarted(default-off): " + why))
let private stale why = Error(StaleEntityVersion("NativeStarted(default-off): " + why))

// This derives an identity only; it does not verify a live claim or grant
// account/project authority. The same-head canonical verifier must run in
// the transaction before any reviewer fence may be created.
let internal reviewerFencePath (claim: ReviewerProjectClaim) =
    let canonicalUuid (format: string) (value: string) =
        match Guid.TryParseExact(value, format) with
        | true, id when id <> Guid.Empty && id.ToString(format) = value -> true
        | _ -> false
    if isNull (box claim) || isNull (box claim.ReviewCandidate) then
        bad "Missing distinct reviewer claim"
    else
        let c = claim.ReviewCandidate
        if c.Kind <> "Reviewer"
           || not (canonicalUuid "D" c.ProjectId && canonicalUuid "D" c.ProjectLedgerId)
           || c.ProjectEpoch < 1 || String.IsNullOrWhiteSpace c.OwnerAgentId
           || not (canonicalUuid "N" c.ReviewId && canonicalUuid "D" c.ClaimOperationId)
           || c.ReviewVersion < 1L || c.ReviewVersion > int64 Int32.MaxValue
           || not (oid c.ReviewedHeadOid)
           || String.IsNullOrWhiteSpace c.ReviewRef
           || not (c.ReviewRef.StartsWith("refs/heads/jarvis-review/", StringComparison.Ordinal))
           || c.AssignmentGeneration <> 0L
           || not (String.IsNullOrEmpty c.TaskId && String.IsNullOrEmpty c.AttemptId
                   && String.IsNullOrEmpty c.SourceRepositoryId && String.IsNullOrEmpty c.SourceBaseOid
                   && String.IsNullOrEmpty c.SourceRef && String.IsNullOrEmpty c.SourceLeaseSha256)
           || not (canonicalUuid "D" c.DispatchId && canonicalUuid "D" c.EffectOperationId)
           || claim.OperationId = Guid.Empty || claim.OriginalReservationOperationId = Guid.Empty
           || not (oid claim.ExpectedProjectHead && oid claim.AccountHead)
           || String.IsNullOrWhiteSpace claim.DeviceId || String.IsNullOrWhiteSpace claim.WindowRegistration
           || ([ string claim.OperationId; string claim.OriginalReservationOperationId
                 c.DispatchId; c.EffectOperationId; c.ClaimOperationId ] |> Set.ofList |> Set.count) <> 5 then
            bad "Invalid distinct reviewer claim identity or original UUID binding"
        else
            Ok (NativeSchedulerAccountReservation.noSendFencePath c)

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

// The producer verifies the immutable original account receipt and its lineage.
// The receipt hash comes from the actual account ledger, not a caller assertion.
let internal verifiedReservation (account: AccountBinding) projects (coverage: NativeAccountCoverage)
                                 accountHead originalId (candidate: NativeDispatchCandidate) =
    let policy = AccountCapacityLedger.readNativeRosterPolicy account projects
    let limit (limits: NativePoolLimits) =
        if candidate.Kind = "Worker" then limits.Worker else limits.Reviewer
    let allowed =
        match policy.Policy with
        | Some configured when policy.Status = "PolicyVerified"
                               && policy.VerifiedAccountHead = Some accountHead ->
            limit configured.AccountLimits > 0
            && (configured.Projects |> Array.exists (fun p ->
                p.ProjectId = candidate.ProjectId && p.LedgerId = candidate.ProjectLedgerId
                && p.Epoch = candidate.ProjectEpoch && limit p.Limits > 0))
        | _ -> false
    if not allowed then bad "Current configured roster denies pre-send"
    else
        match AccountCapacityLedger.readOperation account originalId with
        | Ok (snapshot, Some receipt) when snapshot.Head = accountHead && receipt.Kind = "NativeReserve" ->
            let locator =
                { ReservationOperationId = originalId; ReservationRequestSha256 = receipt.RequestSha256
                  Candidate = candidate; ExpectedGeneration = 1L
                  ExpectedProjectFenceOid = ""; ExpectedProviderSessionId = "" }
            match NativeSchedulerAccountReservation.readOriginal account projects coverage locator with
            | Ok context when context.AccountHead = accountHead
                              && context.CurrentReservation.Status = "Reserved"
                              && String.IsNullOrEmpty context.CurrentReservation.TerminalOperationId ->
                Ok context.CurrentReservation
            | Ok _ -> stale "Original reservation is no longer held before send"
            | Error e -> stale (sprintf "Original reservation verification denied: %A" e)
        | _ -> stale "Original account receipt absent or account head changed"

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
    && claim.Candidate.Kind = "Worker"
    && claim.OperationId <> Guid.Empty
    && claim.OriginalReservationOperationId <> Guid.Empty
    && oid claim.ExpectedProjectHead && oid claim.AccountHead
    && not (String.IsNullOrWhiteSpace claim.DeviceId)
    && not (String.IsNullOrWhiteSpace claim.WindowRegistration)
    && ([ string claim.OperationId; string claim.OriginalReservationOperationId
          claim.Candidate.DispatchId; claim.Candidate.EffectOperationId ] |> Set.ofList |> Set.count) = 4

// Both typed claims use the same account verification, exact project CAS and
// shared no-send fence path. A cancellation tombstone is an existing fence,
// never a claimable empty slot.
let private claimCore (account: AccountBinding) (projects: LedgerBinding array)
                      (coverage: NativeAccountCoverage) operationId expectedProjectHead accountHead
                      originalId (c: NativeDispatchCandidate) deviceId windowRegistration
                      path kind claimJson checkProject
                      (launchIntent: (LedgerFileChange * LedgerFileChange) option) =
    if isNull projects || isNull (box coverage) then bad "No complete configured roster"
    elif launchIntent |> Option.exists (fun (metadata, arguments) ->
        let invalid (change: LedgerFileChange) =
            isNull (box change) || change.ExpectedBlobOid.IsSome
            || String.IsNullOrWhiteSpace change.Content
        invalid metadata || invalid arguments
        || (match Guid.TryParse c.EffectOperationId with
            | true, effectId ->
                metadata.Path <> $"records/native-scheduler/launch-intents/{effectId:N}.json"
                || arguments.Path <> $"records/native-scheduler/launch-arguments/{effectId:N}.json"
            | _ -> true)) then
        bad "Invalid original launch intent path or content"
    else
        match projects |> Array.tryFind (fun p ->
            string p.ProjectId = c.ProjectId && string p.LedgerId = c.ProjectLedgerId
            && p.Epoch = c.ProjectEpoch) with
        | None -> bad "Canonical project binding absent from configured roster"
        | Some project ->
            verifiedReservation account projects coverage accountHead originalId c
            |> Result.bind (fun _ ->
                let intent =
                    { OperationId = operationId; Actor = c.OwnerAgentId; Kind = kind
                      RequestJson =
                          match launchIntent with
                          | None -> claimJson
                          | Some (metadata, arguments) ->
                              json {| claim = JsonSerializer.Deserialize<JsonElement>(claimJson)
                                      launchIntentSha256 = NativeSchedulerRosterPolicy.sha256 metadata.Content
                                      argumentsSha256 = NativeSchedulerRosterPolicy.sha256 arguments.Content |} }
                LedgerStore.transact project intent (fun snapshot ->
                    if snapshot.Commit <> expectedProjectHead then stale "Project head changed before original pre-send CAS"
                    elif not (coverage.ProjectHeads |> Array.exists (fun head ->
                        head.ProjectId = c.ProjectId && head.LedgerId = c.ProjectLedgerId
                        && head.Epoch = c.ProjectEpoch && head.VerifiedHead = snapshot.Commit)) then
                        stale "Configured roster coverage does not witness the pre-send project head"
                    else
                        checkProject project snapshot
                        |> Result.bind (fun () ->
                            verifiedReservation account projects coverage accountHead originalId c
                            |> Result.bind (fun original ->
                                LedgerStore.readAt project snapshot path
                                |> Result.bind (function
                                    | Some _, _ -> stale "Original claim already has a project fence or cancellation"
                                    | None, _ ->
                                        let effect =
                                            { SchemaVersion = 1; ProjectId = c.ProjectId
                                              ProjectLedgerId = c.ProjectLedgerId; ProjectEpoch = c.ProjectEpoch
                                              AccountId = string account.AccountId
                                              AccountLedgerId = string account.LedgerId; AccountEpoch = account.Epoch
                                              AccountHead = accountHead
                                              OriginalReservationOperationId = string originalId
                                              ReservationCanonicalKey = original.CanonicalKey
                                              Candidate = c; ClaimOperationId = string operationId
                                              DispatchId = c.DispatchId; EffectOperationId = c.EffectOperationId
                                              SourceLeaseSha256 = c.SourceLeaseSha256
                                              DeviceId = deviceId; WindowRegistration = windowRegistration
                                              Status = "HeldBeforeSend"; ProviderSessionId = ""; TerminalOriginOid = "" }
                                        Ok ([ { Path = path; ExpectedBlobOid = None; Content = json effect } ]
                                            @ (launchIntent |> Option.map (fun (metadata, arguments) -> [metadata; arguments]) |> Option.defaultValue [])))))))

let private claimStartedCore account projects coverage (claim: StartedProjectClaim)
                             (launchIntent: (LedgerFileChange * LedgerFileChange) option) =
    if not (claimValid claim) then bad "Invalid original UUID or immutable source/head binding"
    else
        let c = claim.Candidate
        let kind = if launchIntent.IsSome then "NativeStartedLaunchIntent" else "NativeStartedPreSendClaim"
        claimCore account projects coverage claim.OperationId claim.ExpectedProjectHead claim.AccountHead
            claim.OriginalReservationOperationId c claim.DeviceId claim.WindowRegistration
            (NativeSchedulerAccountReservation.noSendFencePath c) kind (json claim)
            (fun project snapshot -> checkStartedProject project snapshot claim) launchIntent

let internal claimStarted account projects coverage claim =
    claimStartedCore account projects coverage claim None

let internal claimStartedWithLaunchIntent account projects coverage claim metadata arguments =
    claimStartedCore account projects coverage claim (Some (metadata, arguments))

let internal claimReviewerWithLaunchIntent account projects coverage (claim: ReviewerProjectClaim) metadata arguments =
    reviewerFencePath claim
    |> Result.bind (fun path ->
        let c = claim.ReviewCandidate
        claimCore account projects coverage claim.OperationId claim.ExpectedProjectHead claim.AccountHead
            claim.OriginalReservationOperationId c claim.DeviceId claim.WindowRegistration path
            "NativeReviewerLaunchIntent" (json claim)
            (fun project snapshot ->
                NativeSchedulerAccountReservation.verifyReviewerClaim project snapshot c
                |> Result.mapError (fun e -> StaleEntityVersion(sprintf "Canonical reviewer claim denied: %A" e)))
            (Some(metadata, arguments)))
