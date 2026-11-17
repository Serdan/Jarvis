# F1 — SQL-literal case folding allows a noncanonical SQLite constraint through schema identity (blocking)

**Frozen source:** `a064165c57b5ec8cf15e71a70cd9ec6b35e48fca`, exact base `51b67bf1d5ba7e16512b915f8004456440b3489c`. Review `7cb92fd97b61432d8fae482b70c0f15e` (Reviewer 2). **Scope:** the partial B v2 physical provenance/schema slice only; no authenticated eight-kind ingress, F1/G1b or cutover approval.

## Cause

`Server/WorkSqliteAuthority.fs:493-495` defines `compactSchemaSql` as removal of **all** Unicode whitespace followed by `ToLowerInvariant()` on the **entire** persisted `sqlite_master.sql` string. `schemaCatalog` applies it to every schema definition (497-507). `expectedCatalogs` constructs canonical v1/v2 catalogs from `initialSchemaSql` and `provenanceV2UpgradeSql` in real SQLite (511-517), and `physicalSchemaMatches` accepts `schemaCatalog connection = canonical` (579-589). Neither lowercasing nor whitespace deletion is semantics-preserving **inside SQLite string literals**; such literals are case-sensitive with default BINARY collation.

The canonical `work_records` DDL contains `CHECK(external_effect IN ('none','prepared','unknown','definitive-no-effect','observed'))` (around line 457). A foreign layout replacing just `'unknown'` with `'UNKNOWN'` leaves table/index names, column order/type/NOT NULL/PK, `work_meta`, application ID and both version pragmas untouched. Because the whole DDL is lowercased before catalog comparison, the canonical and modified catalogs compare **equal**, although the actual constraints are different.

## Independent execution — disposable in-memory SQLite, exact branch source DDL

I extracted the literal `initialSchemaSql` and `provenanceV2UpgradeSql` from the frozen F# source and executed both in Python's actual `sqlite3` engine, with a canonical connection and another whose sole DDL alteration is `'unknown'` -> `'UNKNOWN'`. I replicated the F# catalog transform (`whitespace removal + lowercase`) on each actual `sqlite_master` catalog.

```
version=1: catalog_equal=True; app=1247236683; user=1; meta=1
  effect=unknown: canonical=allowed, altered=rejected
  effect=UNKNOWN: canonical=rejected, altered=allowed
version=2: catalog_equal=True; app=1247236683; user=2; meta=2
  effect=unknown: canonical=allowed, altered=rejected
  effect=UNKNOWN: canonical=rejected, altered=allowed
```

The named columns and indexes are identical because only a quoted CHECK literal changed. The code's `physicalSchemaMatches` therefore cannot distinguish this foreign/corrupt authority in either v1 or v2. It would return true from both `Initialize()` and `InitializeProvenanceV2()` on the altered version, despite changed admissible effect values. This is a control-flow conclusion from frozen F# source plus actual SQLite reproduction, not a claim that a new F# negative test has been run.

The prior commented-out CHECK bypass was genuinely corrected, and the existing two negative tests exercise it. This is a **separate** literal-normalization bypass surviving the new catalog comparison. Removing whitespace inside a quoted literal could likewise disguise certain changes.

## Required correction

Normalize SQL only where semantics allow it (outside quoted literals/identifiers), or compare exact whole SQLite-generated canonical DDL/catalog representations without global lowercasing/whitespace stripping. Preserve the real canonical v1/v2 source-of-truth and all named column/index/meta checks. Add v1 and v2 **RED-first** negative fixtures whose `'unknown'` CHECK literal is case-altered while names/columns/indexes/meta remain canonical; assert real SQLite accepts `'UNKNOWN'` and rejects `'unknown'`, and assert `Initialize()` / `InitializeProvenanceV2()` reject the layout as `ForeignDatabase` **without** advancing schema version, `last_sequence`, or changing existing receipts. Retain the previous comment-weakening regression cases.

No issue is asserted with the source provenance clearing/replay correction itself. Independently observed focused .NET 11 Release test result on exact frozen worktree: 11 passed, 0 failed, 0 skipped. Frozen `git diff --check` exit 0. Those passing tests do not cover this distinct CHECK-literal change.

**Disposition:** ChangesRequested for this physical schema acceptance boundary. Do not infer full trusted ingress, F1 inverse, Recovery or live cutover approval.