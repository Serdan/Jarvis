module ProjectCapacityExclusionWorker21Tests
open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.ProjectCapacityAdmission
open Client.ProjectCapacityDispatchTransition
open Client.ProjectCapacityExclusionTransition
open ProjectCapacityDisposableTests

let setup a =
    initializeFixture a "fixture" (Guid.NewGuid()) 1 Map.empty |> good |> ignore
    let original, effect = Guid.NewGuid(), Guid.NewGuid()
    reserve a "worker" original effect "Worker" 2 3 |> good |> ignore
    original, effect

let exclude binding op original effect =
    excludeTransition binding "worker" op original effect "Worker" 2 3
let dispatch binding op original effect =
    beginTransition binding "worker" op original effect "Worker" 2 3

[<Test>]
let exclusionWinsAndSurvivesRecovery () =
    withClones (fun a b ->
        let original, effect = setup a
        let op = Guid.NewGuid()
        Assert.That(exclude a op original effect |> good, Is.EqualTo(Committed))
        Assert.That(dispatch b (Guid.NewGuid()) original effect |> Result.isError, Is.True)
        // A later reservation must survive replay of the original exclusion receipt.
        Assert.That(reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> Result.isOk, Is.True)
        let freshPath = Path.Combine(Path.GetDirectoryName(a.RepositoryPath), "exclusion-restart")
        git (Path.GetDirectoryName(a.RepositoryPath)) [ "clone"; a.ApprovedFetchUrl; freshPath ] |> ignore
        let fresh = { a with RepositoryPath = freshPath }
        Assert.That(exclude fresh op original effect |> good, Is.EqualTo(Replay))
        Assert.That(dispatch fresh (Guid.NewGuid()) original effect |> Result.isError, Is.True)
        Assert.That(exclude fresh (Guid.NewGuid()) original effect |> Result.isError, Is.True)
        Assert.That(reserve fresh "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> Result.isError, Is.True))

[<Test>]
let dispatchWinsAndCapacityRemainsOccupied () =
    withClones (fun a b ->
        let original, effect = setup a
        Assert.That(dispatch a (Guid.NewGuid()) original effect |> good, Is.EqualTo(Committed))
        Assert.That(exclude b (Guid.NewGuid()) original effect |> Result.isError, Is.True)
        Assert.That(reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 1 1 |> Result.isError, Is.True))

[<Test>]
let wrongIdentityAndReplayCannotChangeRequest () =
    withClones (fun a b ->
        let original, effect = setup a
        let run actor op orig eff role version generation =
            excludeTransition b actor op orig eff role version generation
        let fail actor op orig eff role version generation =
            Assert.That(run actor op orig eff role version generation |> Result.isError, Is.True)
        fail "worker" (Guid.NewGuid()) (Guid.NewGuid()) effect "Worker" 2 3
        fail "other" (Guid.NewGuid()) original effect "Worker" 2 3
        fail "worker" (Guid.NewGuid()) original (Guid.NewGuid()) "Worker" 2 3
        fail "worker" (Guid.NewGuid()) original effect "Reviewer" 2 3
        fail "worker" (Guid.NewGuid()) original effect "Worker" 1 3
        fail "worker" (Guid.NewGuid()) original effect "Worker" 2 4
        fail "worker" Guid.Empty original effect "Worker" 2 3
        fail "worker" effect original effect "Worker" 2 3
        fail "worker" original original effect "Worker" 2 3
        let op = Guid.NewGuid()
        exclude a op original effect |> good |> ignore
        fail "worker" op original effect "Worker" 2 4)

[<Test>]
let exclusionOnlySettlesExactReservation () =
    withClones (fun a b ->
        initializeFixture a "fixture" (Guid.NewGuid()) 2 Map.empty |> good |> ignore
        let firstOriginal, firstEffect = Guid.NewGuid(), Guid.NewGuid()
        let secondOriginal, secondEffect = Guid.NewGuid(), Guid.NewGuid()
        reserve a "worker" firstOriginal firstEffect "Worker" 2 3 |> good |> ignore
        reserve a "worker" secondOriginal secondEffect "Worker" 2 3 |> good |> ignore
        Assert.That(exclude a (Guid.NewGuid()) firstOriginal firstEffect |> good, Is.EqualTo(Committed))
        Assert.That(dispatch b (Guid.NewGuid()) secondOriginal secondEffect |> good, Is.EqualTo(Committed))
        reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 2 3 |> good |> ignore
        Assert.That(reserve b "worker" (Guid.NewGuid()) (Guid.NewGuid()) "Worker" 2 3 |> Result.isError, Is.True))

[<Test>]
let simultaneousTransitionsHaveOneWinner () =
    withClones (fun a b ->
        let original, effect = setup a
        let x = Task.Run(fun () -> exclude a (Guid.NewGuid()) original effect |> Result.isOk)
        let y = Task.Run(fun () -> dispatch b (Guid.NewGuid()) original effect |> Result.isOk)
        Task.WaitAll [| x :> Task; y :> Task |]
        Assert.That((if x.Result then 1 else 0) + (if y.Result then 1 else 0), Is.EqualTo(1)))
