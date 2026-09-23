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
      ConnectedAt: DateTimeOffset
      RegisteredKey: string option }

type ClientSessionStatus =
    { Key: string
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
    let sessions = Dictionary<string, ClientSessionStatus>()
    let connections = Dictionary<string, TransportConnectionStatus>()

    let tryGet key (dictionary: Dictionary<string, 'a>) =
        match dictionary.TryGetValue key with
        | true, value -> ValueSome value
        | false, _ -> ValueNone

    let updateSession key updater =
        match tryGet key sessions with
        | ValueSome session ->
            let updated = updater session
            sessions[key] <- updated
            ValueSome updated
        | ValueNone ->
            ValueNone

    member _.TransportConnected(connectionId: string) =
        lock gate (fun () ->
            connections[connectionId] <-
                { ConnectionId = connectionId
                  ConnectedAt = DateTimeOffset.UtcNow
                  RegisteredKey = None })

    member _.Register(registration: ClientRegistration, connectionId: string) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            let previous =
                sessions
                |> tryGet registration.Key

            let generation =
                match previous with
                | ValueSome session -> session.Generation + 1L
                | ValueNone -> 1L

            let reconnectReason =
                match previous with
                | ValueNone -> "Initial registration"
                | ValueSome session -> $"Reconnect after {session.State} generation {session.Generation}"

            let registrationReason =
                if String.Equals(registration.ProtocolVersion, AgentProtocol.version, StringComparison.Ordinal) then
                    reconnectReason
                else
                    $"{reconnectReason}; protocol mismatch: client {registration.ProtocolVersion}, server {AgentProtocol.version}"

            let session =
                { Key = registration.Key
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

            sessions[registration.Key] <- session

            let connectedAt =
                match tryGet connectionId connections with
                | ValueSome connection -> connection.ConnectedAt
                | ValueNone -> now

            connections[connectionId] <-
                { ConnectionId = connectionId
                  ConnectedAt = connectedAt
                  RegisteredKey = Some registration.Key }

            { Generation = generation
              RegisteredAt = now
              RegistrationReason = registrationReason })

    member _.Disconnect(connectionId: string, reason: string option) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            match tryGet connectionId connections with
            | ValueSome connection ->
                connections.Remove(connectionId) |> ignore

                match connection.RegisteredKey with
                | Some key ->
                    updateSession key (fun session ->
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
                | None -> ()
            | ValueNone -> ())

    member _.TouchConnection(connectionId: string) =
        lock gate (fun () ->
            match tryGet connectionId connections with
            | ValueSome { RegisteredKey = Some key } ->
                let now = DateTimeOffset.UtcNow

                updateSession key (fun session ->
                    if session.ConnectionId = Some connectionId then
                        { session with LastSeenAt = now }
                    else
                        session)
                |> ignore
            | _ -> ())

    member _.RecordDispatchSuccess(key: string, connectionId: string) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            updateSession key (fun session ->
                if session.ConnectionId = Some connectionId && session.State = Registered then
                    { session with
                        LastSeenAt = now
                        LastFailure = None }
                else
                    session)
            |> ignore)

    member _.RecordDispatchFailure(key: string, connectionId: string, reason: string) =
        lock gate (fun () ->
            let now = DateTimeOffset.UtcNow

            updateSession key (fun session ->
                if session.ConnectionId = Some connectionId then
                    { session with
                        LastSeenAt = now
                        LastFailure = Some reason }
                else
                    session)
            |> ignore)

    member _.GetSession(key: string) =
        lock gate (fun () -> tryGet key sessions)

    member _.GetTransportConnection(connectionId: string) =
        lock gate (fun () -> tryGet connectionId connections)

    member this.GetConnectionId(key: string) =
        match this.GetSession key with
        | ValueSome { State = Registered; ConnectionId = Some connectionId } -> ValueSome connectionId
        | _ -> ValueNone
