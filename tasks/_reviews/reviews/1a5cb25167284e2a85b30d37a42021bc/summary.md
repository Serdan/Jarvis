# Independent review — ChangesRequested

**Request:** `_reviews/1a5cb25167284e2a85b30d37a42021bc`  
**Reviewer:** Reviewer 3  
**Frozen source:** `6c291da9ab43217fcaa732952386ae85601c395d`  
**Base:** `bed92f3d2b39ef7c256a8d1955c48883af99cb60`  
**Outcome:** **ChangesRequested** — full Server .NET 11 Release suite is red and `ConnectWork` is missing from the canonical public command catalog. See `findings/connectwork-command-catalog.md`.

## Independent inspection

- Exact checkout verified with `git rev-parse HEAD`; clean `git status --porcelain`; `git diff --check <base> <head>` exits 0. Diff scope 9 files, +270/-24 lines.
- `IssuedWorkCredential.Issue` uses .NET 11 `Guid.NewGuid().ToString("D")`, canonical lowercase RFC 4122 UUIDv4/variant, deliberately distinct from public launch UUID, with 122 random bits. `VerifierSha256` retains original SHA-256 of the bearer text; redacted `ToString()`. New `RevealForExplicitToolArgument` supplies the transient value.
- `WorkCredentialFormat.isSupportedBearer` keeps existing 43-character Base64URL verifier inputs while accepting canonical UUIDv4. `WorkCredentialVerifier` retains original schema 1 with project/launch/account scope, compares SHA-256 digests in fixed time at the pinned original Git verifier. No new identity/credential ledger or forged label fallback.
- `ConnectWork`, `ClaimNextWork`, `CheckpointWork`, `CompleteWork` expose explicit model-provided `credential` and forward through `WorkQueueWriteCommand.Credential` into `transitionWithExplicitCredential`. Server derives authenticated operator actor separately from supplied `agentId`. Client permission-approval arguments and command descriptions omit bearer preimage.
- Public Work writes load current Git head, check version, authenticate original launch scope/secret and **return `RejectedMissingBinding`** with `Applied=false`: no live registration/claim/checkpoint/complete, host binding, Scheduler, automatic private injection or cutover. WQ02 fixture still owns winning-CAS authorization, generation, exact-receipt replay, STOP and cross-launch negatives.
- Reviewed existing native fake dispatch and permission paths for stale 43-character-only verification; both verifier sites updated and test source continues to exercise two-clone, wrong credential, STOP and replay.
- **Coverage note:** new facade tests exercise Connect and Claim directly; Checkpoint/Complete are statically inspected through same authenticated facade and rely on adjacent fixture/permission coverage. Adding direct positive-auth/default-off and invalid-bearer tests for both terminal MCP methods is prudent in the corrective patch.

## Local .NET 11 Release evidence

| Command | Result |
|---|---|
| `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueCredentialTests --no-restore` | **6/6 passed**, exit 0 |
| `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter <native-dispatch/private-host/reservation-permission/practical-admission/legacy-fence/integration-source classes>` | **26/26 passed**, exit 0 |
| `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` | **Failed**, exit 1. `ConnectWork` caused write tool 67→68 and client-backed tool 159→160 inventory failures and command catalog parity mismatch. |
| `git diff --check <base> <head>`, `git status --porcelain` | **Passed**, clean checkout |

No hosted Actions, Codex, live source execution, real Scheduler, deployment or production cutover was performed.

## Required disposition

**ChangesRequested.** Add `ConnectWork` as a public `WorkQueueWrite` alias in `Server/McpTools.fs`, update server method counts and catalog tests, verify the new credential parameter and metadata on all four Work write MCP methods, rerun complete Server.Tests Release green, and submit a **new pinned frozen independent review**. Preserve original verifier, default-off behavior and all Client tests.