# P1 — Missing SQLite journal silently becomes a fresh replay domain

**Verdict impact:** ChangesRequested for frozen source `f85c288d45981aef5502ce953f7e135e2fd9e965`. The independently Accepted inert parent `e7a8a3ed` is not being rejudged; the new routed host file-edit path now exposes this pre-existing journal weakness.

## Exact evidence

- `Client/Modules/SourceEffectHostBinding.fs:125-146` reaches `SourceEffectJournal.openAtHostRoot` immediately before accepting the real opt-in WriteFile/PatchFile command.
- `Client/Modules/SourceEffectJournal.fs:215-258`: `openAtHostRoot` requires an existing directory, but if `source-effects-v1.sqlite` is absent, `firstCreation = not (File.Exists dbPath)` leads to `SqliteOpenMode.ReadWriteCreate`, creates empty `effects` and `fences` tables, then returns `Ok store`. There is no independent durable enrollment/continuity marker establishing that the root has *never* held a prior journal.
- `Client/Modules/SourceEffectJournal.fs:292-321`: `beginBeforeDispatch` queries solely the local `effects` table by original UUID. No row means `FirstDispatch`. `runAwaitedHostFileEdit` at lines 484+ executes its callback for `FirstDispatch`; it only denies `AlreadyRecorded`.

**Failure sequence:** client A commits Prepared for original UUID U and may perform a file edit; host crashes/restarts or the database disappears. With the same configured, still-existing root, client B invokes the *same* original UUID U and same request. The opener silently creates a new empty database; U is absent, so `FirstDispatch` executes the file edit again. Repointing the opt-in journal root at another existing but uninitialized directory has the same reset property. Even unchanged UUID/request/scope cannot prevent the second effect. This contradicts the stated missing-journal RecoveryBlocked and original-UUID crash/reconnect invariant.

This is a code-path proof, **not** a claim that a disposable deletion/replay experiment was run: Jarvis denied the attempted shell fixture with `PermissionDenied: Shell executable is denied by default: bash`. Existing passing tests reopen the *preserved* same SQLite database; none cover database loss between two invocations.

## Required correction and independent tests

1. Separate explicit, trusted first-time journal provisioning from operational `openExistingForDispatch`; the dispatch path MUST reject a missing or replaced database as RecoveryBlocked, not auto-create. Bind approved journal continuity/root identity to a durable host trust anchor so a root switch cannot reset replay identity. Never infer pristine initialization from `File.Exists = false` during a source command.
2. Add an exact two-client test: A records U and causes an effect or uncertainty; close all handles; remove/replace the SQLite database or select a blank configured root; B retries identical U and payload; **no callback executes**, journal reports RecoveryBlocked. Include process restart, intact-database replay control, malformed/corrupt schema, and changed payload/scope.
3. Preserve the existing sticky RecoveryBlocked / default-off gates and do not enable YieldSource, Scheduler, migration or source succession as a workaround.

## Nonblocking scope reminder

`bindCanonicalClaim` chooses the matching claim from the canonical snapshot based on a caller-selected checkout and checks the registered launch is nonempty; it does not authenticate the incoming caller/session *to* that launch. This prototype must not be promoted to authoritative agent-origin/source-succession evidence without an independent admission/actor binding. Host WIP ref immutability, true original-UUID upstream admission and process/push/out-of-band coverage remain unproven by design.
