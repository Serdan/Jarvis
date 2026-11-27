module ObserveAgentAgePreflight

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions

// Executable proposal only; never used by the registered production route.
type Age =
    | Elapsed of TimeSpan
    | MissingLastSeen
    | InvalidLastSeen
    | InvalidObservationClock
    | FutureLastSeen

let elapsed (observed: DateTimeOffset) (seen: DateTimeOffset option) =
    if observed = DateTimeOffset.MinValue then InvalidObservationClock
    else
        match seen with
        | None -> MissingLastSeen
        | Some t when t = DateTimeOffset.MinValue -> InvalidLastSeen
        | Some t when t > observed -> FutureLastSeen
        | Some t -> Elapsed (observed - t)

let parseRoundTrip (raw: string) =
    match DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, value -> Some value
    | _ -> None

let fromWire (observed: string) (seen: string) =
    match parseRoundTrip observed with
    | None -> InvalidObservationClock
    | Some now when now = DateTimeOffset.MinValue -> InvalidObservationClock
    | Some now when isNull seen -> elapsed now None
    | Some now ->
        match parseRoundTrip seen with
        | None -> InvalidLastSeen
        | Some timestamp -> elapsed now (Some timestamp)

type Projection =
    { State: string
      Execution: string
      ObservedAtUtc: DateTimeOffset
      LastSeenAtUtc: DateTimeOffset option
      LastSeenProvenance: string
      LastSeenIsCanonical: bool
      LastSeenAgeClock: string
      LastSeenAgeSeconds: int64 option
      LastSeenAgeStatus: string }

let project (clock: unit -> DateTimeOffset) state execution seen =
    let now = clock()
    let age = elapsed now seen
    let seconds, status =
        match age with
        | Elapsed duration -> Some (duration.Ticks / TimeSpan.TicksPerSecond), "Valid"
        | MissingLastSeen -> None, "MissingLastSeen"
        | InvalidLastSeen -> None, "InvalidLastSeen"
        | InvalidObservationClock -> None, "InvalidObservationClock"
        | FutureLastSeen -> None, "FutureLastSeen"
    { State = state; Execution = execution; ObservedAtUtc = now; LastSeenAtUtc = seen
      LastSeenProvenance = if Option.isSome seen then "LocalClientActivityCache" else "Unknown"
      LastSeenIsCanonical = false; LastSeenAgeClock = "ClientUtcWallClock"
      LastSeenAgeSeconds = seconds; LastSeenAgeStatus = status }

let equal name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let require name predicate =
    if not predicate then failwith name

let contains (needle: string) (source: string) = source.Contains(needle, StringComparison.Ordinal)

let section (start: string) (finish: string) (source: string) =
    let i = source.IndexOf(start, StringComparison.Ordinal)
    require ("missing section start: " + start) (i >= 0)
    let j = source.IndexOf(finish, i + start.Length, StringComparison.Ordinal)
    require ("missing section end: " + finish) (j >= 0)
    source.Substring(i, j - i)

let verifySource root requireWired =
    let read relative = File.ReadAllText(Path.Combine(root, relative))
    let producer = read "Client/Modules/OrchestrationApi.fs" |> section "module AgentObservationApi =" "module TaskApi ="
    let output = producer |> section "box\n" "|}))"
    let focus = producer |> section "Option.map (fun item ->" "let lastSeen ="
    let focusFields = Regex.Matches(focus.Replace("{|", ""), @"(?m)^\s*([a-z][A-Za-z0-9]*)\s*=")
                      |> Seq.cast<Match> |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
    equal "focused projection allowlist" (set ["taskId"; "title"; "lifecycle"; "attemptId"; "attemptVersion"; "attemptPhase"; "assignmentGeneration"]) focusFields
    let fields =
        Regex.Matches(output.Replace("{|", ""), @"(?m)^\s*([a-z][A-Za-z0-9]*)\s*=")
        |> Seq.cast<Match> |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
    // Exact allowlist for the existing producer: no private payloads, session IDs,
    // instructions, source content or policy booleans. Any new field needs review.
    let oldFields =
        set [ "projectName"; "observerAgentId"; "targetAgentId"; "snapshotCommit"
              "canonicalStateSource"; "canonicalStateFreshness"; "observedAtUtc"
              "agentVersion"; "stateSequence"; "state"; "execution"; "unassignedDisposition"
              "stateReason"; "stateEnteredAtUtc"; "workingTurnStartedAtUtc"; "focused"
              "operatorActionRequired"; "needsOrchestratorAssignment"; "slotAvailable"
              "pendingNotificationCount"; "pendingReviewCount"; "availableReviewCount"
              "unacknowledgedFeedbackCount"; "blockedAttemptCount"; "blockedTaskIds"
              "lastSeenAtUtc"; "lastSeenProvenance"; "lastSeenIsCanonical" ]
    let proposed = set [ "lastSeenAgeSeconds"; "lastSeenAgeStatus"; "lastSeenAgeClock" ]
    require "raw observation contract lost" (Set.isSubset oldFields fields)
    if requireWired then
        require "RED: actual ObserveAgent producer lacks proposed derived age fields" (Set.isSubset proposed fields)
        equal "wired output allowlist" (Set.union oldFields proposed) fields
    else
        equal "baseline output allowlist" oldFields fields
        equal "baseline derived age absent" Set.empty (Set.intersect fields proposed)
    require "local provenance explicitly noncanonical" (contains "lastSeenIsCanonical = false" producer)
    equal "one source clock sample" 1 (Regex.Matches(producer, @"DateTimeOffset\.UtcNow").Count)
    require "canonical read and cache lookup" (contains "LedgerBindingConfig.loadWithSnapshot" producer &&
        contains "getObservedAgentInboxAt" producer && contains "getLocalLastSeen cmd.ProjectName cmd.TargetAgentId" producer)
    for prohibited in ["RecordAgentSeen"; "activateReadyOnActivity"; "PendingNotifications |> "; "NotificationId"; "ActiveSessionId"; "InstructionLayers"; "NativeHealth"] do
        require ("unexpected producer path: " + prohibited) (not (contains prohibited producer))
    let route = read "Server/McpTools.fs" |> section "static member ObserveAgent(" "static member CreateTask("
    require "registered command maps identities" (contains "ObserveAgentCommand" route &&
        contains "ObserverAgentId = agentId; TargetAgentId = targetAgentId" route)
    let dispatch = read "Client/SignalR/Client.fs"
    require "actual local producer route" (contains "OrchestrationApi.AgentObservationApi.observe cmd" dispatch &&
        contains "rt.Tui.GetAgentLastSeen(project, agent)" dispatch)
    require "forged observer label rejected" (contains "not (String.Equals(observation.ObserverAgentId, agentId, StringComparison.Ordinal))" dispatch)
    require "observation does not receive response alerts" (contains "| ObserveAgentCommand _ -> false" dispatch)
    require "activation guarded by nonempty activity actor" (contains "if String.IsNullOrWhiteSpace agentId || not (activatesAgent agentId command)" dispatch &&
        contains "| _ when isLocalAgentActivity command -> agentId" dispatch &&
        contains "if not (String.IsNullOrWhiteSpace localAgentId) then" dispatch)
    let activity = read "Common/Messages/AgentCommand.fs" |> section "let isAgentActivity = function" "let protocolName command"
    require "observation grouped with non-activity commands" (Regex.IsMatch(activity, @"\| ObserveAgentCommand _\s+(?:\| [^\n]+\s+)*?\| GetClientActivityCommand _ -> false"))
    let ledger = read "Client/Modules/ReviewWorkflow.fs" |> section "let getObservedAgentInboxAt" "let getBranchAgentInbox"
    require "same immutable snapshot read" (contains "getAgentWorkingTurnAt binding snapshot observerAgentId" ledger &&
        contains "getAgentInboxAt binding snapshot targetAgentId" ledger)
    let cache = read "Client/Modules/ConsoleTui.fs" |> section "member _.GetAgentLastSeen(" "member _.SetAgentAccess("
    require "lookup only; no activity writes" (contains "agentSeen.TryGetValue" cache && not (contains "RecordAgentSeen" cache) &&
        not (contains "SaveAgentSeen" cache) && not (contains "agentSeen[" cache))
    let health = read "Client/Modules/NativeHealth.fs"
    require "Health uses independent server time and activity" (contains "observation.ServerNowUtc - seen >= timeout" health &&
        contains "let! latest = dependencies.Activity" health &&
        contains "sameCandidate candidate current" health)
    printfn "PASS: registered source route, output boundary, purity guards, and independent Health evidence"

let verifyModel () =
    let now = DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero)
    for seconds in [599L; 600L; 601L] do
        let seen = now.AddSeconds(-float seconds)
        for state, execution in [ "Working", "Working"; "Ready", "Stopped"; "Parked", "Stopped"; "NeedsWork", "Stopped" ] do
            let mutable samples = 0
            let result = project (fun () -> samples <- samples + 1; now.AddSeconds(float (samples - 1))) state execution (Some seen)
            equal "single sampled observation instant" 1 samples
            equal "exact boundary age" (Some seconds) result.LastSeenAgeSeconds
            equal "preserved observation instant" now result.ObservedAtUtc
            equal "preserved last seen" (Some seen) result.LastSeenAtUtc
            equal "preserved state" state result.State
            equal "preserved execution" execution result.Execution
            equal "local evidence is never canonical" false result.LastSeenIsCanonical
            equal "local evidence provenance" "LocalClientActivityCache" result.LastSeenProvenance
            equal "explicit clock" "ClientUtcWallClock" result.LastSeenAgeClock
            equal "status independent of state and threshold" "Valid" result.LastSeenAgeStatus
    let check name seen seconds status =
        let result = project (fun () -> now) "Working" "Working" seen
        equal (name + " seconds") seconds result.LastSeenAgeSeconds
        equal (name + " status") status result.LastSeenAgeStatus
        equal (name + " preserves input") seen result.LastSeenAtUtc
    check "missing" None None "MissingLastSeen"
    check "future by tick" (Some(now.AddTicks 1L)) None "FutureLastSeen"
    check "future by second" (Some(now.AddSeconds 1.)) None "FutureLastSeen"
    check "invalid sentinel" (Some DateTimeOffset.MinValue) None "InvalidLastSeen"
    check "equal" (Some now) (Some 0L) "Valid"
    check "subsecond floor" (Some(now.AddTicks(-5999999999L))) (Some 599L) "Valid"
    check "equivalent offset" (Some(now.ToOffset(TimeSpan.FromHours 2.).AddSeconds(-601.))) (Some 601L) "Valid"
    let absent = project (fun () -> now) "Ready" "Ready" None
    equal "missing provenance" "Unknown" absent.LastSeenProvenance
    let format (t: DateTimeOffset) = t.ToString("O", CultureInfo.InvariantCulture)
    for malformed in [ ""; "bogus"; "2026-11-27T12:00:00"; "2026-02-30T12:00:00.0000000+00:00" ] do
        equal "malformed last seen" InvalidLastSeen (fromWire (format now) malformed)
        equal "malformed observation" InvalidObservationClock (fromWire malformed (format now))
    equal "null wire input" MissingLastSeen (fromWire (format now) null)
    equal "null clock" InvalidObservationClock (fromWire null null)
    equal "invalid observation sentinel" InvalidObservationClock (elapsed DateTimeOffset.MinValue None)
    equal "wire round trip" (Elapsed(TimeSpan.FromSeconds 600.)) (fromWire (format now) (format (now.AddSeconds(-600.))))
    // Generated monotonic arithmetic checks, including days beyond Int32 seconds.
    let generated = Random(20261127)
    let ticksCases = [ yield! [0L; 1L; 9999999L; 6000000000L; 31536000000000000L]
                       for _ in 1..1000 do yield generated.NextInt64(0L, now.UtcTicks) ]
    for ticks in ticksCases do
        let t = now.AddTicks(-ticks)
        let result = project (fun () -> now) "Working" "Working" (Some t)
        equal "generated valid elapsed" (Some(ticks / TimeSpan.TicksPerSecond)) result.LastSeenAgeSeconds
    printfn "PASS: deterministic model, boundary/state matrix, missing/future/malformed, offset and precision"

[<EntryPoint>]
let main args =
    let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let requireWired = Array.contains "--require-wired" args
    try
        verifySource root requireWired
        verifyModel ()
        0
    with ex ->
        eprintfn "%s" ex.Message
        1
