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
      RegisteredAt: DateTimeOffset
      LastSeenAt: DateTimeOffset
      DisconnectedAt: DateTimeOffset option
      LastFailure: string option
      RegistrationReason: string }

type UserService() =
    let gate = obj()
    let sessions = Dictionary<string, ClientSessionStatus>(StringComparer.Ordinal)
    let connections = Dictionary<string, TransportConnectionStatus>(StringComparer.Ordinal)

    let tryGet key (dictionary: Dictionary<string, 'a>) =
        match dictionary.TryGetValue key with
        | true, value -> ValueSome value
        | false, _ -> ValueNone

    let updateSession userId updater =
        match tryGet userId sessions with
        | ValueSome session ->
            let updated = updater session
            sessions[userId] <- updated
            ValueSome updated
        | ValueNone ->
            ValueNone

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

        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow
            let previous = tryGet userId sessions

            let generation =
                match previous with
                | ValueSome session -> session.Generation + 1L
                | ValueNone -> 1L

            let connectionReason =
                match previous with
                | ValueNone ->
                    $"Initial registration from {registration.DeviceName}"
                | ValueSome session when String.Equals(session.DeviceId, registration.DeviceId, StringComparison.Ordinal) ->
                    $"Reconnect of {registration.DeviceName} after {session.State} generation {session.Generation}"
                | ValueSome session ->
                    $"Device switch from {session.DeviceName} generation {session.Generation} to {registration.DeviceName}"

            let registrationReason =
                if String.Equals(registration.ProtocolVersion, AgentProtocol.version, StringComparison.Ordinal) then
                    connectionReason
                else
                    $"{connectionReason}; protocol mismatch: client {registration.ProtocolVersion}, server {AgentProtocol.version}"

            let session =
                { UserId = userId
                  DeviceId = registration.DeviceId
                  DeviceName = registration.DeviceName
                  ConnectionId = Some connectionId
                  State = Registered
                  Generation = generation
                  ProtocolVersion = registration.ProtocolVersion
                  ClientVersion = registration.ClientVersion
                  RegisteredAt = now
                  LastSeenAt = now
                  DisconnectedAt = None
                  LastFailure = None
                  RegistrationReason = registrationReason }

            sessions[userId] <- session

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
              RegistrationReason = registrationReason })

    member _.Disconnect(connectionId: string, reason: string option) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            match tryGet connectionId connections with
            | ValueSome connection ->
                connections.Remove(connectionId) |> ignore

                updateSession connection.UserId (fun session ->
                    if session.ConnectionId = Some connectionId then
                        { session with
                            ConnectionId = None
                            State = Disconnected
                            LastSeenAt = now
                            DisconnectedAt = Some now
                            LastFailure = Some(defaultArg reason "Transport disconnected") }
                    else
                        session)
                |> ignore
            | ValueNone -> ())

    member _.TouchConnection(connectionId: string) =
        lock gate (fun () ->
            match tryGet connectionId connections with
            | ValueSome connection ->
                let now = DateTimeOffset.UtcNow

                updateSession connection.UserId (fun session ->
                    if session.ConnectionId = Some connectionId then
                        { session with LastSeenAt = now }
                    else
                        session)
                |> ignore
            | ValueNone -> ())

    member _.RecordDispatchSuccess(userId: string, connectionId: string) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            updateSession userId (fun session ->
                if session.ConnectionId = Some connectionId && session.State = Registered then
                    { session with
                        LastSeenAt = now
                        LastFailure = None }
                else
                    session)
            |> ignore)

    member _.RecordDispatchFailure(userId: string, connectionId: string, reason: string) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            updateSession userId (fun session ->
                if session.ConnectionId = Some connectionId then
                    { session with
                        LastSeenAt = now
                        LastFailure = Some reason }
                else
                    session)
            |> ignore)

    member _.GetSession(userId: string) =
        lock gate (fun () -> tryGet userId sessions)

    member _.GetTransportConnection(connectionId: string) =
        lock gate (fun () -> tryGet connectionId connections)

    member this.GetConnectionId(userId: string) =
        match this.GetSession userId with
        | ValueSome { State = Registered; ConnectionId = Some connectionId } -> ValueSome connectionId
        | _ -> ValueNone
