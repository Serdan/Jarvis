# Hermetic .NET 11 Client.Tests Release runner (Linux only)

**Platform scope:** This foundation deliberately supports Linux only. On Windows, .NET `SpecialFolder.ApplicationData` / `LocalApplicationData` may ignore `APPDATA` and `LOCALAPPDATA` environment overrides; on macOS, known-folder resolution is likewise unverified. The runner and global NUnit fixture reject non-Linux execution before any profile config/activity access. The canonical follow-on `wq11-windows-hermetic-client-tests` requires test-only known-folder resolution or a disposable Windows account, Windows .NET 11 Release proof, and a separate accepted frozen review. Do not claim cross-platform hermeticity or remove the fail-closed gate.


Use `dotnet fsi scripts/test-client-hermetic.fsx --` as the supported Client test entrypoint. It runs `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore`, with NUnit diagnostics, a 75-second per-test hang detector, a 240-second default hard process-tree timeout, and explicit executed-test-count verification. Do not infer success from an empty test discovery or a shell exit code alone.

Before the first run in a new worktree:

```sh
dotnet restore Client.Tests/Client.Tests.fsproj
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore
```

Full suite (bounded), once Release binaries exist:

```sh
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --timeout-seconds 240
```

Target a fixture/module without skipping it silently:

```sh
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter 'FullyQualifiedName~ActivityLogTests' --timeout-seconds 120
```

The runner creates one randomly named private directory per invocation, sets HOME, USERPROFILE, XDG_CONFIG_HOME, XDG_DATA_HOME, XDG_STATE_HOME, XDG_CACHE_HOME, XDG_RUNTIME_DIR, APPDATA, LOCALAPPDATA, DOTNET_CLI_HOME, TEMP, TMP, and TMPDIR **before launching the testhost**, and removes the directory on success, test failure, launch failure, and timeout. It also strips every inherited `GIT_*` override (including Git author/committer identity, `GIT_CONFIG_COUNT`, injected parameters and repository/index bindings) before installing **per-run disposable Git global config** and explicit test-only `GIT_AUTHOR_*` / `GIT_COMMITTER_*` values. Offline Git fixtures therefore cannot inherit the operator's identity or higher-precedence configuration. Use `--self-test-git-sentinel` to inject synthetic higher-precedence identity/config sentinels *before* sanitization and filter `FullyQualifiedName~git_commit_identity_does_not_inherit_operator_or_sentinel_overrides` to prove author and committer in a disposable Git commit. The parent Jarvis client and operator environment are not modified. The global NUnit SetUpFixture in `Client.Tests/HermeticHarnessTests.fs` independently verifies that all supplied layout directories match the expected disposable paths, that no sandbox ancestor or leaf is a symlink/junction/reparse point, and that Client config, activity, application-data, temp and Git config resolve physically within the private root. Two throwaway directories test linked `config`/`data` escape rejection without using any operator profile data. A direct `dotnet test` launch creates its own disposable fixture root, provides the same temporary Git identity, and fails closed if the OS runtime has already cached an unsafe special-folder mapping.

The path checks are read-only with respect to the operator's `~/.config/Jarvis`: neither the runner nor the proof tests read, rewrite or enumerate live activity, identity, client configuration, or ledger-binding data. Intentional `UnauthorizedAccessException` and `InvalidDataException` diagnostics may appear in negative ActivityLog tests without indicating that live files were accessed.

For the full suite, increase the bounded hard timeout (for example `--timeout-seconds 900`) if a known expensive test family exhausts the 240-second default. A timeout is a FAIL and needs named diagnosis or segmented reruns; it is not an acceptance signal.

For concurrent-process regression, launch **two instances** with `--no-build --filter FullyQualifiedName~HermeticHarnessTests` in parallel. Each has an independently generated HOME/XDG/tmp tree and must report a nonzero executed-test count and exit 0. For cleanup-after-failed-setup regression, run `dotnet fsi scripts/test-client-hermetic.fsx -- --self-test-failed-setup`; it must exit nonzero and print `scratch cleanup verified = true`. This flag is for harness diagnostics only.

The temporary NUnit results directory and potential hang-diagnostic artifacts are deleted with the sandbox. Always report exact Release test counts and named failures/hangs; hard timeouts and zero executed tests are failures, never PASS. This WQ-11 preparatory change does not modify production service, scheduler, account/project ledgers, or WQ-01 contract fixtures.
