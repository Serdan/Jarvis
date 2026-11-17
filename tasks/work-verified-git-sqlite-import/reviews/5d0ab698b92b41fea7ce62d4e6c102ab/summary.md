# Independent review — C3 original-Git publisher prerequisite

**Verdict: Accepted (documentation-only prerequisite proposal).** Frozen head `d025da38fa0b3606714e44214ee6e41870a09475`; parent `9c290e299faf7ee40061f262f682c36b30d0771a`; one added document: `docs/specs/work-original-git-writer-fence-prerequisite-2026-11-17.md`.

## Submitted premise
Audit found existing legacy transaction publication not remotely barred for all devices; optional host-local fence cannot authenticate disconnected client/pending-intent/source-effect high-water. This proposal specifies source-enforced admission, journals and negative multi-client proof before a positive gateway can exist. No code or cutover GO.

## Evidence examined
- Confirmed exact frozen one-file diff and parent chain via local read-only `git diff` / `git show`.
- Verified `Server/WorkLegacyImportFence.fs` exposes `IWorkLegacyOriginalGitFenceGateway` and expressly has no production adapter (base lines 65–84); fixture values cannot certify real fleet quiescence.
- Verified `Client/Modules/LedgerStore.fs` performs direct Git publication in `transact` (base lines 2182–2262), with replay success before planner invocation (2197–2202).
- Verified `Client/Modules/AccountCapacityLedger.fs` independently publishes with Git force-with-lease (base lines 610–663).
- Verified `Client/Modules/LegacyWriterProtocolFence.fs` defaults host scope off when binding absent (base lines 199–202, 259–262), not independent all-device evidence.
- Verified `Client/Modules/LegacyDirectWriterFence.fs` reports original pending-intent, source-effect and fleet protocol high-water as Unknown (base lines 117–132).
- Checked requirements to preserve default-off Git-backed authority without production activation.

## Assessment and boundary
The proposal correctly refuses to derive exclusive all-client fencing from local checks, Git head equality, a single-client statement or disposable fixture receipts. Its separately reviewed dependency covers remote-enforced CAS admission (including stale credential paths), source/device/project/account identity, authentic pending-effect and all-client witnesses, crash/replay semantics, independent verification, and hermetic multi-client negative tests. It retains separate operator GO/NO-GO. This frozen change is documentation-only; no code or test changes were submitted. A build could not substitute for the missing protocol proof and was not run for this document.

**Scope limit:** Accept the blocker analysis and prerequisite specification only. This does **not** accept a C3 production adapter, prove remote enforcement, approve any live writer fence, promote SQLite authority, or authorize migration. C3 remains blocked until separately reviewed protocol implementation and new exact-SHA verification. No findings requiring document changes.
