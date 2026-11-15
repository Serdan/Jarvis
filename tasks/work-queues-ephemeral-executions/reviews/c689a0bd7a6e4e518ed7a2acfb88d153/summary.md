# Independent frozen architecture re-review — ChangesRequested

**Reviewer:** Reviewer 2
**Review:** `c689a0bd7a6e4e518ed7a2acfb88d153`
**Task:** `work-queues-ephemeral-executions`
**Exact frozen source:** `a54261ba05a08df1525542aea82fd952505e3f12`, `refs/heads/docs/work-queues-design-corrections`
**Reviewed document:** `docs/specs/work-queues-and-ephemeral-executions.md` (523 lines, 116,213 bytes; clean registered worktree at exact SHA)
**Prior reviews:** `f0ec0c91bb1049aa9b40bf9ea7adc751` (Reviewer 1, C01–C02; source `ef486f4617d211db0719924626ae3b05bc1f40ca`), `b7fd8a31f67b40f384ead6b569d93724` (B01–B05), `d4fbb56e2d4c451eab1ef23c281dc647` (F01–F06).
**Method:** Independent full-spec state-machine, ledger-CAS, crash/replay, cross-ledger token ownership, task/review/integration, lease, source and migration examination. Documentation only; no tests executed, source edits, merge, restart, deployment or schedule.

## Explicit verdict: ChangesRequested

**C01 and C02 are materially corrected.** Their new state transitions, token-owner rules, idempotent receipts, and added negative-space cases 48–55 are coherent. All previous F01–F06 and B01–B05 remedies remain substantively retained. The architecture of durable work items, ephemeral one-claim-per-UUID launches, task-addressed conversations, role subscriptions and a single logical account-global capacity limit is appropriate.

**One distinct P1 blocking gap remains: S01, ordinary taskless stalled launch recovery.** See `findings/01-stalled-taskless-launch-recovery.md`. The new `RecoverStalledExecution` (§9, line 280) explicitly requires an unchanged exclusive work claim and either requeues that item or blocks it. An ordinary Manager (or a submitted, pre-claim Worker/Reviewer) has no item or claim. `TerminalizeLaunch` allows a reconciler to terminate only on positive terminal proof, and the spec has not defined how expiry-based recovery creates that proof for these no-claim launches. A stalled Manager at `maxConcurrentAgents=1` can consequently strand the global token and prevent the scheduler from launching a replacement. Prepared-handoff Manager recovery *is* covered separately; ordinary taskless recovery is not.

## C01 and C02 verification

| Gate | Frozen specification | Independent assessment |
| --- | --- | --- |
| **C01: token reserved, no project launch** | §4 line 117 onward; §7 lines 177–199; §8 lines 238–242; API §12 line 374; negative tests **48–50** | **Addressed.** `AbortUnboundProjectAdmission` writes a minimal project launch tombstone under the *original* launch ID, agent UUID and project-admission operation ID. Normal admission and abort compete on the same project CAS/launch-ID absence condition; immutable tombstone fences late/replayed admission. A definite failure must be verified; a lost/unknown original Git outcome retains the token until its operation receipt is reconciled. Account release requires independent tombstone/terminal proof and frees precisely the matching launch token once. Also applies to scheduler taskless Manager admission. |
| **C02: prepared handoff vs delayed swap/abort** | §7 lines 186–190; §8 lines 251–260; API §12 lines 375–376; negative tests **51–55** | **Addressed.** Project `PrepareManagerHandoff` first irreversibly fences Manager and registers pending child demand. `ResolveManagerHandoff` next commits one account-ledger CAS `Swapped` **or** `Aborted` disposition under the original handoff ID. `Aborted` retains **Manager** as token owner; project finalization cancels child and creates Manager terminal proof before Manager release. `Swapped` makes **child** the token owner; project finalization either authorizes its once-only dispatch or terminalizes child and releases child token if invalid. A historical prepare receipt cannot override a global Aborted outcome. |
| **Crash/replay and owner** | §7 lines 195–201; §8 lines 253–258; tests **33–35, 48–55** | **Addressed at spec level.** Lost account decision or project receipt replays original stable IDs; project cancellation cannot delete prepared child demand before global choice; account token release checks **current** owner. Aborted/Swapped partial states converge with correct parent/child cleanup. Unknown browser submit and uncertain Git outcomes retain capacity; no cross-repository atomicity is claimed. |

Acceptance items **48–55 are specified scenarios, not executed test evidence**. They explicitly cover failed project admission, lost receipt, admission-vs-tombstone race, delayed swap vs abort, crash after each account decision, child cancellation, swapped token owner and stalled prepared Manager.

## Earlier F/B correction matrix

| Issue | Retained invariant and source |
| --- | --- |
| **F01, B03 — duplicate demand / priority rematching** | §§4, 6, 8: canonical project demand credits cover distinct available work generations via injective *compatible* matching. Highest-priority **feasible** ClaimNextWork atomically rematches *all* other outstanding credits. Invalidation cannot silently count as other demand. Tests 20, 36. |
| **F02, B01 — session/activation/no-work** | §§3, 7–9: one successful claim per launch, exact claim replay, ManagerFinished/NoWork launch-level terminal receipts, separate project terminal then account release. Strict physical provider-process cap is explicitly **not** implied by logical `maxConcurrentAgents`. Tests 5–7, 21–22, 34. **S01 is the separate no-claim *stalled-reclaim* omission.** |
| **B02 — last-slot Manager project fence** | §8: project prepare before global token transition, child blocked until verified project finalization and global decision, one-shot journal recovery. Tests 31, 35, 51–55. |
| **F03 — source continuation** | §9: isolated activation worktrees; pinned repo/base and generation-scoped remote WIP ref/immutable reachable SHA before normal yield; retain refs; reject unsafe dirty/untracked/unpublished or mutating-job handoff. Tests 12, 19, 42. |
| **F04 — review canonicality** | §10: same project CAS for review claim and work projection; verdict not reviewer launch termination; post-verdict YieldWork/BlockWork cannot reopen a decided review; exactly-once policy producer. Tests 22, 27. |
| **F05 — current source writer** | §§4, 10: one actionable source-writer stage per lineage/generation; concurrent ChangesRequested findings join the same stage; stale feedback remains audit evidence, explicit plan required for distinct branches. Tests 9, 28. |
| **F06 — challenge/index coverage** | §§5, 11, 14: source/index high-water and per-shard coverage, fail closed incomplete queue, challenge block-versus-satisfaction CAS, claimed review and uncertain-intent migration refuses. Tests 15, 23, 29. |
| **B04 — migration epochs** | §14: fixed participating-project set; account `LegacyActive→Frozen→Preparing→Validated→Activated`, inert per-project Prepared proofs, protocol writer fence before preparation, verified account+project reader/writer gates, explicit pre-activation abort and no destructive rollback. Tests 16, 23, 29, 37. |
| **B05 — resize** | §8, §12: configuration version and tokens share account CAS domain; shrink below live/uncertain occupancy rejected, race with reserve serialized, no unproved eviction. Test 38. |

## Remaining scope and checks

- **UUID / STOP / stalls (§§1–3, 9, 12–13):** A new session gets a fresh immutable agent UUID, separate from task/role/provider ID/visual Worker N. Existing `queue_response_alert`, `get_response_alerts`, `clear_response_alert` target that UUID; a STOP-request is deterministic, delivered before other instructions when possible, and neither delivery nor loss is evidence of termination. `Stalled/StopRequested` is a one-way project CAS transition; post-STOP heartbeats do not resurrect it. Claimed-work reclaim fences claim generation, treats unresolved source jobs/external effects as RecoveryBlocked, and separately handles a Manager in prepared handoff. Tests 39–47, 55 cover those paths. **S01 addresses missing no-claim reclaim.**
- **Global cap / scheduler (§8):** One account-global Git admission ledger counts Manager/Lead/Worker/Reviewer/Integrator across all projects/devices and serializes reservations, releases, handoffs and resize. Scheduler skips Manager when full; its read cannot authorize launch without reserve CAS. Non-agent reconciliation can run while cap full; uncertain tokens stay occupied. Cap is **logical Jarvis execution**, never advertised as a provider-tab count. Tests 30–32, 38.
- **Task conversation (§11):** Questions/answers/decisions on canonical task/work subject, immutable history and cursor continuation across UUIDs; blocking answers atomically satisfy challenge dependencies. Notes/decisions never silently mutate spec, verdict or integration. Tests 24–26.
- **TUI (§13):** Task-title-first Active & Stalled, separate verified prioritized Next, Blocked/Needs-operator filter and pending/uncertain launches shown as Starting. Statuses derived from authoritative work/lease facts, not a second writable Task.Active flag. UUID shown in details, not used as title. Tests 43–44.
- **Exact review/integration (§10):** Review pins immutable source SHA/ref; Accepted can publish an integration work item only under task policy with review ID/SHA/target. Integrator verifies actual accepted SHA and target, requires re-review of edits after conflict, and treats verdict, integration and task closure as separate events. Tests 9–10, 22, 27.
- **Migration/rollout (§§14–15):** Handshake blocks old writers before staged conversion, account activation is operator-authorized and partial project conversion never exposes new queues. This frozen docs review does not authorize Phase F.

## Required correction before acceptance

Add explicit **no-claim stalled launch recovery** (same `RecoverStalledExecution` with a separately defined mode, or a dedicated `RecoverStalledLaunchWithoutClaim`) using original launch/UUID/lease/journal proof. Project CAS must fence remaining Manager dispatch/late ClaimNextWork and write one terminal receipt only after children, uncertain submits and other side effects are safe; then account CAS releases **only that launch's current token** once. Preserve prepared-handoff Swapped/Aborted precedence and RecoveryNeeded on uncertainty. Add negative cases for ordinary cap-one stalled Manager, Manager with independent child tokens and uncertain submit, pre-claim NoWork vs concurrent recovery, and late old-session writes. Retain C01/C02 tests **48–55** and F/B corrections unchanged in intent. Request fresh exact-SHA review after correction.

**Canonical verdict: ChangesRequested.** Architecture/design review only; no implementation, source mutation, integration or deployment authorization.
