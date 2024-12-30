namespace Server.Services

open System.Threading.Tasks
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR
open FsToolkit.ErrorHandling
open Microsoft.FSharp.Core

type ClientService(ctx: IHubContext<HubService, IClientService>, users: UserService, tracker: ClientResponseTracker) =
    member this.SendMessageToAll(message) = ctx.Clients.All.ReceiveMessage(message)

    member this.SendCommandToUser(message: AgentMessage) =
        let getId key =
            users.GetConnectionId(key)
            |> ValueOption.toResult $"User not found with key: {message.Key}"

        let core () =
            taskResult {
                let! id = getId message.Key
                let correlationId, trackingTask = tracker.Register()
                let client = ctx.Clients.Client(id)
                do! client.ReceiveCommand(correlationId, message.Command)
                return! trackingTask
            }

        task {
            try
                let! result = core ()
                return result |> Result.defaultWith _.Message
            with :? TaskCanceledException ->
                return "Timeout. Client didn't respond."
        }
