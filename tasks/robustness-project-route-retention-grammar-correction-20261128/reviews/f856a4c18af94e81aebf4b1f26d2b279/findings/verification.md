# Verification record

- Frozen source: `678657eda146512ba139df69c39c20c27be6685e`; base: `89d9e38531869ca6f4fa4e514f9d2f4ed13e51a1`.
- Independent Reviewer 9 filtered Release run, job `d6175871f4664bd5aee37b570c7a2aad`: terminal `Completed`, exit code 0; 32 passed, 0 failed, 0 skipped (3 seconds). Command: `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --configuration Release --filter FullyQualifiedName~BrowserBridgeTests.projectRouteAcknowledgementRetainsOnlyBoundUnknown --logger trx;LogFileName=reviewer9-route-retention.trx`.
- Existing full `BrowserBridgeTests` Release TRX, `Client.Tests/TestResults/route-retention-grammar-green-678657ed.trx`, read from the same frozen worktree: 84 executed, 84 passed, 0 failed; all 32 route-retention cases passed.
- The full Client test suite was not rerun on this source.
- The independent filtered build emitted the unrelated existing recommendation `FS0760` at `Client/Program.fs:834`; the build and tests completed successfully.
- No live sends, deployment, merge, or source edits were performed.