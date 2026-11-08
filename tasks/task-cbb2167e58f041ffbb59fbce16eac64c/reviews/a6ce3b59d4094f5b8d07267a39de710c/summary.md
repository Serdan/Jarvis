# P2 source self-review — verified server update readiness and recovery

**Frozen implementation:** `f8b6de738716c720be92b01176c089179c9f472e` on `fix/server-readiness-rollback`.
**Task:** `task-cbb2167e58f041ffbb59fbce16eac64c` (JRV-2026-11-07).
**Reviewer:** Jarvis Lead in same session; not an independent reviewer.

## Correctness and security invariants

- The fixed-argument root helper is installed atomically from reviewed source with the narrow systemd-run sudoers rule. The update is scheduled independently of the server connection; no arbitrary executable, URL or shell payload is accepted from MCP callers.
- Before modifying the live executable, the helper verifies the existing server and its protocol routes. It downloads a fixed HTTPS artifact and matching SHA-256 manifest, enforces size/time bounds, and prepares a verified previous-binary backup.
- Readiness verifies systemd MainPID is active, hashes the running `/proc/<pid>/exe` inode against the expected binary, confirms that the same PID owns the correct loopback listener, and checks actual OAuth resource metadata, MCP 401 Bearer challenge, and SignalR negotiate 401 with client:connect. Each probe shares a finite 35-second deadline with bounded external systemctl, ss, hashing and curl calls.
- Replacement stages beside the executable (same-filesystem atomic rename) and flushes staged bytes. Rollback intent is written atomically under root-owned mode-0700 storage before stopping the service. The update is not successful until binary identity and all three routes pass.
- On failure after the journal is created, the updater stops the candidate, restores the SHA-256-verified previous binary, restarts, and independently rechecks that original process and routes. Failed recovery leaves a marker and reports the precise failure; an interrupted transaction is recovered before a future new attempt.
- Status is written atomically, readable through unchanged GetServerUpdateStatus, and contains only stage names and hashes rather than secrets. The original public authorization boundary is retained: readiness demands HTTP 401 rather than bypassing auth.
- Server starts only after initializing SQLite feedback storage; a failed critical startup dependency cannot serve seemingly healthy metadata.

## Negative-space coverage

- Black-box harness: **18/18 passed**. Its temporary root, fake process table, fake systemd and HTTP preserve executable identity across replacements. Tests cover successful update, unchanged release, wrong process hash, unrelated listener PID or address, broken MCP or SignalR routes, delayed start and transient failures, manifest ambiguity and checksum mismatch, missing previous binary, restart/rollback errors, failed final status publication, and interrupted-journal recovery.
- Real Server Release: **96/96 passed**, including the added unauthenticated SignalR route and invalid SQLite startup integration test.
- Full Client Release: **478/478 passed**. POSIX shell syntax and Git whitespace checks clean.

## Operational limits and rollout

- The helper requires an actual single-file Linux systemd MainPID, iproute2 `ss`, a Kestrel HTTP listener at `127.0.0.1:5000`, and the fixed deployed audience/Host. Configuration drift fails closed. It does not validate external reverse-proxy reachability or the ongoing availability of remote Auth0; these require release smoke verification.
- The new installer needs both source scripts at bootstrap. No root installer, production updater, or deployed systemd unit was invoked in testing.
- Protocol 3.62 search output changed in earlier merges, so actual deployment still requires a coordinated client/server release; this source acceptance does not authorize deployment.

**Review disposition:** Accept for source integration. No unresolved blocking findings in the reviewed code and tests. Evidence is Linux-only and from a same-session self-review.