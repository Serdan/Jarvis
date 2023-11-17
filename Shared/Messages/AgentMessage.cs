namespace Shared.Messages;

public record AgentMessage<T>
    where T : AgentCommand
{
    public AgentMessage(string Key, T Command)
    {
        this.Key = Key;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Command is null && typeof(T) == typeof(AgentCommand.ListProjectsCommand))
        {
            this.Command = (new AgentCommand.ListProjectsCommand() as T)!;
        }
        else
        {
            this.Command = Command!;
        }
    }

    public string Key { get; init; }
    public T Command { get; init; }
}
