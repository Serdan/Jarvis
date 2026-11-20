# Independent review — exact named UUID mutex

**Verdict: ChangesRequested.** One P2 acceptance-test gap remains; I found no production-code correctness defect in the frozen implementation.

## Frozen snapshot and scope

- Request: `9a61b18c0cf84d8e9ea365250b07651e`
- Task: `jarvis-p1-safe-pending-lock-migration-20261120`
- Reviewed commit: `86715f7e900e834b6fd9fb22ca82b3463ab38f7e`
- Base: `95a471fc565b57fe309c65fc85f371693dbe9067`
- Source: `refs/heads/fix/operator-named-uuid-cutover-20261120`
- Target: `refs/heads/main`

Applied the Operator's narrowed rollout scope: exactly two controlled clients (Linux and Windows), updated together after both are stopped. I did not apply superseded file-lock reclamation, mixed-generation compatibility, F2 cleanup/liveness, mutex/file coexistence, or migration-infrastructure requirements. The base-to-candidate diff contains only `Client/Modules/OrchestrationApi.fs` and `Client.Tests/ReviewWorkflowTests.fs`; the worktree was clean and `git diff --check` passed.

## Code assessment

1. **Lock identity and OS scope:** `exactLockName` derives the binding key from the same root-hash used by `LedgerBindingConfig.pathForRoot`, then appends the canonical `Guid.ToString("N")`. Windows names are prefixed with `Global\\`; Linux uses the host's named-mutex namespace. This is host-local process exclusion, not a distributed mutex. Cross-device writes remain protected by the canonical ledger's UUID/request-hash check and Git ref publication protocol; the mutex itself does not span the Linux and Windows machines.

2. **Full critical section:** `withIntentLock` acquires one named mutex with `WaitOne(TimeSpan.FromSeconds(60.))`, returns a timeout error when acquisition returns false, treats `AbandonedMutexException` as successful acquisition, runs the supplied operation while owning the mutex, releases it in `finally`, and disposes the handle with `use`. The production Task/Review/Challenge write paths enter through `executeLedgerWrite`; the callback includes the pending-intent persistence, binding verification, and the remote Git transaction (fetch, push, and confirmation). The lock is therefore not released after merely writing the local journal.

3. **Recovery and uncertain effects:** The existing recovery path is preserved. Pending intent is persisted before the first authoritative Git fetch inside the write callback. An `OutcomeUnknown` is reconciled by reading the canonical operation UUID; `LedgerStore.transact` checks the UUID and request hash before creating a new operation. Explicit retry first checks the exact saved command hash and pinned verified head, and then re-enters the current typed writer. The callback is not looped or retried after an IOException; the targeted test verifies one callback invocation and a retained pending record. Abandonment releases the lock but is not treated as evidence that a remote push failed.

4. **No lock-file debt:** The candidate removes the stripe/per-UUID FileStream locks and the `.locks` directory creation. The OS handle is the only lock resource; the changed tests check that no application lock directory or exact-UUID lock file is created.

5. **Unrelated UUIDs:** Names differ by full UUID, so UUIDs sharing the old three-character prefix no longer contend. The independent-process test covers both the unrelated-UUID fast path and same-UUID waiting.

## Validation and evidence limits

- Independently ran on Linux with .NET SDK `11.0.100`: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~PendingIntent|FullyQualifiedName~PendingLock"` — **7 passed, 0 failed, 0 skipped**. This built the Client and Client.Tests Release assemblies. The build emitted an unrelated FS0760 warning in `Client/Program.fs:188`.
- `git diff --check` passed; frozen worktree remained clean.
- Operator-supplied Windows evidence: native .NET 11 Release build succeeded; standalone FSI reported same-UUID waiting of 3452 ms, unrelated UUID completion in 43 ms, abandoned-owner recovery, and no `.locks` directory. I did not independently rerun Windows execution: the registered Windows client did not expose the Jarvis project checkout to this review session. The Windows figures above are therefore recorded as Operator-provided evidence, not as my own execution.

## Finding

See `findings/timeout-regression-test.md`. The implementation has the required 60-second bound, but the test project contains no regression test for the timeout branch. This is an explicit acceptance item in the canonical task specification, so I am requesting that deterministic coverage before accepting the change.

## Rollout boundary

No merge, deployment, client update, restart, or cutover was performed. Rollout remains a separate explicit Operator authorization: stop both clients; verify both are stopped and no in-flight remote effects need reconciliation; update both from the same approved source; restart both; verify registered client and protocol information.