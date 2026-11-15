# Independent frozen WQ-02 r2 review — ChangesRequested

**Reviewer:** Reviewer 2  
**Review:** `8f0b3189123049c59146a9b35a5d6409`  
**Exact frozen source:** `98cdd713d270a81690773c5c513d9180a65431ab`  
**Accepted WQ-01 base:** `7b589173135178a634495146675dad5db647e5f8`  
**Previous WQ-02 review:** `9da890df8c3b4d9ca6b3c2dca18472f5`, ChangesRequested on `71fc78d051d6509596389abfa8fe7cb5f444cd1f`.

## Independent inspection and verification

Reviewed the exact frozen diff, complete `Client/Modules/WorkQueueGitLedger.fs`, all 18 focused `Client.Tests/WorkQueueGitLedgerTests.fs` cases, and relevant inherited `Server/WorkQueueContracts.fs` admission, dependency, claim, role/ancillary, completion and terminal transitions. The source worktree HEAD matches the frozen SHA and remained clean. Original submission changed only adapter and test source from the previous candidate; five files differ from the integrated WQ-01 base.

Local **.NET 11.0.100 Release**, not GitHub Actions:
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueGitLedgerTests --verbosity minimal`: **18/18 passed**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueue --verbosity minimal`: **77/77 passed**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --verbosity minimal`: **212/212 passed**.
- Attempted full `Client.Tests -c Release --no-build`, but it **timed out** after 180 seconds (ExitCode -1). Captured output included Jarvis activity-persistence permission errors. **No complete Client.Tests pass is claimed**; do not bypass operator-profile protection. WQ-11 hermetic harness is separate work.
- Independently reproduced the new P1 below with exact frozen Release assemblies and a disposable bare Git remote. The temporary F# script and scratch repository were removed. No source modified, no merge/deploy.

## Corrected previous findings

1. **P1 forged prerequisite waivers: corrected at the adapter boundary.** `validInitialStage` rejects nonempty waivers on stage/Role-message publication (`WorkQueueGitLedger.fs:303-311,398-408`), and persisted `structurallyCoherent` rejects waiver maps (`:262-270`). New tests prove D and Role work remain blocked behind unfinished B and wake only on legal B completion.
2. **P1 forged already-Registered launch: corrected at direct admission.** `validInitialLaunch` enforces ReservedUnregistered, zero version/dispatch, no lease/credit/claim/ancillary, valid project/role/token fields, and `AdmitProject` rejects duplicate token IDs (`:313-326,409-418`). New tests reject Registered, Submitted and terminal states and retain the legal register/claim path.
3. **P1 source-writing Complete without proof: corrected fail-closed.** `CompleteOrdinary` rejects Implementation and Integration (`:426-433`); new two-clone tests prove claimed work and downstream blocked D do not change when source lineage is absent or incomplete.
4. **P2 Registered no-claim terminal without proof: corrected fail-closed.** `TerminalizeNoClaim` rejects until verified session/journal evidence exists (`:444-447`); new tests cover live, already-claimed, and stopped-lease states. No positive verified release path is claimed.

The inherited regression coverage also exercises exact operation replays and payload mismatch, two-clone CAS, single primary claim and matched credits, dependency wake/FIFO, read-only yield/checkpoint, handler generations, ancillary Role gates, stopped intake, and historical malformed Task message rejection.

## New P1 — Successful publication can poison the canonical ledger

**Detailed finding:** `findings/p1-null-payload-poisons-canonical-ledger.md`.

The exact frozen adapter accepts `PublishStage` of an otherwise normal Planning stage with `QueueId = null`, commits it, then refuses to reload the committed project record. Independently executed reproducible output:

```text
PUBLISH_SUCCEEDED
LOAD_FAILED: LedgerIntegrityError
  "Malformed canonical work ledger: Unexpected null in canonical work record."
```

`CanonicalCodec.encodeType` emits JSON null for null strings (`:32-33`), while `decodeType` rejects null (`:74-76`). `validInitialStage` and `structurallyCoherent` do not reject the malformed queue field; `change` commits content without a decode/validation roundtrip (`:485-492`). This is a reachable, version-correct public mutation, not forged raw Git state, and leaves the canonical record unreadable until manual repair. It is an internal integrity flaw even in the deliberately isolated WQ-02 foundation.

**Required narrow correction:** Reject null/invalid nested stage/message fields; verify candidate serialized content round-trips and remains structurally coherent before any Git CAS commit. Add two-clone malformed-stage/message negatives proving unchanged version/receipts and subsequent valid claim. Preserve all four previous guards and exact replay. Rerun Release checks and submit a **new frozen independent review**.

## Remaining separate preactivation gates

This is a `MigrationEpoch.Frozen` fixture, not the live scheduler. Authenticated account-global reservation/release and first connect, independent producer/queue/source high-water proofs, trusted clock witness, remote source-ref/SHA and mutating-job reconciliation, positive source-writing terminal/recovery proofs and F06 integration/waiver authority remain unavailable. Passing tests do not authorize activation, migration or deployment; shape-validated historical launch state alone is not authenticated provenance.

## Verdict

**ChangesRequested** on exact frozen `98cdd713d270a81690773c5c513d9180a65431ab`. The earlier four bypasses are closed in their exposed paths, but the independently executed new P1 canonical-serialization poison prevents acceptance. No implementation edits, merge, GitHub Actions or deployment performed by reviewer.
