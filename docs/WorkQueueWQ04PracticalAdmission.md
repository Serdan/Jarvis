# WQ04/WQ07: explicit practical pinned-role admission (default-off)

## Scope and boundary

The opt-in `Client.WorkQueuePracticalAdmission.WorkQueuePracticalAdmission.admitExplicit`
is an isolated, authorized-Git-ledger bridge, **not a live Scheduler or account
launcher**. Its `explicitEnabled` argument defaults conceptually to **false** at
every caller: no runtime host, timer, browser bridge, provider, migration, or
production Scheduler registers or invokes it. The normal
`AccountAdmissionAdapter.admit` entry point remains fail-closed.

The positive source scope is deliberately **canonical CreateTask planning
work only**. The accepted `WorkQueueReceiptProducer.produce` must first have
written the real WQ04 work stage and its immutable original source-operation
identity and historical entity witness to the project Git ledger. Review,
integration, role-message, source-writing and external browser effects are not
authorized by this adapter.

## Transaction sequence

1. Re-fetch the approved project Git ledger; require an issued, enabled
   **current** WQ04 role revision, a compatible selected source task, an
   authentic original CreateTask receipt and historical source entity, and the
   exact immutable WQ04 origin blob OID. Fail closed **before** reserving a
   global token for ordinary missing-role or missing-source inputs.
2. Reserve capacity **first** through the existing WQ07 account-global Git CAS,
   with its original operation UUID, bounded cap, pinned project ledger identity
   and role version. Re-fetch the token and require status `Held`; never infer
   free capacity from a timeout or stale local cache.
3. Use `WorkQueueGitLedger.executePinnedRoleAdmission` for the project CAS.
   Its **winning LedgerStore.transact planner**, not a caller preflight,
   re-fetches the pinned WQ04 role/project history, canonical stage/source
   operation and original source entity, and the exact held account-global
   token. It then applies the real WQ02 `Contracts.admitProject` and verifies
   its **actual assigned credit** equals the selected immutable WQ04 work.
   The single project commit writes the WQ02 project record, original
   `AdmissionWon` decision and WQ04 role-admission witness together. The
   existing WQ04 historical role loader authenticates the original operation,
   definition OID, catalog OID and same-CAS triple thereafter.
4. Exact original operation retries reuse the WQ07 receipt and the project
   decision. The adapter checks matching launch/role/source/credit identity on
   replay. A changed operation request, missing original receipt, uncertain
   remote write, foreign project or stale source is **not** a new launch grant.
5. A completed or legitimately aborted launch frees global capacity only
   through `AccountAdmissionAdapter.releaseTerminal`, which re-verifies the
   immutable WQ02 terminal decision and project/account/role identity. A
   C01 abort has **no** executable WQ04 admission: the role loader validates
   its exclusive original WQ02 no-session abort operation and tombstone
   instead of inventing a role witness.

This is **not** a globally atomic transaction. The project CAS can lose its
acknowledgment after winning, or the process can stop between global and
project commits. Those states retain their original UUIDs and evidence;
the account token remains held until exact replay or directly audited
`AccountAdmissionAdapter.abortUnbound` confirms a definitive exclusive
unbound tombstone and terminal release. Do not mint a new UUID on an
unknown outcome; do not release on timeout or an arbitrary STOP/journal flag.

## Bounded Release verification

All sources, remotes, WQ07 tokens and launch IDs in
`Client.Tests/WorkQueuePracticalAdmissionTests.fs` are **disposable,
locally created two-clone fixtures**. In .NET 11:

```sh
dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore \
  --filter 'FullyQualifiedName~WorkQueuePracticalAdmissionTests' \
  --logger 'console;verbosity=normal'
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~WorkQueueRoleAuthorityTests|FullyQualifiedName~WorkQueueReleaseAcceptanceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests'
```

The tests cover real task-origin publication, original admission and replay,
foreign project, unknown role, stale source blob, cap-one conflict, project
lost-ack/recovery, terminal release/replay and C01 manual repair. No real
account, browser session, agent launch, remote deployment or unattended
Scheduler is used.
