# WQ03 — host-bound original source-effect ID (nonproduction file-route prototype)

Scope: `worker6-wq03-host-bound-uuid-3627`, an isolated descendant of the independently Accepted diagnostic source journal `e7a8a3ed`. This is a **source-only review candidate**. It neither modifies nor authorizes the separately reviewed WQ03/UI combined commit.

## New transport

- The original **caller-retained UUID** is an optional `sourceOperationId` parameter on WriteFile and PatchFile only. It is validated as a nonempty UUID and serialized into a separate SignalR `ReceiveSourceCommand(correlationId, commandJson, agentId, originalOperationId)` call. Correlation ID, ordinary ledger operation ID, tool job ID and generation UUID are never used as substitutes.
- The client preserves this exact field through the bounded command queue and tool-job promotion. The ordinary ReceiveCommand contract is unchanged; when the source field is absent it is not synthesized.
- Experimental host-bound execution requires exact local `JARVIS_WQ03_NONPRODUCTION_HOST_BOUND_FILE=host-bound-file-v1` and an explicitly configured absolute `JARVIS_WQ03_SOURCE_JOURNAL_ROOT` outside the project workspace. Both flags are **off by default**, with no production activation path.
- Once this mode is explicitly enabled, mutation-capable commands to `wq03-*` generation checkouts **without** an original UUID are denied, rather than falling through unjournaled.

## Independently bound source scope

The client resolves the registered worktree and re-loads its approved trust-binding head, canonical project Git-CAS snapshot, and source remote identity. It accepts **one** canonical Implementation work with state Claimed, a registered matching launch/primary work, coherent project/repository identity, clear dependency gate, and the exact `wq03-{workId}-{launchId}-g{claimGeneration}` checkout name. The journal scope is generated *from this state*, with exact project UUID, ledger UUID/epoch, approved remote fingerprint, immutable source ref and claim generation. No caller-supplied scope or authoritative `TrustedScope` object enters the RPC.

The original caller ID, computed command SHA, and generated scope are committed to the durable host SQLite/EXTRA journal **before** the awaited file-edit handler. A second dispatch of the same UUID, a changed request, mismatched scope, or an uncertain first result refuses replay. Only a successful awaited host-owned WriteFile/PatchFile can produce that narrow terminal file receipt; process/Git/remote outcomes remain unknown.

## Explicit hard blockers

- This transport does **not** prove the authority of a caller-retained UUID if a malicious/buggy caller invents a *different* UUID for the same underlying effect. A future canonical upstream intent/admission protocol must bind retries to the original.
- WQ03 `YieldSource`, production Scheduler, migration, deployment, source ref protection and WIP immutability remain **disabled/unproven**. Existing `verifySourceExternalEffectsAndRefProtection` unconditionally denies YieldSource.
- StartJob, RunCommand, RunProjectTask, ImportFile, GitCommit, push/merge/retry and other source-capable routes have **no** positive effect-terminal proof here. In opt-in mode they are denied for the generation checkout if they lack the original UUID; an ID supplied on these routes is also rejected.
- Host WIP immutable retention, authoritative original push/process-tree reconciliation, durable canonical RecoveryBlocked CAS and a complete legacy/out-of-band mutation-route inventory need independent evidence. `inspectForHandoff` remains `RecoveryBlocked=true` even when all known file-effect records appear terminal.
- Local trust binding and canonical project read are performed immediately before a source file edit, but this does not establish an atomic lease over arbitrary external edits or prove absence of concurrent host changes. No integration or cutover is authorized by this prototype.

## Local review checks

Build .NET 11 Release for both Client.Tests and Server.Tests. Run bounded client NUnit categories for SourceEffectJournal, SourceEffectRouteBridge and SourceEffectHostBinding, plus server service/dispatch regressions. Negative cases cover wrong project, approved remote mismatch, stale generation/checkout/launch/gate, UUID replay and scope/rebind, unsupported routes, and two-client race. Production host/Git protection is not tested or claimed.
