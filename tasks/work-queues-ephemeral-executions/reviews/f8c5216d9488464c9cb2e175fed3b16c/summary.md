# Reviewer 2 — Independent frozen architecture review

**Verdict: ChangesRequested (one P1 blocker, three P2 clarifications).**

- **Request:** `f8c5216d9488464c9cb2e175fed3b16c`
- **Task:** `work-queues-ephemeral-executions`
- **Frozen review/source:** `refs/heads/jarvis-review/work-queues-ephemeral-executions/f8c5216d9488464c9cb2e175fed3b16c/r1` / exact `9f1cd0fbbb65e250409e4e352d63f5c4b7dd9aa9`
- **Target:** `refs/heads/main`; baseline `1481bfa03aea4def721d2b24835bff9b9076160a`
- **Scope:** 2026-11-15 docs-only architecture; no implementation, integration, restart, deployment, scheduler activation or migration authorized.
- **Independent verification:** Verified local review worktree HEAD equals exact frozen source SHA. `git show --format=fuller --stat` identifies exactly one changed file, `docs/specs/work-queues-and-ephemeral-executions.md` (306 insertions, 336 deletions). `git diff --check <baseline> <frozen> -- <spec>` returned exit 0. Read the **entire ~101 KB/528-line** frozen spec, including 49 stated negative-space acceptance cases. Those cases are **proposed fixtures**, not executed/passing test evidence.

## Findings

**F01 — P1 / BLOCKING: Dedicated Role-message primary claim vs TerminalizeLaunch(Finished) disagree.** §7 declares a dedicated Role-message launch may claim message work as its one primary activation; §11's `CompleteSubjectMessageHandling` handles message IDs but does not explicitly complete that primary work item and terminalize its launch; §7 and acceptance case 44 instead use `TerminalizeLaunch(Finished)`, available only with **no active primary claim**. This admits a stuck launch/credit or token release without atomic message-work completion. Specify the authoritative state transition and add crash/ack/batch/NoWork interleavings. See [F01](findings/f01-role-message-primary-terminal.md).

**F02 — P2: Task-message demand without active task actor.** §5 projects actionable Task messages into a queue, but default subscriptions do not cover dedicated Task-message handling and §11 leaves no-actor messages Pending. Specify explicitly passive-until-next-task-actor semantics or a Lead triage route without implicit Task reopening, plus coverage for blocked/closed/no-actor tasks. See [F02–F04](findings/f02-f04-clarifications.md).

**F03 — P2: UUID-per-actual-session depends on the unspecified sessionKey generation/lifetime.** Equal-key replay correctly returns the same UUID, but the design must ensure that two different physical provider sessions do not reuse one launch-global/bootstrap session key and share a UUID/lease. Freeze cooperative key scoping and duplicate-session fixtures. See [F02–F04](findings/f02-f04-clarifications.md).

**F04 — P2: New hard dependency on an already-Claimed stage needs an explicit active-claim policy.** Current tests cover unclaimed credit invalidation, not the race between Lead inserting a hard prerequisite and Worker checkpoint/CompleteWork/yield/recovery. Define claim invalidation/finish policy and source-safe recovery. See [F02–F04](findings/f02-f04-clarifications.md).

## Areas inspected without a new blocker

1. **Control plane:** Replacing Manager with non-agent Scheduler/Reconciler removes recursive model launch and cap-one handoff; historical Manager receipts are fenced and retained at migration.
2. **Priority and dependency:** Two-tier blocking-first FIFO uses only hard unsatisfied stage-generation edges; blocked cycles remain unrunnable; Tarjan SCC/self-loop warnings are diagnostics with verified graph coverage, not edits or priority overrides.
3. **Matching and capacity:** Every normal unclaimed launch has compatible injective demand coverage, with project-CAS rematching; account-global reserve/resize/release CAS covers all projects and uncertain logical launches. Retained C01 project tombstone fences global-first admission failure before release.
4. **Identity/recovery:** Pre-connect launch ID is distinct from UUID generated on first successful InitializeAgent; same-session replay and conflicting-key rejection are specified. Unregistered submission journal/fencing is separate from registered claimed/no-claim STOP/lease recovery; logical terminality is not equated with provider exit.
5. **Source/review/integration:** Durable remote WIP/source lineage, one writer per lineage, canonical exact frozen review/decision, separate reviewer work completion, exact accepted SHA integration and explicit task closure are preserved.
6. **Communication/migration:** Durable Task/Role threads use first-handler CAS, not read-consumes semantics; ancillary Role handling preserves pending launch matching; account/project epoch writer gates, old Manager drain, and explicit operator activation protect cutover.

## Required next action

Clarify and repair **F01** in the canonical spec; address or explicitly defer the P2 cases with executable fixture names. Commit a **new source SHA**, submit a **fresh frozen independent review**, and keep all implementation, integration, migration and production actions gated. The previously Accepted Manager-centric doc at `35e7465a...` is historical, not approval of this superseding design.

**Review documentation only:** No source or live runtime change performed.