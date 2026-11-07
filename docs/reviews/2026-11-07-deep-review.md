# Jarvis — whole-project deep technical review

**Date:** 2026-11-07  
**Baseline:** `e50c24afceb03d7caf180dee2099144f27ebeb89` (merged `main`, protocol 3.61)  
**Review branch:** `review/deep-audit-2026-11-07`  
**Scope:** Client, Common, Server, Git-backed operation ledger and review lifecycle, workspace/file/path operations, agent activity, SignalR and OAuth routing, local jobs and process execution, TUI, persistence, testing and production updates.  
**Method:** Static end-to-end call-path inspection; comparison against the 2026-10-27 audit; isolated Release test jobs on a clean linked worktree. This is not a penetration test, a sustained load benchmark, or a proof that every path was exhaustively inspected.

## Executive assessment

Jarvis has a solid underlying design for its intended single-operator deployment. The most important invariants — authenticated user-bound routing, correlated SignalR replies tied to device generation, explicit source/worktree path selection, canonical Git-ledger transactions with replay-safe operation identities, review/assignment generation checks, and verified update checksums — are present and broadly coherent.

The dominant remaining concerns are **boundaries between individually correct components**, rather than the need for a wholesale rewrite. In particular, filesystem hash checks are not atomic with writes; subprocess timeouts do not consistently bound descendant-held pipes; file download/read operations need stronger network and resource boundaries; and some Git-ledger read paths scale with *all historical records*. These issues matter more than stylistic cleanup or further abstractions.

The original October 27 audit should not be treated as entirely current. TaskStore and V1 WorkTask paths have been removed, worktree instruction selection has been repaired, and the dashboard now has a batch snapshot reader. The still-relevant earlier observations are explicitly identified below.

## Findings in recommended priority order

### JRV-2026-11-01 — File precondition checks do not make writes atomic

**Priority:** P1 — lost update / file integrity. **Confidence:** High (source-proven race window, not stress-reproduced).  
**Evidence:** `Client/Modules/ProjectBrowser.fs:917-1002`; `Client/IO/Impl/FileOperations.fs:88-109`.

`writeFile` reads the existing contents and verifies an optional expected SHA-256, then invokes `File.WriteAllText` or `File.AppendAllText` as a separate operation. `patchFile` likewise reads and hashes a file, applies a unified diff in memory and writes it in-place at line 1001. No per-path transaction or cross-process exclusion covers the entire check-and-write interval. The underlying `File.WriteAllText` can truncate the destination before the new content is complete.

Two agents/clients can read the same original file, both satisfy the supplied expected hash, and then overwrite one another while both report success. A crash or interrupted write may leave a partial result. Existing multi-hunk test coverage establishes atomic **diff calculation**, not atomic filesystem replacement or compare-and-swap.

**Recommendation:** Introduce a single workspace-file mutation primitive that writes a staged sibling, flushes it and atomically replaces the destination while retaining safety checks. Hold a scoped mutation lock that works across independent Jarvis processes and revalidate the expected content/identity inside that boundary. Preserve permissions and file metadata deliberately. Avoid claiming that a plain rename alone supplies cross-process CAS.

**Acceptance:** Two concurrent writes against the same expected hash have at most one successful commit. A forced failure before replacement leaves the old complete file intact. Append/write/patch, nonexistent-file constraints, and worktree routing use the same invariant.

### JRV-2026-11-02 — A descendant retaining stdout can defeat the RunCommand timeout

**Priority:** P1 — availability. **Confidence:** High (source-proven; originally JRV-04 on 2026-10-27).  
**Evidence:** `Client/Modules/ClientShell.fs:66-137`; compare the bounded process-group implementation in `Client/Modules/ProjectPaths.fs:159-210`.

`runProcess` starts stdout and stderr drains, waits `timeoutSeconds` for the immediate child, optionally kills its process tree, then **unconditionally blocks** on `stdoutTask.Result` and `stderrTask.Result`. If the parent exits but a surviving child holds either inherited pipe open, the original wait succeeds and no kill occurs. The drains may therefore wait indefinitely, despite a declared command timeout.

**Recommendation:** Give process launch, exit, drain and cleanup **one end-to-end deadline**. On Unix launch a process group; on Windows use a job object. Cancel bounded stream drains and terminate the whole group/job even if the original process has already exited, with bounded reaping. `WorktreeGit.list` already demonstrates most of this pattern.

**Acceptance:** A deliberately detached child that keeps stdout/stderr open cannot delay `run_command` beyond the configured deadline plus small cleanup allowance; partial output remains bounded.

### JRV-2026-11-03 — HTTPS imports lack destination/redirect trust restrictions

**Priority:** P2 — authorized-caller network boundary. **Confidence:** High (source); originally JRV-03.  
**Evidence:** `Client/SignalR/Client.fs:298-368`; `Client/Modules/PermissionPolicy.fs:135-136,184-198`; `Client/Runtime.fs:10`.

`ImportFile` verifies only that `download_url` is an absolute HTTPS URI. It passes the URL to a default `HttpClient` with normal redirect behavior; no restrictions prevent access to loopback/private network addresses or a redirect into one. `PartialTrust` auto-allows non-process operations, including this network-plus-workspace write. An authorized OAuth caller can potentially fetch an internal HTTPS resource reachable from the client's machine, write it into an allowed workspace, and read it back. This is **not an unauthenticated vulnerability**, and its impact depends on what the connected client can reach.

**Recommendation:** Distinguish trusted ChatGPT attachment imports from generic URL imports in the typed API. For attachment imports require approved origins through every redirect. If arbitrary URL imports are intentionally supported, require explicit local permission and a defined egress policy including private/link-local/loopback IPs, DNS re-resolution and redirects. Enforce independent size/time limits. Own/reuse the `HttpClient` rather than allocating a new one per `Runtime.httpClient` access.

**Acceptance:** Tests reject private hosts and redirect pivots, preserve the legitimate attachment flow, and show that generic network imports are not silently authorized under partial trust.

### JRV-2026-11-04 — Source reads and searches are bounded only *after* expensive work

**Priority:** P2 — client memory/latency. **Confidence:** High (source).  
**Evidence:** `Client/Modules/ProjectBrowser.fs:685-725,767-856`; `Client/IO/Impl/FileOperations.fs:13-35`; `Client/SignalR/Client.fs:199-249`.

Unrestricted `read_file` invokes `File.ReadAllText` without a file-size cap. The line-numbered path materializes the selected lines into a list and then a string, with no upper endLine by default. The response is serialized in full **before** the protocol checks `maxResponseBytes`. A large file can therefore consume memory/CPU far beyond the advertised response limit. `search_files` collects the recursive listing before applying `maxResults`; `search_text` scans a potentially large tree for a bounded number of hits but has no independent traversal/deadline budget.

**Recommendation:** Check lengths before allocation and stream bounded text pages with offsets/continuations, line and byte limits. Cap traversal, file count and execution time separately from returned result count. Preserve a clear `truncated` or continuation indication rather than silently eliding data.

**Acceptance:** A 100+ MiB file can be paged without a 100+ MiB managed string allocation, and a huge tree cannot trigger unbounded traversal on `maxResults=1`.

### JRV-2026-11-05 — AgentInbox scales with global historical ledger records

**Priority:** P2 — operational performance. **Confidence:** High for repeated full traversal; no before/after benchmark.  
**Evidence:** `Client/Modules/ReviewWorkflow.fs:2334-2462`; `Client/Modules/LedgerStore.fs:957-975,1010-1064`.

Every `getAgentInbox` fetches the canonical head, enumerates all notifications, reads all integration records, enumerates task paths **twice**, then loads all tasks, attempts and reviews to assemble one agent's inbox. Many `required<T>` reads route through Git `ls-tree`/`show` lookups. The work is proportional to the ledger's total historical content, not the one agent's actionable records. In a multi-agent repository, frequent inbox polling magnifies Git-process launch and remote-fetch costs.

**Recommendation:** Reuse one snapshot enumeration, apply the existing `cat-file --batch` pattern from the AgentDashboard, and introduce a derived per-agent read model or index if measurements warrant it. Maintain the canonical ledger as authority and ensure updates to the read model are transactionally validated or safely recomputable.

**Acceptance:** A fixture with many historical tasks/reviews/notifications shows bounded Git process count and proportional latency; inbox semantics and snapshot consistency are unchanged.

### JRV-2026-11-06 — Activity history and last-seen files lack explicit owner-only permissions

**Priority:** P2 — local privacy hardening. **Confidence:** High for absent safeguards; exposure depends on the host directory modes and umask. Originally JRV-06.  
**Evidence:** `Client/Modules/ActivityLog.fs:30-34,96-105,119-121,152-166`; compare `Client/Modules/OAuth.fs:278-305`.

Activity history includes command details, arguments, paths and errors. The activity folder is created with standard defaults, logs are appended with ordinary `FileStream`, and the last-seen JSON is written with ordinary `File.WriteAllText`. Unlike the refresh-token store, neither directory nor files enforce 0700/0600 on Unix. Even if typical installations have a private parent folder, the component should establish its own privacy contract.

**Recommendation:** Create/repair directory at 0700 and files at 0600 (where supported), reject links, and test with a permissive umask. `LoadRecent` at lines 168–205 also scans all retained files before taking a small number of entries; use a bounded tail/reverse index for large histories.

**Acceptance:** Explicit mode tests plus retention/rotation tests preserve functionality and do not permit another local user to read private history under permissive defaults.

### JRV-2026-11-07 — Update rollback checks only systemd activity, not application readiness

**Priority:** P2 — release availability. **Confidence:** Confirmed (source); originally JRV-08.  
**Evidence:** `scripts/install-server-updater.sh:19-40`.

The deployment verifies SHA-256, stages the binary, saves `.prev`, restarts systemd and waits three seconds, but declares success after `systemctl is-active --quiet`. An active service with failed auth discovery, inaccessible MCP endpoint, dead SignalR hub, or broken dependencies can be reported as deployed successfully.

**Recommendation:** Add bounded local readiness probes for the service route, authentication discovery and hub handshake/health as appropriate. Roll back when readiness fails, and report the failed step in the updater status file. Keep a manual recovery route.

**Acceptance:** An intentionally active-but-unhealthy server triggers automatic rollback; a healthy protocol-compatible release passes.

### JRV-2026-11-08 — Server device/conversation selections grow without a retention policy

**Priority:** P3 — maintainability and long-lived server resources. **Confidence:** High (source); originally JRV-07.  
**Evidence:** `Server/Services/UserService.fs:34-40,72-168,170-192,222-293`.

The service retains one session per observed user/device ID and one selection per conversation with no expiration; disconnect marks entries but does not purge them. A restart resets all dictionaries. This is probably small in the intended single-instance, single-operator deployment, but long-lived instances have unbounded metadata retention.

**Recommendation:** Add simple in-memory TTL/pruning and explicit fallback semantics. No distributed state store is justified for Jarvis's deliberately simple server.

### JRV-2026-11-09 — Dashboard caches notification JSON indefinitely

**Priority:** P3 — memory lifecycle. **Confidence:** High (source).  
**Evidence:** `Client/Modules/AgentDashboard.fs:33-42,54-75,85-92,150-161`.

`parsedNotifications` is a global `ConcurrentDictionary` keyed by the entire notification JSON string. It never evicts entries, so it can retain every distinct historical notification and its parsed object for the lifetime of the client process. This undermines the separate snapshot-scoped caching and grows with project history.

**Recommendation:** Prefer bounded per-snapshot/oid caches with lifetime controls, or reparse lightweight notification JSON within an existing batch. Test bounded memory under a long notification history.

### JRV-2026-11-10 — MCP endpoint lacks the rate policy used by other public routes

**Priority:** P3 — defense in depth. **Confidence:** Source-confirmed configuration; impact not load-tested.  
**Evidence:** `Server/Program.fs:139-172,183-187,213-237`.

The SignalR hub and tokenized exports use `RequireRateLimiting(Fixed)`, whereas `MapMcp("/mcp")` uses authentication alone. Authenticated requests can be resource-heavy, including remote Git operations and long timeouts. Existing OAuth and local confirmation boundaries remain meaningful, so this is *not* a bypass.

**Recommendation:** Apply a sensible per-authenticated-principal concurrency/rate budget to MCP requests, preserving interactive work and not using only shared NAT/IP as the partition key. Benchmark normal agent throughput before selecting limits.

### JRV-2026-11-11 — No tracked continuous build/test gate

**Priority:** P3 — engineering assurance. **Confidence:** Confirmed by `git ls-files` inventory.  
**Evidence:** `Jarvis.slnx`; `Client.Tests`; `Server.Tests`; no tracked `.github/workflows` or equivalent CI configuration.

The project has substantial automated tests, but there is no repository-declared gate requiring both suites and a source build before merges/deployments. Manual verification was performed for the current protocol feature, but this is easy to regress as more agents contribute changes.

**Recommendation:** Add a simple CI build + Release Client/Server tests, and a release provenance check tying published artifact hashes to the exact reviewed main commit. Do not introduce an elaborate orchestration service.

## Comparison to 2026-10-27 review

| Previous item | Current assessment |
| --- | --- |
| JRV-01 global ownership claim races in retired TaskStore | Superseded by canonical Git-ledger transactional assignment; old TaskStore no longer tracked |
| JRV-02 linked-worktree AGENTS.md | Addressed by selected-checkout instruction resolution and explicit hierarchy tool |
| JRV-03 arbitrary HTTPS ImportFile | Open — new JRV-2026-11-03 |
| JRV-04 subprocess timeout and output-drain | Open — new JRV-2026-11-02 |
| JRV-05 dashboard first-page limit | Reworked with a canonical snapshot and `cat-file --batch` reader |
| JRV-06 activity log Unix modes | Open — new JRV-2026-11-06 |
| JRV-07 in-memory server selection lifetime | Open — new JRV-2026-11-08 |
| JRV-08 active-only update health | Open — new JRV-2026-11-07 |
| JRV-09 obsolete WorkTask compatibility | Old implementation no longer tracked |

## Positive verification and architectural strengths

- `Server/Auth.fs`, `Server/Program.fs` and `Server/McpTools.fs`: Auth0 bearer validation and explicit scope checks before forwarding typed commands; SignalR registration is scoped to an authenticated subject.
- `Server/Services/ClientResponseTracker.fs:16-61`: reply correlation checks user, connection and generation, rejecting stale or cross-device results.
- `Server/Services/ExportStore.fs`: cryptographically random download tokens, byte/entry quotas, expiry and no-cache delivery; export URLs are capabilities rather than publicly indexed files.
- `Client/Modules/ProjectPaths.fs`: checkout-relative path containment, link rejection and typed project/worktree targets. `WorktreeGit.list` already demonstrates deadline-aware process-group cleanup.
- `Client/Modules/LedgerStore.fs:1660-1765`: the transaction planner reruns against verified canonical Git heads; task, attempt, agent, review and idempotent operation receipts land in one tree, with expected-blob validation and replay checks after push.
- `Client/Modules/OrchestrationApi.fs`: local pending-operation journals provide recovery evidence for uncertain network outcomes, and remote IDs are checked before acknowledgment.
- `Client/Modules/OAuth.fs`: refresh tokens use owner-only file creation and link checks; child processes filter sensitive environment variable names by default.
- The recent protocol 3.61 feature correctly distinguishes source and destination activity for cross-project notifications, and scopes agent-state cache invalidation to actual typed ledger mutations.
- Baseline Release test jobs for Client and Server on this pinned review worktree both completed with exit code **0**. The job-result stream omitted per-suite counts; immediately preceding merged-main Release runs reported **392 Client** and **95 Server** tests passing. Do not infer a load/chaos-test result from these suites.

## Remediation order and suggested work breakdown

1. **Availability and integrity first:** one bounded process executor shared by run/linked-worktree Git paths; atomic, race-aware file mutation primitives, exercised by concurrency and fault injection tests.
2. **Bound external and large-data reads:** trusted attachment import/redirect policy; paged file reads and bounded search traversal.
3. **Scale canonical inbox queries:** reuse a fetched snapshot, batch Git object reads, measure before adding indices.
4. **Operational hardening:** explicit history file permissions and privacy tests; readiness-aware update rollback.
5. **Long-term maintenance:** prune in-memory caches and selection dictionaries; simple test/release CI; per-user MCP resource budget if load tests justify it.

## Review limitations

No exploit against private services, shell-escape attempt, forced process hang, destructive filesystem concurrency test, server-restart fault injection, or GitHub load benchmark was performed. Findings labeled source-proven identify real missing enforcement points, while impact and practical exploitability may depend on the operator's trust level, filesystem permissions, and number of agents. The report was written on a separate review worktree with **no application source modifications**, no deployment and no notifications to other agents.
