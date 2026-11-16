# WQ11 frozen independent Windows hermeticity review — Reviewer 1
Review a3e613648e45419d98ff5646bf4bd07c, task wq11-windows-hermetic-client-tests
Frozen exact commit: 24662203aead3b5e5289ac8f97998447783669ff
Base: 1cf0bf7ce6057430f616655f03f33227015a8e4e
Disposition: ChangesRequested

## F01 — P1 / blocking: token-backed operator-profile containment is checked only AFTER creating Windows scratch files

Evidence:
- `scripts/test-client-hermetic.fsx:54-79` checks `--scratch-parent` against `Environment.GetFolderPath(UserProfile/ApplicationData/LocalApplicationData)`. These are shell/special-folder values; they are not a mandatory native token-profile attestation before writes.
- `scripts/test-client-hermetic.fsx:98-110` creates `jarvis-client-tests-{guid}` directly beneath that parent, and `:144-165` creates layout folders and writes gitconfig.
- `Client.Tests/HermeticHarnessTests.fs:74-126` provides a stronger `GetUserProfileDirectoryW` token-backed profile check; however it is invoked only from `[OneTimeSetUp]` at `:249-251`, after the runner has written scratch and started VSTest. The source itself notes shell known-folder values can follow synthetic mappings.

The required guarantee is **no operator-profile reads/writes before setup**, not merely testhost refusal after allocating. The present boundary cannot establish that guarantee if shell mappings differ from token identity. Hard-fail unsafe/unattestable parents in the FSI parent process *before* `Directory.CreateDirectory`. Use the native token-profile provenance check there, or execute under a genuinely disposable OS account/profile. Add a negative preallocation test with synthetic/native-mocked protected locations (never probe the actual operator profile with file writes). Retain testhost attestation as a second independent fence.

## F02 — P1 / blocking: Windows scratch directories are not established or verified as private

Evidence:
- `scripts/test-client-hermetic.fsx:104-116, 144-170` creates the scratch directory and its content via ordinary `Directory.CreateDirectory`; only Linux applies private Unix file modes. No Windows discretionary ACL or effective-principal check is installed.
- `Client.Tests/HermeticHarnessTests.fs:168-175` explicitly skips `AssertOwnerOnly` on Windows.
- The real Kruso runner uses `C:\Windows\Temp` as the common parent; without an explicit verified private child DACL, inherited access is a deployment-dependent unknown. Checking `ReparsePoint`/junctions protects against existing links, not modification by other principals between checks and use.

Task contract requires physically contained, disposable and private test roots, including credential/Git identity material and exact teardown. Require a Windows owner-only security descriptor or equivalent independently attested disposable-account isolation on newly created scratch before writing files; reject non-private ACLs, unexpected links and ambiguous ownership; verify on testhost startup and on cleanup. Add Windows local-only ACL/adversarial link tests under disposable identities. Do not claim a private scratch boundary from lexical path, UUID and no-op ACL assertion alone.

## F03 — acceptance evidence currently insufficient for this frozen exact SHA

The canonical task handoff records a genuine Kruso .NET 11 Release build and 8/8 focused safety tests at earlier `bf336cc2055a1e873190d1f005540e1ecf839e1c`, not a full pass at `24662203`. The review's initial summary reports additional focused Windows checks (8/8 concurrent twice, 12/12 bounded, 3/3 ActivityExport, 37 discovered/32 passed with five explicit platform skips) and describes full test job `469f1e79a43f40afb7d5c6d1eea3c512` as running. Read-only Kruso `list_jobs` confirms that job is STILL RUNNING as inspected (started 2026-11-16 01:02:30 UTC), with the earlier full `b09ca45dd1854e9f8adfd62ab9402a49` having timed out exit 124 after 600 seconds. The Kruso QA checkout is `bf336cc` plus six uncommitted source changes; the claimed byte identity of those six changes to the frozen SHA was not independently attested by this reviewer. Thus no completed exact-frozen full Windows Client.Tests result exists in the evidence presently available to this reviewer. Do not infer pass from started job or successful focused runs.

Complete bounded full Windows Release tests in a verified disposable context after F01/F02, record exact source/blob fingerprint, SDK/OS, number executed/passed/skipped/failed, exit, timeout, descendant drain and scratch deletion. Include failure/timeout and concurrent host tests. Keep GitHub-hosted Actions workflow 419044061 disabled, and do not treat a hosted run or Linux-only output as Windows acceptance. Until then the Windows full-suite/portability gate is Blocked, not Accepted.

## Other review observations and validation boundary
- Positive: test-only `ClientKnownFolders.installForTests` is internal/friend-visible and production defaults to OS locations; activity/config/identity/storage redirection is centralized. `BoundedProcess.installTestHelperHost` is likewise test-scoped, uses the .NET 11 Release apphost rather than VSTest as the group helper, and the nested Windows job addresses pipe-holding descendants. Runtime Git variables are cleared and test-only identity installed. Scratch cleanup handles readonly Git objects, and Windows count parsing rejects zero-discovery success. These are meaningful improvements, but do not establish the missing boundary.
- Independent source inspected read-only at exact `24662203` in a dedicated `reviewer1-wq11-frozen-24662203` worktree; `git diff --stat` shows 24 changed files, 779 insertions/109 deletions against base. Current local Linux SDK is 11.0.100. Independent Linux restore/build/test was attempted through a disposable wrapper but Jarvis refused the shell executable (`PermissionDenied: Shell executable is denied by default: bash`); no test pass is claimed for this reviewer. No Windows test command, hosted Actions, main push, production restart, profile mutation or source edit was performed.
- Source review and available Kruso job metadata are the evidence boundary. Reviewer 1 is not the initiating agent for Kruso's buffered jobs, so the job results were not independently retrieved.

## Required next actions
1. Move true token-backed scratch-parent rejection into preallocation runner, keep independent testhost fence, add safe negative test.
2. Enforce/attest Windows private ACL or disposable-account isolation for scratch and descendants before any writes; test failure closed on unsafe DACL/links.
3. Finish exact-frozen, local/self-hosted Windows .NET 11 Release full/focused/negative/concurrent runs with immutable source fingerprint and cleanup evidence, then request a NEW frozen independent review. No hosted CI or main integration meanwhile.
