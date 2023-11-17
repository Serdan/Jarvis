// ReSharper disable InconsistentNaming
namespace Shared.AlgebraicTypes;

public readonly struct Option<TValue>
{
    internal readonly TValue value;

    public bool IsSome { get; }
    public bool IsNone => !IsSome;

    public Option(TValue value)
    {
        this.value = value;
        IsSome = true;
    }

    public TResult Match<TResult>(Func<TValue, TResult> some, Func<TResult> none) =>
        IsSome
            ? some(value)
            : none();

    public Option<TResult> Select<TResult>(Func<TValue, TResult> f) =>
        IsSome
            ? some(f(value))
            : none;

    public Option<TResult> Select<TResult>(Func<TValue, Option<TResult>> f) =>
        IsSome
            ? f(value)
            : none;

    public Option<TResult> SelectMany<TInner, TResult>(Func<TValue, Option<TInner>> selector, Func<TValue, TInner, TResult> resultSelector)
    {
        if (IsNone)
        {
            return none;
        }

        var inner = selector(value);

        if (inner.IsNone)
        {
            return none;
        }

        return some(resultSelector(value, inner.value));
    }

    public Result<TValue> ToResult(string error) =>
        IsSome
            ? Ok(value)
            : Error(error);

    public static implicit operator Option<TValue>(NoneOption _) => default;
}

public readonly record struct NoneOption;

public static partial class Prelude
{
    public static Option<TValue> some<TValue>(TValue value) => new(value);
    public static NoneOption none => default;

    public static IEnumerable<TValue> filter<TValue>(IEnumerable<Option<TValue>> values)
    {
        foreach (var value in values)
        {
            if (value.IsSome)
            {
                yield return value.value;
            }
        }
    }

    public static IEnumerable<TValue> filter<TValue>(IEnumerable<Result<TValue>> values)
    {
        foreach (var value in values)
        {
            if (value.IsOk)
            {
                yield return value.value;
            }
        }
    }
}
