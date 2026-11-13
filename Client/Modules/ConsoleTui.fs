module Client.ConsoleTui

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Common
open Client
open Client.TerminalMouse
open Client.TerminalLayout
open Client.TerminalFrame

type private MouseTarget =
    | SwitchActivity
    | SwitchAgents
    | SwitchJobs
    | SwitchSettings
    | SelectAgent of string
    | SelectActivity of int
    | SelectJob of string
    | SelectPermission of int
    | SelectSetting of int

type private MouseHit =
    { Rect: Rect; Target: MouseTarget }

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
    let agentSeen = Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
    let persistedAgentSeen = Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
    do
        activityLog |> Option.iter (fun store ->
            for key, timestamp in store.LoadAgentSeen() do
                agentSeen[key] <- timestamp
                persistedAgentSeen[key] <- timestamp)
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
    let mutable agentProjectPage = 0
    let mutable showAgentDetails = true
    let mouseHits = ResizeArray<MouseHit>()
    let mutable lastMouseClick: (MouseTarget * DateTimeOffset) option = None
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
    let mutable draw = Frame(80, 24)
    let mutable previousFrame: Frame option = None
    let mutable frameSize = (0, 0)
    let mutable lastRenderError: string option = None
    let mutable activityPageRows = 1
    let mutable agentPageRows = 1
    let mutable jobPageRows = 1
    let mutable jobOutputPageRows = 1

    // Navigation uses the capacity recorded for the most recently displayed
    // frame. Rendering computes that capacity from actual header and footer space.
    let activityViewportRows () = activityPageRows
    let agentViewportRows () = agentPageRows
    let jobViewportRows () = jobPageRows

    let registerMouseHit x width target =
        if width > 0 then
            try
                let y = draw.CursorTop
                if y >= 0 && y < draw.Height then
                    mouseHits.Add({ Rect = { X = x; Y = y; Width = width; Height = 1 }
                                    Target = target })
            with _ -> ()

    let currentModeIs tab =
        match tab, mode with
        | SwitchActivity, (ActivityMode | FilterMode | CopyRecentMode | PermissionMode) -> true
        | SwitchAgents, (AgentsMode | AgentFilterMode) -> true
        | SwitchJobs, (JobsMode | JobDetailsMode | JobCancelMode | JobFilterMode) -> true
        | SwitchSettings, (SettingsMode | SettingsEditMode | SettingsConfirmMode) -> true
        | _ -> false

    let writeNavigation () =
        for label, target in
            [ "Activity", SwitchActivity; "Agents", SwitchAgents
              "Jobs", SwitchJobs; "Settings", SwitchSettings ] do
            let x = draw.CursorLeft
            let text = if currentModeIs target then $"[{label}] " else $" {label}  "
            registerMouseHit x text.Length target
            draw.Write text
        draw.WriteLine()

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
        max 0 ((filteredActivityUnsafe()).Length - activityViewportRows())

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
                    (activityViewportRows())
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
        | None -> draw.WriteLine rendered
        | Some projectName ->
            let token = ActivityFilter.projectToken projectName
            let index = rendered.IndexOf(token, StringComparison.Ordinal)

            if index < 0 then
                draw.WriteLine rendered
            else
                let previous = draw.ForegroundColor
                draw.Write(rendered.Substring(0, index))
                draw.ForegroundColor <- projectColor projectName
                draw.Write(token)
                draw.ForegroundColor <- previous
                draw.WriteLine(rendered.Substring(index + token.Length))

    let writeColoredProject projectName =
        let previous = draw.ForegroundColor
        draw.ForegroundColor <- projectColor projectName
        draw.Write(ActivityFilter.projectToken projectName)
        draw.ForegroundColor <- previous

    let hotkeyColor = ConsoleColor.Cyan

    let writeHotkeyToken (token: string) =
        let previous = draw.ForegroundColor
        draw.ForegroundColor <- hotkeyColor
        draw.Write token
        draw.ForegroundColor <- previous

    let writeMnemonic hotkey (label: string) =
        let hotkeyText = string hotkey
        let index = label.IndexOf(hotkeyText, StringComparison.OrdinalIgnoreCase)

        if index < 0 then
            draw.Write label
        else
            draw.Write(label.Substring(0, index))
            writeHotkeyToken (label.Substring(index, 1))
            draw.Write(label.Substring(index + 1))

    let writeActivityHotkeys () =
        draw.Write "Keys: "
        writeHotkeyToken "↑/↓"; draw.Write " activity, "
        writeHotkeyToken "PgUp/PgDn"; draw.Write " page, "
        writeHotkeyToken "End"; draw.Write " latest, "
        writeHotkeyToken "Enter"; draw.Write " details, "
        writeHotkeyToken "/"; draw.Write " filter, "
        writeHotkeyToken "@"; draw.Write " project, "
        writeMnemonic 's' "status"; draw.Write ", "
        writeMnemonic 'c' "clear"; draw.Write ", "
        writeMnemonic 'p' "permissions"; draw.Write ", "
        writeMnemonic 'j' "jobs"; draw.Write ", "
        writeMnemonic 'a' "agents"; draw.Write ", "
        writeMnemonic 'g' "settings"; draw.Write ", "
        writeMnemonic 'r' "copy recent"; draw.Write ", "
        writeMnemonic 'o' (if showObservers then "hide observers" else "show observers"); draw.Write ", "
        writeMnemonic 'q' "quit"; draw.WriteLine ""

    let writePermissionHotkeys () =
        draw.Write "Keys: "
        writeHotkeyToken "↑/↓"; draw.Write " permission, "
        writeMnemonic 'a' "allow once"; draw.Write ", "
        writeMnemonic 's' "allow exact for session"; draw.Write ", "
        writeMnemonic 'e' "executable"; draw.Write ", "
        writeMnemonic 'd' "deny"; draw.Write ", "
        writeHotkeyToken "Esc"; draw.Write " activity, "
        writeMnemonic 'q' "quit"; draw.WriteLine ""

    let writeFilterHotkeys () =
        draw.Write "Filter: type text; "
        writeHotkeyToken "@"; draw.Write "term filters project, "
        writeHotkeyToken "Backspace"; draw.Write " edits, "
        writeHotkeyToken "Enter"; draw.Write " applies, "
        writeHotkeyToken "Esc"; draw.WriteLine " cancels"

    let writeCopyRecentHotkeys () =
        draw.Write $"Copy recent commands: {copyRecentCountText}"
        draw.Write "  "
        writeHotkeyToken "Enter"; draw.Write " copies, "
        writeHotkeyToken "Esc"; draw.WriteLine " cancels"

    let activityRangeUnsafe () =
        let filtered = filteredActivityUnsafe()
        clampScrollOffsetUnsafe()
        let endExclusive = filtered.Length - activityScrollOffset
        let start = max 0 (endExclusive - activityViewportRows())
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
        | [] -> draw.WriteLine($"{label,-11}")
        | first :: rest ->
            draw.WriteLine($"{label,-11}{first}")
            let indent = String.replicate labelWidth " "
            for line in rest do
                draw.WriteLine($"{indent}{line}")

    let renderActivityDetailsUnsafe width =
        if showActivityDetails then
            match selectedActivityUnsafe() with
            | None -> ()
            | Some entry ->
                draw.WriteLine ""
                draw.WriteLine "Activity details"
                draw.WriteLine "----------------"
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

    let measureRows (renderContent: unit -> unit) =
        let original = draw
        try
            draw <- Frame(original.Width, original.Height)
            renderContent()
            draw.CursorTop
        finally draw <- original

    let renderPermissionRequestsUnsafe width =
        draw.WriteLine ""
        let permissionSuffix =
            if prompts.Count = 0 then ""
            else $" ({prompts.Count})"
        draw.WriteLine $"Permission requests{permissionSuffix}"
        draw.WriteLine "-------------------"
        if prompts.Count = 0 then
            draw.WriteLine "No pending permission requests."
        else
            // Each request takes 2-3 rows; leave room for a footer and never
            // render off-screen requests as if they could be selected.
            let available = max 0 (draw.Height - draw.CursorTop - 2)
            let capacity = min prompts.Count (available / 3)
            let start =
                if mode = PermissionMode then
                    max 0 (min (prompts.Count - capacity) (selectedPrompt - capacity + 1))
                else 0
            for index = start to start + capacity - 1 do
                let prompt = prompts[index]
                let marker =
                    if mode = PermissionMode && index = selectedPrompt then ">"
                    else " "
                registerMouseHit 0 width (SelectPermission index)
                draw.Write $"{marker} #{prompt.Id} {prompt.Request.CommandName}"
                match prompt.Request.ProjectName with
                | Some projectName ->
                    draw.Write " "
                    writeColoredProject projectName
                | None -> ()
                draw.WriteLine ""
                draw.WriteLine $"    {trim prompt.Request.Summary (Math.Max(20, width - 4))}"
                AgentCommandInfo.reason prompt.Command
                |> Option.iter (fun reason ->
                    draw.WriteLine $"    Reason: {trim reason (Math.Max(20, width - 12))}")
        draw.WriteLine ""

    // Ordinary list screens use a small clickable preview. The full request
    // list, summaries and reasons remain in the paged Permission screen.
    let renderPermissionPreviewUnsafe width =
        if prompts.Count > 0 then
            draw.WriteLine ""
            draw.WriteLine $"Permission requests ({prompts.Count}; p to review)"
            let first = prompts[0]
            registerMouseHit 0 width (SelectPermission 0)
            draw.WriteLine(trim $"  #{first.Id} {first.Request.CommandName}" width)

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
            let maxRows = jobViewportRows()
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
        draw.WriteLine "Keys: Up/Down select, PgUp/PgDn page, Enter output, t toggle completed,"
        draw.WriteLine "      f filter project, c cancel, p permissions, Esc activity, q quit"

    let writeJobDetailsHotkeys () =
        draw.WriteLine "Keys: Up/Down/PgUp/PgDn scroll output, b follow/bottom, c cancel,"
        draw.WriteLine "      p permissions, Esc jobs, q quit"

    let renderJobsUnsafe width =
        let items = jobListUnsafe() |> List.toArray
        let running = jobSummaries |> List.filter JobView.isRunning |> List.length
        let finished = jobSummaries.Length - running
        let safe value = JobView.safeLine value
        draw.WriteLine $"Jobs: {running} running, {finished} recent completed"
        let filterLabel = if jobProjectFilter = "" then "(all)" else safe jobProjectFilter
        let completedLabel = if jobIncludeCompleted then "shown" else "hidden"
        draw.WriteLine $"Project filter: {filterLabel} | Finished: {completedLabel}"
        jobError |> Option.iter (fun error -> draw.WriteLine($"Error: {trim (safe error) (width - 7)}"))
        match mode with
        | JobsMode | JobFilterMode ->
            if mode = JobFilterMode then draw.WriteLine $"Filter project: {safe jobProjectFilter} (Enter apply, Esc revert)"
            jobPageRows <-
                TerminalLayout.availableRows draw.Height draw.CursorTop
                    (if prompts.Count > 0 then 2 else 1)
            if items.Length = 0 then draw.WriteLine "No matching jobs."
            else
                let selected = items |> Array.tryFindIndex (fun job -> Some job.JobId = jobSelectedId) |> Option.defaultValue 0
                let maxRows = jobViewportRows()
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
                    registerMouseHit 0 width (SelectJob job.JobId)
                    draw.Write(marker + " ")
                    let previous = draw.ForegroundColor
                    draw.ForegroundColor <- projectColor job.ProjectName
                    draw.Write(safe (ActivityFilter.projectToken job.ProjectName))
                    draw.ForegroundColor <- previous
                    draw.WriteLine (" " + trim (safe ($"{status} | {elapsed} | {reason}")) (max 15 (width - (ActivityFilter.projectToken job.ProjectName).Length - 3)))
        | JobDetailsMode | JobCancelMode ->
            match jobDetailId |> Option.bind (fun id -> jobSummaries |> List.tryFind (fun job -> job.JobId = id)) with
            | None -> draw.WriteLine "Job no longer retained by the client."
            | Some job ->
                let field name value = draw.WriteLine(trim (safe ($"{name}: {value}")) width)
                field "Id" job.JobId
                field "Project" job.ProjectName
                if draw.Height >= 22 then
                    field "Reason" (job.Reason |> Option.defaultValue "(none)")
                    field "Command" (String.concat " " (job.Executable :: job.Args))
                    field "Directory" (job.WorkingDirectory |> Option.defaultValue "(project root)")
                    field "Started" (job.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                    field "Finished" (job.CompletedAt |> Option.map (fun value -> value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")) |> Option.defaultValue "-")
                    field "Duration" (JobView.duration DateTimeOffset.UtcNow job)
                field "Status" (JobView.statusText job.Status)
                if jobOutputTruncated then draw.WriteLine "Output capture truncated at the job's byte limit."
                draw.WriteLine (if jobFollow then "Output (following)" else "Output (scroll paused; b to follow)")
                draw.WriteLine "---------------------"
                let lines = jobOutputLines |> List.toArray
                jobOutputPageRows <-
                    TerminalLayout.availableRows draw.Height draw.CursorTop
                        (if mode = JobCancelMode then 5 else 2)
                let rows = jobOutputPageRows
                jobOutputScroll <- max 0 (min jobOutputScroll (max 0 (lines.Length - rows)))
                let finish = max 0 (lines.Length - jobOutputScroll)
                let start = max 0 (finish - rows)
                for i = start to finish - 1 do
                    let line = lines[i]
                    let prefix = if line.Stream = JobOutputStream.StdErr then "ERR " else "OUT "
                    draw.WriteLine(trim (prefix + line.Text) width)
                if lines.Length = 0 then draw.WriteLine "(no output captured)"
                if jobOutputScroll > 0 then draw.WriteLine $"({jobOutputScroll} lines below)"
                if mode = JobCancelMode then
                    draw.WriteLine ""
                    draw.WriteLine $"Cancel running job {job.JobId} for {safe job.ProjectName}?"
                    draw.WriteLine "y confirms; n/Esc returns."
        | _ -> ()
        draw.WriteLine ""
        if prompts.Count > 0 then draw.WriteLine $"{prompts.Count} pending permission request(s): press p to review."

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
            let viewport = agentViewportRows()
            agentSelected <- Some items[bounded].Key
            if bounded < agentScroll then agentScroll <- bounded
            elif bounded >= agentScroll + viewport then
                agentScroll <- bounded - viewport + 1

    let moveAgentSelectionUnsafe delta =
        let items = agentListUnsafe()
        if items.Length > 0 then
            let index =
                items |> Array.tryFindIndex (fun row -> Some row.Key = agentSelected)
                |> Option.defaultValue 0
            selectAgentIndexUnsafe (index + delta)

    let writeAgentHotkeys () =
        draw.Write "Keys: "
        writeHotkeyToken "↑/↓"; draw.Write " agent, "
        writeHotkeyToken "PgUp/PgDn"; draw.Write " page, "
        writeHotkeyToken "End"; draw.Write " last, "
        writeHotkeyToken "Enter"; draw.Write " details, "
        writeHotkeyToken "/"; draw.Write " filter, "
        writeHotkeyToken "@"; draw.Write " project, "
        writeMnemonic 'r' "refresh"; draw.Write ", "
        writeMnemonic 'p' "permissions"; draw.Write ", "
        writeHotkeyToken "Esc"; draw.Write " activity, "
        writeMnemonic 'q' "quit"; draw.WriteLine "  • mouse: click/select, double-click/details, wheel/scroll"

    let agentColumn value width =
        if width <= 0 then ""
        else (trim (compact value) width).PadRight(width)

    let writeAgentTableHeader width projectWidth agentWidth =
        let header =
            "   Age  " + agentColumn "Project" projectWidth + " "
            + agentColumn "Agent" agentWidth + " "
            + agentColumn "Mail" 5 + " "
            + agentColumn "State" 10 + " " + agentColumn "Seen" 9 + " Task"
        let previous = draw.ForegroundColor
        draw.ForegroundColor <- ConsoleColor.DarkGray
        draw.WriteLine(trim header width)
        draw.ForegroundColor <- previous

    let seenAt (row: AgentView.AgentRow) = AgentView.lastSeenAt row agentSeen

    let writeColoredAgentLine width projectWidth agentWidth marker lastSeen (row: AgentView.AgentRow) =
        let original = draw.ForegroundColor
        let originalBackgroundRgb = draw.BackgroundRgb
        let selected = mode = AgentsMode && Some row.Key = agentSelected
        let normalForeground = if selected then ConsoleColor.White else original
        draw.BackgroundRgb <- if selected then Some(38uy, 38uy, 38uy) else Some(16uy, 16uy, 16uy)
        draw.ForegroundColor <- normalForeground
        let timestamp = AgentView.stateAge DateTimeOffset.UtcNow row
        let flag = if Set.contains row.Key agentChanged then "*" else " "
        let mail = if row.PendingNotifications = 0 then "" else $"[{row.PendingNotifications}]"
        let state = AgentView.displayState row.State
        let task =
            row.Focus
            |> Option.map (fun focus -> $"#{focus.TaskId} {focus.Title}")
            |> Option.defaultValue ""
        let writeCell value cellWidth color =
            draw.ForegroundColor <- color
            draw.Write(agentColumn value cellWidth)
        // Two selection/change markers and a space form the leftmost gutter.
        draw.Write(marker + flag + " ")
        writeCell timestamp 4 normalForeground
        draw.Write(" ")
        let projectForeground =
            match projectColor row.Project, selected with
            | ConsoleColor.Blue, true -> ConsoleColor.Cyan
            | ConsoleColor.DarkCyan, true -> ConsoleColor.Cyan
            | ConsoleColor.DarkGreen, true -> ConsoleColor.Green
            | ConsoleColor.DarkMagenta, true -> ConsoleColor.Magenta
            | color, _ -> color
        writeCell (AgentView.projectColumnLabel projectWidth row) projectWidth projectForeground
        draw.ForegroundColor <- normalForeground
        draw.Write(" ")
        writeCell row.Agent agentWidth normalForeground
        draw.Write(" ")
        writeCell mail 5 (if row.PendingNotifications > 0 then ConsoleColor.Yellow else normalForeground)
        draw.ForegroundColor <- normalForeground
        draw.Write(" ")
        writeCell state 10 (if selected && row.State = "Completed" then ConsoleColor.Cyan else AgentView.stateColor row.State)
        draw.ForegroundColor <- normalForeground
        draw.Write(" ")
        writeCell (AgentView.relativeAge DateTimeOffset.UtcNow lastSeen) 9 normalForeground
        draw.Write(" ")
        let remaining = max 0 (width - draw.CursorLeft)
        draw.Write(agentColumn task remaining)
        // Fill the row so the selection highlight spans the available width
        // even for agents with no task.
        let padding = max 0 (width - draw.CursorLeft)
        if padding > 0 then draw.Write(String(' ', padding))
        draw.WriteLine()
        draw.ForegroundColor <- original
        draw.BackgroundRgb <- originalBackgroundRgb

    let renderAgentDetailsUnsafe width =
        if showAgentDetails then
            match selectedAgentUnsafe () with
            | None -> ()
            | Some row when draw.Height < 32 ->
                // Preserve a useful selected-agent context without letting the
                // details pane consume the table on an ordinary 24-row terminal.
                let task = row.Focus |> Option.map (fun t -> "#" + t.TaskId + " " + t.Title) |> Option.defaultValue ""
                draw.WriteLine ""
                draw.WriteLine (trim (sprintf "Details: %s | %s | %s" row.Agent (AgentView.displayState row.State) task) width)
            | Some row ->
                draw.WriteLine ""
                draw.WriteLine "Agent details"
                draw.WriteLine "-------------"
                writeDetailField width "Agent" row.Agent
                writeDetailField width "Project" (ActivityFilter.projectToken row.ProjectLabel)
                writeDetailField width "State" (AgentView.displayState row.State)
                seenAt row
                |> Option.iter (fun seen -> writeDetailField width "Last seen" (seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
                if row.PendingNotifications > 0 then
                    writeDetailField width "Unread notifications" (string row.PendingNotifications)
                    row.LatestNotification |> Option.iter (writeDetailField width "Latest message")
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
                draw.WriteLine ""
                draw.WriteLine "Status reflects the canonical remote orchestration ledger, not live chat activity."

    let renderAgentsUnsafe width =
        normalizeAgentSelectionUnsafe()
        let filtered = agentListUnsafe()
        let all = agentSnapshot |> Option.map AgentView.rows |> Option.defaultValue []
        let totals =
            agentSnapshot |> Option.map AgentView.totalCounts
            |> Option.defaultWith (fun () -> AgentView.projectCounts [] 0)
        let unavailable =
            agentSnapshot
            |> Option.map (fun snap -> snap.Projects |> List.sumBy (fun p -> if p.Counts.IsNone then 1 else 0))
            |> Option.defaultValue 0
        let stale =
            agentSnapshot
            |> Option.map (fun snap -> snap.Projects |> List.sumBy (fun p ->
                if p.Status = AgentView.Stale then 1 else 0))
            |> Option.defaultValue 0
        let projects =
            agentSnapshot
            |> Option.map (AgentView.visibleProjects agentFilter)
            |> Option.defaultValue []
            |> List.toArray
        let projectItems =
            projects |> Array.map (fun project ->
                let name =
                    agentSnapshot
                    |> Option.map (fun snap -> AgentView.projectDisplayName snap project)
                    |> Option.defaultValue project.Project
                project, AgentView.projectStatusLines width name project)
        let projectPageSize =
            if draw.Height < 32 || width < 55 then 1
            else max 1 (min 5 (draw.Height / 14))
        let projectPageCount = max 1 ((projects.Length + projectPageSize - 1) / projectPageSize)
        agentProjectPage <- max 0 (min agentProjectPage (projectPageCount - 1))
        let projectFirst = agentProjectPage * projectPageSize
        let projectLast = min projects.Length (projectFirst + projectPageSize)
        let projectHeaderRows =
            if projects.Length = 0 then 0
            else
                1 + (projectItems
                     |> Array.skip projectFirst
                     |> Array.take (projectLast - projectFirst)
                     |> Array.sumBy (fun (_, lines) -> lines.Length))
        let filterSuffix =
            if String.IsNullOrWhiteSpace agentFilter then ""
            else $" [text=\"{trim (compact agentFilter) (max 20 (width - 22))}\"]"
        let selected = filtered |> Array.tryFindIndex (fun row -> Some row.Key = agentSelected) |> Option.defaultValue 0
        let issues =
            agentSnapshot
            |> Option.map (fun snap ->
                (snap.Projects |> List.choose (fun project ->
                    project.Error |> Option.map (fun error -> project.Project + ": " + error)))
                @ snap.Issues)
            |> Option.defaultValue []
        let headerRows =
            if draw.Height >= 22 then 4 + (if agentChanged.IsEmpty then 0 else 1)
            else 2
        let headerRows = headerRows + projectHeaderRows + 1 + (if agentError.IsSome then 1 else 0)
                                    + (if mode = AgentFilterMode then 1 else 0)
        let inlineDetails =
            if showAgentDetails then
                measureRows (fun () -> renderAgentDetailsUnsafe width)
            else 0
        let footerRows =
            1 + inlineDetails
            + (if issues.IsEmpty then 0 else 3 + min 5 issues.Length)
            + (if prompts.Count = 0 then 0 else 3)
        agentPageRows <-
            TerminalLayout.availableRows draw.Height (draw.CursorTop + headerRows) footerRows
        let viewport = agentViewportRows()
        agentScroll <- max 0 (min agentScroll (max 0 (filtered.Length - viewport)))
        if filtered.Length > 0 && selected < agentScroll then agentScroll <- selected
        if filtered.Length > 0 && selected >= agentScroll + viewport then
            agentScroll <- selected - viewport + 1
        let last = min filtered.Length (agentScroll + viewport)
        let historySuffix =
            if filtered.Length = 0 then $" [0 of {all.Length}]"
            elif filtered.Length > viewport || filtered.Length <> all.Length then
                $" [{agentScroll + 1}-{last} of {filtered.Length} / {all.Length} total]"
            else ""
        draw.WriteLine $"Agents{filterSuffix}{historySuffix}"
        let partial = if unavailable = 0 then "" else sprintf " [PARTIAL: %d unavailable]" unavailable
        let partial = partial + (if stale = 0 then "" else sprintf " [STALE: %d projects]" stale)
        let full =
            sprintf "Tracked: %d | Review: %d | Unread: %d | Working: %d | Ready: %d | Waiting: %d | Parked: %d"
                totals.Tracked totals.PendingReviews totals.Unread totals.Working totals.Ready totals.Waiting totals.Parked
        let concise =
            sprintf "Total T%d Rev%d U%d W%d Rd%d Wait%d P%d"
                totals.Tracked totals.PendingReviews totals.Unread totals.Working totals.Ready totals.Waiting totals.Parked
        if draw.Height >= 22 then draw.WriteLine "------"
        let summary = if (full + partial).Length <= width && draw.Height >= 22 then full else concise
        let summaryLine =
            if (unavailable > 0 || stale > 0) && (summary + partial).Length > width then
                sprintf "PARTIAL(%d)/STALE(%d) %s" unavailable stale summary
            else summary + partial
        draw.WriteLine (trim summaryLine width)
        if projects.Length > 0 then
            let heading =
                if width < 55 then
                    if projectPageCount = 1 then "Projects (totals)"
                    else sprintf "Projects [%d/%d] ([/])" (agentProjectPage + 1) projectPageCount
                else
                    let pagination =
                        if projectPageCount = 1 then ""
                        else sprintf "  [%d/%d, [ ] pages]" (agentProjectPage + 1) projectPageCount
                    "Projects (whole-project totals)" + pagination
            draw.WriteLine (trim heading width)
            for index = projectFirst to projectLast - 1 do
                let project, lines = projectItems[index]
                let previous = draw.ForegroundColor
                draw.ForegroundColor <-
                    match project.Status with
                    | AgentView.Stale -> ConsoleColor.DarkYellow
                    | AgentView.TemporarilyUnavailable -> ConsoleColor.Yellow
                    | AgentView.Unavailable -> ConsoleColor.Red
                    | AgentView.Fresh -> projectColor project.Project
                for line in lines do draw.WriteLine line
                draw.ForegroundColor <- previous
        let refreshed =
            agentSnapshot
            |> Option.map (fun snap -> snap.RefreshedAt.ToLocalTime().ToString("HH:mm:ss"))
            |> Option.defaultValue "never"
        let refreshingSuffix =
            if agentRefreshing then " (refreshing...)"
            elif agentError.IsSome && agentSnapshot.IsSome then " (STALE: refresh failed)"
            else ""
        if draw.Height >= 22 then draw.WriteLine $"Snapshot: {refreshed}{refreshingSuffix}"
        if draw.Height >= 22 && not agentChanged.IsEmpty then
            draw.WriteLine $"Updated agents: {agentChanged.Count} (*)"
        agentError |> Option.iter (fun error -> draw.WriteLine(trim ("Error: " + compact error) width))
        if mode = AgentFilterMode then draw.WriteLine $"Filter: {trim (compact agentFilter) (max 20 (width - 9))}"
        if agentSnapshot.IsNone && not agentRefreshing then
            draw.WriteLine "No snapshot loaded. Press r to refresh."
        elif filtered.Length = 0 then
            draw.WriteLine "No matching agents."
        else
            // Agent rows always use the available width. A details-pane
            // breakpoint formerly cut rows to ~55 columns at width 105.
            let rowWidth = TerminalLayout.agentListWidth width
            let projectWidth, agentWidth = AgentView.columnWidths rowWidth filtered
            writeAgentTableHeader rowWidth projectWidth agentWidth
            for index = agentScroll to last - 1 do
                let row = filtered[index]
                let marker = if mode = AgentsMode && Some row.Key = agentSelected then ">" else " "
                registerMouseHit 0 rowWidth (SelectAgent row.Key)
                writeColoredAgentLine rowWidth projectWidth agentWidth marker (seenAt row) row
        if showAgentDetails then renderAgentDetailsUnsafe width

        if not issues.IsEmpty then
            draw.WriteLine ""
            draw.WriteLine $"Agent issues ({issues.Length})"
            draw.WriteLine "------------"
            for issue in issues |> List.truncate 5 do
                draw.WriteLine(trim (compact issue) width)

        renderPermissionPreviewUnsafe width

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
        draw.WriteLine "Settings (user-level configuration)"
        draw.WriteLine "-----------------------------------"
        match settingsSnapshot with
        | None -> draw.WriteLine "Config unavailable."
        | Some snapshot ->
            draw.WriteLine(trim (safe snapshot.Path) width)
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
                registerMouseHit 0 width (SelectSetting index)
                draw.WriteLine(trim (safe $"{marker} {settingLabels[index]}: {value}{source}") width)
            match effective with
            | Some e ->
                draw.WriteLine ""
                draw.WriteLine "Startup effective values (changes may require restart):"
                for index = 0 to settingKeys.Length - 1 do
                    draw.WriteLine(trim (safe $"{settingLabels[index]}: {settingsEffective settingKeys[index] e}") width)
            | None -> ()
        draw.WriteLine ""
        if mode = SettingsEditMode then
            draw.WriteLine(trim (safe $"Editing {settingLabels[settingsSelected]}: {settingsEditing}") width)
            draw.WriteLine "Enter accepts field, Esc cancels edit."
            if settingsSelected = 3 then
                draw.WriteLine "Warning: allowlisted variables can expose credentials to processes started by agents."
        elif mode = SettingsConfirmMode then
            draw.WriteLine "WARNING: Full trust disables local confirmation for agent operations."
            draw.WriteLine "Press y to save, n/Esc to cancel."
        else
            draw.WriteLine "Up/Down select, Enter edit, u restore default, s save, Esc back."
            draw.WriteLine "Saved changes to workspace, trust and limits require restart."
            let originalVariables = settingsSnapshot |> Option.bind (fun s -> s.Stored.AllowedEnvironmentVariables)
            if originalVariables <> settingsStaged.AllowedEnvironmentVariables then
                draw.WriteLine "Warning: environment allowlist changes can expose credential variables."
        settingsError |> Option.iter (fun error -> draw.WriteLine(trim (safe error) width))
        if prompts.Count > 0 then draw.WriteLine $"{prompts.Count} permission requests pending (p to review)."

    let renderUnsafe () =
        let width, height =
            try
                if Console.IsOutputRedirected then (80, 24)
                else (max 1 (Console.WindowWidth - 1), max 1 Console.WindowHeight)
            with _ -> (80, 24)
        draw <- Frame(width, height)
        mouseHits.Clear()
        try
            lastRenderError |> Option.iter (fun error -> draw.WriteLine ("Render warning: " + error))
            let compactHeader = draw.Height < 22
            if compactHeader then
                draw.WriteLine $"Jarvis · {connectionLabel connectionState} · {serverHost}"
                writeNavigation()
            else
                draw.WriteLine "Jarvis Client"
                draw.WriteLine "============="
                let previous = draw.ForegroundColor
                draw.ForegroundColor <- connectionColor connectionState
                draw.Write(connectionLabel connectionState)
                draw.ForegroundColor <- previous
                draw.WriteLine $" • {serverHost}"
                draw.WriteLine $"Key: {key}"
                writeNavigation()
            if not compactHeader then
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
                    draw.WriteLine "Settings: fields, save, defaults, permissions"
            draw.WriteLine ""

            match mode with
            | PermissionMode ->
                renderPermissionRequestsUnsafe width
            | AgentsMode | AgentFilterMode ->
                renderAgentsUnsafe width
            | JobsMode | JobDetailsMode | JobCancelMode | JobFilterMode ->
                renderJobsUnsafe width
            | SettingsMode | SettingsEditMode | SettingsConfirmMode ->
                renderSettingsUnsafe width
            | _ ->
                let detailsRows = measureRows (fun () -> renderActivityDetailsUnsafe width)
                activityPageRows <-
                    TerminalLayout.availableRows draw.Height (draw.CursorTop + 2)
                        (1 + detailsRows + (if prompts.Count > 0 then 3 else 0))
                ensureActivitySelectionUnsafe() |> ignore
                let filtered, startIndex, endExclusive = activityRangeUnsafe()
                let historySuffix =
                    if filtered.Length = 0 then
                        $" [0 of {activity.Count}]"
                    elif filtered.Length > activityViewportRows() || filtered.Length <> activity.Count then
                        $" [{startIndex + 1}-{endExclusive} of {filtered.Length} / {activity.Count} total]"
                    else
                        ""

                let filterSuffix =
                    let text = if String.IsNullOrWhiteSpace activityFilterText then "" else sprintf " text=\"%s\"" activityFilterText
                    let status = if statusFilter = ActivityFilter.All then "" else $" status={ActivityFilter.statusLabel statusFilter}"
                    if text = "" && status = "" then "" else $" [{text.Trim()}{status}]"

                draw.WriteLine $"Activity{filterSuffix}{historySuffix}"
                draw.WriteLine "--------"

                if activity.Count = 0 then
                    draw.WriteLine "No activity yet."
                elif filtered.Length = 0 then
                    draw.WriteLine "No matching activity."
                else
                    for index = startIndex to endExclusive - 1 do
                        let marker =
                            if mode = ActivityMode && selectedActivityId = Some filtered[index].Id then ">"
                            else " "
                        registerMouseHit 0 width (SelectActivity filtered[index].Id)
                        writeColoredProjectLine width marker filtered[index]

                renderActivityDetailsUnsafe width

                renderPermissionPreviewUnsafe width
            if draw.Present(previousFrame) then
                previousFrame <- Some draw
                frameSize <- width, height
                lastRenderError <- None
            else
                // No hit region is considered valid until the frame is presented.
                mouseHits.Clear()
                previousFrame <- None
                frameSize <- 0, 0
        with ex ->
            mouseHits.Clear()
            previousFrame <- None
            frameSize <- 0, 0
            lastRenderError <- Some ex.Message

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
                        let snapshot = AgentView.preserveTransientProjects agentSnapshot snapshot
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

    let handleMouseUnsafe mouse =
        match mouse with
        | Scroll(direction, _, _) ->
            match mode with
            | AgentsMode -> moveAgentSelectionUnsafe (direction * 3)
            | ActivityMode -> moveActivitySelectionUnsafe (direction * 3)
            | JobsMode -> moveJobSelectionUnsafe (direction * 3)
            | JobDetailsMode ->
                jobFollow <- false
                jobOutputScroll <- max 0 (min (max 0 (jobOutputLines.Length - 1)) (jobOutputScroll - direction * 3))
                if jobOutputScroll = 0 then jobFollow <- true
            | PermissionMode -> selectedPrompt <- max 0 (min (prompts.Count - 1) (selectedPrompt + direction))
            | _ -> ()
        | Click(x, y) ->
            // Never let a click abandon a modal edit/approval confirmation.
            if not (List.contains mode [ SettingsEditMode; SettingsConfirmMode
                                         JobCancelMode; CopyRecentMode
                                         AgentFilterMode; JobFilterMode; FilterMode ]) then
                mouseHits
                |> Seq.tryFind (fun hit -> TerminalLayout.contains x y hit.Rect)
                |> Option.iter (fun hit ->
                    let now = DateTimeOffset.UtcNow
                    let isDouble =
                        match lastMouseClick with
                        | Some(previous, at) when previous = hit.Target
                                                  && now - at <= TimeSpan.FromMilliseconds(400.) -> true
                        | _ -> false
                    lastMouseClick <- Some(hit.Target, now)
                    match hit.Target with
                    | SwitchActivity -> mode <- ActivityMode
                    | SwitchAgents ->
                        mode <- AgentsMode
                        normalizeAgentSelectionUnsafe()
                        startAgentRefreshUnsafe()
                    | SwitchJobs ->
                        mode <- JobsMode
                        pollJobsUnsafe()
                    | SwitchSettings -> openSettingsUnsafe()
                    | SelectAgent id when mode = AgentsMode ->
                        agentSelected <- Some id
                        if isDouble then showAgentDetails <- true
                    | SelectActivity id when mode = ActivityMode ->
                        selectedActivityId <- Some id
                        if isDouble then showActivityDetails <- true
                    | SelectJob id when mode = JobsMode ->
                        jobSelectedId <- Some id
                        if isDouble then openJobDetailsUnsafe()
                    | SelectPermission i when prompts.Count > i && i >= 0 ->
                        if mode <> PermissionMode then permissionReturnMode <- mode
                        selectedPrompt <- i
                        mode <- PermissionMode
                        showActivityDetails <- false
                    | SelectSetting i when mode = SettingsMode ->
                        settingsSelected <- i
                        if isDouble then
                            settingsEditing <- settingsValue settingKeys[settingsSelected] settingsStaged
                                               |> Option.defaultValue ""
                            settingsError <- None
                            mode <- SettingsEditMode
                    | _ -> ())

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

    member _.RecordAgentSeen(project: string, agentId: string) =
        if not (String.IsNullOrWhiteSpace project || String.IsNullOrWhiteSpace agentId) then
            lock syncRoot (fun () ->
                let now = DateTimeOffset.UtcNow
                let key = project + "::" + agentId
                agentSeen[key] <- now
                let persist =
                    match persistedAgentSeen.TryGetValue key with
                    | true, previous -> now - previous >= TimeSpan.FromSeconds(10.)
                    | _ -> true
                if persist then
                    let saved =
                        match activityLog with
                        | None -> true
                        | Some store ->
                            try
                                store.SaveAgentSeen(agentSeen |> Seq.map (fun entry -> entry.Key, entry.Value))
                                true
                            with :? System.IO.IOException -> false
                    // A failed atomic snapshot must be retried on the next
                    // attributed call, not treated as durably persisted.
                    if saved then persistedAgentSeen[key] <- now
                if mode = AgentsMode || mode = AgentFilterMode then renderUnsafe())

    // A read-only observation of the local activity cache. This is not a
    // canonical ledger timestamp: another connected client may have seen
    // later tool activity that this process has not observed.
    member _.GetAgentLastSeen(project: string, agentId: string) =
        lock syncRoot (fun () ->
            match agentSeen.TryGetValue(project + "::" + agentId) with
            | true, timestamp -> Some timestamp
            | _ -> None)

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
            let decoder = Decoder()
            render()

            while not shouldQuit && not cancellationToken.IsCancellationRequested do
                try
                    if not Console.IsOutputRedirected then
                        let size = (max 1 (Console.WindowWidth - 1), max 1 Console.WindowHeight)
                        if size <> frameSize then
                            lock syncRoot renderUnsafe
                    if (mode = JobsMode || mode = JobDetailsMode || mode = JobCancelMode || mode = JobFilterMode)
                       && DateTimeOffset.UtcNow - jobLastPoll > TimeSpan.FromMilliseconds(750.) then
                        lock syncRoot (fun () ->
                            pollJobsUnsafe()
                            renderUnsafe())
                    if (mode = AgentsMode || mode = AgentFilterMode)
                       && DateTimeOffset.UtcNow - agentLastPoll > TimeSpan.FromSeconds(45.) then
                        lock syncRoot (fun () -> startAgentRefreshUnsafe())
                    // SGR wheel reports are multi-character sequences. Reading a
                    // single character followed by a 35 ms delay can leave a huge
                    // input backlog; rendering for every completed report makes it
                    // worse. Drain a bounded burst, then render once per wheel run.
                    let buffered = ResizeArray<InputEvent>()
                    let mutable consumed = 0
                    while consumed < 2048 && Console.KeyAvailable do
                        let key = Console.ReadKey(intercept = true)
                        consumed <- consumed + 1
                        for event in decoder.Feed(DateTimeOffset.UtcNow, key) do
                            buffered.Add event
                    if consumed = 0 then
                        for event in decoder.Flush(DateTimeOffset.UtcNow) do
                            buffered.Add event
                    let events = buffered |> Seq.toList |> coalesceWheelEvents
                    for input in events do
                        match input with
                        | Mouse mouse ->
                            lock syncRoot (fun () ->
                                handleMouseUnsafe mouse
                                renderUnsafe())
                        | Key keyInfo ->
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
                                        | ActivityMode -> moveActivitySelectionUnsafe -(activityViewportRows())
                                        | AgentsMode -> moveAgentSelectionUnsafe -(agentViewportRows())
                                        | JobsMode -> moveJobSelectionUnsafe -(jobViewportRows())
                                        | JobDetailsMode ->
                                            jobFollow <- false
                                            jobOutputScroll <- min (jobOutputLines.Length - 1 |> max 0) (jobOutputScroll + jobOutputPageRows)
                                        | _ -> ()
                                        renderUnsafe())
                                | _, ConsoleKey.PageDown ->
                                    lock syncRoot (fun () ->
                                        match mode with
                                        | ActivityMode -> moveActivitySelectionUnsafe (activityViewportRows())
                                        | AgentsMode -> moveAgentSelectionUnsafe (agentViewportRows())
                                        | JobsMode -> moveJobSelectionUnsafe (jobViewportRows())
                                        | JobDetailsMode ->
                                            jobOutputScroll <- max 0 (jobOutputScroll - jobOutputPageRows)
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
                                | '[', _ when mode = AgentsMode ->
                                    lock syncRoot (fun () -> agentProjectPage <- max 0 (agentProjectPage - 1); renderUnsafe())
                                | ']', _ when mode = AgentsMode ->
                                    lock syncRoot (fun () -> agentProjectPage <- agentProjectPage + 1; renderUnsafe())
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
                    if consumed = 0 && events.IsEmpty then
                        do! Task.Delay(15, cancellationToken)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    lock syncRoot (fun () ->
                        addActivityUnsafe None $"TUI error: {compact ex.Message}" None Informational |> ignore
                        renderUnsafe())
                    do! Task.Delay(250, cancellationToken)
        }
