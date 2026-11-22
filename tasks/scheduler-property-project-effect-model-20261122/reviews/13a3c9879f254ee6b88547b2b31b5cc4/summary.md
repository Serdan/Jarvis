# Reviewer 3 independent security/property review — ChangesRequested

Frozen commit: `731fa5245bf7a278756aa5fee832bbfdd950c12c`; task `scheduler-property-project-effect-model-20261122`; canonical spec blob `bbfa80507371662399fdd2e1e59f6787605d0b90`; review `13a3c9879f254ee6b88547b2b31b5cc4`.

Source inventory: clean author branch from base `dcac884fc061869572b12a03befc12f11c009f2f`, exactly five new files, all inside exclusive scripts/tests and docs/testing lease; no shared fsproj or production edits.

Independent terminal execution:
- `9640caf3cc7d4c818bfae51db2dd31ed`: Node property.test.mjs, exit 0, 14/14, zero fail/skip.
- `1b685791ec234173bd35aadd9f880925`: `dotnet run -c Release --project ProjectEffectModel.csproj` net11.0, exit 0, 8/8 model-only tests.
- `88fb58cf75c746ceb44afd78844f83de`: StageA accepted fixture suites, exit 0, 33/33, explicitly not native scheduler acceptance.
- `262575b24939445b8e32c466172cfcf7`: independently instrumented all 256 generated 48-command Node traces: 48 StartAuthorized, 42 VerifiedNoSend, 12 Released, but **0 provider submissions**. C# random loop likewise never calls SendAfterCommittedStart.
- `d9e240cdd6cd4a22a486999404915393`: independent lost-result counterexample: after fake-provider side effect accepted once but project did not persist its returned state, project counts zero sends; model accepts no-send (`VerifiedNoSend`), then `Released`, `completed=true`. Six real-Git generated 28-command suffixes seeds 101–106 reach zero `Released` outcomes; fixed deterministic cases separately test release.

Blocking **P1**: `findings/unpersisted-provider-send-allows-nosend-release.md` — real uncertainty is eliminated by storing provider submission in project state; fake provider no-session is only an unverified caller string. As an *oracle*, model incorrectly permits no-send/account release after lost fake-provider submission evidence. This is a Phase A model defect, not demonstrated StageB production behavior.

**P2**: `findings/generated-traces-miss-postsend-effects.md` — randomized traces do not exercise ambiguous external post-send outcomes, and real-Git equality compares the same `step` function to itself; generated green is therefore not an independent production adapter or adequate generated post-send safety evidence.

**Authority limit:** Phase A is intentionally test-only and explicitly disclosed; actual F# StageB B1/B2 remains unverified and conditional on separately Accepted correction and source custody. No live account, provider, BrowserBridge, main write, deploy, migration, scheduling or source edit. Proposed remediations are bounded to original Phase A negative-space/proof task scope; independent adjudication governs any attempted expansion.