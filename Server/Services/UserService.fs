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

/// In-process, idle-based routing retention. Active transport registrations
/// are never candidates for eviction, regardless of elapsed time.
type UserRetentionPolicy =
    { DisconnectedTimeToLive: TimeSpan
      ConversationIdleTimeToLive: TimeSpan
      SweepInterval: TimeSpan
      MaxDisconnectedPerUser: int
      MaxDisconnectedTotal: int
      MaxConversationsPerUser: int
      MaxConversationsTotal: int }

module UserRetentionPolicy =
    let defaults =
        { DisconnectedTimeToLive = TimeSpan.FromDays 7.
          ConversationIdleTimeToLive = TimeSpan.FromDays 1.
          SweepInterval = TimeSpan.FromMinutes 2.
          MaxDisconnectedPerUser = 64
          MaxDisconnectedTotal = 2048
          MaxConversationsPerUser = 256
          MaxConversationsTotal = 8192 }

    let validate policy =
        if policy.DisconnectedTimeToLive <= TimeSpan.Zero
           || policy.ConversationIdleTimeToLive <= TimeSpan.Zero
           || policy.SweepInterval <= TimeSpan.Zero then
            invalidArg "policy" "Routing retention intervals must be positive."
        if policy.MaxDisconnectedPerUser < 1 || policy.MaxDisconnectedTotal < 1
           || policy.MaxConversationsPerUser < 1 || policy.MaxConversationsTotal < 1 then
            invalidArg "policy" "Routing retention capacity bounds must be positive."
        policy

type private ConversationSelection =
    { DeviceId: string
      LastUsedAt: DateTimeOffset }

type RoutingRetentionCounts =
    { DeviceSessions: int
      DisconnectedSessions: int
      ConversationSelections: int
      DefaultSelections: int
      ConnectedTransports: int }

type UserService(clock: unit -> DateTimeOffset, retention: UserRetentionPolicy) =
    let utcNow = clock
    let policy = UserRetentionPolicy.validate retention
    let gate = obj()
    let sessions = Dictionary<struct (string * string), ClientSessionStatus>()
    let defaults = Dictionary<string, string>(StringComparer.Ordinal)
    let conversationSelections = Dictionary<struct (string * string), ConversationSelection>()
    let connections = Dictionary<string, TransportConnectionStatus>(StringComparer.Ordinal)
    // A process-local monotonically increasing generation avoids reusing an
    // expired device's generation when it later re-registers. No tombstones.
    let mutable nextGeneration = 0L
    let mutable nextSweep = DateTimeOffset.MinValue

    let tryGet key (dictionary: Dictionary<'key, 'a>) =
        match dictionary.TryGetValue key with
        | true, value -> ValueSome value
        | false, _ -> ValueNone

    let activeFallback userId =
        // Prefer the oldest still-connected registration, breaking ties by
        // device ID. Do not automatically route to a disconnected alternative.
        let mutable best: ClientSessionStatus option = None
        for session in sessions.Values do
            if session.UserId = userId && session.State = Registered && session.ConnectionId.IsSome then
                match best with
                | Some previous when
                    (session.RegisteredAt, session.DeviceId) >=
                    (previous.RegisteredAt, previous.DeviceId) -> ()
                | _ -> best <- Some session
        best |> Option.map _.DeviceId

    let repairSelections () =
        let orphanDefaults = ResizeArray<string>()
        for KeyValue(userId, deviceId) in defaults do
            if not (sessions.ContainsKey(struct (userId, deviceId))) then
                orphanDefaults.Add userId
        for userId in orphanDefaults do
            defaults.Remove userId |> ignore
            match activeFallback userId with
            | Some deviceId -> defaults[userId] <- deviceId
            | None -> ()

        let orphanConversations = ResizeArray<struct (string * string)>()
        for KeyValue(key, selected) in conversationSelections do
            let struct (userId, _) = key
            if not (sessions.ContainsKey(struct (userId, selected.DeviceId))) then
                orphanConversations.Add key
        for key in orphanConversations do conversationSelections.Remove key |> ignore

    let oldestDisconnected userFilter =
        let mutable found: (struct (string * string) * DateTimeOffset) option = None
        for KeyValue(key, session) in sessions do
            if session.State = Disconnected && (userFilter |> Option.forall ((=) session.UserId)) then
                let disconnectedAt = session.DisconnectedAt |> Option.defaultValue session.LastSeenAt
                let struct (userId, deviceId) = key
                let candidate = (disconnectedAt, userId, deviceId)
                match found with
                | Some (otherKey, otherDate) ->
                    let struct (otherUser, otherDevice) = otherKey
                    if candidate < (otherDate, otherUser, otherDevice) then
                        found <- Some (key, disconnectedAt)
                | None -> found <- Some (key, disconnectedAt)
        found |> Option.map fst

    let disconnectedCount userFilter =
        let mutable count = 0
        for session in sessions.Values do
            if session.State = Disconnected && (userFilter |> Option.forall ((=) session.UserId)) then
                count <- count + 1
        count

    let trimDisconnected userId =
        let mutable removed = false
        while disconnectedCount (Some userId) > policy.MaxDisconnectedPerUser do
            match oldestDisconnected (Some userId) with
            | Some key ->
                sessions.Remove key |> ignore
                removed <- true
            | None -> invalidOp "Disconnected device retention count is inconsistent."
        while disconnectedCount None > policy.MaxDisconnectedTotal do
            match oldestDisconnected None with
            | Some key ->
                sessions.Remove key |> ignore
                removed <- true
            | None -> invalidOp "Global disconnected device retention count is inconsistent."
        if removed then repairSelections ()

    let oldestConversation userFilter =
        let mutable found: (struct (string * string) * DateTimeOffset) option = None
        for KeyValue(key, selected) in conversationSelections do
            let struct (userId, conversationId) = key
            if userFilter |> Option.forall ((=) userId) then
                match found with
                | Some (otherKey, otherDate) ->
                    let struct (otherUser, otherConversation) = otherKey
                    if (selected.LastUsedAt, userId, conversationId) <
                       (otherDate, otherUser, otherConversation) then
                        found <- Some (key, selected.LastUsedAt)
                | None -> found <- Some (key, selected.LastUsedAt)
        found |> Option.map fst

    let conversationCount userFilter =
        let mutable count = 0
        for KeyValue(key, _) in conversationSelections do
            let struct (userId, _) = key
            if userFilter |> Option.forall ((=) userId) then count <- count + 1
        count

    let trimConversations userId =
        while conversationCount (Some userId) > policy.MaxConversationsPerUser do
            match oldestConversation (Some userId) with
            | Some key -> conversationSelections.Remove key |> ignore
            | None -> invalidOp "Conversation retention count is inconsistent."
        while conversationSelections.Count > policy.MaxConversationsTotal do
            match oldestConversation None with
            | Some key -> conversationSelections.Remove key |> ignore
            | None -> invalidOp "Global conversation retention count is inconsistent."

    let sweepExpired (now: DateTimeOffset) =
        let oldDevices = ResizeArray<struct (string * string)>()
        for KeyValue(key, session) in sessions do
            if session.State = Disconnected then
                let since = session.DisconnectedAt |> Option.defaultValue session.LastSeenAt
                if now - since >= policy.DisconnectedTimeToLive then oldDevices.Add key
        for key in oldDevices do sessions.Remove key |> ignore

        let oldConversations = ResizeArray<struct (string * string)>()
        for KeyValue(key, selection) in conversationSelections do
            if now - selection.LastUsedAt >= policy.ConversationIdleTimeToLive then
                oldConversations.Add key
        for key in oldConversations do conversationSelections.Remove key |> ignore
        repairSelections ()
        nextSweep <- now + policy.SweepInterval

    let sweepIfDue () =
        let now = utcNow ()
        if now >= nextSweep then sweepExpired now
        now

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
        | ValueNone ->
            match activeFallback userId with
            | Some deviceId ->
                defaults[userId] <- deviceId
                tryGet (struct (userId, deviceId)) sessions
            | None -> ValueNone

    let selectedDeviceId userId conversationId now =
        let fallback () =
            match getDefault userId with
            | ValueSome session -> Some session.DeviceId
            | ValueNone -> None
        match conversationId with
        | Some id when not (String.IsNullOrWhiteSpace id) ->
            let key = struct (userId, id)
            match tryGet key conversationSelections with
            | ValueSome selected when sessions.ContainsKey(struct (userId, selected.DeviceId)) ->
                conversationSelections[key] <- { selected with LastUsedAt = now }
                Some selected.DeviceId
            | ValueSome _ ->
                conversationSelections.Remove key |> ignore
                fallback ()
            | ValueNone -> fallback ()
        | _ -> fallback ()

    member _.TransportConnected(userId: string, connectionId: string) =
        if String.IsNullOrWhiteSpace userId || String.IsNullOrWhiteSpace connectionId then
            invalidArg "connectionId" "Authenticated transport user and connection ID are required."
        lock gate (fun () ->
            let now = sweepIfDue ()
            // SignalR IDs are unique while live. Reject accidental reuse
            // rather than overwriting an existing owner's live transport.
            if connections.ContainsKey connectionId then
                invalidOp "Transport connection ID is already active."
            connections[connectionId] <-
                { ConnectionId = connectionId
                  UserId = userId
                  ConnectedAt = now
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
            let now = sweepIfDue ()
            // Only the authenticated transport's owner can bind a device to
            // it. No absent/mismatched connection may create an active route.
            match tryGet connectionId connections with
            | ValueSome transport when transport.UserId = userId -> ()
            | _ -> invalidOp "Device registration requires its authenticated transport."
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

            // Generation remains unique even after a stale device record is
            // pruned, with no unbounded tombstone dictionary.
            nextGeneration <- nextGeneration + 1L
            let generation = nextGeneration

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
            trimDisconnected userId

            { Generation = generation
              RegisteredAt = now
              RegistrationReason = registrationReason
              ServerProtocolVersion = AgentProtocol.version
              EnabledCommands = enabledCommands })

    member _.Disconnect(connectionId: string, reason: string option) =
        lock gate (fun () ->
            let now = sweepIfDue ()

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
                trimDisconnected connection.UserId
            | ValueNone -> ()
        )

    member _.TouchConnection(connectionId: string) =
        lock gate (fun () ->
            match tryGet connectionId connections with
            | ValueSome connection ->
                let now = sweepIfDue ()

                updateSessionByConnection connectionId (fun session ->
                    { session with LastSeenAt = now })
            | ValueNone -> ())

    member _.RecordDispatchSuccess(userId: string, connectionId: string) =
        lock gate (fun () ->
            let now = sweepIfDue ()

            updateSessionByConnection connectionId (fun session ->
                if session.UserId = userId && session.State = Registered then
                    { session with LastSeenAt = now; LastFailure = None }
                else session))

    member _.RecordDispatchFailure(userId: string, connectionId: string, reason: string) =
        lock gate (fun () ->
            let now = sweepIfDue ()

            updateSessionByConnection connectionId (fun session ->
                if session.UserId = userId then
                    { session with LastSeenAt = now; LastFailure = Some reason }
                else session))

    member _.GetSelectedDeviceId(userId: string, conversationId: string option) =
        lock gate (fun () ->
            let now = sweepIfDue ()
            selectedDeviceId userId conversationId now)

    member _.GetSelectedSession(userId: string, conversationId: string option, deviceOverride: string option) =
        lock gate (fun () ->
            let now = sweepIfDue ()
            // An explicit per-call override never falls back to a different
            // device, including after the requested device expires.
            let selected = deviceOverride |> Option.orElseWith (fun () -> selectedDeviceId userId conversationId now)
            match selected with
            | Some deviceId -> tryGet (struct (userId, deviceId)) sessions
            | None -> ValueNone)

    member _.SelectConversationDevice(userId: string, conversationId: string, deviceId: string) =
        lock gate (fun () ->
            let now = sweepIfDue ()
            if String.IsNullOrWhiteSpace conversationId || conversationId.Length > 256 then false
            else
                match tryGet (struct (userId, deviceId)) sessions with
                | ValueSome _ ->
                    conversationSelections[struct (userId, conversationId)] <-
                        { DeviceId = deviceId; LastUsedAt = now }
                    trimConversations userId
                    true
                | ValueNone -> false)

    member _.GetSession(userId: string) =
        lock gate (fun () ->
            sweepIfDue () |> ignore
            getDefault userId)

    member _.GetSessionByDevice(userId: string, deviceId: string) =
        lock gate (fun () ->
            sweepIfDue () |> ignore
            tryGet (struct (userId, deviceId)) sessions)

    member _.GetSessionByConnection(connectionId: string) =
        lock gate (fun () ->
            sweepIfDue () |> ignore
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
            sweepIfDue () |> ignore
            sessions.Values
            |> Seq.filter (fun session -> session.UserId = userId)
            |> Seq.sortBy (fun session -> session.DeviceName, session.DeviceId)
            |> Seq.toArray)

    member _.GetDefaultDeviceId(userId: string) =
        lock gate (fun () ->
            sweepIfDue () |> ignore
            match getDefault userId with
            | ValueSome session -> Some session.DeviceId
            | ValueNone -> None)

    member _.SetDefaultDevice(userId: string, deviceId: string) =
        lock gate (fun () ->
            sweepIfDue () |> ignore
            match tryGet (struct (userId, deviceId)) sessions with
            | ValueSome _ ->
                defaults[userId] <- deviceId
                true
            | ValueNone -> false)

    member _.GetTransportConnection(connectionId: string) =
        lock gate (fun () ->
            sweepIfDue () |> ignore
            tryGet connectionId connections)

    /// Force an expiry pass. Normal calls sweep lazily at most once per
    /// interval; this method is useful for diagnostics and clock-based tests.
    member _.PruneExpired() =
        lock gate (fun () -> sweepExpired (utcNow ()))

    /// Aggregate-only diagnostics; exposes no user IDs or conversation keys.
    member _.RetentionCounts =
        lock gate (fun () ->
            sweepIfDue () |> ignore
            { DeviceSessions = sessions.Count
              DisconnectedSessions = disconnectedCount None
              ConversationSelections = conversationSelections.Count
              DefaultSelections = defaults.Count
              ConnectedTransports = connections.Count })

    member this.GetConnectionId(userId: string) =
        match this.GetSession userId with
        | ValueSome { State = Registered; ConnectionId = Some connectionId } -> ValueSome connectionId
        | _ -> ValueNone

    // ASP.NET Core DI requires a real public parameterless constructor:
    // F# optional arguments compile to non-optional FSharpOption parameters.
    new () = UserService((fun () -> DateTimeOffset.UtcNow), UserRetentionPolicy.defaults)
