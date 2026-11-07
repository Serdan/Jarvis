# Bounded read and search checkpoint (JRV-2026-11-04)

This is a **work-in-progress design checkpoint**, not release-ready code. It is on fix/bounded-reads-searches and has no deployment.

## Backward-compatible source reads

Existing ReadFile continues to return a plain text string, but rejects files above 512 KiB before allocating full content. Line-range ReadFile retains its one-based, inclusive StartLine/EndLine numbering; it rejects scans over 8 MiB, ranges over 2048 lines, and returned text over approximately 256 KiB. FileOperations.ReadAllBytes has an 8 MiB allocation ceiling. Large text files use the separately typed ReadFilePage command; they do not become binary transfers (ExportFile remains separate).

ReadFilePage returns FilePath, Content, StartByte, NextByte, TotalBytes, FirstLine, NextLine and NextCursor. Pages are strictly UTF-8, default to 48 KiB and are bounded to 64 KiB. The decoder stops only at valid UTF-8 codepoint boundaries; the next page resumes at precisely the remaining byte offset. CR/LF counts are retained in the typed cursor. A 101 MiB test source is read using only a single bounded buffer.

The cursor binds the absolute path digest, file length, last-write timestamp, byte offset and current line number. File changes detected by the file metadata make subsequent pages fail explicitly instead of silently combining versions. This is **metadata-based snapshot detection**, not immutable snapshot isolation: a malicious or unusual concurrent writer restoring the same size and timestamp could evade it. A source can also change between the final metadata check and the caller receiving its response. Further review should determine whether stronger validation is required.

The new command updates the common protocol to 3.62 and adds a matching server MCP tool. The old ReadFile interface and response remain intact.

## Early-terminating search and directory enumeration

SearchFiles and SearchText use a common iterative, lazy walker; it processes matching files before recursing into directories. It no longer collects the tree before limiting results. Results are capped at 500 by an explicit MaxResults, with an implicit default cap of 100 (which fails explicitly rather than silently truncating when reached). Independent limits are 12,000 files, 2,000 directories, three seconds between traversal operations and an estimated 480 KiB output. Exceeding work/output limits yields a ValidationError beginning with "Search incomplete", not an empty or seemingly complete match list. Text search additionally scans no more than 8 MiB per file or 48 MiB across the traversal.

Existing glob matching, folder filters, worktree routing and source symlink/containment guards are retained; existing tests pass. Directory listing independently caps 2,000 entries and rejects a large directory instead of materializing it. ReadFiles permits at most 32 paths and accounts for an aggregate text response budget of approximately 700 KiB, emitting per-file resource errors when necessary.

## Verification and remaining work

Focused Release checks have **44/44 passed**, including existing ProjectBrowser and PathSafety tests, plus a 101 MiB page read, Unicode pagination, metadata cursor conflicts, symlink rejection, lazy file/text search with MaxResults=1 and hundreds of thousands of generated candidates, explicit work-budget exhaustion, bounded directory lists and ReadFiles aggregate caps.

Outstanding before considering integration:

- Full Client and Server Release suites have **not** been run on this checkpoint.
- Review remaining memory/time edge cases: output-size estimates versus JSON escaping, character count versus byte count, changes during a read, very long single lines, partial tree/search results and exact budget semantics. Current search work-limit behavior is a clear error, not resumable partial search pagination.
- The current directory walker intentionally prioritizes local file matches before folder matches, whereas the older eager search assembled folders then files. Confirm or adjust ordering expectations.
- Review source metadata cursor integrity and path-binding across the actual checkout/worktree routing.
- Conduct the requested implementation review and correction loop. No merge or deployment is authorized by this checkpoint.

All limits should be treated as distinct constraints: bytes read, returned lines, nodes traversed, elapsed work and serialized response size. The final transport's existing maxResponseBytes check remains a last line of defense, never the primary allocator budget.
