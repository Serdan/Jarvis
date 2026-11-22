# F1 — Required expectedRevision has no supported MCP acquisition path (P1, protocol usability)

**Frozen source:** `ce1bd9fd7ee60663a300f2405896b84e65ec0d00` (early W25 protocol-only review; not full P0 tool acceptance).

**Evidence.** `Server/McpTools.fs:1179-1184` rejects absent `expectedRevision`; `:1785-1791` makes it mandatory in the sole new remote settings tool. `Server/McpTools.fs:1603-1628` shows `ListDevices` returns device identity, connection status and capabilities, **not** the client's current settings/config revision. The source has no new authenticated revision-read command/tool; `Client/Program.fs:40-59` local `config show` also does not print the revision. The docs say to obtain a current readback revision without identifying an available protocol route. An existing persisted config has a SHA-256 revision that a remote user cannot safely infer.

**Reproduction / test to add.** Start from a fresh authorized MCP session with the normal `ListDevices` and new tool surface, select a valid connected device and attempt the first `set` with no out-of-band filesystem/CLI access. The only existing tool rejects missing revision and none of the exposed read responses supplies a correct one. This is a source/interface reachability finding, not a claim that an end-to-end device harness was run.

**Impact.** The named-setting tool cannot normally perform its first update without a manual/off-protocol method to discover revision; this defeats the requested practical one-tool operation. Removing the CAS precondition or guessing a revision is **not** an acceptable fix.

**Bounded correction for W25/W24 contract coordination.** Expose the currently stored revision to the same authenticated *exact-device* protocol workflow (possibly a read-only operation on the same named-setting tool or another existing compatible status/readback response), or otherwise provide a demonstrable supported route; retain strict CAS and no implicit device fallback. Prove first-use from disposable two-device MCP harness. Any larger API restructuring is for independent adjudication, not implied by this finding.
