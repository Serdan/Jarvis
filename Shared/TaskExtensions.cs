namespace Shared;

public static class TaskExtensions
{
    public static async Task<TResult> Select<TValue, TResult>(this Task<TValue> self, Func<TValue, TResult> f) =>
        f(await self);
}
