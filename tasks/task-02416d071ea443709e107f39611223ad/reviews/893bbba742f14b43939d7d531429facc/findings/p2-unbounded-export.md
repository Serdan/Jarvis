# P2 — DOM file export has no operation-wide deadline; may commit after server times out

Frozen source `66d4e06566c3df5e8b771875f421815e19bc6770`.

`Client/Modules/BrowserBridge.fs:461–509` applies `timeoutMs` (100–60,000 ms) only to wait/capture and the initial `snapshotReady` response. File-export chunk transfer at lines **562–590** loops over up to **8 MiB** in **32 KiB** chunks, issuing a *fresh 10,000 ms timeout for every chunk*, with **no overall transfer deadline** or propagated cancellation token. This permits up to **256 × 10 seconds = 42 minutes 40 seconds** of chunk waits; extension `readSnapshotChunk` even refreshes its retained-snapshot expiration after each successful chunk. `Server/Services/ClientService.fs:14–20` caps ordinary forwarded commands at **10 minutes**, so the server may return a timeout while the JarvisClient continues exporting and eventually atomically creates the DOM file without the caller ever observing success. The result is an indeterminate side effect across a user-specified explicit export.

**Fix:** Bound the *entire* capture/export by an explicit monotonic deadline (including chunk requests, validation, flush and final rename), propagate cancellation to pending requests and cleanup, and ensure a rejected/expired operation cannot later commit its destination file. If export is expected to outlive the generic server timeout, use an explicit durable job with reconcileable status instead. Test slow chunks and deadline exhaustion in addition to checksum/normal transfer.

**Severity: P2** — violates the explicit bounded-deadline and cleanup contract and creates late, caller-unobserved project file changes.
