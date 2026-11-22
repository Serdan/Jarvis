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
