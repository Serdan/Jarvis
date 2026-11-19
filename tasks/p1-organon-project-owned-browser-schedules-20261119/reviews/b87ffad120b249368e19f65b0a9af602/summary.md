# Reviewer 2 — independent frozen correction review

**Recommended verdict: Accepted for the frozen default-off source correction, with a HARD Release/integration/rollout HOLD.**  
**Review ID:** `b87ffad120b249368e19f65b0a9af602`  
**Frozen head:** `cab748286b2252d273445b87e57ebcc226fcaca6`  
**Pinned baseline:** `a83549a6897a4e26aa4834ff81dc38b6e16ee85e`  
**Previous ChangesRequested review:** `c8297b98feba4df29b0c9f7a3a25e016`, frozen `67427229bb05fdb3100655ca8f2568049f0b9431`.

## Independent inspection

The frozen branch contains exactly two commits relative to the pinned baseline: the eight-file owner/source separation and the three-file SHA-256 correction. The registered review worktree was clean and `git rev-parse HEAD` matched the frozen head after testing.

The previous major provenance finding is addressed in the inspected source. `Client/Modules/BrowserSchedules.fs` persists `ScriptSha256` in schema v5, obtains a digest through a confined source resolver at both scheduling APIs and guarded owner rebind, rejects missing/invalid cross-project proof, and suspends legacy cross-project records lacking a digest. `Client/Program.fs` reads the script with the normal source-project resolver and strict UTF-8 decoding, hashes the bytes it actually read, compares against the stored digest, and sends those *same read bytes as decoded script* to the browser only after authorization. Revision mismatch is a non-retrying suspension instead of silently following mutable launcher content. Legacy same-project records remain compatible.

The original owner/source split maps source authorization independently from schedule owner permission for both public APIs; `RunCommand` remains owner-bound. The rebind preserves schedule revision, due time, attempt, protected arguments, window and result state, without touching live schedules. No out-of-lease source edit appears in the two-commit diff.

## Independently observed build/test evidence

- `dotnet build Server/Server.fsproj -c Release --no-restore --verbosity minimal`: **exit 0; 0 errors, 2 warnings** (FS0066, FS3511).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserSchedules --verbosity minimal`: **exit 1; 0 tests executed**. The transitive Common and Client Release compile succeeded; `Client.Tests/BrowserBridgeTests.fs(939,43)` failed **FS0764** because a Worker 22-owned test constructor lacks `SourceProjectName`. See `findings/release-test-hold.md`.
- The author reports 8/8 tests via a disposable FSI harness, but this is **not independently verified** and is **not** a combined Release test PASS. Exact runtime source-hash tests remain gated by the shared constructor.

## Disposition and hard boundaries

**Accept only the reviewed source correction** closing Reviewer 3's earlier mutable-source finding; there is no independently verified full Client.Tests pass and no authorization for integration, activation, restart, live schedule reconciliation, browser execution, or Work cutover. Integrator must retain HOLD until an authorized Worker 22 handoff/integration fixes the external test constructor and combined non-RED Release evidence is recorded for the exact integrated source. No changes to the four live schedules or persisted private prompts were performed during this review.