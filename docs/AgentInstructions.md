# Agent context and explicit repository instructions

## Context boundaries

The 2026-11-23 Operator decision replaces implicit repository instruction
delivery with a canonical project launch template for the stable workflow
contract, explicit self-agent inbox reads for dynamic role/assignment context,
and ordinary operations that return requested results, errors and necessary
operational controls/receipts. Source implementation is not permission to
activate this change. Before integration and rollout, the context-delivery
cutover gate must establish canonical template readiness for every supported
manual and automatic launch path (or retire obsolete paths), independent review,
and Operator approval.

Jarvis is an MCP bridge, not the host model's instruction harness. Repository
files and skills are contextual guidance, not authorization or host-level
messages. Reading them does not grant additional execution authority.

## Explicit repository hierarchy

Call `get_agent_instructions(projectName, agentId, directory, cursor)` when
repository-specific guidance is relevant. A checkout-relative directory is the
target; use the parent directory of a file when inspecting its instructions.
The canonical case-sensitive filename is `AGENTS.md`. Sources are ordered:

1. Workspace root `AGENTS.md` (key `.`).
2. Selected project or registered worktree root `AGENTS.md` (logical project key).
3. Each ancestor through the requested directory, in ancestor-to-descendant order.

Do not scan unrelated sibling trees or outside the workspace. A missing file
contributes no document; an empty file is included. Path confinement, UTF-8
validation and symlink/reparse-point protections remain enforced. A more
specific document may refine earlier guidance but cannot override host
restrictions, the user's intent or Jarvis permissions. Ordinary `read_file`
remains available for an explicitly named source file.

The hierarchy is returned under the path-keyed `AGENTS.md` mapping. There is
no instruction-specific file size or hierarchy-count limit. Transport-bounded
pages have a `nextCursor`; repeat with the identical selector and directory.
For a split file, `chunk` records the logical path, byte offsets, total bytes
and SHA-256; reconstruct the complete content across pages. A changed hierarchy
or invalid continuation fails rather than silently combining incompatible
snapshots. The caller must have the existing workspace read permissions.

## Persistent roles and individual agent instructions

Repository `AGENTS.md` is still scoped by filesystem path. Stable responsibilities
are now separate, canonical **Git-ledger entities**, independent of tasks and
source branches:

- `records/roles/{roleId}.json` defines one shared role (stable ID, human-readable
  purpose, Markdown instructions, version, updating actor and ledger sequence).
- `records/agent-instructions/{base64url(agentId)}.json` defines the agent's
  ordered role memberships, optional individual Markdown instructions, version
  and provenance. This record does **not** replace the existing agent lifecycle
  record and does not require an assignment.

`create_role` and `update_role` create/revise canonical roles; `get_role` and
`list_roles` read them. `attach_role` and `detach_role` change one registered
agent's membership; `set_agent_instructions` replaces that agent's individual
instructions (empty content clears the body but preserves roles). Read with
`get_agent_instruction_config` or `resolve_agent_instructions`.
All writes require `git:write`, derive the immutable operation actor from the
authenticated user, and participate in the same idempotent, optimistic
multi-record ledger transactions as ordinary orchestration. Role creation
starts at version 1; updates require the exact current version. An agent with
no configuration has implicit version **0**, used on its first attach/update.
Concurrent writes, stale versions, duplicate role attachment, unregistered
agents and missing role references fail without rewriting historical records.

The canonical instruction-layer metadata describes the instruction order as **global (when available), workspace
`AGENTS.md`, project/directory `AGENTS.md` from root to target, attached
roles in attachment order, individual agent instructions, and assignment-specific
instructions**. Earlier layers are authoritative over later layers: a
lower-priority document can specialize behavior, but cannot negate an earlier
restriction, a user's instruction, host rules, or Jarvis permissions. Role
membership never grants permissions or transitions agent activity.

Each **self-agent inbox** includes a distinct `instructionLayers` object alongside
its existing task, review and notification data. The explicit
`get_agent_instructions` result still contains only repository documents under
the path-keyed `AGENTS.md` object; it adds a separate `instructionLayers`
object on the first hierarchy page for a registered agent with a verified
bound ledger. That object contains the verified ledger snapshot,
ordered role IDs, agent configuration version, precedence, and each document's
scope, identifier, source ledger path, role/configuration version, Git blob OID,
SHA-256 and full instruction content. Repository files are never duplicated
inside it. New and resumed sessions obtain current role content on their **next
inbox or initial explicit instruction read**, including after role edits,
checkout switches or restarts; no instruction cache is authoritative.
Earlier Git snapshots preserve the exact prior content, while existing task
specs, checkpoints and reviews remain untouched.

Local `AGENTS.md` retrieval does not depend on a working remote ledger. When
the approved ledger is temporarily unavailable or contended, the first
hierarchy page still includes the local documents, but `instructionLayers`
becomes a **typed incomplete diagnostic**:
`{ "status": "unavailable", "complete": false, "code": "RemoteUnavailable",
"message": "INCOMPLETE INSTRUCTIONS: ... NOT loaded ..." }`.
This is not an empty role list or evidence of a successful role check. Retry
the **initial** request once the remote is available. Malformed records,
broken trust validation, an invalid configuration, missing referenced roles
or a mismatched registered identity remain errors, never degraded successes.

On `nextCursor` continuations, the local hierarchy is fetched independently
without another remote ledger roundtrip. Each continuation exposes
`instructionLayers` with `status: "continuation"`,
`complete: false`, `code: "RoleLayersNotRepeated"` and a reminder to use
the initial page's verified role content or restart without a cursor to refresh.
Role content is **not** repeated or silently treated as empty. Unregistered
identities with a reachable ledger have no role layers to return; an unknown
identity cannot be established while the remote ledger is offline, so it
receives the same incomplete warning as a previously registered agent.

Role and individual bodies are bounded at 16 KiB of escaped JSON each; agents
may attach at most 16 distinct roles. This controls worst-case transport size
without silently truncating instructions. Explicit repository instruction
pagination continues independently, reserving response capacity for complete
role/agent layers on the first page.

## MCP initialization and ordinary operations

Initialization uses a concise static description of the explicit context
contract, independent of the selected device, checkout or workspace contents.
Agents obtain canonical assignment context via `agent_inbox` and read
repository guidance explicitly when applicable. Referenced skills and policies
are loaded on demand. No ordinary operation reads repository instructions to
attach ambient context, including agent inbox, file/command operations and job
polling. Missing, malformed, oversized or divergent repository guidance cannot
block unrelated operations or generate omission/write notices.

Source preparation still resolves the selected checkout and configured named
task once. The same prepared source and task definition are used for
authorization and execution, independent of guidance retrieval. Existing path
confinement, worktree validation, permission checks, recovery IDs and canonical
agent-state invalidation remain intact.

## Staged wire compatibility

This source change retains the legacy `AgentCommandResponse.Instructions`
and `InstructionDocument` wire shape while the separately gated operational
response contract is reviewed. The new client emits only actual operational
controls through that field. The new server renders only the recognized
`response-alert` and `working-turn` scopes; old repository documents,
omission/write notices and unknown scopes are not projected. The same
repository-only filtering applies to the nested legacy original response in
MCP job result JSON, without altering stored job data, original operation
results/errors or mutation evidence. STOP-first alert ordering remains ahead
of timing guidance and ordinary results.

| Client | Server | Behavior |
| --- | --- | --- |
| New | New | No implicit repository discovery or projection; operational controls preserved. |
| New | Old | Existing wire shape decodes unchanged; no new repository documents emitted; existing control scopes still render. |
| Old | New | Legacy documents/notices are not rendered, including nested original job guidance. The old client still performs source discovery and may reject malformed files before dispatch. |
| Old | Old | Legacy behavior until coordinated upgrade. |

Wire compatibility does not prove bootstrap readiness or authorize a mixed
deployment. No launch may be stranded without the canonical workflow contract.
The separate control-contract and cutover tasks own eventual wire retirement,
release coordination and activation. No actual `AGENTS.md` file, role record,
or source/ledger authority is removed by this change.

## Verification

Focused client tests exercise ordinary file reads/writes, command execution,
job start/list/poll, malformed/oversized/missing/divergent guidance, explicit
hierarchy pagination and errors, checkout confinement and the immutable
configured-task snapshot. Canonical inbox activation and cross-client cache
invalidation remain independent of repository context. Server tests verify
legacy wire decoding/projection, terminal job evidence, and unchanged
operational alert order, ordinary errors and non-text results.

## References

- [Jarvis command surface](AgentCommandSurface.md)
- [Response envelope schema](AgentCommandResponse.schema.json)
- [Jarvis skills](Skills.md)
- [Response alerts](ResponseAlerts.md)
