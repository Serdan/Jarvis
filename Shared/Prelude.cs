// ReSharper disable InconsistentNaming

namespace Shared;

public static partial class Prelude
{
    public static Unit unit { get; } = new();

    public static Unions.Result union<T>(Result<T> result) =>
        result.Match<Unions.Result>(
            x => new Unions.Result.Ok(x!),
            x => new Unions.Result.Error(x)
        );

    public static Unions.Result<TValue> union2<TValue>(Result<TValue> result) =>
        result.Match<Unions.Result<TValue>>(
            x => new Unions.Result<TValue>.Ok(x),
            x => new Unions.Result<TValue>.Error(x)
        );

    public static Unions.Option union<T>(Option<T> result) =>
        result.Match<Unions.Option>(
            x => new Unions.Option.Some(x!),
            () => new Unions.Option.None()
        );

    public static OptionUnion<T> union2<T>(Option<T> result) =>
        result.Match<OptionUnion<T>>(
            x => new OptionUnion<T>.Some(x!),
            () => new OptionUnion<T>.None()
        );

    public static Unit call(Action action)
    {
        action();
        return unit;
    }
}
