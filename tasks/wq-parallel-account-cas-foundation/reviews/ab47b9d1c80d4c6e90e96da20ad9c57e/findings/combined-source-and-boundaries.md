# Independent combined-source findings — account Git-CAS on WQ-02

**Frozen request:** `ab47b9d1c80d4c6e90e96da20ad9c57e`  
**Exact source:** `3a17eac3e9489e14e50d1d34a1b9917f74b1dcdc`  
**Classification:** No blocking combined-source findings. The limitations below are mandatory future integration gates, not approval for production activation.

## F01 — Source lineage and merge scope: PASS
- The frozen merge commit has first parent `e0ae297f42217a19cde29fb7729bf509a2448e53` (integrated WQ-02) and second parent `e72650db82f883ad15108b9d83d7cdfa28efaff5` (account-CAS accepted as review `d4e8d63a6b684d7e9bea5b32654937c2`, verdict sequence 3046).
- `git diff e72650db..3a17eac3 -- Client/Modules/AccountCapacityLedger.fs Client.Tests/AccountCapacityLedgerTests.fs docs/account-admission-git-cas-foundation.md` is empty. The accepted account foundation, 14 tests, and documentation are byte-for-byte unchanged.
- `git diff e0ae297f..3a17eac3` changes exactly five files: the account module, its tests, design note, and one compile include in each of `Client/Client.fsproj` and `Client.Tests/Client.Tests.fsproj`. No WQ-02 implementation file was modified.
- Both .fsproj resolutions preserve the preceding `WorkQueueGitLedger` compilation include, placing account module/tests in the intended order. Frozen worktree status clean. No main push or deployment.

## F02 — Account authority and concurrency: PASS within foundation scope
- `AccountCapacityLedger.fs:121-342`: distinct account Git ref, explicit account/ledger/epoch and approved remote endpoints, freshly fetched trusted-head ancestry; operations do not mutate the ordinary source checkout.
- `AccountCapacityLedger.fs:369-519`: canonical full semantic request hashes, stable operation IDs, immutable result receipts with first-parent origin/blob/trailer verification; per-write exact-head `--force-with-lease` prevents stale snapshots from overcommitting. Post-push ambiguous verification returns `AccountOutcomeUnknown`, not false success.
- `AccountCapacityLedger.fs:525-611`: held tokens count across projects, one token per unique launch ID, preserved released tombstones, shared CAS for resize/reserve, stale config and below-occupancy resize guards. Release requires a trusted adapter-supplied current terminal proof matching project/launch/admission/terminal identities; insufficient proof keeps the token held.
- `AccountCapacityLedgerTests.fs:93-220,473-560`: disposable project adapter uses one immutable per-launch decision path for admission-vs-abort, with both winner orders and late original retry; verifies terminal origin instead of absence-only inference. This is a **fixture**, not production WQ-02 terminal binding.

## F03 — Local Release evidence: PASS
- Reviewer-run `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~AccountCapacityLedgerTests --verbosity minimal`: job `b2713ffed1844ba9bc797f6222c1ffee`, exit 0, **14 passed / 0 failed / 0 skipped**, `net11.0`; restored, rebuilt Client and Client.Tests Release.
- Reviewer-run `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueGitLedgerTests --verbosity minimal`: job `d76a3d341c3c42fc9d3de14671160399`, exit 0, **21 passed / 0 failed / 0 skipped**, `net11.0`.
- The two reported compiler warnings point to `Client/Program.fs:188` (FS0760) and `Client.Tests/BrowserSchedulesTests.fs:97` (FS3369), outside this five-file merge delta. Worker 4 also reported full Server.Tests 212/212 and solution Release build, but those full-suite claims were not independently rerun here.
- Source grep of the frozen tree finds account module references outside its own code, tests and documentation only in the two .fsproj compile includes; no Scheduler, production account admission, browser, migration or deployment hook is activated.

## Future WQ-07 integration gates — advisory, not accepted implementation
1. Implement and independently review the real WQ-02 canonical project-terminal verifier: fetch current trusted project head; check exact immutable terminal and admission operations, generation, side effects and final abort/admission fence. The disposable `LedgerStore.putRecord` model is not that production adapter.
2. Distinguish account reserve operation UUID from potentially different project admission operation UUID and test both replay/abort histories. The fixture intentionally reuses an ID.
3. Require explicit operator migration and capability gates (account Activated epoch, compatible project Prepared epoch, accepted WQ-01/WQ-02 contracts) before invoking this inert reserve/release primitive.
4. Enforce remote account-branch writer protection/transition provenance. Trusted-head ancestry and token shape checks alone do not reconstruct every descendant state transition from operation receipts. Do not interpret this review as authorizing hostile/out-of-band Git writes.
5. No cross-ledger atomicity, physical agent-process termination evidence, production Scheduler activation or deployment is established here.

**Conclusion:** No blocking source/architecture or .NET 11 Release regression in this **combined-source inert foundation**. Accepted only at the specified frozen commit and scope; later production binding remains independently gated.
