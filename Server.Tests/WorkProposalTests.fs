module ServerWorkProposalTests

open NUnit.Framework
open Jarvis.WorkQueues

let private ok = function
    | Ok value -> value
    | Error reason -> failwithf "Unexpected proposal rejection: %A" reason

let private expectError (expected: WorkProposalError) = function
    | Error reason -> Assert.That((reason = expected), Is.True)
    | Ok _ -> Assert.Fail(sprintf "Expected %A" expected)

let private pin version =
    { Version = version
      BlobOid = sprintf "spec-sha-%d" version }

let private draft () = WorkProposal.createDraft "feature-1" (pin 1) |> ok

let private verified action (record: WorkProposalRecord) =
    { ReceiptId = sprintf "lead-%A-%d" action record.Version
      WorkId = record.WorkId
      ExpectedVersion = record.Version
      Spec = record.CurrentSpec
      Action = action
      CanonicallyVerified = true }

let private submitted () =
    let original = draft ()
    WorkProposal.apply original.Version WorkProposalCommand.Submit original |> ok |> fun u -> u.Record

[<Test>]
let draftHasNoDemandAndSubmitEmitsOnlyLead () =
    let original = draft ()
    Assert.That(WorkProposal.demand true original, Is.EqualTo(WorkProposalDemand.NoDemand))
    let result = WorkProposal.apply original.Version WorkProposalCommand.Submit original |> ok
    Assert.That(result.Publication.IsNone, Is.True)
    Assert.That(result.Record.State, Is.EqualTo(WorkProposalState.Proposed))
    Assert.That(WorkProposal.demand true result.Record,
                Is.EqualTo(WorkProposalDemand.LeadPlanning("feature-1", pin 1)))
    Assert.That(result.Record.History.Length, Is.EqualTo(1))

[<Test>]
let approvalPublishesPinnedSpecAndReadinessGatesDemand () =
    let pending = submitted ()
    let proof = verified LeadProposalAction.Approve pending
    let approved =
        WorkProposal.apply pending.Version (WorkProposalCommand.Approve(proof, -5)) pending |> ok
    Assert.That(approved.Record.State, Is.EqualTo(WorkProposalState.Open))
    Assert.That(approved.Record.ApprovedSpec, Is.EqualTo(Some(pin 1)))
    Assert.That(approved.Record.LeadPriority, Is.EqualTo(Some -5))
    Assert.That(approved.Publication.IsSome, Is.True)
    Assert.That(approved.Publication.Value.AuthorizationReceipt, Is.EqualTo(proof.ReceiptId))
    Assert.That(WorkProposal.demand false approved.Record, Is.EqualTo(WorkProposalDemand.NoDemand))
    Assert.That(WorkProposal.demand true approved.Record,
                Is.EqualTo(WorkProposalDemand.ExecutionEligible("feature-1", pin 1)))

[<Test>]
let unverifiedAndMismatchedLeadDecisionsCannotApprove () =
    let pending = submitted ()
    let proof = verified LeadProposalAction.Approve pending
    WorkProposal.apply pending.Version
        (WorkProposalCommand.Approve({ proof with CanonicallyVerified = false }, 5)) pending
    |> expectError WorkProposalError.UnverifiedAuthority
    WorkProposal.apply pending.Version
        (WorkProposalCommand.Approve({ proof with Spec = pin 8 }, 5)) pending
    |> expectError WorkProposalError.DecisionMismatch
    WorkProposal.apply pending.Version
        (WorkProposalCommand.Approve({ proof with Action = LeadProposalAction.ReturnForRevision }, 5)) pending
    |> expectError WorkProposalError.DecisionMismatch
    WorkProposal.apply (pending.Version - 1)
        (WorkProposalCommand.Approve(proof, 5)) pending
    |> expectError (WorkProposalError.VersionConflict(pending.Version - 1, pending.Version))

[<Test>]
let returnedProposalRequiresRevisionSubmissionAndFreshApproval () =
    let pending = submitted ()
    let oldProof = verified LeadProposalAction.Approve pending
    let returned =
        WorkProposal.apply pending.Version
            (WorkProposalCommand.Return(verified LeadProposalAction.ReturnForRevision pending, "scope too wide")) pending
        |> ok
    Assert.That(returned.Publication.IsNone, Is.True)
    Assert.That(returned.Record.State, Is.EqualTo(WorkProposalState.Draft))
    Assert.That(WorkProposal.demand true returned.Record, Is.EqualTo(WorkProposalDemand.NoDemand))
    let revised =
        WorkProposal.apply returned.Record.Version
            (WorkProposalCommand.ReviseDraft(pin 2)) returned.Record |> ok
    let resubmitted =
        WorkProposal.apply revised.Record.Version WorkProposalCommand.Submit revised.Record |> ok
    WorkProposal.apply resubmitted.Record.Version
        (WorkProposalCommand.Approve(oldProof, 5)) resubmitted.Record
    |> expectError WorkProposalError.DecisionMismatch
    let approved =
        WorkProposal.apply resubmitted.Record.Version
            (WorkProposalCommand.Approve(verified LeadProposalAction.Approve resubmitted.Record, 5))
            resubmitted.Record |> ok
    Assert.That(approved.Publication.Value.ApprovedSpec, Is.EqualTo(pin 2))
    Assert.That(approved.Record.History.Length, Is.EqualTo(5))

[<Test>]
let openSpecImmutableAndPriorityChangesAreVersioned () =
    let pending = submitted ()
    let published =
        WorkProposal.apply pending.Version
            (WorkProposalCommand.Approve(verified LeadProposalAction.Approve pending, 1)) pending |> ok
    WorkProposal.apply published.Record.Version
        (WorkProposalCommand.ReviseDraft(pin 2)) published.Record
    |> expectError (WorkProposalError.InvalidTransition(WorkProposalState.Open, "revise"))
    let changed =
        WorkProposal.apply published.Record.Version
            (WorkProposalCommand.SetPriority(
                verified LeadProposalAction.ChangePriority published.Record, 20, "critical path"))
            published.Record |> ok
    Assert.That(changed.Publication.IsNone, Is.True)
    Assert.That(changed.Record.ApprovedSpec, Is.EqualTo(published.Record.ApprovedSpec))
    Assert.That(changed.Record.LeadPriority, Is.EqualTo(Some 20))
    Assert.That(changed.Record.History.Length, Is.EqualTo(3))
    WorkProposal.apply published.Record.Version WorkProposalCommand.Submit changed.Record
    |> expectError (WorkProposalError.VersionConflict(published.Record.Version, changed.Record.Version))

[<Test>]
let internalOriginalProducerRequiresExplicitVerifiedBypass () =
    let internalAuth =
        { ReceiptId = "original-reviewed-receipt"
          WorkId = "feature-1"
          OriginalEventId = "review-successor-1"
          Spec = pin 1
          MayPublishOpen = true
          CanonicallyVerified = true }
    WorkProposal.createAuthorizedOpen "feature-1" (pin 1) 3
        { internalAuth with CanonicallyVerified = false }
    |> expectError WorkProposalError.UnverifiedAuthority
    WorkProposal.createAuthorizedOpen "feature-1" (pin 1) 3
        { internalAuth with MayPublishOpen = false }
    |> expectError WorkProposalError.UnverifiedAuthority
    WorkProposal.createAuthorizedOpen "other" (pin 1) 3 internalAuth
    |> expectError WorkProposalError.DecisionMismatch
    let authorized = WorkProposal.createAuthorizedOpen "feature-1" (pin 1) 3 internalAuth |> ok
    Assert.That(authorized.Record.State, Is.EqualTo(WorkProposalState.Open))
    Assert.That(authorized.Publication.IsSome, Is.True)
    Assert.That(authorized.Record.OriginalProducerReceipt, Is.EqualTo(Some internalAuth.ReceiptId))

[<Test>]
let illegalTransitionsAndUnpinnedRevisionsFailClosed () =
    let original = draft ()
    WorkProposal.apply original.Version
        (WorkProposalCommand.Approve(
            { ReceiptId = "fake"; WorkId = "feature-1"; ExpectedVersion = 1
              Spec = pin 1; Action = LeadProposalAction.Approve; CanonicallyVerified = true }, 0))
        original
    |> expectError (WorkProposalError.InvalidTransition(WorkProposalState.Draft, "approve"))
    WorkProposal.apply original.Version (WorkProposalCommand.ReviseDraft(pin 3)) original
    |> expectError WorkProposalError.InvalidSpecPin
    let pending = submitted ()
    WorkProposal.apply pending.Version
        (WorkProposalCommand.Return(verified LeadProposalAction.ReturnForRevision pending, " ")) pending
    |> expectError WorkProposalError.InvalidReason
    WorkProposal.apply pending.Version WorkProposalCommand.Submit pending
    |> expectError (WorkProposalError.InvalidTransition(WorkProposalState.Proposed, "submit"))
