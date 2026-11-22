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

**Original eight-test auth limitation:** The original fixture injects an already-authenticated
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
- Original eight-test suite did not establish production signed JWT issuer validation,
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

## Adjudicated independent review corrections (2026-11-22)

Original frozen source `380cac50eb3aac51457bef1b78f571bcf85119fe`
received independent `ChangesRequested` review
`c6a3296dc9b549b6886c21b578f23190` (decision 11045).
Independent Adjudicator 3 checkpoint **11058** authorized only the original
standalone test/docs lease: F1 **NARROW**, F2 **ACCEPT**, F3 **NARROW**.
No Server/Client production source, shared project file, main, live state,
service, schedules, or deployment was changed.

### F1 — real ASP.NET offline bearer handler/policy (NARROW)

`OfflineAspNetBearerIssuerSignatureScopeAndRealSQLiteHubBinding` constructs
an actual .NET11 ASP.NET `JwtBearerHandler` with a disposable HMAC fixture key,
fresh per-request scoped DI, issuer and audience taken from production
`Server.Auth.issuer` and `Server.Auth.validAudiences`, and an actual
`IAuthorizationService` policy invoking production
`Server.Auth.hasScope ClientConnect`. It tests issuer- and signature-invalid
tokens, missing `client:connect` scope, a valid signed token, and a valid
different-subject token. The accepted verified principal is bound to the
production `HubService`/registered-session identity before reading an actual
disposable `OptionalFeedbackStore` SQLite record. A different valid subject
cannot adopt Alice's registered session. An auth handler's scoped cache is
reset for each tested request; sharing one root-service scope would have
produced a false issuer-negative green.

**Explicit boundary:** this exercises the real ASP.NET handler plus
authorization service and production `Auth` rules in an offline request
context. It does **not** host production `Program.configureApp` behind
Kestrel/TestServer, retrieve Auth0 OIDC signing metadata, or prove the live
`/client` SignalR handshake. The policy requires authenticated identity and
`client:connect` but **not** `sub` directly; a subjectless signed token
passes that policy but lacks the registered user identity required by the hub.
Do not invent a stricter production policy or claim complete host JWT parity.
For literal full-host issuer parity, a separate disposable app-host test lease
with offline metadata/configuration override is still required.

### F2 — canonical owner transfer and role mutation (ACCEPT)

`OwnerTransferRevokesStaleNativeCandidateAndPreservesNewCanonicalOwner`
captures the original candidate from a separate clone, records authenticated
stale server SQLite activity, registers a successor, and invokes genuine
`ReviewWorkflow.transferAttemptOwner` with exact task, attempt, agent and
generation versions. The stale native health check and direct Git CAS recovery
are denied; the successor's queued attempt, immutable spec/source, new
attempt ID, and no recovery receipt are verified from the second clone.

`CanonicalRoleAttachmentCannotForgeWorkingAssignmentOrRecoveryOwner`
separately creates and attaches a canonical role to a registered successor.
Role association is **not** an ownership handoff; the incumbent remains
eligible under existing policy. A client-supplied candidate impersonating
the role-holder cannot resolve a working assignment or perform native
recovery. The task owner, generation, spec and no forged recovery receipt
are verified from the independent clone. No synthetic ledger edits or invented
role-as-CAS-invalidation rule were used.

### F3 — exact boundary reaching production NativeHealth (NARROW)

`ImmutableBoundaryObservationRunsThroughRealNativeHealthAndGitCas`
injects only the already-authenticated transport `AgentActivitySnapshot`
with a frozen server clock at exact **599, 600 and 601 seconds**.
The actual `NativeHealth.checkOnce`, `NativeHealth.current`,
`NativeHealth.recover` and native Git CAS receipt and generation are
asserted for each independent disposable two-clone fixture.

`TransportPortNegativeCanaryFailsActualNativeHealthBoundaryAssertion`
perturbs the transport `LastSeenAtUtc` from exactly 600 to 599 seconds,
runs the **real** native production decision/recovery path and captures the
intentionally failing must-recover assertion as a negative canary. The
mutated input cannot produce a recovery receipt or generation increment.
This is a mutation at the transport port **reaching the production SUT**,
not a tracked production-source mutation; the original local comparator
witness remains merely supplemental.

### Full-suite source and evidence

The original **8/8** positive harness tests remain in the same standalone
project. The correction adds **7** independently named cases (three
exact-boundary cases, the transport negative canary, owner transfer,
role attachment, and JWT policy/SQLite identity integration), for **15** unfiltered tests. The final source-bound Release run is
independently TRX-checked as **15 passed, 0 failed, 0 skipped**.

The source-pinned Release invocation is:

```sh
dotnet test scripts/tests/automation-health-coupled-20261122/HealthCoupled.Tests.fsproj \
  -c Release --no-restore -p:BuildProjectReferences=false \
  --logger 'trx;LogFileName=health-adjudicated-full-release.trx' \
  --results-directory /tmp/jarvis-health-coupled-worker19-results
```

The full `Client.Tests` suite remains **INCONCLUSIVE** as independently
reported earlier, and unrestricted simultaneous SQLite reads, actual external
identity-provider metadata, host-network JWT ingress and native Windows are
outside this isolated proof. No test result should be used to activate Health,
Scheduler, deployment, migration or any live effect.

**Final terminal evidence (new correction):** `start_job`
`01ac1d21e3dc43509f2e611f90d9707c` completed exit **0**:
`Passed: 15; Failed: 0; Skipped: 0; Total: 15; Duration: 33s`.
Exact unfiltered TRX:
`/tmp/jarvis-health-coupled-worker19-results/health-adjudicated-full-release.trx`.
A separate XML parse found `total=15, executed=15, passed=15, failed=0`,
and 15 `UnitTestResult` entries all with `outcome=Passed`.
SDK `dotnet --version`: `11.0.100`. Target `net11.0`,
configuration `Release`, Linux, standalone `HealthCoupled.Tests.fsproj`,
`--no-restore -p:BuildProjectReferences=false` using earlier compiled
first-party Release project references. Earlier parser/compile/test REDs for
the new JWT fixture were retained in distinct job/TRX records and not
relabeled; the final correction includes fresh per-request DI isolation and
production-matching scope semantics. The original 8/8 and focused 6/6
F2/F3 and 1/1 JWT proof remain separate positive evidence, not a substitute
for this unfiltered 15/15 gate. The earlier **full Client.Tests INCONCLUSIVE**
status is unchanged.
