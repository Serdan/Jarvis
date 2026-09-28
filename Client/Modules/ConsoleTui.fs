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
      mutable Status: ActivityStatus
      mutable FailureDetail: string option }

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
    let mutable activityScrollOffset = 0
    let mutable showLastError = false
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

    let addActivityUnsafe projectName message status =
        let wasScrolled = activityScrollOffset > 0
        let entry =
            { Id = nextActivityId
              Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss")
              ProjectName = projectName
              Message = message
              Status = status
              FailureDetail = None }

        nextActivityId <- nextActivityId + 1
        activity.Add entry

        if wasScrolled then
            activityScrollOffset <- activityScrollOffset + 1

        while activity.Count > maxHistory do
            activity.RemoveAt 0

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

    let writeColoredProjectLine width entry =
        let rendered = activityText entry |> fun value -> trim value width

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

    let latestFailureUnsafe () =
        activity
        |> Seq.rev
        |> Seq.tryFind (fun entry -> entry.FailureDetail.IsSome)

    let errorDetailLines width (detail: string) =
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
            Console.WriteLine "Keys: ↑/↓ permission, A allow once, S allow exact, E allow executable, D deny, PgUp/PgDn activity, End latest, V error, Q quit"
            Console.WriteLine ""

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
                    writeColoredProjectLine width activity[index]

            Console.WriteLine ""
            Console.WriteLine "Permission requests"
            Console.WriteLine "-------------------"

            if prompts.Count = 0 then
                Console.WriteLine "No pending permission requests."
            else
                for index = 0 to prompts.Count - 1 do
                    let prompt = prompts[index]
                    let marker = if index = selectedPrompt then ">" else " "
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

            if showLastError then
                match latestFailureUnsafe() with
                | Some entry ->
                    Console.WriteLine ""
                    Console.WriteLine "Latest error"
                    Console.WriteLine "------------"
                    Console.WriteLine(trim (activityText entry) width)
                    entry.FailureDetail
                    |> Option.iter (fun detail ->
                        for line in errorDetailLines width detail do
                            Console.WriteLine line)
                | None -> ()

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
            addActivityUnsafe None (compact message) Informational |> ignore
            renderUnsafe())

    member _.StartActivity(projectName, message) =
        lock syncRoot (fun () ->
            let id = addActivityUnsafe projectName message Running
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
                                selectedPrompt <- max 0 (selectedPrompt - 1)
                                renderUnsafe())
                        | ConsoleKey.DownArrow ->
                            lock syncRoot (fun () ->
                                selectedPrompt <- min (prompts.Count - 1) (selectedPrompt + 1)
                                selectedPrompt <- max 0 selectedPrompt
                                renderUnsafe())
                        | ConsoleKey.PageUp ->
                            lock syncRoot (fun () ->
                                activityScrollOffset <- min (maxScrollOffsetUnsafe()) (activityScrollOffset + visibleActivityRows)
                                renderUnsafe())
                        | ConsoleKey.PageDown ->
                            lock syncRoot (fun () ->
                                activityScrollOffset <- max 0 (activityScrollOffset - visibleActivityRows)
                                renderUnsafe())
                        | ConsoleKey.End ->
                            lock syncRoot (fun () ->
                                activityScrollOffset <- 0
                                renderUnsafe())
                        | ConsoleKey.V ->
                            lock syncRoot (fun () ->
                                showLastError <- not showLastError
                                renderUnsafe())
                        | ConsoleKey.A -> completeSelected AllowOnce
                        | ConsoleKey.S -> completeSelected AllowExactForSession
                        | ConsoleKey.E -> completeSelected AllowExecutableForSession
                        | ConsoleKey.D -> completeSelected Deny
                        | ConsoleKey.Q -> shouldQuit <- true
                        | _ -> ()
                    else
                        do! Task.Delay(50, cancellationToken)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    lock syncRoot (fun () ->
                        addActivityUnsafe None $"TUI error: {compact ex.Message}" Informational |> ignore
                        renderUnsafe())
                    do! Task.Delay(250, cancellationToken)
        }
