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
              Feedback = ""; TestEvidence = ""; Inspected = false; Tested = false; TestDetails = "" }
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
              Feedback = ""; TestEvidence = ""; Inspected = false; Tested = false; TestDetails = "" }
    match Client.PermissionPolicy.evaluate write with
    | Error(Client.ConfirmationRequired request) ->
        Assert.That(request.Permissions, Does.Contain(VersionControlWrite))
        Assert.That(request.Permissions, Does.Contain(NetworkAccess))
    | other -> Assert.Fail(sprintf "Expected V2 Git-write confirmation: %A" other)


[<Test>]
let v2TaskSetupAndInboxRefuseUnconfiguredCheckout () =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        let inbox : V2TaskReadCommand = { ProjectName = "Project1"; AgentId = "Worker"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L }
        match Client.V2ReviewApi.V2TaskApi.read inbox context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("V2 ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected unconfigured V2 inbox to fail: %A" other)
        let mutation : V2TaskWriteCommand =
            { ProjectName = "Project1"
              Action = "CreateTask"
              OperationId = Guid.NewGuid().ToString("D")
              TaskId = "100"
              AttemptId = ""
              ActorId = "Orchestrator"
              OwnerId = ""
              ReviewerId = ""
              Title = "Task"
              Html = "<h1>Task</h1>"
              ExpectedVersion = 0
              ExpectedAgentVersion = 0
              AssignmentGeneration = 0
              Summary = ""
              NextAction = ""
              Reason = ""
              BaseOid = ""
              TargetRef = ""
              DecisionSequence = 0L; Evidence = "" }
        match Client.V2ReviewApi.V2TaskApi.write mutation context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("V2 ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected unconfigured V2 task write to fail: %A" other)
        Assert.That(File.Exists(Client.V2ReviewApi.ReviewV2Binding.pathForRoot project), Is.False)
        Assert.That(GitCommandTests.gitOutput context [ "status"; "--porcelain" ], Is.Empty)
    finally Directory.Delete(root, true)

[<Test>]
let v2TaskMutationsRequireGitWritePermission () =
    Client.PermissionPolicy.clearGrants()
    let inbox = V2TaskReadCommand { ProjectName = "Project1"; AgentId = "Worker"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L }
    match Client.PermissionPolicy.evaluate inbox with
    | Ok () -> ()
    | other -> Assert.Fail(sprintf "Inbox should be read-only: %A" other)
    let write =
        V2TaskWriteCommand
            { ProjectName = "Project1"; Action = "CreateTask"
              OperationId = Guid.NewGuid().ToString("D"); TaskId = "100"
              AttemptId = ""; ActorId = "Orchestrator"; OwnerId = ""; ReviewerId = ""
              Title = "Task"; Html = "<h1>Task</h1>"; ExpectedVersion = 0
              ExpectedAgentVersion = 0; AssignmentGeneration = 0;
              Summary = ""; NextAction = ""; Reason = ""; BaseOid = ""; TargetRef = ""
              DecisionSequence = 0L; Evidence = "" }
    match Client.PermissionPolicy.evaluate write with
    | Error(Client.ConfirmationRequired approval) ->
        Assert.That(approval.Permissions, Does.Contain(VersionControlWrite))
        Assert.That(approval.Permissions, Does.Contain(NetworkAccess))
    | other -> Assert.Fail(sprintf "Expected explicit Git-write confirmation: %A" other)


[<Test>]
let v2IntegrationReadWriteRequireExplicitLedgerBinding () =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        let read : V2IntegrationReadCommand =
            { ProjectName = "Project1"; TaskId = "integration"
              ActionId = Guid.NewGuid().ToString("D") }
        match Client.V2ReviewApi.V2IntegrationApi.read read context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("V2 ledger disabled"))
        | other -> Assert.Fail(sprintf "Integration inspection must fail closed: %A" other)
        let write : V2IntegrationWriteCommand =
            { ProjectName = "Project1"; Action = "RecordIntegration"
              OperationId = Guid.NewGuid().ToString("D"); TaskId = "integration"
              ActionId = Guid.NewGuid().ToString("D"); RequestId = ""
              ExpectedVersion = 1; TargetRef = ""; ExpectedTargetHead = "" }
        match Client.V2ReviewApi.V2IntegrationApi.write write context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("V2 ledger disabled"))
        | other -> Assert.Fail(sprintf "Integration recording must fail closed: %A" other)
        Assert.That(File.Exists(Client.V2ReviewApi.ReviewV2Binding.pathForRoot project), Is.False)
        Assert.That(GitCommandTests.gitOutput context [ "status"; "--porcelain" ], Is.Empty)
    finally Directory.Delete(root, true)

[<Test>]
let v2IntegrationMutationsNeedExplicitGitWritePermission () =
    Client.PermissionPolicy.clearGrants ()
    let read =
        V2IntegrationReadCommand
            { ProjectName = "Project1"; TaskId = "reviewed"
              ActionId = Guid.NewGuid().ToString("D") }
    match Client.PermissionPolicy.evaluate read with
    | Ok () -> ()
    | other -> Assert.Fail(sprintf "Integration inspection should be read-only: %A" other)
    let write =
        V2IntegrationWriteCommand
            { ProjectName = "Project1"; Action = "PlanIntegration"
              OperationId = Guid.NewGuid().ToString("D"); TaskId = "reviewed"
              ActionId = Guid.NewGuid().ToString("D")
              RequestId = Guid.NewGuid().ToString("D")
              ExpectedVersion = 1; TargetRef = "refs/heads/main"
              ExpectedTargetHead = String.replicate 40 "a" }
    match Client.PermissionPolicy.evaluate write with
    | Error(Client.ConfirmationRequired approval) ->
        Assert.That(approval.Permissions, Does.Contain(VersionControlWrite))
        Assert.That(approval.Permissions, Does.Contain(NetworkAccess))
    | other -> Assert.Fail(sprintf "Integration source operations require explicit approval: %A" other)


[<Test>]
let v2OperationStatusUsesReadOnlyTypedLedgerCommand () =
    Client.PermissionPolicy.clearGrants ()
    let read =
        V2LedgerReadCommand
            { ProjectName = "Project1"
              Action = "GetOperationStatus"
              OperationId = Guid.NewGuid().ToString("D") }
    match Client.PermissionPolicy.evaluate read with
    | Ok () -> ()
    | other -> Assert.Fail(sprintf "Status lookup must require no mutation approval: %A" other)
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        match Client.V2ReviewApi.V2LedgerApi.read
                { ProjectName = "Project1"; Action = "GetOperationStatus"
                  OperationId = Guid.NewGuid().ToString("D") } context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("V2 ledger disabled"))
        | other -> Assert.Fail(sprintf "Unbound operation lookup must fail closed: %A" other)
        Assert.That(File.Exists(Client.V2ReviewApi.ReviewV2Binding.pathForRoot project), Is.False)
    finally Directory.Delete(root, true)


[<Test>]
let v2LedgerFailuresRetainTypedCodesOverWire () =
    let cases : (Client.LedgerStore.LedgerFailure * string) list =
        [ Client.LedgerStore.LedgerContended, "LedgerContended"
          Client.LedgerStore.OperationIdReused, "OperationIdReused"
          Client.LedgerStore.OutcomeUnknown "operation 123", "OutcomeUnknown"
          Client.LedgerStore.RemoteUnavailable "offline", "RemoteUnavailable"
          Client.LedgerStore.StaleEntityVersion "changed", "StaleEntityVersion"
          Client.LedgerStore.LedgerIntegrityError "rewritten", "LedgerIntegrityError"
          Client.LedgerStore.ReviewAlreadyClaimed "frozen", "ReviewAlreadyClaimed" ]
    for failure, expected in cases do
        match failure
              |> Client.V2ReviewApi.V2LedgerErrors.toEffect
              |> Client.Effect.EffectError.toAgentError with
        | Common.AgentError.V2LedgerFailure error ->
            Assert.That(error.Code, Is.EqualTo(expected))
            Assert.That(error.Message, Is.Not.Empty)
        | other ->
            Assert.Fail(sprintf "Expected typed V2 failure, got: %A" other)
