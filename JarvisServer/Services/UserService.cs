using System.Collections.Concurrent;
using Shared.AlgebraicTypes;

namespace JarvisServer.Services;

public class UserService
{
    private readonly ConcurrentDictionary<string, string> users = new();

    public void Add(string key, string connectionId)
    {
        users[key] = connectionId;
    }

    public void Remove(string connectionId)
    {
        var key = users.FirstOrDefault(x => x.Value == connectionId).Key;
        if (key is not null)
        {
            users.Remove(key, out _);
        }
    }

    public Result<string> GetConnectionId(string key) =>
        users.TryGetValue(key, out var value)
            ? Ok(value)
            : Error($"User not found with key: {key}");
}
