# Review findings — native ingress qualification test contract

## Disposition

No actionable defect was found in the scoped correction. The change fixes an inherited false-red source-topology predicate and preserves the previously accepted production composition. This verdict applies only to the exact immutable source SHA below; it does not authorize integration, deployment, activation, or a scheduler policy change.

## Frozen source and scope

- Task: `automation-w17-native-ingress-test-contract-correction-20261127`
- Review request: `5b9c9679c45346c6bfb8f0dfcbed0778`
- Exact reviewed head: `1e1ed907e5d0cbf7e5eef325fc563c412342aaae`
- Recorded base / target: `914907d14803ddf861e438419147903dcbf8f7f7` / `refs/heads/main`
- Source ref: `refs/heads/fix/native-ingress-contract-w17-20261127`
- Registered source worktree: `w17-native-ingress-correction`; observed clean at the exact reviewed head.
- The narrow comparison `9c008ddf4d0d2081c267d3f1c035d31d163d4829..1e1ed907e5d0cbf7e5eef325fc563c412342aaae` changes only `tests/AutomationChallengeWake20261122/NativeIngressTests.fs` (+82/-22). The final commit adds explicit string parameter types to the local NUnit mutation helpers. No production, SignalR, server, or other test source is changed by this correction.
- The full composed branch is five commits ahead of the recorded base. Its production composition is the exact previously accepted W17 candidate `9c008ddf4d0d2081c267d3f1c035d31d163d4829`; the original independent review `c125cc75812a49e3bf3109bd4f362e36` remains Accepted (decision sequence 19868). This review does not replace or broaden that earlier verdict.

## Adjudicated requirement

Read the complete Adjudicator 2 checkpoint `cp19955` from task `automation-w17-inherited-native-ingress-test-contract-adjudication-20261127` (sequence 19955). It records a NARROW, test-only authorization: the old predicate compared the global lexical positions of `OAuth.login` and the `challengeHost` declaration. Because `registerClientRuntime` is declared before its caller `runClient`, the predicate was false on both the W8 parent and the candidate even though the caller invokes `prepareConnection()` before host registration. This was an inherited test-contract false red, not evidence of a runtime regression. The adjudication explicitly forbids production edits, a parent execution claim, replay of the expired wrapper, and publication/activation by the worker.

## Source assessment

The revised `registrationPresent` uses bounded source spans for the callee registration function, its callback routes and shutdown record, and the caller's concrete `prepareConnection` body. It checks the required sequence within the appropriate scope:

- `prepareConnection()` is invoked before the default-off host/coordinator/registration setup;
- the native gate remains hard default-off and the coordinator/host use the existing registration seam;
- manual-granted, original-operation and ordinary authenticated routes all pass the effective `onChallengeCommit`, which delegates to the composite job-wake callback;
- dispatch drains before job ingress disposal and host shutdown, and ingress disposal precedes the downstream host;
- the caller's concrete preparation performs synchronous `OAuth.login` before building/returning the connection, passes that same preparation function to `registerClientRuntime`, and calls production `registration.StopAsync()`.

The assembly check and the existing actual-registration and B1 tests remain intact. The local negative witnesses are nonvacuous: each checks that the mutation changes the source before requiring the predicate to reject it. They cover missing/reordered preparation, caller bypass, synchronous OAuth removal, each callback route individually, composite delegation removal, host/caller shutdown removal, reversing drain/host-stop order, and moving job-ingress disposal after the downstream host. The guard remains source-topology evidence, not runtime instrumentation; that boundary is explicit and appropriate to the adjudicated scope.

## Exact-source qualification

The three named Release TRX files were read from the exact clean registered worktree and their XML counters and test results parsed directly:

| Gate | Exact TRX | Result |
|---|---|---:|
| ChallengeWake / B1 | `w17-native-contract-1e1ed907-AutomationChallengeWake20261122.trx` | 13/13 passed |
| Actual registration | `w17-native-contract-1e1ed907-AutomationActualRegistration20261127.trx` | 1/1 passed |
| Job terminal wake | `w17-native-contract-1e1ed907-AutomationJobTerminalWake20261126.trx` | 16/16 passed |

The ChallengeWake TRX includes `CompiledProgramOwnsCallbackRegistrationAndShutdownOrder`, `RealPostJournalAuthenticatedSignalRCoreTwoClientsAndReplay`, and `DisabledNativeIngressDoesNotReadOrWakeAfterCommit`. The actual-registration TRX includes `actual_registered_routes_preserve_legacy_and_compiled_off_native_authority`. The JobTerminalWake TRX includes `ProgramRegistersTheSameDefaultOffRuntime` and the hosted authenticated wake cases. Each TRX reports Completed, all tests executed, zero failures, zero errors, zero timeouts and zero skips.

Immutable owner checkpoint `cp20357` records the three original engineering handles as terminal, exit 0, not timed out, and bound to the exact full SHA above; it records the corresponding TRX names and counts. The TRX files and current exact-head/clean-worktree observation were independently inspected here. The original Worker 17 job handles were not retrievable from the Reviewer 7 job-result context (the job service returned NotFound for one handle), so the process exit codes are supported by that immutable owner checkpoint rather than a fresh raw job-result retrieval by this reviewer. No tests were rerun, and no parent execution is claimed.

These are the three scoped qualification gates, not a full repository suite. The previously accepted review's 35/35 focused composition evidence remains separate historical evidence; it is not represented as a new run at this SHA.

## Preserved limitations and authority

The existing accepted review's limits remain: the native host is compiled default-off; the actual-registration gate demonstrates registered receiver wiring and OFF nonactivation, not enabled production scheduler/native launch or the complete actual registered CAS producer path. The correction does not change those semantics or claim those capabilities. No source was edited, no merge or target advance was performed, and no publication, deployment, scheduler activation, or policy change is authorized by this review.

## Findings

No new defect attributable to this correction. Accept as the exact, narrowly adjudicated test-contract correction only.
