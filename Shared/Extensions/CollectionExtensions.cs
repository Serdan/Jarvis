using System.Collections.Immutable;

namespace Shared.Extensions;

public static class CollectionExtensions
{
    public static TSource Head<TSource>(this ImmutableArray<TSource> source) => source[0];
    public static ImmutableArray<TSource> Tail<TSource>(this ImmutableArray<TSource> source) => source[1..];
}
