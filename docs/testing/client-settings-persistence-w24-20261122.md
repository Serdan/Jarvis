# P0 client settings persistence/runtime — Worker 24 verification ledger

Source root: `dcac884fc061869572b12a03befc12f11c009f2f` (main at isolation). Private worktree `fix/client-settings-runtime-w24-20261122`. No live operator WSL profile or registered device is used. No integration, deployment or scheduling is authorized by these tests.

## Distinguish observed failures from proven causes

Operator reported (1) workspace root appeared unchanged and did not persist after restart and (2) machine name did not work. These are both required end-to-end acceptance cases. Source inspection alone is **not** proof of cause. The initial RED-first .NET11 Release test proved a **specific adjacent defect**: `updateKey "workspace-path"` accepted a nonexistent absolute directory which would cause `Program.getDir` to prompt for a different path. It failed on original source, 1 failed / 10 passed in `/tmp/jarvis-w24-settings-red/w24-settings-red.trx`. The fixed version passed 11/11 in `/tmp/jarvis-w24-settings-after/w24-settings-after2.trx`.

The original connected-client machine name was only reloaded at registration and on reconnect. The private code now checks name changes during the connected loop and re-registers using the same deviceId. A disposable real SignalR hub test verifies two differently configured names reach `Program.register`, but **does not by itself prove the main loop re-registration or original observed failure**. An offline real ConsoleTui PTY keyboard harness independently proves Save + fresh reopen for both reported settings and a write/unset cycle across all eight.

## Surface matrix

| TUI label / canonical key | JSON key | Startup override | Actual consumer / boundary | Application semantics | Proven settings-specific evidence / remaining gate |
|---|---|---|---|---|---|
| Machine name / `machine-name` | `machineName` | `--machine-name` | `Program.resolveMachineName` -> `Program.register` -> SignalR device view | Re-register on connected loop check; CLI override wins | Real PTY TUI Save+fresh reopen, separate CLI process and two names through disposable SignalR hub PASS; connected main-loop trigger independently untested |
| Workspace directory / `workspace-path` | `workspacePath` | `--path` | `Program.runClient` -> `Runtime(getDir ...)` -> `ProjectIO.root` -> `ProjectBrowser.listProjects` | Restart required; nonexistent path rejected, no silent default | RED->GREEN missing root, real PTY TUI Save+reopen, A1/B1 actual Runtime/listProjects and separate CLI processes with spaces PASS |
| Trust level / `trust-level` | `trustLevel` | `--trust` | `TrustLevel.parse` -> `Runtime` permission policy | Restart required | Real TUI edit/unset, persisted `Runtime :> PermissionIO` NoTrust, remote elevation negative PASS; particular command permission decision still pending |
| Environment variables / `allowed-environment-variables` | `allowedEnvironmentVariables` | `--allow-env`, `JARVIS_ALLOWED_ENVIRONMENT_VARIABLES` | `ProcessEnvironment.configureAllowedEnvironmentVariables` | Restart required; TUI local approval for expansions; remote expansion denied | Real TUI edit/local-confirm/unset, effective allowlist preserves W24_TOKEN but removes W24_PASSWORD through real sanitizer; actual subprocess exposure pending |
| Max import bytes / `max-import-bytes` | `maxImportBytes` | `--max-import-bytes` | `Runtime.MaxImportBytes` -> import path | Restart required | TUI edit/unset, persisted Runtime.MaxImportBytes=8192 PASS; actual under/over limit import pending |
| Browser bridge port / `browser-port` | `browserPort` | `--browser-port` | `BrowserBridge.Service(port)` -> 127.0.0.1 listener | Restart required | TUI edit/unset, configured random loopback listener HTTP 403 unauthenticated PASS; disable/rebind pending |
| Native Health enabled / `native-health-enabled` | `nativeHealthEnabled` | none | `NativeHealth.Service` reloads `ClientConfig.load` during checks | Next Health check; never automatically enable by default | Real TUI edit/unset, persisted settingsFromStored Enabled true then default false PASS; actual service check pending |
| Native Health idle seconds / `native-health-timeout-seconds` | `nativeHealthTimeoutSeconds` | none | `NativeHealth.settingsFromStored` -> service | Next Health check, default 600s | Real TUI edit/unset, persisted settingsFromStored 601 and default 600 PASS; 599/600/601 real check pending |

## Shared update path

`ClientConfig.save(snapshot,stored)` is the staging TUI Save boundary. It validates all keys, checks revision under cross-process sibling file lock, prepares 0600 temp JSON preserving unknown keys, calls flush/fsync, atomically replaces, re-opens and verifies digest/values before a successful acknowledgement.

`ClientConfig.applyAt(path, expectedRevision:string option, key, value:string option)` and `applyKey/applyUnset` use that same save. CLI `config set/unset` routes through them. `applyRemoteAt` / `applyRemote` additionally fail closed on elevated trust and environment allowlist expansion (require local operator confirmation), rather than inventing a remote privileged route. Worker 25 owns protocol/tool wiring; no tool implementation from Worker 24.

TUI shows a per-field `[unsaved]` marker and separately labels startup effective values, confirms trust full and environment allowlist expansion, and distinguishes persistent/readback acknowledgement from actual runtime effect. Changes are not automatically applied to a running Runtime except machine name re-registration and existing Native Health checks.

## Test receipts

- Initial source-only RED: `/tmp/jarvis-w24-settings-red/w24-settings-red.trx`, 1 failed / 10 passed; targeted nonexistent workspace path.
- Repaired focused initial: `/tmp/jarvis-w24-settings-after/w24-settings-after2.trx`, 11/11 PASS.
- Persisted eight keys, fresh runtime project enumeration, 25 concurrent CAS races, real isolated SignalR: `/tmp/jarvis-w24-settings-matrix/w24-settings-matrix-v3.trx`, 16/16 PASS.
- Additional isolated fresh CLI `config path/set/show/unset` processes and malformed/symlink negatives: `/tmp/jarvis-w24-settings-matrix/w24-settings-matrix-cli-v4.trx`, 18/18 PASS.
- Actual offline TUI PTY trials: initial `/tmp/jarvis-w24-real-tui/w24-real-tui.trx` 2 RED because the disposable pseudo-terminal was initialized with zero usable display dimensions; fixed fixture with explicit `stty rows/cols`. Initial repaired runs demonstrated disk Save for both reported fields but had presentation-only false negatives on clipped output. Final matrix below is green.
- Isolated persisted runtime consumers, including actual loopback HTTP 403: `/tmp/jarvis-w24-runtime/w24-real-runtime-consumers.trx`, 1/1 PASS.
- **Final source-bound Release all-settings matrix** `/tmp/jarvis-w24-settings-final/w24-settings-final-focused.trx`: **23/23 PASS, 0 failed, 0 skipped**, actual PTY TUI keys and eight-field Save/default, fresh CLI processes, real source-bound runtime/SignalR, CAS and negatives.

## Still required to close P0

Independent RED-first proof of the operator-observed already-connected machine-name no-effect and complete live-client loop behavior (no live operator device was touched). TUI mouse path, actual import under/over-limit action, process spawn, BrowserBridge disable, Native Health fake-clock SUT checks, XDG/HOME variations and negative power-loss/permissions are still beyond the focused source-bound matrix. Source-bound full unfiltered Client Release TRX with honest existing unrelated fanout RED, a negative mutant, exact frozen source commit, independent review, and accepted-only integration under separate Operator GO are still separate gates.

Do not interpret any checkpoint or partial suite as global P0 acceptance.

## Adjudicated narrow correction (after immutable Review 2c5ba92 / seq11280)

The source review on `55fdbcb93681abaa07d94cc62da2d2fef6ce59b3` was **ChangesRequested**, with Adjudicator 3 independently disposing findings F1–F4 as **NARROW**, not acceptance. Previous matrix and “still required” entries above describe the initial frozen revision and must not be mistaken for the current correction's final status.

### Correction ownership and typed service

Worker 24 owns only `Client/Modules/ClientConfig.fs`, `Client/Modules/ConsoleTui.fs`, `Client/Program.fs`, local settings tests, offline harness and this evidence. Worker 25 exclusively owns protocol/SignalR dispatch/server MCP tool wiring. No shared `*.fsproj` edit, live WSL mutation or main integration occurred.

`ClientConfig.applySettingsAt path caller currentRuntime startupArgs expectedRevision mutation` has:
- `SettingsMutation = Replace(stored) | Set(canonicalKey,value) | Unset(canonicalKey)`.
- `SettingsCaller = LocalTuiUnconfirmed | LocalTuiConfirmed | LocalCli | RemoteAgent`; actual adapters fix provenance, and remote wire cannot select local approval.
- Typed validation/confirmation/revision/storage failures; remote requires a concrete expected revision and denies trust or env allowlist increases. Especially, **`Some "none" -> None` elevates to default partial and requires explicit local TUI confirmation**. CLI `config set/unset` is an explicitly invoked local command, not a remote approval path.
- Snapshot `Before` and verified readback `After`, `CurrentRuntime`, `BeforeEffective`, `AfterEffective`, `Overrides`, `AppliedNow`, `RestartRequired`, `ReconnectRequired`, `RegistryRefreshPending`, `NativeHealthRefreshPending`. The existing locked CAS, 0600 temp/atomic replacement/fsync and readback remain the sole writer; no second implementation.
- `ClientConfig.installRunningContext` is only called from actual locally parsed `Program.runClient`; `tryRunningContext` exposes its startup runtime and flags for the **selected** client tool adapter; `recordRegisteredMachineName` tracks successful connected-loop registration. A running client's settings that require restart are not represented as immediately applied. Native Health's configured changes are evaluated at its next check.

The TUI saves via this same service and shows a separate local confirmation screen, while CLI `config set/unset` uses it with `LocalCli`; Worker 25's selected-device path must use `RemoteAgent` and fail closed if a real startup context is unavailable. `applyRemote` remains a low-level compatibility seam, not the shared result contract.

### Bounded fresh behavior evidence (no invented historical RED)

The operator's historical two failures were not directly reproducible on the live machine, and **no historical RED has been fabricated**. The only original source RED was the adjacent nonexistent workspace acceptance described above. New tests exercise the **production code paths** in disposable sandboxes:
- Real PTY TUI editing/saving workspace root B after A, then a *fresh executable process* loading persisted configuration, constructing the actual `Runtime` and executing `ProjectBrowser.listProjects`. The expected B-only project path is observed and the A-only project disappears. This is offline startup/project access, **not** a live logged-in remote client.
- Production connected-loop `Program.refreshRegisteredMachineName` against an actual disposable SignalR registration hub: A→B name updates on same device ID; unchanged B does not produce duplicate Connect calls; reconnect re-registers B. Startup CLI machine-name override masks persisted changes.
- TUI PTY explicit no-trust→unset negative decline retains identical JSON revision, positive Y persists default partial; typed remote no-elevation and missing/stale revision negatives.
- Eight setting consumers verified through representative actual actions: permission evaluator no-trust/partial known provider decision; real spawned child filtered environment; bounded HTTP fake-response download too-large rejection/no stage and exact-under-limit success; real loopback listener disable/port rebind/HTTP 403; Native Health fake authenticated server clock default-off and 599/600/601-second recoveries. The full combination matrix, actual live extensions, and arbitrary power-loss cannot be inferred.
- `ClientConfig` typed transaction checks readback, current effective runtime versus persisted effective settings, startup overrides, restart/reconnect/registry/Health status; actual full service integration with Worker 25 remains a **separate** gate.

### Test ledger and acceptance boundary

Focused corrective Release test run prior to adding a small running-context observation accessor: `/tmp/w24-settings-focused/w24-settings-focused-v3.trx` **31 pass / 0 fail / 0 skip**. Two preceding PTY fixture failures were test input errors (reset to default partial needed Y; editing nonempty workspace required U before typing B), explicitly corrected. This is not the final source-exact acceptance receipt; re-run after final source and pin new commit.

Required final gates: compile final source, fresh source-exact .NET 11 Release focused and full unfiltered Client TRX, preserve exact CAS/negative control, create immutable candidate SHA and request **new independent review**. Until the new review is Accepted, the original CR source and this candidate are excluded from accepted-only composition. Worker25's separate selected-device server/tool acceptance is likewise held.

Final corrective focus on the complete W24 candidate after the running-context accessor: `/tmp/w24-settings-final/w24-settings-final-focused.trx`: **31 PASS / 0 FAIL / 0 SKIP**, .NET 11 Release, job `d94ed4d173e44cba82403e91f53f343d` terminal exit 0. This supersedes the 31/31 pre-accessor receipt above. The full unfiltered Release Client run is a separate running job `cd5327f6ed95489c981d0a5e4d01f94e` writing `/tmp/w24-client-full-final/w24-client-full-unfiltered.trx`; its result must not be assumed from this focused pass.
