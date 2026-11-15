# Reviewer 1 — independent frozen review: CHANGES REQUESTED

**Pinned source:** `c5a2a1f5c3d784e7bb275a9e7be97ae214e0c7f3` against `90ed868d04524d0483ccd979cffb5029d8cf7027`. **Scope:** four test/harness files only, no production source mutation. **Verdict:** `ChangesRequested`.

**Independently checked:** exact committed patch, underlying ClientConfig and ActivityLog path logic, test project and Git fixtures; Linux runner smoke `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter FullyQualifiedName~HermeticHarnessTests --timeout-seconds 120` passed **1/1**, exit **0**, reported disposable cleanup. Owner's updated terminal evidence reports full .NET 11 Release **728/728 passed**, exit **0**, timeout **false**, and prior failure/negative-space probes. This resolves the earlier incomplete-suite evidence gap. I did **not** independently rerun all 728 tests; Jarvis does not allow Reviewer 1 to read Worker 3's private tool-job buffer.

**Blocking source findings:**
- [F01](findings/f01-symlink-containment.md) **P1** — externally managed sandbox root accepts symlink/junction escape via lexical prefix; fail-closed assertion can pass for an outside physical path
- [F02](findings/f02-git-identity-inheritance.md) **P1** — `GIT_CONFIG_GLOBAL` does not override inherited `GIT_AUTHOR_*`, `GIT_COMMITTER_*` or other higher-precedence config overrides; test identity may leak
- [F03](findings/f03-windows-known-folders.md) **P1 portability** — Windows known-folder locations do not follow `APPDATA`/`LOCALAPPDATA` env overrides; tests fail closed rather than run hermetically in normal Windows profiles

**Re-review gate:** Resolve F01/F02 with regression evidence from safely disposable test paths and injected sentinel identities. Correct or explicitly scope/track F03 before cross-platform use. Push a **new exact commit**, re-run focused and full bounded Release validation against that exact SHA, and submit a new frozen review. Do not merge based on this request.

---

# WQ-11 Hermetic Client.Tests — exact frozen review evidence

**Source and base.** Review exactly pushed commit `c5a2a1f5c3d784e7bb275a9e7be97ae214e0c7f3` on `refs/heads/fix/wq11-hermetic-client-tests`, against accepted base `90ed868d04524d0483ccd979cffb5029d8cf7027` (`refs/heads/main`). The registered worktree is clean and tracks `origin/fix/wq11-hermetic-client-tests`. Frozen source has not been modified since review claim.

**Scope.** `Client.Tests/HermeticHarnessTests.fs`, `Client.Tests/Client.Tests.fsproj`, `scripts/test-client-hermetic.fsx`, `scripts/test-client-hermetic.md`. Global NUnit fail-closed private-config/temp/Git identity fixture and per-run disposable HOME/XDG/activity/temp/Git-global-config runner; process-tree bound, zero-discovery/nonzero-exit propagation, concurrent and failed-setup isolation. No production, scheduler, WQ-01, or live account-ledger changes.

**Exact final .NET 11 Release evidence (completed after submission).** Jarvis `GetJobResult` for tool job `13aedc4c2fe744df831c5bf3f5110e52` returned `ExitCode: 0`, `TimedOut: false`, empty stderr. The bounded hermetic full-suite invocation used `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --timeout-seconds 720`. That script launches `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore` with a disposable `--results-directory`, normal-console logger, and `--blame-hang-timeout 75s`. NUnit printed `Test Run Successful`, `Total tests: 728`, `Passed: 728`, `Total time: 5,0386 Minutes`. The wrapper printed `Hermetic Client.Tests: exit 0; executed tests 728; disposable environment removed.` It deliberately omitted earlier verbose test-output characters; this is **not** evidence of skipped tests, since NUnit's full execution summary was retained and nonzero discovery was enforced.

**Prior and negative-space evidence.** .NET 11 Release build: zero errors; targeted activity+ledger tests 41/41; LedgerStore identity regressions 5/5; direct bare test fixture 1/1; two concurrent wrapper instances each 1/1 with distinct scratch IDs; intentionally failed setup nonzero and cleanup verified; zero matched tests exit 2. Earlier 240-second full-suite timeout and Git identity failure were *failures*, not claimed passes; corrected identity setup and bounded 720-second suite now pass. Read-only fixture path checks prevent falling through to the operator's real Jarvis config; no live config inspection or mutation was performed.

**Review focus.** Verify cleanup/failure behavior, cross-platform special-folder mapping, Git identity isolation, process timeout/zero-test semantics, and absence of unrelated source changes. Independent Reviewer 1 currently holds the originally frozen review `0fbebd7eb88d43498c8741fad61fe761`; this evidence updates its documentation only and does not alter its pinned commit. Integration remains separate and contingent on Accepted verdict.
