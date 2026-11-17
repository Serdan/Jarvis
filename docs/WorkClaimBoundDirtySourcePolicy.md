# WQ11 — claim-bound dirty Implementation source evidence (default-off)

## Why this exists

The **original** WQ03 source claim is deliberately strict: a trusted, linked, detached, **clean** generation checkout at the canonical continuation SHA is verified inside the winning WQ02 claim CAS. After an authorized source edit, that checkout is necessarily **dirty**. Reapplying the clean-at-claim validator for every subsequent operation rejects legitimate implementation work. Removing the validator would instead permit a caller-supplied or stale checkout.

This narrow adapter separates **historic claim authority** from **post-edit evidence**, without changing WQ03 or the existing WQ11 scope writer. It is not a new runnable source system.

## Authority and invariants

`WorkQueueClaimBoundDirtySourcePolicy.inspectDisposable` is internal and **disabled by default**. It takes an explicit `enabled` fixture flag; no production dispatcher, Scheduler, browser transport, Git push or old Agent RPC invokes it. A positive `EvidenceVerifiedForFixture` is *not* permission to run a tool, mutate a Git ledger, publish a WQ02 checkpoint, resume a source job, or hand off to the next generation.

On every inspection, it independently loads the original verified project Git CAS, requires the current revision to exactly equal the immutable revision history, verifies credential hash in fixed time against the original authenticated admission, live launch/lease/claim, stage, grant expiry/revocation, Work/stage/claim generation, source lineage, original source SHA/ref and separate target. It rejects altered requested scope, foreign project/repository/remote, stale lease, STOP, wrong credential, missing history or missing original source claim. The adapter does **not** accept agent labels or a claimed `dirty=true` bit as authority.

It rechecks the **physical path from the original grant**, including the generation-qualified linked worktree name, registered detached worktree entry, same object store, unchanged original HEAD, approved repository URL and remote source ref/continuation, and no symbolic-link ancestors. File paths are restricted to checkout-relative, no traversal, no symbolic-link parents/targets. The initial clean-at-claim requirement remains enforced unchanged by WQ03/WQ11.

A historical FileEdit observation must be a **host-owned, awaited** `WriteFile`/`PatchFile` from the bound checkout. The original `AgentCommand` (including exact destination and payload) is hashed with the existing `SourceEffectRouteBridge.requestSha256` function; its digest must match an anchored source journal record with the *same original operation UUID, project/ledger/remote/claim scope, FileEdit kind and terminal proof* `host-awaited-file-edit-returned-v1`. An invented path, digest, caller label or completion flag is insufficient. Missing, prepared, uncertain, different-digest, differently scoped or corrupted evidence returns a typed rejection or `HostProofUnavailable`.

## Explicit limits (NO-GO)

* Successful file-edit evidence proves **only** that exact awaited original host handler returned successfully. It is not a globally complete inventory of edits; the journal inspection intentionally continues to report `RecoveryBlocked=true` due uninstrumented mutation routes.
* Tests and managed processes do not have an independently authenticated process-tree termination/exit receipt. `TestCompletion` always returns `HostProofUnavailable`, even for normal process exit messages. No speculative test completion or later continuation authorization is granted.
* A read-only checkpoint inspection may observe the original terminal file-edit receipt; it does not invoke or authorize a new WQ02 checkpoint CAS. A real checkpoint needs a separately reviewed host→journal→winning-CAS binding and a complete original-operation inventory.
* Git commit/push/merge, subsequent source continuation and their terminal effects cannot be inferred from a host file edit or a Git CAS. A Git CAS does not make the filesystem mutation atomic. Source handoff still requires WQ03's independent remote/ref validation and existing recovery-blocked policy.
* No production private host proof, source-effect route completeness, original source UUID transport for all routes or process-tree attestation exists. The public source runner, scheduler, migration and cutover remain **NO-GO**.

## Local test evidence

`Client.Tests/WorkQueueClaimBoundDirtySourcePolicyTests.fs` creates two clones of a bare Git remote with an actual WQ02 credentialed source claim and WQ11 immutable grant. It checks clean original claim, the audited host awaited file edit and resulting dirty checkout, identical historical evidence under both clones, and negative foreign same-name checkout, stale generation, credential, ref, moved HEAD, symlink, path traversal, missing/uncertain journal and revocation. Existing `WorkQueueReservationPermissionsTests` and `SourceEffectHostBindingTests` remain independently in place.

Release run: `dotnet build Client.Tests/Client.Tests.fsproj -c Release` then `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll --TestCaseFilter:FullyQualifiedName~WorkQueueClaimBoundDirtySourcePolicyTests`. All are disposable local fixtures. Never run hosted GitHub Actions for this work.
