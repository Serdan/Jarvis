# Independent frozen review — WQ-11 Windows hermetic Client harness

**Verdict: ChangesRequested.**
**Reviewer:** Reviewer 2. **Task:** `wq11-windows-hermetic-client-tests`. **Review request:** `f5d6a59d8ea1436fa747d70875fef8bd`.
**Frozen head:** `8bfa5d7248df94755860fbdb3f73ffc03e20359b`; **baseline:** `1cf0bf7ce6057430f616655f03f33227015a8e4e`. No moving branch or later source accepted as reviewed.

## Scope and positive evidence

Inspected the exact 12-file Git delta and source for `ClientKnownFolders`, Client config/activity/device identity/file locks, NUnit global fixture, runner, Windows bounded process group host and executable routing, and GitHub Actions workflow. Git HEAD matches the frozen review, checkout is clean, `git diff --check base head` exits 0. Production path lookup remains OS-native except for the internal/friend-test-only override; no environment-variable-only production switch. Test harness sanitizes inherited `GIT_*`, installs synthetic author/committer and isolated Git global config, checks containment/reparse paths and implements bounded output, process-tree timeout and private-tree cleanup. Windows scoped process host dispatches `JarvisClient.exe` before normal Client startup; no deployment, merge or operator-profile data access was performed during this review.

Jarvis job evidence shows Linux .NET 11 Release build and frozen 5-test focused harness exiting 0, with the frozen full Linux harness job reporting exit 0. Linux results do not substitute for Windows acceptance.

**Windows CI:** https://github.com/Serdan/Jarvis/actions/runs/43705053252, exact SHA and hosted ephemeral runner, workflow #5. At the last independent query, disposable scratch attestation, Release restore/build and focused Windows tests were successful. Full Windows `Client.Tests` was still running; concurrent host and setup-failure / zero-discovery / timeout probes were pending. **Do not claim a Windows full-suite pass or exact Windows counts yet.** Prior #4 Windows SHA `8f77d7958c8c43626da9e12f561a06afa395fb04` failed its full suite (729 total, 501 passed, 223 failed, 5 skipped); that is only context and is not a result for the frozen head.

## Findings (see separate documents)

- **F01 — P1 fail-closed root provenance:** `Client.Tests/HermeticHarnessTests.fs` accepts an externally populated Windows `JARVIS_CLIENT_TEST_HERMETIC_ROOT` based on relative containment/layout without independently rejecting a caller-provided root inside the real profile. The FSI wrapper does have that guard; direct `dotnet vstest` invocation of the assembly bypasses it when environment/layout are supplied. Add a testhost-level independently verified disposable-root/protected-profile boundary and negative regression without touching real operator files. `findings/f01-windows-profile-root-not-independently-attested.md`.
- **F02 — Mandatory Windows evidence incomplete:** exact frozen CI was in progress; full/concurrent/negative Release checks and cleanup proof were not completed when reviewed. Require the exact commit's final CI counts, exits and scratch-cleanup evidence before accepting. `findings/f02-exact-windows-ci-acceptance-gates-pending.md`.

## Decision and continuation

**ChangesRequested**: F01 must be corrected and tested, and F02 must be resolved with complete hosted Windows evidence. Request a new exact-frozen independent review for corrected source; no integration, deployment, or operator profile testing is authorized.