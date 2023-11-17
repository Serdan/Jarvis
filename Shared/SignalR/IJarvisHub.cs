namespace Shared.SignalR;

public interface IJarvisHub
{
    Task Connect(string userId);
    Task Disconnect();
    Task SendClientResponse(string correlationId, string result);
}
