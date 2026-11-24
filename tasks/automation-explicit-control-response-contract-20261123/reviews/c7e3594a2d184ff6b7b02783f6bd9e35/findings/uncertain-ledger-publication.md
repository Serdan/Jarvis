# Blocking: uncertain ledger publication reported as confirmed state effects

Reviewed immutable head: eaf2286af6540cdd0b169fe8291d66a1f078b3ff.

The explicit operational response task requires preserving the distinction between confirmed, unknown and pending state-change evidence and conservative cache invalidation. The typed contract still incorrectly classifies an actual uncertain ledger publication as confirmed. This is an incomplete handling of an existing error path, not a claim that the underlying ledger publication defect originated in this branch.

Source trace:
- Client/Modules/LedgerStore.fs:1474-1509 produces OutcomeUnknown when publication cannot be established.
- Client/Modules/OrchestrationApi.fs:26-51 maps it to LedgerFailureError with Code=OutcomeUnknown; executeLedgerWrite at 2130-2161 can return that error after unsuccessful verification. The operation ID is attached at 2056-2092.
- Client/Effect.fs:22-31 maps this to AgentError.LedgerFailure, not AgentError.UncertainOutcome.
- Client/SignalR/Client.fs:1510-1541 supplies the known implicit and executed effects list, even on that error (possibly empty, possibly including a separately committed activation).
- Common/Messages/AgentCommand.fs:927-935 infers Unknown only for UncertainOutcome or a null change list; otherwise returns Confirmed. The wire mapper at 991-999 then labels the incomplete list confirmed.
- Server/McpTools.fs:509-546 emits complete-state metadata; publishChanges at 978-1002 consequently skips conservative invalidate-all. Other possibly changed agents may retain stale cache entries.

Independent executable reproduction against frozen Release assembly, job 3ba37f4ba26b4f02b2f8ac596de2d9dc, deliberate assertion failure (exit 1, not a build failure):

    let prior = [{ ProjectName = "Demo"; AgentId = "AlreadyActivated" }]
    let error = AgentError.LedgerFailure { Code = "OutcomeUnknown"; Message = "Cannot verify operation after publication"; OperationId = Some "operation"; VerifiedLedgerHead = None }
    let response : AgentCommandResponse = { Result = None; Error = Some error; Instructions = []; AgentStateChanges = prior; InboxActivation = None; PendingJob = None }
    let wire = JsonSerializer.Serialize response
    let roundtrip = JsonSerializer.Deserialize<AgentCommandResponse> wire
    assert (AgentStateEvidence.isUnknown roundtrip)

Observed response.StateChangeEvidence and roundtrip.StateChangeEvidence both Confirmed [AlreadyActivated], wire AgentStateChangesStatus="confirmed". Expected Unknown with the known partial list retained. No actual external publication attempted by this test; the source trace establishes the error path.

Acceptance: classify actual typed uncertain publication as Unknown, retain known effects, operation UUID and any separately committed activation; ensure the same classification is used by merge, wire projection and conservative invalidation. Add regression coverage for empty and nonempty known evidence, direct and terminal/promoted responses, and compatibility. Server.Tests/ServiceTests.fs:1610-1642 currently checks the OutcomeUnknown error formatting/code, not completeness or invalidation. Remedy design and any separate correction assignment remain with the implementation/adjudication roles.