using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using JarvisClient;
using JarvisClient.Extensions;
using JarvisGuiClient.Messages;
using JarvisGuiClient.Utility;
using Kehlet.Functional.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Shared;
using Shared.SignalR;

namespace JarvisGuiClient.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProjectBrowser browser;

    [ObservableProperty]
    private string key = "";

    [ObservableProperty]
    private string projectDirectory = "z:\\repos";

    [ObservableProperty]
    private string status = "";

    public MainViewModel()
    {
        RenewKey();

        connection = new HubConnectionBuilder()
                     .AddJsonProtocol()
                     .WithUrl("https://jarvis.kehlet.dev/client")
                     .Build();

        var file = new FileSystem();
        browser = ProjectBrowser.Create(file, ProjectDirectory);
        var client = new UserClient(connection, browser);

        connection.On(client, x => x.ReceiveMessage);
        connection.On(client, x => x.ReceiveCommand);

        WeakReferenceMessenger.Default.Register<MainViewModel, ApplicationClosingMessage>(this, (recipient, _, token) => recipient.Disconnect(token));
    }

    partial void OnProjectDirectoryChanged(string value)
    {
        browser.ProjectDirectory = value;
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "Project folder"
        };
        if (dialog.ShowDialog() is true)
        {
            ProjectDirectory = dialog.SafeFolderName;
        }
    }

    [RelayCommand]
    private void RenewKey()
    {
        Key = RandomNumberGenerator.GetBytes(18)
                                   .Apply(Convert.ToBase64String);
    }

    private HubConnection? connection;

    [RelayCommand]
    private async Task Connect()
    {
        if (connection is null)
        {
            Status = "connection failed. restart";
            return;
        }

        if (Directory.Exists(ProjectDirectory) is false)
        {
            Status = "Invalid directory";
        }

        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync<IJarvisHub>(x => x.Connect(Key));
            Status = "Connected";
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
    }

    private async Task Disconnect(CancellationToken token = default)
    {
        if (connection is null || connection.State is HubConnectionState.Disconnected)
        {
            return;
        }

        await connection.InvokeAsync<IJarvisHub>(x => x.Disconnect());
        await connection.StopAsync(token);
        await connection.DisposeAsync();
        connection = null;
    }
}
