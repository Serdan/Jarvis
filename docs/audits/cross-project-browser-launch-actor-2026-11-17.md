# P0 cross-project browser-launch actor isolation

Date: 2026-11-17  
Owner: Jarvis Worker 5  
Task: `p0-cross-project-launch-actor-isolation`  
Branch: `fix/cross-project-launch-actor-worker5`  
Baseline: `1046d5a4647ef4cdc39f33f94f3d04a5c60d0279`

## Incident and evidence boundary

The task reports Jarvis Manager's yield operation `d331300c-e08c-4f01-a2a6-12563c11a4d1` at ledger sequence 6983 followed by apparent activation at sequence 6985. The historical exact invoking browser call is not in this source checkout. Attribution of that particular activation to the Favn launcher remains an incident hypothesis; a prompt containing a Favn role is not identity evidence.

Favn Manager's notification `eae691fc4e4348e088c61636ace64869` reports a project-local copy of the launcher at Favn GitHub blob `4183036f638ed346d01a930705895418ec12c000`, in registered Favn worktree `favn-manager-browser-bridge`, invoked with `projectName=Favn` and `agentId=Manager`. Favn reports three reviewer launches, all with cleanup. This is an operational report, not a replayed execution proof or independently verified source-commit review. The notification was acknowledged.

## Confirmed attribution path (static source)

1. `Server/McpTools.fs`, `BrowserRunInNewTab`, composes `projectName` with the optional `worktreeName` and sends `BrowserRunInNewTabCommand` containing that resulting project. `agentId` is an independent caller parameter, not a value from page JavaScript.
2. `Common/Messages/AgentCommand.fs`, `AgentCommandInfo.projectName`, returns `cmd.ProjectName` for that command; `isAgentActivity` treats a browser launch as attributed agent activity.
3. `Server/Services/ClientService.fs`, `SendCommandToUser`, records a best-effort activity hint using the command project and supplied caller actor. `RecordAgentActivityForUser` removes the registered `::worktree` suffix for cross-device delivery. It does not authenticate an actor from prompt contents. These hints are not authoritative launch authorization.
4. `Client/SignalR/Client.fs` resolves the JavaScript file using `cmd.ProjectName` and `cmd.ScriptPath`. `browser-scripts/chatgpt/launchAgent.js` is a page-facing `execute(args)` implementation and cannot select project actor provenance.

Therefore, a call with `projectName=Jarvis` and `agentId=Manager` is **Jarvis/Manager activity**, whatever the prompt says. A real Favn Manager launch must use `projectName=Favn` and `agentId=Manager` with a file owned by Favn (optionally a registered Favn worktree). This is a caller-argument identity boundary, not a script-name or content-identity boundary. Cross-project script reuse by selecting its foreign source project conflates these roles.

## Corrective scope

- Document the actual identity contract in the tool's description and browser-bridge instructions.
- Add offline regressions for project-specific launch-command attribution, even with misleading prompt content, and independent `AfterAgentIdle` deadlines for identically named `Manager` agents in Favn and Jarvis.
- Preserve existing project path validation, permission gating, one-shot invocation, uncertain-send journaling, and created-tab cleanup; no changes to the launch implementation or live scheduling switches.
- Retain the project-local launcher as the operational fix. Do **not** add an implicit cross-project script-resolution override: that would require a separately reviewed caller/source authority design.

## What this does not prove

No historical browser dispatch receipt, individual Favn reviewer launch IDs, or raw account-level sequence 6985 cause was independently inspected here. The source tests demonstrate invariants of current code, not why a specific old Jarvis Manager session reactivated. Tests must not be construed as real browser launch proof. No future uncertain browser action may be retried automatically, no genuine Jarvis activity is suppressed, and this audit does not authorize a server/client deployment or an unattended scheduler cutover.

## Review gates

Require a clean, exact Git commit and independent frozen review of the changed source/test/docs paths before integration. Verify a Release-mode build and narrow regressions, recording actual results. If restore/build infrastructure is unavailable, mark tests UNVERIFIED rather than inferring success from process exit alone. The Integrator alone chooses eligible reviewed integration. Manual Favn/Jarvis same-name browser launch verification, if ever requested, needs explicit authorization and one-shot effect-state checks.

## Executed offline verification (2026-11-17)

`dotnet build Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --verbosity quiet`: **PASSED**, 0 errors, three pre-existing warnings (FS0760, FS0066, FS3369). The first build failed due to NUnit `Is.EqualTo` array overload ambiguity in the newly added assertions; this was corrected. The first narrow test execution returned 1/2 because the test fixture mistakenly gave Jarvis Manager no trusted activity watermark, triggering the intended fail-closed `ActivityProofUnavailable` behavior. Corrected fixture now supplies both managers' trusted initial watermarks and updates only Favn.

`dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter FullyQualifiedName~crossProjectBrowserLaunchUsesExplicitProjectForCallerActivity|FullyQualifiedName~sameNamedManagersAcrossProjectsHaveIndependentIdleDeadlines --verbosity quiet`: **PASSED**, 2/2 tests, 0 failed, 0 skipped, 125ms test duration. No real browser launch was performed by these tests. Server Release build must be separately recorded if performed.

`dotnet build Server/Server.fsproj --configuration Release --verbosity quiet`: **PASSED**, 0 errors, two existing compiler warnings (FS0066, FS3511). This is a source compilation check; neither client nor server was deployed/restarted.

## Independent review remediation (review a5c8e63b1e234aee9c716c6475d59385, decision 7072)

Reviewer 2 returned **ChangesRequested** because original metadata-only tests did not traverse the server activity recorder, exact Favn launch evidence was not independently bound, and Ready/Working and uncertain-result tests were not identified. Feedback was acknowledged. This amendment does **not** claim P0 closure.

- Extended `Server.Tests/ServiceTests.fs` test `attributed_tool_dispatch_survives_missing_sqlite_and_notifies_only_user_devices` to pass synthetic `BrowserRunInNewTabCommand` values through `ClientService.SendCommandToUser` and capture actual `RecordAgentActivity` calls on registered devices: a `Favn::favn-manager-browser-bridge` source produces **Favn/Manager** hints; a Jarvis source produces **Jarvis/Manager** hints, despite misleading cross-project prompt text. The test also checks an unrelated user's device receives none, an `UncertainOutcome` Favn browser response remains unknown, that response is dispatched once without retry, and its hints cannot reset Jarvis/Manager. Hints remain explicitly nonauthoritative.
- Release build `dotnet build Server.Tests/Server.Tests.fsproj --configuration Release --no-restore --verbosity quiet`: **PASSED**, zero errors (existing warnings). Release `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~attributed_tool_dispatch_survives_missing_sqlite_and_notifies_only_user_devices|FullyQualifiedName~post_commit_uncertain_client_reply_remains_unknown_across_server_dispatch_and_public_mcp' --verbosity normal`: **PASSED 2/2**, zero skipped or failed. Mock SignalR only; no browser effects.
- Existing precise state/idle tests rerun: `ImplicitActivityStartsReadyAssignmentAndPreservesWorkingClock` (`Client.Tests/ReviewWorkflowTests.fs:3204-3356`, Ready -> Working -> yielded Ready -> Working), `sameNamedManagersAcrossProjectsHaveIndependentIdleDeadlines` (`Client.Tests/BrowserSchedulesTests.fs:714-758`), `missingCrossDeviceActivityProofBlocksBothClientsAndRestartedIdleSchedules` (`Client.Tests/BrowserSchedulesTests.fs:639-689`), `idleLaunchFailsClosedWhenDisconnectedAndReconcilesMissedOtherDeviceActivity` (`Client.Tests/BrowserSchedulesTests.fs:431-475`). Release tests: **PASSED 4/4**.
- Existing `post_commit_uncertain_client_reply_remains_unknown_across_server_dispatch_and_public_mcp` (`Server.Tests/ServiceTests.fs:1750-1842`) checks no duplicate after post-commit ambiguity; synthetic Favn-specific ambiguity now also exercises the browser command path.

**Remaining authorization/evidence boundary:** this code still accepts caller-supplied `projectName` and `agentId`. Tests establish correct routing **when the actual caller selects its own project**, not that a foreign project name can never be supplied. The previously reported Favn-owned launcher and actual `projectName=Favn` invocation remain unverified independently. A bounded request went to Favn Manager via cross-project notification operation `ba36687d-ea06-4a41-ab88-5b860db9bf11` (Favn ledger sequence 3596); no launch was replayed. Until exact original source/invocation/dispatch evidence is received or a separately reviewed caller-project/script-source authority boundary is designed, **P0 remains HOLD**. Historical Jarvis sequence 6985 cause remains **UNKNOWN**. No deployment, scheduler activation or integration authority follows from these tests.
