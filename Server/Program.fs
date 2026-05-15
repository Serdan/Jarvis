#nowarn "20"

open System
open System.Globalization
open System.Threading.RateLimiting
open System.Text.Json
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

let notFoundHandler: HttpHandler = RequestErrors.notFound (text "Not Found")

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

let private jsonOptions =
    JsonSerializerOptions(JsonSerializerDefaults.Web)

let bind<'a> path (handler: AgentMessage<'a> -> HttpHandler) : HttpHandler =
    route path
    >=> fun next ctx ->
        task {
            let! message = JsonSerializer.DeserializeAsync<AgentMessage<'a>>(ctx.Request.Body, jsonOptions)
            return! handler message next ctx
        }

let agentEndpoints =
    let endpoints =
        [ bind<ListCommandsCommand> "/listCommands" Endpoints.listCommands
          bind<ListProjectsCommand> "/listProjects" Endpoints.listProjects
          bind<GetProjectDetailsCommand> "/getProjectDetails" Endpoints.getProjectDetails
          bind<ListDirectoryCommand> "/listDirectory" Endpoints.listProjectDirectory
          bind<SearchFilesCommand> "/searchFiles" Endpoints.searchFiles
          bind<SearchTextCommand> "/searchText" Endpoints.searchText
          bind<ReadFileCommand> "/readFile" Endpoints.readFile
          bind<ReadFilesCommand> "/readFiles" Endpoints.readFiles
          // Compatibility aliases for older deployed action schemas.
          bind<GetProjectDetailsCommand> "/openProject" Endpoints.getProjectDetails
          bind<ListDirectoryCommand> "/listProjectDirectory" Endpoints.listProjectDirectory
          bind<ReadFileCommand> "/openfile" Endpoints.readFile
          bind<ReadFileCommand> "/readfile" Endpoints.readFile
          bind<WriteFileCommand> "/writeFile" Endpoints.writeFile
          bind<PatchFileCommand> "/patchFile" Endpoints.patchFile
          bind<RunCommandCommand> "/runCommand" Endpoints.runCommand
          bind<GitStatusCommand> "/getGitStatus" Endpoints.getGitStatus
          bind<GitDiffCommand> "/getGitDiff" Endpoints.getGitDiff
          bind<GitCommitCommand> "/gitCommit" Endpoints.gitCommit
          bind<StartJobCommand> "/startJob" Endpoints.startJob
          bind<ListJobsCommand> "/listJobs" Endpoints.listJobs
          bind<GetJobResultCommand> "/getJobResult" Endpoints.getJobResult
          bind<CancelJobCommand> "/cancelJob" Endpoints.cancelJob ]

    requiresApiKey >=> noResponseCaching >=> POST >=> choose endpoints

let configureApp (appBuilder: WebApplication) =
    appBuilder.UseGiraffeErrorHandler(errorHandler) |> ignore
    appBuilder.UseRouting() |> ignore

    appBuilder
        .MapHub<HubService>("/client")
        .RequireRateLimiting(rateLimiterPolicy)
    |> ignore

    appBuilder.MapGet("/", Func<string>(fun () -> "the future is tomorrow")) |> ignore

    appBuilder.Map(
        "/agent",
        Action<IApplicationBuilder>(fun (branch) ->
            branch.UseGiraffe(agentEndpoints))
    ) |> ignore

    appBuilder

let configureServices (services: IServiceCollection) =
    services.AddRouting().AddGiraffe()

    services
        .AddSignalR()
        .AddJsonProtocol()
        .AddHubOptions<HubService>(fun x ->
            x.EnableDetailedErrors <- true
            x.MaximumReceiveMessageSize <- Nullable<int64>(1024L * 1024L))

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
