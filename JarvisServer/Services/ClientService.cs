using Microsoft.AspNetCore.SignalR;
using Shared;
using Shared.Messages;

namespace JarvisServer.Services;

public class ClientService(IHubContext<JarvisHub, IUserClient> hub, UserService users, ClientResponseTracker tracker)
{
    public Task SendMessageToAll(string message) =>
        hub.Clients.All.ReceiveMessage(message);

    public async Task<string> SendCommandToUser<T>(AgentMessage<T> message)
        where T : AgentCommand
    {
        try
        {
            Result<string> query = await from id in users.GetConnectionId(message.Key)
                                         let tracking = tracker.Register()
                                         let client = hub.Clients.Client(id)
                                         let task = client.ReceiveCommand(tracking.Id, message.Command)
                                         select task.Select(() => tracking.Task);

            return query.IfError(x => x.Message);
        }
        catch (TaskCanceledException)
        {
            return "Timeout. Client didn't respond.";
        }
    }
}

public static class TaskExtensions
{
    public static async Task<TResult> Select<TResult>(this Task self, Func<Task<TResult>> f)
    {
        await self;
        return await f();
    }
}
