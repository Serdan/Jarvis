# MCP user-scoped capacity budgets

## Motivation and evidence

The authenticated `/mcp` endpoint formerly had no HTTP budget, although `/client` and `/exports` used a 30-request/10-second per-IP fixed window. A single long-lived MCP tool can await a local device command for minutes. Applying that existing IP policy to MCP would make unrelated authenticated users behind a shared proxy or NAT compete for the same quota.

Before choosing defaults, the 2026-11-08 local activity JSONL corpus was analyzed **in aggregate** (no commands, paths, user identifiers, or project names exported): 169,558 event rows, 85,069 distinct activity IDs, and 84,489 completed activities with valid elapsed durations across 15 days. Observed local activity starts peaked at 13 in one second and 34 in a 10-second bucket; the 99th percentile among occupied 10-second buckets was 12. Activity durations had median 9 ms, p95 12,888 ms, p99 50,719 ms, and maximum 540,357 ms. These are **client activity measurements, not HTTP /mcp request counts, authenticated-user cardinality, or simultaneous tool execution**. The existing client serializes much of its local work. No direct production HTTP concurrency measurement is claimed.

The default **240 / 10 seconds per subject** rate budget provides approximately 7 times the observed peak 10-second activity starts; the **24 active + 16 waiting** tool capacity admits a 40-agent fan-out without immediate rejection (verified in an in-process burst test). These defaults are deliberately generous and **provisional**. Adjust them based on actual deployment throughput, CPU/memory use, 429 frequency, device response latency and queued-call timeout rates; do not treat the activity proxy as a load test of production infrastructure.

## Two separate controls

| Resource | Default | Scope | Behavior at capacity |
|---|---:|---|---|
| HTTP request starts | 240 per 10 seconds | Authenticated `sub`, hashed into rate-limiter partition key | HTTP **429** with `Retry-After` (ceil to at least 1 second); no rate queue |
| Running MCP tool invocations | 24 | Authenticated `sub` | Further calls enter this user's FIFO queue |
| Queued MCP tool invocations | 16 | Same `sub` | Excess calls immediately return an MCP `tools/call` result with `isError: true` and a retry hint |
| Maximum time waiting for a tool permit | 5 seconds | Queued tool call | Busy tool result with a 2-second hint; execution never started |
| Busy tool result retry hint | 2 seconds | MCP error text | Human- and agent-readable; **not an HTTP `Retry-After` header** |

MCP discovery and initialization consume the HTTP request-rate allowance but **do not** occupy long-lived tool concurrency slots. The tool filter preserves per-call metadata, device selection and agent/session routing for admitted calls. Existing `/client` SignalR and `/exports` policies remain as they were; the public OAuth resource metadata and application challenge endpoints are unaffected. Unauthorized MCP calls retain the Bearer authentication challenge before rate limiting.

A single user can consume their full tool budget without occupying any other user's allocation, even behind the same IP. Authenticated tokens missing a usable subject share a constrained fallback partition rather than bypassing either limit; individual tools still enforce their existing identity and scope checks.

The tool admission timeout covers **queueing only**. Once admitted, it is disabled, and the request's existing cancellation and command-specific deadlines determine the operation's lifetime. On HTTP disconnect, the server abandons its pending SignalR response correlation without marking the connected device unhealthy; this does **not** guarantee that an already-dispatched local operation stops executing. An uncertain outcome should be reconciled with the operation ID before retrying a write.

The in-process tool-limit dictionary releases and disposes a subject's limiter when the last active/queued invocation finishes; no long-lived per-user concurrency entries persist after idle. The request-rate policy is an ordinary ASP.NET Core rate limiter. Budgets are intentionally process-local, appropriate for the single-instance Jarvis server; they do not coordinate across replicas.

## Runtime configuration

The server reads `McpLimits` from its ordinary configuration at startup. With Jarvis's `AddEnvironmentVariables("Jarvis")` prefix, use:

| Environment variable | Default | Valid range |
|---|---:|---:|
| `JarvisMcpLimits__MaxConcurrentTools` | 24 | 1–1024 |
| `JarvisMcpLimits__MaxQueuedTools` | 16 | 0–1024 |
| `JarvisMcpLimits__QueueWaitMilliseconds` | 5000 | 1–60000 |
| `JarvisMcpLimits__RequestsPerWindow` | 240 | 1–100000 |
| `JarvisMcpLimits__WindowSeconds` | 10 | 1–3600 |
| `JarvisMcpLimits__BusyRetrySeconds` | 2 | 1–60 |

Malformed and out-of-range settings fail startup with a field-specific error instead of silently disabling protection. Changing values requires a server restart. Do not lower the concurrent-tool capacity below actual parallel-agent fan-out without measuring queuing and observing agent behavior.

## Verification

- `ServerMcpCapacityTests`: per-subject HTTP partitioning behind one IP, 429 + Retry-After via a **real loopback Kestrel/ASP.NET Core middleware** pipeline (test-only synthetic principals; not a production Auth0 token test), config validation, independent concurrency pools, queue timeout/no unintended execution, queued cancellation, cleanup after a throwing tool, and the 40-simultaneous-call burst.
- `ServerServiceTests.client_request_abort_cancels_outstanding_device_correlation_without_faulting_device`: typed SignalR fake confirms disconnected caller's correlation is removed while device generation and registered state remain valid.
- Existing real-server `ServerMcpAuthenticationTests` cover HTTP OAuth challenge and public metadata behavior independently. The complete Release suites are required before integration.
- Production traffic/telemetry was **not** used to justify an exact concurrency optimum. A later release can measure actual queue depth, rejected `/mcp` calls and p95/99 request latency before tightening defaults.

## Limitations and operational cautions

- An MCP `tools/call` **busy response is application-level** (`isError: true`); it is not an HTTP 429, to preserve the MCP JSON-RPC response envelope. The separate HTTP rate rejection is status 429 with a `Retry-After` header.
- The server cannot undo an already-delivered client-side command merely because the HTTP caller disconnected. Preserve operation IDs and use status inspection for uncertain writes.
- Long-lived tools hold one permit for their entire await, including device-side permission prompts. The budget intentionally restricts the number of such requests, rather than terminating valid work solely because a time threshold was reached.
- The defaults are intentionally permissive for legitimate multi-agent fan-out and are not a guarantee against a large number of independently authenticated principals. A global host-pressure circuit breaker would be a separate design requiring measured aggregate capacity.
