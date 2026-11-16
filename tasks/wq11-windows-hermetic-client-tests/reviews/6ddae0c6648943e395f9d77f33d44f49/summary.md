# Independent review — Accepted (narrow test-fixture optimization)

**Frozen source:** `403e41ce0e19052b079ac64dcf4d9532e86489ba`  
**Delta under review:** `2f9ce992..403e41ce` (only `Client.Tests/ChallengeWorkflowTests.fs`); the broader pinned base `1cf0bf7c` contains preceding WQ11 work with separate gates.

Reviewed the exact one-file change. A one-time canonical Git ledger template registers four fixture agents, then each test creates its own private `git clone --mirror --no-hardlinks` of that template and two independent working checkouts. Per-test writes still go to that test's separate bare remote; the template is never modified after setup. Fixture roots are created within try/finally and both setup failures and teardown clean their own private test directories. The NUnit fixture is deliberately serial after the preceding revert, so template initialization and cleanup have no same-fixture concurrency conflict. No production WQ/CAS logic changed.

**Independent local evidence (.NET SDK 11.0.100, frozen source):**
- `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore --nologo --verbosity minimal`: exit 0; two pre-existing FS0760/FS3369 warnings, zero errors.
- `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter FullyQualifiedName~ChallengeWorkflowTests --timeout-seconds 300 --self-test-git-sentinel`: exit 0; **36/36 tests passed** in 54.27s; harness reported child drained, disposable environment removed and scratch cleanup verified.

No blocking findings for the **test-only Git subprocess reduction**. Worker reports an exact-SHA Kruso Windows focused two-test run but I did not independently reproduce a complete Windows suite. **Do not interpret this acceptance as Windows portability/production approval**: the unfiltered full Windows Client.Tests F03 acceptance remains blocked and must remain separate. No GitHub Actions or deployment was run.