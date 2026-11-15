# Independent WQ-06 source findings — Reviewer 3

**Severity:** No blocking defects observed in the isolated transport. The following conditions are **release/integration blockers**, not claims that the reviewed inert source is defective.

## I-01 — WQ-04 authoritative producer/high-water dependency (integration gate)
- `Client/Modules/WorkQueueConversationTransport.fs:39-43,113-116,153-165`: `RoleWorkFromProducer` is a caller-supplied WorkStage and message display order is message-ID order, not an authoritative event/FIFO cursor. WQ-06 does not claim to implement WQ-04's producer/index source of truth.
- `WorkQueueConversationTransport.fs:74-82,116-147,322-337,342-381`: read/prepare/complete check WQ-01's existing covered flags and hard-ID presence. This is fail-closed for flagged gaps, but the flags alone are **not proof** that future WQ-04 authoritative source-event/high-water replay and versioned role demand are complete.
- Required: WQ-04 independent Accepted + integrated exact source; then reconcile WQ-06's post/read/claim adapter with real high-water and role producer, assert exact source cursor/provenance and FIFO order, and submit a **new combined task-bound frozen review** before canonical integration or any live route.

## I-02 — No production route, security or operational acceptance (activation gate)
- `WorkQueueConversationTransport.fs:11-14` explicitly makes the module inert, and changed-file audit confirms no server handler, Scheduler, live operator API, or production deployment wiring.
- `post` accepts an actor string and prepared reply wrapper delegates to WQ-02 project CAS. Production authorization of operator Challenge dependencies, task/role identity, role-producer authority, Scheduler dispatch, stalled-session requeue and source/host safety must be tested at the eventual real routes; this frozen review does **not** certify them.

## Correctness evidence for the scoped implementation
- `readThreadAt` (`116-151`) pins one verified project snapshot and validates Handled reply against immutable same-snapshot record (schema, message, project, subject, generation and canonical JSON); it does not consume/claim on read.
- `claimAncillary` (`167-205`) recovers original immutable operation origin, exact request hash, project version and original receipt, rather than selecting a later same-launch generation from moving HEAD.
- `primaryMessageClaim` (`209-232`) identifies exactly one primary Role-message work/generation. `activeHandler` (`234-288`) gates primary/ancillary lease identity, registered non-STOP live launch, active primary claim, hard-edge satisfaction and correct work/message generation before preparing replies.
- `prepareReply` (`301-337`) appends only an immutable object, not a Handled side effect; duplicate reply ID/content conflict is rejected. `completePrepared` (`342-381`) ensures the original prepared record matches lease and uses one existing CAS mutation to handle reply, terminalize dedicated primary Role work or complete ancillary work. Unexpected post-publication replies recover by original operation, not a synthetic second answer.
- `resolveGatedAncillary` (`383-390`) delegates to WQ-01's graph CAS without releasing the separate primary Plan.
- Independently executed .NET11 Release tests: WQ-06 focused **4/4**, Client WorkQueue **29/29**, Server WorkQueue **88/88**; clean unchanged worktree and exact direct parent `8ed9671543f05073f7d453728b50772138dd87f2`.

**Recommendation:** Accept exact inert SHA `371fdb636f694c60c69746f15786603dc697a947` for this scoped independent review; keep the original WQ-06 canonical integration, combined-review and production gates closed pending I-01/I-02.