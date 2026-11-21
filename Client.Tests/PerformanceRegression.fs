module Client.Tests.PerformanceRegression

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open Client.LedgerStore
open NUnit.Framework

type Sample =
    { DurationMs: float
      AllocatedBytes: int64
      GitProcesses: int
      RemoteFetches: int
      SingleBlobReads: int
      BatchBlobReads: int
      GitStdoutBytes: int64
      ResultBytes: int }

let sample (operation: unit -> 'a) (serialize: 'a -> string) =
    let before = GC.GetTotalAllocatedBytes(true)
    let timer = Stopwatch.StartNew()
    let value, counters = LedgerStore.measureGitReads operation
    timer.Stop()
    let allocated = GC.GetTotalAllocatedBytes(true) - before
    let payload = serialize value
    value, payload,
    { DurationMs = timer.Elapsed.TotalMilliseconds
      AllocatedBytes = allocated
      GitProcesses = counters.ProcessStarts
      RemoteFetches = counters.RemoteFetches
      SingleBlobReads = counters.IndividualBlobReads
      BatchBlobReads = counters.BatchBlobReads
      GitStdoutBytes = counters.OutputBytes
      ResultBytes = System.Text.Encoding.UTF8.GetByteCount(payload) }

let private boundedInboxWithMaxProcesses maxProcesses (metric: Sample) =
    Assert.That(metric.GitProcesses, Is.LessThanOrEqualTo(maxProcesses),
                "Inbox must not launch Git subprocesses per historical record.")
    Assert.That(metric.RemoteFetches, Is.EqualTo(1),
                "A canonical inbox read requires exactly one remote fetch.")
    Assert.That(metric.SingleBlobReads, Is.LessThanOrEqualTo(1))
    Assert.That(metric.BatchBlobReads, Is.LessThanOrEqualTo(1))
    Assert.That(metric.GitStdoutBytes,
                Is.LessThanOrEqualTo(int64 metric.ResultBytes * 32L + 65536L),
                "Unexpected Git stdout amplification compared with canonical response.")

let boundedInbox metric = boundedInboxWithMaxProcesses 12 metric

// Verifying remote ancestry across a changed head costs extra bounded Git
// probes. Keep this budget distinct from the tighter warm/cold contract.
let boundedAfterHeadAdvance metric = boundedInboxWithMaxProcesses 14 metric

let sampleCount () =
    match Int32.TryParse(Environment.GetEnvironmentVariable("JARVIS_PERF_SAMPLES")) with
    | true, n when n >= 30 && n <= 100 -> n
    | _ -> 30

let percentile (values: Sample list) p =
    let data = values |> List.map (fun m -> m.DurationMs) |> List.sort |> List.toArray
    let index = max 0 (min (data.Length - 1) (int (Math.Ceiling(float data.Length * p)) - 1))
    data[index]

let statistics values =
    {| count = List.length values
       p50Ms = percentile values 0.50
       p95Ms = percentile values 0.95
       p99Ms = percentile values 0.99
       maxMs = values |> List.maxBy (fun m -> m.DurationMs) |> fun m -> m.DurationMs
       maxAllocatedBytes = values |> List.maxBy (fun m -> m.AllocatedBytes) |> fun m -> m.AllocatedBytes
       maxGitProcesses = values |> List.maxBy (fun m -> m.GitProcesses) |> fun m -> m.GitProcesses
       maxFetches = values |> List.maxBy (fun m -> m.RemoteFetches) |> fun m -> m.RemoteFetches
       maxSingleBlobReads = values |> List.maxBy (fun m -> m.SingleBlobReads) |> fun m -> m.SingleBlobReads
       maxBatchBlobReads = values |> List.maxBy (fun m -> m.BatchBlobReads) |> fun m -> m.BatchBlobReads
       maxGitStdoutBytes = values |> List.maxBy (fun m -> m.GitStdoutBytes) |> fun m -> m.GitStdoutBytes
       responseBytes = values.Head.ResultBytes |}

// Host metadata is informational only; it must never be used as a latency
// acceptance threshold or imply Windows parity with a disconnected host.
let private cpuModel () =
    try
        let model =
            if OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo") then
                File.ReadLines("/proc/cpuinfo")
                |> Seq.tryFind (fun line -> line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun line ->
                    let separator = line.IndexOf(':')
                    if separator >= 0 then line.Substring(separator + 1).Trim() else "")
            else Option.ofObj (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"))
        match model with
        | Some value when not (String.IsNullOrWhiteSpace value) ->
            value.Substring(0, min value.Length 256)
        | _ -> "unknown"
    with _ -> "unknown"

let report sourceSha workload recordCount cold warm mutated samples =
    let index = Environment.GetEnvironmentVariable("JARVIS_PERF_RUN_INDEX")
    let run =
        if String.IsNullOrWhiteSpace index then "1"
        elif index |> Seq.forall Char.IsAsciiDigit then index
        else invalidArg "JARVIS_PERF_RUN_INDEX" "Run number must be decimal."
    let directory =
        match Environment.GetEnvironmentVariable("JARVIS_PERF_REPORT_DIR") with
        | null | "" -> Path.Combine(TestContext.CurrentContext.WorkDirectory, "perf-regression")
        | path -> path
    Directory.CreateDirectory(directory) |> ignore
    let obj =
        {| schemaVersion = 1
           workload = workload
           sourceSha = sourceSha
           configuration = "Release"
           runtime = RuntimeInformation.FrameworkDescription
           os = RuntimeInformation.OSDescription
           architecture = RuntimeInformation.ProcessArchitecture.ToString()
           processorCount = Environment.ProcessorCount
           cpuModel = cpuModel ()
           fixtureRecords = recordCount
           synthetic = true
           run = run
           cold = statistics [cold]
           warmFirst = statistics [warm]
           afterHeadAdvance = statistics [mutated]
           warmSamples = statistics samples |}
    let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let json = JsonSerializer.Serialize(obj, options)
    let file = Path.Combine(directory, $"{workload}-{recordCount}-run-{run}.json")
    File.WriteAllText(file, json)
    TestContext.Progress.WriteLine(json)
    file
