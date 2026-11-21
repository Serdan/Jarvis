# P1 pending UUID named-mutex safety — Worker 6 verification (2026-11-21)

## Scope, provenance and authority

- Canonical task: `jarvis-p1-safe-pending-lock-migration-20261120`, attempt `e49ee3e542ba4bb8b70b1b867fdac826`, generation 1, pinned spec blob `a8dd9b77c2bf8bd628beb70b590e906ddd202a9d`.
- Verified source baseline: local `main` / `origin/main` `d702352ef705f9991ac1cb14dc15a13cce78aa44` (the direct child of simplified exact-named-mutex source `86715f7e900e834b6fd9fb22ca82b3463ab38f7e`).
- Registered isolated source lease: `test/pending-mutex-release-qa-worker6-20261121`, worktree `worker6-pending-mutex-qa-20261121`, baseline above. Only `Client/Modules/OrchestrationApi.fs`, `Client.Tests/ReviewWorkflowTests.fs` and this evidence record belong to this task.
- No deployment, client stop/restart/update, pending-operation mutation in a real client, production Git publication, or Scheduler/Health activation was authorized or performed. All test mutations use disposable local test fixtures.

## Material finding and correction

The baseline exact-UUID OS named-mutex serializes same-UUID cross-process callers, but `PendingIntentJournal.executeUnsafe` could invoke `apply` again when an existing saved intent followed an uncertain response. Its file-persistence identity match is not proof the preceding remote effect failed; an abandoned owner similarly cannot establish that fact.

The new regression `PendingLockRefusesOrdinaryReplayAfterAmbiguousIOException` was run against the unmodified source before the fix: **RED**, .NET 11 Release, exit **1**, one failed, with `Ordinary repeat must not replay an ambiguous callback: Ok 17`.

Correction: an ordinary existing-UUID call does not re-invoke a potentially replaying callback. For ledger writes, a matching **already-Applied** canonical receipt is returned by read-only verification under the UUID mutex, without invoking the original effect again; a mismatched or absent receipt remains quarantined. The existing `PendingRecoveryApi.retry` path grants narrowly scoped authorization only after operator identity, stored SHA-256, exact verified canonical head, matched ledger binding, and action allowlist checks. Before invoking an explicitly authorized callback, the mutex-held path rechecks the canonical head and absence of an applied receipt. The existing journal remains for inspection on refusal, IOException, crash or disconnection. Authorization is scoped using `AsyncLocal` with `finally` restoration. The prepublication diagnostic action now says `inspect-pending-and-reconcile` rather than suggesting an ordinary same-UUID retry.

**Open compatibility/liveness trade-off for independent review:** A persisted prepublication DNS or directory-fsync failure now also refuses a normal second call, even where the test can prove the first callback never issued Git I/O. The in-memory observation is not durable proof available to a restarted process. Some actions (notably `YieldAgent`/`ResumeAgent`) are not on the current operator retry allowlist, so operational remediation may require separately authorized supersession or a future proof-bearing durable phase marker. Do not infer approval of a new automatic retry policy from the passing safety tests.

Existing exact per-binding-path UUID mutex naming, 60-second default acquisition timeout, and no per-UUID or stripe lock files remain unchanged. Correctness across true Windows processes requires native Windows evidence and cannot be inferred from Linux success.

## Source-bound Linux Release evidence

Machine: Linux `Serdan-Home`; `dotnet --version` = `11.0.100`; source-bound tests run in the isolated worktree, configuration `Release`, target `net11.0`.

1. Baseline focused named-mutex/cross-process/crash/timeout/lost-response test run: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~PendingIntentNamedMutexRecoversAfterOwnerProcessExits|FullyQualifiedName~PendingIntentExactLocksAllowUnrelatedUUIDsWithinLegacyStripe|FullyQualifiedName~PendingLockNeverRetriesAnAmbiguousIOExceptionCallback|FullyQualifiedName~LostResponseRecoversAcceptedRemoteOperationButNeverGuessesFailure" --logger "console;verbosity=normal"`; exit **0**, **4/4 passed**.
2. RED-before regression: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~PendingLockRefusesOrdinaryReplayAfterAmbiguousIOException" --logger "console;verbosity=normal"`; exit **1**, **0/1 passed** as expected before correction.
3. GREEN-after same focused regression: same command; exit **0**, **1/1 passed**.
4. First adjacent run exposed three obsolete tests expecting ordinary same-UUID replay; exit **1**, **11/14 passed**. These were corrected to assert preservation and explicit recovery, not replay.
5. Corrected adjacent command: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~PendingIntent|FullyQualifiedName~PendingLock|FullyQualifiedName~LostResponse|FullyQualifiedName~OperatorRetry|FullyQualifiedName~PendingNamedMutex|FullyQualifiedName~RemoteOutage" --logger "console;verbosity=normal"`; exit **0**, **14/14 passed**. Includes exact cross-process mutex exclusion; different UUIDs sharing three-character prefix concurrently; owner exit/abandonment; no callback on timeout, exception or restart; journal symlink fail-closed; per-UUID/binding name isolation; no lock artifacts; local handle disposal; canonical identity refusal; and explicit saved-command operator retry.
6. First full `ReviewWorkflowTests` run exposed nine legacy same-UUID auto-replay expectations (post-push, DNS, parent/rename fsync, timeout and resume), while 140 passed: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter "FullyQualifiedName~ReviewWorkflowTests" --logger "console;verbosity=minimal"`; exit **1**, **140/149 passed**. The correction preserves read-only canonical receipt recovery and fail-closes unconfirmed replay, with tests updated to exercise those distinct paths.
7. Expanded focused final-fix matrix: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter "FullyQualifiedName~Pending|FullyQualifiedName~PostPush|FullyQualifiedName~ResumeApiRecords|FullyQualifiedName~TransientPrepublicationDns|FullyQualifiedName~YieldAgentDns|FullyQualifiedName~PublicTaskYieldBindingDns|FullyQualifiedName~PublicReviewBindingDns|FullyQualifiedName~NewDefinitePrepublication|FullyQualifiedName~OperatorRetry" --logger "console;verbosity=minimal"`; exit **0**, **36/36 passed**, including the read-only Applied-receipt continuation without callback replay.
8. Final entire `ReviewWorkflowTests` fixture on final source: `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter "FullyQualifiedName~ReviewWorkflowTests" --logger "console;verbosity=minimal"`; exit **0**, **149/149 passed**, **0 skipped**, elapsed **2m23s**. Distinct Linux evidence only, not Windows acceptance.

**The 60-second real-duration timeout was not separately waited out end-to-end**: source specifies `TimeSpan.FromSeconds(60.)`, while the regression uses the explicitly scoped reduced timeout seam to verify the no-effect timeout branch.

## Blocking acceptance dependency: native Windows

At verification, approved native Windows Jarvis device `KRUSO-PF539JMB` (ID `b04e8889-9ff3-4957-b058-ba50aaeab4dd`) is **Disconnected**, last seen `2026-11-20T16:47:42.4935386+00:00`, protocol 3.68, generation 7. A read-only `cmd.exe /c ver` probe through that selected device failed with a disconnected-client error. **No native Windows tests ran, so cross-platform acceptance is BLOCKED.**

When the operator/Lead restores an approved native Windows connection without a client cutover, independently run the same frozen source under native Windows .NET 11 Release and verify same-UUID process exclusion, same-prefix unrelated UUID concurrency, abandoned holder, timeout/no callback, no per-UUID files, symlink rejection (with appropriate permissions), ambiguity/no replay, binding isolation, and handle cleanup. Record exact command, source SHA, build/test exits and environment. This is a required acceptance gate, not a Linux-derived inference.

## Review and cutover boundaries

Freeze a distinct source commit and submit it to the ordinary independently managed Jarvis review queue with a summary that explicitly says Windows acceptance is not yet established. Do not merge, deploy, stop/restart clients, or activate the scheduler from this task. A future coordinated two-client cutover needs separate explicit Operator authorization after independent acceptance and native Windows evidence.
