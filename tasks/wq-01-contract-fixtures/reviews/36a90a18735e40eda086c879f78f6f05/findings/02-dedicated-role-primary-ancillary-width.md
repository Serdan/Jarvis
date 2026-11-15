# P2 — Dedicated Role-message primary launch can handle a second message as ancillary work

**Independent reviewer:** Reviewer 1  
**Review:** `36a90a18735e40eda086c879f78f6f05`  
**Exact frozen source:** `4f3b47d85dfe26cbd5a48da37e98f49e4cdfc6bf`  
**Severity:** P2 — explicit one-message dedicated-launch contract and test coverage.  
**Source:** `Server/WorkQueueContracts.fs:713-759, 884-950, 955-992`; `Server.Tests/WorkQueueContractTests.fs:325-359`.  
**Architecture:** §11 lines 369-375: a launch created solely for Role-message work handles one message and completes that primary work/launch atomically; further messages remain distinct Pending generations, with no opportunistic dedicated-primary batch widening.

## Observation

`claimNext` binds exactly one Role message M1 as L's primary work `role-msg:M1` and records `MessageState.ClaimedPrimary L`. The new `hasLivePrimaryActivation` predicate checks a live, ungated, undecided primary stage but does **not** distinguish ordinary Plan/Task primary work from `WorkKind.RoleMessage`. `claimAncillaryMessage` then permits any other Pending same-role message M2 through the ancillary `Some workId` branch.

## Simple valid-state counterexample

Publish two well-formed Role(Lead) messages M1 and M2, with respective Available stage generations, and admit **only one** Lead launch L. After dispatch/registration, `claimNext` selects older M1 as the dedicated primary. With no other unclaimed launches, `matchCredits current [] remaining` succeeds. `claimAncillaryMessage(current.Version, freshOp, L, M2)` claims `role-msg:M2` and M2 as ancillary while `role-msg:M1` remains primary. The L session may then `completeAncillaryMessage(M2)`, and subsequently `completeMessageWork(M1)`; it has handled two Role messages in a launch whose required dedicated-message scope was one. No second primary claim or extra token was created, but the single-message lifecycle restriction has been bypassed.

The existing `case_52_53_dedicated_role_message_claims_exactly_one_message` checks that M2 stays Pending immediately after M1's primary claim and tests with another already-admitted L2 credit. It never attempts ancillary intake for M2 in the **no competing credit** case. The new live-primary predicate tests similarly use Plan, not a RoleMessage primary.

## Required correction and test

Disallow ancillary intake by a launch whose primary stage is dedicated `WorkKind.RoleMessage _` (or explicitly establish a reviewed architectural exception; the currently accepted text describes a one-message launch). Retain ancillary Role intake for a genuinely unrelated active Plan/implementation/review primary and the full compatible-credit rematching rule. Add a valid-input one-launch/two-Role-message negative test with fresh and stale CAS, and a positive Plan-primary control; assert M2 remains Pending and Available, and L's primary M1 completes/terminalizes normally. Test with and without a second reserved launch.

**Disposition:** Contract-level scope defect distinct from the now-correct STOP and coverage fences. No assertion of production deployment. 
