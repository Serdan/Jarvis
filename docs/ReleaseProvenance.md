# CI and release artifact provenance

## Continuous validation

`.github/workflows/ci.yml` runs on pull requests and pushes to `main`. Linux and Windows jobs use .NET 11, locked dependency restore, Release solution build, Server Release tests, and provenance negative controls. **Client Release tests are supported in Linux only**, through the existing isolated `scripts/test-client-hermetic.fsx` runner. Windows runs only the compile and Server tests: the Client test harness deliberately rejects Windows until known-folder isolation is proved. This omission is explicit, not evidence of Windows Client test success. See `scripts/test-client-hermetic.md` for the limitation. The local provenance black-box tests use temporary Git repositories and never deploy or contact production.

Reproduce the checks at the repository root:

```sh
dotnet restore Jarvis.slnx --locked-mode
dotnet build Jarvis.slnx --configuration Release --no-restore
dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --timeout-seconds 900  # Linux only; individual-test hang detection remains 75s
dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-build --no-restore
python scripts/tests/test-release-provenance.py
```

The five F# project `packages.lock.json` files are committed and enforced through `Directory.Build.props`. The locked graph covers `linux-x64`, `win-x64`, and `osx-arm64`; release publishing selects one via `-p:RuntimeIdentifier` without discarding the other locked targets. When intentionally updating dependencies, run `dotnet restore Jarvis.slnx --force-evaluate`, inspect all lockfile changes, and commit the reviewed graph. Do not run an unlocked restore in CI.

GitHub Actions reports check failures but **does not itself prevent merging**. Configure repository branch protection / rulesets to require the `Release validation (ubuntu-latest)` and `Release validation (windows-latest)` checks before merging `main`. No Auth0, DigitalOcean, or production credentials are used by the jobs.

## Explicit release procedure

Publishing is always a deliberate local operation. Start from the **exact reviewed Git commit**, with a clean tracked and untracked worktree. Review the local commit and target branch/ref before starting. The release gate refuses dirty source and detects a commit change while publishing.

```sh
# In a clean, trusted checkout at the reviewed commit:
dotnet scripts/build.cs clean
dotnet restore Jarvis.slnx --locked-mode
dotnet scripts/publish-client-downloads.cs
dotnet scripts/publish-server-download.cs
dotnet scripts/release-provenance.cs verify --complete
```

The client script cross-publishes `linux-x64`, `win-x64`, and `osx-arm64`; the server script publishes `linux-x64`. Both write only to local `artifacts/downloads`. No script in this workflow uploads, deploys, or restarts services. The existing server updater still independently checks `SHA256SUMS` and preserves its rollback/recovery behavior.

The release directory contains:

- `JarvisClient-linux-x64`, `JarvisClient-win-x64.exe`, `JarvisClient-osx-arm64`, `JarvisServer-linux-x64`;
- `RELEASE-PROVENANCE.json` with exact source commit, source ref, SDK version, deterministic SHA-256 fingerprint of the five dependency lockfiles, configuration, runtime identifier, embedded client URL where applicable, file names, and SHA-256 hashes;
- `SHA256SUMS` covering every binary **and** `RELEASE-PROVENANCE.json`.

Each direct `dotnet scripts/build.cs publish-client` or `publish-server` Release invocation also writes an adjacent one-binary provenance stamp. Download collection verifies the stamp's commit, runtime, SDK, lockfile fingerprint, configuration, embedded server URL consistency, and actual file SHA-256 before copying. Existing downloads from a different commit/SDK/dependency graph, unstamped files, unknown files, or mismatched hashes are rejected rather than silently combined. If starting another release, explicitly clean the old `artifacts/downloads` after preserving/archiving any needed release evidence.

To verify externally delivered files, obtain the binaries, manifest, and `SHA256SUMS` from the same release, check their hashes, and compare the manifest's exact `sourceCommit` with the reviewed Git commit and its source ref. The manifest is an **audit trail, not a digital signature**: SHA-256 only detects accidental or unauthorized changes when the expected hash/manifest is acquired from a trusted channel. It does not independently prove who built the artifacts. Keep reviews, release authorization, and authenticated delivery separate.

## Recovery and failure behavior

A failed restore, build, Release test, dirty-worktree check, or provenance verification is not releasable. Do not upload partially assembled local downloads. Rebuild from the reviewed clean commit and rerun `verify --complete` before using the existing manual upload/deployment instructions in `ClientDownloads.md` and `ServerUpdates.md`. CI never deploys and should not receive release credentials.
