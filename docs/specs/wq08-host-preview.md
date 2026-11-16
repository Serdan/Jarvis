# WQ-08: explicit client-owned scheduler host preview

Task: `wq-08-manager-scheduler-bootstrap`. This is a **default-off read-only
host adapter**, not a Scheduler service or migration. All preview I/O occurs in
JarvisClient from independently configured Git ledgers. No server storage,
timers, model Manager, account reservations, project writes, source mutations,
browser sends, deployment, GitHub-hosted Actions, or `canDispatch` changes.

## Operator command

```
dotnet run --project Client/Client.fsproj -c Release -- \
  scheduler preview /path/to/project /path/to/account-preview-binding.json Worker
```

The project root **must** already have an explicitly enabled locally approved
v2 `LedgerBindingConfig` trust binding. The separate account JSON file is an
explicit operator-supplied **read-only** selector, not permission to reserve
credit or to change account-wide configuration. It has these properties:

```json
{
  "repositoryPath": "/path/to/approved/account/clone",
  "remoteName": "origin",
  "approvedFetchUrl": "/path/to/remote.git",
  "approvedPushUrl": "/path/to/remote.git",
  "accountId": "00000000-0000-4000-8000-000000000001",
  "ledgerId": "00000000-0000-4000-8000-000000000002",
  "epoch": 1
}
```

Use the **real approved** values for an existing account. The example UUIDs
and URLs above are illustrative, not configured credentials. The account reader
validates its binding and fetches the authoritative Git ref, not stale local
files. The project reader loads `WorkQueueRoleAuthority.loadAt` from one
verified project Git head, including the historical versioned role catalog.
The role is resolved to its current enabled definition. `ReadOrigin` reads
the immutable WQ-04 work-origin record at that head; it exposes the stored
source ref, source SHA, request hash, and operation ID where available.
`ReadAdmission` reads the corresponding original project witness.

The JSON result includes `mode: ReadOnlyDryRun`, `canDispatch: false`,
account/project heads, capacity and occupancy, versioned role, available and
skipped candidate work, an informational `wouldLaunch` candidate, existing
launch states, original dispatch/browser ID/outcome when the read port supplies
it, and `issues[]` with the exact unresolved records and next actions.

**Importantly**, an origin stored at a verified project head is a recorded
source claim, not independently authenticated upstream source authority. The
preview does not assert that source/current role/account/project observations
were acquired atomically or authorize a launch. If the source record is absent,
it says `SourceReceiptMissing`, rather than forging a SHA or inventing an
empty queue. WQ-04 selection is per requested role/project, not a complete
cross-project Scheduler ranking. Account capacity is a separate fresh fetch;
another client may consume the last slot afterward.

## Browser one-shot and uncertainty

The existing WQ-08 `SchedulerDispatchPrototype` carries a stable launch ID,
fingerprint, one-shot browser operation, account/project heads, and original
outcome through durable injected CAS ports, including fail-closed unknown
recovery and independent fake-browser tests. This preview does **not** bind
those ports to the real browser. `ReadOriginal` is an injectable, read-only
port and deliberately returns `BrowserJournalUnknown` against the live
adapter until WQ-03 exposes an approved original one-shot journal reader.
A missing result **is never permission to send again**, release the token, or
create a new execution. The `ExistingLaunches` array is bounded to 64
role-matching launches; truncation is explicitly surfaced.

For uncertain/contended records, preserve the original launch, project-head,
account-head, WQ-04 origin, admission and browser operation IDs. Check the
remote Git history, recover original receipt and resolve whether it was
applied, then choose an authorized CAS retry or direct, audited repair with a
backup Git SHA/diff; never overwrite a concurrent update or silently drop
unknown outcomes. See `wq08-practical-dispatch-prototype.md` for the manual
ledger repair packet.

## Verification / production gate

Client preview tests inject two independent reader instances, a full account
(cap=1), an unsubmitted candidate, missing source, unknown browser result, and
unavailable remote heads. Existing `Server.Tests` WQ-08 tests exercise the
original browser via a fake adapter; `Client.Tests` WQ-07 account/admission
fixtures exercise disposable two-clone Git last-slot/C01 races. Neither test
mode permits a production browser submission.

Still required for production: WQ-04 canonical work-source evidence in the
same applicable admission path, WQ-03 browser original reader / host adapter,
WQ-06 connection bootstrap handler binding, account trust / concurrency
reconciliation and operator cutover authorization. Production
`Contracts.canDispatch` remains **off**.
