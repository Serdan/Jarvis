module AutomationPolicyPropertiesW20

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ProjectCapacityPolicy
open AutomationHealthHostedFixture

let private withClones work =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-policy-properties-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let a,b,_,_,_ = create root
        for role in ["Worker"; "Reviewer"] do
            Client.RoleInstructions.RoleInstructions.createRole a "Operator" (Guid.NewGuid()) role "fixture" "fixture"
            |> success |> ignore
        work a b
    finally
        Directory.Delete(root,true)

// Independent accepted revision and optional dimensions, not a copy of the ledger.
[<TestCase(17)>]
[<TestCase(41)>]
let policyHistories seed =
    withClones (fun a b ->
        let rng = Random(seed)
        let mutable revision = 0
        let mutable total : int option = None
        let mutable roles = Map.empty
        let prefix = [| None,Map.empty; Some 2,Map.empty; None,Map.ofList ["Worker",1]; Some 3,Map.ofList ["Reviewer",2]; None,Map.empty |]
        for step in 0 .. 23 do
            let binding,reader = if step % 2 = 0 then a,b else b,a
            let nextTotal,nextRoles =
                if step < prefix.Length then prefix[step]
                else
                    (if rng.Next(2) = 0 then None else Some(rng.Next(1,6))),
                    (["Worker";"Reviewer"] |> List.choose (fun role -> if rng.Next(2) = 0 then None else Some(role,rng.Next(1,5))) |> Map.ofList)
            let expected = if step >= prefix.Length && step % 3 = 1 then revision - 1 else revision
            let op = Guid.NewGuid()
            let before = (LedgerStore.fetch reader |> success).Commit
            let outcome = updateOptional binding "operator" op expected nextTotal nextRoles
            let context = sprintf "seed=%d step=%d revision=%d expected=%d outcome=%A" seed step revision expected outcome
            Assert.That(Result.isOk outcome, Is.EqualTo((expected = revision)), context)
            if expected = revision then
                revision <- revision + 1
                total <- nextTotal
                roles <- nextRoles
                let committed = (LedgerStore.fetch reader |> success).Commit
                let retry = updateOptional reader "operator" op expected nextTotal nextRoles |> success
                Assert.That(retry.AlreadyApplied, Is.True, context)
                let changed = if nextTotal.IsSome then None else Some 1
                Assert.That(updateOptional binding "operator" op expected changed nextRoles |> Result.isError, Is.True, context)
                Assert.That((LedgerStore.fetch reader |> success).Commit, Is.EqualTo(committed), context)
            else
                Assert.That((LedgerStore.fetch reader |> success).Commit, Is.EqualTo(before), context)
            for client in [a;b] do
                let snapshot = LedgerStore.fetch client |> success
                let current = readCurrentOptional client snapshot |> success |> fst
                Assert.That(current.Revision, Is.EqualTo(revision), context)
                Assert.That(current.Total = total, Is.True, context)
                Assert.That(current.Roles = roles, Is.True, context)
                for role in ["Worker";"Reviewer"] do
                    Assert.That((readOptionalForRole client snapshot revision role |> success |> fst) = current, Is.True, context)
                Assert.That(readOptionalForRole client snapshot (revision + 1) "Worker" |> Result.isError, Is.True, context)
                Assert.That(readOptionalForRole client snapshot revision "Unknown" |> Result.isError, Is.True, context))

[<Test>]
let concurrentPolicyWritersHaveOneSuccessor () =
    withClones (fun a b ->
        let opA,opB = Guid.NewGuid(),Guid.NewGuid()
        let rolesA,rolesB = Map.empty,Map.ofList ["Worker",2]
        let first = Task.Run(fun () -> updateOptional a "operator" opA 0 (Some 2) rolesA)
        let second = Task.Run(fun () -> updateOptional b "operator" opB 0 None rolesB)
        Task.WaitAll [|first :> Task;second :> Task|]
        Assert.That([first.Result;second.Result] |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        let winnerOp,winnerTotal,winnerRoles =
            if Result.isOk first.Result then opA,Some 2,rolesA else opB,None,rolesB
        for reader in [a;b] do
            let snapshot = LedgerStore.fetch reader |> success
            let policy = readCurrentOptional reader snapshot |> success |> fst
            Assert.That(policy.Revision, Is.EqualTo(1))
            Assert.That(policy.Total = winnerTotal, Is.True)
            Assert.That(policy.Roles = winnerRoles, Is.True)
            let retry = updateOptional reader "operator" winnerOp 0 winnerTotal winnerRoles |> success
            Assert.That(retry.AlreadyApplied, Is.True)
            Assert.That((LedgerStore.fetch reader |> success).Commit, Is.EqualTo(snapshot.Commit)))

[<Test>]
let legacyPolicyUpgradeDoesNotInventCapsOrPermitDowngrade () =
    withClones (fun a b ->
        let roles = Map.ofList ["Worker",2]
        let oldOp = Guid.NewGuid()
        update a "operator" oldOp 0 3 roles |> success |> ignore
        let head = LedgerStore.fetch b |> success
        let old = readCurrentOptional b head |> success |> fst
        Assert.That(old.Schema, Is.EqualTo(1))
        Assert.That(old.Total = Some 3 && old.Roles = roles, Is.True)
        Assert.That(readOptionalForRole b head 1 "Reviewer" |> Result.isError, Is.True)
        updateOptional b "operator" (Guid.NewGuid()) 1 None Map.empty |> success |> ignore
        let upgraded = LedgerStore.fetch a |> success
        let current = readCurrentOptional a upgraded |> success |> fst
        Assert.That(current.Schema, Is.EqualTo(2))
        Assert.That(current.Revision, Is.EqualTo(2))
        Assert.That(current.Total.IsNone && Map.isEmpty current.Roles, Is.True)
        Assert.That(readOptionalForRole a upgraded 2 "Reviewer" |> Result.isOk, Is.True)
        Assert.That(readForRole a upgraded 2 "Worker" |> Result.isError, Is.True)
        Assert.That(update a "operator" (Guid.NewGuid()) 2 3 roles |> Result.isError, Is.True)
        let replay = update b "operator" oldOp 0 3 roles |> success
        Assert.That(replay.AlreadyApplied, Is.True)
        Assert.That((LedgerStore.fetch a |> success).Commit, Is.EqualTo(upgraded.Commit)))
