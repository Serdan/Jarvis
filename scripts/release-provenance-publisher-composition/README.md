# Isolated release publisher provenance composition

This candidate composes the accepted offline verifier and preflight witness
into the existing client and server download publisher commands. It only
produces local downloads; it does not authorize or perform deployment,
change qualification policy, probe hosts, or publish a release remotely.
The accepted helper remains unchanged.

## Caller contract

The local publisher now fails closed without an explicit trusted evidence
descriptor and its independently supplied SHA-256 pin. Python 3, Git and
the existing .NET SDK are required on the publisher machine. From the
checkout root (absolute evidence paths are recommended):

```sh
dotnet scripts/publish-client-downloads.cs --evidence /path/to/evidence.json --evidence-sha256 <64-lowercase-hex>
dotnet scripts/publish-server-download.cs --evidence /path/to/evidence.json --evidence-sha256 <64-lowercase-hex>
```

The descriptor is a bounded JSON object with exactly these fields:

```json
{
  "schema": 1,
  "source": {"commit": "<full-commit-oid>", "tree": "<full-tree-oid>"},
  "qualification": {"path": "qualification.json", "sha256": "<digest>"},
  "stamps": {
    "JarvisServer-linux-x64": {"path": "server-stamp.json", "sha256": "<digest>"}
  }
}
```

For the client, the exact stamp inventory is JarvisClient-linux-x64,
JarvisClient-win-x64.exe and JarvisClient-osx-arm64. Paths are relative to
the descriptor's directory or explicit absolute paths. All pins must
come from the caller's trusted evidence boundary; never trust pins
derived from an untrusted adjacent file. The stamp receipts use the
accepted verifier's source-stamp contract, representing a real offline
inspection of the exact bundled managed assembly and binding its source
revision to the binary digest. The publisher does not inspect assembly
metadata itself or synthesize stamp receipts from HEAD. A preprovided
stamp is usable only if the fresh build produces those exact bytes.
A non-reproducible build without a matching independently observed stamp
fails closed. A live contemporaneous stamp producer/qualification adapter
requires separate authorization and evidence; none is introduced here.

The caller-pinned qualification receipt adapts the accepted preflight
receipt shape, with schema 1, kind qualification, a stable nonempty
token nonce, source (exact commit/tree), state exited, integer exitCode
0, and before/after observations in the verifier's checkout format with
phases before-qualification and after-qualification. This establishes
only the supplied terminal and source-binding facts, not that any
particular qualification suite was selected or sufficient. Selection and
acceptance of actual qualification remain with the existing policy and
Operator. The disposable fixture's placeholder qualification and
synthetic stamps are not production evidence.

## Local effect boundary

1. Read and pin the descriptor and receipts using the accepted verifier's
   bounded JSON/file observation, reject missing/malformed/mismatched
   input, and require the current checkout to match the exact clean
   source/tree. Copy pinned input bytes into a fresh per-invocation
   directory under artifacts/release-provenance.
2. Use the existing build.cs command and original product/RID/configuration
   and client URL, but give each invocation a previously nonexistent
   output directory. Capture clean exact checkout observations around the
   actual subprocess and bind its successful exit, SDK, unique build
   occurrence and emitted binary digest. A stale conventional output
   is never accepted. Started/nonzero/unknown work is not replayed.
3. Stage each copied artifact, preserve client Unix executable modes,
   generate candidate copy receipts, and use the exact accepted verifier
   for the complete batch. Reject before touching existing downloads if
   any staged copy or evidence fails.
4. Replace the intended final files from the same-filesystem staging
   directory, create distinct final-copy receipts, reverify the final
   paths, and only then replace SHA256SUMS. Preserve the client inventory
   and server unrelated-entry checksum conventions. The new provenance
   report covers only artifacts in this invocation, not unrelated files.
   This is local single-writer preparation, not a transactional multi-file
   deployment: if a filesystem failure interrupts the final replacements,
   the old checksum is not replaced and the command does not succeed.
   The caller must reconcile the terminal exit and filesystem state;
   do not infer success from a staged receipt or replay an unknown effect.

Per-invocation evidence is retained locally, including input snapshots,
invocation/build/copy receipts and staged/final verifier reports. The
staged manifest describes staged paths at verification time; those paths
are moved on success. The final manifest describes actual final paths.
A copy receipt describes that copy operation, not an observed terminal
exit of the whole publisher or live publication. The actual publisher
process exit is a separate required observation. There is no signing,
independent attestation or source claim beyond the caller-pinned contract.
The isolated checkout, local process environment, tools and output
directory must be controlled by the caller.

## Offline validation

Run the accepted verifier's existing tests and the composed fixture from
a clean committed source:

```sh
python3 -B -m unittest discover -s scripts/release-provenance -p test_verify.py -v
python3 -B scripts/release-provenance-publisher-composition/test_publish.py
```

The composed fixture invokes both actual C# publisher entry points using
the managed Release file-app SDK and an isolated local clone of the
candidate. It uses a fake nested dotnet build (including its SDK query),
disposable bytes, synthetic source-stamp observations and a placeholder
qualification subprocess, with offline NuGet configuration and Git
file-only protocol. It checks success, absent/invalid/dirty/stale evidence,
substitution, nonzero build and late client failure, final checksums,
Unix client mode and exact receipt linkage. It never runs a real product
build, real qualification, SSH, host probes or live publication.
