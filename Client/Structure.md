# Client Module Structure

- **IO/** — Filesystem, project, and web IO abstractions.
- **Modules/OAuth.fs** — Auth0 native Authorization Code + PKCE login and token refresh.
- **Modules/DeviceIdentity.fs** — Stable non-secret installation identifier.
- **Modules/ProjectPaths.fs** — Workspace/project containment and path safety.
- **Modules/ProjectBrowser.fs** — Structured project/file operations.
- **Modules/PermissionPolicy.fs** — Local authorization and confirmation policy.
- **Modules/ProcessEnvironment.fs** — Sensitive environment-variable filtering for child processes.
- **Modules/ClientShell.fs** — Bounded processes, project tasks, and git operations.
- **Modules/JobManager.fs** — Long-running job lifecycle.
- **SignalR/** — Authenticated server connection and command dispatch.
- **Program.fs** — CLI parsing, OAuth sign-in, workspace initialization, SignalR lifecycle.
