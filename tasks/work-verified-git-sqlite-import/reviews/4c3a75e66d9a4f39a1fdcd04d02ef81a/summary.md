# Reviewer 2 decision — ChangesRequested

Frozen source: `76944ed50389b73b93ee307063049e8e80f6a368` on `refs/heads/feat/work-legacy-import-preflight-worker4-20261117`; base `2a26edb7d81caf45c5482c5924a9e46cb703078d`.

**Verified positives:** exact frozen worktree head and clean checkout; five-file additive diff; `git diff --check` clear; independently ran `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkLegacyImportPreflightTests --nologo --verbosity quiet` under .NET 11 Release: **22 passed, 0 failed**. The review package also reports 349/349 full Server.Tests Release, but this session did not independently rerun the full suite. C1 is deliberately disposable-marker/temporary-path gated; no live migration, writer fence or cutover was performed.

**Blocking correctness findings:**

1. [P1] `findings/receipt-stage-consistency.md`: accepted review/integration receipt rows can survive preflight when their corresponding stage is still `DomainState.Undecided` and be used as acceptance evidence/persisted as positive SQLite rows. Require bidirectional stage/receipt matching and negative tests.
2. [P1] `findings/negative-review-decision.md`: `FinalizedPendingTerminal` frozen review with a verified `Accepted=false` decision is incorrectly rejected. Preserve valid negative outcomes without authorizing integration.

**Scope/gates:** this verdict only addresses C0 verified mapping and C1 disposable atomic fixture. No source-authenticated original Git CAS writer fence or real production cutover is present, and this review does not approve one. After fixes, resubmit a fresh frozen commit, verify negative tests and account/project rollback, and request independent re-review.
