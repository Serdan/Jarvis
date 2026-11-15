# P1 — First-write pending-journal ancestors are not crash-durable before remote fetch

**Frozen review:** `7a89d6b374924f85a1cdcd07d7ed06ea`; exact source `d7ceafb1692a13f916d36c6593c3e40a72a9c226` (base `7b589173135178a634495146675dad5db647e5f8`).

**Invariant:** Every public Task/Review write must have a recoverable original UUID and immutable command before its first remote fetch, including a first-ever write on a freshly approved local binding. A successful fsync of a file and its containing directory is insufficient if that containing directory was itself newly linked into an unsynced parent directory.

**Source evidence (frozen SHA):**
- `Client/Modules/OrchestrationApi.fs:53-63`: `LedgerBindingConfig.pathForRoot` locates the approved binding in `<config>/ledger-v2/<hash>.json`.
- `Client/Modules/OrchestrationApi.fs:633-636`: `journalDirectory` is `<config>/ledger-v2/pending/<hash>`.
- `Client/Modules/OrchestrationApi.fs:648-660`: `withIntentLock` calls `Directory.CreateDirectory(journal)` (potentially creating both `pending` and `<hash>`) and creates `.locks`; it does **not** fsync `ledger-v2` or `pending` after creating those entries.
- `Client/Modules/OrchestrationApi.fs:842-871`: `persistUnsafe` flushes the temporary intent file, renames it, and `durableArchiveMove` syncs only the leaf `journal` directory (`:605-632`). No ancestor-directory synchronization occurs before `executeLedgerWrite` runs remote binding verification and Git transaction (`:1960-2000`, `:2580-2720`).

**Counterexample:** Begin with a valid approved binding but no `ledger-v2/pending` tree, as on a first public write. The client creates `pending/<hash>`, writes and fsyncs the command file, renames `U.json`, and fsyncs only `pending/<hash>`. It then proceeds to Git remote I/O. A power loss before the kernel persists the creation/link of `pending` in `ledger-v2` or `<hash>` in `pending` can make the entire journal path unreachable after restart, despite the leaf fsync having succeeded. Thus the caller's original U+command may be lost precisely during an uncertain remote operation. On POSIX filesystems, fsyncing a child directory does not by itself guarantee persistence of its newly created entry in the parent directory.

**Evidence boundary:** This is a source-backed crash-durability gap in the fresh-directory path, **not** a claim that the 11 passing fault-injection tests reproduced a real power loss or that an observed production incident is attributable to it. The newly added post-rename leaf-fsync test correctly proves its narrower guarantee, but does not cover ancestor creation.

**Narrow correction:** Before starting remote Git I/O on a first write, durably sync the parent entries for any newly created `pending` and `<hash>` directories (or conservatively sync the parent chain under the existing approved binding directory). Fail closed if any required directory fsync fails, leaving the original UUID+command where possible for recovery. Add deterministic first-write fault injection at the ancestor-sync boundary: asserted zero remote fetch/push, preserved original identity/hash, and successful later same-UUID recovery/replay. No WQ-02 changes or generalized offline database.

**Verdict impact:** **P1 blocker — RequestChanges** until first-write journal ancestry is durably established.