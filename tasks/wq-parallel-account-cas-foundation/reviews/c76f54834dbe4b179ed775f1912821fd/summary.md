# Reviewer 2 — independent frozen review

**Review:** `c76f54834dbe4b179ed775f1912821fd`  
**Task:** `wq-parallel-account-cas-foundation` (Worker 4)  
**Exact source:** `75c2dd887e119315373aff66bbe4acc852fe4656` on frozen review ref `refs/heads/jarvis-review/wq-parallel-account-cas-foundation/c76f54834dbe4b179ed775f1912821fd/r1`  
**Base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`  
**Verdict: ChangesRequested.**

## Independent validation

- Diff: five files, 1,068 added lines; inert account CAS module, nine disposable tests, documentation, two project-file includes.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~AccountCapacityLedgerTests --no-restore`: **passed 9/9, failed 0, skipped 0** on .NET 11, with two unrelated pre-existing compiler warnings.
- Read the full account implementation, all nine tests, fixture-backed `LedgerStore.putRecord`, and applicable accepted architecture §7/C01 and global admission/migration clauses. Source worktree was clean and still pointed at the exact submitted head.
- Core positive properties checked in code: one global token per launch, held/released tombstone, same account Git lease for reserve/resize/release, config CAS, original-result operation replay, immutable receipt origin/trailer check, remote freshness/trusted ancestry, fail-closed unknown push outcome, and no live Scheduler or migration wiring.

## Findings

1. **P1 F01 — Abort-only project fixture is not terminal under concurrent original admission.** Its terminal and admission write different project paths, while the verifier infers `AbortOnlyUnbound` from temporary absence of the admission path. After global release, a late original admission can still succeed; no shared launch-ID absence/version CAS fences it. The clean-abort positive test therefore does not establish C01 safety. See `findings/abort-only-cas-race.md`. **Blocking for this frozen foundation/race-fixture submission.**
2. **P2 F02 — Missing deterministic actual lost-ack/stale-parent race coverage.** Named lost-ACK test only invokes a successful call then replay; task-start gates do not guarantee both writers planned at the same old head. Failure/uncertainty branches need controlled fixtures. See `findings/unknown-outcome-race-tests.md`.

## Disposition

Correct F01 and add the missing controlled interleavings, or explicitly fail closed on abort-only fixture proof until the accepted WQ-02 adapter can provide its immutable per-launch terminal CAS. Submit a new exact-source frozen review after changes. WQ-07 production binding, migration cutover, Scheduler activation, and integration must remain gated on independently accepted WQ-01/WQ-02 contracts; this review does not authorize them. No source files were edited or merged by the reviewer.