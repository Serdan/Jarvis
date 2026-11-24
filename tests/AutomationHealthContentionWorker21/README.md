# Shared-store activity admission regression

Run on .NET 11 from the repository root, using only local loopback endpoints and
temporary SQLite databases:

```sh
dotnet test tests/AutomationHealthContentionWorker21/Contention.fsproj -c Release -m:1
dotnet test Server.Tests/Server.Tests.fsproj -c Release -m:1 --filter 'FullyQualifiedName~AgentActivityAuthorityTests|FullyQualifiedName~OptionalFeedbackStoreTests|FullyQualifiedName~FeedbackStoreTests'
```

The fixture uses the genuine server DI and authenticated HTTP/SignalR routes,
with a registered Hub and synthetic test user/device. An agent command may
produce the expected missing-device result; its authenticated ingress still
must be observed. The normal shared-store workload must verify all distinct
and repeated activity keys after bounded drain. A split-store control is used
only to isolate deliberate lock/timeout witnesses. The test measures ingress
latency without asserting an environment-dependent performance threshold.

Authority remains fail-closed during pending/in-flight writes, including a
timed-out physical operation. The latest genuine observation can be admitted
while another operation owns the worker. Coalescing retains the highest
observed timestamp, not the last arrival's potentially reversed clock.
A waiter's completion is not proof. Proof requires a timely, current-version
write and a fresh matching SQLite read. Dispatched failures are not replayed.
The shared worker serializes physical operations; at most one posted audit can
reserve a turn during an activity write. Awaited diagnostics remain nonwaiting
on contention; posted diagnostics remain bounded and best-effort.

The original red-first baseline was the pinned main commit
`9f6b9760955f0acb2673e2cab8b8a852ec4dd2ac` with the initial fixture: normal
sequential 1/40 and concurrent 0/40 verified. The correction is measured on
the same workload, with additional supersession/authority assertions. The
accepted hosted negatives from commit
`271698321a65a4b215e1d89cc8c00858e7e22d9c` are a separate, unchanged test
prerequisite; they are not modified or incorporated here. Exact run provenance
and measurements are recorded in the canonical task checkpoint.

These are hosted in-process and store-level tests, not evidence of deployed
behavior, independent OS-process restart, Windows or physical cross-device
parity, or composed Health/Challenges/Scheduler activation.
