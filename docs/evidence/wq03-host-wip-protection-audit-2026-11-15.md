# WQ-03 remote WIP host-protection proof — fail closed

**Audit:** 2026-11-15, Worker 2, canonical task `wq03-wip-host-protection-proof`, assignment generation 1.
**Disposition:** **FAIL / activation blocker** for the actual tested Jarvis Git transport and nested `refs/heads/jarvis-wip/**` namespace. No host settings were changed. No production WIP ref was probed or modified. No code changed, so .NET 11 Release tests were not applicable.

## Trust anchor and read-only inventory

- Jarvis project's configured `origin` fetch and push URL: `https://github.com/Serdan/Jarvis.git` (`git remote -v`). GitHub repository ID `720501829`, owner `Serdan`, visibility public (`get_repo`); connector reports its own `admin/push/pull=true`. **Do not equate connector permissions with the Git transport credential.**
- Local `gh api user --jq .login` returned `Serdan`. `git config --show-origin --get-all credential.helper` returned no configured helper (exit 1). The exact token, credential-helper/environment provenance and future Scheduler execution credential were not disclosed/verified; do **not** assert that the `gh` and `git push` tokens are identical.
- Read-only `gh api repos/Serdan/Jarvis/rulesets --jq 'map({id,name,target,enforcement,bypass_actors})'` returned `[]`. There are **no returned ruleset IDs/versions/bypass entries** to anchor a protection claim.
- `gh api repos/Serdan/Jarvis/rules/branches/jarvis-wip/wq03-host-proof-audit/gen1 --jq .` returned `[]` for a nested candidate. The same effective-rules query against the **existing live nested fixture** `jarvis-wip/wq03-host-proof-20261115-1353-worker2/g0001` returned `[]`.
- `gh api repos/Serdan/Jarvis/branches/main/protection` returned HTTP 404 `Branch not protected`. This `main` check is only additional context, **not** proof about any WIP branch. A separate GitHub connector read of that endpoint returned 403 `Resource not accessible by integration`, so the CLI read is the useful host-policy observation.
- `git ls-remote --heads origin 'refs/heads/jarvis-wip/*'` returned no refs at audit time; that is **not** immutability or GC proof.

## Exact disposable host fixture (same local Jarvis Git push transport)

Tested only `refs/heads/jarvis-wip/wq03-host-proof-20261115-1353-worker2/g0001`, with a preflight exact `git ls-remote --heads origin <ref>` confirming the ref was absent. Both temporary commits used the existing checkout's tree `687a6b20172d4841a9dc508bf7d6ecf697a6f5a8` and parent `90ed868d04524d0483ccd979cffb5029d8cf7027`, with distinct messages containing `[skip ci]`. No source files or branch pointers were edited to create them.

- **A (initial continuation):** `4be7a16af1067f2d4188996dd6ee9b4c8d86db51`
- **B (non-fast-forward replacement):** `6574e190ea49af49f04584a4be958f8df9da8beb`

Bounded commands and observed results:

1. `git push --porcelain --force-with-lease=<ref>: origin A:<ref>`: **exit 0, new branch**; remote tip readback exactly A. The empty lease ensured that only a previously absent fixture ref could be created.
2. Two **independent HTTPS shallow clones** under distinct `/tmp/jarvis-wq03-host-proof-worker2-20261115-clone-{a,b}` paths fetched `--depth 1 --single-branch --no-checkout --branch jarvis-wip/wq03-host-proof-20261115-1353-worker2/g0001`. Both `git -C <clone> rev-parse HEAD` returned **A exactly**.
3. `git push --porcelain --force-with-lease=<ref>:A origin B:<ref>`: **exit 0, forced update** `4be7a16a...6574e190`. `git ls-remote` verified the remote tip was B. This directly disproves the required no-overwrite/no-force guarantee using the tested effective Git transport.
4. `git push --porcelain --force-with-lease=<ref>:B origin :<ref>`: **exit 0, deleted**. A subsequent exact `git ls-remote --heads origin <ref>` returned empty. This directly disproves no-delete protection. The SHA lease prevented an accidental change to any concurrently altered ref.
5. The only reported hosted workflow was `WQ-11 disposable Windows Client.Tests`, state `disabled_manually`. Read-only `gh api repos/Serdan/Jarvis/actions/runs?branch=jarvis-wip/wq03-host-proof-20261115-1353-worker2/g0001 --jq .total_count` returned **0**; no Actions job was requested.
6. Both temporary clone directories were explicitly removed with `rm -r -f -- <clone-a> <clone-b>` after a non-force `rm -r` attempt timed out at a write-protected pack-file prompt. `ls -d -- <clone-a> <clone-b>` returned `No such file or directory` for each. The production fixture ref is absent and the original main checkout remained clean (`## main...origin/main [behind 36]`). Two unreachable fixture commit objects may remain in the local Git object store until normal local Git housekeeping; this does not alter a branch or source tree.

This test proves **two-clone exact fetch at creation time**, **unsafe forced overwriting**, and **unsafe deletion**. It does **not** establish long-term host object retention, actual GC expiry, or the identity/authorization of untested actors. It is especially not evidence that future Scheduler credentials, deploy keys, PATs, GitHub Apps, administrators, or bypass identities are safe.

## WQ-03 host contract matrix

| Contract | Verdict | Grounded observation / missing evidence |
| --- | --- | --- |
| Ref path matches deeply nested `refs/heads/jarvis-wip/**` | **Tested** | Created and fetched the named two-level nested fixture ref |
| Create-only remote publication | **FAIL** | Tested writer could replace the original commit with B |
| No force/update/overwrite under active Git transport | **FAIL** | Lease-guarded non-fast-forward forced update succeeded |
| No deletion under active Git transport | **FAIL** | Lease-guarded remote deletion succeeded |
| Applicable host ruleset with immutable ID/version | **FAIL** | Ruleset list and effective per-branch rules both empty |
| Bypass/admin/deploy credential safety | **INCOMPLETE / BLOCKED** | No applied rule/bypass policy; exact production credential and all alternate identities not audited |
| Two independent clones can fetch exact initial SHA | **PASS (point-in-time only)** | Both fresh clones resolved A before overwrite |
| Retain the ref and object throughout unfinished lineage | **FAIL / retention unproven** | Deletion succeeded; no enforced retention or recovery-fence horizon. No long-duration GC evidence |
| Disposable fixture cleanup | **PASS** | Remote ref absent, local temp clones absent; main checkout clean |
| WQ-03 live handoff/succession readiness | **BLOCKED** | Required immutable host retention is demonstrably absent; source effect bridge is a separate critical-path requirement |

## Operator-facing blockers and safe proof plan

1. **Keep `verifySourceExternalEffectsAndRefProtection` rejecting normal `YieldSource`; keep WQ-03 source handoff and Scheduler inactive.** Do not reinterpret the accepted inert WQ-03 review (`98eb5bdf48c14bef9eb069e1b5a7570f`, exact source `2d3a5b881436826063ece164a4f86c6f654b84fa`) as cutover approval.
2. With **separate explicit operator authorization**, configure host-enforced rules for the *entire nested* `refs/heads/jarvis-wip/**` namespace. Permit first creation but reject all subsequent update, non-fast-forward/force and deletion paths, including administrators and any bypass identities that can be used by production agents. Validate GitHub branch-pattern semantics empirically for a deeply nested name; never assume `*` and `**` are interchangeable.
3. Independently capture canonical repository/remote identity, ruleset IDs plus retrieved details/version or ETag, enforcement status, conditions, bypass list, subject credentials/roles and approval authority. Verify the **actual source-producing and Git-pushing identities** (CLI, scheduled host, deploy key/App, recovery process), not merely the GitHub connector or interactive account.
4. Run a **separately authorized disposable same-provider, same-effective-permission negative fixture** under the final policy: create succeeds; overwrite, force, delete each fail with host-enforced rejection; two independent clones fetch the original SHA. Under truly immutable rules, disposable refs may intentionally become undeletable: use a dedicated disposable repository/ruleset replica or an operator-owned retained test namespace with a separately approved eventual cleanup plan. Never destructively probe a real production WIP ref.
5. Specify and prove the retention/recovery-fence horizon for all unfinished claims, checkpoints, original operation IDs, and successor lineages. Document reachable object/ref durability and GC/pruning behavior rather than inferring retention from one successful fetch. Preserve remote refs until independent completion/recovery proof.
6. Coordinate with `wq03-source-effect-journal-bridge` before any separately reviewed positive handoff/cutover. Missing terminal process/push evidence must remain RecoveryBlocked; no success based on a clean worktree, current `ls-remote`, or a missing receipt.

**Operator decision required:** authorize and implement the above host policy/permission configuration and its safe fixture environment, then re-audit. Worker 2 made **no** host-policy changes, source bridge edits, main push, deployment, Scheduler activation, GitHub Actions execution, or Codex calls.
