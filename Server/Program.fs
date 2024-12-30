#nowarn "20"

open System
open System.Globalization
open System.Threading.RateLimiting
open System.Threading.Tasks
open Common
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpOverrides
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Extensions.Primitives
open Server
open Server.Services

let rateLimiterPolicy = "Fixed"

let notFoundHandler = RequestErrors.notFound (text "Not Found")

let errorHandler (ex: Exception) (logger: ILogger) =
    logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> ServerErrors.INTERNAL_ERROR ex.Message

let accessDenied = setStatusCode 401 >=> text "Access Denied"

let validateApiKey (ctx: HttpContext) =
    match ctx.TryGetRequestHeader "X-Api-Key" with
    | Some key ->
        let opt = ctx.GetService<IOptionsSnapshot<JarvisOptions>>()
        opt.Value.ApiKey = key
    | None -> false

let requiresApiKey: HttpHandler = authorizeRequest validateApiKey accessDenied

let bind<'a> = routeBind<AgentMessage<'a>>

let agentEndpoints =
    let endpoints =
        [ bind<OpenProjectCommand> "/openProject" Endpoints.openProject
          bind<ListProjectDirectoryCommand> "/listProjectDirectory" Endpoints.listProjectDirectory
          bind<ReadFileCommand> "/readFile" Endpoints.readFile
          bind<WriteFileCommand> "/writeFile" Endpoints.writeFile
          bind<TextReplaceSectionCommand> "/textReplaceSection" Endpoints.textReplaceSection
          bind<TextReplaceCommand> "/textReplace" Endpoints.textReplace ]

    subRoute "/agent" <| requiresApiKey
    >=> noResponseCaching
    >=> POST
    >=> choose endpoints

let endpoints =
    choose [ route "/" >=> text "the future is tomorrow"; agentEndpoints; notFoundHandler ]

let configureApp (appBuilder: WebApplication) =
    appBuilder.MapHub<HubService>("/client").RequireRateLimiting(rateLimiterPolicy)

    appBuilder
        .UseGiraffeErrorHandler(errorHandler)
        .UseRouting()
        .UseGiraffe(endpoints)

let configureServices (services: IServiceCollection) =
    services.AddRouting().AddGiraffe()

    services
        .AddSignalR()
        .AddJsonProtocol()
        .AddHubOptions<HubService>(fun x -> x.EnableDetailedErrors <- true)

    services
        .AddSingleton<UserService>()
        .AddSingleton<ClientResponseTracker>()
        .AddScoped<ClientService>()

    services.AddRateLimiter(fun options ->
        options.OnRejected <-
            (fun context _ ->
                match context.Lease.TryGetMetadata(MetadataName.RetryAfter) with
                | true, retryAfter ->
                    context.HttpContext.Response.Headers.RetryAfter <-
                        (retryAfter.TotalSeconds |> int).ToString(NumberFormatInfo.InvariantInfo)
                        |> StringValues
                | _ -> ()

                context.HttpContext.Response.StatusCode <- StatusCodes.Status429TooManyRequests

                ValueTask.CompletedTask)

        options.AddFixedWindowLimiter(
            rateLimiterPolicy,
            fun opt ->
                opt.PermitLimit <- 3
                opt.Window <- TimeSpan.FromSeconds(10L)
                opt.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                opt.QueueLimit <- 1
        )

        ())

let builder = WebApplication.CreateBuilder()

builder.Configuration.AddEnvironmentVariables("Jarvis")
builder.Services.Configure<JarvisOptions>(builder.Configuration)

configureServices builder.Services

let app = builder.Build()

app.UseRateLimiter()

if app.Environment.IsDevelopment() then
    app.UseDeveloperExceptionPage()

    ()
else
    app.UseForwardedHeaders(
        ForwardedHeadersOptions(
            ForwardedHeaders = (ForwardedHeaders.XForwardedFor ||| ForwardedHeaders.XForwardedProto)
        )
    )

    app.UseHttpsRedirection()

    ()


configureApp app
app.Run()
