# Reviewer 5 — independent Challenges P1 tests-only rebase findings (2026-11-21)

**Review:** fca0b69e4f1d4a439c951c9a4b2e7d61  
**Source:** 50bf6da84abcd03cc65b631f8849d873715cfcbb  
**Base and current remote main at verification:** d702352ef705f9991ac1cb14dc15a13cce78aa44  
**Old supporting source (not approved by inheritance):** 9a8a85d72996f57b3dbea7ee904894c81f168b3f

## F1 — Exact Git provenance and delta preservation: PASS

Independently checked in registered, clean `worker1-challenge-accepted-main-rebase-20261120` worktree:
- `git show -s --format=%H%n%P` shows frozen source with **direct single parent** `d702352e...`; `git rev-parse HEAD origin/main` shows source and base respectively. `git ls-remote origin refs/heads/main` independently confirms remote main is the same exact base. The earlier accepted source parent `95a471fc...` is an ancestor of that base (`git merge-base --is-ancestor`, exit 0).
- `git diff --name-status base source` reports only `M Client.Tests/ChallengeWorkflowTests.fs`. `git diff --stat` reports 344 insertions, 3 deletions; `git diff --check` exit 0.
- Old accepted and new source file Git blobs are both `5823fe2dce6a44c2661eb3f206431af6edc4a482`; old parent and new base file blobs are both `e17badc8e2352f2164e48a21f1e85d65e75e3e1f`. Identical before/after blobs establish exact same file content and same delta, not merely test name matching.
- `git status --short --branch` shows no tracked/untracked changes before or after tests; final HEAD remains exact reviewed SHA.

## F2 — Test semantics and negative paths: PASS

Read the frozen patch independently. Five new NUnit tests, plus strengthened existing parked/unpark assertion, cover:
1. `FinalSatisfactionReplayWithWaiterIsIdempotentAndPreservesActor`: first resolution and repeat on another binding, `AlreadyApplied`, identical operation ID/sequence/hash/original JSON/paths, single queued generation and correct resolving actor.
2. `SatisfactionWinsConcurrentOwnerReleaseAndCASReplans`: ordered real bare-Git remote pre/post-receive barrier forces both competing writers to attempt the same initial head; satisfaction wins, owner release performs the eventual requeue with correct actor.
3. `OwnerReleaseWinsConcurrentSatisfactionAndCASReplans`: inverse ordered race and attribution to later satisfaction actor; both durable operations retain correct actor and receipts.
4. `FreshCloneWakesTheExactDurableBlockedGeneration`: fresh clone of canonical bare remote observes blocked wait, resolves it, and reconstructs exact generation-2 assignment.
5. `TerminalizedTaskMakesAnEligibleWaitInertOnOwnerRelease`: close task before owner release; no spurious second-generation assignment or requeue lifecycle event, and eligible wait remains inert.

Shared assertions additionally inspect persisted canonical attempt, agent Ready/Stopped assignment, wait and both by-owner/by-challenge indexes, previous/current generation linkage, exactly one requeue history event, issuer ID and sequence, operation receipt actor/sequence, and absence of generation 3. Race barrier has bounded timeout and finally-release paths; no mocked CAS result substitutes for real bare-remote write contention. Source contains tests only; no production fix or behavioral change is claimed.

## F3 — Independent .NET 11 Release execution: PASS

`dotnet --version` returned **11.0.100**; the test runner identified `Client.Tests.dll (.NETCoreApp,Version=v11.0)`.

- Focused command: `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter "FullyQualifiedName~FinalSatisfactionReplayWithWaiterIsIdempotentAndPreservesActor|FullyQualifiedName~SatisfactionWinsConcurrentOwnerReleaseAndCASReplans|FullyQualifiedName~OwnerReleaseWinsConcurrentSatisfactionAndCASReplans|FullyQualifiedName~FreshCloneWakesTheExactDurableBlockedGeneration|FullyQualifiedName~TerminalizedTaskMakesAnEligibleWaitInertOnOwnerRelease" --nologo --verbosity minimal`. Managed process job `1fbeb7e1946d4b85aed4f80e91cd7ab5`: **Completed, actual OS exit 0, 5 passed / 0 failed / 0 skipped, test duration 11 s**.
- Full Challenge group command: `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --no-build --filter FullyQualifiedName~ChallengeTests --nologo --verbosity minimal`. The `--no-build` invocation uses the binary freshly compiled by the above Release test invocation from this exact clean HEAD. Managed process job `206db35a1b174fba80cabbb29b4e1955`: **Completed, actual OS exit 0, 41 passed / 0 failed / 0 skipped, test duration 1 m 9 s**.
- Build produced the unchanged `Client/Program.fs:188` FS0760 IDisposable-construction style warning; no build errors. This review does **not** claim full Client.Tests, Server.Tests, full-solution Release, integration, deployment, or native runtime cutover validation.

## Verdict and boundaries

**No blocking findings. Recommend Accepted** for the exact tests-only rebased source and this review ID. Old Accepted verdict is only supporting history, not acceptance inheritance. Source branch is present locally but was not advertised by `git ls-remote origin`; no publish or integration is asserted or authorized here. Integrator must separately satisfy canonical integration and remote-lineage rules. No source edits, pushes, integrations, deployments, live schedules, or other agent launches performed by Reviewer 5.
