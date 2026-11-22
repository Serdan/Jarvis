# F2 — published SHA256SUMS URL is malformed in migration/release documentation (P2)

**Frozen source:** `9c0427dde4d25f1e2d684f85a85b1fd6fd0228c7`.

**Location:** `docs/ClientDownloads.md:96-102`, specifically line 101.

**Original requirement:** accurately document the client distribution and SHA256SUMS release channel as part of phase-1 Windows artifact retirement.

**Defect:** the "Public URLs" block gives `https://jarvis2.kehlet.dev/downloads/https://jarvis2.kehlet.dev/downloads/SHA256SUMS` rather than `https://jarvis2.kehlet.dev/downloads/SHA256SUMS`. The duplicated origin/path was introduced by this frozen diff. The separate smoke-test command later in the document correctly shows the latter URL.

**Impact:** an operator who copies the public URL can fail to retrieve the checksum manifest, hindering manual release verification. No need to touch actual publishing or runtime behavior.

**Bounded correction:** remove the duplicated prefix in the public URL. A small documentation assertion is optional; the single-line correction suffices.
