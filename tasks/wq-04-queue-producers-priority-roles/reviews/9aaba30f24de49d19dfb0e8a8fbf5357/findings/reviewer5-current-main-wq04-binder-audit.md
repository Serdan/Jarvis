# Reviewer 5 — independent frozen WQ04/WQ08 merge audit

**Verdict: Accepted (inert current-main integration only).**

## Frozen target and provenance

- Review `9aaba30f24de49d19dfb0e8a8fbf5357`, task `wq-04-queue-producers-priority-roles`, frozen reviewed head `ed358380400efaefcc3b95de9f03f8d1ad2678c5`.
- Exact merge parents: first `380a6cab474311809397ba80afeb1057b4505546` (WQ08 integrated main), second `76ca7864002157cde6901a562ea64dba8306e68c` (independently Accepted WQ04 source/CAS binder correction, prior decision seq3855).
- Independently ran `git merge-tree --write-tree 380a6cab... 76ca7864...`; result tree `87df6f643da1a9b353445512dc21fc6951a218e0` equals `ed358380^{tree}` exactly. Merge has no unreviewed conflict resolution or additional content.
- `git diff --check 380a6cab... ed358380...` passed, and worktree `git status --porcelain` remained empty after tests. First-parent diff is exactly 8 files: 2 modified project compile manifests and 6 additions (two source modules, two tests and two docs), 1,118 inserted lines.
- `git diff --exit-code 76ca7864... ed358380... --` all six added WQ04 modules/tests/docs exited 0: accepted source/test/document bytes are unchanged. The two .fsproj files necessarily incorporate WQ08 baseline entries, and the first-parent diff adds only the two WQ04 compile includes to each relevant project file; no WQ08 or earlier includes were removed.

## Independent local .NET 11 Release validation

- SDK `dotnet --version`: `11.0.100`.
- `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll /TestCaseFilter:FullyQualifiedName~WorkQueueCanonicalSourceEvidenceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests`: **11 passed, 0 failed** in 11 s. These cover original operation receipts, changed audit bytes, reviewed SHA/target/ref and decision versions, role membership, immutable challenge/operator responses, pending-vs-integrated states, caller high-water forgery, stale two-clone heads, forged role versions and raced CAS attempts with no successful operation receipt.
- `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll /TestCaseFilter:FullyQualifiedName~WorkQueue`: **56 passed, 0 failed** in 49 s.
- Explicit `dotnet build Server.Tests/Server.Tests.fsproj -c Release --nologo` succeeded (0 errors; pre-existing warnings), then `dotnet vstest Server.Tests/bin/Release/net11.0/Server.Tests.dll`: **255 passed, 0 failed** in 7 s.
- The unrelated unrestricted full Client.Tests run was canceled after producing no terminal result. It is *not* cited as passing evidence; the bounded 11/11 and 56/56 VSTest suites above independently completed.

## Security / semantic boundary

1. `Client/Modules/WorkQueueCanonicalSourceEvidence.fs` explicitly returns a typed immutable source-event fact with `Coverage = IncompleteCoverage`. It establishes original Git orchestration operation identity/receipt and entity origin, **not** a same-CAS authorization over source host effects, project queue, account capacity or role catalog.
2. `Client/Modules/WorkQueueCanonicalCasBinder.fs` exposes **only** `RecoveryBlocked` (no Authorized/Ready success variant). `inspectAt` records source, project and role gaps; `attemptUnderCas` always throws `IncompleteCoverage / RecoveryBlocked` from the real project transaction planner, so no project write or operation receipt is committed, even under two-clone races.
3. Independent `git grep` shows no production references to the new modules outside the source modules themselves and their .fsproj compile declarations. No Scheduler, Manager, claim/admission, host-effect or live route was wired by this merge.
4. The existing `WorkQueueGitLedger.fs` is unchanged from first parent. Its `change` planner still rejects non-fixture `PublishStage` irrespective of caller ID/kind, blocks role messages without canonical authority, rejects semantic WQ04 mutations, and unconditionally rejects `ProduceEvent` when source/index authority is absent. The legacy frozen WQ02 fixture remains fenced separately.
5. No evidence of activation of source handoff, WQ03 host push, production launch, account reservation, Scheduler, deployment, migration or server-local durable state. This review does not authorize any of those.

## Residual requirements (not a blocker to **inert** acceptance)

Positive WQ04 production integration still lacks an independently accepted historical role-catalog/same-CAS claim predicate, account-global launch-credit join, complete canonical source/index high-water and WQ03 host side-effect / protected WIP-ref proof. Worker11 historical role-catalog candidate was ChangesRequested (seq3808). Source reader explicitly records this as IncompleteCoverage. No reviewer should interpret this verdict or its passing tests as approval to open positive PublishStage, AdmitProject, ClaimNext or live Scheduler. Those require a new exact-source, independently reviewed change.

**Conclusion:** The clean exact merge preserves both accepted WQ08 base and the accepted inert WQ04 source/CAS correction, retains all default-deny semantics, and passes the targeted independent .NET11 Release tests. **Accepted for integration as inert code only.**
