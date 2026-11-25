module CanonicalProjectCapacityRoleBindingTests

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open Client.CanonicalProjectCapacityRoleBinding
open ProjectCapacityDisposableTests

let agent = "Worker 14"
let task = "role-binding-test"
let op () = Guid.NewGuid()
let ignoreGood result = good result |> ignore
let setup (a: LedgerBinding) =
    ReviewWorkflow.registerAgent a (op ()) agent |> ignoreGood
    RoleInstructions.createRole a "Operator" (op ()) "Worker" "Implementation" "Implement task" |> ignoreGood
    RoleInstructions.attachRole a "Operator" (op ()) agent "Worker" 0 |> ignoreGood
    ReviewWorkflow.createTask a (op ()) task "Fixture task" |> ignoreGood
    let attempt = op ()
    ReviewWorkflow.assignTask a (op ()) task attempt agent |> ignoreGood
    { Project = a.ProjectId; Ledger = a.LedgerId; Epoch = a.Epoch; TargetAgent = agent
      Task = task; Attempt = attempt.ToString("N"); Generation = 1; Role = "Worker"
      RoleVersion = 1; ConfigVersion = 1 }

let snapshot a = LedgerStore.fetch a |> good
let expectError value = Assert.That(Result.isError value, Is.True)

[<Test>]
let canonicalIdentityAndNegativeAssertions () =
    withClones (fun a b ->
        let expected = setup a
        let head = snapshot b
        let project target e = projectAt b head target e
        let proof = project (Some agent) expected |> good
        Assert.That(identity proof, Is.EqualTo(expected))
        Assert.That(snapshotCommit proof, Is.EqualTo(head.Commit))
        let _, oid = LedgerStore.readAt b head "records/roles/Worker.json" |> good
        Assert.That(roleBlobOid proof, Is.EqualTo(oid.Value))
        project None expected |> expectError
        project (Some "Other") expected |> expectError
        for bad in
            [ { expected with Role = "Reviewer" }; { expected with RoleVersion = 2 }
              { expected with ConfigVersion = 2 }; { expected with Project = op () }
              { expected with Ledger = op () }; { expected with Epoch = 2 }
              { expected with Generation = 2 }; { expected with Task = "other" }
              { expected with Attempt = (op ()).ToString("N") } ] do
            project (Some agent) bad |> expectError
        projectAt { b with ProjectId = op () } head (Some agent) expected |> expectError
        ReviewWorkflow.registerAgent a (op ()) "Unassigned" |> ignoreGood
        projectAt b (snapshot b) (Some "Unassigned") { expected with TargetAgent = "Unassigned" } |> expectError)

[<Test>]
let ambiguousAndChangedRolesDeny () =
    withClones (fun a b ->
        let expected = setup a
        RoleInstructions.createRole a "Operator" (op ()) "Reviewer" "Review" "Review task" |> ignoreGood
        RoleInstructions.attachRole a "Operator" (op ()) agent "Reviewer" 1 |> ignoreGood
        let head = snapshot b
        projectAt b head (Some agent) { expected with ConfigVersion = 2 } |> expectError
        projectAt b head (Some agent) { expected with ConfigVersion = 2; Role = "Reviewer" } |> expectError
        RoleInstructions.detachRole a "Operator" (op ()) agent "Reviewer" 2 |> ignoreGood
        RoleInstructions.updateRole a "Operator" (op ()) "Worker" 1 "Implementation" "Updated instruction" |> ignoreGood
        let changed = snapshot b
        projectAt b changed (Some agent) { expected with ConfigVersion = 3 } |> expectError
        projectAt b changed (Some agent) { expected with ConfigVersion = 3; RoleVersion = 2 } |> good |> ignore)

let recover a =
    let head = snapshot a
    let ag = ReviewWorkflow.getAgentWorkingTurnAt a head agent |> good
    let _, attempt = ReviewWorkflow.getTaskAt a head task |> good
    let attempt = attempt.Value
    ReviewWorkflow.recoverAgentAssignment a (op ()) task agent attempt.Version ag.Version attempt.AssignmentGeneration "Fixture recovery" |> ignoreGood

let reserveIntent operation effect (expected: ExpectedAssignment) =
    { OperationId = operation; Actor = "authenticated-fixture-host"; Kind = "Fixture.RoleBoundReserve"
      RequestJson = JsonSerializer.Serialize {| assignment = expected; effect = effect; policyRevision = 1 |} }

let prepareCapacity a =
    Client.ProjectCapacityPolicy.update a "Operator" (op ()) 0 2 (Map.ofList ["Worker", 2]) |> ignoreGood
    Client.ProjectCapacityAdmission.initializeFixture a "fixture" (op ()) 2 (Map.ofList ["Worker", 2]) |> ignoreGood

let plan a head operation effect expected =
    projectAt a head (Some agent) expected
    |> Result.bind (fun witness ->
        let actual = identity witness
        Client.ProjectCapacityPolicy.readForRole a head 1 actual.Role
        |> Result.bind (fun _ ->
            Client.ProjectCapacityAdmission.planReservation a head actual.TargetAgent operation effect actual.Role actual.RoleVersion actual.Generation))

[<Test>]
let replayIsReceiptNotNewWitness () =
    withClones (fun a b ->
        let expected = setup a
        prepareCapacity a
        let operation, effect = op (), op ()
        let intent = reserveIntent operation effect expected
        let mutable calls = 0
        let reserve binding =
            LedgerStore.transact binding intent (fun head ->
                calls <- calls + 1
                plan binding head operation effect expected)
        let first = reserve a |> good
        Assert.That(first.AlreadyApplied, Is.False)
        recover b
        let oldCalls = calls
        let replay = reserve a |> good
        Assert.That(replay.AlreadyApplied, Is.True)
        Assert.That(calls, Is.EqualTo(oldCalls))
        projectAt b (snapshot b) (Some agent) expected |> expectError
        projectAt b (snapshot b) (Some agent) { expected with Generation = 2 } |> good |> ignore
        let changed = reserveIntent operation effect { expected with Generation = 2 }
        LedgerStore.transact b changed (fun _ -> failwith "mismatched replay must not plan") |> expectError)

[<Test>]
let concurrentGenerationChangeDeniesRetry () =
    withClones (fun a b ->
        let expected = setup a
        prepareCapacity a
        use entered = new ManualResetEventSlim(false)
        use proceed = new ManualResetEventSlim(false)
        let mutable calls = 0
        let operation, effect = op (), op ()
        let pending = Task.Run(fun () ->
            LedgerStore.transact a (reserveIntent operation effect expected) (fun head ->
                calls <- calls + 1
                let proposed = plan a head operation effect expected
                if calls = 1 then
                    Assert.That(Result.isOk proposed, Is.True)
                    entered.Set()
                    if not (proceed.Wait(TimeSpan.FromSeconds 30.)) then failwith "timeout"
                proposed))
        try
            Assert.That(entered.Wait(TimeSpan.FromSeconds 30.), Is.True)
            recover b
        finally proceed.Set()
        pending.Result |> expectError
        Assert.That(calls, Is.GreaterThanOrEqualTo(2))
        let newOperation = op ()
        let fresh = { expected with Generation = 2 }
        LedgerStore.transact b (reserveIntent newOperation effect fresh) (fun head ->
            plan b head newOperation effect fresh) |> ignoreGood)

[<Test>]
let ambiguousNestedFocusAndMismatchedOwnerDeny () =
    withClones (fun a b ->
        let expected = setup a
        let key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes agent).TrimEnd('=').Replace('+','-').Replace('/','_')
        let path = "agents/" + key + ".json"
        let rewrite path (change: string -> string) =
            LedgerStore.transact a { OperationId = op (); Actor = "fixture"; Kind = "Fixture.CorruptRecord"; RequestJson = "{}" }
                (fun head ->
                    let raw, oid = LedgerStore.readAt a head path |> good
                    let next = change raw.Value
                    Assert.That(next, Is.Not.EqualTo(raw.Value))
                    Ok [{ Path = path; ExpectedBlobOid = oid; Content = next }]) |> ignoreGood
        // The outer record is valid; two nested task IDs are ambiguous even
        // when the serializer would choose the last (correct) value.
        let marker = "\"currentAssignment\":{"
        rewrite path (fun raw -> raw.Replace(marker, marker + "\"taskId\":\"other\","))
        projectAt b (snapshot b) (Some agent) expected |> expectError
        rewrite path (fun raw -> raw.Replace(marker + "\"taskId\":\"other\",", marker))
        let attemptPath = "tasks/" + task + "/attempts/" + expected.Attempt + ".json"
        rewrite attemptPath (fun raw -> raw.Replace("\"ownerAgentId\":\"Worker 14\"", "\"ownerAgentId\":\"Other\""))
        projectAt b (snapshot b) (Some agent) expected |> expectError)
