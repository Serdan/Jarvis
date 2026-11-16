# WQ11 independent review — Changes requested

**Reviewer:** Reviewer 2  
**Frozen source:** `b8a886a3de2298b392693f6054ee256d57015bf2` (`test/wq11-fake-provider-first-connect-worker7`; base `0231ea78ed758a8fa1c63295c59f74ca53267b89`)  
**Scope:** `Client.Tests/WorkQueueReleaseAcceptanceTests.fs` (+220 lines), `docs/WorkQueueWQ11FakeProviderSmoke.md` (+103 lines).

## Independently verified
- **Focused Release smoke:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~CanonicalDemandLaunchesFakeProviderFirstConnectThenReleasesCapOne' --logger 'console;verbosity=normal'` — **1 passed, 0 failed**.
- **Adjacent Release regressions:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WorkQueueReleaseAcceptanceTests|FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~WorkQueueSchedulerHostPreviewTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests' --logger 'console;verbosity=minimal'` — **41 passed, 0 failed, 0 skipped**, 51 seconds test execution.
- Frozen worktree clean. Test uses two disposable local Git clones; canonical WQ04 task/role and WQ08 selected demand, actual WQ07 cap-one token admission, WQ02 CAS and original-UUID replay, terminal proof/release, and held unknown original are sensibly asserted. Production `CanDispatch=false`, WQ04 same-CAS role witness gap, live WQ03/browser and cross-project Scheduler limitations are explicitly stated.

## Blocking finding
- `findings/fake-provider-handoff.md`: **The provider-to-first-connect step is not exercised.** `submitFakeOnce` only counts a simulated send; the session key is generated separately and `InitializeAgent` is directly committed through the inert WQ02 fixture. This allows the positive smoke to pass without any fake-provider result causing the WQ01 first connection, contrary to the documented account of the test. See test lines 523-559 and doc lines 38-46. Request the smallest default-off in-process callback that returns the connection result and feeds the existing first-connect contract, or explicitly document that missing interface and narrow the acceptance claim. Do not introduce a production Scheduler or additional speculative fencing.

**Verdict:** Request changes on the frozen SHA, limited to the fake-provider/first-connect evidence gap.