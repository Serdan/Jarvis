# Independent review — changes requested

**Review:** `bc0aeacd36df40b591f53c44d3b9a4fd`  
**Task:** `agent-next-response-alerts` (Worker 3)  
**Frozen commit:** `29bb8f5bcc352b8fd43bc7f7f7efbc9d72b0b4c0`  
**Decision:** Request changes — one P1 blocking finding.

## Blocking defect

- [P1: Completed GetJobResult drops and consumes new operator alerts](findings/completed-job-poll-drops-alert.md). When an alert is queued after a tool job has finished but before its result is polled, the client consumes the alert, then the server substitutes the original `ToolResponse` and drops the poll-time instructions. A STOP is silently lost. Fix the evidence merge and add the missing completed-job polling regression test before the next frozen review.

## Scope and evidence

- Inspected the submitted base `a709027e9cf97d0dde0518887e6bd61a4c77800b` → frozen head `29bb8f5bcc352b8fd43bc7f7f7efbc9d72b0b4c0` diff (9 files, 445 additions / 8 deletions), including ledger queue, response injection, MCP adapter, and tests.
- Positive design: project/agent-keyed durable bounded queue, UTF-8 validation, CAS consumption, STOP priority, preservation of standard guidance in the ordinary path, and exact target isolation are present.
- Independently ran **.NET 11 Release** focused Client.Tests response-alert cases: **5 passed, 0 failed**.
- Independently ran **.NET 11 Release** Server.Tests `operator_alert` case: **1 passed, 0 failed**.
- `git diff --check` on the frozen range passed.
- Worker 3's initial report states full Server.Tests 133/133 passed and full client suite was running; this independent review did not verify the full suite.
- Existing tests cover errored `GetJobResult` (missing job) but omit a completed tool job with a non-null `ToolResponse`, the failing branch.

No integration, merge, source modification, or deployment was performed.