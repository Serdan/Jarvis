using JarvisServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Messages;
using static Shared.Messages.AgentCommand;

namespace JarvisServer;

using static FunctionalConsole;

public static class Endpoints
{
    public static Task<string> ListProjects(ClientService client, [FromBody] AgentMessage<ListProjectsCommand> message) =>
        client.SendCommandToUser(message.Apply(WriteLine))
              .Select(WriteLine);

    public static Task<string> OpenProject(ClientService client, [FromBody] AgentMessage<GetProjectDetailsCommand> message) =>
        client.SendCommandToUser(message.Apply(WriteLine))
              .Select(WriteLine);

    public static Task<string> ListProjectDirectory(ClientService client, [FromBody] AgentMessage<ListProjectDirectoryCommand> message) =>
        client.SendCommandToUser(message)
              .Select(WriteLine);

    public static Task<string> OpenFile(ClientService client, [FromBody] AgentMessage<OpenFileCommand> message) =>
        client.SendCommandToUser(message)
              .Select(WriteLine);

    public static Task<string> WriteFile(ClientService client, [FromBody] AgentMessage<WriteFileCommand> message) =>
        client.SendCommandToUser(message)
              .Select(WriteLine);
}
