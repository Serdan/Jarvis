using System.Text.Json;
using System.Text.Json.Serialization;
using JarvisClient.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Shared.Messages;
using Shared.SignalR;
using static Shared.FunctionalConsole;
using static Shared.Messages.AgentCommand;

namespace JarvisClient;

/// <summary>
/// Represents a user client in the Jarvis project, handling communication and commands.
/// </summary>
/// <param name="hub">The SignalR HubConnection used for client-server communication.</param>
/// <param name="browser">The instance of ProjectBrowser for file and directory management.</param>
public class UserClient(HubConnection hub, ProjectBrowser browser) : IUserClient
{
    /// <summary>
    /// Receives and displays a message from the server.
    /// </summary>
    /// <param name="message">The message to be received and displayed.</param>
    /// <returns>A Task representing the asynchronous operation of message reception.</returns>
    public Task ReceiveMessage(string message)
    {
        Console.WriteLine(message);
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Receives and processes a command from the server, executing appropriate actions in the ProjectBrowser.
    /// </summary>
    /// <param name="correlationId">The correlation ID for the command.</param>
    /// <param name="command">The command to be processed.</param>
    /// <returns>A Task representing the asynchronous operation of command processing.</returns>
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
                TextReplaceSectionCommand(var projectName, var filePath, var sectionIdentifiers, var replacementContent) =>
                    browser.ReplaceSection(projectName, filePath, sectionIdentifiers, replacementContent),
                TextReplaceCommand(var projectName, var filePath, var search, var content) => browser.Replace(projectName, filePath, search, content),
                TextInsertBeforeCommand(var projectName, var filePath, var search, var content) => browser.InsertBefore(projectName, filePath, search, content),
                TextInsertAfterCommand(var projectName, var filePath, var search, var content) => browser.InsertAfter(projectName, filePath, search, content),
                RunUnitTestsCommand(var projectName, var filePath) => browser.RunUnitTests(projectName, filePath),
                _ => error("Unknown command")
            };

            response = result.IfError(x =>
            {
                WriteLine(x.Message);
                return x.Message;
            });
        }
        catch (Exception e)
        {
            WriteLine(e.Message);
            response = e.Message;
        }
        finally
        {
            await hub.InvokeAsync<IJarvisHub>(x => x.SendClientResponse(correlationId, response));
        }
    }

    /// <summary>
    /// Serializes a given value to a JSON string using predefined JsonSerializer options.
    /// </summary>
    /// <param name="value">The value to be serialized.</param>
    /// <returns>A Result containing the serialized JSON string or an error message if serialization fails.</returns>
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

    /// <summary>
    /// Serializes a given value to a JSON string using predefined JsonSerializer options.
    /// </summary>
    /// <param name="value">The value to be serialized.</param>
    /// <returns>A Result containing the serialized JSON string or an error message if serialization fails.</returns>
    private static AsyncEffect<string> Serialize2<T>(T value)
    {
        try
        {
            return okAsyncEffect(JsonSerializer.Serialize(value, Options()));
        }
        catch (Exception e)
        {
            return errorAsyncEffect<string>(e.Message);
        }
    }

    /// <summary>
    /// Configures JsonSerializerOptions for serialization processes in the UserClient.
    /// </summary>
    /// <returns>Configured JsonSerializerOptions with specific settings for the UserClient.</returns>
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
