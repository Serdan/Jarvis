namespace JarvisServer.Extensions;

public static class ApplicationBuilderExtensions
{
    public static void Use<TMiddleware>(this IApplicationBuilder app, string path)
    {
        app.UseWhen(context => context.Request.Path.StartsWithSegments(path), x => x.UseMiddleware<TMiddleware>());
    }
}
