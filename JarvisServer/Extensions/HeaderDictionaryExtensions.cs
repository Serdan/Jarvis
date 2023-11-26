using Microsoft.Extensions.Primitives;

namespace JarvisServer.Extensions;

public static class HeaderDictionaryExtensions
{
    public static Option<StringValues> Get(this IHeaderDictionary self, string key) =>
        self.TryGetValue(key, out var value)
            ? some(value)
            : none;
}
