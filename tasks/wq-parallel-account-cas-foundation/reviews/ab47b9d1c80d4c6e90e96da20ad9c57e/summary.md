# Reviewer 1 — frozen independent combined-source review

**Verdict: Accepted**, strictly for the **inert account-global capacity Git-CAS foundation** combined onto the integrated WQ-02 baseline. Production WQ-07 activation, Scheduler binding, migration and deployment are **not** approved.

- **Request:** `ab47b9d1c80d4c6e90e96da20ad9c57e`
- **Exact frozen source:** `3a17eac3e9489e14e50d1d34a1b9917f74b1dcdc`
- **Parent 1:** `e0ae297f42217a19cde29fb7729bf509a2448e53` (integrated WQ-02)
- **Parent 2:** `e72650db82f883ad15108b9d83d7cdfa28efaff5` (account-CAS Accepted by independent review `d4e8d63a6b684d7e9bea5b32654937c2`, decision seq 3046)
- **Detailed findings/advisories:** `findings/combined-source-and-boundaries.md`

## Independent source/architecture findings
The combined merge preserves the previously accepted account source and 14 tests byte-for-byte: `git diff e72650db..3a17eac3` for module, tests and design note returns no differences. Diff against WQ-02 baseline consists of **exactly five files** (the 611-line account module, 626-line account fixture tests, 110-line design note and two F# compile includes); neither WQ-02 implementation nor WQ-02 tests changed. The two compile-order merge conflicts are resolved with both modules present and correctly ordered. Git status of the frozen worktree is clean.

Reviewed `Client/Modules/AccountCapacityLedger.fs`, all 14 `Client.Tests/AccountCapacityLedgerTests.fs` tests, and the design note. Account ledger authority is separate from project ledger; exact-head Git CAS unifies global reserve/release/resize, held tokens count across projects, released tombstones block reuse, full-semantic UUID receipts replay their immutable original result, and unknown transport outcomes do not authorize capacity release. The disposable project-proof verifier uses a **single** admission-vs-abort immutable per-launch CAS path and checks terminal origin; it is not the real WQ-02 production adapter. No production Scheduler/activation reference exists outside the two project-file compile entries.

## Independent .NET 11 Release validation
1. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~AccountCapacityLedgerTests --verbosity minimal` — **PASS**, 14/14, exit 0; net11.0 Release Client and Client.Tests built locally. Job `b2713ffed1844ba9bc797f6222c1ffee`.
2. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueGitLedgerTests --verbosity minimal` — **PASS**, 21/21, exit 0; net11.0 Release. Job `d76a3d341c3c42fc9d3de14671160399`.
3. FS0760 at `Client/Program.fs:188` and FS3369 at `Client.Tests/BrowserSchedulesTests.fs:97` are outside the merge's modified paths. Worker 4 additionally reported full Server.Tests 212/212 and solution Release success; those broader runs are owner evidence, **not** independently rerun in this review.

## Scope gates
The real production WQ-02 project-terminal adapter, separate account/global-vs-project operation identities and failure recovery, project Prepared/account Activated epoch and operator rollout gates, physical-process terminal reconciliation, and account-ref writer policy **remain outstanding**. A trusted Git branch must reject out-of-band descendant state changes; source ancestry alone is not a complete transition audit.

**Decision rationale:** both accepted ancestors are preserved unchanged, merge scope is limited and compile-safe, 14 account-CAS and 21 WQ-02 Release regressions pass independently, and the account foundation remains inert. No blocking combined-source defects found. See detailed advisory document for nonblocking production prerequisites. No GitHub Actions, Codex, integration to main, or deployment performed.
