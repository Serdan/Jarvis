# Independent review — Accepted

## Exact source and scope

- Review: `7f2c842fedb74164a181cfd353cd0c1e` (`_reviews`), version 2.
- Reviewed commit: `1e1ed907e5d0cbf7e5eef325fc563c412342aaae`.
- Source: `refs/heads/fix/native-ingress-contract-w17-20261127`.
- Base/target: `914907d14803ddf861e438419147903dcbf8f7f7` / `refs/heads/main`.
- Canonical worktree inspection independently confirmed the exact reviewed HEAD and a clean tree.
- Full composition `914907d..1e1ed907`: five commits ahead, fourteen files changed. The prior implementation/composition is outside this correction's scope and remains subject to its existing W17 acceptance and Integrator qualification.
- Adjudicated narrow delta `9c008dd..1e1ed907`: two commits ahead, exactly one file changed: `tests/AutomationChallengeWake20261122/NativeIngressTests.fs` (+82/-22). No Program, SignalR, runtime, server, or other production source changes occur in this delta. The final commit `1e1ed907` itself only adds explicit `string` annotations to the two local helper signatures.

## Findings

No source findings in the adjudicated narrow correction.

The revised `registrationPresent` guard checks the actual function spans instead of comparing global lexical declaration positions. It verifies:
- `registerClientRuntime` invokes concrete `prepareConnection()` before default-off host registration and callback composition;
- manual-granted, original-operation, and ordinary authenticated command routes all use the effective `onChallengeCommit`;
- the composite callback delegates to `jobWake.OnChallengeCommit`;
- shutdown drains dispatch before disposing job ingress and stopping the downstream challenge host/scheduler;
- the `runClient` caller's concrete preparation performs synchronous OAuth login, builds the authenticated SignalR connection, passes that preparation into `registerClientRuntime`, and calls `registration.StopAsync()`.

The targeted mutants are non-vacuous: the `replace` helper asserts that each replacement actually changes the source, while `reject` requires the topology predicate to become false. Coverage includes missing/reordered preparation, bypassing the concrete caller preparation, OAuth removal, each callback route, composite delegation, both shutdown paths, and reversed shutdown order. This avoids the previous false assumption that declaration order proves invocation order.

## Test and evidence boundary

The canonical attempt checkpoints record the exact source SHA and named Release TRX evidence:
- cp20292: ChallengeWake suite, 13 passed / 0 failed / 0 skipped, exit 0, no timeout.
- cp20320: actual-registration suite, 1 passed / 0 failed / 0 skipped, exit 0, no timeout.
- The worker's review summary reports the terminal-wake suite as 16 passed / 0 failed / 0 skipped, exit 0, no timeout, with its named TRX.

These execution results are owner-reported checkpoint/summary evidence, not executions performed by this reviewer. The original ChallengeWake result handle had expired from client memory; the terminal-wake handle is owned by the initiating Worker 17 and cannot be read as Reviewer 9. I did not rerun either job. The exact source commit and clean worktree were independently verified, and the source/test contract was independently inspected.

The task spec cites adjudication cp19955. Its operative NARROW requirements are fully reproduced in the canonical task spec and were used for this review; I could not retrieve a separate cp19955 checkpoint using the current correction task or the original cross-device task references. This is recorded as a provenance limitation, not as a source defect. Prior W17 acceptance and Integrator qualification are not re-adjudicated here.

## Verdict

Accepted for the exact frozen source SHA `1e1ed907e5d0cbf7e5eef325fc563c412342aaae`, limited to the adjudicated test-only correction. No publication, target advancement, deployment, scheduler activation, or runtime enablement is authorized or implied by this verdict. Lead/Integrator retain the specified publication and exact-source composition gates.
