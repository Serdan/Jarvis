# Automation Stage B2 disposable account/project/terminal probe — Worker 16 — 2026-11-22

## Scope and disposition

**B2 HOLD.** The only permitted work is an offline, test-only probe on frozen accepted-only Integrator source `d312f1cc83685e781ce69e57ad0d4bf613bfb843`, in `test/automation-stageb2-disposable-worker16-20261122`. This document and `tests/AutomationStageB2Disposable20261122/**` are the entire leased change set. No production module, existing test, shared .fsproj, live provider, BrowserBridge send, server, main, original Git publisher, schedule, deployment or cutover is touched.

Worker 23's independently submitted test-only port probe `b9a3c223de9164fa827a4bac6b4a3e09e2d722fd` reports genuine default-off Health/Challenges/scheduler read and control paths but no production B2 provider-private host and no authenticated provider terminal. This test does not borrow its review acceptance as production permission.

## Source-exact RED: a genuinely incompatible canonical attempt phase

The native account reservation source `Client/Modules/NativeSchedulerAccountReservation.fs`, `candidateFromVerifiedProject`, tests a genuine canonical Worker `ReviewAttempt.Phase = "Started"` at an independently verified project Git head; it also requires an exact source lease, signed complete roster, and account CAS. The B1 fake project claim `Client/Modules/NativeProjectEffectFence.fs`, `checkCandidate`, requires its canonical Worker `CanonicalAttempt.Phase = "Ready"` in the project Git head used to claim. The same original attempt cannot have both phases at one immutable project head. Rewriting its phase merely to make a harness pass would violate source/lease and lifecycle provenance.

An intentional positive bridge assertion was run RED-first against the **actual B1 module** compiled with `STAGE_B_DISPOSABLE_TEST_ONLY` on a disposable local bare Git ref containing a canonical `Started` attempt. Managed .NET 11 Release job `c3a5cae01a964da49b432b5bc427e5c9` exited **1**, TRX `tests/AutomationStageB2Disposable20261122/TestResults/w16-b2-red.trx`: **1 failed, 0 passed, 0 skipped** (failure `StartedAccountCandidateMustBridgeToB1WithoutRewritingCanonicalLease`). The test source is retained as an explicit opt-in RED, not silently rewritten into a positive pass or presented as actual native account CAS proof.

An earlier syntactically invalid test build `72d232e19dc647a6959f824a06b3bd40` also exited 1 but was merely a test authoring error and is NOT B2 evidence.

## Additional typed and authority mismatches

1. The actual `NativeSchedulerAccountReservation.reserveVerifiedDisposable` is **internal** and requires `AccountBinding`, verified signed roster, complete coverage and `NativeDispatchCandidate`; its public `reserve` is deliberately always disabled. Production Client exposes no authenticated host/session issuer.
2. B1 `PreSendClaim.Account` is `FakeAccountReservationReceipt` carrying literal marker `STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1`, not the native signed account CAS receipt or `NativeReservationRecord`. B1 `seedFakeReservation` and `releaseFakeReservation` use `LedgerStore.LedgerBinding` and a separate fake document `records/native-stageb-fake-account/*`, **not** the genuine `AccountCapacityLedger.AccountBinding` native occupancy document. Forging the fake marker from an account CAS is not receipt translation.
3. Genuine account `releaseVerifiedDisposable` requires independent `INativeDisposableTerminalVerifier` plus exact `NativeProviderSessionTerminal` operation on the original project branch, matching session, effect, project fence ancestor and provider terminal blob OIDs. B1 `recordFakeTerminal` writes `NativeStageBTerminalTestOnly` with an untrusted injected `FakeProviderTerminal`; the two receipts are not equivalent and cannot release the native slot.
4. `NativeProjectEffectFence.fs` is excluded from `Client.fsproj` and guarded by `#if STAGE_B_DISPOSABLE_TEST_ONLY`; the fake module must remain in the standalone test project. Worker 23 confirmed production `Client` DLL cannot resolve that module (FS0039). `WorkQueueNativePrivateHostBridge.inspectProduction true` reports missing provider bootstrap, authenticated provider binding and credential-authenticated first connect.
5. Account CAS won/project CAS lost must remain **occupied** until an original native terminal; neither closed tab, mock exception, Unknown, foreign/late terminal nor an unverified B1 fake terminal may release native account capacity. This full bridge cannot currently be invoked without violating an existing type or lease invariant.

## Test matrix and evidence boundary

| Scope | Test purpose | Authority |
| --- | --- | --- |
| Account accepted source | Signed full roster, exact source lease, two independent Git account clients, Worker/Reviewer cap, original UUID replay, Unknown occupancy, verified terminal release | Available in existing `Client.Tests/NativeSchedulerRosterPolicyTests.fs`; **not a composed B2 receipt** |
| New B2 RED | Original canonical `Started` attempt tries real B1 claim; requires immediate fail rather than a fabricated green | Genuine B1 method on disposable project Git; account receipt is **fake only**, not a real native CAS |
| New B1 positive/negative | `Ready` canonical attempt, exact original UUID replay, altered request and competing UUID, persisted dispatch before fake port, only one fake send, Unknown/foreign/late terminal denied, cross-client terminal readback | `STAGE_B_DISPOSABLE_TEST_ONLY` fake provider, no browser or production host |
| Production account entrypoint | Public `reserve` must fail closed without a trusted issuer | Source-callable default-off guard, **not** positive authentication |
| Required B2 end-to-end | Genuine account CAS -> B1 claim -> provider terminal -> native once-only release, with cap-one cross-project, late/revoked/restart negatives | **UNSUPPORTED / RED** because phases, account receipt type, trust root and terminal operation do not align |

## Smallest follow-up contract (proposal, not implemented)

Owner: Lead to assign to authorized Client account/StageB bridge and host protocol work, outside this tests/docs lease. Define one explicit **test-only typed cross-ledger reservation-to-project-effect binding** whose input is the *actual* account CAS original operation ID/receipt, immutable `NativeReservationRecord`, signed roster/coverage and current verified project attempt/source head. The binding must agree on one legal attempt phase without mutating canonical history, and issue an auditable original project fence in the account native release protocol's exact lineage. Its terminal result must be independently authenticated to the native `NativeProviderSessionTerminal` operation and provider terminal blob; the adapter must not accept `FakeAccountReservationReceipt` as authentic. Until those guards can be satisfied and independently reviewed, production B2 remains HOLD and any standalone B1 green only proves disposable fake-only behavior.

## Reproduction

From the isolated source worktree with .NET SDK 11.0.100:

```sh
dotnet test tests/AutomationStageB2Disposable20261122/AutomationStageB2Disposable20261122.fsproj -c Release --logger 'trx;LogFileName=w16-b1-verified.trx' --results-directory tests/AutomationStageB2Disposable20261122/TestResults
```

The explicitly selected expected RED can be reproduced separately using the named `StartedAccountCandidateMustBridgeToB1WithoutRewritingCanonicalLease` NUnit test. Do not count it as a passing B2 case.

**No full unfiltered Client/Server Release, live B2, or real browser/provider send is claimed.**

## Final source-bound terminal result (verified)

Final managed job `2ac75d9ec9244ebdbf4fd181d860e05b` completed with **exit 0** on SDK `11.0.100`, from the clean accepted-only base plus only this new test project and documentation. TRX: `tests/AutomationStageB2Disposable20261122/TestResults/w16-b2-native-account-and-b1.trx`: **23 discovered / 22 executed and passed / 0 executed failures / 1 intentional `Explicit` B2 RED excluded from this non-explicit run**. This includes **19 existing unchanged native signed-roster/account-reservation tests**, linked by source file into the test-only assembly `Client.Tests` to access the established internal disposable route, plus **3 new** B1/default-off tests; the intentional B2 RED is NUnit `Explicit` and was not executed in this non-explicit run. Its separate source-bound job `c3a5cae01a964da49b432b5bc427e5c9` exited **1** with **1 failed / 0 passed** (see above); it is not counted as green. Earlier focused B1-only job `0c6b030b9184423abf0e777c1474724b` completed exit 0: 3/3 pass, TRX `w16-b1-verified.trx`.

Exact source files compiled/added: new `tests/AutomationStageB2Disposable20261122/AutomationStageB2Disposable20261122.fsproj`, new `StageB2Tests.fs`, new `StageB1Tests.fs`, original read-only `Client.Tests/NativeSchedulerRosterPolicyTests.fs`, original `Client/Modules/NativeProjectEffectFence.fs` under disposable conditional, and original accepted `Client/Client.fsproj` reference. No change to either original test or original production file.

Verified base native/account source Git blob OIDs: `Client/Modules/NativeSchedulerAccountReservation.fs` `fd9c9591c74ce906e15dfc191c2dbac0995b8dc3`; `AccountCapacityLedger.fs` `8f4f3978ce599d0a3c5a411952aa621ee8f24430`; `ChallengeWorkflow.fs` `981011513c2e7636d9318e907987d4d2232e3fda`; `NativeHealth.fs` `4753293d7da5379db18ade6f03e40ac34fb3d58b`; `WorkQueueReactiveWake.fs` `87f81d557b2f7ed00efccb693b9c08a78e3cb0e1`; `WorkQueueSchedulerHeadBinding.fs` `6218666e1f5089773afbe567781b71b2d6c60124`; `WorkQueueSchedulerHostLoop.fs` `800a7388dc301a1560de0aa22f07f5a1d9fbad59`; `WorkQueueNativePrivateHostBridge.fs` `9a2b51f261da7c1a42e455fcbe78bfb0642f61c8`. These are verified `git ls-tree` blobs of frozen base `d312f1cc83685e781ce69e57ad0d4bf613bfb843`.

**Interpretation:** Native account CAS is tested on its own in authentic local disposable signed-roster Git fixtures, and B1 fake dispatch is tested independently. The production B2 host, native account-to-B1 authenticated proof mapping and once-only native release across that mapping are **not demonstrated**. B2 remains HOLD.
