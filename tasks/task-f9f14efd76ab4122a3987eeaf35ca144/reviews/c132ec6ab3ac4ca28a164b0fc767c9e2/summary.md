# Source review: P2 local activity history privacy and bounded replay

**Frozen source:** `65a0f2ed5720a1811bdea23ad57a8175825439bc` (`fix/activity-history-safety`).
**Task:** `task-f9f14efd76ab4122a3987eeaf35ca144` / finding JRV-2026-11-06.
**Reviewer:** Jarvis Lead, same-session self-review; no separate independent reviewer for Jarvis.

## Invariants and evidence

1. **Private storage and migrations.** `Store` creates/chmods the dedicated activity directory 0700 and leaf JSONL/agent-seen/staged files 0600 on Unix, independent of umask. Existing broad modes are repaired. All `activity-*.jsonl` leaves including malformed legacy names are secured, and symlinks (including dangling/root swapped after store construction) are rejected. The root is expected to reside in the user's private LocalApplicationData path.
2. **Atomic agent-seen state.** A private same-directory `CreateNew` temp is written, flushed, then renamed over the destination. No file contents or command arguments enter diagnostics. Failure leaves prior state intact; TUI retries the next attributed call rather than marking it persisted. Snapshot readers permit `FileShare.Delete` so concurrent Windows rename is not blocked by a short-lived open reader. Last-seen reading is capped at 4 MiB.
3. **Bounded recent replay.** A reverse 16 KiB block reader enforces 32 MiB total bytes read per `LoadRecent`, 512 KiB maximum JSONL line, and 5000 maximum distinct snapshots. It uses chronological date + numeric segment suffix rather than lexicographic path order, repairs modes on reads, deduplicates by ActivityId using the latest persisted valid state, and normalizes Running/Awaiting permission to Interrupted. It tolerates trailing truncated/oversized records and UTF-8 BOM, CRLF, and multibyte text split at block boundaries.
4. **Error handling.** An activity append failure is diagnostic but cannot break the agent command. Last-seen saves throw a path-neutral IOException for the TUI to catch and retry. Temporary-file cleanup cannot mask the original exception, and even stderr diagnostic write failure is isolated.
5. **History selection meaning.** Old `LoadRecent` retained the last N *first-seen IDs* after a full forward scan; new reverse traversal retains the last N *updated IDs* and presents them in original StartedAt order. This is an intentional behavior change, now covered by a test where a long-running activity finishes after two newer ones. The new behavior suits a recent activity monitor, retaining recent completions while bounding I/O. An exact old selection across arbitrary late updates would require a persisted creation-order index or full historical scan; the documentation expressly states this difference.

## Negative-space coverage

- Focused Release `ActivityLogTests`: **26/26 passed** on Linux after review fixes, including default/loose modes under permissive umask, malformed and dangling symlinks, root replacement, stale/corrupt/oversized last-seen state, atomic staging and TUI retry, rotation, recent-update state ordering, Unicode, BOM/CRLF, malformed giant line, 256 MiB sparse prefix with <256 KiB read, and worst-case 32 MiB byte cap.
- Previous complete branch test (before review corrections): **495/495 Client, 96/96 Server** passed. A newer full Release job `e3f904da00d94a59a02c8eade4e57ea2` is running for the frozen revised branch and must finish before review decision.
- Exact `git diff --check` passes and `git merge-tree --write-tree main fix/activity-history-safety` indicates a clean merge. Review source and change documentation `docs/ActivityHistoryPrivacy.md` are committed.

## Boundaries

- Mode repair and symlink revalidation are intended for a private per-user directory, not same-UID actively hostile processes replacing paths between a link check and `open`. The latter requires descriptor-relative native openat/O_NOFOLLOW/handle-based validation, not represented as a guarantee here.
- On Windows `%LOCALAPPDATA%` inherits its user-private ACL; custom shared parents are not hardened into private ACLs. No native Windows or macOS test run has been claimed; that separate cross-platform task remains open.
- An update occurring far beyond the 32 MiB tail will not be replayed. The method explicitly returns a partial display tail under extreme retention rather than pretending to be a complete audit log.

**Disposition:** Source review complete; subject to final full Release result before Accept and integration.