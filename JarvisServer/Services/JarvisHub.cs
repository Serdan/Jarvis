using Microsoft.AspNetCore.SignalR;
using Shared;
using Shared.SignalR;

namespace JarvisServer.Services;

public class JarvisHub(UserService service, ClientResponseTracker tracker) : Hub<IUserClient>, IJarvisHub
{
    public Task Connect(string userId)
    {
        service.Add(userId, Context.ConnectionId);
        return Clients.Client(Context.ConnectionId)
                      .ReceiveMessage("Connected");
    }

    public async Task Disconnect()
    {
        var id = Context.ConnectionId;
        service.Remove(id);
        await Clients.Client(id)
                     .ReceiveMessage("Disconnected");
    }

    public Task SendClientResponse(string correlationId, string result)
    {
        tracker.Complete(correlationId, result);

        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        service.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
