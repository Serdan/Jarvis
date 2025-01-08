using System.Security.Cryptography;
using JarvisClient;
using JarvisClient.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Shared.Messages;
using Shared.SignalR;

var clientOptions = new ClientOptions
{
    Path = args switch
    {
        ["--path", { } path] => path,
        _ => ""
    }
};

await using var connection = new HubConnectionBuilder()
                             .AddJsonProtocol()
                             .WithUrl("https://jarvis.kehlet.dev/client")
                             .Build();

var file = new FileSystem();
var browser = ProjectBrowser.Create(file, clientOptions.Path);
var client = new UserClient(connection, browser);

connection.On(client, x => x.ReceiveMessage);
connection.On(client, x => x.ReceiveCommand);
connection.On("ReceiveCommand", (string id, AgentCommand cmd) => client.ReceiveCommand(id, cmd));


var loop = true;

Console.CancelKeyPress += (_, _) => loop = false;

try
{
    await connection.StartAsync();

    var key = RandomNumberGenerator.GetBytes(18)
                                   .Apply(Convert.ToBase64String);

    Console.WriteLine("Provide this key to the agent:");
    Console.WriteLine(key);
    Console.WriteLine();

    await connection.InvokeAsync<IJarvisHub>(x => x.Connect(key));
    await connection.InvokeAsync("Connect", key);

    while (loop)
    {
        Console.ReadLine();
        Console.WriteLine(connection.State);
        if (connection.State is HubConnectionState.Disconnected)
        {
            await connection.InvokeAsync<IJarvisHub>(x => x.Connect(key));
        }
    }
}
catch (Exception e)
{
    Console.WriteLine(e);
    throw;
}
finally
{
    await connection.InvokeAsync<IJarvisHub>(x => x.Disconnect());
    await connection.StopAsync();
}
