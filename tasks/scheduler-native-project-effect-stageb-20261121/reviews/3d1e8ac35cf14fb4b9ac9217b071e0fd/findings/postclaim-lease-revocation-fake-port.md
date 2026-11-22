# P1 — Post-claim canonical lease revocation does not fence the subsequent fake-port dispatch

**Frozen reviewed source:** `67e2ed9d80607daa3dc87157d535717454e817d2` (Worker 22 StageB B1 r1 correction), based on `dcac884fc061869572b12a03befc12f11c009f2f`. **Task:** `scheduler-native-project-effect-stageb-20261121`, GO B1 and Negative proof ("stale head/lease/review", "conflict before effect", preserve original Worker attempt generation or Reviewer frozen claim/version and assignment/source lease). This finding is within the original disposable/no-send boundary, not a request for B2/real account/browser wiring.

## Reproduced defect

`Client/Modules/NativeProjectEffectFence.fs:233-239` calls `checkCandidate` during **claim**, validating the then-current canonical Worker attempt or Reviewer review. However, **beginFakeEffect** (`277-281`) uses `update` (`243-273`), which reads only the previously persisted `NativeEffectRecord` and tests its candidate/effect/status/identity; it does **not** re-read the canonical task, attempt generation/source lease or claimed review status/version at the new transaction snapshot. `dispatchFakePort` (`323-334`) confirms its own durable `Dispatching` marker, then calls the injected non-idempotent fake port. That confirmation does not prove the underlying canonical lease is still valid.

**Independent source-bound RED control:** untouched author branch at `67e2ed9d...`; a temporary, separate `/tmp/jarvis-reviewer1-stageb-revocation-a9af36ec1b634ca8a27d6b70548ea7f2/NativeProjectEffectStageBProbe.fsproj` compiles the exact original StageB F# file and real `Client/Client.fsproj`/`LedgerStore.fs`; it copies the original 17 tests and adds one after-claim adversarial test, without modifying the submitted branch.

1. Create disposable bare project/account Git ledgers and clones using original `withFixture`; seed a canonical Ready Worker attempt and fake reservation.
2. Successfully `claim f.One c` at original candidate generation/source.
3. On a different clone, `LedgerStore.transact f.Two` changes the original canonical attempt JSON `phase` from `Ready` to `Cancelled` and commits/publishes it before the send attempt.
4. On a third clone, call `dispatchFakePort f.Three f.Worker c.EffectId (Guid.NewGuid()) port`, with an observable fake port returning a fake `Unknown` terminal.
5. Expected: reject stale lease and invoke port zero times. **Observed:** dispatch returned `Ok`, so `denied` raised `Unexpected acceptance; expected fail closed`; the fake port is necessarily invoked on that return path. Raw Jarvis job `dae52889b64a45feb888d82ed342998e` terminal **exit 1**, after the original 17 tests passed; failure at temp Program.fs:456. This is a deliberate RED safety oracle, not a green acceptance gate.

The same omission applies source-logically to a Reviewer whose originally claimed frozen review becomes decided/withdrawn or changes version after `claim` but before dispatch; that Reviewer scenario was **not** independently executed.

## Impact and bounded remediation

A pre-send claim made while authorized can outlive revocation of the underlying original worker/reviewer assignment. This permits the **disposable** callback to run after a canonical cancellation/lease change, violating the original stale-lease-before-effect negative boundary. It does not show a live provider invocation: the module is wrapped in `STAGE_B_DISPOSABLE_TEST_ONLY`, is absent from production Client.fsproj, and real account/provider authority remains held at B2.

Narrow the project-side **begin/dispatch authorization** to re-read and verify the canonical Worker attempt generation/source lease or Reviewer frozen claim/version/status within the same remote CAS snapshot that writes `Dispatching`. Add distinct after-claim-before-begin cancellation/generation/source and reviewer-decision negatives using real disposable Git clones, with an observable zero-call fake port. Do **not** apply a blanket current-lease gate to terminal recording/release after an uncertain or already-sent effect: completion/reconciliation must stay possible. Preserve the existing full terminal-proof intent fix, fake-account once-only release and B2 HOLD.

**Other verified evidence:** independent original frozen `.NET 11 Release` job `da60f85989864619bdd510f4e1aa3d26` exit 0, 17/17; unchanged Stage A Node job `dfe6263befda46d395835cd3c45e688d` exit 0, 33/33. Those greens do not cover this temporal revocation interleaving.