# Independent frozen WQ-07 correction review — ACCEPTED

**Reviewer:** Reviewer 1  
**Task:** `wq-07-account-global-capacity`  
**Review:** `f62c48f9df724e68a9fa61bbf48e31d0`  
**Exact frozen source:** `eb1ec7c8360f99029728cea56ebf8bb78b3c0499`  
**Direct parent:** previously ChangesRequested `b7aadb7b1490220ba10310f121c8eaca5e9fe969`  
**Verified baseline ancestor:** `033d6384a6e706c8262a18c26c4f43c8158462fe`  
**Verdict:** **ACCEPTED for exact frozen, inert source**; no remaining blocking issue found in the reviewed correction. This is not permission to activate Scheduler, migrate account state, deploy, or integrate a different SHA.

## Scope and independent source inspection

The previous review `5247f0661c3940b3b9ea4b8879d2e9bf` identified (P1) a same-project-GUID different-ledger confused-deputy release and (P2) missing deterministic post-publication project-CAS lost-ack regressions. I read both prior finding documents and independently inspected the five changed files in the exact correction: `AccountCapacityLedger.fs`, `AccountAdmissionAdapter.fs`, `WorkQueueGitLedger.fs`, `AccountAdmissionAdapterTests.fs` and `AccountCapacityLedgerTests.fs`.

The original project ledger authority is now persisted on account tokens as immutable ledger GUID, epoch, and SHA-256 of the original project identity plus approved fetch/push endpoints. Reservation's stable request identity includes these values. `tokenOwns` checks original identity at admission, C01 abort, proof-based release and replay; terminal proofs independently loaded from project Git carry the same authority, and the account release CAS rejects mismatches. No raw credential-bearing remote URL is stored in account token state.

The added real two-remote negative keeps the authentic first-ledger launch and its cap-one token held, verifies a genuine abort witness from a second internally consistent ledger with the same project GUID, rejects release/abort/altered-authority replay, and prevents another project from overcommitting the global cap. The test-only post-publication acknowledgment-loss seam exercises the actual WQ-02 CAS before discarding the reply, and the tests distinguish committed AdmitProject, committed C01, and account release Git-push lost acknowledgement, with original UUID/receipt replay and no double release. Earlier global-first/project-second, C01 exclusion, role/version, resize and two-clone tests remain in the focused suite.

## Independently executed evidence

- `git rev-parse HEAD` = exact `eb1ec7c8...`, parent chain verified; `git merge-base --is-ancestor 033d6384... eb1ec7c8...` exit 0.
- Post-test `git status --porcelain=v1` empty; `git diff --check b7aadb7b eb1ec7c8` exit 0.
- `dotnet --version` = **11.0.100**.
- Independent local Release command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~AccountCapacityLedgerTests|FullyQualifiedName~WorkQueueGitLedgerTests' --logger 'console;verbosity=minimal'`.
- **PASS: 47 total, 47 passed, 0 failed, 0 skipped, exit 0**; Common, Client and Client.Tests compiled for .NET 11 Release. Only preexisting unrelated FS0760/FS3369 warnings appeared.
- Frozen Client source grep confirms account reservation/release are called by the adapter, not a live Scheduler. No GitHub Actions, main push, Scheduler activation, migration, deployment or Codex.

Detailed audit and boundary qualifications: `findings/verified-wq07-corrections.md`.

**Decision rationale:** P1 original project authority binding is enforced in the account CAS and all relevant adapter paths; P2 unknown-outcome recovery is covered at genuine post-publication boundaries. Tests independently pass on the exact review commit. The code remains deliberately inert; any source combination with a newer main or production activation needs the applicable separate review/operator gates.