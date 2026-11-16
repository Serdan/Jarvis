# Independent Review 2 — WQ10 offline cutover rehearsal

**Verdict: Changes requested (two P1 findings)**  
Frozen review source: `3bf142f22fc7a54b5e4c0b69e42f3b10e29f5376`  
Baseline: `275dce20cd0d3b51bb8369d27cdb6f03a8942691`

The additive wrapper is carefully labeled read-only and preserves `productionCutover=NOT_AUTHORIZED`; the two-clone fixture exercises an old-writer head conflict, cap-one reservation, pending Task/Role messages, Manager activity and original uncertain operations. There is no evidence of source/production mutation by the wrapper.

Independent validation on the frozen branch:
- `dotnet build tools/Wq10MigrationPreview/Wq10MigrationPreview.csproj -c Release --no-restore`: exit 0, **0 warnings, 0 errors**.
- `python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v`: exit 0, **20/20 tests passed** (7.541 seconds).
- Checked the actual host-preview implementation at WQ08 reference commit `f1ad0e89`, not just WQ10's synthetic JSON.

**Blocking findings:**
1. [Real WQ08 host output cannot be green](findings/real-host-dispatch-disabled.md): the real host always emits `DispatchDisabled`; WQ10 incorrectly treats it as a fatal issue. The shipped green fixture lacks this mandatory issue.
2. [Caller-controlled candidate/provenance bypass](findings/require-candidate-bypass.md): an actual adversarial local run with `requireCandidate=false`, two pending legacy tasks/Task+Role messages, and neither project's `wouldLaunch` present still reported `PREPARED_FOR_REVIEW` with zero blockers.

Request source fixes and regression tests on a **new frozen review**. Do not integrate this commit as currently submitted. This review does **not** authorize any production cutover, Manager retirement, Scheduler activation, epoch mutation or browser effect.
