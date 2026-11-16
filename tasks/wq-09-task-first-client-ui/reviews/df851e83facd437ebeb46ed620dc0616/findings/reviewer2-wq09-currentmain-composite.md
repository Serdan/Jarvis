# Reviewer 2 — independent WQ09 current-main composite review

**Canonical verdict: Accepted (inert groundwork only).**
Task: `wq-09-task-first-client-ui`.
Claimed task-bound request: `df851e83facd437ebeb46ed620dc0616`.
Frozen exact SHA: `1d87393fa5e7f99da2605b641844768c78d348e4`.
Exact parent: `ed358380400efaefcc3b95de9f03f8d1ad2678c5`.
Predecessor WQ09 inert source: `7ce5c3a085c9ad7a12d4e389686e3e7ded656200`, independently Accepted by Reviewer 5 in canonical review `9417d42e0c2a426eba4fb169271bd442`, sequence 3828.

## Independent frozen-source and integration checks

1. `git show -s --format=...` verifies the exact single parent `ed358380`, not a different main baseline. `git diff --name-status ed358380 1d87393` returns exactly four paths:
   - A `Client/Modules/TaskFirstUiProjection.fs`
   - A `Client.Tests/TaskFirstUiProjectionTests.fs`
   - M `Client/Client.fsproj`
   - M `Client.Tests/Client.Tests.fsproj`
   No WQ04, WQ05, WQ08, WQ06, live TUI, scheduler, server, or host-effect file is changed.
2. Independent `git rev-parse <commit>:<path>` confirms the implementation blob is `5ac42992a725a9e7a5531177b4db7163e5538816` in BOTH the accepted `7ce5c3a` and combined `1d87393`. Test blob is `6ba91b8f76f46b426c27bb7c4b88d378e0fde36b` in BOTH. This is **byte identity**, not a claim of semantic similarity. The review of `7ce5c3a` remains explicitly Accepted, and no code/test fixes have been silently rewritten in the composite.
3. Independent inspection of the two .fsproj diffs confirms they each insert precisely one `Compile Include` for the new implementation/test, preserving their existing includes and order. `git diff --check` exits zero. The dedicated integration worktree points at exact `1d87393` and is clean, including after the test run.
4. Frozen `git grep` against Client and Server for `TaskFirstUiProjection|projectDashboard|renderPreview|previewComposer` finds the new module's declarations and the .fsproj include but **no production caller**. Source inspection confirms `previewComposer.CanPost = false`, no mutation/claim/post/dispatch implementation, and an explicitly read-only terminal preview. This change cannot activate the live ConsoleTui or Scheduler by itself.

## Safety/invariant review retained from accepted source

- **Account-global capacity:** `capacityView` returns `Evidence.Unknown` even when account/project token and numeric version values match because this DTO cannot prove a joined cross-ledger Git head/high-water CAS fence. The tests cover independent-clone, reordered/stale, released/terminal, missing, and duplicate token states. No fictional occupied/free token count is certified.
- **Claimed nonclear gates:** `claimedRowState` checks gate before lease/clock; dependency, integration, external-effect, and source-recovery holds remain `RecoveryHold`, appear in blocked rows/count, and are not shown runnable/healthy. Tests exercise real `Contracts.setHardPrerequisite`, fresh/stale/missing clock, and stop/recovery variants.
- **Integrity and coverage:** map keys must equal canonical stage/launch/message IDs; blank message subject and incomplete message/graph/queue/source evidence produce Unknown rather than an invented empty/healthy view. Hard dependency cycle evidence includes real stage-path edges, with SCC/self-loop work withheld from Next.
- **Presentation:** the entire terminal preview passes through `terminalSafe`, stripping control/Unicode format/line-separator effects from titles, launch IDs, task/role names, cycle paths, message bodies, reply states, and unknown reasons; canonical DTO text is unchanged. `Role(Lead)` thread requires no UUID or prior session; pending/unregistered launch never invents an agent UUID.
- Existing accepted per-project unclaimed/total review counts and existing main WQ04/WQ05/WQ08 code/tests are **untouched**, so their semantics are not changed by this composite.

## Independent local test evidence

Executed on the exact frozen integration worktree (not a different source branch):
```
dotnet --version
# 11.0.100
dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter 'FullyQualifiedName~TaskFirstUiProjection|FullyQualifiedName~WorkQueue|FullyQualifiedName~AgentView' --nologo
```
Actual result: **exit 0, net11.0, 118 passed / 0 failed / 0 skipped, duration 48 seconds**. The test invocation independently rebuilt `Common`, `Client`, and `Client.Tests` in Release, validating the combined F# compile order and WQ04/WQ05/WQ08 compatible source. Output reports the pre-existing advisory FS0760 and FS3369 warnings; no errors. The full Client/Server suites were **not** independently claimed passed here; the prior Worker 7 attempted full Client suite timed out without a test summary. Filtered pass plus accepted byte-identical source, narrow exact four-file delta and live-path isolation form the bounded acceptance evidence.

## Decision and limits

**Accepted** for integrating this exact inert/read-only WQ09 composite onto main through the canonical integration workflow. The verdict does **not** authorize any live Task/Role posting, ConsoleTui switch, account/global capacity admission, work claim, Scheduler or YieldSource activation, browser launch, host-effect mutation, production migration, restart, deployment, GitHub Actions, Codex, or main push by the reviewer. The later combined WQ06/host activation remains separately gated. No independent acceptance of a cross-ledger high-water authority mechanism is inferred from this UI preview.
