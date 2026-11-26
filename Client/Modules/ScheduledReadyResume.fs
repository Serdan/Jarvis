module Client.ScheduledReadyResume

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.RoleInstructions

/// Capacity is a Scheduler admission decision, not a reservation or provider
/// inventory. All Working agents (including manual launches) are counted.
type Limits =
    { Total: int option
      Roles: Map<string, int> }

let disabled = { Total = None; Roles = Map.empty }

type Ready =
    { AgentId: string
      StateSequence: int64
      TaskId: string
      AttemptId: string
      Generation: int
      Priority: int
      IssuedSequence: int64 }

type Entry =
    { AgentId: string
      Working: bool
      Roles: Set<string>
      Ready: Ready option }

let private bad message = Error(InvalidBinding("Scheduled ready resume: " + message))

let validLimits limits =
    Option.forall (fun n -> n > 0) limits.Total
    && not (isNull (box limits.Roles))
    && Map.forall (fun role n -> not (String.IsNullOrWhiteSpace role) && n > 0) limits.Roles

let permits limits (roster: Entry list) (target: Entry) =
    let working = roster |> List.filter (fun entry -> entry.Working)
    validLimits limits
    && not target.Working
    && (limits.Total |> Option.forall (fun cap -> working.Length < cap))
    && (limits.Roles |> Map.forall (fun role cap ->
        not (Set.contains role target.Roles)
        || (working |> List.filter (fun entry -> Set.contains role entry.Roles) |> List.length) < cap))

/// No special occupancy credit for a submitted-but-not-yet-Working launch.
/// Never stop existing agents when a cap is lowered.
let candidates limits roster =
    roster
    |> List.filter (permits limits roster)
    |> List.choose _.Ready
    |> List.sortBy (fun r -> -int64 r.Priority, r.IssuedSequence, r.AgentId, r.TaskId, r.AttemptId)

let private key (id: string) =
    Convert.ToBase64String(Encoding.UTF8.GetBytes id).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private readAgentId binding snapshot path =
    LedgerStore.readAt binding snapshot path
    |> Result.bind (function
        | Some raw, Some _ ->
            try
                use json = JsonDocument.Parse raw
                let id = json.RootElement.GetProperty("agentId").GetString()
                if String.IsNullOrWhiteSpace id || path <> "agents/" + key id + ".json" then
                    bad "Agent identity/path mismatch."
                else Ok id
            with _ -> bad "Malformed registered agent record."
        | _ -> bad "Missing registered agent record.")

// Ready is read without invoking agent_inbox (which would activate the agent).
// The canonical inbox projection also validates blocked attempt/wait indexes.
let private readReady binding snapshot (agent: ReviewAgent) =
    if agent.State <> "Ready" || agent.Execution <> "Stopped" ||
       not (String.IsNullOrWhiteSpace agent.ActiveSessionId) then Ok None
    else
        ReviewWorkflow.getAgentInboxAt binding snapshot agent.AgentId
        |> Result.bind (fun inbox ->
            match inbox.Focused, inbox.RunnableQueue with
            | Some focused, [runnable]
                when focused = runnable
                     && focused.Task.Lifecycle = "InProgress"
                     && focused.Task.AttemptId = focused.Attempt.AttemptId
                     && focused.Attempt.OwnerAgentId = agent.AgentId
                     && focused.Attempt.AssignmentGeneration > 0
                     && agent.StateSequence > 0L
                     && not (inbox.Blocked |> List.exists (fun item ->
                         item.Attempt.AttemptId = focused.Attempt.AttemptId)) ->
                let candidate =
                    { AgentId = agent.AgentId
                      StateSequence = agent.StateSequence
                      TaskId = focused.Task.TaskId
                      AttemptId = focused.Attempt.AttemptId
                      Generation = focused.Attempt.AssignmentGeneration
                      Priority = focused.Attempt.Priority
                      IssuedSequence = focused.Attempt.IssuedSequence }
                ReviewWorkflow.getResponseAlertsAt binding snapshot agent.AgentId
                |> Result.map (fun queue ->
                    if queue.Pending |> List.exists (fun alert ->
                        alert.Message.TrimStart().StartsWith("STOP", StringComparison.OrdinalIgnoreCase)) then None
                    else Some candidate)
            | _ -> Ok None)

/// The roster and optional role attachments come from a single verified head.
/// With no role caps, role-resolution failures of unrelated Working agents
/// must not become a capacity prerequisite.
let readRosterAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (limits: Limits) =
    LedgerStore.listAt binding snapshot "agents"
    |> Result.bind (fun paths ->
        let paths = paths |> List.filter (fun path ->
            path.StartsWith("agents/", StringComparison.Ordinal)
            && path.EndsWith(".json", StringComparison.Ordinal)
            && not (path.EndsWith(".challenge-subscription.json", StringComparison.Ordinal)))
        let read path =
            readAgentId binding snapshot path
            |> Result.bind (fun id -> ReviewWorkflow.getAgentWorkingTurnAt binding snapshot id)
            |> Result.bind (fun agent ->
                readReady binding snapshot agent
                |> Result.bind (fun ready ->
                    let working = agent.Execution = "Working"
                    let roles =
                        if Map.isEmpty limits.Roles || (not working && ready.IsNone) then Ok Set.empty
                        else
                            RoleInstructions.resolveAtKnownAgent binding snapshot agent.AgentId
                            |> Result.map (fun resolved -> Set.ofList resolved.RoleIds)
                    roles |> Result.map (fun ids ->
                        { AgentId = agent.AgentId; Working = working; Roles = ids; Ready = ready })))
        paths |> List.fold (fun state path ->
            state |> Result.bind (fun entries -> read path |> Result.map (fun entry -> entry :: entries))) (Ok []))

// One stable ID per Ready episode. Changes to unrelated project head, policy,
// rendered prompt or schedule occurrence cannot mint a replacement send.
let operationId (binding: LedgerBinding) (ready: Ready) =
    let material =
        JsonSerializer.Serialize([| "ScheduledReadyResume:v1"; string binding.ProjectId; string binding.LedgerId
                                    string binding.Epoch; ready.AgentId; string ready.StateSequence
                                    ready.TaskId; ready.AttemptId; string ready.Generation |])
    let bytes = SHA256.HashData(Encoding.UTF8.GetBytes material)[..15]
    bytes[7] <- (bytes[7] &&& 0x0Fuy) ||| 0x50uy
    bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
    Guid bytes

// The v1 episode ID remains the first intent. Each later ID is derived only
// from positive immutable no-send proof or explicit verified termination of the preceding intent.
let private derivedId (domain: string) (prior: Guid) =
    let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(domain + ":" + prior.ToString("N")))[..15]
    bytes[7] <- (bytes[7] &&& 0x0Fuy) ||| 0x50uy
    bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
    Guid bytes

let private proofId = derivedId "ScheduledReadyResume:NotInvoked:v1"
let private nextId = derivedId "ScheduledReadyResume:NextAttempt:v1"
let private sendBoundaryId = derivedId "ScheduledReadyResume:SendBoundary:v1"
let private checkinId = derivedId "ScheduledReadyResume:Checkin:v1"
let private activationId = derivedId "ScheduledReadyResume:Activation:v1"
let private noCheckinProofId = derivedId "ScheduledReadyResume:NoCheckin:v1"
let private intentPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/intent.json"
let private proofPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/not-invoked.json"
let private sendBoundaryPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/send-boundary.json"
let private checkinPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/checkin.json"
let private noCheckinProofPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/no-checkin.json"

let private launchDeadline (sentAt: DateTimeOffset) = sentAt.AddMinutes(5.0)

// The immutable operation receipt stores a canonical JSON envelope whose
// request member is a JSON string; it is not a LedgerIntent.
let private operationRequest (operation: LedgerOperation) =
    use envelope = JsonDocument.Parse(operation.CanonicalRequestJson)
    envelope.RootElement.GetProperty("request").GetString()

type private EvidenceSnapshot =
    { HeadCommit: string
      Sequence: int64 }

let private evidenceSnapshotOf (snapshot: LedgerSnapshot) =
    { HeadCommit = snapshot.Commit; Sequence = snapshot.Header.NextSequence - 1L }

let private sendBoundaryContent (intent: LedgerOperation) (sentAt: DateTimeOffset) =
    JsonSerializer.Serialize(
        {| launchId = intent.OperationId; intentOperationId = intent.OperationId
           intentRequestHash = intent.RequestHash; intentSequence = intent.Sequence
           sendBoundaryAtUtc = sentAt.ToUniversalTime(); checkinDeadlineUtc = launchDeadline sentAt |})

let private sendBoundaryIntent id (original: LedgerOperation) sentAt : LedgerIntent =
    { OperationId = sendBoundaryId id; Actor = original.Actor
      Kind = "ScheduledReadyResumeSendBoundary"
      RequestJson = sendBoundaryContent original sentAt }

let private checkinContent (intent: LedgerOperation) (boundary: LedgerOperation) agentId (checkedInAt: DateTimeOffset) =
    JsonSerializer.Serialize(
        {| launchId = intent.OperationId; agentId = agentId; actor = agentId
           intentOperationId = intent.OperationId; intentRequestHash = intent.RequestHash
           intentSequence = intent.Sequence; sendBoundaryOperationId = boundary.OperationId
           sendBoundaryRequestHash = boundary.RequestHash; checkedInAtUtc = checkedInAt.ToUniversalTime()
           activityKind = "AuthenticatedAgentInbox" |})

let private checkinIntent id (original: LedgerOperation) (boundary: LedgerOperation) agentId checkedInAt : LedgerIntent =
    { OperationId = checkinId id; Actor = agentId; Kind = "ScheduledReadyResumeCheckin"
      RequestJson = checkinContent original boundary agentId checkedInAt }

let private noCheckinProofContent (intent: LedgerOperation) (boundary: LedgerOperation)
                                 (checkedAt: DateTimeOffset) (lastSeenAt: DateTimeOffset) (serverNow: DateTimeOffset) (snapshot: EvidenceSnapshot) =
    use boundaryJson = JsonDocument.Parse(operationRequest boundary)
    let sentAt = boundaryJson.RootElement.GetProperty("sendBoundaryAtUtc").GetDateTimeOffset()
    JsonSerializer.Serialize(
        {| launchId = intent.OperationId; intentOperationId = intent.OperationId
           intentRequestHash = intent.RequestHash; intentSequence = intent.Sequence
           sendBoundaryOperationId = boundary.OperationId; sendBoundaryRequestHash = boundary.RequestHash
           checkinDeadlineUtc = launchDeadline sentAt; checkedAtUtc = checkedAt.ToUniversalTime()
           activityLastSeenAtUtc = lastSeenAt.ToUniversalTime(); activityServerNowAtUtc = serverNow.ToUniversalTime()
           evidenceKind = "CanonicalGitLedgerNoLaunchCheckin"
           evidenceSnapshotCommit = snapshot.HeadCommit; evidenceSnapshotSequence = snapshot.Sequence |})

let private noCheckinProofIntent id (original: LedgerOperation) (boundary: LedgerOperation)
                               checkedAt lastSeenAt serverNow snapshot : LedgerIntent =
    { OperationId = noCheckinProofId id; Actor = original.Actor; Kind = "ScheduledReadyResumeNoCheckin"
      RequestJson = noCheckinProofContent original boundary checkedAt lastSeenAt serverNow snapshot }

let private proofContent (intent: LedgerOperation) (boundary: LedgerOperation option) =
    JsonSerializer.Serialize(
        {| intentOperationId = intent.OperationId; intentRequestHash = intent.RequestHash
           intentSequence = intent.Sequence
           sendBoundaryOperationId = boundary |> Option.map _.OperationId
           sendBoundaryRequestHash = boundary |> Option.map _.RequestHash
           stage = "DefinitelyNotInvokedBeforeSend" |})

let private proofIntent (id: Guid) (original: LedgerOperation) (boundary: LedgerOperation option) : LedgerIntent =
    { OperationId = proofId id; Actor = original.Actor; Kind = "ScheduledReadyResumeNotInvoked"
      RequestJson = proofContent original boundary }

let private operatorResolutionId = derivedId "ScheduledReadyResume:OperatorResolution:v1"
let private operatorResolutionPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/operator-resolution.json"

let private operatorResolutionContent (intent: LedgerOperation) (boundary: LedgerOperation option)
                                     (ready: Ready) actor rationale (resolvedAt: DateTimeOffset) (snapshot: EvidenceSnapshot) =
    JsonSerializer.Serialize(
        {| launchId = intent.OperationId; intentOperationId = intent.OperationId
           intentRequestHash = intent.RequestHash; intentSequence = intent.Sequence
           sendBoundaryOperationId = boundary |> Option.map _.OperationId
           sendBoundaryRequestHash = boundary |> Option.map _.RequestHash
           agentId = ready.AgentId; taskId = ready.TaskId; attemptId = ready.AttemptId
           assignmentGeneration = ready.Generation; actor = actor
           executionEndedConfirmed = true; rationale = rationale
           resolvedAtUtc = resolvedAt.ToUniversalTime()
           evidenceSnapshotCommit = snapshot.HeadCommit; evidenceSnapshotSequence = snapshot.Sequence |})

let private operatorResolutionIntent id (original: LedgerOperation) boundary ready actor rationale resolvedAt snapshot : LedgerIntent =
    { OperationId = operatorResolutionId id; Actor = actor; Kind = "ScheduledReadyResumeOperatorResolution"
      RequestJson = operatorResolutionContent original boundary ready actor rationale resolvedAt snapshot }

let private readContentAt binding snapshot path (operation: LedgerOperation) =
    LedgerStore.readAt binding snapshot path
    |> Result.bind (function
        | Some content, Some _ ->
            match Guid.TryParseExact(operation.OperationId, "D") with
            | true, id ->
                let expected : LedgerIntent =
                    { OperationId = id; Actor = operation.Actor; Kind = operation.Type; RequestJson = content }
                LedgerStore.verifyIntegrationOperationIntent binding operation expected
            | _ -> bad "Malformed immutable evidence operation identity."
        | _ -> bad $"Immutable evidence file does not match operation {operation.OperationId}.")

let private readReadyFromIntent (intent: LedgerOperation) =
    try
        use doc = JsonDocument.Parse(operationRequest intent)
        let ready = doc.RootElement.GetProperty("ready")
        Ok
            { AgentId = ready.GetProperty("AgentId").GetString()
              StateSequence = ready.GetProperty("StateSequence").GetInt64()
              TaskId = ready.GetProperty("TaskId").GetString()
              AttemptId = ready.GetProperty("AttemptId").GetString()
              Generation = ready.GetProperty("Generation").GetInt32()
              Priority = ready.GetProperty("Priority").GetInt32()
              IssuedSequence = ready.GetProperty("IssuedSequence").GetInt64() }
    with ex -> bad $"Malformed scheduled-resume Ready identity: {ex.GetType().Name}."

let private readSendBoundaryAt binding snapshot id (intent: LedgerOperation) =
    LedgerStore.getOperationAt binding snapshot (sendBoundaryId id)
    |> Result.bind (function
        | None -> Ok None
        | Some boundary when boundary.Type = "ScheduledReadyResumeSendBoundary"
                             && boundary.Actor = intent.Actor
                             && boundary.Sequence > intent.Sequence
                             && boundary.Path = sendBoundaryPath id ->
            try
                use doc = JsonDocument.Parse(operationRequest boundary)
                let sentAt = doc.RootElement.GetProperty("sendBoundaryAtUtc").GetDateTimeOffset()
                let deadline = doc.RootElement.GetProperty("checkinDeadlineUtc").GetDateTimeOffset()
                let expected = sendBoundaryIntent id intent sentAt
                if deadline <> launchDeadline sentAt then bad "Invalid scheduled-resume check-in deadline."
                else
                    LedgerStore.verifyIntegrationOperationIntent binding boundary expected
                    |> Result.bind (fun () -> readContentAt binding snapshot (sendBoundaryPath id) boundary)
                    |> Result.map (fun () -> Some(boundary, sentAt, deadline))
            with ex -> bad $"Malformed scheduled-resume send boundary: {ex.GetType().Name}."
        | Some _ -> bad "Mismatched scheduled-resume send-boundary receipt.")

let private readCheckinAt binding snapshot id (intent: LedgerOperation) (boundary: LedgerOperation) (ready: Ready) =
    LedgerStore.getOperationAt binding snapshot (checkinId id)
    |> Result.bind (function
        | None -> Ok None
        | Some checkin when checkin.Type = "ScheduledReadyResumeCheckin"
                            && checkin.Actor = ready.AgentId
                            && checkin.Sequence > boundary.Sequence
                            && checkin.Path = checkinPath id ->
            try
                use doc = JsonDocument.Parse(operationRequest checkin)
                let checkedAt = doc.RootElement.GetProperty("checkedInAtUtc").GetDateTimeOffset()
                let expected = checkinIntent id intent boundary ready.AgentId checkedAt
                LedgerStore.verifyIntegrationOperationIntent binding checkin expected
                |> Result.bind (fun () -> readContentAt binding snapshot (checkinPath id) checkin)
                |> Result.bind (fun () ->
                    use boundaryDoc = JsonDocument.Parse(operationRequest boundary)
                    let sentAt = boundaryDoc.RootElement.GetProperty("sendBoundaryAtUtc").GetDateTimeOffset()
                    let deadline = boundaryDoc.RootElement.GetProperty("checkinDeadlineUtc").GetDateTimeOffset()
                    if checkedAt < sentAt || checkedAt >= deadline then
                        bad "A check-in outside the send-boundary/deadline window is not valid."
                    else Ok(Some(checkin, checkedAt)))
            with ex -> bad $"Malformed scheduled-resume check-in: {ex.GetType().Name}."
        | Some _ -> bad "Mismatched scheduled-resume check-in receipt.")

let private readNotInvokedAt binding snapshot id (intent: LedgerOperation) boundary =
    LedgerStore.getOperationAt binding snapshot (proofId id)
    |> Result.bind (function
        | None -> Ok None
        | Some proof when proof.Type = "ScheduledReadyResumeNotInvoked"
                          && proof.Actor = intent.Actor
                          && proof.Sequence > intent.Sequence
                          && proof.Path = proofPath id ->
            let boundaryOperation = boundary |> Option.map (fun (op, _, _) -> op)
            let expected = proofIntent id intent boundaryOperation
            LedgerStore.verifyIntegrationOperationIntent binding proof expected
            |> Result.bind (fun () -> readContentAt binding snapshot (proofPath id) proof)
            |> Result.map (fun () -> Some proof)
        | Some _ -> bad "Mismatched scheduled-resume non-invocation proof.")

let private readNoCheckinProofAt binding snapshot id (intent: LedgerOperation)
                                 (boundary: LedgerOperation * DateTimeOffset * DateTimeOffset) =
    let boundaryOperation, sentAt, deadline = boundary
    LedgerStore.getOperationAt binding snapshot (noCheckinProofId id)
    |> Result.bind (function
        | None -> Ok None
        | Some proof when proof.Type = "ScheduledReadyResumeNoCheckin"
                          && proof.Actor = intent.Actor
                          && proof.Sequence > boundaryOperation.Sequence
                          && proof.Path = noCheckinProofPath id ->
            try
                use doc = JsonDocument.Parse(operationRequest proof)
                let root = doc.RootElement
                let checkedAt = root.GetProperty("checkedAtUtc").GetDateTimeOffset()
                let lastSeen = root.GetProperty("activityLastSeenAtUtc").GetDateTimeOffset()
                let serverNow = root.GetProperty("activityServerNowAtUtc").GetDateTimeOffset()
                let evidenceSnapshot =
                    { HeadCommit = root.GetProperty("evidenceSnapshotCommit").GetString()
                      Sequence = root.GetProperty("evidenceSnapshotSequence").GetInt64() }
                let expected = noCheckinProofIntent id intent boundaryOperation checkedAt lastSeen serverNow evidenceSnapshot
                if deadline <> launchDeadline sentAt || serverNow < deadline || checkedAt < deadline ||
                   checkedAt > serverNow || lastSeen > serverNow || lastSeen >= sentAt ||
                   String.IsNullOrWhiteSpace evidenceSnapshot.HeadCommit ||
                   evidenceSnapshot.Sequence >= proof.Sequence || evidenceSnapshot.Sequence >= snapshot.Header.NextSequence then
                    bad "Invalid no-check-in deadline evidence."
                else
                    LedgerStore.verifyIntegrationOperationIntent binding proof expected
                    |> Result.bind (fun () -> readContentAt binding snapshot (noCheckinProofPath id) proof)
                    |> Result.map (fun () -> Some proof)
            with ex -> bad $"Malformed scheduled-resume no-check-in proof: {ex.GetType().Name}."
        | Some _ -> bad "Mismatched scheduled-resume no-check-in proof.")

let private readOperatorResolutionAt binding snapshot id (intent: LedgerOperation)
                                      boundary (ready: Ready) =
    LedgerStore.getOperationAt binding snapshot (operatorResolutionId id)
    |> Result.bind (function
        | None -> Ok None
        | Some resolution when resolution.Type = "ScheduledReadyResumeOperatorResolution"
                               && resolution.Actor = intent.Actor
                               && resolution.Sequence > intent.Sequence
                               && resolution.Path = operatorResolutionPath id ->
            try
                use doc = JsonDocument.Parse(operationRequest resolution)
                let root = doc.RootElement
                let rationale = root.GetProperty("rationale").GetString()
                let resolvedAt = root.GetProperty("resolvedAtUtc").GetDateTimeOffset()
                let evidenceSnapshot =
                    { HeadCommit = root.GetProperty("evidenceSnapshotCommit").GetString()
                      Sequence = root.GetProperty("evidenceSnapshotSequence").GetInt64() }
                let boundaryOperation = boundary |> Option.map (fun (op, _, _) -> op)
                let expected = operatorResolutionIntent id intent boundaryOperation ready resolution.Actor rationale resolvedAt evidenceSnapshot
                if not (root.GetProperty("executionEndedConfirmed").GetBoolean())
                   || String.IsNullOrWhiteSpace rationale || String.IsNullOrWhiteSpace evidenceSnapshot.HeadCommit ||
                   evidenceSnapshot.Sequence >= resolution.Sequence || evidenceSnapshot.Sequence >= snapshot.Header.NextSequence then
                    bad "Operator resolution lacks explicit end confirmation, rationale, or a valid evidence snapshot."
                else
                    LedgerStore.verifyIntegrationOperationIntent binding resolution expected
                    |> Result.bind (fun () -> readContentAt binding snapshot (operatorResolutionPath id) resolution)
                    |> Result.map (fun () -> Some resolution)
            with ex -> bad $"Malformed scheduled-resume operator resolution: {ex.GetType().Name}."
        | Some _ -> bad "Mismatched scheduled-resume operator-resolution receipt.")

type LaunchInspection =
    { Ready: Ready
      ProtocolVersion: int
      SendBoundaryAtUtc: DateTimeOffset option
      CheckinDeadlineAtUtc: DateTimeOffset option
      CheckinAtUtc: DateTimeOffset option
      NoCheckinAtUtc: DateTimeOffset option
      NotInvoked: bool
      OperatorResolved: bool
      OperatorResolvedAtUtc: DateTimeOffset option }

let private protocolVersion (intent: LedgerOperation) =
    try
        use doc = JsonDocument.Parse(operationRequest intent)
        doc.RootElement.GetProperty("sendBoundaryProtocol").GetInt32()
    with _ -> 0

let inspectLaunchAt binding snapshot id =
    LedgerStore.getOperationAt binding snapshot id
    |> Result.bind (function
        | None -> Ok None
        | Some intent when intent.Type = "ScheduledReadyResumeIntent" && intent.Path = intentPath id ->
            readContentAt binding snapshot (intentPath id) intent
            |> Result.bind (fun () -> readReadyFromIntent intent)
            |> Result.bind (fun ready ->
                readSendBoundaryAt binding snapshot id intent
                |> Result.bind (fun boundary ->
                    readNotInvokedAt binding snapshot id intent boundary
                    |> Result.bind (fun notInvoked ->
                        match boundary with
                        | None ->
                            readOperatorResolutionAt binding snapshot id intent None ready
                            |> Result.bind (fun resolution ->
                                if notInvoked.IsSome && resolution.IsSome then bad "Conflicting terminal evidence for scheduled launch."
                                else
                                    Ok(Some
                                        { Ready = ready; ProtocolVersion = protocolVersion intent
                                          SendBoundaryAtUtc = None; CheckinDeadlineAtUtc = None
                                          CheckinAtUtc = None; NoCheckinAtUtc = None
                                          NotInvoked = notInvoked.IsSome; OperatorResolved = resolution.IsSome
                                          OperatorResolvedAtUtc = resolution |> Option.map (fun op ->
                                              use doc = JsonDocument.Parse(operationRequest op)
                                              doc.RootElement.GetProperty("resolvedAtUtc").GetDateTimeOffset()) }))
                        | Some(boundaryOperation, sentAt, deadline) ->
                            readCheckinAt binding snapshot id intent boundaryOperation ready
                            |> Result.bind (fun checkin ->
                                readNoCheckinProofAt binding snapshot id intent (boundaryOperation, sentAt, deadline)
                                |> Result.bind (fun noCheckin ->
                                    readOperatorResolutionAt binding snapshot id intent boundary ready
                                    |> Result.bind (fun resolution ->
                                        if checkin.IsSome && (noCheckin.IsSome || notInvoked.IsSome || resolution.IsSome) then
                                            bad "Conflicting terminal evidence for scheduled launch."
                                        elif noCheckin.IsSome && notInvoked.IsSome then
                                            bad "Conflicting replacement authorizations for scheduled launch."
                                        elif notInvoked.IsSome && resolution.IsSome then
                                            bad "Conflicting replacement authorizations for scheduled launch."
                                        else
                                            let noCheckinAt =
                                                noCheckin
                                                |> Option.map (fun proof ->
                                                    use proofDoc = JsonDocument.Parse(operationRequest proof)
                                                    proofDoc.RootElement.GetProperty("checkedAtUtc").GetDateTimeOffset())
                                            Ok(Some
                                                { Ready = ready; ProtocolVersion = protocolVersion intent
                                                  SendBoundaryAtUtc = Some sentAt
                                                  CheckinDeadlineAtUtc = Some deadline
                                                  CheckinAtUtc = checkin |> Option.map snd
                                                  NoCheckinAtUtc = noCheckinAt
                                                  NotInvoked = notInvoked.IsSome
                                                  OperatorResolved = resolution.IsSome
                                                  OperatorResolvedAtUtc = resolution |> Option.map (fun op ->
                                                      use doc = JsonDocument.Parse(operationRequest op)
                                                      doc.RootElement.GetProperty("resolvedAtUtc").GetDateTimeOffset()) })))))))
        | Some _ -> bad "Mismatched scheduled-resume intent receipt.")

// Negative actor activity is diagnostic, never execution-termination proof.
// An unresolved predecessor continues to own the fence even if an older
// software version recorded a no-check-in receipt.
let rec private currentLaunchAt binding snapshot (ready: Ready) id =
    inspectLaunchAt binding snapshot id
    |> Result.bind (function
        | None -> Ok(id, None)
        | Some inspection when inspection.Ready <> ready ->
            bad "A scheduled launch receipt does not match the exact Ready episode."
        | Some inspection when inspection.NotInvoked || inspection.OperatorResolved ->
            currentLaunchAt binding snapshot ready (nextId id)
        | Some inspection -> Ok(id, Some inspection))

let private requireCurrentLaunchAt binding snapshot ready id =
    currentLaunchAt binding snapshot ready (operationId binding ready)
    |> Result.bind (function
        | currentId, Some _ when currentId = id -> Ok ()
        | _ -> bad "This launch is not the current unresolved launch; its predecessor or replacement is fenced.")

/// An ordinary actor label is not sufficient to bypass an unresolved
/// scheduled launch. Derive the exact episode independently of STOP/capacity:
/// those affect scheduling eligibility, not ownership of an existing intent.
/// A confirmed first check-in is still fenced until its exact activation.
let guardUncorrelatedIngressAt binding snapshot agentId =
    match ReviewWorkflow.getAgentWorkingTurnAt binding snapshot agentId with
    | Error(StaleEntityVersion _) -> Ok () // no registered agent
    | Error issue -> Error issue
    | Ok agent when agent.State <> "Ready" -> Ok ()
    | Ok agent ->
        let assignment = agent.CurrentAssignment
        ReviewWorkflow.getTaskAt binding snapshot assignment.TaskId
        |> Result.bind (fun (_, attempt) ->
            match attempt with
            | Some current when current.AttemptId = assignment.AttemptId &&
                                current.OwnerAgentId = agentId ->
                let ready =
                    { AgentId = agentId; StateSequence = agent.StateSequence
                      TaskId = assignment.TaskId; AttemptId = current.AttemptId
                      Generation = current.AssignmentGeneration; Priority = current.Priority
                      IssuedSequence = current.IssuedSequence }
                currentLaunchAt binding snapshot ready (operationId binding ready)
                |> Result.bind (function
                    | _, None -> Ok ()
                    | id, Some _ ->
                        bad ($"Launch {id:D} owns this Ready episode. Ordinary actor-only ingress is held until exact check-in/activation, verified no-send, or explicit operator-confirmed termination."))
            | _ -> bad "Cannot establish the exact Ready assignment for launch admission.")

let private checkinStateAt binding snapshot id =
    inspectLaunchAt binding snapshot id
    |> Result.bind (function
        | Some state when not state.NotInvoked && not state.OperatorResolved && state.NoCheckinAtUtc.IsNone ->
            requireCurrentLaunchAt binding snapshot state.Ready id
            |> Result.map (fun () -> Some state)
        | state -> Ok state)

type ActivityEvidence =
    { LastSeenAtUtc: DateTimeOffset
      ServerNowAtUtc: DateTimeOffset }

type CheckinResult =
    | CheckinConfirmed of DateTimeOffset
    | CheckinDuplicate of DateTimeOffset
    | CheckinLate of string

type NoCheckinResult =
    | NoCheckinProven of DateTimeOffset
    | NoCheckinAlreadyProven of DateTimeOffset
    | LaunchAlreadyCheckedIn of DateTimeOffset
    | AssignmentNoLongerReady of string
    | NoCheckinDeadlineNotReached of DateTimeOffset
    | NoCheckinActivityEvidenceUnavailable of string
    | LaunchSuperseded of string

type OperatorResolutionResult =
    | OperatorResolutionRecorded of DateTimeOffset
    | OperatorResolutionAlreadyRecorded of DateTimeOffset

type LaunchReconciliation =
    | LaunchStarted of DateTimeOffset
    | LaunchReplacementAuthorized of DateTimeOffset
    | LaunchAwaitingCheckin of DateTimeOffset
    | LaunchOperatorResolutionRequired of string
    | LaunchActivityEvidenceUnavailable of string
    | LaunchAssignmentNoLongerReady of string
    | LaunchIntentMissing of string

/// Recheck the exact Ready episode and non-consuming STOP queue at the
/// latest verified snapshot immediately before the browser effect.
let stillReadyAt binding snapshot (ready: Ready) =
    ReviewWorkflow.getAgentWorkingTurnAt binding snapshot ready.AgentId
    |> Result.bind (readReady binding snapshot)
    |> Result.map (fun current -> current = Some ready)

let private exactWorkingActivationAt binding snapshot id (ready: Ready) =
    ReviewWorkflow.getAgentInboxAt binding snapshot ready.AgentId
    |> Result.bind (fun inbox ->
        let sameWorking =
            inbox.Agent.State = "Working" &&
            (inbox.Working |> Option.exists (fun work ->
                work.Task.TaskId = ready.TaskId &&
                work.Attempt.AttemptId = ready.AttemptId &&
                work.Attempt.AssignmentGeneration = ready.Generation))
        if not sameWorking then Ok false
        else
            LedgerStore.getOperationAt binding snapshot (activationId id)
            |> Result.bind (function
                | Some activation when activation.Type = "ResumeAgent" &&
                                       activation.Actor = ready.AgentId &&
                                       activation.Sequence = inbox.Agent.StateSequence ->
                    try
                        use doc = JsonDocument.Parse(operationRequest activation)
                        let request = doc.RootElement
                        Ok(request.GetProperty("taskId").GetString() = ready.TaskId &&
                           request.GetProperty("ownerId").GetString() = ready.AgentId &&
                           request.GetProperty("generation").GetInt32() = ready.Generation)
                    with _ -> bad "Malformed launch activation receipt."
                | _ -> Ok false))

/// Validate the check-in at the exact snapshot used for activation. The
/// returned ID and agent version fence the subsequent Ready->Working CAS.
let confirmedActivationAt binding snapshot agentId (launchId: string) =
    match Guid.TryParseExact(launchId, "D") with
    | false, _ -> bad "Invalid scheduled launch identity."
    | true, id ->
        inspectLaunchAt binding snapshot id
        |> Result.bind (function
            | Some inspection when inspection.Ready.AgentId = agentId &&
                                   inspection.CheckinAtUtc.IsSome ->
                stillReadyAt binding snapshot inspection.Ready
                |> Result.bind (fun ready ->
                    if ready then
                        requireCurrentLaunchAt binding snapshot inspection.Ready id
                        |> Result.map (fun () -> Some(activationId id, inspection.Ready))
                    else exactWorkingActivationAt binding snapshot id inspection.Ready
                         |> Result.bind (fun working ->
                            if working then Ok None
                            else bad "The confirmed launch is no longer the current Ready/Working episode."))
            | _ -> bad "No confirmed check-in for this launch and agent.")

/// An absent proof does not grant permission. The next candidate is derived
/// only from verified non-invocation or explicit operator-confirmed termination.
let private recordCheckinOnce binding agentId (launchId: string) (checkedAt: DateTimeOffset) =
    match Guid.TryParseExact(launchId, "D") with
    | false, _ -> bad "Scheduled launch check-in requires the exact launchId returned by the scheduler."
    | true, id ->
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            checkinStateAt binding snapshot id
            |> Result.bind (function
                | None -> bad "The scheduled launch intent does not exist."
                | Some inspection when inspection.Ready.AgentId <> agentId ->
                    bad "The launchId belongs to a different agent; check-in was rejected."
                | Some inspection when inspection.CheckinAtUtc.IsSome ->
                    stillReadyAt binding snapshot inspection.Ready
                    |> Result.bind (fun exactReady ->
                        if exactReady then Ok(CheckinDuplicate inspection.CheckinAtUtc.Value)
                        else
                            exactWorkingActivationAt binding snapshot id inspection.Ready
                            |> Result.bind (fun sameWorking ->
                                if sameWorking then
                                    Ok(CheckinDuplicate inspection.CheckinAtUtc.Value)
                                else Ok(CheckinLate "The original assignment is no longer current; duplicate check-in cannot activate another Ready episode.")))
                | Some inspection when inspection.NotInvoked || inspection.NoCheckinAtUtc.IsSome || inspection.OperatorResolved ->
                    Ok(CheckinLate "This launch was already resolved or superseded; the old session must stop.")
                | Some inspection ->
                    match inspection.SendBoundaryAtUtc, inspection.CheckinDeadlineAtUtc with
                    | Some sentAt, Some deadline when checkedAt >= sentAt && checkedAt < deadline ->
                        LedgerStore.getOperationAt binding snapshot id
                        |> Result.bind (function
                        | None -> bad "Original launch intent missing."
                        | Some original ->
                            readSendBoundaryAt binding snapshot id original
                            |> Result.bind (function
                                | Some(boundaryOperation, currentSentAt, currentDeadline)
                                    when currentSentAt = sentAt && currentDeadline = deadline ->
                                    let checkin = checkinIntent id original boundaryOperation agentId checkedAt
                                    Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding checkin (fun current ->
                                            checkinStateAt binding current id
                                            |> Result.bind (function
                                                | Some currentState when currentState.Ready <> inspection.Ready ->
                                                    bad "The Ready assignment changed before check-in; the launch is fenced."
                                                | Some currentState when currentState.CheckinAtUtc.IsSome ->
                                                    bad "The launch already has a check-in."
                                                | Some currentState when currentState.NotInvoked ||
                                                                         currentState.NoCheckinAtUtc.IsSome ||
                                                                         currentState.OperatorResolved ->
                                                    bad "The launch was resolved before this check-in; the old session must stop."
                                                | Some _ ->
                                                    stillReadyAt binding current inspection.Ready
                                                    |> Result.bind (fun stillReady ->
                                                        if not stillReady then
                                                            bad "The assignment is no longer Ready or has an active session; check-in was rejected."
                                                        else
                                                            LedgerStore.getOperationAt binding current id
                                                            |> Result.bind (function
                                                            | None -> bad "Original launch intent missing."
                                                            | Some currentIntent ->
                                                                readSendBoundaryAt binding current id currentIntent
                                                                |> Result.bind (function
                                                                    | Some(currentBoundary, currentSentAt, currentDeadline)
                                                                        when currentSentAt = sentAt && currentDeadline = deadline ->
                                                                        Ok [{ Path = checkinPath id; ExpectedBlobOid = None; Content = (checkinIntent id currentIntent currentBoundary agentId checkedAt).RequestJson }]
                                                                    | _ -> bad "The send boundary changed before check-in; the launch is fenced.")))
                                                | None -> bad "The scheduled launch intent disappeared before check-in."))
                                    |> Result.map (fun write ->
                                        if write.AlreadyApplied then CheckinDuplicate checkedAt
                                        else CheckinConfirmed checkedAt)
                                | _ -> bad "The durable send boundary is missing or inconsistent; check-in was rejected."))
                    | Some _, Some deadline when checkedAt >= deadline ->
                        Ok(CheckinLate("The five-minute check-in deadline passed at " + deadline.ToString("O") +
                                       "; this late launch cannot reclaim the assignment."))
                    | Some _, Some _ -> bad "The check-in timestamp predates the durable send boundary."
                    | _ -> Ok(CheckinLate "The launch has no durable send boundary; it cannot claim the assignment.")))

/// A concurrent check-in/expiry can win after the first snapshot. Re-read only
/// confirmed terminal evidence; do not retry an ambiguous write as a new claim.
let recordCheckin binding agentId (launchId: string) checkedAt =
    match recordCheckinOnce binding agentId launchId checkedAt with
    | Ok result -> Ok result
    | Error original ->
        match Guid.TryParseExact(launchId, "D") with
        | false, _ -> Error original
        | true, id ->
            match LedgerStore.fetch binding |> Result.bind (fun snapshot ->
                inspectLaunchAt binding snapshot id |> Result.bind (function
                    | Some state when state.Ready.AgentId = agentId && state.CheckinAtUtc.IsSome ->
                        stillReadyAt binding snapshot state.Ready |> Result.bind (fun ready ->
                            if ready then Ok(Some(CheckinDuplicate state.CheckinAtUtc.Value))
                            else exactWorkingActivationAt binding snapshot id state.Ready
                                 |> Result.map (fun working ->
                                     if working then Some(CheckinDuplicate state.CheckinAtUtc.Value)
                                     else Some(CheckinLate "Original launch no longer owns the current assignment.")))
                    | Some state when state.Ready.AgentId = agentId &&
                                      (state.NotInvoked || state.NoCheckinAtUtc.IsSome || state.OperatorResolved) ->
                        Ok(Some(CheckinLate "Original launch was resolved or superseded."))
                    | _ -> Ok None)) with
            | Ok(Some result) -> Ok result
            | _ -> Error original

let proveNotInvokedAt binding (launchId: string) (_checkedAt: DateTimeOffset) =
    match Guid.TryParseExact(launchId, "D") with
    | false, _ -> bad "Invalid scheduled launch identity."
    | true, id ->
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            inspectLaunchAt binding snapshot id
            |> Result.bind (function
                | None -> bad "The scheduled launch intent does not exist."
                | Some inspection when inspection.NotInvoked -> Ok false
                | Some inspection when inspection.SendBoundaryAtUtc.IsSome ->
                    bad "A send boundary exists; this launch cannot be classified as definitely not invoked."
                | Some inspection when inspection.ProtocolVersion < 1 ->
                    bad "Legacy launch intent has no send-boundary protocol; absence of a boundary is not proof."
                | Some inspection when inspection.CheckinAtUtc.IsSome ||
                                      inspection.NoCheckinAtUtc.IsSome ||
                                      inspection.OperatorResolved ->
                    bad "The launch already has a terminal resolution."
                | Some inspection ->
                    LedgerStore.getOperationAt binding snapshot id
                    |> Result.bind (function
                    | None -> bad "Original launch intent missing."
                    | Some original ->
                        let proof = proofIntent id original None
                        Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding proof (fun current ->
                                inspectLaunchAt binding current id
                                |> Result.bind (function
                                    | Some currentState when currentState.Ready <> inspection.Ready ->
                                        bad "The Ready assignment changed before no-send proof."
                                    | Some currentState when currentState.SendBoundaryAtUtc.IsSome ||
                                                             currentState.CheckinAtUtc.IsSome ||
                                                             currentState.NoCheckinAtUtc.IsSome ||
                                                             currentState.NotInvoked ||
                                                             currentState.OperatorResolved ->
                                        bad "The launch acquired a send boundary or terminal proof before no-send reconciliation."
                                    | Some _ ->
                                        stillReadyAt binding current inspection.Ready
                                        |> Result.bind (fun stillReady ->
                                            if stillReady then Ok [{ Path = proofPath id; ExpectedBlobOid = None; Content = proof.RequestJson }]
                                            else bad "The assignment is no longer Ready or has an active session; no-send proof rejected.")
                                    | None -> bad "The launch intent disappeared before no-send proof."))
                        |> Result.map (fun write -> not write.AlreadyApplied))))

let recordNoCheckinProof binding (launchId: string) (evidence: ActivityEvidence) =
    match Guid.TryParseExact(launchId, "D") with
    | false, _ -> bad "Invalid scheduled launch identity."
    | true, id ->
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            inspectLaunchAt binding snapshot id
            |> Result.bind (function
                | None -> bad "The scheduled launch intent does not exist."
                | Some inspection when inspection.CheckinAtUtc.IsSome ->
                    Ok(LaunchAlreadyCheckedIn inspection.CheckinAtUtc.Value)
                | Some inspection when inspection.NoCheckinAtUtc.IsSome ->
                    Ok(NoCheckinAlreadyProven inspection.NoCheckinAtUtc.Value)
                | Some inspection when inspection.NotInvoked || inspection.OperatorResolved ->
                    Ok(LaunchSuperseded "The launch already has a no-send or operator resolution.")
                | Some inspection ->
                    match inspection.SendBoundaryAtUtc, inspection.CheckinDeadlineAtUtc with
                    | Some sentAt, Some deadline when evidence.ServerNowAtUtc < deadline ->
                        Ok(NoCheckinDeadlineNotReached deadline)
                    | Some sentAt, Some deadline
                        when evidence.LastSeenAtUtc = DateTimeOffset.MinValue ||
                             evidence.LastSeenAtUtc > evidence.ServerNowAtUtc ||
                             evidence.LastSeenAtUtc >= sentAt ->
                        Ok(NoCheckinActivityEvidenceUnavailable(
                            "Authoritative activity shows activity at or after the send boundary. It is not an exact launch check-in, so automatic replacement is unsafe."))
                    | Some sentAt, Some deadline ->
                        LedgerStore.getOperationAt binding snapshot id
                        |> Result.bind (function
                        | None -> bad "Original launch intent missing."
                        | Some original ->
                            readSendBoundaryAt binding snapshot id original
                            |> Result.bind (function
                                | Some(boundaryOperation, currentSentAt, currentDeadline)
                                    when currentSentAt = sentAt && currentDeadline = deadline ->
                                    let proof =
                                        noCheckinProofIntent id original boundaryOperation
                                            evidence.ServerNowAtUtc evidence.LastSeenAtUtc
                                            evidence.ServerNowAtUtc (evidenceSnapshotOf snapshot)
                                    Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding proof (fun current ->
                                            inspectLaunchAt binding current id
                                            |> Result.bind (function
                                                | Some currentState when currentState.Ready <> inspection.Ready ->
                                                    bad "The Ready assignment changed before no-check-in proof."
                                                | Some currentState when currentState.CheckinAtUtc.IsSome ->
                                                    bad "The launch checked in before no-check-in proof."
                                                | Some currentState when currentState.NoCheckinAtUtc.IsSome ||
                                                                         currentState.NotInvoked ||
                                                                         currentState.OperatorResolved ->
                                                    bad "The launch already has a terminal resolution."
                                                | Some currentState
                                                    when currentState.SendBoundaryAtUtc <> Some sentAt ||
                                                         evidence.LastSeenAtUtc >= sentAt ||
                                                         evidence.LastSeenAtUtc > evidence.ServerNowAtUtc ->
                                                    bad "The send boundary or authoritative activity evidence changed before no-check-in proof."
                                                | Some _ ->
                                                    stillReadyAt binding current inspection.Ready
                                                    |> Result.bind (fun stillReady ->
                                                        if stillReady then Ok [{ Path = noCheckinProofPath id; ExpectedBlobOid = None; Content = proof.RequestJson }]
                                                        else bad "The assignment is no longer Ready or has an active session; no-check-in proof rejected.")
                                                | None -> bad "The scheduled launch intent disappeared before no-check-in proof."))
                                    |> Result.map (fun write ->
                                        if write.AlreadyApplied then NoCheckinAlreadyProven evidence.ServerNowAtUtc
                                        else NoCheckinProven evidence.ServerNowAtUtc)
                                | _ -> bad "The durable send boundary is missing or inconsistent."))
                    | _ -> Ok(LaunchSuperseded "The launch has no durable send boundary; no-check-in proof is not applicable.")))

let resolveOperator binding actor (launchId: string) expectedReady rationale executionEndedConfirmed resolvedAt =
    if not executionEndedConfirmed then bad "Operator resolution requires explicit confirmation that the old execution ended."
    elif String.IsNullOrWhiteSpace actor then bad "Operator resolution requires an authenticated operator."
    elif String.IsNullOrWhiteSpace rationale then bad "Operator resolution requires a recorded rationale."
    else
        match Guid.TryParseExact(launchId, "D") with
        | false, _ -> bad "Invalid scheduled launch identity."
        | true, id ->
            LedgerStore.fetch binding
            |> Result.bind (fun snapshot ->
                inspectLaunchAt binding snapshot id
                |> Result.bind (function
                    | None -> bad "The scheduled launch intent does not exist."
                    | Some inspection when inspection.Ready <> expectedReady ->
                        bad "Operator resolution rejected: the supplied agent/task/attempt/generation does not match the original launch."
                    | Some inspection when inspection.CheckinAtUtc.IsSome ->
                        bad "Operator resolution rejected: the exact launch has already checked in."
                    | Some inspection when inspection.NotInvoked ->
                        bad "Operator resolution rejected: the launch already has a verified no-send proof."
                    | Some inspection ->
                        LedgerStore.getOperationAt binding snapshot id
                        |> Result.bind (function
                        | None -> bad "Original launch intent missing."
                        | Some original ->
                            if original.Actor <> actor then bad "Only the original authenticated schedule owner may resolve this launch."
                            elif inspection.OperatorResolvedAtUtc.IsSome then
                                Ok(OperatorResolutionAlreadyRecorded inspection.OperatorResolvedAtUtc.Value)
                            else
                                readSendBoundaryAt binding snapshot id original
                                |> Result.bind (fun boundary ->
                                    let boundaryOperation = boundary |> Option.map (fun (operation, _, _) -> operation)
                                    let resolution =
                                        operatorResolutionIntent id original boundaryOperation expectedReady actor
                                            rationale resolvedAt (evidenceSnapshotOf snapshot)
                                    Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding resolution (fun current ->
                                            inspectLaunchAt binding current id
                                            |> Result.bind (function
                                                | Some currentState when currentState.Ready <> expectedReady ->
                                                    bad "The Ready assignment changed before operator resolution."
                                                | Some currentState when currentState.CheckinAtUtc.IsSome ||
                                                                         currentState.NotInvoked ||
                                                                         currentState.OperatorResolved ->
                                                    bad "The launch was already checked in or resolved before operator resolution."
                                                | Some _ ->
                                                    stillReadyAt binding current expectedReady
                                                    |> Result.bind (fun stillReady ->
                                                        if stillReady then Ok [{ Path = operatorResolutionPath id; ExpectedBlobOid = None; Content = resolution.RequestJson }]
                                                        else bad "Operator resolution rejected: the assignment is no longer Ready or an active session exists.")
                                                | None -> bad "The scheduled launch intent disappeared before operator resolution."))
                                    |> Result.map (fun write ->
                                        if write.AlreadyApplied then OperatorResolutionAlreadyRecorded resolvedAt
                                        else OperatorResolutionRecorded resolvedAt)))))

let reconcileAt binding (launchId: string) now activity =
    match Guid.TryParseExact(launchId, "D") with
    | false, _ -> bad "Invalid scheduled launch identity."
    | true, id ->
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            inspectLaunchAt binding snapshot id
            |> Result.bind (function
                | None -> Ok(LaunchIntentMissing "The original scheduled launch intent is missing.")
                | Some inspection when inspection.CheckinAtUtc.IsSome ->
                    Ok(LaunchStarted inspection.CheckinAtUtc.Value)
                | Some inspection when inspection.NotInvoked ->
                    Ok(LaunchReplacementAuthorized now)
                | Some inspection when inspection.OperatorResolvedAtUtc.IsSome ->
                    Ok(LaunchReplacementAuthorized inspection.OperatorResolvedAtUtc.Value)
                | Some inspection when inspection.NoCheckinAtUtc.IsSome ->
                    Ok(LaunchOperatorResolutionRequired "Deadline elapsed without correlated check-in. Actor activity cannot establish execution termination; explicit verified operator resolution is required.")
                | Some inspection ->
                    match inspection.SendBoundaryAtUtc, inspection.CheckinDeadlineAtUtc with
                    | None, _ when inspection.ProtocolVersion >= 1 ->
                        proveNotInvokedAt binding launchId now
                        |> Result.map (fun _ -> LaunchReplacementAuthorized now)
                    | None, _ ->
                        Ok(LaunchOperatorResolutionRequired(
                            "Legacy launch has no durable send boundary; an operator must verify that the old execution ended."))
                    | Some _, Some deadline when now < deadline ->
                        Ok(LaunchAwaitingCheckin deadline)
                    | Some _, Some deadline ->
                        match activity with
                        | None ->
                            Ok(LaunchActivityEvidenceUnavailable(
                                "The five-minute deadline elapsed, but authoritative agent-activity coverage is unavailable. No replacement was authorized."))
                        | Some evidence when evidence.ServerNowAtUtc < deadline ->
                            Ok(LaunchAwaitingCheckin deadline)
                        | Some evidence ->
                            recordNoCheckinProof binding launchId evidence
                            |> Result.map (function
                                | NoCheckinProven _
                                | NoCheckinAlreadyProven _ ->
                                    LaunchOperatorResolutionRequired "Deadline elapsed without correlated check-in. Actor activity cannot establish execution termination; explicit verified operator resolution is required."
                                | LaunchAlreadyCheckedIn at -> LaunchStarted at
                                | NoCheckinDeadlineNotReached next -> LaunchAwaitingCheckin next
                                | NoCheckinActivityEvidenceUnavailable reason -> LaunchActivityEvidenceUnavailable reason
                                | AssignmentNoLongerReady reason -> LaunchAssignmentNoLongerReady reason
                                | LaunchSuperseded reason -> LaunchOperatorResolutionRequired reason)
                    | _ ->
                        Ok(LaunchOperatorResolutionRequired(
                            "The launch has an incomplete or inconsistent send-boundary record; operator resolution is required."))))

let private nextOperationAt binding snapshot ready =
    currentLaunchAt binding snapshot ready (operationId binding ready)
    |> Result.map (function id, None -> Some id | _, Some _ -> None)

type Prepared =
    { Ready: Ready
      Rendered: RoleInstructions.RenderedLaunchPrompt
      Arguments: string
      OperationId: Guid
      Owner: Guid
      SendBoundaryAtUtc: DateTimeOffset option }

type ReadLimits = LedgerBinding -> LedgerSnapshot -> Result<Limits, LedgerFailure>

/// A missing canonical policy is disabled, while malformed/unavailable policy
/// is never silently interpreted as absence.
let readCurrentLimits : ReadLimits =
    fun binding snapshot ->
        ProjectCapacityPolicy.readCurrentOptional binding snapshot
        |> Result.map (fun (policy, _) -> { Total = policy.Total; Roles = policy.Roles })

/// Only the production autonomous route uses this reader. Absence, legacy
/// capacity settings, or malformed/foreign policy never authorize automation.
let readAutomationLimits : ReadLimits =
    fun binding snapshot ->
        ProjectCapacityPolicy.readCurrentOptional binding snapshot
        |> Result.bind (fun (policy, _) ->
            if policy.AutomationEnabled then Ok { Total = policy.Total; Roles = policy.Roles }
            else bad "Project Automation is disabled.")

/// The configured recurring schedule is the explicit default-off route.
/// Require a main project selector and the provider's supported empty start
/// surface; never pass arbitrary route metadata into the launcher.
let validateRoute (action: BrowserSchedules.ScheduledAction) =
    if not (BrowserSchedules.isReadyResume action) ||
       action.TriggerKind <> "Every" || action.RepeatEverySeconds <= 0 ||
       String.IsNullOrWhiteSpace(action.ProjectName) ||
       action.ProjectName.Contains("::", StringComparison.Ordinal) ||
       String.IsNullOrWhiteSpace(action.WindowRegistration) then
        Error "InvalidReadyResumeRoute"
    else
        try
            use doc = JsonDocument.Parse(action.ArgsJson)
            let fields = doc.RootElement.EnumerateObject() |> Seq.toList
            let validUrl =
                match Uri.TryCreate(action.Url, UriKind.Absolute) with
                | true, uri ->
                    uri.Scheme = "https" && uri.Host = "chatgpt.com" &&
                    uri.IsDefaultPort && uri.UserInfo = "" && uri.Query = "" && uri.Fragment = "" &&
                    (uri.AbsolutePath = "/" ||
                     System.Text.RegularExpressions.Regex.IsMatch(
                         uri.AbsolutePath, @"^/g/g-p-[a-z0-9]+(?:-[a-z0-9]+)*/project/?$"))
                | _ -> false
            if fields.Length <> 1 || not validUrl then Error "InvalidReadyResumeRoute"
            else Ok ()
        with _ -> Error "InvalidReadyResumeRoute"

/// Only pass the two documented launcher arguments. The host-only switch is
/// never exposed to the browser script. The configured project destination
/// must be sent explicitly because launchAgent.js defaults to the home page.
let browserArguments (startUrl: string) (prepared: Prepared) =
    let args = JsonSerializer.Serialize(
        {| prompt = prepared.Rendered.Prompt; startUrl = startUrl |},
        JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
    if BrowserArguments.fits args then Ok args
    else Error "Rendered launch exceeds argument budget."

let private addLaunchId (operationId: Guid) (rendered: RoleInstructions.RenderedLaunchPrompt) =
    let launchId = operationId.ToString("D")
    let prompt =
        rendered.Prompt
        + "\n\nScheduled Jarvis launch check-in: your first Jarvis tool call MUST be `agent_inbox` with this exact `launchId`: `"
        + launchId
        + "`. Do not omit, change, or reuse the launchId."
    { rendered with Prompt = prompt; PromptSha256 = RoleInstructions.launchTemplateSha256 prompt }


type HeldLaunch =
    { AgentId: string
      TaskId: string
      AttemptId: string
      AssignmentGeneration: int
      LaunchId: Guid
      LastKnownOutcome: string
      SendBoundaryAtUtc: DateTimeOffset option
      CheckinDeadlineAtUtc: DateTimeOffset option
      CheckinStatus: string
      Reason: string }

type Preparation =
    | EmptyReadyQueue
    | ReadyToLaunch of Prepared
    | HeldLaunches of HeldLaunch list

let rec private heldLaunchAt binding snapshot (ready: Ready) lastKnownOutcome id =
    inspectLaunchAt binding snapshot id
    |> Result.bind (function
        | None -> Ok None
        | Some inspection when inspection.Ready <> ready ->
            bad "A scheduled launch receipt does not match the exact Ready episode."
        | Some inspection when inspection.NotInvoked || inspection.OperatorResolved ->
            heldLaunchAt binding snapshot ready lastKnownOutcome (nextId id)
        | Some inspection ->
            let status, reason =
                match inspection.CheckinAtUtc, inspection.SendBoundaryAtUtc, inspection.CheckinDeadlineAtUtc with
                | Some checkedAt, _, _ ->
                    "started", "The exact scheduled launch checked in at " + checkedAt.ToString("O") + "; replacement is fenced."
                | None, Some sentAt, Some deadline when DateTimeOffset.UtcNow < deadline ->
                    "awaitingCheckin", "Send boundary recorded at " + sentAt.ToString("O") + "; waiting for authenticated launch check-in until " + deadline.ToString("O") + "."
                | None, Some sentAt, Some deadline ->
                    "operatorResolutionRequired", "No attributed check-in by " + deadline.ToString("O") +
                    "; actor activity cannot establish termination. Verify that the old execution ended and record operator resolution before replacement. Send boundary: " + sentAt.ToString("O") + "."
                | None, None, _ when inspection.ProtocolVersion >= 1 ->
                    "sendBoundaryRecoveryRequired", "Intent exists without a send boundary; reconcile a durable no-send proof before replacement."
                | _ ->
                    "operatorResolutionRequired", "Legacy launch has no verifiable send boundary; operator must verify the old execution ended."
            Ok(Some
                { AgentId = ready.AgentId
                  TaskId = ready.TaskId
                  AttemptId = ready.AttemptId
                  AssignmentGeneration = ready.Generation
                  LaunchId = id
                  LastKnownOutcome =
                      if inspection.CheckinAtUtc.IsSome then "AuthenticatedCheckin"
                      elif inspection.NoCheckinAtUtc.IsSome then "DeadlineElapsedNoCorrelatedCheckin"
                      elif inspection.SendBoundaryAtUtc.IsSome then "SendBoundaryRecorded"
                      elif inspection.ProtocolVersion >= 1 then "NoSendBoundaryRecorded"
                      elif String.IsNullOrWhiteSpace lastKnownOutcome then "Unknown"
                      else lastKnownOutcome
                  SendBoundaryAtUtc = inspection.SendBoundaryAtUtc
                  CheckinDeadlineAtUtc = inspection.CheckinDeadlineAtUtc
                  CheckinStatus = status
                  Reason = reason }))

/// Reconcile existing launch fences independently of candidate selection. A
/// capacity hold may delay a replacement but does not erase its deadline.
let heldForReconciliationAt (readLimits: ReadLimits) binding snapshot =
    readLimits binding snapshot
    |> Result.bind (fun limits ->
        readRosterAt binding snapshot limits
        |> Result.bind (fun roster ->
            let rec collect held = function
                | [] -> Ok(List.rev held)
                | ready :: tail ->
                    heldLaunchAt binding snapshot ready "Unknown" (operationId binding ready)
                    |> Result.bind (fun found ->
                        collect (match found with Some launch -> launch :: held | None -> held) tail)
            collect [] (roster |> List.choose _.Ready)))

/// Read current policy, roster and renderer at one pinned head. Unresolved
/// prior attempts are returned separately from an actually empty Ready queue.
let prepareDetailedAt (readLimits: ReadLimits) (binding: LedgerBinding) snapshot projectName lastKnownOutcome =
    readLimits binding snapshot
    |> Result.bind (fun limits ->
        if not (validLimits limits) then bad "Invalid capacity limits."
        else
            readRosterAt binding snapshot limits
            |> Result.bind (fun roster ->
                let rec choose remaining held =
                    match remaining with
                    | [] ->
                        if List.isEmpty held then Ok EmptyReadyQueue
                        else Ok(HeldLaunches(List.rev held))
                    | ready :: tail ->
                        nextOperationAt binding snapshot ready
                        |> Result.bind (function
                            | None ->
                                heldLaunchAt binding snapshot ready lastKnownOutcome (operationId binding ready)
                                |> Result.bind (fun heldLaunch ->
                                    choose tail (heldLaunch |> Option.map (fun value -> value :: held) |> Option.defaultValue held))
                            | Some id ->
                                RoleInstructions.renderLaunchPromptAt binding snapshot projectName ready.AgentId
                                |> Result.bind (fun baseRendered ->
                                    let rendered = addLaunchId id baseRendered
                                    let args = BrowserArguments.serializeMinimalLaunchPrompt rendered.Prompt
                                    if not (BrowserArguments.fits args) then bad "Rendered launch exceeds argument budget."
                                    else
                                        Ok(ReadyToLaunch
                                            { Ready = ready; Rendered = rendered; Arguments = args
                                              OperationId = id; Owner = Guid.NewGuid(); SendBoundaryAtUtc = None })))
                choose (candidates limits roster) []))

/// Compatibility projection for callers which only need a prepared launch.
/// The registered scheduler uses the detailed result to expose held launches.
let prepareAt readLimits binding snapshot projectName =
    prepareDetailedAt readLimits binding snapshot projectName "Unknown"
    |> Result.map (function
        | ReadyToLaunch prepared -> Some prepared
        | EmptyReadyQueue | HeldLaunches _ -> None)

// Only a fresh, successful publication can grant one send. Recheck current
// eligibility and limits inside the transaction; do not require unchanged
// unrelated Git head or reserve capacity for in-flight submissions.
let intentFor actor scriptPath sourceSha256 routeSha256 (prepared: Prepared) : LedgerIntent =
        { OperationId = prepared.OperationId
          Actor = actor
          Kind = "ScheduledReadyResumeIntent"
          RequestJson =
            JsonSerializer.Serialize(
                {| ready = prepared.Ready; owner = prepared.Owner; scriptPath = scriptPath; sourceSha256 = sourceSha256
                   routeSha256 = routeSha256; promptSha256 = prepared.Rendered.PromptSha256
                   templateVersion = prepared.Rendered.TemplateVersion
                   templateBlobOid = prepared.Rendered.TemplateBlobOid
                   sendBoundaryProtocol = 1 |}) }
let commitIntent (readLimits: ReadLimits) (binding: LedgerBinding) actor scriptPath sourceSha256
                 routeSha256 (prepared: Prepared) =
    let intent = intentFor actor scriptPath sourceSha256 routeSha256 prepared
    // Some integrity/transport failures can occur during confirmation rather
    // than before publication. Once any plan succeeds, conservatively retain
    // the exact original intent unless the ledger positively confirms its result.
    let mutable publicationPossible = false
    let plan snapshot =
        nextOperationAt binding snapshot prepared.Ready
        |> Result.bind (fun next ->
            if next <> Some prepared.OperationId then
                Error(StaleEntityVersion "Resume attempt no longer authorized.")
            else
                readLimits binding snapshot
                |> Result.bind (fun limits ->
                    readRosterAt binding snapshot limits
                    |> Result.bind (fun roster ->
                        if not (List.contains prepared.Ready (candidates limits roster)) then
                            Error(StaleEntityVersion "Ready episode no longer eligible or capacity reached.")
                        else
                            RoleInstructions.renderLaunchPromptAt binding snapshot prepared.Rendered.ProjectName prepared.Ready.AgentId
                            |> Result.bind (fun baseCurrent ->
                                let current = addLaunchId prepared.OperationId baseCurrent
                                if current.PromptSha256 <> prepared.Rendered.PromptSha256
                                   || current.TemplateBlobOid <> prepared.Rendered.TemplateBlobOid
                                   || BrowserArguments.serializeMinimalLaunchPrompt current.Prompt <> prepared.Arguments then
                                    Error(StaleEntityVersion "Launch template changed.")
                                else
                                    publicationPossible <- true
                                    Ok [{ Path = intentPath prepared.OperationId
                                          ExpectedBlobOid = None; Content = intent.RequestJson }]))))
    match Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding intent plan with
    | Ok receipt when receipt.AlreadyApplied ->
        Error(StaleEntityVersion "Original launch intent exists; never replay Send.")
    | Error OperationIdReused -> Error OperationIdReused
    | Error(LedgerFailure.OutcomeUnknown reason) -> Error(LedgerFailure.OutcomeUnknown reason)
    | Error issue when publicationPossible ->
        Error(LedgerFailure.OutcomeUnknown(sprintf "Original publication not positively resolved: %A" issue))
    | result -> result

// Private: only the continuation holding a fresh successful intent receipt,
let private commitSendBoundary binding actor (prepared: Prepared) (receipt: LedgerWrite) sentAt =
    LedgerStore.fetch binding
    |> Result.bind (fun snapshot ->
        LedgerStore.getOperationAt binding snapshot prepared.OperationId
        |> Result.bind (function
            | Some original when original.Type = "ScheduledReadyResumeIntent"
                                 && original.Actor = actor
                                 && original.Path = intentPath prepared.OperationId ->
                readReadyFromIntent original
                |> Result.bind (fun ready ->
                    if ready <> prepared.Ready || protocolVersion original < 1 then
                        bad "Send boundary does not match the original scheduled launch."
                    else
                        inspectLaunchAt binding snapshot prepared.OperationId
                        |> Result.bind (function
                            | Some inspection when inspection.SendBoundaryAtUtc.IsNone
                                                  && inspection.CheckinAtUtc.IsNone
                                                  && inspection.NoCheckinAtUtc.IsNone
                                                  && not inspection.NotInvoked
                                                  && not inspection.OperatorResolved ->
                                let boundaryIntent = sendBoundaryIntent prepared.OperationId original sentAt
                                Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding boundaryIntent (fun current ->
                                    LedgerStore.getOperationAt binding current prepared.OperationId
                                    |> Result.bind (function
                                        | Some currentIntent when currentIntent = original ->
                                            inspectLaunchAt binding current prepared.OperationId
                                            |> Result.bind (function
                                                | Some currentState when currentState.SendBoundaryAtUtc.IsNone
                                                                        && currentState.CheckinAtUtc.IsNone
                                                                        && currentState.NoCheckinAtUtc.IsNone
                                                                        && not currentState.NotInvoked
                                                                        && not currentState.OperatorResolved ->
                                                    stillReadyAt binding current prepared.Ready
                                                    |> Result.bind (fun stillReady ->
                                                        if stillReady then
                                                            Ok [{ Path = sendBoundaryPath prepared.OperationId
                                                                  ExpectedBlobOid = None
                                                                  Content = boundaryIntent.RequestJson }]
                                                        else bad "Ready assignment changed before the send boundary.")
                                                | _ -> bad "Scheduled launch already crossed or resolved its send boundary.")
                                        | _ -> bad "Original scheduled launch intent changed before Send."))
                                |> Result.bind (fun receipt ->
                                    if receipt.AlreadyApplied then bad "Send boundary already exists; never replay Send."
                                    else Ok receipt)
                            | _ -> bad "Scheduled launch is no longer authorized to cross the send boundary."))
            | _ -> bad "Original scheduled launch intent is unavailable or has different ownership."))

// Private: only the continuation holding a fresh successful intent receipt,
// before entering Send or after a positively identified pre-send failure, may
// issue this proof. No recovery path calls it without that evidence.
let private proveNotInvoked binding actor (prepared: Prepared) (receipt: LedgerWrite) =
    let original = receipt.Operation
    let id = prepared.OperationId
    if receipt.AlreadyApplied || original.Actor <> actor ||
       original.OperationId <> id.ToString("D") ||
       original.Type <> "ScheduledReadyResumeIntent" then
        bad "Non-invocation requires fresh original intent ownership."
    else
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            readSendBoundaryAt binding snapshot id original
            |> Result.bind (fun boundary ->
                let boundaryOperation = boundary |> Option.map (fun (op, _, _) -> op)
                let intent = proofIntent id original boundaryOperation
                let content = intent.RequestJson
                let readyResult = readReadyFromIntent original
                readyResult
                |> Result.bind (fun ready ->
                    let checkinResult =
                        match boundary with
                        | Some(boundaryOp, _, _) -> readCheckinAt binding snapshot id original boundaryOp ready
                        | None -> Ok None
                    checkinResult
                    |> Result.bind (fun checkin ->
                        if checkin.IsSome then bad "A launch check-in already confirms execution started."
                        else
                            let noCheckinResult =
                                match boundary with
                                | Some info -> readNoCheckinProofAt binding snapshot id original info
                                | None -> Ok None
                            noCheckinResult
                            |> Result.bind (fun noCheckin ->
                                readNotInvokedAt binding snapshot id original boundary
                                |> Result.bind (fun priorProof ->
                                    readOperatorResolutionAt binding snapshot id original boundary ready
                                    |> Result.bind (fun resolution ->
                                        if noCheckin.IsSome || priorProof.IsSome || resolution.IsSome then
                                            bad "Scheduled launch already has terminal replacement evidence."
                                        else
                                            Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding intent (fun current ->
                                                LedgerStore.getOperationAt binding current id
                                                |> Result.bind (function
                                                    | Some currentIntent when currentIntent = original ->
                                                        readSendBoundaryAt binding current id original
                                                        |> Result.bind (fun currentBoundary ->
                                                            let currentBoundaryOperation = currentBoundary |> Option.map (fun (op, _, _) -> op)
                                                            if currentBoundaryOperation <> boundaryOperation then
                                                                bad "Send-boundary evidence changed before non-invocation proof."
                                                            else
                                                                let currentCheckin =
                                                                    match currentBoundary with
                                                                    | Some(boundaryOp, _, _) -> readCheckinAt binding current id original boundaryOp ready
                                                                    | None -> Ok None
                                                                currentCheckin
                                                                |> Result.bind (fun checkin ->
                                                                    if checkin.IsSome then bad "A launch check-in already confirms execution started."
                                                                    else
                                                                        inspectLaunchAt binding current id
                                                                        |> Result.bind (function
                                                                            | Some state when state.Ready = ready &&
                                                                                              not state.OperatorResolved &&
                                                                                              state.NoCheckinAtUtc.IsNone &&
                                                                                              not state.NotInvoked ->
                                                                                // Proof records what happened to this original intent,
                                                                                // not authorization to dispatch now. STOP must not erase it.
                                                                                Ok [{ Path = proofPath id; ExpectedBlobOid = None; Content = content }]
                                                                            | _ -> bad "Original launch already resolved or changed.")))
                                                    | _ -> bad "Original resume intent changed.")))))))))


/// Distinguish a held occurrence from an irreversible unknown send. The
/// existing scheduler may poll again, but never replays an intent.
type ResumeResult =
    | NoEligibleReady
    | Held of string
    | HeldCandidates of HeldLaunch list
    | Submitted of string
    | PublicationUncertain of operationId: Guid * reason: string
    | OutcomeUnknown of string

type SendFailure =
    /// A positively identified failure before the prompt could be invoked.
    /// The continuation may durably prove non-invocation and authorize a later tick.
    | DefinitelyNotInvoked of string
    /// The browser effect may have occurred; never replay without later proof.
    | SendOutcomeUnknown of string

type ExecutionPorts =
    { ReadLimits: ReadLimits
      CanSend: Ready -> System.Threading.Tasks.Task<Result<bool, string>>
      Send: Prepared -> System.Threading.Tasks.Task<Result<string, SendFailure>> }

/// Executable disjoint seam for the configured scheduler caller. CanSend is
/// the existing authenticated route/enable preflight, not an approval prompt.
/// Pending canonical STOP alerts are inspected non-destructively by readReady.
/// The caller must preserve its original durable claim and suspend/reconcile
/// PublicationUncertain with the exact operation ID, never retry from an
/// absent receipt. An unknown browser result also never grants replay.
/// This is a composition seam, not a registered timer or enable switch.
// The host persists selection before the canonical publication and records
// the subsequent effect boundaries in its exact occurrence journal.
let executeOnceTracked (track: string -> Prepared -> bool) (ports: ExecutionPorts) binding actor projectName scriptPath sourceSha256 routeSha256 =
    task {
        match LedgerStore.fetch binding |> Result.bind (fun snapshot ->
            prepareDetailedAt ports.ReadLimits binding snapshot projectName "Unknown") with
        | Error issue -> return Held(sprintf "%A" issue)
        | Ok EmptyReadyQueue -> return NoEligibleReady
        | Ok(HeldLaunches held) -> return HeldCandidates held
        | Ok(ReadyToLaunch prepared) ->
            let! allowed = ports.CanSend prepared.Ready
            match allowed with
            | Error reason -> return Held reason
            | Ok false -> return Held "Scheduled launch stopped."
            | Ok true when not (track "Selected" prepared) ->
                return Held "Schedule claim lost before publication."
            | Ok true ->
                match commitIntent ports.ReadLimits binding actor scriptPath sourceSha256 routeSha256 prepared with
                | Error(LedgerFailure.OutcomeUnknown reason) ->
                    return PublicationUncertain(prepared.OperationId, reason)
                | Error issue -> return Held(sprintf "%A" issue)
                | Ok receipt ->
                    let hold reason =
                        match proveNotInvoked binding actor prepared receipt with
                        | Ok _ -> Held reason
                        | Error issue -> OutcomeUnknown(sprintf "Non-invocation proof not confirmed: %A" issue)
                    if not (track "Published" prepared) then
                        return hold "Schedule claim lost before Send."
                    else
                        let! current = ports.CanSend prepared.Ready
                        match current with
                        | Error reason -> return hold reason
                        | Ok false -> return hold "Scheduled launch stopped before Send."
                        | Ok true when not (track "EnteringSend" prepared) ->
                            return hold "Schedule claim lost before Send."
                        | Ok true ->
                            let sentAt = DateTimeOffset.UtcNow
                            match commitSendBoundary binding actor prepared receipt sentAt with
                            | Error issue -> return OutcomeUnknown(sprintf "Send boundary not confirmed: %A" issue)
                            | Ok _ ->
                                try
                                    let! sent = ports.Send { prepared with SendBoundaryAtUtc = Some sentAt }
                                    match sent with
                                    | Ok value -> return Submitted value
                                    | Error(DefinitelyNotInvoked reason) -> return hold reason
                                    | Error(SendOutcomeUnknown reason) -> return OutcomeUnknown reason
                                with error ->
                                    return OutcomeUnknown(error.GetType().Name)
    }

let executeOnce ports binding actor projectName scriptPath sourceSha256 routeSha256 =
    executeOnceTracked (fun _ _ -> true) ports binding actor projectName scriptPath sourceSha256 routeSha256
