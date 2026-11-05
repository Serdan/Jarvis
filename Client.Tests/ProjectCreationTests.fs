module ProjectCreationTests

open System
open System.IO
open Common
open Client
open NUnit.Framework

let private fixture action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-project-creation-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try action root (GitCommandTests.TestContext root)
    finally Directory.Delete(root, true)

let private command name git cfg agents ledger remote ack =
    { Name = name; InitializeGit = git
      CreateJarvisConfig = cfg; CreateAgentInstructions = agents
      InitializeLedger = ledger; RemoteUrl = remote
      AcknowledgeRemoteVisibility = ack }

let private created = function
    | Ok result -> result
    | Error failure -> failwithf "Unexpected project creation error: %A" failure

[<Test>]
let folderOnlyIsImmediatelyDiscoverableWithoutGit () =
    fixture (fun root rt ->
        let cmd = command "NewProject" false false false false "" false
        let result = ProjectCreation.create cmd rt |> created
        Assert.That(result.name, Is.EqualTo("NewProject"))
        Assert.That(result.gitInitialized, Is.False)
        Assert.That(Directory.Exists(Path.Combine(root, "NewProject")), Is.True)
        Assert.That(Directory.Exists(Path.Combine(root, "NewProject", ".git")), Is.False)
        let projects = ProjectBrowser.listProjects rt
        match projects with
        | Ok names -> Assert.That(names |> Seq.exists (fun (ProjectName name) -> name = "NewProject"), Is.True)
        | Error e -> Assert.Fail(sprintf "%A" e)
        match ProjectCreation.create cmd rt with
        | Error(ValidationError _) -> ()
        | result -> Assert.Fail(sprintf "Existing project should never be overwritten: %A" result))

[<Test>]
let gitAndStarterFilesAreIndependentOptions () =
    fixture (fun root rt ->
        let result = ProjectCreation.create (command "Started" true true true false "" false) rt |> created
        Assert.That(result.gitInitialized, Is.True)
        let path = Path.Combine(root, "Started")
        Assert.That(Directory.Exists(Path.Combine(path, ".git")), Is.True)
        Assert.That(File.ReadAllText(Path.Combine(path, ".jarvis.json")), Does.Contain("tasks"))
        Assert.That(File.ReadAllText(Path.Combine(path, "AGENTS.md")), Does.Contain("Started"))
        let plain = ProjectCreation.create (command "ConfigOnly" false true false false "" false) rt |> created
        Assert.That(plain.gitInitialized, Is.False)
        Assert.That(File.Exists(Path.Combine(root, "ConfigOnly", ".jarvis.json")), Is.True)
        Assert.That(File.Exists(Path.Combine(root, "ConfigOnly", "AGENTS.md")), Is.False))

[<Test>]
let invalidNamesAndUnacknowledgedLedgerNeverCreateFolders () =
    fixture (fun root rt ->
        for name in [ "../escape"; "a/b"; ".."; ".hidden"; "a::tree"; "name\\bad"; "" ] do
            match ProjectCreation.create (command name false false false false "" false) rt with
            | Error _ -> ()
            | result -> Assert.Fail(sprintf "Accepted unsafe name %A: %A" name result)
        match ProjectCreation.create (command "NoConsent" true false false true "https://example.invalid/repo.git" false) rt with
        | Error(ValidationError _) -> ()
        | result -> Assert.Fail(sprintf "Unexpected ledger setup: %A" result)
        Assert.That(Directory.Exists(Path.Combine(root, "NoConsent")), Is.False)
        match ProjectCreation.create (command "MissingRemote" true false false true "" true) rt with
        | Error(ValidationError _) -> ()
        | result -> Assert.Fail(sprintf "Unexpected ledger setup: %A" result)
        Assert.That(Directory.Exists(Path.Combine(root, "MissingRemote")), Is.False))

[<Test>]
let preexistingFileOrDirectoryIsNeverOverwritten () =
    fixture (fun root rt ->
        File.WriteAllText(Path.Combine(root, "AFile"), "preserve")
        Directory.CreateDirectory(Path.Combine(root, "AFolder")) |> ignore
        for name in [ "AFile"; "AFolder" ] do
            match ProjectCreation.create (command name true true true false "" false) rt with
            | Error(ValidationError _) -> ()
            | result -> Assert.Fail(sprintf "Overwrote %s: %A" name result)
        Assert.That(File.ReadAllText(Path.Combine(root, "AFile")), Is.EqualTo("preserve"))
        Assert.That(File.Exists(Path.Combine(root, "AFolder", ".jarvis.json")), Is.False))


[<Test>]
let remoteBackedLedgerInitializesAndReturnsIdentity () =
    fixture (fun root rt ->
        let previousGitConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")
        let testGitConfig = Path.Combine(root, "test-gitconfig")
        File.WriteAllText(testGitConfig, "[user]\n  name = Jarvis Test\n  email = jarvis@example.invalid\n")
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", testGitConfig)
        try
            let bare = Path.Combine(root, "remote.git")
            use git = new System.Diagnostics.Process()
            git.StartInfo.FileName <- "git"
            git.StartInfo.WorkingDirectory <- root
            git.StartInfo.RedirectStandardOutput <- true
            git.StartInfo.RedirectStandardError <- true
            git.StartInfo.ArgumentList.Add("init")
            git.StartInfo.ArgumentList.Add("--bare")
            git.StartInfo.ArgumentList.Add(bare)
            Assert.That(git.Start(), Is.True)
            let _ = git.StandardOutput.ReadToEnd()
            let stderr = git.StandardError.ReadToEnd()
            git.WaitForExit()
            Assert.That(git.ExitCode, Is.EqualTo(0), stderr)

            let cmd = command "WithLedger" true true true true bare true
            let created = ProjectCreation.create cmd rt |> created
            Assert.That(created.ledger.IsSome, Is.True)
            Assert.That(Guid.Parse(created.ledger.Value.projectId), Is.Not.EqualTo(Guid.Empty))
            Assert.That(Guid.Parse(created.ledger.Value.ledgerId), Is.Not.EqualTo(Guid.Empty))
            let health = Client.OrchestrationApi.LedgerBindingConfig.health "WithLedger" rt
            match health with
            | Ok result ->
                Assert.That(result.projectId, Is.EqualTo(created.ledger.Value.projectId))
                Assert.That(result.trustedLedgerHead, Is.EqualTo(created.ledger.Value.trustedLedgerHead))
            | Error error -> Assert.Fail(sprintf "New ledger unavailable: %A" error)

            let bindingPath =
                Client.OrchestrationApi.LedgerBindingConfig.pathForRoot(
                    Path.Combine(root, "WithLedger"))
            if File.Exists bindingPath then File.Delete bindingPath
        finally
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGitConfig))
