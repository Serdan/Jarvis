using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using JarvisGuiClient.Messages;
using JarvisGuiClient.Utility;

namespace JarvisGuiClient;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    protected override void OnExit(ExitEventArgs e)
    {
        WeakReferenceMessenger.Default.SendAsync<ApplicationClosingMessage>().Wait();

        base.OnExit(e);
    }
    
    
}
