module HistoricalCheckpointRead20261127

open System
open System.IO
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open AutomationHealthHostedFixture

let private withLedger test =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-historical-checkpoint-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let first, second, _, _, candidate = create root
        let taskId = candidate.TaskId
        let receipt =
            ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) taskId candidate.AgentId
                candidate.AttemptVersion candidate.AgentVersion candidate.Generation
                "old immutable progress" "continue" "" "original evidence"
            |> success
        let sequence = receipt.Operation.Sequence
        let pinnedHead, original = ReviewWorkflow.getCheckpoint second taskId sequence |> success
        Assert.That(original.AttemptId, Is.EqualTo(candidate.AttemptId))
        Assert.That(original.AssignmentGeneration, Is.EqualTo(candidate.Generation))
        Assert.That(original.Sequence, Is.EqualTo(sequence))
        Assert.That(original.Summary, Is.EqualTo("old immutable progress"))
        Assert.That(pinnedHead, Is.EqualTo(receipt.CurrentHead))
        test first second taskId sequence original
    finally
        Directory.Delete(root, true)

let private close first taskId owner =
    ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId owner 0 0 0 "completed" |> success |> ignore
    let _, task, attempt = ReviewWorkflow.getTask first taskId |> success
    Assert.That(attempt.Value.Phase, Is.EqualTo("Finished"))
    ReviewWorkflow.closeTask first (Guid.NewGuid()) taskId task.Version |> success |> ignore

let private reopen first taskId =
    let _, task, _ = ReviewWorkflow.getTask first taskId |> success
    Assert.That(task.Lifecycle, Is.EqualTo("Closed"))
    ReviewWorkflow.reopenTask first (Guid.NewGuid()) taskId task.Version "reopen for follow-up" |> success |> ignore
    let _, opened, attempt = ReviewWorkflow.getTask first taskId |> success
    Assert.That(opened.Lifecycle, Is.EqualTo("Open"))
    Assert.That(attempt.IsNone, Is.True)

let private expectOriginal reader taskId sequence (original: AgentCheckpoint) =
    let expectedHead = (LedgerStore.fetch reader |> success).Commit
    match ReviewWorkflow.getCheckpoint reader taskId sequence with
    | Error error -> Assert.Fail(sprintf "Historical checkpoint %d unavailable: %A" sequence error)
    | Ok(head, checkpoint) ->
        Assert.That(head, Is.EqualTo(expectedHead))
        Assert.That(checkpoint, Is.EqualTo(original))

[<Test>]
let ``current completed and closed checkpoints stay readable and wrong task or unknown sequence fail`` () =
    withLedger (fun first second taskId sequence original ->
        ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "unrelated" "Unrelated" "<p>Other</p>" |> success |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Other" |> success |> ignore
        ReviewWorkflow.assignTask first (Guid.NewGuid()) "unrelated" (Guid.NewGuid()) "Other" |> success |> ignore
        let before = (LedgerStore.fetch second |> success).Commit
        Assert.That((ReviewWorkflow.getCheckpoint second "unrelated" sequence).IsError, Is.True)
        Assert.That((ReviewWorkflow.getCheckpoint second taskId (sequence + 100000L)).IsError, Is.True)
        Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(before))
        close first taskId original.OwnerAgentId
        expectOriginal second taskId sequence original)

[<Test>]
let ``reopened task with no current attempt still exposes original checkpoint`` () =
    withLedger (fun first second taskId sequence original ->
        close first taskId original.OwnerAgentId
        reopen first taskId
        expectOriginal second taskId sequence original)

[<Test>]
let ``replacement attempt exposes both old and new exact checkpoint records`` () =
    withLedger (fun first second taskId sequence original ->
        close first taskId original.OwnerAgentId
        reopen first taskId
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Replacement" |> success |> ignore
        let replacementId = Guid.NewGuid()
        ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId replacementId "Replacement" |> success |> ignore
        ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Replacement" 0 0 0 |> success |> ignore
        let next = ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) taskId "Replacement" 0 0 0
                       "new progress" "next" "" "" |> success
        let _, current = ReviewWorkflow.getCheckpoint second taskId next.Operation.Sequence |> success
        Assert.That(current.AttemptId, Is.EqualTo(replacementId.ToString("N")))
        Assert.That(current.Summary, Is.EqualTo("new progress"))
        expectOriginal second taskId sequence original)

[<Test>]
let ``historical generation remains exact after same attempt recovery`` () =
    withLedger (fun first second taskId sequence original ->
        let _, _, attempt = ReviewWorkflow.getTask first taskId |> success
        let head = LedgerStore.fetch first |> success
        let agent = ReviewWorkflow.getAgentWorkingTurnAt first head original.OwnerAgentId |> success
        let recovered = ReviewWorkflow.recoverAgentAssignment first (Guid.NewGuid()) taskId original.OwnerAgentId
                            attempt.Value.Version agent.Version original.AssignmentGeneration "recover" |> success
        ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId original.OwnerAgentId 0 0 0 |> success |> ignore
        let next = ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) taskId original.OwnerAgentId 0 0 0
                       "new generation" "next" "" "" |> success
        let _, recovery = ReviewWorkflow.getCheckpoint second taskId recovered.Operation.Sequence |> success
        Assert.That(recovery.Kind, Is.EqualTo("Recovery"))
        Assert.That(recovery.AssignmentGeneration, Is.EqualTo(original.AssignmentGeneration))
        let _, current = ReviewWorkflow.getCheckpoint second taskId next.Operation.Sequence |> success
        Assert.That(current.AttemptId, Is.EqualTo(original.AttemptId))
        Assert.That(current.AssignmentGeneration, Is.EqualTo(original.AssignmentGeneration + 1))
        expectOriginal second taskId sequence original)

[<Test>]
let ``historical checkpoint rejects malformed record and wrong ledger binding`` () =
    withLedger (fun first second taskId sequence original ->
        close first taskId original.OwnerAgentId
        reopen first taskId
        let head = LedgerStore.fetch second |> success
        Assert.That((ReviewWorkflow.getCheckpoint { second with LedgerId = Guid.NewGuid() } taskId sequence).IsError, Is.True)
        let intent = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.MalformedCheckpoint"; RequestJson = "{}" }
        let malformed = LedgerStore.transact first intent (fun snapshot ->
            let path = $"tasks/{taskId}/attempts/{original.AttemptId}/checkpoints/{snapshot.Header.NextSequence}.json"
            Ok [{ Path = path; ExpectedBlobOid = None; Content = "{}" }]) |> success
        match ReviewWorkflow.getCheckpoint second taskId malformed.Operation.Sequence with
        | Error(LedgerIntegrityError _) -> ()
        | result -> Assert.Fail(sprintf "Expected malformed checkpoint integrity error, got %A" result)
        expectOriginal second taskId sequence original
        let path = $"tasks/{taskId}/attempts/{original.AttemptId}/checkpoints/{sequence}.json"
        let pinned = LedgerStore.readAt second head path |> success
        let latest = LedgerStore.readAt second (LedgerStore.fetch second |> success) path |> success
        Assert.That(pinned, Is.EqualTo(latest)))

[<Test>]
let ``pinned read does not mix a later checkpoint or head`` () =
    withLedger (fun first second taskId sequence original ->
        let pinned = LedgerStore.fetch second |> success
        let later = ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) taskId original.OwnerAgentId 0 0 0
                        "later progress" "next" "" "" |> success
        close first taskId original.OwnerAgentId
        reopen first taskId
        let latest = LedgerStore.fetch second |> success
        Assert.That(latest.Commit, Is.Not.EqualTo(pinned.Commit))
        let head, checkpoint = ReviewWorkflow.getCheckpointAt second pinned taskId sequence |> success
        Assert.That(head, Is.EqualTo(pinned.Commit))
        Assert.That(checkpoint, Is.EqualTo(original))
        Assert.That((ReviewWorkflow.getCheckpointAt second pinned taskId later.Operation.Sequence).IsError, Is.True)
        let latestHead, laterRecord = ReviewWorkflow.getCheckpoint second taskId later.Operation.Sequence |> success
        Assert.That(latestHead, Is.EqualTo(latest.Commit))
        Assert.That(laterRecord.Summary, Is.EqualTo("later progress"))
        Assert.That((ReviewWorkflow.getCheckpoint second taskId 0L).IsError, Is.True)
        Assert.That((ReviewWorkflow.getCheckpoint second "unknown-task" sequence).IsError, Is.True))

[<Test>]
let ``historical lookup rejects record identity mismatches`` () =
    withLedger (fun first second taskId sequence original ->
        close first taskId original.OwnerAgentId
        reopen first taskId
        let intent = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = "Fixture.IdentityMismatch"; RequestJson = "{}" }
        let corrupt = LedgerStore.transact first intent (fun snapshot ->
            let originalPath = $"tasks/{taskId}/attempts/{original.AttemptId}/checkpoints/{sequence}.json"
            LedgerStore.readAt first snapshot originalPath
            |> Result.map (fun (body, _) ->
                let path = $"tasks/{taskId}/attempts/{original.AttemptId}/checkpoints/{snapshot.Header.NextSequence}.json"
                [{ Path = path; ExpectedBlobOid = None; Content = body.Value }])) |> success
        match ReviewWorkflow.getCheckpoint second taskId corrupt.Operation.Sequence with
        | Error(LedgerIntegrityError _) -> ()
        | result -> Assert.Fail(sprintf "Expected checkpoint identity error, got %A" result)
        expectOriginal second taskId sequence original)

[<Test>]
let ``transferred attempt and completion checkpoint retain original ownership`` () =
    withLedger (fun first second taskId sequence original ->
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Successor" |> success |> ignore
        let _, task, attempt = ReviewWorkflow.getTask first taskId |> success
        let snapshot = LedgerStore.fetch first |> success
        let agent = ReviewWorkflow.getAgentWorkingTurnAt first snapshot original.OwnerAgentId |> success
        let newId = Guid.NewGuid()
        ReviewWorkflow.transferAttemptOwner first (Guid.NewGuid()) taskId newId "Successor"
            task.Version attempt.Value.Version agent.Version original.AssignmentGeneration "transfer"
            |> success |> ignore
        ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Successor" 0 0 0 |> success |> ignore
        let completed = ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Successor" 0 0 0
                            "successor completed" |> success
        let _, closedTask, _ = ReviewWorkflow.getTask first taskId |> success
        ReviewWorkflow.closeTask first (Guid.NewGuid()) taskId closedTask.Version |> success |> ignore
        expectOriginal second taskId sequence original
        let _, completion = ReviewWorkflow.getCheckpoint second taskId completed.Operation.Sequence |> success
        Assert.That(completion.Kind, Is.EqualTo("Completion"))
        Assert.That(completion.OwnerAgentId, Is.EqualTo("Successor"))
        Assert.That(completion.AttemptId, Is.EqualTo(newId.ToString("N"))))

[<Test>]
let ``historical lookup rejects duplicate exact sequence and nonexistent generation`` () =
    withLedger (fun first second taskId sequence original ->
        close first taskId original.OwnerAgentId
        reopen first taskId
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Replacement" |> success |> ignore
        let replacementId = Guid.NewGuid()
        ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId replacementId "Replacement" |> success |> ignore
        ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Replacement" 0 0 0 |> success |> ignore
        let next = ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) taskId "Replacement" 0 0 0
                       "replacement" "next" "" "" |> success
        let rewrite (body: string) (seq: int64) (generation: int option) =
            let obj = System.Text.Json.Nodes.JsonNode.Parse(body).AsObject()
            obj["sequence"] <- System.Text.Json.Nodes.JsonValue.Create(seq)
            generation |> Option.iter (fun g -> obj["assignmentGeneration"] <- System.Text.Json.Nodes.JsonValue.Create(g))
            obj.ToJsonString()
        let oldPath = $"tasks/{taskId}/attempts/{original.AttemptId}/checkpoints/{sequence}.json"
        let newPath = $"tasks/{taskId}/attempts/{replacementId:N}/checkpoints/{next.Operation.Sequence}.json"
        let intent kind = { OperationId = Guid.NewGuid(); Actor = "fixture"; Kind = kind; RequestJson = "{}" }
        let fakeGen = LedgerStore.transact first (intent "Fixture.MissingGeneration") (fun snapshot ->
            LedgerStore.readAt first snapshot oldPath
            |> Result.map (fun (body, _) ->
                let path = $"tasks/{taskId}/attempts/{original.AttemptId}/checkpoints/{snapshot.Header.NextSequence}.json"
                [{ Path = path; ExpectedBlobOid = None
                   Content = rewrite body.Value snapshot.Header.NextSequence (Some 999) }])) |> success
        match ReviewWorkflow.getCheckpoint second taskId fakeGen.Operation.Sequence with
        | Error(LedgerIntegrityError _) -> ()
        | result -> Assert.Fail(sprintf "Expected missing generation integrity error, got %A" result)
        let duplicate = LedgerStore.transact first (intent "Fixture.DuplicateCheckpoint") (fun snapshot ->
            let seq = snapshot.Header.NextSequence
            LedgerStore.readAt first snapshot oldPath
            |> Result.bind (fun (oldBody, _) ->
                LedgerStore.readAt first snapshot newPath
                |> Result.map (fun (newBody, _) ->
                    [{ Path = $"tasks/{taskId}/attempts/{original.AttemptId}/checkpoints/{seq}.json"
                       ExpectedBlobOid = None; Content = rewrite oldBody.Value seq None }
                     { Path = $"tasks/{taskId}/attempts/{replacementId:N}/checkpoints/{seq}.json"
                       ExpectedBlobOid = None; Content = rewrite newBody.Value seq None }]))) |> success
        match ReviewWorkflow.getCheckpoint second taskId duplicate.Operation.Sequence with
        | Error(LedgerIntegrityError _) -> ()
        | result -> Assert.Fail(sprintf "Expected ambiguous sequence integrity error, got %A" result)
        expectOriginal second taskId sequence original)
