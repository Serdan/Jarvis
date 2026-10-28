module V2ReviewApiTests

open System
open System.IO
open Client
open Common
open NUnit.Framework

[<Test>]
let disabledV2ReadAndWriteDoNotTouchV1Tasks () =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        let bindingPath = Client.V2ReviewApi.ReviewV2Binding.pathForRoot project
        Assert.That(File.Exists(bindingPath), Is.False)
        let query =
            { ProjectName = "Project1"
              Action = "ReviewerQueue"
              TaskId = ""
              RequestId = ""
              ReviewerId = "Reviewer" }
        match Client.V2ReviewApi.V2ReviewApi.read query context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("V2 ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected disabled V2 read, got %A" other)
        let mutation =
            { ProjectName = "Project1"
              Action = "Claim"
              OperationId = Guid.NewGuid().ToString("D")
              TaskId = "315"
              RequestId = Guid.NewGuid().ToString("D")
              ActorId = "Reviewer"
              ReviewerId = ""
              ExpectedVersion = 1
              Generation = 1
              SourceRef = ""
              HeadOid = ""
              Summary = ""
              Verdict = ""
              Feedback = "" }
        match Client.V2ReviewApi.V2ReviewApi.write mutation context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("V2 ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected disabled V2 write, got %A" other)
        Assert.That(File.Exists(bindingPath), Is.False)
        Assert.That(Directory.Exists(Path.Combine(project, "docs", "tasks")), Is.False)
        Assert.That(GitCommandTests.gitOutput context [ "status"; "--porcelain" ], Is.Empty)
    finally
        Directory.Delete(root, true)

[<Test>]
let v2BindingsStayInUserConfigAndAreCheckoutSpecific () =
    let first = Client.V2ReviewApi.ReviewV2Binding.pathForRoot(
        Path.Combine(Path.GetTempPath(), "jarvis-test-" + Guid.NewGuid().ToString("N")))
    let second = Client.V2ReviewApi.ReviewV2Binding.pathForRoot(
        Path.Combine(Path.GetTempPath(), "jarvis-test-" + Guid.NewGuid().ToString("N")))
    Assert.That(first, Is.Not.EqualTo(second))
    Assert.That(Path.GetDirectoryName(first), Is.EqualTo(Path.Combine(
        Path.GetDirectoryName(ClientConfig.configPath()), "ledger-v2")))
    Assert.That(Path.GetFileName(first), Does.EndWith(".json"))

[<Test>]
let v2ReviewMutationsNeedGitApprovalWhileReadsDoNot () =
    Client.PermissionPolicy.clearGrants ()
    let read = V2ReviewReadCommand
                    { ProjectName = "Project1"; Action = "ReviewerQueue"
                      TaskId = ""; RequestId = ""; ReviewerId = "Reviewer" }
    match Client.PermissionPolicy.evaluate read with
    | Ok () -> ()
    | other -> Assert.Fail(sprintf "Expected read-only V2 access, got %A" other)
    let write =
        V2ReviewWriteCommand
            { ProjectName = "Project1"
              Action = "Claim"
              OperationId = Guid.NewGuid().ToString("D")
              TaskId = "315"
              RequestId = Guid.NewGuid().ToString("D")
              ActorId = "Reviewer"
              ReviewerId = ""
              ExpectedVersion = 1
              Generation = 1
              SourceRef = ""
              HeadOid = ""
              Summary = ""
              Verdict = ""
              Feedback = "" }
    match Client.PermissionPolicy.evaluate write with
    | Error(Client.ConfirmationRequired request) ->
        Assert.That(request.Permissions, Does.Contain(VersionControlWrite))
        Assert.That(request.Permissions, Does.Contain(NetworkAccess))
    | other -> Assert.Fail(sprintf "Expected V2 Git-write confirmation: %A" other)
