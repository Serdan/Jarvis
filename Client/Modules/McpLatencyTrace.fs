module Client.McpLatencyTrace

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading

// Opt-in, per-command diagnostics. Only fixed stage names, aggregate counts,
// durations and the server-issued opaque correlation UUID are retained.
// Never record Git arguments, paths, source, actor IDs or request/response bodies.
type private Bucket = { mutable Calls: int; mutable Milliseconds: float }
type Trace =
    private
        { CorrelationId: string
          Started: int64
          Gate: obj
          Buckets: Dictionary<string, Bucket> }

let optedIn () =
    String.Equals(
        Environment.GetEnvironmentVariable("JARVIS_MCP_STAGE_TRACE"),
        "1", StringComparison.Ordinal)

let private ambient = AsyncLocal<Trace>()

let beginTrace enabled (correlationId: string) =
    let previous = ambient.Value
    let validId =
        match Guid.TryParse correlationId with
        | true, id -> id.ToString("D")
        | _ -> "invalid"
    let trace =
        if enabled then
            { CorrelationId = validId
              Started = Stopwatch.GetTimestamp()
              Gate = obj()
              Buckets = Dictionary<string, Bucket>(StringComparer.Ordinal) }
        else Unchecked.defaultof<Trace>
    ambient.Value <- trace
    previous, trace

let restore previous = ambient.Value <- previous

// Never allow a future caller to accidentally turn a dynamically supplied
// Git argument, project label or credential into a telemetry stage name.
let private normalizeStage stage =
    match stage with
    | "json.command_parse"
    | "client.execute_or_promote"
    | "signalr.reply_ack"
    | "git.fetch" | "git.ls-tree" | "git.batch"
    | "git.cat-file" | "git.show" | "git.other"
    | "git.worktree" -> stage
    | _ -> "stage.other"

let private add trace stage milliseconds =
    lock trace.Gate (fun () ->
        match trace.Buckets.TryGetValue stage with
        | true, bucket ->
            bucket.Calls <- bucket.Calls + 1
            bucket.Milliseconds <- bucket.Milliseconds + milliseconds
        | _ ->
            trace.Buckets.Add(stage, { Calls = 1; Milliseconds = milliseconds }))

let record stage started =
    let trace = ambient.Value
    if not (isNull (box trace)) then
        add trace (normalizeStage stage) (Stopwatch.GetElapsedTime(started).TotalMilliseconds)

let recordFor trace stage started =
    if not (isNull (box trace)) then
        add trace (normalizeStage stage) (Stopwatch.GetElapsedTime(started).TotalMilliseconds)

let measure stage (action: unit -> 'a) =
    if isNull (box ambient.Value) then action()
    else
        let started = Stopwatch.GetTimestamp()
        try action()
        finally record stage started

let measureGit (args: string list) (action: unit -> 'a) =
    let stage =
        match args with
        | "fetch" :: _ -> "git.fetch"
        | "ls-tree" :: _ -> "git.ls-tree"
        | "cat-file" :: "--batch" :: _ -> "git.batch"
        | "cat-file" :: _ -> "git.cat-file"
        | "show" :: _ -> "git.show"
        | _ -> "git.other"
    measure stage action

let render trace outcome =
    if isNull (box trace) then None
    else
        // Fixed outcome labels; caller text is never emitted.
        let safeOutcome =
            match outcome with
            | "completed" | "pending" | "failed" -> outcome
            | _ -> "failed"
        let stages =
            lock trace.Gate (fun () ->
                trace.Buckets
                |> Seq.sortBy (fun entry -> entry.Key)
                |> Seq.map (fun entry ->
                    {| stage = entry.Key
                       calls = entry.Value.Calls
                       durationMs = entry.Value.Milliseconds |})
                |> Seq.toArray)
        Some(
            JsonSerializer.Serialize(
                {| schemaVersion = 1
                   source = "client"
                   correlationId = trace.CorrelationId
                   outcome = safeOutcome
                   totalMs = Stopwatch.GetElapsedTime(trace.Started).TotalMilliseconds
                   stages = stages |}))
