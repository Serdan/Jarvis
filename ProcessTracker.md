# Process Tracker

## Goal

Rebuild Jarvis authentication for a public ChatGPT plugin: OAuth-only public access, authenticated local devices, and no model-visible session secret.

## Status

The OAuth/public-plugin migration is complete in the codebase.

## Notes

- The completed ergonomics backlog is documented in `docs/AgentErgonomicsBacklog.md`.
- The completed client activity feedback work is documented in `docs/ClientActivityTasks.md`.
- OAuth architecture is documented in `docs/OAuthMigrationPlan.md`.
- Public submission material is documented in `docs/PluginSubmission.md`.
- Backward compatibility with API-key, Actions, custom-GPT, and copied-session-key surfaces is intentionally not maintained.

## Completed agent-ergonomics work

- [x] 1. Return structured MCP results instead of JSON strings.
- [x] 2. Add ranged file reads with optional line numbers.
- [x] 3. Return line/column/snippet matches from text search.
- [x] 4. Improve PatchFile ergonomics while preserving safety.
- [x] 5. Add first-class directory creation / create-parent support.
- [x] 6. Make process permissions more granular.
- [x] 7. Add project-configured first-class tasks.
- [x] 8. Model incremental job output with ordered events.
- [x] 9. Control inherited environment variables for spawned processes.
- [x] 10. Centralize command-surface metadata.
- [x] 11. Preserve typed errors through MCP.
- [x] 12. Expose explicit session/registration state.

## OAuth / public plugin migration

- [x] 1. Upgrade the MCP SDK.
- [x] 2. Add Auth0 JWT resource-server authentication.
- [x] 3. Publish OAuth protected-resource metadata and authorization challenges.
- [x] 4. Add per-tool OAuth scopes and MCP annotations.
- [x] 5. Remove session-key arguments from MCP.
- [x] 6. Authenticate and register local devices by user identity.
- [x] 7. Route MCP calls to authenticated devices.
- [x] 8. Add native Authorization Code + PKCE login.
- [x] 9. Remove legacy API-key, Actions, and custom-GPT surfaces.
- [x] 10. Add profile identity, portable plugin package, review material, and updated documentation.

## Verification

- Current activity-feedback verification: Client.Tests 106/106; Server.Tests 19/19.
- `dotnet build Jarvis.slnx --no-restore`: 0 warnings, 0 errors.
- Portable `plugin.json` and `mcp.json` parse as JSON and the skill contains valid frontmatter.
