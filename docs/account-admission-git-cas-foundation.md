# Account-global Git-CAS admission foundation (WQ-07 prep)

This module is an **inert account-side foundation**, not a Scheduler or a
migration activation. It lives in `Client/Modules/AccountCapacityLedger.fs`
and is not wired to any MCP command, browser intent, live account or existing
project orchestration handler.

## Authority and boundaries

- A separate account Git ref, `refs/heads/jarvis-account-admission`, holds
  `state.json` and immutable `operations/{UUID}.json` receipts. It never
  owns project tasks, reviews, demand credits, or launch terminality.
- Bindings pin account ID, ledger ID, epoch, source repository, and exact
  approved fetch/push URLs. Every read freshly fetches the remote ref,
  advances a trusted local ancestry anchor by compare-and-swap and rejects
  rewrites or identity mismatches.
- Initialization uses an orphan commit and a create-only ref lease; its
  migration phase begins `LegacyActive`. There is intentionally **no
  activation API** in this foundation. WQ-07 must gate any production
  admission on verified account `Activated(epoch)`, the matching project
  `Prepared(epoch)`, compatible protocol, and explicit rollout approval.
- Every `ReserveGlobalAgentSlot`, `SetGlobalAgentCapacity` or
  `ReleaseGlobalAgentSlot` candidate derives from one fresh account state
  and pushes a new commit with `--force-with-lease` on that exact head.
  Losing writers re-fetch, validate, and replan. A push whose outcome cannot
  be verified reports `AccountOutcomeUnknown` instead of asserting failure.
- Each operation ID has an immutable v1 canonical **full semantic request**
  hash and original-result hash. Replay returns the original result without
  rerunning the planner. Reuse of an operation ID for different project,
  launch, role, role-version, expected config-version, capacity, admission,
  terminal reference, kind, or actor is rejected.
  Receipt reads also verify original first-parent Git addition, matching
  blob and commit trailers, not just internally consistent hashes.
- Account tokens are per **launch ID** (not agent UUID). They are retained as
  `Held` until release and then as `Released` tombstones, so neither an
  uncertain/registered/stalled launch nor a released ID can be silently
  admitted a second time. Scheduler/reconciler aren't admitted as roles.
  Occupancy is the count of all held tokens across projects and devices.
- Resizing and reserving share the **same account ref CAS**, with an
  independently versioned capacity config. A shrink below held occupancy
  reports the exact occupied count and does not evict. A stale expected
  config version is rejected.

## Independent project terminal proof contract

`IProjectTerminalReceiptVerifier.VerifyCurrentTerminal` is a trusted,
separately implemented adapter boundary for accepted WQ-02 project-ledger
contracts. The account release API takes a locator and asks that adapter for
a **verified current project Git terminal receipt**, not a caller-supplied
`isTerminal` boolean. The returned evidence must bind:

- the exact project ID, launch ID, original account admission operation ID,
  terminal operation ID, project ledger ID, verified Git head, immutable blob
  OID, and launch generation;
- `LaunchTerminal` for ordinary project-terminal launch outcomes, or
  `AbortOnlyUnbound` for the C01 preadmission project tombstone that
  *competes with the original admission on the same absence/version CAS*.

An adapter must fetch the current trusted project ref, inspect the pinned
canonical operation/launch state and immutable terminal receipt, confirm that
the launch is irrevocably fenced, and reconcile original admission/one-shot
effects before it certifies a terminal result. For an abort-only tombstone,
it must prove that the original project admission could not also commit.
An absent result, ambiguous Git reply, stale receipt, conflicting project
state, wrong owner, or missing proof keeps the global token held. A release
records the exact project evidence in the same account CAS as the token's
`Released` tombstone, freeing **exactly one** matching token.

The disposable tests use a deliberately scoped project-ledger adapter
backed by the existing `LedgerStore` (real bare Git, two clones, immutable
project operation, current-head read and history check). It exercises both
terminal types and contradictory fixture histories; it is **not** WQ-02's
production terminal-state implementation. Final integration must replace
it with the accepted WQ-02 adapter and acceptance fixtures.

## Tests and operational assumptions

`Client.Tests/AccountCapacityLedgerTests.fs` uses per-test disposable bare
Git remotes with two independent clones and separately initialized account
and project orphan refs. It exercises cap=1 cross-project concurrent reserve,
two-client lost-ack replay, full semantic mismatch, resize/reserve races,
verified normal terminal release, C01 abort-only release and contradictory
admission, duplicate release, launch ID reuse fencing, epoch mismatch,
remote rewind rejection, resize replay, descendant receipt tampering, and
clean source checkout invariants.

Remote policy must reject unapproved out-of-band rewrites/deletion of the
account ref. The local trusted-head check detects rewinds *after* the fact,
but the Git client alone cannot enforce hosted branch protection. No
cross-ledger atomic commit or physical provider-process exit is asserted.
