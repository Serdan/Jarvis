module AutomationHealthHostedFixture

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Client
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.NativeHealth
open Client.NativeHealth.NativeHealth

let git cwd (args: string list) =
    use child = new Process()
    child.StartInfo.FileName <- "git"
    child.StartInfo.WorkingDirectory <- cwd
    child.StartInfo.RedirectStandardOutput <- true
    child.StartInfo.RedirectStandardError <- true
    args |> List.iter child.StartInfo.ArgumentList.Add
    if not (child.Start()) then failwith "Git fixture process did not start."
    let output = child.StandardOutput.ReadToEnd()
    let errors = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then failwithf "Disposable Git failed: %s" errors
    output.Trim()

let success = function
    | Ok x -> x
    | Error issue -> failwithf "Canonical Git fixture failed: %A" issue

let create (root: string) =
    let bare = Path.Combine(root, "remote.git")
    let oneRoot = Path.Combine(root, "client-one")
    let twoRoot = Path.Combine(root, "client-two")
    Directory.CreateDirectory(oneRoot) |> ignore
    Directory.CreateDirectory(twoRoot) |> ignore
    git root [ "init"; "--bare"; bare ] |> ignore
    let onePath = Path.Combine(oneRoot, "Fixture")
    let twoPath = Path.Combine(twoRoot, "Fixture")
    git root [ "clone"; bare; onePath ] |> ignore
    git onePath [ "config"; "user.name"; "Hosted Test" ] |> ignore
    git onePath [ "config"; "user.email"; "hosted@example.test" ] |> ignore
    File.WriteAllText(Path.Combine(onePath, "source.txt"), "Test-only canonical input")
    git onePath [ "add"; "source.txt" ] |> ignore
    git onePath [ "commit"; "-m"; "disposable" ] |> ignore
    git onePath [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
    git root [ "clone"; bare; twoPath ] |> ignore
    git twoPath [ "config"; "user.name"; "Hosted Test" ] |> ignore
    git twoPath [ "config"; "user.email"; "hosted@example.test" ] |> ignore

    let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
    let binding path : LedgerBinding =
        { RepositoryPath = path
          RemoteName = "origin"
          ApprovedFetchUrl = bare
          ApprovedPushUrl = bare
          ProjectId = projectId
          LedgerId = ledgerId
          Epoch = 1 }
    let first, second = binding onePath, binding twoPath
    LedgerStore.initialize first true |> success |> ignore
    ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Worker" |> success |> ignore
    let taskId = "health-hosted-cas"
    ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId
        "Original health task" "<h1>Source preserved</h1>" |> success |> ignore
    let attemptId = Guid.NewGuid()
    ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Worker" |> success |> ignore
    ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Worker" 0 0 0 |> success |> ignore
    let originalHead = git onePath [ "rev-parse"; "HEAD" ]
    let sourceRef = "refs/heads/" + git onePath [ "branch"; "--show-current" ]
    let _, _, attempt = ReviewWorkflow.getTask first taskId |> success
    ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Worker"
        attempt.Value.Version attempt.Value.AssignmentGeneration originalHead sourceRef |> success |> ignore
    let snapshot = LedgerStore.fetch first |> success
    let agent = ReviewWorkflow.getAgentWorkingTurnAt first snapshot "Worker" |> success
    let task, attemptValue = ReviewWorkflow.getTaskAt first snapshot taskId |> success
    let active = attemptValue.Value
    let candidate : Candidate =
        { ProjectName = "Fixture"
          ProjectId = projectId
          LedgerId = ledgerId
          Epoch = 1
          ApprovedFetchUrl = bare
          ApprovedPushUrl = bare
          GitHead = snapshot.Commit
          AgentId = "Worker"
          AgentVersion = agent.Version
          TaskId = taskId
          TaskVersion = task.Version
          AttemptId = active.AttemptId
          AttemptVersion = active.Version
          Generation = active.AssignmentGeneration
          SpecBlobOid = active.SpecBlobOid
          SourceRef = active.SourceRef }
    let registerTrust (binding: LedgerBinding) =
        let path = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot binding.RepositoryPath
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let trust : Client.OrchestrationApi.LedgerTrustBinding =
            { SchemaVersion = 1
              Enabled = true
              ProjectRoot = binding.RepositoryPath
              ProjectId = projectId.ToString("D")
              LedgerId = ledgerId.ToString("D")
              Epoch = 1
              RemoteName = "origin"
              ApprovedFetchUrl = bare
              ApprovedPushUrl = bare
              HighestTrustedHead = snapshot.Commit }
        File.WriteAllText(path, JsonSerializer.Serialize(trust))
    registerTrust first
    registerTrust second
    first, second, Runtime(oneRoot), Runtime(twoRoot), candidate
