#nowarn "20"

open System
open System.Globalization
open System.Security.Cryptography
open System.Threading.RateLimiting
open System.Text
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
open ModelContextProtocol.Server
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

let private fixedTimeEquals (expected: string) (provided: string) =
    if String.IsNullOrEmpty(expected) || String.IsNullOrEmpty(provided) then
        false
    else
        let expectedBytes = Encoding.UTF8.GetBytes(expected)
        let providedBytes = Encoding.UTF8.GetBytes(provided)
        CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes)

let validateMcpApiKey (ctx: HttpContext) =
    let authorization = ctx.Request.Headers.Authorization.ToString()
    let bearerPrefix = "Bearer "

    if authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) then
        let provided = authorization.Substring(bearerPrefix.Length).Trim()
        let options = ctx.RequestServices.GetRequiredService<IOptionsSnapshot<JarvisOptions>>()
        fixedTimeEquals options.Value.McpApiKey provided
    else
        false

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
        [ bind<ListCommandsCommand> (AgentProtocol.legacyRoute "ListCommands") Endpoints.listCommands
          bind<ListProjectsCommand> (AgentProtocol.legacyRoute "ListProjects") Endpoints.listProjects
          bind<GetProjectDetailsCommand> (AgentProtocol.legacyRoute "GetProjectDetails") Endpoints.getProjectDetails
          bind<ListDirectoryCommand> (AgentProtocol.legacyRoute "ListDirectory") Endpoints.listProjectDirectory
          bind<SearchFilesCommand> (AgentProtocol.legacyRoute "SearchFiles") Endpoints.searchFiles
          bind<SearchTextCommand> (AgentProtocol.legacyRoute "SearchText") Endpoints.searchText
          bind<ReadFileCommand> (AgentProtocol.legacyRoute "ReadFile") Endpoints.readFile
          bind<ReadFilesCommand> (AgentProtocol.legacyRoute "ReadFiles") Endpoints.readFiles
          // Compatibility aliases for older deployed action schemas.
          bind<GetProjectDetailsCommand> "/openProject" Endpoints.getProjectDetails
          bind<ListDirectoryCommand> "/listProjectDirectory" Endpoints.listProjectDirectory
          bind<ReadFileCommand> "/openfile" Endpoints.readFile
          bind<ReadFileCommand> "/readfile" Endpoints.readFile
          bind<WriteFileCommand> (AgentProtocol.legacyRoute "WriteFile") Endpoints.writeFile
          bind<PatchFileCommand> (AgentProtocol.legacyRoute "PatchFile") Endpoints.patchFile
          bind<RunCommandCommand> (AgentProtocol.legacyRoute "RunCommand") Endpoints.runCommand
          bind<ListProjectTasksCommand> (AgentProtocol.legacyRoute "ListProjectTasks") Endpoints.listProjectTasks
          bind<RunProjectTaskCommand> (AgentProtocol.legacyRoute "RunProjectTask") Endpoints.runProjectTask
          bind<GitStatusCommand> (AgentProtocol.legacyRoute "GetGitStatus") Endpoints.getGitStatus
          bind<GitDiffCommand> (AgentProtocol.legacyRoute "GetGitDiff") Endpoints.getGitDiff
          bind<GitCommitCommand> (AgentProtocol.legacyRoute "GitCommit") Endpoints.gitCommit
          bind<StartJobCommand> (AgentProtocol.legacyRoute "StartJob") Endpoints.startJob
          bind<ListJobsCommand> (AgentProtocol.legacyRoute "ListJobs") Endpoints.listJobs
          bind<GetJobResultCommand> (AgentProtocol.legacyRoute "GetJobResult") Endpoints.getJobResult
          bind<CancelJobCommand> (AgentProtocol.legacyRoute "CancelJob") Endpoints.cancelJob ]

    requiresApiKey >=> noResponseCaching >=> POST >=> choose endpoints

let configureApp (appBuilder: WebApplication) =
    appBuilder.UseGiraffeErrorHandler(errorHandler) |> ignore
    appBuilder.UseRouting() |> ignore

    appBuilder.UseWhen(
        (fun ctx -> ctx.Request.Path.StartsWithSegments(PathString("/mcp"))),
        (fun branch ->
            branch.Use(
                Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                    task {
                        if validateMcpApiKey ctx then
                            do! next.Invoke(ctx)
                        else
                            ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
                            ctx.Response.Headers.WWWAuthenticate <- StringValues("Bearer")
                            do! ctx.Response.WriteAsync("Unauthorized")
                    }))
            |> ignore)
    )
    |> ignore

    appBuilder
        .MapHub<HubService>("/client")
        .RequireRateLimiting(rateLimiterPolicy)
    |> ignore

    appBuilder.MapGet("/", Func<string>(fun () -> "the future is tomorrow")) |> ignore

    appBuilder.MapMcp("/mcp") |> ignore

    appBuilder.Map(
        "/agent",
        Action<IApplicationBuilder>(fun (branch) ->
            branch.UseGiraffe(agentEndpoints))
    ) |> ignore

    appBuilder

let configureServices (services: IServiceCollection) =
    services.AddRouting().AddGiraffe()

    services
        .AddMcpServer()
        .WithHttpTransport(fun options -> options.Stateless <- true)
        .WithTools<JarvisMcpTools>()
    |> ignore

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

        options.AddPolicy(
            rateLimiterPolicy,
            fun context ->
                let partitionKey =
                    match context.Connection.RemoteIpAddress with
                    | null -> "unknown"
                    | address -> address.ToString()

                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    fun _ ->
                        let opt = FixedWindowRateLimiterOptions()
                        opt.PermitLimit <- 30
                        opt.Window <- TimeSpan.FromSeconds(10L)
                        opt.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                        opt.QueueLimit <- 5
                        opt.AutoReplenishment <- true
                        opt
                )
        )
        |> ignore

        ())

let builder = WebApplication.CreateBuilder()

builder.Configuration.AddEnvironmentVariables("Jarvis")
builder.Services.Configure<JarvisOptions>(builder.Configuration)

configureServices builder.Services

let app = builder.Build()

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

app.UseRateLimiter()

configureApp app
app.Run()
