# Reviewer 2 independent frozen WQ04 versioned role authority audit

**Verdict: Accepted — bounded, inert role-catalog/CAS component only.**

- **Task:** `wq04-versioned-role-admission-proof`
- **Review:** `ed89f13401cd4c18bce62b189af79f73`
- **Exact frozen head:** `1cd0b88db631dc4258601e5e531f95c02a34ebd4`
- **Exact parent/base:** `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`
- **Scope:** client-side role-history persistence, pinned same-Git-head preflight, and isolated negative tests. No production Scheduler/Manager/YieldSource authority or integration is approved.

## Independent evidence and source isolation

1. Read canonical reviewer inbox, review details, existing summary and task specification. The request is claimed by Reviewer 2 with version 2 and is bound to the exact frozen head above.
2. Independently inspected the full 444-line `Client/Modules/WorkQueueRoleAuthority.fs` implementation and 288-line `Client.Tests/WorkQueueRoleAuthorityTests.fs` frozen from that SHA, together with the existing `WorkQueueProducers.configureRole` and `LedgerStore.transact` CAS implementation.
3. `git rev-list --parents -n 1` confirms the exact requested base as direct parent. `git diff --name-status` confirms precisely four files: the new authority module and test, plus registrations in `Client.fsproj` and `Client.Tests.fsproj`. Existing WQ04 producer/git-ledger, WQ05 adapter, WQ03 journal and Server code are unchanged. `git diff --check` has no output / exit 0.

## Authority, history and failure-mode review

- **Durable catalog and explicit provisioning.** `records/work-queue/role-catalog.json` is a separate Git ledger record with schema and pinned ProjectId. Missing catalog fails closed, rather than promoting legacy unversioned WQ02 `Project.Subscriptions` to authority. Configuration writes use `LedgerStore.transact` and `Producer.configureRole`, asserting expected catalog revision, exact previous role version and semantic original-UUID request identity.
- **Historical immutability.** The decoder enforces canonical bytes, dense per-role historical versions, current pointers to latest versions, one immutable receipt per published role version, matching project identity, nonempty instructions/subscriptions, and no Manager pseudo-role. Corrupt, deleted, duplicate or noncanonical version/receipt entries are rejected. Nonterminal existing launches must still resolve their original enabled historical role definition.
- **Pinned single-project Git proof.** `loadAt` reads both project and role catalog from one bound snapshot. The proof pins ProjectId, LedgerId, epoch, HEAD, both file blob OIDs, project version and catalog revision; `verifyAt` and `inspectAt` re-read and compare. A future writer must invoke `inspectAt` inside the *same* `LedgerStore.transact` planner as its project mutation. A stale preflight, forged ledger identity or changed catalog does not survive that revalidation.
- **Role uses.** New demand and admission require current enabled role version; admission pins exact current version and rejects duplicate/nonzero-version/invalid prospective launches. Claim uses a registered, non-stopped, not-yet-claimed session and its *historical* subscribed version, rather than automatically inheriting current subscriptions. FirstConnect requires a submitted unregistered launch and its original role. RoleMessage demand requires canonical pending indexed message, correct role subject, queue and work linkage. ReconcileCredits uses an injective bipartite matching over live unclaimed sessions and their historically pinned role versions. New disabled role definitions prevent new launch/demand but do not revoke an older valid version midflight.
- **No false positive authorization.** `RolePreflight.RequiresCanonicalSourceAndAccountProof = true` on every successful inspection. `inspect` alone is explicitly just feasibility against a view and **not** a security or launch permit; a caller-synthesized view must never be used to authorize an effect. The module is not wired to any live Scheduler, Manager or host source route, and a merely nonblank account token is not asserted to prove WQ05 account ownership. Canonical WQ04 source event/high-water, WQ05 account/CAS evidence and WQ03 host continuity remain separate, outstanding combined-acceptance conditions.

## Independent local .NET 11 Release execution

Executed on the exact clean frozen worktree `worker11-wq04-role-authority`:

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueRoleAuthorityTests [--no-restore] -v minimal`

- Two independent local run results: exit code **0**; net11.0; **6 passed, 0 failed, 0 skipped**, ~5–6 seconds test duration.
- Negative tests cover separately missing catalog and WQ02 map non-authority; two-clone revision winner, original UUID replay and mismatched-request rejection; v1 launch/claim pinning after v2 removal and v3 disabled config; RoleMessage subscription/index integrity; stale admission-versus-role-CAS transaction recheck with no mutation written; forged revision/ledger identity and destroyed historical records.
- Worker-submitted summary claims 13/13 combined producer+role tests; I did **not** independently rerun that broader seven-test complement and do not elevate the author claim into separate reviewer evidence. The directly audited six targeted tests pass.

## Nonblocking integration conditions / limits

1. The present component is intentionally inert. It does not by itself provide atomic joins to authentic task/review/challenge/integration source events, account token capacity, cross-ledger high-water, or the runtime transition; none should be inferred from this acceptance.
2. Future combined source review should assert every positive demand/admit/claim/rematch/RoleMessage/first-connect path invokes `inspectAt` in the same transaction, and deny missing or stale source/account proofs, including multi-clone races.
3. An additional explicit post-v2 credit-reconciliation regression would strengthen coverage, although the reviewed implementation uses historical definitions for that matching and the existing v1 claim/version-change tests substantiate the key logic. The catalog is append-only and bounded by ledger file-size constraints; exceeding that bound must remain a failure, not a fallback to unversioned state.

**Canonical decision requested: Accepted for the frozen isolated role-authority module only.** No main push, hosted Actions, live Scheduler/Manager/host mutation, deployment, migration, source edits or Codex performed.
