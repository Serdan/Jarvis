# P2 — TUI history does not display immutable operator-response provenance

**Frozen source:** `f7c5598aed774712fbfa6edab901a194e6541023`.

## Missing operator-visible evidence
The canonical backend properly writes a `RespondOperatorCondition` event containing `ActorId="operator:<hashed OAuth subject>"`, `Evidence`, `OccurredAtUtc` and sequence. `ChallengeWorkflow.getOperatorPage` exposes the event history, and `OperatorChallengeApi.details` wraps it for trusted client projects.

However `ConsoleTui.OperatorChallengeUiAccess` (`Client/Modules/ConsoleTui.fs:86-88`) has **only `Load` and `Reply`**, and the `Client/Program.fs` access setup supplies only these closures. `OperatorChallengeApi.details` has **no TUI caller** (independent `rg` of all relevant client modules confirms it). `renderChallengesUnsafe` (~1242-1280) shows the question, challenge/condition IDs, created time and recorded evidence after success, but does not show the answering operator, immutable response event, or response timestamp. The list entry has only condition metadata; history/provenance is never retrieved.

Thus an operator can read the answer text later, but **cannot verify from the Jarvis client which authenticated operator submitted it and when**, despite this being explicitly required by task spec v6 ("including immutable provenance"). No threaded chat or attachment mechanism is needed.

## Required
Expose the canonical challenge detail/history to the selected TUI item (e.g., a `Details` callback using the existing `OperatorChallengeApi.details`) and display the immutable `RespondOperatorCondition` event's actor and occurrence time alongside the original question and answer. Preserve the existing read-only trusted-project and snapshot/pagination boundaries; do not invent a separate message store. Add a post-answer reload/details TUI test verifying provenance remains visible.

**Impact:** The human workflow loses the audit context needed to distinguish an authenticated answer from another operator or a later response, even though the ledger already retains the correct evidence.
