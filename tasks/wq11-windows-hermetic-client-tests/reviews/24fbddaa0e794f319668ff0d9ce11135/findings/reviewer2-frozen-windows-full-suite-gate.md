# Reviewer 2 — WQ11 frozen Windows hermeticity review (aa24fbdd)

## Scope and provenance
- Independent task-bound review `24fbddaa0e794f319668ff0d9ce11135` of **exact frozen SHA** `aa24fbddca35a4b826cfd04be9d6dd0a469bfe8c`, base `1cf0bf7ce6057430f616655f03f33227015a8e4e`.
- Read the rejected prior Reviewer 1 review `a3e613648e45419d98ff5646bf4bd07c` and its P1 F01/F02, F03 findings. Independently inspected current source diff against `24662203aead3b5e5289ac8f97998447783669ff` (4 files, +317/-9), including complete new `Client.Tests/HermeticWindowsGuard.fs`, parent `scripts/test-client-hermetic.fsx`, testhost and new Windows negatives.
- Verified connected Kruso Windows project `Jarvis-Windows-QA` registered on device `b04e8889-9ff3-4957-b058-ba50aaeab4dd` and **clean detached** checkout `.wq11-frozen-aa24fbdd` at exact SHA; the separate QA main checkout is dirty/older and was **not used** for this review. No hosted GitHub Actions, profile mutations, Wayfold or source edits.

## F01 — corrected in frozen source
- The FSI parent rejects absent/relative scratch parents; `validateScratchParent` calls `WindowsDisposableGuard.attestScratchParent` **before** `tempDirectory` is allocated. That gate obtains token-derived real profile via `OpenProcessToken/GetUserProfileDirectoryW`, attests native roaming/local known folders with no-create flags, compares resolved GUID-volume physical identities and lexical paths, and rejects linked ancestors and protected-folder overlap. NUnit independently repeats token-provenance check.
- Negative tests cover synthetic protected-profile overlap before writes and Windows junction alias rejection. These checks no longer depend on forgeable USERPROFILE or shell-only mapping as authority.

## F02 — corrected in frozen source
- `createPrivateRoot` creates a fresh Windows directory with an explicit protected DACL granting only the current token user SID owner full control **at creation time**, then re-reads owner/DACL.
- `assertOwnerOnly` rejects foreign/missing owner, unprotected scratch root, inherited or explicit nonowner ACEs, unknown denies and missing owner full-control grant. Parent verifies created children and gitconfig; NUnit repeats directory/file checks; cleanup verifies root identity/permissions and rejects linked ancestors.
- Windows adversarial nonowner (Everyone) ACE and junction negative tests passed on the actual local Windows target.

## Independent Windows execution on exact SHA
- Reviewer 2 job `f149de2a158d4dcf8f120e3c20b199b9`: `dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent C:\Windows\Temp --no-build --filter FullyQualifiedName~HermeticHarnessTests --timeout-seconds 180 --self-test-git-sentinel`, **11 passed, 0 failed, exit 0**, child drained true, scratch cleanup verified true. Scratch ID `jarvis-client-tests-c97c9ffda03e41c88d0db814afdb56c8`.
- Reviewer 2 job `f42ca3ebda4046e9bc9490740bf666b7`: `dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent C:\Windows\Temp --self-test-failed-setup`, **intentional exit 1**, no VSTest child, newly created private scratch `jarvis-client-tests-dcfa3c3e369b465e959d0531cef972ea` cleanup verified true.
- Existing Worker 3 handoff also reports Windows .NET11 Release build 0 with two pre-existing warnings, repeated focused 11/11, preallocation denial, 10-second timeout 124 and cleanup. Those are corroborating handoff evidence, not substituted for full-suite acceptance.

## F03 — P1 acceptance still BLOCKED: missing completed full Windows .NET 11 Release suite
- Full exact-SHA Kruso job `a3f96a8ee1d74fc79aa07cbf21fb10ec`, started `2026-11-16T02:00:28Z` with `--timeout-seconds 1200`, remained **Running** at this review. Its buffered output verifies private scratch creation and child VSTest start only, not a full completed result. There is no verified final exit/count (passed/skipped/failed), descendant drainage or cleanup for this full run.
- Task's explicit frozen Review 1 F03 and canonical acceptance require a **completed** Windows `Client.Tests` full suite in the disposable environment; prior timed-out/failed full runs cannot count as passing. A later eventual job success requires a new observed exact outcome and independent frozen review; do not infer success from the pending job.
- Required: finish or explicitly record failure/timeout of the exact-SHA full job; collect final executed/pass/fail/skip count, SDK/OS, nonzero/zero exit, scratch root cleanup/drain evidence, testhost isolation and repeatability. Repair any discovered full-run problems in a new frozen source and resubmit for independent review. Keep hosted Actions workflow disabled and production integration unapproved.

## Verdict
**RequestChanges (F03 only).** F01/F02 source changes and focused negative tests are independently satisfactory, but the broad Windows acceptance gate is **not** met until the full isolated `Client.Tests` job completes and its cleanup and results are verifiable. No main integration or hosted CI/production activation is authorized.