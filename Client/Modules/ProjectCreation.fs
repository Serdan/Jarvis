module Client.ProjectCreation

open System
open System.Diagnostics
open System.IO
open System.Text
open Common
open Client
open Client.IO

let private validName (name: string) =
    not (String.IsNullOrWhiteSpace name)
    && name = name.Trim()
    && name.Length <= 128
    && name <> "." && name <> ".."
    && not (name.StartsWith(".", StringComparison.Ordinal))
    && not (name.Contains("..", StringComparison.Ordinal))
    && not (name.Contains("::", StringComparison.Ordinal))
    && not (name |> Seq.exists (fun ch ->
        Char.IsControl ch || ch = '/' || ch = '\\'
        || Array.contains ch (Path.GetInvalidFileNameChars())))

let private runGit root (args: string list) =
    try
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- root
        proc.StartInfo.UseShellExecute <- false
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        for arg in args do proc.StartInfo.ArgumentList.Add arg
        if not (proc.Start()) then Error(ValidationError "Could not launch git.")
        else
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            if not (proc.WaitForExit(15000)) then
                proc.Kill(true)
                Error(ValidationError "Git project initialization timed out.")
            elif proc.ExitCode <> 0 then
                Error(ValidationError $"Git project initialization failed: {stderr}")
            else Ok stdout
    with ex -> Error(ExceptionError ex)

let private writeNew path (content: string) =
    use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    use writer = new StreamWriter(stream, new UTF8Encoding(false))
    writer.Write content

// A Jarvis project is a directory, not a registration record.
// Ledger initialization is a separate remote operation, and the new project
// is intentionally retained on failure so its state can be inspected/recovered.
let create (cmd: CreateProjectCommand) rt =
    if not (validName cmd.Name) then
        Error(ValidationError "Project name must be a single non-hidden folder name (1-128 characters).")
    elif cmd.InitializeLedger && (not cmd.InitializeGit
                                  || String.IsNullOrWhiteSpace cmd.RemoteUrl
                                  || not cmd.AcknowledgeRemoteVisibility) then
        Error(ValidationError "Ledger initialization requires Git, an explicit remote URL, and remote visibility acknowledgment.")
    elif not cmd.InitializeGit && not (String.IsNullOrWhiteSpace cmd.RemoteUrl) then
        Error(ValidationError "A remote URL requires Git initialization.")
    else
        match ProjectPaths.resolveWorkspacePath [ cmd.Name ] rt with
        | Error error -> Error error
        | Ok path ->
            if Directory.Exists path || File.Exists path then
                Error(ValidationError $"Project already exists: {cmd.Name}")
            else
                try
                    Directory.CreateDirectory path |> ignore
                    let attempt =
                        let git =
                            if cmd.InitializeGit then
                                runGit path [ "init"; "--initial-branch=main" ]
                                |> Result.bind (fun _ ->
                                    if String.IsNullOrWhiteSpace cmd.RemoteUrl then Ok ""
                                    else runGit path [ "remote"; "add"; "origin"; cmd.RemoteUrl ])
                            else Ok ""
                        git
                        |> Result.bind (fun _ ->
                            if cmd.CreateJarvisConfig then
                                writeNew (Path.Combine(path, ".jarvis.json")) "{\"tasks\":{}}\n"
                            if cmd.CreateAgentInstructions then
                                writeNew (Path.Combine(path, "AGENTS.md"))
                                    $"# {cmd.Name}\n\nProject-specific agent instructions.\n"
                            Ok ())
                        |> Result.bind (fun () ->
                            if not cmd.InitializeLedger then Ok None
                            else
                                let projectId = Guid.NewGuid()
                                let ledgerId = Guid.NewGuid()
                                OrchestrationApi.LedgerBindingConfig.initializeNew
                                    cmd.Name rt projectId ledgerId 1
                                    "origin" cmd.RemoteUrl cmd.RemoteUrl true
                                |> Result.map (fun binding ->
                                    Some {| projectId = string projectId
                                            ledgerId = string ledgerId
                                            trustedLedgerHead = binding.HighestTrustedHead |}))
                    match attempt with
                    | Error error ->
                        Error(ValidationError $"Project folder was created at {path}, but setup failed: {error}. Inspect or complete it manually; no automatic deletion was performed.")
                    | Ok ledger ->
                        Ok {| name = cmd.Name; path = path
                              gitInitialized = cmd.InitializeGit
                              remoteConfigured = not (String.IsNullOrWhiteSpace cmd.RemoteUrl)
                              jarvisConfigCreated = cmd.CreateJarvisConfig
                              agentInstructionsCreated = cmd.CreateAgentInstructions
                              ledger = ledger |}
                with ex ->
                    Error(ValidationError $"Project folder may have been created at {path}, but setup failed: {ex.Message}. Inspect it manually.")
