// EXPECTED RED: the Challenge -> reactive scheduler adapter is deliberately
// excluded from the production Client.fsproj. No runtime host registration
// exists and this must not turn into a green production API accidentally.
// Run after the isolated Release build, from repository root:
// dotnet fsi --exec tests/AutomationChallengeWake20261122/ExpectedRedHostHook.fsx
#r "../../Client/bin/Release/net11.0/JarvisClient.dll"
open Client.AutomationChallengeWakeAdapter
printfn "Unexpected: unwired Challenge adapter was exported by production Client."
