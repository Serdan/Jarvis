# Hermetic .NET 11 Client.Tests Release runner

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

The runner creates one randomly named private directory per invocation, sets HOME, USERPROFILE, XDG_CONFIG_HOME, XDG_DATA_HOME, XDG_STATE_HOME, XDG_CACHE_HOME, XDG_RUNTIME_DIR, APPDATA, LOCALAPPDATA, DOTNET_CLI_HOME, TEMP, TMP, and TMPDIR **before launching the testhost**, and removes the directory on success, test failure, launch failure, and timeout. It also installs a **per-run disposable Git global config** with a non-personal test identity so offline `git commit-tree` fixtures cannot rely on, read, or modify the operator's Git identity. The parent Jarvis client and operator environment are not modified. The global NUnit SetUpFixture in `Client.Tests/HermeticHarnessTests.fs` independently verifies that the Client config path, OS application-data paths and temp path resolve *within* that private root. A direct `dotnet test` launch creates its own disposable fixture root, provides the same temporary Git identity, and fails closed if the OS runtime has already cached an unsafe special-folder mapping.

The path checks are read-only with respect to the operator's `~/.config/Jarvis`: neither the runner nor the proof tests read, rewrite or enumerate live activity, identity, client configuration, or ledger-binding data. Intentional `UnauthorizedAccessException` and `InvalidDataException` diagnostics may appear in negative ActivityLog tests without indicating that live files were accessed.

For the full suite, increase the bounded hard timeout (for example `--timeout-seconds 900`) if a known expensive test family exhausts the 240-second default. A timeout is a FAIL and needs named diagnosis or segmented reruns; it is not an acceptance signal.

For concurrent-process regression, launch **two instances** with `--no-build --filter FullyQualifiedName~HermeticHarnessTests` in parallel. Each has an independently generated HOME/XDG/tmp tree and must report a nonzero executed-test count and exit 0. For cleanup-after-failed-setup regression, run `dotnet fsi scripts/test-client-hermetic.fsx -- --self-test-failed-setup`; it must exit nonzero and print `scratch cleanup verified = true`. This flag is for harness diagnostics only.

The temporary NUnit results directory and potential hang-diagnostic artifacts are deleted with the sandbox. Always report exact Release test counts and named failures/hangs; hard timeouts and zero executed tests are failures, never PASS. This WQ-11 preparatory change does not modify production service, scheduler, account/project ledgers, or WQ-01 contract fixtures.
