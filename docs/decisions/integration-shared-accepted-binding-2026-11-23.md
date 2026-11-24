# Shared Accepted review integration binding (source-only)

Task: `integration-shared-accepted-review-binding-20261121`.
Isolated base: `9f6b9760955f0acb2673e2cab8b8a852ec4dd2ac`. The independently
Accepted trusted-writer prerequisite `b6559a27afcee954bad73772f2fd9c1db7ced117`
is in that ancestry. This is not evidence of a managed receipt for it.

## Contract

The existing task-bound UUID-only `submitIntegration` entry point and its
canonical request bytes remain unchanged. The typed core
`submitIntegrationWithReview` accepts either `TaskBound(reviewId)` or
`SharedAccepted(reviewId, approvalOperationId, reviewRef)`. Shared always
resolves `_reviews` explicitly, not by trying one namespace and falling back
to another. Its source ref, source SHA and immutable review ref must all be
explicit and exact. The destination must be a real task, never the shared
namespace itself.

Use the existing canonical Operator Challenge response, not a new authority,
boolean flag, agent response, free-text similarity or an inferred task link.
The response evidence is the exact serialized
`SharedAcceptedBindingApproval` (kind `SharedAcceptedIntegrationBinding/v1`,
schemaVersion 2), produced by `sharedAcceptedBindingApprovalText`.
The challenge must name the real task; its original response operation must
be `RespondOperatorCondition` with authenticated `operator:` actor, an
Operator-owned satisfied condition, matching original event and response
sequence. The operation UUID, original ledger commit, and exact canonical
request are checked. No approval response is created by this source change.

Approval pins the task, original historical attempt, assignment generation,
spec blob, source repository ID, shared review UUID/ref/source version,
reviewed head, descriptive source ref, base and target, owner, independent
reviewer, decision sequence and operation UUID. The actual decision commit
must contain the matching Accepted review and matching historical assignment
and issue. Subsequent review acknowledgment is permitted; another review
version/decision, unrelated assignment, conflicting repository or pinned
source lease is not. A historically unbound attempt is not a lease; it needs
the same explicit exact Operator-approved mapping. The actual immutable managed
review ref and reviewed commit are fetched and verified separately.

The canonical task's existing IntegrationSubmission gains optional
`sharedBinding` with approval and submission operation UUIDs. The exact
review ref already stores the namespace; no duplicate review or parallel
integration database is needed. The shared submission request hash includes
the namespace, immutable review ref and approval UUID, as well as exact source
ref and SHA. Old task-bound operations retain their original hash.
Completion and WorkComposition acceptance recover the original submission
operation from canonical path history (so deleting the shared marker and
changing the ref cannot downgrade it to task-bound history), compare the
original pending receipt and exact request, and revalidate the immutable
decision and approval. Completion compares the originally verified pending
blob at ledger CAS; the existing verified non-force Git publication,
mechanically clean two-parent composition, stale target, uncertain-effect
journal, completion operation provenance and composition graph CAS remain
the authority. No raw generic-transact path is made authoritative.

The trusted-writer threat model remains the one in
[integration-trusted-writer-managed-acceptance-2026-11-21.md](integration-trusted-writer-managed-acceptance-2026-11-21.md):
a principal with unrestricted authority to rewrite both canonical ledger and
Git refs is outside this contract. The independent review, Operator challenge
and original operation checks do not claim cryptographic independence from
that writer.

## Read-only historical identity evidence

Observed canonical ledger snapshot `b2aa99b4e9de7ec18ab532580ddf44f52bc5155d`.
All five managed refs below were separately observed via `git ls-remote`
and matched the listed exact SHAs. For the shared cases the reviewed source
version is 1 and the ref is
`refs/heads/jarvis-review/_reviews/<review UUID>/r1`. The historical attempt
records and assignment issues at the original decisions are consistent with
the following candidate mappings; this is evidence for an Operator to
evaluate, **not** approval or publication authority.

| Real task | Review UUID / exact reviewed SHA | Original decision sequence / operation UUID | Historical attempt / generation / source lease |
|---|---|---|---|
| scheduler-core-planner-20261120 | c2d0f7a1e8b44a9f93d51b0a6f72e4c8 / 34facab5db97b2f238d2415c8f7ae6b59866b754 | 9468 / 1b247362-29db-43d3-a881-3eefc39135b4 | 20261120130540008000000000000911 / 4 / exact pinned lease |
| challenge-accepted-tests-main-rebase-20261120 | fca0b69e4f1d4a439c951c9a4b2e7d61 / 50bf6da84abcd03cc65b631f8849d873715cfcbb | 9548 / 6211db3f-fb16-4e0a-bdc8-d815c60e5437 | 90d374bbf0b844ef9af2958774419532 / 1 / exact pinned lease |
| p1-organon-project-owned-browser-schedules-20261119 | 7883e0fe26c0ede07782c4c686ed1a2d / 7883e0fe26c0ede07782c4c686ed1a2df836fade | 9503 / d08b7d16-9f80-48a2-876b-a3571d67b4fd | f918bb032a36468582b4efaab699b3c1 / 4 / unbound, checkpoint 9474 names exact SHA |
| scheduler-native-account-reservation-cas-20261121 | b73e6c0d8f2145a99c6740e2f3a851bd / 5607baf9913f4976add13c984e0182906d4fe8c7 | 10769 / 20fc2e7a-5c1d-4009-896b-b987423298e4 | 8b1a7f0d608443178ac2f277bc658d21 / 1 / unbound, checkpoints 10727 and 10864 name exact SHA |
| browserbridge-sourceprojectname-fixture-compat-20261121 (separate task-bound correction) | 238a528fd030b10d601fdc12695f7c05 / 238a528fd030b10d601fdc12695f7c054e2379b5 | 9704 / 874d063a-f351-462f-b1ac-33dbc860b74d | already task-bound review; no shared binding |

Original decision commit locators (same order):
`8f9ba3e1fd0605e716151075817cdf999993b7f4`,
`135005e91760a38f8db1900d4cc9824e975a2e3a`,
`f403f1f777c1539b9fcaff0adfde8601f8d702f4`,
`b8371013d017b0bb8c491af59296d467b8f2b4eb`,
`31fb592ffac1f641af13033b293547f2231568aa`.
Canonical review objects record the exact original TaskRef, base, main target,
owner and reviewer; the historical decision request has the expected
`DecideBranchReview` schemaVersion 2 envelope. The two unbound owners'
checkpoints provide supporting historical linkage, not independently
authorized source leases. No actual source-task approval was issued.

## Gates and deployment boundary

The parameterized two-clone disposable bare-Git tests retain all five real
task/review UUIDs and descriptive branch names but create fresh local fixture
commits, decisions and Operator replies. They never mutate the original
reviews, refs, tasks or schedules. Cover missing/mismatched/agent-only
approval, fake shared task, wrong task/generation, v0, ChangesRequested,
self-review, branch drift, wrong SHA, exact retry and changed intent,
historical task-bound direct compatibility, target advance, mechanical merge,
extra tree edits, receipt downgrade/tampering and composition acceptance.

The independently Accepted core commit
`03bc394272002dd0198674c40d1fa8e48191c2e9` (review
`b7c1827c8c4c44619fc78bebf5092d51`, decision 12659) retained the old wire.
Lead's Decision B on challenge `9462a0c5908a4d698b872ad8e1e1653a`
authorizes this additive, source-only transport completion and exact integration
reconciliation cases, without changing Worker 23's launch-template matcher.

The existing SubmitIntegration tool takes three optional strings:
reviewNamespace, reviewRef and bindingApprovalOperationId. Omitting all three
keeps the old wire action and six-field canonical request. Providing any
selects the distinct SubmitSharedAcceptedIntegration wire action; older clients
reject the unknown action instead of silently ignoring the shared locator.
The new client rejects incomplete locators, wrong namespaces, or shared data
on the legacy action. It uses the existing TaskWrite envelope and Integration
manual-grant/activity classification, not a new tool or authorization class.
Both actions produce canonical SubmitIntegration ledger operations. Completion
has no new caller-controlled locator: it resolves the original submission.

Pending-intent reconciliation checks the exact local action, canonical
operation kind, project, actor and original operation UUID, complete request
identity (including all nine shared fields), and the single task integration
path, receipt status, sequence and matching change/effect digest. A shared
submission's receipt must also name the exact review ref. Completion matches
its three-field request and Integrated receipt. Mismatches and absent
operations retain the uncertain local record; reconciliation never replays a
Git effect. Disposable fixtures simulate lost acknowledgments of successful
submissions and completions, wrong command identities and absent operations.
No unrelated notification, settings, pending-intent or WorkQueue protocol
machinery was modified. Independent source review and
separate Operator publication approval are mandatory. W14 and Operator
challenge `69b0752cce6f4905823bff2c1d170444` remain the live boundary:
no production submission, actual source-task approval, target push,
deployment, or Health/Manager/Scheduler activation is authorized.
