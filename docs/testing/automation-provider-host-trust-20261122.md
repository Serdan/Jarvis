# Native account → provider host/trust contract (default-off)

**Scope:** Worker 12, task `automation-provider-host-trust-contract-20261122`. This is a typed, source-pinned *non-authorizing* contract and independent disposable test harness, **not** a production host implementation, connected B2 completion, BrowserBridge send, schedule or cutover.

## Frozen source and verified ownership

- Base full commit: `d312f1cc83685e781ce69e57ad0d4bf613bfb843` (accepted-only private composition).
- Source branch/worktree: `refs/heads/test/automation-provider-host-trust-worker12-20261122`, registered at `.Jarvis-worktrees/automation-provider-host-trust-worker12-20261122`, initially clean at the exact base. Main is not changed.
- Eight source blobs read-only verified at the base through `git ls-tree -r`:

| Path | Exact Git blob |
|---|---|
| Client/Modules/AccountCapacityLedger.fs | `8f4f3978ce599d0a3c5a411952aa621ee8f24430` |
| Client/Modules/ChallengeWorkflow.fs | `981011513c2e7636d9318e907987d4d2232e3fda` |
| Client/Modules/NativeHealth.fs | `4753293d7da5379db18ade6f03e40ac34fb3d58b` |
| Client/Modules/NativeProjectEffectFence.fs | `255e9c6956e2e110c738e4c27e01959ea0ca6b8a` |
| Client/Modules/NativeSchedulerAccountReservation.fs | `fd9c9591c74ce906e15dfc191c2dbac0995b8dc3` |
| Client/Modules/NativeSchedulerRosterPolicy.fs | `99483f6764dd99b98db328d9c367ee573f24e28a` |
| Client/Modules/WorkQueueNativePrivateHostBridge.fs | `9a2b51f261da7c1a42e455fcbe78bfb0642f61c8` |
| Client/Modules/WorkQueueReactiveWake.fs | `87f81d557b2f7ed00efccb693b9c08a78e3cb0e1` |

## Actual interfaces, authority and observed mismatch

1. **Account reservation**: `Client.NativeSchedulerAccountReservation.NativeDispatchCandidate` carries original dispatch/effect UUID, project/ledger/epoch, owner, canonical attempt/generation, source repository SHA-256, source base OID/ref and exact source-lease digest. Its internal `reserveVerifiedDisposable` authenticates the signed whole-account roster, complete cross-project snapshot, and canonical Worker attempt on verified project HEAD **with `Phase = "Started"`** before account-head transactional CAS writes a `NativeReservationRecord` with `Status = "Reserved"`, `Generation = 1`. It is fixture-scoped and never a production host trust root. Public `reserve` and `read` explicitly reject production authority.
2. **Project effect fence B1**: `Client.NativeProjectEffectFence` is available only under `STAGE_B_DISPOSABLE_TEST_ONLY`, not compiled into `Client.fsproj`. Its `claim` requires canonical Worker `Phase = "Ready"` (or immutable claimed Reviewer), exact candidate/source and project-head CAS. It accepts a marker `STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1`, not a genuine native account reservation receipt. `beginFakeEffect` writes a durable `Dispatching` marker before the disposable fake port; `recordFakeTerminal` accepts only `STAGE_B_DISPOSABLE_FAKE_PROVIDER/v1`; `releaseFakeReservation` updates a *separate seeded fake account record*. Neither receipt nor terminal can be promoted to production evidence.
3. **Irreconcilable bridge without a new trusted host**: A Worker cannot be simultaneously the Started attempt required by actual native account CAS and the Ready attempt required by the B1 fake project claim at the same canonical project state. Constructing an intermediate `Ready` snapshot or rewriting fake receipt fields is not an authorization. A new production project-side ingress must deliberately support the Started account-receipt lineage and preserve the authoritative original source and project CAS.
4. **Terminal/release authority**: The existing account `NativeTerminalLocator` is only a locator. `NativeTerminalProof` includes original reservation/effect/terminal UUIDs, canonical key, provider-session ID, project head and terminal blob OIDs. The fixture `releaseVerifiedDisposable` checks an independent verifier, canonical operation/origin, project lineage, current account roster and same-head CAS, rejecting Unknown/duplicate release. Merely passing a shape check on typed data does **not** independently verify the provider-session issuer or source origin.

**Issuer ownership:** Account signed roster and original account CAS belong to the authenticated account ledger; Worker source lease belongs to the canonical project Git ledger. A real provider registration/first-connect/terminal issuer is **not provisioned** in this source. The existing B1 markers belong exclusively to disposable fake fixtures. No test-supplied bool, string, fake marker, `Ok` from a disposable verifier, or local tab closure can supply the missing trust root.

## Proposed host boundary (not wired)

`Client/AutomationNativeProviderTrustContract.fs` contains only pure checks and an *unimplemented* typed host interface. The intended sequence and responsibility are:

1. **Trusted account issuer** fetches canonical account record with exact account HEAD, reservation operation, generation and authenticated roster/project source lease; wins the native account CAS, or holds on a competing client.
2. **Trusted project ingress** reads canonical Started Worker source/ref and atomically writes a production project pre-send claim of the *same* dispatch/effect/account reservation and recorded project head. B1 Ready/fake account claim is not such ingress.
3. **Provider registration / first connect** is authenticated against an identified provider session and durable original project fence. An ACK lost after claim is Uncertain, not permission for a second send; tab closure is not terminal.
4. **Typed terminal and once-only account release** independently verify original project/operation/blob lineage and trusted provider terminal; re-read current project and account HEAD and apply one account CAS. Unknown, revoked without terminal, late ACK, duplicate delivery and loser of a two-client CAS all hold/reconcile, never replay.

**Single narrow next production change:** add a separately reviewed, default-off *trusted native project host ingress* that consumes a canonical **Started** Worker native account reservation and binds the original account reservation UUID, dispatch/effect UUID, canonical source/ref/generation, project pre-send CAS and authenticated provider registration. It must not reuse B1 fake receipt/Ready gate, and cannot release without the existing independent terminal+origin verifier. This task does not implement it.

## Verification boundary

Standalone `tests/AutomationProviderHostTrust20261122/AutomationProviderHostTrust20261122.fsproj` compiles the new unwired file alongside genuine accepted Client types and conditionally linked B1 fake module. Its structural successes mean only that caller-shaped records are internally coherent. No live host, provider, native account ledger modification, real BrowserBridge send, B2 connected terminal, nor actual multi-process CAS is exercised; those remain explicit production RED/unsupported.

## Genuine .NET 11 Release evidence

Actual SDK: `dotnet --version` = **11.0.100**. All commands targeted ONLY `tests/AutomationProviderHostTrust20261122/AutomationProviderHostTrust20261122.fsproj` in this registered private checkout; they did not run a full Client test suite or actual provider/BrowserBridge host.

| Managed job | Actual process/terminal | TRX evidence / meaning |
|---|---|---|
| `2fdd3b6c1ae44f3dac5b30e3133b0868` | exit **1** (compiler FS0225) | Deliberate initial RED: unwired module was absent; no test execution/TRX |
| `25ff209d2fd741d6a70ba6ef178e5e3c` | exit **1** (F# module test syntax errors) | Failed intermediate build, not counted as test PASS |
| `7a17d46fbd094a4397dea835c5df3ab4` | exit **1**, **8 passed / 16 failed** | `provider-host-trust-focused.trx` initially exposed NUnit shared-module fixture initialization errors; not a pass; instance-local fixtures repaired |
| `fe5392f63d5944ff94aa97ac50142d2a` | exit **0**, **24 passed / 0 failed** | First compiled, isolated structural-only PASS before adding the typed host interface |
| `76b132ce624d45ee9e99db59d65fe2d9` | exit **0**, final focused Release | `provider-host-trust-final.trx`: **25 discovered / 24 executed / 24 passed / 0 failed**. The console summary reports Total=24 because NUnit Explicit production RED is excluded from execution; do **not** misreport discovery as 24 |
| `a380729864d141a2adee2fe6ff72f004` | exit **1**, selected expected RED | `provider-host-trust-production-red.trx`: **1 discovered / 1 executed / 0 passed / 1 failed**, exact `ProductionNativeAccountProviderTerminalRemainsUnsupported` test; deliberately fails because no authenticated real Started-project/provider terminal/release call site exists |

This gives passing *pure shape/fail-closed tests*, not a production native host PASS. The forced RED confirms the unsupported trusted interface, not that an actual provider was contacted. No real two-client Git CAS, registration, first connect, account release, live send, schedule, deployment or cutover was executed.

**Immutable review scope:** exactly one NEW unlinked Client module, two NEW standalone test-harness files, and this NEW documentation file. Final SHA and review reference are recorded in the canonical task/review ledger; no main branch or existing fsproj/source file is changed.
