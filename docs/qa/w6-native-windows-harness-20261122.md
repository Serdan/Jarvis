# W6 disposable native Windows harness — source only

**HOLD: NO NATIVE WINDOWS ACCEPTANCE.** This is a reviewable, default-off, test-only composition plan. No Windows Client.Tests, Git restore/build, active named mutex, live profile access, client updates, deployments, or schedules are authorized.

## Canonical basis and exact custody

Worker14 read-only Kruso audit, task automation-windows-mutex-hermetic-gate-audit-20261122, checkpoint **10949**, found the current QA checkout dirty, an untracked unverified .w6-mutex-win-20261122 fixture and both the W6 runner and global NUnit SetUpFixture explicitly Linux-only. TEMP/USERPROFILE redirects cannot isolate Windows native KnownFolders. W6 reviewed source bfde56bcc79cedf923d8eaff7ea66cd0af488f23, frozen review 0f4376c3a15d4a2282ecb2de61320bcb, was accepted at a Linux/source-only boundary, not for Windows. The 150-ms timeout seam never exercised the real 60-second production wait.

WQ11 independently reviewed source e0043d41443669a3aafe11b718bef45c4af992e6, review 0eb6832b38624a42a36fb43874091a5f, provides a guard, Windows owner-only ACL, native profile/known-folder checks, GUID-volume physical path, kill-on-close job object, gated testhost launcher and drained-process receipt. It remains source-only, not an exact-e004 Windows Release TRX.

| Source | Exact Git blob |
| --- | --- |
| W6 Client/Modules/OrchestrationApi.fs | 069ad69f735797e2682dc2520e4df2f06387756e |
| W6 Client.Tests/ReviewWorkflowTests.fs | 336bf67d10ccb7d6037a6e17b53ce89a4bdd3795 |
| W6 Client.Tests/HermeticHarnessTests.fs | cc57136fc0a5ecaedf20f946d01ab54f7e08e776 |
| W6 Client.Tests/Client.Tests.fsproj | bd0a3e5acb3ab4c44e99f7ffcf896c2ec042aef2 |
| W6 scripts/test-client-hermetic.fsx | 32f3f74029eed32eb929b6d6ff8bb1ef571b0f16 |
| WQ11 Client.Tests/HermeticWindowsGuard.fs | fa31bedb9276995eb5e2a264e5cd8372c2d00b9d |
| WQ11 Client.Tests/HermeticHarnessTests.fs | 7b3e74707ed9b11c15b213247b46556efa3f8544 |
| WQ11 Client.Tests/Client.Tests.fsproj (reference, not copied) | f51378076f34402fd6da3b7dcd1ce37f06918bb4 |
| WQ11 scripts/test-client-hermetic.fsx | 5c8e9b48f0f6adfe9861a887fd28dea5b4d7e862 |

The source lease is exclusively scripts/qa/w6-native-windows-harness-20261122/** and this document. Nothing changes W6 or WQ11 tracked source, client code, main or the live Windows QA checkout.

## Guarded workflow, not executed

1. On Linux, run: node --test scripts/qa/w6-native-windows-harness-20261122/selftest.mjs. Static source-pinned verification: node scripts/qa/w6-native-windows-harness-20261122/compose.mjs --check --repo PATH_TO_READ_ONLY_GIT_OBJECTS. Both are read-only; neither is Windows acceptance.
2. Lead first establishes a separate disposable **non-admin** Windows OS SID/account or VM, outside the operator profile, with independent proof that the operator's actual profile/Wayfold/Jarvis/Git locations deny access to that test SID. The latter is an external OS proof: this harness intentionally does not probe the live profile. An owner-reported receipt is not a signed, independently verified result. No receipt means HOLD.
3. Native preflight (native-preflight.fsx) operates before scratch allocation, restore, build or testhost launch, and **must** attest OpenProcessToken/GetUserProfileDirectoryW, token-derived Desktop, Documents, Roaming and Local AppData via SHGetKnownFolderPath, GUID-volume GetFinalPathNameByHandleW, no reparse/junction ancestors, distinct token SID, non-admin privilege, pre-existing scratch parent owner with protected dedicated-SID-only full-control NTFS ACL. Unknown identity or alias means NO-GO. Environment overrides by themselves do nothing.
4. A future separately authorized source-only stage requires both a clean disposable W6 checkout at exact bfde SHA and a clean **independently reviewed** copy of this harness inside the pre-attested private parent, complete offline Git objects including WQ11 e004, and Lead-verified operator-profile-denial evidence. The stage checks exact Git blobs, refuses symlink/gitlink Git trees, and creates a new scratch source tree only after preflight. It extracts W6, verifies unchanged W6 production and ReviewWorkflowTests blobs, then overlays only WQ11 reviewed guard/fixture/runner plus a generated test project compile list and W6NativeChildGuard. The generated NUnit fixture independently calls a native testhost attestation **before** its original setup. It rechecks W6 original blobs and writes W6-STAGED-SOURCE-ONLY.json. A partial failure quarantines its scratch, not a blind recursive deletion. There is no --run implementation.
5. Actual native tests would require another authorized, reviewed step running WQ11's gated process-job testhost, preserving filtered and unfiltered TRX, terminal evidence, physical OS identity, no skipped protected negatives, positive discovery, exact root Git sentinels and complete process-tree drain. Dirty Jarvis-Windows-QA or its untracked W6 fixture must never be treated as source evidence.

Future stage syntax, **not authorization**:

node scripts/qa/w6-native-windows-harness-20261122/compose.mjs --stage --repo CLEAN_ISOLATED_W6_REPO --scratch-parent PRIVATE_EXISTING_ROOT --dedicated-sid DEDICATED_SID --operator-sid DIFFERENT_OPERATOR_SID --review-commit REVIEWED_HARNESS_COMMIT --operator-denial-receipt LEAD_VERIFIED_ISOLATED_RECEIPT

The external receipt is a human review gate, not an independently authenticated cryptographic certificate. The composer cannot and does not launch Windows Client.Tests. A successful source-only stage does not prove the F# overlay compiles, has a safe OS token, or passes the native matrix.

## Required native Release matrix — all still pending

| Obligation | W6 source test or newly required scratch fixture | Evidence gate |
| --- | --- | --- |
| Same exact UUID across two independent processes | PendingIntentExactLocksAllowUnrelatedUUIDsWithinLegacyStripe | Contender excluded; callback never double-runs |
| Distinct UUID sharing legacy 3-char prefix | Same case | Concurrent progress, no false stripe serialization |
| Killed owner / abandoned mutex | PendingIntentNamedMutexRecoversAfterOwnerProcessExits | Safe recovery, no uncertain callback replay |
| Exact identity and handle cleanup | PendingNamedMutexIdentityAndHandleCleanupLeaveNoLockArtifacts | No residual lock files/handles |
| Protected symlink/junction journal | PendingNamedMutexRejectsSymlinkJournalBeforeCallback | Runs and rejects before callback, **zero NUnit Ignore/Skip** |
| Ambiguous IOException callback | PendingLockNeverRetriesAnAmbiguousIOExceptionCallback | Exactly once |
| Ambiguous persisted uncertain intent | PendingLockRefusesOrdinaryReplayAfterAmbiguousIOException | No ordinary replay |
| Lost accepted remote response | LostResponseRecoversAcceptedRemoteOperationButNeverGuessesFailure | Accepted receipt recovery, no guessed failure |
| **Actual default 60-second wait** | NEW scratch-only W6NativeDefault60SecondWait, **not** in frozen W6 | Unmodified default TimeSpan.FromSeconds(60), measured monotonic wall-clock >=60s, owner/contender timing and cleanup, no 150-ms override |

This is a *design matrix*, not nine existing passed test cases. The 60-second test remains a missing separate, reviewable scratch-only overlay fixture. The native run must assert positive unique discovery/executed count, zero skipped protected negatives, nonzero exit on any failure, raw TRX, first error, no journal mutation on negative controls, Git sentinel before/after, job assignment, owner death, drain and cleanup. Full WQ11 native Client.Tests acceptance is separately unproven.

## Current source-only evidence and handoff

Linux static/negative job 554abce83e7246e4b39236fca55d7e84: node --test scripts/qa/w6-native-windows-harness-20261122/selftest.mjs, **7/7 PASS**, 0 fail, 0 skip, exit 0; no overlay or Windows execution. Baseline main dcac884fc061869572b12a03befc12f11c009f2f. Git object identities above were read and compared without source modification. The Linux W6 audit's 36/36, 149/149 and 5/5 are cited as **prior audit evidence**, not rerun here. Remaining gates: independent frozen harness review, dedicated native VM/token and independently verified operator-profile ACL denial, actual 60-second fixture, source-pinned Release build/test/negative terminal TRX. **Lead and Worker6: preserve Windows HOLD.**

Final verification (same frozen candidate): Linux Node job 32d9854ebcd24289a89b1412557ed330, 7/7 PASS, exit 0. Linux dotnet fsi native-preflight.fsx job cba3ac09823b44b99faacead68cb236f compiled/entered the F# script and exited **1 as required**, with W6_NATIVE_HOLD: Windows native kernel/token APIs unavailable; no Windows API call, testhost, restore, or build. This is an expected negative control, not a green Windows result.
