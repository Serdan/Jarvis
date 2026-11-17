# Independent frozen review — Proposed lifecycle and blocker projection

**Decision recommendation: ChangesRequested (P1).**
- Canonical task: `work-proposal-priority-projection`
- Review: `72cb37e14f6244b9a16e8298d2b70f8e`, claimed by Reviewer 5 at sequence 5792.
- Exact frozen commit: `2d1b1a07c44cd420eb2a418fcc2895343ebc72c1`
- Base: `7712bc547c264873036975e30194da03a05cc0ba`; original source ref: `refs/heads/feat/work-proposed-priority-worker2-20261117`.
- Scope: `Server/WorkProposal.fs`, `Server/WorkBlocking.fs`, two focused test files and project file entries. No live Scheduler, DB integration, deployment or hosted Actions.

## Independent evidence
- Exact source inspected in clean isolated registered review worktree `reviewer5-proposed-priority-2d1b-20261117`.
- Real `.NET 11 Release` focused plus adjacent `Server.Tests` (WorkProposal/WorkBlocking/WorkQueue filter): **150/150 passed**, no skips.
- Real full `.NET 11 Release Server.Tests`: **317/317 passed**, no skips.
- `git diff --check 7712bc547c264873036975e30194da03a05cc0ba 2d1b1a07c44cd420eb2a418fcc2895343ebc72c1`: exit 0, clean.
- Reviewed target `docs/specs/work-stateful-orchestration-2026-11-17.md`, canonical task, pure modules, adjacent WorkComposition and queue contracts. No source edits.

## Findings
1. **P1** — `findings/lead-priority-authorization-not-bound.md`: the verified Lead receipt does not bind the numerical priority of approval or subsequent priority change. Both commands accept arbitrary separately supplied priority under the same receipt. Bind the full authorized payload and test mismatches.
2. **P1** — `findings/cancelled-ancestor-descendant-eligibility.md`: a descendant of a cancelled System can still be ranked as an eligible Available stage because the pure gate checks only the child's own cancellation. Fail closed on cancelled ancestry and add a negative case.
3. **P2 scope clarification** — `findings/returned-proposal-without-revision.md`: return-for-revision allows immediate resubmission of the unchanged spec. Either require a revised pin or document/test that this is intentional.

The pure-module isolation, Draft/Proposed/Open staging, graph-based blocker propagation and queue ordering are structurally aligned with the G0 target. However, passing tests do not prove the missing authorization and cancellation negatives. Request a new exact frozen SHA and independent review after corrections. No integration or operator-approved cutover is implied.