namespace Shared;

public interface IGptHub
{
    Task Connect(string userId);
    Task Disconnect();
    Task SendClientResponse(string correlationId, string result);
}
