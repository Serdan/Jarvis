# P2 — WQ-05 integration-gate two-client matrix remains incomplete

**Source:** `Client.Tests/WorkQueueReviewIntegrationAdapterTests.fs:267-501`; `Server.Tests/WorkQueueContractTests.fs:465-520, 640-710`; WQ-05 canonical task spec (F06 acceptance).

Executed independently against `f1b0c527` in a clean isolated checkout: WQ-05 adapter 10/10 .NET 11 Release, server work-queue contracts 77/77 .NET 11 Release. The source test harness creates two disposable Git clones. Its two-clone tests exercise review hard-edge vs cancellation and stale-set replay (`344-380, 469-501`). The integration tests are in-memory single-project ordering, not two-client original push vs hard-edge CAS and durable source/job journal. The existing server contract tests check negative ancestry and pure gate transitions, but not WQ-05's cross-ledger source provenance and downstream eligibility across two clones.

**Required WQ-05 negative matrix**, with exact original operation ID and explicit expected project/remote heads:
- push-intent CAS first; B hard-edge CAS second; original push landing before or after gate; durable `IntegrationGateHold` and `ExternalEffectObservedPendingGate` without `Integrated`, second push or D claim
- B CAS first; stale original completion rejected; no new push
- lost push acknowledgement with remote effect observed and process/job terminal proof; original UUID replay idempotent and one final authorized producer
- ambiguous still-running mutating job (`JobsReconciled=false`) and negative ancestry read (`AncestryVerified=false`) both remain blocked, never inferred definitive no-effect
- two-clone stale downstream D claim and stale queue/high-water read, before and after B satisfaction/verified waiver; no transient ready admission
- post-waiver/satisfied-B `ResolveIntegrationGateException` exact once; old concurrent resolver stale or idempotent, with no duplicate producer or second external push

Do not substitute in-memory booleans for production WQ-03 original journal and host job/remote evidence. The scenario tests may use disposable fakes but final integration must remain gated on accepted WQ04 producer/high-water and WQ03 original-UUID host-protection/recovery proof. No claim of host WIP immutable policy, production activation, Scheduler or deployment is made.