module Client.ConsoleTui

open System
open System.Threading
open System.Threading.Tasks
open Common
open Client

type ClientConnectionState =
    | Connecting
    | Connected
    | Reconnecting
    | Disconnected
    | Closing

type private ActivityStatus =
    | Informational
    | Running
    | AwaitingPermission
    | Completed of durationMs: int64 * outcome: string option
    | Failed of durationMs: int64 * shortError: string
    | Interrupted

type private ActivityEntry =
    { Id: int
      PersistenceId: string
      Origin: string
      StartedAt: DateTimeOffset
      Timestamp: string
      ProjectName: string option
      Message: string
      CommandName: string option
      Reason: string option
      Detail: string option
      mutable Status: ActivityStatus
      mutable FailureDetail: string option }

type private TuiMode =
    | AgentsMode
    | AgentFilterMode
    | ActivityMode
    | PermissionMode
    | FilterMode
    | CopyRecentMode
    | JobsMode
    | JobDetailsMode
    | JobCancelMode
    | JobFilterMode
    | SettingsMode
    | SettingsEditMode
    | SettingsConfirmMode

type private PendingPrompt =
    { Id: int
      Command: AgentCommand
      Request: ConfirmationRequest
      Completion: TaskCompletionSource<PermissionApproval> }

type JobUiAccess =
    { List: unit -> Result<JobSummary list, string>
      Read: string -> int64 -> Result<JobResult, string>
      Cancel: string -> Result<unit, string> }

type AgentUiAccess =
    { Load: unit -> Result<AgentView.Snapshot, string> }

type SettingsUiAccess =
    { Load: unit -> Result<ClientConfigSnapshot, string>
      Save: ClientConfigSnapshot -> ClientStoredConfig -> Result<ClientConfigSnapshot, string>
      Effective: ClientEffectiveConfig }

type ConsoleTui(?activityLog: ActivityLog.Store) =
    let syncRoot = obj()
    let activity = ResizeArray<ActivityEntry>()
    let prompts = ResizeArray<PendingPrompt>()
    let mutable selectedPrompt = 0
    let mutable nextActivityId = 1
    let mutable nextPromptId = 1
    let mutable shouldQuit = false
    let mutable key = ""
    let mutable mode = ActivityMode
    let mutable selectedActivityId: int option = None
    let mutable activityFilterText = ""
    let mutable filterOriginalText = ""
    let mutable copyRecentCountText = "20"
    let mutable copyRecentCountEdited = false
    let mutable statusFilter = ActivityFilter.All
    let mutable showObservers = false
    let mutable activityScrollOffset = 0
    let mutable showActivityDetails = false
    let mutable agentAccess: AgentUiAccess option = None
    let mutable agentSnapshot: AgentView.Snapshot option = None
    let mutable agentChanged = Set.empty<string>
    let mutable agentSelected: string option = None
    let mutable agentFilter = ""
    let mutable agentOriginalFilter = ""
    let mutable agentScroll = 0
    let mutable showAgentDetails = false
    let mutable agentError: string option = None
    let mutable agentRefreshing = false
    let mutable agentLastPoll = DateTimeOffset.MinValue
    let mutable jobsAccess: JobUiAccess option = None
    let mutable jobSummaries: JobSummary list = []
    let mutable jobSelectedId: string option = None
    let mutable jobDetailId: string option = None
    let mutable jobIncludeCompleted = false
    let mutable jobProjectFilter = ""
    let mutable jobOriginalFilter = ""
    let mutable jobListScroll = 0
    let mutable jobOutputLines: JobView.OutputLine list = []
    let mutable jobNextSequence = 0L
    let mutable jobOutputTruncated = false
    let mutable jobFollow = true
    let mutable jobOutputScroll = 0
    let mutable jobError: string option = None
    let mutable jobLastPoll = DateTimeOffset.MinValue
    let mutable permissionReturnMode = ActivityMode
    let mutable jobCancelReturnMode = JobsMode
    let mutable settingsAccess: SettingsUiAccess option = None
    let mutable settingsSnapshot: ClientConfigSnapshot option = None
    let mutable settingsStaged = ClientConfig.empty
    let mutable settingsSelected = 0
    let mutable settingsEditing = ""
    let mutable settingsError: string option = None
    let mutable settingsReturnMode = ActivityMode
    let mutable connectionState = Disconnected
    let mutable serverHost = "-"
    let maxHistory = 500
    let visibleActivityRows = 12

    let trim value maxLength =
        if String.IsNullOrEmpty value || value.Length <= maxLength then value
        else value.Substring(0, maxLength - 1) + "…"

    let compact (value: string) =
        if isNull value then ""
        else value.Replace("\r", " ").Replace("\n", " ").Trim()

    let connectionLabel = function
        | Connecting -> "Connecting"
        | Connected -> "Connected"
        | Reconnecting -> "Reconnecting"
        | Disconnected -> "Disconnected"
        | Closing -> "Closing"

    let connectionColor = function
        | Connected -> ConsoleColor.Green
        | Connecting
        | Reconnecting -> ConsoleColor.Yellow
        | Disconnected -> ConsoleColor.Red
        | Closing -> ConsoleColor.DarkYellow

    let projectColorAssignments =
        ProjectColorAssignments.ProjectColorAssignments(
        [| ConsoleColor.Cyan
           ConsoleColor.Green
           ConsoleColor.Magenta
           ConsoleColor.Yellow
           ConsoleColor.Blue
           ConsoleColor.DarkCyan
           ConsoleColor.DarkGreen
           ConsoleColor.DarkMagenta |])

    let projectColor projectName =
        projectColorAssignments.Get(ActivityFilter.projectRootName projectName)

    let activityStatusMatches (filter: ActivityFilter.Status) (status: ActivityStatus) =
        match status with
        | _ when filter = ActivityFilter.All -> true
        | Running -> filter = ActivityFilter.Running
        | AwaitingPermission -> filter = ActivityFilter.AwaitingPermission
        | Completed _ -> filter = ActivityFilter.Completed
        | Failed _
        | Interrupted -> filter = ActivityFilter.Failed
        | Informational -> filter = ActivityFilter.Informational

    let searchableActivityText (entry: ActivityEntry) =
        let commandParts =
            [ entry.CommandName |> Option.defaultValue ""
              entry.Reason |> Option.defaultValue ""
              entry.Detail |> Option.defaultValue "" ]

        [ yield entry.Message
          yield! commandParts
          yield entry.FailureDetail |> Option.defaultValue "" ]
        |> String.concat " "

    let filteredActivityUnsafe () =
        let terms = ActivityFilter.parseTerms activityFilterText
        activity
        |> Seq.filter (fun entry ->
            (showObservers || entry.Origin <> "Observer")
            && activityStatusMatches statusFilter entry.Status
            && ActivityFilter.matchesTerms terms entry.ProjectName (searchableActivityText entry))
        |> Seq.toArray

    let maxScrollOffsetUnsafe () =
        max 0 ((filteredActivityUnsafe()).Length - visibleActivityRows)

    let clampScrollOffsetUnsafe () =
        activityScrollOffset <- activityScrollOffset |> max 0 |> min (maxScrollOffsetUnsafe())

    let selectedActivityIndexUnsafe () =
        let filtered = filteredActivityUnsafe()
        match selectedActivityId with
        | None -> None
        | Some id ->
            filtered
            |> Array.tryFindIndex (fun entry -> entry.Id = id)

    let selectActivityIndexUnsafe index =
        let filtered = filteredActivityUnsafe()
        match ActivityNavigation.normalizeSelection filtered.Length (Some index) with
        | None ->
            selectedActivityId <- None
            activityScrollOffset <- 0
        | Some selectedIndex ->
            selectedActivityId <- Some filtered[selectedIndex].Id
            activityScrollOffset <-
                ActivityNavigation.scrollOffsetForSelection
                    filtered.Length
                    visibleActivityRows
                    activityScrollOffset
                    selectedIndex

    let ensureActivitySelectionUnsafe () =
        let filtered = filteredActivityUnsafe()
        match selectedActivityIndexUnsafe() with
        | Some index -> Some index
        | None ->
            match ActivityNavigation.normalizeSelection filtered.Length None with
            | None -> None
            | Some index ->
                selectedActivityId <- Some filtered[index].Id
                Some index

    let moveActivitySelectionUnsafe delta =
        let filtered = filteredActivityUnsafe()
        let current = ensureActivitySelectionUnsafe()
        match ActivityNavigation.move filtered.Length delta current with
        | Some index -> selectActivityIndexUnsafe index
        | None -> ()

    let statusName = function
        | Informational -> "Info"
        | Running -> "Running"
        | AwaitingPermission -> "Awaiting permission"
        | Completed _ -> "Completed"
        | Failed _ -> "Failed"
        | Interrupted -> "Interrupted"

    let statusDuration = function
        | Completed(durationMs, _)
        | Failed(durationMs, _) -> Some durationMs
        | _ -> None

    let statusOutcome = function
        | Completed(_, outcome) -> outcome
        | Failed(_, shortError) -> Some shortError
        | _ -> None

    let commandMetadata command =
        match command with
        | None -> None, None, None
        | Some value ->
            Some(AgentCommandInfo.displayName value),
            AgentCommandInfo.fullReason value,
            AgentCommandInfo.fullDetail value

    let persistEntryUnsafe (entry: ActivityEntry) =
        activityLog
        |> Option.iter (fun store ->
            let duration =
                match statusDuration entry.Status with
                | Some value -> Nullable value
                | None -> Nullable()

            let snapshot: ActivityLog.Snapshot =
                { Version = 1
                  ActivityId = entry.PersistenceId
                  Origin = entry.Origin
                  StartedAt = entry.StartedAt
                  ProjectName = entry.ProjectName |> Option.toObj
                  CommandName = entry.CommandName |> Option.toObj
                  Message = entry.Message
                  Reason = entry.Reason |> Option.toObj
                  Detail = entry.Detail |> Option.toObj
                  Status = statusName entry.Status
                  DurationMs = duration
                  Result = statusOutcome entry.Status |> Option.toObj
                  Error = entry.FailureDetail |> Option.toObj }

            store.Append snapshot)

    let addActivityUnsafe projectName message command status =
        let wasScrolled = activityScrollOffset > 0
        let previousFiltered = filteredActivityUnsafe()
        let previousLastId =
            if previousFiltered.Length = 0 then None
            else Some previousFiltered[previousFiltered.Length - 1].Id
        let followLatest =
            not showActivityDetails
            && activityScrollOffset = 0
            && (selectedActivityId.IsNone || selectedActivityId = previousLastId)
        let startedAt = DateTimeOffset.Now
        let commandName, reason, detail = commandMetadata command
        let origin =
            match command with
            | Some(GetGitStatusCommand _)
            | Some(ListJobsCommand _) -> "Observer"
            | _ -> "Worker"
        let entry =
            { Id = nextActivityId
              PersistenceId = Guid.NewGuid().ToString("N")
              Origin = origin
              StartedAt = startedAt
              Timestamp = startedAt.ToString("HH:mm:ss")
              ProjectName = projectName
              Message = message
              CommandName = commandName
              Reason = reason
              Detail = detail
              Status = status
              FailureDetail = None }

        nextActivityId <- nextActivityId + 1
        activity.Add entry
        persistEntryUnsafe entry

        let entryMatchesFilter =
            filteredActivityUnsafe()
            |> Array.exists (fun item -> item.Id = entry.Id)

        if followLatest && entryMatchesFilter then
            selectedActivityId <- Some entry.Id

        if wasScrolled && entryMatchesFilter then
            activityScrollOffset <- activityScrollOffset + 1

        while activity.Count > maxHistory do
            let removed = activity[0]
            activity.RemoveAt 0
            if selectedActivityId = Some removed.Id && activity.Count > 0 then
                selectedActivityId <- Some activity[0].Id

        clampScrollOffsetUnsafe()
        entry.Id

    let tryFindActivityUnsafe id =
        activity |> Seq.tryFind (fun entry -> entry.Id = id)

    let statusSuffix = function
        | Informational -> ""
        | Running -> " …"
        | AwaitingPermission -> " [awaiting permission]"
        | Completed(durationMs, None) -> $" ({durationMs} ms)"
        | Completed(durationMs, Some outcome) -> $" ({durationMs} ms, {outcome})"
        | Failed(durationMs, shortError) -> $" ({durationMs} ms) FAILED: {shortError}"
        | Interrupted -> " [interrupted]"

    let activityText entry =
        let project =
            entry.ProjectName
            |> Option.map (fun value -> ActivityFilter.projectToken value + " ")
            |> Option.defaultValue ""

        $"{entry.Timestamp} {project}{entry.Message}{statusSuffix entry.Status}"

    let writeColoredProjectLine width marker entry =
        let rendered = $"{marker} {activityText entry}" |> fun value -> trim value width

        match entry.ProjectName with
        | None -> Console.WriteLine rendered
        | Some projectName ->
            let token = ActivityFilter.projectToken projectName
            let index = rendered.IndexOf(token, StringComparison.Ordinal)

            if index < 0 then
                Console.WriteLine rendered
            else
                let previous = Console.ForegroundColor
                Console.Write(rendered.Substring(0, index))
                Console.ForegroundColor <- projectColor projectName
                Console.Write(token)
                Console.ForegroundColor <- previous
                Console.WriteLine(rendered.Substring(index + token.Length))

    let writeColoredProject projectName =
        let previous = Console.ForegroundColor
        Console.ForegroundColor <- projectColor projectName
        Console.Write(ActivityFilter.projectToken projectName)
        Console.ForegroundColor <- previous

    let hotkeyColor = ConsoleColor.Cyan

    let writeHotkeyToken (token: string) =
        let previous = Console.ForegroundColor
        Console.ForegroundColor <- hotkeyColor
        Console.Write token
        Console.ForegroundColor <- previous

    let writeMnemonic hotkey (label: string) =
        let hotkeyText = string hotkey
        let index = label.IndexOf(hotkeyText, StringComparison.OrdinalIgnoreCase)

        if index < 0 then
            Console.Write label
        else
            Console.Write(label.Substring(0, index))
            writeHotkeyToken (label.Substring(index, 1))
            Console.Write(label.Substring(index + 1))

    let writeActivityHotkeys () =
        Console.Write "Keys: "
        writeHotkeyToken "↑/↓"; Console.Write " activity, "
        writeHotkeyToken "PgUp/PgDn"; Console.Write " page, "
        writeHotkeyToken "End"; Console.Write " latest, "
        writeHotkeyToken "Enter"; Console.Write " details, "
        writeHotkeyToken "/"; Console.Write " filter, "
        writeHotkeyToken "@"; Console.Write " project, "
        writeMnemonic 's' "status"; Console.Write ", "
        writeMnemonic 'c' "clear"; Console.Write ", "
        writeMnemonic 'p' "permissions"; Console.Write ", "
        writeMnemonic 'j' "jobs"; Console.Write ", "
        writeMnemonic 'a' "agents"; Console.Write ", "
        writeMnemonic 'g' "settings"; Console.Write ", "
        writeMnemonic 'r' "copy recent"; Console.Write ", "
        writeMnemonic 'o' (if showObservers then "hide observers" else "show observers"); Console.Write ", "
        writeMnemonic 'q' "quit"; Console.WriteLine ""

    let writePermissionHotkeys () =
        Console.Write "Keys: "
        writeHotkeyToken "↑/↓"; Console.Write " permission, "
        writeMnemonic 'a' "allow once"; Console.Write ", "
        writeMnemonic 's' "allow exact for session"; Console.Write ", "
        writeMnemonic 'e' "executable"; Console.Write ", "
        writeMnemonic 'd' "deny"; Console.Write ", "
        writeHotkeyToken "Esc"; Console.Write " activity, "
        writeMnemonic 'q' "quit"; Console.WriteLine ""

    let writeFilterHotkeys () =
        Console.Write "Filter: type text; "
        writeHotkeyToken "@"; Console.Write "term filters project, "
        writeHotkeyToken "Backspace"; Console.Write " edits, "
        writeHotkeyToken "Enter"; Console.Write " applies, "
        writeHotkeyToken "Esc"; Console.WriteLine " cancels"

    let writeCopyRecentHotkeys () =
        Console.Write $"Copy recent commands: {copyRecentCountText}"
        Console.Write "  "
        writeHotkeyToken "Enter"; Console.Write " copies, "
        writeHotkeyToken "Esc"; Console.WriteLine " cancels"

    let activityRangeUnsafe () =
        let filtered = filteredActivityUnsafe()
        clampScrollOffsetUnsafe()
        let endExclusive = filtered.Length - activityScrollOffset
        let start = max 0 (endExclusive - visibleActivityRows)
        filtered, start, endExclusive

    let wrappedLines width (detail: string) =
        let maxWidth = max 20 width
        detail.Replace("\r", "").Split('\n')
        |> Seq.collect (fun line ->
            let line = if String.IsNullOrEmpty line then " " else line
            seq {
                let mutable offset = 0
                while offset < line.Length do
                    let length = min maxWidth (line.Length - offset)
                    yield line.Substring(offset, length)
                    offset <- offset + length
            })
        |> Seq.toList

    let selectedActivityUnsafe () =
        let filtered = filteredActivityUnsafe()
        ensureActivitySelectionUnsafe()
        |> Option.map (fun index -> filtered[index])

    let recentCommandExportUnsafe count =
        activity
        |> Seq.filter (fun entry -> showObservers || entry.Origin <> "Observer")
        |> Seq.choose (fun entry ->
            entry.CommandName
            |> Option.map (fun commandName ->
                let exported: ActivityExport.Entry =
                    { Timestamp = entry.Timestamp
                      ProjectName = entry.ProjectName
                      CommandName = commandName
                      Reason = entry.Reason
                      Detail = entry.Detail
                      Status = statusName entry.Status
                      DurationMs = statusDuration entry.Status
                      Result = statusOutcome entry.Status }
                exported))
        |> Seq.rev
        |> Seq.truncate count
        |> Seq.rev
        |> Seq.toList
        |> ActivityExport.formatRecent

    let recentCommandCountUnsafe count =
        activity
        |> Seq.filter (fun entry -> (showObservers || entry.Origin <> "Observer") && entry.CommandName.IsSome)
        |> Seq.rev
        |> Seq.truncate count
        |> Seq.length

    let writeDetailField width label value =
        let labelWidth = 11
        let available = max 20 (width - labelWidth)
        let lines = wrappedLines available value

        match lines with
        | [] -> Console.WriteLine($"{label,-11}")
        | first :: rest ->
            Console.WriteLine($"{label,-11}{first}")
            let indent = String.replicate labelWidth " "
            for line in rest do
                Console.WriteLine($"{indent}{line}")

    let renderActivityDetailsUnsafe width =
        if showActivityDetails then
            match selectedActivityUnsafe() with
            | None -> ()
            | Some entry ->
                Console.WriteLine ""
                Console.WriteLine "Activity details"
                Console.WriteLine "----------------"
                writeDetailField width "Time" entry.Timestamp
                entry.ProjectName
                |> Option.map ActivityFilter.projectToken
                |> Option.iter (writeDetailField width "Project")

                match entry.CommandName with
                | Some commandName ->
                    writeDetailField width "Command" commandName
                    entry.Reason
                    |> Option.iter (writeDetailField width "Reason")
                    entry.Detail
                    |> Option.iter (writeDetailField width "Detail")
                | None ->
                    writeDetailField width "Command" "Log"
                    writeDetailField width "Detail" entry.Message

                writeDetailField width "Origin" entry.Origin
                writeDetailField width "Status" (statusName entry.Status)
                statusDuration entry.Status
                |> Option.iter (fun durationMs -> writeDetailField width "Duration" $"{durationMs} ms")
                statusOutcome entry.Status
                |> Option.iter (writeDetailField width "Result")

                entry.FailureDetail
                |> Option.iter (writeDetailField width "Error")

    let renderPermissionRequestsUnsafe width =
        Console.WriteLine ""
        let permissionSuffix =
            if prompts.Count = 0 then ""
            else $" ({prompts.Count})"
        Console.WriteLine $"Permission requests{permissionSuffix}"
        Console.WriteLine "-------------------"
        if prompts.Count = 0 then
            Console.WriteLine "No pending permission requests."
        else
            for index = 0 to prompts.Count - 1 do
                let prompt = prompts[index]
                let marker =
                    if mode = PermissionMode && index = selectedPrompt then ">"
                    else " "
                Console.Write $"{marker} #{prompt.Id} {prompt.Request.CommandName}"
                match prompt.Request.ProjectName with
                | Some projectName ->
                    Console.Write " "
                    writeColoredProject projectName
                | None -> ()
                Console.WriteLine ""
                Console.WriteLine $"    {trim prompt.Request.Summary (Math.Max(20, width - 4))}"
                AgentCommandInfo.reason prompt.Command
                |> Option.iter (fun reason ->
                    Console.WriteLine $"    Reason: {trim reason (Math.Max(20, width - 12))}")
        Console.WriteLine ""

    let jobListUnsafe () =
        JobView.visibleJobs jobIncludeCompleted jobProjectFilter jobSummaries

    let currentJobUnsafe () =
        jobSummaries
        |> List.tryFind (fun job -> Some job.JobId = jobSelectedId)

    let selectJobIndexUnsafe index =
        let items = jobListUnsafe() |> List.toArray
        if items.Length = 0 then
            jobSelectedId <- None
            jobListScroll <- 0
        else
            let bounded = max 0 (min (items.Length - 1) index)
            jobSelectedId <- Some items[bounded].JobId
            let maxRows = 12
            if bounded < jobListScroll then jobListScroll <- bounded
            elif bounded >= jobListScroll + maxRows then jobListScroll <- bounded - maxRows + 1

    let moveJobSelectionUnsafe delta =
        let items = jobListUnsafe() |> List.toArray
        let current =
            items |> Array.tryFindIndex (fun job -> Some job.JobId = jobSelectedId)
            |> Option.defaultValue 0
        selectJobIndexUnsafe (current + delta)

    let resetJobDetailsUnsafe jobId =
        jobDetailId <- Some jobId
        jobSelectedId <- Some jobId
        jobOutputLines <- []
        jobNextSequence <- 0L
        jobOutputTruncated <- false
        jobFollow <- true
        jobOutputScroll <- 0
        jobError <- None

    let pollJobsUnsafe () =
        jobLastPoll <- DateTimeOffset.UtcNow
        match jobsAccess with
        | None -> jobError <- Some "Job manager unavailable."
        | Some access ->
            match access.List() with
            | Error error -> jobError <- Some error
            | Ok summaries ->
                jobError <- None
                let oldSelection = currentJobUnsafe()
                jobSummaries <- summaries
                match oldSelection with
                | Some before when JobView.isRunning before
                                   && (jobSummaries |> List.exists (fun after ->
                                       after.JobId = before.JobId && not (JobView.isRunning after))) ->
                    jobIncludeCompleted <- true
                | _ -> ()
                let items = jobListUnsafe()
                if not (items |> List.exists (fun job -> Some job.JobId = jobSelectedId)) then
                    jobSelectedId <- items |> List.tryHead |> Option.map _.JobId
                    jobListScroll <- 0
                match jobDetailId with
                | Some jobId when mode = JobDetailsMode || mode = JobCancelMode ->
                    match access.Read jobId jobNextSequence with
                    | Ok result ->
                        if not result.Events.IsEmpty then
                            jobOutputLines <- JobView.appendOutput 1500 jobOutputLines result.Events
                            if jobFollow then jobOutputScroll <- 0
                        jobNextSequence <- result.NextSequence
                        jobOutputTruncated <- result.Truncated
                    | Error error -> jobError <- Some error
                | _ -> ()

    let openJobDetailsUnsafe () =
        match currentJobUnsafe() with
        | None -> ()
        | Some job ->
            resetJobDetailsUnsafe job.JobId
            mode <- JobDetailsMode
            pollJobsUnsafe()

    let beginJobCancellationUnsafe () =
        let selected =
            match jobDetailId, mode with
            | Some id, JobDetailsMode -> jobSummaries |> List.tryFind (fun job -> job.JobId = id)
            | _ -> currentJobUnsafe()
        match selected with
        | Some job when JobView.isRunning job ->
            jobSelectedId <- Some job.JobId
            jobCancelReturnMode <- mode
            mode <- JobCancelMode
        | _ -> jobError <- Some "Selected job is no longer running."

    let confirmJobCancellationUnsafe () =
        let id = jobSelectedId
        mode <- jobCancelReturnMode
        match id, jobsAccess with
        | Some jobId, Some access ->
            // Recheck the exact ID and current status: list selection may have changed.
            match access.List() with
            | Ok jobs when jobs |> List.exists (fun job -> job.JobId = jobId && JobView.isRunning job) ->
                match access.Cancel jobId with
                | Ok () ->
                    addActivityUnsafe None $"Local cancellation requested for job {jobId}." None Informational |> ignore
                    jobError <- None
                | Error error ->
                    jobError <- Some error
                    addActivityUnsafe None $"Job cancellation failed: {error}" None Informational |> ignore
            | Ok _ -> jobError <- Some "Job has already finished or been removed."
            | Error error -> jobError <- Some error
        | _ -> ()
        pollJobsUnsafe()

    let writeJobsHotkeys () =
        Console.WriteLine "Keys: Up/Down select, PgUp/PgDn page, Enter output, t toggle completed,"
        Console.WriteLine "      f filter project, c cancel, p permissions, Esc activity, q quit"

    let writeJobDetailsHotkeys () =
        Console.WriteLine "Keys: Up/Down/PgUp/PgDn scroll output, b follow/bottom, c cancel,"
        Console.WriteLine "      p permissions, Esc jobs, q quit"

    let renderJobsUnsafe width =
        let items = jobListUnsafe() |> List.toArray
        let running = jobSummaries |> List.filter JobView.isRunning |> List.length
        let finished = jobSummaries.Length - running
        let safe value = JobView.safeLine value
        Console.WriteLine $"Jobs: {running} running, {finished} recent completed"
        let filterLabel = if jobProjectFilter = "" then "(all)" else safe jobProjectFilter
        let completedLabel = if jobIncludeCompleted then "shown" else "hidden"
        Console.WriteLine $"Project filter: {filterLabel} | Finished: {completedLabel}"
        jobError |> Option.iter (fun error -> Console.WriteLine($"Error: {trim (safe error) (width - 7)}"))
        match mode with
        | JobsMode | JobFilterMode ->
            if mode = JobFilterMode then Console.WriteLine $"Filter project: {safe jobProjectFilter} (Enter apply, Esc revert)"
            if items.Length = 0 then Console.WriteLine "No matching jobs."
            else
                let selected = items |> Array.tryFindIndex (fun job -> Some job.JobId = jobSelectedId) |> Option.defaultValue 0
                let maxRows = 12
                jobListScroll <- max 0 (min jobListScroll (max 0 (items.Length - maxRows)))
                if selected < jobListScroll then jobListScroll <- selected
                if selected >= jobListScroll + maxRows then jobListScroll <- selected - maxRows + 1
                let last = min items.Length (jobListScroll + maxRows)
                for index = jobListScroll to last - 1 do
                    let job: JobSummary = items[index]
                    let marker = if selected = index then ">" else " "
                    let elapsed = JobView.duration DateTimeOffset.UtcNow job
                    let status = JobView.statusText job.Status
                    let reason = job.Reason |> Option.defaultValue (String.concat " " (job.Executable :: job.Args))
                    Console.Write(marker + " ")
                    let previous = Console.ForegroundColor
                    Console.ForegroundColor <- projectColor job.ProjectName
                    Console.Write(safe (ActivityFilter.projectToken job.ProjectName))
                    Console.ForegroundColor <- previous
                    Console.WriteLine (" " + trim (safe ($"{status} | {elapsed} | {reason}")) (max 15 (width - (ActivityFilter.projectToken job.ProjectName).Length - 3)))
        | JobDetailsMode | JobCancelMode ->
            match jobDetailId |> Option.bind (fun id -> jobSummaries |> List.tryFind (fun job -> job.JobId = id)) with
            | None -> Console.WriteLine "Job no longer retained by the client."
            | Some job ->
                let field name value = Console.WriteLine(trim (safe ($"{name}: {value}")) width)
                field "Id" job.JobId
                field "Project" job.ProjectName
                field "Reason" (job.Reason |> Option.defaultValue "(none)")
                field "Command" (String.concat " " (job.Executable :: job.Args))
                field "Directory" (job.WorkingDirectory |> Option.defaultValue "(project root)")
                field "Started" (job.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                field "Finished" (job.CompletedAt |> Option.map (fun value -> value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")) |> Option.defaultValue "-")
                field "Duration" (JobView.duration DateTimeOffset.UtcNow job)
                field "Status" (JobView.statusText job.Status)
                if jobOutputTruncated then Console.WriteLine "Output capture truncated at the job's byte limit."
                Console.WriteLine (if jobFollow then "Output (following)" else "Output (scroll paused; b to follow)")
                Console.WriteLine "---------------------"
                let lines = jobOutputLines |> List.toArray
                let rows = max 3 (min 15 (Console.WindowHeight - 19))
                jobOutputScroll <- max 0 (min jobOutputScroll (max 0 (lines.Length - rows)))
                let finish = max 0 (lines.Length - jobOutputScroll)
                let start = max 0 (finish - rows)
                for i = start to finish - 1 do
                    let line = lines[i]
                    let prefix = if line.Stream = JobOutputStream.StdErr then "ERR " else "OUT "
                    Console.WriteLine(trim (prefix + line.Text) width)
                if lines.Length = 0 then Console.WriteLine "(no output captured)"
                if jobOutputScroll > 0 then Console.WriteLine $"({jobOutputScroll} lines below)"
                if mode = JobCancelMode then
                    Console.WriteLine ""
                    Console.WriteLine $"Cancel running job {job.JobId} for {safe job.ProjectName}?"
                    Console.WriteLine "y confirms; n/Esc returns."
        | _ -> ()
        Console.WriteLine ""
        if prompts.Count > 0 then Console.WriteLine $"{prompts.Count} pending permission request(s): press p to review."

    let agentListUnsafe () =
        agentSnapshot
        |> Option.map (AgentView.visibleRows agentFilter)
        |> Option.defaultValue []
        |> List.toArray

    let selectedAgentUnsafe () =
        agentListUnsafe() |> Array.tryFind (fun row -> Some row.Key = agentSelected)

    let normalizeAgentSelectionUnsafe () =
        let items = agentListUnsafe()
        if not (items |> Array.exists (fun row -> Some row.Key = agentSelected)) then
            agentSelected <- items |> Array.tryHead |> Option.map _.Key
            agentScroll <- 0

    let selectAgentIndexUnsafe index =
        let items = agentListUnsafe()
        if items.Length > 0 then
            let bounded = max 0 (min (items.Length - 1) index)
            agentSelected <- Some items[bounded].Key
            if bounded < agentScroll then agentScroll <- bounded
            elif bounded >= agentScroll + visibleActivityRows then
                agentScroll <- bounded - visibleActivityRows + 1

    let moveAgentSelectionUnsafe delta =
        let items = agentListUnsafe()
        if items.Length > 0 then
            let index =
                items |> Array.tryFindIndex (fun row -> Some row.Key = agentSelected)
                |> Option.defaultValue 0
            selectAgentIndexUnsafe (index + delta)

    let writeAgentHotkeys () =
        Console.Write "Keys: "
        writeHotkeyToken "↑/↓"; Console.Write " agent, "
        writeHotkeyToken "PgUp/PgDn"; Console.Write " page, "
        writeHotkeyToken "End"; Console.Write " last, "
        writeHotkeyToken "Enter"; Console.Write " details, "
        writeHotkeyToken "/"; Console.Write " filter, "
        writeHotkeyToken "@"; Console.Write " project, "
        writeMnemonic 'r' "refresh"; Console.Write ", "
        writeMnemonic 'p' "permissions"; Console.Write ", "
        writeHotkeyToken "Esc"; Console.Write " activity, "
        writeMnemonic 'q' "quit"; Console.WriteLine ""

    let writeColoredAgentLine width marker (row: AgentView.AgentRow) =
        let timestamp =
            row.StateEnteredAt
            |> Option.map (fun updated -> updated.ToLocalTime().ToString("HH:mm:ss"))
            |> Option.defaultValue "--:--:--"
        let token = ActivityFilter.projectToken row.Project
        let flag = if Set.contains row.Key agentChanged then "*" else " "
        let status = AgentView.agentSummary row |> compact
        let rendered = trim $"{marker}{flag} {timestamp} {token} {row.Agent}: {status}" width
        let index = rendered.IndexOf(token, StringComparison.Ordinal)
        if index < 0 then Console.WriteLine rendered
        else
            let original = Console.ForegroundColor
            Console.Write(rendered.Substring(0, index))
            Console.ForegroundColor <- projectColor row.Project
            Console.Write(token)
            Console.ForegroundColor <- original
            let rest = rendered.Substring(index + token.Length)
            Console.WriteLine rest
            Console.ForegroundColor <- original

    let renderAgentDetailsUnsafe width =
        if showAgentDetails then
            match selectedAgentUnsafe () with
            | None -> ()
            | Some row ->
                Console.WriteLine ""
                Console.WriteLine "Agent details"
                Console.WriteLine "-------------"
                writeDetailField width "Agent" row.Agent
                writeDetailField width "Project" (ActivityFilter.projectToken row.Project)
                writeDetailField width "State" row.State
                row.StateEnteredAt
                |> Option.iter (fun value ->
                    writeDetailField width "State since" (value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
                row.StateReason |> Option.iter (writeDetailField width "Reason / next action")
                if agentChanged.Contains row.Key then
                    writeDetailField width "Updated" "Ledger state changed since previous refresh."
                row.Focus |> Option.iter (fun task ->
                    writeDetailField width "Task" (task.TaskId + " - " + task.Title)
                    writeDetailField width "Lifecycle" task.Lifecycle
                    writeDetailField width "Attempt" task.Phase
                    writeDetailField width "Assignment generation" (string task.AssignmentGeneration)
                    task.SourceRef |> Option.iter (writeDetailField width "Source ref")
                    task.BaseCommit |> Option.iter (writeDetailField width "Base commit"))
                Console.WriteLine ""
                Console.WriteLine "Status reflects the canonical remote orchestration ledger, not live chat activity."

    let renderAgentsUnsafe width =
        normalizeAgentSelectionUnsafe()
        let filtered = agentListUnsafe()
        let all = agentSnapshot |> Option.map AgentView.rows |> Option.defaultValue []
        let reviewCount = agentSnapshot |> Option.map (fun snap -> snap.Projects |> List.sumBy _.PendingReviews) |> Option.defaultValue 0
        let completedCount = all |> List.filter (fun row -> row.State = "Completed") |> List.length
        let filterSuffix =
            if String.IsNullOrWhiteSpace agentFilter then ""
            else $" [text=\"{trim (compact agentFilter) (max 20 (width - 22))}\"]"
        let selected = filtered |> Array.tryFindIndex (fun row -> Some row.Key = agentSelected) |> Option.defaultValue 0
        agentScroll <- max 0 (min agentScroll (max 0 (filtered.Length - visibleActivityRows)))
        if filtered.Length > 0 && selected < agentScroll then agentScroll <- selected
        if filtered.Length > 0 && selected >= agentScroll + visibleActivityRows then
            agentScroll <- selected - visibleActivityRows + 1
        let last = min filtered.Length (agentScroll + visibleActivityRows)
        let historySuffix =
            if filtered.Length = 0 then $" [0 of {all.Length}]"
            elif filtered.Length > visibleActivityRows || filtered.Length <> all.Length then
                $" [{agentScroll + 1}-{last} of {filtered.Length} / {all.Length} total]"
            else ""
        Console.WriteLine $"Agents{filterSuffix}{historySuffix}"
        Console.WriteLine "------"
        let stateCount state = all |> List.filter (fun row -> row.State = state) |> List.length
        Console.WriteLine
            (sprintf "Tracked: %d | Review: %d | Working: %d | Ready: %d | Assigned: %d | Completed: %d | Parked: %d"
                all.Length reviewCount (stateCount "Working") (stateCount "Ready")
                (stateCount "Assigned") completedCount (stateCount "Parked"))
        let refreshed =
            agentSnapshot
            |> Option.map (fun snap -> snap.RefreshedAt.ToLocalTime().ToString("HH:mm:ss"))
            |> Option.defaultValue "never"
        let refreshingSuffix = if agentRefreshing then " (refreshing...)" else ""
        Console.WriteLine $"Snapshot: {refreshed}{refreshingSuffix}"
        if not agentChanged.IsEmpty then
            Console.WriteLine $"Updated agents: {agentChanged.Count} (*)"
        agentError |> Option.iter (fun error -> Console.WriteLine(trim ("Error: " + compact error) width))
        if mode = AgentFilterMode then Console.WriteLine $"Filter: {trim (compact agentFilter) (max 20 (width - 9))}"
        if agentSnapshot.IsNone && not agentRefreshing then
            Console.WriteLine "No snapshot loaded. Press r to refresh."
        elif filtered.Length = 0 then
            Console.WriteLine "No matching agents."
        else
            for index = agentScroll to last - 1 do
                let row = filtered[index]
                let marker = if mode = AgentsMode && Some row.Key = agentSelected then ">" else " "
                writeColoredAgentLine width marker row

        renderAgentDetailsUnsafe width

        agentSnapshot |> Option.iter (fun snap ->
            let errors =
                snap.Projects |> List.choose (fun project ->
                    project.Error |> Option.map (fun error -> project.Project + ": " + error))
            let issues = errors @ snap.Issues
            if not issues.IsEmpty then
                Console.WriteLine ""
                Console.WriteLine $"Agent issues ({issues.Length})"
                Console.WriteLine "------------"
                for issue in issues |> List.truncate 5 do
                    Console.WriteLine(trim (compact issue) width))

        renderPermissionRequestsUnsafe width

    let settingKeys = [| "machine-name"; "workspace-path"; "trust-level"; "allowed-environment-variables"; "max-import-bytes" |]
    let settingLabels = [| "Machine name"; "Workspace directory"; "Trust level"; "Allowed environment variables"; "Maximum import bytes" |]

    let settingsValue key (stored: ClientStoredConfig) =
        match key with
        | "machine-name" -> stored.MachineName
        | "workspace-path" -> stored.WorkspacePath
        | "trust-level" -> stored.TrustLevel
        | "allowed-environment-variables" -> stored.AllowedEnvironmentVariables |> Option.map (String.concat ",")
        | "max-import-bytes" -> stored.MaxImportBytes |> Option.map string
        | _ -> None

    let settingsEffective key (config: ClientEffectiveConfig) =
        match key with
        | "machine-name" -> config.MachineName
        | "workspace-path" -> config.WorkspacePath
        | "trust-level" -> config.TrustLevel
        | "allowed-environment-variables" -> String.concat "," config.AllowedEnvironmentVariables
        | "max-import-bytes" -> string config.MaxImportBytes
        | _ -> ""

    let openSettingsUnsafe () =
        settingsReturnMode <- mode
        settingsSnapshot <- None
        settingsStaged <- ClientConfig.empty
        settingsError <- None
        match settingsAccess with
        | None -> settingsError <- Some "Configuration service unavailable."
        | Some access ->
            match access.Load() with
            | Error error -> settingsError <- Some error
            | Ok snapshot ->
                settingsSnapshot <- Some snapshot
                settingsStaged <- snapshot.Stored
        mode <- SettingsMode

    let saveSettingsUnsafe () =
        match settingsAccess, settingsSnapshot with
        | Some access, Some snapshot ->
            match access.Save snapshot settingsStaged with
            | Error error -> settingsError <- Some error
            | Ok saved ->
                settingsSnapshot <- Some saved
                settingsError <- Some "Saved. Restart client for changed runtime settings; machine name updates at next registration."
        | _ -> settingsError <- Some "Configuration unavailable."

    let applySettingsEditUnsafe () =
        let key = settingKeys[settingsSelected]
        match ClientConfig.updateKey key settingsEditing settingsStaged with
        | Error error -> settingsError <- Some error
        | Ok stored ->
            settingsStaged <- stored
            settingsError <- None
            mode <- SettingsMode

    let renderSettingsUnsafe width =
        let safe = JobView.safeLine
        Console.WriteLine "Settings (user-level configuration)"
        Console.WriteLine "-----------------------------------"
        match settingsSnapshot with
        | None -> Console.WriteLine "Config unavailable."
        | Some snapshot ->
            Console.WriteLine(trim (safe snapshot.Path) width)
            let effective = settingsAccess |> Option.map _.Effective
            for index = 0 to settingKeys.Length - 1 do
                let key = settingKeys[index]
                let value = settingsValue key settingsStaged |> Option.defaultValue "(default)"
                let source =
                    match effective with
                    | Some e when e.Overrides.Contains(
                        match key with
                        | "machine-name" -> "machineName"
                        | "workspace-path" -> "workspacePath"
                        | "trust-level" -> "trustLevel"
                        | "allowed-environment-variables" -> "allowedEnvironmentVariables"
                        | _ -> "maxImportBytes") -> " [startup override]"
                    | _ -> ""
                let marker = if settingsSelected = index then ">" else " "
                Console.WriteLine(trim (safe $"{marker} {settingLabels[index]}: {value}{source}") width)
            match effective with
            | Some e ->
                Console.WriteLine ""
                Console.WriteLine "Startup effective values (changes may require restart):"
                for index = 0 to settingKeys.Length - 1 do
                    Console.WriteLine(trim (safe $"{settingLabels[index]}: {settingsEffective settingKeys[index] e}") width)
            | None -> ()
        Console.WriteLine ""
        if mode = SettingsEditMode then
            Console.WriteLine(trim (safe $"Editing {settingLabels[settingsSelected]}: {settingsEditing}") width)
            Console.WriteLine "Enter accepts field, Esc cancels edit."
            if settingsSelected = 3 then
                Console.WriteLine "Warning: allowlisted variables can expose credentials to processes started by agents."
        elif mode = SettingsConfirmMode then
            Console.WriteLine "WARNING: Full trust disables local confirmation for agent operations."
            Console.WriteLine "Press y to save, n/Esc to cancel."
        else
            Console.WriteLine "Up/Down select, Enter edit, u restore default, s save, Esc back."
            Console.WriteLine "Saved changes to workspace, trust and limits require restart."
            let originalVariables = settingsSnapshot |> Option.bind (fun s -> s.Stored.AllowedEnvironmentVariables)
            if originalVariables <> settingsStaged.AllowedEnvironmentVariables then
                Console.WriteLine "Warning: environment allowlist changes can expose credential variables."
        settingsError |> Option.iter (fun error -> Console.WriteLine(trim (safe error) width))
        if prompts.Count > 0 then Console.WriteLine $"{prompts.Count} permission requests pending (p to review)."

    let renderUnsafe () =
        try
            Console.Clear()
            let width = Math.Max(20, Console.WindowWidth - 1)

            Console.WriteLine "Jarvis Client"
            Console.WriteLine "============="
            let previous = Console.ForegroundColor
            Console.ForegroundColor <- connectionColor connectionState
            Console.Write(connectionLabel connectionState)
            Console.ForegroundColor <- previous
            Console.WriteLine $" • {serverHost}"
            Console.WriteLine $"Key: {key}"
            match mode with
            | ActivityMode ->
                writeActivityHotkeys()
            | PermissionMode ->
                writePermissionHotkeys()
            | FilterMode ->
                writeFilterHotkeys()
            | CopyRecentMode ->
                writeCopyRecentHotkeys()
            | AgentsMode ->
                writeAgentHotkeys()
            | AgentFilterMode ->
                writeFilterHotkeys()
            | JobsMode | JobFilterMode ->
                writeJobsHotkeys()
            | JobDetailsMode | JobCancelMode ->
                writeJobDetailsHotkeys()
            | SettingsMode | SettingsEditMode | SettingsConfirmMode ->
                Console.WriteLine "Settings: fields, save, defaults, permissions"
            Console.WriteLine ""

            match mode with
            | AgentsMode | AgentFilterMode ->
                renderAgentsUnsafe width
            | JobsMode | JobDetailsMode | JobCancelMode | JobFilterMode ->
                renderJobsUnsafe width
            | SettingsMode | SettingsEditMode | SettingsConfirmMode ->
                renderSettingsUnsafe width
            | _ ->
                ensureActivitySelectionUnsafe() |> ignore
                let filtered, startIndex, endExclusive = activityRangeUnsafe()
                let historySuffix =
                    if filtered.Length = 0 then
                        $" [0 of {activity.Count}]"
                    elif filtered.Length > visibleActivityRows || filtered.Length <> activity.Count then
                        $" [{startIndex + 1}-{endExclusive} of {filtered.Length} / {activity.Count} total]"
                    else
                        ""

                let filterSuffix =
                    let text = if String.IsNullOrWhiteSpace activityFilterText then "" else sprintf " text=\"%s\"" activityFilterText
                    let status = if statusFilter = ActivityFilter.All then "" else $" status={ActivityFilter.statusLabel statusFilter}"
                    if text = "" && status = "" then "" else $" [{text.Trim()}{status}]"

                Console.WriteLine $"Activity{filterSuffix}{historySuffix}"
                Console.WriteLine "--------"

                if activity.Count = 0 then
                    Console.WriteLine "No activity yet."
                elif filtered.Length = 0 then
                    Console.WriteLine "No matching activity."
                else
                    for index = startIndex to endExclusive - 1 do
                        let marker =
                            if mode = ActivityMode && selectedActivityId = Some filtered[index].Id then ">"
                            else " "
                        writeColoredProjectLine width marker filtered[index]

                renderActivityDetailsUnsafe width

                renderPermissionRequestsUnsafe width
        with _ ->
            ()

    // Git-backed inspection can take time. Do not block the terminal or UI lock.
    let startAgentRefreshUnsafe () =
        if not agentRefreshing then
            agentRefreshing <- true
            agentLastPoll <- DateTimeOffset.UtcNow
            let provider = agentAccess
            Task.Run(Action(fun () ->
                let result =
                    try
                        match provider with
                        | Some access -> access.Load()
                        | None -> Error "Agent snapshot provider unavailable."
                    with ex -> Error ex.Message
                lock syncRoot (fun () ->
                    agentRefreshing <- false
                    match result with
                    | Ok snapshot ->
                        agentChanged <-
                            agentSnapshot
                            |> Option.map (fun before -> AgentView.changedAgents before snapshot)
                            |> Option.defaultValue Set.empty
                        agentSnapshot <- Some snapshot
                        agentError <- None
                        normalizeAgentSelectionUnsafe()
                    | Error error ->
                        agentError <- Some error
                    renderUnsafe()))) |> ignore

    let render () = lock syncRoot renderUnsafe

    let completeSelected approval =
        let completion =
            lock syncRoot (fun () ->
                if prompts.Count = 0 then
                    None
                else
                    let index = selectedPrompt |> max 0 |> min (prompts.Count - 1)
                    let prompt = prompts[index]
                    prompts.RemoveAt index
                    selectedPrompt <- selectedPrompt |> min (prompts.Count - 1) |> max 0
                    if prompts.Count = 0 then
                        mode <- permissionReturnMode
                    renderUnsafe()
                    Some prompt.Completion)

        completion |> Option.iter (fun tcs -> tcs.TrySetResult approval |> ignore)

    let resetFilteredViewUnsafe () =
        selectedActivityId <- None
        activityScrollOffset <- 0
        showActivityDetails <- false

    let beginFilterUnsafe prefix =
        filterOriginalText <- activityFilterText

        if not (String.IsNullOrEmpty prefix) then
            let separator =
                if String.IsNullOrWhiteSpace activityFilterText || Char.IsWhiteSpace(activityFilterText[activityFilterText.Length - 1]) then
                    ""
                else
                    " "

            activityFilterText <- activityFilterText + separator + prefix

        mode <- FilterMode
        resetFilteredViewUnsafe()

    let updateFilterTextUnsafe value =
        activityFilterText <- value
        resetFilteredViewUnsafe()

    let cycleStatusFilterUnsafe () =
        statusFilter <- ActivityFilter.nextStatus statusFilter
        resetFilteredViewUnsafe()

    let clearFiltersUnsafe () =
        activityFilterText <- ""
        statusFilter <- ActivityFilter.All
        resetFilteredViewUnsafe()

    let restoredStatus (snapshot: ActivityLog.Snapshot) =
        match snapshot.Status with
        | "Info" -> Informational
        | "Completed" ->
            let duration = if snapshot.DurationMs.HasValue then snapshot.DurationMs.Value else 0L
            Completed(duration, Option.ofObj snapshot.Result)
        | "Failed" ->
            let duration = if snapshot.DurationMs.HasValue then snapshot.DurationMs.Value else 0L
            let shortError = Option.ofObj snapshot.Result |> Option.defaultValue "Failed"
            Failed(duration, shortError)
        | "Interrupted" -> Interrupted
        | _ -> Interrupted

    do
        activityLog
        |> Option.iter (fun store ->
            for snapshot in store.LoadRecent(maxHistory) do
                let entry =
                    { Id = nextActivityId
                      PersistenceId = snapshot.ActivityId
                      Origin = if snapshot.Origin = "Observer" then "Observer" else "Worker"
                      StartedAt = snapshot.StartedAt
                      Timestamp = snapshot.StartedAt.ToString("HH:mm:ss")
                      ProjectName = Option.ofObj snapshot.ProjectName
                      Message = snapshot.Message
                      CommandName = Option.ofObj snapshot.CommandName
                      Reason = Option.ofObj snapshot.Reason
                      Detail = Option.ofObj snapshot.Detail
                      Status = restoredStatus snapshot
                      FailureDetail = Option.ofObj snapshot.Error }

                nextActivityId <- nextActivityId + 1
                activity.Add entry

            if activity.Count > 0 then
                selectedActivityId <- Some activity[activity.Count - 1].Id)

    member _.SetSettingsAccess(access: SettingsUiAccess) =
        lock syncRoot (fun () -> settingsAccess <- Some access)

    member _.SetJobAccess(access: JobUiAccess) =
        lock syncRoot (fun () -> jobsAccess <- Some access)

    member _.SetAgentAccess(access: AgentUiAccess) =
        lock syncRoot (fun () -> agentAccess <- Some access)

    member _.SetKey value =
        lock syncRoot (fun () ->
            key <- value
            renderUnsafe())

    member _.SetConnectionState(state, serverUrl: string) =
        lock syncRoot (fun () ->
            connectionState <- state

            if not (String.IsNullOrWhiteSpace serverUrl) then
                try
                    serverHost <- Uri(serverUrl).Host
                with _ ->
                    serverHost <- serverUrl

            renderUnsafe())

    member _.Log message =
        lock syncRoot (fun () ->
            addActivityUnsafe None (compact message) None Informational |> ignore
            renderUnsafe())

    member _.Message(projectName: string option, message: string) =
        lock syncRoot (fun () ->
            addActivityUnsafe projectName (compact message) None Informational |> ignore
            renderUnsafe())

    member _.StartActivity(command: AgentCommand) =
        lock syncRoot (fun () ->
            let id =
                addActivityUnsafe
                    (AgentCommandInfo.projectName command)
                    (AgentCommandInfo.invocation command)
                    (Some command)
                    Running
            renderUnsafe()
            id)

    member _.MarkActivityAwaitingPermission id =
        lock syncRoot (fun () ->
            tryFindActivityUnsafe id
            |> Option.iter (fun entry ->
                entry.Status <- AwaitingPermission
                persistEntryUnsafe entry)
            renderUnsafe())

    member _.MarkActivityRunning id =
        lock syncRoot (fun () ->
            tryFindActivityUnsafe id
            |> Option.iter (fun entry ->
                entry.Status <- Running
                persistEntryUnsafe entry)
            renderUnsafe())

    member _.CompleteActivity(id, durationMs, outcome) =
        lock syncRoot (fun () ->
            tryFindActivityUnsafe id
            |> Option.iter (fun entry ->
                entry.Status <- Completed(durationMs, outcome)
                persistEntryUnsafe entry)
            renderUnsafe())

    member _.FailActivity(id, durationMs, shortError, fullError) =
        lock syncRoot (fun () ->
            tryFindActivityUnsafe id
            |> Option.iter (fun entry ->
                entry.Status <- Failed(durationMs, shortError)
                entry.FailureDetail <- Some fullError
                persistEntryUnsafe entry)
            renderUnsafe())

    member _.GetActivitySnapshot(projectName: string option, limit: int option, ?includeObservers: bool) =
        lock syncRoot (fun () ->
            let now = DateTimeOffset.Now
            let count = limit |> Option.defaultValue 20 |> max 1 |> min 100
            let projectMatches (entry: ActivityEntry) =
                match projectName with
                | None -> true
                | Some expected ->
                    entry.ProjectName
                    |> Option.exists (fun actual -> String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))

            let entries =
                activity
                |> Seq.filter projectMatches
                |> Seq.filter (fun entry -> defaultArg includeObservers false || entry.Origin <> "Observer")
                |> Seq.rev
                |> Seq.truncate count
                |> Seq.rev
                |> Seq.map (fun entry ->
                    { Origin = entry.Origin
                      StartedAt = entry.StartedAt
                      AgeMs = max 0L (int64 (now - entry.StartedAt).TotalMilliseconds)
                      ProjectName = entry.ProjectName
                      CommandName = entry.CommandName
                      Message = entry.Message
                      Reason = entry.Reason
                      Detail = entry.Detail
                      Status = statusName entry.Status
                      DurationMs = statusDuration entry.Status
                      Result = statusOutcome entry.Status
                      Error = entry.FailureDetail })
                |> Seq.toList

            { Entries = entries })

    member _.PromptPermission command request =
        let tcs = TaskCompletionSource<PermissionApproval>(TaskCreationOptions.RunContinuationsAsynchronously)

        lock syncRoot (fun () ->
            let prompt =
                { Id = nextPromptId
                  Command = command
                  Request = request
                  Completion = tcs }

            nextPromptId <- nextPromptId + 1
            prompts.Add prompt
            selectedPrompt <- prompts.Count - 1
            renderUnsafe())

        tcs.Task

    member _.RequestQuit() =
        shouldQuit <- true

    member _.ShouldQuit = shouldQuit

    member _.RunInputLoop(cancellationToken: CancellationToken) =
        task {
            render()

            while not shouldQuit && not cancellationToken.IsCancellationRequested do
                try
                    if (mode = JobsMode || mode = JobDetailsMode || mode = JobCancelMode || mode = JobFilterMode)
                       && DateTimeOffset.UtcNow - jobLastPoll > TimeSpan.FromMilliseconds(750.) then
                        lock syncRoot (fun () ->
                            pollJobsUnsafe()
                            renderUnsafe())
                    if (mode = AgentsMode || mode = AgentFilterMode)
                       && DateTimeOffset.UtcNow - agentLastPoll > TimeSpan.FromSeconds(45.) then
                        lock syncRoot (fun () -> startAgentRefreshUnsafe())
                    if Console.KeyAvailable then
                        let keyInfo = Console.ReadKey(intercept = true)

                        if mode = SettingsEditMode then
                            lock syncRoot (fun () ->
                                match keyInfo.Key with
                                | ConsoleKey.Enter -> applySettingsEditUnsafe()
                                | ConsoleKey.Escape -> mode <- SettingsMode
                                | ConsoleKey.Backspace when settingsEditing.Length > 0 ->
                                    settingsEditing <- settingsEditing.Substring(0, settingsEditing.Length - 1)
                                | _ when not (Char.IsControl keyInfo.KeyChar) ->
                                    settingsEditing <- settingsEditing + string keyInfo.KeyChar
                                | _ -> ()
                                renderUnsafe())
                        elif mode = SettingsConfirmMode then
                            lock syncRoot (fun () ->
                                match keyInfo.Key with
                                | ConsoleKey.Y -> saveSettingsUnsafe(); mode <- SettingsMode
                                | ConsoleKey.N | ConsoleKey.Escape -> mode <- SettingsMode
                                | _ -> ()
                                renderUnsafe())
                        elif mode = JobFilterMode then
                            lock syncRoot (fun () ->
                                match keyInfo.Key with
                                | ConsoleKey.Enter -> mode <- JobsMode
                                | ConsoleKey.Escape ->
                                    jobProjectFilter <- jobOriginalFilter
                                    mode <- JobsMode
                                | ConsoleKey.Backspace when jobProjectFilter.Length > 0 ->
                                    jobProjectFilter <- jobProjectFilter.Substring(0, jobProjectFilter.Length - 1)
                                | _ when not (Char.IsControl keyInfo.KeyChar) ->
                                    jobProjectFilter <- jobProjectFilter + string keyInfo.KeyChar
                                | _ -> ()
                                let items = jobListUnsafe()
                                jobSelectedId <- items |> List.tryHead |> Option.map _.JobId
                                jobListScroll <- 0
                                renderUnsafe())
                        elif mode = AgentFilterMode then
                            lock syncRoot (fun () ->
                                match keyInfo.Key with
                                | ConsoleKey.Enter -> mode <- AgentsMode; normalizeAgentSelectionUnsafe()
                                | ConsoleKey.Escape ->
                                    agentFilter <- agentOriginalFilter
                                    mode <- AgentsMode
                                    normalizeAgentSelectionUnsafe()
                                | ConsoleKey.Backspace when agentFilter.Length > 0 ->
                                    agentFilter <- agentFilter.Substring(0, agentFilter.Length - 1)
                                    normalizeAgentSelectionUnsafe()
                                | _ when not (Char.IsControl keyInfo.KeyChar) ->
                                    agentFilter <- agentFilter + string keyInfo.KeyChar
                                    normalizeAgentSelectionUnsafe()
                                | _ -> ()
                                renderUnsafe())
                        elif mode = JobCancelMode then
                            lock syncRoot (fun () ->
                                match keyInfo.Key with
                                | ConsoleKey.Y -> confirmJobCancellationUnsafe()
                                | ConsoleKey.N | ConsoleKey.Escape -> mode <- jobCancelReturnMode
                                | _ -> ()
                                renderUnsafe())
                        elif mode = CopyRecentMode then
                            match keyInfo.Key with
                            | ConsoleKey.Backspace ->
                                lock syncRoot (fun () ->
                                    copyRecentCountEdited <- true
                                    if copyRecentCountText.Length > 0 then
                                        copyRecentCountText <- copyRecentCountText.Substring(0, copyRecentCountText.Length - 1)
                                    renderUnsafe())
                            | ConsoleKey.Enter ->
                                let request =
                                    lock syncRoot (fun () ->
                                        match Int32.TryParse copyRecentCountText with
                                        | true, requested when requested > 0 ->
                                            let count = min maxHistory requested
                                            let actual = recentCommandCountUnsafe count
                                            let text =
                                                if actual = 0 then None
                                                else Some(recentCommandExportUnsafe count)
                                            mode <- ActivityMode
                                            renderUnsafe()
                                            Some(actual, text)
                                        | _ -> None)

                                match request with
                                | Some(0, _) ->
                                    lock syncRoot (fun () ->
                                        addActivityUnsafe None "No command activity to copy." None Informational |> ignore
                                        renderUnsafe())
                                | Some(actual, Some text) ->
                                    let result = Clipboard.copyText text
                                    lock syncRoot (fun () ->
                                        let message =
                                            match result with
                                            | Ok provider -> $"Copied {actual} recent commands to clipboard via {provider}."
                                            | Error error -> $"Clipboard copy failed: {error}"
                                        addActivityUnsafe None message None Informational |> ignore
                                        renderUnsafe())
                                | _ -> ()
                            | ConsoleKey.Escape ->
                                lock syncRoot (fun () ->
                                    mode <- ActivityMode
                                    renderUnsafe())
                            | _ when Char.IsDigit keyInfo.KeyChar ->
                                lock syncRoot (fun () ->
                                    let digit = string keyInfo.KeyChar
                                    if not copyRecentCountEdited then
                                        copyRecentCountText <- digit
                                        copyRecentCountEdited <- true
                                    elif copyRecentCountText.Length < 3 then
                                        copyRecentCountText <- copyRecentCountText + digit
                                    renderUnsafe())
                            | _ -> ()
                        elif mode = FilterMode then
                            match keyInfo.Key with
                            | ConsoleKey.Backspace ->
                                lock syncRoot (fun () ->
                                    if activityFilterText.Length > 0 then
                                        updateFilterTextUnsafe (activityFilterText.Substring(0, activityFilterText.Length - 1))
                                    renderUnsafe())
                            | ConsoleKey.Enter ->
                                lock syncRoot (fun () ->
                                    mode <- ActivityMode
                                    renderUnsafe())
                            | ConsoleKey.Escape ->
                                lock syncRoot (fun () ->
                                    activityFilterText <- filterOriginalText
                                    mode <- ActivityMode
                                    resetFilteredViewUnsafe()
                                    renderUnsafe())
                            | _ when not (Char.IsControl keyInfo.KeyChar) ->
                                lock syncRoot (fun () ->
                                    updateFilterTextUnsafe (activityFilterText + string keyInfo.KeyChar)
                                    renderUnsafe())
                            | _ -> ()
                        else
                            match keyInfo.KeyChar, keyInfo.Key with
                            | '/', _ when mode = ActivityMode ->
                                lock syncRoot (fun () ->
                                    beginFilterUnsafe ""
                                    renderUnsafe())
                            | '@', _ when mode = ActivityMode ->
                                lock syncRoot (fun () ->
                                    beginFilterUnsafe "@"
                                    renderUnsafe())
                            | _, ConsoleKey.UpArrow ->
                                lock syncRoot (fun () ->
                                    match mode with
                                    | ActivityMode -> moveActivitySelectionUnsafe -1
                                    | PermissionMode ->
                                        selectedPrompt <- max 0 (selectedPrompt - 1)
                                    | SettingsMode -> settingsSelected <- max 0 (settingsSelected - 1)
                                    | AgentsMode -> moveAgentSelectionUnsafe -1
                                    | JobsMode -> moveJobSelectionUnsafe -1
                                    | JobDetailsMode ->
                                        jobFollow <- false
                                        jobOutputScroll <- min (jobOutputLines.Length - 1 |> max 0) (jobOutputScroll + 1)
                                    | _ -> ()
                                    renderUnsafe())
                            | _, ConsoleKey.DownArrow ->
                                lock syncRoot (fun () ->
                                    match mode with
                                    | ActivityMode -> moveActivitySelectionUnsafe 1
                                    | PermissionMode ->
                                        selectedPrompt <- min (prompts.Count - 1) (selectedPrompt + 1)
                                        selectedPrompt <- max 0 selectedPrompt
                                    | SettingsMode -> settingsSelected <- min (settingKeys.Length - 1) (settingsSelected + 1)
                                    | AgentsMode -> moveAgentSelectionUnsafe 1
                                    | JobsMode -> moveJobSelectionUnsafe 1
                                    | JobDetailsMode ->
                                        jobOutputScroll <- max 0 (jobOutputScroll - 1)
                                        if jobOutputScroll = 0 then jobFollow <- true
                                    | _ -> ()
                                    renderUnsafe())
                            | _, ConsoleKey.PageUp ->
                                lock syncRoot (fun () ->
                                    match mode with
                                    | ActivityMode -> moveActivitySelectionUnsafe -visibleActivityRows
                                    | AgentsMode -> moveAgentSelectionUnsafe -visibleActivityRows
                                    | JobsMode -> moveJobSelectionUnsafe -12
                                    | JobDetailsMode ->
                                        jobFollow <- false
                                        jobOutputScroll <- min (jobOutputLines.Length - 1 |> max 0) (jobOutputScroll + 10)
                                    | _ -> ()
                                    renderUnsafe())
                            | _, ConsoleKey.PageDown ->
                                lock syncRoot (fun () ->
                                    match mode with
                                    | ActivityMode -> moveActivitySelectionUnsafe visibleActivityRows
                                    | AgentsMode -> moveAgentSelectionUnsafe visibleActivityRows
                                    | JobsMode -> moveJobSelectionUnsafe 12
                                    | JobDetailsMode ->
                                        jobOutputScroll <- max 0 (jobOutputScroll - 10)
                                        if jobOutputScroll = 0 then jobFollow <- true
                                    | _ -> ()
                                    renderUnsafe())
                            | _, ConsoleKey.End ->
                                lock syncRoot (fun () ->
                                    let filtered = filteredActivityUnsafe()
                                    if mode = ActivityMode && filtered.Length > 0 then
                                        selectActivityIndexUnsafe (filtered.Length - 1)
                                    elif mode = AgentsMode then
                                        selectAgentIndexUnsafe ((agentListUnsafe()).Length - 1)
                                    renderUnsafe())
                            | _, ConsoleKey.Enter ->
                                lock syncRoot (fun () ->
                                    if mode = ActivityMode && (filteredActivityUnsafe()).Length > 0 then
                                        showActivityDetails <- not showActivityDetails
                                    elif mode = AgentsMode && agentSelected.IsSome then showAgentDetails <- not showAgentDetails
                                    elif mode = JobsMode then openJobDetailsUnsafe()
                                    elif mode = SettingsMode then
                                        settingsEditing <- settingsValue settingKeys[settingsSelected] settingsStaged
                                                           |> Option.defaultValue ""
                                        settingsError <- None
                                        mode <- SettingsEditMode
                                    renderUnsafe())
                            | _, ConsoleKey.G when mode = ActivityMode || mode = JobsMode || mode = AgentsMode ->
                                lock syncRoot (fun () ->
                                    openSettingsUnsafe()
                                    renderUnsafe())
                            | _, ConsoleKey.U when mode = SettingsMode ->
                                lock syncRoot (fun () ->
                                    match ClientConfig.unsetKey settingKeys[settingsSelected] settingsStaged with
                                    | Ok stored -> settingsStaged <- stored; settingsError <- None
                                    | Error error -> settingsError <- Some error
                                    renderUnsafe())
                            | _, ConsoleKey.S when mode = SettingsMode ->
                                lock syncRoot (fun () ->
                                    let original = settingsSnapshot |> Option.map (fun snap -> snap.Stored.TrustLevel)
                                    if settingsStaged.TrustLevel = Some "full" && original <> Some (Some "full") then
                                        mode <- SettingsConfirmMode
                                    else saveSettingsUnsafe()
                                    renderUnsafe())
                            | _, ConsoleKey.Escape when mode = SettingsMode ->
                                lock syncRoot (fun () ->
                                    mode <- settingsReturnMode
                                    renderUnsafe())
                            | _, ConsoleKey.A when mode = ActivityMode || mode = JobsMode ->
                                lock syncRoot (fun () ->
                                    mode <- AgentsMode
                                    normalizeAgentSelectionUnsafe()
                                    startAgentRefreshUnsafe()
                                    renderUnsafe())
                            | ('/', _) | (_, ConsoleKey.F) when mode = AgentsMode ->
                                lock syncRoot (fun () ->
                                    agentOriginalFilter <- agentFilter
                                    mode <- AgentFilterMode
                                    renderUnsafe())
                            | '@', _ when mode = AgentsMode ->
                                lock syncRoot (fun () ->
                                    agentOriginalFilter <- agentFilter
                                    agentFilter <- "@"
                                    normalizeAgentSelectionUnsafe()
                                    mode <- AgentFilterMode
                                    renderUnsafe())
                            | _, ConsoleKey.R when mode = AgentsMode ->
                                lock syncRoot (fun () ->
                                    startAgentRefreshUnsafe()
                                    renderUnsafe())
                            | _, ConsoleKey.Escape when mode = AgentsMode ->
                                lock syncRoot (fun () -> mode <- ActivityMode; renderUnsafe())
                            | _, ConsoleKey.J when mode = ActivityMode ->
                                lock syncRoot (fun () ->
                                    mode <- JobsMode
                                    pollJobsUnsafe()
                                    renderUnsafe())
                            | _, ConsoleKey.F when mode = JobsMode ->
                                lock syncRoot (fun () ->
                                    jobOriginalFilter <- jobProjectFilter
                                    mode <- JobFilterMode
                                    renderUnsafe())
                            | _, ConsoleKey.T when mode = JobsMode ->
                                lock syncRoot (fun () ->
                                    jobIncludeCompleted <- not jobIncludeCompleted
                                    let items = jobListUnsafe()
                                    if not (items |> List.exists (fun job -> Some job.JobId = jobSelectedId)) then
                                        jobSelectedId <- items |> List.tryHead |> Option.map _.JobId
                                    renderUnsafe())
                            | _, ConsoleKey.B when mode = JobDetailsMode ->
                                lock syncRoot (fun () ->
                                    jobFollow <- true
                                    jobOutputScroll <- 0
                                    renderUnsafe())
                            | _, ConsoleKey.C when mode = JobsMode || mode = JobDetailsMode ->
                                lock syncRoot (fun () ->
                                    beginJobCancellationUnsafe()
                                    renderUnsafe())
                            | _, ConsoleKey.Escape when mode = JobDetailsMode ->
                                lock syncRoot (fun () ->
                                    mode <- JobsMode
                                    jobDetailId <- None
                                    renderUnsafe())
                            | _, ConsoleKey.Escape when mode = JobsMode ->
                                lock syncRoot (fun () ->
                                    mode <- ActivityMode
                                    renderUnsafe())
                            | _, ConsoleKey.P when mode = ActivityMode || mode = AgentsMode || mode = JobsMode || mode = JobDetailsMode || mode = SettingsMode ->
                                lock syncRoot (fun () ->
                                    permissionReturnMode <- mode
                                    mode <- PermissionMode
                                    showActivityDetails <- false
                                    renderUnsafe())
                            | _, ConsoleKey.Escape when mode = PermissionMode ->
                                lock syncRoot (fun () ->
                                    mode <- permissionReturnMode
                                    renderUnsafe())
                            | _, ConsoleKey.A when mode = PermissionMode ->
                                completeSelected AllowOnce
                            | _, ConsoleKey.S when mode = PermissionMode ->
                                completeSelected AllowExactForSession
                            | _, ConsoleKey.S when mode = ActivityMode ->
                                lock syncRoot (fun () ->
                                    cycleStatusFilterUnsafe()
                                    renderUnsafe())
                            | _, ConsoleKey.C when mode = ActivityMode ->
                                lock syncRoot (fun () ->
                                    clearFiltersUnsafe()
                                    renderUnsafe())
                            | _, ConsoleKey.R when mode = ActivityMode ->
                                lock syncRoot (fun () ->
                                    copyRecentCountText <- "20"
                                    copyRecentCountEdited <- false
                                    mode <- CopyRecentMode
                                    showActivityDetails <- false
                                    renderUnsafe())
                            | _, ConsoleKey.E when mode = PermissionMode ->
                                completeSelected AllowExecutableForSession
                            | _, ConsoleKey.D when mode = PermissionMode ->
                                completeSelected Deny
                            | _, ConsoleKey.Q -> shouldQuit <- true
                            | _ -> ()
                    else
                        do! Task.Delay(50, cancellationToken)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    lock syncRoot (fun () ->
                        addActivityUnsafe None $"TUI error: {compact ex.Message}" None Informational |> ignore
                        renderUnsafe())
                    do! Task.Delay(250, cancellationToken)
        }
