module QueryModel
open System
open Common
open Client.BrowserSchedules

// Proposed isolated contract, not production dispatch or authorization.
type History = Current | All | ExactStatus of string
type Action = Browser | Command | Any
type Query = { AuthorizedProject: string; History: History; Action: Action }

let private terminal =
    set ["canceled"; "completed"; "failed"; "held"; "heldLaunches"; "noEligibleReady"]
let private effectiveAction (a: ScheduledAction) =
    if String.IsNullOrWhiteSpace a.ActionKind then "BrowserScript" else a.ActionKind
let private effectiveTrigger (a: ScheduledAction) =
    if String.IsNullOrWhiteSpace a.TriggerKind then
        if a.RepeatEverySeconds > 0 then "Every" else "Once"
    else a.TriggerKind

let private scopeMatches project (a: ScheduledAction) =
    ProjectSelector.identity a.ProjectName = Some project

let private historyMatches history (a: ScheduledAction) =
    match history with
    | All -> true
    | ExactStatus status -> a.Status = status
    | Current -> not (terminal.Contains a.Status)

let private actionMatches action (a: ScheduledAction) =
    match action with
    | Any -> true
    | Browser -> effectiveAction a = "BrowserScript"
    | Command -> effectiveAction a = "RunCommand"

let private candidateDeadline (a: ScheduledAction) =
    if isReadyResume a && a.CheckinWakeAtUtc <> DateTimeOffset.MinValue then
        min a.DueAtUtc a.CheckinWakeAtUtc
    else a.DueAtUtc

let private timing (now: DateTimeOffset) (a: ScheduledAction) =
    match a.Status with
    | "running" -> (if a.Disabled then "runningCancellationRequested" else "running"), Nullable<DateTimeOffset>(), Nullable<double>()
    | "waitingActivity" -> (if a.Disabled then "disabled" else "waitingForActivity"), Nullable(), Nullable()
    | "suspended" -> "suspendedNeedsAttention", Nullable(), Nullable()
    | "outcomeUnknown" -> "outcomeUnknown", Nullable(), Nullable()
    | "pending" when a.Disabled -> "disabled", Nullable(), Nullable()
    | "pending" when a.DueAtUtc = DateTimeOffset.MinValue -> "deadlineUnknown", Nullable(), Nullable()
    | "pending" ->
        let due = candidateDeadline a
        let state =
            if effectiveTrigger a = "AfterAgentIdle" then "awaitingIdleVerification"
            elif effectiveTrigger a <> "Once" && effectiveTrigger a <> "Every" then "unknownTrigger"
            elif effectiveAction a <> "BrowserScript" && effectiveAction a <> "RunCommand" then "unknownAction"
            elif due <= now then "dueCandidate"
            else "scheduledCandidate"
        let seconds =
            if state = "unknownTrigger" || state = "unknownAction" then Nullable()
            else Nullable(max 0.0 (due - now).TotalSeconds)
        state, Nullable(due), seconds
    | status when terminal.Contains status -> "terminal", Nullable(), Nullable()
    | _ -> "unknownStatus", Nullable(), Nullable()

type Projection =
    { id: string; projectName: string; actionKind: string; triggerKind: string
      status: string; state: string; disabled: bool
      dueAtUtc: Nullable<DateTimeOffset>; candidateDueAtUtc: Nullable<DateTimeOffset>
      secondsUntilCandidateDue: Nullable<double>; repeatEverySeconds: int; updatedAtUtc: DateTimeOffset }

let project (now: DateTimeOffset) (query: Query) (rows: ScheduledAction list) =
    // Caller must resolve and authorize this canonical project BEFORE invoking.
    if ProjectSelector.identity query.AuthorizedProject <> Some query.AuthorizedProject then
        invalidArg "query" "A canonical project identity is required"
    match query.History with
    | ExactStatus status when String.IsNullOrWhiteSpace status ->
        invalidArg "query" "Exact status must be nonblank"
    | _ -> ()
    rows
    |> List.filter (fun a -> scopeMatches query.AuthorizedProject a && historyMatches query.History a && actionMatches query.Action a)
    |> List.sortBy (fun a -> a.DueAtUtc, a.Id)
    |> List.map (fun a ->
        let state, candidate, seconds = timing now a
        { id = a.Id
          projectName = query.AuthorizedProject
          actionKind = effectiveAction a
          triggerKind = effectiveTrigger a
          status = a.Status
          state = state
          disabled = a.Disabled
          dueAtUtc = (if a.DueAtUtc = DateTimeOffset.MinValue then Nullable() else Nullable(a.DueAtUtc))
          candidateDueAtUtc = candidate
          secondsUntilCandidateDue = seconds
          repeatEverySeconds = a.RepeatEverySeconds
          updatedAtUtc = a.UpdatedAtUtc })

let response now query rows =
    {| observedAtUtc = now; clockSource = "clientSchedulerClock"
       schedules = project now query rows |}
