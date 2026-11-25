module ProjectWorkingCountTests

open System
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open Client.ProjectWorkingCount
open ProjectCapacityDisposableTests

let op () = Guid.NewGuid()
let ignoreGood value = good value |> ignore
let snapshot binding = LedgerStore.fetch binding |> good
let register binding id = ReviewWorkflow.registerAgent binding (op ()) id |> ignoreGood
let createRole binding role = RoleInstructions.createRole binding "Operator" (op ()) role "Fixture" "Fixture" |> ignoreGood
let attach binding id role version = RoleInstructions.attachRole binding "Operator" (op ()) id role version |> ignoreGood
let agent binding id = ReviewWorkflow.getAgentWorkingTurnAt binding (snapshot binding) id |> good
let start binding id =
    ReviewWorkflow.startTasklessWork binding (op ()) id (agent binding id).Version "Fixture start" |> ignoreGood
let stop binding id =
    ReviewWorkflow.yieldTasklessWork binding (op ()) id (agent binding id).Version "Fixture stop" |> ignoreGood
let check binding head target total roles (expected: bool) =
    Assert.That(isHeldAt binding head target total roles |> good, Is.EqualTo(expected))
let workers n = Map.ofList ["Worker", n]

[<Test>]
let disabledAndInvalidLimits () =
    withClones (fun a b ->
        register a "Worker 1"
        start a "Worker 1"
        let head = snapshot b
        check b head "" None Map.empty false
        check b head "Reviewer" None (workers 1) false
        for total, roles in [Some 0, Map.empty; Some -1, Map.empty; None, workers 0; None, workers -1] do
            Assert.That(isHeldAt b head "Worker" total roles |> Result.isError, Is.True)
        Assert.That(isHeldAt b head "" None (workers 1) |> Result.isError, Is.True)
        Assert.That(isHeldAt { b with ProjectId = op () } head "Worker" (Some 1) Map.empty |> Result.isError, Is.True))

[<Test>]
let canonicalWorkingCountsThresholdsAndDrain () =
    withClones (fun a b ->
        for role in ["Worker"; "Reviewer"] do createRole a role
        for id in ["Worker 1"; "Worker 2"; "Reviewer 1"; "Ready worker"; "Idle worker"; "Unroled"] do register a id
        for id in ["Worker 1"; "Worker 2"; "Ready worker"; "Idle worker"] do attach a id "Worker" 0
        attach a "Worker 1" "Reviewer" 1
        attach a "Reviewer 1" "Reviewer" 0
        ReviewWorkflow.createTask a (op ()) "ready-fixture" "Ready fixture" |> ignoreGood
        ReviewWorkflow.assignTask a (op ()) "ready-fixture" (op ()) "Ready worker" |> ignoreGood
        Assert.That((agent a "Ready worker").State, Is.EqualTo("Ready"))
        start a "Worker 1"
        start a "Reviewer 1"
        start a "Unroled"
        let head = snapshot b
        check b head "Worker" (Some 4) Map.empty false
        check b head "Worker" (Some 3) Map.empty true
        check b head "Worker" (Some 2) Map.empty true
        check b head "Worker" None (workers 2) false
        check b head "Worker" None (workers 1) true
        check b head "Reviewer" None (Map.ofList ["Reviewer", 2]) true
        check b head "Worker" (Some 4) (workers 1) true
        check b head "Worker" (Some 3) (workers 2) true
        check b head "Worker" (Some 4) (workers 2) false
        // In-flight/manual starts can overshoot; no reservation or mutation.
        start a "Worker 2"
        let over = snapshot b
        check b over "Worker" (Some 3) (workers 1) true
        // Pinned old snapshot is not silently refreshed.
        check b head "Worker" (Some 4) (workers 2) false
        stop a "Worker 1"
        let draining = snapshot b
        check b draining "Worker" (Some 3) (workers 1) true
        stop a "Worker 2"
        check b (snapshot b) "Worker" (Some 3) (workers 1) false)
