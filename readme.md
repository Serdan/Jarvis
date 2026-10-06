# JARVIS

Jarvis connects ChatGPT and other MCP clients to projects on a user's own computer without uploading the workspace to a hosted development environment.

A local Jarvis client exposes a user-selected workspace. The public Jarvis server authenticates both the MCP caller and the local client with OAuth, matches them by authenticated user identity, and forwards structured project commands over SignalR. Mutating and process-execution operations remain subject to the local client's permission policy.

## Architecture

```text
ChatGPT / MCP client
        |
        | OAuth bearer token
        v
https://jarvis2.kehlet.dev/mcp
        |
        | authenticated user identity
        v
Jarvis server
        |
        | authenticated SignalR connection
        v
JarvisClient on the user's machine
        |
        v
User-selected workspace
```

There is no copied session key. ChatGPT never receives a Jarvis routing secret.

## Features

- Project discovery and metadata.
- Directory listing and bounded file-name/content search.
- Single and batch file reads, including ranged reads.
- Optimistic-concurrency writes and atomic unified-diff patches.
- Named project tasks from `.jarvis.json`.
- Project-local skill discovery, reading, and creation with `ListSkills`, `GetSkill`, and `CreateSkill`.
- Bounded process execution and asynchronous jobs, with optional human-readable `reason` metadata on `RunCommand` and `StartJob`.
- Persisted client activity and fresh-session recovery through `GetClientActivity`.
- Lightweight progress and turn-completion notes in client activity through `Message`.
- Git status/diff and local commits.
- Project-root and symlink confinement.
- Local confirmation and session-grant policy for state-changing operations.
- Sensitive environment-variable filtering for child processes.
- Structured MCP results and typed errors.

## Authentication

Jarvis uses Auth0 as its OAuth authorization server.

Production resource:

```text
https://jarvis2.kehlet.dev
```

MCP endpoint:

```text
https://jarvis2.kehlet.dev/mcp
```

Protected-resource metadata:

```text
https://jarvis2.kehlet.dev/.well-known/oauth-protected-resource
```

OAuth scopes:

| Scope | Purpose |
|---|---|
| `workspace:read` | Project discovery, search, file/skill reads, git/job status, client activity, messages |
| `workspace:write` | File writes, patches, skill creation |
| `process:execute` | Commands, project tasks, jobs, cancellation |
| `git:write` | Local git commits |
| `client:connect` | Authenticate JarvisClient's SignalR connection |

The MCP server verifies token issuer, audience, expiry, and required scope. The native client uses Authorization Code + PKCE and never uses a client secret.

## Run JarvisClient

Production downloads:

```text
https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
https://jarvis2.kehlet.dev/downloads/JarvisClient-osx-arm64
https://jarvis2.kehlet.dev/downloads/JarvisClient-win-x64.exe
https://jarvis2.kehlet.dev/downloads/SHA256SUMS
```

Linux example:

```bash
curl -fsSLO https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
chmod +x JarvisClient-linux-x64
./JarvisClient-linux-x64 --path ~/Projects
```

JarvisClient opens the browser for Auth0 sign-in, receives an authorization code on the loopback callback, exchanges it with PKCE, and authenticates the SignalR connection. The MCP connection and JarvisClient must be signed into the same Jarvis/Auth0 account.

Default native OAuth configuration:

```text
Auth0 domain: dev-kn4j3jz3qv2cvw05.eu.auth0.com
Audience: https://jarvis2.kehlet.dev
Callback: http://127.0.0.1:43821/callback
```

Non-secret OAuth metadata can be overridden with:

```text
JARVIS_AUTH0_DOMAIN
JARVIS_OAUTH_AUDIENCE
JARVIS_OAUTH_CLIENT_ID
JARVIS_OAUTH_REDIRECT_URI
```

### Local permissions

Jarvis exposes one user-facing trust setting:

```bash
./JarvisClient-linux-x64 --trust none
./JarvisClient-linux-x64 --trust partial
./JarvisClient-linux-x64 --trust full
```

`partial` is the default.

- `none` prompts for state-changing and process-execution commands.
- `partial` allows structured Jarvis operations such as file edits and git commits, but prompts before process execution.
- `full` disables interactive Jarvis permission prompts.

Trust affects the local confirmation layer only. Project-root confinement, path validation, OAuth scopes, output limits, command validation, and operating-system permissions still apply.

Spawned commands and jobs strip likely credential-bearing environment variables by default. Explicit exceptions are user-controlled:

```bash
./JarvisClient-linux-x64 --trust full --allow-env NUGET_AUTH_TOKEN
JARVIS_ALLOWED_ENVIRONMENT_VARIABLES=NUGET_AUTH_TOKEN,GITHUB_TOKEN ./JarvisClient-linux-x64 --trust full
```

Project-owned `.jarvis.json` files cannot grant themselves access to filtered environment variables.

## Project tasks

Projects can expose named build/test/format/lint tasks in a project-root `.jarvis.json`:

```json
{
  "tasks": {
    "build": {
      "description": "Build the solution",
      "executable": "dotnet",
      "args": ["build", "Jarvis.slnx", "--no-restore"],
      "timeoutSeconds": 120,
      "maxOutputBytes": 40000
    }
  }
}
```

The executable and arguments come from local project configuration rather than model-supplied task arguments. Jarvis resolves the task before permission approval and executes that same definition afterward.

## Project skills

Projects can store reusable procedural instructions at `.jarvis/skills/<skill-name>/SKILL.md`. `ListSkills(projectName)` discovers names and short descriptions; `GetSkill(projectName, skillName)` reads the full Markdown. `CreateSkill(projectName, skillName, content, overwrite=false)` writes the canonical path and rejects existing skills unless overwrite is explicit.

Skill reads require `workspace:read`; creation requires `workspace:write` and follows the local trust policy. Skills provide guidance and do not execute code or grant permissions. See [Jarvis Skills](docs/Skills.md) for the format and validation rules.

## Client activity and recovery

JarvisClient retains the latest 500 activities and restores them from local JSONL logs after a restart. Logs rotate daily and at 10 MiB, with 14 days of retention. Commands left running or awaiting permission are restored as `Interrupted`; activity recovery does not resume processes or restore job handles.

A fresh agent session can call `GetClientActivity(projectName?, limit=20)` to inspect recent activity, including timestamps, reasons/details, status, duration, and result or failure summaries. Requests are bounded to 1–100 entries, with optional case-insensitive exact project filtering. The client must be connected; use `GetConnectionDiagnostics` separately to inspect connection state. Activity inspection does not add an activity row, and detailed history is not persisted on the server. The client also supports `R` to copy recent commands for chat recovery.

Supply a short `reason` when using `RunCommand` or `StartJob` to explain the operation in activity/history. `ListJobs` also retains `StartJob` reasons. Reasons are presentation metadata and do not change authorization identity or grant permissions.

Use `Message(message, projectName?)` for short progress or turn-completion notes. It accepts non-empty text up to 1000 characters and creates one persisted `Info` entry visible through `GetClientActivity`, optionally associated with a project. It requires `workspace:read`, needs no local approval, and does not modify the workspace.

See [Client Activity Feedback Tasks](docs/ClientActivityTasks.md) for recovery and log behavior, [Agent Command Surface](docs/AgentCommandSurface.md) for command details, and [Privacy and Data Handling](docs/privacy.md) for stored metadata.

## Server configuration

Server settings are read from environment variables with the `Jarvis` prefix:

```text
JarvisAuth0Domain=dev-kn4j3jz3qv2cvw05.eu.auth0.com
JarvisAudience=https://jarvis2.kehlet.dev
JarvisOpenAIAppsChallenge=<set only while verifying the plugin domain>
```

`JarvisOpenAIAppsChallenge`, when present, is returned verbatim from:

```text
/.well-known/openai-apps-challenge
```

## Build

```bash
dotnet scripts/build.cs test
dotnet scripts/build.cs compile
```

Publish clients:

```bash
dotnet scripts/build.cs publish-clients \
  --server https://jarvis2.kehlet.dev/client \
  --rid linux-x64 \
  --rid win-x64 \
  --rid osx-arm64
```

Publish the server:

```bash
dotnet scripts/build.cs publish-server --rid linux-x64
```

Publish production client downloads:

```bash
dotnet scripts/publish-client-downloads.cs
```

## Plugin package

The source-controlled portable plugin package is under `plugin/`:

```text
plugin/
├── plugin.json
├── mcp.json
└── skills/
    └── project-work/
        └── SKILL.md
```

The public plugin itself is submitted against the production HTTPS MCP endpoint.

## Privacy

See `docs/privacy.md`.

## License

MIT. See `LICENSE`.
