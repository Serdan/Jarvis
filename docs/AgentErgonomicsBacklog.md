# Agent Ergonomics Backlog

This backlog records improvements identified while using Jarvis itself to review, edit, build, test, and commit the Jarvis codebase.

The main goal is to reduce unnecessary arbitrary process execution while making the agent-facing API more structured and diagnosable.

## 1. Structured MCP results

Stop returning command-specific JSON as an opaque string from MCP tools.

The client currently serializes command results, SignalR transports the serialized payload, and the MCP layer returns that payload as text. The MCP server should parse successful command payloads and return structured JSON values so arrays, objects, numbers, booleans, and null remain typed.

Keep the legacy HTTP surface compatible unless there is a strong reason to change it.

## 2. Ranged file reads

Extend file reads with optional:

- start line
- end line
- line numbers

This should replace common `sed`, `nl`, and similar RunCommand calls during code review.

Prefer bounded reads that avoid loading or returning an entire large file when only a range is requested.

## 3. Rich text-search matches

Change SearchText from returning filenames only to returning bounded match records such as:

- project-relative path
- line
- column
- short preview/snippet

Support multiple matches per file and retain include/exclude globs and global MaxResults semantics.

MaxResults should bound traversal and file reads, not only final result materialization.

## 4. Patch ergonomics

Keep PatchFile atomic and context-verified, but make routine sequential editing less brittle.

Potential improvements:

- expose a small default fuzzy context window through MCP
- treat hunk line numbers primarily as hints when sufficient context uniquely identifies the target
- preserve strict failure on ambiguous matches

Do not weaken expected-hash support or atomic application.

## 5. Directory creation

Avoid requiring arbitrary RunCommand merely to create directories.

Add either:

- a CreateDirectory command, or
- a createParents option on WriteFile

Directory creation must remain project-root confined and symlink-safe.

## 6. Granular process permissions

Reduce repeated approval friction without attempting unsafe command-line heuristics.

Prefer explicit grants or configured capabilities such as:

- allow this executable for this project/session
- allow this configured project task
- distinguish generic arbitrary process execution from narrowly configured execution

Do not infer safety solely from executable/argument text.

## 7. First-class project tasks

Allow projects to advertise configured tasks such as:

- build
- test
- format
- lint

Each task should declare its executable, arguments, working directory, and permission requirements. Agents should invoke these by name rather than reconstructing arbitrary RunCommand calls.

## 8. Incremental job output

Fix the asymmetric job polling model where stdout has an offset but stderr is resent from the beginning.

Prefer either:

- separate stdout/stderr offsets, or
- an ordered event stream with sequence numbers and stream identity

The latter preserves stdout/stderr interleaving and scales better to repeated polling.

## 9. Spawned-process environment policy

RunCommand and StartJob should not blindly inherit every server/client environment variable.

Introduce an explicit environment policy that strips likely secrets by default, with deliberate project/user exceptions where needed.

Document the behavior and test that sensitive variables do not leak unintentionally.

## 10. Single command specification

Prevent drift between:

- AgentCommand types
- capabilities metadata
- MCP tools
- actions-schema
- legacy HTTP routes/schema
- documentation

Move toward one authoritative command specification from which external metadata/schemas can be generated or validated.

Concrete F# command/result types may remain the implementation types.

## 11. Typed MCP errors

The SignalR bridge now carries AgentError structurally, but MCP currently converts failures into exception text.

Preserve stable error kinds and structured details through the MCP layer where the SDK permits it, including ConfirmationRequest data where relevant.

## 12. Explicit session state

Make session state observable rather than reducing it to a key-to-connection mapping.

Useful state includes:

- disconnected
- transport connected
- registered
- connection generation
- client/protocol version
- last seen / last registration
- reason for registration or dispatch failure

This should make “unknown key”, stale connection, reconnect, and dispatch failures distinguishable.
