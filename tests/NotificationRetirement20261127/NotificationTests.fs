module NotificationRetirement.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Client.LedgerStore
open Client.ReviewWorkflow
open NUnit.Framework

// This standalone project deliberately is not registered in the normal test suite.
// The only repositories it writes are disposable local fixtures.
let private ok = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected ledger error: %A" error

let private git (directory: string) (args: string list) =
    use proc = new Process()
    proc.StartInfo.FileName <- "git"
    proc.StartInfo.WorkingDirectory <- directory
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    args |> List.iter proc.StartInfo.ArgumentList.Add
    if not (proc.Start()) then failwith "Could not start Git fixture."
    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwithf "Git fixture failure: %s" stderr.Result
    stdout.Result.Trim()

let private createProject root name =
    let bare = Path.Combine(root, name + ".git")
    let one = Path.Combine(root, name + "-first")
    let two = Path.Combine(root, name + "-second")
    git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
    git root [ "clone"; bare; one ] |> ignore
    let configure clone =
        git clone [ "config"; "user.name"; "Notification Fixture" ] |> ignore
        git clone [ "config"; "user.email"; "fixture@example.test" ] |> ignore
    configure one
    File.WriteAllText(Path.Combine(one, "source.txt"), "synthetic base")
    git one [ "add"; "source.txt" ] |> ignore
    git one [ "commit"; "-m"; "Fixture base" ] |> ignore
    git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
    git root [ "clone"; bare; two ] |> ignore
    configure two
    let projectId = Guid.NewGuid()
    let ledgerId = Guid.NewGuid()
    let binding path : LedgerBinding =
        { RepositoryPath = path; RemoteName = "origin"
          ApprovedFetchUrl = bare; ApprovedPushUrl = bare
          ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
    let first, second = binding one, binding two
    LedgerStore.initialize first true |> ok |> ignore
    for id in [ "FixtureSender"; "FixtureRecipient"; "FixtureOther" ] do
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) id |> ok |> ignore
    first, second

let private withProjects action =
    if not (OperatingSystem.IsLinux()) then
        Assert.Ignore("The fixture requires a disposable Linux XDG configuration root.")
    let root = Path.Combine(Path.GetTempPath(), "jarvis-notification-retirement-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let isolatedConfig = Path.Combine(root, "config")
    Directory.CreateDirectory(isolatedConfig) |> ignore
    let env =
        [ "XDG_CONFIG_HOME", isolatedConfig
          "GIT_CONFIG_GLOBAL", Path.Combine(root, "empty-git-config")
          "GIT_CONFIG_NOSYSTEM", "1" ]
    File.WriteAllText(Path.Combine(root, "empty-git-config"), "")
    let previous = env |> List.map (fun (key, _) -> key, Environment.GetEnvironmentVariable(key))
    try
        for key, value in env do Environment.SetEnvironmentVariable(key, value)
        Assert.That(Client.ClientConfig.configPath(), Is.EqualTo(Path.Combine(isolatedConfig, "Jarvis", "config.json")))
        let first, second = createProject root "destination"
        let foreign, _ = createProject root "foreign"
        action first second foreign
    finally
        for key, value in previous do Environment.SetEnvironmentVariable(key, value)
        Directory.Delete(root, true)

let private snapshot binding = LedgerStore.fetch binding |> ok
let private pending binding = (ReviewWorkflow.getAgentInbox binding "FixtureRecipient" |> ok).PendingNotifications
let private noticePath (id: Guid) = sprintf "notifications/%s.json" (id.ToString("N"))
let private readNotice binding id =
    let content, _ = LedgerStore.readAt binding (snapshot binding) (noticePath id) |> ok
    content.Value
let private send binding id source message =
    ReviewWorkflow.notifyAgent binding id "FixtureSender" source "FixtureRecipient" "" message
let private delivery binding result =
    result |> ok |> ReviewWorkflow.notificationDelivery binding |> ok

let private assertUnchanged binding (before: string) =
    Assert.That((snapshot binding).Commit, Is.EqualTo(before))

[<TestFixture; NonParallelizable>]
type LegacyControls() =
    [<Test>]
    member _.DeliveredRetryAndRecipientAckPreserveHistory() =
        withProjects (fun first second foreign ->
            let id = Guid.NewGuid()
            let message = "Synthetic historical message"
            let write = send first id "" message |> ok
            Assert.That(write.AlreadyApplied, Is.False)
            let originalDelivery = ReviewWorkflow.notificationDelivery second write |> ok
            Assert.That(originalDelivery.Status, Is.EqualTo("Delivered"))
            Assert.That(originalDelivery.NotificationId, Is.EqualTo(id.ToString("N")))
            let before = (snapshot second).Commit
            let original = readNotice second id
            let retry = send second id "" message |> ok
            Assert.That(retry.AlreadyApplied, Is.True)
            Assert.That(ReviewWorkflow.notificationDelivery first retry |> ok, Is.EqualTo(originalDelivery))
            assertUnchanged second before
            Assert.That(pending second |> List.length, Is.EqualTo(1))
            Assert.That(readNotice first id, Is.EqualTo(original))
            match send first id "" "Changed input" with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Changed-input retry was not rejected: %A" other)
            for actor in [ "FixtureSender"; "FixtureOther"; "AuthenticatedUser" ] do
                match ReviewWorkflow.acknowledgeNotification first (Guid.NewGuid()) actor id with
                | Error(ReviewAccessDenied _) -> ()
                | other -> Assert.Fail(sprintf "Nonrecipient %s ACK was not denied: %A" actor other)
            let foreignBefore = (snapshot foreign).Commit
            match ReviewWorkflow.acknowledgeNotification foreign (Guid.NewGuid()) "FixtureRecipient" id with
            | Error _ -> ()
            | other -> Assert.Fail(sprintf "Foreign ledger ACK unexpectedly succeeded: %A" other)
            assertUnchanged foreign foreignBefore
            assertUnchanged first before
            let ackId = Guid.NewGuid()
            let ack binding = ReviewWorkflow.acknowledgeNotification binding ackId "FixtureRecipient" id |> ok
            Assert.That((ack second).AlreadyApplied, Is.False)
            let acknowledgedHead = (snapshot first).Commit
            Assert.That((ack first).AlreadyApplied, Is.True)
            assertUnchanged first acknowledgedHead
            Assert.That(pending first, Is.Empty)
            match ReviewWorkflow.acknowledgeNotification second (Guid.NewGuid()) "FixtureRecipient" id with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Fresh ACK on historical record was not stale: %A" other)
            use oldRecord = JsonDocument.Parse(original)
            use newRecord = JsonDocument.Parse(readNotice first id)
            let old = oldRecord.RootElement
            let updated = newRecord.RootElement
            Assert.That(updated.GetProperty("acknowledgedAtUtc").GetString(), Is.Not.Empty)
            for property in old.EnumerateObject() do
                if property.Name <> "acknowledgedAtUtc" then
                    Assert.That(updated.GetProperty(property.Name).GetRawText(), Is.EqualTo(property.Value.GetRawText()), property.Name)
            Assert.That(updated.EnumerateObject() |> Seq.length, Is.EqualTo(old.EnumerateObject() |> Seq.length))
            Assert.That(send first id "" message |> delivery second, Is.EqualTo(originalDelivery))
            assertUnchanged first acknowledgedHead)

    [<Test>]
    member _.BlockedRetrySurvivesPreferenceChangesAndStopRemainsIndependent() =
        withProjects (fun first second _ ->
            let setMute muted reply =
                ReviewWorkflow.setNotificationMute first (Guid.NewGuid()) "FixtureRecipient" "FixtureRecipient" muted reply |> ok |> ignore
            setMute true "Original synthetic decision"
            let id = Guid.NewGuid()
            let message = "Synthetic suppressed message"
            let original = send first id "" message |> delivery second
            Assert.That(original.Status, Is.EqualTo("Blocked"))
            Assert.That(original.NotificationId, Is.Null)
            Assert.That(original.AutoReply, Is.EqualTo("Original synthetic decision"))
            let before = (snapshot second).Commit
            let retry = send second id "" message |> ok
            Assert.That(retry.AlreadyApplied, Is.True)
            Assert.That(ReviewWorkflow.notificationDelivery first retry |> ok, Is.EqualTo(original))
            assertUnchanged first before
            Assert.That(pending second, Is.Empty)
            setMute true "Changed synthetic decision"
            Assert.That(send second id "" message |> delivery first, Is.EqualTo(original))
            let alertId = Guid.NewGuid()
            let queue () =
                ReviewWorkflow.queueResponseAlert first alertId "AuthenticatedUser" "FixtureRecipient" "STOP — synthetic fixture" |> ok
            Assert.That((queue ()).AlreadyApplied, Is.False)
            Assert.That((queue ()).AlreadyApplied, Is.True)
            let alerts = ReviewWorkflow.getResponseAlerts second "FixtureRecipient" |> ok
            Assert.That(alerts.Pending |> List.length, Is.EqualTo(1))
            Assert.That(alerts.Pending.Head.AlertId, Is.EqualTo(alertId.ToString("N")))
            Assert.That(alerts.Pending.Head.Message, Does.StartWith("STOP"))
            Assert.That((ReviewWorkflow.getResponseAlerts first "FixtureSender" |> ok).Pending, Is.Empty)
            Assert.That(ReviewWorkflow.takeResponseAlerts second "FixtureRecipient" |> ok, Is.EqualTo(box alerts.Pending))
            Assert.That(ReviewWorkflow.takeResponseAlerts first "FixtureRecipient" |> ok, Is.Empty)
            setMute false ""
            let after = (snapshot second).Commit
            Assert.That(send first id "" message |> delivery second, Is.EqualTo(original))
            assertUnchanged second after
            Assert.That(pending second, Is.Empty))

    [<Test>]
    member _.CrossProjectSourceIdentityAndRecipientAuthority() =
        withProjects (fun first second foreign ->
            // The real server verifies this source sender before forwarding.
            // Here source membership is verified against the separate fixture ledger;
            // this invokes the destination workflow, not the registered server ingress.
            Assert.That((ReviewWorkflow.getAgentInbox foreign "FixtureSender" |> ok).Agent.AgentId, Is.EqualTo("FixtureSender"))
            let id = Guid.NewGuid()
            let original = send first id "foreign" "Synthetic foreign message" |> delivery second
            Assert.That(original.Status, Is.EqualTo("Delivered"))
            let head = (snapshot first).Commit
            Assert.That(send second id "foreign" "Synthetic foreign message" |> delivery first, Is.EqualTo(original))
            assertUnchanged second head
            let received = pending second
            Assert.That(received |> List.length, Is.EqualTo(1))
            Assert.That(received.Head.FromProjectName, Is.EqualTo("foreign"))
            Assert.That(received.Head.FromAgentId, Is.EqualTo("FixtureSender"))
            Assert.That(received.Head.ToAgentId, Is.EqualTo("FixtureRecipient"))
            match ReviewWorkflow.acknowledgeNotification first (Guid.NewGuid()) "FixtureSender" id with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Cross-project sender ACK unexpectedly succeeded: %A" other)
            assertUnchanged first head
            ReviewWorkflow.acknowledgeNotification second (Guid.NewGuid()) "FixtureRecipient" id |> ok |> ignore
            Assert.That(pending first, Is.Empty)
            Assert.That(send first id "foreign" "Synthetic foreign message" |> delivery second, Is.EqualTo(original)))

// These are intentionally RED on the legacy base. Run separately from LegacyControls.
// On retirement, an unseen operation must fail before writing either a notice or a suppression.
[<TestFixture; NonParallelizable; Category("RetirementContract")>]
type RetirementContract() =
    [<TestCase("", false)>]
    [<TestCase("", true)>]
    [<TestCase("foreign", false)>]
    [<TestCase("foreign", true)>]
    member _.FreshSendMustRejectWithoutLedgerMutation(source: string, muted: bool) =
        withProjects (fun first second foreign ->
            if source <> "" then
                Assert.That((ReviewWorkflow.getAgentInbox foreign "FixtureSender" |> ok).Agent.AgentId, Is.EqualTo("FixtureSender"))
            if muted then
                ReviewWorkflow.setNotificationMute first (Guid.NewGuid()) "FixtureRecipient" "FixtureRecipient" true "Synthetic suppression" |> ok |> ignore
            let before = (snapshot second).Commit
            let id = Guid.NewGuid()
            let result = send first id source "Synthetic fresh retirement probe"
            let after = (snapshot second).Commit
            let count = pending second |> List.length
            match result with
            | Error _ ->
                Assert.That(after, Is.EqualTo(before), "Rejected fresh send changed ledger")
                Assert.That(count, Is.Zero, "Rejected fresh send changed pending view")
            | Ok write ->
                let actual = ReviewWorkflow.notificationDelivery second write |> ok
                Assert.Fail(sprintf "INTENTIONAL RED: legacy fresh send returned %s; ledgerChanged=%b; pending=%d" actual.Status (before <> after) count))

// Bind the fixture checkouts in the already-verified disposable XDG root.
// This is the actual client ingress, without a shared registry or a network server.
type private FixtureContext(root: string) =
    interface Client.ProjectIO with
        member _.Project = { Root = Client.ProjectDirectory root; SpecialFiles = []; FolderFilters = [] }
    interface Client.FileIO with
        member _.File = Client.IO.FileOperations.impl
        member _.EditText(path, options, edit) =
            Client.IO.AtomicTextFiles.edit path options edit

let private withIngress (first: LedgerBinding) (foreign: LedgerBinding) action =
    let root = Path.GetDirectoryName(first.RepositoryPath)
    let context = FixtureContext(root)
    let bind (binding: LedgerBinding) =
        Client.OrchestrationApi.LedgerBindingConfig.bindExisting
            (Path.GetFileName(binding.RepositoryPath)) context binding.ProjectId binding.LedgerId binding.Epoch
            binding.RemoteName binding.ApprovedFetchUrl binding.ApprovedPushUrl (snapshot binding).Commit true
        |> ok |> ignore
    bind first
    bind foreign
    let logSettings : Client.ActivityLog.Settings =
        { DirectoryPath = Path.Combine(root, "activity"); RetentionDays = 1; MaxFileBytes = 1024L * 1024L }
    Directory.CreateDirectory(logSettings.DirectoryPath) |> ignore
    use log = new Client.ActivityLog.Store(logSettings)
    let rt = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(activityLog = log), Client.PartialTrust)
    let run actor (command: Common.AgentCommand) =
        Client.SignalR.Client.processIncomingCommand rt (JsonSerializer.Serialize command) actor
        |> Async.AwaitTask |> Async.RunSynchronously
    action run

let private command project action (operationId: Guid) actor owner attempt source message : Common.AgentCommand =
    Common.TaskWriteCommand
        { ProjectName = project; Action = action; OperationId = operationId.ToString("D"); TaskId = ""; AttemptId = attempt
          ActorId = actor; OwnerId = owner; Title = ""; Html = ""; ExpectedVersion = 0; ExpectedAgentVersion = 0
          ExpectedTaskVersion = 0; AssignmentGeneration = 0; Summary = message; NextAction = ""; Reason = ""
          BaseOid = ""; TargetRef = ""; SourceRef = source; AllowDetached = false; AllowUncommitted = false
          DecisionSequence = 0L; Evidence = ""; SessionId = "" }

[<TestFixture; NonParallelizable>]
type RegisteredIngressControls() =
    [<Test>]
    member _.ForeignRouteAndRecipientAckUseActualClientIngress() =
        withProjects (fun first second foreign ->
            withIngress first foreign (fun run ->
                let project = Path.GetFileName(first.RepositoryPath)
                let source = Path.GetFileName(foreign.RepositoryPath)
                let sender = "FixtureSender"
                let recipient = "FixtureRecipient"
                let inbox : Common.AgentCommand =
                    Common.TaskReadCommand
                        { ProjectName = source; LaunchId = null; AgentId = sender; Action = "AgentInbox"
                          TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0
                          BeforeOid = null; AfterOid = null; RelativePath = null }
                let _, verified, _ = run sender inbox
                Assert.That(verified.IsOk, Is.True, "Synthetic source registration must be valid")
                let id = Guid.NewGuid()
                let notify = command project "NotifyAgent" id sender recipient "" source "Synthetic registered foreign message"
                let _, delivered, response = run sender notify
                Assert.That(delivered.IsOk, Is.True, sprintf "%A" delivered)
                Assert.That(response.AgentStateChanges, Is.Empty)
                let notices = pending second
                Assert.That(notices |> List.length, Is.EqualTo(1))
                Assert.That(notices.Head.FromProjectName, Is.EqualTo(source))
                Assert.That(notices.Head.NotificationId, Is.EqualTo(id.ToString("N")))
                let head = (snapshot second).Commit
                let _, replay, _ = run sender notify
                Assert.That(replay.IsOk, Is.True)
                assertUnchanged second head
                let ack actor operation =
                    command project "AcknowledgeNotification" operation actor "" (id.ToString("D")) "" ""
                    |> run actor
                let _, denied, _ = ack sender (Guid.NewGuid())
                Assert.That(denied.IsError, Is.True)
                assertUnchanged first head
                let ackId = Guid.NewGuid()
                let _, accepted, _ = ack recipient ackId
                Assert.That(accepted.IsOk, Is.True, sprintf "%A" accepted)
                Assert.That(pending second, Is.Empty)
                let acknowledged = (snapshot second).Commit
                let _, again, _ = ack recipient ackId
                Assert.That(again.IsOk, Is.True)
                assertUnchanged second acknowledged))

    [<TestCase(false)>]
    [<TestCase(true)>]
    [<Category("RetirementContract")>]
    member _.RegisteredFreshSendMustReject(crossProject: bool) =
        withProjects (fun first second foreign ->
            withIngress first foreign (fun run ->
                let project = Path.GetFileName(first.RepositoryPath)
                let source = if crossProject then Path.GetFileName(foreign.RepositoryPath) else ""
                let sender = "FixtureSender"
                let recipient = "FixtureRecipient"
                let before = (snapshot second).Commit
                let _, result, _ =
                    command project "NotifyAgent" (Guid.NewGuid()) sender recipient "" source "Synthetic registered fresh probe"
                    |> run sender
                let after = (snapshot second).Commit
                match result with
                | Error _ ->
                    Assert.That(after, Is.EqualTo(before))
                    Assert.That(pending second, Is.Empty)
                | Ok _ ->
                    Assert.Fail(sprintf "INTENTIONAL RED: registered fresh send succeeded; ledgerChanged=%b; pending=%d" (after <> before) (pending second |> List.length))))
