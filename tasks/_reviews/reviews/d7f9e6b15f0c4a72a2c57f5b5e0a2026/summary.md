# Independent review — Challenges P1 tests-only coverage

**Verdict: Accepted**

## Reviewed snapshot

- Review request: `d7f9e6b15f0c4a72a2c57f5b5e0a2026`
- Owner: Worker 1
- Frozen review head: `9a8a85d72996f57b3dbea7ee904894c81f168b3f`
- Task/source ref: `refs/heads/test/challenge-requeue-cas-provenance-worker1-20261120`
- Frozen review ref: `refs/heads/jarvis-review/_reviews/d7f9e6b15f0c4a72a2c57f5b5e0a2026/r1`
- Baseline: `95a471fc565b57fe309c65fc85f371693dbe9067`
- The reviewed commit's sole parent is exactly the specified baseline.
- The registered source worktree `worker1-challenge-requeue-tests-20261120` was clean at the reviewed SHA.

## Scope and source inspection

`git diff-tree --no-commit-id --name-status -r <baseline> <reviewed-sha>` reports exactly one modified path:

- `Client.Tests/ChallengeWorkflowTests.fs`

No production code, ledger writer, orchestration API, project inclusion or unrelated file changed. `git diff --check <baseline> <reviewed-sha>` exited 0; `git status --porcelain` was empty.

I inspected the added test helpers and all new/modified cases, and traced the assertions against the unchanged production paths:
- `ChallengeWorkflow.wakeDependents` validates the canonical wait/index and current task, attempt, owner and generation before issuing the next generation.
- `ReviewWorkflow.reconcileEligibleOnRelease` rechecks an Eligible wait on owner release and performs the release-side requeue in the same ledger transaction.
- Completion and administrative unpark pass the actual operation actor into release reconciliation.

The tests use the existing public Challenge/Review workflows and a real shared bare Git remote fixture.

## Coverage assessment

1. **Final-satisfaction replay with a real waiter.** Creates and blocks a real attempt, satisfies its final dependency, then replays the same operation UUID from the other clone. The replay is marked AlreadyApplied and compares the original operation ID, sequence, request hash, original-result hash/JSON and changed paths. The final-state helper verifies one transition from assignment generation 1 to 2, a generation-2 issue with PreviousGeneration 1, generation 3 is stale, exactly one requeue lifecycle event, the original operation actor/sequence, and one resolution history event.

2. **Both satisfaction/release commit orders.** The two tests use independent clones sharing one bare remote. A pre-receive/post-receive hook barrier forces both transactions to arrive based on the same initial remote head, then forces either satisfaction or owner completion to commit first. Both operations must be present afterward. Assertions verify the final attempt, owner assignment, wait status and reverse indexes, assignment issue, lifecycle event, operation records and actor provenance. The immutable wait indexes remain tied to blocked generation 1 while the requeued attempt/issue advance to generation 2; no generation-3 assignment or duplicate lifecycle event appears.

3. **Immediate and delayed actor provenance.** The shared assertion checks AssignmentIssue.IssuerId, TaskLifecycleEvent.ActorId and the operation receipt actor/sequence. Immediate satisfaction is attributed to Lead. The existing parked-owner/unpark case now captures the unpark operation UUID and asserts delayed requeue attribution to Orchestrator, distinct from the blocked owner. The full module run includes this modified case.

4. **Durability across a fresh clone.** Commits a real wait, clones from the shared bare remote, verifies the new clone sees the exact blocked attempt and Waiting status, and resolves the challenge from that clone. The normal requeue assertions then verify generation, owner, wait/index, issue, history and actor.

5. **Terminalized wait inertness.** Makes a wait Eligible while its owner is busy, closes the dependent task, then completes the owner's unrelated assignment. It verifies the closed task remains terminal, its attempt remains Blocked at generation 1, the Eligible wait/index remains inert, no requeue lifecycle event is emitted, and generation 2 does not exist.

No correctness gap was found in the assigned test-only scope.

## Independent Release evidence

Environment: Linux 7.2.14-arch1-1 x86_64; .NET SDK 11.0.100.

Focused new-regression command:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~FinalSatisfactionReplayWithWaiterIsIdempotentAndPreservesActor|FullyQualifiedName~SatisfactionWinsConcurrentOwnerReleaseAndCASReplans|FullyQualifiedName~OwnerReleaseWinsConcurrentSatisfactionAndCASReplans|FullyQualifiedName~FreshCloneWakesTheExactDurableBlockedGeneration|FullyQualifiedName~TerminalizedTaskMakesAnEligibleWaitInertOnOwnerRelease" --logger "console;verbosity=normal"
```

- Exit code: 0
- Result: 5/5 passed
- Test-run duration: 12.2538 seconds

Complete ChallengeWorkflow test-group command:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~ChallengeTests" --logger "console;verbosity=normal"
```

- Exit code: 0
- Result: 41/41 passed
- Test-run duration: 1.3254 minutes

Both Release runs emitted the same pre-existing FS0760 disposal-construction style warning at `Client/Program.fs:188`; no errors or test failures occurred. The reviewer did not modify source.

## Evidence boundary

This verdict is based on the exact frozen commit, direct-parent baseline, source inspection, clean worktree/diff checks, and the two independently executed Linux .NET 11 Release test runs above. It does not claim a full-solution test run, Windows execution, or external CI result.

**Conclusion: Accepted. No findings.**