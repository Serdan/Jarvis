#r "../Common/bin/Release/net11.0/Common.dll"
#r "../Client/bin/Release/net11.0/JarvisClient.dll"
open System
open System.IO
open Client

let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")
let tui = ConsoleTui.ConsoleTui()
let rt = Runtime(".", tui, FullTrust)
let action : BrowserSchedules.ScheduledAction =
    { Id = "test"; DueAtUtc = DateTimeOffset.UtcNow; Url = "https://example.invalid"
      ProjectName = "test"; ScriptPath = "not-a-script.txt"; Alias = ""; ArgsJson = "{}"
      Active = false; WindowRegistration = "test"; CloseTabAfterExecution = true
      RepeatEverySeconds = 0; Status = ""; RegistrationId = ""; ConversationUrl = ""
      Cleanup = ""; AttemptId = ""; RevisionId = ""; Error = ""; UpdatedAtUtc = DateTimeOffset.UtcNow
      ActionKind = "BrowserScript"; TriggerKind = "Once"; Executable = ""; CommandArgs = []
      WorkingDirectory = ""; TimeoutSeconds = 1; MaxOutputBytes = 100
      IdleAgentId = ""; IdleSeconds = 0; LastActivityAtUtc = DateTimeOffset.MinValue
      StartedAtUtc = DateTimeOffset.MinValue; Disabled = false; LastResult = "" }
let run () =
    Program.executeScheduledAction (fun () -> ClientConfig.capacityFenceModeAt path) rt tui None action
    |> fun t -> t.GetAwaiter().GetResult()
let expect expected =
    match run() with
    | Error (_, reason) when reason = expected -> ()
    | x -> failwithf "expected %s, got %A" expected x
try
    File.WriteAllText(path, "{\"schemaVersion\":1,\"capacityFenceEnabled\":true}")
    expect "CapacityFenceHold: scheduled browser launch denied"
    File.WriteAllText(path, "{\"schemaVersion\":1,\"capacityFenceEnabled\":null}")
    expect "CapacityFenceHold: scheduled browser launch denied"
    File.WriteAllText(path, "{\"schemaVersion\":1,\"capacityFenceEnabled\":false}")
    expect "InvalidScriptPath"
    File.Delete(path)
    expect "InvalidScriptPath"
    printfn "Scheduled callback: enabled, invalid, disabled, missing passed"
finally
    if File.Exists(path) then File.Delete(path)
