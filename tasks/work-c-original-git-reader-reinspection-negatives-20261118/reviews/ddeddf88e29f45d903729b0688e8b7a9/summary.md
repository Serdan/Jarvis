# Reviewer 3 — independent correction review

**Decision: Accepted for the bounded, default-off Server C negative diagnostic contract only.**
This is **not** acceptance of production original-Git authentication, physical-effect observation, all-writer STOP, live import, Work Scheduler, deployment, or cutover.

## Immutable provenance and scope

- Task: `work-c-original-git-reader-reinspection-negatives-20261118`; canonical spec blob `48cfba4045b7ee139e0913a57659e03c99b0d3f2`.
- Frozen reviewed SHA: `ddeddf88e29f45d903729b0688e8b7a9e949eb09`; approved source base `5db32e1f73b0c5b2c4988a002910832fbc3d174c`; target `refs/heads/main`.
- Authored branch: `refs/heads/feat/work-c-reader-negative-worker4-20261118`.
- Review ref: `refs/heads/jarvis-review/work-c-original-git-reader-reinspection-negatives-20261118/ddeddf88e29f45d903729b0688e8b7a9/r1`.
- Independent `git ls-remote origin` returned precisely the frozen SHA for **both** refs. Registered Worker4 worktree HEAD equals the SHA and `git status --porcelain=v1` is empty after review/test execution.
- GitHub compare of approved base to frozen SHA: three commits ahead, zero behind, four allowed paths only: new `Server/WorkLegacyImportAuthenticatedReadDiagnostics.fs`, new `Server.Tests/WorkLegacyImportAuthenticatedReadDiagnosticsTests.fs`, and one additive compile include each in `Server/Server.fsproj` and `Server.Tests/Server.Tests.fsproj`.
- Final correction versus previous Reviewer 1 freeze `c2fb80500c42bf6afcd0a36eace7ff3363f371bb` changes only the two diagnostic and test files. `git diff --check base head` and `git show --check head` exited 0.

## Correctness decision: prior ChangesRequested decision 7895 resolved

I read Reviewer 1's complete prior `summary.md` and `findings/ledger-target-classification.md` for review `e20cd3ecdb3b4b88b6097b4870c18936`. Its blocking P2 counterexample was the project/account source path admitting noncanonical refs, missing the shared publisher target validator, trusted remote/binding scope checks, and Git OID shape checks; malformed known-invalid authority could be reported merely `Incomplete`.

On the frozen correction, `checkLedger` now reuses `OriginalGitPublisherAdmission.validateTarget` and additionally requires `target.CanonicalRepositoryId = fence.Binding.TrustedRemoteId`, expected account/project `fence.Binding.Scope`, valid pinned/current/fenced/original Git OIDs, and preserved ledger/epoch/entity/pinned-head checks. Fixtures now use canonical `refs/heads/jarvis-account-admission` and `refs/heads/jarvis-orchestration`. Five new NUnit cases exercise both account and project targets for noncanonical refs; malformed canonical repository IDs/fingerprints; swapped trusted remote; wrong scope; and invalid but mutually equal remote/fenced heads. This closes the exact prior accepted blocking finding without inventing a new authority or weakening the existing SourceWitness.

The prior journal-negative coverage remains intact: ordered/durable source-effect receipts, distinct operation IDs, digest and SHA/OID shape checks, separately read effect anchor, pending/unknown receipt and late-source-ref reinspection, and no positive production `Verified` result. A source grep found runtime references only in the diagnostic's definition, its project compile include, tests and the test compile include; no production reader/service registration. The probe always returns typed `Incomplete`, `Unknown`, or `Rejected`; even coherent fixture evidence terminates `Incomplete`.

## Independent commands actually run (exact frozen clean worktree)

Using `dotnet --version = 11.0.100`:

1. `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkLegacyImportAuthenticatedReadDiagnosticsTests`: exit 0, **20 passed / 0 failed / 0 skipped**, net11.0 Release rebuilt.
2. `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~WorkLegacy`: exit 0, **79 passed / 0 failed / 0 skipped**.
3. `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~OriginalGitPublisher`: exit 0, **24 passed / 0 failed / 0 skipped**.

Existing warnings in `Server/Program.fs`, `Server/Services/ClientService.fs`, and older tests are outside the diff. The reported red-first **5 failing / 15 passing** result before the fix is **author-reported**, not independently rerun; I did independently build and run the corrected suite. None of this establishes full-solution .NET 11 Release acceptance.

## Evidence boundaries / required holds

- **No blocking correction finding remains for this narrowly assigned negative-only diagnostic.**
- These are fixture/disposable-Git and static-source observations, **not** independently authenticated live project/account remote bindings, actual original physical pushes, whole-writer STOP, or complete production C0/C1/C2/SQLite importer evidence. The source task explicitly keeps production C blocked.
- Do not interpret `Accepted` as permission to integrate automatically, stop the original Git writers, activate Scheduler/API, deploy/restart, migrate/import, change credentials, run hosted CI, or cut over. All previously stated operator/Lead gates stay HOLD; **full .NET 11 Release remains UNKNOWN**.
- The reviewed branch was based on `5db32e...`; exact target-main reconciliation, separate integration review/receipt, and any required post-integration tests remain for the authorized Integrator/Lead. This reviewer made no implementation edits, branch changes or merges.

See `findings/correction-evidence-matrix.md` for the classified safety matrix and source locations.