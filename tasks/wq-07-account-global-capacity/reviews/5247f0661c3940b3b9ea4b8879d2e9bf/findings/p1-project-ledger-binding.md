# P1 — Account reservation is not bound to the authoritative project ledger

Frozen source: `b7aadb7b1490220ba10310f121c8eaca5e9fe969`. Review: `5247f0661c3940b3b9ea4b8879d2e9bf`.

## Evidence

- `Client/Modules/AccountCapacityLedger.fs:64-71` defines the original `AccountToken` with `ProjectId`, `LaunchId`, role/version and admission UUID, but **no project LedgerId, epoch or trusted remote identity**. `reserve` at lines 528-550 takes only `projectId`, not its canonical ledger identity.
- `Client/Modules/AccountAdmissionAdapter.fs:57-72` constructs a terminal verifier from the caller-provided project `LedgerBinding`. It checks `locator.ProjectId = binding.ProjectId`, and verifies a real operation and blob in *that supplied project's* trusted head; this proves the supplied ledger internally, not that it is the account token's original project ledger.
- `AccountAdmissionAdapter.releaseTerminal` at lines 234-262 and `abortUnbound` at 268-325 match the token by launch/admission/project GUID and role/version, but do not compare a prebound project ledger identity. `AccountCapacityLedger.release` at lines 588-600 only checks `proof.ProjectLedgerId` is nonblank, not that it equals the ledger that originated the account reservation.
- A `LedgerBinding` can be independently initialized with the same `ProjectId` but a different `LedgerId` and approved remote (`Client/Modules/LedgerStore.fs:58-65, 925-950`). Fresh fetch and immutable origin checks remain valid *within each of the two ledgers*.

## Unsafe cross-ledger execution trace

1. Account cap 1: reserve token T for canonical project P (`ProjectId=X`, `LedgerId=L1`, remote R1); project P admits the launch, which remains reserved/registered/nonterminal.
2. Independently initialized project P' uses the **same project GUID X** but different `LedgerId=L2` and remote R2, with no matching work. The adapter accepts P' as the supplied project because its GUID matches T.
3. `abortUnbound(account, P', same launch/admission/abort/release IDs)` writes a genuine C01 abort-only tombstone in R2, verifies immutable origin there, and `releaseTerminal` releases T. R1's original launch remains nonterminal. Another reservation may now be admitted under cap 1, exceeding the intended logical global cap.

This is a source-derived, reachable confused-deputy path when a wrong or substituted project binding is supplied. It is **not** a claim of a successful on-host exploit or an already active production path: the adapter remains inert. I attempted an additional disposable scratch reproduction but the Jarvis sandbox denied the shell command; no scratch repro result is claimed.

## Required correction

Bind each reserved account token **immutably** to the authoritative project ledger identity, including at minimum its LedgerId and epoch plus an independently trusted project remote/trust-anchor mapping or an equivalent canonical binding identity. Require that identity to match at `admit`, C01, terminal verification, release, and same-UUID replay; do not trust a `ProjectLedgerId` populated only by the presented verifier. Add a deterministic two-remote negative: same project GUID, different ledger identity, real first-ledger admitted/held launch, forged second-ledger terminal/C01; assert global token remains held and the second reservation cannot overcommit.

**Verdict impact:** P1; request changes before exact-source acceptance or Scheduler activation.