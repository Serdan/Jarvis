module AutomationStageB2Disposable20261122.Tests

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open Client.LedgerStore
open Client.NativeProjectEffectFence
open NUnit.Framework

let ok = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected disposable Git failure: %A" error

let git root args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- root
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    p.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
    p.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"
    args |> List.iter p.StartInfo.ArgumentList.Add
    Assert.That(p.Start(), Is.True)
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    Assert.That(p.WaitForExit(30000), Is.True, "Git fixture timeout")
    Assert.That(p.ExitCode, Is.EqualTo(0), stderr)
    stdout.Trim()

let private json value =
    JsonSerializer.Serialize(value, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

let fixture phase action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-b2-disposable-w16-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let remote = Path.Combine(root, "project.git")
        let client = Path.Combine(root, "project-a")
        git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
        git root [ "clone"; remote; client ] |> ignore
        git client [ "config"; "user.name"; "Disposable Test" ] |> ignore
        git client [ "config"; "user.email"; "test@example.test" ] |> ignore
        File.WriteAllText(Path.Combine(client, "seed.txt"), "disposable source")
        git client [ "add"; "seed.txt" ] |> ignore
        git client [ "commit"; "-m"; "fixture initial source" ] |> ignore
        git client [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        let sourceOid = git client [ "rev-parse"; "HEAD" ]
        let binding : LedgerBinding =
            { RepositoryPath = client; RemoteName = "origin"
              ApprovedFetchUrl = remote; ApprovedPushUrl = remote
              ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
        LedgerStore.initialize binding true |> ok |> ignore
        let taskId = "stageb2-native-probe"
        let attemptId = Guid.NewGuid().ToString("N")
        let task : CanonicalTask =
            { TaskId = taskId; Lifecycle = "InProgress"; AttemptId = attemptId }
        let attempt : CanonicalAttempt =
            { TaskId = taskId; AttemptId = attemptId; OwnerAgentId = "Worker 16"
              Phase = phase; AssignmentGeneration = 1
              SourceRepositoryId = String.replicate 64 "a"
              SourceRef = "refs/heads/main"; BaseCommit = sourceOid
              TargetRef = "refs/heads/main" }
        LedgerStore.transact binding
            { OperationId = Guid.NewGuid(); Actor = "Disposable fixture issuer"
              Kind = "StageB2CanonicalTaskFixture"; RequestJson = json {| taskId = taskId; attemptId = attemptId |} }
            (fun _ ->
                Ok [ { Path = "tasks/" + taskId + "/task.json"; ExpectedBlobOid = None; Content = json task }
                     { Path = "tasks/" + taskId + "/attempts/" + attemptId + ".json"
                       ExpectedBlobOid = None; Content = json attempt } ])
        |> ok |> ignore
        let candidate : NativeCandidate =
            { Kind = "Worker"; TaskId = taskId; AttemptId = attemptId
              ReviewId = ""; OwnerAgentId = "Worker 16"; AssignmentGeneration = 1
              ReviewVersion = 0; SourceRepositoryId = attempt.SourceRepositoryId
              SourceRef = attempt.SourceRef; BaseCommit = attempt.BaseCommit
              TargetRef = attempt.TargetRef; ReviewRef = ""; ReviewedHeadOid = "" }
        action binding candidate sourceOid
    finally
        Directory.Delete(root, true)

[<TestFixture>]
type StageB2DisposableRedTests() =
    [<Test; Explicit("Intentional B2 cross-module RED: Started vs Ready")>]
    member _.StartedAccountCandidateMustBridgeToB1WithoutRewritingCanonicalLease() =
        fixture "Started" (fun binding candidate sourceOid ->
            let receipt : FakeAccountReservationReceipt =
                { Marker = "STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
                  AccountLedgerId = Guid.NewGuid().ToString("D")
                  AccountEpoch = 1; AccountHead = sourceOid
                  OriginalReservationOperationId = Guid.NewGuid().ToString("D")
                  ReservationId = Guid.NewGuid().ToString("D")
                  ProjectId = binding.ProjectId.ToString("D")
                  Kind = "Worker" }
            let claim : PreSendClaim =
                { OperationId = Guid.NewGuid()
                  ExpectedProjectHead = (LedgerStore.fetch binding |> ok).Commit
                  Account = receipt; Candidate = candidate
                  DispatchId = Guid.NewGuid(); EffectId = Guid.NewGuid()
                  DeviceId = "disposable-w16"; WindowRegistration = "fake-window" }
            let result = Client.NativeProjectEffectFence.claim binding claim
            Assert.That(Result.isOk result, Is.True,
                "Expected real Started account lease to bridge into accepted B1; any RED is a missing contract seam"))