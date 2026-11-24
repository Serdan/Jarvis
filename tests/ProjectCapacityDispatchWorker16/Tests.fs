module ProjectCapacityDispatchWorker16Tests
open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.ProjectCapacityAdmission
open Client.ProjectCapacityDispatchTransition
open ProjectCapacityDisposableTests

[<Test>]
let concurrentOneShotAndRestart () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        let original, effect = Guid.NewGuid(), Guid.NewGuid()
        reserve a "worker" original effect "Worker" 2 3 |> good |> ignore
        let run binding op = beginTransition binding "worker" op original effect "Worker" 2 3
        let op1, op2 = Guid.NewGuid(), Guid.NewGuid()
        let x = Task.Run(fun () -> run a op1)
        let y = Task.Run(fun () -> run b op2)
        Task.WaitAll [| x :> Task; y :> Task |]
        let results = [x.Result; y.Result]
        Assert.That(results |> List.filter ((=) (Ok Committed)) |> List.length, Is.EqualTo(1))
        Assert.That(results |> List.filter Result.isError |> List.length, Is.EqualTo(1))
        let winner = if x.Result = Ok Committed then op1 else op2
        // Simulate loss of all local process state with a freshly cloned repository.
        let freshPath = Path.Combine(Path.GetDirectoryName(a.RepositoryPath), "restart")
        git (Path.GetDirectoryName(a.RepositoryPath)) [ "clone"; a.ApprovedFetchUrl; freshPath ] |> ignore
        let fresh = { a with RepositoryPath = freshPath }
        Assert.That(run fresh winner |> good, Is.EqualTo(Replay))
        Assert.That(run fresh (Guid.NewGuid()) |> Result.isError, Is.True)
        Assert.That(reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> Result.isError, Is.True))

[<Test>]
let invalidIdentityAndUnknownFailBeforeTransition () =
    withClones (fun a b ->
        let original, effect = Guid.NewGuid(), Guid.NewGuid()
        let run op orig generation = beginTransition b "worker" op orig effect "Worker" 2 generation
        Assert.That(run (Guid.NewGuid()) original 3 |> Result.isError, Is.True)
        initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
        reserve a "worker" original effect "Worker" 2 3 |> good |> ignore
        Assert.That(run (Guid.NewGuid()) original 4 |> Result.isError, Is.True)
        Assert.That(run (Guid.NewGuid()) (Guid.NewGuid()) 3 |> Result.isError, Is.True)
        let op = Guid.NewGuid()
        Assert.That(run op original 3 |> good, Is.EqualTo(Committed))
        Assert.That(run op original 4 |> Result.isError, Is.True))
