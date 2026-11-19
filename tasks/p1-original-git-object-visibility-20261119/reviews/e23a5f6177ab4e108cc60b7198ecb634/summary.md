# Independent review — original Git object visibility (evidence-only)

**Reviewer:** Reviewer 4  
**Frozen head:** `17277a51dcec0f481db9870019e2ba6295b3b739`  
**Base:** `a83549a6897a4e26aa4834ff81dc38b6e16ee85e`  
**Recommendation:** **Accepted for negative evidence and bounded stop only**; no positive production object-reader, publisher effect, or Work cutover approval.

## Author's original scope summary (retained)

Pinned a83549a source; original client local commit and direct push deliver objects while mutating protected ref; no authenticated publisher-side object transfer/reader or trusted inventory is present. Hermetic bare-Git missing-object fixture recorded with protected ref unchanged; production topology unresolved. Evidence note only, source commit 17277a51; clean worktree; git diff --check passed; .NET 11 Release publisher gateway 18/18 plus adjacent admission/source-witness 13/13 passed. No live remote effects or cutover authority.

## Independent checks

- Exact frozen diff adds **only** `docs/design/work-original-git-object-visibility-evidence-2026-11-19.md` (42 lines). No executable source or ref-effect implementation was changed. Independent `git diff --check`: exit 0.
- Checked pinned-base `Client/Modules/LedgerStore.fs:2347-2352` and `Client/Modules/AccountCapacityLedger.fs:689-695`: each locally produces a commit, then uses `git push` to deliver objects **and** update the original protected ref. A locally known SHA is not independent server-side object availability evidence.
- Checked `Server/OriginalGitPublisherAdmission.fs:16-39` (CAS carries proposed OID), `Server/OriginalGitPublisherExecutor.fs:8-28,127-180` (fixture-only remote; no object-read/transfer port), `Server/WorkLegacySourceWitness.fs:10-13,61-87` (production authenticated reader absent), and `Server.Tests/OriginalGitPublisherGatewayTests.fs:481-537` (bare fixture first stages objects via test-only `publisher-stage`, then runs `update-ref`).
- **Independently executed** `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ServerOriginalGitPublisherGatewayTests --logger 'console;verbosity=minimal' -v minimal` from pinned clean main: exit **0**, **18 passed / 0 failed / 0 skipped** on .NET 11; existing F# warnings. These fixtures do not prove authenticated production transfer.
- Author's standalone hermetic missing-object reproduction and additional 13/13 admission/source-witness tests were **not rerun by this reviewer**. Their samples remain author evidence rather than an independent production witness.

## Review boundary

No blocking defect in the documentation-only investigation. Correct to stop rather than mint an unused positive preflight API from caller-controlled identity or OID inputs. Lead/source owners must independently establish the actual single-operator producer/publisher object-sharing topology and trusted repository/epoch/ref mapping; only if no approved shared-object read path exists should they separately authorize a non-ref-mutating quarantined delivery design. All source changes, authenticated publisher authority, STOP, integration and Work cutover remain **default-off** and require independent review and explicit operator approval.