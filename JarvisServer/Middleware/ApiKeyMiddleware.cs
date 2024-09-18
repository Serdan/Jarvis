using JarvisServer.Extensions;
using Microsoft.Extensions.Options;
using static Kehlet.Functional.ResultUnion<Kehlet.Functional.Unit>;

#pragma warning disable CS8509 // The switch expression does not handle all possible values of its input type (it is not exhaustive).

namespace JarvisServer.Middleware;

public class ApiKeyMiddleware(RequestDelegate next)
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    public Task InvokeAsync(HttpContext context, IOptions<JarvisOptions> options)
    {
        var result =
            from headerValue in context.Request.Headers
                                       .Get(ApiKeyHeaderName)
                                       .ToResult("API Key is missing")
            select options.Value.ApiKey.Equals(headerValue)
                ? ok(unit)
                : error("Unauthorized client");

        return union(result) switch
        {
            Ok => next(context),
            Error(var exception) => WriteUnauthorized(context.Response, exception.Message)
        };
    }

    private static Task WriteUnauthorized(HttpResponse response, string message)
    {
        response.StatusCode = 401;
        return response.WriteAsync(message);
    }
}
