module ProjectCapacityDisposableTests
open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ProjectCapacityAdmission

let git directory args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for a in args do p.StartInfo.ArgumentList.Add a
    if not (p.Start()) then failwith "git failed to start"
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode <> 0 then failwith error
    output.Trim()

let good = function Ok x -> x | Error e -> failwithf "%A" e
let withClones work =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-project-capacity-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let bare = Path.Combine(root, "remote.git")
        let first = Path.Combine(root, "first")
        let second = Path.Combine(root, "second")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; first ] |> ignore
        File.WriteAllText(Path.Combine(first, "source.txt"), "fixture")
        git first [ "add"; "source.txt" ] |> ignore
        git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "source" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; bare; second ] |> ignore
        let project, ledger = Guid.NewGuid(), Guid.NewGuid()
        let binding repo =
            { RepositoryPath = repo; RemoteName = "origin"; ApprovedFetchUrl = bare; ApprovedPushUrl = bare
              ProjectId = project; LedgerId = ledger; Epoch = 1 }
        let a, b = binding first, binding second
        LedgerStore.initialize a true |> good |> ignore
        LedgerStore.fetch b |> good |> ignore
        work a b
    finally
        try Directory.Delete(root, true) with _ -> ()

[<Test>]
let unknownAndMismatchesFailClosed () =
    withClones (fun a b ->
        let effect, op = Guid.NewGuid(), Guid.NewGuid()
        Assert.That(reserve a "worker" op effect "Worker" 1 1 |> Result.isError, Is.True)
        initializeFixture a "fixture" (Guid.NewGuid()) 2 (Map.ofList ["Worker", 1]) |> good |> ignore
        reserve a "worker" op effect "Worker" 1 1 |> good |> ignore
        Assert.That(reserve b "worker" op effect "Reviewer" 1 1 |> Result.isError, Is.True)
        Assert.That(excludeBeforeSend b "worker" (Guid.NewGuid()) op effect "Worker" 1 2 |> Result.isError, Is.True)
        Assert.That(excludeBeforeSend b "worker" (Guid.NewGuid()) (Guid.NewGuid()) effect "Worker" 1 1 |> Result.isError, Is.True)
        Assert.That(excludeBeforeSend b "other" (Guid.NewGuid()) op effect "Worker" 1 1 |> Result.isError, Is.True)
        Assert.That(excludeBeforeSend b "worker" (Guid.NewGuid()) op effect "Worker" 2 1 |> Result.isError, Is.True)
        Assert.That(reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> Result.isError, Is.True)
        excludeBeforeSend b "worker" (Guid.NewGuid()) op effect "Worker" 1 1 |> good |> ignore
        let replay = reserve a "worker" op effect "Worker" 1 1 |> good
        Assert.That(replay.AlreadyApplied, Is.True)
        reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> good |> ignore)

[<Test>]
let concurrentTotalCapacity () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        let f binding role = Task.Run(fun () -> reserve binding role (Guid.NewGuid()) (Guid.NewGuid()) role 1 1)
        let x, y = f a "Worker", f b "Reviewer"
        Task.WaitAll [| x :> Task; y :> Task |]
        let results = [x.Result; y.Result]
        Assert.That(results |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        Assert.That(results |> List.filter Result.isError |> List.length, Is.EqualTo(1)))

[<Test>]
let sentRemainsOccupied () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        let effect, op = Guid.NewGuid(), Guid.NewGuid()
        reserve a "worker" op effect "Worker" 1 3 |> good |> ignore
        beginSend b "worker" (Guid.NewGuid()) op effect "Worker" 1 3 |> good |> ignore
        Assert.That(excludeBeforeSend a "worker" (Guid.NewGuid()) op effect "Worker" 1 3 |> Result.isError, Is.True)
        Assert.That(reserve a "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> Result.isError, Is.True))

[<Test>]
let concurrentRoleCapacity () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 3 (Map.ofList ["Worker", 1]) |> good |> ignore
        let f binding = Task.Run(fun () -> reserve binding "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1)
        let x, y = f a, f b
        Task.WaitAll [| x :> Task; y :> Task |]
        Assert.That([x.Result; y.Result] |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        reserve b "reviewer" (Guid.NewGuid()) (Guid.NewGuid()) "Reviewer" 1 1 |> good |> ignore)

[<Test>]
let sendAndExclusionRace () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        let effect, op = Guid.NewGuid(), Guid.NewGuid()
        reserve a "worker" op effect "Worker" 1 1 |> good |> ignore
        let send = Task.Run(fun () -> beginSend a "worker" (Guid.NewGuid()) op effect "Worker" 1 1)
        let exclude = Task.Run(fun () -> excludeBeforeSend b "worker" (Guid.NewGuid()) op effect "Worker" 1 1)
        Task.WaitAll [| send :> Task; exclude :> Task |]
        Assert.That([send.Result; exclude.Result] |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
        let next = reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1
        Assert.That(Result.isOk next, Is.EqualTo(Result.isOk exclude.Result)))

[<Test>]
let inventoryFenceIsRecheckedAfterConcurrentChange () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 2 Map.empty |> good |> ignore
        let path = "records/project-capacity-disposable/inventory.json"
        let intent op kind = { OperationId = op; Actor = "fixture"; Kind = kind; RequestJson = "{}" }
        let setFence binding expected value =
            LedgerStore.transact binding (intent (Guid.NewGuid()) "Fixture.Fence") (fun snapshot ->
                LedgerStore.readAt binding snapshot path
                |> Result.bind (fun (raw, oid) ->
                    if raw <> expected then Error(InvalidBinding "fence changed")
                    else Ok [{ Path = path; ExpectedBlobOid = oid; Content = value }]))
        setFence a None "1" |> good |> ignore
        use entered = new System.Threading.ManualResetEventSlim(false)
        use proceed = new System.Threading.ManualResetEventSlim(false)
        let mutable calls = 0
        let op, effect = Guid.NewGuid(), Guid.NewGuid()
        let attempt = Task.Run(fun () ->
            LedgerStore.transact a (intent op "Fixture.Combined") (fun snapshot ->
                LedgerStore.readAt a snapshot path
                |> Result.bind (fun (raw, _) ->
                    calls <- calls + 1
                    if calls = 1 then
                        entered.Set()
                        if not (proceed.Wait(TimeSpan.FromSeconds 30.)) then failwith "timeout"
                    if raw <> Some "1" then Error(InvalidBinding "unknown or stale inventory fence")
                    else planReservation a snapshot "worker" op effect "Worker" 1 1)))
        Assert.That(entered.Wait(TimeSpan.FromSeconds 30.), Is.True)
        try setFence b (Some "1") "2" |> good |> ignore
        finally proceed.Set()
        let result = attempt.Result
        Assert.That(Result.isError result, Is.True)
        Assert.That(calls, Is.GreaterThanOrEqualTo(2))
        // The failed combined transaction cannot have consumed the capacity.
        reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> good |> ignore)

[<Test>]
let unknownInventoryFailsClosedAndCombinedReserveCommits () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        let path = "records/project-capacity-disposable/inventory.json"
        let op, effect = Guid.NewGuid(), Guid.NewGuid()
        let intent = { OperationId = op; Actor = "worker"; Kind = "Fixture.Combined"; RequestJson = "{}" }
        let combined () =
            LedgerStore.transact a intent (fun snapshot ->
                LedgerStore.readAt a snapshot path |> Result.bind (fun (raw, oid) ->
                    match raw, oid with
                    | Some "ready", Some oid ->
                        planReservation a snapshot "worker" op effect "Worker" 1 1
                        |> Result.map (fun changes -> { Path = path; ExpectedBlobOid = Some oid; Content = "claimed" } :: changes)
                    | _ -> Error(InvalidBinding "unknown inventory")))
        Assert.That(combined () |> Result.isError, Is.True)
        LedgerStore.transact b { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.Inventory"; RequestJson = "{}" }
            (fun snapshot -> LedgerStore.readAt b snapshot path |> Result.bind (fun (_, oid) ->
                Ok [{ Path = path; ExpectedBlobOid = oid; Content = "ready" }])) |> good |> ignore
        combined () |> good |> ignore
        Assert.That(reserve b "other" (Guid.NewGuid()) (Guid.NewGuid()) "Reviewer" 1 1 |> Result.isError, Is.True)
        let snapshot = LedgerStore.fetch b |> good
        let raw, _ = LedgerStore.readAt b snapshot path |> good
        Assert.That(raw, Is.EqualTo(Some "claimed")))
