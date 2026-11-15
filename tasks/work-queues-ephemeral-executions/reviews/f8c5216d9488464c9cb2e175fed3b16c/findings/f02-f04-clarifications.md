# F02–F04 — P2 clarifications and missing negative-space coverage

**Frozen source:** `9f1cd0fbbb65e250409e4e352d63f5c4b7dd9aa9`. These are **non-blocking individually**, but should be resolved during correction/Phase A rather than inferred by implementers.

## F02 — P2: Task-addressed message demand without an eligible task actor

References: §§5–6 (lines 179, 185–199), §8 (lines 254–265), §11 (lines 342–348), acceptance case 38 (line 489).

`PostOperatorMessage(Task(T))` publishes one pending **queue demand** (§5), but the default role subscriptions only explicitly include `Role(Lead)` messages, not standalone Task-message demand (§6). §11 says Task messages may be handled by the current task-stage actor, and with no active actor remain Pending until a future task activation/triage. Unlike Role-message demand, §8 does not specify which role can be launched solely to handle Task-message demand.

Interleaving: T is Blocked with no active stage/actor; operator posts an actionable Task(T) message M requesting a change. M is durable, but there is no compatible registered or schedulable actor to handle it unless **independent** work wakes T or Lead manually triages. If intentional (consistent with "first agent that next acts on T"), define it as a **passive pending Task message** rather than runnable scheduler demand, show its waiting reason in the operator UI, and do not incorrectly diagnose the queue as covered. If task-directed messages should wake a responder, specify an explicit Lead triage stage/compatible subscription that does **not** implicitly reopen T or bypass its dependency. Add cases with Blocked, Closed, Cancelled, and no-actor Task subjects; current case 38 only covers concurrent active Worker and Reviewer.

## F03 — P2: Bind session-key uniqueness to actual provider-execution lifetime

References: §§3 (lines 54–58), 7 (line 236), 8 (lines 262–268), acceptance cases 16–20.

`InitializeAgent(launchId, sessionKey, operationId)` intentionally returns the same UUID on equal-key replay. The contract calls `sessionKey` a value supplied by browser bridge or connecting client but does not explicitly state **how it is generated and scoped** to one actual provider execution rather than one launch or a reusable bootstrap. If two independent provider sessions receive the same launch and reused session key, both may receive the same UUID and act under one lease/claim, despite the UUID-per-execution goal. Conversely, retrying the same physical session with a newly generated key spuriously returns `AlreadyRegistered`.

Freeze a cooperative (not security/ACL) rule: one high-entropy key is created **once per actual provider session** at the browser bridge/session layer or on its first connection, never reused for a distinct session, never stored merely as a launch-global bootstrap constant; retries of that session persist/recover the same key and original initialization operation. Describe what can be verified for duplicate browser submissions and what cannot be verified without provider identity. Add an executable fixture for distinct physical sessions accidentally sharing/copying a bootstrap, not only K1-vs-K2 where uniqueness is assumed.

## F04 — P2: Hard dependency insertion after a work stage is Claimed

References: §5 (lines 164–168), §§6–7 (lines 195, 203–215, 238–246), acceptance cases 26 and 30 (lines 477, 481).

The specification correctly revalidates graph/credit coverage at claim and says dependency edits fence stale projections. It does not explicitly define the **active-claim policy** when Lead adds a new unsatisfied hard prerequisite to a stage already Claimed by a Worker. The stage cannot become ordinary Available/Blocked without reconciling its live activation and source; yet allowing `CompleteWork` on the pre-edit claim may bypass the newly required hard dependency. Rechecking graph version alone defines a CAS failure, not how the claimant should safely checkpoint/block/release.

Freeze whether the edit invalidates and stop/fences the active claim, only gates subsequent stage publication, or triggers an explicit worker acknowledgment/BlockWork, with exact version/generation and WIP rules. Add the interleaving: Worker claims A; Lead adds hard A-requires-B; Worker races CheckpointWork/CompleteWork/YieldWork and stalled recovery; assert either no bypass and durable blocked state or an explicitly allowed finish policy. Existing case 26 checks **unclaimed credit** invalidation, not this race.

## Positives independently checked

- Canonical review claim/verdict/integration are distinct from work-stage terminal receipts; exact frozen source/integration and one source writer per lineage are preserved (§10).
- Two-tier blocker-first FIFO excludes blocked prerequisites; complete injective rematching is checked under project CAS (§§5–8).
- C01 account-first global token with project abort tombstone avoids free-without-fence, and release requires independently verified project proof (§§7–9).
- Stalled registered claimed/no-claim recovery and unregistered pending-registration recovery use distinct fences and do not treat STOP or browser tab closure as physical terminal proof (§9).
- Multi-project activation requires protocol and writer fencing, old Manager drain, complete project/account epoch proof and operator-authorized account activation (§14).

**Verification boundary:** 49 acceptance cases are written **specifications**, not executed tests. This review inspects documentation and Git provenance only; it does not claim runtime behavior or test pass evidence.