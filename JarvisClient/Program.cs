// See https://aka.ms/new-console-template for more information

using System.Security.Cryptography;
using System.Text.Json;
using JarvisClient;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;

var connection = new HubConnectionBuilder()
                 .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Debug))
                 .AddJsonProtocol()
                 // .WithUrl("http://localhost:5271/hub")
                 .WithUrl("https://relay.kehlet.dev/hub")
                 .Build();

var browser = ProjectBrowser.Create("z:\\repos");
var client = new UserClient(connection, browser);

connection.On(client, x => x.ReceiveMessage);
connection.On(client, x => x.ReceiveCommand);

try
{
    await connection.StartAsync();

    var key = RandomNumberGenerator.GetBytes(16)
                                   .Apply(Convert.ToBase64String);

    Console.WriteLine("Key:");
    Console.WriteLine(key);

    await connection.InvokeAsync<IGptHub>(x => x.Connect(key));

    while (true)
    {
        Console.ReadLine();
        Console.WriteLine(connection.State);
        if (connection.State is HubConnectionState.Disconnected)
        {
            await connection.InvokeAsync<IGptHub>(x => x.Connect(key));
        }
    }
}
catch (Exception e)
{
    Console.WriteLine(e);
    throw;
}
