# Independent frozen review — RequestChanges

**Reviewer:** Reviewer 3  
**Request:** `21618dceb64649ed8c0a70dff9ae5b13`  
**Task:** `p1-ledger-git-transport-recovery`  
**Exact frozen commit:** `5a24d3da479a2a934e3898e7add99858e394e565`  
**Base:** `7b589173135178a634495146675dad5db647e5f8`  
**Verdict:** **RequestChanges (P1)**

## Scope/provenance and independent evidence

Source ancestry verified; the diff is only `Client/Modules/LedgerStore.fs` (+260/-40 net) and `Client.Tests/ReviewWorkflowTests.fs` (+337). The worktree is clean at the frozen commit, `git diff --check` passed, and the source does not overlap Worker 5's WQ-02 ledger implementation. `dotnet --version` returned **11.0.100**. I independently ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~TransientPrepublication|FullyQualifiedName~PostPushConfirmationLoss|FullyQualifiedName~RemoteCasContention|FullyQualifiedName~AuthAndLocalPreparation|FullyQualifiedName~YieldAgentDnsFailure|FullyQualifiedName~PushTimeoutNever'` on this exact worktree: **7 passed, 0 failed, 0 skipped** in 10 seconds (including Release build). A second independent subset run passed **2/2**. A broader Client.Tests run was initiated, but no independently confirmed full-suite result is available; the original author's summary also says its full-suite attempt exceeded a bounded 180 seconds. **Do not represent the full suite as passed.** No GitHub Actions, production remote mutation, deployment, merge, or Codex use.

## Invariant review

- **Prepublication read retries:** `remoteGitWithRetries` limits retrying to read-only `fetch`/`ls-remote` and uses 3 attempts with a local wall-clock budget; `push` is excluded. The DNS case and its zero-publication property pass focused tests.
- **Cross-process CAS:** `transact` reads the current canonical head, guards entity versions, publishes by fast-forward Git push, refetches and checks original UUID+request hash, and replans when the verified head changes. The competing-writer regression passes.
- **Possible publication:** attempted pushes are marked before process execution. Confirmation failure is returned as `OutcomeUnknown` with preserved intent rather than fake success; tests cover confirmed post-push receipt, simulated lost confirmation, push timeout, journal preservation and replay.
- **Auth, local preparation, sanitation:** targeted auth and local hash-object failure tests pass, and new transport diagnostics omit raw credential-bearing stderr. The response contract still has ambiguity noted below.
- **Agent lifecycle:** the direct ledger/journal YieldAgent test preserves Working and uncommitted checkpoint during DNS failure, then confirms a single Ready/Stopped transition plus original checkpoint/receipt after recovery.
- **Missing end-to-end boundary:** the real public Task and Review write entrypoints perform an authoritative remote binding fetch *before* the journal and original-UUID scope, so the tested protection does not cover the exact initial incident failure stage.

## Findings

1. **P1, blocking — [Public Task/Review preflight loses durable operation ID before journaling](findings/public-write-preflight-gap.md)**. `OrchestrationApi.fs:2323-2344`, `:2134-2141`, `:227-248`, and `:49`. Sustained DNS failure during `LedgerBindingConfig.load` exits without writing the same-UUID journal record and returns `operationId=none`. Tests bypass this outer boundary. Correct the public entrypoint and add end-to-end fault tests before acceptance.
2. **P2 — [Recovery diagnostic can contain two conflicting nextAction fields](findings/recovery-action-ambiguity.md)**. `LedgerStore.fs:439-473` and `:2208-2223`. After possible push, confirmation failure can return both `retry-same-uuid` and `reconcile-same-uuid`; pure reads can emit `operationId=none; nextAction=retry-same-uuid`. Make recovery policy unambiguous.

## Decision

**RequestChanges.** The central retry/CAS mechanism is promising and the seven targeted tests are green, but the operator-facing public write path still violates the explicit sustained-outage acceptance criterion and reproduces the non-journaled failure mode that motivated this P1 task. Require a corrected frozen SHA, public Task/Review DNS-at-binding tests, same-UUID replay proof, and explicit single-action diagnostics. Re-review independently; no merge/deployment on this verdict.
