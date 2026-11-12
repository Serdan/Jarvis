# Independent frozen review — read-only production Nginx L7 flood exposure audit

**Verdict: RequestChanges — one P2 invalid staging Nginx directive.**

- Task `task-ee09cfca5bf446578f5a9fe449380098`, independent review `aa6c47a86f354931b7d1f8f907b9ec8c`.
- Frozen `a54a939a6ee9aef135382d26eeee8c8fd1110fef` on `docs/security-nginx-l7-audit`, base/current main `5dc2803b573024a31bc0facd302d43404516ce5f`. Exactly one added file `docs/security/NginxL7Exposure2026-11-12.md`. No source, service, Nginx configuration or infrastructure edits.
- Scope: evidence-grounded, *read-only* assessment and tentative staging/rollback proposal without reintroducing arbitrary authenticated MCP or shared-IP quotas. It is not a hardening implementation or operator authorization to change production.

## Positive evidence independently checked

1. The report distinguishes native DigitalOcean L3/L4 defense from unsupported claims of HTTP L7 protection; describes pre-auth HTTP/TLS/CPU exhaustion even with OAuth, identifies that the historical `McpSubjectRate` primarily targeted already-authenticated traffic, and explicitly does **not** claim an attack was detected. One legitimate account uses many agents/devices/NAT paths, so blanket per-IP Nginx `limit_req/limit_conn` could block correct client fanout. Advice favors low-cardinality, privacy-safe monitoring and demand-driven edge WAF or hardware upgrade rather than arbitrary throughput limits.
2. Live independent read-only `ssh -o BatchMode=yes ... root@jarvis2.kehlet.dev 'sudo -n nginx -T | grep -E ...'` **confirmed** `worker_connections 768`, `proxy_pass http://127.0.0.1:5095;`, `proxy_read_timeout 3600;`, `proxy_send_timeout 3600;`, `proxy_connect_timeout 60;`; no explicit `limit_req`, `limit_conn`, `client_header_timeout`, `client_body_timeout`, `ssl_handshake_timeout` or `client_max_body_size` in the filtered effective config. This was read-only and output was filtered to exclude credentials, request URLs or visitor IPs. Does not independently verify all claimed deployed app binary versions or a complete live traffic distribution.
3. Connected DigitalOcean `droplet_list` independently confirmed `JarvisServer2` active, `s-1vcpu-512mb-10gb`, Frankfurt `fra1`, provider monitoring off. This is inventory evidence, not peak usage or a capacity benchmark.
4. Official Nginx `ngx_http_core_module` docs confirm `client_header_timeout 60s`, `client_body_timeout 60s` (between reads), and `client_max_body_size 1m` defaults. Report correctly treats 1 MiB HTTP body versus 12 MiB SignalR messages as a potential transport-specific interoperability issue rather than claiming all WebSocket frames are subject to the HTTP POST body limit. SSE buffering/long-lived 3600s proxy timeouts, Certbot-aware config backup, Nginx syntax check before graceful reload, post-rollback validation and absence of production stress are covered. No naive bot challenges on MCP/SignalR.
5. Worktree exact frozen HEAD `a54a939a6ee9aef135382d26eeee8c8fd1110fef` clean; `git diff --check 5dc2803..a54a939a` exit 0; `git merge-tree --write-tree main a54a939a` exit 0 with tree `53d9490538584c659f4b2bf0c3cde61980cb8f94`. Documentation-only, so no .NET or live load test was necessary or performed. Owner explicitly notes draft could not be parsed against staging Nginx in this environment.

## Blocking finding — P2 invalid illustrative Nginx staging fragment

`findings/p2-nginx-http-invalid-ssl-handshake-timeout.md`.

In the report's proposed HTTPS `server { }` fragment, `ssl_handshake_timeout 20s;` is **not an Nginx HTTP SSL directive**. It is supported in `ngx_stream_ssl_module` (and mail), not the `ngx_http_ssl_module` serving the live vhost. Upstream Nginx ticket #2235 (closed wontfix) confirms HTTP TLS handshake deadline is governed by `client_header_timeout`. Official refs:
- https://nginx.org/en/docs/http/ngx_http_ssl_module.html
- https://nginx.org/en/docs/stream/ngx_stream_ssl_module.html#ssl_handshake_timeout
- https://trac.nginx.org/nginx/ticket/2235

An operator copying this sample into the live HTTP vhost would encounter an unknown directive/context syntax error at `nginx -t`; following the documented syntax-check-before-reload protects live service, so **no production outage is alleged**. Still, this explicit staging candidate is invalid and not reviewable/deployable as written, an avoidable blocker for a security rollout artifact. Remove `ssl_handshake_timeout` from the HTTP example, explain `client_header_timeout` scope accurately, keep all legitimate agent concurrency and upstream streaming protections; optionally validate an isolated full Nginx 1.28 staging vhost before resubmission, never change live production without operator approval.

## Decision

**RequestChanges**, one blocking P2 correctness finding. Overall read-only exposure assessment, NAT-safe threat model, evidence limitations and operator-controlled rollback are sound. Submit a corrected frozen documentation commit for re-review; no integration, deployment, Nginx reload, infrastructure resizing or production stress authorized or performed.
