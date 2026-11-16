# WQ-11 frozen correction review — Reviewer 3
Review: `7b3bba467f4d4930a1d0e2c0db1b3a12`
Frozen source: `1490e0b295ad84ca01ec3714cb67234ea59acf09`
Base: `1cf0bf7ce6057430f616655f03f33227015a8e4e`
Proposed verdict: **ChangesRequested** for F03 active-run recovery, with F02 documentation hardening.

## F01 — previous post-creation cleanup leak: corrected for the scoped path

`scripts/test-client-hermetic.fsx:115-172` now holds root acquisition in a guarded scope; `Client.Tests/HermeticWindowsGuard.fs:249-298` enforces protected ACL and independently attested nonrecursive deletion on post-create failures. `scripts/test-client-hermetic.fsx:206-226,439-455` uses checked cleanup, bounded transient Windows lock retries and explicit non-success when absence is unprovable. A Windows-focused regression injects an error immediately after root creation. Reviewer independently ran `dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent /tmp --self-test-after-root-creation` on Linux at the exact clean SHA: exit 1 as intended, with `scratch cleanup verified = true`. This Linux proof does not substitute for native Windows ACL tests. Worker reports Kruso Windows focused 15/15 at frozen SHA, not independently rerun by this reviewer.

## F03 — P1 BLOCKER: parent-only lease does not prove that no live testhost exists

`Client.Tests/HermeticWindowsGuard.fs:203-234` creates a `FileShare.None` lease held by the FSI **parent**. The testhost subprocess started in `scripts/test-client-hermetic.fsx:383-389` does not hold that handle. An abrupt parent crash/kill releases it even if a Windows child/testhost survives. `scripts/recover-hermetic-client-scratch.fsx:32-39,59-74` tests only the exclusive sidecar claim, root ownership/ACL/path safety, then deletes the root; it never proves the child/process tree has stopped. Therefore recovery can claim and remove an active orphaned testhost's files despite the documented intent that active runs are not recoverable. The existing active-lease regression only tests a parent that is still holding its handle; it does not exercise parent death with a surviving child.

The ordinary timeout path compounds the missing liveness gate: `scripts/test-client-hermetic.fsx:390-397` swallows `Kill(true)` failures, ignores the 10-second `WaitForExit` outcome, and only observes stdout/stderr drain; `:439-449` proceeds to scratch deletion/lease release without proving descendants exited. This is not fixed by a longer timeout, and stdout/stderr drain alone does not attest process-tree emptiness.

**Required:** Bind lease ownership to the lifetime of the entire test job (e.g. Windows Job Object kill-on-close with verified empty job, or a safely inherited/in-use child lease), and fail closed if descendant death cannot be verified. Explicit recovery must reject orphaned-but-live testhost/process-tree cases, not rely solely on a free parent-side lease; add an isolated Windows negative test exercising that transition. Preserve protected ACL/reparse checks and exact-root ownership.

## F02 — P2: runbook bootstrap still leaves ambient profile access ambiguous

`scripts/test-client-hermetic.md:54-65` correctly prohibits hosted Actions and requires a verified QA checkout/scratch parent, but immediately instructs `dotnet restore` and `dotnet build` **outside** the guarded child with only a redirected NuGet package directory. A separate checkout and `--packages` do not by themselves isolate the CLI's user-level NuGet configuration, credentials, dotnet first-run state, or known folders. On the real Kruso account these commands can read/write operator-profile paths before the FSI scratch preflight. The task explicitly requires disposable OS identity or proven no-profile-access isolation before setup.

**Required:** Make the runbook's bootstrap precondition unambiguous: execute SDK restore/build under a verified disposable Windows account/profile/VM, or document and verify an equivalently complete isolation scheme before these commands. Do not imply that an isolated checkout and `C:\Windows\Temp` package path alone attest user-profile isolation. This is a documentation/evidence correction, not evidence that the current focused test code wrote operator data.

## Evidence and boundaries

- Exact Linux reviewer checkout HEAD `1490e0b295ad84ca01ec3714cb67234ea59acf09`; `git status --porcelain` empty; `git diff --check dfdebee6..1490e0b2` exit 0.
- Reviewed harness, Windows ACL/lease guard, explicit recovery helper, runbook, and new regression/fixture deltas. No source changes or main integration.
- A first reviewer Linux probe omitted the explicit parent; that path bypasses the injected root-acquisition branch, started an unfiltered suite, and hit the reviewer command's 50-second external timeout. Its unique private `/tmp/jarvis-client-tests-P8TAo1` root was subsequently removed and verified absent; no full-suite pass is claimed.
- The frozen submission reports Linux Release build exit 0 and Kruso isolated Windows Release build and focused 15/15 exit 0. These are owner-reported evidence, not independently replayed Windows jobs.
- **The separate F03 full unfiltered Windows Client.Tests acceptance remains Blocked**, with no exact-SHA completed exit-0/count/drain/cleanup proof. This review does not authorize portability claims, GitHub-hosted Actions, production changes, or integration.
