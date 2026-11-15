# WQ04 canonical source-event evidence bridge (inert)

Task: wq04-canonical-source-proof-adapter (Worker 10). Baseline: e14173e116dc03b7dbdf261895fde3c893a643ee.

## Evidence boundary

WorkQueueCanonicalSourceEvidence.read returns a typed orchestration-ledger fact at one exact, freshly fetched, trusted Git head. It does NOT authorize a WorkQueue mutation. The result has only the IncompleteCoverage disposition; no Authorized, VerifiedProducer, or caller-set boolean is available.

The caller must provide the exact pinned ledger commit, global high-water (header.nextSequence - 1), original operation UUID, and original request hash. These are selectors and assertions, never authority. The bridge independently:

1. Fetches and validates the approved remote-backed ledger identity and Git ancestry, comparing both pinned head and high-water to the fetched snapshot.
2. Retrieves the immutable canonical ops/<prefix>/<uuid>.json record, verifying request/result hashes, then follows first-parent history to its original introducing commit, unchanged operation blob, and Git commit trailers.
3. Checks original request project, actor, operation type and UUID. Parses original receipt, requires exact effect paths and original sequence/type/UUID, checks uniqueness, and hashes EVERY recorded original entity against the originating tree.
4. Requires requested event path to be an original operation effect. Reads typed event from original tree, checks identity/task/ref/attempt/generation scope and mutation semantics against pinned snapshot. Immutable lifecycle/challenge audit bytes may not change.
5. Refetches canonical ledger head; intervening second-clone advance is stale, not fresh permission.

## Typed event source mapping

- Task lifecycle: tasks/<task>/lifecycle/<sequence>.json. Bind original operation, actor, task, attempt, action, generation; immutable event.
- Accepted review: tasks/<task>/reviews/<uuid>/review.json. Bind original DecideBranchReview decisionSequence, Accepted status, reviewer actor, decision version, managed review ref, source version, target ref, pinned SHA; current record must retain decision binding.
- Challenge/operator response: challenges/<uuid>/events/<sequence>.json. Bind original action/actor/sequence and challenge task. RespondOperatorCondition is an audit event, never automatic authorization; immutable event.
- Integration: tasks/<task>/integrations/<uuid>.json. Bind original SubmitIntegration/CompleteIntegration, reviewed SHA, review UUID/ref/version, integration ref, target ref, and exact Pending or Integrated status. Unknown push or Pending is NEVER Integrated.
- Role membership: records/agent-instructions/<base64url actor>.json. Bind original AttachRole, actor, role, exact config version/update sequence; verify RoleInstructions.resolveAt and role definition version from same pinned snapshot.

The event index is the canonical global transaction sequence in the original commit and receipt; no untrusted caller count, renamed ID, prefix or asserted high-water proves that event.

## IncompleteCoverage is a hard production gate

The orchestration Git ledger transaction can atomically contain its own event, entity, sequence and original operation receipt. It CANNOT atomically transact with:

- the separate project WorkQueue Git CAS record/receipt/index;
- the source Git remote or protected jarvis-wip / review / integration refs;
- the original caller-stable WQ03 host-side source-effect journal, mutating job or remote push result;
- account admission token, original session and independent live Scheduler.

Clone A can read a genuine Accepted review at orchestration head H while clone B advances the independent queue or source branch before A publishes stage. A Git ref CAS in only ONE repository cannot close that window. Retrying a queue planner against a newer state does not retroactively atomize both Git commits. Stale reads and missing coverage must fail closed. A lost source push acknowledgement is UNKNOWN, not a successful Integrated result and not a license to replay with a new UUID.

## Minimum requirements for a future positive WQ04 producer

1. Bind original caller-stable UUID, immutable canonical request (project/source repo, actor/session, task/attempt, assignment generation, source/ref/pinned SHA, target, work/stage generation) and WQ03 effect UUID to the original ledger operation commit. Reject changed-payload replay.
2. Verify source remote/ref and original mutating-job/push under durable host policy and retained immutable WIP snapshots or equivalent proven mechanism, with no bypass credentials.
3. Establish atomic joint source-event and project-CAS authority (single linearized transaction/ref, or a formally specified durable two-phase protocol with monotonic fences, recovery and rejection on uncertainty). Two independent successful Git writes are insufficient.
4. Prove complete, contiguous original event-index/high-water coverage in that same authority, reconcile both clones against remote; NEVER infer coverage from equal projection counts.
5. Independently verify reviewed/target source ancestry, remote target push, integration gate, versioned role scope, original account token and launch/session before enabling consumers.

Until those are implemented and independently reviewed in the combined exact-source integration, keep positive WQ04 producers, WQ05 integration hard-edge dispatch, live Scheduler and YieldSource disabled. This module has no runtime wiring and makes no claim about host protection or cross-ledger atomicity.
