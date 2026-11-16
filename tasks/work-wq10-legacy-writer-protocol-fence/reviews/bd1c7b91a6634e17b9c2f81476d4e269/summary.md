# Reviewer 4 independent current-main composition review — Accepted

**Review:** bd1c7b91a6634e17b9c2f81476d4e269
**Frozen source:** `117ec8630ce3071b532e2c889e3cdc708dc1a46a`
**Task:** `work-wq10-legacy-writer-protocol-fence`
**Scope:** two-parent current-main merge of the already independently Accepted, DEFAULT-OFF WQ10 old Agent/Manager authenticated-command writer fence. This is NOT production migration, fleet-wide writer fencing, Scheduler/Manager retirement, or cutover GO.

## Exact ancestry and merge audit

- `git rev-list --parents -n 1 117ec...`: first parent `aa3855c23c76d49e54229d2711c43b9256524f78` (current main with WQ11 source reservations), second parent `b10d836c7cdf233aff309d202fa6e8d27149a4b1` (previously Reviewer3 Accepted integration-resolution review 84542b24, decision seq5333). The previously Accepted original `2ce8a258d8e5d1579bc005df978b235a3192aee9` is an exact ancestor, not rebased or rewritten.
- `git diff aa385..117ec --stat` changes only the six expected fence files: `LegacyWriterProtocolFence.fs`, `LegacyWriterProtocolFenceTests.fs`, `Client/SignalR/Client.fs`, both compile project files, and `docs/Wq10LegacyWriterProtocolFence.md`. `git diff b10d..117ec` retains current-main WQ11 reservation module/tests/docs and their compile includes. `git show --remerge-diff` has no conflict resolution delta for the project files.
- `git diff --check aa385..117ec` exited 0; `git merge-base --is-ancestor` passed for both original accepted fence and aa385 current-main. Frozen review worktree `lead-wq10-fence-after-aa385-20261116` stayed clean.

## Independent source observations

- Fence remains off without a host-selected binding, and cannot be waived via a self-declared Agent label, operation UUID, command header, or phase. With an enabled trusted host binding it fetches original account/project Git authority and rejects legacy Task/Review/Challenge/Schedule/replay writes at frozen-or-later verified migration epochs; missing trust, wrong protocol, stale heads, phase mismatch and Aborted produce fail-closed Unknown.
- `Client/SignalR/Client.fs` checks before authenticated command dispatch and again at the direct dispatch boundary. The nominally read-only AgentInbox is treated as a legacy writer; implicit Ready-to-Working activation and one-shot response-alert consumption on other reads are suppressed separately while allowing historical reads. WQ04 composition and Work-native queues are classified separately.
- No merge-specific weakening of those paths or clobbering of accepted WQ11 source permission modules was identified.

## Local .NET 11 Release verification at the exact frozen worktree

- Focused `LegacyWriterProtocolFenceTests|SignalR|WorkQueueProjectMigrationPrepareTests|WorkQueueReservationPermissionsTests` `Client.Tests` `--no-build --no-restore`: **30/30 passed**, exit 0, zero skipped.
- Explicit `LegacyWriterProtocolFenceTests|SignalRDispatchTests` filtered `Client.Tests`: **5/5 passed**, exit 0. The second filter term matches the filename but not the actual NUnit namespace; the broader 30-test run includes the SignalR namespace. Do not mistake the earlier 5 as independently exercising SignalR tests.
- Full `Server.Tests` Release: **294/294 passed**, exit 0.
- Full unfiltered `Client.Tests` was attempted, emitted activity-persistence read/save/append InvalidDataException/UnauthorizedAccessException diagnostics and did not produce a completion result after roughly three minutes; I cancelled it to avoid consuming shared build resources. **No full Client pass claimed; no causal assertion that these diagnostics come from the frozen fence change.**

## Remaining NO-GO evidence boundaries

This acceptance is strictly for the default-off, typed authenticated command-path negative guard and its conflict-free composition onto current main. The host file is not a production signer. Direct library/ledger writes and scripts, older/unconnected clients, previously scheduled/rearmed Manager/AfterAgentIdle actions, original writer high-water, authoritative Freeze signer, minimum protocol fleet proof, atomic cross-ledger checks, operator migration GO and true cutover remain unproven. A late already-admitted old write is not globally excluded by this pre-dispatch guard.

**Independent verdict: Accepted, current-main merge composition only.** No production activation, Scheduler/cutover, hosted GitHub Actions, Codex or deployment performed.
