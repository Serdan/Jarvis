# P2 — Return-for-revision permits an unchanged immediate resubmission

**Frozen source:** `2d1b1a07c44cd420eb2a418fcc2895343ebc72c1`
**Affected:** `Server/WorkProposal.fs:196-207` and `Server.Tests/WorkProposalTests.fs:75-99`

## Finding
`Return(verifiedLeadDecision, "revise scope")` changes only the state from Proposed to Draft and appends a Returned event. `Submit` accepts any Draft, including one just returned with the **identical** `CurrentSpec` pin/version and no intervening `ReviseDraft`. A submit immediately after return therefore emits fresh Lead-planning demand for the exact rejected proposal.

The test named `returnedProposalRequiresRevisionSubmissionAndFreshApproval` tests only the successful route with an intervening revision, not the forbidden/ambiguous no-revision path. The target spec describes the Lead's return as **for revision** (§3), and the task explicitly requires the returned revision path.

## Requested resolution
If return-for-revision is intended to require a changed spec, retain the last-returned spec version or equivalent canonical evidence and reject unchanged `Submit` until `ReviseDraft` has advanced the pin; test rejection and the positive revised path. If unchanged resubmission is intentionally permitted, clarify that policy in the spec and test it expressly; do not imply the current transition enforces revision. This is an additional contract concern, not the sole basis of the P1 verdict.