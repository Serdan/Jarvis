# Jarvis Agent Command Surface

This document describes the approved next-generation Jarvis command surface.

Jarvis should not try to be a complete coding-agent runtime. Its purpose is to expose a small, safe, local bridge from the assistant to a user-selected workspace. The command surface should focus on project discovery, file access, patching, controlled execution, and git review/commit operations.

## Design Goals

- Keep commands structured rather than exposing arbitrary filesystem or shell access by default.
- Make read-only commands clearly distinguishable from commands that mutate state.
- Prefer auditable patch-based edits over brittle text replacement commands.
- Keep all path resolution confined to the selected project root.
- Let clients and servers negotiate available capabilities at runtime.
- Avoid long-running HTTP requests by supporting asynchronous job handles later.

## Protocol Metadata

Every Jarvis implementation should support `ListCommands`. It is the compatibility anchor for clients, servers, and agents that may evolve independently.

```fsharp
type ListCommandsCommand = struct end

type PermissionLevel =
    | ReadOnly
    | WorkspaceWrite
    | ProcessExecution
    | VersionControlWrite
    | NetworkAccess
    | Destructive

type CommandCapability =
    { Name: string
      Description: string
      Permissions: PermissionLevel list
      MutatesState: bool
      RequiresConfirmation: bool
      SupportsDryRun: bool
      MaxInputBytes: int option
      MaxOutputBytes: int option
      InputSchemaJson: string option
      OutputSchemaJson: string option }

type ListCommandsResult =
    { ProtocolVersion: string
      Commands: CommandCapability list }
```

`ListCommands` should be available before project selection. It describes the bridge itself, not a particular project.

## Core Command Set

```fsharp
type AgentCommand =
    | ListCommandsCommand
    | ListProjectsCommand
    | GetProjectDetailsCommand of GetProjectDetailsCommand
    | ListDirectoryCommand of ListDirectoryCommand
    | SearchFilesCommand of SearchFilesCommand
    | SearchTextCommand of SearchTextCommand
    | ReadFileCommand of ReadFileCommand
    | ReadFilesCommand of ReadFilesCommand
    | WriteFileCommand of WriteFileCommand
    | PatchFileCommand of PatchFileCommand
    | RunCommandCommand of RunCommandCommand
    | GetGitStatusCommand of GitStatusCommand
    | GetGitDiffCommand of GitDiffCommand
    | GitCommitCommand of GitCommitCommand
```

## Project Discovery Commands

### `ListProjects`

Lists available projects under the configured workspace root.

Read-only.

```fsharp
type ListProjectsCommand = struct end
```

### `GetProjectDetails`

Returns high-level project details and selected special files such as README, TODO, notes, or project metadata.

This replaces the older `OpenProject` name.

Read-only.

```fsharp
type GetProjectDetailsCommand =
    { ProjectName: string }
```

### `ListDirectory`

Lists files and folders in a project-relative directory.

Read-only.

```fsharp
type ListDirectoryCommand =
    { ProjectName: string
      FolderPath: string }
```

### `SearchFiles`

Finds project-relative files by name or glob-like pattern.

Read-only.

```fsharp
type SearchFilesCommand =
    { ProjectName: string
      Query: string
      FolderPath: string option
      MaxResults: int option }
```

### `SearchText`

Searches file contents within a project.

Read-only.

```fsharp
type SearchTextCommand =
    { ProjectName: string
      Query: string
      FolderPath: string option
      IncludeGlobs: string list
      ExcludeGlobs: string list
      MaxResults: int option }
```

## File Access Commands

### `ReadFile`

Reads one project-relative file.

Read-only.

```fsharp
type ReadFileCommand =
    { ProjectName: string
      FilePath: string }
```

### `ReadFiles`

Reads multiple project-relative files in one round trip.

Read-only.

```fsharp
type ReadFilesCommand =
    { ProjectName: string
      FilePaths: string list }
```

### `WriteFile`

Writes or appends full content to a project-relative file.

Mutates state.

```fsharp
type FileWriteMode =
    | Append
    | Write

type WriteFileCommand =
    { ProjectName: string
      FilePath: string
      Content: string
      FileWriteMode: FileWriteMode
      ExpectedHash: string option }
```

`WriteFile` may create a new file when `FileWriteMode = Write`. It must still reject paths outside the resolved project root.

`ExpectedHash` provides optimistic concurrency for overwrites and appends. If supplied, the implementation must reject the write when the current file hash does not match. For new files, `ExpectedHash = None` means the file is expected not to exist.

### `PatchFile`

Applies an auditable patch to a project-relative file.

Mutates state.

```fsharp
type PatchFormat =
    | UnifiedDiff

type PatchFileCommand =
    { ProjectName: string
      FilePath: string
      ExpectedHash: string option
      Format: PatchFormat
      Patch: string }
```

`PatchFile` should become the preferred edit command. It replaces these older commands:

- `ReplaceSection`
- `Replace`
- `InsertBefore`
- `InsertAfter`

The old commands are easy to misuse because they depend on brittle text matching and unclear occurrence semantics. Patch-based edits are easier to review and can detect conflicts with `ExpectedHash`.

`Patch` is a unified diff that applies to exactly `FilePath`. Implementations must reject patches that modify any other path. A future `PatchProject` command can be added if project-wide patches become necessary.

## Controlled Execution Commands

### `RunCommand`

Runs a bounded, synchronous command in a project directory.

May mutate state depending on the command. It should usually require confirmation.

```fsharp
type RunCommandCommand =
    { ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      TimeoutSeconds: int option
      MaxOutputBytes: int option }
```

Execution policy:

- Resolve `WorkingDirectory` under the project root.
- Reject working directories outside the project root.
- Prefer an allowlist for commands.
- Capture stdout, stderr, exit code, and timeout state.
- Truncate output according to `MaxOutputBytes` or implementation defaults.
- Do not inherit sensitive environment variables unless explicitly allowed.

Suggested result shape:

```fsharp
type RunCommandResult =
    { ExitCode: int
      TimedOut: bool
      StdOut: string
      StdErr: string
      Truncated: bool }
```

Specialized helpers such as `RunTests`, `BuildProject`, and `FormatProject` can be implemented either as separate commands or as validated `RunCommand` presets.

## Git Commands

Git commands provide an audit boundary for agent changes.

### `GetGitStatus`

Returns the repository status for a project.

Read-only.

```fsharp
type GitStatusCommand =
    { ProjectName: string }
```

Suggested behavior:

- Run inside the project root.
- Return a porcelain-style summary.
- Include current branch if available.

### `GetGitDiff`

Returns the current diff for a project or path.

Read-only.

```fsharp
type GitDiffCommand =
    { ProjectName: string
      Path: string option
      MaxOutputBytes: int option }
```

Suggested behavior:

- Resolve `Path` under the project root when provided.
- Return truncated output when the diff is too large.
- Include whether truncation occurred.

### `GitCommit`

Creates a local git commit.

Mutates state and should require confirmation.

```fsharp
type GitCommitCommand =
    { ProjectName: string
      Message: string
      Body: string option
      Paths: string list
      AllowEmpty: bool }
```

Suggested result shape:

```fsharp
type GitCommitResult =
    { CommitHash: string
      Summary: string
      StdOut: string
      StdErr: string }
```

Policy:

- Require `Paths` to be non-empty unless `AllowEmpty = true`.
- Stage only the provided paths.
- Reject paths outside the project root.
- Reject empty commits unless `AllowEmpty = true`.
- Return the commit hash and command output.
- Do not set git author identity implicitly. Use the repository or user's configured git identity, and return a clear error if git identity is missing.
- Do not add `git push` initially.

Avoid exposing arbitrary git subcommands at first. `push`, `pull`, `rebase`, `reset`, and `checkout` need a stronger permission model.

## Later: Asynchronous Jobs

Long-running processes should not be tied to a single HTTP request. Add asynchronous job handles after the synchronous execution model is stable.

Use `Job` terminology here because `Command` already means a protocol message in Jarvis.

Preferred names:

```fsharp
type AgentCommand =
    | StartJobCommand of StartJobCommand
    | ListJobsCommand of ListJobsCommand
    | GetJobResultCommand of GetJobResultCommand
    | CancelJobCommand of CancelJobCommand
```

Shared job status:

```fsharp
type JobStatus =
    | Running
    | Completed of exitCode: int
    | FailedToStart of message: string
    | Canceled
```

### `StartJob`

Starts a long-running process and returns a job ID.

```fsharp
type StartJobCommand =
    { ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      MaxOutputBytes: int option }

type StartJobResult =
    { JobId: string
      StartedAt: System.DateTimeOffset }
```

Example use cases:

- `dotnet test --watch`
- `dotnet run`
- `npm run dev`
- log tailing
- language server startup

### `ListJobs`

Lists known jobs and their current status.

Read-only.

```fsharp
type ListJobsCommand =
    { ProjectName: string option
      IncludeCompleted: bool }

type JobSummary =
    { JobId: string
      ProjectName: string
      Executable: string
      Args: string list
      WorkingDirectory: string option
      Status: JobStatus
      StartedAt: System.DateTimeOffset
      CompletedAt: System.DateTimeOffset option }

type ListJobsResult =
    { Jobs: JobSummary list }
```

`ProjectName = None` lists jobs across all projects visible to the current client/session. `IncludeCompleted = false` should return only active jobs.

### `GetJobResult`

Retrieves output and status for a previously started job.

```fsharp
type GetJobResultCommand =
    { JobId: string
      FromOffset: int option }

type JobResult =
    { JobId: string
      Status: JobStatus
      StdOut: string
      StdErr: string
      OutputOffset: int
      Truncated: bool }
```

`FromOffset` avoids resending the same output repeatedly.

### `CancelJob`

Stops a running job.

```fsharp
type CancelJobCommand =
    { JobId: string }
```

Cancellation should first attempt graceful termination and then force-kill after an implementation-defined timeout.

## Permission Model

Jarvis permissions should be explicit, inspectable through `ListCommands`, and enforced by both the server and the local client. The model should describe what a command is allowed to do before the command is executed.

Permissions are not a substitute for path validation or command validation. They are a policy layer on top of the normal safety checks.

### Permission Levels

```fsharp
type PermissionLevel =
    | ReadOnly
    | WorkspaceWrite
    | ProcessExecution
    | VersionControlWrite
    | NetworkAccess
    | Destructive
```

Meaning:

| Level | Meaning | Examples |
|---|---|---|
| `ReadOnly` | Reads local metadata or content without modifying state. | `ListProjects`, `ReadFile`, `GetGitDiff` |
| `WorkspaceWrite` | Modifies files inside an allowed project root. | `WriteFile`, `PatchFile` |
| `ProcessExecution` | Starts local processes. May mutate state indirectly. | `RunCommand`, `StartJob` |
| `VersionControlWrite` | Mutates git state without changing working tree files directly. | `GitCommit` |
| `NetworkAccess` | Performs outbound network IO. | `LoadPage`, future package/security tools |
| `Destructive` | Deletes data, discards work, rewrites history, or performs broad irreversible operations. | future `DeleteFile`, `GitReset`, `GitClean` |

Commands may require more than one level. For example, `RunCommand` is always `ProcessExecution`, and may also require `NetworkAccess` if the executable/preset is known to download dependencies. If process policy cannot determine whether a command performs outbound network IO, the command should be treated as requiring `NetworkAccess`.

### Permission Decisions

```fsharp
type PermissionDecision =
    | Allow
    | RequireConfirmation
    | Deny
```

Implementations should resolve a command to one of these decisions before execution.

Default policy:

| Permission level | Default decision |
|---|---|
| `ReadOnly` | `Allow` |
| `WorkspaceWrite` | `RequireConfirmation` |
| `ProcessExecution` | `RequireConfirmation` |
| `VersionControlWrite` | `RequireConfirmation` |
| `NetworkAccess` | `RequireConfirmation` |
| `Destructive` | `Deny` |

A user or host application may override these defaults, but `Destructive` operations should remain denied until a dedicated permission UX exists.

### Capability Metadata

`CommandCapability` should expose the permission profile of each command.

```fsharp
type CommandCapability =
    { Name: string
      Description: string
      Permissions: PermissionLevel list
      MutatesState: bool
      RequiresConfirmation: bool
      SupportsDryRun: bool
      MaxInputBytes: int option
      MaxOutputBytes: int option
      InputSchemaJson: string option
      OutputSchemaJson: string option }
```

`RequiresConfirmation` is derived from the current policy. It may change per installation, user, session, or project. `Permissions` describes the command itself and should be stable.

If `InputSchemaJson` and `OutputSchemaJson` are added later, they should be included alongside `Permissions`, not replace it.

### Command Permission Defaults

| Command | Permissions | Default decision | Notes |
|---|---|---|---|
| `ListCommands` | `ReadOnly` | `Allow` | Capability discovery. |
| `ListProjects` | `ReadOnly` | `Allow` | Workspace discovery. |
| `GetProjectDetails` | `ReadOnly` | `Allow` | May read special files. |
| `ListDirectory` | `ReadOnly` | `Allow` | Must remain project-root confined. |
| `SearchFiles` | `ReadOnly` | `Allow` | Should respect ignored directories and max result limits. |
| `SearchText` | `ReadOnly` | `Allow` | Should respect ignored directories and max output limits. |
| `ReadFile` | `ReadOnly` | `Allow` | May still be blocked by sensitive-file filters. |
| `ReadFiles` | `ReadOnly` | `Allow` | May still be blocked by sensitive-file filters. |
| `WriteFile` | `WorkspaceWrite` | `RequireConfirmation` | Creates or overwrites project files. |
| `PatchFile` | `WorkspaceWrite` | `RequireConfirmation` | Preferred edit path. |
| `RunCommand` | `ProcessExecution` | `RequireConfirmation` | Use allowlists/presets where possible. |
| `GetGitStatus` | `ReadOnly` | `Allow` | Does not change git state. |
| `GetGitDiff` | `ReadOnly` | `Allow` | Output may be truncated. |
| `GitCommit` | `VersionControlWrite` | `RequireConfirmation` | Must stage only requested paths. |
| `StartJob` | `ProcessExecution` | `RequireConfirmation` | Long-running process. |
| `ListJobs` | `ReadOnly` | `Allow` | Lists job metadata for visible projects/session. |
| `GetJobResult` | `ReadOnly` | `Allow` | Reads buffered process output. |
| `CancelJob` | `ProcessExecution` | `Allow` for same-session jobs, otherwise `RequireConfirmation` | Stops a process. |

### Confirmation Requests

When a command requires confirmation, the host should present a clear summary before execution.

```fsharp
type ConfirmationRequest =
    { CommandName: string
      ProjectName: string option
      Permissions: PermissionLevel list
      Summary: string
      Paths: string list
      Executable: string option
      Args: string list
      EstimatedImpact: string
      SupportsDryRun: bool }
```

Examples of good summaries:

- `Patch 2 files in Client.Tests`
- `Run dotnet test Client.Tests/Client.Tests.fsproj`
- `Commit 9 files with message "Update Jarvis F# rewrite to .NET 10"`

Confirmation should be specific to the exact command payload. If the payload changes, the previous confirmation is invalid.

### Scopes

Permissions should be scoped so users can grant narrow access.

```fsharp
type PermissionScope =
    { ProjectNames: string list option
      PathPrefixes: string list option
      Executables: string list option
      ExpiresAt: System.DateTimeOffset option }
```

Suggested behavior:

- `ProjectNames = None` means all configured projects visible to the client.
- `PathPrefixes = None` means all paths inside the allowed project roots.
- `Executables = None` should not mean arbitrary execution unless the permission level explicitly allows it.
- Expiring grants are preferred for interactive approvals.

Permission grants should be scoped to a user, client, or session.

```fsharp
type PermissionGrant =
    { GrantId: string
      SubjectId: string
      Levels: PermissionLevel list
      Scope: PermissionScope
      CreatedAt: System.DateTimeOffset
      ExpiresAt: System.DateTimeOffset option }
```

`SubjectId` identifies the user/session/client that owns the grant. Grants should not silently transfer between users or client connections.

### Execution Policy

Process execution needs extra policy beyond permission level.

```fsharp
type ExecutablePolicy =
    { Executable: string
      AllowedArgsPrefixes: string list list
      AllowsNetwork: bool
      AllowsWorkspaceWrite: bool
      DefaultTimeoutSeconds: int
      MaxTimeoutSeconds: int }
```

Examples:

- `dotnet test` can be allowed as a safe preset.
- `dotnet build` can be allowed as a safe preset.
- `dotnet run`, `npm run dev`, or arbitrary shell commands should require stronger confirmation.
- Shell executables such as `bash`, `sh`, `cmd`, and `powershell` should be denied by default unless a dedicated shell permission exists.

`RunCommand` and `StartJob` should prefer `Executable` + `Args` over a raw command string. If raw command strings are supported for compatibility, they should be treated as higher risk.

### Sensitive Files

Read-only does not mean unrestricted. Implementations may block or require confirmation for sensitive files even for `ReadFile`, `ReadFiles`, and `SearchText`.

Suggested sensitive patterns:

```text
.env
.env.*
*.pem
*.key
*.pfx
id_rsa
id_ed25519
**/secrets/**
**/.ssh/**
**/.aws/**
**/.azure/**
**/.kube/**
```

Policy options:

- deny reading sensitive files by default;
- allow reading only with explicit confirmation;
- redact likely secrets from returned content;
- allow project-specific overrides.

### Dry Runs

Mutating commands should support dry runs where practical.

| Command | Dry-run behavior |
|---|---|
| `WriteFile` | Return whether the file would be created or overwritten. |
| `PatchFile` | Validate patch and return resulting diff without writing. |
| `GitCommit` | Return staged-path plan and whether there are changes to commit. |
| `RunCommand` | Validate executable, args, working directory, and policy without running. |
| `StartJob` | Same as `RunCommand`, but do not start the process. |

Dry runs should never mutate filesystem, process, or git state.

### Audit Log

Jarvis should keep a local audit log for mutating and execution commands.

Suggested event shape:

```fsharp
type AuditEvent =
    { Timestamp: System.DateTimeOffset
      CommandName: string
      ProjectName: string option
      Permissions: PermissionLevel list
      Decision: PermissionDecision
      Paths: string list
      Executable: string option
      Args: string list
      ResultSummary: string }
```

The log should not store full file contents, secret values, or complete command output by default.

## Client Responsibilities

Jarvis has both a protocol/server surface and a local client implementation. Keep their concerns separate.

Protocol and server responsibilities:

- define command shapes;
- enforce project-root confinement;
- enforce permissions and confirmation decisions;
- return structured errors and results;
- avoid assuming a specific user interface.

Client responsibilities:

- present confirmations clearly;
- manage local permission grants;
- apply local ignore rules for discovery/search where appropriate;
- provide user-facing preferences for hidden folders, generated directories, and sensitive files;
- own UI decisions around previews, diffs, and audit-log display.

Search commands should support enough include/exclude input to let clients express policy, but client-specific defaults belong in the client implementation.

## Additional Protocol Rules

### Protocol Version

The initial version of this command surface is `2.0`.

This is a breaking redesign of the original Jarvis command set. Implementations should not preserve old command names solely for backwards compatibility.

### Error Model

Commands should return typed errors rather than throwing protocol-level exceptions where possible.

```fsharp
type AgentError =
    | NotFound of string
    | PermissionDenied of string
    | ConfirmationRequired of ConfirmationRequest
    | ValidationFailed of string
    | Conflict of string
    | ExecutionFailed of string
    | OutputTruncated of string
```

`ConfirmationRequired` is the preferred confirmation transport. When a command requires confirmation, return this error with the exact `ConfirmationRequest`. The caller may resubmit the same payload after the user grants permission. If the payload changes, the old confirmation is invalid.

### Hash Format

File hashes used by `ExpectedHash` should use this format:

```text
sha256:<lowercase-hex>
```

Implementations may support additional hash algorithms later, but `sha256` is the baseline.

### Patch Atomicity

`PatchFile` must be atomic. Either the entire patch applies successfully, or the target file remains unchanged.

### Line Endings and `.gitattributes`

Jarvis should preserve existing file line endings where practical. New files should use LF by default.

If a repository contains `.gitattributes`, file write and patch operations should respect applicable text/eol attributes when practical. This is especially important for generated patches, cross-platform repositories, and files with explicit `eol=crlf` or `eol=lf` rules.

If `.gitattributes` cannot be evaluated, fall back to preserving the existing file's line endings. For new files without an applicable `.gitattributes` rule, use LF.

## Security Rules

All command implementations must follow these rules:

1. Resolve project names through the configured project registry.
2. Canonicalize all paths before use.
3. Reject paths outside the resolved project root.
4. Distinguish read-only commands from mutating commands.
5. Require explicit confirmation for mutating or command-execution operations when used interactively.
6. Limit command output size.
7. Avoid arbitrary shell execution by default.
8. Do not expose `git push` or destructive git operations initially.
9. Prefer structured arguments over raw command strings.
10. Return clear error values rather than throwing protocol-level exceptions when possible.

## Implementation Plan

1. Replace the shared protocol with the `2.0` command surface.
2. Implement the typed error model and `ConfirmationRequired` flow.
3. Implement `ListCommands`.
4. Replace `OpenProject` with `GetProjectDetails`.
5. Add `SearchFiles` and `SearchText`.
6. Add hash support using `sha256:<lowercase-hex>`.
7. Add `WriteFile` optimistic concurrency.
8. Add atomic `PatchFile` with unified-diff support.
9. Add read-only git commands: `GetGitStatus`, `GetGitDiff`.
10. Add controlled `GitCommit`.
11. Add bounded `RunCommand`.
12. Add asynchronous `StartJob`, `ListJobs`, `GetJobResult`, and `CancelJob` later.
