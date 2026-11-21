# Supplementary regression-run limitation

After the seven source-exact .NET 11 Release TaskOverview tests passed, Reviewer 2 initiated an additional non-required surrounding correctness sweep:

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~LedgerStoreTests|FullyQualifiedName~ReviewWorkflowTests' --logger console;verbosity=minimal`

Job `90429882782e49bf962bb59ca455cd2a` reached its configured 180-second limit (`TimedOut=true`, exit -1). It reported test assembly discovery but no pass/fail counts before termination. Other independent dotnet builds/tests were observed concurrently on the same host. This sweep is **inconclusive, not a pass** and not evidence of test failure. Focused source-bound TaskOverview tests were a separate successful run with 7/7 passed; the acceptance evidence rests on that run plus source inspection and direct process metrics. The optional broader sweep may be rerun separately with a dedicated resource slot if further assurance is needed.
