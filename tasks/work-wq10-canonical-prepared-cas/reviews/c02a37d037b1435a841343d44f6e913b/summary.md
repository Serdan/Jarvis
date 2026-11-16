# Reviewer 4 — WQ10 canonical Prepared binding review

**Frozen commit:** `0b9af0ff62d9f82dd3cb51aa218808f699b433fd` (baseline `53105d3eaa1b476fadfe5470e962b4c5796fe8f9`).  
**Verdict: ChangesRequested.**

The implementation correctly provides a default-off typed recovery-blocked assessment, not an authenticated canonical Prepared writer. It preserves legacy source ownership, reports missing WQ07/WQ02 writer contracts explicitly, pins account/project heads, versions and high-water, and cannot authorize Scheduler activation or legacy Manager retirement. Independently reran .NET 11 Release focused tests: **9 passed, 0 failed**.

**Blocking finding (P1):** `findings/git-metadata-symlink-escape.md`. The scratch validation checks clone root and top-level `.git`, but not writable nested Git metadata. A symlink at `.git/objects` passes the preflight and `git fetch` writes outside the approved fixture. Independently reproduced with disposable remote+clone: **3 new object files outside fixture** after one fetch. This violates explicit no-external-writes requirement, including in read-only mode.

Require a fail-closed nested Git metadata confinement fix (prefer fully private isolated clone metadata) and a negative regression covering linked `.git/objects`/refs before approval. No canonical migration or production authorization should be inferred from the passing existing tests.