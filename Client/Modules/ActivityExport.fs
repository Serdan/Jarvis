module Client.ActivityExport

open System
open System.Text

type Entry =
    { Timestamp: string
      ProjectName: string option
      CommandName: string
      Reason: string option
      Detail: string option
      Status: string
      DurationMs: int64 option
      Result: string option }

let private appendOptional (builder: StringBuilder) (label: string) (value: string option) =
    value
    |> Option.iter (fun text ->
        builder.Append("  ").Append(label).Append(": ").AppendLine(text) |> ignore)

let formatRecent (entries: Entry list) =
    let builder = StringBuilder()
    builder.Append("Jarvis recent commands (")
        .Append(entries.Length)
        .AppendLine(", oldest to newest)")
    |> ignore

    entries
    |> List.iteri (fun index entry ->
        if index > 0 then
            builder.AppendLine() |> ignore

        builder.Append("[")
            .Append(entry.Timestamp)
            .Append("] ")
        |> ignore

        entry.ProjectName
        |> Option.iter (fun projectName ->
            builder.Append("@").Append(projectName).Append(" ") |> ignore)

        builder.AppendLine(entry.CommandName) |> ignore
        appendOptional builder "Reason" entry.Reason
        appendOptional builder "Detail" entry.Detail

        builder.Append("  Status: ").Append(entry.Status) |> ignore

        entry.DurationMs
        |> Option.iter (fun durationMs ->
            builder.Append(" | ").Append(durationMs).Append(" ms") |> ignore)

        entry.Result
        |> Option.iter (fun result ->
            builder.Append(" | ").Append(result) |> ignore)

        builder.AppendLine() |> ignore)

    builder.ToString().TrimEnd()
