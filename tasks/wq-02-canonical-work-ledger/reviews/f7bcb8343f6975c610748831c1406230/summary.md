# Independent frozen WQ-02 r4 review — Accepted (source candidate only)

**Reviewer:** Reviewer 1
**Review ID:** `f7bcb8343f6975c610748831c1406230`
**Exact frozen source:** `f7bcb8343f6975c610748831c1406230e5dfa6c7`
**Accepted WQ-01 source baseline:** `7b589173135178a634495146675dad5db647e5f8` (verified ancestor)
**Prior review:** `5f8a9c41276e4d42a4f43c568f03e2cd`, ChangesRequested seq3308 against `683357d7c1432541abcfa59fbbf483feab49a371`.

## Verdict and scope

**Accepted for the exact frozen, isolated, nonactivated WQ-02 Git-CAS foundation.** No new blocking defect found in this correction. This is NOT approval to activate the queue runtime, migrate existing tasks, claim account-global admission authority, release tokens, merge unreviewed successors, or deploy. Those need separate independently verified proofs and authorization.

## Independently inspected

- Compared `7b589173..` to `f7bcb834..` (five changed files: one new 569-line ledger adapter, 940-line test suite, 48-line read-only contract extension, two project files), and inspected final 9-line adapter/40-line test correction.
- Inspected `Client/Modules/WorkQueueGitLedger.fs` encoding, parsing, envelope hash, structural validation, immutable mutation serialization, `initialize`, `change`, `execute`/`executeAt`, dependency witness, state proof guards and Git-CAS planner.
- Traced original-operation semantics into `Client/Modules/LedgerStore.fs` `intentInput`/`intentHash`/`transact` (near lines 1940–2080): the fetched Git snapshot's UUID receipt and request hash are checked **before** the planner/version comparison. Same UUID+hash returns the original operation; changed hash rejects `OperationIdReused`. The entity, operation receipt and ledger header are published in one Git tree. A contended push must re-fetch and re-plan at the winning remote head.
- Read affected `Server/WorkQueueContracts.fs` contracts: CAS/typed receipts, graph coverage, available ordering and fresh FIFO wake, launch matching/one primary claim, Role-message generations, gate resolution, source-read-only yield; no source-writing handoff was enabled.
- Inspected the concrete cross-clone tests, including stale project-version conflicts, claim/terminal exclusivity, hard-prerequisite gate/role handler fences, forged publication waivers, forged launch registration, poisoned historical reads, no source-writing completion without proof and no unproven terminal release.

## Former P1 original-UUID replay defect — corrected

Previously `WorkQueueGitLedger.change` serialized a fresh `serverWitnessMillis` with the semantic mutation request; `LedgerStore.intentHash` therefore rejected an identical delayed public retry as `OperationIdReused`.

At frozen `f7bcb834`, `WorkQueueGitLedger.fs` near lines 437–454 no longer adds `now` to the canonical intent request. The request still includes `schemaVersion`, `expectedProjectVersion`, and the complete canonical typed `WorkMutation`; `LedgerStore.intentInput` additionally binds project, operation UUID, actor and operation kind. The wall clock is used **only by the winning project transition** to witness its persisted Available timestamp/FIFO. The immutable Git receipt is authoritative for replays, so a later invocation cannot rewrite that first time or consume another FIFO sequence.

The new public API two-clone regression (`Client.Tests/WorkQueueGitLedgerTests.fs` near lines 136–174) starts an actual `execute`, waits 1,300 ms, repeats the identical UUID/actor/version/mutation on clone B, and asserts `AlreadyApplied=true`, exact original operation/request hash/receipt JSON, unchanged remote SHA/project version/state/receipt count/time/sequence. Changed actor, changed expected version and changed mutation all reject as `OperationIdReused`. This exercises the actual public clock-sampling path, not only `executeAt`.

## Architecture and fail-closed invariants

1. **Canonical codec and state:** pre-CAS typed payload roundtrip and candidate state roundtrip; schema/project ID, state hash, structural ownership and receipt-count/version checks; corrupted historical state rejected by `load`.
2. **Scheduling:** only a verified successful CAS publishes a fresh Available witness; unsatisfied hard edges remain Blocked; matching of unclaimed launch credits is injective and rerun on relevant eligibility changes. No second primary claim under stale clone.
3. **Role/Task message semantics:** Task messages cannot be forged into work stages; Role work/message queue/subject/handler identities are tied; dedicated Role primaries cannot acquire a second ancillary message; stale handler generations fail despite fresh project version.
4. **Authority/effect gaps fail closed in this frozen adapter:** initial stage waivers must be empty; launch admission accepts only `ReservedUnregistered` with no lease/claim/credit; `CompleteOrdinary` for Implementation/Integration fails without remote proof; `TerminalizeNoClaim` fails without session/journal proof; read-only yield requires exact current claim generation, clear gate and no source-writing effect.
5. **Git receipt:** same semantic UUID replay is checked prior to planner; modified request identity is rejected; a concurrent push cannot silently overwrite the winning head.

## Independent local Release evidence

- SDK: `dotnet --version` = **11.0.100**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueGitLedgerTests --verbosity minimal`: **21 passed, 0 failed, 0 skipped**, exit 0, ~50s. This rebuilt Release Client and Client.Tests.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --verbosity minimal`: **212 passed, 0 failed, 0 skipped**, exit 0, ~6s.
- `git diff --check 7b589173135178a634495146675dad5db647e5f8 f7bcb8343f6975c610748831c1406230e5dfa6c7`: exit 0.
- `git rev-parse HEAD` still yields exact `f7bcb8343f6975c610748831c1406230e5dfa6c7`; `git status --porcelain` empty after tests.
- Full Client.Tests is **not** claimed because of separate WQ-11 test-host/profile isolation work. Existing compile warnings are outside the reviewed correction. No GitHub Actions, Codex, source change, merge or deployment.

## Explicit remaining preactivation gates (not acceptance claims)

This remains `MigrationEpoch.Frozen` and is not wired into live orchestration. A trusted server-clock witness, account-global reservation/release and authenticated one-shot launch/session evidence, remote source ref/SHA and job-journal reconciliation, versioned external effect/waiver proof, canonical producer/graph/queue/source high-water attestation, migration and runtime activation all remain unfinished. The current test fixture's clock, coverage flags and token fields do **not** establish production authority; no inference of a fully operational scheduler is permitted.

**Final verdict: Accepted for exact frozen nonactivated WQ-02 r4 source SHA `f7bcb8343f6975c610748831c1406230e5dfa6c7`.**