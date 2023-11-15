using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Shared;
using Shared.Messages;
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
            Console.WriteLine("Receiving command");

            var result = command switch
            {
                ListProjectsCommand => browser.ListProjects().Select(ToString),
                GetProjectDetailsCommand(var projectName) => browser.GetProjectDetails(projectName).Select(ToString),
                ListProjectDirectoryCommand(var projectName, var path) => browser.ListProjectDirectory(projectName, path).Select(ToString),
                OpenFileCommand(var projectName, var path) => browser.OpenFile(projectName, path),
                _ => Error("Unknown command")
            };

            response = result.IfError(x => x.Message);
        }
        catch (Exception e)
        {
            response = e.Message;
        }
        finally
        {
            await hub.InvokeAsync<IGptHub>(x => x.SendClientResponse(correlationId, response));
        }
    }

    private static Result<string> ToString<T>(T value)
    {
        try
        {
            return Ok(JsonSerializer.Serialize(value, Options()));
        }
        catch (Exception e)
        {
            return Error(e);
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
