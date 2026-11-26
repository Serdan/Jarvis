# Git-call optimization: baseline and end-to-end equivalence contract

Date: 2026-11-26  
Owner: Perf 1  
Scope: first automated Git-call optimization batch

## Frozen source matrix and comparison rules

The task's original baseline was the freshly fetched remote `main` snapshot pinned at assignment time:

- Original baseline SHA: `9dc95184896118c96f34493da28ce40bf5b3d8d6`
- Registered worktree: `perf-git-call-baseline-20261126`
- Branch: `refs/heads/perf/git-call-baseline-20261126`
- Attempt binding: `BaseCommit=9dc95184896118c96f34493da28ce40bf5b3d8d6`, `TargetRef=refs/heads/main`

Remote `main` was subsequently re-fetched and independently verified at a newer control snapshot:

- Intermediate control SHA: `142f2bd4b2728ea61e18a9d51f7c663c7951e182`
- Registered worktree: `git-call-baseline-perf1-20261126-r2`
- Branch: `perf/git-call-baseline-perf1-20261126-r2`
- Verification recorded in the control worktree: fetched `refs/heads/main`, then compared `git rev-parse refs/remotes/origin/main` with `git ls-remote origin refs/heads/main`; both returned `142f2bd...`.

The immutable Worker 1 checkout candidate is:

- Review ref: `refs/heads/jarvis-review/automation-git-call-optimization-checkout-20261126/4303c1c3217c321fd6a9d773d4e8649c/r1`
- Candidate SHA: `342b4bcfca4d5eec9d44b6c815b58dbdd79b478b`
- Parent: `b6f9ecd985ed393f63c6dc778114a3f2845872b9`
- Registered worktree: `git-call-candidate-342b4bcf-perf1-20261126`
- The candidate is a descendant of both `9dc951...` and `142f2bd...`; `merge-base(9dc951, 142f2bd)=9dc951`.
- The ancestry path from `142f2bd` contains exactly `b6f9ecd98` (cache registered worktree path discovery) and `342b4bcfc` (harden cached Git worktree path resolution). Relative to `142f2bd`, the candidate changes only `Client/Modules/ProjectPaths.fs` and `Client.Tests/AgentInstructionsTests.fs`.

Use `142f2bd` versus `342b4bcf` as the meaningful optimization comparison. The `9dc951` versus `142f2bd` run records intervening source drift; `9dc951` versus `342b4bcf` is a historical end-to-end comparison and must not attribute the whole delta to the candidate optimization. Preserve all three exact source identities and fixture seeds.

The harness requires `Client/` and `Server/` to be clean in each measured source tree. Benchmark, test, and documentation files may be untracked outside those paths in the registered baseline worktree. The dirty primary checkout is never used or modified.

## Reconciliation of accepted historical evidence

These accepted snapshots are useful prior measurements, but neither is an ancestor of the fresh baseline and their source instrumentation is absent from this checkout. Do not silently cherry-pick their source or treat their values as measurements of commit `142f2bd`.

### `72618844318944079550eb898e128b11c03039a7` — end-to-end latency / hot-path goldens

The report describes source-only, opt-in stage diagnostics and disposable Release fixtures. Its deterministic controls include:

- Reviewer inbox: at most 12 Git-runner starts, one fetch, one blob batch and one individual blob read.
- `GetTaskWithSpec`: at most 16 starts and one fetch.
- `ReviewDetails`: four starts and zero fetches for the pinned entity read.
- Challenge list and notification inbox: at most 20 starts and one fetch in the broader schema-v2 fixture.
- Default-off publication preflight: one start and one fetch.
- Unknown `JobResult` polling: zero Git starts, fetches and stdout.
- Bulk `ListWorktrees`: one real `git worktree list --porcelain` command; cleanliness remains unknown (`IsClean=None`) until an explicit current status check.
- A controlled local-bare-remote fetch test checks that a blocked remote child does not block a pinned read, cancellation does not retry or leave a stage ref, and subsequent recovery is explicit.

The report explicitly treats elapsed p50/p95/p99 and memory as informational, not cross-host gates. Its 20-start Challenge/AgentInbox budget belongs to the broader full-path fixture; it does **not** erase the stricter historical 12-start inbox control below.

### `92927b7d9ef59d744cc0bfa4f71fc54278e464f0` — Challenge Git-fanout diagnosis

This diagnostic pinned source `d948e2cdc8aceba5236f621f62d99f3422dfe4fc` against parent `dcac884fc061869572b12a03befc12f11c009f2f`. The full Client run was RED (1385 passed, 3 failed, 1 skipped): the three cold `AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness` cases observed 13 Git-runner starts against an unchanged 12-start ceiling. The exact-parent isolated Release control passed all three cases and measured 11 cold, 12 first-warm, at most 12 across 30 warm samples, and 13 after a published head advance.

The two extra calls were an intentional historical-completeness witness: an `ls-tree` inventory plus a first-parent `git log` under the owner wait-index prefix. Do not remove that integrity witness, skip it for an empty current prefix, weaken deleted-index mutants, or raise the old ceiling to make the failure disappear. Any amortization must preserve issued-then-deleted detection, immutable block-operation validation, owner/challenge reverse edges and error semantics.

**Metric distinction:** the historical `LedgerStore.measureGitReads` counter increments at each in-process Git-runner invocation before `BoundedProcess.run`; it is a scoped invocation-attempt count, not an OS concurrency gauge. The focused ledger-level tests retain the in-process runner-attempt counter. The MCP harness independently counts actual Git process starts using Git Trace2, plus actual `fetch` invocations. Never conflate the two.

## Measurement contract

Measure the whole MCP tool method -> server authorization/routing -> client command dispatch -> local operation -> correlated response path where the harness can exercise it. Also retain focused module-level measurements to attribute the source of any change. The harness must not claim HTTP-envelope or live network RTT when it only measures the in-process MCP tool method and fake SignalR transport.

For every scenario, capture:

1. **Source and fixture identity:** baseline/candidate SHA, parent SHA, dirty status, fixture seed commit, remote URL kind (local bare or delayed local bare), and scenario parameters.
2. **Phase and end-to-end time:** server tool method, server route/send/reply wait where observable, client queue wait, client execution, and total elapsed. Use monotonic clocks within each process; never subtract client timestamps from server timestamps.
3. **Git activity:** runner invocation attempts, actual Git process starts, `fetch` count, and command-class counts (`worktree list`, `status`, `ls-tree`, `log`, `cat-file`, `show`, `batch`). Distinguish direct Git processes from Git's non-Git transport children.
4. **Cold/warm behavior:** one cold call, 30 warm calls, and a separate call after a second fixture clone publishes a new head. Report p50/p95/p99 for warm samples; do not use absolute elapsed-time thresholds as CI gates.
5. **Semantic output:** canonical digest of result class, payload, ordered instructions, typed error and details, state-change evidence, inbox activation, pending-job shape, and canonical refs. Normalize only known volatile timestamps and fixture-root prefixes; preserve stable IDs, error discriminants, ordering, and all canonical ref names.
6. **Durable effects:** exact ref names and normalized tree/content digest before and after writes. Commit OIDs may differ when timestamps are intentionally variable; compare canonical refs and tree contents, not just commit IDs. Use a fresh disposable bare remote and clone for each candidate.
7. **Negative controls:** unauthorized scope (no client dispatch or Git effect), wrong actor/owner, unknown selector, stale CAS/version, operation-ID reuse with changed input, missing/deleted reverse-index witness, delayed fetch cancellation, and unknown job polling. Include only controls applicable to each scenario, but never omit a control merely because it is slow.

### Representative scenarios

| Scenario | Public tool / client operation | Primary attribution |
|---|---|---|
| Project/worktree discovery | `ListWorktrees`, `GetWorktree` | selector resolution, worktree enumeration, status calls |
| Task/review reads | `TaskOverview`, `GetTask`, `GetTaskSpecPage`, `ReviewDetails` | snapshot acquisition, tree inventory, blob batching |
| Ledger write | `RegisterAgent` or a deterministic task write on the disposable ledger | CAS, fetch/push, durable ref and tree effects |
| Recovery | same operation ID retry, `PendingOperations` / operation-status read, unknown `JobResult` poll | idempotency, journal inspection, no duplicate fetch/effect |
| Delayed remote | controlled local bare remote with an upload-pack handshake | blocked fetch, pinned reads, cancellation and explicit recovery |

Population controls should include empty, representative, and larger records (0/220/2200 where the selected fixture supports them). A parser-only 0/220/2200 worktree population is not a claim to have created that many physical worktrees.

## Repeatable Release commands

Run only against the three clean measured source trees listed above. The harness and documentation may be untracked outside `Client/` and `Server/`; those measured source directories must remain clean. The shell wrapper builds/runs the same harness project against each exact source tree and writes the raw report plus Trace2 paths to the chosen output.

```sh
BASELINE_WORKTREE=/path/to/perf-git-call-baseline-20261126
CONTROL_WORKTREE=/path/to/git-call-baseline-perf1-20261126-r2
CANDIDATE_WORKTREE=/path/to/git-call-candidate-342b4bcf-perf1-20261126

dotnet build scripts/perf/git-call-optimization-20261126/GitCallOptimization.Harness.fsproj -c Release -m:1

bash scripts/perf/git-call-optimization-20261126/measure.sh "$BASELINE_WORKTREE" /tmp/jarvis-git-call-9dc951.json
bash scripts/perf/git-call-optimization-20261126/measure.sh "$CONTROL_WORKTREE" /tmp/jarvis-git-call-142f2bd.json
bash scripts/perf/git-call-optimization-20261126/measure.sh "$CANDIDATE_WORKTREE" /tmp/jarvis-git-call-342b4bcf.json

python3 scripts/perf/git-call-optimization-20261126/compare.py /tmp/jarvis-git-call-142f2bd.json /tmp/jarvis-git-call-342b4bcf.json
python3 scripts/perf/git-call-optimization-20261126/compare.py /tmp/jarvis-git-call-9dc951.json /tmp/jarvis-git-call-142f2bd.json
python3 scripts/perf/git-call-optimization-20261126/compare.py /tmp/jarvis-git-call-9dc951.json /tmp/jarvis-git-call-342b4bcf.json

# Ledger-level cold/warm, 0/220/2200, mutation, worktree and delayed-transport coverage.
python3 scripts/tests/git-call-baseline-20261126/compare.py --candidate-sha 342b4bcfca4d5eec9d44b6c815b58dbdd79b478b --include-focused-suite
```

The first comparison isolates the two-commit worktree-path candidate relative to its actual `142f2bd` control. The next two comparisons quantify historical source drift; do not attribute the entire `9dc951` to `342b4bcf` delta to the optimization. The focused ledger-level script runs the same fixtures on both exact commits, including the controlled local-bare-remote upload-pack delay, pinned-read/cancellation/recovery test, and explicit malformed-state, reverse-index, stale-CAS, mutation-stage and job-recovery controls. Every selected filter must produce a TRX with at least one executed test; zero-match filters fail closed. Do not run the full combined suite by default.

The MCP harness emits machine-readable reports and fails closed on missing tool results, missing correlated responses, captured exceptions, semantic digest changes, or durable-state mismatches. Its comparator requires the same deterministic fixture seed/published head and compares the MCP tool-result digest for every scenario, not only the unauthorized control. It reports queue/execution phase timings and warm p50/p95/p99; latency remains informational. Preserve the raw report, Trace2 event streams under each printed fixture root, TRX, exact command lines, and terminal exit status for each source SHA. Run paired measurements on the same host and avoid concurrent benchmark jobs when comparing elapsed time.

### Implementation checkpoint (2026-11-26)

- The isolated Release harness project built successfully against frozen baseline `9dc95184896118c96f34493da28ce40bf5b3d8d6`.
- The focused fixture suite now has a controlled upload-pack handshake that blocks a real fetch, proves a pinned read completes without another fetch, cancels a second fetch without publishing its stage ref or retrying it, then records one explicit recovery fetch.
- The focused runner verifies every selected TRX has at least one executed test, and includes inbox integrity, challenge reverse-index, stale-CAS, mutation-stage, worktree and job-recovery filters.
- Still required before final measurements: execute the injected F# harness tests; add Trace2 `child_start` attribution for Git versus non-Git transport children; record exact ref targets so idempotent retries can prove no ref advancement; add MCP `AgentInbox` and `TaskOverview` scenarios; then run baseline/control/candidate reports and independent review.
 
## Controlled remote and safety

The delayed transport fixture must be a local disposable bare remote with a file handshake around `git-upload-pack`; wait for proof that the remote child is blocked before starting the pinned read. Cancel a second real fetch and assert no retry, no staging-ref residue and no silent state advance; then perform and record a distinct recovery fetch. The delay and handshake are correctness controls, not absolute timing gates. This is POSIX-only unless a separate Windows run is explicitly arranged.

No live remote, production data, deployment, activation, scheduler changes, main checkout edits, or full combined test run is part of this task. Do not change permission, freshness, fencing, CAS, cancellation, idempotency, or uncertainty semantics to improve a timing number.
