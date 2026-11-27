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
