module ProjectCapacityPolicyTests
open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ProjectCapacityPolicy
open ProjectCapacityDisposableTests

let roles () = Map.ofList ["Worker", 1; "Reviewer", 2]
let read binding revision role =
    let snapshot = LedgerStore.fetch binding |> good
    readForRole binding snapshot revision role

[<Test>]
let invalidMissingIdentityAndReplay () =
    withClones (fun a b ->
        Assert.That(read a 1 "Worker" |> Result.isError, Is.True)
        Assert.That(update a "operator" (Guid.NewGuid()) 0 0 (roles ()) |> Result.isError, Is.True)
        Assert.That(update a "operator" (Guid.NewGuid()) 0 1 (Map.ofList ["", 1]) |> Result.isError, Is.True)
        let op = Guid.NewGuid()
        update a "operator" op 0 3 (roles ()) |> good |> ignore
        Assert.That((update b "operator" op 0 3 (roles ()) |> good).AlreadyApplied, Is.True)
        Assert.That(update b "operator" op 0 4 (roles ()) |> Result.isError, Is.True)
        Assert.That(read b 1 "Unknown" |> Result.isError, Is.True)
        Assert.That(read b 2 "Worker" |> Result.isError, Is.True)
        let bad = { b with ProjectId = Guid.NewGuid() }
        Assert.That(LedgerStore.fetch bad |> Result.isError, Is.True)
        update b "operator" (Guid.NewGuid()) 1 4 (roles ()) |> good |> ignore
        Assert.That(update a "operator" (Guid.NewGuid()) 1 4 (roles ()) |> Result.isError, Is.True)
        Assert.That(read a 2 "Worker" |> Result.isOk, Is.True))

[<Test>]
let policyChangeRacesAdmissionSnapshot () =
    withClones (fun a b ->
        update a "operator" (Guid.NewGuid()) 0 2 (roles ()) |> good |> ignore
        use entered = new ManualResetEventSlim(false)
        use proceed = new ManualResetEventSlim(false)
        let mutable calls = 0
        let intent = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.PolicyAdmission"; RequestJson = "{}" }
        let attempt = Task.Run(fun () ->
            LedgerStore.transact a intent (fun snapshot ->
                readForRole a snapshot 1 "Worker"
                |> Result.bind (fun _ ->
                    calls <- calls + 1
                    if calls = 1 then
                        entered.Set()
                        if not (proceed.Wait(TimeSpan.FromSeconds 30.)) then failwith "timeout"
                    Ok [{ Path = "records/project-capacity-policy-test/reservation.json"; ExpectedBlobOid = None; Content = "{}" }])))
        Assert.That(entered.Wait(TimeSpan.FromSeconds 30.), Is.True)
        try update b "operator" (Guid.NewGuid()) 1 1 (roles ()) |> good |> ignore
        finally proceed.Set()
        Assert.That(attempt.Result |> Result.isError, Is.True)
        Assert.That(read b 2 "Worker" |> Result.isOk, Is.True))

[<Test>]
let malformedPolicyFailsClosed () =
    withClones (fun a _ ->
        let path = "records/project-capacity-policy/policy.json"
        let install raw =
            let intent = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.BadPolicy"; RequestJson = "{}" }
            LedgerStore.transact a intent (fun snapshot ->
                LedgerStore.readAt a snapshot path |> Result.map (fun (_, oid) ->
                    [{ Path = path; ExpectedBlobOid = oid; Content = raw }])) |> good |> ignore
        for raw in ["{}"; "{\"Schema\":1,\"Schema\":1}"; "[]"; "null"] do
            install raw
            Assert.That(read a 1 "Worker" |> Result.isError, Is.True)
            Assert.That(update a "operator" (Guid.NewGuid()) 1 3 (roles ()) |> Result.isError, Is.True))
