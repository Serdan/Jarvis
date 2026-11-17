# Independent Reviewer 4 decision — ChangesRequested

**Request:** `b27b2c5fd7f448cf978cf52aa7f1a640` for `work-trusted-sqlite-domain-provenance`  
**Frozen commit:** `69987d0a8b9364b1cfdb4e222f51e90d24dd69ae` against `51b67bf1d5ba7e16512b915f8004456440b3489c`.  
**Review boundary:** isolated B v2 physical original-source provenance schema, migration and regression evidence only. **No** approval for authenticated eight-kind ingress, F1 full-domain backup/inverse, G1b Recovery, live migration, deployment or Work cutover.

## What passes

- Explicit opt-in v1 -> v2 upgrade; original repository ID, full source ref and verification receipt/evidence columns; legacy v1 source SHA is not promoted to verified original origin.
- V2 raw `Commit` is fenced; `CommitProvenanced` includes provenance in the exact operation fingerprint and retains existing sequence/receipt/CAS/capacity transaction structure. New parser rejects duplicate root and nested body JSON properties but does **not** purport to be a closed typed body grammar.
- I independently ran `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~WorkSqliteDomainTests`: **5 passed, 0 failed, 0 skipped**. I also ran the complete `Server.Tests` Release assembly with `--no-restore --no-build`: **493 passed, 0 failed, 0 skipped**. These are test results, not evidence that missing adversarial cases are safe.

## Changes required in this frozen slice

1. **Blocking:** [`findings/source-transition.md`] — permitted source-bearing -> source-less v2 UPDATE clears `source_sha` but leaves four stale provenance columns. The write and receipt commit; subsequent physical read rejects the now-inconsistent row. Either forbid the transition atomically or clear all related columns, with a restart/replay regression.
2. **Blocking:** [`findings/schema-shape.md`] — schema v2 initialization trusts `user_version`, `work_meta` and the number of columns, not the identity of required physical columns. A same-count fabricated/malformed v2 layout returns `Ok 2`. Validate exact expected named physical layout and add negative upgrade/restart tests.
3. **Minor required evidence correction:** [`findings/diff-check.md`] — `git diff --check 51b67bf... 69987d0...` returns **exit 2** for trailing whitespace at evidence-note lines 3–5, contrary to the submitted summary's `diff --check clean` claim.

These findings are independently derived from the frozen source and commands, not a request to execute or integrate the unfinished full Work architecture.

## Explicit remaining HOLD

Even after this physical-slice correction, the original C1/E0/G1 provenance producer and closed eight-kind authentication/body parser, bearer-safe verifier boundary, F1 128-vs-256 ID admission parity, complete two-project JSON round trip, and G1b stale-restore fencing remain **separate blockers**. Do not infer a verified original Git source from a tuple alone, bind a live writer, export an untrusted database as authoritative, or enable Scheduler/cutover.