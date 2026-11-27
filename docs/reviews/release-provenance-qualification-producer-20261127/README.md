# Release provenance: contemporaneous producer seam and handoff

Task: `robustness-release-provenance-qualification-producer-20261127` (Worker 1).
Disposition: **no-go for a complete standalone producer against the frozen
publisher contract; prepare the minimal integrated producer seam for separate
authorization and independent review.** This is a source audit and proposed
acceptance matrix, not release qualification or permission to publish.

## Pinned evidence

Isolated clean registered worktree from local main
`175ad976a6226f1cb07901b4b43f3d6b0ecf3de1`, tree
`57b0a44dd514b76c4b3ce109ea3b2c04dc9a8a38`.
Read-only Git object inspection, no fetch or live host operations. Relevant blobs:

| Path | Git blob |
|---|---|
| scripts/build.cs | 8be5b6e960b17106750e33702acfbfb6ef1307e9 |
| scripts/test-client-hermetic.fsx | 32f3f74029eed32eb929b6d6ff8bb1ef571b0f16 |
| scripts/publish-client-downloads.cs | 2f06385da7ad86c40740c4c4fb324c94e2af4ff5 |
| scripts/publish-server-download.cs | 5bd4a8f7f4e7f7ee79f56fb9bf4316885320cc64 |

Frozen proposed publisher `b21efb32a21a0d2cb473add5a0b7c0288ea2a795`
(review `c3a1c558e42a4eb3a4b11ea82c7a6da8`, pending at assignment)
has `scripts/release-provenance-publisher-composition/publish.py` blob
`94088e438623efb02dcfee7fd925c157fef918db`. It is not authority to change
that candidate. Its verifier/preflight controls use explicitly synthetic
stamps and qualification; they do not prove product Release qualification.

Existing predecessor, discovered by registered worktree enumeration and local
ref inspection: `feat/ci-release-provenance-worker19` at
`a636f5c87fb5e9f708ec8f3e5d968cf74713f3bc`; its
`scripts/release-provenance.cs` blob is
`dd40c86cd431617216023693c4535be91b213bc7`. It is not present at the pinned
main and its review/acceptance is not established by this audit. Its
`docs/ReleaseProvenance.md`, `scripts/build.cs`, CI workflow and
`release-provenance.cs` already implement a proposed preflight -> build ->
stamp -> collect mechanism. Reuse its useful sequencing insight rather than
creating a competing release manifest or copying its implementation:
the stamp function takes expected commit, checks the current checkout and
hashes an output file. It does **not** inspect the bundled managed assembly
or establish that an existing output file came from this build occurrence;
it does not supply the proposed commit/tree, build occurrence and
qualification receipt linkage. Its sidecar is not equivalent to the
verifier's source-stamp receipt. It also changes shared pipeline and
lockfile policy; neither is in this lease.

Canonical parent task `robustness-release-artifact-source-provenance-20261127`
establishes that byte digests, source self-stamp and stronger provenance are
separate. Its referenced checkpoint 18555 was not retrievable through the
current attempt's checkpoint lookup (missing ledger entity); no additional
claims about that historical observation are made. Full task overview was
too large for the tool response. Exact parent/assignment specs and relevant
registered source refs were read instead.

## Actual main boundaries and gaps

* `scripts/build.cs`: `PublishClient`/ `PublishServer` invoke the actual
  `dotnet publish` subprocess, default Release, RID, single-file,
  self-contained, explicit output and existing product options (client
  URL, server native library extraction). `DotNet` returns the process
  exit. No explicit commit/tree receipt, fresh directory assertion, build
  occurrence or artifact-bound metadata inspection is emitted. Existing
  normal output paths can contain prior files. `PublishClients` is a loop
  and `PublishJarvis2Linux` is sequential; do not infer per-RID success
  from an earlier invocation.
* Main client/server download publishers invoke those same build.cs
  commands, then copy files and emit SHA256SUMS. They do not prove that the
  copied bytes came from the observed successful subprocess. The deployment
  skill's canonical test command is `dotnet scripts/build.cs test`;
  its configuration defaults to Debug. That default is not evidence of
  Release qualification. The skill and configuration are not changed here.
* Actual existing Release test primitives: Linux
  `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --timeout-seconds 900`
  (after explicit restore and Release build) uses a private HOME/XDG/temp
  and isolated Git identity, bounded process tree and requires nonzero
  executed test count unless discovery-only. The runner reports terminal
  exit and executed count and deletes private test result files; it rejects
  non-Linux. `dotnet test Server.Tests/Server.Tests.fsproj -c Release
  --no-build --no-restore` is an existing Server Release test invocation;
  the WQ11 offline acceptance document shows both primitives and test
  counts, but is historical evidence for a different source. No source-
  bound fresh qualification JSON producer for the proposed consumer was
  found on pinned main. The exact suite selection, restore/build policy,
  skipped-test policy, and required counts must be confirmed by Lead/
  existing release policy; do not silently substitute an arbitrary command,
  discovery-only, or stale no-build test binaries. Windows Client test
  isolation remains outside this lease.
* Client/Client.fsproj generates ignored `Client/BuildInfo.fs` with the
  server URL before compilation; it is not tracked. Clean Git status alone
  cannot establish hermeticity of ignored outputs, package inputs, or
  transiently modified-and-restored files. Fresh controlled checkout,
  dependencies, output and exclusive local writer are prerequisites. Do
  not claim arbitrary adversarial-process resistance from boundary status
  samples. Main's product projects do not explicitly set
  AssemblyInformationalVersion/SourceRevisionId; the .NET SDK's normal
  source revision mechanism is a candidate, not an observed self-stamp
  for a newly produced executable.

## Why not add a standalone producer now?

The proposed publisher already implements the build receipt around its
actual subprocess: `observation`, fresh session/ per-RID output,
`build_id`, terminal invocation, hash, and `build.json`. Its
`qualification` adapter uses the shared verifier's bounded parser and pinned
file observation; its staged/final copy receipts bind that build receipt.
Those mechanisms should remain single-owner.

However, `publish()` reads and validates **all caller-provided stamp
receipts before invoking any fresh builds**. The descriptor requires the
exact stamp inventory and SHA-256 pin. A source-stamp receipt must be an
offline observation of the informational-version metadata in the exact
single-file executable, bound to those exact bytes. A new standalone
pre-build stamp producer cannot inspect bytes that do not yet exist.
Building a second executable and hoping byte-for-byte reproduction would
prove only the inspected prior bytes and assumes determinism; this is not
a general contemporaneous producer. Reading HEAD and hashing the new
binary is not metadata inspection. Inspecting an adjacent DLL without
proving it is the embedded assembly, scanning arbitrary text, or executing
the released application is not the required observation. No existing
equivalent direct bundle inspection primitive was located on main.

A standalone qualification adapter could record an observed test invocation
but cannot complete this pipeline or choose policy. The frozen consumer
checks an arbitrary nonempty nonce, successful terminal/source/checkout
fields, and caller pin; it does not bind that nonce to the fresh publisher
occurrence or an approved qualification plan. A previous valid same-source
receipt can be supplied again, and the contract does not distinguish a
successful no-op command from actual required tests. Creating a second
generic receipt writer or selecting policy in this lease would either
duplicate the established consumer primitives or allow that ambiguity.
Missing/stale receipt acceptance must be addressed at the composition
boundary, not by an extra unconsumed receipt file.

Consequently no standalone implementation under the new-files-only lease
can establish all required actual producer facts for the proposed publisher.
Do not add another production verifier, parallel publisher, or fake
attestation. The following is the minimal proposed follow-on, conditional on
Lead reconciliation of the frozen publisher review and older CI candidate.

## Required next lease / seam (default-off preparation only)

1. **One occurrence owner.** Extend the accepted/reconciled publisher's
   `publish()` session and receipt helpers, rather than wrapping it in
   another build. Select exact source commit/tree, explicitly authorized
   qualification plan (including runner version/source and required
   test inventory), target inventory and SDK. Allocate a fresh private
   occurrence and per-target directories. Use the existing verifier's
   source/path/digest/JSON primitives after review/integration. Record
   durable started and terminal process observations; distinguish observed
   nonzero exit from launch failure, timeout or unknown outcome.
2. **Qualification boundary.** Adapt the already supported Release
   Client runner and Server test entrypoint, preserving existing policy,
   isolation, actual executed count and failure handling. Capture exact
   argv, approved plan ID/digest, SDK, source/tree and dirty pre/post
   observations; retain bounded nonsecret terminal results and test count
   evidence before the runner deletes private results. Bind the receipt
   to the occurrence, not merely a nonempty nonce. Require freshly built
   exact-source test binaries or observed fresh build/test invocations;
   neither historic results nor a fresh receipt around stale test output
   is sufficient. If the selected runner cannot export the necessary
   evidence without modifying it, request that narrow lease rather than
   parse a competing test policy. Do not qualify by discovery-only, filters
   not authorized by the plan, or placeholder success. No changes to
   the existing rollout gates; qualification selection remains with Lead/
   Operator.
3. **Build boundary.** Keep `build.cs` product publish semantics and
   the publisher's fresh `--output` and actual observed terminal exit.
   Check source/tree and dirty status immediately before and after each
   real build, and recheck SDK/controlled inputs where appropriate.
   Keep the existing unique build ID derived from occurrence and target.
   Verify the expected regular artifact is newly emitted, hash it
   stably, then bind the result to that exact build receipt. If direct
   non-download `build.cs` publishing is later included, its
   `PublishClient` and `PublishServer` post-`DotNet` success are the
   corresponding hook; do not write those functions under this lease.
4. **Post-build/pre-stage source stamp.** In the reconciled publisher,
   immediately after the successful build, exact after-source observation
   and artifact hash, but before `shutil.copy2` and the staged manifest,
   inspect the managed assembly inside the exact fresh single-file output
   with a reviewed offline .NET bundle/metadata inspector. Investigate
   available SDK-compatible tooling; if not available, submit the
   smallest necessary inspector design separately, using existing
   System.Reflection.Metadata for managed metadata wherever possible.
   No guessed bundle parsing, executing the executable, or
   HEAD-derived self-stamp. Record actual observed full revision,
   method and exact artifact digest; validate the same stable bytes
   used for the build hash (including replacement/change controls),
   fail closed on missing/ambiguous/mismatching metadata or unavailable
   inspector. Keep self-stamp distinct from the stronger build receipt.
   A source-stamp can then be digest-pinned by the trusted occurrence
   owner, not pre-supplied for nonexistent bytes. Preserve the existing
   caller-pinned option only if policy explicitly requires independently
   supplied observations of reproducible bytes.
5. **Composition before effect.** Bind qualification receipt, build
   receipt, stamp, target and actual staged/final artifact bytes to one
   occurrence/plan. Enforce qualification freshness/identity in the
   consumer before modifying any downloads. Use the accepted verifier
   and existing staged/final rechecks; the caller pin is not a signature.
   Do not relabel an observed local copy as remote release success.
   If a terminal outcome is unknown, retain occurrence evidence and
   reconcile; never rerun merely to obtain a successful receipt.
   No actual release, activation, host probing or deployment in the
   follow-on preparation.

The minimal additional exclusive lease is the reconciled
`scripts/release-provenance-publisher-composition/publish.py` (and its
tests/README), plus a new offline stamp/qualification adapter directory
and tests. Request narrow `scripts/test-client-hermetic.fsx` lease
only if needed to expose its existing terminal count evidence; request
`scripts/build.cs` separately only for direct non-download publish
coverage or if validated SDK stamp output requires a build seam. Any
verifier schema extension needs a separate explicit lease and compatibility
review; prefer the existing verifier plus composition-level checks.
No deployment skill, shared application, CI, dependency lockfile or
qualification policy edits without separate authorization. Resolve
overlap with Worker19 and the pending publisher candidate before touching
shared scripts. Integrator alone publishes target.

## Acceptance matrix for the follow-on

Use disposable offline repositories and real producer entrypoint, not
fabricated receipt JSON as the positive test. Start from known clean
source and approved fixture qualification plan. Use a small .NET test
application and supported SDK metadata inspector for a genuinely bundled
source stamp; exercise client/server/RID command selection with controlled
fixture invocations. Clearly label fixture versus actual Jarvis product
qualification. Preserve process exit and exact source/target/digest
evidence. Test these cases before any live use:

| Case | Required observed result |
|---|---|
| Successful real fixture build + qualification, clean source | One fresh occurrence, terminal exits and nonzero executed count; actual embedded revision from fresh output; matching build/stamp/qualification digests; staged and final verified |
| Build exit nonzero, even if a file is emitted | Nonzero observed, no positive build/stamp/release receipt or final download effect |
| Exit zero with missing output / conventional stale artifact | Fresh-output gate rejects; no fallback to previous artifact |
| Qualification exit nonzero, timeout or missing executed tests | No successful qualification or final download; timeout/unknown preserved |
| Qualification succeeds but wrong/unapproved test plan or stale test binaries | Reject; no receipt accepted just from a successful no-op |
| HEAD or tree changes, dirty pre/post, tracked or untracked change | Reject at actual process boundary; no successful source claim |
| Artifact substitution before inspection, between hash and copy, staged or final bytes | Digest/identity mismatch, no successful final verification/checksum publication |
| Missing/malformed/mismatched source stamp or wrong embedded revision | Unknown or mismatch, never a successful self-stamp |
| Missing, stale or substituted qualification/build receipt, including prior same-source nonce | Reject absent or wrong occurrence, plan, target and receipt digest; caller pin alone insufficient |
| Mixed multi-RID batch, late failure | No success or new checksum before entire staged inventory verifies; retain/reconcile any partial final effect |
| Started/unknown process outcome | No inferred success or automatic replay; original occurrence remains identifiable |

Report exact command, SDK, clean source SHA/tree, terminal exit, required
test counts, observed digests and per-control outcomes from the actual
fixture producer. After a successful independent review and explicit
Lead handoff, separately qualify the real Jarvis product under approved
Release policy. This audit ran no build, test, producer or publisher
processes and asserts none of those results. Static validation only:
Git object identity, source reads and local worktree/ref discovery.
Existing synthetic preflight/publisher test outcomes are inherited
context, not new evidence. No network/SSH, deployment, activation,
host probes or real release were performed.
