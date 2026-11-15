# P2 — Registered no-claim terminal receipt lacks session-terminal evidence

**Frozen source:** `71fc78d051d6509596389abfa8fe7cb5f444cd1f`

**Evidence:** `Client/Modules/WorkQueueGitLedger.fs:378`; inherited `Server/WorkQueueContracts.fs:714-732,783-796,1379-1417,1419-1427`.

The new Git mutation `TerminalizeNoClaim(launchId)` calls `Contracts.terminalizeNoClaim`, which moves **any** `Registered`, `EverClaimed=false` launch directly to `TerminalPendingRelease("terminal:"+op)`. The transition does not require a stopped lease, authenticated session-terminal event, settled original dispatch journal, timeout, or preserved first-connect provenance. By comparison, the inherited `recoverRegisteredNoClaim` demands a stopped lease, generation and timeout plus settled journal; `finishInvalidatedLaunch` demands both dispatch and session-terminal proofs for registered launches. The account contract `releaseGlobal` considers any nonempty `TerminalPendingRelease` receipt sufficient to release a token.

**Counterexample:** A launch legitimately registers but has not yet invoked `ClaimNext`; a separate client calls `TerminalizeNoClaim` while that registered session remains live. The project CAS records a terminal receipt even though the execution still exists. Once this receipt is accepted by any future project/account release path, an account slot can be reused while the original execution remains active. The isolated WQ-02 adapter does not implement account-global release today, so this is a **handoff/integration safety defect**, not a claim that a production token was actually freed.

**Correction:** Separate a voluntary, authenticated no-work terminal event (bound to the exact registered session/lease generation) from stalled/lost-session recovery (canonical dispatch-journal + session-terminal/timeout evidence), and ensure only proven terminal receipts qualify for cross-ledger token release. Add stop/terminal races and two-client forged/no-proof terminal negative tests; preserve identical replay receipts.

**Scope:** Do not deploy or connect this bounded adapter before the account-gate authority is implemented.