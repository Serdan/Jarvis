# Reviewer 4 — Independent early Worker 25 protocol review

**Verdict: ChangesRequested for the EARLY protocol-only source** (not a full P0 named-setting tool acceptance and not authorization for main import). Claimed review `a62f915e76e14abd8f3e43a4d56dbbf2` under `_reviews`; exact frozen source `ce1bd9fd7ee60663a300f2405896b84e65ec0d00`, base `dcac884fc061869572b12a03befc12f11c009f2f`. Seven changed paths, independent read-only source diff; no source/main/deploy/live client modifications.

## Independently verified positives

- Closed eight-setting wire enum; typed JSON parser rejects aliases, mismatched scalar types, missing/malformed SHA-256 revision. `Server/McpTools.fs`, `Common/Messages/AgentCommand.fs`.
- Exact `deviceId` required by tool before send; request-argument device routing overrides metadata and is forwarded without fallback through `RequestDeviceRouting.withMetadata` and `ClientService.SendCommandToUser`; existing user-scoped selected-device behavior rejects cross-account and missing explicit devices. This is source and unit-level evidence, **not yet** an authenticated two-device end-to-end remote mutation test.
- MCP `workspace:write` scope and typed command permission classification present. Client adapter references `ClientConfig.applyRemote`, rather than writing a config file itself. Independently inspected W24 private implementation contains trust/allowlist expansion checks, but those are a **separate unintegrated dependency**, not accepted runtime proof.
- Independently executed exact W25 worktree .NET 11 Release Server focus **5 PASS / 0 FAIL / 0 SKIP** (TRX `/tmp/jarvis-reviewer4-w25/reviewer4-w25-early-protocol.trx`), full Server **565 PASS / 0 FAIL / 0 SKIP** (TRX `/tmp/jarvis-reviewer4-w25/reviewer4-w25-server-full.trx`), and `dotnet fsi tests/NamedSettingToolContract.fsx` PASS (16 canonical positives, 14 malformed negatives and revision/bound cases). Neither suite exercises actual client persistence or local permission UI.

## Findings requiring narrow correction

1. **F1 / P1** — `expectedRevision` is mandatory for initial remote mutation, but the ordinary authenticated MCP surfaces (including `ListDevices`) have no way to read that device's current settings revision. The command therefore cannot perform first use without out-of-band means. See `findings/revision-discovery.md`. Preserve CAS; resolve through same exact-device authenticated readback path, avoiding a second settings writer.
2. **F2 / P2** — The `allowed-environment-variables` JSON string list is flattened to CSV, while client parsing splits on comma **and** semicolon; malformed one-element names can be silently reinterpreted as multiple valid variables. See `findings/allowlist-array-identity.md`. Validate each name before flattening or preserve a typed list through shared service; add negative controls.

## Explicit evidence/acceptance boundary

The frozen W25 branch is intentionally **Client RED/incomplete**: the referenced `ClientConfig.applyRemote` implementation is owned by W24, and `PermissionPolicy` custody/coverage is unresolved. Neither a Client Release compile, full Client TRX, real TUI/CLI/tool unified path, actual workspace-path persistence/restart discovery, machine-name re-registration, hostile trust escalation through dispatch, CAS mutant, nor two-device authenticated E2E is accepted by this review. `EffectiveRuntimeValue` is explicitly unobserved except MaxImportBytes; that honesty is appropriate for partial scope, but full requirement remains open. No author source edit, main import, deploy, or live WSL change undertaken.

The two findings are specific to protocol usability/value fidelity; do not conflate them with the acknowledged W24/PermissionPolicy integration gap or expand the task into a wholesale redesign. Any scope-remedy dispute belongs to independent adjudication.
