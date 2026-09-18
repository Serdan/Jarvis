module ServerServiceTests

open System
open System.Threading.Tasks
open Common
open NUnit.Framework
open FsUnitTyped
open Server.Services

[<Test>]
let ``user service preserves a replacement connection when the old connection is removed`` () =
    let users = UserService()
    users.Add("session", "old-connection")
    users.Add("session", "new-connection")

    users.Remove("old-connection")

    users.GetConnectionId("session")
    |> shouldEqual (ValueSome "new-connection")

[<Test>]
let ``response tracker completes registered response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register(TimeSpan.FromSeconds 5L)
        let expected =
            { Result = Some "ok"
              Error = None }

        tracker.Complete(correlationId, expected)
        let! actual = pending

        actual |> shouldEqual expected
    }

[<Test>]
let ``response tracker cancellation removes pending response`` () =
    task {
        let tracker = ClientResponseTracker()
        let correlationId, pending = tracker.Register(TimeSpan.FromSeconds 5L)

        tracker.Cancel(correlationId)

        try
            let! _ = pending
            Assert.Fail("Expected pending response to be canceled.")
        with :? TaskCanceledException ->
            ()
    }

[<Test>]
let ``response tracker honors configured timeout`` () =
    task {
        let tracker = ClientResponseTracker()
        let _, pending = tracker.Register(TimeSpan.FromMilliseconds 25L)

        try
            let! _ = pending
            Assert.Fail("Expected pending response to time out.")
        with :? TaskCanceledException ->
            ()
    }
