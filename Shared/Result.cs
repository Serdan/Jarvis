namespace Shared;

public readonly struct Result<TValue>
{
    internal readonly TValue value;
    private readonly AggregateException error;

    public bool IsOk { get; }
    public bool IsError => !IsOk;

    private Result(TValue value)
    {
        this.value = value;
        error = null!;
        IsOk = true;
    }

    private Result(AggregateException error)
    {
        value = default!;
        this.error = error;
        IsOk = false;
    }

    public Result<TResult> Select<TResult>(Func<TValue, TResult> f) =>
        IsOk
            ? Ok(f(value))
            : Error(error);

    public Result<TResult> Select<TResult>(Func<TValue, Result<TResult>> f) =>
        IsOk
            ? f(value)
            : Error(error);

    public Result<TValue> Select(Func<TValue, ErrorResult> f) =>
        IsOk
            ? this
            : f(value);

    public Result<TResult> SelectMany<TInner, TResult>(Func<TValue, Result<TInner>> selector, Func<TValue, TInner, TResult> resultSelector)
    {
        if (IsError)
        {
            return Error(error);
        }

        var inner = selector(value);

        if (inner.IsError)
        {
            return Error(inner.error);
        }

        return Ok(resultSelector(value, inner.value));
    }

    public Result<TResult> SelectMany<TInner, TResult>(Func<TValue, Result<TInner>> selector, Func<TValue, TInner, Result<TResult>> resultSelector)
    {
        if (IsError)
        {
            return Error(error);
        }

        var inner = selector(value);

        if (inner.IsError)
        {
            return Error(inner.error);
        }

        return resultSelector(value, inner.value);
    }

    public async Task<Result<TResult>> Select<TResult>(Func<TValue, Task<TResult>> f) =>
        IsOk
            ? Ok(await f(value))
            : Error(error);

    public async Task<Result<Unit>> Select(Func<TValue, Task> f)
    {
        if (IsOk)
        {
            await f(value);
            return Ok(unit);
        }
        else
        {
            return Error(error);
        }
    }

    public async Task<Result<TResult>> SelectMany<TResult>(Func<TValue, Task<Result<TResult>>> f) =>
        IsOk
            ? await f(value)
            : Error(error);

    public TResult Match<TResult>(Func<TValue, TResult> ok, Func<AggregateException, TResult> error) =>
        IsOk
            ? ok(value)
            : error(this.error);
    
    public TValue IfError(Func<AggregateException, TValue> f) =>
        IsOk
            ? value
            : f(error);

    public void IfError(Action<AggregateException> f) => 
        f(error);

    public static Result<TValue> OkResult(TValue value) => new(value);
    public static Result<TValue> ErrorResult(AggregateException exception) => new(exception);

    public static implicit operator Result<TValue>(ErrorResult errorResult) => ErrorResult(errorResult.Exception);
}

public readonly record struct ErrorResult(AggregateException Exception);

public static partial class Prelude
{
    public static Result<TValue> Ok<TValue>(TValue value) => Result<TValue>.OkResult(value);
    public static ErrorResult Error(AggregateException exception) => new(exception);
    public static ErrorResult Error(Exception exception) => new(new(exception));
    public static ErrorResult Error(string message) => new(new(message));
}
