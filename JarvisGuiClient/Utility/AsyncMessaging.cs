using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace JarvisGuiClient.Utility;

public class AsyncMessage : CollectionRequestMessage<Task>
{
    readonly CancellationTokenSource? cancellationTokenSource;

    public AsyncMessage()
    {
    }

    public AsyncMessage(CancellationToken cancellationToken)
    {
        this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public CancellationToken CancellationToken => cancellationTokenSource?.Token ?? default;
}

public static class MessengerExtensions
{
    public static void Register<TMessage>(this IMessenger messenger, object recipient, Func<object, TMessage, CancellationToken, Task> asyncHandler)
        where TMessage : AsyncMessage
    {
        messenger.Register<TMessage>(recipient, (r, message) =>
        {
            var task = asyncHandler(r, message, message.CancellationToken);
            message.Reply(task);
        });
    }

    public static void Register<TRecipient, TMessage>(this IMessenger messenger, TRecipient recipient, Func<TRecipient, TMessage, CancellationToken, Task> asyncHandler)
        where TMessage : AsyncMessage
        where TRecipient : class
    {
        messenger.Register<TRecipient, TMessage>(recipient, (r, message) =>
        {
            var task = asyncHandler(r, message, message.CancellationToken);
            message.Reply(task);
        });
    }

    public static Task SendAsync<TMessage>(this IMessenger messenger, TMessage message)
        where TMessage : AsyncMessage
    {
        messenger.Send(message);
        return Task.WhenAll(message.Responses);
    }

    public static Task SendAsync<TMessage>(this IMessenger messenger)
        where TMessage : AsyncMessage, new()
        => messenger.SendAsync(new TMessage());
}
