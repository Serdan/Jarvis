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
| `operation` | `read`, `set` or `unset` | `read` obtains the revision without writes; `unset` restores the default |
| `value` | JSON string/number/boolean/string array, null for read/unset | Strict type depends on the setting; no arbitrary JSON fragments |
| `expectedRevision` | required 64-character SHA-256 hex for set/unset; omitted for read | Fail if the client's current config changed since observation; never guess or remove CAS |

Canonical names, strictly case-sensitive:

- `machine-name`: string (1–128 non-control chars, validated locally)
- `workspace-path`: string (existing absolute project-root directory, not a project)
- `trust-level`: string (`none`, `partial`, `full`)
- `allowed-environment-variables`: JSON array of individually valid variable names (leading letter/underscore, then letters/digits/underscore, no empty/delimiter-containing/duplicate entries); each name validated before conversion to the TUI-compatible local representation
- `max-import-bytes`: nonnegative 64-bit integer
- `browser-port`: integer between 1024 and 65535
- `native-health-enabled`: boolean
- `native-health-timeout-seconds`: integer between 1 and 604800

Example requests (illustrative, **not** a request to change a live client). On first use, read the selected device's current revision through this same tool, then pass it back to a set/unset on that exact device:

```json
{"deviceId":"<device-id-from-list_devices>","settingName":"machine-name","operation":"read","value":null}
```

The read reply's `Revision` is the required `expectedRevision` for the subsequent write. Reading is non-mutating; set/unset still reject a missing or stale revision.

```json
{"deviceId":"<device-id-from-list_devices>","settingName":"workspace-path","operation":"set","value":"/home/example/Projects","expectedRevision":"<SHA-256 Revision returned by the read call>"}
```

```json
{"deviceId":"<device-id-from-list_devices>","settingName":"machine-name","operation":"set","value":"my-wsl-client","expectedRevision":"<64-character-current-sha256-readback>"}
```

```json
{"deviceId":"<device-id-from-list_devices>","settingName":"browser-port","operation":"unset","value":null,"expectedRevision":"<64-character-current-sha256-readback>"}
```

## Authority, persistence, and runtime evidence

The MCP server parses the closed typed command, authenticates the caller for
`workspace:write`, and sends it **only** to the exact selected client. For
`read`, the client loads the existing snapshot without writing it. For
`set` and `unset`, the provisional client adapter delegates to
`ClientConfig.applyRemote` for revision-checked atomic save and readback.
This is a **low-level persistence boundary, not yet the complete W24 typed
application service** shared by TUI, CLI and tool. Final runtime and override
reporting must be wired to W24's separately reviewed correction; no server or
tool path may edit `config.json` directly.

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
