# Original Git publisher admission — Worker 16 source audit and implementation gate

**Date:** 2026-11-17
**Task:** work-original-git-publisher-admission-protocol, generation 1
**Source baseline:** origin/main 2cf1aa3625661aa24aba5c2118764afafe31e6ca
**Status:** P0 source audit and proposed remote protocol. **HARD NO-GO** for live exclusive fence, all-client witness, C3 import, or SQLite promotion until authenticated remote publication controls and independent proof exist. This document changes no production configuration.

## 1. Boundary and verified observations

The original project's authoritative CAS branch is refs/heads/jarvis-orchestration, maintained by Client/Modules/LedgerStore.fs. The account-wide CAS branch is refs/heads/jarvis-account-admission, maintained by Client/Modules/AccountCapacityLedger.fs. Each is separately updated using git push. The Git remote for the current Jarvis source checkout resolves to GitHub, but a source checkout remote is **not** proof of the configured original project/account ledgers or their permission rules. The audit did not inspect private tokens or live GitHub rulesets and does not claim all Git credentials, clients or direct transport channels are enrolled.

Concrete writes in the source baseline:

| Publication | Source | Semantics / gap |
| --- | --- | --- |
| Project-ledger initialization | LedgerStore.fs:1090-1128 | First nonforce push of refs/heads/jarvis-orchestration; can precede any higher-level command fence |
| Project direct record | LedgerStore.fs:1130-1210 | Independent PutRecord CAS, remote confirmation; no mandatory common remote publisher |
| Project transaction | LedgerStore.fs:2182-2314 | Fetch/planner/retry and nonforce push; same-UUID success returns before planner, so planner-only fences are bypassable |
| Source review snapshot | LedgerStore.fs:1890-1915 | Create-only push to refs/heads/jarvis-review/... with lease; has separate effect from ledger CAS |
| Source integration snapshot | LedgerStore.fs:1930-1955 | Create-only push to refs/heads/jarvis-integration/...; can occur before canonical receipt |
| Source review-ref cleanup | LedgerStore.fs:1994-2019 | Ref deletion with exact lease; still a source effect and still a remote publication |
| Account-ledger initialization | AccountCapacityLedger.fs:580-608 | Empty-ref lease followed by push to refs/heads/jarvis-account-admission |
| Account transaction | AccountCapacityLedger.fs:610-688 | Independent account CAS, force-with-lease and post-push verification |

Additional bypass surfaces are source Git pushes by operator-run commands, local Git transports, authenticated repository API write operations, browser/workflow effects, alternate source branches/tags, and old versions of installed Jarvis clients. A complete admitted-ref inventory must include all original project and account ledgers, managed review/integration refs and all source-effect ref classes. A Git branch whitelist cannot silently omit an effect just because its caller is labelled read, recovery, or Work-native.

LegacyWriterProtocolFence.fs is an optional per-host command-level fence selected by JARVIS_WQ10_LEGACY_WRITER_FENCE_BINDING: absent binding returns permitted/default-off. It classifies AgentInbox activation as a mutation, but WorkNative commands bypass that particular fence. LegacyDirectWriterFence.fs wraps some direct legacy workflows and source-ref preflights, also default-off. Neither controls a disconnected old client at the remote Git CAS. Its inspectHighWater intentionally reports unknown for all-client pending intents, source effects and minimum protocol. SourceEffectJournal.fs and OriginalBrowserJournal.fs are local/instrumented stores; they do not cover historical uninstrumented paths or other clients.

The C2 Server/WorkLegacyImportFence.fs gateway remains a hermetic contract. Its fixture can return an internally matching value but does not independently authenticate all client channels. No positive live WorkLegacyGitFenceReceipt can be produced from the present source and trust evidence.

## 2. Required common protocol (source seam and remote authority)

The additive OriginalGitPublisherAdmission types model the contract; they are **not** a live admission adapter. A production adapter must route all original project/account publishing through one exclusive remote authority. Merely returning a client-side permit before a subsequent Git push has a time-of-check/time-of-use gap.

An original publication carries:

- **Authority:** verified account ID; project ID where relevant; ledger ID and epoch; canonical repository ID; exact approved remote/push endpoint and protected ref (including source refs).
- **Publisher:** authenticated publisher ID, original device ID, protocol version and credential generation (never an untrusted agent-name string or CLI claim).
- **Operation:** stable original UUID, canonical request digest, expected old and proposed new SHA, exact ref-change kind, original pending-intent high-water and source-effect high-water. An unknown result remains journaled under the same UUID.
- **Remote decision:** applied CAS, identical-UUID replay with unchanged result, stale-head conflict, fenced rejection, or unknown outcome. A response is not proof until independently reread against the remote and durable admission journal.

A durable exclusive barrier has a monotonic epoch and operation UUID, exact account and project/ref coverage, per-scope pre-fence original remote head, last admitted operation sequence, authenticated credential-channel inventory, complete original pending/source-effect journals, and unresolved-effect set. It must be linearized with publication in the *same enforcing domain*: after barrier acquisition every non-recovery original mutation, delayed already-issued push, alternate source-ref publication and old UUID retry is denied at the remote before ref advancement. Read/reconciliation may continue; an unverified or partially acquired barrier stays fenced. Equal source and observed heads are not all-client proof.

Independent C3 verification must reread all trusted remote heads and admission records, check original source ancestry and exact ref/epoch identities, verify high-water against every newer reachable commit and reject unknown/revoked/incomplete evidence. Never use synthetic booleans, a fixed protocol version, the local NextSequence, or a successful CAS alone as a global witness.

## 3. Remote enforcement and current hard NO-GO

GitHub.com rulesets can restrict selected branch/tag updates and can name GitHub Apps as bypass actors; GitHub Enterprise Server has customizable pre-receive hooks. This is not evidence that the *current project/account/original source refs* have complete restrictive rulesets, that no old credential bypasses them, or that a specific gateway is the sole writer. GitHub.com does not expose a general custom pre-receive hook for this client to install. Rulesets also require independent verification of administrator/bypass permissions and GitHub APIs/workflows, not merely an SDK check.

**NO-GO until an operator separately authorizes and verifies the following provisioning on disposable repositories first:**

1. Inventory canonical original account and every project repository/ledger/ref plus source refs, all repo administrators, GitHub Apps, PAT/deploy-key/SSH/Actions permissions, alternate push URLs and GitHub API ref writers. Fail if coverage is incomplete or an unreviewed actor can write a protected ref.
2. Establish a publisher-only remote rule for *every* mutable original ref and source-effect ref class, or a truly exclusive server-side receive-pack gateway with no direct filesystem access for clients. Default deny all old direct writers including old but still valid credentials. The privileged publisher identity must not be shared with legacy clients, browser agents or general development jobs.
3. Run the publisher behind an authenticated serialized CAS/barrier service with durable operation/unknown-effect journal, canonical identity binding, credential-generation and epoch fences; isolate its privileged Git credential. A separate pre-push client permit does not implement the publication boundary.
4. Verify the enforcement is active from an independent client and the host administration plane. Prove old/stale clones with still-valid credentials cannot update each protected ref nor create alternate source refs, including via API, despite not consulting the Jarvis client. Prove the privileged gateway's CAS and barrier are serialized, and test an in-flight push at the barrier boundary.
5. Fence *all* original project and account authorities as one deliberate recovery-safe transition. Verify zero unexplained original pending intents/source effects and exact authenticated high-water. Preserve an unknown/partial phase as fenced. Independently reread and verify the barrier from C3.
6. Submit exact-source independent review, .NET 11 Release Client.Tests/Server.Tests and disposable two-project/one-account multi-client race evidence. The operator must still issue a separate C3 and production cutover GO/NO-GO. Do not switch any live Git credentials or rules here.

In particular, changing client code or installing a hook on a test bare repository cannot fence an unchanged GitHub push credential. A test hook demonstrates the *remote-rejection property* in a disposable environment, not GitHub production provisioning.

## 4. Disposable remote fixture / negative-space acceptance

Use two distinct project bare Git remotes, one separate account bare Git remote and at least two clones per affected remote. Seed the original protected branches while each remote is unfenced. Install an enforcing pre-receive deny hook on each **disposable** bare remote, then attempt git push from stale clones without consulting client fencing. The remote must explicitly reject each push and leave the protected head unchanged. Repeat for managed review/integration refs and an unexpected alternative source ref. This is a necessary negative proof, not an authenticated publisher or an atomic barrier installation proof.

The full P1/P2 follow-up must provide the affirmative authority case as well: a privileged publisher process that exclusively updates refs under the same durable barrier transaction domain, restart/UUID replay with identity checks, lost responses, races between in-flight publication and barrier, project/account partial fences, forge/divergence, absent journal and credential inventory. Remote rule provisioning and trusted credential inventory must be demonstrated, not simulated by a client-provided boolean.

## 5. Ownership and rollout protection

This lane owns the original remote writer audit, typed contract, hermetic fixture and narrowly gated compatibility seams. It does **not** change Worker 4 C2/C3 importer, Worker 1 Scheduler, Worker 13 backup, Worker 12 Recovery, Worker 2 API, live project/account remotes, production credentials, hooks, rule settings, host deployment or unattended scheduling. Existing original Git writers must behave exactly as before while default-off. All release and independent-review evidence must be pinned to an exact source SHA.

## 6. Review-1 P0 typed-contract corrections (no new admission authority)

The original P0 frozen review `ca4582f5ea744b9cb71ac82ee94130d7` requested two corrections and one Git-ref shape fix. The follow-up retains its deliberately negative-only evidence boundary:

- **Reserved refs cannot be disguised as source effects.** `SourceEffect` envelopes reject either `refs/heads/jarvis-orchestration` or `refs/heads/jarvis-account-admission`, regardless of claimed project scope. Project/account CAS refs remain tied to their respective declared purposes.
- **A source ref must be independently registered.** `validateTargetAgainstTrustedInventory`, `validateCasAgainstTrustedInventory`, and `validateFenceRequestAgainstTrustedInventory` compare the full claimed account/project/ledger/epoch/purpose/remote fingerprint against the registered entry keyed by canonical repository identity and exact ref. Missing or conflicting entries fail closed. This is *only* a pure equality helper: callers must not be allowed to provide their own trusted inventory. A future authenticated enforcing publisher must derive its complete ref/credential inventory from independent remote authority and reject operations there, under the same serialization boundary as Git publication. These helpers do **not** establish credential coverage, live remote enforcement, or full quiescence.
- **Physical ref collisions are rejected.** Within a requested barrier, the physical `(CanonicalRepositoryId, GitRef)` identifies one ref. Two claimed project entries with different ledger epoch, ledger ID, project ID or remote fingerprint for that same physical ref are invalid, even if the expanded record tuples differ. This still does not prove the request includes *every* authoritative project, credential channel, or source effect.
- **Git dot-component rejection.** Hidden path components such as `refs/heads/jarvis-review/.shadow` fail shape checking, matching `git check-ref-format` for this case. Shape checking cannot substitute for ref classification or authorization.

Tests exercise exact-ref alias attempts, unknown alternate source refs, spoofed target identities, conflicting trusted registry entries, conflicting physical-fence scopes and dot-prefixed ref components. This remains an additive, **default-off P0 contract**. The disposable pre-receive hook demonstrates remote rejection in a fixture only. Until an actual authorized publisher serializes publication and barrier acquisition and the host prohibits every bypass transport, **live C3, all-client fence witnesses and cutover remain HARD NO-GO**.
