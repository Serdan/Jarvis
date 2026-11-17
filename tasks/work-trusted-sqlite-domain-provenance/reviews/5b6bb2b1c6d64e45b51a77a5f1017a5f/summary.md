# Independent Reviewer 4 decision — ChangesRequested

**Review request:** `5b6bb2b1c6d64e45b51a77a5f1017a5f` for `work-trusted-sqlite-domain-provenance`.
**Frozen source:** `60df897dba87b42440420b69cbdcd0c7f1706360`; exact base `51b67bf1d5ba7e16512b915f8004456440b3489c`.
**Scope:** B physical v2 original-source provenance, explicit migration and evidence only. This is not an approval for authenticated eight-kind ingress, original Git verification, F1 backup/inverse, G1b Recovery, live migration, deployment or cutover.

## Independently verified corrections

1. The previous source-bearing → source-less update bug is corrected: the trusted-schema CAS update explicitly sets all four extra provenance columns to NULL together with `source_sha`; a focused regression tests restart, five NULLs and exact receipt replay.
2. Physical layout validation checks exact named/type/NOT NULL/PK columns for all B tables, declared indexes, expected metadata and key constraint fragments. The prior same-count/wrong-name and dropped-index cases now fail in tests.
3. The exact frozen `git diff --check 51b67bf... 60df897...` exits **0**. I independently ran .NET 11 Release `WorkSqliteDomainTests`: **9/9 pass**, and complete `Server.Tests` Release: **497/497 pass**, both 0 failed/skipped. Working-tree head matches the frozen review SHA and is clean.

## New blocking finding

See [`findings/schema-constraint-comment-bypass.md`]. `physicalSchemaMatches` treats the **presence of CHECK/UNIQUE/REFERENCES fragments in raw `sqlite_master.sql`** as evidence that the constraints are enforced. An in-memory SQLite reproduction starting from this branch's exact v1 DDL replaced `CHECK(json_valid(body_json))` with a block comment containing the same text. The new substring check still finds the fragment; columns/indexes/meta remain identical; SQLite then *accepts* `body_json='not json'`. By the frozen F# control flow, `Initialize` and explicit v1→v2 preflight would wrongly accept this corrupt/foreign physical schema. This undermines the declared fail-closed database identity guarantee. Validate actual constraints (e.g. exact whole canonical DDL, not substrings), and add v1/v2 negative fixtures.

**Verdict:** ChangesRequested on the exact frozen physical B slice. No independent implementation acceptance, F1/G1b promotion, or operator cutover authorization is inferred. Preserve other agents' file ownership and historical/default-off Git authority.