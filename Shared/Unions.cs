using ExhaustiveMatching;

namespace Shared;

public static class Unions
{
    public abstract record Result<T>
    {
        private Result()
        {
        }

        public sealed record Ok(T Value) : Result<T>;

        public sealed record Error(AggregateException Exception) : Result<T>;
    }

    public abstract record Result
    {
        private Result()
        {
        }

        public sealed record Ok(object Value) : Result;

        public sealed record Error(AggregateException Exception) : Result;
    }

    public abstract record Option
    {
        private Option()
        {
        }

        public sealed record Some(object Value) : Option;

        public sealed record None : Option;
    }
}

public abstract partial record OptionUnion<TValue>
{
    private OptionUnion()
    {
    }

    public sealed partial record Some(TValue Value) : OptionUnion<TValue>;

    public sealed partial record None : OptionUnion<TValue>;
}
