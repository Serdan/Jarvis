using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using JarvisServer;
using JarvisServer.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services
       .AddSignalR()
       .AddJsonProtocol()
       .AddHubOptions<JarvisHub>(x => x.EnableDetailedErrors = true);

builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<ClientResponseTracker>();
builder.Services.AddScoped<ClientService>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

const string rateLimiterPolicy = "Fixed";
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected += (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int) retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        return ValueTask.CompletedTask;
    };
    options.AddFixedWindowLimiter(rateLimiterPolicy, opt =>
    {
        opt.PermitLimit = 3;
        opt.Window = TimeSpan.FromSeconds(10);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 1;
    });
});

var app = builder.Build();

app.UseRateLimiter();

if (app.Environment.IsDevelopment() is false)
{
    app.UseForwardedHeaders(new()
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    app.UseHttpsRedirection();
}


app.MapGet("/version", () => "1.7");
app.MapGet("/", () => "ok");
app.MapHub<JarvisHub>("/client").RequireRateLimiting(rateLimiterPolicy);

app.MapGroup("/agent")
   .Apply(group =>
   {
       group.MapPost("/listprojects", Endpoints.ListProjects);
       group.MapPost("/getprojectdetails", Endpoints.OpenProject);
       group.MapPost("/listprojectdirectory", Endpoints.ListProjectDirectory);
       group.MapPost("/openfile", Endpoints.OpenFile);
       group.MapPost("/writefile", Endpoints.WriteFile);
       group.MapPost("/sectionreplace", Endpoints.ReplaceSection);

       return group;
   })
   .RequireRateLimiting(rateLimiterPolicy);


app.Run();
