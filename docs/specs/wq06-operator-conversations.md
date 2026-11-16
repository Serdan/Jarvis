# WQ06: manually operated Task / Role conversations (isolated)

Status: **development-only, not production Scheduler authority**. This implementation is
additive to the approved Git ledger and does not change WQ02/WQ04 canonical
work, source effects, role admission, or their protective guards. It is a
practical, inspectable Task/Role inbox while the existing transport remains
inert pending separate integration. Do not dual-write the same operator
message into both paths.

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

- This is a **manual** Task/Role conversation queue, not registered server
  MCP endpoints, not automatic scheduling, and not an authorized migration
  of the WQ02 project state machine.
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
