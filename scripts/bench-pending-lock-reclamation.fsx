#I "../Client/bin/Release/net11.0"
#r "Common.dll"
#r "JarvisClient.dll"

open System
open System.IO
open System.Diagnostics
open System.Text.Json

let root = Path.Combine(Path.GetTempPath(), "jarvis-lock-reclaim-profile-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(root) |> ignore
let binding : Client.LedgerStore.LedgerBinding =
    { RepositoryPath = root; RemoteName = "origin"
      ApprovedFetchUrl = root; ApprovedPushUrl = root
      ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
let bindingPath = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot root
let folder =
    Path.Combine(Path.GetDirectoryName(bindingPath), "pending",
                 Path.GetFileNameWithoutExtension bindingPath, ".locks")
Directory.CreateDirectory(folder) |> ignore
let persist (id: Guid) =
    let payload = JsonSerializer.Serialize(
        {| Action = "CreateTask"; TaskId = id.ToString("N"); Title = "Perf" |})
    match Client.OrchestrationApi.PendingIntentJournal.persist binding id "TaskWrite" payload with
    | Ok () -> ()
    | Error error -> failwithf "Persist failed: %A" error
try
    // All numbers below are locally measured, not production latency.
    for target in [0; 1000; 5000; 20000] do
        let current = Directory.GetFiles(folder).Length
        for i in current .. target - 1 do
            File.WriteAllText(Path.Combine(folder, sprintf "unrelated-%06d.lock" i), "")
        let sample =
            [ for _ in 1..5 do
                let watch = Stopwatch.StartNew()
                persist (Guid.NewGuid())
                watch.Stop()
                yield watch.Elapsed.TotalMilliseconds ]
            |> List.sort
        printfn "Unrelated lock artifacts=%d, persist+cleanup p50=%.2f ms, max=%.2f ms"
            target sample.[2] sample.[4]
    let prefix = Guid.NewGuid().ToString("N").Substring(0, 3)
    let stripe = Path.Combine(folder, prefix + ".lock")
    let old =
        [ for i in 1..256 do
            let id = Guid.ParseExact(prefix + sprintf "%029x" i, "N")
            yield Path.Combine(folder, id.ToString("N") + ".lock") ]
    for path in old do File.WriteAllText(path,"")
    let watch = Stopwatch.StartNew()
    persist (Guid.ParseExact(prefix + String.replicate 28 "f" + "1", "N"))
    watch.Stop()
    printfn "256 stale same-stripe exact files, cleanup elapsed=%.2f ms; remaining=%d"
        watch.Elapsed.TotalMilliseconds (old |> List.filter File.Exists |> List.length)
finally
    if Directory.Exists(root) then Directory.Delete(root, true)
