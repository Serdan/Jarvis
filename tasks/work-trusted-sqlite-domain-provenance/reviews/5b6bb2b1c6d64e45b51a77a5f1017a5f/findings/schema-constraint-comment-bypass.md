# F1 — Commented-out SQLite CHECK constraint passes physical schema validation (blocking)

**Frozen review:** `60df897dba87b42440420b69cbdcd0c7f1706360` against `51b67bf1d5ba7e16512b915f8004456440b3489c`.

**Source:** `Server/WorkSqliteAuthority.fs`, `WorkSqliteInternals.physicalSchemaMatches` (the new `storedDdl`/`requires` logic, approximately lines 478–531), called from both `Initialize` and `InitializeProvenanceV2`.

## Independently reproduced

The new constraint validation does **not** verify that the required CHECK/UNIQUE/reference expression actually constrains a SQLite table. It retrieves raw `sqlite_master.sql`, removes whitespace/lowercases, and checks whether that string **contains** an expected fragment. SQLite preserves SQL comments in the DDL text.

Using the branch's own exact v1 `let create` DDL in a disposable in-memory Python `sqlite3` connection, I made exactly this change:

```sql
body_json TEXT NOT NULL /* CHECK(json_valid(body_json)) */,
```

All table columns, their types, nullability and PK positions, all indexes, application ID `1247236683`, `user_version=1`, `work_meta.schema_version=1`, and every *other* constraint remain unchanged.

Observed from a real SQLite execution:

```
stored_DDL_passes_fragment_check= True
meta_schema= 1
app_id= 1247236683
user_version= 1
invalid_body_JSON_inserted= not json
```

The malformed DDL still contains `CHECK(json_valid(body_json))` literally (inside a block comment); thus `requires "work_records" [ "CHECK(json_valid(body_json))" ]` passes. All other `physicalSchemaMatches` checks also match this deliberately minimal modification, so the new initializer/upgrade validation **would report the weakened v1 layout as healthy**. The database demonstrably accepts an invalid `body_json` payload, which the required CHECK was supposed to prohibit. This creates a false-positive foreign/corrupt authority schema and can propagate through explicit v1→v2 upgrade.

This is separate from the previously reported same-count/wrong-name and missing-index cases. The green nine focused tests do not exercise commented-out or otherwise non-enforcing critical DDL fragments.

## Required correction

Make the physical constraint validation structural rather than fragment-presence-only, e.g., normalize and compare the **complete expected canonical table DDL** for each version (including the exact post-`ALTER TABLE` v2 form), or use an equivalently strict mechanism that cannot be satisfied by comments or unrelated SQL literals. Reject the modified schema with `ForeignDatabase` on `Initialize` and on `InitializeProvenanceV2` without changing version, sequence or receipts. Add negative v1 and v2 tests demonstrating that a DDL fragment in a comment does not count as an enforced constraint.

Do not treat an arbitrary source tuple or this physical-schema correction as authenticated eight-kind ingress, a passed F1 backup/inverse or a G1b Recovery/cutover authorization.