# Independent re-review — browser DOM MCP authorization on protocol 3.63

**Verdict: RequestChanges only because live paired-browser acceptance is still pending operator consent; the prior P1 server security/dispatch defect is resolved.**

Frozen commit: `94c73e426a9283b7d66f21d63e7cb996a2fcbaa0` on `refs/heads/feature/browser-bridge`, base `28fd0c3d2fa887a8cd7289e07cb65b476de351f9`, target `main`.
Prior rejected review: `6ee449dff04248428ba43bbfb6820974` at `b9dc25b`.

## Verified P1 correction

- `Server/McpTools.fs` replaces non-exhaustive `requiredScope : AgentCommand -> string` with exhaustive `requiredScopes : AgentCommand -> string list`. The previously omitted `BrowserCaptureDomCommand` now requires `Auth.ProcessExecute`, and `BrowserExportDomCommand` requires **both** `Auth.WorkspaceWrite` and `Auth.ProcessExecute`. An additional `Auth.WorkspaceRead` check still applies because export is project-scoped.
- `send` rejects the first missing scope with an OAuth `insufficient_scope` challenge and **never** forwards unauthorized commands. This preserves the existing authenticated user check and agent-state effect forwarding.
- A real routed Server test constructs an authenticated `HttpContext`, fake client SignalR transport/response tracker and invokes the **exact public MCP methods** for both capture/export. It confirms valid requests reach the expected typed command, `worktreeName` maps correctly to `Jarvis::browser-bridge`, and missing process:execute, workspace:write and project workspace:read each deny dispatch with a proper challenge.
- `Common/Common.fsproj` and `Server/Server.fsproj` make **FS0025 warnings fatal**, ensuring new union cases cannot silently bypass protocol/authorization pattern matches.
- Previous client-side activity matcher, protocol 3.63 merge and mono-deadline snapshot export fixes remain present and previously independently inspected. No new P1 or P2 implementation issues identified in this narrow re-review.

## Remaining acceptance dependency

`findings/p2-live-extension-acceptance.md`: The browser task explicitly requires at least one **paired real Chromium/Edge extension** test on a JS-hydrated fixture, covering registered user window, host opt-in, created tab, project-owned script, delay/selector/event DOM waits, checked file export and safe close. The owner confirms this remains pending the operator's consent and extension setup. Mock server/extension tests cannot replace actual live browser acceptance. No unauthorized browser setup or deployment was attempted.

## Independent evidence

- Full merged Client Release suite **572/572 passed**, job `b984cc62e7a14a4592544b4f57f0205e`.
- Full merged Server Release suite **128/128 passed**, job `9a707e468324442d96d4bdc4fc0fffd6`.
- Browser extension Node suite **35/35 passed**, job `b86f2d39634b490fbb8a9597915579f1`.
- **735 tests passed, 0 failed** (572 + 128 + 35).
- Frozen checkout HEAD exact; worktree clean and `git diff --check` clean; `git merge-tree --write-tree main 94c73e4` exits 0, conflict-free.
- No reviewer source modifications, integration or deployment.

**Decision:** RequestChanges solely for the explicit live browser acceptance gate. The public MCP authorization P1 is fixed and independently verified; Lead/operator may plan a consented E2E fixture before re-submission.
