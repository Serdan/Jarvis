# Jarvis hardening backlog — 2026-10-08

## P1 — Client lifecycle and protocol
- [ ] Fix Unix self-update process replacement: preserve terminal ownership using exec, handle rollback and failure; test actual restart behavior.
- [ ] Design secure refresh-token persistence (OS keyring preferred) and unattended reconnect; preserve explicit logout/revocation and document security tradeoffs.
- [ ] Negotiate client command capabilities at registration; reject unsupported commands server-side with actionable version diagnostics.
- [ ] Bound ExportStore by global/per-user bytes and entries, proactive expiration, concurrency tests.

## P2 — Security and retention
- [ ] Bind pending command responses to authenticated user, connection and generation; reject stale/wrong responses; add tests.
- [ ] Define telemetry retention and implement pruning for connection events and operations; document feedback retention.
- [ ] Add feedback resolution status, timestamp, commit and tool surface; migrate schema and test.
- [ ] Canonicalize executable shell-denial checks and document their non-sandbox security role.
- [ ] Add signed update manifest with embedded public key and signature verification.

## P3 — Quality
- [ ] Add MCP/SignalR integration tests for authorization, isolation, reconnect, timeout, and version mismatch.
- [ ] Enforce restrictive Unix permissions for local persisted data.
- [ ] Align UpdateClient RequiresConfirmation metadata with trust policy.
- [ ] Refactor large command coordinator modules by capability without changing public protocol.

## Acceptance criteria
Each task requires regression tests, documentation updates where applicable, clean build, and a focused commit. Do not restart the interactive Jarvis client or deploy without explicit authorization. Work on client/server source only until rollout is approved.
