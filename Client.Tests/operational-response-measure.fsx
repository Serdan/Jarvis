// Run in a fresh dotnet fsi process with --lib pointing at the exact
// Release Client.Tests output directory of each source snapshot.
// Compatible with pre-control and typed response assemblies. Disposable only.
#r "FsCodec.dll"
#r "FsCodec.SystemTextJson.dll"
#r "FsToolkit.ErrorHandling.dll"
#r "FsToolkit.ErrorHandling.TaskResult.dll"
#r "FSharp.UMX.dll"
#r "Kehlet.FSharp.IO.dll"
#r "Common.dll"
#r "JarvisClient.dll"

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open Common
open Client

let root = Path.Combine(Path.GetTempPath(), "jarvis-response-measure-" + Guid.NewGuid().ToString("N"))
let project = Path.Combine(root, "Demo")
let source = Path.Combine(project, "src")
let io () =
    if OperatingSystem.IsLinux() && File.Exists("/proc/self/io") then
        File.ReadAllLines("/proc/self/io")
        |> Array.choose (fun line ->
            let fields = line.Split(':')
            match fields with
            | [| key; value |] when key = "rchar" || key = "syscr" -> Some(key, Int64.Parse(value.Trim()))
            | _ -> None)
        |> Map.ofArray
    else Map.empty
let quantile (values: float array) p =
    let ordered = Array.sort values
    ordered[max 0 (int (Math.Ceiling(p * float ordered.Length)) - 1)]
try
    Directory.CreateDirectory(source) |> ignore
    File.WriteAllText(Path.Combine(root, "AGENTS.md"), String.replicate 1871 "a")
    File.WriteAllText(Path.Combine(project, "AGENTS.md"), String.replicate 1518 "b")
    File.WriteAllText(Path.Combine(source, "actual.fs"), "module Actual")
    let rt = Runtime(root, Client.ConsoleTui.ConsoleTui(), PartialTrust)
    let command = ReadFileCommand { ProjectName = "Demo"; FilePath = "src/actual.fs"; StartLine = None; EndLine = None; IncludeLineNumbers = None }
    let input = JsonSerializer.Serialize<AgentCommand>(command)
    let invoke () =
        let _, result, response =
            Client.SignalR.Client.processIncomingCommand rt input ""
            |> fun t -> t.GetAwaiter().GetResult()
        match result with
        | Ok value when JsonSerializer.Deserialize<string>(value) = "module Actual" -> response
        | _ -> failwithf "Unexpected source read: %A" result
    // Warm JIT/dispatch once, then 30 measured real calls.
    invoke () |> ignore
    let beforeIO = io ()
    let samples =
        Array.init 30 (fun _ ->
            let clock = Stopwatch.StartNew()
            let response = invoke ()
            let bytes = JsonSerializer.SerializeToUtf8Bytes(response).Length
            clock.Stop()
            clock.Elapsed.TotalMilliseconds, bytes)
    let afterIO = io ()
    let ms = samples |> Array.map fst
    let lengths = samples |> Array.map snd
    let ioDelta = afterIO |> Map.toList |> List.map (fun (key, value) -> key, value - beforeIO[key])
    printfn "n=30 wire_bytes_min=%d wire_bytes_max=%d p50_ms=%.3f p95_ms=%.3f process_read_counters=%A"
        (Array.min lengths) (Array.max lengths) (quantile ms 0.50) (quantile ms 0.95) ioDelta
finally
    if Directory.Exists(root) then Directory.Delete(root, true)
