# Reviewer 2 — WQ-06 authenticated operator conversations: Accepted

**Exact frozen source:** `e13d1bc2ec08659c2dee7d9f6e4b2700a0301f33`
**Base:** `289e32754b6e2bd59b157d51c3fd79a16e881e98`
**Scope:** 11 files (517 insertions, 18 deletions) for default-disabled MCP exposure of the already accepted manual Git-backed Task/Role conversation store.

## Independent inspection

- Reviewed new MCP entries and command routing in `Server/McpTools.fs`, `Common/Messages/AgentCommand.fs`, `Client/SignalR/Client.fs`, `Client/Modules/OrchestrationApi.fs`, `Client/Modules/PermissionPolicy.fs`, configuration, tests and operator documentation.
- Confirmed all 8 MCP methods are **default-disabled** by `EnableOperatorConversations=false`. Mutations require OAuth `git:write` (and existing project `workspace:read`) and client local version-control permission. Server derives the actor `operator:<sha256(sub)>` from the verified subject; write methods expose no free-text actor or repository path.
- Client resolves a preapproved Git-bound project using `LedgerBindingConfig.load projectName`; does not create new bindings from caller input. Existing manual `WorkQueueOperatorConversations` handles per-thread optimistic Git CAS, explicit Pending/Claimed/Handled transition, original operation UUID receipt replay, handler-generation checks and audited exact-blob repair.
- Read/Write commands do not activate a project agent; passive list does not consume response alerts. No Scheduler authorization, account tokens, legacy transport guard edits, production deployment/restart or epoch migration. The documented manual queue is intentionally separate from canonical WQ04 demand; must not dual-write upon later rollout.

## Independent test evidence on frozen worktree

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueOperatorConversationsTests --nologo`: **7 passed / 0 failed**, including the new authenticated-adapter two-clone Git CAS tests, operation replay, a competing handler, released-generation stale reply rejection and invalid project identity.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~wq06_manual_conversation_mcp --nologo`: **1 passed / 0 failed**, covering default-off, missing `git:write` rejection and registered MCP methods.
- `dotnet build Jarvis.slnx -c Release --no-restore --nologo`: **pass**, 0 errors, one existing F# FS0760 warning.
- `git diff --check <base> <frozen>`: **clean**.

## Review boundary

No blocking defect identified within this bounded **manual, opt-in** adapter. The tests exercise the local Git-bound adapter and the server guard separately, not a deployed end-to-end HTTP/SignalR session; no production enablement or deployment approval is implied. Full WQ06 automatic conversation transport and Scheduler integration remain separate gated work.

**Verdict: Accepted.** Exact frozen source only; integration is a separate canonical action.
