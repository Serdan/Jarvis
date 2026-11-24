module Client.NativeProjectEffectTransition

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Client.LedgerStore
open Client.NativeSchedulerAccountReservation

// A project-side transition planner, not an account authority or provider
// issuer. All plans MUST run within LedgerStore.transact on the same canonical
// project binding. The authenticated runtime must independently verify the
// original reservation/receipt/generation and construct typed no-send evidence
// from that verified context, including on CAS retry. No callback or boolean
// supplied to this module can prove that verification occurred.
type internal OriginalEffectIdentity =
    { AccountId: Guid
      AccountLedgerId: Guid
      AccountEpoch: int
      ReservationOperationId: Guid
      CanonicalKey: string
      Candidate: NativeDispatchCandidate }

type internal HeldFence =
    { ClaimOperationId: Guid
      ClaimRequestSha256: string
      ClaimOrigin: string
      ClaimBlobOid: string }

type internal ExclusionPredecessor =
    | BeforeClaim
    | ExactHeldFence of HeldFence

let private bad reason = Error(InvalidBinding("Native effect transition: " + reason))
let private stale reason = Error(StaleEntityVersion("Native effect transition: " + reason))
let private oid value =
    not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{40}([0-9a-f]{24})?$")
let private digest value =
    not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$")
let private uuid value =
    match Guid.TryParseExact(value, "D") with
    | true, id -> id <> Guid.Empty && string id = value
    | _ -> false
let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

// The worker path is the existing ingress key; reviewer key is the common
// observed W12/W16 contract. No reservation-keyed parallel fence exists.
// At composition use the accepted shared account-side key helper directly.
let private fencePathValid (candidate: NativeDispatchCandidate) (path: string) =
    let hash = Client.NativeSchedulerRosterPolicy.NativeSchedulerRosterPolicy.sha256
    match candidate.Kind with
    | "Worker" when not (String.IsNullOrWhiteSpace candidate.TaskId)
                    && not (String.IsNullOrWhiteSpace candidate.AttemptId) ->
        path = ("records/native-account-started-effect-v1/"
                + hash (candidate.ProjectLedgerId + "|" + candidate.Kind + "|"
                        + candidate.TaskId + "|" + candidate.AttemptId) + ".json")
    | "Reviewer" when not (String.IsNullOrWhiteSpace candidate.ReviewId)
                      && candidate.ReviewVersion > 0L && uuid candidate.ClaimOperationId ->
        let key = JsonSerializer.Serialize(
                    {| projectId = candidate.ProjectId; ledgerId = candidate.ProjectLedgerId
                       epoch = candidate.ProjectEpoch; reviewId = candidate.ReviewId
                       reviewVersion = candidate.ReviewVersion
                       claimOperationId = candidate.ClaimOperationId |}, options)
        path = ("records/native-account-reviewer-effect-v1/" + hash key + ".json")
    | _ -> false

let private checkIdentity (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                          expectedHead (identity: OriginalEffectIdentity) path =
    if isNull (box identity) || isNull (box identity.Candidate) then
        bad "Missing original effect identity."
    else
        let c = identity.Candidate
        if identity.AccountId = Guid.Empty || identity.AccountLedgerId = Guid.Empty
           || identity.AccountEpoch < 1 || identity.ReservationOperationId = Guid.Empty
           || not (digest identity.CanonicalKey)
           || not (uuid c.DispatchId && uuid c.EffectOperationId)
           || c.DispatchId = c.EffectOperationId
           || c.DispatchId = string identity.ReservationOperationId
           || c.EffectOperationId = string identity.ReservationOperationId
           || c.ProjectId <> string binding.ProjectId
           || c.ProjectLedgerId <> string binding.LedgerId
           || c.ProjectEpoch <> binding.Epoch
           || snapshot.Header.ProjectId <> c.ProjectId
           || snapshot.Header.LedgerId <> c.ProjectLedgerId
           || snapshot.Header.Epoch <> c.ProjectEpoch
           || not (fencePathValid c path) then
            bad "Foreign or malformed original identity / fence key."
        elif not (oid expectedHead) || snapshot.Commit <> expectedHead then
            stale "Verified project head changed."
        else Ok ()

let private readMissing binding snapshot path =
    match LedgerStore.readAt binding snapshot path with
    | Ok (None, None) -> Ok ()
    | Ok _ -> stale "Fence/evidence already exists; cannot reopen the original."
    | Error e -> Error e

// Claim and pre-fence exclusion compete for the same previously absent path.
// Call this inside the claim transaction, before adding the fence/intent/args.
// A malformed or historical existing record is also a hold, not absence.
let internal checkClaim binding snapshot expectedHead identity path =
    checkIdentity binding snapshot expectedHead identity path
    |> Result.bind (fun () -> readMissing binding snapshot path)

let private field (doc: JsonElement) (name: string) =
    doc.GetProperty(name).GetString()

// Match the existing compiled HeldBeforeSend record without defining another
// persisted DTO. The full original blob must be present at the verified claim
// origin AND at the current head, so a rewritten or begun send is not eligible.
let private heldRecordMatches (identity: OriginalEffectIdentity) (held: HeldFence) raw =
    try
        use document = JsonDocument.Parse(raw: string)
        let root = document.RootElement
        let c = identity.Candidate
        let original = JsonSerializer.Deserialize<NativeDispatchCandidate>(
                           root.GetProperty("candidate").GetRawText(), options)
        root.GetProperty("schemaVersion").GetInt32() = 1
        && field root "projectId" = c.ProjectId
        && field root "projectLedgerId" = c.ProjectLedgerId
        && root.GetProperty("projectEpoch").GetInt32() = c.ProjectEpoch
        && field root "accountId" = string identity.AccountId
        && field root "accountLedgerId" = string identity.AccountLedgerId
        && root.GetProperty("accountEpoch").GetInt32() = identity.AccountEpoch
        && field root "originalReservationOperationId" = string identity.ReservationOperationId
        && field root "reservationCanonicalKey" = identity.CanonicalKey
        && original = c
        && field root "claimOperationId" = string held.ClaimOperationId
        && field root "dispatchId" = c.DispatchId
        && field root "effectOperationId" = c.EffectOperationId
        && field root "sourceLeaseSha256" = c.SourceLeaseSha256
        && field root "status" = "HeldBeforeSend"
        && field root "providerSessionId" = ""
        && field root "terminalOriginOid" = ""
    with _ -> false

let private readHeld binding snapshot expectedHead identity path (held: HeldFence) =
    checkIdentity binding snapshot expectedHead identity path
    |> Result.bind (fun () ->
        if isNull (box held) || held.ClaimOperationId = Guid.Empty
           || held.ClaimOperationId = identity.ReservationOperationId
           || string held.ClaimOperationId = identity.Candidate.DispatchId
           || string held.ClaimOperationId = identity.Candidate.EffectOperationId
           || not (oid held.ClaimOrigin && oid held.ClaimBlobOid)
           || not (digest held.ClaimRequestSha256) then
            bad "Missing exact original claim witness."
        else
            LedgerStore.getOperationAt binding snapshot held.ClaimOperationId
            |> Result.bind (function
                | Some op when Array.contains path op.Paths
                               && op.RequestHash = held.ClaimRequestSha256
                               && op.Actor = identity.Candidate.OwnerAgentId ->
                    LedgerStore.verifiedIntegrationOperationSnapshotAt binding snapshot op
                    |> Result.bind (fun origin ->
                        if origin.Commit <> held.ClaimOrigin then
                            stale "Claim origin differs."
                        else
                            match LedgerStore.readAt binding origin path,
                                  LedgerStore.readAt binding snapshot path with
                            | Ok (Some original, Some originBlob), Ok (Some current, Some currentBlob)
                                when originBlob = held.ClaimBlobOid
                                     && currentBlob = originBlob && original = current
                                     && heldRecordMatches identity held original -> Ok original
                            | Error e, _ | _, Error e -> Error e
                            | _ -> stale "Original HeldBeforeSend fence is absent, changed or not held.")
                | _ -> stale "Original claim operation does not bind the canonical fence."))

// Begin-send must be a *fresh* committed CAS before any external effect. A
// replayed LedgerWrite.AlreadyApplied, failed CAS or uncertain CAS is NOT send
// permission. This consumes the exact held blob; neither begin-send nor
// exclusion can succeed after the other has consumed it.
let internal planBeginSend binding snapshot expectedHead identity path held =
    readHeld binding snapshot expectedHead identity path held
    |> Result.bind (fun raw ->
        try
            let record = JsonNode.Parse(raw).AsObject()
            record["status"] <- JsonValue.Create("Dispatching")
            Ok { Path = path; ExpectedBlobOid = Some held.ClaimBlobOid
                 Content = record.ToJsonString() }
        with _ -> bad "Malformed held effect.")

let internal noSendEvidencePath (operationId: Guid) =
    $"records/native-scheduler/no-send/{operationId:N}.json"

// The account locator describes account occupancy, not the actual project
// predecessor. The operation intent binds both independently. The evidence
// object remains the exact account-consumer blob; only the enclosing request
// adds the local head/predecessor. A preclaim request can never be rebound to
// a held claim by refreshing its caller arguments or by a CAS retry.
type private ExclusionRequest =
    { ExpectedProjectHead: string
      Predecessor: ExclusionPredecessor
      EvidenceJson: string }

let private exactFields (element: JsonElement) (names: string list) =
    element.ValueKind = JsonValueKind.Object
    && (element.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toList)
       = List.sort names

let rec private uniqueProperties (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        let properties = element.EnumerateObject() |> Seq.toArray
        (properties |> Array.map (fun p -> p.Name) |> Array.distinct |> Array.length) = properties.Length
        && (properties |> Array.forall (fun p -> uniqueProperties p.Value))
    | JsonValueKind.Array -> element.EnumerateArray() |> Seq.forall uniqueProperties
    | _ -> true

let private parseExclusionRequest (identity: OriginalEffectIdentity) (intent: LedgerIntent) =
    try
        use document = JsonDocument.Parse(intent.RequestJson)
        let root = document.RootElement
        if not (exactFields root ["schemaVersion"; "expectedProjectHead"; "predecessor"; "evidence"])
           || not (uniqueProperties root)
           || root.GetProperty("schemaVersion").GetInt32() <> 1 then
            bad "Malformed or ambiguous project exclusion request."
        else
            let head = field root "expectedProjectHead"
            let witness = root.GetProperty("predecessor")
            let predecessor =
                match field witness "kind" with
                | "BeforeClaim" when exactFields witness ["kind"] -> Some BeforeClaim
                | "ExactHeldFence" when
                    exactFields witness ["kind"; "claimOperationId"; "claimRequestSha256"; "claimOrigin"; "claimBlobOid"]
                    && uuid (field witness "claimOperationId") ->
                    Some (ExactHeldFence
                        { ClaimOperationId = Guid.ParseExact(field witness "claimOperationId", "D")
                          ClaimRequestSha256 = field witness "claimRequestSha256"
                          ClaimOrigin = field witness "claimOrigin"
                          ClaimBlobOid = field witness "claimBlobOid" })
                | _ -> None
            let evidence = root.GetProperty("evidence")
            let original = evidence.GetProperty("original")
            let candidate =
                JsonSerializer.Deserialize<NativeDispatchCandidate>(
                    original.GetProperty("candidate").GetRawText(), options)
            let accountFence = field original "expectedProjectFenceOid"
            let generation = original.GetProperty("expectedGeneration").GetInt64()
            let accountLocatorMatches =
                match predecessor with
                | Some BeforeClaim -> accountFence = "" && generation = 1L
                | Some (ExactHeldFence held) ->
                    (accountFence = "" && generation = 1L)
                    || (oid accountFence && accountFence = held.ClaimOrigin && generation > 1L)
                | None -> false
            if not (oid head)
               || not (exactFields evidence ["schemaVersion"; "state"; "accountId"; "accountLedgerId"; "accountEpoch";
                                              "reservationOrigin"; "canonicalKey"; "original"; "noSendOperationId"])
               || not (exactFields original ["reservationOperationId"; "reservationRequestSha256"; "candidate";
                                              "expectedGeneration"; "expectedProjectFenceOid"; "expectedProviderSessionId"])
               || evidence.GetProperty("schemaVersion").GetInt32() <> 1
               || field evidence "state" <> "VerifiedNoSend"
               || field evidence "accountId" <> string identity.AccountId
               || field evidence "accountLedgerId" <> string identity.AccountLedgerId
               || evidence.GetProperty("accountEpoch").GetInt32() <> identity.AccountEpoch
               || not (oid (field evidence "reservationOrigin"))
               || field evidence "canonicalKey" <> identity.CanonicalKey
               || field evidence "noSendOperationId" <> string intent.OperationId
               || field original "reservationOperationId" <> string identity.ReservationOperationId
               || not (digest (field original "reservationRequestSha256"))
               || candidate <> identity.Candidate
               || field original "expectedProviderSessionId" <> ""
               || not accountLocatorMatches then
                bad "Exclusion evidence does not bind the original operation, identity and project predecessor."
            else
                match predecessor with
                | Some p -> Ok { ExpectedProjectHead = head; Predecessor = p; EvidenceJson = evidence.GetRawText() }
                | None -> bad "Missing project predecessor."
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? Collections.Generic.KeyNotFoundException
    | :? FormatException
    | :? ArgumentException ->
        bad "Malformed project exclusion request."

// Atomic exclusion changes only the canonical fence and the immutable evidence
// path. The request envelope is hashed by LedgerStore; the raw nested account
// evidence remains byte-for-byte the consumer's account locator. The caller's
// expected head and predecessor are assertions, never a substitute for the
// immutable request. Authentication of the account receipt remains the
// responsibility of the existing authenticated caller, on every CAS retry.
let internal planExclusion binding snapshot expectedHead identity path predecessor
                           (intent: LedgerIntent) =
    checkIdentity binding snapshot expectedHead identity path
    |> Result.bind (fun () ->
        if isNull (box intent) || intent.OperationId = Guid.Empty
           || intent.OperationId = identity.ReservationOperationId
           || string intent.OperationId = identity.Candidate.DispatchId
           || string intent.OperationId = identity.Candidate.EffectOperationId
           || intent.Kind <> "NativeVerifiedNoSend"
           || String.IsNullOrWhiteSpace intent.Actor
           || String.IsNullOrWhiteSpace intent.RequestJson then
            bad "Missing distinct no-send operation or evidence."
        else
            parseExclusionRequest identity intent
            |> Result.bind (fun request ->
                if request.ExpectedProjectHead <> expectedHead || request.Predecessor <> predecessor then
                    stale "Exclusion request head or predecessor differs from the asserted plan."
                else
                    let evidencePath = noSendEvidencePath intent.OperationId
                    let before =
                        match request.Predecessor with
                        | BeforeClaim ->
                            readMissing binding snapshot path |> Result.map (fun () -> None)
                        | ExactHeldFence held ->
                            if held.ClaimOperationId = intent.OperationId then
                                bad "No-send operation must differ from claim."
                            else
                                readHeld binding snapshot request.ExpectedProjectHead identity path held
                                |> Result.map (fun _ -> Some held.ClaimBlobOid)
                    before
                    |> Result.bind (fun previous ->
                        readMissing binding snapshot evidencePath
                        |> Result.map (fun () ->
                            [ { Path = path; ExpectedBlobOid = previous; Content = request.EvidenceJson }
                              { Path = evidencePath; ExpectedBlobOid = None; Content = request.EvidenceJson } ]))))
