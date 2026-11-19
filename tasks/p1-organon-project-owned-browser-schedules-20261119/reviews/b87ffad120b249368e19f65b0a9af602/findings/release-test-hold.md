# Release verification blocked by shared-file constructor (external lease)

**Reviewed SHA:** `cab748286b2252d273445b87e57ebcc226fcaca6`  
**Severity:** Release gate HOLD; does **not** by itself request a change to Worker 18's leased source.

Independent command in the clean, exact frozen worktree:
`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~BrowserSchedules --verbosity minimal`

**Actual result:** exit 1 before tests executed. Common and Client compiled in Release, then the Client.Tests F# compile failed with **FS0764** at `Client.Tests/BrowserBridgeTests.fs(939,43)`: no assignment for `SourceProjectName` on `Common.ScheduleBrowserExecutionCommand`. Worker 22 has the exclusive lease for this shared test surface under the canonical task contract; Worker 18 must not edit it or weaken the test. Do **not** count targeted tests as independently passing.

Independent command:
`dotnet build Server/Server.fsproj -c Release --no-restore --verbosity minimal`

**Actual result:** exit 0, 0 errors, 2 warnings (FS0066 and FS3511). The tested worktree HEAD was separately verified as the reviewed SHA.

**Required before integration / runtime consideration:** Obtain the safe Worker 22 shared-file handoff or its integrated correction, update the affected constructor under an authorized lease, then run combined non-RED Release evidence on the exact integrated source. Preserve default-off, do not update/restart runtime, change four live schedules or infer Work cutover GO.