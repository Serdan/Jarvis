namespace Server.Services

open System
open System.Threading.Tasks
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR

type HubService(users: UserService, tracker: ClientResponseTracker) =
    inherit Hub<IClientService>()

    override this.OnConnectedAsync() =
        users.TransportConnected(this.Context.ConnectionId)
        base.OnConnectedAsync()

    override this.OnDisconnectedAsync(``exception``) =
        let reason =
            match ``exception`` with
            | null -> None
            | ex -> Some ex.Message

        users.Disconnect(this.Context.ConnectionId, reason)
        base.OnDisconnectedAsync(``exception``)

    member this.Connect(registration: ClientRegistration) =
        users.Register(registration, this.Context.ConnectionId)
        |> Task.FromResult

    member this.SendClientResponse(correlationId: string, response: AgentCommandResponse) =
        users.TouchConnection(this.Context.ConnectionId)
        tracker.Complete(correlationId, response)
        Task.CompletedTask

    interface IHubService with
        member this.Connect(registration) = this.Connect(registration)

        member this.SendClientResponse(correlationId, result) = this.SendClientResponse(correlationId, result)
