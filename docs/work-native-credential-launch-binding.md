# Work-native credential launch binding (P1, default-off)

**Status:** Offline / disposable adapter only. **Operational NO-GO.** The accepted original WQ02 credential/verifier implementation (8c306e50, main d9f5f02c) is not an authorization to dispatch production browser sessions. No Scheduler, public Work mutation, remote provider, persistent Agent replacement, deployment or migration switch is enabled by this change.

## Source and evidence boundaries

- The existing WQ12 original prepared intent has a stable original launch, account admission, browser operation, dispatch, first-connect and WQ04 role/source/credit witness. `admitPreparedWithCredentialExplicit` threads a volatile `IssuedWorkCredential` to the existing original WQ04/C01 project CAS; the verifier is created in the same four-path original admission Git operation. Legacy `admitPreparedExplicit` is unchanged. An already-issued verifier is **not** a license to mint another bearer; lost original admission acknowledgements preserve the matching volatile ticket and original WQ07 token.
- `WorkQueueNativeCredentialDispatchAdapter.prepareProduction` and `dispatchProduction` explicitly return HostBridgeUnavailable, before issuing a secret or mutating durable state. Only `prepareDisposable`, `submitDisposable` and `firstConnectDisposable` support an **explicit offline fake provider**. They have no live registration.
- `NativePrivateBootstrap.TakeOnceFor` exposes the bearer only to the matching **cooperative execution binding** once, through an in-memory object; the ticket and bootstrap stringify as redacted. That binding is **not provider-backed attestation**. An actual provider-specific private bootstrap and first authenticated relay transport must still be implemented and independently observed.
- The original client-owned WQ03 SQLite/anchor one-shot journal and a create-only project Git original-host claim (same path/payload as existing WQ11) precede the fake provider call. A claimed or partially acknowledged send cannot authorize resubmission: uncertain submission, journal/host corruption, lost acknowledgement or callback exception retains original launch/account credit for operator reconciliation. The disposable unknown-send path also records WQ02 `OutcomeUnknown` when the project CAS can be verified; if it cannot, the WQ03 marker still forbids resending. A provider acceptance without a consumed private bootstrap is treated as uncertain.
- The fake receiver authenticates its first connect with the original stored verifier through `executeCredentialFixtureAt`, which is **test-only**; replay uses the same original first-connect operation. The accepted core credential test covers wrong bearer, cross launch, exact operation replay and STOP. Neither a public label nor an optional role is used as a runtime principal.

## Gaps before production GO

1. Provider host needs an execution-specific **private injection** channel that never sends the bearer as prompt, source, browser argument JSON, Git, logs, review text or ordinary diagnostics. Existing `BrowserBridge` argument/script APIs are not this channel.
2. The first Work connection/tool route needs accepted verifier enforcement and same-session operation replay without the disposable fixture or persistent Agent registration, with a proven host lifecycle and failure handling.
3. Unknown provider send needs a durable host-proven reconciliation record before any new dispatch is considered. Reissued bearer cannot be reconstructed from the verifier; unknown/lost in-memory preimage is an explicit repair/terminal case.
4. Independent review must validate exact source, Git two-clone races and .NET 11 Release tests. Lead alone integrates; no live migration/cutover based on offline fake provider evidence.

## Local verification

`dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter "FullyQualifiedName~WorkQueueNativeCredentialDispatchAdapterTests|FullyQualifiedName~WorkQueueCredentialTests|FullyQualifiedName~WorkQueueDurableLaunchIntentTests|FullyQualifiedName~WorkQueuePracticalAdmissionTests"`

The disposable fixtures use local bare Git remotes and separate clients. No hosted GitHub Actions or Codex.
