module ProjectInventoryCompositionTests
open System
open System.IO
open System.Diagnostics
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ProjectCapacityAdmission
open ProjectInventoryComposedFixture

let good = function Ok x -> x | Error e -> failwithf "%A" e
let git directory args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for a in args do p.StartInfo.ArgumentList.Add a
    if not (p.Start()) then failwith "git start"
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode <> 0 then failwith error
    output.Trim()

let withClones test =
    let root = Path.Combine(Path.GetTempPath(), "inventory-composed-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let bare, first, second = Path.Combine(root,"remote.git"), Path.Combine(root,"a"), Path.Combine(root,"b")
        git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
        git root ["clone"; bare; first] |> ignore
        File.WriteAllText(Path.Combine(first,"source"), "fixture")
        git first ["add"; "source"] |> ignore
        git first ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "source"] |> ignore
        git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
        git root ["clone"; bare; second] |> ignore
        let project, ledger = Guid.NewGuid(), Guid.NewGuid()
        let binding repo = { RepositoryPath = repo; RemoteName = "origin"; ApprovedFetchUrl = bare
                             ApprovedPushUrl = bare; ProjectId = project; LedgerId = ledger; Epoch = 1 }
        let a, b = binding first, binding second
        LedgerStore.initialize a true |> good |> ignore
        LedgerStore.fetch b |> good |> ignore
        test a b
    finally
        try Directory.Delete(root, true) with _ -> ()

[<Test>]
let missingStaleAndReplayMismatch () =
    withClones (fun a b ->
        let fence, op, effect = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        Assert.That(ProjectInventoryComposedFixture.reserve a "worker" op effect "Worker" 1 1 fence |> Result.isError, Is.True)
        initialize a "fixture" (Guid.NewGuid()) 1 fence |> good |> ignore
        Assert.That(ProjectInventoryComposedFixture.reserve b "worker" op effect "Worker" 1 2 fence |> Result.isError, Is.True)
        Assert.That(ProjectInventoryComposedFixture.reserve b "worker" op effect "Worker" 1 1 (Guid.NewGuid()) |> Result.isError, Is.True)
        let receipt = ProjectInventoryComposedFixture.reserve b "worker" op effect "Worker" 1 1 fence |> good
        Assert.That(receipt.AlreadyApplied, Is.False)
        let replay = ProjectInventoryComposedFixture.reserve a "worker" op effect "Worker" 1 1 fence |> good
        Assert.That(replay.AlreadyApplied, Is.True)
        Assert.That(ProjectInventoryComposedFixture.reserve a "worker" op effect "Reviewer" 1 1 fence |> Result.isError, Is.True)
        let snapshot = LedgerStore.fetch a |> good
        let inventory, _ = LedgerStore.readAt a snapshot path |> good
        Assert.That(inventory.Value.Contains(effect.ToString()), Is.True)
        Assert.That(ProjectInventoryComposedFixture.reserve a "other" (Guid.NewGuid()) (Guid.NewGuid()) "Reviewer" 1 1 fence |> Result.isError, Is.True))

[<Test>]
let competingClientsCommitOneReservation () =
    withClones (fun a b ->
        let fence = Guid.NewGuid()
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        initialize a "fixture" (Guid.NewGuid()) 1 fence |> good |> ignore
        let f binding = Task.Run(fun () -> ProjectInventoryComposedFixture.reserve binding "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 fence)
        let x, y = f a, f b
        Task.WaitAll [| x :> Task; y :> Task |]
        Assert.That([x.Result; y.Result] |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        let snapshot = LedgerStore.fetch b |> good
        let state, _ = read b snapshot 1 fence |> good
        Assert.That(state.Effects.Length, Is.EqualTo(1)))

[<Test>]
let changedFenceOnRetryCannotReserve () =
    withClones (fun a b ->
        let fence, op, effect = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        initialize a "fixture" (Guid.NewGuid()) 1 fence |> good |> ignore
        use entered = new System.Threading.ManualResetEventSlim(false)
        use proceed = new System.Threading.ManualResetEventSlim(false)
        let mutable calls = 0
        let request = {| project = a.ProjectId; ledger = a.LedgerId; epoch = a.Epoch; operation = op
                         actor = "worker"; effect = effect; role = "Worker"; roleVersion = 1
                         generation = 1; fence = fence |}
        let attempt = Task.Run(fun () ->
            LedgerStore.transact a (intent "worker" op "FixtureInventory.Reserve" request) (fun snapshot ->
                calls <- calls + 1
                let result = plan a snapshot "worker" op effect "Worker" 1 1 fence
                if calls = 1 then
                    entered.Set()
                    if not (proceed.Wait(TimeSpan.FromSeconds 30.)) then failwith "barrier timeout"
                result))
        Assert.That(entered.Wait(TimeSpan.FromSeconds 30.), Is.True)
        try
            let newFence = Guid.NewGuid()
            LedgerStore.transact b (intent "fixture" (Guid.NewGuid()) "FixtureInventory.ChangeFence" {| fence = newFence |}) (fun snapshot ->
                read b snapshot 1 fence |> Result.map (fun (state, oid) ->
                    [{ Path = path; ExpectedBlobOid = Some oid; Content = encode { state with Fence = newFence } }]))
            |> good |> ignore
        finally proceed.Set()
        Assert.That(attempt.Result |> Result.isError, Is.True)
        Assert.That(calls, Is.GreaterThanOrEqualTo(2))
        // No capacity was committed with the rejected inventory plan.
        Client.ProjectCapacityAdmission.reserve b "other" (Guid.NewGuid()) (Guid.NewGuid()) "Reviewer" 1 1 |> good |> ignore)

[<Test>]
let freshCloneRetainsUncertainReservation () =
    withClones (fun a b ->
        let fence, effect = Guid.NewGuid(), Guid.NewGuid()
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        initialize a "fixture" (Guid.NewGuid()) 1 fence |> good |> ignore
        ProjectInventoryComposedFixture.reserve a "worker" (Guid.NewGuid()) effect "Worker" 1 1 fence |> good |> ignore
        let root = Directory.GetParent(a.RepositoryPath).FullName
        let reopened = Path.Combine(root, "reopened")
        git root ["clone"; a.ApprovedFetchUrl; reopened] |> ignore
        let fresh = { b with RepositoryPath = reopened }
        let snapshot = LedgerStore.fetch fresh |> good
        let state, _ = read fresh snapshot 1 fence |> good
        Assert.That(state.Effects, Does.Contain(effect))
        Assert.That(ProjectInventoryComposedFixture.reserve fresh "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 fence |> Result.isError, Is.True))
