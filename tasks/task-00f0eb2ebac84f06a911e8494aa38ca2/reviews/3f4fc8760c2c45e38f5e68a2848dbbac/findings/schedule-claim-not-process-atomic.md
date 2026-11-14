# P1 — Separate JarvisClient processes can both claim and execute the same due schedule

**Frozen head:** `2ad69b6cca7966ba79cdaa9c786a40300bec6865`

**Locations:** `Client/Modules/BrowserSchedules.fs:83-120, 436-518, 528-529`; `Client/Program.fs:274-282`.

The scheduler uses a **per-Service `lock gate` and in-memory `actions` list**. Every JarvisClient process sharing the user's config uses the same `browser-schedules.json` path. There is no cross-process singleton/lock, file transaction/CAS, or shared durable occurrence-claim ledger. `setActions` simply serializes each process's state into its own temporary file and overwrites the same `path` using `File.Move(temp, path, true)`; it does not verify that the on-disk occurrence is still pending before the caller proceeds to `Task.Run execute`.

Concrete interleaving: two JarvisClient processes read a pending one-off or Manager heartbeat schedule on startup. At the due time both independently enter their own `Service.Start` lock, generate different `AttemptId`s, write `running` using last-writer-wins replacement, and each executes the same command/browser script. Their later `update` calls compare only their process-local copy and cannot undo the duplicate side effect. Even if the server rejects a duplicate device connection, this local scheduler is started before SignalR registration and runs disconnected, so server session exclusivity is not a scheduler claim.

The task explicitly requires atomic claims, no double dispatch under concurrent ticks/restarts, and Manager single-flight behavior. A per-process monitor is not an atomic shared occurrence claim.

**Required:** Enforce one durable writer/executor per schedule store (e.g. process-exclusive lifetime lock) or implement a real cross-process claim/CAS with consistent rereads and winner-only execution. Handle simultaneous startup/ticks, crash handoff, and replacement without silently overwriting another executor's state. Add a regression with two independent `Service` instances/processes using the same file, both polling the same due occurrence, demonstrating only one execution.

**Evidence:** Independent frozen source review; no modification to Worker 5's source branch.
