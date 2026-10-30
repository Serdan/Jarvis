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
- Short-lived HTTPS export links for bounded project files, so generated images and other files can be opened by the remote user without exposing the workspace filesystem.
- Binary file import from ChatGPT file parameters with streamed local download, hashing, and atomic placement.
- Verified self-update for published Linux/macOS clients: the client reads `SHA256SUMS` from its configured Jarvis server, atomically swaps the standalone executable with a `.prev` rollback copy, and restarts attached to the same terminal.
- Optimistic-concurrency writes and atomic unified-diff patches.
- Named project tasks from `.jarvis.json`.
- Persisted unfinished work tasks in `.jarvis/tasks` that reference committed task descriptions and carry resumable execution checkpoints.
- Project-local and workspace-global skill discovery, reading, and creation with `ListSkills`, `GetSkill`, and `CreateSkill`.
- Bounded process execution and asynchronous jobs, with optional human-readable `reason` metadata on `RunCommand` and `StartJob`.
- Persisted client activity and fresh-session recovery through `GetClientActivity`, with observer diagnostics excluded by default and retrievable explicitly.
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

### User-level client configuration

JarvisClient stores its editable configuration in the current OS user's configuration directory, **not next to the executable**: `$XDG_CONFIG_HOME/Jarvis/config.json` on Linux (default `~/.config/Jarvis/config.json`), `%APPDATA%\\Jarvis\\config.json` on Windows, and `~/Library/Application Support/Jarvis/config.json` on macOS. The existing stable device ID remains separate and is not changed by renaming a client.

Configuration is a versioned JSON object with optional `machineName`, `workspacePath`, `trustLevel`, `allowedEnvironmentVariables` and `maxImportBytes` fields. Missing fields retain previous defaults. To inspect or edit configuration locally without authenticating:

```sh
JarvisClient config path
JarvisClient config show
JarvisClient config set machine-name "Home Desktop"
JarvisClient config set workspace-path "/home/user/Projects"
JarvisClient config set trust-level partial
JarvisClient config set max-import-bytes 33554432
JarvisClient config set allowed-environment-variables "NUGET_AUTH_TOKEN,GITHUB_TOKEN"
JarvisClient config unset machine-name
```

Startup flags (`--machine-name`, `--path`, `--trust`, `--max-import-bytes`) override persisted scalar settings for that run. The sensitive-environment-variable allowlist combines saved names, `JARVIS_ALLOWED_ENVIRONMENT_VARIABLES`, and `--allow-env` flags. Invalid config is reported instead of silently ignoring it; settings are saved through atomic replacement with a revision check. OAuth tokens are not stored in this file.

Press **g** on the client's Activity or Jobs screen to open **Settings**. Select with arrow keys, edit with Enter, unset with **u**, save with **s**, and return without saving with Escape. Turning on full trust requires an explicit confirmation. The screen shows active runtime values and flags startup overrides. Workspace, trust and import-limit changes take effect after restarting the client; a changed display name is picked up when the client next registers/reconnects. The device UUID is stable.

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

Imported files default to a 32 MiB local maximum. Override it when launching the client:

```bash
./JarvisClient-linux-x64 --max-import-bytes 134217728
./JarvisClient-linux-x64 --max-import-bytes 0
```

`0` means unlimited. A tool call may request a lower per-import limit, but cannot raise the client-configured maximum.

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

## Worktrees and Git-backed orchestration

Jarvis discovers worktrees using Git metadata. New worktrees use hidden sibling directories such as `~/Projects/.Wayfold-worktrees/input-config/`, and `ListProjects` groups worktrees under their main projects. A registered worktree can be selected for project-scoped commands using `projectName="Wayfold::input-config"`.

**Current task authority:** an explicitly initialized project uses one Git orphan branch, `refs/heads/jarvis-orchestration`, on its configured remote. The unprefixed orchestration MCP tools manage canonical HTML task specs, agents, assignments, reviews and integrations. The former source-branch task handlers are retired. Names such as `CreateTask` and `AssignTask` now refer exclusively to orphan-ledger operations; old commands such as `ListTasks` and `CheckpointTask` are not available. Existing `docs/tasks/` HTML/JSON files remain reference material, not live state; upgrading Jarvis does not automatically migrate them.

See [Migration to Git-backed orchestration](docs/OrchestrationMigration.md) for initialization, selective task carry-over and recovery. The [orchestration V2 architecture](docs/specs/orchestration-v2-architecture.md) defines the authority rules. The older [Git-backed task files guide](docs/GitBackedTasks.md) describes the retired workflow.

## Automatic repository instructions

Jarvis automatically loads applicable AGENTS.md files on project-scoped commands. Guidance is discovered from the local workspace root, the project root, and ancestor folders of the targeted file or working directory. The client includes the ordered source paths, hashes, and original Markdown in an Instructions array on its command response. The MCP server presents these as labeled content blocks before the ordinary result; it also advertises the contract through static MCP initialization instructions.

Instruction sources remain project data and never grant permissions. Missing files are normal; unsafe, unreadable, or oversized applicable files reject a command before side effects. Instructions are currently supplied on every applicable call rather than being session-cached.

See [Automatic Agent Instructions](docs/AgentInstructions.md) and the [response JSON Schema](docs/AgentCommandResponse.schema.json) for hierarchy, limits, and the wire envelope.

## Project and global skills

Projects store instructions at `<project>/.jarvis/skills/<skill-name>/SKILL.md`. The shared workspace skill scope uses `<workspace>/.jarvis/skills/<skill-name>/SKILL.md`.

`ListSkills(projectName)` returns local and global skills with an explicit scope. Use `projectName="@global"` for `GetSkill` or `CreateSkill` when targeting the global scope. Existing skills require `overwrite=true`.

Skill reads require `workspace:read`; creation requires `workspace:write` and follows the local trust policy. Skills provide guidance and do not execute code or grant permissions. See [Jarvis Skills](docs/Skills.md) for the format and validation rules.

## Local Jobs screen

Press `j` on the Jarvis client Activity screen to open the local Jobs list. Running jobs appear by default; `t` toggles recently completed jobs, `f` filters by project name, and `Enter` opens job details with bounded stdout/stderr output. Output follows new events until you scroll; `b` resumes following. Press `c` to request cancellation of the selected running job and confirm with `y`; `p` opens pending permissions and `Esc` returns. This view uses the local client JobManager, so it remains available while disconnected from the server. Jobs from other machines are not listed, and persisted activity does not imply a job survives a client restart.

## Client activity and recovery

JarvisClient retains the latest 500 activities and restores them from local JSONL logs after a restart. Logs rotate daily and at 10 MiB, with 14 days of retention. Commands left running or awaiting permission are restored as `Interrupted`; activity recovery does not resume processes or restore job handles.

A fresh agent session can call `GetClientActivity(projectName?, limit=20, includeObservers=false)` to inspect recent activity, including timestamps, reasons/details, status, duration, and result or failure summaries. Requests are bounded to 1–100 entries, with optional case-insensitive exact project filtering. The client must be connected; use `GetConnectionDiagnostics` separately to inspect connection state. Activity inspection does not add an activity row, and detailed history is not persisted on the server. The client also supports `R` to copy recent commands for chat recovery.

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
