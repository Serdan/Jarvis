# Independent follow-up review — ChangesRequested

**Reviewed frozen head:** `3a35c382d274c46f4ba95951b2e38d252b3dadb2`  
**Parent:** `92c0b1b4be19b2e177594dc2a3985f4854d69116`  
**Base:** `95a471fc565b57fe309c65fc85f371693dbe9067`  
**Source:** `refs/heads/fix/exact-lock-reclamation-20261120`  
**Target:** `refs/heads/main`  
**Review request:** `b534e8918ef84cfcad2a5e85f2095630`

## Verdict

**ChangesRequested.** The candidate materially improves the deployed file-lock protocol and resolves several previous findings, but it does not yet meet the task's bounded-reclamation or mixed-generation migration acceptance. Do not treat this as approval to merge the P1 task as complete, deploy a new lock family, or activate the named-mutex branch.

## Finding disposition

### F1 — Resolved by explicit threat-model narrowing; not a general unlink proof

The implementation now states that supported clients must acquire the shared/exclusive prefix stripe before opening an exact-UUID lock file (`OrchestrationApi.fs:654-662`). Cleanup takes that stripe exclusively after the operation's shared stripe and exact-file handles have been disposed. That excludes the legacy exclusive-stripe generation and cooperating current file-lock clients, including clients waiting on the exact file.

The direct exact-file probe still closes before `File.Delete` (`OrchestrationApi.fs:702-710`). An arbitrary process that does not participate in the stripe can still open the inode in that interval on Unix. The candidate correctly labels the probe as courtesy-only and the release document explicitly excludes arbitrary non-cooperating openers (`ClientDownloads.md:71-73`). This satisfies the prior review's requested scope narrowing, provided that this remains the actual supported-client boundary. It does **not** establish safety for arbitrary processes opening or mutating the lock directory.

### F2 — High: reclamation still has no guaranteed liveness or bounded-growth property

The fixed 32-file truncation is gone. A quiescent pass now enumerates matching names and processes every captured candidate in batches of 64 (`OrchestrationApi.fs:678-713)); the new 130-file test demonstrates a serial quiet pass can drain a finite backlog (`ReviewWorkflowTests.fs:5013-5048)).

However, each batch makes only one non-blocking attempt to acquire the exclusive stripe. Any exception is swallowed and that batch is abandoned; there is no retry, durable continuation, or background/periodic sweep (`OrchestrationApi.fs:692-713`). Two gaps remain:

1. **Finite-burst race:** enumeration is deliberately performed before taking the stripe. Cleanup A can capture a stale candidate array and be descheduled. Operation B can then create and release its exact lock. A can acquire the exclusive stripe first and process only its older snapshot; B's cleanup can attempt the stripe while A holds it, fail once, and return. B's exact file is then left behind with no guaranteed later pass. The serial 130-file test does not exercise overlapping cleanup snapshots or competing sweepers.
2. **Sustained contention:** if same-prefix operations continuously hold shared stripe handles, no cleanup can acquire the exclusive gate. Every cleanup pass can abandon its batches, while each new UUID can add another exact file. There is no fairness or admission mechanism that bounds this backlog. Once traffic stops, another successful sweep is needed; the code does not schedule one. The candidate's own task note acknowledges that a strict bound under perpetual same-stripe contention is unproven.

The 64-file batch bounds the number of paths handled per exclusive-gate acquisition; it does not bound the number of retained files or guarantee a future successful sweep. This remains a blocker for the task's explicit requirement to prevent unbounded artifact accumulation. Add a deterministic competing-sweeper/late-candidate regression and define a liveness policy. For sustained contention, either provide a safe fair/periodic reclamation mechanism with a stated bound, or explicitly revise the acceptance contract so it no longer promises bounded growth under that condition.

### F3 — High: incompatible file/mutex generations remain unenforced

The current candidate remains on the deployed file protocol. The separate named-mutex experiment takes the same shared stripe and serializes the UUID with a named mutex (`1be1c1cdd40a26fb468c24ff55b7be4b7913ff86`, `OrchestrationApi.fs:700-718`); this candidate takes the same shared stripe and serializes with an exact file (`3a35c382d`, `OrchestrationApi.fs:755-767`). Those two per-UUID primitives do not exclude one another. Two such clients can therefore execute the same UUID concurrently while both hold the shared stripe.

The new `ClientDownloads.md:45-73` is clear and materially better: it prohibits publishing, installing, rolling back into, or co-running the mutex generation and describes the required drain and approval procedure. But this is documentation, not an enforceable gate. On the frozen candidate, `Client/Program.fs:77-90` still advertises the assembly version (currently the undifferentiated default) and wire protocol, not a lock-family identity. `UserService.fs:333-352` records a protocol mismatch diagnostically at registration; `ClientService.fs:103-109` only checks exact protocol when a caller opts into that per-command condition. Neither identifies nor rejects an incompatible lock family before effect dispatch.

The named lock-family admission/drain requirement is assigned to `jarvis-p1-client-build-identity-version-protocol-ui-20261120`; that task is still Open with no attempt. Thus no implementation or acceptance evidence for the enforceable gate exists yet. F3 is not resolved for the full task outcome. Keep the mutex generation prohibited; do not infer safety from matching protocol versions, shared stripes, disconnection, or documentation alone. Before any incompatible cutover, require immutable build/source identity plus lock-family identity, fail-closed admission for unknown/incompatible clients, handling of disconnected/stale processes and rollback artifacts, and tests proving no same-UUID file/mutex overlap.

This is a blocker to declaring the mixed-generation migration complete or enabling a cutover. The cleanup-only code does not itself switch to mutexes, but the P1 task's stated mixed-client acceptance remains open.

### F4 — Original stripe-hold concern addressed; residual scan cost is measured but not bounded

Candidate enumeration now happens before acquiring the exclusive stripe, so the directory walk no longer holds the gate (`OrchestrationApi.fs:680-695`). Deletion is performed in separately gated batches of 64. This resolves the prior finding's main contention mechanism.

I independently ran `scripts/bench-pending-lock-reclamation.fsx` on Linux against the Release build. Results were 11.22 ms p50 / 47.67 ms max at 0 unrelated artifacts; 11.69 / 22.75 ms at 1,000; 13.36 / 17.99 ms at 5,000; 21.84 / 30.14 ms at 20,000; and 38.98 ms to drain 256 stale same-prefix files (0 remaining). These are useful local temporary-file measurements.

The scan remains synchronous and materializes all matching candidates (`Seq.toArray`), so scan latency and memory remain O(directory entries / matching files); five samples per unrelated-artifact level and one 256-file drain do not establish a hard latency or memory bound. The benchmark does not model production Git, concurrent cleanup, or a very large same-prefix backlog. Treat F4 as substantially addressed, with this residual limitation recorded rather than as a demonstrated production-performance guarantee.

### F5 — Resolved for best-effort cleanup failures

The operation result is captured after both lock handles are disposed, then cleanup runs, and the captured `outcome` is returned (`OrchestrationApi.fs:755-767`). Enumeration, gate acquisition, per-file inspection/probe, and deletion exceptions are contained inside the best-effort cleanup helper (`OrchestrationApi.fs:678-713`). The injected `InvalidOperationException` regression confirms a cleanup fault does not turn a successful persistence callback into a failed result. The unchanged ambiguous-IOException test confirms the operation callback is not retried.

No new path retries the remote operation. Lock acquisition still retries only recognized sharing violations within the existing 60-second deadline; the callback is invoked once. This preserves the important no-replay property for uncertain remote Git writes.

## Symlink/reparse-point review

The candidate retains the journal-directory and `.locks`-directory reparse-point checks (`OrchestrationApi.fs:720-728`). Before deleting a candidate, it requires `FileInfo.LinkTarget = null` and rejects `FileAttributes.ReparsePoint` (`OrchestrationApi.fs:698-700`), then performs the courtesy exclusive-open probe. I found no new path that intentionally follows a candidate symlink. The changed test file adds no direct symlink/reparse-point regression test, however. These are path-based checks, not an atomic handle-relative delete; a non-cooperating actor able to mutate the directory between inspection and deletion remains outside the declared support guarantee.

## Evidence and limits

- Frozen review head and candidate worktree both resolve to `3a35c382d274c46f4ba95951b2e38d252b3dadb2`; the candidate worktree was clean.
- Diff from base is limited to `Client/Modules/OrchestrationApi.fs`, `Client.Tests/ReviewWorkflowTests.fs`, `docs/ClientDownloads.md`, and the benchmark script. `git diff --check` was clean.
- Independently ran the .NET 11 Release filter `FullyQualifiedName~PendingIntent|FullyQualifiedName~PendingLock`: **11 passed, 0 failed, 0 skipped**.
- Independently reproduced the Linux temporary-file benchmark results recorded above.
- The task spec records a native Windows Release build and standalone cross-process smoke on this exact candidate. I did not independently reproduce Windows execution from this Linux host; native NUnit remains unavailable because the existing hermetic test setup is Linux-only. Treat Windows results as recorded evidence, not my independent verification.
- No production Git remote-write or deployment test was run. No merge, deployment, restart, or protocol cutover was performed.

## Required before task acceptance

1. Close F2 with a tested cleanup-liveness design, including overlapping sweeps and sustained same-prefix contention, or formally revise the bounded-growth requirement with an explicit accepted operational limit.
2. Complete and independently review the client build/lock-family identity and fail-closed compatibility-admission/drain gate before any named-mutex cutover.
3. Keep the current file-based family as the only allowed deployed family until item 2 is proven. Do not treat the documentation prohibition as enforcement.
