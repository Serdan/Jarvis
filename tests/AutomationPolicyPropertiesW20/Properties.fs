module AutomationPolicyPropertiesW20

open System
open NUnit.Framework
open Client.LedgerStore
open Client.ProjectCapacityPolicy
open ProjectCapacityDisposableTests

// The model stores only the last accepted revision and policy values.
[<TestCase(17)>]
[<TestCase(41)>]
let policyHistories seed =
    withClones (fun a b ->
        let rng = Random(seed)
        let mutable revision = 0
        let mutable total = 0
        let mutable roles = Map.empty
        for step in 0 .. 17 do
            let binding = if step % 2 = 0 then a else b
            let nextTotal = rng.Next(1, 6)
            let nextRoles =
                if rng.Next(2) = 0 then Map.ofList [ "Worker", rng.Next(1, 5) ]
                else Map.ofList [ "Reviewer", rng.Next(1, 5); "Worker", rng.Next(1, 5) ]
            let expected = if revision > 0 && step % 3 = 1 then revision - 1 else revision
            let op = Guid.NewGuid()
            let outcome = update binding "operator" op expected nextTotal nextRoles
            let context = sprintf "seed=%d step=%d revision=%d expected=%d" seed step revision expected
            Assert.That(Result.isOk outcome, Is.EqualTo((expected = revision)), context)
            if expected = revision then
                revision <- revision + 1
                total <- nextTotal
                roles <- nextRoles
                let retry = update (if step % 2 = 0 then b else a) "operator" op expected nextTotal nextRoles |> good
                Assert.That(retry.AlreadyApplied, Is.True, context)
                Assert.That(update binding "operator" op expected (nextTotal + 1) nextRoles |> Result.isError, Is.True, context)
            let reader = if step % 2 = 0 then b else a
            let snapshot = LedgerStore.fetch reader |> good
            let current = readForRole reader snapshot revision "Worker" |> good |> fst
            Assert.That(current.Revision, Is.EqualTo(revision), context)
            Assert.That(current.Total, Is.EqualTo(total), context)
            Assert.That((current.Roles = roles), Is.True, context)
            Assert.That(readForRole reader snapshot (revision + 1) "Worker" |> Result.isError, Is.True, context)
            Assert.That(readForRole reader snapshot revision "Unknown" |> Result.isError, Is.True, context))
