module AgentInstructionsTests

open System
open System.IO
open Client
open Client.AgentInstructions
open Common
open NUnit.Framework

let private workspace test =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-instructions-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
    try test root (Runtime(root))
    finally if Directory.Exists root then Directory.Delete(root, true)

let private create root name (content: string) =
    let path = Path.Combine(root, name)
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, content)
    path

let private command path =
    ReadFileCommand { ProjectName = "Demo"; FilePath = path; StartLine = None; EndLine = None; IncludeLineNumbers = None }

let private docs rt cmd =
    match resolve rt cmd with
    | Ok documents -> documents
    | Error error -> failwithf "Unexpected instructions error: %A" error

[<Test>]
let ordered_hierarchy_and_scope () =
    workspace (fun root rt ->
        create root "AGENTS.md" "workspace" |> ignore
        create root "Demo/AGENTS.md" "project" |> ignore
        create root "Demo/src/AGENTS.md" "source" |> ignore
        create root "Demo/src/core/AGENTS.md" "core" |> ignore
        create root "Demo/other/AGENTS.md" "unrelated" |> ignore
        let result = docs rt (command "src/core/a.cs")
        Assert.That(result |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "AGENTS.md"; "Demo/AGENTS.md"; "Demo/src/AGENTS.md"; "Demo/src/core/AGENTS.md" ] :> obj))
        Assert.That(result |> List.map (fun d -> d.Scope), Is.EqualTo([ "workspace"; "project"; "project"; "project" ] :> obj))
        Assert.That(result |> List.map (fun d -> d.Content), Is.EqualTo([ "workspace"; "project"; "source"; "core" ] :> obj))
        Assert.That(result |> List.forall (fun d -> d.Sha256.StartsWith("sha256:") && d.Sha256.Length = 71), Is.True))

[<Test>]
let multiple_paths_deduplicate_ancestors () =
    workspace (fun root rt ->
        create root "Demo/AGENTS.md" "project" |> ignore
        create root "Demo/src/AGENTS.md" "source" |> ignore
        create root "Demo/src/a/AGENTS.md" "a" |> ignore
        create root "Demo/src/b/AGENTS.md" "b" |> ignore
        create root "Demo/other/AGENTS.md" "other" |> ignore
        let cmd = ReadFilesCommand { ProjectName = "Demo"; FilePaths = ["src/b/t.cs"; "src/a/t.cs"] }
        Assert.That(docs rt cmd |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "Demo/AGENTS.md"; "Demo/src/AGENTS.md"; "Demo/src/a/AGENTS.md"; "Demo/src/b/AGENTS.md" ] :> obj)))

[<Test>]
let missing_empty_and_changed_files () =
    workspace (fun root rt ->
        Assert.That(docs rt (command "a.cs"), Is.Empty)
        let path = create root "Demo/AGENTS.md" ""
        Assert.That(docs rt (command "a.cs"), Is.Empty)
        File.WriteAllText(path, "first")
        let first = (docs rt (command "a.cs")).Head
        File.WriteAllText(path, "second")
        let second = (docs rt (command "a.cs")).Head
        Assert.That(first.Content, Is.EqualTo("first"))
        Assert.That(second.Content, Is.EqualTo("second"))
        Assert.That(second.Sha256, Is.Not.EqualTo(first.Sha256)))

[<Test>]
let invalid_encoding_and_size_fail () =
    workspace (fun root rt ->
        let path = create root "Demo/AGENTS.md" "valid"
        File.WriteAllBytes(path, [| 0xffuy; 0xfeuy |])
        match resolve rt (command "a.cs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("UTF-8"))
        | other -> Assert.Fail(sprintf "Expected invalid UTF-8: %A" other)
        File.WriteAllText(path, String('a', 32 * 1024))
        Assert.That((docs rt (command "a.cs")).Head.Content.Length, Is.EqualTo(32 * 1024))
        File.WriteAllText(path, String('a', 256 * 1024 + 1))
        match resolve rt (command "a.cs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("262144"))
        | other -> Assert.Fail(sprintf "Expected file limit: %A" other))

[<Test>]
let combined_limit_fail () =
    workspace (fun root rt ->
        create root "AGENTS.md" (String('a', 200000)) |> ignore
        create root "Demo/AGENTS.md" (String('b', 200000)) |> ignore
        create root "Demo/src/AGENTS.md" (String('c', 200000)) |> ignore
        match resolve rt (command "src/a.cs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("524288"))
        | other -> Assert.Fail(sprintf "Expected total size limit: %A" other))

[<Test>]
let reject_symlinked_agents () =
    workspace (fun root rt ->
        let other = create root "Demo/other.md" "untrusted"
        let link = Path.Combine(root, "Demo", "AGENTS.md")
        try
            File.CreateSymbolicLink(link, other) |> ignore
        with
        | :? UnauthorizedAccessException -> Assert.Ignore("Symlinks unavailable")
        | :? PlatformNotSupportedException -> Assert.Ignore("Symlinks unsupported")
        match resolve rt (command "a.cs") with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail(sprintf "Expected link rejection: %A" other))

[<Test>]
let invalid_instruction_prevents_file_write () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-instructions-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            create root "Demo/AGENTS.md" (String('x', 256 * 1024 + 1)) |> ignore
            let target = Path.Combine(root, "Demo", "should-not-exist.txt")
            let command =
                WriteFileCommand
                    { ProjectName = "Demo"
                      FilePath = "should-not-exist.txt"
                      Content = "side effect"
                      FileWriteMode = FileWriteMode.Write
                      ExpectedHash = None
                      CreateParents = None }
            let! outcome, _ = Client.SignalR.Client.receiveCommandWithInstructions (Runtime(root)) command
            match outcome with
            | Error(Client.ValidationError message) ->
                Assert.That(message, Does.Contain("AGENTS.md"))
            | other -> Assert.Fail(sprintf "Expected instruction validation error: %A" other)
            Assert.That(File.Exists(target), Is.False)
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let oversized_or_invalid_guidance_does_not_block_ledger_status_operations () =
    workspace (fun root rt ->
        let path = create root "Demo/AGENTS.md" (String('x', 256 * 1024 + 1))
        let stateCommand =
            V2TaskWriteCommand
                { ProjectName = "Demo"; Action = "YieldAgent"; OperationId = Guid.NewGuid().ToString("D")
                  TaskId = "252"; ActorId = "Agent 7"; AttemptId = ""; OwnerId = ""
                  Title = ""; Html = ""; ExpectedVersion = 1; ExpectedAgentVersion = 1
                  ExpectedTaskVersion = 1; AssignmentGeneration = 1
                  Summary = ""; NextAction = ""; Reason = ""; BaseOid = ""; TargetRef = ""
                  SourceRef = ""; AllowDetached = false; AllowUncommitted = false
                  DecisionSequence = 0L; Evidence = ""; SessionId = "" }
        Assert.That(docs rt stateCommand, Is.Empty)
        Assert.That(docs rt (V2TaskReadCommand
                { ProjectName = "Demo"; AgentId = "Agent 7"; Action = "AgentInbox"
                  TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
                  Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null
                  RelativePath = null }), Is.Empty)
        match resolve rt (command "source.fs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("262144"))
        | other -> Assert.Fail(sprintf "Expected source instruction validation: %A" other)
        File.WriteAllBytes(path, [| 0xffuy |])
        Assert.That(docs rt stateCommand, Is.Empty)
        match resolve rt (command "source.fs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("UTF-8"))
        | other -> Assert.Fail(sprintf "Expected invalid UTF-8 for source command: %A" other))

[<Test>]
let oversized_guidance_does_not_preempt_real_ledger_status_error () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-instructions-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            create root "Demo/AGENTS.md" (String('x', 256 * 1024 + 1)) |> ignore
            let command =
                V2TaskReadCommand
                    { ProjectName = "Demo"; AgentId = "Agent 7"; Action = "AgentInbox"
                      TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
                      Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null
                      RelativePath = null }
            let! response, guidance =
                Client.SignalR.Client.receiveCommandWithInstructions (Runtime(root)) command
            Assert.That(guidance, Is.Empty)
            match response with
            | Error(Client.ValidationError message) when message.Contains("AGENTS.md") ->
                Assert.Fail("Ledger read was blocked by instruction source validation.")
            | Error _ -> () // Unbound test ledger must fail for its actual cause.
            | Ok _ -> Assert.Fail("Test project unexpectedly has a bound orchestration ledger.")
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let first_project_command_receives_instructions_without_prelude () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-instructions-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            create root "AGENTS.md" "workspace guidance" |> ignore
            create root "Demo/AGENTS.md" "project guidance" |> ignore
            let! response, instructions =
                Client.SignalR.Client.receiveCommandWithInstructions (Runtime(root))
                    (GetProjectDetailsCommand { ProjectName = "Demo" })
            match response with
            | Ok _ -> ()
            | Error error -> Assert.Fail(sprintf "Unexpected command error: %A" error)
            Assert.That(instructions |> List.map (fun d -> d.Content),
                        Is.EqualTo([ "workspace guidance"; "project guidance" ] :> obj))
        finally if Directory.Exists root then Directory.Delete(root, true)
    }
