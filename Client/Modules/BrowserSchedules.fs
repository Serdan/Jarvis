module Client.BrowserSchedules

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Common

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
      RevisionId: string
      Error: string
      UpdatedAtUtc: DateTimeOffset
      ActionKind: string
      TriggerKind: string
      Executable: string
      CommandArgs: string list
      WorkingDirectory: string
      TimeoutSeconds: int
      MaxOutputBytes: int
      IdleAgentId: string
      IdleSeconds: int
      LastActivityAtUtc: DateTimeOffset
      StartedAtUtc: DateTimeOffset
      Disabled: bool
      LastResult: string }

// The opt-in is host metadata, not a browser script argument. Other scripts
// and legacy browser schedules retain their ordinary execution semantics.
let isReadyResume (action: ScheduledAction) =
    if action.ActionKind <> "BrowserScript" ||
       action.ScriptPath <> "browser-scripts/chatgpt/launchAgent.js" then false
    else
        try
            use doc = JsonDocument.Parse(action.ArgsJson)
            match doc.RootElement.TryGetProperty("resumeReady") with
            | true, marker -> marker.ValueKind = JsonValueKind.True
            | _ -> false
        with _ -> false

let private isUncertainResume (action: ScheduledAction) =
    isReadyResume action && action.Status = "suspended" &&
    not (String.IsNullOrEmpty action.Error) &&
    action.Error.StartsWith("ResumePublicationUncertain:", StringComparison.Ordinal)

// The occurrence's Error field carries private durable stage evidence while
// running/suspended. No public schedule record shape or other caller changes.
// Legacy running records without the explicit PreSelection marker are ambiguous.
[<CLIMutable>]
type ResumeSelection =
    { Stage: string
      BindingSha256: string
      OperationId: Guid
      Actor: string
      RequestJson: string }

let private preSelection = "ResumeStage:PreSelection"
let private stagePrefix = "ResumeStage:"
let private uncertainPrefix = "ResumePublicationUncertain:"

let tryResumeSelection (action: ScheduledAction) =
    try
        let raw = action.Error
        let prefix =
            if raw.StartsWith(stagePrefix, StringComparison.Ordinal) then stagePrefix
            elif raw.StartsWith(uncertainPrefix, StringComparison.Ordinal) then uncertainPrefix
            else ""
        if prefix = "" then None
        else
            let selection = JsonSerializer.Deserialize<ResumeSelection>(raw.Substring(prefix.Length))
            if isNull (box selection) || selection.OperationId = Guid.Empty ||
               String.IsNullOrWhiteSpace(selection.Actor) || String.IsNullOrWhiteSpace(selection.RequestJson) ||
               String.IsNullOrWhiteSpace(selection.BindingSha256) ||
               not (List.contains selection.Stage ["Selected"; "Published"; "EnteringSend"]) then None
            else Some selection
    with _ -> None

let private suspendResume action =
    match tryResumeSelection action with
    | Some selection -> uncertainPrefix + JsonSerializer.Serialize(selection)
    | None -> uncertainPrefix + " interrupted ambiguous resume; reconcile original intent."

let private nextOccurrence (now: DateTimeOffset) (action: ScheduledAction) =
    let seconds = float action.RepeatEverySeconds
    let elapsed = max 0.0 (now - action.DueAtUtc).TotalSeconds
    action.DueAtUtc.AddSeconds((floor(elapsed / seconds) + 1.0) * seconds)

let private finishInterrupted now action error =
    { action with Status = (if action.Disabled then "canceled" else "pending")
                  DueAtUtc = nextOccurrence now action; Error = error; UpdatedAtUtc = now }

let private sameResumeProject (left: ScheduledAction) (right: ScheduledAction) =
    isReadyResume left && isReadyResume right &&
    String.Equals(left.ProjectName, right.ProjectName, StringComparison.OrdinalIgnoreCase)

// A claim must survive sudden power loss before any external side effect.
// On Unix, the containing directory must be synced after the atomic rename.
// On Windows, use MOVEFILE_WRITE_THROUGH with replace semantics.
module private DurableRename =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int OpenDirectory(string path, int flags)

    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int Sync(int fd)

    [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int Close(int fd)

    [<DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool MoveFileEx(string source, string destination, uint32 flags)

module private DurableScheduleFile =
    let private syncParentDirectory folder =
        let flags = if OperatingSystem.IsMacOS() then 0x100000 else 0x10000
        let fd = DurableRename.OpenDirectory(folder, flags)
        if fd < 0 then
            raise (IOException(sprintf "Cannot open schedule directory for fsync (errno=%d)." (Marshal.GetLastPInvokeError())))
        try
            if DurableRename.Sync(fd) <> 0 then
                raise (IOException(sprintf "Schedule claim directory fsync failed (errno=%d)." (Marshal.GetLastPInvokeError())))
        finally
            DurableRename.Close(fd) |> ignore

    let replace temp path =
        if OperatingSystem.IsWindows() then
            // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
            if not (DurableRename.MoveFileEx(temp, path, 0x9u)) then
                raise (IOException(sprintf "Durable schedule replace failed (win32=%d)." (Marshal.GetLastPInvokeError())))
        else
            File.Move(temp, path, true)
            syncParentDirectory (Path.GetDirectoryName(Path.GetFullPath(path)))

let private describe (a: ScheduledAction) =
    let origin =
        match Uri.TryCreate(a.Url, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" || uri.Scheme = "http" ->
            uri.GetLeftPart(UriPartial.Authority)
        | _ -> ""
    {| id = a.Id
       dueAtUtc = a.DueAtUtc
       status = a.Status
       projectName = a.ProjectName
       actionKind = (if String.IsNullOrWhiteSpace a.ActionKind then "BrowserScript" else a.ActionKind)
       triggerKind = (if String.IsNullOrWhiteSpace a.TriggerKind then
                          (if a.RepeatEverySeconds > 0 then "Every" else "Once") else a.TriggerKind)
       executable = a.Executable
       commandArgumentCount = (if isNull (box a.CommandArgs) then 0 else a.CommandArgs.Length)
       workingDirectory = a.WorkingDirectory
       timeoutSeconds = a.TimeoutSeconds
       maxOutputBytes = a.MaxOutputBytes
       scriptPath = a.ScriptPath
       origin = origin
       alias = a.Alias
       windowRegistration = a.WindowRegistration
       repeatEverySeconds = a.RepeatEverySeconds
       idleAgentId = a.IdleAgentId
       idleSeconds = a.IdleSeconds
       lastActivityAtUtc = a.LastActivityAtUtc
       startedAtUtc = a.StartedAtUtc
       disabled = a.Disabled
       closeTabAfterExecution = a.CloseTabAfterExecution
       registrationId = a.RegistrationId
       conversationUrl = a.ConversationUrl
       cleanup = a.Cleanup
       attemptId = a.AttemptId
       lastResult = a.LastResult
       error = a.Error
       updatedAtUtc = a.UpdatedAtUtc |}

type Service(path: string, execute: ScheduledAction -> Task<Result<string,string * string>>, ?now: unit -> DateTimeOffset, ?pollMs: int, ?agentLastSeen: string -> string -> DateTimeOffset option, ?verifyIdleActivity: string -> string -> Task<Result<DateTimeOffset option,string>>, ?onFault: string -> unit, ?reconcileResume: ScheduledAction -> ResumeSelection -> Task<bool>) =
    let gate = obj()
    // A process-wide exclusive lifetime lease, acquired BEFORE loading state.
    // A second JarvisClient cannot read stale pending and race a claim. The
    // kernel releases this lease when its owner crashes.
    let ownership =
        let absolute = Path.GetFullPath(path)
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)) |> ignore
        let lockPath = absolute + ".owner.lock"
        if File.Exists(lockPath) && File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint) then
            invalidOp "Execution scheduler ownership path must not be a symbolic link."
        try
            let stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            stream
        with :? IOException as error ->
            raise (IOException("Execution scheduler already owned by another JarvisClient process (exclusive schedule store lock).", error))
    let cancellation = new CancellationTokenSource()
    let clock = defaultArg now (fun () -> DateTimeOffset.UtcNow)
    let interval = defaultArg pollMs 1000
    let seen = defaultArg agentLastSeen (fun _ _ -> None)
    // An unverified local hint is never sufficient to launch a Manager.
    // Production rejects idle until independent client/Git evidence exists.
    let verify = defaultArg verifyIdleActivity (fun _ _ ->
        Task.FromResult(Error "ActivityVerificationUnavailable: authenticated server snapshot is required."))
    let reportFault = defaultArg onFault ignore
    let reconcile = defaultArg reconcileResume (fun _ _ -> Task.FromResult false)
    let nextResumeCheck = Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
    let isIdle (a: ScheduledAction) = a.TriggerKind = "AfterAgentIdle"
    let mutable actions: ScheduledAction list = []
    let mutable started = false
    let mutable disposed = false
    let mutable worker: Task = Task.CompletedTask
    // Idle waiting is long-lived; reconcile missed hints periodically rather
    // than issuing a fresh server read every scheduling tick forever.
    let nextWaitingCheck = Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
    let waitingPoll = TimeSpan.FromMilliseconds(float (max 150 (interval * 15)))

    let persist items =
        let folder = Path.GetDirectoryName(Path.GetFullPath(path))
        Directory.CreateDirectory(folder) |> ignore
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            use file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(temp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            let bytes = JsonSerializer.SerializeToUtf8Bytes({| schemaVersion = 3; actions = items |})
            file.Write(bytes)
            file.Flush(true)
            file.Close()
            DurableScheduleFile.replace temp path
        finally
            if File.Exists(temp) then File.Delete(temp)

    let setActions items =
        let active = items |> List.filter (fun item -> item.Status = "pending" || item.Status = "running" || item.Status = "waitingActivity" || isUncertainResume item)
        let history =
            items
            |> List.filter (fun item -> item.Status <> "pending" && item.Status <> "running" && item.Status <> "waitingActivity" && not (isUncertainResume item))
            |> List.sortByDescending (fun item -> item.UpdatedAtUtc)
            |> List.truncate 100
        let bounded = active @ history
        persist bounded
        actions <- bounded

    let update id attemptId status (rawResult: string) (error: string) =
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
            match actions |> List.tryFind (fun a ->
                not disposed && a.Id = id && a.AttemptId = attemptId && a.Status = "running") with
            | None -> ()
            | Some _ ->
                setActions (actions |> List.map (fun a ->
                    if a.Id = id && a.AttemptId = attemptId && a.Status = "running" then
                        let now = clock()
                        let resultSummary =
                            if a.ActionKind = "RunCommand" then
                                if String.IsNullOrWhiteSpace(rawResult) then ""
                                elif rawResult.Length > 200 then rawResult.Substring(0, 200)
                                else rawResult
                            else ""
                        if isReadyResume a && error.StartsWith(uncertainPrefix, StringComparison.Ordinal) then
                            // Keep the exact original identity even if cancellation
                            // raced the active occurrence; never replace it with a
                            // truncated generic exception or an absent receipt.
                            if a.Error = preSelection then
                                finishInterrupted now a "Interrupted before selection; original occurrence retired."
                            else
                                { a with Status = "suspended"; RegistrationId = registrationId
                                         Error = suspendResume a; LastResult = resultSummary; UpdatedAtUtc = now }
                        elif a.Disabled then
                            { a with Status = "canceled"; Error = error; LastResult = resultSummary; UpdatedAtUtc = now }
                        elif effectiveStatus = "suspended" then
                            // A provider UI contract failure needs inspection, not another
                            // scheduled attempt against the same broken selector.
                            { a with Status = "suspended"; RegistrationId = registrationId
                                     Error = error; LastResult = resultSummary; UpdatedAtUtc = now }
                        elif isIdle a then
                            // A Manager call during execution re-arms only AFTER the launch finishes.
                            match seen a.ProjectName a.IdleAgentId with
                            | Some latest when latest > a.LastActivityAtUtc ->
                                { a with Status = "pending"; LastActivityAtUtc = latest
                                         DueAtUtc = latest.AddSeconds(float a.IdleSeconds)
                                         Error = error; LastResult = resultSummary; UpdatedAtUtc = now }
                            | _ ->
                                { a with Status = "waitingActivity"; Error = error
                                         LastResult = resultSummary; UpdatedAtUtc = now }
                        elif a.RepeatEverySeconds > 0 then
                            let current = clock()
                            let seconds = float a.RepeatEverySeconds
                            let elapsed = max 0.0 (current - a.DueAtUtc).TotalSeconds
                            let skipped = floor(elapsed / seconds) + 1.0
                            { a with Status = "pending"; DueAtUtc = a.DueAtUtc.AddSeconds(skipped * seconds)
                                     RegistrationId = registrationId; ConversationUrl = conversationUrl
                                     Cleanup = cleanup; Error = effectiveStatus + ": " + error
                                     LastResult = resultSummary; UpdatedAtUtc = current }
                        else
                            { a with Status = effectiveStatus; RegistrationId = registrationId
                                     ConversationUrl = conversationUrl; Cleanup = cleanup
                                     Error = error; LastResult = resultSummary; UpdatedAtUtc = clock() }
                    else a)))

    do
        try
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
                   root.GetProperty("schemaVersion").GetInt32() <> 2 &&
                   root.GetProperty("schemaVersion").GetInt32() <> 3 then
                    invalidOp "Unsupported browser schedules state schema."
                let restored =
                    JsonSerializer.Deserialize<ScheduledAction list>(root.GetProperty("actions").GetRawText())
                // Legacy entries omit CommandArgs. A null F# list can throw
                // during structural equality before scheduling even begins.
                let needsMigration =
                    restored |> List.exists (fun entry -> isNull (box entry.CommandArgs))
                let normalized =
                    restored |> List.map (fun entry ->
                        if isNull (box entry.CommandArgs) then { entry with CommandArgs = [] }
                        else entry)
                actions <-
                    normalized
                    |> List.map (fun loaded ->
                        // Assign an immutable identity to pre-revision journal entries.
                        // It changes only when an operator replaces a schedule.
                        let action =
                            if String.IsNullOrWhiteSpace(loaded.RevisionId) then
                                { loaded with RevisionId = Guid.NewGuid().ToString("N") }
                            else loaded
                        // An inactive schedule cannot authorize its own reactivation.
                        // Historical failure details are evidence, not permission.
                        if action.Status = "running" then
                            if isReadyResume action then
                                if action.Error = preSelection && action.RepeatEverySeconds > 0 then
                                    finishInterrupted (clock()) action "Interrupted before selection; original occurrence retired."
                                else
                                    { action with Status = "suspended"; Error = suspendResume action; UpdatedAtUtc = clock() }
                            elif isIdle action then
                                let now = clock()
                                let stale = action.StartedAtUtc.AddMinutes(30.)
                                { action with Status = "pending"
                                              DueAtUtc = max now stale
                                              Error = "outcomeUnknown: prior Manager launch interrupted; next attempt only after stale cutoff."
                                              UpdatedAtUtc = now }
                            elif action.RepeatEverySeconds > 0 then
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
                        elif action.Status = "pending" &&
                             (String.IsNullOrWhiteSpace(action.ActionKind) || action.ActionKind = "BrowserScript") &&
                             String.IsNullOrWhiteSpace(action.WindowRegistration) then
                            { action with Status = "failed"
                                          Error = "WindowRegistrationRequired: reconfigure old browser schedule."
                                          UpdatedAtUtc = clock() }
                        else action)
                if needsMigration || normalized <> actions then persist actions

        with _ ->
            ownership.Dispose()
            reraise()

    member _.Schedule(id: string, seconds: int, url: string, project: string, script: string, alias: string, args: string, active: bool, ?windowRegistration: string, ?closeAfter: bool, ?repeatEverySeconds: int) =
        let window = defaultArg windowRegistration ""
        let closeAfter = defaultArg closeAfter true
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
                        elif actions |> List.exists (fun x -> x.Id = id && isUncertainResume x) then
                            Error "ResumePublicationUncertain: reconcile the original intent before replacing this schedule."
                        elif (actions |> List.filter (fun x ->
                                (x.Status = "pending" || x.Status = "waitingActivity") && x.Id <> id)
                                |> List.length) >= 100 then
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
                                  AttemptId = ""; RevisionId = Guid.NewGuid().ToString("N")
                                  Error = ""; UpdatedAtUtc = time
                                  ActionKind = "BrowserScript"; TriggerKind = (if repeat > 0 then "Every" else "Once")
                                  Executable = ""; CommandArgs = []; WorkingDirectory = ""
                                  TimeoutSeconds = 0; MaxOutputBytes = 0
                                  IdleAgentId = ""; IdleSeconds = 0
                                  LastActivityAtUtc = DateTimeOffset.MinValue
                                  StartedAtUtc = DateTimeOffset.MinValue
                                  Disabled = false; LastResult = "" }
                            setActions (entry :: (actions |> List.filter (fun x -> x.Id <> id)))
                            Ok entry.DueAtUtc)
            with :? JsonException -> Error "InvalidArgumentsJson"

    member _.ScheduleExecution(cmd: ScheduleExecutionCommand) =
        let maxSeconds = 30 * 24 * 60 * 60
        let validId (id: string) =
            not (String.IsNullOrWhiteSpace id) && id.Length <= 64 &&
            (id |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_'))
        let required (text: string option) = text |> Option.defaultValue ""
        let time = clock()
        let initialSeen =
            if cmd.TriggerKind = "AfterAgentIdle" then
                seen cmd.ProjectName (required cmd.IdleAgentId)
            else None
        let due =
            match cmd.TriggerKind with
            | "Once" ->
                match cmd.RunAtUtc, cmd.RunAfterSeconds with
                | Some at, None when at > time && at <= time.AddDays(30.) -> Ok at
                | None, Some delay when delay >= 0 && delay <= maxSeconds -> Ok(time.AddSeconds(float delay))
                | _ -> Error "InvalidOnceTrigger: supply a future UTC time or a delay of at most 30 days."
            | "Every" ->
                match cmd.RepeatEverySeconds, cmd.RunAtUtc, cmd.RunAfterSeconds with
                | Some repeat, None, delay when repeat >= 60 && repeat <= maxSeconds &&
                                                (delay |> Option.defaultValue 0) >= 0 &&
                                                (delay |> Option.defaultValue 0) <= maxSeconds ->
                    Ok(time.AddSeconds(float (delay |> Option.defaultValue repeat)))
                | _ -> Error "InvalidEveryTrigger: interval must be 60 seconds to 30 days."
            | "AfterAgentIdle" ->
                match cmd.IdleAgentId, cmd.IdleSeconds with
                | Some agent, Some idle when not (String.IsNullOrWhiteSpace agent) &&
                                             idle >= 60 && idle <= maxSeconds &&
                                             cmd.RunAtUtc.IsNone && cmd.RunAfterSeconds.IsNone ->
                    Ok((initialSeen |> Option.defaultValue time).AddSeconds(float idle))
                | _ -> Error "InvalidIdleTrigger: supply an agent and an idle interval of 60 seconds to 30 days."
            | _ -> Error "UnknownTriggerKind: expected Once, Every, or AfterAgentIdle."
        let actionError =
            match cmd.ActionKind with
            | "RunCommand" ->
                if String.IsNullOrWhiteSpace(required cmd.Executable) then Some "ExecutableRequired"
                elif isNull (box cmd.Args) || cmd.Args.Length > 128 ||
                     (cmd.Args |> List.exists (fun a -> isNull a || a.Length > 8192)) then
                    Some "InvalidCommandArguments"
                elif cmd.TimeoutSeconds |> Option.exists (fun x -> x < 1 || x > 3600) then
                    Some "InvalidTimeoutSeconds"
                elif cmd.MaxOutputBytes |> Option.exists (fun x -> x < 1 || x > 1048576) then
                    Some "InvalidMaxOutputBytes"
                else None
            | "BrowserScript" ->
                let url = required cmd.Url
                let validUrl =
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, uri -> (uri.Scheme = "http" || uri.Scheme = "https") &&
                                   String.IsNullOrEmpty uri.UserInfo && url.Length <= 2048
                    | _ -> false
                if String.IsNullOrWhiteSpace(required cmd.ScriptPath) then Some "InvalidScriptPath"
                elif String.IsNullOrWhiteSpace(required cmd.WindowRegistration) then Some "WindowRegistrationRequired"
                elif not validUrl then Some "InvalidTabUrl"
                elif Encoding.UTF8.GetByteCount(required cmd.ArgsJson) > 65536 then Some "InputTooLarge: arguments exceed 64 KiB."
                else
                    try
                        use json = JsonDocument.Parse(cmd.ArgsJson |> Option.defaultValue "{}")
                        if json.RootElement.ValueKind <> JsonValueKind.Object then Some "InvalidArgumentsJson"
                        else None
                    with :? JsonException -> Some "InvalidArgumentsJson"
            | _ -> Some "UnknownActionKind: expected RunCommand or BrowserScript."
        if not (validId cmd.ScheduleId) then Error "InvalidScheduleId: use 1-64 letters, numbers, - or _."
        elif String.IsNullOrWhiteSpace cmd.ProjectName then Error "ProjectNameRequired"
        else
            match due, actionError with
            | Error error, _ -> Error error
            | _, Some error -> Error error
            | Ok at, None ->
                lock gate (fun () ->
                    if actions |> List.exists (fun a -> a.Id = cmd.ScheduleId && a.Status = "running") then
                        Error "ScheduleRunning: cannot replace an executing action."
                    elif actions |> List.exists (fun a -> a.Id = cmd.ScheduleId && isUncertainResume a) then
                        Error "ResumePublicationUncertain: reconcile the original intent before replacing this schedule."
                    elif (actions |> List.filter (fun a ->
                            a.Id <> cmd.ScheduleId && (a.Status = "pending" || a.Status = "waitingActivity"))
                            |> List.length) >= 100 then
                        Error "ScheduleLimitReached"
                    else
                        let entry =
                            { Id = cmd.ScheduleId; DueAtUtc = at; ProjectName = cmd.ProjectName
                              Url = required cmd.Url; ScriptPath = required cmd.ScriptPath
                              Alias = required cmd.Alias; ArgsJson = cmd.ArgsJson |> Option.defaultValue "{}"
                              Active = cmd.Active |> Option.defaultValue false
                              WindowRegistration = required cmd.WindowRegistration
                              CloseTabAfterExecution = cmd.CloseTabAfterExecution |> Option.defaultValue true
                              RepeatEverySeconds = if cmd.TriggerKind = "Every" then cmd.RepeatEverySeconds.Value else 0
                              Status = "pending"; RegistrationId = ""; ConversationUrl = ""; Cleanup = ""
                              AttemptId = ""; RevisionId = Guid.NewGuid().ToString("N")
                              Error = ""; UpdatedAtUtc = time
                              ActionKind = cmd.ActionKind; TriggerKind = cmd.TriggerKind
                              Executable = required cmd.Executable; CommandArgs = cmd.Args
                              WorkingDirectory = required cmd.WorkingDirectory
                              TimeoutSeconds = cmd.TimeoutSeconds |> Option.defaultValue 60
                              MaxOutputBytes = cmd.MaxOutputBytes |> Option.defaultValue 65536
                              IdleAgentId = required cmd.IdleAgentId
                              IdleSeconds = cmd.IdleSeconds |> Option.defaultValue 0
                              LastActivityAtUtc = initialSeen |> Option.defaultValue DateTimeOffset.MinValue
                              StartedAtUtc = DateTimeOffset.MinValue
                              Disabled = false; LastResult = "" }
                        setActions (entry :: (actions |> List.filter (fun a -> a.Id <> cmd.ScheduleId)))
                        Ok entry.DueAtUtc)

    member _.Cancel(id: string) =
        lock gate (fun () ->
            match actions |> List.tryFind (fun x -> x.Id = id) with
            | None -> Error "ScheduleNotFound"
            | Some a when a.Status = "pending" || a.Status = "waitingActivity" || a.Status = "running" ->
                setActions (actions |> List.map (fun item ->
                    if item.Id = id then
                        { item with Disabled = true
                                    Status = (if item.Status = "running" then "running" else "canceled")
                                    UpdatedAtUtc = clock() }
                    else item))
                Ok()
            | Some a -> Error ("ScheduleNotActive:" + a.Status))

    member _.List() =
        lock gate (fun () ->
            actions
            |> List.sortBy (fun a -> a.DueAtUtc)
            |> List.map describe
            |> JsonSerializer.Serialize)

    member _.Get(id: string) =
        lock gate (fun () ->
            match actions |> List.tryFind (fun a -> a.Id = id) with
            | Some action -> Ok(JsonSerializer.Serialize(describe action))
            | None -> Error "ScheduleNotFound")

    // Revalidate the exact durable occurrence after async preflight and
    // immediately before Send. Cancellation cannot authorize a replacement.
    member _.IsCurrentClaim(action: ScheduledAction) =
        lock gate (fun () ->
            not disposed && not cancellation.IsCancellationRequested &&
            (actions |> List.exists (fun a ->
                a.Id = action.Id && a.RevisionId = action.RevisionId
                && a.AttemptId = action.AttemptId && a.Status = "running" && not a.Disabled)))

    /// Persist the exact original selection before publication and the
    /// subsequent boundaries before entering Send. Stage changes belong to
    /// one exact active occurrence; no stale callback can advance it.
    member _.RecordResumeStage(action: ScheduledAction, selection: ResumeSelection) =
        lock gate (fun () ->
            match actions |> List.tryFind (fun a ->
                not disposed && not cancellation.IsCancellationRequested &&
                a.Id = action.Id && a.RevisionId = action.RevisionId &&
                a.AttemptId = action.AttemptId && a.Status = "running" && isReadyResume a) with
            | None -> false
            | Some current ->
                let valid =
                    match selection.Stage, tryResumeSelection current with
                    | "Selected", None -> current.Error = preSelection && not current.Disabled
                    | "Published", Some prior ->
                        prior.Stage = "Selected" && { prior with Stage = selection.Stage } = selection
                    | "EnteringSend", Some prior ->
                        not current.Disabled && prior.Stage = "Published" && { prior with Stage = selection.Stage } = selection
                    | _ -> false
                if not valid then false
                else
                    let marker = stagePrefix + JsonSerializer.Serialize(selection)
                    setActions (actions |> List.map (fun a ->
                        if a = current then { a with Error = marker; UpdatedAtUtc = clock() } else a))
                    true)

    member _.Start() =
        lock gate (fun () ->
            if started then invalidOp "Execution scheduler already started."
            if interval < 10 then invalidArg "pollMs" "Polling interval is too small."
            started <- true
            // Run off the TUI synchronization context so the recurring loop
            // continues after its first asynchronous delay.
            let runLoop () = task {
                try
                    while not cancellation.IsCancellationRequested do
                        // Reconcile one exact suspended occurrence against the
                        // authenticated canonical ledger, outside the store lock.
                        // Missing or mismatched receipt never clears the hold.
                        let recovery =
                            lock gate (fun () ->
                                let now = DateTimeOffset.UtcNow
                                actions
                                |> List.tryPick (fun a ->
                                    if not (isUncertainResume a) ||
                                       (match nextResumeCheck.TryGetValue a.Id with
                                        | true, next when now < next -> true
                                        | _ -> false) then None
                                    else tryResumeSelection a |> Option.map (fun selection -> a, selection))
                                |> Option.map (fun (a, selection) ->
                                    nextResumeCheck[a.Id] <- now.Add(waitingPoll)
                                    a, selection))
                        match recovery with
                        | Some (entry, selection) ->
                            let! isolated = task {
                                try return! reconcile entry selection
                                with _ -> return false
                            }
                            if isolated then
                                lock gate (fun () ->
                                    if not disposed && actions |> List.contains entry then
                                        setActions (actions |> List.map (fun a ->
                                            if a = entry then
                                                finishInterrupted (clock()) a
                                                    ("Original intent isolated by verified receipt: " + selection.OperationId.ToString("D"))
                                            else a)))
                        | None -> ()
                        // Local notifications may postpone a deadline, but are
                        // not proof that an idle agent has stopped sending tools.
                        // Never hold the state lock across server verification.
                        let candidate =
                            lock gate (fun () ->
                                let current = clock()
                                let refreshed =
                                    actions |> List.map (fun a ->
                                        if not (isIdle a) || a.Disabled then a
                                        else
                                            let latest = seen a.ProjectName a.IdleAgentId
                                            match a.Status with
                                            | "pending" | "waitingActivity" ->
                                                match latest with
                                                | Some at when at > a.LastActivityAtUtc ->
                                                    { a with LastActivityAtUtc = at
                                                             DueAtUtc = at.AddSeconds(float a.IdleSeconds)
                                                             Status = "pending"; UpdatedAtUtc = current }
                                                | _ when a.Status = "waitingActivity" &&
                                                         a.StartedAtUtc <> DateTimeOffset.MinValue &&
                                                         current >= a.StartedAtUtc.AddMinutes(30.) ->
                                                    // A submitted Manager may never make its first
                                                    // tool call. The old attempt is now uncertain;
                                                    // allow ONE verified replacement after 30m.
                                                    { a with Status = "pending"; DueAtUtc = current
                                                             Error = "outcomeUnknown: Manager launch had no tool activity for 30 minutes."
                                                             UpdatedAtUtc = current }
                                                | _ -> a
                                            | "running" ->
                                                let activity = latest |> Option.defaultValue DateTimeOffset.MinValue
                                                let lastProgress = max a.StartedAtUtc activity
                                                if current >= lastProgress.AddMinutes(30.) then
                                                    { a with Status = "pending"; DueAtUtc = current
                                                             Error = "outcomeUnknown: previous Manager launch stale after 30 minutes."
                                                             UpdatedAtUtc = current }
                                                else a
                                            | _ -> a)
                                if refreshed <> actions then setActions refreshed
                                let running = actions |> List.filter (fun a -> a.Status = "running")
                                let alreadyRunningManager entry =
                                    isIdle entry && (running |> List.exists (fun a ->
                                        isIdle a && a.ProjectName = entry.ProjectName
                                        && a.IdleAgentId = entry.IdleAgentId))
                                let resumeHeld entry =
                                    isReadyResume entry &&
                                    (actions |> List.exists (fun a ->
                                        a.Id <> entry.Id && sameResumeProject a entry
                                        && (a.Status = "running" || isUncertainResume a)))
                                let due =
                                    if running.Length >= 8 then None
                                    else
                                        actions
                                        |> List.filter (fun a ->
                                            a.Status = "pending" && not a.Disabled
                                            && a.DueAtUtc <= current && not (alreadyRunningManager a)
                                            && not (resumeHeld a))
                                        |> List.sortBy (fun a -> a.DueAtUtc)
                                        |> List.tryHead
                                match due with
                                | Some item -> Some item
                                | None ->
                                    // A finished Manager stays waiting until a fresh
                                    // server watermark proves a new idle episode.
                                    // Read slowly when nothing is due, unlike an active
                                    // due deadline which is always freshly verified.
                                    let waiting =
                                        actions
                                        |> List.tryFind (fun a ->
                                            isIdle a && a.Status = "waitingActivity" && not a.Disabled
                                            && (match nextWaitingCheck.TryGetValue a.Id with
                                                | true, nextCheck -> DateTimeOffset.UtcNow >= nextCheck
                                                | _ -> true))
                                    waiting |> Option.iter (fun a ->
                                        nextWaitingCheck[a.Id] <- DateTimeOffset.UtcNow.Add(waitingPoll))
                                    waiting)

                        let! verification =
                            match candidate with
                            | Some entry when isIdle entry ->
                                task {
                                    try
                                        return! verify entry.ProjectName entry.IdleAgentId
                                    with ex ->
                                        return Error("ActivityVerificationUnavailable: " + ex.GetType().Name)
                                }
                            | _ -> Task.FromResult(Ok None)

                        let next =
                            lock gate (fun () ->
                                if disposed || cancellation.IsCancellationRequested then None
                                else
                                    match candidate with
                                    | None -> None
                                    | Some prior ->
                                        match actions |> List.tryFind (fun a -> a.Id = prior.Id) with
                                        | None -> None
                                        | Some currentEntry when currentEntry.RevisionId <> prior.RevisionId ||
                                             currentEntry <> prior || currentEntry.Disabled ||
                                             (currentEntry.Status <> "pending" && currentEntry.Status <> "waitingActivity") ->
                                            // Verification belongs to the exact immutable
                                            // candidate. An operator replacement or updated
                                            // deadline must obtain its own fresh server read.
                                            None
                                        | Some currentEntry ->
                                            let now = clock()
                                            let eligible () =
                                                currentEntry.Status = "pending" && currentEntry.DueAtUtc <= now
                                                && (actions |> List.filter (fun a -> a.Status = "running") |> List.length) < 8
                                                && (not (isReadyResume currentEntry) ||
                                                    not (actions |> List.exists (fun a ->
                                                        a.Id <> currentEntry.Id && sameResumeProject a currentEntry
                                                        && (a.Status = "running" || isUncertainResume a))))
                                                && (not (isIdle currentEntry) ||
                                                    not (actions |> List.exists (fun a ->
                                                        isIdle a && a.Status = "running"
                                                        && a.ProjectName = currentEntry.ProjectName
                                                        && a.IdleAgentId = currentEntry.IdleAgentId)))
                                            let claim () =
                                                let claimed =
                                                    { currentEntry with Status = "running"
                                                                        AttemptId = Guid.NewGuid().ToString("N")
                                                                        StartedAtUtc = now
                                                                        Error = (if isReadyResume currentEntry then preSelection else currentEntry.Error)
                                                                        UpdatedAtUtc = now }
                                                // An fsync-backed claim is committed while
                                                // holding this process's lifetime ownership lease.
                                                setActions (actions |> List.map (fun a ->
                                                    if a.Id = currentEntry.Id then claimed else a))
                                                Some claimed
                                            if not (isIdle currentEntry) then
                                                if eligible() then claim() else None
                                            else
                                                match verification with
                                                | Error reason ->
                                                    // No verified snapshot means no Manager launch.
                                                    // Persist one diagnostic transition, not a write per poll.
                                                    let message = "AgentActivityUnverified: " + reason
                                                    if currentEntry.Error <> message then
                                                        setActions (actions |> List.map (fun a ->
                                                            if a.Id = currentEntry.Id then
                                                                { a with Error = message; UpdatedAtUtc = now }
                                                            else a))
                                                    None
                                                | Ok None ->
                                                    // A fresh DB with zero rows is NOT proof
                                                    // that the agent has been idle, even if a
                                                    // local cached timer has already expired.
                                                    let message = "ActivityProofUnavailable: no trusted cross-device activity watermark."
                                                    if currentEntry.Error <> message then
                                                        setActions (actions |> List.map (fun a ->
                                                            if a.Id = currentEntry.Id then
                                                                { a with Error = message; UpdatedAtUtc = now }
                                                            else a))
                                                    None
                                                | Ok (Some verified) ->
                                                    let local = seen currentEntry.ProjectName currentEntry.IdleAgentId
                                                    let latest =
                                                        [Some verified; local]
                                                        |> List.choose id
                                                        |> List.sortDescending
                                                        |> List.tryHead
                                                    match latest with
                                                    | Some at when at > currentEntry.LastActivityAtUtc ->
                                                        let due = at.AddSeconds(float currentEntry.IdleSeconds)
                                                        let due =
                                                            if currentEntry.LastActivityAtUtc = DateTimeOffset.MinValue then
                                                                max currentEntry.DueAtUtc due
                                                            else due
                                                        setActions (actions |> List.map (fun a ->
                                                            if a.Id = currentEntry.Id then
                                                                { a with LastActivityAtUtc = at; DueAtUtc = due
                                                                         Status = "pending"; Error = ""
                                                                         UpdatedAtUtc = now }
                                                            else a))
                                                        None
                                                    | _ when currentEntry.Status = "waitingActivity" -> None
                                                    | _ when eligible() -> claim()
                                                    | _ -> None)
                        match next with
                        | None -> ()
                        | Some entry ->
                            let run () = task {
                                try
                                    let! result = execute entry
                                    match result with
                                    | Ok value -> update entry.Id entry.AttemptId "completed" value ""
                                    | Error (value, reason) ->
                                        let status =
                                            if isReadyResume entry &&
                                               reason.StartsWith("ResumePublicationUncertain:", StringComparison.Ordinal) then "suspended"
                                            elif isReadyResume entry &&
                                                 reason.StartsWith("UiIncompatible:", StringComparison.Ordinal) then "suspended"
                                            elif reason.StartsWith("UiIncompatible:", StringComparison.Ordinal) then
                                                // Retry at the next interval; never replay this attempt.
                                                if entry.RepeatEverySeconds > 0 then "failed" else "suspended"
                                            elif reason.Contains("OutcomeUnknown", StringComparison.OrdinalIgnoreCase) then "outcomeUnknown"
                                            else "failed"
                                        update entry.Id entry.AttemptId status value
                                            (if reason.Length > 250 then reason.Substring(0, 250) else reason)
                                with ex ->
                                    if isReadyResume entry then
                                        update entry.Id entry.AttemptId "suspended" ""
                                            ("ResumePublicationUncertain: unexpected error: " + ex.GetType().Name)
                                    else
                                        update entry.Id entry.AttemptId "outcomeUnknown" ""
                                            ("Unexpected error: " + ex.GetType().Name)
                            }
                            Task.Run(Func<Task>(fun () -> run () :> Task)) |> ignore
                        do! Task.Delay(interval, cancellation.Token)
                with
                | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
            }
            // Restart only the polling loop after an internal fault. Claims
            // are fsync-persisted before external execution; an already
            // running occurrence is never submitted again by this supervisor.
            let rec supervise () = task {
                try
                    do! runLoop ()
                with
                | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
                | error ->
                    try reportFault ("Persistent scheduler polling fault (retrying): " + error.ToString())
                    with _ -> ()
                    try
                        do! Task.Delay(1000, cancellation.Token)
                        return! supervise ()
                    with :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
            }
            worker <- Task.Run(Func<Task>(fun () -> supervise () :> Task)))

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            // Do not release the cross-process lease while the tick loop can
            // still persist a claim. Late async results must not write state.
            try worker.Wait(TimeSpan.FromSeconds(10.)) |> ignore
            with :? AggregateException as errors when
                errors.InnerExceptions |> Seq.forall (fun e -> e :? OperationCanceledException) -> ()
            lock gate (fun () -> disposed <- true)
            ownership.Dispose()
            cancellation.Dispose()

let defaultPath () =
    Path.Combine(Path.GetDirectoryName(Client.ClientConfig.configPath()), "browser-schedules.json")
