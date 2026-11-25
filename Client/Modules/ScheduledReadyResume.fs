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
            path.StartsWith("agents/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal))
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

type Prepared =
    { Ready: Ready
      Rendered: RoleInstructions.RenderedLaunchPrompt
      Arguments: string
      OperationId: Guid }

type ReadLimits = LedgerBinding -> LedgerSnapshot -> Result<Limits, LedgerFailure>

/// Read current policy, roster and renderer at one pinned head. A prior intent
/// is never launch permission, regardless of its outcome or missing browser receipt.
let prepareAt (readLimits: ReadLimits) (binding: LedgerBinding) snapshot projectName =
    readLimits binding snapshot
    |> Result.bind (fun limits ->
        if not (validLimits limits) then bad "Invalid capacity limits."
        else
            readRosterAt binding snapshot limits
            |> Result.bind (fun roster ->
                let rec choose = function
                    | [] -> Ok None
                    | ready :: remaining ->
                        let id = operationId binding ready
                        LedgerStore.getOperationAt binding snapshot id
                        |> Result.bind (function
                            | Some _ -> choose remaining
                            | None ->
                                RoleInstructions.renderLaunchPromptAt binding snapshot projectName ready.AgentId
                                |> Result.bind (fun rendered ->
                                    let args = BrowserArguments.serializeMinimalLaunchPrompt rendered.Prompt
                                    if not (BrowserArguments.fits args) then bad "Rendered launch exceeds argument budget."
                                    else Ok(Some { Ready = ready; Rendered = rendered; Arguments = args; OperationId = id })))
                choose (candidates limits roster)))

// Only a fresh, successful publication can grant one send. Recheck current
// eligibility and limits inside the transaction; do not require unchanged
// unrelated Git head or reserve capacity for in-flight submissions.
let commitIntent (readLimits: ReadLimits) (binding: LedgerBinding) actor scriptPath sourceSha256
                 routeSha256 (prepared: Prepared) =
    let intent : LedgerIntent =
        { OperationId = prepared.OperationId
          Actor = actor
          Kind = "ScheduledReadyResumeIntent"
          RequestJson =
            JsonSerializer.Serialize(
                {| ready = prepared.Ready; scriptPath = scriptPath; sourceSha256 = sourceSha256
                   routeSha256 = routeSha256; promptSha256 = prepared.Rendered.PromptSha256
                   templateVersion = prepared.Rendered.TemplateVersion
                   templateBlobOid = prepared.Rendered.TemplateBlobOid |}) }
    if prepared.OperationId <> operationId binding prepared.Ready then bad "Mismatched launch operation identity."
    else
        Client.LegacyDirectWriterFence.LegacyDirectWriterFence.transact binding intent (fun snapshot ->
            readLimits binding snapshot
            |> Result.bind (fun limits ->
                readRosterAt binding snapshot limits
                |> Result.bind (fun roster ->
                    if not (List.contains prepared.Ready (candidates limits roster)) then
                        Error(StaleEntityVersion "Ready episode no longer eligible or capacity reached.")
                    else
                        RoleInstructions.renderLaunchPromptAt binding snapshot prepared.Rendered.ProjectName prepared.Ready.AgentId
                        |> Result.bind (fun current ->
                            if current.PromptSha256 <> prepared.Rendered.PromptSha256
                               || current.TemplateBlobOid <> prepared.Rendered.TemplateBlobOid
                               || BrowserArguments.serializeMinimalLaunchPrompt current.Prompt <> prepared.Arguments then
                                Error(StaleEntityVersion "Launch template changed.")
                            else
                                Ok [{ Path = $"records/scheduled-ready-resume/{prepared.OperationId:N}/intent.json"
                                      ExpectedBlobOid = None; Content = intent.RequestJson }]))))
        |> Result.bind (fun receipt ->
            if receipt.AlreadyApplied then Error(StaleEntityVersion "Original launch intent exists; never replay Send.")
            else Ok receipt)


/// Distinguish a held occurrence from an irreversible unknown send. The
/// existing scheduler may poll again, but never replays an intent.
type ResumeResult =
    | NoEligibleReady
    | Held of string
    | Submitted of string
    | PublicationUncertain of operationId: Guid * reason: string
    | OutcomeUnknown of string

type ExecutionPorts =
    { ReadLimits: ReadLimits
      CanSend: Ready -> System.Threading.Tasks.Task<Result<bool, string>>
      Send: Prepared -> System.Threading.Tasks.Task<Result<string, string>> }

/// Executable disjoint seam for the configured scheduler caller. CanSend is
/// the existing authenticated route/enable preflight, not an approval prompt.
/// Pending canonical STOP alerts are inspected non-destructively by readReady.
/// The caller must preserve its original durable claim and suspend/reconcile
/// PublicationUncertain with the exact operation ID, never retry from an
/// absent receipt. An unknown browser result also never grants replay.
/// This is a composition seam, not a registered timer or enable switch.
let executeOnce (ports: ExecutionPorts) binding actor projectName scriptPath sourceSha256 routeSha256 =
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
            | Ok true ->
                match commitIntent ports.ReadLimits binding actor scriptPath sourceSha256 routeSha256 prepared with
                | Error(LedgerFailure.OutcomeUnknown reason) ->
                    return PublicationUncertain(prepared.OperationId, reason)
                | Error issue -> return Held(sprintf "%A" issue)
                | Ok _ ->
                    let! current = ports.CanSend prepared.Ready
                    match current with
                    | Error reason -> return Held reason
                    | Ok false -> return Held "Scheduled launch stopped before Send."
                    | Ok true ->
                        try
                            let! sent = ports.Send prepared
                            match sent with
                            | Ok value -> return Submitted value
                            | Error reason -> return OutcomeUnknown reason
                        with error ->
                            return OutcomeUnknown(error.GetType().Name)
    }
