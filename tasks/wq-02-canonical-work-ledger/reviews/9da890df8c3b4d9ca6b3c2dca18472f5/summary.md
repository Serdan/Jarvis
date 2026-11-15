# Independent frozen WQ-02 review — ChangesRequested

**Review:** `9da890df8c3b4d9ca6b3c2dca18472f5`  
**Reviewer:** Reviewer 2  
**Exact frozen implementation:** `71fc78d051d6509596389abfa8fe7cb5f444cd1f`  
**Accepted integrated WQ-01 base:** `7b589173135178a634495146675dad5db647e5f8`  
**Source branch:** `feat/wq02-canonical-ledger-worker5`  
**Scope:** WQ-02 bounded, nonactivated canonical project Git CAS work/dependency/claim ledger. No deployment, migration, production scheduler cutover or account authority granted.

## Independent verification

Inspected the exact frozen diff, entire new `Client/Modules/WorkQueueGitLedger.fs` (433 lines), entire new `Client.Tests/WorkQueueGitLedgerTests.fs` (542 lines), the WQ-02 task specification and applicable inherited `Server/WorkQueueContracts.fs` transitions, plus the staged project metadata and clean registered source worktree. `Server/WorkQueueContracts.fs` has 48 added lines for `yieldReadOnly`; two .fsproj changes wire the adapter/tests.

Ran independently in the exact frozen worktree on **.NET 11 Release**:
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueGitLedgerTests --verbosity minimal`: **12 passed / 0 failed / 0 skipped**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueue --verbosity minimal`: **77 passed / 0 failed / 0 skipped**.

The 12 focused tests exercise two-clone CAS conflict, exact replay/operation-ID changes, publication FIFO override, blocked prerequisite ordering and wake, register/claim/terminal, dedicated Role and ancillary handler generation, gated ancillary release, malicious historical passive Task WorkId, concurrent bare Git remote publication, read-only checkpoint/yield and primary hard gate. These are useful and passed, but do not exercise the defects below. A further optional disposable shell-based reproducer could not be executed because Jarvis explicitly denied `bash` (`PermissionDenied`); the counterexamples below are **source-level transition traces**, not claimed executed reproductions. No implementation files were modified.

## Blocking findings

1. **P1 — Forged published waiver bypasses hard dependency.** `findings/p1-published-waiver-bypass.md`. New stage or Role-message publication permits arbitrary `WaivedDependencies` entries and treats them as satisfied. A dependent D can become Available/claimable while B remains unfinished. **Fix:** reject pre-populated waivers, require verified separate authority-backed mutation, and test forged inputs against the real Git ledger.
2. **P1 — Forged already-Registered launch bypasses first connect.** `findings/p1-forged-registered-admission.md`. `AdmitProject` accepts an arbitrary `Launch` record including fake `Registered`/session/lease/account token, and `ClaimNext` can then consume work without Reserved->Submitted->first-connect provenance. **Fix:** enforce exact initial launch shape and authority boundaries; add negative two-clone tests.
3. **P1 — Implementation completes without verified source.** `findings/p1-source-writing-complete.md`. `CompleteOrdinary` accepts undecided Implementation under a clear gate, marking it Completed, terminalizing launch and waking dependents without reachable source ref/SHA or mutating-job reconciliation. **Fix:** reject source-writing complete until a separate, canonical proof-validated completion mutation exists; add negative + downstream wake tests.
4. **P2 — Registered no-claim terminal lacks evidence.** `findings/p2-no-claim-terminal-evidence.md`. `TerminalizeNoClaim` allows Registered/no-claim -> terminal token-release receipt with no authenticated session-terminal, stop/timeout or journal evidence. **Fix before account-global release integration:** distinguish voluntary authenticated terminal from recovery, bind proof to session/lease, regression-test races.

## Architecture and safety observations

Positive: a single `LedgerStore.transact` Git-tree CAS encloses project state and complete canonical mutation payload, with immutable external operation receipts and exact replay behavior; a losing clone does not issue a second primary claim. Claimed Role-message handler generation and ancillary dependency gate transitions retain the primary, and completed prerequisites trigger a fresh sequence for blocked-stage wake. Source-writing `YieldReadOnly`/`CheckpointReadOnly` are explicitly rejected, and no production handlers or scheduler are redirected.

Important limits, *not presented as defects caused by this isolated patch*: it initializes a Frozen migration epoch and does not implement authenticated account-global token reservation/release, authoritative producer/source/queue high-water verification, trusted server-clock FIFO witness (uses client UTC in `execute`), remote Git ref/SHA + mutating job provenance, or F06 integration/waiver effect authorization. These are explicit **pre-activation gates** in the task specification. They must not be silently treated as already delivered by passing fixture tests. There is no evidence or authorization for deployment/migration.

## Verdict and required retest

**ChangesRequested** on exact frozen `71fc78d051d6509596389abfa8fe7cb5f444cd1f`. The three P1s permit invalid transitions inside the claimed canonical foundation, independent of production activation. Apply narrow fail-closed guards first; do not broaden this correction into an account/global deployment project. Add deterministic negative tests for all four findings, rerun `.NET 11 Release` cross-clone and inherited contract checks, and submit a **new exact frozen independent review** of the corrected source. Keep WQ-02 unintegrated/unactivated until Accepted.
