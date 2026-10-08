namespace Server.Services

open System
open System.Collections.Generic
open Common
open Common.SignalR

type ClientSessionState =
    | Registered
    | Disconnected

type TransportConnectionStatus =
    { ConnectionId: string
      UserId: string
      ConnectedAt: DateTimeOffset
      RegisteredDeviceId: string option }

type ClientSessionStatus =
    { UserId: string
      DeviceId: string
      DeviceName: string
      ConnectionId: string option
      State: ClientSessionState
      Generation: int64
      ProtocolVersion: string
      ClientVersion: string
      SupportedCommands: string list
      RegisteredAt: DateTimeOffset
      LastSeenAt: DateTimeOffset
      DisconnectedAt: DateTimeOffset option
      LastFailure: string option
      RegistrationReason: string }

type UserService() =
    let gate = obj()
    let sessions = Dictionary<struct (string * string), ClientSessionStatus>()
    let defaults = Dictionary<string, string>(StringComparer.Ordinal)
    let connections = Dictionary<string, TransportConnectionStatus>(StringComparer.Ordinal)

    let tryGet key (dictionary: Dictionary<'key, 'a>) =
        match dictionary.TryGetValue key with
        | true, value -> ValueSome value
        | false, _ -> ValueNone

    let updateSessionByConnection connectionId updater =
        match tryGet connectionId connections with
        | ValueSome connection ->
            match connection.RegisteredDeviceId with
            | Some deviceId ->
                let key = struct (connection.UserId, deviceId)
                match tryGet key sessions with
                | ValueSome session when session.ConnectionId = Some connectionId ->
                    sessions[key] <- updater session
                | _ -> ()
            | None -> ()
        | ValueNone -> ()

    let getDefault userId =
        match tryGet userId defaults with
        | ValueSome deviceId -> tryGet (struct (userId, deviceId)) sessions
        | ValueNone -> ValueNone

    member _.TransportConnected(userId: string, connectionId: string) =
        lock gate (fun () ->
            connections[connectionId] <-
                { ConnectionId = connectionId
                  UserId = userId
                  ConnectedAt = DateTimeOffset.UtcNow
                  RegisteredDeviceId = None })

    member _.Register(userId: string, registration: ClientRegistration, connectionId: string) =
        if String.IsNullOrWhiteSpace(userId) then
            invalidArg "userId" "Authenticated user id is required."

        if String.IsNullOrWhiteSpace(registration.DeviceId) then
            invalidArg "registration" "DeviceId is required."

        if String.IsNullOrWhiteSpace(registration.DeviceName) then
            invalidArg "registration" "DeviceName is required."

        let advertisedCommands =
            registration.SupportedCommands
            |> Option.ofObj
            |> Option.defaultValue []
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> Set.ofList

        let enabledCommands =
            AgentProtocol.commandDefinitions
            |> List.map (fun definition -> definition.Name)
            |> List.filter advertisedCommands.Contains

        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow
            let key = struct (userId, registration.DeviceId)
            let previous = tryGet key sessions

            // Re-registering another device on this transport must not leave
            // the previous device falsely marked connected.
            match tryGet connectionId connections with
            | ValueSome transport when transport.UserId = userId ->
                match transport.RegisteredDeviceId with
                | Some previousDeviceId when previousDeviceId <> registration.DeviceId ->
                    let oldKey = struct (userId, previousDeviceId)
                    match tryGet oldKey sessions with
                    | ValueSome old when old.ConnectionId = Some connectionId ->
                        sessions[oldKey] <- { old with State = Disconnected; ConnectionId = None; DisconnectedAt = Some now }
                    | _ -> ()
                | _ -> ()
            | _ -> ()

            let generation =
                match previous with
                | ValueSome session -> session.Generation + 1L
                | ValueNone -> 1L

            let connectionReason =
                match previous with
                | ValueNone ->
                    $"Initial registration from {registration.DeviceName}"
                | ValueSome session ->
                    $"Reconnect of {registration.DeviceName} after {session.State} generation {session.Generation}"

            let registrationReason =
                let negotiation =
                    $"negotiated {enabledCommands.Length}/{AgentProtocol.commandDefinitions.Length} server commands"

                if String.Equals(registration.ProtocolVersion, AgentProtocol.version, StringComparison.Ordinal) then
                    $"{connectionReason}; {negotiation}"
                else
                    $"{connectionReason}; protocol mismatch: client {registration.ProtocolVersion}, server {AgentProtocol.version}; {negotiation}"

            let session =
                { UserId = userId
                  DeviceId = registration.DeviceId
                  DeviceName = registration.DeviceName
                  ConnectionId = Some connectionId
                  State = Registered
                  Generation = generation
                  ProtocolVersion = registration.ProtocolVersion
                  ClientVersion = registration.ClientVersion
                  SupportedCommands = enabledCommands
                  RegisteredAt = now
                  LastSeenAt = now
                  DisconnectedAt = None
                  LastFailure = None
                  RegistrationReason = registrationReason }

            sessions[key] <- session
            if not (defaults.ContainsKey userId) then defaults[userId] <- registration.DeviceId

            let connectedAt =
                match tryGet connectionId connections with
                | ValueSome connection -> connection.ConnectedAt
                | ValueNone -> now

            connections[connectionId] <-
                { ConnectionId = connectionId
                  UserId = userId
                  ConnectedAt = connectedAt
                  RegisteredDeviceId = Some registration.DeviceId }

            { Generation = generation
              RegisteredAt = now
              RegistrationReason = registrationReason
              ServerProtocolVersion = AgentProtocol.version
              EnabledCommands = enabledCommands })

    member _.Disconnect(connectionId: string, reason: string option) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            match tryGet connectionId connections with
            | ValueSome connection ->
                connections.Remove(connectionId) |> ignore

                match connection.RegisteredDeviceId with
                | Some deviceId ->
                    let key = struct (connection.UserId, deviceId)
                    match tryGet key sessions with
                    | ValueSome session when session.ConnectionId = Some connectionId ->
                        sessions[key] <-
                            { session with
                                ConnectionId = None
                                State = Disconnected
                                LastSeenAt = now
                                DisconnectedAt = Some now
                                LastFailure = Some(defaultArg reason "Transport disconnected") }
                    | _ -> ()
                | None -> ()
            | ValueNone -> ())

    member _.TouchConnection(connectionId: string) =
        lock gate (fun () ->
            match tryGet connectionId connections with
            | ValueSome connection ->
                let now = DateTimeOffset.UtcNow

                updateSessionByConnection connectionId (fun session ->
                    { session with LastSeenAt = now })
            | ValueNone -> ())

    member _.RecordDispatchSuccess(userId: string, connectionId: string) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            updateSessionByConnection connectionId (fun session ->
                if session.UserId = userId && session.State = Registered then
                    { session with LastSeenAt = now; LastFailure = None }
                else session))

    member _.RecordDispatchFailure(userId: string, connectionId: string, reason: string) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            updateSessionByConnection connectionId (fun session ->
                if session.UserId = userId then
                    { session with LastSeenAt = now; LastFailure = Some reason }
                else session))

    member _.GetSession(userId: string) =
        lock gate (fun () -> getDefault userId)

    member _.GetSessionByDevice(userId: string, deviceId: string) =
        lock gate (fun () -> tryGet (struct (userId, deviceId)) sessions)

    member _.GetSessionByConnection(connectionId: string) =
        lock gate (fun () ->
            match tryGet connectionId connections with
            | ValueSome transport ->
                match transport.RegisteredDeviceId with
                | Some deviceId ->
                    match tryGet (struct (transport.UserId, deviceId)) sessions with
                    | ValueSome session when session.ConnectionId = Some connectionId -> ValueSome session
                    | _ -> ValueNone
                | None -> ValueNone
            | ValueNone -> ValueNone)

    member _.ListDevices(userId: string) =
        lock gate (fun () ->
            sessions.Values
            |> Seq.filter (fun session -> session.UserId = userId)
            |> Seq.sortBy (fun session -> session.DeviceName, session.DeviceId)
            |> Seq.toArray)

    member _.GetDefaultDeviceId(userId: string) =
        lock gate (fun () ->
            match tryGet userId defaults with
            | ValueSome value -> Some value
            | ValueNone -> None)

    member _.SetDefaultDevice(userId: string, deviceId: string) =
        lock gate (fun () ->
            match tryGet (struct (userId, deviceId)) sessions with
            | ValueSome _ ->
                defaults[userId] <- deviceId
                true
            | ValueNone -> false)

    member _.GetTransportConnection(connectionId: string) =
        lock gate (fun () -> tryGet connectionId connections)

    member this.GetConnectionId(userId: string) =
        match this.GetSession userId with
        | ValueSome { State = Registered; ConnectionId = Some connectionId } -> ValueSome connectionId
        | _ -> ValueNone
