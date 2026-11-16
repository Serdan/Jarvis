# Work-native verified lifecycle Git-CAS seam (Worker 12)

Source baseline: `53105d3eaa1b476fadfe5470e962b4c5796fe8f9`.
Branch: `feat/work-native-lifecycle-cas-worker12-20261116`.
Scope: **test-injected, disposable Frozen Work ledger only**. No production
Work/Scheduler cutover, browser send, account release, migration or MCP mutation
route is enabled. The existing `ClaimNextWork`, `CheckpointWork`, and
`CompleteWork` public methods remain `RejectedMissingBinding`.

## Typed port

`Client.WorkQueueNativeLifecycleCas.transitionDisposable` is gated by an
explicit `enabled` flag and a host-injected
`INativeWorkSessionJournal`. The session port must independently read an
authenticated ephemeral session and, for completion, verify a specific
terminal-evidence SHA-256 from the actual client transport. The caller cannot
substitute a persistent Agent identity. No production implementation of the
port is installed.

The adapter verifies inside the winning WQ02 project Git transaction:

* The original WQ04 project, role-catalog history and Work snapshot share
  one pinned Git head; the chosen Work ID and stage generation are exact.
* The original immutable source operation (ID, actor, request hash, ref/commit),
  issuing WQ02 admission and WQ04 versioned role credit match this Work.
* An independently fetched WQ07 account ledger holds exactly one token for the
  original project/ledger/epoch/authority, launch, role revision and admission.
  An unknown, foreign or released token cannot authorize a transition.
* A WQ03 original is Accepted in the enrolled local host journal, and its
  matching immutable one-shot host claim/PutRecord operation exists **at the
  pinned project head**. Missing/foreign host roots, uncertain originals and
  stale original operation IDs block the CAS.
* WQ02 original dispatch and first-connect operation receipts exist.
  The host-attested session UUID, session key and lease generation equal the
  registered WQ02 launch; stop-requested and >10 minute stale leases fail closed.
* `Claim` reruns WQ04's deterministic role feasibility in the planner and
  checks the **actual** selected WQ02 Work ID, next claim generation, primary
  launch and consumed credit. `ClaimVerifiedWork` stores the selected Work ID,
  stage generation and next claim generation in the immutable semantic
  request, so replay under a changed generation collides on the original UUID.
  A caller-requested alternate selection fails.
* `Checkpoint` preserves ownership/state and writes the evidence hash in
  WQ02's canonical `CheckpointReadOnly` semantic receipt.
* `Complete` writes a new `CompleteVerifiedReadOnly` canonical command
  binding claim generation and SHA-256 to the operation UUID. It requires the
  host's independent terminal verification. Only read-only Planning Work
  can take this port; FrozenReview requires an independently finalized
  review receipt, and Implementation/Integration/RoleMessage are not
  silently completed. The resulting WQ02 terminal witness is immutable.
  Accepted reviews, integrated source effects or domain finalization are
  **never** inferred from Work completion.

WQ02 `LedgerStore.transact` owns the atomic project mutation, immutable
operation receipt and lost-acknowledgement/idempotent replay. Two clones with
a stale expected version cannot both claim. Reusing the same operation UUID
with a different digest or semantic payload is an explicit collision. WQ07
capacity remains held until its separate terminal proof and release adapter;
a Work transition does not release a global token.

## Evidence gap and live-cutover blocker

The current WQ03 original-browser journal and WQ02 registered session records
are **not** a production-grade authenticated session-terminal evidence
journal. This patch intentionally leaves the transport/source-effects bridge
unsupplied. The injected journal is only exercised by disposable fake-provider
tests; its verifier is not caller-supplied evidence of live terminal effects.
Live scheduler/MCP registration would be unsafe without a durable, independently
authenticated ephemeral execution + terminal journal, source-effect reconciliation
for writing Work, and a release path that separately verifies the WQ02 terminal
witness. The frozen fixture does not satisfy those missing live prerequisites.

## Acceptance

Run Release .NET 11 tests:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release \
  --filter FullyQualifiedName~WorkQueueNativeLifecycleCasTests
dotnet test Client.Tests/Client.Tests.fsproj -c Release \
  --filter FullyQualifiedName~WorkQueueToolFacadeTests
```

The native fixtures provision independent disposable project/account bare Git
remotes and two clones, pinned WQ04 Work/source/role issuance, a WQ07 held token,
an accepted fake WQ03 original, and a host-injected session. They cover single
winning claim, stale second writer, exact-UUID replay, generation-bound
checkpoint/terminal and SHA collision, and refusal of foreign role, foreign
account admission, foreign session and absent original host continuity. They
assert no source checkout dirtiness and no implicit WQ07 release.

The legacy Agent inbox, task assignment, production scheduler, real browser
bridge, original fake-provider dispatch code, and live command registration
are untouched.
