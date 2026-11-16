module AgentRosterCacheTests

open System
open Common.SignalR
open NUnit.Framework
open Server.Services

let private identity =
    "a5db4af0dde4452eb337994e534abdf3:0edf788fb25e4b08ac5795cceea7d9c2:1"

let private otherIdentity =
    "b5db4af0dde4452eb337994e534abdf3:1edf788fb25e4b08ac5795cceea7d9c2:1"

let private claim identity name =
    { Identity = identity; ProjectName = name; Aliases = [| name |] }

let private agent name state =
    { AgentId = name
      State = state
      StateEnteredAtUtc = Nullable<DateTimeOffset>()
      StateReason = ""
      Focus = Unchecked.defaultof<AgentRosterFocus>
      RoleIds = [||]
      Activity = ""
      ReviewContext = ""
      PendingNotifications = 0
      LatestNotification = "" }

let private data identity name commit agents =
    { Identity = identity
      ProjectName = name
      Aliases = [| name |]
      Head = commit
      Agents = agents
      UnclaimedReviews = 2
      PendingReviews = 3 }

let private aHead = String.replicate 40 "a"
let private bHead = String.replicate 40 "b"

let private completed (request: AgentRosterRefreshRequest) ledger =
    { RequestId = request.RequestId; Identity = request.Identity
      Data = ledger; Error = "" }

let private advertise (cache: AgentRosterCache) (user: string) (device: string) (name: string) (id: string) =
    cache.Advertise(user, user + "-" + device, device, name, 1L, [| claim id "Jarvis" |])

let private single (requests: RosterDispatch list) =
    Assert.That(requests.Length, Is.EqualTo(1))
    requests.Head

[<Test>]
let overlappingDevicesShareOneLedgerAndOneInitialFetch () =
    let cache = AgentRosterCache()
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    Assert.That(advertise cache "alice" "laptop" "Laptop" identity, Is.Empty)
    cache.Complete("alice", first.ConnectionId,
        completed first.Request (data identity "Jarvis" aHead [| agent "Worker 7" "Working" |]))
    |> ignore

    let roster = cache.View("alice")
    Assert.That(roster.Ledgers.Length, Is.EqualTo(1))
    Assert.That(roster.Ledgers[0].Data.Agents.Length, Is.EqualTo(1))
    Assert.That(roster.Ledgers[0].Status, Is.EqualTo("Fresh"))
    Assert.That(roster.Ledgers[0].ProviderMachines,
                Is.EquivalentTo([| "Desktop"; "Laptop" |]))

[<Test>]
let differentLedgersWithIdenticalProjectNamesDoNotCollapse () =
    let cache = AgentRosterCache()
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    let second = advertise cache "alice" "laptop" "Laptop" otherIdentity |> single
    cache.Complete("alice", first.ConnectionId,
        completed first.Request (data identity "Jarvis" aHead [| agent "Agent 1" "Ready" |])) |> ignore
    cache.Complete("alice", second.ConnectionId,
        completed second.Request (data otherIdentity "Jarvis" bHead [| agent "Agent 1" "Parked" |])) |> ignore
    Assert.That(cache.View("alice").Ledgers.Length, Is.EqualTo(2))

[<Test>]
let disconnectPreservesVerifiedRosterAndMarksItStale () =
    let cache = AgentRosterCache()
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    cache.Complete("alice", first.ConnectionId,
        completed first.Request (data identity "Jarvis" aHead [| agent "Owner" "Working" |])) |> ignore
    Assert.That(cache.Disconnect("alice", first.ConnectionId), Is.Empty)
    let ledger = cache.View("alice").Ledgers[0]
    Assert.That(ledger.Status, Is.EqualTo("Stale"))
    Assert.That(ledger.Data.Agents[0].AgentId, Is.EqualTo("Owner"))
    Assert.That(ledger.Error, Does.Contain("No connected provider"))

[<Test>]
let failureFallsBackAndRejectsLateAndWrongProviderReplies () =
    let cache = AgentRosterCache()
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    Assert.That(advertise cache "alice" "laptop" "Laptop" identity, Is.Empty)
    let failed = { RequestId = first.Request.RequestId; Identity = identity
                   Data = Unchecked.defaultof<_>; Error = "Git fetch failed" }
    let fallback = cache.Complete("alice", first.ConnectionId, failed) |> Option.get
    Assert.That(fallback.ConnectionId, Is.EqualTo("alice-laptop"))
    // A delayed success from the first provider cannot overwrite the fallback.
    let oldData = data identity "Jarvis" aHead [| agent "Wrong" "Working" |]
    cache.Complete("alice", first.ConnectionId, completed first.Request oldData) |> ignore
    cache.Complete("alice", first.ConnectionId, completed fallback.Request oldData) |> ignore
    Assert.That(cache.View("alice").Ledgers[0].Data, Is.Null)
    cache.Complete("alice", fallback.ConnectionId,
        completed fallback.Request (data identity "Jarvis" bHead [| agent "Correct" "Ready" |])) |> ignore
    Assert.That(cache.View("alice").Ledgers[0].Data.Agents[0].AgentId,
                Is.EqualTo("Correct"))

[<Test>]
let providerDisconnectImmediatelyReissuesRequestElsewhere () =
    let cache = AgentRosterCache()
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    advertise cache "alice" "laptop" "Laptop" identity |> ignore
    let fallback = cache.Disconnect("alice", first.ConnectionId) |> single
    Assert.That(fallback.ConnectionId, Is.EqualTo("alice-laptop"))

[<Test>]
let olderRefreshIdCannotRegressNewerCachedSnapshot () =
    let mutable now = DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero)
    let cache = AgentRosterCache(fun () -> now)
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    cache.Complete("alice", first.ConnectionId, completed first.Request
        (data identity "Jarvis" aHead [| agent "Agent 1" "Ready" |])) |> ignore
    now <- now.AddMinutes(1.)
    let second = cache.RefreshDue("alice") |> single
    cache.Complete("alice", second.ConnectionId, completed second.Request
        (data identity "Jarvis" bHead [| agent "Agent 1" "Working" |])) |> ignore
    cache.Complete("alice", first.ConnectionId, completed first.Request
        (data identity "Jarvis" aHead [| agent "Agent 1" "Ready" |])) |> ignore
    Assert.That(cache.View("alice").Ledgers[0].Data.Head, Is.EqualTo(bHead))

[<Test>]
let activityIsScopedByClaimAndTracksLatestDeviceWithoutChangingAgentState () =
    let cache = AgentRosterCache()
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    cache.Complete("alice", first.ConnectionId,
        completed first.Request (data identity "Jarvis" aHead [| agent "Worker 7" "Ready" |])) |> ignore
    advertise cache "alice" "laptop" "Laptop" identity |> ignore
    let early = DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero)
    let late = early.AddMinutes(1.)
    cache.RecordActivity("alice", "desktop", "Jarvis", "Worker 7", early)
    cache.RecordActivity("alice", "laptop", "Jarvis", "Worker 7", late)
    cache.RecordActivity("alice", "desktop", "Jarvis", "Worker 7", early)
    let view = cache.View("alice")
    Assert.That(view.Activity.Length, Is.EqualTo(1))
    Assert.That(view.Activity[0].DeviceName, Is.EqualTo("Laptop"))
    Assert.That(view.Activity[0].LastSeenAtUtc, Is.EqualTo(late))
    Assert.That(view.Ledgers[0].Data.Agents[0].State, Is.EqualTo("Ready"))

[<Test>]
let unknownHistoryOnRestartAndNoCrossUserActivityLeakage () =
    let cache = AgentRosterCache()
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    cache.Complete("alice", first.ConnectionId,
        completed first.Request (data identity "Jarvis" aHead [| agent "Owner" "Ready" |])) |> ignore
    cache.RecordActivity("alice", "desktop", "Jarvis", "Owner", DateTimeOffset.UtcNow)
    Assert.That(cache.View("bob").Ledgers, Is.Empty)
    Assert.That(cache.View("bob").Activity, Is.Empty)
    let restarted = AgentRosterCache()
    Assert.That(restarted.View("alice").Ledgers, Is.Empty)
    Assert.That(restarted.View("alice").Activity, Is.Empty)

[<Test>]
let refreshDeadlineFallsBackAndStaysBounded () =
    let mutable now = DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero)
    let cache = AgentRosterCache(fun () -> now)
    let first = advertise cache "alice" "desktop" "Desktop" identity |> single
    advertise cache "alice" "laptop" "Laptop" identity |> ignore
    now <- now.AddSeconds(31.)
    let fallback = cache.RefreshDue("alice") |> single
    Assert.That(fallback.ConnectionId, Is.EqualTo("alice-laptop"))
    Assert.That(fallback.Request.PreviousHead, Is.Empty)
    cache.Complete("alice", first.ConnectionId,
        completed first.Request (data identity "Jarvis" aHead [| agent "Wrong" "Ready" |])) |> ignore
    Assert.That(cache.View("alice").Ledgers[0].Data, Is.Null)