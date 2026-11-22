// RED on accepted-only baseline: adapter must be absent from production DLL.
// GREEN only after B1 fsproj host registration: production exports an inert
// default-OFF function and performs no canonical reads, wakes or dispatch.
#r "../../Common/bin/Release/net11.0/Common.dll"
#r "../../Client/bin/Release/net11.0/JarvisClient.dll"
open Client.AutomationChallengeWakeAdapter
match inspectCommitted false Unchecked.defaultof<_> Unchecked.defaultof<_> Unchecked.defaultof<_> with
| CommittedWakeDecision.Off -> printfn "GREEN: production compiled Challenge wake is strictly default-OFF"
| other -> failwithf "Disabled adapter produced an effect: %A" other
