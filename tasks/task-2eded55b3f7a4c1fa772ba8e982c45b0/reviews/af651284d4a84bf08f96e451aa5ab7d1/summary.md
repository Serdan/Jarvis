# Frozen source review: P3 per-user MCP capacity limits

**Source:** `refs/heads/feat/mcp-user-capacity` at `1738f25eb3a277222a4d2ca41ebe892402ae338b`. **Target:** `refs/heads/main`. **Reviewer:** Jarvis Lead (same-session self-review; there is no separate reviewer for the Jarvis project). **Task:** JRV-2026-11-10.

## Invariants and design

- HTTP `/mcp` is authenticated first and then rate-limited through ASP.NET Core middleware by SHA-256 of the canonical JWT `sub`, not by remote IP/proxy. Fixed-window 240 requests per 10s, zero queue and HTTP 429 with positive integer Retry-After. The existing `/client` and `/exports` policies remain independent; public OAuth metadata/challenge endpoints remain unthrottled.
- Stateless MCP discovery/initialize/list methods share the subject's request-rate allowance, but only actual `tools/call` invocations enter the tool-concurrency budget. This avoids starving cheap discovery behind device-facing requests that may await permission for minutes.
- `McpUserCapacity` has per-subject concurrency limit 24, FIFO queue length 16, queue deadline 5s. Immediate saturation and queue timeout produce an MCP `CallToolResult` with `IsError=true` and a 2-second retry hint; not HTTP 429 because the MCP call reached JSON-RPC evaluation. A successfully admitted tool is no longer subject to queue timeout.
- Authenticated tokens missing a usable `sub` are still rate- and concurrency-limited in a shared fallback bucket rather than bypassing protection. Tool-level identity/authorization checks remain intact.
- Each subject's concurrency limiter is disposed and removed under lock after its final active or waiting invocation leaves. Queue cancellation and operation exceptions cannot leak permits or subject entries.
- `ClientService` observes HTTP `RequestAborted` while awaiting SignalR dispatch and correlation response. It cancels the now-unobservable correlation without changing connected-device health; the original device generation/user/connection validation remains. A dispatched command may still execute locally after disconnect and requires operation-ID based reconciliation.
- Settings are typed, bounded and validated once at startup; ordinary configuration/environment variable overrides allow measuring/tuning without a distributed limiter.

## Load evidence

Measured the existing private local activity JSONL corpus in aggregate: 169,558 rows, 85,069 distinct activities over 15 days; observed peak 13 starts/s and 34 starts/10s, with p95 duration ~12.9s. **This is client activity, not authenticated HTTP concurrency or production throughput.** Selected subject HTTP rate budget ~7 times the observed peak activity start burst, plus 40 concurrent active+queued tool slots; this generous initial sizing is provisional and documented in `docs/reviews/McpCapacity.md`. No claims about measured global host capacity.

## Negative-space tests

- Fixed-window partition tests prove independent users behind the same IP have separate quotas, and rejection includes positive `RetryAfter` metadata.
- Real Kestrel loopback middleware test (synthetic principals, no production Auth0 bypass) verifies four successful `/mcp` requests for A, 429/Retry-After for fifth, independent four requests for B at the identical IP, and a public unthrottled metadata route.
- Tool limiter tests cover 40 simultaneous calls and immediate 41st rejection, independent per-subject allocations, FIFO completion order, deadline exhaustion without launching work, queue cancellation, exception cleanup, and continuation of admitted execution beyond the queue deadline.
- Typed SignalR bridge test cancels the HTTP request after receiving the correlation ID, proves tracker cannot subsequently complete that correlation, and verifies the device stays Registered with no spurious failure.
- Existing real-server `McpAuthenticationTests` check Bearer 401/challenge and public OAuth metadata on actual `/mcp`; existing SignalR scope and generation tests remain.
- Focused source review tests pass in Release. The **final full source Release** on exact frozen `1738f25` completed exit code 0: **108/108 Server and 498/498 Client**, job `ee8ecd0ed2df443e8c1f853b96b0fea0`. The earlier pre-extra-tests run also passed 106/106 Server and 498/498 Client.
- `git show --check HEAD` passed, source branch is clean/pushed, and `git merge-tree --write-tree main feat/mcp-user-capacity` produced a conflict-free tree. No protocol version or client wire shape changes.

## Explicit boundaries

- No global all-user capacity governor; the P3 finding specifically targets per-user quotas on a deliberately single-instance service. A large number of distinct authenticated principals can consume aggregate host capacity and would require a separate measured host-pressure design.
- Retry-After on an MCP tool busy result is text rather than an HTTP header; only HTTP rate rejections get an actual 429 and Retry-After.
- Per-call cancellation stops the server wait and tracking, not necessarily a local command already delivered. Writes still rely on idempotent operation IDs and status checking.
- Production Auth0 token authentication was not replaced/test-overridden. The loopback test exercises the shared production middleware callback with **test-only** principal injection, and the separate real-server auth test protects the OAuth challenge.

**Disposition:** ACCEPT for source integration. No unresolved blocking defects; exact frozen Release gate passed. Do not claim production rollout or actual HTTP concurrency benchmark. Do not deploy here.