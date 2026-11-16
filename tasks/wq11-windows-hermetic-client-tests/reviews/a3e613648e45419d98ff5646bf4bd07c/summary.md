# Reviewer 1 — WQ11 Windows hermetic Client.Tests (frozen exact SHA)

**Verdict: ChangesRequested** for review `a3e613648e45419d98ff5646bf4bd07c` at `24662203aead3b5e5289ac8f97998447783669ff` (base `1cf0bf7ce6057430f616655f03f33227015a8e4e`).

Full findings: `findings/frozen-windows-isolation.md`.

- **F01 P1:** `scripts/test-client-hermetic.fsx:72-110` performs its profile exclusion against shell known-folders, then allocates scratch before token-backed native profile attestation in the NUnit testhost. Move genuine token-profile exclusion into the parent harness **before any writes**.
- **F02 P1:** `scripts/test-client-hermetic.fsx:104-170` creates ordinary Windows directories without a restrictive/attested ACL, and `HermeticHarnessTests.fs:168-175` skips owner-only verification on Windows. Enforce private NTFS security or a separately disposable OS account and fail closed.
- **F03 gate:** Kruso has focused Windows passes and negative timeout tests, but frozen-SHA full `.NET 11 Release` job `469f1e79a43f40afb7d5c6d1eea3c512` was still **Running**, not passed, at inspection. The QA checkout remains at `bf336cc` with six uncommitted edits; immutable exact-source attestation and a completed full 735-test result were not furnished. Prior 600s full attempt exited 124. Keep Windows acceptance/portability **Blocked** pending local/self-hosted proof.

Positive: scoped test-only Client known-folder and process-helper overrides, Git identity sanitization, link checks, ReadOnly cleanup, zero-discovery fail-closed behavior. Read-only exact source reviewed in a separate frozen worktree; local .NET SDK 11.0.100. Local isolated shell validation was denied by Jarvis policy; no reviewer-authored test pass claimed.

**Next:** fix F01/F02, prove source/QA bytes match a NEW frozen SHA, complete focused/full/concurrent/failure/timeout Windows local tests with exact exit/count/drain/cleanup evidence, resubmit to shared reviewer queue. No GitHub Actions (workflow 419044061 remains disabled), main integration, production restart, or operator-profile test execution.
