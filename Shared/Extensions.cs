namespace Shared;

public static class Extensions
{
    public static TResult Apply<T, TResult>(this T self, Func<T, TResult> f) => f(self);

    public static async Task<Unit> ToUnitTask(this Task task)
    {
        await task;
        return new Unit();
    }
}
