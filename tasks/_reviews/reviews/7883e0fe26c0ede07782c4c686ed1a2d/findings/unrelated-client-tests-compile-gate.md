# Nonblocking verification limitation: full Client.Tests cannot currently compile

**Severity for this correction:** external build gate, not a regression in frozen `7883e0fe26c0ede07782c4c686ed1a2df836fade`.

## Independent evidence

`dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~BrowserSchedules --logger console;verbosity=normal` on .NET SDK 11.0.100 in a clean exact-SHA review worktree compiled Common and Client Release, then failed **OS exit 1** while compiling `Client.Tests/BrowserBridgeTests.fs:939,43`:
```
error FS0764: No assignment given for field 'SourceProjectName' of type 'Common.ScheduleBrowserExecutionCommand'
```
This file is untouched in the reviewed correction: `git diff --exit-code cab748286b2252d273445b87e57ebcc226fcaca6 7883e0fe26c0ede07782c4c686ed1a2df836fade -- Client.Tests/BrowserBridgeTests.fs` exits 0. Normal full-project Release test success **cannot** be asserted.

To independently verify the change without touching that unrelated source region, created a QA-only test project under the review worktree's ignored `obj/` directory, linking the **exact original unmodified** `Client.Tests/BrowserSchedulesTests.fs` and `Client.Tests/Program.fs` into an assembly named `Client.Tests` (preserves original F# entrypoint and internal friend assembly access), referencing the exact Client project and matching NUnit packages. The isolated Release command exited 0 with **37 discovered; 36 pass; one optional production fixture skip**, ~6.34 s. The earlier minimal QA harness lacking Program.fs caused four old test fixtures to read an uninitialized top-level SHA constant; an F# reflection probe confirmed the null field, and the faithful full-entrypoint harness eliminated all four failures without touching reviewed code.

## Decision boundary

Do not misreport the full-project gate as green, infer production acceptance, modify `BrowserBridgeTests.fs` under this review, or re-enable live schedules. The unrelated Client.Tests compile issue should be reconciled in its own owner/source lease and full Release run repeated at the appropriate integration gate. For this two-file correction, source provenance, focused Release evidence and all negative paths are independently sufficient for an **Accepted** frozen-source verdict.