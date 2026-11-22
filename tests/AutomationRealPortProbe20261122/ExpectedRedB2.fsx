// EXPECTED RED: this disposable B1 fence is intentionally absent from the
// normal Client production assembly. Keep excluded from the passing NUnit suite.
// From repository root after the Release build:
// dotnet fsi --exec tests/AutomationRealPortProbe20261122/ExpectedRedB2.fsx
#r "../../Client/bin/Release/net11.0/JarvisClient.dll"
open Client.NativeProjectEffectFence
printfn "If this compiles, investigate accidental B1 exposure in production."
