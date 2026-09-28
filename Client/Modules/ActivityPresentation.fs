module Client.ActivityPresentation

open System
open System.Text.Json
open Client
open Client.Effect
open Common

let private tryProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value
    else None

let private tryString (name: string) (element: JsonElement) =
    tryProperty name element
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.String then Option.ofObj (value.GetString())
        else None)

let private tryInt (name: string) (element: JsonElement) =
    tryProperty name element
    |> Option.bind (fun value ->
        match value.TryGetInt32() with
        | true, result -> Some result
        | _ -> None)

let private tryBool (name: string) (element: JsonElement) =
    tryProperty name element
    |> Option.bind (fun value ->
        match value.ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | _ -> None)

let private plural (count: int) (singular: string) (plural: string) =
    if count = 1 then $"{count} {singular}" else $"{count} {plural}"

let private shortId (value: string) =
    if String.IsNullOrWhiteSpace value then value
    elif value.Length <= 8 then value
    else value.Substring(0, 8)

let tryResultSummary (command: AgentCommand) (payload: string) =
    try
        use document = JsonDocument.Parse(payload)
        let root = document.RootElement

        match command with
        | RunCommandCommand _
        | RunProjectTaskCommand _ ->
            match tryInt "ExitCode" root, tryBool "TimedOut" root with
            | Some exitCode, Some true -> Some $"timeout, exit {exitCode}"
            | Some exitCode, _ -> Some $"exit {exitCode}"
            | _ -> None
        | GitCommitCommand _ ->
            tryString "CommitHash" root
            |> Option.map (shortId >> fun hash -> $"commit {hash}")
        | SearchFilesCommand _
        | SearchTextCommand _ when root.ValueKind = JsonValueKind.Array ->
            root.GetArrayLength() |> fun count -> plural count "match" "matches" |> Some
        | ListDirectoryCommand _ when root.ValueKind = JsonValueKind.Array ->
            root.GetArrayLength() |> fun count -> plural count "item" "items" |> Some
        | ListProjectsCommand when root.ValueKind = JsonValueKind.Array ->
            root.GetArrayLength() |> fun count -> plural count "project" "projects" |> Some
        | ReadFilesCommand _ when root.ValueKind = JsonValueKind.Array ->
            root.GetArrayLength() |> fun count -> plural count "file" "files" |> Some
        | PatchFileCommand _ ->
            match tryInt "HunksApplied" root, tryInt "ChangedLines" root with
            | Some hunks, Some lines ->
                let hunkSummary = plural hunks "hunk" "hunks"
                let lineSummary = plural lines "line" "lines"
                Some $"{hunkSummary}, {lineSummary}"
            | _ -> None
        | StartJobCommand _ ->
            tryString "JobId" root
            |> Option.map (shortId >> fun id -> $"job {id}")
        | ListJobsCommand _ ->
            tryProperty "Jobs" root
            |> Option.bind (fun jobs ->
                if jobs.ValueKind = JsonValueKind.Array then
                    jobs.GetArrayLength() |> fun count -> plural count "job" "jobs" |> Some
                else None)
        | _ -> None
    with _ ->
        None

let private compact maxLength (value: string) =
    let value =
        if isNull value then ""
        else value.Replace("\r", " ").Replace("\n", " ").Trim()

    if value.Length <= maxLength then value
    else value.Substring(0, maxLength - 1) + "…"

let shortError (error: EffectError) =
    let kind, message =
        match error with
        | ExceptionError ex -> "Exception", ex.Message
        | GenericError message -> "ExecutionFailed", message
        | ValidationError message -> "ValidationFailed", message
        | ContextError message -> "ExecutionFailed", message
        | NotFoundError resource -> "NotFound", resource
        | Client.PermissionDenied message -> "PermissionDenied", message
        | Client.ConfirmationRequired request -> "ConfirmationRequired", request.Summary
        | AggregatedErrors errors -> "MultipleErrors", $"{errors.Length} errors"

    let message = compact 72 message
    if String.IsNullOrWhiteSpace message then kind else $"{kind}: {message}"

let fullError (error: EffectError) =
    match error with
    | ExceptionError ex -> ex.ToString()
    | _ -> EffectError.toString error
