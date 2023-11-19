namespace Shared.Messages;

public record AgentMessage<T>(string Key, T Command)
    where T : AgentCommand;
