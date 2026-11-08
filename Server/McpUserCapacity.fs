namespace Server

open System
open System.Collections.Generic
open System.Globalization
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Primitives

/// Budget applies to an authenticated subject, never to an IP address.
/// Unlike HTTP discovery, tools/call may wait minutes for a connected client.
type McpLoadSettings =
    { MaxConcurrentTools: int
      MaxQueuedTools: int
      QueueWaitMilliseconds: int
      RequestsPerWindow: int
      WindowSeconds: int
      BusyRetrySeconds: int }

module McpLoadSettings =
    let defaults =
        { MaxConcurrentTools = 24
          MaxQueuedTools = 16
          QueueWaitMilliseconds = 5000
          RequestsPerWindow = 240
          WindowSeconds = 10
          BusyRetrySeconds = 2 }

    let fromConfiguration (configuration: IConfiguration) =
        let read name defaultValue minimum maximum =
            let key = "McpLimits:" + name
            let raw = configuration[key]
            if String.IsNullOrWhiteSpace raw then defaultValue
            else
                let mutable parsed = 0
                if not (Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, &parsed))
                   || parsed < minimum || parsed > maximum then
                    invalidOp $"McpLimits:{name} must be between {minimum} and {maximum}."
                parsed
        { MaxConcurrentTools = read "MaxConcurrentTools" defaults.MaxConcurrentTools 1 1024
          MaxQueuedTools = read "MaxQueuedTools" defaults.MaxQueuedTools 0 1024
          QueueWaitMilliseconds = read "QueueWaitMilliseconds" defaults.QueueWaitMilliseconds 1 60000
          RequestsPerWindow = read "RequestsPerWindow" defaults.RequestsPerWindow 1 100000
          WindowSeconds = read "WindowSeconds" defaults.WindowSeconds 1 3600
          BusyRetrySeconds = read "BusyRetrySeconds" defaults.BusyRetrySeconds 1 60 }

module McpSubjectRate =
    [<Literal>]
    let Policy = "McpSubjectRate"

    let partition (settings: McpLoadSettings) (context: HttpContext) =
        // Tokens with no subject stay in a single constrained partition.
        // Authentication still runs first; tools require a real subject.
        let subject = Auth.tryUserId context.User |> Option.defaultValue "<missing-subject>"
        let key = Auth.profileId subject
        RateLimitPartition.GetFixedWindowLimiter(
            key,
            fun _ ->
                let limiter = FixedWindowRateLimiterOptions()
                limiter.PermitLimit <- settings.RequestsPerWindow
                limiter.Window <- TimeSpan.FromSeconds(float settings.WindowSeconds)
                limiter.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                limiter.QueueLimit <- 0
                limiter.AutoReplenishment <- true
                limiter
        )

    let onRejected (context: OnRejectedContext) (_: CancellationToken) =
        let seconds =
            match context.Lease.TryGetMetadata(MetadataName.RetryAfter) with
            | true, retryAfter -> max 1 (int (Math.Ceiling retryAfter.TotalSeconds))
            | _ -> 1
        context.HttpContext.Response.Headers.RetryAfter <-
            StringValues(seconds.ToString(CultureInfo.InvariantCulture))
        context.HttpContext.Response.StatusCode <- StatusCodes.Status429TooManyRequests
        ValueTask.CompletedTask

type ToolAdmission<'a> =
    | Executed of 'a
    | Busy of retryAfterSeconds: int

type private SubjectCapacity(limiter: ConcurrencyLimiter) =
    member _.Limiter = limiter
    member val References = 0 with get, set
    interface IDisposable with
        member _.Dispose() = limiter.Dispose()

/// Single-instance server resource admission, with bounded FIFO queues and
/// prompt queue cancellation. Entries are retired as soon as their last active
/// or queued call completes, so the subject dictionary cannot grow indefinitely.
type McpUserCapacity(settings: McpLoadSettings) =
    let gate = obj()
    let partitions = Dictionary<string, SubjectCapacity>(StringComparer.Ordinal)

    let getPartition subject =
        lock gate (fun () ->
            let key = Auth.profileId subject
            let current =
                match partitions.TryGetValue key with
                | true, existing -> existing
                | _ ->
                    let options = ConcurrencyLimiterOptions()
                    options.PermitLimit <- settings.MaxConcurrentTools
                    options.QueueLimit <- settings.MaxQueuedTools
                    options.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                    let created = new SubjectCapacity(new ConcurrencyLimiter(options))
                    partitions.Add(key, created)
                    created
            current.References <- current.References + 1
            key, current)

    let release key (partition: SubjectCapacity) =
        lock gate (fun () ->
            partition.References <- partition.References - 1
            if partition.References = 0 then
                partitions.Remove(key) |> ignore
                (partition :> IDisposable).Dispose())

    member _.ActiveSubjectCount = lock gate (fun () -> partitions.Count)

    member _.Execute<'a>(
        subject: string,
        cancellation: CancellationToken,
        operation: unit -> Task<'a>
    ) : Task<ToolAdmission<'a>> =
        task {
            cancellation.ThrowIfCancellationRequested()
            if String.IsNullOrWhiteSpace subject then
                invalidArg "subject" "An authenticated MCP subject is required."
            let key, partition = getPartition subject
            try
                // This deadline applies only while queued; it does not cancel
                // a legitimately running tool that needs minutes to finish.
                use queueDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
                queueDeadline.CancelAfter(settings.QueueWaitMilliseconds)
                let! outcome =
                    task {
                        try
                            let! lease = partition.Limiter.AcquireAsync(1, queueDeadline.Token).AsTask()
                            return Some lease
                        with :? OperationCanceledException when not cancellation.IsCancellationRequested ->
                            // Timed out in the bounded queue.
                            return None
                    }
                match outcome with
                | None -> return Busy settings.BusyRetrySeconds
                | Some permit ->
                    use lease = permit
                    if not lease.IsAcquired then return Busy settings.BusyRetrySeconds
                    else
                        // Queue deadline does not apply once the tool is
                        // admitted; only the request's own cancellation does.
                        queueDeadline.CancelAfter(Timeout.InfiniteTimeSpan)
                        cancellation.ThrowIfCancellationRequested()
                        let! result = operation()
                        return Executed result
            finally
                release key partition
        }
