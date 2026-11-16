# WQ06: manually operated Task / Role conversations (isolated)

Status: **manual, authenticated MCP adapter opt-in; not production Scheduler
authority**. This remains additive to the approved Git ledger and independent
of WQ02/WQ04 work, source effects, role admission and their guards. The Jarvis
MCP surface is **disabled by default** until an operator sets
`EnableOperatorConversations=true` in the server configuration. Do not
dual-write the same operator message into this and automatic transport.

## Storage and state

Each subject `Task:<task-id>` or `Role:<role-id>` has a Git-backed
conversation document under
`records/work-queue/operator-conversations/threads/<sha256(kind:id)>.json`.
It stores ordered messages and an append-only-in-document audit of every
successful action. A message can be:

- `Pending`: visible, unclaimed, can be claimed by an active operator/agent.
- `Claimed`: handler name and increasing handler generation recorded.
- `Handled`: immutable original reply ID/body and original handler retained.

Normal read is passive. Every mutation is one ordinary remote Git CAS
through `LedgerStore.transact` against an exact per-subject version/blob.
A competing claim receives a visible stale-version/conflict result, never
an invisible second handler. A later claimant increments generation, so
old handler replies cannot complete the message. Operator release requires
an explicit reason and records the old handler, generation, and prior Git
commit. It does not silently time out leases.

Every action has a caller-stable UUID. If a push may have succeeded but its
acknowledgement was lost, `inspect` checks its immutable receipt at a
verified head; repeating the *identical* operation ID, actor, and parameters
replays that receipt instead of creating a second message/answer. A missing
receipt at the observed head is **not proof** a timed-out push can never
arrive; preserve the original UUID/parameters and reconcile before making
a replacement decision.

The thread version, message status, exact message/reply IDs, handler,
generation, Git prior-head audit and returned Git commit/receipt sequence
are operator-visible. On an anomalous record, no source change is required
to repair the Git-backed JSON: an explicitly authorized local operator can
inspect raw data, compare against a previous blob, and publish a replacement
with a backup blob SHA and independent immutable repair audit.

## Local CLI

The existing client executable now has a `conversation` command. It uses
only a pre-existing, locally enabled and trusted Jarvis ledger binding for
`ROOT` (the project checkout). This is an intentional local-operator
interface, **not a remotely callable MCP tool**. The `ACTOR` argument
is an audit label, not an authentication claim or proof of a live launch.

```bash
CLIENT="dotnet /path/to/JarvisClient.dll"
ROOT="/path/to/approved/project"
$CLIENT conversation "$ROOT" Role Lead list
$CLIENT conversation "$ROOT" Role Lead post Operator "$POST_UUID" 0 "$MESSAGE_ID" "Please triage"
$CLIENT conversation "$ROOT" Role Lead claim HandlerA "$CLAIM_UUID" 1 "$MESSAGE_ID"
$CLIENT conversation "$ROOT" Role Lead handle HandlerA "$HANDLE_UUID" 2 "$MESSAGE_ID" 1 "$REPLY_ID" "Decision and rationale"
$CLIENT conversation "$ROOT" inspect "$HANDLE_UUID"
```

Use fresh UUIDs for each new mutation and globally unique message/reply IDs,
but reuse the **same** UUID and arguments after a lost acknowledgement.
Always take the version and handler generation from a fresh `list`; they
are CAS preconditions, not locks. Role threads do not require an online Lead,
but posting does not create Scheduler credits or launch an agent. Task
threads likewise do not implicitly reopen or approve tasks.

For a stalled handler, use `release ACTOR OP_UUID VERSION MESSAGE_ID GENERATION REASON`.
This returns the message to Pending, preserving the audit; a subsequent
claim increases the generation. Do not release merely because a message
seems slow: check the handler and its outcome first.

## Authenticated Jarvis tools (opt-in, no autonomous execution)

The new MCP methods require an approved matching Jarvis server/client and
`EnableOperatorConversations=true` server configuration. The checked-in
setting is `false`; this change does **not** deploy, enable, or launch them.
No scheduling, launch credits, WQ04 transport, or WQ02 migration is authorized.

The server derives actor/handler identity from the authenticated OAuth subject
(`operator:<sha256-sub>`). No MCP argument can select an arbitrary actor,
local repository ROOT, Git remote, or trusted ledger path. The client resolves
the project through its existing approved ledger binding. Identity is
OAuth-account scoped; multiple agents using one account share an operator
handler label. The explicit claim generation is still a concurrency fence.

Read-only methods require `workspace:read`; writes require `git:write` and
local version-control authorization. All are project-scoped:

- `ListOperatorConversation`: passive Task/Role thread read, including
  Pending/Claimed/Handled, reply, audit, handler generation and verified Git
  head and per-thread CAS version.
- `RawOperatorConversation`: raw Git JSON, verified head and backup blob SHA
  even for malformed content, for explicit repair inspection.
- `InspectOperatorConversationOperation`: original UUID receipt, Applied or
  NotFoundAtVerifiedHead. Absence at a verified head does not prove a timed-out
  write can never arrive.
- `PostOperatorConversationMessage`: add Pending message at expected thread
  version with stable message ID; no Scheduler demand.
- `ClaimOperatorConversationMessage`: claim one Pending message by Git CAS
  under the authenticated actor, increment handler generation.
- `HandleOperatorConversationMessage`: record immutable reply at expected
  thread version and current handler generation, with stable reply ID.
- `ReleaseOperatorConversationMessage`: explicitly release a claim under
  expected version and generation with an audit reason; no automatic timeout.
- `RepairOperatorConversation`: replace a JSON document only at its exact
  original blob SHA, with reason and separate immutable repair audit. Review
  the raw before/after diff; it does not auto-correct the document.

All actions take `projectName`, `agentId` (Jarvis routing label), subject
kind `Task` or `Role` and subject ID as applicable. Mutations take
`expectedVersion`, action-specific message/reply IDs and the original
`operationId`. For a new write the UUID can be omitted and returned by
Jarvis. For an unknown outcome, retry the **identical** UUID, actor and
parameters; inspect the original receipt first. The server-derived actor
cannot be overridden. Git remains the sole durable conversation store.

## Direct repair and visible diff

The privileged repair path intentionally edits the Git-backed conversation
record without waiting for a special schema-migration or application patch.
The operator remains responsible for reviewing changes and choosing a
consistent replacement. Use `raw` to inspect malformed documents that
normal `list` refuses; the output contains the verified Git head, current
blob SHA, and original content.

```bash
$CLIENT conversation "$ROOT" Role Lead raw
# Back up the printed blob SHA. Git owns the immutable backup object:
git -C "$ROOT" cat-file blob "$OLD_BLOB_SHA" > backup.json
git diff --no-index -- backup.json replacement.json
$CLIENT conversation "$ROOT" Role Lead preview-repair replacement.json
$CLIENT conversation "$ROOT" Role Lead repair Operator "$REPAIR_UUID" "$OLD_BLOB_SHA" "why this edit is needed" replacement.json
$CLIENT conversation "$ROOT" inspect "$REPAIR_UUID"
```

The repair CAS checks that the current thread blob still equals
`OLD_BLOB_SHA`. It commits the replacement and a separate
`records/work-queue/operator-conversations/repairs/<UUID>.json` audit
containing actor, reason, prior Git head, old blob SHA, and replacement SHA.
The old version remains in Git history. Conflicts require re-inspection.
Unknown outcomes require receipt reconciliation using the same UUID.
To repair a missing subject, pass literal `none` as old blob SHA.

The repair command can replace a structurally invalid JSON document with a
valid one; it will not automatically fabricate missing state or claim that
an invalid replacement is correct. Only an approved local operator with
write authority should invoke it. Preserve the original diff and reason
for reviews.

## Constraints and follow-on integration

- This is a **manual** Task/Role queue with **default-disabled** authenticated
  MCP endpoints; not automatic scheduling or a WQ02 state-machine migration.
- Existing `WorkQueueConversationTransport.fs` retains its fail-closed
  WQ04/WQ03 production gates. Its historical positive tests against the
  guarded production path still fail pending a fixture/authority integration;
  do not reinterpret them as passing or weaken guards to satisfy them.
- The four `WorkQueueConversationTransportTests.fs` cases therefore remain
  excluded from the default `Client.Tests` compile/run. They are opt-in with
  `-p:RunWq06TransportFixtureTests=true` for prerequisite fixture work, not a
  passing validation suite. Restore default coverage only after valid canonical
  producer and pinned role-origin evidence is supplied.
- A subject document has the ledger's 64-KiB bounded-file limit; overflow
  errors must be surfaced. For sustained production volume, decide on
  per-message append-only records and a cursor/index before enabling
  automatic consumers.
- When WQ04/Task/Role producer authority is actually integrated, plan one
  explicit migration and ownership decision. Never treat this manual inbox
  as a second automatic source of work, or silently merge its messages
  with canonical WQ02 launch credits.
- No hosted GitHub Actions, unattended Scheduler, deployment, restart,
  production migration, or Codex invocation is authorized by this work.

## Validation

`Client.Tests/WorkQueueOperatorConversationsTests.fs` uses two disposable
Git clones and the real `LedgerStore` remote CAS path, covering passive
Task/Role reads, one winning handler, stale-generation rejection, durable
reply and lost-ack replay, direct backup/audit repair, and unrelated subject
isolation. Run with `.NET 11` in Release, for example:

```bash
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueOperatorConversationsTests
```


## Observed live MCP registration gap (2026-11-16)

At the 2026-11-16 check, the authenticated, connected Jarvis `ListCommands`
response reported protocol `3.64` but **did not advertise any of the eight**
`*OperatorConversation*` aliases. The available connected MCP tool catalog
likewise did not expose a callable Task/Role conversation method. In contrast,
the checked-in source at `main` (`275dce20`) includes the typed server
methods, `ledgerAliasCapabilities` entries, default-off server configuration,
client routing, and disposable Git-CAS tests. The source catalog entries are
not conditional on `EnableOperatorConversations`; actual method execution is
guarded by that flag.

This is an **observed registration/availability gap**, not evidence of a
working live operator endpoint. The check cannot distinguish a stale deployed
server binary, a stale exposed MCP schema, or another version/capability mismatch.
It does not authorize a deployment, restart, config change, or real message
push. The local `.NET 11 Release` tests exercise the source path, not the
current live connected Jarvis deployment.

Before a future operator-authorized enablement, verify that matching
server/client builds expose all eight named methods through the authenticated
tool catalog, explicitly configure the normally false
`EnableOperatorConversations` flag, confirm the approved project Git ledger
binding, and perform a **disposable**, authenticated list/post/claim/handle/
inspect replay test first. Preserve original operation UUIDs and the
verified-head/Unknown evidence at every transport boundary. Do not treat the
manual Task/Role thread as Scheduler demand or automatic role authority.
