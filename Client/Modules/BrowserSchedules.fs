module Client.BrowserSchedules

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

// A client-local browser action queue. A claimed occurrence is never replayed after restart.
[<CLIMutable>]
type ScheduledAction =
    { Id: string
      DueAtUtc: DateTimeOffset
      Url: string
      ProjectName: string
      ScriptPath: string
      Alias: string
      ArgsJson: string
      Active: bool
      WindowRegistration: string
      CloseTabAfterExecution: bool
      RepeatEverySeconds: int
      Status: string
      RegistrationId: string
      ConversationUrl: string
      Cleanup: string
      AttemptId: string
      Error: string
      UpdatedAtUtc: DateTimeOffset }

type Service(path: string, execute: ScheduledAction -> Task<Result<string,string * string>>, ?now: unit -> DateTimeOffset, ?pollMs: int) =
    let gate = obj()
    let cancellation = new CancellationTokenSource()
    let clock = defaultArg now (fun () -> DateTimeOffset.UtcNow)
    let interval = defaultArg pollMs 1000
    let mutable actions: ScheduledAction list = []
    let mutable started = false
    let mutable worker: Task = Task.CompletedTask

    let persist items =
        let folder = Path.GetDirectoryName(Path.GetFullPath(path))
        Directory.CreateDirectory(folder) |> ignore
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            use file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(temp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            let bytes = JsonSerializer.SerializeToUtf8Bytes({| schemaVersion = 2; actions = items |})
            file.Write(bytes)
            file.Flush(true)
            file.Close()
            File.Move(temp, path, true)
        finally
            if File.Exists(temp) then File.Delete(temp)

    let setActions items =
        let active = items |> List.filter (fun item -> item.Status = "pending" || item.Status = "running")
        let history =
            items
            |> List.filter (fun item -> item.Status <> "pending" && item.Status <> "running")
            |> List.sortByDescending (fun item -> item.UpdatedAtUtc)
            |> List.truncate 100
        let bounded = active @ history
        persist bounded
        actions <- bounded

    let update id status (rawResult: string) error =
        // Accept legacy registration-only results and structured browser workflow results.
        let registrationId, conversationUrl, cleanup, effectiveStatus =
            try
                use doc = JsonDocument.Parse(rawResult)
                let root = doc.RootElement
                let prop (name: string) =
                    match root.TryGetProperty(name) with
                    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                    | _ -> ""
                let state = prop "status"
                let url =
                    match root.TryGetProperty("scriptResult") with
                    | true, payload when payload.ValueKind = JsonValueKind.Object ->
                        match payload.TryGetProperty("conversationUrl") with
                        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                        | _ -> ""
                    | _ -> ""
                let safeUrl =
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, uri when uri.Scheme = "https" || uri.Scheme = "http" ->
                        uri.GetLeftPart(UriPartial.Path)
                    | _ -> ""
                prop "registrationId", safeUrl, prop "cleanup",
                (if state = "notSubmitted" then "failed" else status)
            with _ -> rawResult, "", "", status
        lock gate (fun () ->
            match actions |> List.tryFind (fun a -> a.Id = id && a.Status = "running") with
            | None -> ()
            | Some _ ->
                setActions (actions |> List.map (fun a ->
                    if a.Id = id && a.Status = "running" then
                        if effectiveStatus = "suspended" then
                            // A provider UI contract failure needs inspection, not another
                            // scheduled attempt against the same broken selector.
                            { a with Status = "suspended"; RegistrationId = registrationId
                                     Error = error; UpdatedAtUtc = clock() }
                        elif a.RepeatEverySeconds > 0 then
                            let current = clock()
                            let seconds = float a.RepeatEverySeconds
                            let elapsed = max 0.0 (current - a.DueAtUtc).TotalSeconds
                            let skipped = floor(elapsed / seconds) + 1.0
                            { a with Status = "pending"; DueAtUtc = a.DueAtUtc.AddSeconds(skipped * seconds)
                                     RegistrationId = registrationId; ConversationUrl = conversationUrl
                                     Cleanup = cleanup; Error = effectiveStatus + ": " + error
                                     UpdatedAtUtc = current }
                        else
                            { a with Status = effectiveStatus; RegistrationId = registrationId
                                     ConversationUrl = conversationUrl; Cleanup = cleanup
                                     Error = error; UpdatedAtUtc = clock() }
                    else a)))

    do
        if File.Exists(path) then
            if File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
                invalidOp "Browser schedules must not use a symbolic-link state file."
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                let mode = File.GetUnixFileMode(path)
                if (mode &&& (UnixFileMode.GroupRead ||| UnixFileMode.OtherRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)) <> enum 0 then
                    invalidOp "Browser schedules state file permissions are too broad."
            use data = JsonDocument.Parse(File.ReadAllText(path))
            let root = data.RootElement
            if root.GetProperty("schemaVersion").GetInt32() <> 1 &&
               root.GetProperty("schemaVersion").GetInt32() <> 2 then
                invalidOp "Unsupported browser schedules state schema."
            let restored =
                JsonSerializer.Deserialize<ScheduledAction list>(root.GetProperty("actions").GetRawText())
            actions <-
                restored
                |> List.map (fun action ->
                    if action.Status = "running" then
                        if action.RepeatEverySeconds > 0 then
                            let now = clock()
                            let seconds = float action.RepeatEverySeconds
                            let elapsed = max 0.0 (now - action.DueAtUtc).TotalSeconds
                            let skipped = floor(elapsed / seconds) + 1.0
                            { action with Status = "pending"
                                          DueAtUtc = action.DueAtUtc.AddSeconds(skipped * seconds)
                                          Error = "outcomeUnknown: previous occurrence interrupted; never replayed."
                                          UpdatedAtUtc = now }
                        else
                            { action with Status = "outcomeUnknown"
                                          Error = "JarvisClient stopped after claiming this one-shot action; it was not replayed."
                                          UpdatedAtUtc = clock() }
                    elif action.Status = "pending" && action.RepeatEverySeconds > 0 &&
                         action.DueAtUtc < clock() then
                        let now = clock()
                        let seconds = float action.RepeatEverySeconds
                        let elapsed = (now - action.DueAtUtc).TotalSeconds
                        { action with DueAtUtc = action.DueAtUtc.AddSeconds((floor(elapsed / seconds) + 1.0) * seconds)
                                      UpdatedAtUtc = now }
                    elif action.Status = "pending" && String.IsNullOrWhiteSpace(action.WindowRegistration) then
                        { action with Status = "failed"
                                      Error = "WindowRegistrationRequired: reconfigure old browser schedule."
                                      UpdatedAtUtc = clock() }
                    else action)
            if restored <> actions then persist actions

    member _.Schedule(id: string, seconds: int, url: string, project: string, script: string, alias: string, args: string, active: bool, ?windowRegistration: string, ?closeAfter: bool, ?repeatEverySeconds: int) =
        let window = defaultArg windowRegistration ""
        let closeAfter = defaultArg closeAfter false
        let repeat = defaultArg repeatEverySeconds 0
        if String.IsNullOrWhiteSpace(id) || id.Length > 64 ||
           (id |> Seq.exists (fun c -> not (Char.IsLetterOrDigit(c) || c = '-' || c = '_'))) then
            Error "InvalidScheduleId: use 1-64 letters, numbers, - or _."
        elif String.IsNullOrWhiteSpace(window) || window.Length > 128 then
            Error "WindowRegistrationRequired"
        elif repeat <> 0 && (repeat < 60 || repeat > 30 * 24 * 60 * 60) then
            Error "InvalidRepeatInterval: must be 60 seconds to 30 days."
        elif seconds < 5 || seconds > 30 * 24 * 60 * 60 then
            Error "InvalidDelay: must be between 5 seconds and 30 days."
        elif String.IsNullOrWhiteSpace(project) || String.IsNullOrWhiteSpace(script) then
            Error "InvalidScriptPath"
        elif isNull alias || alias.Length > 128 || (alias |> Seq.exists Char.IsControl) then
            Error "InvalidTabAlias"
        elif String.IsNullOrWhiteSpace(args) then
            Error "InvalidArgumentsJson"
        elif Encoding.UTF8.GetByteCount(args) > 65536 then
            Error "InputTooLarge: arguments exceed 64 KiB."
        else
            try
                use parsed = JsonDocument.Parse(args)
                let validUrl =
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, uri ->
                        (uri.Scheme = "http" || uri.Scheme = "https") &&
                        String.IsNullOrEmpty(uri.UserInfo) && url.Length <= 2048
                    | _ -> false
                if not validUrl then Error "InvalidTabUrl"
                else
                    lock gate (fun () ->
                        if actions |> List.exists (fun x -> x.Id = id && x.Status = "running") then
                            Error "ScheduleRunning: cannot replace an executing action."
                        elif actions |> List.filter (fun x -> x.Status = "pending" && x.Id <> id) |> List.length >= 100 then
                            Error "ScheduleLimitReached"
                        else
                            let time = clock()
                            let entry =
                                { Id = id; DueAtUtc = time.AddSeconds(float seconds); Url = url
                                  ProjectName = project; ScriptPath = script; Alias = alias
                                  ArgsJson = args; Active = active; WindowRegistration = window
                                  CloseTabAfterExecution = closeAfter; RepeatEverySeconds = repeat
                                  Status = "pending"
                                  RegistrationId = ""; ConversationUrl = ""; Cleanup = ""
                                  AttemptId = ""; Error = ""; UpdatedAtUtc = time }
                            setActions (entry :: (actions |> List.filter (fun x -> x.Id <> id)))
                            Ok entry.DueAtUtc)
            with :? JsonException -> Error "InvalidArgumentsJson"

    member _.Cancel(id: string) =
        lock gate (fun () ->
            match actions |> List.tryFind (fun x -> x.Id = id) with
            | None -> Error "ScheduleNotFound"
            | Some a when a.Status <> "pending" -> Error ("ScheduleNotPending:" + a.Status)
            | Some _ ->
                setActions (actions |> List.map (fun a ->
                    if a.Id = id then { a with Status = "canceled"; UpdatedAtUtc = clock() }
                    else a))
                Ok())

    member _.List() =
        lock gate (fun () ->
            actions
            |> List.sortBy (fun a -> a.DueAtUtc)
            |> List.map (fun a ->
                {| id = a.Id
                   dueAtUtc = a.DueAtUtc
                   status = a.Status
                   projectName = a.ProjectName
                   scriptPath = a.ScriptPath
                   origin = (Uri(a.Url)).GetLeftPart(UriPartial.Authority)
                   alias = a.Alias
                   windowRegistration = a.WindowRegistration
                   repeatEverySeconds = a.RepeatEverySeconds
                   closeTabAfterExecution = a.CloseTabAfterExecution
                   registrationId = a.RegistrationId
                   conversationUrl = a.ConversationUrl
                   cleanup = a.Cleanup
                   attemptId = a.AttemptId
                   error = a.Error
                   updatedAtUtc = a.UpdatedAtUtc |})
            |> JsonSerializer.Serialize)

    member _.Start() =
        lock gate (fun () ->
            if started then invalidOp "Browser scheduler already started."
            if interval < 10 then invalidArg "pollMs" "Polling interval is too small."
            started <- true
            worker <- task {
                try
                    while not cancellation.IsCancellationRequested do
                        let next =
                            lock gate (fun () ->
                                let due =
                                    actions
                                    |> List.filter (fun a -> a.Status = "pending" && a.DueAtUtc <= clock())
                                    |> List.sortBy (fun a -> a.DueAtUtc)
                                    |> List.tryHead
                                match due with
                                | None -> None
                                | Some entry ->
                                    setActions (actions |> List.map (fun a ->
                                        if a.Id = entry.Id then
                                            { a with Status = "running"; AttemptId = Guid.NewGuid().ToString("N"); UpdatedAtUtc = clock() }
                                        else a))
                                    Some entry)
                        match next with
                        | None -> do! Task.Delay(interval, cancellation.Token)
                        | Some entry ->
                            try
                                let! result = execute entry
                                match result with
                                | Ok registrationId -> update entry.Id "completed" registrationId ""
                                | Error (registrationId, reason) ->
                                    let status =
                                        if reason.StartsWith("UiIncompatible:", StringComparison.Ordinal) then "suspended"
                                        elif reason.Contains("OutcomeUnknown", StringComparison.OrdinalIgnoreCase) then "outcomeUnknown"
                                        else "failed"
                                    update entry.Id status registrationId
                                        (if reason.Length > 250 then reason.Substring(0, 250) else reason)
                            with ex ->
                                update entry.Id "outcomeUnknown" "" ("Unexpected error: " + ex.GetType().Name)
                with :? OperationCanceledException -> ()
            }
            ())

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            // A running action stays marked as such until its callback completes.
            // If the client process terminates, startup marks it outcomeUnknown.
            cancellation.Dispose()

let defaultPath () =
    Path.Combine(Path.GetDirectoryName(Client.ClientConfig.configPath()), "browser-schedules.json")
