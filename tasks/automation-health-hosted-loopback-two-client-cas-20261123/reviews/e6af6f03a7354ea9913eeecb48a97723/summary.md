# Independent review — ChangesRequested

- Review ID: `e6af6f03a7354ea9913eeecb48a97723`; reviewer: Reviewer 3.
- Frozen head: `d8ebf0025f11873a9a0f58aedaa7aaf541cb7187`; base `dcac884fc061869572b12a03befc12f11c009f2f`.
- Canonical task/spec: `automation-health-hosted-loopback-two-client-cas-20261123` / `e5c8d455b90d5eb58ea5f22971f669cdce7bb989`.
- **Verdict: ChangesRequested** for one bounded source-test coverage finding in `findings/required-hosted-negative-matrix.md`. This is not a known production flaw, does not expand the author lease, and does not invalidate the independently witnessed core positive.

## Independently confirmed

A genuine `Program.configureServices` + `configureApp` offline ASP.NET host maps production `/mcp` and registered `/client` SignalR, locally signed/validated JWT with `client:connect`, two distinct registered same-user client sessions. A qualifying authenticated MCP tool ingress reaches production routing, persists current-process server SQLite attributed activity, and both `GetAgentActivity` reads return the same per-project/agent timestamp. Both native Health callbacks use these live SignalR reads and two separate clone runtimes sharing one disposable canonical Git remote; concurrent checks produce one canonical assignment-generation transition and a `RecoverAgentAssignment` deterministic receipt. Stale second writer loses; future/deleted test-owned SQLite row is rejected. The positive does **not** establish third-client command completion.

- Frozen diff: only four **new** lease-allowed files, no existing production/fsproj changes, worktree clean before run.
- Independent filtered .NET11 Release: job `2c52d2e0bf0b4686864fdc3c50b5a7a7`, 1/1 PASS, exit 0; `reviewer3-health-hosted-green.trx` SHA256 `7204d47bab7a17d94bd0e3e025225833aa7c5035fe5cb3c8644a11f61960f208`.
- Independent foreign-project mutant: job `dd7c87e3d36d459291b6969093288cbf`, 0/1 PASS, expected exit 1, live Hub `ActivityProofUnavailable`; `reviewer3-health-hosted-mutant-red.trx` SHA256 `cc5c62a0ba464061d8775dccd6a03cf36f983cf878b4492e9efec2924d801685`.
- Worker baseline absence RED job `9450f5df773b4a18a6b0d6f139e832fc` and final GREEN job `abdcae4602d548e6879199cbdb9b08d9` documented, with worker `hosted-final-green.trx` SHA256 `f0341fd2d20a50934ef921dbdd7c1fada4644ceccd0bc9c4db52aa90259da0b1`.

## Unmet original matrix

The standalone hosted test has no host/store restart proof, no exact 600-second boundary test (only fresh HOLD at 600s and accelerated positive with explicitly test-only 1s), and no hosted reversed-clock or mid-check changed source/version/generation HOLD proof. The different-user negative precedes Alice's actual ingress. Documentation honestly records some limits; pre-existing pure-snapshot tests are not equivalent to hosted end-to-end negative coverage. See the single finding for reproduction and proportional correction. **Do not run 600 real seconds, restart a production OS/service, synthesize positive activity, or construe this as deployment, scheduling or cutover approval.**
