module OrchestrationApiTests

open System
open System.IO
open Client
open Common
open NUnit.Framework

[<Test>]
let disabledReadAndWriteDoNotTouchV1Tasks () =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        let bindingPath = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot project
        Assert.That(File.Exists(bindingPath), Is.False)
        let query =
            { ProjectName = "Project1"
              Action = "ReviewerQueue"
              TaskId = ""
              RequestId = ""
              ReviewerId = "Reviewer"; DocumentPath = "" }
        match Client.OrchestrationApi.ReviewApi.read query context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain(" ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected disabled  read, got %A" other)
        let mutation =
            { ProjectName = "Project1"
              Action = "Claim"
              OperationId = Guid.NewGuid().ToString("D")
              TaskId = "315"
              RequestId = Guid.NewGuid().ToString("D")
              ActorId = "Reviewer"
              ReviewerId = ""
              ExpectedVersion = 1
              Kind = "Progress"
              SourceRef = ""
              HeadOid = ""
              TargetRef = ""
              Summary = ""
              Verdict = ""
              DocumentPath = ""; Content = ""; ExpectedBlobOid = "" }
        match Client.OrchestrationApi.ReviewApi.write mutation context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain(" ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected disabled  write, got %A" other)
        Assert.That(File.Exists(bindingPath), Is.False)
        Assert.That(Directory.Exists(Path.Combine(project, "docs", "tasks")), Is.False)
        Assert.That(GitCommandTests.gitOutput context [ "status"; "--porcelain" ], Is.Empty)
    finally
        Directory.Delete(root, true)

[<Test>]
let ledgerBindingsStayInUserConfigAndAreCheckoutSpecific () =
    let first = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot(
        Path.Combine(Path.GetTempPath(), "jarvis-test-" + Guid.NewGuid().ToString("N")))
    let second = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot(
        Path.Combine(Path.GetTempPath(), "jarvis-test-" + Guid.NewGuid().ToString("N")))
    Assert.That(first, Is.Not.EqualTo(second))
    Assert.That(Path.GetDirectoryName(first), Is.EqualTo(Path.Combine(
        Path.GetDirectoryName(ClientConfig.configPath()), "ledger-v2")))
    Assert.That(Path.GetFileName(first), Does.EndWith(".json"))

[<Test>]
let ledgerReviewMutationsNeedGitApprovalWhileReadsDoNot () =
    Client.PermissionPolicy.clearGrants ()
    let read = ReviewReadCommand
                    { ProjectName = "Project1"; Action = "ReviewerQueue"
                      TaskId = ""; RequestId = ""; ReviewerId = "Reviewer"; DocumentPath = "" }
    match Client.PermissionPolicy.evaluate read with
    | Ok () -> ()
    | other -> Assert.Fail(sprintf "Expected read-only  access, got %A" other)
    let write =
        ReviewWriteCommand
            { ProjectName = "Project1"
              Action = "Claim"
              OperationId = Guid.NewGuid().ToString("D")
              TaskId = "315"
              RequestId = Guid.NewGuid().ToString("D")
              ActorId = "Reviewer"
              ReviewerId = ""
              ExpectedVersion = 1
              Kind = "Progress"
              SourceRef = ""
              HeadOid = ""
              TargetRef = ""
              Summary = ""
              Verdict = ""
              DocumentPath = ""; Content = ""; ExpectedBlobOid = "" }
    match Client.PermissionPolicy.evaluate write with
    | Error(Client.ConfirmationRequired request) ->
        Assert.That(request.Permissions, Does.Contain(VersionControlWrite))
        Assert.That(request.Permissions, Does.Contain(NetworkAccess))
    | other -> Assert.Fail(sprintf "Expected  Git-write confirmation: %A" other)


[<Test>]
let ledgerTaskSetupAndInboxRefuseUnconfiguredCheckout () =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        let inbox : TaskReadCommand = { ProjectName = "Project1"; AgentId = "Worker"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null }
        match Client.OrchestrationApi.TaskApi.read inbox context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain(" ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected unconfigured  inbox to fail: %A" other)
        let mutation : TaskWriteCommand =
            { ProjectName = "Project1"
              Action = "CreateTask"
              OperationId = Guid.NewGuid().ToString("D")
              TaskId = "100"
              AttemptId = ""
              ActorId = "Orchestrator"
              OwnerId = ""
              Title = "Task"
              Html = "<h1>Task</h1>"
              ExpectedVersion = 0
              ExpectedAgentVersion = 0; ExpectedTaskVersion = 0
              AssignmentGeneration = 0
              Summary = ""
              NextAction = ""
              Reason = ""
              BaseOid = ""
              TargetRef = ""
              SourceRef = ""; AllowDetached = false; AllowUncommitted = false
              DecisionSequence = 0L; Evidence = ""; SessionId = "" }
        match Client.OrchestrationApi.TaskApi.write mutation context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain(" ledger disabled"))
        | other -> Assert.Fail(sprintf "Expected unconfigured  task write to fail: %A" other)
        Assert.That(File.Exists(Client.OrchestrationApi.LedgerBindingConfig.pathForRoot project), Is.False)
        Assert.That(GitCommandTests.gitOutput context [ "status"; "--porcelain" ], Is.Empty)
    finally Directory.Delete(root, true)

[<Test>]
let ledgerTaskMutationsRequireGitWritePermission () =
    Client.PermissionPolicy.clearGrants()
    let inbox = TaskReadCommand { ProjectName = "Project1"; AgentId = "Worker"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null }
    match Client.PermissionPolicy.evaluate inbox with
    | Ok () -> ()
    | other -> Assert.Fail(sprintf "Inbox should be read-only: %A" other)
    let write =
        TaskWriteCommand
            { ProjectName = "Project1"; Action = "CreateTask"
              OperationId = Guid.NewGuid().ToString("D"); TaskId = "100"
              AttemptId = ""; ActorId = "Orchestrator"; OwnerId = ""
              Title = "Task"; Html = "<h1>Task</h1>"; ExpectedVersion = 0
              ExpectedAgentVersion = 0; ExpectedTaskVersion = 0; AssignmentGeneration = 0;
              Summary = ""; NextAction = ""; Reason = ""; BaseOid = ""; TargetRef = ""
              SourceRef = ""; AllowDetached = false; AllowUncommitted = false
              DecisionSequence = 0L; Evidence = ""; SessionId = "" }
    match Client.PermissionPolicy.evaluate write with
    | Error(Client.ConfirmationRequired approval) ->
        Assert.That(approval.Permissions, Does.Contain(VersionControlWrite))
        Assert.That(approval.Permissions, Does.Contain(NetworkAccess))
    | other -> Assert.Fail(sprintf "Expected explicit Git-write confirmation: %A" other)



[<Test>]
let pendingRecoveryCommandsAreDistinctExplicitWriteActions () =
    Client.PermissionPolicy.clearGrants()
    let operationId = Guid.NewGuid().ToString("D")
    let retry : AgentCommand =
        RetryPendingIntentCommand
            { ProjectName = "Project1"; OperationId = operationId
              OperatorId = "Operator"; ExpectedCommandSha256 = String.replicate 64 "a"
              ExpectedVerifiedHead = String.replicate 40 "b" }
    let supersede : AgentCommand =
        SupersedePendingIntentCommand
            { ProjectName = "Project1"; OperationId = operationId
              OperatorId = "Operator"; ExpectedCommandSha256 = String.replicate 64 "a"
              ExpectedVerifiedHead = String.replicate 40 "b"
              RelatedOperationId = Guid.NewGuid().ToString("D")
              RelatedRequestHash = String.replicate 64 "c"
              Reason = "Equivalent canonical operation has been verified." }
    for command in [ retry; supersede ] do
        match Client.PermissionPolicy.evaluate command with
        | Error(Client.ConfirmationRequired approval) ->
            Assert.That(approval.Permissions, Does.Contain(VersionControlWrite))
            Assert.That(approval.Permissions, Does.Contain(NetworkAccess))
        | other -> Assert.Fail(sprintf "Recovery must require explicit local approval: %A" other)
        let json = System.Text.Json.JsonSerializer.Serialize(command)
        let roundtrip = System.Text.Json.JsonSerializer.Deserialize<AgentCommand>(json)
        Assert.That(roundtrip, Is.EqualTo(command))
        Assert.That(AgentCommandInfo.projectName command, Is.EqualTo(Some "Project1"))
    Assert.That(AgentCommandInfo.displayName retry, Is.EqualTo("RetryPendingIntent"))
    Assert.That(AgentCommandInfo.displayName supersede, Is.EqualTo("SupersedePendingIntent"))

[<Test>]
let ledgerOperationStatusUsesReadOnlyTypedLedgerCommand () =
    Client.PermissionPolicy.clearGrants ()
    let read =
        LedgerReadCommand
            { ProjectName = "Project1"
              Action = "GetOperationStatus"
              OperationId = Guid.NewGuid().ToString("D"); Cursor = ""; PageSize = 0 }
    match Client.PermissionPolicy.evaluate read with
    | Ok () -> ()
    | other -> Assert.Fail(sprintf "Status lookup must require no mutation approval: %A" other)
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext root
        GitCommandTests.initRepo context
        match Client.OrchestrationApi.LedgerApi.read
                { ProjectName = "Project1"; Action = "GetOperationStatus"
                  OperationId = Guid.NewGuid().ToString("D"); Cursor = ""; PageSize = 0 } context with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain(" ledger disabled"))
        | other -> Assert.Fail(sprintf "Unbound operation lookup must fail closed: %A" other)
        Assert.That(File.Exists(Client.OrchestrationApi.LedgerBindingConfig.pathForRoot project), Is.False)
    finally Directory.Delete(root, true)


[<Test>]
let ledgerFailuresRetainTypedCodesOverWire () =
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
              |> Client.OrchestrationApi.LedgerErrors.toEffect
              |> Client.Effect.EffectError.toAgentError with
        | Common.AgentError.LedgerFailure error ->
            Assert.That(error.Code, Is.EqualTo(expected))
            Assert.That(error.Message, Is.Not.Empty)
        | other ->
            Assert.Fail(sprintf "Expected typed  failure, got: %A" other)


[<Test>]
let historicalPendingJournalKindsRemainRecognizedAfterRenaming () =
    let normalize = Client.OrchestrationApi.PendingIntentJournal.normalizeCommandType
    Assert.That(normalize "V2TaskWrite", Is.EqualTo("TaskWrite"))
    Assert.That(normalize "V2ReviewWrite", Is.EqualTo("ReviewWrite"))
    Assert.That(normalize "V2LedgerWrite", Is.EqualTo("LedgerWrite"))
    Assert.That(normalize "TaskWrite", Is.EqualTo("TaskWrite"))
    Assert.That(normalize "Other", Is.EqualTo("Other"))

[<Test>]
let integrationSubmissionRoutesReviewIdAndTargetRefInCorrectOrder () =
    // Real server command encoding: Reason holds the optional review ID,
    // TargetRef holds the remote integration target branch.
    let cmd : TaskWriteCommand =
        { ProjectName = "Project1"; Action = "SubmitIntegration"
          OperationId = Guid.NewGuid().ToString("D"); TaskId = "boundary"
          AttemptId = Guid.NewGuid().ToString("D"); ActorId = "Owner"; OwnerId = ""
          Title = ""; Html = ""; ExpectedVersion = 0; ExpectedAgentVersion = 0
          ExpectedTaskVersion = 0; AssignmentGeneration = 0; Summary = ""
          NextAction = ""; Reason = Guid.NewGuid().ToString("N")
          BaseOid = String.replicate 40 "a"; TargetRef = "refs/heads/main"
          SourceRef = "refs/heads/feature/review"; AllowDetached = false
          AllowUncommitted = false; DecisionSequence = 0L; Evidence = ""
          SessionId = "" }
    let reviewId, targetRef = Client.OrchestrationApi.TaskApi.integrationSubmissionReferences cmd
    Assert.That(reviewId, Is.EqualTo(cmd.Reason))
    Assert.That(targetRef, Is.EqualTo(cmd.TargetRef))
    Assert.That(targetRef, Does.StartWith("refs/heads/"))
    Assert.That(reviewId, Is.Not.EqualTo(targetRef))
    let withoutReview = { cmd with Reason = "" }
    let noReviewId, sameTarget = Client.OrchestrationApi.TaskApi.integrationSubmissionReferences withoutReview
    Assert.That(noReviewId, Is.Empty)
    Assert.That(sameTarget, Is.EqualTo(cmd.TargetRef))
