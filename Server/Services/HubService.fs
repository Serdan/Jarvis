namespace Server.Services

open System.Threading.Tasks
open Common.SignalR
open Microsoft.AspNetCore.SignalR

type HubService(users: UserService, tracker: ClientResponseTracker) =
    inherit Hub<IClientService>()

    override this.OnDisconnectedAsync(``exception``) =
        task {
            users.Remove(this.Context.ConnectionId)
            do! this.OnDisconnectedAsync(``exception``)
        }

    interface IHubService with
        member this.Connect(userId) =
            users.Add(userId, base.Context.ConnectionId)
            Task.CompletedTask

        member this.SendClientResponse(correlationId, result) =
            tracker.Complete(correlationId, result)
            Task.CompletedTask
