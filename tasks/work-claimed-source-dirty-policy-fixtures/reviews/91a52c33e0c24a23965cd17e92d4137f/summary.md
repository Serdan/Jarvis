# Checkout-integrity / UUIDv4 compatibility merged-source re-review

## Frozen submission
Exact frozen `0c505355c9be0714c2c9eb136cb02e5ab2f4bf0b`, published at `refs/heads/fix/wq11-claim-checkout-uuidv4-merge-20261117`, base `d356d14c46eef89af4522c05411f25dfd6c10065`. The source includes originally Accepted checkout reincarnation `d8a2f1f23dd867732f5f3c534a7b78309ae1ebd5` and integrated UUIDv4 credential `cc4b35976b704339e2ee61ee01159aa8c9c9a26d`. The original merged Client Release run had rejected canonical 36-character UUIDv4 credentials via a stale 43-character-only local gate; this frozen fix replaces that gate with `WorkCredentialFormat.isSupportedBearer` and adds a UUIDv4 positive fixture assertion.

## Reviewer 1 independent verification (2026-11-17)
- Confirmed checked-out `HEAD` and read-only `git ls-remote origin` both equal the exact frozen SHA; verified both Accepted WQ11 checkout commit and integrated UUIDv4 commit are ancestors. Inspected the frozen two-file patch and surrounding credential validation, reservation, grant, source and journal boundaries.
- The new shared format gate permits canonical lowercase RFC 4122 UUIDv4 and historical 43-character Base64URL values. Both require the original launch-scoped Git SHA-256 verifier and `CryptographicOperations.FixedTimeEquals`. The original launch/registered lease, `StopRequested`, claim generation, grant revision, physical checkout, Git administration/common/object store, immutable checkout-instance marker, source ref/HEAD and host FileEdit journal checks remain enforced. No live Scheduler or production source mutation was used.
- Independently ran `dotnet 11.0.100` Release tests against the exact frozen checkout (no hosted Actions):
  - `Client.Tests/Client.Tests.fsproj -c Release --no-restore`, filtered `WorkQueueClaimBoundDirtySourcePolicyTests|WorkQueueCredentialTests|WorkQueueReservationPermissionsTests|WorkQueueNativeCredentialDispatchAdapterTests`: **28/28 passed**, including canonical UUIDv4 positive, prior WQ02 Base64URL hash verifier, wrong-credential/cross-launch, two-clone original CAS, STOP, same-clone same-path reincarnation, foreign clone replacement, dirty file evidence/uncertain journal, and default-off behavior.
  - Same Release test assembly `--no-build`, filtered `WorkQueueCanonicalSourceEvidenceTests|SourceEffectHostBindingTests|WorkQueueGitLedgerTests`: **45/45 passed**, including stale clone, original UUID replay, source-effect trust boundary, STOP and Git CAS negatives.
  - Total independently executed: **73/73 passed**.
- `git diff --check d356d14..0c505355` returned exit 0; `git status --porcelain=v1` empty; no source edits or integrations.

**Reviewer disposition:** Accepted. No blocking findings observed in the reviewed change and regression scope. This is not a claim that the entire repository test suite was run.
