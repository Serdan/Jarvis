# WQ-11 hermetic Client.Tests (.NET 11 Release)

## Current acceptance boundary (2026-11-16)

**Windows portability is NOT accepted.** Focused Windows tests have been
reported on Kruso, but an unfiltered full Windows Client.Tests invocation has
not produced a clean exit-0, test counts, drained process tree and verified
scratch cleanup. The full-suite **F03 gate remains Blocked**. A previous
130-case ReviewWorkflow partition timed out; neither that result nor a
still-running job is a pass. Linux validation does not establish Windows or
macOS hermeticity; macOS is unsupported.

**Do not enable, dispatch, retry or push a workflow that triggers GitHub-hosted
Actions.** The operator disabled the disposable WQ-11 workflow because no
runner credits are available. The checked-in workflow is historical and is
NOT the authorized validation route. Do not run bare `dotnet test
Client.Tests/Client.Tests.fsproj` in the real Windows user profile. Never
modify Wayfold, the operator profile, production service, or WQ-01.

## Isolation contract

On Windows use only the separately registered, self-hosted
`Jarvis-Windows-QA` project on Kruso, or an equivalently independently
attested disposable Windows account/VM. The selected checkout must be clean
and pinned to the exact source SHA under review. Verify Windows, `dotnet
--version` (11.x), Release artifacts, and an explicitly provisioned
disposable scratch parent **outside** the token-derived real user profile
and physical AppData known folders. Reject absent, unverified, linked or
profile-overlapping paths before allocating scratch. Do not assume changing
only TEMP/USERPROFILE makes the environment safe.

The FSI parent allocates a random private root with an atomic protected
owner-only Windows ACL. It holds an exclusive **owner-only sidecar lease in
the attested parent** from before root creation through verified teardown.
NUnit independently checks native token-profile exclusion, owner-only ACLs,
the layout, and non-reparse containment before Client storage is used.
Client-owned config, activity, ledger, identity and file-edit lock paths use
test-only `ClientKnownFolders` overrides, not a production environment
override. The child gets disposable HOME, APPDATA/LOCALAPPDATA, XDG, temp,
DOTNET_CLI_HOME and isolated Git identity/config. Inherited `GIT_*`
overrides are removed. The parent/operator environment is not changed.

Windows invokes prebuilt .NET 11 Release `Client.Tests.dll` through
`dotnet vstest`, not the zero-test `dotnet test` path. The harness accepts
`--filter`, `--list-tests`, `--no-build` and `--no-restore` only.
Results require an actual positive executed-test count. The runner bounds
output, logs per-test progress, sets a per-test hang detector, kills the
child process tree on timeout, and requires drained stdout/stderr. Scratch
removal retries only transient Windows I/O locks. **Directory.Exists=false
is never treated as absence proof**: access denial and uncertain paths fail
closed, and cleanup success is printed only after independent native-path
absence proof and lease removal.

## Local/self-hosted Windows diagnostic sequence

Use commands only from the already attested disposable QA checkout, with a
scratch parent whose isolation was independently verified. Example commands
(the parent is an example of an independently verified Kruso path, not a
universal safe location):

```powershell
dotnet --version
dotnet restore Client.Tests/Client.Tests.fsproj --packages "C:\Windows\Temp\jarvis-qa-packages-EXACT-ID" --nologo --verbosity minimal
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore --nologo --verbosity minimal
dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent "C:\Windows\Temp" --no-build --filter "FullyQualifiedName~HermeticHarnessTests" --timeout-seconds 180 --self-test-git-sentinel
```

Investigate the stalled full suite **before** extending its deadline:
record the last completed case, process-tree liveness and CPU, TRX or hang
diagnostics, and the exact timeout/exit/cleanup outcome. Use guarded
`--list-tests` discovery and deterministic bounded `--filter` partitions
to isolate a stuck fixture. Fix the test or subprocess; do not substitute
another opaque hours-long run. For the acceptance gate, execute one
unfiltered run from a clean, frozen exact-SHA checkout with a justified
bounded timeout derived from measured partitions. Record command, Windows
and .NET versions, commit SHA, tests executed/passed/failed/skipped,
exit code, timedOut=false, child descendants drained, private temp/package
locations, scratch ID/path and cleanup=true. Repeatability, concurrency,
setup-failure, zero-discovery and forced-timeout cases are also required.

A deliberately failed setup must exit nonzero **and** prove complete
private cleanup. A zero-test result is a failure (exit 2), not a pass.
Native ACL rejection, junction rejection, after-root-creation failure,
access-denied negative coverage and an active-lease recovery rejection
must be tested on Windows; never weaken the gate to make them pass.

## Explicit stale-root recovery

Use `scripts/recover-hermetic-client-scratch.fsx` **only** after observing
the failed run's exact `jarvis-client-tests-<32-hex>` ID and verifying the
corresponding harness/testhost process tree is no longer live. Specify
`--scratch-parent` and that exact `--scratch-id`; never sweep or guess IDs.

```powershell
dotnet fsi scripts/recover-hermetic-client-scratch.fsx -- --scratch-parent "C:\Windows\Temp" --scratch-id "jarvis-client-tests-0123456789abcdef0123456789abcdef"
```

The helper independently re-attests the parent/profile, refuses unrecognized
IDs or missing pre-lease provenance, and must acquire the **exclusive lease**
before deleting any root content. A currently running harness holds the
lease, so the helper fails without touching its files. A crashed process
releases the OS handle; recovery then checks owner-only protected ACL,
path ancestry/reparse safety and independently verified post-deletion
absence. Inaccessible roots and unleased legacy roots fail closed for
operator investigation. Retain the exact command, exit, lease and scratch
cleanup evidence; never interpret failure as successful cleanup.

## Linux checks

```sh
dotnet restore Client.Tests/Client.Tests.fsproj
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter 'FullyQualifiedName~HermeticHarnessTests' --timeout-seconds 180 --self-test-git-sentinel
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --timeout-seconds 900
```

Linux fixture isolation is Linux-only. The Windows success gate still
requires a new exact-SHA independent task-bound frozen review after the
above Windows evidence. No main integration or portability claim before
Accepted.
