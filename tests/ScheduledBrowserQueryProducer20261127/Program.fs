open System
open System.Text
open System.Text.Json
open Client.BrowserSchedules
open Fixture

let equal label expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
let check label condition = if not condition then failwith label
let query = { AuthorizedProject = "Jarvis"; History = ScheduleHistory.Current; Action = ScheduleAction.Browser }
let parse (json: string) =
    use doc = JsonDocument.Parse json
    doc.RootElement.Clone()
let prop name (x: JsonElement) = x.GetProperty(name: string)
let text name x = (prop name x).GetString()
let schedules x = (prop "schedules" x).EnumerateArray() |> Seq.toList
let rowIds xs = xs |> List.map (text "id")
let get id xs = xs |> List.find (fun x -> text "id" x = id)
let state id expected xs = equal id expected (text "state" (get id xs))
let seconds id expected xs = equal id expected ((prop "secondsUntilCandidateDue" (get id xs)).GetDouble())
let noCandidate id xs =
    for name in ["candidateDueAtUtc"; "secondsUntilCandidateDue"] do
        equal (id + "." + name) JsonValueKind.Null ((prop name (get id xs)).ValueKind)
let assertLegacy (store: Service) input =
    let expected = input |> List.sortBy (fun a -> a.DueAtUtc) |> List.map legacyDescribe |> JsonSerializer.Serialize
    equal "legacy List bytes" expected (store.List())
    for item in input do
        equal ("legacy Get bytes " + item.Id) (Ok(JsonSerializer.Serialize(legacyDescribe item))) (store.Get item.Id)
    equal "legacy missing Get" (Error "ScheduleNotFound") (store.Get "not-present")
    expected
let run (store: Service) q = store.Query(q) |> parse |> schedules
let reject (store: Service) q =
    try store.Query(q) |> ignore; false
    with :? ArgumentException -> true

[<EntryPoint>]
let main _ =
    let scopedJson =
        withProducer rows (fun store clockCalls ->
            let legacy = assertLegacy store rows
            let beforeClock = clockCalls()
            let json = store.Query(query)
            equal "one observation" (beforeClock + 1) (clockCalls())
            let envelope = parse json
            equal "clock value" now ((prop "observedAtUtc" envelope).GetDateTimeOffset())
            equal "clock source" "clientSchedulerClock" (text "clockSource" envelope)
            let result = schedules envelope
            equal "actual scoped membership" expected (rowIds result |> Set.ofList)
            equal "exact deterministic ordering" ["wait"; "unknown"; "due"; "future-status"; "a"; "b"; "suspend"] (rowIds result)
            equal "normalized action" "BrowserScript" (text "actionKind" (get "a" result))
            equal "normalized trigger" "Once" (text "triggerKind" (get "a" result))
            equal "canonical checkout" "Jarvis" (text "projectName" (get "due" result))
            seconds "a" 120. result
            seconds "due" 0. result
            state "due" "dueCandidate" result
            state "wait" "waitingForActivity" result
            state "suspend" "suspendedNeedsAttention" result
            state "unknown" "outcomeUnknown" result
            state "future-status" "unknownStatus" result
            for id in ["wait"; "suspend"; "unknown"; "future-status"] do noCandidate id result
            let all = run store { query with History = ScheduleHistory.All }
            equal "all scoped history" 45 all.Length
            state "completed" "terminal" all
            noCandidate "completed" all
            equal "exact history" 36 (run store { query with History = ScheduleHistory.ExactStatus "canceled" }).Length
            equal "exact status not prefix" 0 (run store { query with History = ScheduleHistory.ExactStatus "cancel" }).Length
            equal "command only" ["command"] (run store { query with Action = ScheduleAction.Command } |> rowIds)
            equal "any action" (Set.add "command" expected) (run store { query with Action = ScheduleAction.Any } |> rowIds |> Set.ofList)
            equal "other scope" 6 (run store { query with AuthorizedProject = "Favn" }).Length
            for project in [""; "Jarvis::worker"; "Jarvis::"; "../Jarvis"] do
                check ("invalid scope " + project) (reject store { query with AuthorizedProject = project })
            check "blank exact status" (reject store { query with History = ScheduleHistory.ExactStatus " " })
            equal "envelope allowlist" (set ["observedAtUtc"; "clockSource"; "schedules"]) (envelope.EnumerateObject() |> Seq.map (fun x -> x.Name) |> Set.ofSeq)
            for item in result do
                equal "row allowlist" (set ["id"; "projectName"; "actionKind"; "triggerKind"; "status"; "state"; "disabled"; "dueAtUtc"; "candidateDueAtUtc"; "secondsUntilCandidateDue"; "repeatEverySeconds"; "updatedAtUtc"]) (item.EnumerateObject() |> Seq.map (fun x -> x.Name) |> Set.ofSeq)
            for secret in ["PROMPT_SECRET"; "ARG_SECRET"; "ERROR_SECRET"; "RESULT_SECRET"; "URL_SECRET"; "ALIAS_SECRET"; "private-path"; "private-executable"; "private-directory"] do
                check ("private field " + secret) (not (json.Contains secret))
            equal "legacy unaffected after query" legacy (assertLegacy store rows)
            printfn "Actual producer: %d legacy rows / %d UTF-8 bytes -> %d scoped rows / %d UTF-8 bytes" rows.Length (Encoding.UTF8.GetByteCount legacy) result.Length (Encoding.UTF8.GetByteCount json)
            json)
    withProducer (List.rev rows) (fun store _ -> equal "reverse input exact scoped bytes" scopedJson (store.Query query))
    let extra =
        [ { row "wake" "Jarvis" "pending" "BrowserScript" "Every" 120. with
              ScriptPath = "browser-scripts/chatgpt/launchAgent.js"; ArgsJson = "{\"resumeReady\":true,\"secret\":\"PROMPT_SECRET\"}"
              CheckinWakeAtUtc = now.AddSeconds 5. }
          { row "fakeWake" "Jarvis" "pending" "BrowserScript" "Every" 120. with CheckinWakeAtUtc = now.AddSeconds 5. }
          { row "disabled" "Jarvis" "pending" "BrowserScript" "Every" 20. with Disabled = true }
          row "idle" "Jarvis" "pending" "BrowserScript" "AfterAgentIdle" -1.
          { row "noDue" "Jarvis" "pending" "BrowserScript" "Once" 1. with DueAtUtc = DateTimeOffset.MinValue }
          row "futureTrigger" "Jarvis" "pending" "BrowserScript" "Later" 1.
          row "unknownIdle" "Jarvis" "pending" "FutureAction" "AfterAgentIdle" 1.
          row "unknownBoth" "Jarvis" "pending" "FutureAction" "Later" 1.
          row "terminalUnknown" "Jarvis" "failed" "FutureAction" "Later" 1.
          row "suspendedUnknown" "Jarvis" "suspended" "FutureAction" "AfterAgentIdle" 1.
          row "badScope" "Jarvis::" "pending" "BrowserScript" "Once" 1. ]
    withProducer extra (fun store _ ->
        let result = run store { query with History = ScheduleHistory.All; Action = ScheduleAction.Any }
        check "invalid stored scope excluded" (not (List.contains "badScope" (rowIds result)))
        seconds "wake" 5. result
        seconds "fakeWake" 120. result
        state "disabled" "disabled" result
        state "idle" "awaitingIdleVerification" result
        seconds "idle" 0. result
        state "noDue" "deadlineUnknown" result
        state "futureTrigger" "unknownTrigger" result
        state "unknownIdle" "unknownAction" result
        state "unknownBoth" "unknownAction" result
        state "terminalUnknown" "terminal" result
        state "suspendedUnknown" "suspendedNeedsAttention" result
        for id in ["disabled"; "noDue"; "futureTrigger"; "unknownIdle"; "unknownBoth"; "terminalUnknown"; "suspendedUnknown"] do noCandidate id result
        equal "unknown raw timestamp preserved" (now.AddSeconds 1.) ((prop "dueAtUtc" (get "unknownIdle" result)).GetDateTimeOffset()))
    printfn "PASS: actual query, membership, ordering, legacy bytes, timing, privacy, zero dispatch/store writes"
    0
