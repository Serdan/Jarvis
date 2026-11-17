# Reviewer 2 — independent corrective Roslyn LSP hooks review

**Task:** `roslyn-lsp-hooks`  
**Request:** `f135fae7b35043a3ac75c49a5f1313cc`  
**Frozen source:** `e00abdd6432768065af9716758f6115cab4a0f1f`  
**Base:** `944bca7fa38cb5e973b04ce7829966cca351202b`  
**Previous verdict:** ChangesRequested, review `7e8cfb91eab54c67b29b174a63695d34`  
**Verdict:** **ChangesRequested**

## Independent checks and improvements

- Read the prior four review documents and current canonical task acceptance criteria; inspected corrective 11-file source/test/doc diff. The registered source worktree was clean after tests.
- Independently executed Release .NET 11 focused `CodeQualityHooksTests|GitCommandTests|WorktreeTests`: **42 passed, zero failed, zero skipped, exit 0** with operator-installed official Roslyn LSP DLL. A first no-DLL run passed 40 and skipped two opt-in live tests.
- `git diff --check 944bca7fa38cb5e973b04ce7829966cca351202b e00abdd6432768065af9716758f6115cab4a0f1f`: exit 0.
- Corrections now route a Working `agent_inbox` to source-ref-based prewarm without fallback to main; report persisted after-write results structurally; narrow unapproved nonmatching single-file pre-commit matching; and cover real Roslyn recovery, selected-only format fixes, exclusion of unrelated staged files, and blocking/advisory outcomes. These are material fixes to the earlier review findings.

## Blocking outstanding items

1. **P1 new runtime-path performance regression:** `Client/SignalR/Client.fs:71-101` and its TaskRead dispatch synchronously run bounded `git worktree list --porcelain` (and registered selector resolution) **before returning the inbox** on every sourced Working read, even when hooks are not configured; each Git discovery has a ten-second bound. The Roslyn session may prewarm asynchronously, but normal inbox response is not nonblocking. See `findings/inbox-prewarm-blocks-normal-read.md`. Source finding, no measured live stall.
2. **P1 explicit acceptance evidence still missing:** full unfiltered client suite is not passing on evidence (reported 240-second timeout); Wayfold small/large cold/warm parity and RSS, distinct compilation/analyzer contexts across worktrees, and active inbox-to-prewarm/nonblocking repeated-read tests are not evidenced. See `findings/acceptance-gates-remain-open.md`. Passing focused 42/42 does not replace these gates.

## Scope and disposition

Independent review only; no author source changes, no merging, no deployment, no Wayfold edits. The revised branch is substantially better but not yet eligible for acceptance under the stated contract. Fix the synchronous inbox cost; add targeted activation tests; provide bounded missing acceptance evidence or isolate environmental full-suite failures without treating timeout as pass. Resubmit a new frozen source review.
