module Client.AutomationNativeProviderSessionHost

open System
open System.Text.Encodings.Web
open System.Text.Json
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerAccountReservation
open Client.NativeSchedulerRosterPolicy
open Client.AutomationNativeAccountProjectIngress

// The existing paired BrowserBridge authenticates extension transport,
// not ChatGPT provider session/turn identity. A registered tab/window, script
// acknowledgement, URL, disappearance or DOM stop control is never a terminal.
type NativeHostHold =
    | AccountAuthorityUnavailable
    | ProviderFirstConnectionUnavailable
    | ProviderTerminalUnavailable
    | ReviewerIngressUnavailable

let productionHolds () =
    [ AccountAuthorityUnavailable; ProviderFirstConnectionUnavailable
      ProviderTerminalUnavailable; ReviewerIngressUnavailable ]

[<CLIMutable>]
type OriginalLaunchIntent =
    { SchemaVersion: int
      ProjectName: string
      Claim: StartedProjectClaim
      AccountGeneration: int64
      CanonicalKey: string
      TemplateVersion: int
      TemplateBlobOid: string
      PromptSha256: string
      ArgumentsSha256: string
      ObservedConnectionId: string
      ObservedWindowGeneration: int
      State: string }

// Distinct persisted reviewer schema: never reinterpret Worker attempt fields
// as a review. The common operation paths still fence the original effect ID.
[<CLIMutable>]
type OriginalReviewerLaunchIntent =
    { SchemaVersion: int
      ProjectName: string
      Claim: ReviewerProjectClaim
      AccountGeneration: int64
      CanonicalKey: string
      TemplateVersion: int
      TemplateBlobOid: string
      PromptSha256: string
      ArgumentsSha256: string
      AgentConfigVersion: int
      InstructionProvenanceSha256: string
      ObservedConnectionId: string
      ObservedWindowGeneration: int
      State: string }

let private options =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                          PropertyNameCaseInsensitive = true,
                          Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
let private json value = JsonSerializer.Serialize(value, options)
let private bad reason = Error(InvalidBinding("NativeSessionHost: " + reason))
let private stale reason = Error(StaleEntityVersion("NativeSessionHost: " + reason))

// Only the paired service supplies connection identity. This is transport
// preflight, not a provider identity or a proof that a send did/did not occur.
let private observedWindowOwner (bridge: BrowserBridge.Service) registration =
    if isNull (box bridge) || String.IsNullOrWhiteSpace registration then
        bad "Paired browser window is missing."
    else
        bridge.TryGetExactWindowOwner(registration)
        |> Result.mapError (fun reason -> InvalidBinding("NativeSessionHost: " + reason))

let private intentPath (effectId: Guid) =
    $"records/native-scheduler/launch-intents/{effectId:N}.json"

let private argumentsPath (effectId: Guid) =
    $"records/native-scheduler/launch-arguments/{effectId:N}.json"

// Source-bound reviewer rendering is independently useful preparation, not
// authority to journal or dispatch. Resolve role instructions and the common
// launch template on the very same verified project snapshot. Do not invent
// a reviewer prompt format or silently append instruction text to the wire.
type internal ReviewerLaunchRendering =
    { Rendered: RoleInstructions.RoleInstructions.RenderedLaunchPrompt
      AgentConfigVersion: int
      InstructionProvenanceSha256: string
      Arguments: string
      ArgumentsSha256: string }

let internal renderReviewerArgumentsAt (project: LedgerBinding) (snapshot: LedgerSnapshot)
                                       projectName timeoutMs (claim: ReviewerProjectClaim) =
    reviewerFencePath claim
    |> Result.bind (fun _ ->
        let c = claim.ReviewCandidate
        if c.ProjectId <> string project.ProjectId || c.ProjectLedgerId <> string project.LedgerId
           || c.ProjectEpoch <> project.Epoch || snapshot.Commit <> claim.ExpectedProjectHead then
            stale "Original reviewer project identity/head changed."
        elif timeoutMs < 100 || timeoutMs > 27000 then
            bad "Invalid full launch argument timeout."
        else
            RoleInstructions.RoleInstructions.resolveAt project snapshot c.OwnerAgentId
            |> Result.bind (fun layers ->
                RoleInstructions.RoleInstructions.renderLaunchPromptAt
                    project snapshot projectName c.OwnerAgentId
                |> Result.bind (fun rendered ->
                    let arguments = json {| prompt = rendered.Prompt; timeoutMs = timeoutMs |}
                    if not (BrowserArguments.fits arguments) then
                        bad "Full serialized reviewer launch arguments exceed bridge budget."
                    else
                        Ok { Rendered = rendered; AgentConfigVersion = layers.AgentConfigVersion
                             InstructionProvenanceSha256 = NativeSchedulerRosterPolicy.sha256 (json layers)
                             Arguments = arguments
                             ArgumentsSha256 = NativeSchedulerRosterPolicy.sha256 arguments })))

// This is explicitly the accepted *signed disposable* account authority. No
// production grant, reservation, script execution or second dispatcher exists
// here. The entire original prompt envelope is pinned in the same project CAS
// as the Started ingress; losing/replaying it never authorizes another send.
let internal prepareVerifiedDisposable root (account: AccountBinding) operatorKey subject
                                       (projects: LedgerBinding array)
                                       (coverage: NativeDisposableCoverage)
                                       (bridge: BrowserBridge.Service)
                                       projectName timeoutMs (claim: StartedProjectClaim) =
    if isNull (box claim) || isNull (box claim.Candidate) || isNull projects
       || isNull (box coverage) then bad "Missing original claim or signed coverage."
    elif claim.Candidate.Kind <> "Worker" then bad "Reviewer requires separately verified claim/lease ingress."
    elif timeoutMs < 100 || timeoutMs > 27000 then bad "Invalid full launch argument timeout."
    else
        let c = claim.Candidate
        match Guid.TryParse c.EffectOperationId, projects |> Array.tryFind (fun p ->
            string p.ProjectId = c.ProjectId && string p.LedgerId = c.ProjectLedgerId
            && p.Epoch = c.ProjectEpoch) with
        | (true, effectId), Some project when effectId <> Guid.Empty && string effectId = c.EffectOperationId ->
            match observedWindowOwner bridge claim.WindowRegistration with
            | Error e -> Error e
            | Ok (connectionId, windowGeneration) ->
                match NativeSchedulerAccountReservation.readVerifiedDisposable
                        root account operatorKey subject projects coverage with
                | Error e -> bad (sprintf "Verified disposable account unavailable: %A" e)
                | Ok (accountSnapshot, state) ->
                    if accountSnapshot.Head <> claim.AccountHead then stale "Original account head changed."
                    else
                        match state.Records |> Array.tryFind (fun r ->
                            r.OriginalOperationId = string claim.OriginalReservationOperationId) with
                        | Some record when record.Candidate = c && record.Status = "Reserved"
                                           && record.Generation > 0L ->
                            match LedgerStore.fetch project with
                            | Error e -> Error e
                            | Ok snapshot when snapshot.Commit <> claim.ExpectedProjectHead ->
                                stale "Original project head changed; never rerender for a replacement attempt."
                            | Ok snapshot ->
                                match RoleInstructions.RoleInstructions.renderLaunchPromptAt
                                        project snapshot projectName c.OwnerAgentId with
                                | Error e -> Error e
                                | Ok rendered ->
                                    let arguments = json {| prompt = rendered.Prompt; timeoutMs = timeoutMs |}
                                    if not (BrowserArguments.fits arguments) then
                                        bad "Full serialized browser launch arguments exceed bridge budget."
                                    else
                                        match observedWindowOwner bridge claim.WindowRegistration with
                                        | Ok (currentOwner, currentGeneration) when currentOwner = connectionId && currentGeneration = windowGeneration ->
                                            let intent =
                                                { SchemaVersion = 2; ProjectName = rendered.ProjectName
                                                  Claim = claim; AccountGeneration = record.Generation
                                                  CanonicalKey = record.CanonicalKey
                                                  TemplateVersion = rendered.TemplateVersion
                                                  TemplateBlobOid = rendered.TemplateBlobOid
                                                  PromptSha256 = rendered.PromptSha256
                                                  ArgumentsSha256 = NativeSchedulerRosterPolicy.sha256 arguments
                                                  ObservedConnectionId = connectionId
                                                  ObservedWindowGeneration = windowGeneration
                                                  State = "HeldBeforeSend" }
                                            let change =
                                                { Path = intentPath effectId; ExpectedBlobOid = None
                                                  Content = json intent }
                                            // Keep the exact wire bytes separate: a valid 64 KiB envelope
                                            // must not grow past the ledger entity bound through JSON escaping.
                                            let argumentChange =
                                                { Path = argumentsPath effectId; ExpectedBlobOid = None
                                                  Content = arguments }
                                            claimVerifiedDisposableWithLaunchIntent root account operatorKey subject
                                                projects coverage claim change argumentChange
                                            |> Result.bind (fun write ->
                                                if write.AlreadyApplied then stale "Original launch intent already committed; do not replay."
                                                else Ok write)
                                        | _ -> stale "Browser window registration changed during preparation."
                        | _ -> stale "Original native reservation missing, stale or foreign."
        | _ -> bad "Invalid original effect UUID or foreign project binding."

// Read-only reconciliation of original intent. Origin, operation and current
// bytes are sourced from the authoritative project lineage, not the browser.
let private readOriginalJournal (binding: LedgerBinding) (claimOperationId: Guid)
                                (effectId: Guid) operationKind =
    if claimOperationId = Guid.Empty || effectId = Guid.Empty then bad "Missing original UUID."
    else
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            match LedgerStore.getOperationAt binding snapshot claimOperationId with
            | Ok (Some operation) when operation.Type = operationKind
                                       && Array.contains (intentPath effectId) operation.Paths
                                       && Array.contains (argumentsPath effectId) operation.Paths ->
                match LedgerStore.getOperationOriginAt binding snapshot operation,
                      LedgerStore.readAt binding snapshot (intentPath effectId),
                      LedgerStore.readAt binding snapshot (argumentsPath effectId) with
                | Ok origin, Ok (Some raw, Some blob), Ok (Some arguments, Some _) ->
                    Ok (operation, origin, blob, raw, arguments)
                | _ -> bad "Missing source-bound original intent or operation origin."
            | _ -> bad "Original launch operation is missing or not a native launch intent.")

let private validConnection (value: string) =
    match Guid.TryParseExact(value, "N") with
    | true, id when id <> Guid.Empty && id.ToString("N") = value -> true
    | _ -> false

let private matchesOriginalBytes (request: JsonElement) (raw: string) (arguments: string)
                                 argumentsSha256 promptSha256 =
    use args = JsonDocument.Parse(arguments)
    let prompt = args.RootElement.GetProperty("prompt").GetString()
    request.GetProperty("launchIntentSha256").GetString() = NativeSchedulerRosterPolicy.sha256 raw
    && request.GetProperty("argumentsSha256").GetString() = NativeSchedulerRosterPolicy.sha256 arguments
    && argumentsSha256 = NativeSchedulerRosterPolicy.sha256 arguments
    && promptSha256 = RoleInstructions.RoleInstructions.launchTemplateSha256 prompt
    && BrowserArguments.fits arguments

let private matchesProject (binding: LedgerBinding) (candidate: NativeDispatchCandidate) =
    candidate.ProjectId = string binding.ProjectId
    && candidate.ProjectLedgerId = string binding.LedgerId
    && candidate.ProjectEpoch = binding.Epoch

let internal readOriginalIntent (binding: LedgerBinding) (claimOperationId: Guid) (effectId: Guid) =
    readOriginalJournal binding claimOperationId effectId "NativeStartedLaunchIntentDisposable"
    |> Result.bind (fun (operation, origin, blob, raw, arguments) ->
        try
            use envelope = JsonDocument.Parse(operation.CanonicalRequestJson)
            use request = JsonDocument.Parse(envelope.RootElement.GetProperty("request").GetString())
            let pinned = JsonSerializer.Deserialize<OriginalLaunchIntent>(raw, options)
            let originalClaim =
                JsonSerializer.Deserialize<StartedProjectClaim>(
                    request.RootElement.GetProperty("claim").GetRawText(), options)
            if pinned.SchemaVersion <> 2 || pinned.State <> "HeldBeforeSend"
               || not (validConnection pinned.ObservedConnectionId) || pinned.ObservedWindowGeneration < 1
               || pinned.Claim <> originalClaim || pinned.Claim.OperationId <> claimOperationId
               || pinned.Claim.Candidate.EffectOperationId <> string effectId
               || not (matchesProject binding pinned.Claim.Candidate)
               || not (matchesOriginalBytes request.RootElement raw arguments pinned.ArgumentsSha256 pinned.PromptSha256) then
                bad "Original launch intent is malformed or rewritten."
            else Ok (origin, blob, pinned, arguments)
        with _ -> bad "Malformed immutable original launch intent.")

// This does not confer send permission or revalidate a current claim. It
// reads an already-journaled historical reviewer intent, even after a later
// release/reclaim, and checks its original typed claim and exact arguments.
let internal readOriginalReviewerIntent (binding: LedgerBinding) (claimOperationId: Guid) (effectId: Guid) =
    readOriginalJournal binding claimOperationId effectId "NativeReviewerLaunchIntent"
    |> Result.bind (fun (operation, origin, blob, raw, arguments) ->
        try
            use envelope = JsonDocument.Parse(operation.CanonicalRequestJson)
            use request = JsonDocument.Parse(envelope.RootElement.GetProperty("request").GetString())
            let pinned = JsonSerializer.Deserialize<OriginalReviewerLaunchIntent>(raw, options)
            let originalClaim =
                JsonSerializer.Deserialize<ReviewerProjectClaim>(
                    request.RootElement.GetProperty("claim").GetRawText(), options)
            let c = pinned.Claim.ReviewCandidate
            let digest (value: string) =
                not (isNull value) && value.Length = 64
                && (value |> Seq.forall (fun ch -> (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
            if pinned.SchemaVersion <> 1 || pinned.State <> "HeldBeforeSend"
               || not (validConnection pinned.ObservedConnectionId) || pinned.ObservedWindowGeneration < 1
               || pinned.Claim <> originalClaim || pinned.Claim.OperationId <> claimOperationId
               || c.EffectOperationId <> string effectId || not (matchesProject binding c)
               || pinned.AccountGeneration <> 1L || not (digest pinned.CanonicalKey)
               || pinned.AgentConfigVersion < 0 || not (digest pinned.InstructionProvenanceSha256)
               || not (matchesOriginalBytes request.RootElement raw arguments pinned.ArgumentsSha256 pinned.PromptSha256) then
                bad "Original reviewer launch intent is malformed or rewritten."
            else
                reviewerFencePath pinned.Claim
                |> Result.bind (fun fence ->
                    if not (Array.contains fence operation.Paths) then
                        bad "Original reviewer intent lacks the distinct review-claim fence."
                    else Ok (origin, blob, pinned, arguments))
        with _ -> bad "Malformed immutable original reviewer launch intent.")

// The existing bridge does not expose an authenticated provider first-connect
// or terminal issuer. Never infer one from the original journal, an agent
// lifecycle transition, a browser ACK, a registration or tab closure.
let authorizeProviderDispatch () : Result<unit, NativeHostHold list> =
    Error(productionHolds())

let releaseFromProviderTerminal () : Result<unit, NativeHostHold> =
    Error ProviderTerminalUnavailable

// Program and offline tests share this exact ports factory. The existing B1
// registration remains the sole owner of the host, callback and shutdown.
// There is no authorized production head reader or way to enable native sends.
let internal defaultOffHostPorts coordinator report =
    let hold = sprintf "Native session host HOLD: %A" (productionHolds())
    report hold
    Client.WorkQueueSchedulerHostLoop.WorkQueueSchedulerHostLoop.productionPorts
        coordinator (fun () -> Error hold) (fun _ -> ())
