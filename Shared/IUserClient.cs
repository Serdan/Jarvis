using Shared.Messages;

namespace Shared;

public interface IUserClient
{
    Task ReceiveMessage(string message);
    Task ReceiveCommand(string correlationId, AgentCommand command);
}
