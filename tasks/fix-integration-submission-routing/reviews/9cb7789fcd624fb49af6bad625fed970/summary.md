# Review decision — Accept

Exact source snapshot: `f6ba73d3dce8192671ab90c1581d2323576069b6`, incremental base `8442b6602c18e3eb9c26875e59461a380b923ae3`.

Verified the original defect: `submitIntegration` expects `(sourceRef, submittedCommit, reviewId, targetRef)`, but `TaskApi.write` previously passed `(sourceRef, submittedCommit, cmd.TargetRef, cmd.Reason)`. The fix now maps `cmd.Reason` to `reviewId` and `cmd.TargetRef` to `targetRef`, forwarding them in the correct order. The new focused regression checks an explicit review ID and the omitted-ID case. No other routing semantics change; existing ledger validation remains intact. No blocking findings.

Test evidence: focused client routing regression passed; Server.Tests command exited 0. Combined full suite did not pass: two environment-sensitive client tests failed (`startJob` environment redaction timing, selected-worktree active-job guard), and a filtered client rerun timed out. `git diff --check` clean. This limitation must be reported accurately and full suite should be rerun after integration in an uncontended environment.

Decision: Accept. Integrate exact commit with normal Git merge, then deploy only with explicit operator approval. Production client will need this update before integration submission ledger bookkeeping can work.