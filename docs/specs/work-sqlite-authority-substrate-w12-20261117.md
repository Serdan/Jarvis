# SQLite Work authority substrate (Worker 12, 2026-11-17)

Implementation lane B of docs/specs/work-stateful-implementation-alignment-2026-11-17.md.
Source baseline: G0 origin/main 7712bc547c264873036975e30194da03a05cc0ba.

## Deliberately disabled

The new Server.WorkSqliteAuthority class is not wired into Program.fs, RPCs, the Scheduler or production configuration. Constructing it has no filesystem side effect; Initialize() must be called explicitly on an approved path. All tests use disposable per-test databases. The existing FeedbackStore remains untouched. This changes no live Git-ledger authority or migration state.

## Transaction boundary

One server SQLite file, WAL with synchronous FULL, owns Work records and account-global capacity in one transaction domain. Typed WorkSqliteTransactionCommand carries create-only or expected-version record updates, account capacity deltas, and read-only cross-record CAS guards. Guards pin graph, stage, claim, STOP, role and exact source SHA from related records before any mutation is committed. This is a storage substrate, not a model-exposed permission API; future authenticated domain reducers must enforce lifecycle legality and supply their own source/review/integration proof checks.

Each successful novel command writes all project changes and capacity, a monotonically committed sequence, an immutable SHA-256 operation receipt, and an audit row under a single SQLite transaction. Failed commands roll back every write and consume no sequence. A replay of the identical operation (including its guards) returns the original receipt *before* checking now-stale versions. Operation-ID reuse with a different canonical payload fails closed. Capacity has a bounded check constraint. Database identity and schema version refuse accidental use of the separate legacy telemetry database or a future incompatible schema.

Schema rows carry explicit provenance fields for source/review/integration SHA, graph/role versions, stage/claim/STOP generation, and unresolved external effect state. A raw body JSON object is intentionally internal and must never contain plaintext bearer credentials; the future domain must store only scheduled credential verifiers. Ephemeral manual grants must remain in server memory, not this database.

## Boundaries still outstanding

This lane is not the complete Work transition reducer or claim authorization surface. No scheduler, import, JSON backup/restore, Recovery, source-effect reconciler, automatic capacity release, or operator grant persistence is implemented. In particular, a transaction guard proves the values it was explicitly given, not that an arbitrary caller was authorized to choose those values. The future server command facade must enforce project/role/source/permission checks and required guard sets. This code cannot be used to authorize live Work without subsequent lanes and explicit operator GO.

## Tests

Server.Tests/WorkSqliteAuthorityTests.fs includes real .NET 11 Release fixtures for fresh creation and restart, WAL-backed persistence, replay and collision, rollback after a partial mutation, competing connections with one CAS winner, two project IDs sharing capacity in one commit, account-overcommit rollback, foreign database/future-schema refusal, invalid source/JSON, and independent graph/source/role/STOP/generation guards. Run dotnet test Server.Tests/Server.Tests.fsproj -c Release locally; no hosted Actions.
