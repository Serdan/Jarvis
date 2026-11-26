module Client.ProviderLaunch

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Identity of an original launch effect, not a vendor thread, tab, or process.
/// Named Agent targets are transitional; a queue claim needs no Agent identity.
type LaunchTarget =
    | NamedAssignment of agentId: string * taskId: string * attemptId: string * generation: int64
    | NamedReview of agentId: string * reviewId: string * version: int64 * claimOperationId: Guid
    | QueueClaim of workId: string * generation: int64

type LaunchIdentity =
    { ProjectId: Guid
      LaunchId: Guid
      EffectId: Guid
      Target: LaunchTarget }

type Billing = Included | Metered

/// Configuration only. These capabilities and tags are never permissions.
type InferenceProfile =
    { Id: string
      Provider: string
      Model: string option
      ReasoningEffort: string option
      McpProfiles: Set<string>
      Capabilities: Set<string>
      Tags: Set<string>
      Billing: Billing }

type AvailableProfile =
    { Profile: InferenceProfile
      Available: bool
      Admitted: bool }

type ProfileRequirements =
    { RequiredProfile: string option
      RequiredProvider: string option
      McpProfile: string
      RequiredCapabilities: Set<string>
      PreferredTags: Set<string> }

let private nonblank value = not (String.IsNullOrWhiteSpace value)

let private validProfile (p: InferenceProfile) =
    nonblank p.Id && nonblank p.Provider
    && (p.Model |> Option.forall nonblank)
    && (p.ReasoningEffort |> Option.forall nonblank)
    && (p.McpProfiles |> Set.forall nonblank)

/// Choose once, before the original effect is claimed. Admission/availability
/// are inputs from the existing policy, not substitutes for it. This slice does
/// not select metered profiles, even if otherwise eligible.
let selectProfile defaultProfile requirements (profiles: AvailableProfile list) =
    let ids = profiles |> List.map (fun item -> item.Profile.Id)
    if not (nonblank requirements.McpProfile)
       || (profiles |> List.exists (fun item -> not (validProfile item.Profile)))
       || ids.Length <> (ids |> Set.ofList |> Set.count) then
        Error "Invalid or duplicate inference profile configuration"
    else
        profiles
        |> List.filter (fun item ->
            let p = item.Profile
            item.Available && item.Admitted && p.Billing = Included
            && (requirements.RequiredProfile |> Option.forall ((=) p.Id))
            && (requirements.RequiredProvider |> Option.forall ((=) p.Provider))
            && Set.contains requirements.McpProfile p.McpProfiles
            && Set.isSubset requirements.RequiredCapabilities p.Capabilities)
        |> List.sortBy (fun item ->
            let p = item.Profile
            -(Set.intersect requirements.PreferredTags p.Tags |> Set.count),
            (p.Id <> defaultProfile), p.Id)
        |> List.tryHead
        |> Option.map (fun item -> item.Profile)
        |> function Some p -> Ok p | None -> Error "No eligible admitted unmetered profile"

/// Exact canonical renderer output. Host must obtain it from the verified
/// original ledger snapshot and journal it before entering either adapter.
type CanonicalBootstrap =
    { SnapshotCommit: string
      TemplateVersion: int
      TemplateBlobOid: string
      PromptSha256: string
      Prompt: string }

type LaunchRequest =
    { Identity: LaunchIdentity
      Profile: InferenceProfile
      McpProfile: string
      Bootstrap: CanonicalBootstrap }

let private oid (value: string) =
    not (isNull value) && (value.Length = 40 || value.Length = 64)
    && (value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

let promptDigest (prompt: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes prompt)
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let private validTarget = function
    | NamedAssignment (agent, task, attempt, generation) ->
        nonblank agent && nonblank task && nonblank attempt && generation > 0L
    | NamedReview (agent, review, version, operation) ->
        nonblank agent && nonblank review && version > 0L && operation <> Guid.Empty
    | QueueClaim (work, generation) -> nonblank work && generation > 0L

/// Shape and byte integrity only, NOT admission or verification of the
/// bootstrap's canonical origin. The original host owns that responsibility.
let validateRequest request =
    let i, b, p = request.Identity, request.Bootstrap, request.Profile
    if i.ProjectId = Guid.Empty || i.LaunchId = Guid.Empty || i.EffectId = Guid.Empty
       || i.LaunchId = i.EffectId || not (validTarget i.Target) then
        Error "Invalid launch identity"
    elif not (validProfile p) || p.Billing <> Included
         || not (Set.contains request.McpProfile p.McpProfiles) then
        Error "Invalid or ineligible launch profile"
    elif not (oid b.SnapshotCommit) || not (oid b.TemplateBlobOid)
         || b.TemplateVersion <= 0 || not (nonblank b.Prompt)
         || Encoding.UTF8.GetByteCount b.Prompt > 65536
         || b.PromptSha256 <> promptDigest b.Prompt then
        Error "Invalid canonical bootstrap"
    else Ok ()

type TransportFact =
    | DefinitiveNoSend
    | Acknowledged
    | OutcomeUnknown
    | LocalProcessRunning
    | LocalProcessExited of exitCode: int
    | CancellationRequested
    | UnsupportedCapability

type TransportObservation =
    { Identity: LaunchIdentity
      ProfileId: string
      Fact: TransportFact }

/// Only a canonical reader can supply these observations. They are never
/// produced by provider acknowledgements, a thread ID, or process exit.
/// This projection does not authenticate observations or mutate the ledger.
type CanonicalFact = CallHomeVerified | ExecutionTerminal of terminalOutcome: string

type CanonicalObservation =
    { Identity: LaunchIdentity
      ProfileId: string
      Fact: CanonicalFact }

type ExecutionState =
    | Unobserved
    | Uncertain
    | NotSubmitted
    | Submitted
    | VerifiedCallHome
    | Terminal of terminalOutcome: string
    | ConflictingEvidence

let transport (request: LaunchRequest) fact : TransportObservation =
    { Identity = request.Identity; ProfileId = request.Profile.Id; Fact = fact }

/// Recompute from the durable original effect's observations; arrival order
/// and duplicates do not change the result. A foreign generation/profile is
/// rejected rather than silently used. No state here grants replay or release.
let reconcile identity profileId (transportFacts: TransportObservation list)
              (canonicalFacts: CanonicalObservation list) =
    if (transportFacts |> List.exists (fun e -> e.Identity <> identity || e.ProfileId <> profileId))
       || (canonicalFacts |> List.exists (fun e -> e.Identity <> identity || e.ProfileId <> profileId)) then
        Error "Foreign launch observation"
    else
        let t = transportFacts |> List.map (fun e -> e.Fact) |> Set.ofList
        let c = canonicalFacts |> List.map (fun e -> e.Fact) |> Set.ofList
        let noSend = Set.contains DefinitiveNoSend t
        let ack = Set.contains Acknowledged t
        let callHome = Set.contains CallHomeVerified c
        let terminals =
            c |> Set.toList |> List.choose (function ExecutionTerminal outcome -> Some outcome | _ -> None)
        if (noSend && (ack || callHome || not terminals.IsEmpty))
           || terminals.Length > 1 || (terminals |> List.exists (nonblank >> not)) then
            Ok ConflictingEvidence
        else
            match terminals with
            | [outcome] -> Ok (Terminal outcome)
            | _ when callHome -> Ok VerifiedCallHome
            | _ when ack -> Ok Submitted
            | _ when noSend -> Ok NotSubmitted
            | _ when t |> Set.exists (function UnsupportedCapability -> false | _ -> true) -> Ok Uncertain
            | _ -> Ok Unobserved

/// Calls are made only behind the existing permission, admission, original
/// effect journal and generation fences. Observe/cancel never dispatch; the
/// caller persists and reconciles observations with the original effect.
type Adapter =
    { Provider: string
      Launch: LaunchRequest -> Task<TransportObservation list>
      Observe: LaunchRequest -> Task<TransportObservation list>
      Cancel: LaunchRequest -> Task<TransportObservation list> }

let chooseAdapter (profile: InferenceProfile) (adapters: Adapter list) =
    match adapters |> List.filter (fun a -> a.Provider = profile.Provider) with
    | [adapter] -> Ok adapter
    | _ -> Error "Missing or ambiguous provider adapter"
