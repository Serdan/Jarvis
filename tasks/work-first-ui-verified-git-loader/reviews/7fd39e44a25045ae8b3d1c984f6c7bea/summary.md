# Independent frozen review — Work-first SHA-256 Git head remediation

**Verdict: Accepted** (no new blocking findings).

- **Review:** `7fd39e44a25045ae8b3d1c984f6c7bea`
- **Exact frozen source:** `cf6d43dd219db259ed5e243eecfc1c6029ceef28`
- **Pre-remediation head:** `aeab8b208deafda979339249ead473ba8c7f7809`
- **Canonical base:** `ee50af8b68694e0e5fda1c28a251d07db3363aab`
- **Previous finding:** `d0b4c33f6fa04ab8a8a1a0bb61b58991`, `findings/sha256-head-format-rejected.md` (P2).

## Independent source inspection

The frozen delta changes only `Client/Modules/WorkQueueDashboardReadBinding.fs`, `Client/Modules/WorkDashboardView.fs`, `Client/Modules/LedgerStore.fs` and `Client.Tests/WorkQueueDashboardReadBindingTests.fs`. Both dashboard validators now accept exactly 40 or 64 **lowercase** hexadecimal digits, matching `LedgerStore.oidPattern`. The fetch cache initialization uses a zero old-OID sentinel with the verified head's format length, allowing Git's SHA-256 `update-ref` compare-and-swap on an absent cache ref while preserving the expected existing trusted ref for subsequent CAS.

The positive new fixture creates a real SHA-256 bare Git remote and clone, publishes canonical Work, then reads via `createAccess(...).Load()`, asserting the 64-digit verified head, Verified coverage, read-only state, no claim permission and NotReady queue display. The negative new fixture corrupts the disposable remote ref with 39/41/63/65 hex digits, non-hex, and uppercase cases and asserts the project becomes local UNKNOWN without leaking stale Work or erasing the other approved project.

Existing pinned same-head WQ04 authority checks, account coverage checks and default-off host access remain intact. No production registry, `ConsoleTui.SetWorkAccess` installation, scheduler dispatch, mutation, deployment or cutover is introduced; operator-approved host registration is explicitly **out of scope**, not evidence of operational readiness.

## Independent .NET 11 Release evidence

In a dedicated clean worktree at the exact frozen commit, `dotnet --version` was `11.0.100`.

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueDashboardReadBinding`: **9/9 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~LedgerStore|FullyQualifiedName~WorkQueueDashboardReadBinding|FullyQualifiedName~WorkQueueRoleAuthority|FullyQualifiedName~WorkQueueToolFacade|FullyQualifiedName~WorkDashboardView'`: **43/43 passed** (includes focused nine).
- `git diff --check aeab8b2 cf6d43dd`: exit 0.
- `find /tmp -maxdepth 1 -type d -name 'jarvis-work-read-ui-*'`: none.
- Frozen review worktree status clean after tests.

**Conclusion:** The prior SHA-256 rejection is remedied without adding a safety regression found in the reviewed delta. Accept this exact commit for Lead's normal nonforce integration gate. Do not construe acceptance as approval to wire a production authority or dispatch Work.