# Reviewer 5 — frozen C2 offline contract review

**Verdict: Accepted for the bounded, default-off and disposable-fixture source slice only.**

**Frozen source:** `528b72659a6865aa1b8bb47875a2c4bbb4fdd4a9` against `e86e0133886f4f51b703a76167384071245e103c`. The reviewed delta adds `Server/WorkLegacyImportFence.fs` and dedicated fixture tests, wires them into both F# projects, and extends `WorkLegacyImportSqlite` so the original-fence audit is in the same SQLite transaction as the imported records and capacity.

**Independent evidence:** on the exact frozen checkout, `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ServerWorkLegacyImportFenceTests --verbosity quiet` passed **10/10**; unfiltered `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --verbosity quiet` passed **412/412**, no failures. The source and tests cover preflight/identity/approval rejection before CAS, old writer admission quiescence before the first CAS, changed/forked heads, forged or unknown receipts, partial CAS failures, preserved fence on rollback, exact replay, and atomic account/project/audit persistence.

**Crucial limit of this decision:** This is an **unhosted interface and hermetic gateway double**, not a production source-authenticated original-Git writer gate. `IWorkLegacyOriginalGitFenceGateway` requires durable exclusive admission, independent original remote/Git trust verification, real all-client protocol/pending-intent/source-effect witnesses, and recovery across restart; this patch does not supply or deploy that implementation. Booleans and witness hashes inside fixture receipts are not themselves proof that a real remote writer or other devices have stopped. The existing original Git ledgers remain authoritative; this review is **not a cutover GO**, an authorization to deploy/restart/enable SQLite, or a claim of live all-client quiescence.

Before a later live-adapter/cutover review, require a real independently authenticated original-writer barrier and persisted witness covering every client and unknown effect, a tested restart and concurrent cross-device scenario, and explicit operator authorization. The current C2 approval schema assumes all included project bindings belong to one `Approval.DeviceId`; separately verify or extend that design if migration must span multiple devices.

No blocking defect found **within the deliberately limited offline fixture slice**. The broader canonical importer/writer-fence task remains incomplete pending live authority and rollout gates.
