namespace Server.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Core

// Bounded-cardinality production metrics for every dispatch. The correlation
// ID is deliberately NOT a metric label; only the finite command and outcome
// domains are tags. Slow requests are correlated through structured logs.
module private ServerDispatchTelemetry =
    let meter = new Meter("Jarvis.Server.Dispatch")
    let route = meter.CreateHistogram<float>("jarvis.server.dispatch.route.ms", "ms")
    let send = meter.CreateHistogram<float>("jarvis.server.dispatch.signalr_send.ms", "ms")
    let reply = meter.CreateHistogram<float>("jarvis.server.dispatch.reply_wait.ms", "ms")
    let total = meter.CreateHistogram<float>("jarvis.server.dispatch.total.ms", "ms")

    let record commandName outcome routeMs sendMs replyMs totalMs =
        let tags =
            [| KeyValuePair<string, obj>("command", box commandName)
               KeyValuePair<string, obj>("outcome", box outcome) |]
        route.Record(routeMs, tags)
        send.Record(sendMs, tags)
        reply.Record(replyMs, tags)
        total.Record(totalMs, tags)

module ProjectNotificationRouting =
    // The client's LedgerApi accepts "Health", not the public MCP tool name
    // "LedgerHealth". Keep the internal routing probe explicit and tested.
    let healthProbe projectName =
        LedgerReadCommand { ProjectName = projectName; Action = "Health"; OperationId = ""; Cursor = ""; PageSize = 0 }

    /// A promoted health check has not validated this device yet. Resolve the
    /// EXACT client's original job, never issue a second health probe or accept
    /// the handle as proof of a verified destination ledger.
    let resolveHealth (projectName: string) (expectedActor: string)
                      (deadline: DateTimeOffset) (initial: AgentCommandResponse)
                      (poll: GetJobResultCommand -> Task<AgentCommandResponse>) =
        let failure detail : AgentCommandResponse =
            { Result = None
              Error = Some(ExecutionFailed("Ledger health not verified: " + detail))
              Instructions = []
              AgentStateChanges = []
              InboxActivation = None
              PendingJob = None }
        let rec resolve (current: AgentCommandResponse) =
            task {
                match current.PendingJob with
                | None -> return current
                | Some handle when handle.Kind <> JobKind.ToolOperation
                                   || handle.ProjectName <> Some projectName
                                   || handle.AgentId <> expectedActor ->
                    return failure "pending handle does not belong to the verified project and initiating actor."
                | Some handle when DateTimeOffset.UtcNow >= deadline ->
                    return failure "pending health job did not finish before the bounded routing deadline."
                | Some handle ->
                    let! returned =
                        poll { ProjectName = projectName
                               JobId = handle.JobId
                               AfterSequence = None }
                    match returned.Result, returned.Error, returned.PendingJob with
                    | Some payload, None, None ->
                        try
                            let job = JsonSerializer.Deserialize<JobResult>(payload)
                            if isNull (box job) || job.JobId <> handle.JobId then
                                return failure "poll returned a different or malformed job."
                            else
                                match job.ToolResponse, job.Status with
                                | Some terminal, ToolCompleted when terminal.PendingJob.IsNone ->
                                    return terminal
                                | Some terminal, ToolFailed _ when terminal.PendingJob.IsNone && terminal.Error.IsSome ->
                                    return terminal
                                | None, Running ->
                                    return! resolve current
                                | _ ->
                                    return failure "health job ended without a terminal verified result."
                        with _ -> return failure "health job returned an invalid typed response."
                    | _ -> return failure "health job could not be read from its original device."
            }
        resolve initial

    let ordered (selected: ClientSessionStatus voption) (sessions: ClientSessionStatus list)
                (explicitDevice: bool) =
        if explicitDevice then
            match selected with
            | ValueSome session -> [ session ]
            | ValueNone -> []
        else
            match selected with
            | ValueSome session ->
                session :: (sessions |> List.filter (fun item -> item.DeviceId <> session.DeviceId))
            | ValueNone -> sessions

    let rejectionReason commandName requireCurrentProtocol (session: ClientSessionStatus) =
        if session.State <> Registered || session.ConnectionId.IsNone then Some "disconnected"
        elif not (List.contains "LedgerRead" session.SupportedCommands) then Some "missing LedgerRead support"
        elif not (List.contains commandName session.SupportedCommands) then Some $"missing {commandName} support"
        elif requireCurrentProtocol && session.ProtocolVersion <> AgentProtocol.version then
            Some $"protocol {session.ProtocolVersion} (requires {AgentProtocol.version})"
        else None

    let candidates selected sessions explicitDevice commandName requireCurrentProtocol =
        ordered selected sessions explicitDevice
        |> List.filter (fun item -> rejectionReason commandName requireCurrentProtocol item |> Option.isNone)

    // Avoid propagating arbitrary client error strings, which may include local paths.
    let probeFailure (response: AgentCommandResponse) =
        match response.Error with
        | Some(NotFound _) -> "destination project or ledger binding not found"
        | Some(PermissionDenied _) -> "permission denied"
        | Some(ValidationFailed _) -> "ledger binding or project validation failed"
        | Some(Conflict _) -> "ledger health conflict"
        | Some(ExecutionFailed _) -> "ledger health execution failed or timed out"
        | Some(UncertainOutcome _) -> "ledger health outcome is uncertain"
        | Some(OutputTruncated _) -> "ledger health response truncated"
        | Some(ConfirmationRequired _) -> "unexpected ledger health confirmation"
        | Some(LedgerFailure failure) -> $"ledger health failed ({failure.Code})"
        | None -> "ledger health returned no successful result"

    let failureMessage projectName commandName requireCurrentProtocol explicitDevice
                       (sessions: ClientSessionStatus list) (probes: (string * string) list) =
        let excluded =
            sessions |> List.choose (fun session ->
                rejectionReason commandName requireCurrentProtocol session
                |> Option.map (fun reason -> $"{session.DeviceName}: {reason}"))
        let details = excluded @ (probes |> List.map (fun (name, reason) -> $"{name}: {reason}"))
        let description =
            if List.isEmpty details then
                if explicitDevice then "No selected/explicit device is available."
                else "No registered client is available."
            else
                let prefix = details |> List.truncate 8 |> String.concat "; "
                if details.Length > 8 then $"{prefix}; and {details.Length - 8} more" else prefix
        $"Cannot route cross-project notification for '{projectName}': {description} " +
        "Check the destination ledger binding and connected client capabilities."


module AgentStateInvalidationRouting =
    // A state-changing tool completes against one selected client. Invalidate
    // every connected device for the authenticated user, but never other users.
    let recipients (sessions: ClientSessionStatus array) =
        sessions
        |> Array.choose (fun session ->
            if session.State = Registered then session.ConnectionId else None)
        |> Array.distinct

    let affectedByProject (mutations: AgentStateChange list) =
        if isNull (box mutations) then []
        else
            mutations
            |> List.filter (fun change ->
                not (String.IsNullOrWhiteSpace change.ProjectName)
                && not (String.IsNullOrWhiteSpace change.AgentId))
            |> List.groupBy (fun change -> change.ProjectName)
            |> List.map (fun (project, changes) ->
                project, (changes |> List.map _.AgentId |> List.distinct |> List.toArray))


type ClientService(ctx: IHubContext<HubService, IClientService>, users: UserService, tracker: ClientResponseTracker, http: IHttpContextAccessor, logger: ILogger<ClientService>) =
    member this.SendMessageToAll(message) = ctx.Clients.All.ReceiveMessage(message)

    // Typed, per-agent invalidation only reaches authenticated registered
    // devices for this user; no canonical agent state lives on the server.
    member _.InvalidateAgentStatesForUser(userId: string, projectName: string, agentIds: string array) : Task =
        if isNull agentIds || agentIds.Length = 0 then Task.CompletedTask
        else
            let unique = agentIds |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.distinct
            users.ListDevices(userId)
            |> AgentStateInvalidationRouting.recipients
            |> Array.map (fun connectionId ->
                ctx.Clients.Client(connectionId).InvalidateAgentStates(projectName, unique))
            |> Task.WhenAll

    // A client exception after a possible mutation cannot identify all
    // affected agents. Invalidate every cached agent for THIS authenticated
    // user's registered devices, rather than declaring zero changes.
    member _.InvalidateAllAgentStatesForUser(userId: string) : Task =
        users.ListDevices(userId)
        |> AgentStateInvalidationRouting.recipients
        |> Array.map (fun connectionId ->
            ctx.Clients.Client(connectionId).InvalidateAllAgentStates())
        |> Task.WhenAll

    member private this.ResponseTimeout(command: AgentCommand) =
        let permissionMargin = TimeSpan.FromMinutes(5L)
        match command with
        | RunCommandCommand cmd ->
            let executionSeconds = cmd.TimeoutSeconds |> Option.defaultValue 60 |> max 1
            TimeSpan.FromSeconds(float executionSeconds) + permissionMargin
        | _ -> TimeSpan.FromMinutes(10L)

    member this.SendCommandToUser(userId: string, command: AgentCommand, ?conversationId: string, ?deviceId: string, ?agentId: string) =
        task {
            let requestAcceptedAt = Stopwatch.GetTimestamp()
            let requestAborted =
                match http.HttpContext with
                | null -> CancellationToken.None
                | context -> context.RequestAborted
            requestAborted.ThrowIfCancellationRequested()
            // A verified cross-project source activation precedes destination
            // dispatch. It remains known if this transport later times out.
            let sourceChanges, sourceActivation =
                AgentCommandInfo.confirmedSourceEvidence command
            match users.GetSelectedSession(userId, conversationId, deviceId) with
            | ValueNone when deviceId.IsSome ->
                let requested = deviceId.Value
                let error =
                    if String.IsNullOrWhiteSpace requested || requested.Length > 256 then
                        ValidationFailed "deviceId must be a nonempty device ID of at most 256 characters."
                    else
                        NotFound $"Jarvis device '{requested}' is not registered for the authenticated user. Use ListDevices to find available devices."
                return { Result = None; Error = Some error; Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
            | ValueNone ->
                return
                    { Result = None
                      Error = Some(NotFound "No Jarvis client has registered for the authenticated user.")
                      Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
            | ValueSome session ->
                match session.State, session.ConnectionId with
                | Disconnected, _ ->
                    let reason = session.LastFailure |> Option.defaultValue "Transport disconnected"
                    return
                        { Result = None
                          Error =
                            Some(
                                ExecutionFailed
                                    $"Jarvis device {session.DeviceName} is disconnected (generation {session.Generation}, protocol {session.ProtocolVersion}, last seen {session.LastSeenAt:O}). Reason: {reason}"
                            )
                          Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
                | Registered, None ->
                    return
                        { Result = None
                          Error =
                            Some(
                                ExecutionFailed
                                    $"Jarvis device {session.DeviceName} generation {session.Generation} is registered without an active connection."
                            )
                          Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
                | Registered, Some connectionId ->
                    let commandName = AgentCommandInfo.protocolName command

                    if not (session.SupportedCommands |> List.contains commandName) then
                        return
                            { Result = None
                              Error =
                                Some(
                                    ExecutionFailed
                                        $"Jarvis device {session.DeviceName} does not advertise support for {commandName}. Client {session.ClientVersion} uses protocol {session.ProtocolVersion}; the server uses protocol {AgentProtocol.version}. Update Jarvis to a compatible version and reconnect."
                                )
                              Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
                    else
                        let correlationId, trackingTask = tracker.Register(userId, connectionId, session.Generation, this.ResponseTimeout command)
                        let client = ctx.Clients.Client(connectionId)
                        let commandJson = JsonSerializer.Serialize<AgentCommand>(command)
                        let routeMs = Stopwatch.GetElapsedTime(requestAcceptedAt).TotalMilliseconds
                        let mutable sendMs = 0.0
                        let mutable replyMs = 0.0
                        let report outcome =
                            let totalMs = Stopwatch.GetElapsedTime(requestAcceptedAt).TotalMilliseconds
                            // Telemetry must never alter mutation semantics or hide
                            // an actual response because a logging sink failed.
                            try
                                ServerDispatchTelemetry.record commandName outcome routeMs sendMs replyMs totalMs
                                if totalMs >= 2000.0 || outcome <> "completed" then
                                    logger.LogInformation(
                                        "MCP dispatch {CorrelationId} {Command} {Outcome}: routeMs={RouteMs:F1} signalrSendMs={SendMs:F1} replyWaitMs={ReplyMs:F1} totalMs={TotalMs:F1}",
                                        correlationId, commandName, outcome, routeMs, sendMs, replyMs, totalMs)
                            with _ ->
                                ()

                        let mutable replyStartedAt = None
                        let dispatchAt = Stopwatch.GetTimestamp()
                        try
                            do! client.ReceiveCommand(correlationId, commandJson, defaultArg agentId "").WaitAsync(requestAborted)
                            sendMs <- Stopwatch.GetElapsedTime(dispatchAt).TotalMilliseconds
                            users.RecordDispatchSuccess(userId, connectionId)

                            let awaitReplyAt = Stopwatch.GetTimestamp()
                            replyStartedAt <- Some awaitReplyAt
                            try
                                let! response = trackingTask.WaitAsync(requestAborted)
                                replyMs <- Stopwatch.GetElapsedTime(awaitReplyAt).TotalMilliseconds
                                report "completed"
                                return response
                            with :? TaskCanceledException when not requestAborted.IsCancellationRequested ->
                                replyMs <- Stopwatch.GetElapsedTime(awaitReplyAt).TotalMilliseconds
                                let outcome =
                                    match users.GetSessionByConnection(connectionId) with
                                    | ValueSome active when active.State = Registered && active.Generation = session.Generation -> "tracker-timeout"
                                    | _ -> "disconnected"
                                report outcome
                                let reason =
                                    $"Client response timeout for {session.DeviceName} generation {session.Generation}."
                                users.RecordDispatchFailure(userId, connectionId, reason)
                                return
                                    { Result = None
                                      Error =
                                        Some(
                                            UncertainOutcome
                                                { Message = $"{reason} Protocol {session.ProtocolVersion}, client {session.ClientVersion}, last seen {session.LastSeenAt:O}."
                                                  OperationId = AgentCommandInfo.recoveryOperationId command }
                                        )
                                      Instructions = []; AgentStateChanges = sourceChanges
                                      InboxActivation = sourceActivation; PendingJob = None }
                        with ex ->
                            match replyStartedAt with
                            | Some started when replyMs = 0.0 ->
                                replyMs <- Stopwatch.GetElapsedTime(started).TotalMilliseconds
                            | None ->
                                sendMs <- Stopwatch.GetElapsedTime(dispatchAt).TotalMilliseconds
                            | _ -> ()
                            tracker.Cancel(correlationId)
                            if requestAborted.IsCancellationRequested then
                                report "upstream-abort"
                                // The caller is gone, not the device. Keep
                                // the client session healthy and remove the
                                // now-unobservable correlation promptly.
                                return raise (OperationCanceledException(requestAborted))
                            else
                                report "signalr-send-failure"
                                let reason =
                                    $"Dispatch failed for {session.DeviceName} generation {session.Generation}: {ex.Message}"
                                users.RecordDispatchFailure(userId, connectionId, reason)
                                return
                                    { Result = None
                                      Error =
                                        Some(UncertainOutcome
                                            { Message = reason
                                              OperationId = AgentCommandInfo.recoveryOperationId command })
                                      Instructions = []; AgentStateChanges = sourceChanges
                                      InboxActivation = sourceActivation; PendingJob = None }
        }


    // Only used by cross-project notifications. Probe connected clients for the
    // destination's verified ledger binding instead of assuming the sender's
    // selected device has the destination project.
    member this.SendCommandToProject(userId: string, projectName: string, command: AgentCommand,
                                     ?conversationId: string, ?deviceId: string, ?agentId: string) =
        task {
            let selected = users.GetSelectedSession(userId, conversationId, deviceId)
            let sessions = users.ListDevices(userId) |> Array.toList
            let commandName = AgentCommandInfo.protocolName command
            let requireCurrentProtocol =
                match command with
                | TaskWriteCommand write when write.Action = "NotifyAgent"
                      && not (String.IsNullOrWhiteSpace write.SourceRef) -> true
                | CrossProjectNotificationCommand _ -> true
                | _ -> false
            let ordered = ProjectNotificationRouting.ordered selected sessions deviceId.IsSome
            let eligible =
                ProjectNotificationRouting.candidates selected sessions deviceId.IsSome
                    commandName requireCurrentProtocol
            let rec dispatch (remaining: ClientSessionStatus list) (probeFailures: (string * string) list) =
                task {
                    match remaining with
                    | [] ->
                        return { Result = None
                                 Error = Some(NotFound(ProjectNotificationRouting.failureMessage projectName commandName
                                      requireCurrentProtocol deviceId.IsSome ordered (List.rev probeFailures)))
                                 Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
                    | session :: rest ->
                        let! health = this.SendCommandToUser(
                            userId,
                            ProjectNotificationRouting.healthProbe projectName,
                            ?conversationId = conversationId, deviceId = session.DeviceId, ?agentId = agentId)
                        let! verified =
                            ProjectNotificationRouting.resolveHealth projectName (defaultArg agentId "")
                                (DateTimeOffset.UtcNow.AddMinutes(2.)) health
                                (fun query ->
                                    task {
                                        do! Task.Delay(250)
                                        return! this.SendCommandToUser(
                                            userId, GetJobResultCommand query,
                                            ?conversationId = conversationId,
                                            deviceId = session.DeviceId, ?agentId = agentId)
                                    })
                        let stillSameDevice =
                            match users.GetSessionByDevice(userId, session.DeviceId) with
                            | ValueSome current ->
                                current.State = Registered
                                && current.ConnectionId = session.ConnectionId
                                && current.Generation = session.Generation
                            | ValueNone -> false
                        match verified.Result, verified.Error, verified.PendingJob, stillSameDevice with
                        | Some _, None, None, true ->
                            // A verified ledger binding on the same connected
                            // device is required before any destination write.
                            // Once dispatched, NEVER retry a potentially
                            // committed mutation against another client.
                            let! destination =
                                this.SendCommandToUser(userId, command,
                                    ?conversationId = conversationId, deviceId = session.DeviceId, ?agentId = agentId)
                            // Bind the pending handle to this verified device.
                            // A later poll MUST NOT fall back to the user's current default device.
                            return
                                match destination.PendingJob with
                                | None -> destination
                                | Some handle ->
                                    let routed = { handle with DeviceId = Some session.DeviceId }
                                    { destination with
                                        PendingJob = Some routed
                                        Result = Some(JsonSerializer.Serialize routed) }
                        | _ ->
                            let reason =
                                if not stillSameDevice then "candidate device session changed during ledger verification"
                                else ProjectNotificationRouting.probeFailure verified
                            return! dispatch rest ((session.DeviceName, reason) :: probeFailures)
                }
            return! dispatch eligible []
        }
