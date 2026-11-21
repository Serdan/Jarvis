# Independent review — native account Worker/Reviewer CAS

**Verdict: ChangesRequested** on frozen `5e37133d5f1b7e7c6e13c890b662caf14eafa9c8`, review `6f8017dc4d53457b8beea2e4a2fd916d`, task `scheduler-native-account-reservation-cas-20261121`. No main edit, integration, browser/provider send, deployment or schedule activation was performed.

## Exact provenance and independently executed checks

- Original Git parent verified with `git rev-parse HEAD^`: `a422338e64b82026d25b28e6b63287f945b8bb73`. Source HEAD independently verified before and after tests as `5e37133d5f1b7e7c6e13c890b662caf14eafa9c8`; worktree `scheduler-native-account-cas-w4-20261121` remained clean. Frozen diff: `Client/Modules/NativeSchedulerAccountReservation.fs`, `Client/Modules/AccountCapacityLedger.fs`, `Client/Client.fsproj`, `Client.Tests/NativeSchedulerRosterPolicyTests.fs` only.
- `dotnet --version`: **11.0.100**. Independent command `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~NativeSchedulerAccountReservationTests --logger console;verbosity=normal`: **exit 0, 9/9 passed**, real disposable two-clone Git remotes, cap-one CAS, UUID replay, changed payload, quota zero, lost ACK, Unknown occupancy, unsigned native-state mutation, WQ07 Held and production default-off.
- Second independent command with `--filter FullyQualifiedName~NativeSchedulerRosterPolicyTests`: **exit 0, 17/17 passed** (the same nine plus eight roster/security inherited cases), including forged local project replacement, signed ancestry transplant, duplicate/foreign project roster, out-of-band Git rewrite, lost push ACK and two-client version conflict. The 17 total contains the earlier nine; it is **not** 26 distinct cases. No test timeouts, source edits, or unverified cross-host performance claims.

## What the implementation does establish

It uses the pre-existing account Git CAS head and immutable request-hashed receipt transport rather than a second ref; keeps WQ07 state semantics, migration and Held coexistence fenced; counts `Reserved/Dispatching/Submitted/Uncertain/Active/Unknown` as one occupied record; enforces Worker/Reviewer account and project quota zero; rejects duplicate canonical identity under a new dispatch UUID; and explicitly rejects public production reservation/read when host/session authority is absent. The two-client, lost-ACK and unsigned-state tests are legitimate source-bound regressions.

## Task-blocking findings (details in separate canonical documents)

1. **P0 — `findings/unauthenticated-canonical-candidate.md`:** candidate fields are shape-checked but not authenticated to a same-head project task/attempt/source lease or immutable Reviewer claim. Existing happy-path fixtures themselves use arbitrary purported IDs and 64-hex hashes. Original source-provenance/claim requirement is not implemented; immutable receipts do not supply that missing authority.
2. **P1 — `findings/unbound-terminal-proof.md`:** terminal OIDs are syntax-checked, not tied to current verified project head or recorded project fence; the passing Unknown release fixture returns an unrelated **account** commit OID as project and provider terminal proof. A fixture oracle returning Ok does not prove stale/unrelated terminal rejection.
3. **P1 — `findings/empty-terminal-id-poisons-state.md`:** released records require a nonempty terminal GUID on read, but release can commit `Guid.Empty` if the verifier returns matching proof; a valid pre-write document becomes unreadable after commit. Add pre-CAS rejection and a no-head-change negative.

These are source/acceptance findings within the original task contract, not a request to activate native production admission, invent a new credential/issuer root, or implement live browser sends. The current production default-off boundary meaningfully contains active exploitability; reviewer verdict does **not** claim a live service compromise. Suggested remedies are candidates for independent task-scope adjudication, not authority to unilaterally expand work.

**Decision rationale:** passing current fixtures does not close original requirements to authenticate canonical candidate provenance and prove terminal release/negative invariants. Request source-bound repairs and negative tests on a new frozen SHA, with fresh independent review.