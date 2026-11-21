# Native project/effect fence — Stage A evidence and fixture seam

**Date:** 2026-11-21. **Owner:** Jarvis Worker 19. **Canonical task:** `scheduler-native-project-effect-fence-20261121`, pinned spec v2, blob `5ea021c8ff29c0155058ce4065f72a6411b1b97b`, attempt `dd9cc84573294d518a19202611210019`, generation 1.

**Scope of this artifact:** Stage A only: offline inspection, isolated test-double construction, nonoverlap and exact dependency check. No runtime integration, no production browser call, no live Git account/project authority mutation, no deployment, schedule change or scheduler activation. Stage B remains HOLD.

## Verified source and dependency boundary

- Source base: `d702352ef705f9991ac1cb14dc15a13cce78aa44` (`main` at first inspection, clean). Own isolated worktree: `scheduler-project-effect-fence-stagea-worker19-20261121`, branch `refs/heads/test/scheduler-native-project-effect-fence-stagea-worker19-20261121`; canonical attempt source binding verified on the same base and target `refs/heads/main`. Main was not edited.
- Independently Accepted architecture audit: `docs/scheduler/capacity-authority-audit.md` at frozen `95410cc5eec2383f80160b5aaeb9b15660b45b59`, review `a0468ce277e744d8a91cd2d263311b16` Accepted, decision 9647. Read from its separate registered worktree because it is not in the baseline `main` checkout.
- Accepted pure Phase-1 planner: `scripts/jarvis-ledger-planner.mjs` at `34facab5db97b2f238d2415c8f7ae6b59866b754`, review `c2d0f7a1e8b44a9f93d51b0a6f72e4c8` as pinned by the audit. It is a planner, not capacity/claim authority, and is not present on the inspected `main`.
- Phase-2 canonical snapshot adapter: `scripts/jarvis-phase2-snapshot-adapter.mjs`, separate Worker 6 branch, with documentation `docs/scheduler/phase-2-snapshot-adapter.md`. The inspected adapter deliberately leaves `completeness.projects=false`, `accountCapacity.complete=false`, and native occupancy unproved. Its task attempt is Finished, but no integration submission was found for that task; no authority may be inferred from its local checkout.
- Authenticated roster/policy: Worker 1 frozen `bfa9cbe94a1e4a10aa18c3f78add6d3fec54a859`, independently Accepted review `afc757b14b8e4f83bd51210e5458ad5c` decision 9752, but integration submission `0a0f0c376ef54d14b44ad2110bfb4b16` remains **Pending** and is not verified on main.
- Native account reservation CAS: `scheduler-native-account-reservation-cas-20261121` remains Started under Worker 4, with no independently Accepted/integrated release evidence observed. This alone keeps Stage B on HOLD. Separate authenticated provider/session terminal proof and Operator live authorization remain required after both dependencies.

## Exact existing mechanism inspection

| Source | Relevant observed contract | Non-authority / required join |
| --- | --- | --- |
| `Client/Modules/LedgerStore.fs:59-101` | Bound `ProjectId`, `LedgerId`, `Epoch`, approved fetch/push remote, immutable `LedgerSnapshot` head and request receipts | Must be actual current project-head CAS with original native dispatch identity, not a detached local preflight |
| `Client/Modules/ReviewWorkflow.fs:3498-3514` | `claimBranchReview` requires Unclaimed + exact version and writes under `LegacyDirectWriterFence.transact` | A successful reviewer claim is not a browser send, capacity reservation or provider terminal |
| `Client/Modules/WorkQueueCanonicalCasBinder.fs:100-155` | Audit-only inspection is deliberately `RecoveryBlocked`; existing practical binder is WQ04 Work Queue stage authority | Never reinterpret it as native pre-send fence or producer permission |
| `Client/Modules/OriginalBrowserJournal.fs:47-96,360-397` and `Client.Tests/OriginalBrowserJournalTests.fs:51-120` | WQ03 client-owned SQLite original-operation marker before fake callback, immutable outcome, lost-ACK/restart cannot re-mark | Strong test pattern, but **not** an authenticated native project-ledger CAS; cannot be native authority or a substitute for project claim |
| `Client/Modules/BrowserBridge.fs:299-368` | `OpenTab` selects registered window, sends `openTab` and returns `OutcomeUnknown` on timeout/exception after start | OpenTab ACK/registered tab state cannot establish model prompt submission or provider terminal. A retry is unsafe |
| `Client.Tests/BrowserBridgeTests.fs:44-112` | Local fake WebSocket handshake/response fixture using protocol version 2 | Reuse the fake-transport test style; never open a real extension/tab in Scheduler tests |
| `Client/Modules/AccountCapacityLedger.fs`, `AccountAdmissionAdapter.fs` (audit lines 17-20) | WQ07 account Git CAS, immutable operation receipts, lost-ACK handling and token release exist | WQ07 `Held` tokens are Work Queue launches, not native Worker/Reviewer session occupancy; Worker 4 owns the native extension |

## Bounded nonoverlapping Stage B design seam (proposal only)

Add one small `NativeProjectEffectFence` adapter to the **existing** project Git CAS ledger; do not fork the account ledger, WQ04 journal, WQ03 SQLite journal, or Phase-1 planner. Its immutable pre-send envelope must include:

1. Account ID/ledger ID/epoch, current verified account head and the **exact original** account reservation receipt/operation ID.
2. Project ID/ledger ID/epoch, approved remote/ref, verified current project head, native dispatch UUID and exact project send-claim operation ID.
3. One typed canonical candidate: Worker task/attempt/owner/assignment generation/source repository/ref/base/target/verified lease, or Reviewer task/review ID/version/frozen reviewed head/source/ref and authenticated claim winner.
4. Original browser effect UUID, host/device/registered browser-window binding and request hash. No new effect UUID may replace an Uncertain original.
5. Monotonic effect status: durable exclusive pre-send claim, `Dispatching`, `Submitted` or `Uncertain`, with separately evidenced `PrestartNoSend`, provider `Active/Unknown`, and verified terminal. All transitions retain original identity and immutable receipts.

Required ordering: verified account reservation -> fresh project-head recheck -> exactly one project pre-send CAS winner -> durable original effect start marker -> at-most-one BrowserBridge call. A losing/stale/unknown project CAS **never** invokes the browser and keeps capacity occupied until a verified no-effect project terminal receipt resolves the slot. A successful project claim is not proof of a successful browser effect. Once a send is uncertain, even tab closure, transport timeout, crash, reconnect, changed clock, worker yield/complete or review verdict cannot re-arm it.

Terminal release must match the original account reservation, project claim, effect UUID, candidate generation/review version, project terminal/no-send receipt and *trusted provider terminal* (or explicitly attributable operator reconciliation); release is a separate idempotent **account CAS**. No default-off diagnostic may fabricate `complete:true` membership/capacity or a provider terminal signal.

Suggested eventual isolated source paths: `Client/Modules/NativeProjectEffectFence.fs`, `Client.Tests/NativeProjectEffectFenceTests.fs`, plus minimal build-order wiring after dependency reconciliation. These were **not** created or edited in Stage A. Avoid shared project-file wiring until the baseline and owners are reconciled.

## Disposable fixture and negative-space matrix

Owned test-only files:
- `scripts/tests/fixtures/native-project-effect-fake-browser.mjs` — pure fake browser effect port, deliberately **non-idempotent** and non-authoritative. Modes: independently labelled fake prestart/no-send, normal acknowledged send, post-send ACK loss, post-send timeout, post-send client crash; retains provider-active observation after tab closure, reconnect and clock rollback. No network, browser, Git or provider call.
- `scripts/tests/native-project-effect-stage-a.test.mjs` — Node `node:test` self-tests and 16 named negative-case identities.

The fixture **intentionally produces two provider effects** if the same caller invokes it twice after lost ACK. This makes the missing native project send fence observable when the future SUT is injected. A passing fixture self-test proves only the **test double's** fault injection; it does not prove a real project/account CAS or a production launch.

Stage B must independently run the 16-case negative matrix against real disposable **two-clone bare-Git project/account remotes**, including: two Workers same attempt/different IDs; two Reviewers same frozen review; competing third client; account win/project CAS lost; project claim ACK lost; stale worker generation/review ref; before-send conflict; browser submitted/ACK lost; crash/reconnect/clock rollback; foreign/replayed effect UUID; Unknown provider session; verified terminal once-only release and duplicate/foreign/stale terminal denial. It must also test missing/forged account roster, source/lease and provider terminal evidence, and prove zero real external sends.

## Execution evidence and explicit limits

- Stage A offline Node fixture: `node --test scripts/tests/native-project-effect-stage-a.test.mjs` from the isolated worktree; observed **exit 0, 11 tests passed, 0 failed**.
- Current Stage A owns only four test-only files and this document (two baseline fake-browser files plus the once-only model and its negative tests). No production modules, account CAS source, roster/Phase-1/Phase-2 scripts, BrowserBridge tests, Health modules or MCP response-alert fixtures were changed.
- This does **not** claim acceptance of the native Scheduler, project effect fence, account capacity release or authenticated provider-session terminal proof. Production Stage B remains **HOLD** pending upstream independent acceptance, verified integration, bounded source-lease nonoverlap and explicit Operator authorization.

- Existing offline .NET 11 Release baseline: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~OriginalBrowserJournalTests|FullyQualifiedName~BrowserBridgeTests' --nologo` in the same isolated worktree; Jarvis job `d8e204f4a05a4ba6956ab265337e79c1` observed **exit 0, 56 passed, 0 failed, 0 skipped, total 56**, with existing nonfatal F# compiler warnings. This validates current baseline BrowserBridge/journal behavior, **not** the unimplemented native Scheduler CAS.


## Stage A continuation: fake once-only project-claim and terminal oracle (2026-11-21)

Additional **test-only, non-authoritative** paths:
- `scripts/tests/fixtures/native-project-effect-fence-stage-a.mjs`: process-local fake project/account CAS, exact candidate/version/ref, reservation receipt and project/account head checks; immutable original dispatch/effect IDs; synchronous `Dispatching` marker before first asynchronous fake-browser call; at-most-one fake Send; fail-closed fake terminal/release gate.
- `scripts/tests/native-project-effect-once-only-stage-a.test.mjs`: negative tests for competing Workers and Reviewers, third-writer CAS conflicts, reservation-with-project-claim-loss, stale assignment/review/source/account head, lost project claim ACK, concurrent and repeated sends, post-send ACK loss/timeout/crash, tab closure/reconnect/clock rollback, foreign/replayed IDs, Unknown/Active provider, forged/stale/foreign terminal proof, prestart no-send and exactly-once simulated release.

**Observed isolated Node gate:** `node --test scripts/tests/native-project-effect-stage-a.test.mjs scripts/tests/native-project-effect-once-only-stage-a.test.mjs`; **exit 0, 33 passed, 0 failed, 0 skipped**. There were **zero real external browser sends**: all effects are in-memory entries in the disposable fake. The 16 negative-case names remain a Stage B acceptance checklist; these Stage A cases test a *model*, not the real two-clone Git-CAS implementation or source-authorized terminal verifier.

**Safety and evidence boundary:** Fake process-local versions are not Git-ledger CAS; `FAKE_PROVIDER` and `FAKE_PROJECT_CAS` observations are not authenticated provider/session or project evidence. This model is intentionally isolated from runtime, has no scheduling or browser launch path, and cannot be used as production authority. In particular, fake release success proves only local test gating, **not** safe release of a real account reservation. Stage B remains HOLD for Worker 4 native account CAS independently Accepted/integrated, authoritative roster/Phase-1/Phase-2 composition and exact source provenance, current project-ledger CAS, provider/session terminal attestation and separate Operator live authorization.
