# Direct ObserveAgent age: isolated preflight (not wired)

## Scope and evidence

Task `robustness-observe-agent-derived-age-20261127`, attempt `bd26f962573440cd8d7a045aaaf1d280`, generation 1, pinned spec blob `8ff31add26881c3bebb0c323808c03100aec3a1e`. Source base `291b5499051fbddfff1c76308e1a0202a5d1862f`; no modifications to production code, existing tests, catalog, policies, live agents or deployment. This proposal is reviewable independently and is not a rollout blocker or permission to wire it.

### Registered route and evidence boundary

All line references are to the exact base above.

| Path | Evidence |
| --- | --- |
| `Server/McpTools.fs:1978–1982` | Read-only workspace OAuth scope, explicit observer and subject IDs; sends `ObserveAgentCommand`. |
| `Common/Messages/AgentCommand.fs:1320–1339` | Classifies observation as non-activity for server/client activity hints. |
| `Client/SignalR/Client.fs:453–459, 529–535, 1396–1398, 1832–1835, 1892–1925` | Suppresses response alerts, rejects mismatched actor before dispatch, selects empty local activity actor for non-activity, no Ready activation or last-seen write; dispatches to actual producer with TUI lookup. |
| `Client/Modules/OrchestrationApi.fs:2595–2650` | One verified snapshot; whitelisted summarized response; one client UTC clock sample inside output; returns raw `observedAtUtc` and optional `lastSeenAtUtc`, `lastSeenProvenance`, `lastSeenIsCanonical=false`. No derived age. |
| `Client/Modules/ReviewWorkflow.fs:4430–4437` | Verifies observer and reads subject on same immutable snapshot without write. State derivation at 98–104: Working, else Ready if assignment, else Parked/NeedsWork; nonworking execution is Stopped. |
| `Client/Modules/ConsoleTui.fs:2480–2516` | Local cache is a project/agent-keyed monotone watermark for this cache, not a canonical heartbeat; read-only lookup can be missing or stale across clients. |
| `Client/Modules/AgentView.fs:290–293` | Agent activity belongs to parent project even for worktree routes; a failed/alias lookup is not absence of activity. Preserve current lookup behavior in this preflight; normalize only through separately authorized shared seam. |
| `Client/Modules/NativeHealth.fs:17–21, 55–62, 76–86, 105–151` | Separate authenticated server clock/activity, configurable default 600 seconds, invalid/missing/future fail closed; inclusive >= comparison with fresh candidate + final activity recheck. |
| `Server/Services/FeedbackStore.fs:331–351` | Authenticated user/project/agent scoped persisted attributed activity; unverified watermark fails closed. |

This is a producer and transport trace, not a claim that a particular live deployment is identical to this source. Existing `Client.Tests/ReviewWorkflowTests.fs:4312–4441` exercises actual local SignalR/temporary Git observation across eight Ready agents and two runtimes, forged/missing identities, genuine activation, unchanged state/last-seen; `Client.Tests/BrowserSchedulesTests.fs:498–507` exercises the activity predicate. `Server.Tests/ServiceTests.fs:1323–1342` exercises metadata. The new runtime witness additionally seeds a private notification and a deliberately old local watermark in only one runtime and checks the actual output, not a copied serializer.

## Proposed additive fields

Keep all existing response properties exactly, including raw round-trip UTC strings, optional timestamp, snapshot commit, state/execution, provenance and canonical flag. Capture one `observedAt` from the client UTC wall clock (after the existing snapshot/cache read, at the same projection point as today's clock sample), use it for both serialization and elapsed arithmetic. Pass it as a value to a pure elapsed helper; do not sample a second time or derive it from request latency. The cache and ledger read are separate events; this does not assert an atomic cross-source snapshot.

| New property | Meaning |
| --- | --- |
| `lastSeenAgeSeconds` | Optional nonnegative Int64 elapsed whole seconds (floor from duration ticks, not floating rounding); null if evidence invalid/unavailable. |
| `lastSeenAgeStatus` | `Valid`, `MissingLastSeen`, `InvalidLastSeen`, `FutureLastSeen`, `InvalidObservationClock`. A valid zero is distinct from null. |
| `lastSeenAgeClock` | `ClientUtcWallClock`; always diagnostic for this route. |

A local timestamp equal to observation instant gives zero; future by even one tick gives null, not clamping. Missing cache gives null + existing Unknown provenance. A typed sentinel minimum timestamp is invalid. The optional wire parser in the executable is an input-boundary witness for null/malformed round-trip text; production already holds a typed DateTimeOffset option, so do not add a serialize/parse cycle to producer. Compare instants with offsets, not strings. Preserve original raw timestamp even if invalid/future, and distinguish invalid clock from missing last-seen. Reject invalid/malformed observation instant first. Whole seconds are for display only; use precise duration for any actual policy comparison.

At 599/600/601 seconds diagnostic age is 599/600/601, regardless of Working/Ready/Parked/NeedsWork. Do not expose a local-cache boolean called dead, idle, stale-agent, recoverable, or permission. Old local cache evidence may coexist with a recent server watermark or nonworking state; missing local cache may coexist with active work. A nonworking record retains historical age but it is not an active-turn inactivity clock; even Working is not enough to infer recovery. Keep workingTurnStartedAtUtc distinct. If a separate authorized projection obtains an authenticated policy decision, it must explicitly distinguish its source/time and unknown status, not promote the direct observation cache. The existing Native Health implementation, not this model, owns inclusive threshold and fresh-candidate/recheck authority; 600 is its configurable default, not a constant to use here.

## Wiring lease and tests

W4 owns the compact project/frontier projection and authoritative policy predicates. Share only a small typed, pure elapsed-time result helper (observation instant + optional timestamp -> valid duration or explicit missing/invalid/future); provenance belongs to the calling projection. Prefer W4's eventual shared seam over introducing a competing module. NativeHealth's authenticated activity and eligibility stay separate, including policy version/settings, candidate identity and CAS/rechecks. Do not turn a client cache into server activity by renaming it.

After W17/W6/W4 ordering and integration, Lead must explicitly grant the direct-producer lease on `Client/Modules/OrchestrationApi.fs` (`AgentObservationApi.observe` only), any shared elapsed helper compile wiring and exact direct-route regression in `Client.Tests` as required, aligned with W4's accepted API. No change to Server registration, Common command activity, SignalR actor binding, TUI write paths, or Health recovery needed to add diagnostics; if actual code proves otherwise, request scope reconciliation rather than silently editing them. Do not merge this preflight as production implementation. Refresh source trace on actual integration base and convert the baseline red witness to a true runtime additive-contract test. Require old/new payload compatibility, private-data allowlist, one deterministic sampled clock, null/invalid/future/offset/precision cases and no new ledger/activity/auth effects. Keep W8's separate reviewer-authority serial correction source frozen unless an adjudicated handoff is issued.

### Reproduction (from source root; .NET 11 Release)

```text
dotnet run --project tests/ObserveAgentAgePreflight20261127/ObserveAgentAgePreflight.fsproj -c Release
dotnet run --no-build --project tests/ObserveAgentAgePreflight20261127/ObserveAgentAgePreflight.fsproj -c Release -- --require-wired
dotnet run --project tests/ObserveAgentAgePreflight20261127/RuntimeWitness/RuntimeWitness.fsproj -c Release
dotnet test Client.Tests/Client.Tests.fsproj -c Release -m:1 --filter "FullyQualifiedName~CrossAgentObservationIsReadOnlyAcrossEightReadyAgentsAndSessions|FullyQualifiedName~activityPredicateDoesNotCountReadOnlyAgentObservationOrJobPolling" --logger "trx;LogFileName=observe-agent-purity.trx" --results-directory /tmp/jarvis-w8-observe-agent-age-20261127
```

The first executable is an isolated proposed model plus pinned-source contract/negative-space checks; it is not production wiring. The second command must fail specifically with the missing-derived-fields witness on the unmodified base; an arbitrary compilation or infrastructure failure is not a valid red. The third invokes the actual registered client dispatch against a disposable local bare Git fixture and verifies baseline and purity, including differing local cache evidence and private notification redaction. No external service. The fourth runs existing true production route/predicate regressions without editing those tests. Terminal evidence is recorded separately after jobs complete; do not treat source inspection or a started job as a passing run.
