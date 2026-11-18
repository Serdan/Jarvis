# Correction evidence matrix — independent Reviewer 3

**Classification:** No blocking findings for the assigned default-off diagnostic. The following is a verified negative-space matrix, **not** production-positive acceptance.

## Frozen-source analysis

The prior Reviewer 1 finding `ledger-target-classification.md` at decision **7895** observed two invalid fixture refs and missing canonical checks in `Server/WorkLegacyImportAuthenticatedReadDiagnostics.fs:116-158`. The correction at `ddeddf88e29f45d903729b0688e8b7a9e949eb09` changes the account/project `checkLedger` guard at lines 130-154:

- `OriginalGitPublisherAdmission.validateTarget target` is shared with the production publisher shape contract (`Server/OriginalGitPublisherAdmission.fs:94-140`). It restricts `AccountLedger` to `refs/heads/jarvis-account-admission`, `ProjectLedger` to `refs/heads/jarvis-orchestration`, disallows wrong purpose/scope, malformed refs, empty account/ledger identity and malformed 64-character repository/fingerprint hashes.
- `target.CanonicalRepositoryId = fence.Binding.TrustedRemoteId` detects mismatch with the pinned trusted remote; `fence.Binding.Scope` is required to be account or project as appropriate.
- `sourceHead`, `ledger.CurrentRemoteHead`, `fence.FencedHead` and `fence.OriginalHead` must have 40- or 64-hex-character Git OID shapes. Existing consistency, pinned head, ledger ID/epoch and entity identity checks remain.
- This is consistent with the relevant canonical `Server/WorkLegacySourceWitness.fs:251-287` identity/scope gate without claiming to implement all later C1 checks.

## Reproduced .NET 11 Release negative classifications

New `Server.Tests/WorkLegacyImportAuthenticatedReadDiagnosticsTests.fs:445-499` cases all explicitly probe **both** original account and original project ledgers in a coherent two-project/one-account fixture. After the fix:

| Malformed known authority | Expected / observed |
| --- | --- |
| Account/project target ref other than canonical admission/orchestration ref | `Rejected` |
| Canonical repository ID not SHA256-shaped | `Rejected` |
| Approved remote fingerprint not SHA256-shaped | `Rejected` |
| `Fence.Binding.TrustedRemoteId` swapped away from target repository ID | `Rejected` |
| `Fence.Binding.Scope` mismatched to account/project | `Rejected` |
| Remote and fenced heads both equal to an invalid non-Git OID | `Rejected` |

These were independently rebuilt and executed as part of the **20/20 focused Release pass** on the exact source; the tests' earlier red-first output (five failures) is **author-reported, not reviewer-executed**.

## Existing preserved negative gates

Source `Server/WorkLegacyImportAuthenticatedReadDiagnostics.fs:41-115,160-272`, tests `Server.Tests/WorkLegacyImportAuthenticatedReadDiagnosticsTests.fs:98-441`:

- **`Incomplete`:** null/absent independently authenticated reader, absent account/project binding, absent independently authenticated separate source-effect anchor, or apparently coherent mock/fixture values. The eight Project/Work/Stage/Launch/Role/Review/Integration/ExternalEffect kinds are explicitly exercised. There is **no `Verified` return branch** and no production registration.
- **`Unknown`:** unavailable or uncertain original source read; pending/unresolved physical effects; remote-head conflict with valid shape; source/ref lineage changed across reinspection; delayed separately pushed review/integration ref after ledger CAS; asserted source-effect high water without remote journal; unobserved physical effect or digest inconsistency.
- **`Rejected`:** duplicate/out-of-order/missing source-effect sequences, duplicate operation UUIDs, malformed request/result digests, malformed source-effect publisher target/head, mismatching independent effect anchor and now malformed known-invalid account/project target/binding/head.
- Staging and reinspection tests do not prove a real independently authenticated original publisher or an exclusive all-writer STOP. Tests use mock readers and disposable Git remote fixtures. The source reader remains default-off.

## Independently observed verification receipts

- `git ls-remote origin refs/heads/feat/work-c-reader-negative-worker4-20261118 refs/heads/jarvis-review/.../ddeddf88e29f45d903729b0688e8b7a9/r1`: both exact `ddeddf88e29f45d903729b0688e8b7a9e949eb09`.
- Worktree HEAD same, clean `git status --porcelain=v1` both before and after tests, `git diff --check base head` exit 0, `git show --check head` exit 0.
- `git diff --name-status base head`: only two added C-owned diagnostic/tests and two minimal additive project includes. No edit of `WorkLegacySourceWitness`, `OriginalGitPublisher`, Client, E/F/G/H, work scheduler, publisher or import live interfaces.
- `git grep WorkLegacyImportAuthenticatedReadDiagnostics -- Server Server.Tests`: only declaration/compile includes/test references; no production registration.
- Exact frozen .NET SDK **11.0.100** Release focused: **20 pass, 0 fail, 0 skip** after build; adjacent WorkLegacy **79 pass** and OriginalGitPublisher **24 pass**, both 0 fail/skip. All command exit codes 0 and no timeouts.

## Residual distinctions (non-blocking here; STOP elsewhere)

The canonical shape validation never authenticates a remote repository or its independently observed physical commit. A caller-supplied SHA/URL/digest or echoing test reader cannot elevate authority. Production C requires source-owned authenticated account/project reader and independent remote ref/effect receipt; serializing original publisher and complete STOP; durable pre-journal high water/unknown reconciliation; original UUID/request/result/ref/lineage; C1 post-capture reinspection; and independently accepted integration and operator cutover. These dependencies are outside this task and remain **HOLD**. Whole-project .NET 11 Release and hosted/production checks remain **UNKNOWN**.