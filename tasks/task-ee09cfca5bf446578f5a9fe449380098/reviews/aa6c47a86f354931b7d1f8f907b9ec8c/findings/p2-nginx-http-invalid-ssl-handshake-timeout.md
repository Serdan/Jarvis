# P2 — Proposed Nginx HTTPS staging configuration contains unsupported ssl_handshake_timeout directive

**Frozen commit:** `a54a939a6ee9aef135382d26eeee8c8fd1110fef`
**Document:** `docs/security/NginxL7Exposure2026-11-12.md`, section `Optional first-stage hardening candidate — staging only`.

The candidate proposes adding this directive *inside the existing HTTPS `server { }`*:

```nginx
ssl_handshake_timeout 20s;
```

This is **not a valid directive in Nginx's HTTP/HTTPS `ngx_http_ssl_module`**. The upstream Nginx documentation lists `ssl_handshake_timeout` under `ngx_stream_ssl_module` (and there is a mail equivalent), with `stream, server` context, not HTTP `server`. The HTTP SSL directive list has no `ssl_handshake_timeout`. Upstream Nginx issue #2235 explicitly resolves a proposal to add HTTP TLS-handshake-timeout controls as wontfix, explaining that HTTP TLS handshake timing is bounded by `client_header_timeout`.

References (independently checked 2026-11-12):
- https://nginx.org/en/docs/http/ngx_http_ssl_module.html
- https://nginx.org/en/docs/stream/ngx_stream_ssl_module.html#ssl_handshake_timeout
- https://trac.nginx.org/nginx/ticket/2235

Consequences: blindly copying the supplied staging fragment into the existing HTTP TLS vhost would cause `nginx -t` to fail due to unsupported directive, preventing the documented safe staged rollout. The document correctly admits the *draft* has not been tested and instructs operators to run `nginx -t` before any reload. Thus no production incident is alleged and a properly followed rollout would not reload invalid configuration. Nevertheless this is an **actionable configuration proposal** in the deliverable and violates the acceptance goal of a reviewable, realistic staging candidate.

**Required correction:** remove the invalid `ssl_handshake_timeout` line from the HTTP vhost sample; rely on the supported `client_header_timeout` with an accurate explanation of its relationship to HTTP TLS handshake timing. Revise any claim that HTTP has a separate default `ssl_handshake_timeout 60s`. Leave proxy read/send limits, legitimate streaming and no-per-IP-quotas policy intact. If a separate TLS handshake deadline is truly desired, evaluate supported alternatives and their tradeoffs instead of borrowing an unrelated Nginx stream directive. Add a staging syntax-check result if a compatible isolated Nginx 1.28 installation is later available; do NOT test a modified config against the live production process without separate authorization.

**Severity:** P2, blocking this documentation review. Source/doc/config incompatibility independently established from authoritative upstream module documentation. No live changes or staging syntax execution were made.
