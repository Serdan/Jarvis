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
    | ActivityMode
    | PermissionMode
    | FilterMode
    | CopyRecentMode

type private PendingPrompt =
    { Id: int
      Command: AgentCommand
      Request: ConfirmationRequest
      Completion: TaskCompletionSource<PermissionApproval> }

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
    let mutable activityScrollOffset = 0
    let mutable showActivityDetails = false
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
        projectColorAssignments.Get projectName

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
            activityStatusMatches statusFilter entry.Status
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
        let entry =
            { Id = nextActivityId
              PersistenceId = Guid.NewGuid().ToString("N")
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
            |> Option.map (fun value -> $"@{value} ")
            |> Option.defaultValue ""

        $"{entry.Timestamp} {project}{entry.Message}{statusSuffix entry.Status}"

    let writeColoredProjectLine width marker entry =
        let rendered = $"{marker} {activityText entry}" |> fun value -> trim value width

        match entry.ProjectName with
        | None -> Console.WriteLine rendered
        | Some projectName ->
            let token = $"@{projectName}"
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
        Console.Write($"@{projectName}")
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
        writeMnemonic 'r' "copy recent"; Console.Write ", "
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
        |> Seq.filter (fun entry -> entry.CommandName.IsSome)
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

                writeDetailField width "Status" (statusName entry.Status)
                statusDuration entry.Status
                |> Option.iter (fun durationMs -> writeDetailField width "Duration" $"{durationMs} ms")
                statusOutcome entry.Status
                |> Option.iter (writeDetailField width "Result")

                entry.FailureDetail
                |> Option.iter (writeDetailField width "Error")

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
            Console.WriteLine ""

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
        with _ ->
            ()

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
                        mode <- ActivityMode
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

    member _.GetActivitySnapshot(projectName: string option, limit: int option) =
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
                |> Seq.rev
                |> Seq.truncate count
                |> Seq.rev
                |> Seq.map (fun entry ->
                    { StartedAt = entry.StartedAt
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
                    if Console.KeyAvailable then
                        let keyInfo = Console.ReadKey(intercept = true)

                        if mode = CopyRecentMode then
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
                                    | FilterMode -> ()
                                    | CopyRecentMode -> ()
                                    renderUnsafe())
                            | _, ConsoleKey.DownArrow ->
                                lock syncRoot (fun () ->
                                    match mode with
                                    | ActivityMode -> moveActivitySelectionUnsafe 1
                                    | PermissionMode ->
                                        selectedPrompt <- min (prompts.Count - 1) (selectedPrompt + 1)
                                        selectedPrompt <- max 0 selectedPrompt
                                    | FilterMode -> ()
                                    | CopyRecentMode -> ()
                                    renderUnsafe())
                            | _, ConsoleKey.PageUp ->
                                lock syncRoot (fun () ->
                                    if mode = ActivityMode then
                                        moveActivitySelectionUnsafe -visibleActivityRows
                                    renderUnsafe())
                            | _, ConsoleKey.PageDown ->
                                lock syncRoot (fun () ->
                                    if mode = ActivityMode then
                                        moveActivitySelectionUnsafe visibleActivityRows
                                    renderUnsafe())
                            | _, ConsoleKey.End ->
                                lock syncRoot (fun () ->
                                    let filtered = filteredActivityUnsafe()
                                    if mode = ActivityMode && filtered.Length > 0 then
                                        selectActivityIndexUnsafe (filtered.Length - 1)
                                    renderUnsafe())
                            | _, ConsoleKey.Enter ->
                                lock syncRoot (fun () ->
                                    if mode = ActivityMode && (filteredActivityUnsafe()).Length > 0 then
                                        showActivityDetails <- not showActivityDetails
                                    renderUnsafe())
                            | _, ConsoleKey.P when mode = ActivityMode ->
                                lock syncRoot (fun () ->
                                    mode <- PermissionMode
                                    showActivityDetails <- false
                                    renderUnsafe())
                            | _, ConsoleKey.Escape when mode = PermissionMode ->
                                lock syncRoot (fun () ->
                                    mode <- ActivityMode
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
