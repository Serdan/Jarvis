module ProjectWorkingCountReaderTests

open System
open NUnit.Framework
open Client.LedgerStore
open Client.ProjectCapacityPolicy
open Client.ProjectWorkingCount
open ProjectCapacityDisposableTests
open ProjectWorkingCountTests

// The reader's verified policy and the count must use the same pinned head.
// This is a test-only composition, not a production admission mechanism.
let held binding head target =
    readCurrentOptional binding head
    |> Result.bind (fun (p, _) -> isHeldAt binding head target p.Total p.Roles)

let expect binding head target (expected: bool) =
    Assert.That(held binding head target |> good, Is.EqualTo(expected))

let set binding revision total roles =
    updateOptional binding "Operator" (op ()) revision total roles |> ignoreGood

[<Test>]
let pinnedPolicyAndWorkingCountsCompose () =
    withClones (fun a b ->
        for role in ["Worker"; "Reviewer"; "Lead"] do createRole a role
        for id in ["Manual worker"; "Second worker"; "Manual reviewer"; "Parked worker"] do register a id
        for id in ["Manual worker"; "Second worker"; "Parked worker"] do attach a id "Worker" 0
        attach a "Manual reviewer" "Reviewer" 0
        // Taskless starts stand in for manual Working turns; no scheduler reservation.
        start a "Manual worker"
        start a "Manual reviewer"
        let absent = snapshot b
        expect b absent "Worker" false

        set a 0 None (workers 2)
        let below = snapshot b
        expect b below "Worker" false
        start a "Second worker"
        let at = snapshot b
        expect b at "Worker" true
        expect b at "Reviewer" false
        expect b at "Lead" false

        set a 1 None (workers 1)
        let above = snapshot b
        expect b above "Worker" true
        expect b above "Reviewer" false

        set a 2 (Some 4) Map.empty
        let totalBelow = snapshot b
        expect b totalBelow "Lead" false
        set a 3 (Some 3) Map.empty
        let totalAt = snapshot b
        expect b totalAt "Lead" true
        set a 4 (Some 2) Map.empty
        let totalAbove = snapshot b
        expect b totalAbove "Lead" true

        set a 5 None Map.empty
        let disabled = snapshot b
        expect b disabled "Worker" false
        stop a "Second worker"
        set a 6 (Some 3) (workers 2)
        let drained = snapshot b
        expect b drained "Worker" false

        // Later policy/state changes cannot alter earlier pinned results.
        let _, metrics = LedgerStore.measureGitReads (fun () ->
            expect b absent "Worker" false
            expect b below "Worker" false
            expect b at "Worker" true
            expect b above "Worker" true
            expect b totalBelow "Lead" false
            expect b totalAt "Lead" true
            expect b totalAbove "Lead" true
            expect b disabled "Worker" false)
        Assert.That(metrics.RemoteFetches, Is.Zero))

[<Test>]
let pinnedReadFailuresAreNotDisabledPolicy () =
    withClones (fun a b ->
        createRole a "Worker"
        register a "Manual worker"
        attach a "Manual worker" "Worker" 0
        start a "Manual worker"
        set a 0 None (workers 1)
        let head = snapshot b
        expect b head "Worker" true
        Assert.That(held { b with LedgerId = op () } head "Worker" |> Result.isError, Is.True)
        Assert.That(held b { head with Commit = String('0', 40) } "Worker" |> Result.isError, Is.True)
        let corrupt path raw =
            let intent = { OperationId = op (); Actor = "fixture"; Kind = "Fixture.Corrupt"; RequestJson = "{}" }
            LedgerStore.transact a intent (fun s ->
                LedgerStore.readAt a s path
                |> Result.map (fun (_, oid) -> [{ Path = path; ExpectedBlobOid = oid; Content = raw }]))
            |> ignoreGood
        corrupt "records/project-capacity-policy/policy.json" "{}"
        Assert.That(held b (snapshot b) "Worker" |> Result.isError, Is.True)
        expect b head "Worker" true)
