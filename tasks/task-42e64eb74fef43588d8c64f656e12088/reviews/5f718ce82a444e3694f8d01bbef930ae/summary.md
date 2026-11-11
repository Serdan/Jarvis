# Independent follow-up review — durable role instructions and offline local AGENTS.md fallback

**Verdict: Accept. Previous P2 regression is resolved and independently tested.**

- Task: `task-42e64eb74fef43588d8c64f656e12088`; frozen `refs/heads/feat/shared-agent-roles` at **`aebb9a68738d68e8b757737009c5922ee06b5fee`**, base/current main `ecb59e2272c7e6c4e127345f5034695c2f6e5465`. This supersedes Lead's prior **ChangesRequested** review `9235b38871624351bd94188516e7c0e2` of frozen `569f20d32c979e10969cbb0cd9a4f7b47e4ca0e4` (P2 remote-ledger-blocks-local-agents-md).
- Previous review already assessed durable role records, per-agent config, strict ledger CAS/provenance, multi-agent resolution and MCP tool contracts; this follow-up independently focused on the corrective commit `aebb9a68` and its negative-space tests.

## Re-review of correction

At `Client/Modules/AgentInstructions.fs:516–566`, `getExplicit` now calls `getExplicitRepository` **before** any optional role-ledger operation. This preserves local workspace/project/directory `AGENTS.md` retrieval independently of a remote ledger outage, including for unregistered callers. A configured ledger is then consulted for roles only on the **initial page** (no cursor). Verified remote `RoleInstructions.tryResolveAt` supplies complete role documents; `RemoteUnavailable` and `LedgerContended` are the only errors degraded to a **typed incomplete** `instructionLayers` diagnostic with `status: "unavailable"`, `complete: false`, error `code` and an explicit warning that role/agent instructions were NOT loaded. Unknown agent when offline similarly gets the warning, not an authoritative empty role list.

An explicit continuation cursor is locally verified against the source hierarchy and handled by `getExplicitRepository`, but **does not read the remote role ledger again**; its distinct `status: "continuation"`, `complete: false`, `code: "RoleLayersNotRepeated"` is not confused with complete resolved role content. Role documents are not duplicated across chunks, reducing unnecessary remote I/O and preserving bounded response size. Source failures, malformed trust config, corrupt canonical role identity, corrupt per-agent config and other ledger-integrity failures remain errors, not unavailable successes.

The new `Client.Tests/RoleInstructionsTests.fs` tests actually move the approved bare remote offline, retrieve a 900-kB paginated local hierarchy for registered and unregistered identities with typed unavailable warnings, advance the cursor during outage, then restore and corrupt a canonical role to assert `LedgerIntegrityError`. A second test corrupts a bound agent config identity and also requires an error. Existing tests check normal healthy role/agent bodies, multi-agent layers, bounded serialization and continuation behavior. Documentation `docs/AgentInstructions.md` now matches the initial-page versus continuation refresh contract.

## Independent results on exact frozen source

- Full **Client Release: 556 passed / 0 failed / 0 skipped**, job `4c0581354c6b42079180fd8dea257936`.
- Full **Server Release: 127 passed / 0 failed / 0 skipped**, job `e7ca67f8ff5747c095efd4d722279521`.
- **RoleInstructions-focused Release: 9 passed / 0 failed**, job `7b29cb1581c94fa699c45e53f22e2596`; these are also included in full Client suite.
- `git rev-parse HEAD` matched frozen `aebb9a...` and worktree remained clean. `git diff --check ecb59e2..aebb9a` exit 0; `git merge-tree --write-tree main aebb9a` exit 0 with clean tree `137282aaca332ba89eba100ce3d0b11446c1aa2d`.
- Incidental Client test warnings about activity sidecar persistence (InvalidDataException/UnauthorizedAccessException) did not fail the suite and are not attributable to this role feature on the inspected patch; no tracked file edits were made.

**Verdict: Accept**, no remaining blocking source issue found in scoped correction. This approves the frozen technical review only. It is **not** authorization to integrate or deploy; operator approval and coordinated compatibility review remain distinct.
