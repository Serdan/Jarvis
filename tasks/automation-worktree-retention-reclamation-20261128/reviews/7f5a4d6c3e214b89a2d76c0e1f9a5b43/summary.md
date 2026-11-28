Phase A only: deterministic side-effect-free retention candidate planning from explicit observed safety facts; unknown/ambiguous evidence is ineligible with specific skip reasons; low/high watermarks implement hysteresis; oldest-first selection stops at the healthy free-space target; suggested 40/80 GiB defaults and seven-day age floor are justified against the 2026-11-28 disk incident. Focused Release tests are reported by the submitting agent as passed 14/14. Only four Phase A files changed. No runtime integration, removal, or Scheduler activation; Phase B remains gated on Worker 3's P0 guard acceptance and integration.

## Reviewer 9 checkpoint — independent verification pending

Reviewed the exact frozen source commit `00503e29530bfdf514770f74d7111b619b0c901d` against base `175ad976a6226f1cb07901b4b43f3d6b0ecf3de1` (one commit; exactly four changed files: the pure policy module, its focused tests, and one compile inclusion in each project file). The policy source is side-effect-free and fail-closed across its explicit safety evidence; source inspection has not identified a concrete Phase A defect. Hysteresis, threshold validation, oldest-first selection, ordinal tie-breaking, skip reasons, duplicate IDs, idempotent planning, and capacity exhaustion are represented in the tests. This is not a verdict.

An independent focused Release test run was started against that exact frozen worktree:
- Job ID: `ca77bfab025749e2ba3fe147f7cb4d62`
- Command: `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~WorktreeRetentionPolicyTests`
- Worktree: `worktree-retention-policy-worker10-20261128`
- Source: `00503e29530bfdf514770f74d7111b619b0c901d`
- Required terminal evidence: the job's terminal result, exit code, and test summary (the submitting agent reports 14/14 passed; this reviewer has not independently confirmed it).

Continuation: once the existing job is terminal, retrieve its result exactly once, inspect the exit code and test count, then record the independent Accepted/ChangesRequested verdict. Do not rerun or poll the running engineering job. Keep Phase B/runtime deletion and Scheduler activation out of scope.