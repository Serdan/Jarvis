# WQ-11 hermetic .NET 11 Client.Tests Release runner

**Evidence boundary:** Linux is verified independently by the preceding WQ-11 foundation review. The Windows follow-on introduces a *test-only* Client known-folder resolver, but Windows support remains unaccepted until the exact frozen source passes the Windows GitHub-hosted ephemeral runner and an independent review. macOS is not supported. Never run bare `dotnet test Client.Tests` against a real Windows user profile.

## Storage isolation

All Client-owned paths (config, device identity, activity default, ledger bindings through config, and file-edit locks) use `ClientKnownFolders`. Production resolves the normal OS special folders. The internal `installForTests` scope is exposed only to the `Client.Tests` friend assembly, not through environment variables or the shipping CLI. The global NUnit setup fixture installs it only after checking every externally supplied sandbox path, root ancestor and descendant for lexical containment and symlink/junction/reparse points. On Linux it also enforces owner-only 0700 directories and 0600 Git config. On Windows use an **ephemeral Windows machine/user** and a pre-existing scratch parent outside the real user profile; the runner refuses Windows without `--scratch-parent` and rejects a linked parent or a path inside UserProfile/ApplicationData/LocalApplicationData.

The runner launches the testhost with disposable HOME, USERPROFILE, XDG paths, APPDATA/LOCALAPPDATA, DOTNET_CLI_HOME, TEMP/TMP/TMPDIR, and a disposable Git global config, clearing inherited `GIT_*` (including `GIT_CONFIG_COUNT`, identities, repository/index overrides). Linux runs `dotnet test`; Windows runs the prebuilt **.NET 11 Release** DLL through `dotnet vstest`, bypassing a Windows `dotnet test` zero-output/zero-test no-op when MSBuild/NuGet imports are isolated. Windows passthrough supports `--filter`, `--no-build`, `--no-restore` and `--list-tests` only, and rejects unknown options. The runner uses a hard process-tree timeout, bounded stdout/stderr tails, a nonzero-test-count assertion, and cleanup on successful execution, setup failure, test failure, and timeout; Linux's test invocation additionally retains its 75-second test hang detector. Root path/cleanup evidence is printed. The parent Jarvis client and operator environment remain unchanged.

## Linux commands

```sh
dotnet restore Client.Tests/Client.Tests.fsproj
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter 'FullyQualifiedName~HermeticHarnessTests' --timeout-seconds 180 --self-test-git-sentinel
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --timeout-seconds 900
```

## Windows validation (disposable VM only)

The workflow `.github/workflows/wq11-windows-hermetic-client-tests.yml` runs on GitHub-hosted `windows-latest` with .NET 11 Release, attests that `RUNNER_TEMP` is outside `USERPROFILE`, then runs focused tests, all Client.Tests, two concurrent isolated testhosts, and a deliberately failed setup. A CI worker is an ephemeral Windows OS profile: it is not the operator's Windows account. The caller must provide an explicit existing safe scratch parent (e.g. the workflow's `$env:RUNNER_TEMP`) because the ambient Windows temp directory typically belongs to a real profile.

```powershell
dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent "$env:RUNNER_TEMP" --no-build --filter "FullyQualifiedName~HermeticHarnessTests" --timeout-seconds 180 --self-test-git-sentinel
dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent "$env:RUNNER_TEMP" --no-build --timeout-seconds 900
```

The setup fixture refuses direct Windows runs without the externally supplied `JARVIS_CLIENT_TEST_HERMETIC_ROOT` provisioned by the runner. The Windows regression explicitly verifies that real shell special-folder paths are *not* spoofed, while Client config, activity, ledger, identity, and temp resolve into the private root. The linked-ancestor regression creates a Windows directory junction (or Unix symlink) only between two disposable roots. Any timeout, discovered-test count zero, unsupported platform, invalid parent, unexpected exit, or failed cleanup is a **failure**, not a pass.

## Negative-space probes

```sh
dotnet fsi scripts/test-client-hermetic.fsx -- --self-test-failed-setup
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter 'FullyQualifiedName~nonexistent-test-name' --timeout-seconds 120
```

The failure probe must exit nonzero and report `scratch cleanup verified = true`. The zero-discovery probe must return exit 2. Two concurrent invocations of the focused filter must have different scratch IDs, positive executed-test counts, and independently verified cleanup. No production service restart, migration, live operator data access, or WQ-01 source change belongs to this task.
