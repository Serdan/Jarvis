module ProjectCapacityPolicyTests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ProjectCapacityPolicy
open ProjectCapacityDisposableTests

let roles () = Map.ofList ["Worker", 1; "Reviewer", 2]
let path = "records/project-capacity-policy/policy.json"

let install binding path raw =
    let intent = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.PolicyRecord"; RequestJson = "{}" }
    LedgerStore.transact binding intent (fun snapshot ->
        LedgerStore.readAt binding snapshot path |> Result.map (fun (_, oid) ->
            [{ Path = path; ExpectedBlobOid = oid; Content = raw }])) |> good |> ignore

let roleRaw role =
    System.Text.Json.JsonSerializer.Serialize
        {| schemaVersion = 1; roleId = role; purpose = "fixture"; instructions = "fixture"
           version = 1; updatedBy = "fixture"; updatedSequence = 1L |}

let withRoles work =
    withClones (fun a b ->
        for role in ["Worker"; "Reviewer"; "Lead"] do
            install a ("records/roles/" + role + ".json") (roleRaw role)
        work a b)

let read binding revision role =
    let snapshot = LedgerStore.fetch binding |> good
    readOptionalForRole binding snapshot revision role

let set binding rev total roleCaps =
    updateOptional binding "operator" (Guid.NewGuid()) rev total roleCaps

let assertPolicy binding revision total roleCaps =
    let policy, oid = read binding revision "Worker" |> good
    Assert.That(policy.Revision, Is.EqualTo(revision))
    Assert.That(policy.Total = total, Is.True)
    Assert.That(policy.Roles = roleCaps, Is.True)
    Assert.That(Option.isSome oid, Is.EqualTo(revision > 0))

[<Test>]
let absentAndAllIndependentDimensions () =
    withRoles (fun a b ->
        assertPolicy a 0 None Map.empty
        let snapshot = LedgerStore.fetch a |> good
        let unavailable = { snapshot with Commit = String('0', 40) }
        Assert.That(readOptionalForRole a unavailable 0 "Worker" |> Result.isError, Is.True)
        Assert.That(readOptionalForRole { a with LedgerId = Guid.NewGuid() } snapshot 0 "Worker" |> Result.isError, Is.True)
        Assert.That(read a 1 "Worker" |> Result.isError, Is.True)
        Assert.That(read a 0 "Unknown" |> Result.isError, Is.True)
        let configurations =
            [ None, Map.empty; Some 3, Map.empty; None, (roles ()); Some 4, (roles ())
              Some 2, Map.ofList ["Reviewer", 1]; None, Map.empty ]
        configurations |> List.iteri (fun i (total, caps) ->
            set a i total caps |> good |> ignore
            assertPolicy b (i + 1) total caps
            Assert.That(read b 0 "Worker" |> Result.isError, Is.True)
            let uncapped = read b (i + 1) "Lead" |> good |> fst
            Assert.That(Map.tryFind "Lead" uncapped.Roles, Is.EqualTo(None))))

[<Test>]
let invalidIdentityAndReplay () =
    withRoles (fun a b ->
        for value in [0; -1] do
            Assert.That(set a 0 (Some value) Map.empty |> Result.isError, Is.True)
            Assert.That(set a 0 None (Map.ofList ["Worker", value]) |> Result.isError, Is.True)
        for role in [""; "Unknown"; "worker"; "../Worker"] do
            Assert.That(set a 0 None (Map.ofList [role, 1]) |> Result.isError, Is.True)
        let op = Guid.NewGuid()
        updateOptional a "operator" op 0 (Some 3) (roles ()) |> good |> ignore
        Assert.That((updateOptional b "operator" op 0 (Some 3) (roles ()) |> good).AlreadyApplied, Is.True)
        Assert.That(updateOptional b "operator" op 0 (Some 4) (roles ()) |> Result.isError, Is.True)
        Assert.That(read b 1 "Unknown" |> Result.isError, Is.True)
        Assert.That(read b 2 "Worker" |> Result.isError, Is.True)
        let snapshot = LedgerStore.fetch b |> good
        let bad = { b with ProjectId = Guid.NewGuid() }
        Assert.That(readOptionalForRole bad snapshot 1 "Worker" |> Result.isError, Is.True)
        set b 1 None Map.empty |> good |> ignore
        Assert.That(set a 1 (Some 4) (roles ()) |> Result.isError, Is.True)
        assertPolicy a 2 None Map.empty)

[<Test>]
let legacyConfiguredLimitsPreserved () =
    withRoles (fun a b ->
        let op = Guid.NewGuid()
        update a "operator" op 0 3 (roles ()) |> good |> ignore
        Assert.That((update b "operator" op 0 3 (roles ()) |> good).AlreadyApplied, Is.True)
        assertPolicy b 1 (Some 3) (roles ())
        // A schema-1 unlisted role previously denied; do not broaden admission implicitly.
        Assert.That(read b 1 "Lead" |> Result.isError, Is.True)
        set b 1 (Some 3) (roles ()) |> good |> ignore
        assertPolicy a 2 (Some 3) (roles ())
        Assert.That(read a 2 "Lead" |> Result.isOk, Is.True)
        Assert.That(update a "operator" (Guid.NewGuid()) 2 5 (roles ()) |> Result.isError, Is.True)
        Assert.That(readForRole a (LedgerStore.fetch a |> good) 2 "Worker" |> Result.isError, Is.True))

[<Test>]
let policyChangeRacesAdmissionSnapshot () =
    withRoles (fun a b ->
        use entered = new ManualResetEventSlim(false)
        use proceed = new ManualResetEventSlim(false)
        let mutable calls = 0
        let intent = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.PolicyAdmission"; RequestJson = "{}" }
        let attempt = Task.Run(fun () ->
            LedgerStore.transact a intent (fun snapshot ->
                calls <- calls + 1
                readOptionalForRole a snapshot 0 "Worker"
                |> Result.bind (fun _ ->
                    if calls = 1 then
                        entered.Set()
                        if not (proceed.Wait(TimeSpan.FromSeconds 30.)) then failwith "timeout"
                    Ok [{ Path = "records/project-capacity-policy-test/reservation.json"; ExpectedBlobOid = None; Content = "{}" }])))
        Assert.That(entered.Wait(TimeSpan.FromSeconds 30.), Is.True)
        try set b 0 (Some 1) (roles ()) |> good |> ignore
        finally proceed.Set()
        Assert.That(attempt.Result |> Result.isError, Is.True)
        Assert.That(calls, Is.GreaterThanOrEqualTo(2))
        assertPolicy b 1 (Some 1) (roles ()))

[<Test>]
let competingUpdatesHaveOneSuccessor () =
    withRoles (fun a b ->
        let x = Task.Run(fun () -> set a 0 None (roles ()))
        let y = Task.Run(fun () -> set b 0 (Some 5) Map.empty)
        Task.WaitAll [| x :> Task; y :> Task |]
        Assert.That([x.Result; y.Result] |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        if Result.isOk x.Result then assertPolicy b 1 None (roles ())
        else assertPolicy a 1 (Some 5) Map.empty)

[<Test>]
let malformedPolicyFailsClosed () =
    withRoles (fun a _ ->
        let record schema total caps =
            sprintf "{\"Schema\":%d,\"Project\":\"%O\",\"Ledger\":\"%O\",\"Epoch\":%d,\"Revision\":1,\"Total\":%s,\"Roles\":%s}" schema a.ProjectId a.LedgerId a.Epoch total caps
        let valid = record 2 "null" "{}"
        for raw in [ "{}"; "[]"; "null"; record 3 "null" "{}"; record 1 "null" "{}"
                     record 2 "0" "{}"; record 2 "-1" "{}"; record 2 "\"1\"" "{}"
                     record 2 "null" "null"; record 2 "null" "{\"Worker\":0}"
                     record 2 "null" "{\"Worker\":1,\"Worker\":2}"
                     record 2 "null" "{\"Unknown\":1}"; record 1 "2" "{\"Unknown\":1}"
                     valid.Replace("\"Schema\":2", "\"Schema\":2,\"Schema\":2")
                     valid.Replace("\"Schema\":2", "\"Schema\":2,\"Extra\":1")
                     valid.Replace("\"Total\":null,", "")
                     valid.Replace(string a.ProjectId, string (Guid.NewGuid())) ] do
            install a path raw
            Assert.That(read a 1 "Worker" |> Result.isError, Is.True, raw)
            Assert.That(set a 1 None Map.empty |> Result.isError, Is.True, raw))

[<Test>]
let corruptCanonicalRoleFailsEvenWithNoCaps () =
    withRoles (fun a _ ->
        install a "records/roles/Worker.json" (roleRaw "Reviewer")
        Assert.That(read a 0 "Worker" |> Result.isError, Is.True)
        Assert.That(set a 0 None (Map.ofList ["Worker", 1]) |> Result.isError, Is.True)
        set a 0 (Some 3) (roles ()) |> fun result -> Assert.That(Result.isError result, Is.True))
