module Fixture
open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Client.BrowserSchedules

let now = DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero)
let row id project status action trigger due =
    { Id = id; ProjectName = project; Status = status; ActionKind = action
      TriggerKind = trigger; DueAtUtc = now.AddSeconds(due)
      Url = "https://example.test/private-url?secret=URL_SECRET"
      ScriptPath = "private-path.js"; Alias = "ALIAS_SECRET"
      ArgsJson = """{"prompt":"PROMPT_SECRET"}"""; Active = false
      WindowRegistration = "window"; CloseTabAfterExecution = true
      RepeatEverySeconds = (if trigger = "Every" then 60 else 0)
      RegistrationId = ""; ConversationUrl = ""; Cleanup = ""; AttemptId = ""
      RevisionId = "revision-" + id; Error = "ERROR_SECRET"; UpdatedAtUtc = now
      Executable = "private-executable"; CommandArgs = ["ARG_SECRET"]
      WorkingDirectory = "private-directory"; TimeoutSeconds = 60; MaxOutputBytes = 1024
      IdleAgentId = ""; IdleSeconds = 60; LastActivityAtUtc = DateTimeOffset.MinValue
      StartedAtUtc = DateTimeOffset.MinValue; Disabled = (status = "canceled")
      CheckinWakeAtUtc = DateTimeOffset.MinValue; LastResult = "RESULT_SECRET" }

let rows =
    [ for i in 1..36 do
          yield row (sprintf "history-%02d" i) "Jarvis" "canceled" "BrowserScript" "Once" (float -i)
      for i in 1..6 do
          yield row (sprintf "favn-%02d" i) "Favn" "pending" "BrowserScript" "Once" 10.
      yield row "b" "Jarvis" "pending" "BrowserScript" "Every" 120.
      yield row "a" "Jarvis" "pending" "" "" 120.
      yield row "due" "Jarvis::worker" "pending" "BrowserScript" "Once" -1.
      yield row "wait" "Jarvis" "waitingActivity" "BrowserScript" "AfterAgentIdle" -100.
      yield row "suspend" "Jarvis" "suspended" "BrowserScript" "Every" 120.
      yield row "unknown" "Jarvis" "outcomeUnknown" "BrowserScript" "Once" -5.
      yield row "future-status" "Jarvis" "futureStatus" "BrowserScript" "Once" 10.
      yield row "command" "Jarvis" "pending" "RunCommand" "Once" 10.
      yield row "lookalike" "JarvisOther" "pending" "BrowserScript" "Once" 10.
      yield row "completed" "Jarvis" "completed" "BrowserScript" "Once" -5.
      yield row "failed" "Jarvis" "failed" "BrowserScript" "Once" -5. ]

let expected = set ["a"; "b"; "due"; "wait"; "suspend"; "unknown"; "future-status"]
let ids json =
    use doc = JsonDocument.Parse(json: string)
    doc.RootElement.EnumerateArray()
    |> Seq.map (fun x -> x.GetProperty("id").GetString()) |> Set.ofSeq


let withProducer input consume =
    let dir = Path.Combine(Path.GetTempPath(), "jarvis-scheduled-query-producer-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "schedules.json")
    try
        File.WriteAllText(path, JsonSerializer.Serialize({| schemaVersion = 3; actions = input |}))
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let before = File.ReadAllBytes(path)
        let mutable calls = 0
        let execute (_: ScheduledAction) =
            calls <- calls + 1
            Task.FromResult(Ok "must-not-run")
        let mutable clockCalls = 0
        use store = new Service(path, execute, now=(fun () -> clockCalls <- clockCalls + 1; now))
        let result = consume store (fun () -> clockCalls)
        if before <> File.ReadAllBytes(path) || calls <> 0 then failwith "Unexpected store write or dispatch"
        result
    finally Directory.Delete(dir, true)

// Frozen baseline describe body: independent byte-compatibility oracle, not
// a query policy or production implementation.
let legacyDescribe (a: ScheduledAction) =
    let origin =
        match Uri.TryCreate(a.Url, UriKind.Absolute) with
        | true, uri when uri.Scheme = "https" || uri.Scheme = "http" ->
            uri.GetLeftPart(UriPartial.Authority)
        | _ -> ""
    {| id = a.Id
       dueAtUtc = a.DueAtUtc
       checkinWakeAtUtc = a.CheckinWakeAtUtc
       status = a.Status
       projectName = a.ProjectName
       actionKind = (if String.IsNullOrWhiteSpace a.ActionKind then "BrowserScript" else a.ActionKind)
       triggerKind = (if String.IsNullOrWhiteSpace a.TriggerKind then
                          (if a.RepeatEverySeconds > 0 then "Every" else "Once") else a.TriggerKind)
       executable = a.Executable
       commandArgumentCount = (if isNull (box a.CommandArgs) then 0 else a.CommandArgs.Length)
       workingDirectory = a.WorkingDirectory
       timeoutSeconds = a.TimeoutSeconds
       maxOutputBytes = a.MaxOutputBytes
       scriptPath = a.ScriptPath
       origin = origin
       alias = a.Alias
       windowRegistration = a.WindowRegistration
       repeatEverySeconds = a.RepeatEverySeconds
       idleAgentId = a.IdleAgentId
       idleSeconds = a.IdleSeconds
       lastActivityAtUtc = a.LastActivityAtUtc
       startedAtUtc = a.StartedAtUtc
       disabled = a.Disabled
       closeTabAfterExecution = a.CloseTabAfterExecution
       registrationId = a.RegistrationId
       conversationUrl = a.ConversationUrl
       cleanup = a.Cleanup
       attemptId = a.AttemptId
       lastResult = a.LastResult
       error = a.Error
       updatedAtUtc = a.UpdatedAtUtc |}
