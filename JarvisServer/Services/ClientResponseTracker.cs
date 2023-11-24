using System.Collections.Concurrent;

namespace JarvisServer.Services;

public class ClientResponseTracker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> messages = new();

    public (string Id, Task<string> Task) Register()
    {
        var correlationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<string>();

        var cts = new CancellationTokenSource(60000);
        cts.Token.Register(() =>
        {
            if (messages.TryRemove(correlationId, out var source))
            {
                source.TrySetCanceled();
            }
        });
        
        messages.TryAdd(correlationId, tcs);
        return (correlationId, tcs.Task);
    }

    public void Complete(string correlationId, string result)
    {
        if (messages.TryRemove(correlationId, out var tcs))
        {
            tcs.SetResult(result);
        }
    }
}
