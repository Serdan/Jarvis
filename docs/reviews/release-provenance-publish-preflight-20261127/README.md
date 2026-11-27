# Offline release provenance composition preflight — prepared, not a release gate

## Scope and authority

Task `robustness-release-provenance-publish-preflight-20261127`, attempt
`a97e8b602c864d2baef0709668ab0b1c`, generation 1, pinned spec blob
`18b6a96852169faa44f9238bddd7155bbb2c5c11`; parent
`jarvis-1-0-robustness-and-ergonomics-completion-20261127` v4, especially
the incremental release and context-efficiency decisions. This is exclusive
preflight/handoff work, not permission to change shared build/publish/deploy
code, activate Automation, approve qualification, upload downloads, deploy,
restart a live service, or cut a stable branch. No such action was performed.

### Exact source and evidence

At the fresh read-only origin query (2026-11-27, local tool result exit 0),
`git ls-remote origin refs/heads/main refs/heads/jarvis-review/robustness-release-provenance-malformed-path-correction-20261127/c652fa5636294d4a9c2ed371c209376f/r1 refs/heads/jarvis-review/robustness-release-artifact-source-provenance-20261127/bbd9ec2a0b074c80a9d825d84d5a0278/r1`
returned:

| Identity | Exact commit / evidence |
|---|---|
| Origin main | `291b5499051fbddfff1c76308e1a0202a5d1862f`, tree `fc4aaf661d434c479b403d09ee1535e3d58d3442` |
| Original helper review bbd9ec2a0b074c80a9d825d84d5a0278 | `c0e110c11c1fb8615b2ac1868972951952bb25f8`, ChangesRequested, sequence 18733; not accepted |
| Corrected helper review c652fa5636294d4a9c2ed371c209376f | `92bcb1a97a5a8d27f36eb535553961c4cabb4ce9`, Accepted by Reviewer 3, sequence 18900 |
| Corrected helper source blob | `e48ae20d32f627dbc4a88763ad5e83cfb3620b63`; SHA-256 `905a0edac953f4495624ca52e743bb688af1bd79fa0a0b3f32e2721aaf30c7eb` |

The accepted correction's local remote-tracking ref and actual commit object
matched the remote frozen ref. The local main checkout was at
`175ad976a6226f1cb07901b4b43f3d6b0ecf3de1`, not origin main; it was not
used as the base or modified. The new isolated branch was aligned to the
verified origin main using `git reset --keep` before binding, with no user
changes in the new checkout. The four inspected build/publisher/deploy files
were identical between those two pins (`git diff --name-only` over those paths
returned empty). The exact main tree has no `scripts/release-provenance/`;
the corrected helper is read from the frozen review object, not copied into
main or treated as deployed. The original source verdict remains immutable.
Lead/Integrator must reconcile the original helper plus accepted narrow
correction through independent integration before shared pipeline wiring.

Relevant exact main blob IDs (Git SHA-1, not SHA-256):

| Path | Blob |
|---|---|
| scripts/build.cs | 8be5b6e960b17106750e33702acfbfb6ef1307e9 |
| scripts/publish-client-downloads.cs | 2f06385da7ad86c40740c4c4fb324c94e2af4ff5 |
| scripts/publish-server-download.cs | 5bd4a8f7f4e7f7ee79f56fb9bf4316885320cc64 |
| .agents/skills/deploy/SKILL.md | 6ac52d22fc69be0faa3aee0a8d6bcf4a4785edc4 |
| scripts/release-qualification/qualify.py | 6166ce6ef5a8d1fdcf86ce1ed166884b901d1314 |
| Client/Client.fsproj | 25680b1ba96bac1e315cc3709b989732bb45bf30 |
| Server/Server.fsproj | bb058148a86cf2345d982f3cfe9bc4acc0cbae02 |

## Source trace: where provenance must be bound

- `build.cs`: publish defaults to Release, target RID required.
  `PublishClient` runs dotnet publish Client/Client.fsproj with configuration,
  RID, output directory, single-file/self-contained, explicit JarvisServerUrl.
  `PublishServer` uses Server/Server.fsproj, single-file/self-contained and
  IncludeNativeLibrariesForSelfExtract. Its child exit is returned.
  `PublishClients` iterates RIDs and stops on nonzero. There is no
  contemporaneous commit/tree/cleanliness or terminal build receipt; neither
  a successful exit nor the name of an output directory proves which bytes
  were emitted by this build. The SDK's informational version must be
  observed in the actual artifact; source inspection alone cannot establish
  its presence or correctness. Client/Client.fsproj embeds JarvisServerUrl
  via generated BuildInfo, and the assembly names are JarvisClient/JarvisServer.
- `publish-client-downloads.cs`: invokes `dotnet scripts/build.cs publish-client --rid RID --server https://jarvis2.kehlet.dev/client`
  for linux-x64, win-x64, osx-arm64; stops if nested process nonzero or
  source missing. Copies artifacts/client/RID/JarvisClient[.exe] into
  artifacts/downloads/JarvisClient-RID[.exe], sets executable mode for
  non-Windows output, and writes SHA256SUMS over all current directory files
  other than SHA256SUMS. Existing stale source/output files are not ruled
  out; an unexpected old download is also included. Partial failure may leave
  earlier copied files. A manifest hash is integrity, not build provenance.
- `publish-server-download.cs`: invokes
  `dotnet scripts/build.cs publish-server --rid linux-x64`, then copies
  artifacts/server/linux-x64/JarvisServer into
  artifacts/downloads/JarvisServer-linux-x64. It replaces that file's checksum
  line and preserves other existing lines; it neither checks other entries nor
  binds the build that produced the server. No transaction across the directory.
- Deploy skill: canonical predeploy tests, direct server publish (not necessarily
  the download publisher), stage/upload, preserve JarvisServer.prev, replace and
  restart systemd service, then verify connection/active process. For changes
  to Common/Client or protocol it directs a matching local client publish and
  human-controlled restart; public download publication is separate and uses
  the client download script then rsync --delete. Both direct deployment and
  download paths require their own exact build/byte linkage; checksumming
  the downloads cannot retroactively attest the direct server binary.
- `scripts/release-qualification/qualify.py` and
  `docs/testing/automation-release-qualification-preflight-20261126.md`:
  exact-source disposable qualification runner with start/source/stage
  terminal receipts, console, TRX and evidence hashes. That is a test
  occurrence, not a binary build receipt; inspect actual scoped/full gate
  terminal state, raw counters/rows and source as required. A generic
  inspector's exit 0 is not a test pass or release authorization.

## Minimal composition contract (proposal for the next shared-pipeline owner)

Use the existing verifier rather than duplicating its schema. Establish a
new private build/release-batch occurrence, expected full source commit and
tree, SDK and exact intended product/RID/configuration, embedded endpoint and
publish arguments; record immutable pins outside the mutable artifact/stage
directory. Start from the exact independently reviewed integrated source in a
fresh disposable/controlled checkout, not a dirty interactive tree. Verify
local Git objects, current HEAD/tree and porcelain-v1 untracked-all before and
after each build and qualification. Do not treat ignored files as clean-build
proof: create a fresh output and controlled SDK/dependency/build caches,
reject pre-existing output or enforce new private per-occurrence paths; no
destructive cleaning of the user's checkout. A clean checkout later is not
historical build evidence.

1. Before spawn record stable build ID bound to batch and exact target, argv,
   source, SDK/config and checkout observation. Observe the original child
   terminal OS result (no replay of unknown/nonterminal jobs); only a zero
   exit plus fresh regular output and matching after-build source qualifies.
   Hash the emitted bytes at that boundary, before any publisher copy.
   Capture the digest-bound assembly informational-version observation via a
   suitable offline single-file bundle/managed metadata inspector, not
   arbitrary string search, execution of the released program, or a
   claimed source stamp. If absent/unavailable, classify unknown and reject
   provenance acceptance. Keep the approved build receipt's SHA-256 pin
   separate from artifact-controlled files.
2. Keep qualification evidence independent: exact source/tree, approved
   matrix (scoped and adjacent regression for an incremental batch, or the
   separately authorized complete gate), original occurrence, terminal results
   and receipt pins. Require the exact intended source and applicable tests
   to pass. Do not substitute self-authored qualification JSON or a clean
   checkout for tests. Bind the release receipt to both the build receipt
   digest and approved qualification identity/digest; retain the raw receipts.
3. Copy only expected freshly emitted artifacts into an owner-private stage,
   preserving executable permissions. Independently hash each final copied
   artifact and compare against the pre-copy digest, expected manifest entry,
   exact target and current build occurrence. Check exact batch membership
   and no stale/foreign entries, duplicates, partial failures or unexpected
   symlinks. The client script's broad directory inventory and server script's
   preserved checksum lines need this control. Retain the publisher's original
   terminal receipt. Create release receipt with same build ID/receipt digest,
   source/target, final byte digest and terminal success plus publisher and
   qualification pins. Call corrected verifier with the manifest and local
   source objects; require its verified result, then additionally require
   current occurrence and qualification equality. The verifier alone checks
   consistency of supplied pinned receipts, not freshness against an external
   batch authority or qualification validity.
4. Only after the whole intended stage passes (including all RIDs in a
   public batch), hand off the immutable verified digest set and stage to the
   authorized publisher/deployer. Recheck actual outgoing/uploaded/installed
   bytes against that set at the transfer boundary; isolate against
   concurrent replacement/TOCTOU. Never publish a partial batch or replace
   trusted pins with freshly recomputed values after an inconsistency.
   Missing, dirty, stale, substituted, failed or unknown evidence => not
   accepted; preserve original outcome and reconcile, no speculative retry.
   A checksum list adjacent to mutable files is not an authenticated trust
   anchor; real producer/approver identities and authoritative receipt
   custody remain external to this fixture.

## Executable witness and results

Run from the preflight checkout, with the exact main and corrected helper
objects available locally (no fetch from the runner):

    python3 -B scripts/release-provenance-publish-preflight/fixture.py
    python3 -B scripts/release-provenance-publish-preflight/check_evidence.py

The fixture creates a private temporary directory and local-only shared Git
clone per case, detaches exact main, verifies the commit/tree and checkout
state, extracts the corrected helper from its exact Git object and verifies
the unchanged C# publisher bytes. Uses real local .NET SDK 11.0.100 to
execute the actual publisher C# file in managed Release mode:

    dotnet run --file scripts/publish-server-download.cs -c Release -p:PublishAot=false -p:SelfContained=false --disable-build-servers
    dotnet run --file scripts/publish-client-downloads.cs -c Release -p:PublishAot=false -p:SelfContained=false --disable-build-servers

The outer dotnet is resolved before putting a private fake dotnet on PATH;
only the publisher's nested build.cs invocation is intercepted. The shim
checks the precise argv and writes tiny **fake text artifacts** in the scratch
clone via a separate subprocess, captures actual before/after Git status and
child exit, and binds fake build/stamp receipt pins at that boundary.
No actual build.cs/dotnet publish, real artifact, embedded endpoint test,
assembly extraction, actual qualification test, provider/browser send, upload
or production action is performed. The stamp observation and qualification
command (Python pass) are explicitly simulated. The actual publishers' nested
process dispatch, exit propagation, copy/naming and SHA256SUMS behavior are
real; the gate runs the actual accepted verifier with fixture-only receipts.
Scratch clone cleanup happens after raw synthetic receipts and copied bytes
are emitted. NuGet sources are cleared, private HOME/packages, no inherited
Jarvis/provider credentials and no real remote Git operations in the runner.
This is environment isolation, not an OS/network sandbox.

The initial literal `dotnet scripts/publish-server-download.cs` invocation
under SDK 11.0.100 failed before any nested invocation (exit 1, NU1100:
offline restore could not resolve Microsoft.DotNet.ILCompiler,
Microsoft.NET.ILLink.Tasks and runtime/AOT packages; fixture SHA-256
37dfa0a582ead3fc0de97d15046f0acd88891745fc60f980b8a1485e862d4d7b).
Managed Release execution explicitly disabled AOT and self-contained only for
the publisher *launcher*, not for an actual released product. The corrected
seven-case run exited 0 (job 654d55c4cbeb4876a0eba5496e9b8a9b; retained
first-managed-run.json; harness SHA-256
fd7c3a8f1f0ace20e54122ceaeedb30457ca784df90f47e44e7c3d96a2c9e4be).
The next run was a planned verification of the improved fixture with
pre-copy receipt pins, raw evidence capture and a separate missing
qualification control, not a retry of uncertain work.

Final job `deda0527a5f245a7908d721ea338fe93`, terminal Completed/exit 0,
all 18 output sequences, not truncated. Harness SHA-256
`9c017e4074c66fe2414303e793e841f0ca43bccc93e96a9fe115a568a38bc3cc`.

| Control | Actual publisher exit | Corrected helper exit / status | Composition decision |
|---|---:|---|---|
| Server Linux positive | 0 | 0 verified | accepted (fixture only) |
| Client three RIDs positive | 0 | 0 verified | accepted (fixture only) |
| Absent build/stamp/release | 0 | 2 unknown | rejected |
| Absent qualification | 0 | 0 verified | rejected |
| Dirty tracked source | 0 | 1 mismatch | rejected |
| Stale same bytes, prior build receipt | 0 | 0 verified | rejected |
| Copied file substituted after SHA256SUMS | 0 | 1 mismatch | rejected |
| Nested fake build exits 7 | 7 | not run | rejected; no downloads |

The stale negative deliberately uses identical bytes and the earlier
positive build receipt. The corrected verifier quite properly verifies the
consistent historical receipt set; the external current-occurrence check
rejects it. Similarly a qualification receipt missing from the release
context is outside the helper's existing contract; composition rejects it.
The dirty negative verifies actual Git tracked dirt around the build; the
substitution negative uses the real publisher's old hash and the changed
copied bytes. Both real publishers succeed on some invalid evidence, showing
why their exit/checksum is not release acceptance.

The terminal job index `final-managed-run.json` contains all non-bundle
events and pointers to eight individual raw stdout events. Each bundle
contains exact UTF-8 receipt bytes, SHA-256 pins, base64 copied artifact
and SHA256SUMS bytes, and observed publisher stdout/stderr. These are
synthetic proof records copied from the original job result, not a signed
external attestation. The read-only checker verifies the exact harness
digest, sequence completeness, receipt and copied-byte hashes, invocation
argv, clean/dirty/terminal and occurrence evidence, manifest/checksum
membership and receipt cross-links, including stale receipt equality to
the prior positive. Its local readback exited 0: all eight cases consistent.
The exact helper can be independently re-run from its local frozen object;
the old temporary paths embedded in historical manifests are not live paths
and must not be used. The recorded fake artifacts are neither runnable
executables nor release outputs.

Current host observations: local SDK version, Git object/ref and worktree
observations, actual publisher process exits in disposable local clones and
terminal engineering job readbacks only. No observation of production server,
installed clients, active policy, real artifact provenance, deployment
compatibility or release approval. The normal direct SDK file invocation with
native AOT packages remains untested offline; the managed alternative exercises
the same publisher source boundary. Real qualification, actual bundled
assembly metadata inspection, shared pipeline integration and atomic
publication remain separate work. There is no new minimum-rollout hold here.

## Minimal incremental release-batch acceptance checklist (Lead/Integrator/Operator)

Use for coherent substantial integrated efficiency/usability improvements,
not as replacement of the full 1.0/Automation acceptance gate:

- Identify the exact integrated reviewed production-wired commit/tree and
  intended affected hot paths; ensure accepted reviews and integration
  evidence include the actual route/consumer, not only isolated projection.
  Select affected server/client components, protocol/API/session compatibility,
  existing connected devices, and any migration/default-off requirements.
  Establish focused Release plus applicable adjacent regression/negative
  controls and terminal evidence; account for exclusions. If protocol,
  Common or Client changes require paired rollout, stage compatible binaries
  and preserve user control of local client restart.
- Record baseline for representative actual workflows on the pre-release
  source (Interpreter orchestration, worker bootstrap/continuation, reviewer
  cycle/recovery as applicable): raw/delivered tool bytes, repeated payload,
  tool calls, elapsed/latency distribution, inference turns and manual
  interventions; token/cache telemetry where available, clearly labeled
  byte proxies otherwise. Preserve completion/correctness and exact evidence
  retrieval; measure total work across agents, not local savings that move cost
  into additional retries or calls.
- Produce private exact-source artifacts, applicable qualification/build
  receipts and verified final byte digests as above; explicitly reconcile
  original uncertain jobs/effects, no duplicate execution or silent replay.
  Keep known-good prior binary/config and verified backups of relevant
  authoritative Git ledgers/state, service configuration and existing
  download set if updating downloads; verify recovery path and available
  space. Do not change the live Git/Work authority or introduce a new writer.
- Obtain the applicable separate Operator deployment approval, define staged
  transfer/hash readback and rollback criteria (failed service start,
  connection/protocol incompatibility, provenance mismatch, unacceptable
  latency/error/correctness regression), keep prior server binary and matching
  client artifacts. Under existing deploy skill, verify target/staged bytes,
  preserve JarvisServer.prev and existing backup, atomically replace under
  authorized procedure, verify original restart outcome before retrying.
  Do not manage the user's local interactive client process. Public download
  update only when explicitly intended and whole manifest/directory validated.
- Confirm new actual installed artifact digest/source, service health/PID/
  active timestamp and reconnect, protocol compatibility and registration
  of relevant existing clients after human restart where needed. Smoke
  actual changed registered MCP/client/server routes and ensure expected
  authorization, read/continuation and original-operation semantics; no
  implicit activation or replay. Record unknowns and rollback if needed.
- Keep existing Automation policy unchanged: authenticated authority, existing
  opt-in/default-off and STOP/park semantics, current project/role capacity
  and natural drain, manual bypass (manual Working still counts toward
  subsequent scheduled admission), ordinary schedule/timer semantics and
  Health/Challenge eligibility/once-only wake. No added scheduler enablement,
  Work/SQLite migration, new production sends, or policy/config mutation just
  to obtain a measurement. Compare pre/post policy snapshot and check scoped
  no-send/denial/unknown-effect regressions using approved disposable fixtures
  where required, not live activation.
- Repeat comparable actual workflow measurements on deployed version;
  document exact workload, ledger size, concurrency, sample counts, observed
  bytes/calls/latency and correctness, manual recovery/retained unknowns.
  Verify measurable improvement without dropping required evidence,
  weakening independent review or silently truncating results. Track actual
  deployed versions and discrepancies; qualify further incremental batches
  without waiting solely for unrelated complete-1.0 work. Full combined
  release/observed unattended operation and stable cut remain separate gates.

Handoff: Lead owns the subsequent shared pipeline scope and dependency
ordering. Integrator must establish accepted helper/correction integration;
then the pipeline owner can implement actual build receipt production,
trusted occurrence/qualification pinning, offline bundle stamp extraction,
private staging and final publication checks at the traced seams, independently
review on new exact source, and verify real artifacts in separately authorized
qualification. No production edit or deployment is included in this branch.
