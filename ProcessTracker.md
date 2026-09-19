# Process Tracker

## Goal

Improve Jarvis based on direct agent use during a substantial review/edit/test session, reducing unnecessary RunCommand use and making the MCP surface more structured, efficient, and secure.

## Status

Items 1-8 complete. Starting item 9: spawned-process environment policy.

## Notes

- The approved backlog is documented in `docs/AgentErgonomicsBacklog.md`.
- Work in priority order unless a dependency requires otherwise.
- Keep compatibility with the legacy `/agent` HTTP surface where practical.
- Verify each coherent change with focused tests plus the normal build/test workflow.

## Subtasks

- [x] 1. Return structured MCP results instead of JSON strings.
- [x] 2. Add ranged file reads with optional line numbers.
- [x] 3. Return line/column/snippet matches from text search.
- [x] 4. Improve PatchFile ergonomics while preserving safety.
- [x] 5. Add first-class directory creation / create-parent support.
- [x] 6. Make process permissions more granular.
- [x] 7. Add project-configured first-class tasks.
- [x] 8. Model incremental job output with proper stdout/stderr offsets or ordered events.
- [ ] 9. Control inherited environment variables for spawned processes.
- [ ] 10. Generate external command surfaces from one command specification.
- [ ] 11. Preserve typed errors through MCP.
- [ ] 12. Expose explicit session/registration state.

## Verification

- `dotnet scripts/build.cs test`
- `dotnet build Jarvis.slnx --no-restore`
- Review git diff and status at coherent checkpoints.
