namespace Shared;

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
            ? Some(f(value))
            : None;

    public Option<TResult> Select<TResult>(Func<TValue, Option<TResult>> f) =>
        IsSome
            ? f(value)
            : None;

    public Option<TResult> SelectMany<TInner, TResult>(Func<TValue, Option<TInner>> selector, Func<TValue, TInner, TResult> resultSelector)
    {
        if (IsNone)
        {
            return None;
        }

        var inner = selector(value);

        if (inner.IsNone)
        {
            return None;
        }

        return Some(resultSelector(value, inner.value));
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
    public static Option<TValue> Some<TValue>(TValue value) => new(value);
    public static NoneOption None => default;

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
}
