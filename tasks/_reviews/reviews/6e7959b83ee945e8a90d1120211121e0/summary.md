# Reviewer 5 — independent exact-source review

**Verdict: Accepted; no blocking findings.**

## Frozen source
Review `6e7959b83ee945e8a90d1120211121e0` (`_reviews`), pinned commit `2222f6e6bffc6c3b02403d79b54e3fa94cb64f47`, declared base `6032a03f7a5ce8e4cbc69a82d7b19fb1fcdffcf7`, source `refs/heads/fix/dashboard-filter-challenge-subscription-20261121`, target `refs/heads/main`. Independent `git rev-parse` confirms source worktree HEAD/branch equal pinned SHA; `git merge-base` confirms base; worktree clean before and after tests. Diff limited to `Client/Modules/LedgerStore.fs` and `Client.Tests/ReviewWorkflowTests.fs` (16 lines added, 2 removed).

## Analysis
- The former `agents/*.json` predicate included Challenge subscription files. New `^agents/[A-Za-z0-9_-]+[.]json$` admits only the canonical agent entity filenames, excluding `agents/<key>.challenge-subscription.json`. The ledger path grammar admits both schemas separately.
- `ReviewWorkflow.agentKey` writes base64url-encoded UTF-8 without padding; all valid encoded agent key characters fit the new predicate. It does not exclude valid canonical agent identities.
- `AgentDashboard.projectSnapshot` still parses all selected canonical `ReviewAgent` records and returns errors on malformed ones; no error-swallowing or mutation was added. Other record types remain in the read selection.
- The regression fixture creates a real Challenge subscription, asserts it is excluded from selected dashboard records, then loads a dashboard and verifies canonical rows/notifications. Fixture subscription write is intentional; the production filter is read-only.

## Independent execution (.NET SDK 11.0.100, Release, pinned worktree)
1. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~AgentsDashboardReadsCanonicalReadyInsteadOfSourceBranchTaskFiles --logger "console;verbosity=normal"`: **1/1 passed**, exit 0, no timeout.
2. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~AgentInboxBatchRejectsMalformedCanonicalStateAndUnexpectedOwner|FullyQualifiedName~DashboardBatchMatchesPinnedLedgerReadsAndTracksNewBlobs" --logger "console;verbosity=normal"`: **2/2 passed**, exit 0, no timeout.
All 3 independently run focused Release tests pass. F# FS0760/FS0066/FS3369 compiler warnings are unrelated to these two changed files. Existing submitter note also reported a separate source-bound entity-reader test; that test was **not independently rerun** here.

## Evidence boundary and verdict
Source-exact, isolated, bounded tests only—not full-suite, live TUI smoke, deployment, or integration approval. Direct malformed-agent dashboard end-to-end coverage is not new, but source-level fail-closed propagation remains and dedicated malformed canonical state regression passes. No observed violation of original P1 requirement and no blocking defect. **Accepted**.