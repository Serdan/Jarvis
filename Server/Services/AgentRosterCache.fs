namespace Server.Services

open System
open System.Collections.Generic
open Common.SignalR

/// All identities are scoped to the authenticated user. The server cache is
/// disposable: only connected clients can provide authoritative ledger reads.
type RosterDispatch =
    { ConnectionId: string
      Request: AgentRosterRefreshRequest }

type private RosterProvider =
    { ConnectionId: string
      DeviceId: string
      DeviceName: string
      Generation: int64
      Claim: AgentRosterClaim }

type private RosterPending =
    { RequestId: string
      ConnectionId: string
      RequestedAt: DateTimeOffset
      Tried: Set<string> }

type private RosterSlot =
    { Identity: string
      Providers: Dictionary<string, RosterProvider>
      mutable Data: AgentRosterLedger option
      mutable Pending: RosterPending option
      mutable LastSuccess: DateTimeOffset option
      mutable LastAttempt: DateTimeOffset option
      mutable Error: string option }

/// Thread-safe, bounded, process-local projection. It never mutates the
/// canonical ledger and cannot authorize task, agent, review or scheduling
/// operations. A fresh server starts with no roster or activity history.
type AgentRosterCache(clock: unit -> DateTimeOffset) =
    let gate = obj()
    let ledgers = Dictionary<struct (string * string), RosterSlot>()
    let activity = Dictionary<struct (string * string * string), AgentRosterActivity>()
    let refreshInterval = TimeSpan.FromSeconds 20.
    let refreshDeadline = TimeSpan.FromSeconds 30.
    let maxLedgersPerUser = 256
    let maxAgentsPerLedger = 2048

    let identityValid (value: string) =
        if String.IsNullOrWhiteSpace value || value.Length > 100 then false
        else
            match value.Split(':') with
            | [| projectId; ledgerId; epoch |] ->
                let mutable guid = Guid.Empty
                let mutable number = 0
                Guid.TryParseExact(projectId, "N", &guid)
                && Guid.TryParseExact(ledgerId, "N", &guid)
                && Int32.TryParse(epoch, &number) && number >= 1
            | _ -> false

    let names (claim: AgentRosterClaim) =
        Array.append [| claim.ProjectName |] (if isNull claim.Aliases then [||] else claim.Aliases)
        |> Array.filter (fun name -> not (String.IsNullOrWhiteSpace name) && name.Length <= 256)
        |> Array.distinct

    let makeSlot identity =
        { Identity = identity
          Providers = Dictionary<string, RosterProvider>(StringComparer.Ordinal)
          Data = None
          Pending = None
          LastSuccess = None
          LastAttempt = None
          Error = None }

    let userSlots userId =
        ledgers
        |> Seq.choose (fun (KeyValue(struct (user, _), slot)) ->
            if user = userId then Some slot else None)
        |> Seq.toArray

    let selectNext (slot: RosterSlot) (tried: Set<string>) =
        let now = clock()
        let provider =
            slot.Providers.Values
            |> Seq.filter (fun p -> not (tried.Contains p.ConnectionId))
            |> Seq.sortBy (fun p -> p.DeviceId, p.ConnectionId)
            |> Seq.tryHead
        match provider with
        | None ->
            slot.Pending <- None
            None
        | Some provider ->
            let requestId = Guid.NewGuid().ToString("N")
            let request =
                { RequestId = requestId
                  Identity = slot.Identity
                  ProjectName = provider.Claim.ProjectName
                  PreviousHead = slot.Data |> Option.map _.Head |> Option.defaultValue "" }
            slot.Pending <-
                Some { RequestId = requestId
                       ConnectionId = provider.ConnectionId
                       RequestedAt = now
                       Tried = tried.Add provider.ConnectionId }
            slot.LastAttempt <- Some now
            Some { ConnectionId = provider.ConnectionId; Request = request }

    let failPending (slot: RosterSlot) reason =
        let prior = slot.Pending
        slot.Pending <- None
        slot.Error <- Some reason
        match prior with
        | Some pending -> selectNext slot pending.Tried
        | None -> None

    let validData identity (data: AgentRosterLedger) =
        not (isNull (box data))
        && data.Identity = identity
        && not (String.IsNullOrWhiteSpace data.ProjectName)
        && data.ProjectName.Length <= 256
        && not (isNull data.Agents)
        && data.Agents.Length <= maxAgentsPerLedger
        && data.UnclaimedReviews >= 0 && data.PendingReviews >= data.UnclaimedReviews
        && not (isNull data.Head)
        && (data.Head.Length = 40 || data.Head.Length = 64)
        && (data.Head |> Seq.forall Uri.IsHexDigit)
        && (data.Agents |> Array.forall (fun a ->
            not (isNull (box a))
            && not (String.IsNullOrWhiteSpace a.AgentId)
            && a.AgentId.Length <= 256
            && not (String.IsNullOrWhiteSpace a.State)
            && a.PendingNotifications >= 0))
        && (data.Agents |> Array.map _.AgentId |> Array.distinct |> Array.length) = data.Agents.Length

    new() = AgentRosterCache(fun () -> DateTimeOffset.UtcNow)

    /// Re-registration of the same device makes the old connection's claims
    /// ineligible, even before SignalR observes the old transport closing.
    member _.ForgetDevice(userId: string, deviceId: string) =
        lock gate (fun () ->
            for slot in userSlots userId do
                for provider in slot.Providers.Values |> Seq.toArray do
                    if provider.DeviceId = deviceId then
                        slot.Providers.Remove(provider.ConnectionId) |> ignore
                        if slot.Pending |> Option.exists (fun p -> p.ConnectionId = provider.ConnectionId) then
                            slot.Pending <- None)

    /// A full advertisement supersedes this connection's former claims.
    /// A new provider for an already-fresh ledger does not cause another fetch.
    member _.Advertise(userId: string, connectionId: string, deviceId: string,
                       deviceName: string, generation: int64, claims: AgentRosterClaim array) =
        lock gate (fun () ->
            if isNull claims || claims.Length > maxLedgersPerUser then
                invalidArg (nameof claims) "Too many ledger claims."
            let claims =
                claims
                |> Array.filter (fun c ->
                    not (isNull (box c))
                    && identityValid c.Identity
                    && not (String.IsNullOrWhiteSpace c.ProjectName)
                    && c.ProjectName.Length <= 256)
                |> Array.groupBy _.Identity
                |> Array.map (fun (_, group) ->
                    let primary = group[0]
                    { primary with
                        Aliases = group |> Array.collect names |> Array.distinct })
            let claimed = claims |> Array.map _.Identity |> Set.ofArray
            for slot in userSlots userId do
                if not (claimed.Contains slot.Identity) then
                    slot.Providers.Remove(connectionId) |> ignore
                    match slot.Pending with
                    | Some pending when pending.ConnectionId = connectionId ->
                        slot.Pending <- None
                    | _ -> ()
            let results = ResizeArray<RosterDispatch>()
            let now = clock()
            for claim in claims do
                let key = struct (userId, claim.Identity)
                let slot =
                    match ledgers.TryGetValue key with
                    | true, existing -> existing
                    | _ ->
                        if (userSlots userId).Length >= maxLedgersPerUser then
                            invalidOp "Roster ledger capacity exhausted."
                        let created = makeSlot claim.Identity
                        ledgers.Add(key, created)
                        created
                let previouslyAvailable = slot.Providers.Count > 0
                slot.Providers[connectionId] <-
                    { ConnectionId = connectionId; DeviceId = deviceId
                      DeviceName = deviceName; Generation = generation; Claim = claim }
                // New availability always refreshes stale history; overlapping
                // providers of a live ledger reuse the existing in-flight read.
                let mustRefresh =
                    slot.Pending.IsNone
                    && (not previouslyAvailable || slot.Data.IsNone
                        || slot.LastSuccess |> Option.exists (fun at -> now - at >= refreshInterval))
                if mustRefresh then
                    match selectNext slot Set.empty with
                    | Some dispatch -> results.Add dispatch
                    | None -> ()
            results |> Seq.toList)

    /// A disconnected provider is removed, but its last cached roster survives.
    /// If the active request used that provider, immediately try another.
    member _.Disconnect(userId: string, connectionId: string) =
        lock gate (fun () ->
            let results = ResizeArray<RosterDispatch>()
            for slot in userSlots userId do
                slot.Providers.Remove(connectionId) |> ignore
                match slot.Pending with
                | Some pending when pending.ConnectionId = connectionId ->
                    match failPending slot "Refresh provider disconnected." with
                    | Some dispatch -> results.Add dispatch
                    | None -> ()
                | _ -> ()
            results |> Seq.toList)

    /// A response is accepted only for the current request AND its original
    /// authenticated connection. Late/out-of-order arrivals cannot overwrite
    /// a newer accepted response or resurrect a disconnected provider.
    member _.Complete(userId: string, connectionId: string, response: AgentRosterRefreshResponse) =
        lock gate (fun () ->
            if isNull (box response) then None
            else
                match ledgers.TryGetValue(struct (userId, response.Identity)) with
                | false, _ -> None
                | true, slot ->
                    match slot.Pending with
                    | None -> None
                    | Some pending when pending.RequestId <> response.RequestId
                                        || pending.ConnectionId <> connectionId
                                        || not (slot.Providers.ContainsKey connectionId) -> None
                    | Some pending ->
                        if not (String.IsNullOrWhiteSpace response.Error) then
                            failPending slot ("Roster refresh failed: " + response.Error)
                        elif not (validData response.Identity response.Data) then
                            failPending slot "Roster refresh rejected malformed ledger data."
                        else
                            // Monotonic ancestry is verified on the provider by
                            // comparing its Git snapshot to PreviousHead.
                            slot.Data <- Some response.Data
                            slot.LastSuccess <- Some(clock())
                            slot.Pending <- None
                            slot.Error <- None
                            None)

    /// Fetch at most once per ledger per interval. Expired requests fall back
    /// to an alternative provider; old replies remain rejected by request ID.
    member _.RefreshDue(userId: string) =
        lock gate (fun () ->
            let now = clock()
            let results = ResizeArray<RosterDispatch>()
            for slot in userSlots userId do
                match slot.Pending with
                | Some pending when now - pending.RequestedAt >= refreshDeadline ->
                    match failPending slot "Roster refresh timed out." with
                    | Some dispatch -> results.Add dispatch
                    | None -> ()
                | Some _ -> ()
                | None when slot.Providers.Count > 0
                            && (slot.LastAttempt.IsNone
                                || now - slot.LastAttempt.Value >= refreshInterval) ->
                    match selectNext slot Set.empty with
                    | Some dispatch -> results.Add dispatch
                    | None -> ()
                | None -> ()
            results |> Seq.toList)

    /// The selected MCP route identifies the real device; project-name aliases
    /// resolve only within that device's current claims. Ambiguity fails closed.
    member _.RecordActivity(userId: string, deviceId: string, projectName: string,
                            agentId: string, observedAt: DateTimeOffset) =
        if not (String.IsNullOrWhiteSpace agentId) then
            lock gate (fun () ->
                let project =
                    let separator = projectName.IndexOf("::", StringComparison.Ordinal)
                    if separator < 0 then projectName else projectName.Substring(0, separator)
                let matching =
                    userSlots userId
                    |> Array.filter (fun slot ->
                        slot.Providers.Values
                        |> Seq.exists (fun provider ->
                            provider.DeviceId = deviceId
                            && (names provider.Claim |> Array.exists (fun name ->
                                String.Equals(name, project, StringComparison.OrdinalIgnoreCase)))))
                if matching.Length = 1 then
                    let identity = matching[0].Identity
                    let key = struct (userId, identity, agentId)
                    let prior = match activity.TryGetValue key with true, old -> Some old | _ -> None
                    if prior |> Option.forall (fun old -> observedAt > old.LastSeenAtUtc) then
                        let device =
                            matching[0].Providers.Values
                            |> Seq.find (fun p -> p.DeviceId = deviceId)
                        activity[key] <-
                            { Identity = identity; AgentId = agentId
                              LastSeenAtUtc = observedAt; DeviceName = device.DeviceName })

    member _.View(userId: string) =
        lock gate (fun () ->
            let now = clock()
            let data =
                userSlots userId
                |> Array.sortBy _.Identity
                |> Array.map (fun slot ->
                    let hasData = slot.Data.IsSome
                    let connected = slot.Providers.Count > 0
                    let stale =
                        slot.LastSuccess
                        |> Option.exists (fun last -> now - last >= refreshInterval)
                    let status =
                        if not connected then (if hasData then "Stale" else "Unavailable")
                        elif slot.Error.IsSome then (if hasData then "Stale" else "Unavailable")
                        elif not hasData then "Refreshing"
                        elif stale then "Stale"
                        else "Fresh"
                    let aliases =
                        slot.Providers.Values
                        |> Seq.collect (fun p -> names p.Claim)
                        |> Seq.distinct
                        |> Seq.toArray
                    let machines =
                        slot.Providers.Values
                        |> Seq.map _.DeviceName
                        |> Seq.distinct
                        |> Seq.sort
                        |> Seq.toArray
                    let name =
                        match slot.Data with
                        | Some cached -> cached.ProjectName
                        | None when aliases.Length > 0 -> aliases[0]
                        | None -> slot.Identity
                    { Identity = slot.Identity
                      ProjectName = name
                      Aliases = if aliases.Length = 0 then
                                    slot.Data |> Option.map _.Aliases |> Option.defaultValue [||]
                                else aliases
                      Status = status
                      Error = slot.Error |> Option.defaultValue
                                  (if not connected then "No connected provider for this ledger." else "")
                      ProviderMachines = machines
                      Data = slot.Data |> Option.defaultValue Unchecked.defaultof<_> })
            // Activity is only surfaced for agents in current known ledgers;
            // it does not make absent ledger rows appear authoritative.
            let identities = data |> Array.map _.Identity |> Set.ofArray
            let seen =
                activity
                |> Seq.choose (fun (KeyValue(struct (user, identity, _), entry)) ->
                    if user = userId && identities.Contains identity then Some entry else None)
                |> Seq.toArray
            { Ledgers = data; Activity = seen; ServerNowUtc = now })