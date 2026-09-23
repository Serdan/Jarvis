# Project Structure

## Main directories

- **Client/** — Local workspace bridge, OAuth login, SignalR connection, permissions, project/file operations, processes, jobs, git, and console UI.
- **Client.Tests/** — Client and workspace-safety tests.
- **Common/** — Shared command protocol, capability metadata, result DTOs, and SignalR contracts.
- **Server/** — Public OAuth-protected MCP resource server and authenticated SignalR routing service.
- **Server.Tests/** — Server authentication, routing, MCP metadata, and error tests.
- **plugin/** — Portable plugin manifest, MCP configuration, and Jarvis workflow skill.
- **scripts/** — Build and publish automation.
- **docs/** — Design, privacy, deployment, and protocol documentation.
- **artifacts/** — Generated build/publish outputs; ignored by git.

## Key files

- **Common/Messages/AgentCommand.fs** — Protocol 3 command types and authoritative local-command capability catalog.
- **Common/SignalR/IHubService.fs** — Authenticated device registration contract.
- **Client/Modules/OAuth.fs** — Native Authorization Code + PKCE login and in-memory token refresh.
- **Client/Modules/DeviceIdentity.fs** — Persistent non-secret local device identity.
- **Client/Modules/ProjectBrowser.fs** — Project discovery, search, reads, writes, and patches.
- **Client/Modules/PermissionPolicy.fs** — Local confirmation and session-grant policy.
- **Client/Modules/ProcessEnvironment.fs** — Child-process credential-environment filtering.
- **Client/Modules/ClientShell.fs** — Bounded process, project-task, and git operations.
- **Client/Modules/JobManager.fs** — Long-running local jobs.
- **Server/Auth.fs** — OAuth scopes, issuer/resource metadata helpers, subject and scope extraction.
- **Server/McpTools.fs** — OAuth-scoped MCP tool surface and profile tool.
- **Server/Services/UserService.fs** — Authenticated user/device connection lifecycle.
- **Server/Services/HubService.fs** — Authenticated SignalR hub.
- **Server/Services/ClientService.fs** — Command forwarding and response tracking.
- **Server/Program.fs** — JWT bearer validation, OAuth discovery, domain-verification endpoint, MCP and SignalR routes.
- **plugin/plugin.json** — Portable plugin manifest.
- **plugin/mcp.json** — Production MCP server connection.
- **plugin/skills/project-work/SKILL.md** — Jarvis coding workflow guidance.
- **readme.md** — Project overview and operational documentation.
