# Independent review — Agent retirement integration preflight

**Verdict recommendation: Accepted for the strictly default-off preflight only.** Frozen review SHA `fa30f581c0a71880fa1a976b64a0f91740539957`. Parent 1: current integration baseline `117ec8630ce3071b532e2c889e3cdc708dc1a46a`; parent 2: previously reviewed preflight `9f0c3335772fa931a61a9285946b07c26d068c26`.

## Source and ancestry
- Verified exactly two parents and that both the accepted retirement source and accepted legacy-writer-fence merge `b10d836c7cdf233aff309d202fa6e8d27149a4b1` are ancestors.
- The retirement module, retirement test fixture and preflight document are byte-identical between accepted parent 2 and this frozen merge (`git diff --exit-code` returned 0). The main-side delta contains only six paths: two F# project compile includes, the new module and fixture, the `LedgerStore.fs` bounded inspection helper and its preflight document. `git diff --check 117ec863..fa30f581` returned 0.
- The merged branch was clean. No source deletion, public Work dispatch, Scheduler enablement, Agent retirement or provider transport changes are introduced by this slice.

## Semantic review
- `WorkQueuePersistentAgentRetirement.inspect` fetches one verified project Git-head, then reads every blob at that exact commit; the manifest retains original path and blob OID, including legacy agents, task history, ops receipts, role/instruction records, notifications, challenges, source records and unknown record families. Its deterministic path/OID digest and capped manifest are audit evidence, not a rewritten principal.
- `LedgerStore.listPinnedLedgerBlobsAt` fails closed for malformed Git entry, unsupported mode/non-blob, unsafe path, duplicate path and size/count bounds. Inspected the subprocess wrapper: Git stdout uses `MaxOutputBytes=None`, so its result is not silently pre-truncated before those bounds are checked.
- WQ02 Work and role authority are read from the same pinned head. Work inspection is read-only; claim buttons cannot be enabled and no Agent roster/registration is consulted as an authority source. Missing Work coverage remains explicit UNKNOWN.
- `CutoverAuthorized=false` is hard-coded; the output explicitly disclaims fleet-wide fencing, WQ10 migration reconciliation, live private-host execution/STOP and operator cutover authorization.

## Independent local test evidence
- .NET 11 Release `Client.Tests/Client.Tests.fsproj` focused `WorkQueuePersistentAgentRetirementTests|WorkQueueToolFacadeTests|LegacyWriterProtocolFenceTests`: **17 passed, 0 failed, 0 skipped**.
- .NET 11 Release `Server.Tests/Server.Tests.fsproj`: initial `--no-restore` invocation was a **no-op** because Server.Tests assets had not been restored; NOT counted as test evidence. After successful explicit local restore, reran actual NUnit suite: **294 passed, 0 failed, 0 skipped**.
- No hosted GitHub Actions, Codex, production migration, service deployment or provider execution was run.

## Scope and residual NO-GO
This Accepted review covers the inert historical-manifest/preflight integration, not complete task delivery or production activation. Unproven and explicitly excluded: original fleet-wide writer high-water and direct-ledger writer fence, WQ10 account/project epoch and uncertain operation reconciliation, genuine private bootstrap and launch-bound STOP/terminal proof, Work-first live API/TUI and Scheduler routing, empty-server-data restart proof, and explicit operator cutover authority. Follow-up test hardening may cover malformed/oversized Git trees; no blocking defect was observed in this scoped merge.
