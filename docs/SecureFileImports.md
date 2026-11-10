# HTTPS import trust boundary (JRV-2026-11-03)

## Authorization model

The public Jarvis ImportFile tool already accepts a structured ChatGPT file parameter, with a temporary download_url and file_id. These values are **caller-supplied data, not cryptographic proof of provenance**. The public tool and wire schema remain unchanged.

- **Recognized provider host:** HTTPS at the standard port and a host under the stable oaiusercontent.com domain family. Under PartialTrust, Jarvis auto-allows this structured attachment only if the redirect chain also stays within the provider family.
- **Other HTTPS host:** Under PartialTrust, Jarvis requires local confirmation for the exact command. Only the normalized hostname and selected workspace path appear in the approval prompt, never the temporary signed query. The FullTrust setting explicitly allows these network operations without another dialog.
- **NoTrust:** Always requires a local confirmation, including known provider files.

File IDs, MIME types, filenames, query parameters and HTTP redirects do not turn an unrecognized host into a trusted provider. A future provider hostname not recognized by the current allowlist is still importable with explicit approval; no individual CDN token or ephemeral hostname is hard-coded.

## Network guarantees

The secure importer owns one process-lifetime HttpClient and SocketsHttpHandler; requests no longer allocate a new HttpClient. The handler disables automatic redirects, ambient proxy routing, cookies and decompression, and keeps connections for at most one minute.

Every HTTPS URL and every redirect is parsed and checked; embedded credentials, URL fragments, HTTP downgrades, provider-to-unrecognized redirects under automatic PartialTrust, and more than five redirects are rejected. Explicitly approved generic URLs can redirect to other public HTTPS hosts. Every connection resolves DNS addresses once, rejects any set containing private/non-global addresses, and connects sockets to the vetted IP itself. This avoids DNS rebinding between checking and connecting. TLS/SNI and certificate validation still use the system handler.

Non-public destinations are rejected independently of local approval and include private ranges, localhost, link-local/metadata, carrier NAT, documentation, benchmarking, multicast, and common IPv6 special/translation ranges. A single two-minute cancellation deadline covers redirects, headers and response-body reads.

The sender's requested byte limit and the local configured import limit are enforced against headers and actual bytes. The HTTP body is streamed to a unique sibling staged file with owner-only permissions at creation on Unix, incrementally hashed with SHA-256, flushed, and atomically renamed after the checkout-relative destination is revalidated. Failures and cancellations clean up staged files. Unknown exception messages cannot leak signed download URLs into Jarvis errors or activity logs.

## Limits and compatibility

This is a network egress and file-import protection, not a content scanner or an independent filesystem sandbox. Fully adversarial concurrent replacement of directory ancestors requires descriptor-relative native filesystem operations; that is addressed in the separate atomic-file-mutation task. The secure HTTP client deliberately does not support proxy-only download environments because proxy routing defeats direct IP validation.

The ChatGPT file-parameter interface is unchanged. An attachment that redirects to an unknown external storage domain may require a deliberate policy update; automatic trusted-provider imports do not broaden to arbitrary hosts without operator approval.

The ImportNetworkTests and PermissionPolicyTests suites cover trusted and generic origin authorization, exact grants, spoofing, private IP literals, DNS answer validation/pinning/rebinding, redirect policies and limits, byte caps, request/body cancellation, signed-URL redaction and 0600 staging-file mode. All tests use mock handlers or fake DNS/connect functions without external network dependencies. No server, persisted configuration or protocol changes are required.

Linux integration evidence, November 8, 2026: accepted source `e189ce26d1afc0ef066b7d4212900e2cc7780c26` merged on `main` at `d7fa308cdba1c95b3274ec89cd08508711b16636`. HTTPS-only Release tests passed 454/454 Client.Tests and 95/95 Server.Tests; after the bounded search merge, full Release tests passed 478/478 Client.Tests and 95/95 Server.Tests. Source integration is not evidence of a deployed client.
