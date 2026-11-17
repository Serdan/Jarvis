# Reviewer 4 — independent frozen WQ12 review

**Frozen snapshot:** `5e642f7d7282f6b44665820ebfa407a59c595152`, base `0e9a8d4ea958090c94af11952329565358fdd79b`.

**Scope:** one test-only change in `Client.Tests/WorkQueueDurableLaunchIntentTests.fs` (46 insertions, 9 deletions). No WQ04/WQ07/WQ08/WQ12 production logic, default-off gate, Scheduler, or deployment changes.

**Contract check:** The composition-held prerequisite causes `WorkQueueRoleAuthority.selectPracticalDemand` to have no winner. WQ12 `freshSelection` (currently `WorkQueueDurableLaunchIntent.fs:277-310`) requires the complete trusted project set and a verified global WQ08 source-origin winner; with no winner, its fail-closed diagnostic is `No verified source-origin planning Work wins global blocking-first FIFO.` The old assertion expecting a later composition-CAS diagnostic did not represent this earlier rejection. Updating the assertion does not weaken admission.

**Negative-path evidence:** The test now asserts WQ04 role demand is absent; two independent client/account clones concurrently prepare then replay and each receives the no-winner rejection; incomplete, duplicate and forged project membership are denied; WQ07 tokens remain empty in both account views; no WQ04 launch is recorded; the original project ledger sequence does not advance; no WQ12 durable original exists. Test remains hermetic with disposable Git repositories.

**Validation:** Targeted .NET 11 Release run completed successfully (Jarvis job `67aa6b00995440dc8696a670fc95d438`, exit 0). Exact frozen diff passes `git diff --check`. Full `Client.Tests -c Release --no-restore` gate is independently in progress (job `30ac57c43a5740199e48752203de2329`); **not represented as passed**. The worker/Lead must reconcile and record that gate before integration. No live run, Scheduler activation, or cutover is authorized.

**Decision:** Accept the exact frozen test correction; no blocking code-review findings. Full-suite outcome remains an integration prerequisite, not an inference from review approval.