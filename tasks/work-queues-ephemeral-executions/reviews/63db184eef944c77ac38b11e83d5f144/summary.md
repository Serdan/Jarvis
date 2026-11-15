# Reviewer 1 — frozen architecture review: Accepted

**Review request:** 63db184eef944c77ac38b11e83d5f144  
**Task:** work-queues-ephemeral-executions  
**Frozen source SHA:** 35e7465a122fd9d767a9f75494b241ff2eb7fe72  
**Parent SHA:** a54261ba05a08df1525542aea82fd952505e3f12  
**Branch/ref:** refs/heads/docs/work-queues-design-corrections  
**Reviewed document:** docs/specs/work-queues-and-ephemeral-executions.md (558 lines)  
**Reviewer:** Reviewer 1  
**Prior verdict:** Reviewer 2 ChangesRequested on c689a0bd7a6e4e518ed7a2acfb88d153, sole remaining S01. Earlier C01/C02 findings and F01–F06/B01–B05 were reviewed for retention.

## Explicit verdict — Accepted (architecture/design only)

**No remaining blocking architecture finding identified in this frozen specification.** S01 is corrected by a separately versioned, project-CAS no-claim stalled-launch recovery operation, with exact terminal proof before an account token release. The added Task/Role operator-message design meets the durable first-eligible-handler requirement, including Role(Jarvis,Lead) with no currently running Lead, and preserves compatible demand credits when a running role instance handles messages as ancillary work.

Acceptance is **only** of this proposed documentation contract at the frozen SHA. It is not evidence of passing tests, implementation correctness, existing production behavior, a merged source branch, rollout authorization, or permission to deploy, restart, schedule or enable Manager. The Git commit was independently verified read-only and modifies only the spec document. The registered checkout was clean and at precisely the claimed SHA.

## Key findings

### V01 — S01 safe stalled launch with no claim: resolved

- §§7/9, lines 179–206 and 273–295 define RecoverStalledLaunchWithoutClaim for submitted, registered taskless Manager or zero-claim Worker/Reviewer, requiring Stalled/STOP project receipt, server-witnessed expired lease, launch/UUID and lease-generation checks, absence of active claim, verified child/dispatch/job journals, and one project CAS StalledReclaimedNoClaim receipt fencing late claims and dispatches.
- An independently verified terminal project receipt authorizes **only the current matching launch token** to be freed once in the account global ledger. NoWork, concurrent ClaimNextWork, cooperative ManagerFinished, duplicated recovery and late wakeup serialize against the launch-generation/terminal CAS.
- A Manager's confirmed child tokens are independent; ambiguous child submit, Git or mutating-job effects retain RecoveryNeeded/RecoveryBlocked. A Manager in TerminalPreparedHandoff is excluded and reconciles the globally authoritative Swapped-or-Aborted decision first.
- At maxConcurrentAgents=1 a safely reclaimed ordinary Manager can free the sole token; no phantom task or claim generation is introduced. Tests **56–59** specify the relevant negative-space interleavings.

See findings/01-s01-no-claim-recovery-verified.md.

### V02 — operator Task/Role messages: coherent

- §11, lines 349–371, and proposed APIs lines 381–409 store messages by immutable **Task(project,taskId) OR Role(project,roleId)**, not by numbered Worker, historical session or role-config version. Role(Lead) works without a running Lead UUID. Ordered conversation and cursor survive UUID changes.
- PostOperatorMessage creates immutable message plus deterministic pending handler demand. Passive read does **not** consume; the first eligible current execution wins ClaimPendingSubjectMessages under project CAS. Handler responsibility is independent of primary work claim, scope/approval authority and account permissions.
- Unhandled role messages create compatible Lead-role queue demand under the one global cap. An already-active Lead can take an ancillary handler lease while doing its primary work, but the same project CAS must preserve injective matching for **all other admitted unclaimed compatible launch credits**. If an earlier launch would be stranded, that opportunistic handling is refused or rematches to an alternative.
- CompleteSubjectMessageHandling atomically records response/handled receipts; unreplied claims survive/redeliver on verified yield/terminal/reclaim with the original thread. Challenge satisfaction requires explicit canonical mutation. STOP alerts remain directed solely to **agent UUID** via existing queue_response_alert APIs; no separate security principal or STOP message bus.
- Tests **60–66** cover no active Lead, competing reads/claims, yield/stall replay and duplicate ACK, concurrent Task actors, durable batches, alert separation and the ancillary-versus-reserved-credit race.

See findings/02-task-role-operator-messages-verified.md.

### V03 — earlier corrections and full architecture retained

- **C01 (tests 48–50):** unbound global reservation cannot be released without a competing project-admission/abort-only tombstone CAS under the original launch ID; uncertain Git results retain token. **C02 (51–55):** project prepare/fence precedes single global Swapped-or-Aborted owner decision, then safe project finalize/dispatch/owner-specific release. No branch from the new no-claim recovery bypasses either.
- **F01/B03:** full compatible injective matching with highest-priority feasible work and CAS rematching; **F02/B01/B02:** one claim per launch, explicit launch terminalization, proper last-slot handoff; **F03:** remote immutable WIP source and isolated worktree handoff; **F04/F05:** canonical review CAS and one writer per source lineage; **F06:** source/index coverage and challenge block/wake CAS; **B04:** account/project migration epoch and protocol read/write gates; **B05:** resize and reservations in one account CAS.
- Global cap counts all logical roles across all projects/devices, with a non-agent reconciler when full; TUI remains task-title-first with derived Active/Stalled and separate prioritized Next; exact frozen SHA review and integration remain distinct from task closure; migration requires audited multi-project preparation and operator-authorized activation.

See findings/03-retained-invariants-verified.md.

## Implementation and verification gates (nonblocking to this docs verdict)

1. Exercise negative-space tests **56–66**, alongside retained **1–55**, in disposable project/account Git-ledger simulations with two clients, replayed original operation IDs, lost ACKs, stale provider sessions and reordered project/account commits. These fixtures are *specified*, not executed.
2. Assert the atomic boundary between a primary ClaimNextWork(role-message) and an ancillary ClaimPendingSubjectMessages targeting the same message generation; test handler ACK versus execution terminal/reclaim and additional messages posted during an active handler lease. This is a concrete implementation validation of §§7, 8 and 11, not a proposed second claim/security system.
3. Include an account-reserved-only child/admission journal racing parent S01 recovery, unknown browser submits and mutating jobs, current-token-owner checks, a cap-one Manager and prepared-handoff Aborted/Swapped recovery. Never release on absence of a project record or STOP alert consumption alone.
4. Before any Phase F rollout, verify account/global capacity serialization and resize, task/review/integration exact SHAs, protected source WIP reachability, full index coverage and migration epoch gates against connected clients. Explicit operator release authorization remains required.

**Canonical decision requested: Accepted.** Independent review and review-doc writes only; no source edits, implementation, merge, deployment, restart or schedule.