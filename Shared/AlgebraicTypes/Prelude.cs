// ReSharper disable InconsistentNaming

namespace Shared.AlgebraicTypes;

public static partial class Prelude
{
    public static Unit unit { get; } = new();

    public static ResultUnion<TValue> union<TValue>(Result<TValue> result) =>
        result.Match<ResultUnion<TValue>>(
            x => new ResultUnion<TValue>.Ok(x),
            x => new ResultUnion<TValue>.Error(x)
        );

    public static OptionUnion<T> union<T>(Option<T> result) =>
        result.Match<OptionUnion<T>>(
            x => new OptionUnion<T>.Some(x!),
            () => new OptionUnion<T>.None()
        );

    public static Result<T> @try<T>(Action f, T success)
    {
        try
        {
            f();
            return Ok(success);
        }
        catch (Exception e)
        {
            return Error(e);
        }
    }

    public static Result<T> @try<T>(Func<T> f)
    {
        try
        {
            return Ok(f());
        }
        catch (Exception e)
        {
            return Error(e);
        }
    }
}
