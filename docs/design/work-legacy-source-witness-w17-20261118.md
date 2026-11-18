# Legacy original-Git source / STOP witness: Worker 17 C-to-B evidence

**Status:** default-off, unhosted, source-only proposal. No live migration/cutover approval.

## Exact implementation boundary

- `Server/WorkLegacySourceWitness.fs` composes existing `WorkLegacyImportPreflight.inspect`, the actual C1 SQLite import receipt, existing C2 receipt/fence fields, `OriginalGitPublisherAdmission.validateTarget`, and the accepted C1-to-E `WorkLegacyProjectionEvidence` vocabulary. It does **not** rewrite import, normalize stage JSON, mutate the original ledger, install a reader, or activate the scheduler.
- `IWorkLegacyAuthenticatedSourceReader` is an intentionally **unimplemented** source adapter. A trustworthy implementation must reread original project/account remote refs at exact canonical repository ID + approved remote fingerprint, verified Git commit/ancestry and UUID/request/result transaction identities, role/decision receipts, account capacity version, and source-effect operation high-water from the same authenticated enforcement domain. Caller-supplied strings, local worktree names, cache/JSON, and `SourceAncestryVerified` flags are not proof.
- The returned typed witness carries distinct account, ledger, remote/ref and stage identities, source operation UUID/request SHA and reachable commit receipt, original STOP generation / barrier epoch, high-water summaries for pending intents and source effects, account capacity version, role versions and digests, claim/stage generations, review/integration and launch lineage. A missing/uncertain reader yields typed `Incomplete` / `Unknown`; a mismatch to authenticated evidence yields `Rejected`. The C1 receipt must exist in the disposable SQLite database, and original STOP/source must be re-read after C1.

## What the original accepted modules actually prove

- C0 (`WorkLegacyImportPreflight`) validates structural head, contiguous operation history, graph, stage/generation, review, integration, terminal and account token consistency **of a supplied typed inspection**. It does not independently authenticate the original Git remote, credential, ref or writer.
- C1 (`WorkLegacyImportSqlite`) creates a disposable original snapshot and transaction receipt, but its basic fence is only a supplied offline fence, not a live authorizing original writer STOP.
- C2 (`WorkLegacyImportFence`) defines an all-client, high-water original Git fence gateway and receipt matching. No original authority implementation is installed. A returned C2 record/fixture receipt alone does not exclude stale original clients.
- Accepted Worker 17 C1→E normalization (frozen `3e795e008295f800a3010ca4bf67cb9a52be2977`, independent review `eb29c792f15c4be78f82d1284d67a1b5`) is an offline proof of C1/C2-equality and projected rows under an **injected synthetic verifier**. It explicitly does not confer live original-writer authority.
- Worker 15's independently Accepted producer matrix (frozen `09b739e32b928e8a2014e93032c65edacea55526`, review `58a4fa7593d749948cbc6fa14f9e5b9c`) correctly identifies missing trusted source ingress and full migration/restore gates; its acceptance was **design-only**.

## Blocking upstream dependency — real P1 lost-ack rollback

Worker 21's frozen P1 original-writer review `8d3d20be52e14bc59ee31b32d210f3e4` is **ChangesRequested**, not Accepted. Reviewer 2 found that the independent host state stores an effect high-water *at STOP*, not at each pre-publication admission. A same-UUID SQLite backup taken before an effect can replace the journal **before STOP**; after an external Git push with lost acknowledgment, STOP can read that stale journal and falsely report zero unresolved effects. Merely matching SQLite and a STOP-time counter is unsafe.

The original writer must first durably and independently commit a monotonic effect/admission identity/high-water **before** any irreversible external publication. Remote/ref outcomes, old-client bypass/credential revocation and host root/binding changes must be covered by genuine authenticated enforcement and recovery. A result field called `AllOriginalClientsQuiesced` or a digest with no independent source verification does not repair this P1 finding. In the absence of this upstream witness, **no production `Verified` capture is possible**.

## Fixture evidence and nonclaims

The isolated .NET 11 Release tests use disposable local original Git repositories (two project ledgers deliberately share the same work label plus one account ledger), true local Git commits and canonical refs, a disposable C1 SQLite receipt, and a deliberately synthetic reader with captured source snapshots. They exercise valid scoped identities, caller forgery, stale project ref/commit, missing reader, C1 receipt absence, pending/lost-ack high-water divergence, corrupt remote vs durable source-effect digest, repeated UUID or altered result digest, UNKNOWN effects, stale role, missing effect journal and a real post-STOP Git ref mutation. This proves only offline validation behavior under the supplied reader contract, **not** remote CAS atomicity, host durability, all-client safety, or actual pre-STOP stale-SQLite restoration recovery.

## Next owners / HOLD

- **Worker 21/original Git writer owner:** correct and independently review the pre-STOP independent durable monotonic admission/effect journal, with a *real* lost-ack push and pre-STOP same-UUID SQLite rollback regression. Require authenticated remote/barrier identity, not synthetic fixtures.
- **Worker 15 and Worker 20 (B ingress):** consume the typed witness *only after* an independently authenticated source reader and original hard STOP can be proven, and preserve exact source/approval/role/effect linkage. Do not promote this unregistered witness directly to an authorization.
- **Worker 4 (C handover):** require durable barrier quiescence, complete account/project/ref inventory, exact high-water, all-client admission denial and reinspection before final handover. Unknown effects remain Recovery.
- **Integrator/operator:** all live-original-writer retirement, old/new dual-writer operation, credential/rule changes, automatic Scheduler / Work API activation, SQLite cutover, service restart/deploy and hosted actions remain HOLD pending separate independent acceptance and explicit GO.
