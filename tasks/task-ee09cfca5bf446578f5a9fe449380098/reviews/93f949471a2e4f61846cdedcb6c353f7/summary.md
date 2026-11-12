# Independent corrective re-review — Nginx HTTP TLS handshake timeout guidance

**Verdict: Accept.** The single P2 defect from previous review `aa6c47a86f354931b7d1f8f907b9ec8c` is corrected on frozen `4a7bf4e4082a4a7974e4fbbdd2605c256272ef43`, branch `docs/security-nginx-l7-audit`, base/current main `5dc2803b573024a31bc0facd302d43404516ce5f`. This is a documentation-only re-review, not authorization for any production Nginx change.

## Correction checked in exact frozen diff

Only `docs/security/NginxL7Exposure2026-11-12.md` changed relative to first rejected `a54a939a6ee9aef135382d26eeee8c8fd1110fef`.

- Removed `ssl_handshake_timeout 20s;` from the optional HTTPS **HTTP** `server` staging snippet; it belongs to the Nginx `ngx_stream_ssl_module`/mail context, not `ngx_http_ssl_module`.
- Correctly retains supported `client_header_timeout 20s;` as the only header+HTTP TLS-handshake deadline candidate, together with `client_body_timeout 30s`, idle `keepalive_timeout 30s` and local TCP `proxy_connect_timeout 5s`. The **existing** upstream `proxy_read_timeout 3600` and `proxy_send_timeout 3600` remain intentionally preserved for MCP/SignalR SSE/WebSocket/long-running calls.
- Updated prose and effective-baseline table to explain that standard 60-second `client_header_timeout` bounds HTTPS handshake time in Nginx HTTP, not a separate `ssl_handshake_timeout` default. Replaced misleading HTTP SSL reference description with correct `ngx_http_ssl_module` plus upstream Nginx issue #2235 (closed wontfix).
- Independently cross-checked official current source documentation: `https://nginx.org/en/docs/http/ngx_http_ssl_module.html` lists HTTP SSL directives but not handshake timeout; `https://nginx.org/en/docs/stream/ngx_stream_ssl_module.html#ssl_handshake_timeout` limits stream's directive to `stream, server`; `https://nginx.org/en/docs/http/ngx_http_core_module.html#client_header_timeout` documents syntax, contexts and default `60s`; maintainer comment in `https://trac.nginx.org/nginx/ticket/2235` explicitly says HTTP TLS handshake time is limited by `client_header_timeout`.
- All other original audit findings, uncertainty about representative load, absence of proven L7 WAF, no blind NAT/shared-IP `limit_req`/ `limit_conn`, privacy-safe resource monitoring, staging-only tests, safe `nginx -t`/graceful reload/rollback and explicit operator authorization remain unchanged. No new config/code is proposed for automatic application.

## Verification and boundaries

- Verified exact frozen `git rev-parse HEAD = 4a7bf4e4082a4a7974e4fbbdd2605c256272ef43`, worktree clean.
- `git diff --check 5dc2803..4a7bf4e` exit 0.
- `git merge-tree --write-tree main 4a7bf4e` exit 0, resulting tree `3c7d72ac6db0cf13badeb6db69614f32322cc55a`.
- No .NET tests required for an entirely documentation-only adjustment. As explicitly stated in report, the **illustrative** Nginx fragment has not been parsed against a complete staging Nginx 1.28 configuration; this is appropriately a prerequisite before any separately approved rollout, not a production-ready artifact or a claim of tested deployment. No live production config changed or restarted/reloaded.
- Original review independently verified production read-only `nginx -T` and DigitalOcean inventory; frozen correction neither alters nor expands those claims.

**Verdict: Accept.** No remaining blocking defect found in this single-delta re-review. Technical review only; no merge, production change or deployment performed.
