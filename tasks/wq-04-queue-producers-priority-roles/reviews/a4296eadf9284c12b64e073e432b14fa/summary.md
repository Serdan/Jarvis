# Reviewer 1 — WQ-04 explicit receipt producer: Accept

**Frozen source:** `65708dbf7c7218794c5a39cfb20e002fa916950f` based on `289e32754b6e2bd59b157d51c3fd79a16e881e98`. 5 files/632 added lines; central code `Client/Modules/WorkQueueReceiptProducer.fs`, explicit CLI branch in `Client/Program.fs`, four new two-clone NUnit tests plus documentation.

## Independent verification

- Inspected full receipt producer, original receipt path/type/entity resolution, exact-UUID replay handling, selected `WorkQueueCanonicalSourceEvidence` and `WorkQueueCanonicalCasBinder.publishPractical` verification, CLI isolation, and documentation.
- `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore`: **pass**, 0 errors, 3 existing F# warnings.
- `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter FullyQualifiedName~WorkQueueCanonicalCasBinderTests --timeout-seconds 180`: **11/11 pass**, 0 failed/skipped; test scratch environment removed.
- The four new scenarios include original CreateTask receipt -> project work -> fixture-only claim/complete; original frozen review -> dependent blocked integration; late already-claimed review visibly NeedsAttention, not runnable backfill; simultaneous competing two-clone producer attempts with one published stage. Existing focused negatives also confirm fabricated events/role claims cannot authorize the legacy protected route.

## Acceptance boundary

The explicit `queue-producer` subcommand resolves its original CreateTask / SubmitBranchReview / SubmitIntegration operation from a verified ledger head and derives the event/selector and original provenance; it does not accept caller-chosen source fields. Its separate `produce` operation uses normal project Git CAS, is replayable by original producer UUID after the head advances, and reports recoverable conflict/unknown outcomes. Preview, receipt inspection, origin and demand are inspectable; command is not part of normal client or non-agent Scheduler startup.

**This is Accepted only as a default-off, project-only producer entrypoint.** Claim/complete in the new tests use an explicitly **inert WQ02 fixture**, not a real host launch. It does not authorize or prove account credit reservation, live agent claim, source-effect completion, scheduler dispatch, terminal review integration, automatic migration or deployment. Reconciliation of late claimed reviews remains a manual receipt/backup-SHA/diff/repair procedure; no automatic backfill is claimed. This is consistent with the current bounded operator policy, which favors visible recoverability over speculative global proof requirements.

**Verdict: Accept** frozen change for integration review by Lead; production activation requires separate operator approval and missing live-host evidence.
