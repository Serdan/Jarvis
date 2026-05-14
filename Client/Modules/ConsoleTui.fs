module Client.ConsoleTui

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Common
open Client

type private PendingPrompt =
    { Id: int
      Command: AgentCommand
      Request: ConfirmationRequest
      Completion: TaskCompletionSource<PermissionApproval> }

type ConsoleTui() =
    let syncRoot = obj()
    let logs = Queue<string>()
    let prompts = ResizeArray<PendingPrompt>()
    let mutable selectedPrompt = 0
    let mutable nextPromptId = 1
    let mutable shouldQuit = false
    let mutable key = ""
    let maxLogs = 12

    let addLogUnsafe message =
        let timestamp = DateTimeOffset.Now.ToString("HH:mm:ss")
        logs.Enqueue $"{timestamp}  {message}"

        while logs.Count > maxLogs do
            logs.Dequeue() |> ignore

    let trim value maxLength =
        if String.IsNullOrEmpty value || value.Length <= maxLength then value
        else value.Substring(0, maxLength - 1) + "…"

    let renderUnsafe () =
        try
            Console.Clear()
            Console.WriteLine "Jarvis Client"
            Console.WriteLine "============="
            Console.WriteLine $"Key: {key}"
            Console.WriteLine "Keys: ↑/↓ select permission, A allow once, S allow exact for session, D deny, Q quit"
            Console.WriteLine ""
            Console.WriteLine "Activity"
            Console.WriteLine "--------"

            if logs.Count = 0 then
                Console.WriteLine "No activity yet."
            else
                for log in logs do
                    Console.WriteLine(trim log (Math.Max(20, Console.WindowWidth - 1)))

            Console.WriteLine ""
            Console.WriteLine "Permission requests"
            Console.WriteLine "-------------------"

            if prompts.Count = 0 then
                Console.WriteLine "No pending permission requests."
            else
                for index = 0 to prompts.Count - 1 do
                    let prompt = prompts[index]
                    let marker = if index = selectedPrompt then ">" else " "
                    let project = prompt.Request.ProjectName |> Option.defaultValue "-"
                    Console.WriteLine $"{marker} #{prompt.Id} {prompt.Request.CommandName} project={project}"
                    Console.WriteLine $"    {trim prompt.Request.Summary (Math.Max(20, Console.WindowWidth - 5))}"

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
                    addLogUnsafe $"Permission {approval} for #{prompt.Id} {prompt.Request.CommandName}"
                    renderUnsafe()
                    Some prompt.Completion)

        completion |> Option.iter (fun tcs -> tcs.TrySetResult approval |> ignore)

    member _.SetKey value =
        lock syncRoot (fun () ->
            key <- value
            renderUnsafe())

    member _.Log message =
        lock syncRoot (fun () ->
            addLogUnsafe message
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
            addLogUnsafe $"Permission requested: #{prompt.Id} {request.CommandName}"
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
                        | ConsoleKey.A -> completeSelected AllowOnce
                        | ConsoleKey.S -> completeSelected AllowExactForSession
                        | ConsoleKey.D -> completeSelected Deny
                        | ConsoleKey.Q -> shouldQuit <- true
                        | _ -> ()
                    else
                        do! Task.Delay(50, cancellationToken)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    lock syncRoot (fun () ->
                        addLogUnsafe $"TUI error: {ex.Message}"
                        renderUnsafe())
                    do! Task.Delay(250, cancellationToken)
        }
