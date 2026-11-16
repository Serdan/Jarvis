# Reviewer 2 — independent WQ12 durable launch-intent P1 re-review

**Frozen source:** `91bc7db59ca8a9c41f1cd1a95a1330c5ba1078b6`
**Baseline:** `fa9a858839394abce61ec350f1f1c780fe10385f`
**Previous rejected frozen source:** `9924d0ad24b3478e8590274775a926244312e66f`.
**Recommended verdict:** **Accept for the explicitly default-off source-origin planning Work intent binder.** Both previously recorded P1 recovery issues are addressed. This is not approval for live Scheduler activation or browser/provider execution.

## Previously recorded P1: different-client admission actor

`DurablePlanningLaunchIntent` now records the canonical `AdmissionActor` field (WorkQueueDurableLaunchIntent.fs:23-56), fixed as `WQ12.Prepared:<PrepareOperationId>` at creation (326-366) and checked against the creating operation's immutable identity on read (108-148). `admitPreparedExplicit` builds the WQ07/WQ04 `AccountAdmissionRequest.Actor` from this stored actor, not the invoking client's audit label (539-551). The existing WQ07 request hash includes actor (`AccountCapacityLedger.fs:480-499`), while WQ04 original replay verifies that actor (`WorkQueuePracticalAdmission.fs:155-171`). Different clients therefore replay the same original account/project request and cannot mutate its actor-sensitive semantic identity.

## Previously recorded P1: WQ07 held, WQ04 missing

`admitPreparedExplicit` now first fetches original canonical prepared intent and project state. If the project launch is already present, the lower immutable issuing-CAS verifier handles exact replay. When no project launch exists but a WQ07 Held token matches the precise launch UUID, original admission UUID, project ID/ledger/epoch/authority, role ID/version, the binder checks the original planning Work's stage/generation/eligibility and invokes the existing `WorkQueuePracticalAdmission.admitExplicit` using the same original UUID and immutable actor (WorkQueueDurableLaunchIntent.fs:475-551). It **does not** re-run the no-capacity global winner requirement in this already-reserved recovery case. New reservations still require fresh cross-project FIFO, account head, source OID and role selection (519-535). Foreign/released/inconsistent tokens remain held/blocked, never automatically released or substituted.

The lower `AccountAdmissionAdapter.admitUsing` replays the immutable WQ07 original reservation and checks the held token before project CAS (AccountAdmissionAdapter.fs:375-432). `WorkQueuePracticalAdmission` verifies actual current role/source eligibility for a new project admission and exact historical issuing work/role for replay (WorkQueuePracticalAdmission.fs:102-225). This preserves the two-ledger safety boundary without pretending to make a cross-Git atomic transaction.

## Independent verification

Executed on registered clean worktree `worker1-work-source-intent-20261116` at frozen `91bc7db5`:

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~WorkQueueDurableLaunchIntentTests|FullyQualifiedName~WorkQueueCrossProjectHostPreviewTests|FullyQualifiedName~WorkQueueWorkReplacementVerticalSliceTests|FullyQualifiedName~WorkQueueVerifiedDispatchAdapterTests' --no-restore`

**21 passed, 0 failed, 0 skipped** (.NET 11 Release). The revised fixtures include two clients with different invoking actor strings, WQ07 held-only cap-one recovery without an existing project launch, a conflicting original WQ07 actor that must not reuse an operation UUID, and original replay without a second account token. `git diff --check` is clean. Frozen Client/Server `git grep` finds only the default-off WQ12 source module defining `prepareExplicit` and `admitPreparedExplicit`; no production MCP, scheduler or provider caller is registered.

## Scope boundary

This acceptance does **not** authorize live Work launch or claim, browser send, provider/model execution, scheduler timer, account-token release, or a production cross-ledger transaction. The module still depends on separately approved account/project bindings and the future authenticated execution/journal, integration and cutover reviews. The previously documented non-atomic WQ07/account and WQ02/project Git remotes remain separately reconciled.