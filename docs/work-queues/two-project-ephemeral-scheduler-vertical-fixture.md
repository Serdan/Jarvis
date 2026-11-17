# WQ11 disposable two-project ephemeral Scheduler acceptance

**Scope:** `Client.Tests/WorkQueueTwoProjectEphemeralSchedulerVerticalTests.fs` is
an explicitly invoked, in-process, test-only .NET 11 Release fixture. It is not a
live Scheduler registration, source execution route, migration, or cutover approval.

## What is original and independently verified

- One original WQ07 capacity ledger (cap = 1), two original WQ02 project ledgers,
  each backed by separate disposable bare Git remotes and two independent clones.
  The observer reads original canonical heads and exact approved WQ04 role catalogs.
- WQ04 planning Work is produced from actual CreateTask operation receipts; WQ12
  prepares immutable original launch identity from verified cross-project ranking.
  The fake opt-in credential adapter uses the original WQ07 reservation and WQ02
  **four-path** admission CAS with the credential *verifier only* in Git.
- The WQ03 one-shot fake-provider journal, canonical original-send host claim,
  credential first connect, sealed disposable WQ02 claim/checkpoint/terminal and
  original WQ07 terminal release are observed from the second clones.
- A stale second-client claim, a second credential for the admitted original, an
  invalid terminal witness and unknown-send retries fail closed. Verified terminal
  release (not merely Work Completed) frees the global slot for the other project.
  STOP revokes subsequent credential actions without releasing unresolved credit.
  No `agents/*` file is introduced, no model Manager needs to poll, and no
  long-lived service is started.

## Previously disconnected original interfaces

The WQ08 planner originally classified two *unissued* candidate browser/launch
UUID fields as one duplicated issued intent, preventing WQ12 from preparing
either original in an otherwise valid two-project account. The new narrow check
only compares **valid issued** UUIDs for duplicates; the missing IDs still
trigger `CandidateIdentityInvalid` and cannot authorize fake or real dispatch.
Alias-collision negatives remain intact. The sealed WQ11 disposable lifecycle
also now recognizes the distinct `initialize-work|<launch>` WQ02 native
first-connect receipt, but only with the original matching stored WQ04/WQ07
four-path credential verifier, original host claim and live lease evidence.

## Explicit test-only projection, not a production proof

The production observer/coordinator sees the **real** account migration phase
`LegacyActive` and returns `AccountNotActivated`; default-off hosts perform no
work. For the positive disposable *manual* tick alone, the test projects the
verified read-only preview's migration phase to `Activated` **in memory**. The
original WQ07 ledger remains `LegacyActive` at every point. No migration writer
or permission to cut over is inferred. `prepareProduction` returns the typed
`HostBridgeUnavailable` decision, and `prepareDisposable false` is disabled.
A real private provider bootstrap and credential-authenticated first-connect
relay are not implemented by this fake provider.

The hard dependency (Planning -> FrozenReview -> Integration) exercises real
WQ02 versioned stage/blocked-state behavior, but the review and Integration
stages are explicitly inert test fixtures. They are **not** WQ05 accepted-review
or successful Integration receipts. The Integration authority checker must
report `IntegrationAuthorityUnavailable`; wake hints alone never manufacture
acceptance. A genuinely accepted WQ05 review/Integration-to-Scheduler causal
adapter, independent source authority, a canonical WQ07 `Activated` transition,
and a real private host remain required before full production acceptance.
There is deliberately no invented cross-repository atomic commit.

## Run locally

```bash
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueTwoProjectEphemeralSchedulerVerticalTests
```

Run adjacent WQ07/WQ08/WQ11 fixture tests and the full Server Release suite before
a Lead-owned integration. Only an exact independently reviewed, frozen commit
may be integrated nonforce. Never run hosted Actions, a live Scheduler, or Codex
for this fixture.
