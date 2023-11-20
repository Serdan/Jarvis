using JarvisServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Messages;
using static Microsoft.AspNetCore.Http.Results;
using static Shared.Messages.AgentCommand;

namespace JarvisServer;

public static class Endpoints
{
    public static Task<IResult> ListProjects(ClientService client, [FromBody] AgentMessage<ListProjectsCommand> message) =>
        from result in client.SendCommandToUser(message with { Command = new() })
        select Json(result);

    public static Task<IResult> OpenProject(ClientService client, [FromBody] AgentMessage<GetProjectDetailsCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> ListProjectDirectory(ClientService client, [FromBody] AgentMessage<ListProjectDirectoryCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> OpenFile(ClientService client, [FromBody] AgentMessage<OpenFileCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> WriteFile(ClientService client, [FromBody] AgentMessage<WriteFileCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> ReplaceSection(ClientService client, [FromBody] AgentMessage<SectionReplaceCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> TextReplace(ClientService client, [FromBody] AgentMessage<TextReplaceCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> TextInsertBefore(ClientService client, [FromBody] AgentMessage<TextInsertBeforeCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> TextInsertAfter(ClientService client, [FromBody] AgentMessage<TextInsertAfterCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);

    public static Task<IResult> RunUnitTests(ClientService client, [FromBody] AgentMessage<RunUnitTestsCommand> message) =>
        from result in client.SendCommandToUser(message)
        select Json(result);
}
