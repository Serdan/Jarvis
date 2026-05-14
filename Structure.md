# Project Structure

## Main Directories

- **Client/** - Client-side application code, including SignalR connection handling, local command dispatch, permissions, project browsing, shell/git commands, jobs, audit logging, and console UI.
- **Client.Tests/** - NUnit/FsUnit tests for project browsing, path safety, permissions, patch/hash handling, shell/git commands, and job management.
- **Common/** - Shared protocol messages, SignalR contracts, command capability metadata, permission types, and result DTOs used by both client and server.
- **Server/** - ASP.NET/Giraffe server endpoints, SignalR hub services, client tracking, options, and routing for agent commands.
- **scripts/** - .NET file-based C# build and publish automation.
- **docs/** - Additional project documentation.
- **artifacts/** - Generated build/publish outputs. This directory is ignored by git.

---

## Key Files

- **Jarvis.slnx** - Solution file managing all project components.
- **scripts/build.cs** - Build, test, clean, and publish entry point.
- **Common/Messages/AgentCommand.fs** - Protocol v2 command definitions, command capabilities, permission levels, errors, jobs, and git command types.
- **Common/SignalR/IClientService.fs** - Shared SignalR client contract.
- **Client/SignalR/Client.fs** - Client command receiver/dispatcher and response serialization.
- **Client/Modules/ProjectBrowser.fs** - Project/file browsing, search, reads, writes, and unified-diff patch support.
- **Client/Modules/ProjectPaths.fs** - Workspace/project path resolution and containment safety checks.
- **Client/Modules/PermissionPolicy.fs** - Client-side confirmation policy and session grants.
- **Client/Modules/JobManager.fs** - Long-running local job management.
- **Client/Modules/ClientShell.fs** - Bounded process execution and git command support.
- **Server/Services/ClientService.fs** - Server-side command forwarding and client response tracking.
- **readme.md** - Project overview and build/publish usage.
- **todo.md** - Pending tasks and development roadmap.
- **ProcessTracker.md** - Tracks current refactor goals, subtasks, and verification steps.
- **actions-schema** / **openai_tools.json** - Tool/action schema artifacts for external integration.
