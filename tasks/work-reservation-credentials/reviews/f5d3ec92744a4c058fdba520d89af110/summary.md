# Independent Review — Accepted (frozen, default-off source only)

**Review:** f5d3ec92744a4c058fdba520d89af110  
**Task:** work-reservation-credentials  
**Frozen head:** `8c306e50fed6fe58828cb32c5be3376314e5acce`  
**Base:** `240b915e9b6e2feaf5122791c399b615c7290f5c`  
**Reviewer:** Reviewer 1

## Source evidence

- `Client/Modules/WorkQueueGitLedger.fs` uses a 32-byte CSPRNG secret encoded as Base64URL; only its SHA-256 verifier enters the same original WQ02 Git CAS as the launch and admission witness. The four exact paths are `RecordPath`, `admissionPath`, `credentialPath`, `roleAdmissionPath`. No raw bearer is placed in mutation, operation request, or ordinary diagnostics.
- `Client/Modules/WorkQueueRoleAuthority.fs` checks original `AdmitCredentialProject` mutation semantics and exact four-path operation origin, cross-checks the original and current credential/verifier records; the preexisting `AdmitProject` three-path path remains a distinct supported case.
- `Client/Modules/WorkQueuePracticalAdmission.fs` rechecks account, role, source, credited work and verifier at admission CAS. Replay uses immutable original admission credit rather than the mutable current `Launch.Credit`; wrong/reissued credential and legacy downgrade are rejected.
- `Client/Modules/AccountAdmissionAdapter.fs` accepts exact three- or four-path role admission origins and preserves separate two-path historical terminal/WQ02 evidence. The terminal-release verifier applies the role/origin validation to credentialed admission.
- `Client/Modules/WorkQueueGitLedger.fs` bearer verifier compares 32-byte SHA-256 digests with `CryptographicOperations.FixedTimeEquals`, binds launch/project/account identity and revalidates inside the winning CAS. Existing `InitializeAgent` cannot register credentialed launches. New writes are denied after STOP/terminal; exact previously committed operation replay is still idempotent.
- Two-clone disposable Git tests cover original credential admission, cross-launch forgery, first registration, claim, completion, replay and terminal STOP fences. Source uses original Git CAS; no hosted CI or live Scheduler was invoked.

## Independently executed verification (SDK 11.0.100, Release)

- Focused Client credential + WQ04 tests: **7/7 passed**.
- Wider Client role authority, credential, practical admission and account adapter: **30/30 passed**.
- `Server.Tests/Server.Tests.fsproj`: **294/294 passed**.
- `git diff --check 240b915e..8c306e50`: clean. Separate reviewer worktree remains on exact frozen head.
- Unfiltered Client suite was attempted but canceled after prolonged execution with unrelated activity-persistence permission diagnostics. **No full Client suite pass is claimed.**

## Scope and decision

**Accepted** for the frozen credential-admission and bearer-verification source slice described by this review request, with default-off execution and existing legacy compatibility. **Not a GO for production:** `IssuedWorkCredential.Issue`, private delivery and `executeCredentialFixtureAt` are currently called only from tests, not from a production launcher or public Work-native tool facade. That missing runtime wiring must be reviewed separately before migration/cutover; this verdict makes no such assertion. No production browser dispatch, Scheduler activation, server restart, deployment, GitHub Actions or Codex was used.
