# Reviewer 2 — WQ11 F04 scoped correction: Accepted (F03 remains BLOCKED)

**Frozen source:** `2f9ce99238786d1559c6c97472409b443c929006`
**Canonical review base:** `1cf0bf7ce6057430f616655f03f33227015a8e4e`
**Targeted correction comparison:** `dfdebee6` -> frozen (four changed paths: scratch harness, Windows guard, focused harness test, instruction test).
**Tree identity:** `git rev-parse 2f9ce992^{tree} 2b05a535^{tree}` both yield `1a05130424130a153339bc54f9e732904ac47c32`. An intermediate unsuccessful test-parallelization experiment was fully reverted.

## F04 independent source review

Prior frozen review `b2846b292a714fc7978b0fd98a0de265` identified root creation and post-creation verification happening before the parent `try/finally`, allowing orphaned private scratch after an abnormal failure.

- `Client.Tests/HermeticWindowsGuard.fs` introduces `acquirePrivateRoot`, which encloses atomic protected-ACL creation, attestation, and explicit fail-closed nonrecursive cleanup. On exception, it independently checks linked ancestors and owner-only ACL before removal, and reports cleanup failure rather than silently assuming success.
- `scripts/test-client-hermetic.fsx` now guards the entire explicit-parent allocation phase and moves post-acquisition mode/logging/setup under the existing `try/finally`. Negative `--self-test-after-root-creation` injects failure directly after allocation without starting testhost. No recursive delete on an untrusted acquired root.
- A Windows-only NUnit fixture `failed_windows_root_acquisition_removes_created_root_before_testhost` exercises the same native guard under an already private scratch, with immediate post-create failure.
- Adjacent changes surface bounded per-test progress and add a 180s per-fixture VSTest blame-hang limit. They do not turn a timed-out full Windows run into a pass or enable hosted Actions.

No blocking F04 regression was found. Windows native token-profile and ACL protections from F01/F02 remain in place.

## Independent verification

- Executed exact frozen `dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent /tmp --self-test-after-root-creation` on Linux: **exit 1 expected** (intentional failure), printed `scratch cleanup verified = true`, no child testhost started.
- Exact frozen hermetic runner `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter FullyQualifiedName~HermeticHarnessTests --timeout-seconds 180` on Linux: **exit 0**, **12 executed, 9 passed, 3 Windows-only skipped**, child drained true, scratch cleanup verified true.
- `git diff --check dfdebee6 2f9ce992`: **clean**.
- Worker3's submitted review summary reports native Windows F04 negative cleanup and focused **12/12 Windows tests** passing on a source tree equal to the present frozen tree; independently confirmed the *Git tree identity* but did not personally replay the Windows jobs or their buffered source-device evidence.

## Explicit remaining gate

**F03 full Windows Client.Tests remains BLOCKED.** The submitted Windows non-Challenge partition timed out after partial progress, and a parallelization experiment was abandoned/reverted. Neither a timed-out partial partition nor this Linux-only independent run establishes a complete, unfiltered Windows Release suite with all exit/count, child-tree/drain, and cleanup evidence. No GitHub Actions jobs, main integration, production rollout, or claim of Windows portability is authorized by this scoped F04 acceptance.

**Verdict: Accepted for the frozen F04 code correction only; F03 remains an open independent acceptance blocker.**
