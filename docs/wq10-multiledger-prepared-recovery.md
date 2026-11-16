# WQ10 multi-ledger Prepared recovery (disposable fixture only)

## Scope and authorities

This fixture rehearses **one original account WQ07 authority and two distinct original project WQ02 authorities**. It calls the already-reviewed original Git CAS entrypoints. The account Preparing receipt is a single account transaction, then each project Prepared receipt/graph/queue/operation UUID is a separate single-project transaction. **There is no cross-repository atomic commit, additional coordinating ledger, runtime activation or Scheduler/Manager dispatch.** No host route invokes the assembly-internal positive executor. The public attempt port is intentionally non-writing.

The fixture's exact `.wq10-disposable-fixture` marker and private physical temp Git repositories are necessary but insufficient for authority. The account step additionally verifies three independent pinned *fixture* ECDSA trust keys for protocol, old-writer/source-journal fence, and operator approval. These keys are generated only in tests; they are not production trust anchors. Original signed roster includes both project identities, authority digests, epochs, Frozen Git heads, WQ02 versions and high-water values. The coordinator checks the independently seeded project journal IDs and source-effect high-water against the signed account checkpoints **before** the account transaction, then delegates the original UUID/hash and immutable-history checks to each existing project CAS writer.

## Execution and replay

1. Validate exact one-account/two-project roster, independent authorities, original UUIDs, epochs, checkpoint heads, and original journal identities. Wrong actor signatures or markers cannot authorize a write.
2. Execute **original account Preparing CAS**, never synthesizing a second account state. Read the existing WQ07 original UUID receipt on retry; retain its signed request hash and original outcome.
3. Verify account Preparing phase, zero held tokens, and the unchanged observed account head/version before each project. Project A and project B each commit their own original WQ02 Prepared receipt, inert blocked queue, conversion graph, and project operation receipt in that project's CAS.
4. Re-read actual Git authorities. If any step fails, preserve previous commits and report the account head, each project head/status and original operation IDs. An uncertain acknowledgment is investigated by replaying the **same** original UUID, not by blindly minting a new one.

A **PreparedInert** project is not runnable: WQ02 stages are Blocked with a non-clear gate, no launches, no token release, and `RuntimeAuthorized=false`. Even when both projects are PreparedInert, `AllProjectsInertPrepared` is only a fixture observation, **not** a production migration or activation verdict.

## Partial-recovery example

If A prepares and the second clone changes B's original Git head before B's CAS, A remains PreparedInert, B remains unprepared, and the account remains Preparing. The coordinator reports the actual account/A/B Git heads and leaves A's unchanged Agent, Manager, task, review, source, operation and journal blobs intact. The same original UUID can replay A idempotently while B's stale expected Frozen head still fails. **Never force-reset either project or paper over B's new source head.** The operator must examine the original competing write, signed roster, old-writer evidence, source-journal high-water and exact observed heads, then explicitly approve any repair/re-plan. A changed account head after A is surfaced as AccountHeadDiverged, not as success.

## Production NO-GO boundary

This rehearsal does not establish real externally authenticated protocol/fence/operator witnesses, complete legacy writer-fence high-water, source-role/permission authority, an injected external host bridge, verified public route, or post-publication activation evidence. The fixture entrypoint remains `internal`; public migration remains disabled. No Scheduler, Manager, legacy Agent retirement cutover, deploy, hosted GitHub Actions or Codex is part of this proof.

Local test command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueMultiLedgerPreparedRecoveryTests`. Use two-clone negative tests and adjacent account/project Preparing tests before reviewing an exact frozen source commit.
