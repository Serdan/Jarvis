# Independent review — WQ08 cross-project read-only host

**Source:** frozen `5218ff0c1dd6c66698008c9a5efe86f8bc815489` versus `b277cd0d1b850b97ad81d889e2278aaef74e74ae`.

**Verdict: ChangesRequested.** Two authority-completeness issues affect the correctness of the global preview. See `findings/account-authority-positive.md` (P1: simulated winner persists after a contradictory held account/project authority or missing held launch) and `findings/released-historical-token.md` (P2: settled historical tokens from retired projects incorrectly suppress all selection). Add negative-space tests for both. Keep the global projection read-only, default-off, and separate from runnable launch approval.

**Positive evidence:** source diff inspected across host adapter, planner, CLI entrypoint and runbook. The CLI only exposes `scheduler preview-global`; `CanDispatch` is constant `false`; prospective candidate launch/browser IDs stay absent. Valid two-project, blocking-first, cap-full, missing source/head and unknown original-browser-send fixture paths are present. Independently ran `.NET 11 Release` tests on frozen worker worktree: `FullyQualifiedName~WorkQueueCrossProject` **29/29 passed**, `FullyQualifiedName~WorkQueueSchedulerHostPreview` **12/12 passed**; combined **41 passed, 0 failed**. Compiler warnings originated in existing unrelated files. Passing existing tests do not cover the two identified authority cases.

**Integration recommendation:** do not integrate this frozen commit until the two findings are resolved and a new exact-SHA review is submitted.