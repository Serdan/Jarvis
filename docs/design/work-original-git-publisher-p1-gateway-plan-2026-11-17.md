# P1 original-Git publisher gateway — implementation design and authorization boundary

**Status:** DESIGN ONLY; proposed next slice for `work-original-git-publisher-admission-protocol`. No implementation, remote provisioning, credentials, production adapter, migration, or cutover is authorized by this document.

**Source base:** integrated P0 `a5cb1906ab5402d047d525e495cbc38fe750cd5c` (reviewed `70ed7631b9a0f25e5cba3f6822b453607d65e3ad`, review `e3eab74cbdf04b12833f7ae7285dc016`, Accepted for *P0 only*). Companion evidence: `docs/specs/work-original-git-publisher-admission-audit-2026-11-17.md` and `docs/specs/work-original-git-writer-fence-prerequisite-2026-11-17.md`.

## 1. Decision and non-negotiable boundary

Design a **publisher-owned Git mutation**, not a permission token that an old client receives before performing an ordinary push. Only an authenticated server publisher may possess the credential/transport that can mutate every protected original project/account/source-effect ref; the server serializes each real ref CAS and exclusive barrier. The ordinary client sends a typed request and receives a durable operation receipt (or an unresolved result). A still-valid, unmodified stale client must be denied at the *remote* even if it never contacts the publisher.

There are two independent obligations:

1. **Logical publisher linearization:** authoritative server journal + one serialization domain for exact CAS, barrier and unresolved effects. A local lock, SQLite state or type validation alone never excludes an old Git credential.
2. **Remote exclusion:** independently verified host rules/credential inventory or a receive-pack gateway that denies every bypass actor and ref channel, including Git/API/SSH/Actions, direct repository filesystem access, alternate source refs, creation/deletion and in-flight pre-fence publication.

Both are required before claiming positive barrier evidence. The current hosted GitHub configuration and complete client/credential population have *not* been audited. Consequently production authorization and C3 promotion are **HARD NO-GO**.

Do **not** create a second orchestration database/service just for this feature: the target Work design already uses one server SQLite authority. A disposable P1 fixture may use an isolated temporary SQLite database behind a storage-neutral internal publisher interface; a later approved production adapter must share the host's authoritative transaction domain and recovery policy, while preserving the existing unrelated FeedbackStore. No ordinary Git operation may become a hot-path authority for new SQLite Work.

## 2. Suggested minimal code boundary

The existing `Server/OriginalGitPublisherAdmission.fs` defines `OriginalGitPublisherTarget`, `OriginalGitPublisherIdentity`, `OriginalGitPublisherCas`, `OriginalGitPublisherFenceRequest`, `IOriginalGitPublisherAuthority`, and syntactic/trusted-inventory consistency predicates. These are **inputs**, not authenticated evidence. Do not modify the accepted P0 module unless a specific compatibility reason is independently reviewed.

Proposed additive internal modules (exact names to validate in design review):

- `Server/OriginalGitPublisherState.fs`: pure state transitions for `Active -> FencePending -> Fenced` and `RecoveryHold`, operation replay and typed errors; no IO and no positive witness constructor from caller booleans.
- `Server/OriginalGitPublisherStore.fs`: durable transactional publisher journal with monotonic global sequence, exact physical ref keys, operation UUID/digest, admitted epoch, old/new head, Pending/Applied/Rejected/Unknown, source-effect IDs and genuine pending-intent high-water. Implement against a disposable SQLite fixture first; use the eventual Work host SQLite authority by an explicit adapter, not a parallel production DB.
- `Server/OriginalGitPublisherExecutor.fs`: an authenticated gateway that owns the Git push/update mechanism, serialized with state transitions and barrier acquisition. Its remote executor is injected in tests and is never exposed as a general client-side `mayPush` boolean. It must atomically pin authority inventory/ref identity to each operation.
- `Server.Tests/OriginalGitPublisherGatewayTests.fs`: isolated, entirely disposable two-project + one-account remotes, uncooperative stale clones, fault injection, restart and race proofs.

These names are a design sketch, **not a lease to edit them yet**. The only narrowly authorized original client compatibility points, if separately reviewed, are the actual publication functions in `Client/Modules/LedgerStore.fs` and `Client/Modules/AccountCapacityLedger.fs`: initialization, direct record, transaction, review/integration ref push/delete and account initialization/CAS. Their default-off behavior must remain byte-for-byte equivalent from the caller's perspective. Do not attempt a client pre-push permit as an intermediate positive solution. No changes to the importer, Scheduler, backup, Recovery, API or production transport configuration.

## 3. Authority, protocol and ref coverage

An authenticated publisher derives (never trusts caller-supplied) the account/project/device identity, publisher ID, protocol version and credential generation from a verified connection and server-side registry. It binds canonical repository ID, exact push endpoint, approved remote fingerprint, ledger ID/epoch, ref name and purpose to an independently maintained trusted inventory. The inventory is keyed by **physical (repository, ref)**; conflicting classifications/epochs are rejected. It covers original project CAS, global account CAS, review/integration/source-effect refs, any dynamically created ref, deletions and API writes. Ref namespaces that cannot be exhaustively protected are a NO-GO, not an implicit wildcard allowance.

Requests carry original operation UUID, canonical request digest, exact expected/new OIDs, source-effect operation UUID where applicable, and relevant durable original intent/effect high-water. The server persists `(authenticated identity, operation UUID, request digest, target, expected/new OID, epoch)` and a monotonic journal sequence. A repeated UUID with identical envelope returns its *same durable outcome*; changed identity/request/target returns `ReusedOperationId`; an Unknown outcome is never retried as a fresh push or silently declared Applied. Existing source-effect and old-client pending-intent history cannot be inferred from the new journal alone: absent coverage is an unresolved import/fence prerequisite.

Before issuing a positive remote admission, verify all credential channels and privileged bypass roles at the enforcing host, not merely the approved Git URL string. Remote response + current head alone is not an all-client effect witness; independently read durable authority records and Git heads.

## 4. Linearization and crash states

Use one serialized publisher transaction/critical section for *every* real ref mutation and the initial exclusive barrier decision. Do not grant an admission, unlock, and then call `git push`: that creates a TOCTOU gap. A durable prepare receipt is written **before** the external Git effect and the effect stays owned by the same publisher serialization lane until confirmation/uncertainty is recorded. Any transport failure after possible publication enters `Unknown` / `RecoveryHold`; do not release the fence based on a missing receipt.

Suggested state transitions:

| Current | Action | Required result |
| --- | --- | --- |
| Active | new validated CAS | prepare durable operation, serialize actual remote CAS, confirm against trusted Git remote, persist exact receipt; uncertainty -> RecoveryHold |
| Active | barrier requested | persist FencePending first, stop admitting new publication across all account/project/source scopes; wait for or reconcile every previously issued operation |
| FencePending | any original mutation or UUID replay that could publish | remote denies; only independent reconciliation reads are allowed |
| FencePending | all authorized channels/ref heads/high-water resolved | record monotonically increasing Fenced epoch and immutable scope/remote-head inventory; release barrier receipt only after independent readback |
| FencePending | missing scope, in-flight/unknown effect, unverifiable host rules | remain fenced/pending; no positive quiescence receipt |
| Fenced | original mutation, including alternate ref or delayed old request | deny at remote; no ref advancement |
| Any | crash/restart or stale snapshot | recover durable state and Git refs; if a state transition could have published without a recorded result, RecoveryHold until explicit reconciliation |

The server must *not* claim that a single SQLite transaction can encompass a remote Git push. Model the effect as a journaled, reconciled external operation; prevent further operations/barrier completion on ambiguity. Where two repos must be fenced, a durable global FencePending state is established before any per-ref transition, and a partial account/project fence remains nonpositive and closed to publication.

For a disposable bare-Git fixture only, a publisher process with exclusive filesystem access may call `git update-ref <ref> <new> <old>` while holding the publisher serialization lock. A blanket pre-receive rejection hook stops stale clones using normal receive-pack. This *does not* prove the fixture excludes a clone with direct bare-repository filesystem privileges; test that explicitly as a missing host-permission guarantee. Do not pass an easily replayed magic push option or local environment variable as proof of authenticated remote admission. For GitHub.com the analogous enforcement requires approved host rules/sole writer identity and external verification; custom pre-receive hooks on disposable bare remotes are not evidence of such provisioning.

## 5. Required hermetic tests and proof separation

Write red-first tests, then implementation, with fixed source SHA and .NET 11 Release results:

1. Two separate project bare remotes and one account bare remote, two active client clones and a **still-valid stale** clone; each stale clone demonstrably writes before installation and is *remotely rejected* afterward without consulting any Jarvis client code. Test project/account CAS, review/integration ref creation/deletion, alternate refs/tags and unchanged remote heads.
2. One-winner concurrent CAS; exact old/new OID precondition; two same-UUID identical replays return one receipt; different digest/identity/device/epoch and duplicate physical refs reject. Same displayed project name across distinct devices never aliases authority.
3. Barrier versus in-flight CAS: execution immediately before barrier is either durably accounted for or unresolved; once barrier linearizes, no original publication may become visible. Test multiple interleavings and deliberately delayed remote response.
4. Fault injection before durable prepare, after prepare/before remote, after remote ref advancement/before receipt, during barrier installation, after FencePending and across restart. Unknown may remain blocked; it must not silently succeed or republish.
5. Incomplete trusted inventory/credential list, unsupported host enforcement, unavailable remote, missing original pending/source-effect journal, wrong ref/remote fingerprint/epoch, forged fence receipt, divergent heads and partial account/project coverage must all fail closed.
6. Legacy off-path regression for original client project/account publication and source-effect flows; no production config touch, no live Git activity, no Scheduler/Work activation. Run focused and adjacent Server.Tests/Client.Tests in Release and `git diff --check`.

Separate evidence in every review: **(A)** synthetic pure state-machine correctness, **(B)** disposable bare-remote enforcement of *normal Git push channels*, **(C)** independently verified hosted authority including all credentials/alternate transports. A+B never imply C. Full C3/production GO requires C plus separate operator approval.

## 6. Ordered delivery gates / explicit decisions

**P1a (this document):** independently review the authority/trust model, minimal module interfaces, mutation serialization design, source ownership and test matrix. No production code is accepted by a design review.

**P1b:** only after design/lease approval, implement pure state machine + isolated durable fixture with red-first remote negative tests and fault injection. Preserve current source and default-off behavior. Submit one exact-source review and report any test unavailable.

**P1c:** separately review the narrow original project/account CAS compatibility seam, route all relevant new-client mutations to the publisher, and test off-path behavior. No direct legacy credential is revoked yet; hence remain NO-GO for actual all-client fence.

**P2:** prove full multi-client remote exclusion with disposable host permissions, restart/unknown outcomes and source-effect journals; investigate hosted GitHub rule/credential prerequisites separately. The operator must authorize any *real* provisioning, recredentialing, remote freeze and C3 cutover. A design review or accepted hermetic test is never implicit permission.

**Questions for Lead/operator prior to positive gateway work:** Who owns the canonical privileged remote publisher credential? Is the intended deployment GitHub.com with protected refs and a publisher-only GitHub App, or an exclusively controlled receive-pack host? Can **every** original ledger and source-effect ref (including dynamic/alternate refs), transport and administrative bypass be covered? Is original client credential revocation feasible without interrupting current production usage? Until affirmative, source-attested answers and an independent proof exist, the final answer is **HARD NO-GO** for a positive original-Git quiescence witness.

## 7. Scope exclusions

No direct edits here to `Server/WorkLegacyImportFence.fs`, Work SQLite schema, Scheduler, Recovery, backup, API, production remotes/credentials/rules or live Jarvis ledgers. No server restart, deployment, GitHub Actions, hosted tests or automatic activation. Preserve the accepted P0 audit and integration unchanged.
