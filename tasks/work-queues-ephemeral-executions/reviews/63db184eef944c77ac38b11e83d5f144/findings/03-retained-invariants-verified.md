# Verification V03 — retained full-architecture safety and rollout gates (no blocking finding)

**Frozen source:** 35e7465a122fd9d767a9f75494b241ff2eb7fe72; documentation-only change from a54261ba05a08df1525542aea82fd952505e3f12. Entire 558-line specification independently inspected, not only its new paragraphs. The commit changes only docs/specs/work-queues-and-ephemeral-executions.md.

## Prior C, B and F findings checked against the frozen contract

| Prior finding | Current invariant / negative-space coverage | Assessment |
| --- | --- | --- |
| C01: global token but failed/unbound project launch | §§4, 7–8 and tests 48–50: project abort-only tombstone shares the same launch-ID absence CAS guard as admission, binds original admission operation, fences delayed Git/replay; unknown admission cannot be inferred rejected from an absent record; account release requires independently verified terminal/abort proof | Retained |
| C02: prepared Manager handoff versus abort/swap | §8 and tests 51–55: irreversible project prepare fence precedes global single Swapped-or-Aborted disposition; later project finalization obeys current token owner; aborted never allows delayed swap; child dispatch only after verified swap + finalization | Retained |
| F01/B03: launch demand overcount or stranded compatible credit | §§4, 6, 8 and tests 20, 36, 66: injective matching of distinct eligible generations across all live unclaimed launches, highest-priority feasible ClaimNextWork and *atomic full rematch* when credits change | Retained |
| F02/B01: taskless launch and capacity release | §7/tests 5–7, 21–22, 34 plus new 56–59: one claim max per launch, explicit ManagerFinished/NoWork/Cancelled/Error or no-claim reclaim receipt, separate project terminal then global release, no tab-close evidence | Retained and extended |
| B02: last-capacity-slot Manager child dispatch | §8/tests 31, 35, 51–55: project fence before account CAS swap; swap-or-abort receipt, no child submit while unresolved, correct owner on partial recovery | Retained |
| F03: source continuity | §9/tests 12, 19, 42: isolated activation worktrees, pinned repository/base/source and immutable generation-scoped remote WIP commit verified reachable before normal yield, retention and mutating-job protection; stale old agent cannot overwrite canonical continuation | Retained |
| F04: review claim/status dual authority | §10/tests 22, 27: queue claim and canonical review claim in one project CAS; verdict distinct from reviewer CompleteWork; after verdict YieldWork/BlockWork cannot reopen; one integration policy producer | Retained |
| F05: parallel source writers | §§4, 10/tests 9, 28: single current actionable writer per source lineage and generation, concurrent ChangesRequested converge on one correction stage, stale reviews cannot overwrite current source | Retained |
| F06: challenge race and index authority | §§5, 11, 14/tests 15, 23, 29: challenge block/satisfaction CAS, source high-water plus per-shard queue/attention coverage fail closed, no incomplete index treated as empty | Retained |
| B04: multi-project migration epoch | §14/tests 16, 23, 29, 37: fixed project membership, protocol writer fence before prepare, inert Prepared proofs, account Frozen/Preparing/Validated/Activated transition, matched account/project read-write gate, controlled preactivation abort only | Retained |
| B05: account capacity resize | §8/tests 30, 38: cap config and tokens in one account-ledger CAS; shrink below active/uncertain count rejected; concurrent reserve/resize serialize | Retained |

## Cross-cutting architecture checks

- **One account-global logical execution limit, all projects/roles/devices.** Manager/Lead/Worker/Reviewer/Integrator share account tokens. Scheduler skips Manager when full and still permits *non-agent* safe reconciliation. Pending, unknown or submitted launches remain counted. Last-slot Manager handoff remains ordered and crash-reconcilable. Logical Jarvis revocation is expressly not a physical provider-process hard cap.
- **Project-versus-account authority.** Task work, reviews, conversations, claims, role-message queues and derived attention indexes are project Git ledgers. Capacity tokens, config and migration epoch belong only to account Git ledger. No cross-repository atomic commit is claimed; receipts, original operation IDs, conservative uncertain states and compensating/reconciling transitions are required.
- **Task-first presentation.** Active/Stalled are derived per work claim and lease; task title is primary, UUID in details, prioritized verified Next queue remains visible, Blocked/Needs operator and uncertain Starting are separate. No duplicate writable Task.Active flag or persistent Worker roster.
- **Exact review/integration evidence.** A frozen review pins source SHA/ref. Accepted can publish integration demand only for the same reviewed SHA and explicit task policy; Integrator must verify exact remote ancestry, and changed/conflicted source requires fresh review. Review verdict, work-stage termination, integration receipt and task closure are different events.
- **Migration and operational safety.** Protocol handshake fences legacy writers before any staged conversion; failed or unverified project/account epochs suppress new work. Source and pending-intent provenance preserved. No implementation, merge, restart, deployment, schedule enablement or account cutover is authorized by this docs review.

## Assessment

No regression or newly observed blocking architectural contradiction was found in the retained invariants. The user-requested tests 48–66 are **design acceptance scenarios**, not tests run in this review. Phase A's deterministic two-clone/account+project simulations, original-operation journal reconciliation, exact frozen review/integration and migration gates remain mandatory before code or rollout acceptance.