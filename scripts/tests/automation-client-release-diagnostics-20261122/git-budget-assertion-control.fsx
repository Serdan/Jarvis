#r "../../../Client/bin/Release/net11.0/JarvisClient.dll"
#r "../../../Client.Tests/bin/Release/net11.0/nunit.framework.dll"
#r "../../../Client.Tests/bin/Release/net11.0/Client.Tests.dll"
open System
open NUnit.Framework
open Client.Tests.PerformanceRegression

// Bound to the existing first-party real performance guard, no synthetic
// replacement of the 12-process threshold and no modification to NUnit files.
// A fake 13-start observation MUST fail the real assertion while 12 succeeds.
// This is an assertion-negative control, not a claim to reproduce an actual
// LedgerStore Git subprocess under load: that requires the real test/TRX.
let valid =
    { DurationMs = 1.0
      AllocatedBytes = 0L
      GitProcesses = 12
      RemoteFetches = 1
      SingleBlobReads = 1
      BatchBlobReads = 1
      GitStdoutBytes = 0L
      ResultBytes = 4096 }
boundedInbox valid
let rejectsThirteen =
    try
        boundedInbox { valid with GitProcesses = 13 }
        false
    with :? AssertionException -> true
if not rejectsThirteen then
    failwith "The real first-party 12-process gate did not reject its 13-process negative control"
boundedAfterHeadAdvance { valid with GitProcesses = 14 }
printfn "PRODUCTION_GIT_BUDGET_ASSERTION_CONTROL_PASS cold12=PASS cold13=REJECTED head14=PASS"
