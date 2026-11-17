# Reviewer 4 — independent frozen review

**Request** `6b3e990b0a7446d0b74f0138c8d2b019` · **Task** `task-b0bdb201f57e4c568c9b3456401bfb26` · **Base** `aa2e46ff2927b8e9fd0af25204377c299f2ec576` · **Frozen head** `a636f5c87fb5e9f708ec8f3e5d968cf74713f3bc`.

**Verdict: ChangesRequested (acceptance evidence incomplete).**

Reviewed all 15 changed files (new GitHub Linux/Windows workflow, five lockfiles and RID selection, Release build gates, local client/server publishing and downloaded-artifact manifest/checksum verification, documentation, isolated negative tests). Publish flow is deliberately manual with no auto-deploy; manifest accurately documents that checksums are an audit trail, not a digital signature. The existing updater's own SHA256/rollback boundary remains separate. Did not edit implementation, merge, deploy, or authorize migration.

Independently executed in the clean frozen checkout:
- `dotnet restore Jarvis.slnx --locked-mode`: PASS.
- `dotnet build Jarvis.slnx --configuration Release --no-restore`: PASS (non-fatal F# warnings).
- `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-build --no-restore`: **488/488 PASS**.
- `python3 scripts/tests/test-release-provenance.py`: PASS (isolated synthetic Git repos).
- `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter FullyQualifiedName~HermeticHarnessTests --timeout-seconds 120`: **3/3 PASS**, sandbox cleaned.
- Final `git status --short`: clean.

**Blocking finding:** `findings/full-client-release-gate-unverified.md`. The full Client Release suite, specifically required by the canonical task's acceptance criteria and exercised in Linux CI with 900-second deadline, has no demonstrated successful nonzero-count run; the author explicitly reports direct Client run exceeding 300s and an ongoing hermetic diagnosis. The 3 passing targeted Client harness cases cannot stand in for the full suite. Supply exact-checkout full pass/failure diagnostics and hosted CI evidence or explicitly scope the latter as unverified; diagnose hangs rather than relying on timeout expansion.

**External rollout condition:** the workflow reports status; branch protection/rulesets must actually require both OS checks to prevent merging known failures. The docs say so correctly, but remote enforcement was not verified here. Windows hosted execution, publish of real cross-RID binaries, external hash distribution, and deployment were not tested in this review. No GO is implied.