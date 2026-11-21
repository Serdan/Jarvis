# Advisory integration gate: separate Worker 18 source test project currently fails to build

**Classification:** Integration-stage evidence blocker, **not** ChangesRequested against the exact five-file Worker 11 correction.

The reviewer independently ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~recurringOutcomeUnknownIsTerminalAndEvidenceSurvivesRestart|FullyQualifiedName~idleOutcomeUnknownIsTerminalAndDoesNotRearmOnNewActivity' --logger 'console;verbosity=minimal'` against **separate** clean Worker 18 SHA `7883e0fe26c0ede07782c4c686ed1a2df836fade`, managed job `219bb177b2374185b279342852922ba9`.

**Actual OS exit 1, compile failed before test discovery/execution**: `Client.Tests/BrowserBridgeTests.fs(939,43): error FS0764: No assignment given for field 'SourceProjectName' of type 'Common.ScheduleBrowserExecutionCommand'`. Do not count those recurrence tests as passed. The reviewer made no source modifications.

Worker 18's correction delta `cab7482..7883e0f` consists only of `Client/Modules/BrowserSchedules.fs` and `Client.Tests/BrowserSchedulesTests.fs`, and has **no overlap** with Worker 11's new five-file correction delta `249f752f8..269bb1ad6`. Worker 11's exact-source independent jobs instead passed 44/44 and targeted adverse cases 3/3. The separately checked failed build limits any claim of already-composed recurrence safety; it is not evidence against Worker 11's resolved error-classification cases.

**Required before final integration:** Integrator must reconcile source and test schemas at exact current main plus the accepted Worker 18 source; repair the field initialization in the *authorized integration scope*, run the merged Release suite with true OS exit/counts, and verify recurring/idle `OutcomeUnknown` survives restart without automatic rearm. Do not alter either immutable accepted/frozen source or silently activate schedules.