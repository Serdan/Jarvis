# V2-09 task specification authoring, provenance and bounded reading

## Contract

Canonical task specifications remain Git blobs at tasks/<taskId>/spec.html
on the single verified orphan-branch ledger. Importing or exporting a source
worktree file is never implicit synchronization. The server remains stateless;
the registered client performs Git and file operations after its normal
permission checks.

The canonical UTF-8 HTML limit is now **512 KiB**, matching the architecture.
The former 128 KiB HTML restriction in both the workflow and ledger write
layer was removed; ordinary entity JSON continues under the 64 KiB cap.
Task-create/revision operation records contain only a **SHA-256 digest of
HTML content**, not the full 512 KiB payload. Durable pending local operation
journals retain the exact command and reconcile the digest with the
canonical recorded request. The experimental V2 typed protocol is **3.34**.

## Authoring and pinning

- CreateTask and legacy ReviseTaskSpec still validate safe UTF-8 HTML. Pinned
  ReviseTaskSpec requires the expected task version plus exact canonical
  spec blob OID, including when a task is already InProgress.
- ImportTaskSpec reads only an explicitly selected **registered Git worktree**.
  It requires a relative HTML path under the checkout and checks path
  traversal, hidden Git paths, reparse/symlink components, UTF-8 validity,
  worktree HEAD branch and commit, source-blob hash and an optional
  caller-supplied expected source blob. The source bytes, OID, branch, HEAD,
  repository identity and relative path are rechecked from the filesystem
  during each ledger CAS plan. It atomically commits the task/spec and
  an immutable import-provenance record. This source blob represents the
  **worktree bytes**, not a claim that HEAD itself contains those bytes.
- Assigned attempts retain their original immutable instruction generation
  and pinned spec despite a canonical revision. AdoptTaskSpec explicitly
  requires exact task/attempt/agent versions, current canonical spec OID,
  unchanged unstarted Assigned owner and no review submission; it issues
  generation N+1 with reason SpecAdoption. Historical issue/receipt records
  remain unchanged. A Working agent cannot silently adopt new instructions.
- ExportTaskSpec is a **Git-write permission** command even though it does
  not mutate the ledger. It creates a clearly marked non-authoritative HTML
  file in the registered worktree, with task, ledger snapshot, source blob
  and exact operation UUID. The move is create-only; retry of the same UUID
  and exact content returns AlreadyApplied=true without overwriting,
  while other UUIDs, changed content, symlinks and traversal are rejected.
  Source exports never grant write authority to local copies.

## Content and history

GetTaskSpecPage supports up to **32768 UTF-8 bytes** per page, no
mid-code-point splits, total byte count and next-cursor/truncation flags.
A portable URL-safe base64 cursor pins project and ledger UUIDs, task, exact
canonical snapshot commit, exact spec blob, offset and a maximum 30-minute
expiry. Resume verifies the canonical ledger ancestry and provenance of
the historical blob at that task path. There are no server signing keys,
external databases, filesystem checkouts or mutable caches required for
ordinary reads. Cursor corruption, mismatched project/task, oversized
pages and invalid UTF-8 offsets fail closed.

DiffTaskSpec compares two pinned historical OIDs after proving both
occurred at that task's canonical spec path, even if the live spec has
changed. Diff output is bounded at 128 KiB with an explicit Truncated flag
and UTF-8-safe truncation. The subsequent task-history/roster **paging of
task metadata and ledger commits** belongs to V2-10, not V2-09.

## Security and limits

Task HTML rejects active script/embed/frame/form elements, event-handler
attributes and JavaScript URI content at canonical write time. UI must
still render it as untrusted content. Symlink checks are local
path-component checks, not an OS-level race-free openat/no-follow handle;
a hostile concurrent local filesystem actor could change a component
between checking and opening. Source file revalidation closes the tested
preflight/CAS race but external edits after the final check and before
the remote push cannot be globally atomic with a separate Git ledger.
This is a documented limitation, not a claimed distributed transaction.

No production V2 ledger was created or imported, no V1 writer switched,
and no source repository pushed or deployed by this implementation.
Actor labels are logical, not cryptographic (V2-11).

## Verification

Disposable local bare-Git-remote fixtures cover exact 512 KiB acceptance
and 512 KiB + 1 rejection without ledger mutation, active-HTML rejection,
trailing-newline round trips, Unicode UTF-8 paging pinned across revisions,
unexpected/forged cursors, historical diff and non-historical OID rejection,
source import provenance, symlink/path traversal, immutable task CAS and
deterministic changed-file failure after preflight, export no-overwrite and
UUID/idempotency behavior, explicit Assigned adoption and stale/Working
generation rejection, and opted-in typed V2 client protocol commands.
Server tool/catalog tests verify read-only versus Git-write tool routing.

Final complete test counts and implementation commit are added after
the full suite is verified.
