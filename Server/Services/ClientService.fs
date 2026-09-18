namespace Server.Services

open System
open System.Threading.Tasks
open System.Text.Json
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR
open Microsoft.FSharp.Core

type ClientService(ctx: IHubContext<HubService, IClientService>, users: UserService, tracker: ClientResponseTracker) =
    member this.SendMessageToAll(message) = ctx.Clients.All.ReceiveMessage(message)

    member private this.ResponseTimeout(command: AgentCommand) =
        let permissionMargin = TimeSpan.FromMinutes(5L)
        match command with
        | RunCommandCommand cmd ->
            let executionSeconds = cmd.TimeoutSeconds |> Option.defaultValue 60 |> max 1
            TimeSpan.FromSeconds(float executionSeconds) + permissionMargin
        | _ -> TimeSpan.FromMinutes(10L)

    member this.SendCommandToUser(message: AgentMessage) =
        task {
            match users.GetConnectionId(message.Key) with
            | ValueNone ->
                return
                    { Result = None
                      Error = Some(NotFound $"User not found with key: {message.Key}") }
            | ValueSome id ->
                let correlationId, trackingTask = tracker.Register(this.ResponseTimeout message.Command)
                let client = ctx.Clients.Client(id)
                let commandJson = JsonSerializer.Serialize<AgentCommand>(message.Command)

                try
                    do! client.ReceiveCommand(correlationId, commandJson)

                    try
                        return! trackingTask
                    with :? TaskCanceledException ->
                        return
                            { Result = None
                              Error = Some(ExecutionFailed "Timeout. Client didn't respond.") }
                with ex ->
                    return
                        { Result = None
                          Error = Some(ExecutionFailed ex.Message) }
        }
