namespace Server.Services

open System.Threading.Tasks
open Common.SignalR
open Microsoft.AspNetCore.SignalR

type HubService(users: UserService, tracker: ClientResponseTracker) =
    inherit Hub<IClientService>()

    override this.OnDisconnectedAsync(``exception``) =
        users.Remove(this.Context.ConnectionId)
        base.OnDisconnectedAsync(``exception``)

    member this.Connect(userId: string) =
        users.Add(userId, this.Context.ConnectionId)
        Task.CompletedTask

    member this.SendClientResponse(correlationId: string, result: string) =
        tracker.Complete(correlationId, result)
        Task.CompletedTask

    interface IHubService with
        member this.Connect(userId) = this.Connect(userId)

        member this.SendClientResponse(correlationId, result) = this.SendClientResponse(correlationId, result)
