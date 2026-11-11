# Independent frozen-commit review — cross-platform trust-binding contention

**Verdict: Accept.**

**Source boundary:** `9e2a8e7d22b786202530135682cf15bf95e93565` on `fix/trust-binding-contention`, based on main `ecb59e2272c7e6c4e127345f5034695c2f6e5465`. Registered review worktree was clean. A dry `git merge-tree --write-tree` produced `654a4a36477cd25c1379f7115b76fdf671cfd59a` without conflict; this is **not** integration authorization.

## Source review

- Examined `LedgerBindingConfig.withConfigLock`, `loadAtRootWithSnapshotUsing` and `rebindRemoteUsing` in `OrchestrationApi.fs`; remote Git fetch and ancestry verification no longer occur inside the exclusive local trust-binding lock. A second locked read uses identity/configuration and trusted-head CAS, bounded re-fetch on concurrent head advances, and fail-closed rejection of incompatible changes.
- Lock-opening sharing violations are mapped to typed `TrustBindingContended` instead of generic `ValidationError`; permission, malformed binding, missing path and unrelated I/O errors remain distinct. No lock stealing, identity bypass or trust reset introduced.
- Audited `AgentDashboard` / `AgentView` / `ConsoleTui` handling: transiently busy projects show unknown rather than zero without a verified snapshot; a prior verified snapshot is visibly carried as **STALE** and deduplicated by canonical identity across aliases, and subsequent ordinary 45-second Agents refresh replaces stale data with freshly verified data. Failed nontransient binding/identity checks do not re-use prior rows.
- Examined deterministic tests for held locks, simulated Win32/Linux/macOS sharing-error HRESULTs, malformed identity, Unix permission denial, overlapping slow remote fetches, monotonic head progression, rebind CAS, and live dashboard stale/recovery.
- **Scope limit:** Cross-platform error codes are injected; native Windows/macOS runtime tests were not executed by Lead. On first-load contention, the diagnostic `Agent issues` footer can still show detailed typed failure text, but the main project status is concise and distinguishable from corruption.

## Independent verification

- Server Release: **127/127 pass** (job `34f3b9bccdae4f59b4e8090e4d588890`).
- Client Release initial full run: **557/558 pass**, one unnamed failure in quiet output (job `a4fc8a9ebd404970902bc703433b0260`). Treated as unresolved until replay rather than ignored.
- Client Release independent diagnostic repeat: **558/558 pass**, **0 failed, error or timeout**, corroborated by `Client.Tests/TestResults/TrustReviewFull.trx` (job `8b0bcd9997f6405e81de0320dceddf0c`). The first failure cannot be attributed to a particular test because its quiet-mode output did not preserve the name. This remains test-suite stability evidence, not a demonstrated change-specific regression.
- Author reported full 558-case Release pass on frozen branch before review.

**Decision:** Accept implementation correctness on frozen boundary. Integration/deployment remains an independent operator decision under the task's explicit approval gate; **no merge, push or deployment** performed by reviewer.