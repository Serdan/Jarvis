module SettingsHarness

open System
open System.Threading
open Client
open Client.ConsoleTui

// Offline disposable TUI exercise. Uses the exact production ConsoleTui key
// handling and real ClientConfig Save without any SignalR, OAuth, or browser.
[<EntryPoint>]
let main _ =
    if Environment.GetEnvironmentVariable("JARVIS_W24_ISOLATED_TUI") <> "yes" then
        eprintfn "Isolated settings harness requires JARVIS_W24_ISOLATED_TUI=yes."
        2
    else
        match ClientConfig.load() with
        | Error error -> eprintfn "%s" error; 2
        | Ok snapshot ->
            match ClientConfig.resolve snapshot.Stored [] with
            | Error error -> eprintfn "%s" error; 2
            | Ok effective ->
                let tui = new ConsoleTui()
                tui.SetSettingsAccess
                    { Load = ClientConfig.load
                      Save = ClientConfig.save
                      Effective = effective }
                tui.SetConnectionState(Disconnected, "offline isolated settings fixture")
                use cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25.))
                try
                    tui.RunInputLoop(cancel.Token).GetAwaiter().GetResult()
                    if tui.ShouldQuit then 0 else 3
                with ex ->
                    eprintfn "TUI harness failed: %s" ex.Message
                    4
