# Independent frozen-source review: per-project review counts

**Reviewer:** Reviewer 1  
**Decision:** Accept (no blocking findings in the frozen scoped patch)  
**Frozen commit:** `9e62f60803b66fc0394f372261ec8ffaf32a8571`  
**Comparison base:** `7b589173135178a634495146675dad5db647e5f8`  
**Review request:** `77b3b13ed34644dd917e769ff2c8b217`

## Verified source/invariants

Inspected the entire base-to-frozen diff (six files, 166 additions/34 deletions) and corresponding source:
`Client/Modules/AgentDashboard.fs`, `AgentView.fs`, `ConsoleTui.fs`, plus `AgentDashboardCacheTests.fs`, `AgentViewTests.fs`, and `ReviewWorkflowTests.fs`.

1. **Canonical source and U/T semantics.** `projectSnapshot` enumerates each canonical `tasks/**/review.json` once from a verified ledger snapshot. The fold counts current `Unclaimed` as both U and T, `Claimed` as T, excludes terminal states; `_reviews` uses the same path. Malformed canonical read errors fail closed rather than produce a fabricated zero.
2. **Dedup and head consistency.** The identity is `ProjectId:LedgerId:Epoch`; `load` groups approved project aliases before fetching. Snapshot combination refuses different heads or inconsistent contents for the same identity and retains same-named distinct ledgers as separate projects. Cached derived counts are keyed by verified identity and immutable Git head, so claim/release/decide/withdraw changes become visible through the existing refresh path, not new polling.
3. **Display/unknown states.** Project rows present `Reviews U/T` or `RevU/T`; aggregate uses the same two counters. Narrow widths wrap tokens and paginate projects. `Stale` and unavailable counts remain visually distinct from known 0/0, with same-name ledger suffix display retained. Project filters apply to project summaries; text/agent filters do not silently redefine project totals.
4. **Test coverage.** The added end-to-end fixture changes the actual canonical review state through submit/claim/release/decide/withdraw and validates `(U,T) = (2,2),(1,2),(2,2),(1,2),(1,1),(0,0)` after refresh, including `_reviews` and deduped aliases. Dashboard alias/identity and width tests exercise fresh, stale, unavailable, long-name and terminal-size cases.

## Independent executable evidence

- The dedicated QA checkout `qa/review-counts-77b3` was clean and `git rev-parse HEAD` returned the exact frozen commit above.
- `git diff --check 7b589173... 9e62f608...`: exit 0, no whitespace errors.
- Independently ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~AgentViewTests|FullyQualifiedName~AgentDashboardCacheTests|FullyQualifiedName~ProjectReviewQueueTracksUnclaimedClaimedAndTerminalReviewRecordsAtRefresh" --nologo --logger "console;verbosity=minimal"` in the frozen QA worktree (Jarvis job `d8ca7b361394497a833f6c281df7b5c5`). .NET 11.0 Release Client and Client.Tests compiled; **54 passed, 0 failed, 0 skipped, exit 0**.
- Prior Worker 1 targeted job `bc34eb7a2a7c411998d3e9187bef4db7`: independently inspected receipt, 54/54 passed. A separate five-case focused job `b6074ee5f9f54e72a4afa25e7d8e508d`: 5/5 passed.
- **Do not claim a full-suite pass.** The previous full Release Client.Tests job `08840014e3b3492ea98d2ef13f754ba9` reached compilation and test start, then was **Canceled**, with no overall suite verdict. An earlier targeted build/test attempt `a8043b4af4324289ac53f488de98fbdc` failed 1/54 on a full-width assertion; the frozen test was corrected and subsequent focused 54/54 runs passed. Full-suite evidence remains an integration/CI follow-up, not a blocking source finding under this bounded review.

## Verdict and boundary

**Accept** the exact frozen scoped change. No blocking correctness finding was identified in the implemented count, identity, refresh or terminal-display contract. This is not approval to merge, deploy, claim a complete solution test pass, or activate any schedule; Lead retains integration and broader regression gates. The review does not alter other queued tasks.
