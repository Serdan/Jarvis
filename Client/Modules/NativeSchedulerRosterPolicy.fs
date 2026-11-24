module Client.NativeSchedulerRosterPolicy

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// Account configuration under the existing authenticated client and Git ledger.
// Roster membership is not provider-session evidence or launch authority.
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
      Version: int64
      MembershipComplete: bool
      AccountLimits: NativePoolLimits
      Projects: NativeRosterProject array }

[<CLIMutable>]
type NativeRosterUpdate =
    { SchemaVersion: int
      OperationId: string
      Actor: string
      PreviousVersion: int64
      ExpectedAccountHead: string
      Policy: NativeRosterPolicy }

// Provenance observations are from individually fetched project Git heads.
// These are NOT a globally atomic project/session occupancy snapshot.
type NativeVerifiedProjectHead =
    { ProjectId: string
      LedgerId: string
      Epoch: int
      VerifiedHead: string }

// The projection is intentionally unusable for dispatch even for genuine
// policy reads. A separate provider-session and reservation protocol is required.
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

    // Exact authority values stay outside Git; remote fingerprints bind existing
    // configured account and project identities without persisting credentials.
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
           || policy.Version < 1L || not policy.MembershipComplete
           || not (limitsValid policy.AccountLimits)
           || isNull policy.Projects || policy.Projects.Length = 0 then
            Error "Missing or incomplete configured account identity, full membership, or typed limits."
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

    // Ordinary configuration CAS intent, not a separate authorization protocol.
    let internal validateUpdate (update: NativeRosterUpdate) =
        if isNull (box update) || update.SchemaVersion <> 1
           || not (guidIsCanonical update.OperationId)
           || String.IsNullOrWhiteSpace update.Actor
           || update.Actor <> update.Actor.Trim() || update.Actor.Length > 160
           || update.PreviousVersion < 0L
           || update.PreviousVersion = Int64.MaxValue
           || not (oid update.ExpectedAccountHead)
           || isNull (box update.Policy)
           || update.Policy.Version <> update.PreviousVersion + 1L then
            Error "Invalid versioned roster configuration update."
        else validatePolicy update.Policy

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
