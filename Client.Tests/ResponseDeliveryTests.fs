module ResponseDeliveryTests

open System
open System.Threading.Tasks
open Client.SignalR
open FsUnitTyped
open Microsoft.AspNetCore.SignalR.Client
open NUnit.Framework

[<Test>]
let ``response waits for connection to become active`` () =
    let mutable polls = 0
    let mutable sends = 0

    let state () =
        polls <- polls + 1
        if polls < 3 then HubConnectionState.Reconnecting
        else HubConnectionState.Connected

    let send () =
        task {
            sends <- sends + 1
            return Ok()
        }

    let result =
        ResponseDelivery.sendWithReconnectRetry
            (TimeSpan.FromMilliseconds 100.0)
            (TimeSpan.FromMilliseconds 1.0)
            state
            send
        |> Async.AwaitTask
        |> Async.RunSynchronously

    result |> shouldEqual (Ok())
    sends |> shouldEqual 1

[<Test>]
let ``inactive connection invocation is retried even if reconnect wins the state race`` () =
    let mutable sends = 0

    let send () =
        task {
            sends <- sends + 1

            if sends = 1 then
                return
                    Error(
                        InvalidOperationException(
                            "The 'InvokeCoreAsync' method cannot be called if the connection is not active"
                        )
                        :> exn
                    )
            else
                return Ok()
        }

    let result =
        ResponseDelivery.sendWithReconnectRetry
            (TimeSpan.FromMilliseconds 100.0)
            (TimeSpan.FromMilliseconds 1.0)
            (fun () -> HubConnectionState.Connected)
            send
        |> Async.AwaitTask
        |> Async.RunSynchronously

    result |> shouldEqual (Ok())
    sends |> shouldEqual 2

[<Test>]
let ``non transport send errors are not retried`` () =
    let mutable sends = 0
    let expected: exn = Exception("server rejected response")

    let send () =
        task {
            sends <- sends + 1
            return Error expected
        }

    let result =
        ResponseDelivery.sendWithReconnectRetry
            (TimeSpan.FromMilliseconds 100.0)
            (TimeSpan.FromMilliseconds 1.0)
            (fun () -> HubConnectionState.Connected)
            send
        |> Async.AwaitTask
        |> Async.RunSynchronously

    match result with
    | Error actual -> actual |> shouldEqual expected
    | Ok() -> Assert.Fail("Expected send failure.")

    sends |> shouldEqual 1

[<Test>]
let ``response delivery times out while connection remains unavailable`` () =
    let result =
        ResponseDelivery.sendWithReconnectRetry
            (TimeSpan.FromMilliseconds 20.0)
            (TimeSpan.FromMilliseconds 1.0)
            (fun () -> HubConnectionState.Reconnecting)
            (fun () -> Task.FromResult(Ok()))
        |> Async.AwaitTask
        |> Async.RunSynchronously

    match result with
    | Error (:? TimeoutException) -> ()
    | Error ex -> Assert.Fail($"Expected TimeoutException, got {ex.GetType().Name}.")
    | Ok() -> Assert.Fail("Expected timeout.")

[<Test>]
let ``an in flight reply invocation is bounded and never replayed on timeout`` () = task {
    let mutable attempts = 0
    let pending = TaskCompletionSource<Result<unit, exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    let send () =
        attempts <- attempts + 1
        pending.Task
    let! result =
        ResponseDelivery.sendWithReconnectRetry
            (TimeSpan.FromMilliseconds 40.0)
            (TimeSpan.FromMilliseconds 1.0)
            (fun () -> HubConnectionState.Connected)
            send
    match result with
    | Error (:? TimeoutException) -> ()
    | Error ex -> Assert.Fail($"Expected bounded timeout, got {ex.GetType().Name}.")
    | Ok () -> Assert.Fail("A pending invocation cannot be acknowledged.")
    Assert.That(attempts, Is.EqualTo(1), "Never retry a command response with an uncertain send.")
    pending.TrySetResult(Ok()) |> ignore
}
