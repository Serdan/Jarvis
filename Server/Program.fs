#nowarn "20"

open System
open System.Globalization
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpOverrides
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Primitives
open ModelContextProtocol.Server
open Server
open Server.Services

let rateLimiterPolicy = "Fixed"

let private getConfiguredOptions (configuration: IConfiguration) =
    let options =
        { Auth0Domain = configuration["Auth0Domain"]
          Audience = configuration["Audience"]
          OpenAIAppsChallenge = configuration["OpenAIAppsChallenge"] }

    if String.IsNullOrWhiteSpace(options.Auth0Domain) then
        invalidOp "Auth0Domain is required."

    if String.IsNullOrWhiteSpace(options.Audience) then
        invalidOp "Audience is required."

    options

let private getDatabasePath (configuration: IConfiguration) =
    let path = configuration["DatabasePath"]

    if String.IsNullOrWhiteSpace(path) then
        invalidOp "DatabasePath is required."

    path

let configureServices (services: IServiceCollection) (configuration: IConfiguration) =
    let configured = getConfiguredOptions configuration
    let databasePath = getDatabasePath configuration

    services.Configure<JarvisOptions>(configuration) |> ignore

    services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(fun options ->
            options.Authority <- Auth.issuer configured
            options.Audience <- configured.Audience
            options.MapInboundClaims <- false
            options.RequireHttpsMetadata <- true

            let events = JwtBearerEvents()

            events.OnMessageReceived <-
                Func<MessageReceivedContext, Task>(fun context ->
                    let accessToken = context.Request.Query["access_token"].ToString()

                    if not (String.IsNullOrWhiteSpace(accessToken))
                       && context.HttpContext.Request.Path.StartsWithSegments(PathString("/client")) then
                        context.Token <- accessToken

                    Task.CompletedTask)

            events.OnChallenge <-
                Func<JwtBearerChallengeContext, Task>(fun context ->
                    context.HandleResponse()
                    context.Response.StatusCode <- StatusCodes.Status401Unauthorized

                    let resourceMetadata = Auth.resourceMetadataUri configured
                    let challenge =
                        if context.Request.Path.StartsWithSegments(PathString("/client")) then
                            sprintf "Bearer resource_metadata=\"%s\", scope=\"%s\"" resourceMetadata Auth.ClientConnect
                        else
                            sprintf "Bearer resource_metadata=\"%s\"" resourceMetadata

                    context.Response.Headers.WWWAuthenticate <- StringValues(challenge)
                    Task.CompletedTask)

            options.Events <- events)
    |> ignore

    services.AddAuthorization(fun options ->
        options.AddPolicy(
            Auth.ClientConnectPolicy,
            fun policy ->
                policy.RequireAuthenticatedUser() |> ignore
                policy.RequireAssertion(
                    Func<AuthorizationHandlerContext, bool>(fun context ->
                        Auth.hasScope Auth.ClientConnect context.User)
                )
                |> ignore
        ))
    |> ignore

    services.AddHttpContextAccessor() |> ignore

    services
        .AddMcpServer()
        .WithHttpTransport(fun options -> options.Stateless <- true)
        .WithTools<JarvisMcpTools>()
    |> ignore

    services
        .AddSignalR()
        .AddJsonProtocol()
        .AddHubOptions<HubService>(fun options ->
            options.EnableDetailedErrors <- true
            options.ClientTimeoutInterval <- TimeSpan.FromSeconds 60.0
            options.KeepAliveInterval <- TimeSpan.FromSeconds 10.0
            options.MaximumReceiveMessageSize <- Nullable<int64>(12L * 1024L * 1024L))
    |> ignore

    services
        .AddSingleton<UserService>()
        .AddSingleton<ClientResponseTracker>()
        .AddSingleton<FeedbackStore>(fun _ -> FeedbackStore(databasePath))
        .AddScoped<ClientService>()
    |> ignore

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
                        let limiter = FixedWindowRateLimiterOptions()
                        limiter.PermitLimit <- 30
                        limiter.Window <- TimeSpan.FromSeconds(10L)
                        limiter.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                        limiter.QueueLimit <- 5
                        limiter.AutoReplenishment <- true
                        limiter
                )
        )
        |> ignore)
    |> ignore

let configureApp (app: WebApplication) =
    let configured = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<JarvisOptions>>().Value

    app.UseRouting() |> ignore
    app.UseAuthentication() |> ignore
    app.UseAuthorization() |> ignore
    app.UseRateLimiter() |> ignore

    app
        .MapHub<HubService>("/client")
        .RequireAuthorization(Auth.ClientConnectPolicy)
        .RequireRateLimiting(rateLimiterPolicy)
    |> ignore

    app.MapGet("/", Func<string>(fun () -> "the future is tomorrow")) |> ignore

    app.MapGet(
        "/.well-known/openai-apps-challenge",
        Func<IResult>(fun () ->
            if String.IsNullOrWhiteSpace(configured.OpenAIAppsChallenge) then
                Results.NotFound()
            else
                Results.Text(configured.OpenAIAppsChallenge.Trim(), "text/plain"))
    )
    |> ignore

    app.MapGet(
        "/.well-known/oauth-protected-resource",
        Func<IResult>(fun () ->
            Results.Json(
                {| resource = configured.Audience
                   authorization_servers = [| Auth.issuer configured |]
                   scopes_supported = Auth.mcpScopes |> List.toArray |}
            ))
    )
    |> ignore

    app.MapMcp("/mcp") |> ignore

    app

let builder = WebApplication.CreateBuilder()

builder.Configuration.AddEnvironmentVariables("Jarvis") |> ignore
builder.Services.Configure<JarvisOptions>(builder.Configuration) |> ignore

configureServices builder.Services builder.Configuration

let app = builder.Build()

if app.Environment.IsDevelopment() then
    app.UseDeveloperExceptionPage() |> ignore
else
    app.UseForwardedHeaders(
        ForwardedHeadersOptions(
            ForwardedHeaders = (ForwardedHeaders.XForwardedFor ||| ForwardedHeaders.XForwardedProto)
        )
    )
    |> ignore

    app.UseHttpsRedirection() |> ignore

configureApp app |> ignore
app.Run()
