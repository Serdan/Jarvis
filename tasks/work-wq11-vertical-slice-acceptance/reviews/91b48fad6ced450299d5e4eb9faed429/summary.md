# Independent review — WQ11 vertical-slice fail-closed acceptance

**Reviewer:** Reviewer 3
**Task:** `work-wq11-vertical-slice-acceptance`
**Frozen source:** `35939d478bc4b836a17f99506b1bfbe6ebee150c`
**Baseline:** `53105d3eaa1b476fadfe5470e962b4c5796fe8f9`
**Disposition:** Accept the *test-only, explicit NO-GO* evidence. **This is not approval of the integrated Work-to-Scheduler launch path, production activation, or Agent migration.**

## Independent checks

- Inspected the exact three-file change: `Client.Tests/WorkQueueWorkReplacementVerticalSliceTests.fs` (+255), `Client.Tests/Client.Tests.fsproj` (+1), and `docs/work-replacement-vertical-slice.md` (+112). No production module, hosted workflow, or cutover change.
- Confirmed two distinct project authorities, two approved clones each, and a separate account authority with cap=1. The added test reads pinned graph/Work/role heads from another clone and detects *Blocked* composition versus separately *Available* WQ02 Work; it never promotes the latter to executable eligibility.
- Confirmed `WorkCompositionGitLedger.execute` rejects caller-claimed `Accept` evidence without a same-head canonical verifier. Work-native `ClaimNext` rejects with `RejectedMissingBinding`; cross-project WQ08 preview reports `SourceLineageUnknown`, no winner, and no token; repeat preview is stable. Reactive wake defaults off; explicitly enabled fixture wake does not invoke planner or executor while account migration is `LegacyActive`.
- Inspected the adjacent `WorkQueueAcceptedAdaptersCapOneSmokeTests`: separate *fake-only* WQ04/WQ07 reservation and release, WQ03 Git-journal original, first-connect fake callback, replay, second-client evidence, cap exhaustion, unknown outcome fencing, and successor capacity. These are deliberately **not** a connected production Work-native execution.
- Independent command results on the frozen branch: `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore -v quiet` succeeded (0 errors; 3 warnings in unchanged files); `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll` with the WQ11 plus seven adjacent focused class filters passed **51/51**, 0 failed, 0 skipped. Branch `git status` clean.

## Boundary / outstanding NO-GOs

1. **Composition acceptance-to-Work eligibility:** no installed same-snapshot verifier joining unit spec/graph version, independent review/integration receipt, Work origin and generation to an eligible transition. New test cannot and does not demonstrate a successful acceptance/integration unlock.
2. **Work-native claim/session/terminal binding:** read-only WQ09 view is not a live claim or ephemeral executor. Separate fake-only legacy `InitializeAgent` fixture must not be advertised as replacing persistent Agents.
3. **Global dispatch and original effect:** source-origin planning stages lack immutable source ref/commit in WQ08 host comparison; there is no complete durable cross-project pre-send intent connected to WQ04/WQ07/WQ03, real provider-first-connect or terminal effect reconciliation. Unknowns remain blocked.
4. **Cutover:** account remains `LegacyActive`; no Scheduler activation, hosted CI, real browser/provider send, service restart, migration, or Windows/Kruso acceptance was performed.

**Recommendation:** Accept these additions solely as a reproducible, honest fail-closed acceptance gate and runbook. Record **WQ11/Work-to-Scheduler end-to-end GO = NO** until the named bindings are independently implemented and reviewed. Do not infer launch permission from an `Available` WQ02 stage, read-only simulated winner, or a passing fake callback suite.
