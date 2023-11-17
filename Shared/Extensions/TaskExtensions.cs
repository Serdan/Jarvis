namespace Shared.Extensions;

public static class TaskExtensions
{
    public static async Task<TResult> Select<TValue, TResult>(this Task<TValue> self, Func<TValue, TResult> f) =>
        f(await self);

    public static async Task<Unit> ToUnitTask(this Task task)
    {
        await task;
        return unit;
    }
}
