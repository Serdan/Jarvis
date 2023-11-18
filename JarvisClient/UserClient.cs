using System.Text.Json;
using System.Text.Json.Serialization;
using JarvisClient.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Shared.Messages;
using Shared.SignalR;
using static Shared.FunctionalConsole;
using static Shared.Messages.AgentCommand;

namespace JarvisClient;

public class UserClient(HubConnection hub, ProjectBrowser browser) : IUserClient
{
    public Task ReceiveMessage(string message)
    {
        Console.WriteLine(message);
        return Task.CompletedTask;
    }

    public async Task ReceiveCommand(string correlationId, AgentCommand command)
    {
        var response = "Something went wrong";
        try
        {
            Console.WriteLine($"Receiving command: {command}");

            var result = command switch
            {
                ListProjectsCommand => browser.ListProjects().Apply(Serialize),
                GetProjectDetailsCommand(var projectName) => browser.GetProjectDetails(projectName).Select(Serialize),
                ListProjectDirectoryCommand(var projectName, var path) => browser.ListProjectDirectory(projectName, path).Select(Serialize),
                OpenFileCommand(var projectName, var path) => browser.OpenFile(projectName, path),
                WriteFileCommand(var projectName, var filePath, var content, var mode) => browser.WriteFile(projectName, filePath, content, mode),
                SectionReplaceCommand(var projectName, var filePath, var sectionIdentifiers, var replacementContent, var backupOption) =>
                    browser.ReplaceSection(projectName, filePath, sectionIdentifiers, replacementContent, backupOption),
                _ => error("Unknown command")
            };

            response = result.IfError(x => x.Message.Apply(WriteLine));
        }
        catch (Exception e)
        {
            response = e.Message.Apply(WriteLine);
        }
        finally
        {
            await hub.InvokeAsync<IJarvisHub>(x => x.SendClientResponse(correlationId, response));
        }
    }

    private static Result<string> Serialize<T>(T value)
    {
        try
        {
            return ok(JsonSerializer.Serialize(value, Options()));
        }
        catch (Exception e)
        {
            return error(e);
        }
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }
}
