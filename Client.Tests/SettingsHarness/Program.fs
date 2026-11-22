module SettingsHarness

open System
open System.IO
open System.Threading
open Common
open Client
open Client.ConsoleTui

// Offline disposable TUI exercise. Uses the exact production ConsoleTui key
// handling and real ClientConfig Save without any SignalR, OAuth, or browser.
[<EntryPoint>]
let main args =
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
                // Real fresh process, config reload and ProjectBrowser command
                // against the same Runtime type used by the production client.
                // The isolated harness never authenticates or touches live WSL.
                if args = [| "--project-probe" |] then
                    if not (Directory.Exists effective.WorkspacePath) then
                        eprintfn "Configured workspace root unavailable: %s" effective.WorkspacePath
                        3
                    else
                        let rt = new Runtime(effective.WorkspacePath)
                        match Client.ProjectBrowser.listProjects rt with
                        | Error issue ->
                            eprintfn "Project discovery failed: %A" issue
                            4
                        | Ok projects ->
                            printfn "ROOT %s" effective.WorkspacePath
                            printfn "MACHINE %s" effective.MachineName
                            for ProjectName name in projects do
                                printfn "PROJECT %s %s" name (Path.Combine(effective.WorkspacePath, name))
                            0
                else
                    let tui = new ConsoleTui()
                    tui.SetSettingsAccess
                        { Load = ClientConfig.load
                          Save = (fun confirmed before staged ->
                              ClientConfig.applySettingsAt before.Path
                                  (if confirmed then LocalTuiConfirmed else LocalTuiUnconfirmed)
                                  effective [] (Some before.Revision) (Replace staged))
                          Effective = effective }
                    tui.SetConnectionState(Disconnected, "offline isolated settings fixture")
                    use cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25.))
                    try
                        tui.RunInputLoop(cancel.Token).GetAwaiter().GetResult()
                        if tui.ShouldQuit then 0 else 3
                    with ex ->
                        eprintfn "TUI harness failed: %s" ex.Message
                        4
