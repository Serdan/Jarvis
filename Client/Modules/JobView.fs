module Client.JobView

open System
open System.Collections.Generic
open Common

type OutputLine =
    { Sequence: int64
      Stream: JobOutputStream
      Text: string }

let isRunning (job: JobSummary) = job.Status = JobStatus.Running

let statusText = function
    | JobStatus.Running -> "Running"
    | JobStatus.Completed code -> $"Exited {code}"
    | JobStatus.Canceled -> "Canceled"
    | JobStatus.FailedToStart message -> $"Failed: {message}"

let safeLine (value: string) =
    if isNull value then ""
    else
        value
        |> Seq.map (fun ch ->
            if Char.IsControl(ch) || ch = '\u001b' || ch = '\u007f' then ' ' else ch)
        |> Array.ofSeq
        |> fun chars -> String(chars)

let visibleJobs includeCompleted (projectFilter: string) (jobs: JobSummary list) =
    jobs
    |> List.filter (fun job ->
        (includeCompleted || isRunning job)
        && (String.IsNullOrWhiteSpace projectFilter
            || job.ProjectName.Contains(projectFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
    |> List.sortWith (fun left right ->
        let byRunning = compare (if isRunning left then 0 else 1) (if isRunning right then 0 else 1)
        if byRunning <> 0 then byRunning
        else
            let byStart = compare right.StartedAt left.StartedAt
            if byStart <> 0 then byStart else String.CompareOrdinal(left.JobId, right.JobId))

let duration (now: DateTimeOffset) (job: JobSummary) =
    let finish = job.CompletedAt |> Option.defaultValue now
    let seconds = max 0L (int64 (finish - job.StartedAt).TotalSeconds)
    if seconds >= 3600L then sprintf "%dh %02dm" (seconds / 3600L) ((seconds % 3600L) / 60L)
    elif seconds >= 60L then sprintf "%dm %02ds" (seconds / 60L) (seconds % 60L)
    else sprintf "%ds" seconds

// Output from JobManager is bounded by bytes, but the TUI also caps retained lines.
let appendOutput maxLines (current: OutputLine list) (events: JobOutputEvent list) =
    let output = ResizeArray<OutputLine>(current)
    for event in events do
        // Each event can contain multiple lines, including a trailing newline.
        let text = if isNull event.Text then "" else event.Text
        let lines = text.Replace("\r", "").Split('\n')
        let count = if lines.Length > 0 && lines[lines.Length - 1] = "" then lines.Length - 1 else lines.Length
        for i = 0 to count - 1 do
            output.Add { Sequence = event.Sequence; Stream = event.Stream; Text = safeLine lines[i] }
    let excess = max 0 (output.Count - max 1 maxLines)
    if excess > 0 then output.RemoveRange(0, excess)
    output |> Seq.toList
