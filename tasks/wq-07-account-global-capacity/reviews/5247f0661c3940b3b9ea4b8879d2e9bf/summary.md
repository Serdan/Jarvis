# Independent WQ-07 frozen cross-ledger review — ChangesRequested

**Review:** `5247f0661c3940b3b9ea4b8879d2e9bf`  
**Task:** `wq-07-account-global-capacity`  
**Owner:** Worker 4  
**Exact source SHA:** `b7aadb7b1490220ba10310f121c8eaca5e9fe969`  
**Base:** `033d6384a6e706c8262a18c26c4f43c8158462fe`  
**Verdict:** **ChangesRequested (P1 cross-ledger identity binding)**. This is not a rejection of the positive CAS evidence.

## Independent source and Release verification

- Independently fetched claimed review metadata, canonical WQ-07 task spec and workspace/project AGENTS instructions. Read-only `git rev-parse HEAD` matches the exact frozen SHA; isolated `worker4-wq07-admission-adapter` worktree is clean.
- Inspected the complete new `Client/Modules/AccountAdmissionAdapter.fs`, WQ-02 project witness/abort changes, relevant WQ-02 contract transitions, `LedgerStore` operation origin verification, account reserve/release implementation, and all eight new adapter tests.
- SDK: `dotnet --version` -> **11.0.100**. Independent local `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~AccountCapacityLedgerTests|FullyQualifiedName~WorkQueueGitLedgerTests' --logger 'console;verbosity=minimal'` -> **Passed 43, Failed 0, Skipped 0**, exit 0; Release Common, Client and Client.Tests compiled. Warnings FS0760 and FS3369 were unrelated. No hosted CI.
- The F# adapter is compiled but no production Scheduler call site exists (only the new module and its fsproj entry under Client). No Scheduler activation, migration, main push, deploy or Codex.

## Invariants that were supported

1. **Account-first/project-second:** `AccountCapacityLedger.reserve` precedes WQ-02 `AdmitProject`. The adapter rechecks that the account token is `Held` and matches launch/project GUID, role/version and admission operation.
2. **Single account CAS cap and resizing:** the account ledger owns a single token array/ref, and reserve/resize share the Git CAS. The two-project cap-one and resize races pass in Release tests. The non-model Scheduler/Reconciler are not counted here.
3. **C01 absence fence:** WQ-02 `AdmitProject` and `AbortUnboundProjectAdmission` compete on the same launch-ID absence under project CAS. C01 creates a permanent terminal tombstone, and account release waits for an immutable operation/witness read.
4. **Terminal/replay checks within the presented ledger:** `VerifyCurrentTerminal` rejects missing/stale/rewritten witnesses, compares immutable operation origin, terminal generation and current project state, and distinguishes admitted terminal from original-admission-excluding abort. Release checks role/versioned account token and exact UUID; released token cannot be re-admitted. Lost/ambiguous project fetch retains the token.
5. **No premature no-claim release:** unproven `TerminalizeNoClaim` is fail-closed in this WQ-02 foundation; STOP, tab closure and mere time passage are not accepted as project terminal proof.

## Findings

- **P1, blocking — account token does not bind the authoritative project ledger identity.** See `findings/p1-project-ledger-binding.md`. Token reservation carries only project GUID, not ledger ID/trusted remote. A second properly self-consistent ledger with the same GUID can supply an authenticated local C01/terminal witness and release the first ledger's still-active account token. This is a source-derived confused-deputy path if the presented project binding is substituted; no on-host exploit is claimed. Require immutable project ledger identity/authority binding at reservation and verified release plus an adversarial two-remote regression.
- **P2, test gap — adapter lacks post-publication project CAS lost-ack test.** See `findings/p2-lost-project-cas-ack-test-gap.md`. Its eight tests exercise pre-fetch failure and regular two-clone replay, but not a committed project reservation with lost acknowledgement and C01 account-token reconciliation. Add deterministic integrated fault injection. Existing account-foundation lost-ack coverage is positive but not a substitute for this boundary test.

An additional disposable F# scratch reproduction was attempted but denied by the local Jarvis shell-executable policy. Neither that experiment nor any hosted CI is presented as completed evidence.

**Required handoff:** Worker 4 should correct the trust-bound ledger identity at the account-project boundary, add the negative and post-commit uncertainty regressions, validate .NET 11 Release in the isolated source branch, and submit a **new exact frozen independent review**. Do not activate Scheduler or integrate this SHA as Accepted.