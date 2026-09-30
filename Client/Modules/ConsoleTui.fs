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

type private ActivityEntry =
    { Id: int
      Timestamp: string
      ProjectName: string option
      Message: string
      Command: AgentCommand option
      mutable Status: ActivityStatus
      mutable FailureDetail: string option }

type private TuiMode =
    | ActivityMode
    | PermissionMode

type private PendingPrompt =
    { Id: int
      Command: AgentCommand
      Request: ConfirmationRequest
      Completion: TaskCompletionSource<PermissionApproval> }

type ConsoleTui() =
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

    let projectColors =
        [| ConsoleColor.Cyan
           ConsoleColor.Green
           ConsoleColor.Magenta
           ConsoleColor.Yellow
           ConsoleColor.Blue
           ConsoleColor.DarkCyan
           ConsoleColor.DarkGreen
           ConsoleColor.DarkMagenta |]

    let projectColor (projectName: string) =
        let mutable hash = 2166136261u
        for ch in projectName do
            hash <- (hash ^^^ uint32 (int ch)) * 16777619u
        projectColors[int (hash % uint32 projectColors.Length)]

    let maxScrollOffsetUnsafe () =
        max 0 (activity.Count - visibleActivityRows)

    let clampScrollOffsetUnsafe () =
        activityScrollOffset <- activityScrollOffset |> max 0 |> min (maxScrollOffsetUnsafe())

    let selectedActivityIndexUnsafe () =
        match selectedActivityId with
        | None -> None
        | Some id ->
            activity
            |> Seq.tryFindIndex (fun entry -> entry.Id = id)

    let selectActivityIndexUnsafe index =
        match ActivityNavigation.normalizeSelection activity.Count (Some index) with
        | None ->
            selectedActivityId <- None
            activityScrollOffset <- 0
        | Some selectedIndex ->
            selectedActivityId <- Some activity[selectedIndex].Id
            activityScrollOffset <-
                ActivityNavigation.scrollOffsetForSelection
                    activity.Count
                    visibleActivityRows
                    activityScrollOffset
                    selectedIndex

    let ensureActivitySelectionUnsafe () =
        match selectedActivityIndexUnsafe() with
        | Some index -> Some index
        | None ->
            match ActivityNavigation.normalizeSelection activity.Count None with
            | None -> None
            | Some index ->
                selectedActivityId <- Some activity[index].Id
                Some index

    let moveActivitySelectionUnsafe delta =
        let current = ensureActivitySelectionUnsafe()
        match ActivityNavigation.move activity.Count delta current with
        | Some index -> selectActivityIndexUnsafe index
        | None -> ()

    let addActivityUnsafe projectName message command status =
        let wasScrolled = activityScrollOffset > 0
        let previousLastId =
            if activity.Count = 0 then None
            else Some activity[activity.Count - 1].Id
        let followLatest =
            not showActivityDetails
            && activityScrollOffset = 0
            && (selectedActivityId.IsNone || selectedActivityId = previousLastId)
        let entry =
            { Id = nextActivityId
              Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss")
              ProjectName = projectName
              Message = message
              Command = command
              Status = status
              FailureDetail = None }

        nextActivityId <- nextActivityId + 1
        activity.Add entry

        if followLatest then
            selectedActivityId <- Some entry.Id

        if wasScrolled then
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

    let activityRangeUnsafe () =
        clampScrollOffsetUnsafe()
        let endExclusive = activity.Count - activityScrollOffset
        let start = max 0 (endExclusive - visibleActivityRows)
        start, endExclusive

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
        ensureActivitySelectionUnsafe()
        |> Option.map (fun index -> activity[index])

    let statusName = function
        | Informational -> "Info"
        | Running -> "Running"
        | AwaitingPermission -> "Awaiting permission"
        | Completed _ -> "Completed"
        | Failed _ -> "Failed"

    let statusDuration = function
        | Completed(durationMs, _)
        | Failed(durationMs, _) -> Some durationMs
        | _ -> None

    let statusOutcome = function
        | Completed(_, outcome) -> outcome
        | Failed(_, shortError) -> Some shortError
        | _ -> None

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

                match entry.Command with
                | Some command ->
                    writeDetailField width "Command" (AgentCommandInfo.displayName command)
                    AgentCommandInfo.fullReason command
                    |> Option.iter (writeDetailField width "Reason")
                    AgentCommandInfo.fullDetail command
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
            Console.Write "Connection: "
            let previous = Console.ForegroundColor
            Console.ForegroundColor <- connectionColor connectionState
            Console.Write(connectionLabel connectionState)
            Console.ForegroundColor <- previous
            Console.WriteLine $" • {serverHost}"
            Console.WriteLine $"Key: {key}"
            match mode with
            | ActivityMode ->
                Console.WriteLine "Keys: ↑/↓ activity, PgUp/PgDn page, End latest, Enter details, P permissions, Q quit"
            | PermissionMode ->
                Console.WriteLine "Keys: ↑/↓ permission, A allow once, S allow exact, E allow executable, D deny, Esc activity, Q quit"
            Console.WriteLine ""

            ensureActivitySelectionUnsafe() |> ignore
            let startIndex, endExclusive = activityRangeUnsafe()
            let historySuffix =
                if activity.Count > visibleActivityRows then
                    $" [{startIndex + 1}-{endExclusive} of {activity.Count}]"
                else ""

            Console.WriteLine $"Activity{historySuffix}"
            Console.WriteLine "--------"

            if activity.Count = 0 then
                Console.WriteLine "No activity yet."
            else
                for index = startIndex to endExclusive - 1 do
                    let marker =
                        if mode = ActivityMode && selectedActivityId = Some activity[index].Id then ">"
                        else " "
                    writeColoredProjectLine width marker activity[index]

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
            |> Option.iter (fun entry -> entry.Status <- AwaitingPermission)
            renderUnsafe())

    member _.MarkActivityRunning id =
        lock syncRoot (fun () ->
            tryFindActivityUnsafe id
            |> Option.iter (fun entry -> entry.Status <- Running)
            renderUnsafe())

    member _.CompleteActivity(id, durationMs, outcome) =
        lock syncRoot (fun () ->
            tryFindActivityUnsafe id
            |> Option.iter (fun entry -> entry.Status <- Completed(durationMs, outcome))
            renderUnsafe())

    member _.FailActivity(id, durationMs, shortError, fullError) =
        lock syncRoot (fun () ->
            tryFindActivityUnsafe id
            |> Option.iter (fun entry ->
                entry.Status <- Failed(durationMs, shortError)
                entry.FailureDetail <- Some fullError)
            renderUnsafe())

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

                        match keyInfo.Key with
                        | ConsoleKey.UpArrow ->
                            lock syncRoot (fun () ->
                                match mode with
                                | ActivityMode -> moveActivitySelectionUnsafe -1
                                | PermissionMode ->
                                    selectedPrompt <- max 0 (selectedPrompt - 1)
                                renderUnsafe())
                        | ConsoleKey.DownArrow ->
                            lock syncRoot (fun () ->
                                match mode with
                                | ActivityMode -> moveActivitySelectionUnsafe 1
                                | PermissionMode ->
                                    selectedPrompt <- min (prompts.Count - 1) (selectedPrompt + 1)
                                    selectedPrompt <- max 0 selectedPrompt
                                renderUnsafe())
                        | ConsoleKey.PageUp ->
                            lock syncRoot (fun () ->
                                if mode = ActivityMode then
                                    moveActivitySelectionUnsafe -visibleActivityRows
                                renderUnsafe())
                        | ConsoleKey.PageDown ->
                            lock syncRoot (fun () ->
                                if mode = ActivityMode then
                                    moveActivitySelectionUnsafe visibleActivityRows
                                renderUnsafe())
                        | ConsoleKey.End ->
                            lock syncRoot (fun () ->
                                if mode = ActivityMode && activity.Count > 0 then
                                    selectActivityIndexUnsafe (activity.Count - 1)
                                renderUnsafe())
                        | ConsoleKey.Enter ->
                            lock syncRoot (fun () ->
                                if mode = ActivityMode && activity.Count > 0 then
                                    showActivityDetails <- not showActivityDetails
                                renderUnsafe())
                        | ConsoleKey.P ->
                            lock syncRoot (fun () ->
                                mode <- PermissionMode
                                showActivityDetails <- false
                                renderUnsafe())
                        | ConsoleKey.Escape ->
                            lock syncRoot (fun () ->
                                mode <- ActivityMode
                                renderUnsafe())
                        | ConsoleKey.A ->
                            if mode = PermissionMode then
                                completeSelected AllowOnce
                        | ConsoleKey.S ->
                            if mode = PermissionMode then
                                completeSelected AllowExactForSession
                        | ConsoleKey.E ->
                            if mode = PermissionMode then
                                completeSelected AllowExecutableForSession
                        | ConsoleKey.D ->
                            if mode = PermissionMode then
                                completeSelected Deny
                        | ConsoleKey.Q -> shouldQuit <- true
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
