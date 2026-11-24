module ManualCapacityCompositionWorker23
open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client.ProjectCapacityAdmission
open Client.ProjectCapacityDispatchTransition
open ProjectCapacityDisposableTests

// Test-only composition. Fixture inventory is not evidence of production completeness.
// A fresh reservation and fresh begin transition are required; neither replay can send.
let dispatch binding fence original effect beginOp send =
    match ProjectInventoryComposedFixture.reserve binding "worker" original effect "Worker" 1 1 fence with
    | Error _ -> false
    | Ok receipt when receipt.AlreadyApplied -> false
    | Ok _ ->
        match beginTransition binding "worker" beginOp original effect "Worker" 1 1 with
        | Ok Committed -> send(); true
        | _ -> false

[<Test>]
let missingStaleFullAndReplayCannotSend () =
    withClones (fun a b ->
        let fence, original, effect, beginOp = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        let mutable sends = 0
        let send () = Interlocked.Increment(&sends) |> ignore
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        Assert.That(dispatch a fence original effect beginOp send, Is.False)
        ProjectInventoryComposedFixture.initialize a "fixture" (Guid.NewGuid()) 1 fence |> good |> ignore
        Assert.That(dispatch b (Guid.NewGuid()) original effect beginOp send, Is.False)
        Assert.That(dispatch a fence original effect beginOp send, Is.True)
        Assert.That(dispatch b fence original effect beginOp send, Is.False)
        Assert.That(dispatch b fence (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid()) send, Is.False)
        Assert.That(sends, Is.EqualTo(1)))

[<Test>]
let concurrentContendersOnlyOneSends () =
    withClones (fun a b ->
        let fence = Guid.NewGuid()
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        ProjectInventoryComposedFixture.initialize a "fixture" (Guid.NewGuid()) 1 fence |> good |> ignore
        let mutable sends = 0
        let send () = Interlocked.Increment(&sends) |> ignore
        let run binding = Task.Run(fun () -> dispatch binding fence (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid()) send)
        let x, y = run a, run b
        Task.WaitAll [| x :> Task; y :> Task |]
        Assert.That([x.Result; y.Result] |> List.filter id |> List.length, Is.EqualTo(1))
        Assert.That(sends, Is.EqualTo(1)))

[<Test>]
let failureAfterBeginRetainsOccupancyAndNeverReplays () =
    withClones (fun a b ->
        let fence, original, effect, beginOp = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        ProjectInventoryComposedFixture.initialize a "fixture" (Guid.NewGuid()) 1 fence |> good |> ignore
        let mutable sends = 0
        let failed () = sends <- sends + 1; failwith "bridge outcome unknown"
        Assert.Throws<Exception>(fun () -> dispatch a fence original effect beginOp failed |> ignore) |> ignore
        Assert.That(dispatch b fence original effect beginOp (fun () -> sends <- sends + 1), Is.False)
        Assert.That(dispatch b fence (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid()) (fun () -> sends <- sends + 1), Is.False)
        Assert.That(sends, Is.EqualTo(1)))
