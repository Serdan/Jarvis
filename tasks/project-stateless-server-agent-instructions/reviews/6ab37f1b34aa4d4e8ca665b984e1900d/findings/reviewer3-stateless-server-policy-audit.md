# Independent Reviewer 3 findings — stateless Jarvis server architecture policy

**Review:** `6ab37f1b34aa4d4e8ca665b984e1900d`  
**Task:** `project-stateless-server-agent-instructions`  
**Verdict:** **Accepted — exact docs-only scope**  
**Frozen commit:** `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e`  
**Required parent:** `dc71085090e4c914a3a336baba248907a3fb3030`  
**Reviewed branch:** `docs/stateless-server-project-policy`

## Independent source and scope verification

1. Read own agent and reviewer inbox, shared available reviews, the task-bound review details and canonical task specification; claimed this exact review at canonical sequence **3748**.
2. Inspected the frozen source worktree `lead-stateless-server-policy`, read root `AGENTS.md` with line numbers, and compared it independently against the exact parent.
3. `git rev-list --parents -n 1 e837d2d67277af8eb4b634ff58d5ba9e1b59d19e` returned `e837d2d67277af8eb4b634ff58d5ba9e1b59d19e dc71085090e4c914a3a336baba248907a3fb3030`: exact single parent matches the requested main baseline.
4. `git diff --name-status dc71085090e4c914a3a336baba248907a3fb3030 e837d2d67277af8eb4b634ff58d5ba9e1b59d19e` returned **only** `M AGENTS.md`. Full diff contains 31 additions and no removals. No code, configuration, CI, deployment or host settings modifications.
5. `git diff --check dc71085090e4c914a3a336baba248907a3fb3030 e837d2d67277af8eb4b634ff58d5ba9e1b59d19e` exited **0**, no output. Candidate worktree status clean (`## docs/stateless-server-project-policy`).

## Policy semantics and acceptance criteria

- **Project-wide applicability:** New `## Server architecture: stateless by contract` section is in the project's root `AGENTS.md`. Existing seven lines of canonical task/review/challenge and no-routine-notifications coordination remain byte-for-byte unchanged. Higher-layer workspace instructions remain separate.
- **Low-maintenance server:** Explicitly calls for simple authenticated relay on the least expensive viable droplet, minimizing CPU/RAM/disk/services and avoiding split authority across server and client machines.
- **Authoritative state boundary:** Durable project/workflow/scheduling/execution/recovery state belongs to clients and approved Git-backed ledgers; server-local storage must not authorize claims, launches, source effects, or other project actions.
- **Allowed durable file exception:** Jarvis-level agent instruction files are explicitly the sole intentionally durable server-filesystem exception. Replaceable in-memory connection/route data, bounded transient exports and correlation state are permitted.
- **Existing SQLite nuance:** `FeedbackStore` is acknowledged as **possibly previously authorized**; text neither declares it unauthorized nor orders removal. Only bounded, best-effort, noncanonical telemetry is tolerable if loss causes no correctness effect, backup, restore, migration, or restart dependency. No permission for introducing new durable server-owned databases/files without explicit operator approval.
- **Critical `AfterAgentIdle` protection:** Explicitly recognizes `attributed_agent_activity` SQLite watermarks as presently consequential for scheduling. Missing, reset or recreated SQLite data must never be interpreted as evidence of inactivity, and must **fail closed** until trustworthy client/ledger-owned activity evidence is available. Accordingly, existing DB must **not** be classified as safely discardable until demonstrated.
- **Testable operational invariant:** Empty-data-directory replacement/restart criterion (excluding agent instruction files and routine deployment configuration), with correctness, idempotency, scheduling safety and recovery surviving, including reconnection, multi-client, stale and unknown outcomes.

## Bounded finding / residual work

This is **acceptance of the wording and documentation-only source change**, not certification that the running Jarvis host currently meets the invariant. In particular, the `AfterAgentIdle` SQLite dependency requires separate correction and negative tests before a disposable server can be asserted. This review did not run .NET tests because no executable source changed and no runtime fix was requested. No Scheduler/source activation, GitHub Actions, remote push, main integration, deployment, migration, or Codex was performed.

**No changes requested for this frozen docs commit.** The architectural warning is explicit and appropriately fails closed rather than granting false operational assurance.
