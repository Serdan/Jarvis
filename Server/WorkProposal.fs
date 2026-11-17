namespace Jarvis.WorkQueues

open System

// Stateful Work target: pure domain decisions only. The SQLite owner must commit
// the updated record and any publication intention in ONE transaction. Neither
// a Draft write nor a generic TaskCreated event authorizes an executable stage.
[<RequireQualifiedAccess>]
type WorkProposalState =
    | Draft
    | Proposed
    | Open

type WorkProposalSpecPin =
    { Version: int
      BlobOid: string }

[<RequireQualifiedAccess>]
type LeadProposalAction =
    | ReturnForRevision
    | Approve
    | ChangePriority

// The storage/authentication adapter verifies this receipt against the
// authenticated Lead role and canonical Work/version/spec before invoking the
// pure transition. A caller-supplied role label is never sufficient proof.
type VerifiedLeadProposalDecision =
    { ReceiptId: string
      WorkId: string
      ExpectedVersion: int
      Spec: WorkProposalSpecPin
      Action: LeadProposalAction
      // Values are bound to the verified Lead receipt; command callers cannot override them.
      AuthorizedPriority: int option
      AuthorizedReason: string option
      CanonicallyVerified: bool }

// Separate explicit authority for routine internal original producers such as
// accepted review/integration successors. This is NOT the interactive default.
type VerifiedInternalWorkProducer =
    { ReceiptId: string
      WorkId: string
      OriginalEventId: string
      Spec: WorkProposalSpecPin
      AuthorizedPriority: int
      MayPublishOpen: bool
      CanonicallyVerified: bool }

[<RequireQualifiedAccess>]
type WorkProposalEvent =
    | DraftRevised of version: int * spec: WorkProposalSpecPin
    | Submitted of version: int * spec: WorkProposalSpecPin
    | Returned of version: int * receiptId: string * reason: string
    | Approved of version: int * receiptId: string * priority: int
    | InternalOpen of version: int * receiptId: string * sourceId: string
    | PriorityChanged of version: int * receiptId: string * priority: int * reason: string

type WorkProposalRecord =
    { WorkId: string
      Version: int
      State: WorkProposalState
      CurrentSpec: WorkProposalSpecPin
      LastReturnedSpec: WorkProposalSpecPin option
      ApprovedSpec: WorkProposalSpecPin option
      LeadPriority: int option
      ApprovalReceipt: string option
      OriginalProducerReceipt: string option
      History: WorkProposalEvent list }

type WorkProposalPublication =
    { WorkId: string
      Version: int
      ApprovedSpec: WorkProposalSpecPin
      LeadPriority: int
      AuthorizationReceipt: string }

// The caller must persist Publication alongside Record before any producer can
// observe an Open item. It is an intention, not a source effect or dispatch.
type WorkProposalUpdate =
    { Record: WorkProposalRecord
      Publication: WorkProposalPublication option }

[<RequireQualifiedAccess>]
type WorkProposalDemand =
    | NoDemand
    | LeadPlanning of workId: string * submittedSpec: WorkProposalSpecPin
    | ExecutionEligible of workId: string * approvedSpec: WorkProposalSpecPin

[<RequireQualifiedAccess>]
type WorkProposalCommand =
    | ReviseDraft of WorkProposalSpecPin
    | Submit
    | Return of VerifiedLeadProposalDecision * reason: string
    | Approve of VerifiedLeadProposalDecision * priority: int
    | SetPriority of VerifiedLeadProposalDecision * priority: int * reason: string

[<RequireQualifiedAccess>]
type WorkProposalError =
    | InvalidIdentity
    | InvalidSpecPin
    | VersionConflict of expected: int * actual: int
    | InvalidTransition of state: WorkProposalState * action: string
    | UnverifiedAuthority
    | DecisionMismatch
    | InvalidReason
    | RevisionRequired

[<RequireQualifiedAccess>]
module WorkProposal =
    let private nonBlank (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validSpec (spec: WorkProposalSpecPin) =
        spec.Version > 0 && nonBlank spec.BlobOid

    let private event version event update =
        { update with
            Version = version
            History = update.History @ [ event ] }

    let private publication (record: WorkProposalRecord) =
        match record.State, record.ApprovedSpec, record.LeadPriority with
        | WorkProposalState.Open, Some pin, Some priority ->
            record.ApprovalReceipt
            |> Option.orElse record.OriginalProducerReceipt
            |> Option.map (fun receipt ->
                { WorkId = record.WorkId
                  Version = record.Version
                  ApprovedSpec = pin
                  LeadPriority = priority
                  AuthorizationReceipt = receipt })
        | _ -> None

    // Interactive/ad-hoc creation is always Draft.
    let createDraft workId pin =
        if not (nonBlank workId) then Error WorkProposalError.InvalidIdentity
        elif not (validSpec pin) then Error WorkProposalError.InvalidSpecPin
        else
            Ok
                { WorkId = workId
                  Version = 1
                  State = WorkProposalState.Draft
                  CurrentSpec = pin
                  LastReturnedSpec = None
                  ApprovedSpec = None
                  LeadPriority = None
                  ApprovalReceipt = None
                  OriginalProducerReceipt = None
                  History = [] }

    // The only original-producer bypass path. The adapter must verify source,
    // original workflow, scope, role and receipt within the database transaction.
    let createAuthorizedOpen workId pin priority (authorization: VerifiedInternalWorkProducer) =
        if not (nonBlank workId) then Error WorkProposalError.InvalidIdentity
        elif not (validSpec pin) then Error WorkProposalError.InvalidSpecPin
        elif not authorization.CanonicallyVerified || not authorization.MayPublishOpen then
            Error WorkProposalError.UnverifiedAuthority
        elif authorization.WorkId <> workId || authorization.Spec <> pin
             || authorization.AuthorizedPriority <> priority
             || not (nonBlank authorization.ReceiptId)
             || not (nonBlank authorization.OriginalEventId) then
            Error WorkProposalError.DecisionMismatch
        else
            let record =
                { WorkId = workId
                  Version = 1
                  State = WorkProposalState.Open
                  CurrentSpec = pin
                  LastReturnedSpec = None
                  ApprovedSpec = Some pin
                  LeadPriority = Some priority
                  ApprovalReceipt = None
                  OriginalProducerReceipt = Some authorization.ReceiptId
                  History = [ WorkProposalEvent.InternalOpen(1, authorization.ReceiptId, authorization.OriginalEventId) ] }
            Ok { Record = record; Publication = publication record }

    let private verifyDecision action priority reason (decision: VerifiedLeadProposalDecision) (record: WorkProposalRecord) =
        if not decision.CanonicallyVerified then Error WorkProposalError.UnverifiedAuthority
        elif decision.WorkId <> record.WorkId
             || decision.ExpectedVersion <> record.Version
             || decision.Spec <> record.CurrentSpec
             || decision.Action <> action
             || decision.AuthorizedPriority <> priority
             || decision.AuthorizedReason <> reason
             || not (nonBlank decision.ReceiptId) then
            Error WorkProposalError.DecisionMismatch
        else Ok ()

    let private checkReason reason =
        nonBlank reason && reason.Length <= 1024

    let apply expectedVersion command (record: WorkProposalRecord) =
        if record.Version <> expectedVersion then
            Error(WorkProposalError.VersionConflict(expectedVersion, record.Version))
        else
            let nextVersion = record.Version + 1
            let updated change = Ok { Record = change; Publication = None }
            match command, record.State with
            | WorkProposalCommand.ReviseDraft pin, WorkProposalState.Draft ->
                if not (validSpec pin) || pin.Version <> record.CurrentSpec.Version + 1
                   || pin.BlobOid = record.CurrentSpec.BlobOid then
                    Error WorkProposalError.InvalidSpecPin
                else
                    event nextVersion (WorkProposalEvent.DraftRevised(nextVersion, pin))
                        { record with CurrentSpec = pin }
                    |> updated
            | WorkProposalCommand.Submit, WorkProposalState.Draft ->
                if record.LastReturnedSpec = Some record.CurrentSpec then
                    Error WorkProposalError.RevisionRequired
                else
                    event nextVersion (WorkProposalEvent.Submitted(nextVersion, record.CurrentSpec))
                        { record with State = WorkProposalState.Proposed }
                    |> updated
            | WorkProposalCommand.Return(decision, reason), WorkProposalState.Proposed ->
                verifyDecision LeadProposalAction.ReturnForRevision None (Some reason) decision record
                |> Result.bind (fun () ->
                    if not (checkReason reason) then Error WorkProposalError.InvalidReason
                    else
                        event nextVersion (WorkProposalEvent.Returned(nextVersion, decision.ReceiptId, reason))
                            { record with
                                State = WorkProposalState.Draft
                                LastReturnedSpec = Some record.CurrentSpec }
                        |> updated)
            | WorkProposalCommand.Approve(decision, priority), WorkProposalState.Proposed ->
                verifyDecision LeadProposalAction.Approve (Some priority) None decision record
                |> Result.map (fun () ->
                    let approved =
                        event nextVersion (WorkProposalEvent.Approved(nextVersion, decision.ReceiptId, priority))
                            { record with
                                State = WorkProposalState.Open
                                ApprovedSpec = Some record.CurrentSpec
                                LeadPriority = Some priority
                                ApprovalReceipt = Some decision.ReceiptId }
                    { Record = approved; Publication = publication approved })
            | WorkProposalCommand.SetPriority(decision, priority, reason), WorkProposalState.Open ->
                verifyDecision LeadProposalAction.ChangePriority (Some priority) (Some reason) decision record
                |> Result.bind (fun () ->
                    if not (checkReason reason) then Error WorkProposalError.InvalidReason
                    else
                        event nextVersion (WorkProposalEvent.PriorityChanged(nextVersion, decision.ReceiptId, priority, reason))
                            { record with LeadPriority = Some priority }
                        |> updated)
            | WorkProposalCommand.ReviseDraft _, state -> Error(WorkProposalError.InvalidTransition(state, "revise"))
            | WorkProposalCommand.Submit, state -> Error(WorkProposalError.InvalidTransition(state, "submit"))
            | WorkProposalCommand.Return _, state -> Error(WorkProposalError.InvalidTransition(state, "return"))
            | WorkProposalCommand.Approve _, state -> Error(WorkProposalError.InvalidTransition(state, "approve"))
            | WorkProposalCommand.SetPriority _, state -> Error(WorkProposalError.InvalidTransition(state, "priority"))

    // Readiness is an independently verified composition/source/role/queue
    // predicate. Open alone MUST NOT materialize implementation demand.
    let demand readinessVerified (record: WorkProposalRecord) =
        match record.State with
        | WorkProposalState.Draft -> WorkProposalDemand.NoDemand
        | WorkProposalState.Proposed -> WorkProposalDemand.LeadPlanning(record.WorkId, record.CurrentSpec)
        | WorkProposalState.Open when readinessVerified ->
            // Eligibility is NOT a second publication event; the immutable
            // approval intention was already returned by the CAS transition.
            record.ApprovedSpec
            |> Option.map (fun pin -> WorkProposalDemand.ExecutionEligible(record.WorkId, pin))
            |> Option.defaultValue WorkProposalDemand.NoDemand
        | WorkProposalState.Open -> WorkProposalDemand.NoDemand
