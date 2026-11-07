# P2 security review: HTTPS imports (JRV-2026-11-03)

Reviewed commit: e189ce26d1afc0ef066b7d4212900e2cc7780c26
Branch: fix/secure-https-imports
Task: task-0eebda7cc2f642a2ad2a707800d75684
Reviewer: Jarvis Lead (self-review, no separate agent)

## Trust and network boundaries

- The existing ChatGPT openai/fileParams surface remains unchanged. The model-supplied file_id, MIME type and filename are never treated as provenance; only a normalized HTTPS provider domain qualifies for PartialTrust automatic approval. Other HTTPS hosts require exact local authorization, or the user's deliberately configured FullTrust; NoTrust still prompts. Only the hostname and destination path appear in the prompt, not ephemeral signed query credentials.
- All initial and redirected URLs are restricted to HTTPS, without embedded URI credentials, fragments, internal literal IP destinations or downgrade. The trusted attachment path cannot silently broaden to a generic public CDN on an unapproved redirect. Generic explicitly authorized imports can follow public HTTPS redirects, with the same network-address checks. Five hops maximum.
- The transport disables automatic redirects, proxies, cookies and decompression. SocketsHttpHandler.ConnectCallback resolves hostnames, requires every A/AAAA candidate to be globally routable, then connects to a checked IP address without a second resolution. TLS and hostname certificate validation remain enabled through the normal .NET handler. Connection reuse holds only previously validated public sockets.
- The source-level policy excludes private, loopback, link-local, metadata, carrier NAT, reserved, documentation, benchmarking, multicast and common IPv6 special/transitional ranges. Both direct IP literals and DNS answers are checked.

## Data and filesystem handling

- A process-lifetime reusable HttpClient replaces per-call allocation. One two-minute linked cancellation deadline covers redirects, headers and body streaming, including endpoints that never send an end-of-body marker.
- Requested/local byte limits are checked against Content-Length and actual bytes read, with subtraction-based overflow-safe arithmetic. Files stream to unique sibling stages with Unix 0600 set at creation and incremental SHA-256. They are flushed before atomic rename, with selected project/worktree containment rechecked immediately before publication. Failure paths remove partial stages. Exposed error messages do not include URLs, query signatures or arbitrary network exception text.
- The existing source path resolver handles checkout boundaries. Descriptor-relative native filesystem operations needed against an actively malicious same-user process switching ancestors are a separate P1 task (atomic file mutations) and are not claimed solved here. The new import implementation does not weaken the current path policy.

## Tests and verification

- Full Client Release: **426/426 passed**.
- Full Server Release: **95/95 passed**.
- Focused secure ImportNetwork and PermissionPolicy: **64/64 passed**.
- Regression tests cover trusted and spoofed provider hostnames, local confirmation and exact grants, public/private and mixed DNS addresses, DNS rebinding pinning, redirect policies and HTTPS downgrade, limits and unknown-length body streams, timeouts for both headers and body, redaction of secret-bearing network exception text, and owner-only Unix staging permissions.
- Git diff --check: clean.
- Tests use deterministic fake HTTP handlers and DNS/connect delegates; native live TLS routing was not penetration-tested against external systems. No dependency on external file services is introduced into CI.

## Operational tradeoff

The conservative provider-origin rule is based on the stable oaiusercontent.com file-delivery domain family and is not cryptographic provider attestation. If ChatGPT adopts another stable host or redirects to external object storage, PartialTrust may require an explicit local confirmation or policy update; it will not silently follow an unrecognized external host. This intentional restriction is documented in docs/SecureFileImports.md. It preserves safe fallback rather than pretending every future provider host is automatically trusted.

**Disposition: Accepted for integration planning, not authorization to deploy.** No unresolved source-level blocking issue was identified in the inspected HTTPS import boundary. This is a same-agent self-review, not an independent security audit, and it does not claim native adversarial filesystem or live network penetration verification. No agent notifications sent.
