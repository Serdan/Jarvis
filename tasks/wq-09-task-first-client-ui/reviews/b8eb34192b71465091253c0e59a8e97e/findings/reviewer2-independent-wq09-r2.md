# Reviewer 2 independent WQ09 correction audit — ChangesRequested

**Task:** `wq-09-task-first-client-ui`  
**Task-bound review:** `b8eb34192b71465091253c0e59a8e97e`  
**Frozen exact source:** `2ddbaecf7144c9b27a102bdcdecd125df26ead60`  
**Immediate parent:** `6ed915d4615c9cc795c3da786e461a7bd0ac1f3a` (Reviewer 4 ChangesRequested, decision seq 3686)  
**Independent decision:** **ChangesRequested**. Do not use this review as permission to integrate, launch, post, claim, migrate or deploy.

## Scope and source integrity

- Claimed **only** review `b8eb34192b71465091253c0e59a8e97e`; canonical claim receipt sequence **3764**. Sibling unclaimed review `468a967ef02b475a963811542ac84a15` at `bbc7b2c3b9a53840d33bfaff892308ac6dc4c2e5` is distinct and was not claimed.
- Independently ran `git rev-list --parents -n 1` for each exact SHA: **both** `2ddbaecf` and `bbc7b2c3` are direct descendants of `6ed915d`, **neither** contains the other's corrections.
- `2ddbaecf` changes exactly two files relative to its parent: `Client/Modules/TaskFirstUiProjection.fs` and `Client.Tests/TaskFirstUiProjectionTests.fs`. Relative to the accepted `5e9cc791` baseline only four files differ: these two new files and their two fsproj compile inclusions. The independently reviewed review-count UI source and operational TUI/dispatch code are unchanged.
- `git diff --check 5e9cc791..2ddbaecf` returned exit 0 with no diagnostics; the registered exact source worktree was clean after tests, `HEAD` remained exactly `2ddbaecf`. `git grep TaskFirstUiProjection -- Client` found the definition and fsproj inclusion only, not `ConsoleTui` or operational callers. The model is inert/read-only.

## Independently confirmed corrections to Reviewer 4's three defects

1. **Claimed non-clear dependency/source/integration gates now held, not healthy.** `gateHoldReason` exhaustively matches `DependencyGatePending`, `IntegrationGateHold`, `ExternalEffectObservedPendingGate`, `SourceRecoveryBlocked`; `claimedRowState` preserves the claim but shows `RecoveryHold`; blocked/held count includes these claimed rows (`TaskFirstUiProjection.fs:273-296, 428-469`). Added test mutates a real already-claimed stage using canonical `Contracts.setHardPrerequisite`, checking fresh/stale/missing-clock variants; additional table cases check all four gate variants. A gate is not cleared by a lease.
2. **Message index identity now checked.** `projectDashboard` validates key equals `SubjectMessage.Id`, nonblank IDs and nonblank Task/Role subject ID before `Threads=Known` (`:369-379`). Added negatives cover key mismatch, duplicate record IDs across mismatched keys, blank message ID and blank subject; valid role message still renders.
3. **Obvious contradictory account/project capacity now Unknown.** `capacityView` rejects missing launch admission ID, duplicate project admission IDs, absent account reservation by launch ID, live launch with released account token, uncovered project queue and invalid launch identity (`:298-340`). `Account.Tokens` is keyed by `Launch.Id`, confirmed in accepted contract and both sibling implementations. Added negative cases cover missing/released/duplicate account tokens. However, this is not a complete reconciliation; see blocking finding below.
- Additional conservative `claimedState`/`launchPhase` behavior returns unknown for absent clock or invalid stale threshold. Rendered message ID/state/body controls are sanitized. Inherited WQ01/02 `Contracts.orderAvailable`, SCC diagnostics, graph/queue/source coverage and Role(Lead) no-session behavior remain read-only; accepted review-count tests remained in the bounded suite.

## Blocking finding A — no authoritative cross-ledger high-water join, but capacity shown KNOWN

**Location:** `TaskFirstUiProjection.fs:301-340`, `:354-355`, and `Client.Tests/TaskFirstUiProjectionTests.fs` test `consistentAndTerminalReleaseSnapshotsPreserveAccurateCapacityAccounting`.

`projectDashboard` accepts two independent `Result<Project,string>` and `Result<Account,string>` snapshots; it receives **no project/account head pair, shared verified high-water watermark, CAS read fence, or cross-ledger proof**. The accepted DTO definitions (`Server/WorkQueueContracts.fs:215-236`) have independent `Project.Version` and `Account.Version`, but no provenance showing these snapshots were observed consistently. `capacityView` checks only values: if a project launch `L` and the account token `L=false` match, it returns `Evidence.Known { Occupied=...; Free=... }`. The new test *explicitly asserts* `Known.Free=1` for a project with launch L and account tokens L and another project, despite no cross-ledger proof. These independently supplied maps may be stale relative to one another and cannot establish free account-global capacity.

Reviewer 4's seq3686 defect required reconciling joined **project launch/token scope and heads**, or showing Unknown/inconsistent, rather than inventing free slots. The **other** unclaimed sibling correction `bbc7b2c3` addresses this more conservatively: after checking reservations, when any project launches remain but no authoritative join is present, it returns `Evidence.Unknown "Account/project cross-ledger heads and high-water join unverified"`. The newer `2ddbaecf` removes that safeguard and returns `Known`. This is a substantive safety regression between the parallel candidates, not an aesthetic difference or test-count discrepancy.

**Required:** Do not present a `Known` free-capacity claim while project and account ledger snapshots have no verified common head/high-water evidence. Either extend an independently proven immutable adapter contract to carry and verify that join (outside this inert preview's current scope), or return `Unknown` for affected projected capacity until it exists. Add tests for matched-but-unjoined live reservations, stale/reordered account/project snapshots, and unrelated account reservations. Keep this purely read-only; no token reservation/claim/source dispatch.

## Blocking finding B — unresolved divergent frozen corrections violate current task reconciliation gate

The latest canonical task specification explicitly recognizes the two separately frozen, unclaimed corrections `468a967e` (`bbc7b2c3`) and `b8eb3419` (`2ddbaecf`), both from rejected ancestor `6ed915d`. It requires Worker 7 to inspect both diffs, preserve nonoverlapping fixes, create **one reconciled isolated exact source**, run bounded .NET 11 Release negatives, submit a **new task-bound independent frozen review**, and withdraw superseded unclaimed requests with state/version checks. Comparing `git diff bbc7b2c3..2ddbaecf` shows non-equivalent high-water behavior, so accepting `2ddbaecf` as the chosen resolution would silently drop a safety measure from its sibling. Neither review alone satisfies the current integration-preparation gate.

**Required:** Perform the canonical source reconciliation in a new commit and new task-bound review; do not accept, integrate or silently supersede either existing review on the strength of the current code/test count. Reviewer 2 has not claimed, modified, or withdrawn the sibling review.

## Additional inert-preview safety observation

`renderPreview` sanitizes `MessageId`, message `State`, and `Body` (`:492-503, :534-537`), but still interpolates caller-derived `SubjectTitle`, role IDs, work IDs, launch IDs, blockers, and launch recovery strings without `terminalSafe` (`:516-526, :530-542`). A title containing ANSI/control characters can still forge terminal presentation. This is **not a live injection surface while the module remains unwired**, but sanitize all displayed untrusted strings and add a negative fixture before any ConsoleTui/terminal exposure. The canonical records themselves must remain unchanged.

## Independent bounded Release evidence

- Ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~TaskFirstUiProjectionTests|FullyQualifiedName~AgentViewTests|FullyQualifiedName~AgentDashboardCacheTests|FullyQualifiedName~TerminalFrameTests|FullyQualifiedName~TerminalLayoutTests" --nologo --verbosity minimal` inside the clean frozen `2ddbaecf` worktree.
- Local .NET **11** build and VSTest completed exit **0**: **92 passed, 0 failed, 0 skipped (92 total)**, `Client.Tests.dll (.NETCoreApp,Version=v11.0)`. Existing FS0760 and FS3369 compiler warnings occurred in unrelated files. This is **not** an unfiltered full-suite pass.
- No source edits, GitHub Actions, Codex, main push, deployment, live Scheduler, migration, production service or UI integration operations occurred.

## Verdict

**ChangesRequested** for exact `2ddbaecf7144c9b27a102bdcdecd125df26ead60`. The three original local presentation defects have credible fixes and the bounded tests pass, but cross-ledger high-water trust is still overstated and the newer branch is unreconciled with the parallel safety correction. Keep the implementation inert and isolated; require one reconciled, newly frozen, task-bound independent review before any integration discussion.