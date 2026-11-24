# Native Started account → project pre-send CAS (2026-11-23)

> Historical Stage A/B evidence follows. The 2026-11-24 Operator correction supersedes the separate signing/approval requirements and the old productionClaim denial. See the Stage C update at the end and the native provider session host testing record for current scope and evidence.

**Historical evidence:** the original standalone/signed-fixture requirements
below describe the prior accepted implementation, not the controlling
2026-11-24 Scheduler architecture. Host task spec v7
(`e5d5b5713698dec2e4bc74fad39988dfd512c7c8`) now requires use of the
existing authenticated client/configuration/ledger boundary, without an
additional signer or approval system. Worker 12 owns the account-side
replacement and canonical reviewer-claim verifier. Worker 16's Stage C
consumer checkpoint is documented in
[the native provider session host evidence](automation-native-provider-session-host-20261123.md).
Do not treat earlier signing-only tests as new architecture acceptance.
Existing default-off operation and unknown-outcome occupancy remain required.

## Authority boundary

Canonical task: `automation-native-account-project-ingress-terminal-20261123`, specification v2, pinned spec blob `215087bdd0d7596f2859a1b69641aa04877d23fb`. Base source `d312f1cc83685e781ce69e57ad0d4bf613bfb843` is the accepted-only native snapshot, **not** an assertion that every accepted source is on `main`.

This is a **default-OFF, standalone-compiled disposable fixture** and **not** the production scheduler or provider integration. The new `Client/AutomationNativeAccountProjectIngress.fs` is deliberately absent from `Client/Client.fsproj`; the sole compilation site is `tests/AutomationNativeAccountProjectIngress20261123/AutomationNativeAccountProjectIngress20261123.fsproj` under the friend test assembly `Client.Tests`. The public `productionClaim` rejects every input. Never add this file to the production .fsproj without separate operator authorization, source lease, and a genuine authenticated provider root.

## Corrected contract

The old `NativeProjectEffectFence.checkCandidate` requires a Worker in `Ready` and consumes `STAGE_B_DISPOSABLE_TEST_ONLY` fake account receipts. The actual `NativeSchedulerAccountReservation.candidateFromVerifiedProject` source validates the Worker in `Started` with an immutable canonical task/attempt/source lease. A phase rewrite, fake-receipt cast, W12 interface shape, or caller-supplied boolean does not bridge the mismatch.

The new `claimVerifiedDisposable` only accepts independently read **signed disposable** account roster and reservation authority. It reads `NativeSchedulerAccountReservation.readVerifiedDisposable` twice (including inside the expected-head project transaction), validates the account IDs/epoch/head, original reservation operation UUID and immutable account receipt, original dispatch/effect IDs, source, role, and reservation generation, then independently rereads the project task and Started attempt and checks the exact source lease SHA-256. One `LedgerStore.transact` with an original operation UUID and exact `ExpectedProjectHead` may write one `HeldBeforeSend` project fence. A project-losing contender **does not** free the already booked account slot, and a lost ACK does **not** authorize dispatch.

## Terminal issuer is still HOLD

No accepted production native provider-session issuer exists. The suggested W12 `ITrustedNativeProviderHost` does not provide a registered authenticated issuer; disposable `FakeProviderTerminal`, window closure, arbitrary verifier `Ok`, unknown, late, duplicate, and ACK-lost observations are not authenticated terminal operations. `denyUntrustedTerminal` always returns `Denied`; no account release or native provider-send API is exposed.

A future separately leased source path must issue `NativeProviderSessionTerminal` with the original project operation origin, provider-session/first-connection evidence, original dispatch/effect/reservation UUIDs, terminal blob OID, and lineage verification, then call the existing `NativeSchedulerAccountReservation.releaseVerifiedDisposable` only after a genuine verified terminal. The missing exact production callsite is the authenticated provider/session host-to-project terminal issuer and the registered scheduler ingress to the new Started pre-send fence; this source-only fixture does not implement either. **B2 not complete**.

## Reproducibility / negative space

From the isolated worktree:

```sh
dotnet test tests/AutomationNativeAccountProjectIngress20261123/AutomationNativeAccountProjectIngress20261123.fsproj -c Release --no-restore --logger 'trx;LogFileName=started-native-focused.trx'
```

This is the complete intended scope: no shared project files, full Client suite, live provider, deploy, `main`, push, schedules, cutover, or source-claim promotion. A disposable bare Git account and project remote are created under a temporary directory for signed approval, account reservation, two independent clone CAS, and fail-closed negative inputs. Source SHA / TRX outcome / mutant evidence must be filled from actual terminal runs; never infer PASS from compilation or a pending job.

### Initial RED control

Bounded Release job `6b44575c36e646229e8794cef3e6fdc2` completed exit code **1**: compiler FS0225, new ingress source did not exist. This is the initial red compilation control, not a semantic mutation result. Further positive/negative and mutation evidence must be recorded separately.

## Executed .NET 11 Release evidence (exact isolated worktree)

- Base accepted native source: `d312f1cc83685e781ce69e57ad0d4bf613bfb843` (verify path blobs against this checkout); assignment/spec: v2 blob `215087bdd0d7596f2859a1b69641aa04877d23fb`.
- RED-first missing ingress source: job `6b44575c36e646229e8794cef3e6fdc2`, exit 1 (FS0225), no test execution.
- First compiled fixture: job `e70b38c6e3a244b9b56507e7fe9092c7`, exit 0, 4 executed / 4 passed / 0 skipped.
- Expanded signed-roster, revoke, restarted clone and negative-space fixture: job `53f3187d4cbf405fa2e9ef03b30bcbf9`, exit 0, 7 executed / 7 passed / 0 skipped.
- **Intentional negative mutation**, removing the `snapshot.Commit <> claim.ExpectedProjectHead` guard from the **new file only**: job `c0c7b992a99b4f4d900996458bb89ebc`, exit 1, filtered 1 executed / **1 failed** / 0 skipped, failure `stale project head changed project CAS`; TRX `TestResults/mutant-red.trx`. Restored the exact source file SHA-256 `cce8392d57eb84f9e9b7c6f39807630ae36fd753c94147ac87166f7e557a753c` and re-ran: job `64bd442399d94eb2aebb5f8cf71b4561`, exit 0, 7 executed / 7 passed / 0 skipped, TRX `TestResults/restored-native-focused.trx`.
- Final test project also directly links accepted **old** `NativeProjectEffectFence.fs` under `STAGE_B_DISPOSABLE_TEST_ONLY`, solely inside the standalone fixture, to prove old fake B1 refuses the same canonical Started Worker. Its result must be recorded from the final job, not extrapolated from the seven-test result.

Disposable fixtures create local, ephemeral bare Git account and project remotes and sign the roster using fixture-only ECDSA; source path/attempt hashes and UUIDs derive from canonical Git records. The test assembly is named `Client.Tests` solely to access preexisting `internal` test-only APIs; no production assembly, existing shared `.fsproj`, or provider authority is modified. Evidence proves **source-only disposable correctness** and no live automation.

- Final old-B1 incompatibility regression and all eight source-only controls: job `fc0a6e0a2e4c4fa2b15067b2436761b7`, exit **0**, discovered/executed **8**, passed **8**, failed **0**, skipped **0**; TRX `tests/AutomationNativeAccountProjectIngress20261123/TestResults/final-native-focused.trx`. The old fake Stage B1's `Ready` guard rejects the real canonical Started attempt; no old fake B1 claim is committed.

## Review 12273 corrections

The disposable ingress now rereads the signed roster policy with the reservation at both pre-send checks. Account and member-project Worker limits must be positive; a reservation already filling a limit of one is valid. Coverage and account/project identity, epoch, and heads remain bound. This does not establish cross-ledger atomicity, release occupancy, or enable production.

The signed successor tests retain membership and reservation while setting account or project Worker capacity to zero, refreshing the account head and coverage. Before the policy fix, job `1f6213366958452c8114086c4ad1e952` exited 1: 3 executed, 2 failed (both revoked policies were admitted), 1 passed, 0 skipped; TRX SHA-256 `5e6667ba7a2fe1cb079d74697f7e1238ed907ff2e2fb85e6a3cc81a1b0cb9c30`. After the fix, job `6b45f63cde054df9b948c31fc5eaa65e` exited 0: 10 executed and passed, 0 skipped; TRX SHA-256 `850c506fd8fdd2d06e6e40215fc3230fe2c0ae0274d7ce80b037456f1aa8d47d`. Job `80660212b9ee453085925d13031854b5` independently reran the current focused fixture: exit 0, 10/10 passed, 0 skipped.

The generation test advances the real disposable Started attempt, refreshes project head and coverage, and preserves the candidate and account receipt. It expects the assignment/source-lease denial, no claim or fence, and unchanged reservation. Compound-guard bypass control job `0e522709293d401cb03077e43537470e` exited 1: 1 executed, 1 failed, 0 skipped, because the altered source admitted the claim. It restored the source SHA-256 `2170479cc84c675fd0d6925044235b9325782c4e84fdb97c8fc24c6aaeb003a0`. This control tests the compound guard, not each comparator independently. Terminal issuer, production wiring, and B2 remain on HOLD.

Final restored-source Release job `02ddbadf622840c3a0e375a714916438` exited 0, 10 discovered/executed, 10 passed, 0 skipped; TRX SHA-256 `2b09b77a20afaac43a2185f4d7004a8d9ce300bfbf1372dbbd11d71723fc7f5b`. Generation mutant TRX SHA-256 `e08d386b8f2fa38b3316eaef076f875984ce33ae22a72208c46d0ea47e6b90ab`. Tested ingress SHA-256 `2170479cc84c675fd0d6925044235b9325782c4e84fdb97c8fc24c6aaeb003a0`; test fixture SHA-256 `333671fcadc31e6bf9e7be9ef9786920196fd6f3e52caf2dc1e7b7a9733ec762`. Prior reviewed source is `931b7dc93abb76fb3c5fc2f80208b974466e597e`, parent accepted base `d312f1cc83685e781ce69e57ad0d4bf613bfb843`; relevant accepted dependency blobs: roster `99483f6764dd99b98db328d9c367ee573f24e28a`, reservation `fd9c9591c74ce906e15dfc191c2dbac0995b8dc3`, old fence `255e9c6956e2e110c738e4c27e01959ea0ca6b8a`, account ledger `8f4f3978ce599d0a3c5a411952aa621ee8f24430`, project ledger `602035282ec9021b5a0173cba3c18bdadf0920e4`.

## Stage C — existing client authentication (source-only, 2026-11-24)

The controlling architecture is task automation-native-provider-session-host-20261123,
spec v8, blob 00639e2f72766240c6b4838e0373416ab925233c. The historical
signed-disposable tests above are not requirements for the current path.
The existing authenticated account/project bindings and canonical Git
ledger operations are the sole authority context.

The Worker and distinct Reviewer ingress share verifiedReservation and
one project CAS. The receipt hash is read from the actual original account
operation and checked by W12 readOriginal (original candidate, receipt
lineage, generation one, no project fence/session or terminal). The current
verified roster must still include the exact project ledger/epoch and
positive limit for the actual kind. Inside the same expected-head project
CAS, the Worker validates canonical Started task/attempt/source lease;
the Reviewer invokes W12's verifyReviewerClaim against that exact
snapshot. An existing fence path, including cancellation, is never an
empty slot. The key comes from W12 noSendFencePath. The original
launch-intent and argument bytes are committed atomically with the fence.

The revised standalone ingress suite uses normal configured account
policy rather than fixture signing. Exact final Client source compiled in
job 26e4121a454b4cd9867f4ea2c8ef219f; the ingress suite re-used that build
in Release job 68536e8b9aae432aaf3efc06f83d6461: exit 0, 9/9 pass,
zero skipped. The removed obsolete signing-only production-denial
assertion is not a regression of actual authentication. The old fake
Stage B path remains a historical negative control, never a real issuer.

Evidence is against the explicitly authorized frozen W12 candidate
4a400be780d8e0b91cbd6ff6913fffff455b675b, not an independently accepted
producer. W23 owns the genuine project exclusion CAS; this ingress
does not manufacture that proof or call a provider. Final review,
composed real no-send validation, and publication remain gated by
independently accepted W12/W23 constituent heads. No live effects.
