# Reviewer 8 — independent review

## Decision scope

Accept the snapshot-equivalence/correctness implementation at `544abd4329377686b1f59eb38605d71ba1064198`. This is not performance acceptance. The paired W8 baseline/stage benchmark remains outstanding, as the task contract requires.

## Frozen source inspected

- Review head: `544abd4329377686b1f59eb38605d71ba1064198`
- Recorded base: `175ad976a6226f1cb07901b4b43f3d6b0ecf3de1`
- The source diff is limited to `Client/Modules/LedgerStore.fs`, `Client/Modules/ReviewWorkflow.fs`, `Client/Modules/ScheduledReadyResume.fs`, and the three files in `tests/SchedulerRosterBatch20261127/`.
- The claimed candidate worktree was clean. No source edits were made during this review.

## Correctness findings

No blocking correctness defect found.

1. The scheduler-specific reader uses the supplied immutable `snapshot.Commit` for one NUL-delimited `ls-tree` and one `cat-file --batch`. It validates selected ledger paths, regular-blob mode/type, OID syntax, returned OID/type/length, record delimiters, complete output consumption, and duplicate selected paths. Missing or malformed selected blobs fail closed.
2. The scheduler path bypasses both reads from and writes to the dashboard blob cache. The test that warms the dashboard cache and then removes a roster blob verifies that stale cached content cannot satisfy a fresh scheduler read.
3. The selected path predicate preserves the old `listAt("agents")` filtering, including exclusion of challenge-subscription records. `decodeSchedulerRosterAgent` preserves the old canonical agent-id/path cross-check, `validAgent` check, and shared `decodeEntity<ReviewAgent>` validation (including schema, lifecycle and duplicate-JSON-member checks).
4. The roster is materialized in canonical path order. Agent path keys are Base64URL ASCII, so F# map ordering matches Git's path ordering; the existing reverse-fold behavior is retained.
5. Both `readRosterAt` call sites remain in place: the initial `prepareAt` preflight and the fresh `commitIntent` transaction preflight. The intent CAS, confirmed-publication boundary, no-replay fence, and send path are unchanged.

## Independent Release verification

Executed in a separate disposable worktree at the exact review head:

```
dotnet test tests/SchedulerRosterBatch20261127/SchedulerRosterBatch20261127.fsproj -c Release --logger "trx;LogFileName=reviewer8-scheduler-roster-batch.trx" --results-directory artifacts/reviewer8-scheduler-roster-batch
```

- Job: `61d3579eefb043ff9575991cefd38d82`
- Terminal result: exit code 0; 29 passed, 0 failed, 0 skipped; 35 seconds.
- TRX SHA-256: `a2ee02c21b6d64c69cd11e4e923fd67ed034f29f987bbb6a8ee09d06706785e2`
- Two compiler warnings were emitted (FS0760 in `Client/Program.fs`, and FS0988 for the linked test module); there were no errors.
- The process-count test asserts 41 serial process starts versus 2 batched starts for 10 idle agents, with no remote fetch; the warm run remains at 2. This is a unit-level process-count result, not an end-to-end performance claim.
- The isolated test worktree was clean after execution and was removed. The test project is standalone, so the command above explicitly targets it rather than relying on `Jarvis.slnx` or `scripts/build.cs test`.

## Residual coverage note

The new suite compares the serial oracle and batched reader at distinct immutable heads, re-reads the original head after later writes, and exercises cache freshness, malformed/missing blobs, role/wait projections, and launch-control regressions. It does not inject a deterministic write precisely between the `prepareAt` and `commitIntent` calls. That is a residual integration-test opportunity, not a blocker for this reader-only change: both fresh-snapshot call sites are unchanged, and the reader is explicitly bound to the snapshot supplied by each caller.

## Conclusion

Accept for snapshot-equivalence correctness only. Do not mark the performance objective complete until the paired baseline/stage benchmark and end-to-end measurements are available.