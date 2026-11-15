# Reviewer 2 — WQ03 host WIP immutability and ledger-anchored snapshot alternative

**Review:** `f8a18e4c2db84f69b03a542136e7c108`  
**Task:** `wq03-wip-host-protection-proof`  
**Frozen head:** `519e349c22a43e00030f423cf83fbe4e4538cccf`  
**Review base:** `cc89eb919c0bf965632f07ec0d6ace5b2c3acde2`  
**Decision:** **Accepted — documentation-only fail-closed host audit and conditional architecture analysis. NOT an activation approval.**

## Exact-snapshot and scope checks

- Reviewed frozen `docs/wq03-host-proof-worker2` worktree at exact SHA. `git diff --name-status cc89eb9 519e349` identifies only four added documentation/evidence files: `docs/evidence/wq03-host-wip-protection-audit-2026-11-15.md`, `docs/evidence/wq03-ledger-anchored-snapshot-alternative-2026-11-15.md`, and two local offline fixture traces. No implementation, routing, tests, source host policy, scheduler, or credential files changed. `git diff --check` exits 0.
- Independently ran read-only `gh api repos/Serdan/Jarvis/rulesets` and `gh api repos/Serdan/Jarvis/rules/branches/jarvis-wip/wq03-host-proof-audit/gen1`: both currently return `[]` with exit 0. This supports absence of returned repository/applicable rules **for the checked identity/path**, not exhaustive proof about all credentials and future policy.
- Independently ran exact `git ls-remote --heads origin refs/heads/jarvis-wip/wq03-host-proof-20261115-1353-worker2/g0001`: exit 0 with empty stdout, consistent with disposal of the hosted fixture. I did **not** repeat destructive hosted pushes.
- Independently inspected approved inert WQ03 source `2d3a5b881436826063ece164a4f86c6f654b84fa` using `git grep -n -C 7 verifySourceExternalEffectsAndRefProtection`. It returns unconditional `RecoveryBlocked` on missing authoritative mutating-job/original-push journals and immutable host retention at lines 387–393; `YieldSource` calls it at line 899. This cross-check refers to the **pinned approved source**, not line numbers in the different docs worktree baseline. Also verified `LedgerStore.fs:156` in the docs checkout names `refs/heads/jarvis-orchestration`; its ordinary Git-CAS update mechanism by itself does not enforce host no-force/no-delete.

## Host contract assessment

- **FAIL — create-only/no-overwrite/no-force:** author supplies exact disposable nested ref, A `4be7a16af1067f2d4188996dd6ee9b4c8d86db51`, B `6574e190ea49af49f04584a4be958f8df9da8beb`, and lease-guarded `git push` commands reporting successful replacement. The directly observed writer transport therefore lacks the required immutable-WIP guarantee. I independently corroborated the empty effective rules query, but the hosted push stdout is in the author’s bounded narrative rather than an independently repeated reviewer trace; do not present this review as a second hosted force-push experiment.
- **FAIL — no-delete:** author reports a lease-guarded deletion of the same disposable ref with exit 0; independent current `ls-remote` confirms that ref is absent. This is insufficient for positive retention proof, and is affirmative counterevidence to relying on current refs for unfinished lineage.
- **PASS only point-in-time / unknown long horizon:** the author reports independent shallow clones fetched A before the replacement. Neither that fetch, current absence, or routine Git reachability establishes full recovery-horizon retention. Exact future scheduler token, other principals, bypass identities, policy mutation, remote GC and long-duration lifecycle controls remain **UNKNOWN/INCOMPLETE**. The report correctly does not turn unknowns into passing controls.
- **Activation remains blocked:** the author keeps source-effects journal, original operation identity and process terminalization separate from any Git ref or ledger read; no positive `YieldSource`, Scheduler or production source succession is requested.

## Ledger-snapshot alternative assessment

- Offline traces give a coherent **conditional feasibility** demonstration: a self-contained small Git bundle (source A `05c63045dce93f41cc451b72cf6bd059710a71fa`, SHA256 `9313ef52e311fc19bb4a42820d2574a88b5ade97fc232021b815d0630bed6af7`) as a *reachable ledger tree blob* is extracted by two distinct clones and restores A even after local source-ref deletion and GC. A raw SHA-only source pointer does not survive source GC; deletion of the ledger anchor similarly eliminates recoverability. This correctly demonstrates that content addressing alone is not retention or authority.
- Cooperative ledger `--force-with-lease` CAS rejects a stale second-clone update in the local fixture. This does not protect against a credential authorized to force-push or delete the ledger ref. The author explicitly requires externally protected, retained canonical ledger/WORM trust authority, with real effective-identity testing.
- The counterexample old writer retains separate worktree and later pushes B after A is snapshotted, while the bundle still recovers A. This convincingly separates **snapshot content integrity** from **physical/remote source-effect exclusion**. Generation-independent checkouts, no shared writable Git object store, no inherited push token, and original job/push terminal fences remain mandatory.
- The original UUID/lost-ACK fixture demonstrates a local exact-UUID/request-hash ledger recovery pattern and duplicate-CAS rejection, but **not** original hosted push settlement or cancellation. Unknown/absent receipts stay fail-closed. No claim of durable `SourceRecoveryBlocked` persisted state is made.

## Limitations and nonblocking follow-up

The proof is documentation and illustrative offline experiments, **not implemented or production acceptance-tested snapshot ingestion**. The two .txt traces summarize bounded local fixture outputs; original shell scripts and independently preserved raw hosted push responses are not included in the four-file diff. This limits reproducibility and evidentiary independence, but does not invalidate a fail-closed research report. Any future activation evidence should retain verbatim redacted hosted stdout/HTTP rejection traces, fixture IDs and clean-up proofs under the final *actual* production credentials.

The tiny 293-byte bundle does not demonstrate realistic source-tree scale, performance, secure parsing limits, concurrent retention costs, backup/restore, or host GC SLA. A new implementation track must test bounded producer/consumer, complete Git object closure, one-authority CAS publication of immutable bytes plus receipt, independent generation storage, unknown original push/old process terminalization, exact idempotency and recovery after source/ledger GC and lost ACK. Host/operator consent is required before policy edits or test probes that could strand undeletable refs.

## Result

**Accept these four documentation/evidence artifacts for their stated fail-closed purpose.** The strongest evidence supports **not** activating host-bound WIP source handoff today. A ledger-embedded snapshot may remove reliance on per-generation protected source WIP refs, but **not** the requirement for a protected, retained authoritative ledger anchor, trusted process/push fences and effective credential proof. No code, GitHub Actions, production host mutation, Scheduler activation, migration or deployment authorized by this review.
