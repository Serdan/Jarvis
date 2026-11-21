# Jarvis-managed review and integration snapshots

## Contract

Jarvis publishes each review candidate as a **create-only, verified remote Git ref**
`refs/heads/jarvis-review/<taskId>/<reviewId>/r<sourceVersion>`.
The ledger does not advertise a claimable review until the remote advertises that
exact commit. The canonical review record pins the tuple
`(taskId, reviewId, SourceVersion, ReviewRef, ReviewedHeadOid)`, independent
of the mutable contributor `TaskRef`.

`submit_review` still accepts an explicit `headOid`. To submit the HEAD of
an already committed local source branch, pass an empty `headOid` with its
fully qualified `sourceRef`. Jarvis resolves exactly one live checkout of
that branch, checks that it is not detached or dirty (including untracked
files), and confirms HEAD equals the source ref. An absent/ambiguous checkout
fails rather than guessing. **No development-branch push is needed**.

After an independent review is **Accepted**, `submit_integration` may supply
its `reviewId` with empty `sourceRef` and `submittedCommit` (or the exact
reviewed SHA). It fetches and verifies `ReviewRef` from the approved remote,
then creates `refs/heads/jarvis-integration/<submissionId>` with an
absent-ref compare-and-swap push and post-push exact-OID verification.
Integration submission never changes the target branch or deploys anything.

The canonical integration record pins `ReviewId`, `ReviewRef`,
`ReviewVersion` (immutable source-ref version), `ReviewedHeadOid`,
`SubmittedCommit`, `IntegrationRef`, `TargetHeadAtSubmission` and
`TargetRef`. `SourceRef` is **descriptive only**, never authority for a
new integration.

A review that is Unclaimed, Claimed, ChangesRequested, missing its canonical
SHA, or changed on the remote cannot authorize integration. The submitted
source SHA need **not** descend from the current target head. Independent target
advancement does not invalidate the Accepted source or require a worker rebase
and full source re-review. The integration record captures the advertised target
head at submission for later ancestry verification.

## Integrator procedure (explicit, never automatic)

Use a fresh isolated worktree. Read the canonical submission and accepted
review through Jarvis first. Fetch the integration ref from the configured
approved remote and verify that its SHA equals both `ReviewedHeadOid` and
`SubmittedCommit`. Fetch the current target, verify it descends from
`TargetHeadAtSubmission`, and compose **that target** with the exact reviewed
SHA. Keep the source ref immutable and the original Accepted review untouched.
For example, with an isolated clean integration worktree and substituted
canonical values:

```sh
git fetch --no-tags origin refs/heads/jarvis-integration/<submissionId>
test "$(git rev-parse FETCH_HEAD)" = "<reviewedSha>"
git fetch --no-tags origin <targetRef>
target_head="$(git rev-parse FETCH_HEAD)"
git merge-base --is-ancestor <targetHeadAtSubmission> "$target_head"
# In a fresh, clean integration worktree at "$target_head":
# If "$target_head" is ancestor of <reviewedSha>, use that exact SHA directly.
# Otherwise compose a real two-parent merge (target first, reviewed SHA second):
git merge --no-ff --no-edit <reviewedSha>
test "$(git rev-parse HEAD^1)" = "$target_head"
test "$(git rev-parse HEAD^2)" = "<reviewedSha>"
expected_tree="$(git merge-tree --write-tree --no-messages "$target_head" <reviewedSha>)"
test "$(git rev-parse 'HEAD^{tree}')" = "$expected_tree"
# Inspect the merged diff/scope and run merged-result Release tests.
git push --porcelain origin HEAD:<targetRef>
```

The publication command is an **ordinary non-force push**. If the target moved
before publication, the push must fail rather than overwrite it. Re-fetch,
recompose against the latest target, re-run scope/test gates, and retry a new
non-force push. A normal target race is not a new independent source review.
The direct fast-forward case pushes `<reviewedSha>:<targetRef>` instead.
Never replay an uncertain push blindly: inspect the remote receipt and the
pending publication before choosing the next action.

`complete_integration` verifies that the remote target contains the exact
reviewed source and the claimed integrated commit. A distinct integration commit
must have **exactly two parents**, with the first descending from
`TargetHeadAtSubmission` and the second exactly `SubmittedCommit`. Its tree
must equal Git's conflict-free mechanical merge of those parents. It rejects
arbitrary extra changes, reordered/extra parents and conflict resolutions.
If conflict resolution or nonmechanical changes are necessary, obtain a focused
independent integration-specific review before publication. The present
completion record has no field for such focused approval: **hold rather than
pretend that ancestry alone authorizes the conflict resolution**. Do not re-review
the unchanged source unnecessarily.

The ledger records the Accepted source SHA/ref, submission target head, and
distinct integrated SHA. The first parent of that immutable integrated merge
SHA is the actual composition target, and its second parent is the exact
reviewed source, so both remain reconstructible from the recorded receipt.
Ordinary Git ancestry proves the final published
target contains those objects; it cannot by itself attest hosting permissions
or the original push flags. Host protection and publisher evidence remain
mandatory production prerequisites.

## Host permissions: mandatory production prerequisite

**Ref naming and Git compare-and-swap are not access control.**
Git transport cannot establish who may push a future ref update.
Before deployment, the repository operator must establish and independently
verify Git-host rules (for example GitHub rulesets) covering the *entire*
`jarvis-review/` and `jarvis-integration/` branch namespaces. The rules
must deny contributors creation, update, force-update and deletion of managed
refs; only a dedicated, scoped Jarvis service identity may publish them.
The authorized Jarvis push remote must use that identity, distinct from
contributors' normal credentials. No agent ID is a security principal.

Validate this using two genuinely different remote credentials: a
contributor must fail **all three** create/update/delete probes for both
namespaces, and the Jarvis service identity must be able to create a
new, exact ref. Verify advertised SHA after every service publication.
Do not infer protections from branch names, an authenticated local agent,
a successful push, or generic Git remote diagnostics. If the host or
credential boundaries cannot be verified, **do not deploy or enable this
workflow**. Bare Git test fixtures are transport tests only, not proof
of production host permissions.

## Failure, retries, and retention

A timeout after remote publication may leave an immutable orphan while the
ledger transaction is not yet confirmed. Preserve the original operation
UUID, inspect the exact remote ref/SHA, reconcile the canonical receipt and
retry idempotently. Never silently substitute a new source branch HEAD.
An existing ref pointing to another SHA fails closed. A claimed review is
never replaced; ChangesRequested work requires a new independent frozen
review.

Do not immediately delete a ref following a concurrent ledger conflict:
another submitter may own the same reviewed SHA. Retain orphan, withdrawn,
or superseded managed refs at least **30 days after their last possible
active use** and only prune after checking a fresh ledger snapshot proves
no review, pending submission, or integration references them. Pruning
requires an exact-SHA compare-and-swap deletion; mismatch must refuse
deletion, and the historical ledger record must remain. No automatic
retention job is part of this change; until an audited retention command
exists, leave the refs intact.

## Compatibility and rollout

No wire command or existing positional argument changes. New JSON fields
are additive; older review and integration records remain readable. Older
branch-only integration records have no `IntegrationRef` and preserve
their previous completion behavior. Creating a **new** branch-only
submission is rejected. Historical Accepted reviews without a pinned
`ReviewedHeadOid` / `SourceVersion` need a new independently frozen
review before a managed integration can be requested.

**One-time bootstrap:** this implementation's own review may have been
published by the *pre-upgrade* Jarvis client and therefore has no pinned
SHA fields in its ledger record. Accept that frozen review, integrate its
exact verified SHA through the existing branch-based submission flow (which
may require a one-time contributor-branch push), and only then deploy this
change. Do not try to use the new integration API on that old record or
weaken immutable-provenance checks to make the rollout self-hosting.

Deploy the protocol-compatible client/service updates together, configure
host protection and a dedicated service credential before enabling new
submissions, and verify through two independent checkouts. This change
does not merge, deploy or schedule background cleanup.
