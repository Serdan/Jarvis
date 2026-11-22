# Update named client setting (private P0 implementation contract)

The server offers one MCP tool, `update_named_setting` (backed by
`UpdateNamedSettingCommand`), for **one client-local setting at a time**.
This draft lives on Worker 25's private source branch; it is not deployed
and is pending independent review and the separately reviewed Worker 24
settings service.

## Request contract

| Field | Type | Meaning |
| --- | --- | --- |
| `deviceId` | string, required | Exact connected client ID from `list_devices`; no account-global fallback |
| `settingName` | strict string enum | One of the eight canonical names below |
| `operation` | `set` or `unset` | `unset` restores the default and requires JSON `null` as the value |
| `value` | JSON string/number/boolean/string array | Type depends on the setting; no arbitrary JSON fragments |
| `expectedRevision` | required 64-character SHA-256 hex string | Fail if the client's current config changed since observation |

Canonical names, strictly case-sensitive:

- `machine-name`: string (1–128 non-control chars, validated locally)
- `workspace-path`: string (existing absolute project-root directory, not a project)
- `trust-level`: string (`none`, `partial`, `full`)
- `allowed-environment-variables`: JSON array of environment variable names
- `max-import-bytes`: nonnegative 64-bit integer
- `browser-port`: integer between 1024 and 65535
- `native-health-enabled`: boolean
- `native-health-timeout-seconds`: integer between 1 and 604800

Example requests (illustrative, **not** a request to change a live client):

```json
{"deviceId":"<device-id-from-list_devices>","settingName":"workspace-path","operation":"set","value":"/home/example/Projects","expectedRevision":"<64-character-current-sha256-readback>"}
```

```json
{"deviceId":"<device-id-from-list_devices>","settingName":"machine-name","operation":"set","value":"my-wsl-client","expectedRevision":"<64-character-current-sha256-readback>"}
```

```json
{"deviceId":"<device-id-from-list_devices>","settingName":"browser-port","operation":"unset","value":null,"expectedRevision":"<64-character-current-sha256-readback>"}
```

## Authority, persistence, and runtime evidence

The MCP server parses the closed typed command, authenticates the caller for
`workspace:write`, and sends it **only** to the exact selected client. The
client delegates to `ClientConfig.applyRemote`, which shares the TUI/CLI
validation, revision-checked atomic save and readback. It is not a remote
shell, general config-path write or server-wide setting. Neither the server
nor the tool should edit `config.json` directly.

A remote request that increases trust or expands the environment allowlist
**fails closed** and requires a separate secure local operator confirmation.
A generic permission prompt or forged confirmation flag cannot authorize it.
A stale revision or offline/missing device must never cause a fallback update.

Returned old/new persisted values and revisions describe **durable readback**,
not active runtime changes. Environment allowlists are redacted. The running
client must distinguish values observable in memory from values it cannot
observe. In this private patch, `MaxImportBytes` is directly observable,
whereas other effective runtime fields remain explicitly `NotObserved`,
with `StartupOverride = null`; these gaps need the Worker 24 typed shared
runtime snapshot before P0 acceptance. Workspace/trust/import/browser
changes are restart-bound, machine name is registration-bound, and Native
Health configuration takes effect on a later check. No automatic restart or
re-registration occurs in the tool.

## Test and integration boundary

The lightweight source-bound contract harness is
`dotnet fsi tests/NamedSettingToolContract.fsx` (16 canonical set/unset
positive cases plus negative malformed names/types and revision/bounds).
The real client dispatch and full two-client authenticated end-to-end tests
require the independently accepted W24 shared API and an explicitly leased
test-project compile entry. Do not report a green server build or schema
harness as proving actual workspace discovery or machine registration.

Do not import this work to main, deploy, restart any client, change a live
WSL home/config/device, or activate schedules without an explicit operator GO.
