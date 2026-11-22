module Client.AutomationChallengeWakeAdapter

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.WorkQueueReactiveWake

/// A locator for a pre-existing, immutable Challenge resolution and its original
/// blocked attempt. It is untrusted until all fields match one fresh approved
/// Git project head, the original operation origin, and canonical index lineage.
type ChallengeWakeCandidate =
    { ProjectId: Guid
      LedgerId: Guid
      Epoch: int
      Commit: string
      OperationId: Guid
      ChallengeId: string
      ConditionId: string
      TaskId: string
      AttemptId: string
      OwnerAgentId: string
      BlockedGeneration: int }

[<RequireQualifiedAccess>]
type WakeDecision =
    | Off
    | Hold of code: string
    | Ready of verifiedProjectHead: string * currentGeneration: int

[<RequireQualifiedAccess>]
type WakeDelivery =
    | Rejected of WakeDecision
    | Emitted of WorkWakeResult

// This file deliberately is not compiled into Client.fsproj. Nothing in it
// mutates the Challenge/project ledger, enrolls a Work, starts a scheduler,
// makes browser/provider calls, or manufactures a new execution identity.
module private Canonical =
    exception Reject of string

    let fail code = raise (Reject code)

    let fromLedger code = function
        | Ok x -> x
        | Error _ -> fail code

    let normalizedGuid text =
        match Guid.TryParseExact(text, "N") with
        | true, id when id <> Guid.Empty && id.ToString("N") = text -> true
        | _ -> false

    let validHead (head:string) =
        not (String.IsNullOrWhiteSpace head)
        && head.Length = 40
        && head |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    let required<'t> binding snapshot path =
        let body, oid = LedgerStore.readAt binding snapshot path |> fromLedger "CanonicalReadFailure"
        match body, oid with
        | Some content, Some _ ->
            try
                use document = JsonDocument.Parse content
                if document.RootElement.GetProperty("schemaVersion").GetInt32() <>
                   (if typeof<'t> = typeof<ReviewAttempt> || typeof<'t> = typeof<ReviewAgent> then 3 else 2) then
                    fail "CanonicalSchemaMismatch"
                let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                let value = JsonSerializer.Deserialize<'t>(content, options)
                if isNull (box value) then fail "CanonicalSchemaMismatch"
                value
            with
            | :? JsonException -> fail "CanonicalSchemaMismatch"
            | :? InvalidOperationException -> fail "CanonicalSchemaMismatch"
            | :? KeyNotFoundException -> fail "CanonicalSchemaMismatch"
        | _ -> fail "MissingCanonicalEvidence"

    let requireChangedPaths (op:LedgerOperation) paths =
        if isNull op.Paths || paths |> List.exists (fun p -> not (Array.contains p op.Paths)) then
            fail "ResolutionMismatch"

    let safeAgentKey (agent:string) =
        Convert.ToBase64String(Encoding.UTF8.GetBytes agent)
            .TrimEnd('=').Replace('+','-').Replace('/','_')

    let indexPath prefix task attempt generation =
        sprintf "%s/%s/%s/%d.json" prefix task attempt generation

    let equalLocator (candidate:ChallengeWakeCandidate) (locator:AttemptChallengeWaitIndex) =
        locator.TaskId = candidate.TaskId
        && locator.AttemptId = candidate.AttemptId
        && locator.OwnerAgentId = candidate.OwnerAgentId
        && locator.AssignmentGeneration = candidate.BlockedGeneration

    let validate (binding:LedgerBinding) (candidate:ChallengeWakeCandidate) =
        if isNull (box binding) || isNull (box candidate) then fail "InvalidCandidate"
        if binding.ProjectId <> candidate.ProjectId || binding.LedgerId <> candidate.LedgerId
           || binding.Epoch <> candidate.Epoch then fail "ForeignProject"
        if candidate.OperationId = Guid.Empty || not (validHead candidate.Commit)
           || not (normalizedGuid candidate.ChallengeId)
           || not (normalizedGuid candidate.ConditionId)
           || not (normalizedGuid candidate.AttemptId)
           || String.IsNullOrWhiteSpace candidate.TaskId
           || String.IsNullOrWhiteSpace candidate.OwnerAgentId
           || candidate.BlockedGeneration < 1 then fail "InvalidCandidate"

        let snapshot = LedgerStore.fetch binding |> fromLedger "CanonicalReadFailure"
        if snapshot.Commit <> candidate.Commit then fail "StaleHead"
        if snapshot.Header.ProjectId <> binding.ProjectId.ToString("D")
           || snapshot.Header.LedgerId <> binding.LedgerId.ToString("D")
           || snapshot.Header.Epoch <> binding.Epoch then fail "ForeignProject"

        // Generation is a canonical attempt fact, never an event-supplied
        // entitlement. Reject a wrong generation before constructing the
        // immutable requeue issuance locator from it.
        let _, currentAttempt = ReviewWorkflow.getTaskAt binding snapshot candidate.TaskId
                                |> fromLedger "CanonicalReadFailure"
        match currentAttempt with
        | Some value when value.AttemptId = candidate.AttemptId
                          && value.AssignmentGeneration = candidate.BlockedGeneration + 1 -> ()
        | _ -> fail "StaleGeneration"

        let operation =
            LedgerStore.getOperationAt binding snapshot candidate.OperationId
            |> fromLedger "CanonicalReadFailure"
            |> Option.defaultWith (fun () -> fail "ResolutionMismatch")
        let _origin = LedgerStore.getOperationOriginAt binding snapshot operation
                      |> fromLedger "CanonicalReadFailure"
        if operation.Type <> "ResolveChallengeCondition"
           || operation.OperationId <> candidate.OperationId.ToString("D")
           || operation.Sequence < 1L then fail "ResolutionMismatch"

        let challengePath = sprintf "challenges/%s/challenge.json" candidate.ChallengeId
        let eventPath = sprintf "challenges/%s/events/%d.json" candidate.ChallengeId operation.Sequence
        let attemptPath = sprintf "tasks/%s/attempts/%s.json" candidate.TaskId candidate.AttemptId
        let waitPath = sprintf "tasks/%s/attempts/%s/challenge-wait.json" candidate.TaskId candidate.AttemptId
        let issuePath = sprintf "tasks/%s/attempts/%s/assignments/%d.json"
                            candidate.TaskId candidate.AttemptId (candidate.BlockedGeneration + 1)
        let lifecyclePath = sprintf "tasks/%s/lifecycle/%d.json" candidate.TaskId operation.Sequence
        let agentPath = sprintf "agents/%s.json" (safeAgentKey candidate.OwnerAgentId)
        requireChangedPaths operation [challengePath;eventPath;attemptPath;waitPath;issuePath;lifecyclePath;agentPath]

        // The operation record is cryptographically checked by LedgerStore;
        // also tie the immutable event to the exact request, not just a status.
        try
            use envelope = JsonDocument.Parse operation.CanonicalRequestJson
            let root = envelope.RootElement
            let request = root.GetProperty("request").GetString()
            use payload = JsonDocument.Parse request
            let data = payload.RootElement
            if root.GetProperty("kind").GetString() <> operation.Type
               || root.GetProperty("actor").GetString() <> operation.Actor
               || root.GetProperty("operationId").GetString() <> operation.OperationId
               || data.GetProperty("challengeId").GetString() <> candidate.ChallengeId
               || data.GetProperty("conditionId").GetString() <> candidate.ConditionId then
                fail "ResolutionMismatch"
        with
        | :? JsonException -> fail "ResolutionMismatch"
        | :? KeyNotFoundException -> fail "ResolutionMismatch"
        | :? InvalidOperationException -> fail "ResolutionMismatch"

        let resolvedEvent:ChallengeEvent = required binding snapshot eventPath
        let challenge:AgentChallenge = required binding snapshot challengePath
        if resolvedEvent.ChallengeId <> candidate.ChallengeId
           || resolvedEvent.ConditionId <> candidate.ConditionId
           || resolvedEvent.Action <> operation.Type
           || resolvedEvent.ActorId <> operation.Actor
           || resolvedEvent.Sequence <> operation.Sequence
           || resolvedEvent.AfterStatus <> "Satisfied"
           || challenge.ChallengeId <> candidate.ChallengeId
           || (not (String.IsNullOrEmpty challenge.TaskId) && challenge.TaskId <> candidate.TaskId)
           || challenge.Status <> "Satisfied"
           || not (challenge.Conditions |> List.exists (fun condition ->
                 condition.ConditionId = candidate.ConditionId
                 && condition.Status = "Satisfied"
                 && condition.OwnerAgentId = operation.Actor))
            then fail "ResolutionMismatch"

        let wait:AttemptChallengeWait = required binding snapshot waitPath
        if wait.TaskId <> candidate.TaskId || wait.AttemptId <> candidate.AttemptId
           || wait.OwnerAgentId <> candidate.OwnerAgentId
           || wait.Status <> "Requeued"
           || wait.Version < 2
           || isNull (box wait.ChallengeIds)
           || not (List.contains candidate.ChallengeId wait.ChallengeIds) then
            fail "NotReady"
        // Every dependency is still Satisfied; a stale or poisoned sibling
        // cannot be silently upgraded by the later scheduler signal.
        for id in wait.ChallengeIds do
            if not (normalizedGuid id) then fail "IndexMismatch"
            let sibling:AgentChallenge = required binding snapshot (sprintf "challenges/%s/challenge.json" id)
            if sibling.ChallengeId <> id || sibling.Status <> "Satisfied"
               || (not (String.IsNullOrEmpty sibling.TaskId) && sibling.TaskId <> candidate.TaskId) then
                fail "NotReady"

        // The same public inbox reader used by the real client verifies all
        // historical owner/challenge reverse edges and their originating block
        // operation (including opposite-direction or future-generation poison).
        let inbox = ReviewWorkflow.getAgentInboxAt binding snapshot candidate.OwnerAgentId
                    |> fromLedger "IndexMismatch"
        let task, activeAttempt = ReviewWorkflow.getTaskAt binding snapshot candidate.TaskId
                                  |> fromLedger "CanonicalReadFailure"
        let attempt = activeAttempt |> Option.defaultWith (fun () -> fail "NotReady")
        if task.TaskId <> candidate.TaskId || task.Lifecycle <> "InProgress"
           || task.AttemptId <> candidate.AttemptId
           || attempt.AttemptId <> candidate.AttemptId
           || attempt.OwnerAgentId <> candidate.OwnerAgentId
           || attempt.Phase <> "Queued"
           || attempt.AssignmentGeneration <> candidate.BlockedGeneration + 1
           || attempt.IssuedSequence <> operation.Sequence
           || inbox.Agent.AgentId <> candidate.OwnerAgentId
           || inbox.Agent.State <> "Ready"
           || inbox.Agent.Execution <> "Stopped"
           || isNull (box inbox.Agent.CurrentAssignment)
           || inbox.Agent.CurrentAssignment.TaskId <> candidate.TaskId
           || inbox.Agent.CurrentAssignment.AttemptId <> candidate.AttemptId
           || not (inbox.RunnableQueue |> List.exists (fun item ->
                item.Task.TaskId = candidate.TaskId
                && item.Attempt.AttemptId = candidate.AttemptId
                && item.Attempt.AssignmentGeneration = candidate.BlockedGeneration + 1)) then
            fail "NotReady"

        let ownerPrefix = "challenge-waits/by-owner/" + safeAgentKey candidate.OwnerAgentId
        let challengePrefix = "challenge-waits/by-challenge/" + candidate.ChallengeId
        let ownerIndex:AttemptChallengeWaitIndex =
            required binding snapshot (indexPath ownerPrefix candidate.TaskId candidate.AttemptId candidate.BlockedGeneration)
        let challengeIndex:AttemptChallengeWaitIndex =
            required binding snapshot (indexPath challengePrefix candidate.TaskId candidate.AttemptId candidate.BlockedGeneration)
        if not (equalLocator candidate ownerIndex) || challengeIndex <> ownerIndex then
            fail "IndexMismatch"

        let issue:AssignmentIssue = required binding snapshot issuePath
        let lifecycle:TaskLifecycleEvent = required binding snapshot lifecyclePath
        if issue.Reason <> "ChallengeUnblock" || issue.TaskId <> candidate.TaskId
           || issue.AttemptId <> candidate.AttemptId
           || issue.OwnerAgentId <> candidate.OwnerAgentId
           || issue.IssuerId <> operation.Actor
           || issue.Generation <> attempt.AssignmentGeneration
           || issue.PreviousGeneration <> candidate.BlockedGeneration
           || issue.IssuedSequence <> operation.Sequence
           || issue.SpecBlobOid <> attempt.SpecBlobOid
           || lifecycle.Action <> "RequeueChallengeBlockedAttempt"
           || lifecycle.TaskId <> candidate.TaskId
           || lifecycle.AttemptId <> candidate.AttemptId
           || lifecycle.Sequence <> operation.Sequence
           || lifecycle.OperationId <> operation.OperationId
           || lifecycle.PreviousGeneration <> candidate.BlockedGeneration
           || lifecycle.NewGeneration <> attempt.AssignmentGeneration
           || lifecycle.PreviousPhase <> "Blocked"
           || lifecycle.NewPhase <> "Queued" then
            fail "ResolutionMismatch"
        snapshot.Commit, attempt.AssignmentGeneration

/// Read-only, fresh-Git gate. No callback and no Git I/O when disabled.
let inspect enabled (binding:LedgerBinding) (candidate:ChallengeWakeCandidate) =
    if not enabled then WakeDecision.Off
    else
        try
            let head, generation = Canonical.validate binding candidate
            WakeDecision.Ready(head, generation)
        with
        | Canonical.Reject code -> WakeDecision.Hold code
        | _ -> WakeDecision.Hold "CanonicalReadFailure"

/// The hint is *not* a requeue, membership check, capacity reservation, or
/// durable authority. An enabled coordinator independently rereads Git and
/// may return Disabled/NeedsAttention; it alone owns the existing Work fence.
let deliver enabled binding candidate (coordinator:WorkQueueReactiveWake.Coordinator) =
    match inspect enabled binding candidate with
    | WakeDecision.Ready _ ->
        try WakeDelivery.Emitted(coordinator.Wake [WorkWakeTrigger.DependencyCommitted])
        with _ -> WakeDelivery.Rejected(WakeDecision.Hold "CoordinatorFailure")
    | other -> WakeDelivery.Rejected other
