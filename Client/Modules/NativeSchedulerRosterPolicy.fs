module Client.NativeSchedulerRosterPolicy

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// Policy is deliberately not native reservation, provider-session, or launch authority.
// It is a signed account-root statement about the *entire* approved membership.
[<CLIMutable>]
type NativePoolLimits =
    { Worker: int
      Reviewer: int }

[<CLIMutable>]
type NativeRosterProject =
    { ProjectId: string
      Alias: string
      LedgerId: string
      Epoch: int
      SourceRef: string
      RemoteAuthoritySha256: string
      Limits: NativePoolLimits }

[<CLIMutable>]
type NativeRosterPolicy =
    { SchemaVersion: int
      AccountId: string
      AccountLedgerId: string
      AccountEpoch: int
      AccountAuthoritySha256: string
      AuthenticatedSubject: string
      Version: int64
      MembershipComplete: bool
      AccountLimits: NativePoolLimits
      Projects: NativeRosterProject array }

[<CLIMutable>]
type NativeSignedRosterApproval =
    { SchemaVersion: int
      OperationId: string
      Actor: string
      PreviousVersion: int64
      ExpectedAccountHead: string
      Policy: NativeRosterPolicy
      SignerPublicKeySha256: string
      SignatureBase64: string }

// Provenance observations are from individually fetched project Git heads.
// These are NOT a globally atomic project/session occupancy snapshot.
type NativeVerifiedProjectHead =
    { ProjectId: string
      LedgerId: string
      Epoch: int
      VerifiedHead: string }

// The projection is intentionally unusable for dispatch even for genuine
// approvals. A separate provider-session and reservation protocol is required.
type NativeRosterProjection =
    { Status: string
      HoldReasons: string array
      VerifiedAccountHead: string option
      VerifiedProjectHeads: NativeVerifiedProjectHead array
      ObservedAtUtc: DateTimeOffset option
      Policy: NativeRosterPolicy option
      MembershipComplete: bool
      PolicyComplete: bool
      OccupancyComplete: bool
      AccountCapacityComplete: bool
      LaunchAuthorized: bool }

module NativeSchedulerRosterPolicy =
    [<Literal>]
    let PolicyPath = "native/scheduler-roster-policy-v1.json"

    [<Literal>]
    let ProjectSourceRef = "refs/heads/jarvis-orchestration"

    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let sha256 (raw: string) =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()

    let internal keySha256 (key: byte array) =
        if isNull key then "" else
        Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()

    // Exact authority values stay outside Git; account and project remote URL
    // fingerprints are included in the signed policy, never credential-bearing URLs.
    let internal accountAuthoritySha256 accountId ledgerId epoch fetchUrl pushUrl =
        JsonSerializer.Serialize(
            {| accountId = accountId; ledgerId = ledgerId; epoch = epoch
               approvedFetchUrl = fetchUrl; approvedPushUrl = pushUrl |},
            jsonOptions) |> sha256

    let private guidIsCanonical (value: string) =
        let mutable id = Guid.Empty
        not (String.IsNullOrWhiteSpace value)
        && Guid.TryParse(value, &id) && id <> Guid.Empty
        && id.ToString("D") = value

    let private oid (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{40}([0-9a-f]{24})?$")

    let private digest (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$")

    let private limitsValid (limits: NativePoolLimits) =
        not (isNull (box limits))
        && limits.Worker >= 0 && limits.Reviewer >= 0
        && limits.Worker <= 1000000 && limits.Reviewer <= 1000000

    let internal validatePolicy (policy: NativeRosterPolicy) =
        if isNull (box policy) || policy.SchemaVersion <> 1
           || not (guidIsCanonical policy.AccountId)
           || not (guidIsCanonical policy.AccountLedgerId)
           || policy.AccountEpoch < 1
           || not (digest policy.AccountAuthoritySha256)
           || String.IsNullOrWhiteSpace policy.AuthenticatedSubject
           || policy.AuthenticatedSubject.Length > 256
           || policy.AuthenticatedSubject <> policy.AuthenticatedSubject.Trim()
           || policy.Version < 1L || not policy.MembershipComplete
           || not (limitsValid policy.AccountLimits)
           || isNull policy.Projects || policy.Projects.Length = 0 then
            Error "Missing or incomplete signed account root, full membership, or typed limits."
        elif policy.Projects |> Array.exists (fun p ->
            isNull (box p)
            || not (guidIsCanonical p.ProjectId)
            || not (guidIsCanonical p.LedgerId)
            || p.Epoch < 1
            || String.IsNullOrWhiteSpace p.Alias
            || p.Alias.Length > 100 || p.Alias <> p.Alias.Trim()
            || p.SourceRef <> ProjectSourceRef
            || not (digest p.RemoteAuthoritySha256)
            || not (limitsValid p.Limits)) then
            Error "Project membership lacks exact project/ledger/epoch/source/remote or typed quota."
        else
            let unique (key: NativeRosterProject -> string) =
                policy.Projects |> Array.map key |> Array.distinctBy (fun (v: string) -> v.ToUpperInvariant())
                |> Array.length = policy.Projects.Length
            if not (unique _.ProjectId)
               || not (unique _.LedgerId)
               || not (unique _.Alias)
               || not (unique _.RemoteAuthoritySha256) then
                Error "Duplicate project ID, ledger ID, alias or remote authority."
            else Ok ()

    // A length-framed, domain-separated immutable statement, not a caller's
    // "approved" boolean or an arbitrary actor label.
    let internal approvalMessage (approval: NativeSignedRosterApproval) =
        let fields =
            [ "jarvis-native-roster-approval-v1"
              string approval.SchemaVersion
              approval.OperationId
              approval.Actor
              string approval.PreviousVersion
              approval.ExpectedAccountHead
              JsonSerializer.Serialize(approval.Policy, jsonOptions)
              approval.SignerPublicKeySha256 ]
        fields
        |> List.map (fun value ->
            let safe = if isNull value then "" else value
            string (Encoding.UTF8.GetByteCount safe) + ":" + safe)
        |> String.concat "" |> Encoding.UTF8.GetBytes

    let internal verifySignedApproval (trustedOperatorKey: byte array)
                             (approval: NativeSignedRosterApproval) =
        let mutable opId = Guid.Empty
        if isNull (box approval) || approval.SchemaVersion <> 1
           || not (Guid.TryParse(approval.OperationId, &opId))
           || opId = Guid.Empty || opId.ToString("D") <> approval.OperationId
           || String.IsNullOrWhiteSpace approval.Actor
           || approval.Actor <> approval.Actor.Trim()
           || approval.Actor.Length > 160
           || approval.PreviousVersion < 0L
           || not (oid approval.ExpectedAccountHead)
           || isNull (box approval.Policy)
           || approval.Policy.Version <> approval.PreviousVersion + 1L
           || isNull trustedOperatorKey || trustedOperatorKey.Length = 0
           || approval.SignerPublicKeySha256 <> keySha256 trustedOperatorKey
           || String.IsNullOrWhiteSpace approval.SignatureBase64 then
            Error "No trusted signed, version-pinned operator policy approval."
        else
            match validatePolicy approval.Policy with
            | Error reason -> Error reason
            | Ok () ->
                try
                    use verifier = ECDsa.Create()
                    let mutable consumed = 0
                    verifier.ImportSubjectPublicKeyInfo(trustedOperatorKey.AsSpan(), &consumed)
                    if consumed = trustedOperatorKey.Length
                       && verifier.VerifyData(
                            approvalMessage approval,
                            Convert.FromBase64String approval.SignatureBase64,
                            HashAlgorithmName.SHA256) then Ok ()
                    else Error "Operator approval signature does not verify."
                with _ -> Error "Invalid operator approval signature or public key."

    let internal hold reason head =
        { Status = "NotAuthoritative"
          HoldReasons = [| reason |]
          VerifiedAccountHead = head
          VerifiedProjectHeads = [||]; ObservedAtUtc = None
          Policy = None; MembershipComplete = false; PolicyComplete = false
          OccupancyComplete = false; AccountCapacityComplete = false
          LaunchAuthorized = false }

    let internal verified head projectHeads policy =
        { Status = "PolicyVerified"
          HoldReasons = [| "Native provider sessions and account reservations are unverified." |]
          VerifiedAccountHead = Some head
          VerifiedProjectHeads = projectHeads
          // Host-local wall-clock observation only; never an expiry/launch fence.
          ObservedAtUtc = Some DateTimeOffset.UtcNow
          Policy = Some policy; MembershipComplete = true; PolicyComplete = true
          OccupancyComplete = false; AccountCapacityComplete = false
          LaunchAuthorized = false }
