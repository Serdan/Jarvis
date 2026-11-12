# Jarvis production ingress: read-only HTTP L7 flood audit

**Date:** 2026-11-12 (UTC). **Scope:** JarvisServer2 public HTTPS ingress. **Status:** proposal only; **no production changes, flood probes, reloads or deployment**. P2 security follow-up; independent of the accepted urgent authenticated-quota-removal P1.

## Decision

The public endpoint has an **HTTP/application-layer exhaustion exposure**. Anonymous HTTPS requests, deliberately incomplete headers/handshakes or a large volume of ordinary-looking calls can consume Nginx/TLS worker connections, CPU, RAM, buffers and Kestrel work *before* OAuth rejects them. A 1-vCPU/512-MB host has limited spare capacity. DigitalOcean's free DDoS protection covers **L3/L4 only**, not HTTP L7. **No current attack has been established.** Idle snapshots are not a load benchmark.

**Recommended:** do not throttle legitimate authenticated agents, particularly through any shared-IP or NAT quota. Add privacy-safe aggregate resource/error monitoring; optionally stage minimal pre-auth timeout hardening while preserving existing 3600-second upstream proxy windows. Accept the residual L7 risk for a low-exposure one-user service until observed attacks or uptime requirements justify a managed edge L7 WAF. Upgrade Droplet hardware if **measured legitimate** usage saturates it, not as an L7 security substitute.

## Evidence: deployed production, read-only

SSH verified live Nginx `nginx -T` and `nginx -t` (effective existing configuration syntax passes), listeners, systemd resources, and UFW rules. DigitalOcean's read-only droplet metadata and direct DNS lookups establish origin topology. No credentials, visitor IPs, raw access logs, private keys, request URLs or tokens were read or included.

| Area | Verified state |
| --- | --- |
| Droplet | DigitalOcean **JarvisServer2**, Basic `s-1vcpu-512mb-10gb`, Frankfurt `fra1`; provider monitoring **disabled** |
| DNS / edge | Single hostname A record points **directly to this Droplet's public IPv4**; no CNAME/AAAA; no CDN or HTTP L7 edge shield in the observed ingress path |
| Listeners | Nginx on public IPv4/IPv6 TCP 80/443; Kestrel **127.0.0.1:5095** only; UFW active, default inbound deny, `Nginx Full`/SSH allow rules |
| Nginx | **1.28.0 (Ubuntu)**, Certbot TLS termination, TLSv1.2/TLSv1.3; HTTP 80 redirects; `worker_processes auto`, `worker_connections 768` |
| Routes | Static `/downloads/` and privacy routes; catch-all `location /` proxies `/mcp`, `/client`, OAuth metadata, exports and remaining routes to local Kestrel |
| Proxy | HTTP/1.1, Upgrade/Connection forwarding; `proxy_read_timeout 3600`, `proxy_send_timeout 3600`, `proxy_connect_timeout 60` (seconds) |
| Effective HTTP limits | No explicit `limit_req`, `limit_conn`, zones, header/body/keepalive/handshake timeout overrides or body-size override. **Nginx defaults still apply**: 60s header read, 60s body inter-read, 75s idle keepalive, 1-MiB HTTP request body, `proxy_request_buffering on`, `proxy_buffering on` |
| Host snapshot | Load average ~0.13/0.12/0.07, shown RAM 456 MiB total and ~203 MiB available, ~62 MiB swap used; root filesystem 74 percent used (~2.3 GiB free); ~43–49 established TCP sessions in two separate idle snapshots |
| Services | `jarvis.service` active, ~172 MiB cgroup memory, `Restart=always`, no explicit cgroup memory high/max; Nginx active ~15 MiB memory; Nginx logrotate file present |
| Kernel | `net.core.somaxconn=4096`, `tcp_max_syn_backlog=128`, `nf_conntrack_max=4096`. Nginx's 768 worker-connection setting also counts upstream sockets, not 768 guaranteed independent users |

**Evidence limits:** no traffic distribution or production p95/p99 data, attack-pattern confirmation, peak-memory sampling, or staging test. Cloud firewall associations could not independently be enumerated via the available DigitalOcean connector; UFW rules and direct DNS routing were independently confirmed. The source tree on current local main maps OAuth-authorized MCP at `/mcp` and SignalR at `/client`; it does not prove which exact application binary is currently deployed or that the separate P1 quota-removal is live. The production config's existing `nginx -t` passed; **the draft below has not been syntax-checked**.

## Threats and design limits

- **Network floods:** DigitalOcean provides always-on volumetric/protocol protection on OSI L3/L4. This does not defend against HTTP GET/POST floods or slow pre-auth HTTP work.
- **Stalled connections:** Nginx is event driven and already enforces defaults. Reducing TLS negotiation/header deadlines can reclaim sockets sooner, but high-latency clients must be tested. `client_body_timeout` measures inactivity **between reads**, not the total upload duration; a trickle attack can still persist.
- **Request body abuse:** the current default 1-MiB body limit and `proxy_request_buffering on` bound some upstream exposure, but buffering can consume Nginx disk. Never raise body limits without accounting for temporary storage.
- **Well-formed unauthorized floods:** Nginx still accepts TLS and proxies the request; the application evaluates OAuth after work is incurred. No magic local timeout eliminates valid-looking L7 request floods. A distributed attacker could exhaust CPU, memory and finite Nginx worker connections.
- **Public paths:** `/downloads/` and some ASP.NET discovery/export routes legitimately accept anonymous traffic. Export URLs may include bearer-like path components; do not emit raw URLs/tokens in telemetry.
- **Legitimate concurrency:** many agents can share an OpenAI egress IP, proxy or NAT. `limit_req_zone` or `limit_conn_zone` keyed to `$binary_remote_addr` may reject the *only real user* while doing little against distributed L7 traffic. Do not add these. Do not use unverified headers or apparent bearer tokens as Nginx trust/identity. No CAPTCHA/JS challenges on machine-to-machine MCP, OAuth discovery, SSE, SignalR negotiations or WebSocket traffic.

**Possible already-existing interoperability issue:** Nginx's default 1-MiB *HTTP body* ceiling differs from the source SignalR `MaximumReceiveMessageSize=12 MiB`. WebSocket frames do not use ordinary request-body sizing, but a long-polling POST or large MCP HTTP request could receive 413. Measure real transports/payloads before changing either bound. Default response `proxy_buffering on` may delay streaming SSE unless the application sends `X-Accel-Buffering: no`; verify end-to-end streaming before changing buffering or splitting route locations. Do **not** shorten the existing 3600-second upstream read/send timeouts for long-lived connections.

## Optional first-stage hardening candidate — staging only

This is an **additive illustrative snippet**, not a complete replacement vhost or authorized production change. Insert candidate directives in the **existing TLS HTTPS server** and replace **only** the existing local upstream connect timeout inside its current catch-all proxy location. Do not change auth, path handling, upstream read/send timeouts, forwarded headers or application quotas:

```nginx
# Existing HTTPS server { ... }:
client_header_timeout 20s;  # complete HTTP headers; default 60s
client_body_timeout   30s;  # gap BETWEEN successive body reads; default 60s
ssl_handshake_timeout 20s;  # unfinished TLS negotiation; default 60s
keepalive_timeout     30s;  # idle HTTP keepalive; NOT active SSE/WebSocket

# Existing location / { ... }:
proxy_connect_timeout 5s;  # replace 60s; only TCP dial to 127.0.0.1:5095
# Keep proxy_read_timeout 3600; proxy_send_timeout 3600;
```

These are *candidate test values*, **not production-proven thresholds**. They are not per-IP or user rate limits. A shorter local TCP connect timeout can return 502/504 more quickly under upstream overload; shorter idle keepalive can increase connection churn. If genuine MCP clients are harmed, leave that directive unchanged. They cannot fully prevent HTTP floods.

There is **no Nginx, Docker or Podman** binary in the local developer environment, so a realistic staging configuration/syntax check was **not possible this turn**. A separate disposable host/container running Nginx 1.28 should syntax-check the *full vhost* with certificate placeholders; a successful `nginx -t` is necessary but not sufficient for behavior. **Never** test changes by editing or reloading the live host without explicit authorization.

## Verification and safe rollout

1. **Observe first:** use privacy-safe aggregate sampling of CPU saturation, RAM available, swap-in/out, disk free, Nginx active/reading/writing/waiting connections, TCP session totals, service restarts/OOM, queueing, 408/413/429/499/502/503/504 counts and normal authorized request latency. No visitor IPs, subject IDs, authorization tokens, full URLs, bodies or raw access logs in exported metrics. Provider monitoring is currently disabled; assess agent overhead on 512 MB before enabling.
2. **Staging behavior tests:** short bounded anonymous incomplete-header/TLS/slow-body tests **in staging only**; real OAuth discovery, unauthorized responses and authorized MCP tools; realistic concurrent agents from **one simulated shared NAT**; long idle SSE, WebSocket upgrades, SignalR reconnect/long polling, large legitimate bodies and static downloads. Compare p95/p99, errors, memory/CPU/connection use and session continuity. Avoid sustained flood tests on production.
3. **Operator-controlled trial:** after independent review and **separate explicit production approval**, save the exact live Certbot-aware vhost/includes, prepare a minimal diff, run `nginx -t`, then perform a *graceful reload* (never a restart as a validation step). Monitor normal multi-agent sessions and 4xx/5xx/latency/resource deltas. Abort on legitimate client failures, dropped streaming, or notable error/latency regression.
4. **Rollback:** atomically restore saved vhost/includes, run `nginx -t`, gracefully reload; confirm authentication, MCP, long-lived SSE, WebSocket/SignalR, downloads and health. If syntax validation fails, do **not** reload. No live changes were executed for this audit.

## When a managed HTTP shield or hardware upgrade makes sense

A managed **L7 WAF/HTTP DDoS edge** (Cloudflare or equivalent) is warranted if anonymous request bursts, errors, connection slot exhaustion, CPU pressure or an availability target justify added complexity. Use noninteractive HTTP attack-detection rules that preserve machine clients and streaming/Upgrade semantics; test actual OpenAI/LM Studio fan-out. Restrict origin bypass with a reviewed ingress allowlist/origin policy and correctly configured trusted forwarding headers. Merely moving DNS while leaving the public origin directly accessible is not complete shielding.

A larger Droplet is appropriate for demonstrated *legitimate* CPU, memory or latency pressure. Raising hardware capacity does not stop intentional L7 floods. For the present single-user deployment, keep NAT-friendly unlimited authenticated flow, collect low-cost aggregate evidence, and consider the timeout experiment before committing to an edge service.

**Public technical references:** [DigitalOcean DDoS Protection](https://docs.digitalocean.com/platform/ddos-protection/) (L3/L4, no L7); [NGINX HTTP core](https://nginx.org/en/docs/http/ngx_http_core_module.html) (defaults and semantics); [NGINX proxy module](https://nginx.org/en/docs/http/ngx_http_proxy_module.html) (request/response buffering); [NGINX SSL module](https://nginx.org/en/docs/http/ngx_http_ssl_module.html) (TLS handshake deadline).
