# Jarvis deep technical review

**Date:** 2026-10-27  
**Review baseline:** main at 81036d80151a8fa4a8b0d7aa78018fe37ee314eb  
**Scope:** public MCP server, SignalR/device routing, local client, permission policy, project and file operations, repository-backed orchestration, diagnostics, tests, and deployment. The browser bridge was inspected separately as an unmerged feature branch.

## Executive assessment

Jarvis has a sound architectural foundation: authenticated MCP requests are associated with a user and a locally connected device; a typed protocol carries bounded commands to the client; local permission policy, project-root checks, Git worktrees, and repository-backed task state constrain and organize operations. The task/assignment/checkpoint distinction and connection-generation validation are particularly valuable.

The principal risks are not a need for architectural replacement, but enforcement gaps between subsystems: global task uniqueness under concurrency, instruction discovery for linked worktrees, arbitrary network fetches from the client, and process timeout completeness. Additional maintenance work concerns dashboard pagination, local privacy, lifecycle state, release health checking, and legacy protocol cleanup.

**Verification performed:** At the review baseline, `dotnet test Jarvis.slnx --no-restore --verbosity quiet` passed: **269 client tests + 60 server tests = 329 passed, 0 failed**. The build emitted non-fatal F# warnings. The review included source inspection and a live worktree-specific AGENTS.md behavior check. No destructive fault injection, penetration testing, deployment, or source changes were performed.

**Priority convention:** High = correctness, security, or availability issue affecting an established contract. Medium = meaningful operational or scaling issue. Low = maintenance and documentation.

## Findings (ordered by priority)

### JRV-01 — Global ownership claims are not consistently atomic

- **Priority:** High
- **Confidence:** High (code-path inspection; race not yet reproduced in an automated test)
- **Files:** Client/Modules/TaskStore.fs:619-627, 635-711, 916-1014
- **Observation:** AssignTask wraps its cross-worktree checks and subsequent mutation in the project-wide globalAssignmentMutation lock. ClaimTask first invokes ensureGlobalClaimAvailable, then calls updateState, which uses only serializeMutations scoped to the selected project identifier. The two paths do not share one lock for their complete check-and-write transaction.
- **Failure mode:** Concurrent claims on distinct worktrees, or ClaimTask racing AssignTask, can each pass a pre-write global availability check and commit mutually inconsistent ownership. Subsequent GetAgentAssignment/snapshot calls can identify the conflict but do not prevent it.
- **Fix:** Make every operation that can establish or reassign actionable ownership use one common project-wide transaction boundary, including a single normalization of main/worktree selectors. Keep optimistic revision checks within that boundary. Consider cross-process Git/file-based coordination if multiple JarvisClient processes can address the same workspace.
- **Acceptance:** A simultaneous ClaimTask/ClaimTask and ClaimTask/AssignTask stress test cannot produce duplicate actionable agent ownership or duplicate active claims for one task; stale revisions fail without modifying task files. Normal independent task updates remain possible.

### JRV-02 — AGENTS.md discovery fails for linked-worktree selectors

- **Priority:** High
- **Confidence:** Confirmed (source inspection and live behavior)
- **Files:** Client/Modules/AgentInstructions.fs:131-160; Client/Modules/ProjectPaths.fs:116-143
- **Observation:** resolveProjectRoot correctly understands a logical selector such as Wayfold::inn-fps-inversion. AgentInstructions.resolve subsequently constructs candidate instruction filenames as projectName + '/AGENTS.md' and reads them through resolveWorkspacePath. This treats the selector as a physical folder rather than mapping candidates to the registered worktree path.
- **Live check:** GetGitStatus on Wayfold produced the repository guidance from Wayfold/AGENTS.md. The same tool on Wayfold::inn-fps-inversion returned no guidance, although the selected worktree belongs to that repository. This confirmed a missing-instructions case; it does not prove every worktree layout fails.
- **Impact:** Worktree agents can silently miss applicable project-root and nested instructions despite successfully operating on the checkout's files.
- **Fix:** Resolve instruction candidates against the **physical selected project/worktree root**, while preserving a distinct workspace-level AGENTS.md source. Discover ancestors of the actual target path, safely validating each candidate and respecting limits. Document the source order and avoid making guidance dependent on the logical selector spelling.
- **Acceptance:** Identical applicable guidance is delivered to main and linked checkouts (including linked directories outside the normal workspace tree), with nested instructions applied only to matching paths; unsafe links and oversized guidance still fail safely.

### JRV-03 — ImportFile permits arbitrary HTTPS network destinations

- **Priority:** High (security boundary)
- **Confidence:** High (code inspection; no exploit executed)
- **Files:** Client/SignalR/Client.fs:105-190; Client/Modules/PermissionPolicy.fs:142-143; Client/Runtime.fs:9-15; Server/McpTools.fs:1059-1080
- **Observation:** ImportFile checks for an absolute HTTPS URI and bounds the received data, but does not verify that the host is an approved file provider or exclude loopback/private-network destinations. The HttpClient redirect policy is not constrained here. ImportFile is classified as a structured workspace write and is automatically allowed in default partial trust.
- **Impact:** An authenticated caller with the required scopes may be able to use JarvisClient's network position to retrieve reachable internal HTTPS content and save it into the workspace, then inspect it using workspace-read capability. This is not an unauthenticated exposure, but is a meaningful local-network trust boundary.
- **Fix:** For ChatGPT file parameters, enforce trusted origins and validate every redirect destination. For any intentional arbitrary-URL import capability, distinguish it from trusted attachment imports, block private/loopback/link-local targets (including DNS re-resolution cases), and require explicit local approval. Retain byte/time limits.
- **Related issue:** Runtime.httpClient currently constructs a fresh HttpClient on every access; use one owned/shared instance and dispose it with the runtime.
- **Acceptance:** Tests reject untrusted origins, localhost/private hosts, and redirects into them, and accept the intended approved file-download flow without weakening size limits or atomic-placement behavior.

### JRV-04 — RunCommand timeout does not bound all output-drain paths

- **Priority:** High (availability/reliability)
- **Confidence:** High (source-level failure mode; reproduction still needed)
- **File:** Client/Modules/ClientShell.fs:71-143
- **Observation:** runProcess reads stdout/stderr asynchronously and uses Process.WaitForExit(timeout). It subsequently blocks on stdoutTask.Result and stderrTask.Result. A descendant retaining an inherited pipe open can prevent stream EOF after the parent exits. The two stream tasks are therefore not bounded by the process wait timeout.
- **Impact:** The command handler can remain blocked even when the target process has exited or has been killed. The server may time out, leaving ambiguous client activity and incomplete cleanup.
- **Fix:** Use a single end-to-end deadline and cancellable asynchronous process/stream-drain coordination. Always bound final output collection, terminate descendants where appropriate, and return an explicit timeout with partial bounded output.
- **Acceptance:** A fixture whose parent exits while a child holds stdout open completes within the configured deadline; cancellation/timeout never waits indefinitely for pipe EOF.

### JRV-05 — Agents dashboard ignores snapshot pagination

- **Priority:** Medium
- **Confidence:** Confirmed (code inspection)
- **Files:** Client/Modules/AgentDashboard.fs:107-120; Client/Modules/TaskStore.fs:1218-1362
- **Observation:** AgentDashboard.load fetches only offset 0 / limit 60 for each project. The underlying snapshot exposes TotalMatches and NextOffset, but the dashboard does not continue paging or report that its content is partial.
- **Impact:** With more than 60 task records, the screen can silently omit historical or current relevant rows. Actionable-first sorting reduces risk but does not guarantee a complete view.
- **Fix:** Fetch pages until complete (with cancellation/bounds for large repositories), or explicitly display partial-result status and a way to load more.
- **Acceptance:** A repository fixture with >60 records yields complete or clearly marked partial agent state; UI refresh remains responsive.

### JRV-06 — Local activity log permissions are not explicitly private

- **Priority:** Medium (privacy hardening)
- **Confidence:** High for missing explicit controls; real-world exposure depends on filesystem parent permissions and umask
- **Files:** Client/Modules/ActivityLog.fs:95-120; compare Client/Modules/ClientConfig.fs:179-188
- **Observation:** ActivityLog creates its folder and append-only JSONL files without setting Unix owner-only permissions. ClientConfig explicitly sets private permissions for its own files. Activity metadata can contain paths, arguments, messages, and error details.
- **Impact:** Depending on local directory permissions and umask, another local account might read activity metadata.
- **Fix:** Apply 0700 on the activity directory and 0600 on files on Unix; validate/repair existing permissions as appropriate. Keep Windows ACL behavior explicit. A secondary scalability improvement is to make LoadRecent bounded without scanning all retained files.
- **Acceptance:** Permission tests with a permissive umask establish owner-only access; history replay and rotation still work.

### JRV-07 — Device and conversation routing state has no explicit expiry/recovery policy

- **Priority:** Medium
- **Confidence:** High (code inspection)
- **File:** Server/Services/UserService.fs:34-40, 222-293
- **Observation:** Session, default-device, and per-conversation selection dictionaries are maintained in server process memory without an apparent eviction policy. Selections disappear on server restart. There is no explicit expiry of old disconnected device entries or conversation bindings.
- **Impact:** Unbounded metadata growth on a long-running instance and unintuitive device selection following server restarts.
- **Fix:** Introduce bounded retention, explicit disconnected-session expiry, and documented fallback/recovery semantics; persist only the minimal device preference information if continuity is required.
- **Acceptance:** A restart and stale-conversation test show deterministic selection; stale records are reclaimed without redirecting active conversations.

### JRV-08 — Release health check validates process presence, not application readiness

- **Priority:** Medium
- **Confidence:** Confirmed (script inspection)
- **File:** scripts/install-server-updater.sh:19-40
- **Observation:** Binary SHA-256 verification and backup/rollback are present. After systemd restart, the script sleeps briefly and checks is-active, but does not validate actual application availability, MCP responsiveness, or hub behavior.
- **Impact:** A service that runs but cannot serve real requests could be classified as a successful deployment.
- **Fix:** Add bounded local readiness/health probes with meaningful failure reporting and rollback on failed readiness. Preserve update status diagnostics across the restart.
- **Acceptance:** A deliberately unhealthy-but-active service triggers rollback and an actionable failure status; a healthy service passes.

### JRV-09 — Retired WorkTask implementation and documentation remain partially present

- **Priority:** Low
- **Confidence:** Confirmed (source and README inspection)
- **Files:** Client/Modules/WorkTaskCompat.fs; Client/SignalR/Client.fs:255-259; Client/Modules/PermissionPolicy.fs:129-133; Server/McpTools.fs:268-273; readme.md
- **Observation:** The public legacy tools are retired, but internal command variants and compatibility implementations remain. The README both advertises old persisted WorkTasks and later explains they are retired.
- **Impact:** Dead/ambiguous protocol surface, extra maintenance/tests, and confusing agent/operator documentation.
- **Fix:** After confirming any remaining migration or compatibility requirement, remove unreachable variants and compatibility code, associated tests, and obsolete docs in one coherent change. Keep archival data untouched unless explicitly reviewed.
- **Acceptance:** The public catalog and README describe only supported flows; no reachable legacy command path remains and the suite still passes.

## Additional observations

- **Version traceability:** Both observed devices reported client version 1.0.0.0 while advertising different protocol versions (3.13 and 3.18 at the review time). Capability negotiation works, but a build identifier/commit SHA in diagnostics would improve incident analysis.
- **Tests and CI:** The 329-test baseline is encouraging and covers many state transitions. I did not find source-controlled CI workflow files in the tracked repository inventory. Add a visible build/test gate plus failure-injection and concurrency tests for the high-priority invariants.
- **Client jobs:** Job handle persistence across restart is intentionally not supported and correctly documented. Orchestrators must not infer agent idleness from an empty job list.
- **Task state:** Git-backed HTML + JSON, explicit owner/assignment/checkpoint/nextActor fields, revision checking, and guarded worktree retirement are all directionally sound. Preserve these invariants during remediation.
- **Server routing:** OAuth scope checks, user-bound device selection, and response correlation by connection and generation are appropriate defensive layers.
- **Browser bridge:** The browser-control work was on an unmerged feature/browser-bridge checkout when reviewed. Its opt-in loopback listener, manual registration, pairing proof, and bounded messages are sensible. Treat this as a separate pre-merge review with tests for disconnect/replacement, stale tab generation, timeout outcome ambiguity, concurrent requests, and isolation between registered tabs; do not treat it as deployed mainline functionality.

## Remediation plan

1. **Correctness first:** JRV-02 worktree instructions and JRV-01 atomic ownership, including targeted regression/concurrency tests.
2. **Security/availability:** JRV-03 network destination policy and shared HttpClient; JRV-04 bounded process I/O completion.
3. **Operational completeness:** JRV-05 pagination, JRV-06 local file permissions, JRV-07 routing-state retention, JRV-08 readiness checks.
4. **Housekeeping:** JRV-09 legacy cleanup, version traceability, CI, and separate browser-bridge pre-merge validation.

## Review limitations

This was a repository/source and existing-test review, not a full third-party dependency audit, penetration test, cross-platform runtime certification, or production disaster-recovery exercise. Findings marked high confidence are based on identifiable code paths; where no reproducer was executed, the recommended first implementation step is a failing regression test. No fixes are claimed by this report.

**Repository state during review:** main had an unrelated untracked .worktrees/ directory. That material must be preserved and excluded from any report-only commit.
