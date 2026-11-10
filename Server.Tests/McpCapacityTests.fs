module ServerMcpCapacityTests

open System
open System.Net
open System.Security.Claims
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open global.Server

let private settings active queued waitMs =
    { McpLoadSettings.defaults with
        MaxConcurrentTools = active
        MaxQueuedTools = queued
        QueueWaitMilliseconds = waitMs }

let private hold () = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let private succeeded = function
    | Executed _ -> true
    | Busy _ -> false

[<Test>]
let configuration_defaults_support_the_observed_activity_burst () =
    let config = ConfigurationBuilder().AddInMemoryCollection().Build()
    let limits = McpLoadSettings.fromConfiguration config
    Assert.That(limits.MaxConcurrentTools, Is.EqualTo(24))
    Assert.That(limits.MaxQueuedTools, Is.EqualTo(16))
    Assert.That(limits.QueueWaitMilliseconds, Is.EqualTo(5000))
    Assert.That(limits.RequestsPerWindow, Is.EqualTo(240))
    Assert.That(limits.WindowSeconds, Is.EqualTo(10))

[<Test>]
let capacity_configuration_overrides_are_validated () =
    let from pairs =
        let data = System.Collections.Generic.Dictionary<string, string>()
        for key, value in pairs do data[key] <- value
        ConfigurationBuilder().AddInMemoryCollection(data).Build()
    let valid =
        from [ "McpLimits:MaxConcurrentTools", "8"
               "McpLimits:MaxQueuedTools", "0"
               "McpLimits:QueueWaitMilliseconds", "250"
               "McpLimits:RequestsPerWindow", "24"
               "McpLimits:WindowSeconds", "5"
               "McpLimits:BusyRetrySeconds", "3" ]
        |> McpLoadSettings.fromConfiguration
    Assert.That(valid.MaxConcurrentTools, Is.EqualTo(8))
    Assert.That(valid.MaxQueuedTools, Is.Zero)
    Assert.That(valid.QueueWaitMilliseconds, Is.EqualTo(250))
    Assert.That(valid.RequestsPerWindow, Is.EqualTo(24))
    Assert.That(valid.WindowSeconds, Is.EqualTo(5))
    Assert.That(valid.BusyRetrySeconds, Is.EqualTo(3))
    for key, value in [
        "MaxConcurrentTools", "0"
        "MaxQueuedTools", "-1"
        "QueueWaitMilliseconds", "0"
        "RequestsPerWindow", "0"
        "WindowSeconds", "99999"
        "BusyRetrySeconds", "garbage" ] do
        let config = from [ "McpLimits:" + key, value ]
        Assert.Throws<InvalidOperationException>(TestDelegate(fun () ->
            McpLoadSettings.fromConfiguration(config) |> ignore)) |> ignore

[<Test>]
let subject_scoped_fixed_window_does_not_couple_same_ip_users_and_returns_retry_metadata () =
    let configured = { McpLoadSettings.defaults with RequestsPerWindow = 4; WindowSeconds = 10 }
    use limiter =
        PartitionedRateLimiter.Create<HttpContext,string>(
            fun context -> McpSubjectRate.partition configured context)
    let contextFor userId =
        let context = DefaultHttpContext()
        context.Connection.RemoteIpAddress <- IPAddress.Loopback
        context.User <-
            ClaimsPrincipal(ClaimsIdentity([ Claim("sub", userId) ], "test-auth"))
        context :> HttpContext
    let a = contextFor "user-a"
    let b = contextFor "user-b"
    for i in 1 .. 4 do
        use accepted = limiter.AttemptAcquire(a)
        Assert.That(accepted.IsAcquired, Is.True, $"A request {i}")
    use overloaded = limiter.AttemptAcquire(a)
    Assert.That(overloaded.IsAcquired, Is.False)
    match overloaded.TryGetMetadata(MetadataName.RetryAfter) with
    | true, retryAfter ->
        Assert.That(retryAfter, Is.GreaterThan(TimeSpan.Zero))
        Assert.That(retryAfter, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(10.)))
    | _ -> Assert.Fail("Rejected HTTP MCP subject quota must give a Retry-After hint.")
    for i in 1 .. 4 do
        use accepted = limiter.AttemptAcquire(b)
        Assert.That(accepted.IsAcquired, Is.True, $"B request {i}")
    use overloadedB = limiter.AttemptAcquire(b)
    Assert.That(overloadedB.IsAcquired, Is.False)

[<Test>]
let separate_users_get_independent_capacity_when_one_is_saturated () =
    task {
        let configured = settings 2 1 2000
        let budget = McpUserCapacity(configured)
        let release = hold()
        let waiting value () =
            task {
                do! release.Task
                return value
            }
        let a1 = budget.Execute("user-a", CancellationToken.None, waiting 1)
        let a2 = budget.Execute("user-a", CancellationToken.None, waiting 2)
        let a3 = budget.Execute("user-a", CancellationToken.None, waiting 3)
        let! busy = budget.Execute("user-a", CancellationToken.None, fun () -> Task.FromResult 4)
        match busy with
        | Busy retry -> Assert.That(retry, Is.EqualTo(configured.BusyRetrySeconds))
        | Executed _ -> Assert.Fail("User A should be full at two active and one queued.")
        let! independent = budget.Execute("user-b", CancellationToken.None, fun () -> Task.FromResult 99)
        Assert.That(independent, Is.EqualTo(Executed 99))
        Assert.That(budget.ActiveSubjectCount, Is.EqualTo(1), "User B partition should retire immediately.")
        release.SetResult(())
        let! others = Task.WhenAll([| a1; a2; a3 |])
        Assert.That(others |> Array.forall succeeded, Is.True)
        Assert.That(budget.ActiveSubjectCount, Is.Zero)
    }

[<Test>]
let queued_tool_calls_timeout_without_starting_work_or_retaining_subjects () =
    task {
        let budget = McpUserCapacity(settings 1 1 80)
        let release = hold()
        let first = budget.Execute("subject", CancellationToken.None, fun () ->
            task {
                do! release.Task
                return 1
            })
        let mutable ran = false
        let! waiting =
            budget.Execute("subject", CancellationToken.None, fun () ->
                task {
                    ran <- true
                    return 2
                })
        Assert.That(waiting, Is.EqualTo(box (Busy 2 : ToolAdmission<int>)))
        Assert.That(ran, Is.False)
        Assert.That(budget.ActiveSubjectCount, Is.EqualTo(1))
        release.SetResult(())
        let! finished = first
        Assert.That(finished, Is.EqualTo(Executed 1))
        Assert.That(budget.ActiveSubjectCount, Is.Zero)
    }

[<Test>]
let cancelled_queued_tool_releases_its_slot_and_propagates_cancellation () =
    task {
        let budget = McpUserCapacity(settings 1 1 2000)
        let release = hold()
        let first = budget.Execute("subject", CancellationToken.None, fun () ->
            task {
                do! release.Task
                return 1
            })
        use cancel = new CancellationTokenSource()
        let second = budget.Execute("subject", cancel.Token, fun () -> Task.FromResult 2)
        cancel.Cancel()
        let! canceled =
            task {
                try
                    let! _ = second
                    return false
                with :? OperationCanceledException -> return true
            }
        Assert.That(canceled, Is.True)
        Assert.That(budget.ActiveSubjectCount, Is.EqualTo(1))
        release.SetResult(())
        let! _ = first
        Assert.That(budget.ActiveSubjectCount, Is.Zero)
    }

[<Test>]
let throwing_tool_does_not_leak_user_capacity () =
    task {
        let budget = McpUserCapacity(settings 1 0 100)
        let! failed =
            task {
                try
                    let! _ = budget.Execute<int>("subject", CancellationToken.None, fun () ->
                        Task.FromException<int>(InvalidOperationException("expected")))
                    return false
                with :? InvalidOperationException -> return true
            }
        Assert.That(failed, Is.True)
        Assert.That(budget.ActiveSubjectCount, Is.Zero)
        let! next = budget.Execute("subject", CancellationToken.None, fun () -> Task.FromResult 7)
        Assert.That(next, Is.EqualTo(Executed 7))
    }

[<Test>]
let forty_simultaneous_agent_tools_fit_default_active_and_fifo_queue_limits () =
    task {
        let budget = McpUserCapacity(McpLoadSettings.defaults)
        let release = hold()
        let all =
            [| for id in 1 .. 40 ->
                   budget.Execute("single-operator", CancellationToken.None,
                       fun () -> task {
                           do! release.Task
                           return id
                       }) |]
        Assert.That(budget.ActiveSubjectCount, Is.EqualTo(1))
        let! overflow = budget.Execute("single-operator", CancellationToken.None, fun () -> Task.FromResult 41)
        Assert.That(overflow, Is.EqualTo(box (Busy McpLoadSettings.defaults.BusyRetrySeconds : ToolAdmission<int>)))
        release.SetResult(())
        let! accepted = Task.WhenAll(all)
        Assert.That(accepted |> Array.forall succeeded, Is.True)
        Assert.That(budget.ActiveSubjectCount, Is.Zero)
    }


[<Test>]
let http_mcp_subject_rate_returns_429_and_retry_after_without_limiting_unrelated_users_or_metadata () =
    task {
        // Real loopback HTTP middleware with test-only synthetic principals:
        // exercises production partition and rejection handlers, not JWT parsing.
        use listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        let endpoint = $"http://127.0.0.1:{port}"
        let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder()
        builder.WebHost.UseUrls(endpoint) |> ignore
        let configured = { McpLoadSettings.defaults with RequestsPerWindow = 4; WindowSeconds = 10 }
        builder.Services.AddRateLimiter(fun options ->
            options.OnRejected <- McpSubjectRate.onRejected
            options.AddPolicy(McpSubjectRate.Policy, McpSubjectRate.partition configured) |> ignore)
        |> ignore
        use app = builder.Build()
        app.UseRouting() |> ignore
        app.Use(
            Func<RequestDelegate, RequestDelegate>(fun next ->
                RequestDelegate(fun context ->
                    let subject = context.Request.Headers["X-Test-Subject"].ToString()
                    context.User <- ClaimsPrincipal(ClaimsIdentity([ Claim("sub", subject) ], "test"))
                    next.Invoke(context)))
        ) |> ignore
        app.UseRateLimiter() |> ignore
        app.MapGet("/mcp", Func<string>(fun () -> "ok"))
            .RequireRateLimiting(McpSubjectRate.Policy) |> ignore
        app.MapGet("/metadata", Func<string>(fun () -> "public")) |> ignore
        do! app.StartAsync()
        use http = new System.Net.Http.HttpClient()
        http.Timeout <- TimeSpan.FromSeconds 5L
        let get (subject: string) (path: string) =
            task {
                use request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, endpoint + path)
                request.Headers.Add("X-Test-Subject", subject)
                return! http.SendAsync(request)
            }
        for i in 1 .. 4 do
            use! accepted = get "a" "/mcp"
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"subject A request {i}")
        use! rejected = get "a" "/mcp"
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests))
        Assert.That(rejected.Headers.RetryAfter, Is.Not.Null)
        Assert.That(rejected.Headers.RetryAfter.Delta.HasValue, Is.True)
        Assert.That(rejected.Headers.RetryAfter.Delta.Value.TotalSeconds, Is.GreaterThanOrEqualTo(1.))
        for i in 1 .. 4 do
            use! accepted = get "b" "/mcp"
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"subject B request {i}")
        use! exempt = get "a" "/metadata"
        Assert.That(exempt.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        do! app.StopAsync()
    }


[<Test>]
let queued_tools_start_in_fifo_order () =
    task {
        let budget = McpUserCapacity(settings 1 2 2000)
        let firstGate = hold()
        let secondGate = hold()
        let secondStarted = hold()
        let thirdStarted = hold()
        let first =
            budget.Execute("same-subject", CancellationToken.None, fun () ->
                task {
                    do! firstGate.Task
                    return 1
                })
        let second =
            budget.Execute("same-subject", CancellationToken.None, fun () ->
                task {
                    secondStarted.TrySetResult(()) |> ignore
                    do! secondGate.Task
                    return 2
                })
        let third =
            budget.Execute("same-subject", CancellationToken.None, fun () ->
                task {
                    thirdStarted.TrySetResult(()) |> ignore
                    return 3
                })
        // The second admitted tool cannot be overtaken by the third.
        firstGate.SetResult(())
        do! secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.))
        Assert.That(thirdStarted.Task.IsCompleted, Is.False)
        secondGate.SetResult(())
        do! thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.))
        let! values = Task.WhenAll([| first; second; third |])
        Assert.That(values, Is.EqualTo([| Executed 1; Executed 2; Executed 3 |] :> obj))
        Assert.That(budget.ActiveSubjectCount, Is.Zero)
    }


[<Test>]
let admitted_tool_is_not_cancelled_by_expired_queue_wait_deadline () =
    task {
        let budget = McpUserCapacity(settings 1 0 30)
        let entered = hold()
        let release = hold()
        let running =
            budget.Execute("subject", CancellationToken.None, fun () ->
                task {
                    entered.TrySetResult(()) |> ignore
                    do! release.Task
                    return 101
                })
        do! entered.Task
        do! Task.Delay(130)
        Assert.That(running.IsCompleted, Is.False)
        release.SetResult(())
        let! result = running
        Assert.That(result, Is.EqualTo(Executed 101))
        Assert.That(budget.ActiveSubjectCount, Is.Zero)
    }
