namespace Server.Services

open System
open System.Threading.Tasks
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open Server

type HubService(users: UserService, tracker: ClientResponseTracker, store: FeedbackStore, logger: ILogger<HubService>) =
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
            store.AddConnectionEvent(
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
            )
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
        let session = users.GetSession(this.UserId)
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
        base.OnDisconnectedAsync(error)

    member this.Connect(registration: ClientRegistration) =
        let result = users.Register(this.UserId, registration, this.Context.ConnectionId)
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

    member this.SendClientResponse(correlationId: string, response: AgentCommandResponse) =
        users.TouchConnection(this.Context.ConnectionId)
        tracker.Complete(correlationId, response)
        Task.CompletedTask

    interface IHubService with
        member this.Connect(registration) = this.Connect(registration)

        member this.SendClientResponse(correlationId, result) = this.SendClientResponse(correlationId, result)
