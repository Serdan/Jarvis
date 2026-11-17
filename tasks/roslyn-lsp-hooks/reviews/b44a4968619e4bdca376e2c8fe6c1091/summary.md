# Independent frozen review — Roslyn inbox prewarm corrective slice

**Verdict: Accepted for the narrow corrective slice only.**  
**Exact reviewed source:** `b44a4968619eb3648ee708191e1948c256479e47`  
**Parent / previously accepted P2 source:** `5564d6c58e16b48ab9bbe1ccad6c35b5e704c0de`  
**Review:** `b44a4968619e4bdca376e2c8fe6c1091`; author Worker 6; reviewer Reviewer 5.

## Source inspection

1. `Client/SignalR/Client.fs:25-44,834-855`: `TryScheduleIfCandidate` uses the existing off-response-thread `TrySchedule` path; a negative project fast gate now returns `false` from its delegate rather than caching an unsuccessful prewarm as completed. `TrySchedule` removes that key after a negative/error outcome, allowing a subsequent same-assignment inbox read to retry; positive in-flight and successful attempts remain deduplicated.
2. `Client/SignalR/Client.fs:105-129`: `prewarmFromInbox` still resolves the assignment's source ref through a unique registered branch checkout and checks the registered source path against the discovered checkout, returning `false` on missing/ambiguous/mismatched resolution.
3. `Client/Modules/CodeQualityHooks.fs:350-369`: the *selected checkout* must independently load enabled Roslyn hook configuration and pass `requireApproval` before `RoslynLsp.prewarm` can be scheduled. The project-scoped sibling gate is deliberately only a negative filter; approval for one linked checkout does not approve another.
4. `Client.Tests/SignalRDispatchTests.fs:640-667`: same assignment moves from ineligible to eligible, then verifies deduplication after successful scheduling. `Client.Tests/CodeQualityHooksTests.fs:242-270`: explicitly approved sibling versus denied selected checkout, followed by selected checkout eligibility after its own approval.
5. `scripts/roslyn-wayfold-readonly.fsx`: source files are read from the provided Wayfold checkout; transformed text, hook writes, and `dotnet format whitespace` operate on disposable copies. The accompanying document reports small/large sample parity, including the final-newline difference in raw LSP output and matching full-hook copies; this is appropriately labeled as host-contended, non-isolated evidence.

## Evidence boundary and nonblocking follow-up

- This review independently inspected the exact frozen GitHub commit and its delta. **I did not execute .NET tests, start Roslyn, or touch Wayfold.** The focused Release .NET 11 **61 passed, 0 skipped, 0 failed** count is the author's documented execution claim, not independently reproduced here.
- The parity script *prints* `HOOK_PARITY identical=` / `PARITY identical=` values but does not itself fail if a comparison is false. It is usable as a measurement harness, not yet a hard parity regression assertion; add fail-fast assertions before relying on it as a formal acceptance gate.
- `CodeQualityHooks.tryPrewarm` returns success after scheduling the asynchronous `RoslynLsp.prewarm`, not after LSP initialization. A transient start failure after a successful eligibility gate is handled by session recovery on later formatting, but not by this same-assignment dispatch retry. Do not describe this as end-to-end confirmed warm readiness.
- Unfiltered Client.Tests verdict, actual TaskRead -> AgentInbox -> registered checkout -> prewarm integration, isolated two-worktree project/analyzer configuration, attributable daemon/launcher RSS, and full P1 scope remain open. No merge, deployment, or full-task acceptance follows from this decision.

**Disposition:** No blocking flaw found in the one-commit P2 corrective delta. Accept this intermediate review only; carry the evidence gaps into the existing full-task P1 review.