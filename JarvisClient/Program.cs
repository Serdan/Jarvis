// See https://aka.ms/new-console-template for more information

using System.Security.Cryptography;
using JarvisClient;
using JarvisClient.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Shared.Extensions;
using Shared.SignalR;

await using var connection = new HubConnectionBuilder()
                             .AddJsonProtocol()
                             .WithUrl("https://jarvis.kehlet.dev/hub")
                             .Build();

var browser = ProjectBrowser.Create("z:\\repos");
var client = new UserClient(connection, browser);

connection.On(client, x => x.ReceiveMessage);
connection.On(client, x => x.ReceiveCommand);

var loop = true;

Console.CancelKeyPress += (_, _) => loop = false;

try
{
    await connection.StartAsync();

    var key = RandomNumberGenerator.GetBytes(16)
                                   .Apply(Convert.ToBase64String);

    Console.WriteLine("Key:");
    Console.WriteLine(key);

    await connection.InvokeAsync<IJarvisHub>(x => x.Connect(key));

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
