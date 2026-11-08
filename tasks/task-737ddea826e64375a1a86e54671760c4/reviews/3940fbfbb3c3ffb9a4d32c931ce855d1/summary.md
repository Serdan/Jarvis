# P2 implementation self-review — bounded file reads and search (JRV-2026-11-04)

**Reviewed branch:** fix/bounded-reads-searches  
**Frozen head:** 3940fbfbb3c3ffb9a4d32c931ce855d10b35a635  
**Task:** task-737ddea826e64375a1a86e54671760c4  
**Review type:** Same-agent source and regression review, not independent reviewer.  
**Disposition:** Accepted for source correctness and integration planning; no production merge/deploy authorized by this review.

## Design and source review

Reviewed Common/Messages/AgentCommand.fs, Client/Modules/BoundedTextPages.fs, Client/IO/Impl/FileOperations.fs, Client/Modules/ProjectBrowser.fs, associated route, tool metadata and permission/instruction maps, focused tests and docs/BoundedReadsSearches.md.

- Unbounded metadata-then-read windows were closed using a shared bounded stream. A producer extending a source during a read cannot force an unlimited text or binary allocation. A 256 Ki-character single-line cap prevents StreamReader.ReadLine from allocating an entire giant line; early line ranges in much larger files still succeed. Unpaged reads are capped before the legacy response is constructed.
- New protocol 3.62 ReadFilePage exposes 16–64 KiB strict UTF-8 pages and an HMAC-authenticated continuation bound to the absolute selected checkout path, file length, creation/write timestamps, offset and line state. Tampered, cross-file and normally changed-file cursors are rejected. Cursor keys are process-scoped, so a client restart requires restarting from page one. Change detection is metadata based, not a guaranteed immutable snapshot under deliberately restored timestamps.
- Results that involve escaped text now measure actual JSON wire bytes, not raw UTF-8 or approximate item lengths. Limits cover direct/ranged/multi-file reads, directory enumeration, search previews and project-special-file aggregation. Limits apply before building the final transport response.
- A single iterative, lazy walker replaces eager recursive tree-wide collection. The code keeps checkout/worktree and symlink validation, glob matching, folder filters, and file-first traversal. MaxResults stops file enumeration early. Independent file, directory, enumerated-entry, match-count, elapsed-work, text-scan and encoded-response limits are enforced.
- The review found that work-exhaustion errors discarded useful matches. These were replaced by typed SearchResult records with Matches, StopReason, FilesVisited and DirectoriesVisited. StopReason is a closed typed union. Only Finished asserts a complete scan; every other case explicitly communicates a partial result. SearchFiles and SearchText retain their MCP command names but **change output shape**, intentionally, under protocol 3.62.
- Glob patterns are length/count checked before compilation, regex matching has a bounded timeout and matching symlink directories are checked before publication. No tree-wide index or unbounded recursion is introduced.

## Verification evidence

- **Focused Client Release:** 58/58 passed, including new adversarial BoundedReadsTests, legacy ProjectBrowserTests and PathSafetyTests.
- **Complete Client Release:** 415/416 passed (416 total). The *sole failure* was the independently tracked pre-existing 5-second JobManagerTests.startJob strips sensitive inherited environment timeout. It occurred in earlier suites and this task does not change JobManager. No bounded read/search test failed.
- **Complete Server Release:** 95/95 passed.
- **Git diff --check:** clean; final branch commit pushed to origin and worktree clean at time of review.
- Evidence covers a 101 MiB file read without full allocation, Unicode/CRLF page boundaries, file change and forged cursor, escape-amplified JSON output, long lines, early large-file matches, symlinks and worktree resolution, filtered candidate limits, multiple concurrent result budgets, typed partial response serialization, and special-file aggregation.

## Explicit caveats and release gates

1. **Full Client Release is not green** because of the existing JobManager timeout. Resolve or explicitly assess it before claiming a green release gate; do not alter an unrelated test to manufacture a pass.
2. SearchFiles and SearchText response shapes changed from arrays to SearchResult records. Coordinated server/client deployment and updating any consuming automation are required. Do not deploy mismatched versions.
3. Cursor change detection uses metadata; adversarial same-size-and-timestamp edits and concurrent directory replacement are not excluded. Neither OS-level blocking I/O nor a malicious filesystem mutation is claimed interruptible by the cooperative time budget.
4. Search partial results are usable in the current response but cannot be continued from a cursor. Callers must narrow the query, folder or globs to refine incomplete results; this is documented in the MCP descriptions and design note.

**Conclusion:** No unresolved correctness blocker was found in the reviewed implementation based on inspected code and regressions. Accept the implementation as an isolated, review-complete branch. **This acceptance is not approval to merge or deploy while the separate release readiness conditions remain open.** No other agent was notified.
