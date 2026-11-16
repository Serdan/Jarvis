module Client.Tests.AgentRegistrationSetupTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions
open NUnit.Framework

[<TestFixture>]
type AgentRegistrationSetupTests() =
    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        Assert.That(proc.Start(), Is.True)
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        Assert.That(proc.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected ledger failure: %A" error

    let expectStale = function
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected stale conflict: %A" other)

    let expectMissing binding agentId =
        match ReviewWorkflow.getAgentInbox binding agentId with
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Registration must leave no agent: %A" other)

    let fixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-register-setup-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let a = Path.Combine(root, "client-a")
            let b = Path.Combine(root, "client-b")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; a ] |> ignore
            git a [ "config"; "user.name"; "Register Fixture" ] |> ignore
            git a [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(a, "source.txt"), "base")
            git a [ "add"; "source.txt" ] |> ignore
            git a [ "commit"; "-m"; "base" ] |> ignore
            git a [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; b ] |> ignore
            git b [ "config"; "user.name"; "Register Fixture" ] |> ignore
            git b [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> ok |> ignore
            action first second
        finally
            for checkout in [ "client-a"; "client-b" ] do
                let dir = Path.Combine(root, checkout)
                let path = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot dir
                let pending = Path.Combine(Path.GetDirectoryName path, "pending", Path.GetFileNameWithoutExtension path)
                if Directory.Exists pending then Directory.Delete(pending, true)
                if File.Exists path then File.Delete path
                if File.Exists(path + ".lock") then File.Delete(path + ".lock")
            Directory.Delete(root, true)

    let createRole binding =
        RoleInstructions.createRole binding "Lead" (Guid.NewGuid()) "Implementer"
            "Implement assigned tasks" "Read the task spec and implement it." |> ok |> ignore

    let createTask binding taskId =
        ReviewWorkflow.createTaskWithSpecAs binding "Lead" (Guid.NewGuid())
            taskId taskId ("<h1>" + taskId + "</h1>") |> ok |> ignore

    [<Test>]
    member _.AllFourRegistrationCombinationsAreAtomicAndPreserveInitialStates() =
        fixture (fun a b ->
            createRole a
            createTask a "task-only"
            createTask a "role-and-task"

            let bare = ReviewWorkflow.registerAgentWithSetupAs a "Lead" (Guid.NewGuid()) "Bare" "" "" |> ok
            Assert.That(bare.Operation.Paths.Length, Is.EqualTo(1))
            let bareInbox = ReviewWorkflow.getAgentInbox b "Bare" |> ok
            Assert.That(bareInbox.Agent.State, Is.EqualTo("Parked"))
            Assert.That(bareInbox.Agent.Execution, Is.EqualTo("Stopped"))
            Assert.That((RoleInstructions.getAgentConfig b "Bare" |> ok).IsNone, Is.True)

            let roleOnly = ReviewWorkflow.registerAgentWithSetupAs a "Lead" (Guid.NewGuid())
                               "RoleOnly" "Implementer" "" |> ok
            Assert.That(roleOnly.Operation.Paths.Length, Is.EqualTo(2))
            let roleInbox = ReviewWorkflow.getAgentInbox b "RoleOnly" |> ok
            Assert.That(roleInbox.Agent.State, Is.EqualTo("Parked"))
            Assert.That(roleInbox.Agent.CurrentAssignment, Is.Null)
            Assert.That(roleInbox.InstructionLayers.RoleIds, Is.EqualTo(box [ "Implementer" ]))
            Assert.That(roleInbox.InstructionLayers.Documents.Head.Content,
                        Is.EqualTo("Read the task spec and implement it."))

            let taskOperation = Guid.NewGuid()
            let taskOnly = ReviewWorkflow.registerAgentWithSetupAs a "Lead" taskOperation
                               "TaskOnly" "" "task-only" |> ok
            Assert.That(taskOnly.Operation.Paths.Length, Is.EqualTo(4))
            let taskInbox = ReviewWorkflow.getAgentInbox b "TaskOnly" |> ok
            Assert.That(taskInbox.Agent.State, Is.EqualTo("Ready"))
            Assert.That(taskInbox.Agent.Execution, Is.EqualTo("Stopped"))
            Assert.That(taskInbox.Agent.CurrentAssignment.TaskId, Is.EqualTo("task-only"))
            Assert.That(taskInbox.Agent.CurrentAssignment.AttemptId,
                        Is.EqualTo(ReviewWorkflow.registrationAttemptId taskOperation "TaskOnly" "task-only"))
            Assert.That((RoleInstructions.getAgentConfig b "TaskOnly" |> ok).IsNone, Is.True)

            let fullOperation = Guid.NewGuid()
            let both = ReviewWorkflow.registerAgentWithSetupAs a "Lead" fullOperation
                           "Both" "Implementer" "role-and-task" |> ok
            Assert.That(both.Operation.Paths.Length, Is.EqualTo(5))
            let fullInbox = ReviewWorkflow.getAgentInbox b "Both" |> ok
            Assert.That(fullInbox.Agent.State, Is.EqualTo("Ready"))
            Assert.That(fullInbox.InstructionLayers.RoleIds, Is.EqualTo(box [ "Implementer" ]))
            Assert.That(fullInbox.Agent.CurrentAssignment.AttemptId,
                        Is.EqualTo(ReviewWorkflow.registrationAttemptId fullOperation "Both" "role-and-task"))
            let _, task, maybeAttempt = ReviewWorkflow.getTask b "role-and-task" |> ok
            Assert.That(task.Lifecycle, Is.EqualTo("InProgress"))
            let attempt = maybeAttempt.Value
            Assert.That(attempt.Phase, Is.EqualTo("Queued"))
            Assert.That(attempt.AssignmentGeneration, Is.EqualTo(1))
            Assert.That(attempt.SpecBlobOid, Is.Not.Empty)
            let _, _, _, issue, _ = ReviewWorkflow.getAssignment b "role-and-task" 1 |> ok
            Assert.That(issue.IssuerId, Is.EqualTo("Lead"))
            Assert.That(issue.SpecBlobOid, Is.EqualTo(attempt.SpecBlobOid)))

    [<Test>]
    member _.ReplayAndInvalidReferencesCannotPartiallyRegister() =
        fixture (fun a b ->
            createRole a
            createTask a "registered-task"
            let id = Guid.NewGuid()
            let original = ReviewWorkflow.registerAgentWithSetupAs a "Lead" id
                               "Registered" "Implementer" "registered-task" |> ok
            let replay = ReviewWorkflow.registerAgentWithSetupAs b "Lead" id
                             "Registered" "Implementer" "registered-task" |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.OriginalResultJson,
                        Is.EqualTo(original.Operation.OriginalResultJson))
            match ReviewWorkflow.registerAgentWithSetupAs b "Lead" id
                      "Registered" "" "registered-task" with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Changed setup must reject reused UUID: %A" other)

            expectStale (ReviewWorkflow.registerAgentWithSetupAs a "Lead" (Guid.NewGuid())
                             "MissingRole" "NotARole" "")
            expectMissing b "MissingRole"
            expectStale (ReviewWorkflow.registerAgentWithSetupAs a "Lead" (Guid.NewGuid())
                             "MissingTask" "Implementer" "not-a-task")
            expectMissing b "MissingTask"
            expectStale (ReviewWorkflow.registerAgentWithSetupAs a "Lead" (Guid.NewGuid())
                             "AlreadyOwned" "Implementer" "registered-task")
            expectMissing b "AlreadyOwned"
            expectStale (ReviewWorkflow.registerAgentWithSetupAs a "Lead" (Guid.NewGuid())
                             "Registered" "Implementer" "")
            let _, task, attempt = ReviewWorkflow.getTask b "registered-task" |> ok
            Assert.That(task.AttemptId, Is.EqualTo(attempt.Value.AttemptId))
            Assert.That(attempt.Value.OwnerAgentId, Is.EqualTo("Registered")))

    [<Test>]
    member _.CorruptCanonicalSpecRejectsRegistrationWithoutPartialWrites() =
        fixture (fun a b ->
            createTask a "corrupt-spec"
            let path = "tasks/corrupt-spec/spec.html"
            let snapshot = LedgerStore.fetch a |> ok
            let _, blob = LedgerStore.readAt a snapshot path |> ok
            let corruption : LedgerIntent =
                { OperationId = Guid.NewGuid(); Actor = "Lead"; Kind = "TestCorruptSpec"
                  RequestJson = "{\"taskId\":\"corrupt-spec\"}" }
            LedgerStore.transact a corruption (fun _ ->
                Ok [ { Path = path; ExpectedBlobOid = blob; Content = "<script>bad()</script>" } ])
            |> ok |> ignore
            match ReviewWorkflow.registerAgentWithSetupAs a "Lead" (Guid.NewGuid())
                      "CorruptTarget" "" "corrupt-spec" with
            | Error(LedgerIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Corrupt HTML must fail closed: %A" other)
            expectMissing b "CorruptTarget")

    [<Test>]
    member _.ConcurrentRegistrationsCannotBothClaimTheSameTask() =
        fixture (fun a b ->
            createTask a "contested"
            let opA, opB = Guid.NewGuid(), Guid.NewGuid()
            let left = Task.Run(fun () ->
                ReviewWorkflow.registerAgentWithSetupAs a "Lead" opA "ContenderA" "" "contested")
            let right = Task.Run(fun () ->
                ReviewWorkflow.registerAgentWithSetupAs b "Lead" opB "ContenderB" "" "contested")
            Task.WaitAll([| left :> Task; right :> Task |])
            let succeeded = [ left.Result; right.Result ] |> List.filter Result.isOk
            Assert.That(succeeded.Length, Is.EqualTo(1))
            let _, task, attempt = ReviewWorkflow.getTask a "contested" |> ok
            let winner = attempt.Value.OwnerAgentId
            Assert.That(winner = "ContenderA" || winner = "ContenderB", Is.True)
            Assert.That(task.Lifecycle, Is.EqualTo("InProgress"))
            let loser = if winner = "ContenderA" then "ContenderB" else "ContenderA"
            expectMissing a loser)
