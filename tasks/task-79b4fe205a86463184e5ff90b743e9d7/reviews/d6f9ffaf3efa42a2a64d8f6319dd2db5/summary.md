# Independent review — side-effect-free ObserveAgent (protocol 3.63)

**Verdict: RequestChanges — one P2 acceptance gap.**
Frozen source: `9033bcdc0213b320b1391d96a6cb305d34ffed50`, branch `refs/heads/feat/observe-agent-readonly`, base `a9adf77b0b1b75420f2d99e901319df713e817b3`, target `main`.

## Confirmed behavior

The explicit `observe_agent(projectName, agentId, targetAgentId)` path has separate actor and subject labels, requires the OAuth user's workspace-read authority and two registered same-project agents, and excludes observation from Ready-to-Working activation, TUI last-seen attribution, and automatic Working-turn guidance. Its observer label is compared with the transport actor label; **those labels originate in the same MCP argument**, so this check does not authenticate an agent independently of the OAuth user, which is documented as the authority boundary.

The implementation fetches a verified canonical Git snapshot once and uses that identical commit for observer registration and target inbox projection. It returns state/version/sequence, assignment/attempt, blocker IDs, pending counts and operator flags without notification bodies or sessions. Last-seen is explicitly local/possibly unknown, not canonical. The new regression covers 8 Ready targets across 2 runtime sessions, unchanged canonical states and snapshot, absent synthetic last-seen, mismatch/unknown/cross-project errors, and genuine self-inbox activation.

## Independent Release verification

- `dotnet build Jarvis.slnx -c Release` with installed .NET 10 SDK **passed** (0 errors).
- Focused cross-agent observation test **1 passed / 0 failed**.
- Complete `Client.Tests` **527 passed / 0 failed**.
- Complete `Server.Tests` **123 passed / 0 failed**.
- `git diff --check` **passed**; frozen worktree **clean**.

## Blocking finding

**P2 — user-visible Ready-to-Working activation evidence is missing.** The task requires `agent_inbox` to show any automatic activation's before/after state clearly. The new code changes tool metadata and documents the side effect, but the MCP result still contains only the post-activation canonical inbox. `AgentStateChanges` is an internal invalidation envelope and contains agent IDs, not before/after states; `Server/McpTools.fs` does not expose it as tool-result content. From a `state: Working` result, the caller cannot know whether this call activated the agent, and activation may have committed even if the subsequent read fails.

See `findings/activation-visibility.md` for exact reproduction path and requested public-response regression tests. Expose a committed, caller-visible before/after transition (or equivalent explicit activation flag with the before/after state), and distinguish repeat reads and post-activation errors.

## Additional context

The older `agent_inbox` continues to attribute activity to its supplied `agentId`, even if a caller misuses it for cross-agent inspection; the new API is the intended read-only alternative. This is an architectural limitation worth making explicit, but the separate observer endpoint itself is sound.

No source modifications, integration or deployment by the reviewer. Protocol 3.63 still requires a separately authorized coordinated client/server release.
