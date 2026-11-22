# Reviewer 2 — independent phase-1 packaging review

**Verdict: ChangesRequested** for immutable `9c0427dde4d25f1e2d684f85a85b1fd6fd0228c7`, request `f51f93a9271b4c2ca90a473fb43d05c2`, baseline `dcac884fc061869572b12a03befc12f11c009f2f`. This is the **phase-1 packaging lease**, not final judgment on all Windows runtime cleanup; inventory `6e0f099f` was separately Accepted for inventory only.

## Frozen scope and positive evidence

- Verified registered Worker26 worktree is clean and at the exact reviewed commit. Diff touches only `scripts/build.cs`, `scripts/publish-client-downloads.cs`, `docs/ClientDownloads.md`, new `scripts/tests/client-packaging-retirement-20261122/packaging.test.mjs`. No SelfUpdate, browser bridge, security, P0 settings, shared projects, main, deployment or live client edits.
- `build.cs` rejects unsupported/mixed native Windows client RIDs before spawn and leaves generic Server RID publishing unchanged. The staged publisher produces Linux x64 and macOS ARM64 releases and a two-file SHA256SUMS, omitting Windows.
- Independent exact-commit Node suite: job `d0312f88638d4b1bb8790ba65ed28359`, exit 0, **10/10**, including symlink and foreign non-prefix file protections, fail-second-RID, stale prior output and two negative mutants.
- Examined Worker26 completed jobs on this same worktree: `db516cb10210415596ffe00851f13de8` .NET11 Release solution build exit0; `4f7632b1eb204f1a8fd2fa49634b0f14` real Linux/macOS Release cross-publish exit0; `90bde72f24274e99a72e636c9506b71f` real SHA256SUMS both OK; `d8ee0782c6254a0da8f486bd2179660d` existing SelfUpdate Release focused TRX **4/4**, exit0. No native macOS execution claimed.
- No full Client/Server TRX, WSL project-discovery scratch, native mutex/crash/lease full gates, or Windows Chrome-to-WSL handshake is claimed for this bounded packaging phase. Bridge and opt-in auth source remain unchanged, but end-to-end interoperability is not established here.

## Findings on frozen source

1. **F1 P1, blocking — data loss:** `findings/unmanaged-prefix-data-loss.md`. `scripts/publish-client-downloads.cs:120` allows any `JarvisClient-`-prefixed file as publisher-managed. Successful directory swap deletes unrelated user content. Independent disposable scratch job `8a806abe89c24f309045c1c6e80b4250` returned `{"exitCode":0,"wasPersonalFileDeleted":true}` with `JarvisClient-personal-ledger`; the existing test only checks `personal-notes.txt`. Contradicts explicit unmanaged-file/data-preservation gate.
2. **F2 P2 — broken documented checksum URL:** `findings/checksum-doc-url.md`. `docs/ClientDownloads.md:101` contains a duplicated `https://jarvis2.kehlet.dev/downloads/` prefix.

Both defects are within the original packaging safety/documentation obligations. Suggested narrow remedies are recorded separately; they are not unilaterally imposed as new scope. Do not import/deploy this source. A source correction requires new tests and fresh frozen independent review; historical inventory and evidence remain intact.
