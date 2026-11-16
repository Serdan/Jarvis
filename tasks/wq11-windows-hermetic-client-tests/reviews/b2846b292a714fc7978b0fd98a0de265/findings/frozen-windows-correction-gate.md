# WQ11 frozen correction review findings — Reviewer 1
Review: `b2846b292a714fc7978b0fd98a0de265`  
Frozen commit: `dfdebee64ac3ebb08a2a5282709d7e3ddc0a7077`  
Base: `1cf0bf7ce6057430f616655f03f33227015a8e4e`  
Verdict: **ChangesRequested** (F03 remains blocking)

## F01 — prior P1 parent prewrite profile isolation: corrected
- Runner `scripts/test-client-hermetic.fsx:64-88` now demands explicit pre-existing Windows scratch parent and invokes `WindowsDisposableGuard.attestScratchParent` before directory creation at `:105-121`.
- `Client.Tests/HermeticWindowsGuard.fs` opens process token and queries `GetUserProfileDirectoryW`; it resolves actual directories via `GetFinalPathNameByHandleW` volume-GUID identities, disallows linked ancestors, and excludes both real token profile and parent-process Roaming/Local AppData paths, including enclosing roots and aliases. Absent/unverifiable identity raises rather than falling back to shell mapping.
- NUnit retains a second `attestScratchParentTokenOnly` check from its own process after fake HOME/APPDATA values are installed. Pure synthetic-profile rejection and Windows junction tests have been added.
- These changes address F01's specific missing native parent-process check before scratch allocation.

## F02 — prior P1 private NTFS scratch ACL: corrected
- `WindowsDisposableGuard.createPrivateRoot` constructs a protected DACL granting only current user SID FullControl, creates directory with the descriptor, then reads owner and access rules back. It rejects inherited grants on root, non-owner allow/deny ACEs, incomplete rights and reparse roots.
- `scripts/test-client-hermetic.fsx:129-134,174-180,349-357` verifies children and gitconfig and checks root again before cleanup. `HermeticHarnessTests.fs:174-187` no longer skips owner-only checks on Windows.
- Windows negative test deliberately injects an Everyone allow rule and asserts rejection, then restores owner-only inheritance. Reviewer 2 independently ran this and related focused tests at prior frozen `aa24fbddca35a4b826cfd04be9d6dd0a469bfe8c`: 11/11 passing, job `f149de2a158d4dcf8f120e3c20b199b9`. The correction between that frozen SHA and this one is only the Windows upper timeout bound.

## F03 — P1 BLOCKER: exact-SHA full Windows suite has not passed
- Clean detached current-source Windows job `1040c3c905584cdfa74c75449f8e281a`, `dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent C:\\Windows\\Temp --no-build --timeout-seconds 21600 --self-test-git-sentinel`, started 2026-11-16 02:13:59 UTC and was **Running** at review inspection. A running job is not a pass; no completed full-suite exit/result counts and cleanup evidence was available.
- Prior `aa24fbdd` full job `a3f96a8ee1d74fc79aa07cbf21fb10ec` completed with exit `124` after 1,200 seconds. Earlier full 1,800-second job `469f1e79a43f40afb7d5c6d1eea3c512` also exited `124`. The 21,600-second Windows maximum in `dfdebee6` addresses timeout headroom, not test completion or hermeticity itself.
- **Required:** exact commit fingerprint and clean read-only QA checkout identity; completed `.NET 11 Release` full `Client.Tests` count/passed/failed/skipped, exit, timeout and descendant-drain evidence; private scratch and package/profile containment and cleanup; concurrent and negative-path outcomes. No hosted Actions, main integration, deployment, or portability claim until evidence is reviewed.

## F04 — P2: early root-creation failures bypass cleanup
- `scripts/test-client-hermetic.fsx:105-120` creates root and checks attributes **before** entering the `try/finally` at `:156`. `Client.Tests/HermeticWindowsGuard.fs` also verifies the DACL immediately after creation. If verification, attributes, or a reparse check throws after the directory was created, cleanup at `:349-358` does not execute.
- This can leave an empty, freshly allocated scratch directory on disk on an abnormal fail-closed path. It does **not** by itself prove sensitive data were written, but violates the task's exact failed-setup cleanup contract.
- **Required fix:** bring creation and attestation into a guarded acquisition/cleanup scope, remove only independently verified owned roots, and report unrecoverable unsafe cleanup explicitly rather than silently leaving an orphan. Add a synthetic failure-injection regression after creation before normal setup.

## Reviewer verification limits
- Read-only Git diff and source inspection of exact commit, plus an isolated, initially clean reviewer worktree; `git diff --check` exited 0.
- Local Linux .NET SDK 11.0.100 FSI invocation with `--self-test-failed-setup` compiled the shared guard, intentionally exited 1, and printed `scratch cleanup verified = true`.
- `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore` in fresh reviewer worktree exited 1 **NETSDK1004**, solely because local `project.assets.json` had not been restored; this is not an implementation test failure and not a build pass.
- No independent Windows full-suite pass, no GitHub-hosted test, no operator-profile test execution, and no source/main mutation claimed.
