# WQ-01 Phase A: work queues, launch contracts and deterministic fixtures

Status: **isolated contract/fixture implementation, not production activation**. The accepted architecture is [work-queues-and-ephemeral-executions.md](work-queues-and-ephemeral-executions.md), at base commit `90ed868d04524d0483ccd979cffb5029d8cf7027`. The target architecture's §16 enumerates 80 acceptance/negative-space cases; this branch is **not evidence that all 80 have passed**.

## Boundary

- `Server/WorkQueueContracts.fs` defines pure F# contracts and state transitions, included in the server assembly but **not invoked by server handlers, scheduling, browser submission, project account persistence, production migration or deployment**. No production or schema behavior changes as a result of these functions being compiled.
- `Server.Tests/WorkQueueContractTests.fs` exercises immutable snapshots, explicit expected versions and operation IDs; `Project` and `Account` represent **separate** versioned CAS domains, with stable operation payload replay. Negative-space tests race stale and winning logical snapshots.
- The two-client disposable Git fixture creates a bare Git project and two clones, demonstrates one non-force push winning and the second stale snapshot being rejected, then fetches the original winning receipt. It does **not** yet serialize every contract transition to the real Git task ledger. Further two-repository/account project interleavings belong in the canonical persistence implementation.
- The fixture virtual clock supplies `now` explicitly for registration, heartbeat, STOP and reclaim operations. Real server clocks, provider identity continuity and STOP delivery must be wired and independently verified in later phases.

## Invariants modeled and tested

| Boundary | Model / negative-space coverage |
| --- | --- |
| FIFO and dependencies | Two tiers: blocking-first then original availability timestamp, project, sequence, work ID; no permanent Worker quotas or Manager model. Dependency coverage failures return Incomplete, not empty/healthy. Tarjan SCC includes self-loops and exact cycle edges. |
| Source lineage | Explicit repository/task/target/lineage identity fences concurrent actionable implementation stages; a completed generation permits an intentional successor. Safe requeue requires typed read-only or remote WIP snapshot evidence, with mutating-job reconciliation marker. |
| Account capacity | Reserve/resize/release share one versioned account CAS; a token cannot be released by absent/mismatching project terminal proof; global-before-project admission abort writes an abort-only project tombstone under the original launch before account release. |
| Launch identity and lease | One-shot submit; UUID appears only on first successful registration with a per-provider-execution session key. Same operation replay returns identical UUID; competing registration rejects; heartbeat versus STOP uses project CAS lease generation; no-claim reclaim and unregistered reclaim fence late work. |
| Demand credits | Canonical injective role-compatible matching and bounded augmenting-path rematching prevent a flexible role from stranding an already-reserved role-specific item; claim has one primary item per launch. |
| Operator messages | Role messages generate one primary work item per pending message. Dedicated primary reply completion commits message Handled, W Completed and terminal launch in one project CAS. Ancillary handler acknowledgement does not terminate unrelated primary work; unfinished ancillary claims return to Pending/Available on that primary launch's terminal transition. Task messages are passive, not standalone launch demand. |
| Frozen review finality | Decided review becomes DomainFinalizedTerminalPending while work is still Claimed, rejects retroactive hard dependencies and cannot complete work before its canonical verdict. Gate-before-verdict rejects the stale decision. |
| Integration external effects | Original one-shot push journal pins accepted source SHA, target and operation ID. Positive observation requires those identities plus verified ancestry/job reconciliation, not merely a timestamp or negative remote read. A hard dependency creates a hold; observed effect remains a GateException, not Integrated/IntegrationAuthorized, until satisfied prerequisites or a versioned waiver; waived historical edges remain recorded. |
| Migration | Only all-Activated account/project snapshots permit the modeled new dispatch epoch; no Manager handoff path is added. |

## Test commands

```sh
dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~ServerWorkQueueContractTests
dotnet test Server.Tests/Server.Tests.fsproj -c Release
dotnet build Jarvis.slnx -c Release
```

The new NUnit fixtures include both success and denial cases. Their names cite the §16 case numbers they exercise; **a shared number is a targeted regression scenario, not a claim of exhaustive end-to-end case coverage**. Full canonical Release gates must still execute all 80 target acceptance cases with actual two-client ledger storage, lost acknowledgement/recovery, browser bridge, mutating job journal, account capacity and migration epoch instrumentation.

## Required follow-on implementation / verification, not supplied by this branch

1. Wire serializable project/account Git persistence, exact operation receipts, real high-water/index coverage and domain authorities to the contracts; idempotency maps in memory cannot substitute for durable multi-client proofs.
2. Authenticate/verifiably associate a distinct physical provider session key and original dispatch intent; the pure fixture models distinct per-provider keys, but cannot prove that a copied key was not reused without bridge evidence.
3. Verify actual immutable remote source WIP refs/SHAs, original Git merge/push ancestry and independently settled mutating jobs; typed proof values in a fixture are **test inputs**, never sufficient authority in production.
4. Replace placeholder nonempty waiver/response/source proof receipts with canonical versioned project, Lead/operator approval and review/integration/challenge records. Bind all real review claim, terminal-only, gate reconciliation and integration producer transitions under one project Git CAS. For F06, every cross-project dependent must compare verified IntegrationGateHold coverage, including stale index fences.
5. Complete all lease recovery modes, including claimed execution and domain-finalized terminal-only recovery, audited ancillary message handler claim/reply recovery, worktree cleanup/retention and exact global token owner receipts.
6. Implement and validate the non-agent scheduler, real browser one-shot dispatch, conversation thread API, task-first UI, migration epoch fences and backwards-compatible historical receipts in their subsequent phases.
7. Freeze and independently review each implementation boundary. **Operator approval remains required** before canary, migration activation, replacing the old Manager schedule, restart or deployment.

## Review notes

The intent is to give later implementations a reusable *executable semantic reference* with explicit rejection outcomes, rather than put mock dispatch logic on the production code path. Since account and project CAS cannot be combined atomically, the C01 abort-only project proof and exact account release remain separate and must be confirmed across real Git clients. Since Git external push is not atomic with project CAS, `ExternalEffectObservedPendingGate` is an observed fact and a durable workflow **hold**, never a permission to publish downstream integration success. 
