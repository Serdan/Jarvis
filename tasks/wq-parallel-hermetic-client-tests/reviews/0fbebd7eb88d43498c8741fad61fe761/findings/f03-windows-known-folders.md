# F03 — P1: Windows known-folder paths cannot be redirected by APPDATA variables alone

Frozen source: `scripts/test-client-hermetic.fsx:125-150`, `Client.Tests/HermeticHarnessTests.fs:39-77`, `Client/Modules/ClientConfig.fs:48-57`, `Client/Modules/ActivityLog.fs:31-35`.

**Issue.** The runner overrides `APPDATA`, `LOCALAPPDATA`, `USERPROFILE` and checks `Environment.GetFolderPath(SpecialFolder.ApplicationData/LocalApplicationData)` is within a disposable root. On Windows these .NET calls resolve Shell known folders for the user profile, which generally remain at the real profile paths regardless of process env overrides. Consequently setup rejects every ordinary Windows testhost before executing the suite. Dropping the assertions would instead expose the real profile to config/activity I/O.

**Impact.** Linux targeted smoke passes; this is not a viable cross-platform hermetic Client harness on Windows, an important Client target.

**Required fix.** Use a test-only injectable path resolver for Client config/activity/ledger paths or run under a genuinely disposable Windows profile with known-folder redirection. Preserve the fail-closed checks. Add a Windows .NET 11 Release targeted test proving user-specific paths and identity are isolated. If the task explicitly only supports Linux, state that limit and track a separate Windows fix before representing the runner as generally portable.

**Evidence.** Source-level platform reasoning; no Windows testhost was available to execute in this review.