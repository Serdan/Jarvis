namespace Server

open System
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Http
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server

/// Production MCP endpoint and per-call routing are shared with the HTTP
/// regression fixture so tests exercise the actual dispatch policy.
module McpEndpoint =
    // Zero-cardinality tool dispatch measurements. No subject, payload, token,
    // or agent ID is recorded, and metrics never gate admission.
    let private meter = new Meter("Jarvis.Mcp")
    let private inFlight = meter.CreateUpDownCounter<int64>("jarvis.mcp.tool.inflight")
    let private duration = meter.CreateHistogram<double>("jarvis.mcp.tool.duration", "ms")

    let deviceRoutingFilter () =
        McpRequestFilter<CallToolRequestParams, CallToolResult>(fun next ->
            McpRequestHandler<_, _>(fun context cancellation ->
                ValueTask<CallToolResult>(
                    task {
                        inFlight.Add(1L)
                        let started = Stopwatch.GetTimestamp()
                        try
                            return! RequestDeviceRouting.withMetadata context.Params (fun () ->
                                next.Invoke(context, cancellation).AsTask())
                        finally
                            inFlight.Add(-1L)
                            duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds)
                    }
                )))

    /// The account OAuth subject and selected local client do not establish
    /// the identity of a particular inference execution. In particular, the
    /// generic MCP endpoint is not a provider profile's callable ceiling.
    /// Do not manufacture a successful ceiling observation from a filtered
    /// tool list, a route label, or a model-supplied actor or project.
    let tryProviderCallableCeiling (endpoint: PathString) : Result<Set<string>, string> =
        if String.Equals(endpoint.Value, "/mcp", StringComparison.OrdinalIgnoreCase)
           || String.Equals(endpoint.Value, "/mcp/", StringComparison.OrdinalIgnoreCase) then
            Error "The generic MCP endpoint is not a scoped provider endpoint."
        else
            Error "Authenticated provider launch, project and role binding unavailable."

    /// JWT and OAuth remain enforced by the caller's configured auth pipeline.
    /// No fixed-rate or concurrency quota is attached to authenticated MCP.
    let map (app: WebApplication) =
        app.MapMcp("/mcp").RequireAuthorization() |> ignore

        // Reserve a distinct provider namespace without exposing any MCP
        // handler there. All methods and descendants fail closed until an
        // independently verified transport binding and per-call ceiling
        // exist. Never fall through to the generic authorized manual route.
        app.Map("/mcp/provider/{**scope}", RequestDelegate(fun context ->
            context.Response.StatusCode <- StatusCodes.Status403Forbidden
            let reason =
                match tryProviderCallableCeiling context.Request.Path with
                | Error reason -> reason
                | Ok _ -> "Scoped provider dispatch is not enabled."
            context.Response.WriteAsync(reason)))
            .RequireAuthorization() |> ignore
