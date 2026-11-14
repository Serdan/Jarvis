#nowarn "20"

open System
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
            options.TokenValidationParameters.ValidAudiences <- Auth.validAudiences configured
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
        .AddMcpServer(fun options -> options.ServerInstructions <- InstructionGuidance.Text)
        .WithHttpTransport(fun options -> options.Stateless <- true)
        .WithRequestFilters(fun filters ->
            filters.AddCallToolFilter(McpEndpoint.deviceRoutingFilter()) |> ignore)
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
        // Registration generations are protocol identities: a process-local
        // counter would restart at 1 and could match an unresolved correction
        // permit from a previous server boot. Allocate from durable SQLite.
        .AddSingleton<FeedbackStore>(fun _ -> FeedbackStore(databasePath))
        .AddSingleton<UserService>(fun provider ->
            UserService(
                (fun () -> DateTimeOffset.UtcNow),
                UserRetentionPolicy.defaults,
                generationAllocator = (fun userId registration connectionId ->
                    provider.GetRequiredService<FeedbackStore>()
                        .AllocateDeviceRegistrationGeneration(
                            userId, registration.DeviceId, connectionId,
                            registration.ProtocolVersion, registration.ClientVersion))))
        .AddSingleton<ClientResponseTracker>()
        .AddSingleton<ExportStore>(fun _ -> new ExportStore())
        .AddScoped<ClientService>()
    |> ignore

    services.AddRateLimiter(HttpRateLimitProtection.configure) |> ignore

let configureApp (app: WebApplication) =
    let configured = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<JarvisOptions>>().Value

    app.UseRouting() |> ignore
    app.UseAuthentication() |> ignore
    app.UseAuthorization() |> ignore
    app.UseRateLimiter() |> ignore

    app
        .MapHub<HubService>("/client")
        .RequireAuthorization(Auth.ClientConnectPolicy)
        .RequireRateLimiting(HttpRateLimitProtection.Policy)
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

    app.MapGet(
        "/exports/{token}",
        Func<HttpContext, string, ExportStore, IResult>(fun context token store ->
            match store.TryGet token with
            | ValueNone -> Results.NotFound()
            | ValueSome exported ->
                context.Response.Headers.CacheControl <- StringValues("no-store")
                context.Response.Headers["X-Content-Type-Options"] <- StringValues("nosniff")
                context.Response.Headers["Referrer-Policy"] <- StringValues("no-referrer")

                let disposition =
                    System.Net.Http.Headers.ContentDispositionHeaderValue(
                        if ExportMimeType.isInlineSafe exported.MimeType then "inline" else "attachment"
                    )
                disposition.FileNameStar <- exported.FileName
                context.Response.Headers.ContentDisposition <- StringValues(disposition.ToString())

                Results.Bytes(exported.Data, exported.MimeType) :> IResult)
    )
    |> fun endpoint -> endpoint.RequireRateLimiting(HttpRateLimitProtection.Policy)
    |> ignore

    // No speculative concurrency or request-rate quota applies to authenticated MCP.
    // Authenticate MCP initialization/discovery at the HTTP boundary so clients
    // receive an OAuth Bearer challenge before accessing tools. Tool-specific
    // scope checks continue to run independently inside McpTools.
    McpEndpoint.map app

    app

let builder = WebApplication.CreateBuilder()

builder.Configuration.AddEnvironmentVariables("Jarvis") |> ignore
builder.Services.Configure<JarvisOptions>(builder.Configuration) |> ignore

configureServices builder.Services builder.Configuration

let app = builder.Build()

// Resolve the persistent SQLite dependency before accepting readiness traffic.
// Without this, the public discovery and auth routes can appear healthy while
// the feedback/operation store cannot even initialize.
app.Services.GetRequiredService<FeedbackStore>() |> ignore

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
