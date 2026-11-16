module Client.Tests.WorkQueueOperatorConversationsTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Common
open Client.OrchestrationApi
open NUnit.Framework
open Client.LedgerStore
open Client.WorkQueueOperatorConversations

[<TestFixture>]
type WorkQueueOperatorConversationsTests() =
    let success = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected operator conversation error: %A" error

    let git dir args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- dir
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        for arg in args do runner.StartInfo.ArgumentList.Add arg
        Assert.That(runner.Start(), Is.True)
        let output = runner.StandardOutput.ReadToEnd()
        let error = runner.StandardError.ReadToEnd()
        runner.WaitForExit()
        Assert.That(runner.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let withClones work =
        let dir = Path.Combine(Path.GetTempPath(), "jarvis-operator-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        try
            let bare = Path.Combine(dir, "remote.git")
            let a = Path.Combine(dir, "a")
            let b = Path.Combine(dir, "b")
            git dir [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
            git dir [ "clone"; bare; a ] |> ignore
            File.WriteAllText(Path.Combine(a, "start.txt"), "disposable")
            git a [ "add"; "start.txt" ] |> ignore
            git a [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
                    "commit"; "-m"; "fixture" ] |> ignore
            git a [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git dir [ "clone"; bare; b ] |> ignore
            let project = Guid.NewGuid()
            let ledger = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = project; LedgerId = ledger; Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> success |> ignore
            LedgerStore.fetch second |> success |> ignore
            work first second
        finally
            try Directory.Delete(dir, true) with _ -> ()

    let id () = Guid.NewGuid()
    let read binding kind subject =
        WorkQueueOperatorConversations.read binding kind subject |> success
    let entry binding kind subject messageId =
        (read binding kind subject).Thread.Messages |> Array.find (fun m -> m.Id = messageId)

    [<Test>]
    member _.TaskAndRoleThreadsRemainVisibleWithoutAnActiveHandlerOrScheduler() =
        withClones (fun a b ->
            let taskId = id().ToString("D")
            let roleId = id().ToString("D")
            let taskBefore = read a "Task" "my-task"
            Assert.That(taskBefore.Thread.Version, Is.EqualTo(0))
            Assert.That(taskBefore.Thread.Messages.Length, Is.EqualTo(0))
            let first =
                WorkQueueOperatorConversations.post a "Operator" (id()) 0
                    "Task" "my-task" taskId "Please check this issue." |> success
            let role =
                WorkQueueOperatorConversations.post b "Operator" (id()) 0
                    "Role" "Lead" roleId "Please triage." |> success
            let taskThread = read b "Task" "my-task"
            let roleThread = read a "Role" "Lead"
            Assert.That(first.ObservedGitHead, Is.Not.Empty)
            Assert.That(role.ObservedGitHead, Is.Not.Empty)
            Assert.That(taskThread.Thread.Messages.[0].Status, Is.EqualTo("Pending"))
            Assert.That(roleThread.Thread.Messages.[0].Status, Is.EqualTo("Pending"))
            Assert.That(taskThread.Thread.Version, Is.EqualTo(1))
            Assert.That(roleThread.Thread.Version, Is.EqualTo(1))
            Assert.That(taskThread.Thread.Audit.[0].PriorGitHead, Is.EqualTo(taskBefore.GitHead))
            Assert.That(taskThread.Thread.Messages.[0].Id, Is.EqualTo(taskId))
            Assert.That(roleThread.Thread.SubjectKind, Is.EqualTo("Role"))
            Assert.That((read a "Role" "Lead").Thread.Messages.[0].Status, Is.EqualTo("Pending")))

    [<Test>]
    member _.TwoClonesRaceForClaimAndGenerationFencesStaleReplies() =
        withClones (fun a b ->
            let messageId = id().ToString("D")
            WorkQueueOperatorConversations.post a "Operator" (id()) 0
                "Role" "Lead" messageId "Need a decision." |> success |> ignore
            let version = (read a "Role" "Lead").Thread.Version
            WorkQueueOperatorConversations.claim b "WorkerA" (id()) version
                "Role" "Lead" messageId |> success |> ignore
            match WorkQueueOperatorConversations.claim a "WorkerB" (id()) version
                    "Role" "Lead" messageId with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Claim race did not expose CAS conflict: %A" other)
            Assert.That((entry a "Role" "Lead" messageId).Handler, Is.EqualTo("WorkerA"))
            let beforeRelease = read b "Role" "Lead"
            WorkQueueOperatorConversations.release a "Operator" (id()) beforeRelease.Thread.Version
                "Role" "Lead" messageId 1 "Original handler stopped." |> success |> ignore
            let pending = entry b "Role" "Lead" messageId
            Assert.That(pending.Status, Is.EqualTo("Pending"))
            Assert.That(pending.HandlerGeneration, Is.EqualTo(1))
            WorkQueueOperatorConversations.claim b "WorkerB" (id()) (read b "Role" "Lead").Thread.Version
                "Role" "Lead" messageId |> success |> ignore
            let newLease = entry a "Role" "Lead" messageId
            Assert.That(newLease.HandlerGeneration, Is.EqualTo(2))
            match WorkQueueOperatorConversations.handle b "WorkerA" (id())
                    (read b "Role" "Lead").Thread.Version "Role" "Lead" messageId
                    1 (id().ToString("D")) "Stale answer" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old generation answered: %A" other)
            let replyId = id().ToString("D")
            WorkQueueOperatorConversations.handle a "WorkerB" (id())
                (read a "Role" "Lead").Thread.Version "Role" "Lead" messageId 2
                replyId "Approved with a documented exception."
            |> success |> ignore
            let final = read b "Role" "Lead"
            Assert.That(final.Thread.Messages.[0].Status, Is.EqualTo("Handled"))
            Assert.That(final.Thread.Messages.[0].ReplyId, Is.EqualTo(replyId))
            Assert.That(final.Thread.Messages.[0].ReplyBody,
                        Is.EqualTo("Approved with a documented exception."))
            Assert.That(final.Thread.Audit.[2].Reason, Is.EqualTo("Original handler stopped."))
            Assert.That(final.Thread.Audit.[2].Handler, Is.EqualTo("WorkerA"))
            match WorkQueueOperatorConversations.handle b "WorkerB" (id())
                    final.Thread.Version "Role" "Lead" messageId 2 replyId "Different answer" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Distinct reply overwrote Handled answer: %A" other))

    [<Test>]
    member _.LostAcknowledgementCanBeInspectedAndReplayedByExactOperationUuid() =
        withClones (fun a b ->
            let messageId = id().ToString("D")
            let postOp = id()
            let created =
                WorkQueueOperatorConversations.post a "Operator" postOp 0
                    "Task" "task-9" messageId "A durable question." |> success
            // The caller discards the first success to model a lost acknowledgement.
            let receipt =
                WorkQueueOperatorConversations.inspectOperation b postOp |> success
            Assert.That(receipt.Operation.IsSome, Is.True)
            Assert.That(receipt.Operation.Value.Type, Is.EqualTo("WQ06.Operator.Post"))
            Assert.That(receipt.Operation.Value.Sequence, Is.EqualTo(created.LedgerSequence))
            let replay =
                WorkQueueOperatorConversations.post b "Operator" postOp 0
                    "Task" "task-9" messageId "A durable question." |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That((read a "Task" "task-9").Thread.Messages.Length, Is.EqualTo(1))
            match WorkQueueOperatorConversations.post b "Operator" postOp 0
                    "Task" "task-9" messageId "Changed content" with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Operation UUID reused with new payload: %A" other)
            let unknownOp = id()
            let notFound = WorkQueueOperatorConversations.inspectOperation b unknownOp |> success
            Assert.That(notFound.Operation.IsNone, Is.True)
            Assert.That(notFound.VerifiedGitHead, Is.Not.Empty)
            // NotFoundAtVerifiedHead does not imply a timed-out push cannot arrive later.
            Assert.That((entry b "Task" "task-9" messageId).Body,
                        Is.EqualTo("A durable question.")))

    [<Test>]
    member _.HandledAckReplayAndIndependentSubjectsKeepOriginalReply() =
        withClones (fun a b ->
            let messageId = id().ToString("D")
            WorkQueueOperatorConversations.post a "Operator" (id()) 0
                "Task" "abc" messageId "Ping" |> success |> ignore
            WorkQueueOperatorConversations.claim b "Handler" (id()) 1
                "Task" "abc" messageId |> success |> ignore
            let handleOp = id()
            let replyId = id().ToString("D")
            let ack =
                WorkQueueOperatorConversations.handle a "Handler" handleOp 2
                    "Task" "abc" messageId 1 replyId "Pong" |> success
            let replay =
                WorkQueueOperatorConversations.handle b "Handler" handleOp 2
                    "Task" "abc" messageId 1 replyId "Pong" |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.LedgerSequence, Is.EqualTo(ack.LedgerSequence))
            let final = read b "Task" "abc"
            Assert.That(final.Thread.Version, Is.EqualTo(3))
            Assert.That(final.Thread.Messages.[0].ReplyBody, Is.EqualTo("Pong"))
            Assert.That(final.Thread.Audit.[2].PriorGitHead, Is.Not.Empty)
            Assert.That((read a "Task" "other").Thread.Version, Is.EqualTo(0))
            Assert.That((read a "Role" "abc").Thread.Version, Is.EqualTo(0)))

    [<Test>]
    member _.DirectRepairKeepsOriginalGitBackupAndAuditWithoutSchemaMigration() =
        withClones (fun a b ->
            let messageId = id().ToString("D")
            WorkQueueOperatorConversations.post a "Operator" (id()) 0
                "Role" "Lead" messageId "Original question" |> success |> ignore
            let head, originalBlob, originalRaw =
                WorkQueueOperatorConversations.inspectRaw b "Role" "Lead" |> success
            let oid = originalBlob.Value
            let original = originalRaw.Value
            // A privileged operator can repair a malformed record without
            // changing the application. The repair note points to the backup.
            let corrupt =
                """{"SchemaVersion":1,"ProjectId":"anomaly","SubjectKind":"Role"}"""
            let firstRepair = id()
            WorkQueueOperatorConversations.repair a "Operator" firstRepair
                "Role" "Lead" oid "Restore from a malformed legacy merge" corrupt
            |> success |> ignore
            match WorkQueueOperatorConversations.read b "Role" "Lead" with
            | Error(LedgerIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Malformed direct repair went unnoticed: %A" other)
            let _, badBlob, raw = WorkQueueOperatorConversations.inspectRaw b "Role" "Lead" |> success
            Assert.That(raw.Value, Is.EqualTo(corrupt))
            match WorkQueueOperatorConversations.repair b "Operator" (id())
                    "Role" "Lead" oid "Stale repair" original with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale blob precondition did not fail: %A" other)
            let secondRepair = id()
            WorkQueueOperatorConversations.repair b "Operator" secondRepair
                "Role" "Lead" badBlob.Value "Restore original JSON from Git backup" original
            |> success |> ignore
            Assert.That((entry a "Role" "Lead" messageId).Body, Is.EqualTo("Original question"))
            let snap = LedgerStore.fetch a |> success
            let note, _ = LedgerStore.readAt a snap
                              (WorkQueueOperatorConversations.repairPath firstRepair) |> success
            Assert.That(note.IsSome, Is.True)
            Assert.That(note.Value, Does.Contain(oid))
            Assert.That(note.Value, Does.Contain(head))
            Assert.That(note.Value, Does.Contain("Restore from a malformed legacy merge")))


    [<Test>]
    member _.AuthenticatedJarvisWireAdapterPostsClaimsHandlesAndReconcilesOriginalUuid() =
        let actor = "operator:" + String.replicate 64 "a"
        let readCommand action kind subject operationId : OperatorConversationReadCommand =
            { ProjectName = "fixture"; Action = action; SubjectKind = kind
              SubjectId = subject; OperationId = operationId }
        let writeCommand action (op: Guid) version kind subject messageId : OperatorConversationWriteCommand =
            { ProjectName = "fixture"; Action = action; ActorId = actor
              OperationId = op.ToString("D"); SubjectKind = kind; SubjectId = subject
              ExpectedVersion = version; MessageId = messageId; HandlerGeneration = 0
              ReplyId = ""; Text = ""; Reason = ""; ExpectedBlobOid = "" }
        let apply binding cmd =
            OperatorConversationApi.writeWithBinding cmd binding |> success :?> OperatorReceipt
        let snapshot binding kind subject =
            OperatorConversationApi.readWithBinding (readCommand "List" kind subject "") binding
            |> success :?> OperatorThreadSnapshot
        withClones (fun a b ->
            let subject = "task-operator-adapter"
            let messageId = id().ToString("D")
            let originalOperation = id()
            let post =
                { writeCommand "Post" originalOperation 0 "Task" subject messageId with
                    Text = "Please review the evidence." }
            let created = apply a post
            let observer = snapshot b "Task" subject
            Assert.That(observer.Thread.Version, Is.EqualTo(1))
            Assert.That(observer.Thread.Messages.[0].Status, Is.EqualTo("Pending"))
            Assert.That((snapshot b "Task" subject).Thread.Version, Is.EqualTo(1),
                        "Reading a manual conversation must be passive.")
            let operation =
                OperatorConversationApi.readWithBinding
                    (readCommand "Inspect" "" "" (originalOperation.ToString("D"))) b |> success
            Assert.That(JsonSerializer.Serialize(operation), Does.Contain("Applied"))
            let replay = apply b post
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.LedgerSequence, Is.EqualTo(created.LedgerSequence))
            let claim = writeCommand "Claim" (id()) 1 "Task" subject messageId
            apply b claim |> ignore
            let secondClaim = writeCommand "Claim" (id()) 1 "Task" subject messageId
            match OperatorConversationApi.writeWithBinding secondClaim a with
            | Error error -> Assert.That(sprintf "%A" error, Does.Contain("StaleEntityVersion"))
            | Ok _ -> Assert.Fail("Two handlers cannot claim the same generation.")
            let replyId = id().ToString("D")
            let handle =
                { writeCommand "Handle" (id()) 2 "Task" subject messageId with
                    HandlerGeneration = 1; ReplyId = replyId; Text = "Reviewed and accepted." }
            apply a handle |> ignore
            let final = snapshot b "Task" subject
            Assert.That(final.Thread.Version, Is.EqualTo(3))
            Assert.That(final.Thread.Messages.[0].Status, Is.EqualTo("Handled"))
            Assert.That(final.Thread.Messages.[0].ReplyId, Is.EqualTo(replyId))
            Assert.That(final.Thread.Messages.[0].ReplyBody, Is.EqualTo("Reviewed and accepted."))
            let wrongActor = { writeCommand "Post" (id()) 0 "Role" "Lead" "new-message" with
                                ActorId = "arbitrary-administrator"; Text = "Must be refused." }
            match OperatorConversationApi.writeWithBinding wrongActor b with
            | Error error -> Assert.That(sprintf "%A" error, Does.Contain("PermissionDenied"))
            | Ok _ -> Assert.Fail("Caller-supplied actor labels must be refused.")
            let unapprovedIdentity = { a with ProjectId = Guid.NewGuid() }
            match OperatorConversationApi.readWithBinding
                      (readCommand "List" "Task" subject "") unapprovedIdentity with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Mismatched Git ledger identity must be rejected."))

    [<Test>]
    member _.AuthenticatedJarvisWireAdapterFencesReleasedHandlerGeneration() =
        let actor = "operator:" + String.replicate 64 "a"
        let write action (op: Guid) version generation messageId : OperatorConversationWriteCommand =
            { ProjectName = "fixture"; Action = action; ActorId = actor
              OperationId = op.ToString("D"); SubjectKind = "Role"; SubjectId = "Lead"
              ExpectedVersion = version; MessageId = messageId; HandlerGeneration = generation
              ReplyId = ""; Text = ""; Reason = ""; ExpectedBlobOid = "" }
        let apply binding cmd =
            OperatorConversationApi.writeWithBinding cmd binding |> success |> ignore
        withClones (fun a b ->
            let mid = id().ToString("D")
            apply a { write "Post" (id()) 0 0 mid with Text = "Decision needed." }
            apply b (write "Claim" (id()) 1 0 mid)
            apply a { write "Release" (id()) 2 1 mid with Reason = "Original handler stopped." }
            apply b (write "Claim" (id()) 3 0 mid)
            match OperatorConversationApi.writeWithBinding
                { write "Handle" (id()) 4 1 mid with
                    ReplyId = id().ToString("D"); Text = "Stale response." } a with
            | Error error -> Assert.That(sprintf "%A" error, Does.Contain("StaleEntityVersion"))
            | Ok _ -> Assert.Fail("Old handler generation must not complete a reclaimed message.")
            apply b { write "Handle" (id()) 4 2 mid with
                          ReplyId = id().ToString("D"); Text = "Current response." }
            let current = WorkQueueOperatorConversations.read a "Role" "Lead" |> success
            Assert.That(current.Thread.Messages.[0].HandlerGeneration, Is.EqualTo(2))
            Assert.That(current.Thread.Messages.[0].ReplyBody, Is.EqualTo("Current response.")))
