module ContractProgram
open System
open System.IO
open System.Text.Json
open Common
open Client.BrowserSchedules
open Fixture
open QueryModel

let equal label expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
let check label condition = if not condition then failwith label

let query = { AuthorizedProject = "Jarvis"; History = Current; Action = Browser }
let get (rows: Projection list) id = rows |> List.find (fun x -> x.id = id)
let nullable value = Nullable(value)

let contract () =
    let actual = project now query rows
    equal "scope completeness" expected (actual |> List.map (fun x -> x.id) |> Set.ofList)
    equal "no duplicates" actual.Length (actual |> List.map (fun x -> x.id) |> Set.ofList |> Set.count)
    equal "input-order independent" actual (project now query (List.rev rows))
    equal "tie order" ["a"; "b"; "suspend"] (actual |> List.filter (fun x -> x.dueAtUtc = nullable (now.AddSeconds 120.)) |> List.map (fun x -> x.id))
    equal "legacy default action" "BrowserScript" (get actual "a").actionKind
    equal "legacy default trigger" "Once" (get actual "a").triggerKind
    equal "checkout canonical scope" "Jarvis" (get actual "due").projectName
    equal "future seconds" (nullable 120.) (get actual "a").secondsUntilCandidateDue
    equal "overdue seconds" (nullable 0.) (get actual "due").secondsUntilCandidateDue
    equal "overdue state" "dueCandidate" (get actual "due").state
    equal "waiting does not invent deadline" (Nullable<double>()) (get actual "wait").secondsUntilCandidateDue
    equal "suspension is not active cadence" "suspendedNeedsAttention" (get actual "suspend").state
    equal "suspension has no countdown" (Nullable<double>()) (get actual "suspend").secondsUntilCandidateDue
    equal "unknown outcome" "outcomeUnknown" (get actual "unknown").state
    equal "unknown status retained" "futureStatus" (get actual "future-status").status
    equal "unknown status classified" "unknownStatus" (get actual "future-status").state
    let history = project now { query with History = All } rows
    equal "explicit scoped history count" 45 history.Length
    equal "explicit terminal lookup" 36 (project now { query with History = ExactStatus "canceled" } rows).Length
    equal "terminal has no future countdown" (Nullable<double>()) (get history "completed").secondsUntilCandidateDue
    equal "command-only" ["command"] (project now { query with Action = Command } rows |> List.map (fun x -> x.id))
    equal "all actions" (Set.add "command" expected) (project now { query with Action = Any } rows |> List.map (fun x -> x.id) |> Set.ofList)
    equal "other project cannot leak Jarvis" 6 (project now { query with AuthorizedProject = "Favn" } rows).Length
    let reject selector =
        try project now { query with AuthorizedProject = selector } rows |> ignore; false
        with :? ArgumentException -> true
    check "reject qualified/blank/invalid query identity" ([""; "Jarvis::worker"; "Jarvis::"; "../Jarvis"] |> List.forall reject)
    let extra =
        [ { row "wake" "Jarvis" "pending" "BrowserScript" "Every" 120. with
              ScriptPath = "browser-scripts/chatgpt/launchAgent.js"; ArgsJson = """{"resumeReady":true,"secret":"PROMPT_SECRET"}"""
              CheckinWakeAtUtc = now.AddSeconds 5.; Error = "PROMPT_SECRET"; LastResult = "ARG_SECRET" }
          { row "fakeWake" "Jarvis" "pending" "BrowserScript" "Every" 120. with CheckinWakeAtUtc = now.AddSeconds 5. }
          { row "running" "Jarvis" "running" "BrowserScript" "Every" -10. with Disabled = true }
          { row "disabled" "Jarvis" "pending" "BrowserScript" "Every" 20. with Disabled = true }
          row "idle" "Jarvis" "pending" "BrowserScript" "AfterAgentIdle" -1.
          { row "noDue" "Jarvis" "pending" "BrowserScript" "Once" 1. with DueAtUtc = DateTimeOffset.MinValue }
          row "futureTrigger" "Jarvis" "pending" "BrowserScript" "Later" 1.
          row "badScope" "Jarvis::" "pending" "BrowserScript" "Once" 1. ]
    let projected = project now query extra
    equal "actual resume predicate honors wake" (nullable 5.) (get projected "wake").secondsUntilCandidateDue
    equal "unrelated wake ignored" (nullable 120.) (get projected "fakeWake").secondsUntilCandidateDue
    equal "canceled running occurrence still visible" "runningCancellationRequested" (get projected "running").state
    equal "running no countdown" (Nullable<double>()) (get projected "running").secondsUntilCandidateDue
    equal "disabled no countdown" (Nullable<double>()) (get projected "disabled").secondsUntilCandidateDue
    equal "idle not proof of eligibility" "awaitingIdleVerification" (get projected "idle").state
    equal "missing deadline" "deadlineUnknown" (get projected "noDue").state
    equal "unknown trigger" "unknownTrigger" (get projected "futureTrigger").state
    check "invalid stored selector not coerced" (projected |> List.forall (fun x -> x.id <> "badScope"))
    let envelope = response now query (rows @ extra)
    equal "observation uses injected clock" now envelope.observedAtUtc
    equal "clock provenance" "clientSchedulerClock" envelope.clockSource
    let json = JsonSerializer.Serialize envelope
    for secret in ["PROMPT_SECRET"; "ARG_SECRET"; "ERROR_SECRET"; "RESULT_SECRET"; "URL_SECRET"; "ALIAS_SECRET"; "private-path"; "private-executable"; "private-directory"; "launchAgent.js"] do
        check ("disclosure: " + secret) (not (json.Contains secret))
    // Current explicit legacy/no-filter expansion uses the existing producer unchanged,
    // not the scoped model or a newly authorized account-wide read.
    withProducer (fun legacy ->
        equal "legacy history still includes every row" (rows |> List.map (fun x -> x.Id) |> Set.ofList) (ids legacy)
        for secret in ["PROMPT_SECRET"; "ARG_SECRET"; "URL_SECRET"] do
            check ("direct legacy secret: " + secret) (not (legacy.Contains secret)))
    printfn "PASS: scoped contract, legacy actual producer, deterministic order, clocks, privacy, no dispatch/write (%d mixed rows)" rows.Length
