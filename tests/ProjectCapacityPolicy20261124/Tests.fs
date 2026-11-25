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
            Assert.That(readCurrentOptional a (LedgerStore.fetch a |> good) |> Result.isError, Is.True, raw)
            Assert.That(set a 1 None Map.empty |> Result.isError, Is.True, raw))

[<Test>]
let corruptCanonicalRoleFailsEvenWithNoCaps () =
    withRoles (fun a _ ->
        install a "records/roles/Worker.json" (roleRaw "Reviewer")
        Assert.That(read a 0 "Worker" |> Result.isError, Is.True)
        Assert.That(set a 0 None (Map.ofList ["Worker", 1]) |> Result.isError, Is.True)
        set a 0 (Some 3) (roles ()) |> fun result -> Assert.That(Result.isError result, Is.True))

[<Test>]
let currentReaderUsesOnlyPinnedSnapshot () =
    withRoles (fun a b ->
        let absent = LedgerStore.fetch a |> good
        let initial, initialOid = readCurrentOptional a absent |> good
        Assert.That(initial.Revision, Is.Zero)
        Assert.That(initial.Total = None && Map.isEmpty initial.Roles && initialOid = None, Is.True)
        set b 0 (Some 3) (roles ()) |> good |> ignore
        let configured = LedgerStore.fetch a |> good
        set b 1 None Map.empty |> good |> ignore
        let disabled = LedgerStore.fetch a |> good
        let (old, oldOid), metrics = LedgerStore.measureGitReads (fun () -> readCurrentOptional a configured |> good)
        Assert.That(metrics.RemoteFetches, Is.Zero)
        Assert.That(old.Revision, Is.EqualTo(1))
        Assert.That(old.Total = Some 3 && old.Roles = roles () && Option.isSome oldOid, Is.True)
        Assert.That(readCurrentOptional a configured |> good, Is.EqualTo(readOptionalForRole a configured 1 "Worker" |> good))
        let latest, latestOid = readCurrentOptional a disabled |> good
        Assert.That(latest.Revision, Is.EqualTo(2))
        Assert.That(latest.Total = None && Map.isEmpty latest.Roles && Option.isSome latestOid, Is.True)
        Assert.That(latestOid <> oldOid, Is.True)
        Assert.That(readCurrentOptional a absent |> good, Is.EqualTo((initial, initialOid)))
        Assert.That(readCurrentOptional a { configured with Commit = String('0', 40) } |> Result.isError, Is.True)
        Assert.That(readCurrentOptional { a with LedgerId = Guid.NewGuid() } configured |> Result.isError, Is.True)
        install a "records/roles/Worker.json" (roleRaw "Reviewer")
        // Disabled policy has no configured role identities to validate.
        Assert.That(readCurrentOptional a (LedgerStore.fetch a |> good) |> Result.isOk, Is.True)
        Assert.That(readOptionalForRole a (LedgerStore.fetch a |> good) 2 "Worker" |> Result.isError, Is.True))

[<Test>]
let currentReaderPreservesLegacyAndRejectsCorruptConfiguredRole () =
    withRoles (fun a _ ->
        update a "operator" (Guid.NewGuid()) 0 3 (roles ()) |> good |> ignore
        let pinned = LedgerStore.fetch a |> good
        let policy, oid = readCurrentOptional a pinned |> good
        Assert.That(policy.Schema, Is.EqualTo(1))
        Assert.That(policy.Revision, Is.EqualTo(1))
        Assert.That(policy.Total = Some 3 && policy.Roles = roles () && Option.isSome oid, Is.True)
        install a "records/roles/Worker.json" (roleRaw "Reviewer")
        Assert.That(readCurrentOptional a (LedgerStore.fetch a |> good) |> Result.isError, Is.True)
        Assert.That(readCurrentOptional a pinned |> good, Is.EqualTo((policy, oid))))

[<Test>]
let explicitAutomationIsVersionedAndCapacityOnlyWritesPreserveIt () =
    withRoles (fun a b ->
        let readCurrent binding =
            readCurrentOptional binding (LedgerStore.fetch binding |> good) |> good |> fst
        Assert.That((readCurrent a).AutomationEnabled, Is.False)
        set a 0 (Some 3) (roles ()) |> good |> ignore
        Assert.That((readCurrent b).AutomationEnabled, Is.False, "Legacy configured caps are not opt-in.")
        let enabled = Guid.NewGuid()
        updateAutomation b "operator" enabled 1 true (Some 3) (roles ()) |> good |> ignore
        let current = readCurrent a
        Assert.That(current.AutomationEnabled, Is.True)
        Assert.That(current.Revision, Is.EqualTo(2))
        Assert.That((updateAutomation a "operator" enabled 1 true (Some 3) (roles ()) |> good).AlreadyApplied, Is.True)
        Assert.That(updateAutomation a "operator" (Guid.NewGuid()) 1 false None Map.empty |> Result.isError, Is.True)
        set a 2 None Map.empty |> good |> ignore
        Assert.That((readCurrent b).AutomationEnabled, Is.True)
        updateAutomation b "operator" (Guid.NewGuid()) 3 false None Map.empty |> good |> ignore
        Assert.That((readCurrent a).AutomationEnabled, Is.False)
        Assert.That((readCurrent a).Revision, Is.EqualTo(4)))

[<Test>]
let malformedAutomationFieldFailsClosed () =
    withRoles (fun a _ ->
        let policy value =
            sprintf "{\"Schema\":3,\"Project\":\"%O\",\"Ledger\":\"%O\",\"Epoch\":%d,\"Revision\":1,\"Total\":null,\"Roles\":{},\"AutomationEnabled\":%s}" a.ProjectId a.LedgerId a.Epoch value
        for raw in [policy "null"; policy "1"; policy "\"true\""; policy "true,\"AutomationEnabled\":false"] do
            install a path raw
            Assert.That(readCurrentOptional a (LedgerStore.fetch a |> good) |> Result.isError, Is.True, raw))
