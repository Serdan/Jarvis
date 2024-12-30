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
            base.Clients.Client(base.Context.ConnectionId).ReceiveMessage("Connected")
            Task.CompletedTask

        member this.Disconnect() =
            let id = base.Context.ConnectionId
            users.Remove(id)
            base.Clients.Client(id).ReceiveMessage("Disconnected")
            Task.CompletedTask

        member this.SendClientResponse(correlationId, result) =
            tracker.Complete(correlationId, result)
            Task.CompletedTask
