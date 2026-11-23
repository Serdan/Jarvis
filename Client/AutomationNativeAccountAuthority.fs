module Client.AutomationNativeAccountAuthority

open System
open System.Text.Json
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.NativeSchedulerAccountReservation

// Account-side source-only port. A roster proves membership and pool limits,
// not the completeness of live provider sessions or permission to dispatch.
type NativeAccountRosterObservation =
    { AccountHead: string
      ProjectHeads: NativeVerifiedProjectHead list
      AccountId: Guid
      AccountLedgerId: Guid
      AccountEpoch: int
      AccountAuthoritySha256: string
      PolicyVersion: int64
      AuthenticatedSubject: string
      AccountLimits: NativePoolLimits
      ApprovedProjects: NativeRosterProject list }

type NativeOriginalReservationLocator =
    { ReservationOperationId: Guid
      ReservationRequestSha256: string
      Candidate: NativeDispatchCandidate
      ExpectedGeneration: int64
      ExpectedProjectFenceOid: string
      ExpectedProviderSessionId: string }

type NativeOriginalReservationContext =
    { AccountHead: string
      ReservationOrigin: string
      ReservationParent: string
      ReservationReceipt: AccountOperationReceipt
      CurrentReservation: NativeReservationRecord
      VerifiedProjectHeads: NativeVerifiedProjectHead list }

// Deliberately no registered production provider. OAuth transport authentication
// alone does not establish the account-to-subject mapping or signed operator
// approval root required by NativeSchedulerRosterPolicy.
let private hold reason = Error(AccountPolicyNotAuthoritative reason)

let readProductionRoster () : Result<NativeAccountRosterObservation, AccountFailure> =
    hold "Native account subject/operator signing authority is not provisioned."

let readProductionOriginal (_: NativeOriginalReservationLocator)
                           : Result<NativeOriginalReservationContext, AccountFailure> =
    hold "Native account subject/operator signing authority is not provisioned."

let reconcileProduction (_: NativeOriginalReservationLocator) : Result<AccountWrite, AccountFailure> =
    hold "No attributable original-bound operator reconciliation authority is provisioned."

// This reader is internal and always delegates to the marked disposable Git
// sink. It must not be registered by the runtime as production admission.
let internal readDisposableRoster root account key subject projects =
    let projection =
        AccountCapacityLedger.readVerifiedDisposableNativeRosterPolicy
            root account key subject projects
    match projection.Status, projection.VerifiedAccountHead, projection.Policy with
    | "PolicyVerified", Some head, Some policy when projection.MembershipComplete
                                                   && projection.PolicyComplete ->
        Ok { AccountHead = head
             ProjectHeads = Array.toList projection.VerifiedProjectHeads
             AccountId = account.AccountId
             AccountLedgerId = account.LedgerId
             AccountEpoch = account.Epoch
             AccountAuthoritySha256 = policy.AccountAuthoritySha256
             PolicyVersion = policy.Version
             AuthenticatedSubject = policy.AuthenticatedSubject
             AccountLimits = policy.AccountLimits
             ApprovedProjects = Array.toList policy.Projects }
    | _ -> hold (String.concat "; " projection.HoldReasons)

let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private verifyOriginal (b: AccountBinding) (locator: NativeOriginalReservationLocator)
                           (record: NativeReservationRecord)
                           (receipt: AccountOperationReceipt) (parent: string)
                           (previous: string option) (raw: string) =
    try
        use envelope = JsonDocument.Parse(receipt.CanonicalRequestJson)
        let e = envelope.RootElement
        use request = JsonDocument.Parse(e.GetProperty("request").GetString())
        let req = request.RootElement
        let candidate = req.GetProperty("candidate").Deserialize<NativeDispatchCandidate>(options)
        use result = JsonDocument.Parse(receipt.OriginalResultJson)
        let res = result.RootElement
        let original = JsonSerializer.Deserialize<NativeReservationState>(raw, options)
        let matches =
            original.Records
            |> Array.filter (fun r -> r.OriginalOperationId = string locator.ReservationOperationId)
        if receipt.OperationId <> string locator.ReservationOperationId
           || receipt.RequestSha256 <> locator.ReservationRequestSha256
           || e.GetProperty("accountId").GetString() <> string b.AccountId
           || e.GetProperty("kind").GetString() <> "NativeReserve"
           || e.GetProperty("actor").GetString() <> receipt.Actor
           || req.GetProperty("coverageHead").GetString() <> parent
           || candidate <> locator.Candidate || candidate <> record.Candidate
           || res.GetProperty("status").GetString() <> "Reserved"
           || res.GetProperty("dispatchId").GetString() <> record.Candidate.DispatchId
           || res.GetProperty("canonicalKey").GetString() <> record.CanonicalKey
           || original.SchemaVersion <> 1
           || original.AccountId <> string b.AccountId || original.LedgerId <> string b.LedgerId
           || original.Epoch <> b.Epoch || matches.Length <> 1 then
            hold "Original reservation receipt, account lineage or candidate differs."
        else
            let initial = matches[0]
            let previousRecords =
                match previous with
                | None -> [||]
                | Some prior ->
                    let state = JsonSerializer.Deserialize<NativeReservationState>(prior, options)
                    if state.SchemaVersion <> original.SchemaVersion
                       || state.AccountId <> original.AccountId || state.LedgerId <> original.LedgerId
                       || state.Epoch <> original.Epoch then
                        invalidOp "Foreign predecessor reservation document."
                    state.Records
            if initial.Candidate <> record.Candidate
               || initial.CanonicalKey <> record.CanonicalKey
               || initial.Status <> "Reserved" || initial.Generation <> 1L
               || not (String.IsNullOrEmpty initial.ProjectFenceOid)
               || not (String.IsNullOrEmpty initial.ProviderSessionId)
               || not (String.IsNullOrEmpty initial.TerminalOperationId)
               || original.Records <> Array.append previousRecords [| initial |] then
                hold "Original reservation CAS did not create the exact initial reservation."
            else Ok ()
    with _ -> hold "Original reservation receipt or origin payload is malformed."

// Exact original receipt and current record context, for disposable verification
// only. The independently supplied disposable coverage is NOT manufactured
// from roster membership. This is evidence, not a release or a terminal event.
let internal readDisposableOriginal root (account: AccountBinding) key subject projects
                                    (coverage: NativeDisposableCoverage)
                                    (locator: NativeOriginalReservationLocator) =
    if isNull (box locator) || isNull (box locator.Candidate)
       || locator.ReservationOperationId = Guid.Empty || locator.ExpectedGeneration < 1L then
        hold "Exact original reservation locator required."
    else
        let read () =
            NativeSchedulerAccountReservation.readVerifiedDisposable
                root account key subject projects coverage
        read ()
        |> Result.bind (fun (snapshot, state) ->
            match state.Records |> Array.tryFind (fun r ->
                r.OriginalOperationId = string locator.ReservationOperationId) with
            | Some record when record.Candidate = locator.Candidate
                               && record.Generation = locator.ExpectedGeneration
                               && record.ProjectFenceOid = locator.ExpectedProjectFenceOid
                               && record.ProviderSessionId = locator.ExpectedProviderSessionId ->
                AccountCapacityLedger.readNativeReserveOrigin account snapshot locator.ReservationOperationId
                |> Result.bind (fun (receipt, origin, parent, previous, raw) ->
                    verifyOriginal account locator record receipt parent previous raw
                    |> Result.bind (fun () ->
                        // Re-read signed roster, complete project heads and exact
                        // account state after receipt/origin inspection.
                        read ()
                        |> Result.bind (fun (current, currentState) ->
                            if current.Head <> snapshot.Head || currentState <> state then
                                hold "Account state changed during original reservation verification."
                            else
                                Ok { AccountHead = snapshot.Head
                                     ReservationOrigin = origin
                                     ReservationParent = parent
                                     ReservationReceipt = receipt
                                     CurrentReservation = record
                                     VerifiedProjectHeads = Array.toList coverage.ProjectHeads })))
            | _ -> hold "Current reservation identity, generation, project fence or session differs.")
