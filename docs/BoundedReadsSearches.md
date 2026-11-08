# Bounded source reads and searches — JRV-2026-11-04

## Protocol and resource model

Jarvis protocol **3.62** adds the ReadFilePage command and changes SearchFiles/SearchText responses from raw arrays into typed SearchResult records. This is an intentional response-shape change: server, client, and consumers must agree on protocol version. No new duplicate search commands were added.

Each operation enforces independent limits on source bytes, decoded line length, files/directories traversed, wall-clock work, matches, and **actual encoded JSON bytes**. The final transport response-size check remains a backstop, never the primary allocation limit.

## Large-file UTF-8 paging

ReadFilePage returns FilePath, Content, StartByte, NextByte, TotalBytes, FirstLine, NextLine, and NextCursor. Byte offsets are zero-based, line counts one-based. Pages default to 48 KiB, with a 64 KiB maximum and 16-byte minimum. Strict UTF-8 decoding never splits a Unicode code point; a UTF-8 BOM is removed from only the first page. Carriage-return/newline state carries across pages.

Cursors are HMAC-authenticated with a per-client-process key. They bind the absolute checkout path digest, source byte length, creation and last-write timestamps, byte offset and line state. Forged offsets/line numbers are rejected. Tokens become invalid after client restart; modified file metadata triggers an explicit restart requirement. This is metadata-based change detection, not immutable snapshot isolation. Same-size, same-timestamp in-place edits can evade it; an adversarial local process could also replace ancestors concurrently. The separate atomic-file-mutations task addresses related filesystem risks.

## Ordinary text and binary read budgets

- ReadFile unpaged: 512 KiB actual bytes consumed, 700 KiB encoded JSON string.
- ReadFile line range: 8 MiB scan, at most 256 Ki characters per line, 2,048 returned lines, about 256 KiB selected text and 700 KiB encoded JSON.
- ReadFiles: at most 32 file paths, 4 KiB encoded JSON per path and 750 KiB aggregate JSON DTO budget; per-file errors remain explicit.
- GetProjectDetails: at most 16 configured special files and 700 KiB encoded aggregate JSON; paths are checked inside the selected checkout.
- ListDirectory: 2,000 entries, 480 KiB JSON and a three-second enumeration deadline.
- Binary read: 8 MiB source bytes, followed by the existing export/image output-size limits.

A shared limited-read stream enforces source bytes while reading, not by checking metadata and then doing an unbounded read. It also bounds individual decoded lines before building them, so files that grow during an operation cannot allocate arbitrary-sized strings. Early line ranges in huge sources remain usable, and large text is inspectable via ReadFilePage.

## Search partial results

The iterative lazy walker processes files before child folders. MaxResults=1 stops enumeration after one match. Default MaxResults is 100; explicit values range from 1 to 500. Folder filters, include/exclude globs, worktree routing and symlink containment checks remain enforced. Glob counts and lengths are bounded; regex execution has a separate 150 ms per-match timeout.

Every SearchFiles/SearchText success returns Matches, StopReason, FilesVisited, DirectoriesVisited. StopReason is one of: Finished, ResultLimit, FileVisitLimit, DirectoryVisitLimit, EntryVisitLimit, TimeLimit, OutputByteLimit, TextScanLimit. Only Finished asserts complete traversal. All other reasons mean Matches is **partial**, even if empty. This version has no search continuation cursor; narrow the query, folder or globs for follow-up work.

Independent limits: 12,000 files visited; 2,000 directories; 14,000 total entries including filtered candidates; 3 seconds between traversal operations plus a separate per-file text scan work limit; 480 KiB encoded JSON matches; 8 MiB text scan per file; pessimistic 48 MiB aggregate text scan charge; 256 Ki characters per encountered line. Normal filesystem errors remain errors rather than partial successes. OS file reads that block inside the kernel cannot be forcibly interrupted by synchronous managed code; the timers are cooperative between read and traversal operations.

## Evidence boundaries

Focused regressions cover a 101 MiB page-read, UTF-8/CRLF boundaries, cursor tampering and changed sources, invalid UTF-8, symlinks, huge-line budgets, early line ranges and searches in large files, control-character JSON amplification, normal search/glob behavior, hundreds of thousands of lazy candidates, filtered work budgets, partial search serialization, and aggregate multi-file and project-detail reads.

Full Client and Server Release runs and an implementation review are required for integration. The code is not automatically authorized for merging or deployment; rollout must account for the search response-shape change.
