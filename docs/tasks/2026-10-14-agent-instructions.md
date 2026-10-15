# Automatic Agent Instructions — implementation tasks

Source of truth: [AgentInstructions.md](../AgentInstructions.md). Implement in order, with a focused commit and relevant tests for each completed slice. No server deployment or interactive client restart without explicit approval.

## Task 1 — Static MCP server instructions

- [ ] Configure the MCP SDK to advertise the short static Jarvis contract in `InitializeResult.instructions`. Verify the correct SDK API rather than assuming an API name.
- [ ] Keep the instructions independent of project, authenticated user, device, and session.
- [ ] Add a protocol-level handshake test checking the field and content.
- **Accept:** MCP clients can discover the automatic instruction-delivery contract before any project tool call.

## Task 2 — Local hierarchical instruction resolution

- [ ] Implement one client-side resolver taking validated project identity and one or more target paths, returning ordered documents with scope, relative source path, SHA-256 digest, and exact UTF-8 Markdown.
- [ ] Include workspace-root `AGENTS.md`, project-root `AGENTS.md`, and applicable nested directories; support file and directory targets and multiple-target deduplication.
- [ ] Reuse `ProjectPaths` confinement and symlink checks; do not read files outside the client workspace or follow reparse points.
- [ ] Enforce 16 KiB/file, 32 KiB aggregate, 16-file limits. Distinguish absent from unreadable/malformed/unsafe; reject failures before execution.
- [ ] Add tests in `Client.Tests` for precedence, unrelated siblings, missing/empty files, updates, bounds, UTF-8, and symlink traversal.
- **Accept:** The resolver deterministically returns correct contents and fails closed for applicable unsafe/unreadable guidance.

## Task 3 — Response envelope and safe dispatch

- [ ] Add optional typed instruction context to `AgentCommandResponse`, preserving the existing command result and error fields.
- [ ] Resolve context inside the same authenticated, selected-device client command dispatch, before side effects, and attach it to success/failure responses.
- [ ] Define target mapping for all project-scoped commands as in the spec; use the actual resolved working directory for execution/project tasks. Exclude `ListProjects`, global/device/server-only operations, and `@global` skills.
- [ ] Do not fetch guidance as a second unrelated MCP command or use a server-side project cache.
- [ ] Require `workspace:read` for any operation returning instruction contents, alongside its current required scope; preserve all local permissions and confirmation.
- [ ] Add tests ensuring pre-execution instruction failure prevents writes/commands, and two concurrently selected devices cannot exchange guidance.
- **Accept:** The first applicable operation receives its guidance even without session metadata, with no changes to tool input signatures.

## Task 4 — MCP result formatting and interoperability

- [ ] Format the envelope as labeled instruction content blocks preceding the normal result. Include each document's source and hash; present content as repository guidance, not higher-priority instructions.
- [ ] Preserve existing structured/text responses, errors, images, and export links. Check aggregate response bounds and the existing operation ID metadata.
- [ ] Add server tests for successful and failed calls, an image result, file export, multi-file output, and a command with no instructions.
- [ ] Add an end-to-end test through the client/SignalR/MCP boundary if supported by existing test harnesses.
- **Accept:** A standard MCP consumer can recover ordinary results unchanged and observe exactly the applicable guidance.

## Task 5 — Documentation and compatibility verification

- [ ] Update `readme.md`, `docs/AgentCommandSurface.md`, plugin project-work skill, and relevant structures to reflect automatic guidance.
- [ ] Confirm `GetProjectDetails` remains a project metadata API, not a prerequisite for `AGENTS.md`.
- [ ] Run focused client/server tests and the full Jarvis test workflow, inspect diffs, and commit the implementation slices.
- [ ] Manually exercise a Wayfold operation: its root `AGENTS.md` must appear before the tool result, while linked policy documents and skills remain on demand.
- [ ] Verify there are no regressions in cross-device isolation, routing, authorization, or oversized response handling.
- **Accept:** Behavior matches the spec on production-style clients without relying on the agent voluntarily reading `AGENTS.md`.

## Deferred — Session-aware instruction deduplication

Only pursue if repeated documents prove materially expensive.

- [ ] Measure repeated context volume and identify the MCP consumer behavior before designing a cache.
- [ ] If justified, key tracking by authenticated user, stable conversation/session, selected device, project, and source digests, with safe invalidation and eviction.
- [ ] Preserve full injection for sessions without stable identity; never suppress instructions solely because a server process remembers a previous call.
- **Accept:** Any optimization retains the initial implementation's correctness and multi-client isolation.

## Implementation checkpoint — 2026-10-15

Implemented locally, without deployment:

- MCP static server initialization guidance via `ServerInstructions`.
- Client-side workspace/project/nested resolver, byte and document limits, UTF-8 and symlink checks, SHA-256, exact-case filenames, and multiple-target deduplication.
- Typed `AgentCommandResponse.Instructions` array, command pre-execution resolution, content-aware result size accounting, and per-call delivery without session state.
- MCP labeled content blocks before ordinary success/error/image/export content; workspace-read scope enforcement.
- JSON Schema at `docs/AgentCommandResponse.schema.json` and user/plugin/command surface documentation.
- Response protocol version advanced from 3.12 to 3.13.

Automated verification: 182 client tests, 60 server tests passed; `git diff --check` passed. New tests cover hierarchy ordering, sibling exclusion, missing and edited sources, malformed UTF-8, size limits, symlinks, pre-execution write prevention, first-call delivery, response JSON, and MCP content/error preservation.

Remaining release acceptance: directly inspect an MCP handshake against the installed server and smoke-test an actual Wayfold command after updating/deploying both components. Test the two-device live routing isolation after deployment. Avoid claiming live host behavior until these checks have been performed. Do not deploy or restart the interactive client without explicit approval.
