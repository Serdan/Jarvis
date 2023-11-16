using System.Text.Json;
using System.Text.Json.Serialization;
using JarvisServer;
using JarvisServer.Services;
using Microsoft.AspNetCore.HttpOverrides;

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

var app = builder.Build();

if (app.Environment.IsDevelopment() is false)
{
    app.UseForwardedHeaders(new()
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    app.UseHttpsRedirection();
}


app.MapGet("/version", () => "1.4");
app.MapGet("/", () => "ok");
app.MapHub<JarvisHub>("/hub");

app.MapPost("/hub/listprojects", Endpoints.ListProjects);
app.MapPost("/hub/getprojectdetails", Endpoints.OpenProject);
app.MapPost("/hub/listprojectdirectory", Endpoints.ListProjectDirectory);
app.MapPost("/hub/openfile", Endpoints.OpenFile);

app.Run();
