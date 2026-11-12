namespace Server

open System
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Authorization
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

    /// JWT and OAuth remain enforced by the caller's configured auth pipeline.
    /// No fixed-rate or concurrency quota is attached to authenticated MCP.
    let map (app: WebApplication) =
        app.MapMcp("/mcp").RequireAuthorization() |> ignore
