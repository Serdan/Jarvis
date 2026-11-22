# Coupled Automation Health acceptance — 2026-11-22

## Source and trust boundary

- Main baseline: `dcac884fc061869572b12a03befc12f11c009f2f`.
- Independently Accepted server SQLite activity ancestor: `0beb9eb869330fe55d5a804e2fe96aad728c1797`.
- Independently Accepted native recovery ancestor: `aab048c39bad208c058c419b142653a040ff82ab`.
- SDK: `dotnet --version` = `11.0.100`; Linux, Release, net11.0.
- Standalone project: `scripts/tests/automation-health-coupled-20261122/HealthCoupled.Tests.fsproj`.
- Worktree: `test/automation-health-coupled-worker19-20261122`, isolated from main.
- Scope: only new project and F# test source under `scripts/tests/automation-health-coupled-20261122/**` and this document; no production or existing .fsproj edits.

The harness links the unmodified first-party `Server/Server.fsproj` and
`Client/Client.fsproj`. Actual `ClientService.SendCommandToUser` reaches the
server's authenticated agent-activity ingress, `OptionalFeedbackStore`
records/reads disposable SQLite, and registered `HubService.GetAgentActivity`
uses `sub` from an in-process test-authenticated principal. Actual
`NativeHealth.checkOnce`, canonical `NativeHealth.current` and
`NativeHealth.recover` use two independent local `LedgerStore` Git clones
with an offline bare remote, distinct disposable trust bindings, original
canonical task/agent records and real CAS. All external transport responses are
simulated only at the server-client boundary, and no live tool runs.

**Auth limitation:** The test injects an already-authenticated
`ClaimsPrincipal` with a fixture `sub` claim; the ASP.NET JWT-signature and
issuer-validation middleware is *not* exercised. Test clients cannot forge the
`HubService` registered user/session or SQLite row by supplying client-local
activity data to `NativeHealth`, but the test does not establish real OAuth/JWT
issuer acceptance or rejection. That requires a separate app-host/transport
acceptance test with a disposable signed token and controlled issuer.

## Release command and terminal results

From the private worktree root (the project references were rebuilt successfully
in the first run; `BuildProjectReferences=false` reuses those exact compiled
Release references without any production source changes):

```sh
dotnet --version
# 11.0.100
dotnet test scripts/tests/automation-health-coupled-20261122/HealthCoupled.Tests.fsproj -c Release --no-restore -p:BuildProjectReferences=false --logger 'trx;LogFileName=health-coupled-8.trx' --results-directory /tmp/jarvis-health-coupled-worker19-results
# exit 0, Passed 8, Failed 0, Skipped 0, Total 8, Duration 18s
```

Terminal unfiltered TRX:
`/tmp/jarvis-health-coupled-worker19-results/health-coupled-8.trx`.
Original four-case bootstrap test terminal TRX:
`/tmp/jarvis-health-coupled-worker19-results/health-coupled.trx`
(4/4 PASS). Focused original recovery-UUID replay:
`health-concurrent-replay.trx` (1/1 PASS); authenticated tool ingress:
`health-ingress.trx` (1/1 PASS).

Test cases: actual server authority at nominal 599/600/701-second ages;
missing/wrong-user/unregistered activity HOLDS; authenticated production
`SendCommandToUser` ingress refreshes SQLite and suppresses both clients;
two-client synchronized CAS with a shared deterministic operation ID and
one generation transition; stale canonical intervening yield; seeded negative
transport traces (recent refresh, partial reconnect, clock rollback,
future timestamp, missing activity); exact-source receipts, task/attempt/spec
and generation preservation; reference inclusive age oracle and intentionally
incorrect strict-> comparison, yielding a minimal 600-second mutant witness.
The observed 600-second check has small wall-clock scheduling drift; the
reference model, not a fake clock injected into the server, proves exact
inclusive arithmetic at that boundary. The comparator mutation is a local
reference mutant; production code was not deliberately modified.

### RED diagnostics retained, not relabeled

Earlier unfiltered `health-coupled-full.trx` exited 1, 6/7 PASS and one RED.
Two truly simultaneous production SQLite reads caused one client's
`GetVerifiedAgentActivity` to report `ActivityProofUnavailable`; it
correctly held, leaving its peer alone at a two-party Git CAS barrier, which
timed out. `health-concurrent-diagnostic.trx` reproduced the same
fail-closed path. The subsequently successful CAS race serializes the
*transport-boundary server activity requests* with a test semaphore so both
clients obtain individually authenticated SQLite evidence before racing
actual Git recovery. This gives a valid concurrent Git CAS result but **does
not prove unrestricted concurrent SQLite-read availability**. The other
intermediate RED `health-concurrent-serialized.trx` correctly caught
`OperationIdReused` when the test replayed the same UUID with a *different*
reason payload; the final test replays the exact native request and passes.

## Residual gates and ownership

- Independent immutable review of the exact committed test-only source is
  required before the clean private composed Automation gate relies on it.
- Concurrent authenticated SQLite read availability and any need for
  bounded transport retry remain explicit fail-closed coverage, not green
  high-concurrency availability proof.
- Test suite does not establish production signed JWT issuer validation,
  full owner/role rewrite transitions, external network conditions, native
  Windows behavior, or an active/standby multi-server deployment.
- Worker 21's unfiltered **complete Client.Tests** run exceeded 10 minutes
  and had no terminal TRX; it remains **INCONCLUSIVE**. Its independent server
  5/5 and 560/560, client 30/30 and 81/81 Release baseline results are
  distinct and not reclassified by this suite.
- No main integration, Windows machine profile use, Health activation,
  service restart, browser send, deployment, migration, unattended Scheduler,
  or cutover is authorized or performed.

If Lead/Integrator needs real JWT issuer-signature acceptance, request a
separate narrowly scoped disposable ASP.NET test-host lease. Do not weaken
production authorization or add fake authority to this harness.
