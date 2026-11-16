module Client.Tests.WorkCompositionLedgerTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Client.LedgerStore
open Client.WorkCompositionGitLedger
open Jarvis.WorkQueues

let private succeed = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected ledger failure: %A" error

let private git directory arguments =
    use runner = new Process()
    runner.StartInfo.FileName <- "git"
    runner.StartInfo.WorkingDirectory <- directory
    runner.StartInfo.RedirectStandardOutput <- true
    runner.StartInfo.RedirectStandardError <- true
    for argument in arguments do runner.StartInfo.ArgumentList.Add(argument)
    Assert.That(runner.Start(), Is.True)
    let output = runner.StandardOutput.ReadToEnd()
    let error = runner.StandardError.ReadToEnd()
    runner.WaitForExit()
    Assert.That(runner.ExitCode, Is.EqualTo(0), error)
    output.Trim()

let private withTwoClones action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-composition-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let remote = Path.Combine(root, "remote.git")
        let first = Path.Combine(root, "first")
        let second = Path.Combine(root, "second")
        git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
        git root [ "clone"; remote; first ] |> ignore
        File.WriteAllText(Path.Combine(first, "bootstrap.txt"), "source")
        git first [ "add"; "bootstrap.txt" ] |> ignore
        git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "bootstrap" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; remote; second ] |> ignore
        let projectId = Guid.NewGuid()
        let ledgerId = Guid.NewGuid()
        let binding repository =
            { RepositoryPath = repository
              RemoteName = "origin"
              ApprovedFetchUrl = remote
              ApprovedPushUrl = remote
              ProjectId = projectId
              LedgerId = ledgerId
              Epoch = 1 }
        let firstBinding, secondBinding = binding first, binding second
        LedgerStore.initialize firstBinding true |> succeed |> ignore
        LedgerStore.fetch secondBinding |> succeed |> ignore
        action firstBinding secondBinding
    finally
        try Directory.Delete(root, true) with _ -> ()

let private spec title task =
    { Title = title
      Goal = "Deliver " + title
      Scope = "Required outcome"
      AcceptanceCriteria = Set.singleton ("Test " + title)
      Constraints = Set.empty
      LinkedTaskId = task }

[<Test>]
let ``composition graph is durable and shared by two clients through project ledger CAS`` () =
    withTwoClones (fun first second ->
        let _, initial = WorkCompositionGitLedger.load first |> succeed
        Assert.That(initial.Version, Is.EqualTo(0))
        let root = WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None)
        let originalOperation = Guid.NewGuid()
        let write = WorkCompositionGitLedger.execute first "Fixture" originalOperation 0 root |> succeed
        Assert.That(write.AlreadyApplied, Is.False)
        let duplicate = WorkCompositionGitLedger.execute second "Fixture" originalOperation 0 root |> succeed
        Assert.That(duplicate.AlreadyApplied, Is.True)
        let _, graph = WorkCompositionGitLedger.load second |> succeed
        Assert.That(graph.Version, Is.EqualTo(1))
        Assert.That(graph.Units.["project"].Spec.Title, Is.EqualTo("Project"))
        match WorkCompositionGitLedger.execute second "Fixture" (Guid.NewGuid()) 0
                 (WorkCompositionMutation.Create("system", WorkUnitKind.System, Some "project", spec "System" None)) with
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected version conflict: %A" other)
        WorkCompositionGitLedger.execute second "Fixture" (Guid.NewGuid()) 1
            (WorkCompositionMutation.Create("system", WorkUnitKind.System, Some "project", spec "System" None))
        |> succeed |> ignore
        let _, reloaded = WorkCompositionGitLedger.load first |> succeed
        Assert.That(reloaded.Version, Is.EqualTo(2))
        Assert.That(reloaded.Units.["system"].ParentId, Is.EqualTo(Some "project")))

[<Test>]
let ``acceptance mutation rejects unaudited evidence and missing WQ coverage is not readiness`` () =
    withTwoClones (fun first second ->
        WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) 0
            (WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None))
        |> succeed |> ignore
        WorkCompositionGitLedger.execute second "Fixture" (Guid.NewGuid()) 1
            (WorkCompositionMutation.Create("task", WorkUnitKind.Feature, Some "project", spec "Task" (Some "canonical-task")))
        |> succeed |> ignore
        match WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) 2
                 (WorkCompositionMutation.Accept("task", 1, "caller-string")) with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected fail-closed acceptance: %A" other)
        let overviews = WorkCompositionGitLedger.loadOverviews second |> succeed
        let task = overviews |> List.find (fun view -> view.Id = "task")
        Assert.That(task.Phase, Is.EqualTo(WorkUnitPhase.Unverified)))
