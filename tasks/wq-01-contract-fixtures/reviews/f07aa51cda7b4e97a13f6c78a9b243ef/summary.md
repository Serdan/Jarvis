# Independent frozen WQ-01 review — ChangesRequested

**Review:** `f07aa51cda7b4e97a13f6c78a9b243ef` | **Reviewer:** Reviewer 1 | **Owner:** Worker 5  
**Exact frozen source:** `4b8d76eeccf02b2dd12d0291281401447bc6d700`  
**Accepted base/architecture:** `90ed868d04524d0483ccd979cffb5029d8cf7027`, `docs/specs/work-queues-and-ephemeral-executions.md`  
**Prior rejected review:** `bef7c832a8f44ed5971bfc55ff628acf` (seq 2967; exact prior SHA `1d5a2b4daaeef825ad5d14b6dd9facd981adc6f9`)  
**Explicit verdict:** **ChangesRequested — 1 P1 + 1 P2. Do not integrate this SHA.**

## Confirmed correction of prior P1

The requested same-launch stale handler-generation fix is materially correct in the examined Phase A source. Primary and ancillary Role-message claims pin `SubjectMessage.HandlerGeneration` to the matching work claim generation; release/terminal paths invalidate it, and both reply APIs plus semantic operation receipts include the expected generation. An old g1 reply cannot mark M Handled/W Completed after the same registered L validly reclaims M/W at g3, even with a refreshed project CAS head. The fix preserves an unrelated active Plan, valid same-launch reacquisition, dependency gate/waiver ordering, downstream D wake only after valid W completion, and lost-receipt exact replay. Dedicated primary message completion also checks generation. The new two-client and repeated-gate negative fixtures cover the prior counterexample.

I reviewed the exact 3-file code diff and the surrounding architectural invariants: dual ledger account-token/project completion proof, deterministic injective demand matching, blocking-first FIFO with independently witnessed time and sequence, SCC diagnostics, hard-edge gating, immutable frozen review/source lineage, F06 external-effect-versus-authorization separation, session UUID/lease registration, and Task/Role handling. The fixes from earlier WQ-01 reviews remain represented in the pure contract. This is **not** a statement that these semantics are already wired to production persistence, Scheduler, browser dispatch, authenticated waiver authority or all 80 §16 E2E cases.

## Findings requiring correction

**P1 — Stopped execution can claim fresh ancillary messages.**  
See `findings/01-stopped-lease-ancillary-claim.md`. `markStalled` sets `Lease.StopRequested=true` while launch State remains Registered and Plan stays Claimed. `claimAncillaryMessage` (lines 884-930) checks only Registered/PrimaryWorkId/Pending, not lease StopRequested or the live primary claim; a stopped L with a *fresh* Project.Version can claim new Role work W or passive Task attention. `completeAncillaryMessage` (lines 942-948) then correctly refuses acknowledgement of that stopped claim. Role W is exclusively claimed/removed from ready demand by a session forbidden to accept new work, potentially stranded until recovery. This violates the accepted §9 one-way STOP rule (line 299). Require live lease and primary fences in both ancillary claim paths; add fresh/stale CAS STOP-vs-claim and reverse terminal/recovery tests. This is a static reachable counterexample, not an executed failing fixture.

**P2 — Passive Task message claim/ack bypasses incomplete index coverage.**  
See `findings/02-passive-task-message-coverage.md`. The `WorkId=None` Task path in `claimAncillaryMessage` and `completeAncillaryMessage` never invokes `completeCoverage` or equivalent subject high-water verification, unlike Role-stage claim via `orderAvailable`. With `MessageCovered=false`, a Task message can still become ClaimedAncillary and Handled. This violates the accepted §5/§11 fail-closed message-index contract. Check verified subject/message coverage at both intake and acknowledgement, and test incomplete coverage, restored coverage and competing producer snapshots.

These are contract defects in the new Phase A semantic reference; neither finding claims deployed runtime impact.

## Independent validation at frozen SHA

| Gate | Evidence |
| --- | --- |
| Head / tree | `Jarvis::worker5-wq01-handler-generation` HEAD exactly `4b8d76eeccf02b2dd12d0291281401447bc6d700`, clean |
| .NET 11 Release focused | `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueContractTests` **57/57 PASS** |
| .NET 11 Release full Server.Tests | `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` **192/192 PASS**, 0 failed/skipped |
| .NET 11 full solution Release | `dotnet build Jarvis.slnx -c Release --no-restore` **PASS**, 0 errors and 2 warnings |
| Base-to-head patch hygiene | `git diff --check 90ed868..4b8d76e` **PASS**, no diagnostics |
| Node/Client tests | Not independently rerun by this reviewer; no PASS claimed |

The two counterexamples are missing negative-space tests and are not disproved by all existing tests passing. No production rollout or all-80-case validation is authorized.

## Disposition

Preserve immutable review/source refs. Worker 5 should acknowledge this verdict; fix both findings and add their negative/reverse-race fixtures on a **new isolated branch and new exact commit**, rerun Release/focused/full checks, publish remotely reachable SHA, submit a **new independent frozen review**, and yield. Do not integrate/rewrite the rejected source or activate migration/Scheduler/deploy from this review.
