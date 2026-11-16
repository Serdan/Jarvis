namespace Server.Services

open System
open System.Threading.Tasks
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open Server

type HubService(users: UserService, tracker: ClientResponseTracker, store: OptionalFeedbackStore, roster: AgentRosterCache, logger: ILogger<HubService>) =
    inherit Hub<IClientService>()

    member private this.UserId =
        match Auth.tryUserId this.Context.User with
        | Some userId -> userId
        | None -> raise (InvalidOperationException("Authenticated SignalR connection is missing a subject claim."))

    member private this.RecordConnectionEvent(
        eventType,
        deviceId,
        deviceName,
        generation,
        clientVersion,
        protocolVersion,
        durationMs,
        error: Exception
    ) =
        try
            store.TryPost(fun db -> db.AddConnectionEvent(
                this.UserId,
                eventType,
                this.Context.ConnectionId,
                deviceId,
                deviceName,
                generation,
                clientVersion,
                protocolVersion,
                durationMs,
                Option.ofObj error |> Option.map (fun ex -> ex.GetType().FullName),
                Option.ofObj error |> Option.map (fun ex -> ex.Message)
            ))
        with ex ->
            logger.LogWarning(
                ex,
                "Failed to persist Jarvis connection diagnostic event {EventType} for {ConnectionId}.",
                eventType,
                this.Context.ConnectionId
            )

    override this.OnConnectedAsync() =
        users.TransportConnected(this.UserId, this.Context.ConnectionId)
        this.RecordConnectionEvent("TransportConnected", None, None, None, None, None, None, null)
        base.OnConnectedAsync()

    override this.OnDisconnectedAsync(error) =
        let transport = users.GetTransportConnection(this.Context.ConnectionId)
        let session = users.GetSessionByConnection(this.Context.ConnectionId)
        let deviceId, deviceName, generation, clientVersion, protocolVersion =
            match session with
            | ValueSome current when current.ConnectionId = Some this.Context.ConnectionId ->
                Some current.DeviceId,
                Some current.DeviceName,
                Some current.Generation,
                Some current.ClientVersion,
                Some current.ProtocolVersion
            | _ ->
                match transport with
                | ValueSome current -> current.RegisteredDeviceId, None, None, None, None
                | ValueNone -> None, None, None, None, None

        let durationMs =
            match transport with
            | ValueSome current ->
                Some(int64 (DateTimeOffset.UtcNow - current.ConnectedAt).TotalMilliseconds)
            | ValueNone -> None

        let reason =
            match error with
            | null -> None
            | ex -> Some ex.Message

        match error with
        | null ->
            logger.LogInformation(
                "Jarvis client connection {ConnectionId} disconnected.",
                this.Context.ConnectionId
            )
        | ex ->
            logger.LogWarning(
                ex,
                "Jarvis client connection {ConnectionId} disconnected with an error.",
                this.Context.ConnectionId
            )

        this.RecordConnectionEvent(
            "TransportDisconnected",
            deviceId,
            deviceName,
            generation,
            clientVersion,
            protocolVersion,
            durationMs,
            error
        )

        users.Disconnect(this.Context.ConnectionId, reason)
        let refreshes =
            roster.Disconnect(this.UserId, this.Context.ConnectionId)
            |> List.map (fun dispatch ->
                this.Clients.Client(dispatch.ConnectionId).RefreshAgentRoster(dispatch.Request))
        let baseDisconnect = base.OnDisconnectedAsync(error)
        Task.WhenAll(seq {
            yield baseDisconnect
            yield! refreshes
        })

    member this.Connect(registration: ClientRegistration) =
        let result = users.Register(this.UserId, registration, this.Context.ConnectionId)
        roster.ForgetDevice(this.UserId, registration.DeviceId)
        this.RecordConnectionEvent(
            "Registered",
            Some registration.DeviceId,
            Some registration.DeviceName,
            Some result.Generation,
            Some registration.ClientVersion,
            Some registration.ProtocolVersion,
            None,
            null
        )
        Task.FromResult result

    // A disposable SQLite watermark is not an activity proof. Until the
    // client/Git ledger supplies an independently durable, fresh cross-device
    // watermark, idle-based launches are explicitly unavailable.
    // Old clients must also fail closed instead of interpreting a reset DB
    // row as an idle interval.
    member this.GetAgentActivity(projectName: string, agentId: string) =
        match users.GetSessionByConnection(this.Context.ConnectionId) with
        | ValueSome session when session.State = Registered
                                 && session.ConnectionId = Some this.Context.ConnectionId ->
            Task.FromException<AgentActivitySnapshot>(
                InvalidOperationException("ActivityProofUnavailable: server SQLite is not authoritative for AfterAgentIdle."))
        | _ ->
            Task.FromException<AgentActivitySnapshot>(
                UnauthorizedAccessException("A registered Jarvis client is required to verify Manager activity."))

    member private this.ActiveRosterSession() =
        match users.GetSessionByConnection(this.Context.ConnectionId) with
        | ValueSome session when session.State = Registered
                                 && session.ConnectionId = Some this.Context.ConnectionId -> session
        | _ -> raise (UnauthorizedAccessException("A registered Jarvis client is required for roster data."))

    member private this.DispatchRoster(refreshes: RosterDispatch list) =
        refreshes
        |> List.map (fun dispatch ->
            this.Clients.Client(dispatch.ConnectionId).RefreshAgentRoster(dispatch.Request))
        |> Task.WhenAll

    member this.AnnounceAgentLedgers(claims: AgentRosterClaim array) =
        let session = this.ActiveRosterSession()
        roster.Advertise(this.UserId, this.Context.ConnectionId, session.DeviceId,
                         session.DeviceName, session.Generation, claims)
        |> this.DispatchRoster

    member this.SubmitAgentRoster(response: AgentRosterRefreshResponse) =
        this.ActiveRosterSession() |> ignore
        match roster.Complete(this.UserId, this.Context.ConnectionId, response) with
        | Some dispatch -> this.DispatchRoster [dispatch]
        | None -> Task.CompletedTask

    member this.GetAgentRoster() =
        this.ActiveRosterSession() |> ignore
        task {
            let refreshes = roster.RefreshDue(this.UserId)
            if not refreshes.IsEmpty then
                try
                    do! this.DispatchRoster refreshes
                with ex ->
                    logger.LogWarning(ex, "Optional Agent roster refresh dispatch failed")
            return roster.View(this.UserId)
        }

    member this.SendClientResponse(correlationId: string, response: AgentCommandResponse) =
        users.TouchConnection(this.Context.ConnectionId)
        match users.GetSessionByConnection(this.Context.ConnectionId) with
        | ValueSome session when session.State = Registered && session.ConnectionId = Some this.Context.ConnectionId ->
            if not (tracker.Complete(correlationId, this.UserId, this.Context.ConnectionId, session.Generation, response)) then
                logger.LogWarning("Rejected unexpected or stale Jarvis response {CorrelationId} from {ConnectionId}.", correlationId, this.Context.ConnectionId)
        | _ ->
            logger.LogWarning("Rejected Jarvis response {CorrelationId} from inactive connection {ConnectionId}.", correlationId, this.Context.ConnectionId)
        Task.CompletedTask

    interface IHubService with
        member this.Connect(registration) = this.Connect(registration)

        member this.GetAgentActivity(projectName, agentId) = this.GetAgentActivity(projectName, agentId)

        member this.AnnounceAgentLedgers(claims) = this.AnnounceAgentLedgers(claims)

        member this.SubmitAgentRoster(response) = this.SubmitAgentRoster(response)

        member this.GetAgentRoster() = this.GetAgentRoster()

        member this.SendClientResponse(correlationId, result) = this.SendClientResponse(correlationId, result)