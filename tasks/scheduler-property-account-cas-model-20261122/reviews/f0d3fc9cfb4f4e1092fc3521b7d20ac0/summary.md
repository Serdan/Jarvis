# Independent review — ChangesRequested

**Review request:** `f0d3fc9cfb4f4e1092fc3521b7d20ac0`
**Task:** `scheduler-property-account-cas-model-20261122`
**Frozen source SHA:** `a2e57bf59a7b967fafbcb821640bf41851d92738` (Worker 20, six additive test/docs files only; accepted native production ancestor `5607baf9913f4976add13c984e0182906d4fe8c7`).
**Independent Reviewer:** Reviewer 2

## Verified positive evidence
- Source SHA checked from isolated registered worktree HEAD; `git merge-base --is-ancestor 5607baf a2e57bf` exit 0; production `Client/Modules/NativeSchedulerAccountReservation.fs` blob `fd9c9591c74ce906e15dfc191c2dbac0995b8dc3` identical to the accepted source, and original fixture is pinned as `e6988ac42f4191f807b57f04dcb6ddec160b503f`.
- Independent `dotnet fsi --exec scripts/tests/scheduler-property-account-cas-20261122/prepare.fsx` exit 0 and exact pin reported. Independent `dotnet test scripts/tests/scheduler-property-account-cas-20261122/PropertyHarness.fsproj --configuration Release --no-restore --filter FullyQualifiedName~SchedulerAccountCasProperty --logger console;verbosity=normal`, Jarvis job `e7acc79327144d3082437870eb280882`, terminal **exit 0, 4/4 passed**, 27.2825 seconds.
- Wrong source-blob negative control `dotnet fsi --exec .../prepare.fsx 0000000000000000000000000000000000000000` exited **1** with `SOURCE_PIN_MISMATCH` before generating output. Generator's own trace mutant shrunk 4 to 2; abstract 384×24 sweep had 2,099 committed, 6,545 rejected, 572 replayed.
- Independently inspected `NativeReservationFixture.reserve` at `Client.Tests/NativeSchedulerRosterPolicyTests.fs:743-746`, which calls **real** `NativeSchedulerAccountReservation.reserveVerifiedDisposable`. Release and transition tests call their real disposable production methods; original fixture uses actual isolated bare account/project Git repositories, two account clones, ECDSA-signed roster policies, and canonical project task/review records. The 4 green tests are **not mock-only**.
- Confirmed authored production and property source diff empty and worktree clean after removing only the untracked generated compilation unit. No main edits, source edits, deployment or live account effects.

## Blocking original-scope evidence gaps
1. **P1 — Seeded reference model not production-compared:** 9,216 model steps self-check pure `SchedulerAccountCasReference`; none of those generated actions are replayed against native account CAS or compared to authenticated Git receipts/HEAD. Separate real-Git tests are fixed scripts, not differential stateful property runs. See `findings/seeded-model-not-production-compared.md`.
2. **P2 — Missing specified hostile boundaries:** no forced lock failure or ambiguous post-push CAS outcome, and no actual forged terminal proof/fence or absent project-terminal operation passed to production release. Genuine concurrent barrier and fetch-fault tests are acknowledged. See `findings/missing-failclosed-cas-boundaries.md`.

**Verdict: ChangesRequested** on the **test assurance contract**, not on Worker 4's production correctness. Keep StageB/B2, current default-off production scheduler and main integration held independently. Repairs should be bounded to existing isolated test/docs lease or separately adjudicated if a production test interface is genuinely unavailable. A green Release gate alone does not discharge unexercised original acceptance requirements.