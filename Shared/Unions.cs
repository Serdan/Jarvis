namespace Shared;

public static class Unions
{
    public record Result<T>
    {
        private Result()
        {
        }

        public sealed record Ok(T Value) : Result<T>;

        public sealed record Error(AggregateException Exception) : Result<T>;
    }

    public record Result
    {
        private Result()
        {
        }

        public sealed record Ok(object Value) : Result;

        public sealed record Error(AggregateException Exception) : Result;
    }
}
