# Manual attributed browser launch source binding

An attributed BrowserRunInNewTab reads the launcher once before the durable
BrowserLaunchIntent and before any browser operation. Its immutable intent
records `scriptPath` (the requested project-relative script path) and
`executedSourceSha256` (lowercase hexadecimal SHA-256 of the actual decoded
source string encoded as UTF-8). The same fields participate in the ledger
request hash. The original source string is passed to the browser bridge;
there is no second read or post-effect hash. No script or prompt contents or
absolute file location are retained in the intent.

The attributed path accepts strictly valid UTF-8, with at most one leading
UTF-8 BOM removed; line endings and Unicode code points are preserved.
An empty or whitespace-only source is rejected, as are unreadable, malformed
or oversized inputs before intent creation. Generic non-attributed execution
retains the existing file-read behavior. The digest is not a raw-file digest:
a reviewed UTF-8 source with a BOM must be decoded and re-encoded in the same
way to compare hashes; do not normalize CRLF, LF, or Unicode. Local modifications
can therefore be detected by comparison, but this is evidence, not a reviewed
source authorization gate.

The launch ID links the pre-effect intent to the separately persisted outcome.
Historical intents lacking a source digest remain unknown; no backfill or
rewrite is valid. An identical operation replay is not authority to send;
a changed digest or script path for the same operation is a request conflict.
The intent proves the source prepared for the bridge, not that a browser send
occurred, that the UI selection succeeded, or what backend inference effort
was used. An absent or uncertain outcome must not be replayed.
