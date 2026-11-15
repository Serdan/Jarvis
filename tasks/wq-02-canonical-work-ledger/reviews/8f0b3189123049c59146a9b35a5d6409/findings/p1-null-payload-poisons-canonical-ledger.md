# P1 — A successful publication can persist an unreadable canonical project record

**Frozen WQ-02 source:** `98cdd713d270a81690773c5c513d9180a65431ab`  
**Review:** `8f0b3189123049c59146a9b35a5d6409`  
**Severity:** P1, blocking acceptance of the isolated canonical ledger.  
**Evidence type:** Independently executed disposable bare-Git reproduction **and** source-level transition trace. No frozen source modified, no deployment or GitHub Actions.

## Reproducer and observed result

Using the exact frozen .NET 11 Release `JarvisClient.dll` and `Common.dll`, create a disposable bare Git remote and checkout, initialize `LedgerStore` and the WQ-02 project, then publish an otherwise ordinary, initially Available Planning stage with `QueueId = null`, `Dependencies = Set.empty`, `StageGeneration = 1`, `ClaimGeneration = 0`, `Gate = Clear`, `Domain = Undecided`, and no push/waiver/lineage.

Run `WorkQueueGitLedger.execute(binding, "Reviewer 2", freshOperationId, 0, PublishStage stage)`, followed by `WorkQueueGitLedger.load(binding)`. The independently executed script printed exactly:

```text
PUBLISH_SUCCEEDED
LOAD_FAILED: LedgerIntegrityError
  "Malformed canonical work ledger: Unexpected null in canonical work record."
```

The process exited 0 after recording both results and destroyed its disposable Git repository. This is not a speculative historical Git-state rewrite: the documented, exposed `PublishStage` mutation itself successfully committed an unreadable record.

## Causal source trace

- `Client/Modules/WorkQueueGitLedger.fs:32-33,56-61`: `CanonicalCodec.encodeType` permits a null string to become a JSON-null node during record serialization; `JsonObject.Add` supports the null value.
- `Client/Modules/WorkQueueGitLedger.fs:74-76`: the matching decoder categorically rejects any null node with `Unexpected null in canonical work record.`
- `Client/Modules/WorkQueueGitLedger.fs:303-311,398-402`: `validInitialStage` and the `PublishStage` branch do not validate `QueueId` or any other required nested string; normal initial stage fields pass.
- `Server/WorkQueueContracts.fs:546-572`: `publishStage` accepts the stage when no duplicate/writer conflict exists.
- `Client/Modules/WorkQueueGitLedger.fs:258-285,485-492`: `structurallyCoherent` checks IDs, ownership, dependencies and waiver emptiness, but not nonnull `QueueId`; `change` commits `content binding changed` without checking that the resulting JSON is decodable.
- `Client/Modules/WorkQueueGitLedger.fs:328-342`: the next `readPinned` uses `CanonicalCodec.decode` and fails before any domain mutation can proceed.

Other string-carrying records, such as a passive Task message with a null subject identifier or body, warrant the same negative-space validation; this reproduction proves the Planning-stage path.

## Impact

One successful, otherwise version-correct mutation can make the authoritative project record unreadable. Subsequent `load`/mutations fail pending manual repair. Git CAS, immutable operation receipts and state-hash consistency do not protect against emitting an invalid schema representation in the first place. The existing 18 focused tests exercise malformed **historical** records, but do not prevent this legal API publication from poisoning the ledger.

## Required bounded correction

1. Before publication, reject null or structurally invalid required string/collection fields throughout nested work and message payloads; keep validation close to canonical encoding rather than only adding an ad-hoc `QueueId` check.
2. In the transaction planner, do not emit a new project record until the exact canonical content has successfully round-tripped through the decoder and state projection validation. Convert malformed candidate input into a structured rejected transition without persisting a partial or unreadable version.
3. Add two-clone tests for malformed `PublishStage` and `PostMessage`, asserting rejected result, unchanged verified remote/project version and receipts, followed by successful legitimate publication/claim. Preserve exact replay semantics.
4. Rerun .NET 11 Release focused and inherited tests and request a **new frozen review**. Do not expand this narrow fix into live account-global admission, migration, deployment or scheduler activation.

**Conclusion:** **ChangesRequested** on this exact frozen source due to the reproduced P1 integrity break, notwithstanding closure of the four prior findings.
