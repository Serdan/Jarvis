# Independent review — accepted CI/release browser gate correction

**Target:** review `e79db1e54bb444bc9d1692f3d4bd2c11`, frozen `c2b74c887a32b65d80747ad66fc32561109efadf`, baseline `d702352ef705f9991ac1cb14dc15a13cce78aa44`, branch `test/work-launcher-worker1-20261121`. Reviewed the immutable Git object, two-commit diff (`ef602c333`, `c2b74c887`), task spec, and canonical release preflight. Worktree remained clean.

## Scoped conclusion

The CI/release wiring is structurally correct: `scripts/build.cs` executes Client and Server .NET suites first, returning early on failure, then executes `node --test browser-extension/tests/*.test.cjs` with the repository root as working directory. `ProcessStartInfo.ArgumentList` passes the pattern unchanged; Node v26.13.0 resolves it and discovers tests. `.jarvis.json` task `test` and `.agents/skills/deploy/SKILL.md` preflight both invoke `dotnet scripts/build.cs test`. No GitHub Actions workflow is present at this SHA, so this is proof of the documented local release gate, **not** a hosted CI run.

Independent source-bound checks:
- `git diff --check d702352... c2b74c...`: exit 0.
- `node --test browser-extension/tests/chatgpt-work-launcher.test.cjs`: **23 pass / 0 fail**; deterministic in-memory DOM covers hydration, Work mode, model/effort changes, prompt exact-once, failure-without-send, outcome-unknown without retry, and cleanup.
- `node --test browser-extension/tests/*.test.cjs`: **171 total / 107 pass / 64 fail**, exit 1, in frozen worktree. Failures originate from the pre-existing `chatgpt-launcher.test.cjs` suite.
- Same all-suite command against unmodified baseline `d702352...`: exit 1 with the **same 64 existing failures**; new Work tests do not introduce the red cases.
- No authenticated browser prompts, live Send, install, deployment, merge, or schedule effects.

**Release-readiness caveat:** The changed test gate is presently **red**, as it should be when existing browser regression tests fail. Do not report green verification or deploy on this evidence. Repair or independently adjudicate the baseline Chat-suite failures before attempting a green preflight. The Work suite is a simulated-DOM regression suite; a no-send installed-extension probe and live installed-extension acceptance were not performed in this review and must not be inferred.

This review approves only the pinned test-gate source. It does not approve live browser transport or newer model-selection policy changes on other branches. No author source files were edited.

**Canonical full Release command:** `dotnet scripts/build.cs test --configuration Release` was launched in the isolated worktree as job `80dd65cd5f904294b883f8636f3319d9`; outcome will be appended when bounded execution completes.
