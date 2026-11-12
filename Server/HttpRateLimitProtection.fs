namespace Server

open System
open System.Globalization
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Primitives

/// HTTP abuse protection for the independently exposed /client and /exports
/// endpoints. Authenticated /mcp requests deliberately have no fixed quota.
module HttpRateLimitProtection =
    [<Literal>]
    let Policy = "Fixed"

    let onRejected (context: OnRejectedContext) (_: CancellationToken) =
        let seconds =
            match context.Lease.TryGetMetadata(MetadataName.RetryAfter) with
            | true, retryAfter -> max 1 (int (Math.Ceiling retryAfter.TotalSeconds))
            | _ -> 1

        context.HttpContext.Response.Headers.RetryAfter <-
            StringValues(seconds.ToString(CultureInfo.InvariantCulture))
        context.HttpContext.Response.StatusCode <- StatusCodes.Status429TooManyRequests
        ValueTask.CompletedTask

    let configure (options: RateLimiterOptions) =
        options.OnRejected <- onRejected
        options.AddPolicy(
            Policy,
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
        |> ignore
