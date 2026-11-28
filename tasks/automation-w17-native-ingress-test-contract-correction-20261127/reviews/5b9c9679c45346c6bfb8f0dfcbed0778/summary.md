# Independent review — Accepted (narrow test-contract correction)

**Review:** `5b9c9679c45346c6bfb8f0dfcbed0778`  
**Task:** `automation-w17-native-ingress-test-contract-correction-20261127`  
**Reviewed immutable head:** `1e1ed907e5d0cbf7e5eef325fc563c412342aaae`  
**Base / target:** `914907d14803ddf861e438419147903dcbf8f7f7` / `refs/heads/main`

<badge>Accepted</badge>

The exact correction satisfies the NARROW adjudication in Adjudicator 2 checkpoint `cp19955`: it replaces the inherited global lexical-order assertion with function-scoped caller/callee topology checks and nonvacuous negative mutants. The narrow diff from the previously accepted candidate `9c008ddf4d0d2081c267d3f1c035d31d163d4829` changes only `tests/AutomationChallengeWake20261122/NativeIngressTests.fs`; the final helper type annotations resolve the NUnit overload compile failure. No production source changed.

The corrected predicate checks concrete synchronous OAuth preparation in the caller, invocation before native host registration in the callee, default-off coordinator/host construction, all three authenticated callback routes and composite delegation, dispatch-drain/ingress/host shutdown ordering, and the production caller's `registration.StopAsync()`. The assembly check, actual-registration suite and B1 core gates remain intact. Each targeted mutant first proves it changes the source and then must be rejected.

Exact-source Release evidence was inspected from the clean registered worktree at the reviewed SHA:

- ChallengeWake: 13/13 passed, including `CompiledProgramOwnsCallbackRegistrationAndShutdownOrder`.
- Actual registration: 1/1 passed, including `actual_registered_routes_preserve_legacy_and_compiled_off_native_authority`.
- Job terminal wake: 16/16 passed, including `ProgramRegistersTheSameDefaultOffRuntime` and hosted authenticated wake cases.

All three named TRXs report Completed, every test executed, zero failures/errors/timeouts/skips. Immutable owner checkpoint `cp20357` records terminal exit-0/no-timeout receipts for the exact SHA and those TRXs. I independently read and parsed the TRXs and verified the current worktree's exact clean head. The original Worker 17 job handles were not retrievable from the Reviewer 7 job-result context, so raw process exit status remains supported by the immutable owner checkpoint rather than a separate retrieval by this reviewer. These are scoped gates, not a full-suite result; no parent rerun is claimed.

The original W17 composition review `c125cc75812a49e3bf3109bd4f362e36` remains Accepted at sequence 19868. Its previously documented limits remain: default-off native behavior is preserved; these tests do not establish enabled production native scheduling/launch or the complete actual registered CAS producer path.

Detailed source, adjudication, test, provenance and limitation analysis: `findings/review-evidence.md`.

**Decision:** Accept this exact narrowly scoped test-contract correction. This review does not authorize publication, merge, target advancement, deployment, scheduler activation or policy change; Integrator retains publication and qualification authority.
