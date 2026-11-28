# Independent verification

- Status: running; terminal result not yet observed.
- Job ID: `d6175871f4664bd5aee37b570c7a2aad`.
- Frozen source: `678657eda146512ba139df69c39c20c27be6685e` (reviewed against `89d9e38531869ca6f4fa4e514f9d2f4ed13e51a1`).
- Command: `dotnet test Client.Tests/Client.Tests.fsproj --no-restore --configuration Release --filter FullyQualifiedName~BrowserBridgeTests.projectRouteAcknowledgementRetainsOnlyBoundUnknown --logger trx;LogFileName=reviewer9-route-retention.trx`.
- Purpose: independently execute the focused Release regression matrix for route retention, typed acknowledgement markers, and project-slug grammar.
- Required terminal evidence: process exit code, test summary (passed/failed/skipped), and TRX result. No acceptance claim is made from the running job.