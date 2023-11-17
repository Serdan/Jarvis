namespace Shared.Extensions;

public static class Extensions
{
    public static TResult Apply<T, TResult>(this T self, Func<T, TResult> f) => f(self);
}
