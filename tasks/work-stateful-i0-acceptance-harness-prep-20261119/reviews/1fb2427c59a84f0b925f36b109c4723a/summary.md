# Independent frozen review — Stateful Work I0 acceptance preparation

**Review target:** `930574c223fd148c557d86c968e4f19bc2e1d6fb` from base `a83549a6897a4e26aa4834ff81dc38b6e16ee85e` (`test/work-stateful-i0-acceptance-worker9-20261119`), review `1fb2427c59a84f0b925f36b109c4723a`.

**Verdict: ACCEPTED for the isolated, default-off I0 preparation and truthful evidence matrix only. MERGE HOLD remains mandatory.** This is NOT approval to integrate four intentionally failing tests into main, to declare G4/G5/G6 green, to activate the scheduler, or to authorize migration/deployment.

## Independent review evidence

- Inspected the complete exact base-to-frozen diff: only `Server.Tests/WorkStatefulRolloutAcceptanceContractTests.fs` (219 new lines), `Server.Tests/Server.Tests.fsproj` (one include), and `docs/testing/work-stateful-e2e-test-readiness-2026-11-19.md` (83 lines). No production source changes.
- Verified clean frozen worktree and `git diff --check a83549a6 930574c2` exit 0.
- `dotnet --version`: `11.0.100`; independently rebuilt `Server.Tests/Server.Tests.fsproj -c Release --no-restore`: exit 0, 0 errors, 10 warnings in files outside the change.
- Independently executed NUnit on rebuilt `Server.Tests.dll`: new non-RED I0 tests **3 passed / 0 failed**, B/D/E focused group **53 passed / 0 failed / 0 skipped**, C1/F/G/H adjacent group **56 passed / 0 failed / 0 skipped**. New tests overlap the B/D/E filter; not an additive total or a full combined suite.
- Executed the separately filtered `FutureRed` category: exactly **4 discovered, 4 failed intentionally** (authenticated original-Git B ingress, complete trusted JSON-to-empty SQLite restore, stale-backup persisted RecoveryRequired boot fence, authenticated complete H status). These are real failing NUnit tests rather than ignored/skipped pseudo-tests. Unfiltered Server.Tests Release therefore fails by design at this SHA; **no full Release pass or cross-platform / two-client proof is claimed**.

## Contract, provenance and risk assessment

The new disposable SQLite test creates two project scopes with duplicate Work IDs, eight kinds, one shared-account capacity setting, exact synthetic SHA/receipt/UUID data, Prepared/Unknown effect records, restart + same-operation replay and altered-payload rejection. Separate pure D and graph tests cover Draft/Proposed/Open, rejection of unverified Lead approval, required descendant blockers and optional containment. Focused E tests exercise actual cap-one reservation competition and STOP/fences. The matrix correctly distinguishes these source-clean *default-off* component results from authenticated producer authority and integrated safety gates; its synthetic provenance is appropriately declared synthetic rather than a verified original Git source.

**Outstanding blockers are intentional and must not be waived:** (1) authenticated source-typed B producer ingress; (2) trusted complete-domain F backup and deterministic empty-SQLite inverse; (3) persisted G RecoveryRequired fence on stale N versus N+1 evidence before host serve; and (4) authenticated, appropriately scoped complete H status/recoverability. Preserve the current isolated branch and these executable REDs until producer changes and independently reviewed integrations actually implement the predicates, or accepted equivalent executable contracts replace them without weakening the admission gate. Re-pin and rerun full Server + Client Release and recovery/rollback drills before any merge/cutover decision.

**Scope limit:** no live Scheduler, migration, original Git push, external one-shot effect, deployment, restart, or operator permission was exercised. See `findings/merge-hold.md` for the integration gate.
