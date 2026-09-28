namespace Server.Services

open System
open System.Threading.Tasks
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open Server

type HubService(users: UserService, tracker: ClientResponseTracker, logger: ILogger<HubService>) =
    inherit Hub<IClientService>()

    member private this.UserId =
        match Auth.tryUserId this.Context.User with
        | Some userId -> userId
        | None -> raise (InvalidOperationException("Authenticated SignalR connection is missing a subject claim."))

    override this.OnConnectedAsync() =
        users.TransportConnected(this.UserId, this.Context.ConnectionId)
        base.OnConnectedAsync()

    override this.OnDisconnectedAsync(error) =
        let reason =
            match error with
            | null -> None
            | ex -> Some ex.Message

        match error with
        | null ->
            logger.LogInformation(
                "Jarvis client connection {ConnectionId} disconnected.",
                this.Context.ConnectionId
            )
        | ex ->
            logger.LogWarning(
                ex,
                "Jarvis client connection {ConnectionId} disconnected with an error.",
                this.Context.ConnectionId
            )

        users.Disconnect(this.Context.ConnectionId, reason)
        base.OnDisconnectedAsync(error)

    member this.Connect(registration: ClientRegistration) =
        users.Register(this.UserId, registration, this.Context.ConnectionId)
        |> Task.FromResult

    member this.SendClientResponse(correlationId: string, response: AgentCommandResponse) =
        users.TouchConnection(this.Context.ConnectionId)
        tracker.Complete(correlationId, response)
        Task.CompletedTask

    interface IHubService with
        member this.Connect(registration) = this.Connect(registration)

        member this.SendClientResponse(correlationId, result) = this.SendClientResponse(correlationId, result)
