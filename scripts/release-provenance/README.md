# Offline release provenance verifier (schema 1)

This is an isolated evidence inspector, not a publisher, deployment gate, or
release acceptance decision. It does not run the artifact, build, fetch, probe
a host, alter a checkout, emit a receipt, or resume a previous effect. Its only
output is a bounded JSON report on stdout. The caller supplies explicit local
files; do not supply secrets or mutable/live host paths.

From the repository root:

```sh
python3 -B scripts/release-provenance/verify.py /path/to/manifest.json --repo /path/to/local/git/checkout
python3 -B -m unittest discover -s scripts/release-provenance -p test_verify.py -v
```

The optional local repository is used only to look up the exact commit object
and its tree with replacement objects and lazy fetch disabled. No current
checkout status is used as historical build evidence. Omit the repository to
report the source-tree binding as unknown.

## Inputs

Use a JSON manifest with an exact source commit and tree, and one to sixteen
explicit artifact entries. Paths are relative to the manifest's directory
unless absolute. Artifact and receipt paths must be nonempty Unicode scalar
strings of at most 4096 characters, with no NUL or surrogate code points
(including host-specific surrogateescape byte paths). Invalid paths are rejected
before file observation; well-formed but missing files remain unknown.
Use lowercase full Git object IDs (SHA-1 or SHA-256, with the
same length for the commit and tree) and lowercase 64-digit SHA-256 values.
The fixture in `test_verify.py` constructs an entire matching manifest and its
receipts using a disposable local Git repository and synthetic artifact bytes.

Example structure (replace every angle-bracket placeholder with actual values):

```json
{
  "schema": 1,
  "source": {"commit": "<commit>", "tree": "<tree>"},
  "artifacts": [
    {
      "id": "client-linux",
      "path": "JarvisClient-linux-x64",
      "target": {
        "product": "JarvisClient",
        "rid": "linux-x64",
        "configuration": "Release"
      },
      "expectedSha256": "<release checksum>",
      "observedSha256": "<separately observed checksum>",
      "stamp": {"path": "stamp.json", "sha256": "<stamp receipt digest>"},
      "build": {"path": "build.json", "sha256": "<build receipt digest>"},
      "release": {"path": "release.json", "sha256": "<release receipt digest>"}
    }
  ]
}
```

`expectedSha256` can come from the existing publish scripts' SHA256SUMS
entry for the exact artifact. `observedSha256` is an independently supplied
observation of the bytes being compared (not an automatic host read).
Omit an unavailable digest instead of substituting a computed value. The
inspector computes its own SHA-256 over the local file. Missing receipts or
missing receipt pins yield unknown; a digest contradiction yields mismatch.
Manifest and each receipt are limited to 64 KiB, and duplicate JSON keys,
nonfinite numbers, malformed manifest fields, invalid IDs and digest formats
are rejected. Files must be regular files; final-component symlinks are
rejected. Large artifacts are hashed in chunks.

### Receipt contract

The versioned receipt format below is a proposed input contract only. The
existing build/publish scripts do not currently produce these receipts. The
qualification harness uses source and terminal JSON receipts but does not bind
published executable bytes, so it cannot be substituted for a build receipt
without additional evidence. No pipeline changes are made here.

Each receipt must have `schema: 1` and the matching `kind`. A receipt's
SHA-256 pin must come from the caller's trusted evidence boundary, not from
the potentially stale artifact or an untrusted adjacent manifest. Receipt
content is parsed only after matching the supplied pin.

* `source-stamp`: `artifactSha256`, full `commit`, and
  `method: "dotnet-assembly-informational-version"`. This represents an
  offline observation of the .NET assembly informational version, bound to
  the exact binary digest. For a single-file bundle, the observation must
  be made by an appropriate offline inspector of the bundled managed
  assembly, not by executing the app or searching arbitrary binary text.
  This helper checks the supplied receipt and its binding, not the assembly
  metadata directly. Without that receipt the self-stamp remains unknown.
* `build`: `buildId` (stable nonsecret build occurrence ID), `sdkVersion`,
  `source` and `target` (same structures as the manifest),
  `state: "exited"`, `exitCode: 0`, `artifactSha256` (exact emitted
  binary), and `checkoutBefore` / `checkoutAfter` observations. Each
  checkout observation contains `phase` ("before-build" or "after-build"
  respectively), `source`, `status: "clean"` or "dirty", and
  `method: "git-status-porcelain-v1-untracked-all"`. Both observations
  belong to that same build occurrence. The producing build harness must
  capture them around the build; the inspector cannot establish their
  timing, provenance or truth retroactively. A current clean checkout is
  never a substitute. `state: "unknown"`, absent terminal state, or
  `state: "started"` does not become success.
* `release`: same `buildId`, exact `buildReceiptSha256`, `source`,
  `target`, `artifactSha256`, `state: "exited"` and `exitCode: 0`.
  This binds the released bytes to that build receipt rather than assuming
  that an output directory or file name identifies the build.

Only the safe identity fields, hashes, fixed reason codes and evidence states
are returned; arbitrary receipt text and local file paths are not echoed.

## Output and trust boundary

The dimensions are independent:

- `artifactBytes`: local bytes match the caller's expected release checksum.
- `observedBytes`: local bytes match the separate observed checksum.
- `source`: exact commit/tree relation found in local Git objects.
- `sourceStamp`: a digest-pinned stamp observation matches the exact local
  bytes and expected source revision. Its basis explicitly states that this
  is not direct extraction.
- `buildProvenance`: caller-pinned build and release receipts agree on the
  terminal build occurrence, source, target, cleanliness observations and
  exact artifact digest; the commit/tree relation is available locally.
- `receipts`: supplied and measured receipt digests and integrity states.

Every dimension is verified, unknown, or mismatch. "Verified" means the
specified comparison succeeded **within the caller-pinned evidence boundary**.
It does not mean an authenticated build service produced the receipts, the
binary is reproducible, or a release is authorized. A self-stamp alone,
matching bytes alone, or a clean checkout after the fact cannot establish
build provenance. Independently trusted contemporaneous receipts are
required; an attacker who can replace both evidence and its expected pins
can fabricate a consistent report. There is no signing or PKI here.

Overall status is mismatch if any dimension contradicts its expected
evidence, otherwise unknown if any evidence is unavailable, otherwise
verified. Exit codes: 0 verified, 1 mismatch, 2 unknown or invalid manifest.
A missing or nonterminal receipt stays unknown; this inspector never
creates a replacement occurrence or replays work. Integration into the
build pipeline or deployment workflow requires a separate reviewed change.
