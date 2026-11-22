# P0 named-setting product gate — isolated two-native-client matrix (2026-11-22)

**Status: PREPARED, NOT RUN OR ACCEPTED.** Keep W25 protocol source `b12758eec118c39ccd52de740645fa01b3fe4ffa` and test-only review `e25a6e95` distinct from final product evidence. W24 F5 corrected source `1326319b6462fc37ddbc4687ba87004f1869a026` is frozen, with fresh independent review `f5b2a901654248d7993c50e812deb124` now **Accepted**, decision seq11603. W24's unfiltered Release Client job `ce91c4b785d14c97af429eb4258a88a5` exited 0 (1370 passed, 0 failed, 1 skipped). W24 is now source-accepted, but this does not establish a W24+W25 composed product. Import only into a disjoint isolated candidate after source collision checks.

## Source custody and integration prerequisites

- W25 exclusive paths: typed protocol `Common/Messages/AgentCommand.fs`, selected-device transport `Client/SignalR/Client.fs`, `Server/McpTools.fs`, `Server/Services/ClientService.fs`, new isolated tests/docs, and only the explicitly leased project-file entries. Do not touch W24 `ClientConfig.fs`, `ConsoleTui.fs`, `Program.fs` or tests. Do not edit shared project files without fresh source check.
- W25 private **staging** adapter now calls `ClientConfig.tryRunningContext()` then `ClientConfig.applySettings RemoteAgent runtime startupArgs expectedRevision mutation` for mutations. First-use `read` calls `ClientConfig.load` and `resolve` only; never saves. It does not accept remote caller provenance, paths, permission elevation, or runtime snapshots.
- The adapter compiles only against W24's verified typed app service; the separately runnable `Client.Tests/NamedSettingProduct/NamedSettingProduct.Tests.fsproj` is the first isolated focused check. It is not full native/two-client acceptance.
- Composite only after W24 frozen corrected source is **independently Accepted**, with exact Git ancestry and source file collision check. No unaccepted W24 source in reviewed product candidate.
- Independently freeze final composed source, review it, then consider any main integration under a separate Operator GO.

## Authenticated disposable E2E runner — mandatory, still to execute

### Exact non-production boot and genuine OAuth prerequisites

The native client endpoint is build-selectable without changing source: `Client/Client.fsproj` generates `BuildInfo.ServerUrl` from build property `JarvisServerUrl`. Build disposable client binaries with `-p:JarvisServerUrl=http://127.0.0.1:<PORT>/client`, where the port belongs to a newly spawned test server. Server `Program.fs` uses production JWT bearer authentication, issuer discovery/JWKS, audience validation and a `client:connect` scope check; issuer metadata MUST be HTTPS. A mock `UserService.Register` call or permissive test principal does not prove this authentication boundary.

Host a disposable HTTPS OIDC issuer providing discovery/JWKS and `/oauth/token` refresh, with an ephemeral test signing key, separate signed account subjects, suitable audience and scoped MCP/client tokens, and a test CA trusted **only by child processes**. Native `OAuth.fs` already supports `JARVIS_AUTH0_DOMAIN`, `JARVIS_OAUTH_AUDIENCE` and `JARVIS_OAUTH_CLIENT_ID`. It reads an owner-only `oauth-refresh.json` in each client's isolated `XDG_CONFIG_HOME` (derived from `ClientConfig.configPath()`), then redeems its test refresh token. Preseed issuer/audience/clientId/refreshToken for the correct child identity and assert that neither client attempts browser sign-in. Never copy live Auth0 credentials or weaken production JWT validation.

Run two actual native JarvisClient processes A/B with separate `HOME`, `XDG_CONFIG_HOME`, token cache, config, workspace root and persisted device UUID. Launch both against the isolated server; allocate a bounded pseudo-terminal only if ConsoleTui needs one. Disable browser port and schedules, bind loopback only, assign a disposable database path, and fail preflight if a URL, issuer, config path or project root resolves outside the fixture. Capture child PIDs, registered device UUID/name, authenticated account, connection identity and clean shutdown.

Use a real signed test MCP token to invoke the **single** `update_client_setting` tool with explicit device B. Require real ingress to `ClientService.SendCommandToUser`, real SignalR command/reply correlation, `Client.SignalR.Client.receiveCommand`, W24 `ClientConfig.applySettings RemoteAgent`, atomic CAS/save and independent on-disk JSON reread. Keep both native clients connected. First-use `read` must reveal B's real revision without writing; Set/Unset must require its exact SHA-256 revision. Independently hash A/B JSON before/after every positive and negative. A stays byte-identical when B is addressed, including wrong-account, offline B and unknown-device requests. Same-config concurrent writes must produce one winner, one stale-conflict loser and no partial write.

Negative auth controls require missing/invalid/expired JWTs, wrong audience, missing `client:connect` or MCP write scope, and another account's device ID to fail **before** any client mutation. For root/name, verify fresh process project discovery and actual server registration metadata after re-registration; an in-process `Runtime` constructor alone is insufficient. Re-run W24's A-to-B-to-second-TUI-Save F5 under the connected registration loop, including failure and CLI masking.

If disposable HTTPS trust, native OAuth refresh, TUI process launch or server command routing cannot be made callable, record the precise missing non-production test seam and keep the final gate HOLD. Do not silently substitute stubbed sessions or claim full authenticated E2E.


Create **two actual native Jarvis client processes** A and B, each with unique disposable `HOME`, `XDG_CONFIG_HOME`, config file, workspace root, device UUID and registration name. Start a disposable authenticated Jarvis server/SignalR endpoint (or equivalent production-bound authenticated protocol harness) with separate account/session scopes and `workspace:write` authorization. Relay the **real** `UpdateNamedSettingCommand` via `ClientService.SendCommandToUser` to each client's `Client.SignalR.Client.receiveCommand`, never fake a response from a stubbed endpoint. Server must own routing; client must execute actual shared `applySettings` + CAS + disk readback. Test-only authority must be minted inside harness, never exported to a live client.

For every command collect account, target device ID, session/generation, incoming typed command, config path/revision, old/new stored value (secrets redacted), current observed runtime value, startup overrides, activation flags and terminal outcome. Read the on-disk JSON independently after each write. Use strong assertions for before/after identities and SHA-256 revisions. In a new native client process, verify the saved workspace root selects the new project set and the saved machine name reaches actual registration metadata. Keep runtime A active to verify it **does not** magically switch roots.

### Required cases

| Case | Positive proof | Mandatory negative or control |
| --- | --- | --- |
| Device routing/auth | A and B both connect, own config files, selected B write changes only B; A stays byte-identical | missing/offline/stale B, foreign account, missing scope, malformed ID and wrong selected session all fail without fallback |
| First-use and CAS | read returns revision with no write; set with pinned revision changes only chosen key; unset restores default and returns new revision | missing or stale revision; A/B race against same config; rejected operation never changes bytes, revision or other keys |
| Workspace root | root A has A-only project; save B root via tool, disk readback, fresh native process lists B-only project and per-project paths under B | active A runtime remains on A, CLI `--path` override remains effective and displayed, missing/relative/nonexistent path fails without fallback |
| Machine name | save new name; connected-loop registration/re-registration emits the new name and server/device listing sees same device UUID | second TUI Save after A→B retains B (W24 F5), failed registration preserves A, CLI `--machine-name` mask and collision/no duplicate refresh |
| Trust/env | decreases and permitted removals persist using same local service; local TUI confirmed path works as user-authorized | remote elevation `none → partial/full`, unset-to-higher-default and any env expansion rejected before save; forged approval never accepted |
| All other five fields | import limit, port, native Health enabled/timeout, allowlist reductions and their effective consumers, set/unset/default readback, restart/next-check flags | wrong JSON wire type/enum/alias/limit, I/O denial, config corruption/symlink, and any false applied-now classification |
| UI/CLI parity | same result record/persistence boundary for TUI Save, CLI set/unset and remote tool | TUI cancel/decline, stale two-save A→B case, startup overrides, no phantom success after CAS/storage error |

Note: There are eight canonical keys in total. Trust and environment expansion are intentionally **not** remotely settable to higher privileges. The "all settings" matrix must explicitly report these denial semantics, not falsely count them as missing positive capabilities.

## Release evidence protocol (HOLD)

1. Obtain accepted W24 corrected immutable SHA and accepted review verdict; verify W25 exact ownership/disjoint status. Do not infer acceptance from focused tests or unreviewed source.
2. Construct a disposable composition at exact accepted W24 and W25 source heads; retain an auditable manifest listing each path's Git blob and source commit. Keep main untouched.
3. Run isolated focused `dotnet test Client.Tests/NamedSettingProduct/NamedSettingProduct.Tests.fsproj --configuration Release --logger trx` plus genuine two-native authenticated harness. Capture process exit, terminal logs, TRX passed/failed/skipped and source fingerprint; do not use previous UNKNOWN job exit as PASS.
4. Run **unfiltered** .NET 11 Release `Client.Tests/Client.Tests.fsproj` and `Server.Tests/Server.Tests.fsproj` with separate source-bound TRX, plus protocol tests for changed wire schema. No duplicate uncertain job replay. Report unrelated preexisting perf issues distinctly.
5. Submit **new** immutable independent final product review against the exact complete candidate. Explicitly distinguish test-only route acceptance, W24 source acceptance, composed source acceptance and full release proof. A reviewer owns the verdict.
6. Until every prerequisite is proven, mark the product gate **HOLD**; no main push, deploy, restart, operator profile changes, live WSL writes, schedules or cutover.

### Current exact evidence boundary

- Already reported: W25 `b12758ee` partial protocol Accepted; W25 `6e95f0e` two-client routing **test-only** Accepted; neither is final product acceptance.
- W24 `730c6ce` F5 finding adjudicated NARROW seq11526; corrected source `1326319b6462fc37ddbc4687ba87004f1869a026` has W24-only full Client job exit 0 / 1370 pass / 0 fail / 1 skipped. Independent review `f5b2a901654248d7993c50e812deb124` **Accepted** seq11603. This is W24 source acceptance, not composed W25 evidence.
- Private W25 staging adapter and test-project sources are not accepted and are currently uncompiled pending the W24 typed service. No full source-exact Client/Server terminal/TRX can be claimed from this staged change.
