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
    if agent.State <> "Ready" || agent.Execution <> "Stopped" then Ok None
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
// from a positive, immutable pre-Send terminal proof of the preceding intent.
let private derivedId (domain: string) (prior: Guid) =
    let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(domain + ":" + prior.ToString("N")))[..15]
    bytes[7] <- (bytes[7] &&& 0x0Fuy) ||| 0x50uy
    bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
    Guid bytes

let private proofId = derivedId "ScheduledReadyResume:NotInvoked:v1"
let private nextId = derivedId "ScheduledReadyResume:NextAttempt:v1"
let private sendBoundaryId = derivedId "ScheduledReadyResume:SendBoundary:v1"
let private checkinId = derivedId "ScheduledReadyResume:Checkin:v1"
let private noCheckinProofId = derivedId "ScheduledReadyResume:NoCheckin:v1"
let private intentPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/intent.json"
let private proofPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/not-invoked.json"
let private sendBoundaryPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/send-boundary.json"
let private checkinPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/checkin.json"
let private noCheckinProofPath (id: Guid) = $"records/scheduled-ready-resume/{id:N}/no-checkin.json"

let private launchDeadline (sentAt: DateTimeOffset) = sentAt.AddMinutes(5.0)

let private sendBoundaryContent (intent: LedgerOperation) (sentAt: DateTimeOffset) =
    JsonSerializer.Serialize(
        {| launchId = intent.OperationId; intentOperationId = intent.OperationId
           intentRequestHash = intent.RequestHash; intentSequence = intent.Sequence
           sendBoundaryAtUtc = sentAt.ToUniversalTime(); checkinDeadlineUtc = launchDeadline sentAt |})

let private sendBoundaryIntent id (original: LedgerOperation) sentAt : LedgerIntent =
    { OperationId = sendBoundaryId id; Actor = original.Actor
      Kind = "ScheduledReadyResumeSendBoundary"
      RequestJson = sendBoundaryContent original sentAt }

let private checkinContent (intent: LedgerOperation) (boundary: LedgerOperation) agentId checkedInAt =
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
                                 checkedAt (lastSeenAt: DateTimeOffset) (serverNow: DateTimeOffset) snapshot =
    use boundaryJson = JsonDocument.Parse(boundary.RequestJson)
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
                                     (ready: Ready) actor rationale resolvedAt snapshot =
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
        | Some content, Some _ when content = operation.RequestJson -> Ok ()
        | _ -> bad $"Immutable evidence file does not match operation {operation.OperationId}.")

let private readReadyFromIntent (intent: LedgerOperation) =
    try
        use doc = JsonDocument.Parse(intent.RequestJson)
        let ready = doc.RootElement.GetProperty("ready")
        Ok
            { AgentId = ready.GetProperty("AgentId").GetString()
              StateSequence = ready.GetProperty("StateSequence").GetInt64()
              TaskId = ready.GetProperty("TaskId").GetString()
              AttemptId = ready.GetProperty("AttemptId").GetString()
              Generation = ready.GetProperty("Generation").GetInt32() }
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
                use doc = JsonDocument.Parse(boundary.RequestJson)
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
                use doc = JsonDocument.Parse(checkin.RequestJson)
                let checkedAt = doc.RootElement.GetProperty("checkedInAtUtc").GetDateTimeOffset()
                let expected = checkinIntent id intent boundary ready.AgentId checkedAt
                LedgerStore.verifyIntegrationOperationIntent binding checkin expected
                |> Result.bind (fun () -> readContentAt binding snapshot (checkinPath id) checkin)
                |> Result.bind (fun () ->
                    use boundaryDoc = JsonDocument.Parse(boundary.RequestJson)
                    let deadline = boundaryDoc.RootElement.GetProperty("checkinDeadlineUtc").GetDateTimeOffset()
                    if checkedAt >= deadline then bad "A check-in at or after the deadline is not valid."
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
                use doc = JsonDocument.Parse(proof.RequestJson)
                let root = doc.RootElement
                let checkedAt = root.GetProperty("checkedAtUtc").GetDateTimeOffset()
                let lastSeen = root.GetProperty("activityLastSeenAtUtc").GetDateTimeOffset()
                let serverNow = root.GetProperty("activityServerNowAtUtc").GetDateTimeOffset()
                let evidenceSnapshot =
                    { HeadCommit = root.GetProperty("evidenceSnapshotCommit").GetString()
                      Sequence = root.GetProperty("evidenceSnapshotSequence").GetInt64() }
                let expected = noCheckinProofIntent id intent boundaryOperation checkedAt lastSeen serverNow evidenceSnapshot
                if deadline <> launchDeadline sentAt || serverNow < deadline || checkedAt < deadline || lastSeen > serverNow then
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
                use doc = JsonDocument.Parse(resolution.RequestJson)
                let root = doc.RootElement
                let rationale = root.GetProperty("rationale").GetString()
                let resolvedAt = root.GetProperty("resolvedAtUtc").GetDateTimeOffset()
                let evidenceSnapshot =
                    { HeadCommit = root.GetProperty("evidenceSnapshotCommit").GetString()
                      Sequence = root.GetProperty("evidenceSnapshotSequence").GetInt64() }
                let boundaryOperation = boundary |> Option.map (fun (op, _, _) -> op)
                let expected = operatorResolutionIntent id intent boundaryOperation ready resolution.Actor rationale resolvedAt evidenceSnapshot
                if not (root.GetProperty("executionEndedConfirmed").GetBoolean())
                   || String.IsNullOrWhiteSpace rationale then bad "Operator resolution lacks explicit end confirmation and rationale."
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
      CheckinAtUtc: DateTimeOffset option
      NoCheckinAtUtc: DateTimeOffset option
      NotInvoked: bool
      OperatorResolved: bool }

let private protocolVersion (intent: LedgerOperation) =
    try
        use doc = JsonDocument.Parse(intent.RequestJson)
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
                                          SendBoundaryAtUtc = None; CheckinAtUtc = None; NoCheckinAtUtc = None
                                          NotInvoked = notInvoked.IsSome; OperatorResolved = resolution.IsSome }))
                        | Some(boundaryOperation, sentAt, deadline) ->
                            readCheckinAt binding snapshot id intent boundaryOperation ready
                            |> Result.bind (fun checkin ->
                                readNoCheckinProofAt binding snapshot id intent (boundaryOperation, sentAt, deadline)
                                |> Result.bind (fun noCheckin ->
                                    readOperatorResolutionAt binding snapshot id intent boundary ready
                                    |> Result.bind (fun resolution ->
                                        if checkin.IsSome && (noCheckin.IsSome || notInvoked.IsSome || resolution.IsSome) then
                                            bad "Conflicting terminal evidence for scheduled launch."
                                        elif noCheckin.IsSome && (notInvoked.IsSome || resolution.IsSome) then
                                            bad "Conflicting replacement authorizations for scheduled launch."
                                        elif notInvoked.IsSome && resolution.IsSome then
                                            bad "Conflicting replacement authorizations for scheduled launch."
                                        else
                                            let noCheckinAt =
                                                noCheckin
                                                |> Option.map (fun proof ->
                                                    use proofDoc = JsonDocument.Parse(proof.RequestJson)
                                                    proofDoc.RootElement.GetProperty("checkedAtUtc").GetDateTimeOffset())
                                            Ok(Some
                                                { Ready = ready; ProtocolVersion = protocolVersion intent
                                                  SendBoundaryAtUtc = Some sentAt
                                                  CheckinAtUtc = checkin |> Option.map snd
                                                  NoCheckinAtUtc = noCheckinAt
                                                  NotInvoked = notInvoked.IsSome
                                                  OperatorResolved = resolution.IsSome })))))))
        | Some _ -> bad "Mismatched scheduled-resume intent receipt.")

/// An absent proof does not grant permission. The next candidate is derived
/// only from a verified non-invocation, no-check-in, or explicit operator proof.
let private nextOperationAt binding snapshot ready =
    let rec walk id =
        inspectLaunchAt binding snapshot id
        |> Result.bind (function
            | None -> Ok(Some id)
            | Some inspection when inspection.CheckinAtUtc.IsSome -> Ok None
            | Some inspection when inspection.NotInvoked || inspection.NoCheckinAtUtc.IsSome || inspection.OperatorResolved ->
                walk (nextId id)
            | Some _ -> Ok None)
    walk (operationId binding ready)

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

/// Recheck the exact Ready episode and non-consuming STOP queue at the
/// latest verified snapshot immediately before the browser effect.
let stillReadyAt binding snapshot (ready: Ready) =
    ReviewWorkflow.getAgentWorkingTurnAt binding snapshot ready.AgentId
    |> Result.bind (readReady binding snapshot)
    |> Result.map (fun current -> current = Some ready)

/// The configured recurring schedule is the explicit default-off route.
/// Require a main project selector and the provider's supported empty start
/// surface; never pass arbitrary route metadata into the launcher.
let validateRoute (action: BrowserSchedules.ScheduledAction) =
    if not (BrowserSchedules.isReadyResume action) ||
       action.TriggerKind <> "Every" || action.RepeatEverySeconds <= 0 ||
       String.IsNullOrWhiteSpace(action.ProjectName) ||
       action.ProjectName.Contains("::", StringComparison.Ordinal) ||
       String.IsNullOrWhiteSpace(action.WindowRegistration) || not action.CloseTabAfterExecution then
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
      CheckinStatus: string }

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
    | LaunchSuperseded of string

type OperatorResolutionResult =
    | OperatorResolutionRecorded of DateTimeOffset
    | OperatorResolutionAlreadyRecorded of DateTimeOffset

type Preparation =
    | NoEligibleReady
    | ReadyToLaunch of Prepared
    | HeldLaunches of HeldLaunch list

let private heldLaunchAt binding snapshot ready lastKnownOutcome =
    let id = operationId binding ready
    inspectLaunchAt binding snapshot id
    |> Result.map (function
        | None -> None
        | Some inspection when inspection.CheckinAtUtc.IsSome
                              || inspection.NotInvoked
                              || inspection.NoCheckinAtUtc.IsSome
                              || inspection.OperatorResolved -> None
        | Some inspection ->
            let status =
                match inspection.SendBoundaryAtUtc with
                | Some _ -> "awaitingCheckin"
                | None when inspection.ProtocolVersion >= 1 -> "sendBoundaryRecoveryRequired"
                | None -> "operatorResolutionRequired"
            Some
                { AgentId = ready.AgentId
                  TaskId = ready.TaskId
                  AttemptId = ready.AttemptId
                  AssignmentGeneration = ready.Generation
                  LaunchId = id
                  LastKnownOutcome = lastKnownOutcome
                  SendBoundaryAtUtc = inspection.SendBoundaryAtUtc
                  CheckinDeadlineAtUtc = inspection.SendBoundaryAtUtc |> Option.map launchDeadline
                  CheckinStatus = status })

/// Read current policy, roster and renderer at one pinned head. Unresolved
/// prior attempts are returned separately from an actually empty Ready queue.
let prepareAt (readLimits: ReadLimits) (binding: LedgerBinding) snapshot projectName lastKnownOutcome =
    readLimits binding snapshot
    |> Result.bind (fun limits ->
        if not (validLimits limits) then bad "Invalid capacity limits."
        else
            readRosterAt binding snapshot limits
            |> Result.bind (fun roster ->
                let rec choose remaining held =
                    match remaining with
                    | [] ->
                        if List.isEmpty held then Ok NoEligibleReady
                        else Ok(HeldLaunches(List.rev held))
                    | ready :: tail ->
                        nextOperationAt binding snapshot ready
                        |> Result.bind (function
                            | None ->
                                heldLaunchAt binding snapshot ready lastKnownOutcome
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
                choose (candidates limits roster) [])

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
                                | Some _ -> readNoCheckinProofAt binding snapshot id original boundary
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
                                                                        stillReadyAt binding current ready
                                                                        |> Result.bind (fun _ ->
                                                                            Ok [{ Path = proofPath id; ExpectedBlobOid = None; Content = content }])))
                                                    | _ -> bad "Original resume intent changed."))))))))


/// Distinguish a held occurrence from an irreversible unknown send. The
/// existing scheduler may poll again, but never replays an intent.
type ResumeResult =
    | NoEligibleReady
    | Held of string
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
            prepareAt ports.ReadLimits binding snapshot projectName) with
        | Error issue -> return Held(sprintf "%A" issue)
        | Ok None -> return NoEligibleReady
        | Ok(Some prepared) ->
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
                          try
                              let! sent = ports.Send prepared
                              match sent with
                              | Ok value -> return Submitted value
                              | Error(DefinitelyNotInvoked reason) -> return hold reason
                              | Error(SendOutcomeUnknown reason) -> return OutcomeUnknown reason
                          with error ->
                              return OutcomeUnknown(error.GetType().Name)
    }

let executeOnce ports binding actor projectName scriptPath sourceSha256 routeSha256 =
    executeOnceTracked (fun _ _ -> true) ports binding actor projectName scriptPath sourceSha256 routeSha256
