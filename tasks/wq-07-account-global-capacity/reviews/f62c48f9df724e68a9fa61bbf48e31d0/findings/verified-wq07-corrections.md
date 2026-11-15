# Independent WQ-07 correction verification — resolved prior blockers

**Review:** `f62c48f9df724e68a9fa61bbf48e31d0`  
**Exact frozen commit:** `eb1ec7c8360f99029728cea56ebf8bb78b3c0499`  
**Prior ChangesRequested:** `5247f0661c3940b3b9ea4b8879d2e9bf` at `b7aadb7b1490220ba10310f121c8eaca5e9fe969`.  
**Verdict impact:** Both prior findings are resolved within the inert, implementation-only scope. No new blocking source finding.

## P1 — Original project-ledger authority binding: RESOLVED

The previous cross-ledger substitution used a genuine C01 witness from another ledger with the *same project GUID* to free an account token still held for the first ledger.

- `Client/Modules/AccountCapacityLedger.fs` now defines `ProjectLedgerAuthority` and stores immutable `ProjectLedgerId`, `ProjectLedgerEpoch` and `ProjectAuthoritySha256` on `AccountToken`. The digest includes project GUID, ledger ID, epoch, and approved fetch/push URLs; raw remote URLs are not stored in account state.
- `reserve` binds those fields in the original canonical operation/request hash, so same-operation-UUID replay with a changed ledger or remote is rejected.
- `Client/Modules/AccountAdmissionAdapter.fs:223-228` centralizes `tokenOwns` and applies it before project admission, C01 abort/recovery, and terminal release. Verified terminal receipts carry the ledger identity/epoch/digest from the read project binding; `AccountCapacityLedger.release` must match these against the original held token.
- `Client.Tests/AccountAdmissionAdapterTests.fs` `SameGuidForeignTerminalCannotReleaseOriginalLedgerToken` creates a second **real** Git remote with the same project GUID and a different ledger/remote. Its C01 witness successfully verifies *within that foreign ledger*, but release/abort/replay cannot release the first account token; a second project cannot overcommit cap one. A changed approved endpoint is also rejected on original-UUID replay.
- A verified original project binding remains an essential integration precondition; the digest freezes the binding supplied at reservation, not an authorization for a live Scheduler to accept arbitrary caller-provided project bindings. Live activation is outside this review.

## P2 — Post-publication lost-acknowledgment regression: RESOLVED

- `Client/Modules/WorkQueueGitLedger.fs` has an internal test seam that drops the response **after** a successful project CAS publication. It returns `OutcomeUnknown`; it does not change production scheduler wiring.
- `PublishedProjectAdmissionLostAckKeepsOneTokenAndReplaysOriginalWinner` demonstrates a committed original `AdmitProject` despite the lost reply, confirms one immutable project admission/one global token, and proves same-UUID replay from the second clone without overcommit.
- `PublishedC01LostAckRetainsTokenUntilOriginalAbortProofThenReleasesOnce` demonstrates an authentic committed C01 with lost response, token retention during uncertainty, subsequent exact witness verification, and idempotent same-release-UUID replay.
- `TerminalAccountReleaseLostAckReplaysImmutableReceiptWithoutDoubleFree` uses the account Git transport fault seam (successful push with missing acknowledgement plus failed immediate fetch), reads the original immutable release receipt from the second clone, and proves replay returns the same receipt; a new release UUID cannot free again.
- Existing account-capacity/resize and WQ-02 project-ledger tests are included in the independently executed Release suite.

## Independent execution evidence

- Exact checked-out HEAD `eb1ec7c8360f99029728cea56ebf8bb78b3c0499`; direct parent `b7aadb7b1490220ba10310f121c8eaca5e9fe969`; WQ-07 baseline `033d6384a6e706c8262a18c26c4f43c8158462fe` is an ancestor.
- `git diff --check b7aadb7b eb1ec7c8`: exit 0; post-test `git status --porcelain=v1`: empty.
- `dotnet --version`: `11.0.100`.
- Independent local command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~AccountAdmissionAdapterTests|FullyQualifiedName~AccountCapacityLedgerTests|FullyQualifiedName~WorkQueueGitLedgerTests' --logger 'console;verbosity=minimal'`.
- **Exit 0, 47 passed / 0 failed / 0 skipped**; Common, Client, and Client.Tests built for `net11.0` Release. Existing unrelated compiler warnings FS0760 in `Client/Program.fs` and FS3369 in `Client.Tests/BrowserSchedulesTests.fs`.
- `git grep` in frozen Client source finds `AccountCapacityLedger.reserve` and `AccountCapacityLedger.release` called only from `AccountAdmissionAdapter`; no production Scheduler call site for this implementation. No hosted CI, main push, live tokens, deployment, migration, or Codex.

**Conclusion:** Original P1 safety boundary and P2 lost-ack coverage are supported by inspected source plus real local two-clone Release regressions. Accept the exact frozen commit as inert reviewed code only. Any later main integration/rebase must retain exact reviewed ancestry or obtain a fresh frozen combined-source review; live Scheduler/account activation requires separate operator authorization.