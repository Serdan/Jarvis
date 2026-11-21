# Scheduler Phase 2: canonical Git snapshot adapter (read-only preparation)

Status: **default off, no dispatch, no live authority**. Task: `scheduler-phase2-native-dispatch-20261121`, Worker 6. Source: `scripts/jarvis-phase2-snapshot-adapter.mjs`. Regression: `scripts/tests/jarvis-phase2-snapshot-adapter.test.mjs`.

## Contract and authority boundaries

The accepted Phase-1 `planLedger(snapshot, nowUtc)` implementation at `34facab5db97b2f238d2415c8f7ae6b59866b754` and review `c2d0f7a1e8b44a9f93d51b0a6f72e4c8` are not yet integrated into `main`. This Phase-2 module does **not** copy that planner or activate the legacy Manager, Work Scheduler, browser schedules, Health, background timers or an alternate task database.

`captureCanonicalSnapshot({ account, projects, observedAtUtc })` is an isolated read-only adapter. The caller must supply separately approved exact remote URLs, account/project and ledger identities, epoch, and a **previously verified canonical Git head** for **every** remote. It will not discover a roster or authenticate approval from repository names, workspace files, arbitrary URLs or browser tab titles. The supplied `observedAtUtc` is a caller observation timestamp, **not authenticated clock evidence**. In particular, this is a preparation seam, not a trusted production endpoint.

For each read, it creates a disposable local bare Git repository and reads only canonical refs:

| Authority | Immutable ref | Canonical content |
|---|---|---|
| WQ07 account | `refs/heads/jarvis-account-admission` | `state.json` (schema 1) |
| WQ02 project | `refs/heads/jarvis-orchestration` | `ledger.json` (schema 2) plus canonical task, attempt, agent, review, Challenge, index, lifecycle and integration records |

It checks the explicit remote ref twice (before and after the read), fetches that exact ref, confirms the fetched commit equals the advertised head, checks canonical ledger identity/epoch, and proves that the previously trusted head is an ancestor. It rejects malformed paths, non-regular tree objects, malformed JSON, oversized blobs, invalid identity/binding and inconsistent lineage. No source worktree, local last-seen cache, SQLite, FeedbackStore or local browser registration is considered canonical evidence. It performs no Git publication.

**A present read is not proof of a complete account-wide set.** There is not yet an independently authenticated account project membership registry available to this adapter. Even an explicitly supplied array of two projects is only a configuration request and cannot demonstrate that no third project's reservations or browser sessions exist. Therefore `snapshot.completeness.projects === false` until that separate authority exists. Held WQ07 tokens referencing absent configured projects produce explicit diagnostics; an empty WQ07 token list does not prove there are no browser sessions.

For each Held WQ07 token naming a configured project, the adapter compares its `projectLedgerId` and `projectLedgerEpoch` with the **independently fetched and verified** project ledger header, not only the supplied project ID. Disagreement produces `HeldTokenProjectLedgerIdentityMismatch` with token/project identity and both source heads. Duplicate configured project IDs fail closed. Neither a matching token nor its `projectAuthoritySha256` is proof of a native browser session or project membership authority.

## Exact normalized mapping and deliberate incomplete evidence

The returned `snapshot` follows the Phase-1 schema-v2 layout and carries exact account/project commit SHA, ref and observation timestamp, with per-domain `completeness` and `diagnostics` identifying unresolved joins.

| Git-ledger record | Projected value / boundary |
|---|---|
| Current `ReviewTask`, every canonical `ReviewAttempt` | Task lifecycle/current attempt ID; exact owner, version and assignment generation; `Queued` becomes normalized `Ready` |
| `ReviewAgent` plus current `AssignmentIssue` | Exact task/attempt/generation assignment; `Working`, `Ready`, `NeedsWork` and `Parked` distinguished; a missing or conflicting issue is incomplete, not an inferred assignment; unassigned `Working` with null disposition is explicitly unverified/incomplete (`UnassignedWorkingAgent`) |
| Attempt source binding | Exact repository, owner, source/target refs and base commit; **`status: "Unverified"`** until the source ref and physical lease are separately checked against approved repository authority |
| `BranchReview` | `Unclaimed` becomes `Pending`, and `claim:null` means no named reviewer; **`frozen:false`** until the exact source review ref and reviewed code commit are independently verified |
| `AttemptChallengeWait` plus Challenge record and both owner/dependency indexes | `Waiting` becomes `Pending`; selected-generation owner and dependency indices must exactly match the canonical wait. An audited N-to-N+1 requeue keeps indices at immutable N; manual zero-dependency waits have no current-generation indices. A global reverse-index reconciliation rejects orphan/new-generation paths and identity conflicts while retaining matching older historical indices |
| `RequeueChallengeBlockedAttempt` lifecycle audit | Preserves canonical operation ID, ledger sequence, actor, Blocked-to-Queued transition, owner identity, and N to N+1; index remains at immutable N after requeue |
| `IntegrationSubmission` | Preserves original submission/review/source IDs, but canonical record has no attempt generation; normalized `assignmentGeneration:null` with **`completeness.submissions:false`**, not a guessed value |

An unverified source lease, unfrozen review ref, or incomplete submission is **not** evidence suitable for a Phase-1 candidate. They are visible to help diagnose missing authority without fabricating a credible dispatch. Malformed or contradictory canonical records turn the relevant domain's completeness false. There is no projection that executes a Challenge release, requeue, reviewer claim or integration.

Reverse-index completeness is checked against **every** by-owner and by-challenge path in the verified Git tree, not only paths visited at the selected wait generation. A schema-valid orphan path without a canonical wait, a new/current-generation path outside an audited requeue's immutable N, or an index with a conflicting owner, attempt or generation is incomplete. Matching older generations are retained as historical records, never projected as a new current wait or dispatch authority.

The WQ07 account ledger's `MaxConcurrentAgents` and Held/Released tokens are for admitted Work Queue launch tokens. They are **not an authenticated cross-project registry of current ChatGPT worker/reviewer browser tabs, unknown submissions or launch reservations**. Do not map these to `configuredLimits.worker/reviewer`, `sessions:[]`, `reservations:[]`, or a claim of free worker/reviewer slots. The adapter deliberately returns `accountCapacity.complete:false`, `configuredLimits:null`, `sessions:null`, `reservations:null` (and the same for project capacity). Even a zero-held-token account cannot prove dispatch capacity.

This also means no trusted per-account capacity, reservation, source-lease, review-claim CAS or original-browser submission boundary is available yet for this scheduler. The existing WQ07/WQ04/WQ03 primitives must only be connected after independently reviewed Phase-1 integration, an authenticated complete project/session membership and capacity source, and explicit operator permission.

## Default-off invocation

`phase2DefaultOffDiagnostic(capture)` returns `enabled:false`, `sideEffects:0`, empty worker/reviewer candidate arrays and explicit `Phase1NotIntegrated` and `AuthenticatedWorkerReviewerCapacityUnavailable` holds plus canonical adapter diagnostics. It has no mutable state, reservation or launch callbacks. No code path uses `planLedger` until the accepted Phase-1 module is integrated and explicit activation is approved. It is deliberately impossible to flip a Boolean to begin automatic dispatch with these incomplete inputs.

Before any later executable Phase-2 integration, require separately reviewed seams that prove, at the **effect** boundary: a consistent complete account/project snapshot; authenticated active/unknown browser sessions and reservations across *all* projects; reviewer claim identity and current immutable review ref; original worker assignment/owner/lease and version CAS; serialized capacity reservation and stable idempotency key; a durable Submitted/Uncertain outcome for an unacknowledged browser send; no retry of unknown effects; and a visible one-pass operator-authorized dry run without recurring background activation. Worker 5 integration-protocol, Worker 8 authenticated activity ingress, Worker 11 managed jobs, and Worker 18 BrowserSchedules are independently owned and not edited here.

## Regression / reproducibility

The Node tests create **one real disposable bare account Git remote and two independent disposable bare project Git remotes**, with writer checkouts and immutable canonical commits. They assert exact head/source identity, Ready vs Working vs NeedsWork, unassigned Working/null disposition, missing assignment issuance, unclaimed review, no inferred source trust, N-to-N+1 lifecycle/index provenance, manual zero-dependency holds, orphan by-owner **and** by-challenge reverse indices without canonical waits, unexpected N+1 indexes in both namespaces, historical index retention, missing and unexpected reverse-index failures, missing submission generation, absent/foreign project membership, Held token ledger ID/epoch mismatches, invalid identity/ref/time/ancestor and default-off no-effects. There are no production remotes or browser calls.

Commands from the repository root:

```sh
node --test scripts/tests/jarvis-phase2-snapshot-adapter.test.mjs
node --test scripts/tests/manager-heartbeat.test.mjs scripts/tests/jarvis-phase2-snapshot-adapter.test.mjs
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~WorkQueueSchedulerHeadBindingTests|FullyQualifiedName~WorkQueueSchedulerHostLoopTests'
dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter 'FullyQualifiedName~Scheduler'
```

Record exact OS exit, source SHA, test counts and independently reviewed verdict before claiming these gates complete. A passing disposable fixture is not authorization to merge, start a session, deploy or activate Scheduler.
