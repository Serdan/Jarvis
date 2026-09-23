# Process Tracker

## Goal

Rebuild Jarvis authentication for a public ChatGPT plugin: OAuth-only public access, authenticated local devices, and no model-visible session secret.

## Status

The 12 approved agent-ergonomics items are complete. OAuth/public-plugin migration is in progress.

## Notes

- The completed ergonomics backlog is documented in `docs/AgentErgonomicsBacklog.md`.
- The current migration plan is documented in `docs/OAuthMigrationPlan.md`.
- No backward compatibility is required for the old API-key, Actions, custom-GPT, or copied-session-key surfaces.
- Verify each coherent change with focused tests plus the normal build/test workflow.

## Completed agent-ergonomics work

- [x] 1. Return structured MCP results instead of JSON strings.
- [x] 2. Add ranged file reads with optional line numbers.
- [x] 3. Return line/column/snippet matches from text search.
- [x] 4. Improve PatchFile ergonomics while preserving safety.
- [x] 5. Add first-class directory creation / create-parent support.
- [x] 6. Make process permissions more granular.
- [x] 7. Add project-configured first-class tasks.
- [x] 8. Model incremental job output with proper stdout/stderr offsets or ordered events.
- [x] 9. Control inherited environment variables for spawned processes.
- [x] 10. Generate external command surfaces from one command specification.
- [x] 11. Preserve typed errors through MCP.
- [x] 12. Expose explicit session/registration state.

## OAuth / public plugin migration

- [ ] 1. Upgrade the MCP SDK.
- [ ] 2. Add Auth0 JWT resource-server authentication.
- [ ] 3. Publish OAuth protected-resource metadata and challenges.
- [ ] 4. Add per-tool OAuth scopes and MCP annotations.
- [ ] 5. Remove session-key arguments from MCP.
- [ ] 6. Authenticate and register local devices by user identity.
- [ ] 7. Route MCP calls to authenticated devices.
- [ ] 8. Add native authorization-code + PKCE login.
- [ ] 9. Remove legacy auth/Actions/custom-GPT surfaces.
- [ ] 10. Add plugin instructions/metadata and update deployment/docs/tests.

## Verification

- `dotnet scripts/build.cs test`
- `dotnet build Jarvis.slnx --no-restore`
- Review git diff and status at coherent checkpoints.
