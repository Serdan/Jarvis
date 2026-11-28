Phase A only: deterministic side-effect-free retention candidate planning from explicit observed safety facts; unknown/ambiguous evidence is ineligible with specific skip reasons; low/high watermarks implement hysteresis; oldest-first selection stops at the healthy free-space target; suggested 40/80 GiB defaults and seven-day age floor are justified against the 2026-11-28 disk incident. Focused Release tests are reported by the submitting agent as passed 14/14. Only four Phase A files changed. No runtime integration, removal, or Scheduler activation; Phase B remains gated on Worker 3's P0 guard acceptance and integration.

## Reviewer 9 — independent review

Reviewed the exact frozen source commit `00503e29530bfdf514770f74d7111b619b0c901d` against base `175ad976a6226f1cb07901b4b43f3d6b0ecf3de1` (one commit; exactly four changed files: the pure policy module, its focused tests, and one compile inclusion in each project file). The policy source is side-effect-free and fail-closed across its explicit safety evidence; source inspection identified no concrete Phase A defect. Hysteresis, threshold validation, oldest-first selection, ordinal tie-breaking, skip reasons, duplicate IDs, idempotent planning, and capacity exhaustion are represented in the tests.

### Independent focused Release verification

- Job ID: `ca77bfab025749e2ba3fe147f7cb4d62`
- Command: `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~WorktreeRetentionPolicyTests`
- Worktree: `worktree-retention-policy-worker10-20261128`
- Source: `00503e29530bfdf514770f74d7111b619b0c901d`
- Terminal result: completed successfully; exit code 0.
- Build evidence: `Client` and `Client.Tests` compiled successfully.
- Test evidence: 14 passed, 0 failed, 0 skipped; duration 55 ms.

The static inspection and independent focused Release test result support acceptance of Phase A. This review does not cover Phase B/runtime deletion or Scheduler activation; those remain out of scope.