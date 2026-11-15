# F02 — P1: Operator Git identity overrides inherited into test processes

Frozen source: `scripts/test-client-hermetic.fsx:83-88,125-150`, `Client.Tests/HermeticHarnessTests.fs:51-66`, `Client.Tests/ProjectCreationTests.fs:85-125`, commit `c5a2a1f5`.

**Issue.** The runner and direct NUnit fixture create a disposable `GIT_CONFIG_GLOBAL` with `user.name` and `user.email`, but never sanitize inherited `GIT_AUTHOR_NAME`, `GIT_AUTHOR_EMAIL`, `GIT_COMMITTER_NAME`, `GIT_COMMITTER_EMAIL` or Git config environment overrides such as `GIT_CONFIG_COUNT`. `ProcessStartInfo.Environment` inherits these from the operator process. Git identity environment variables supersede `user.*` in global config. Thus offline `git commit-tree` fixtures can retain the real operator's name/email even though the config file is a test fixture.

**Impact.** Git commit identity is nondeterministic, can leak private operator identity into temporary objects or diagnostic output, and contradicts the explicit isolation goal.

**Required fix.** Set explicit test-only `GIT_AUTHOR_*`/`GIT_COMMITTER_*` values and remove or normalize conflicting higher-precedence `GIT_CONFIG_*` overrides in both wrapped and direct invocations. In direct NUnit setup, record/restore every changed variable. Add a sentinel-environment test that runs a disposable Git commit and inspects both author and committer, proving no inherited operator values survive. Do not expose real operator identities.

**Evidence.** Source inspection; independent Linux smoke passed 1/1 but does not cover inherited Git identity variables.