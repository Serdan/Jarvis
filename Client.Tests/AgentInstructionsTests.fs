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
let invalid_encoding_fails_but_oversized_content_truncates () =
    workspace (fun root rt ->
        let path = create root "Demo/AGENTS.md" "valid"
        File.WriteAllBytes(path, [| 0xffuy; 0xfeuy |])
        match resolve rt (command "a.cs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("UTF-8"))
        | other -> Assert.Fail(sprintf "Expected invalid UTF-8: %A" other)
        // Larger files now return a bounded, explicitly incomplete prefix.
        File.WriteAllText(path, String('a', 256 * 1024 + 1))
        let document = (docs rt (command "a.cs")).Head
        Assert.That(document.Content, Does.Contain("INCOMPLETE REPOSITORY INSTRUCTIONS"))
        Assert.That(document.Content, Does.Contain("262145"))
        Assert.That(document.Content, Does.Contain("read_file"))
        Assert.That(document.Content, Does.Contain("Delivered: 65536"))
        Assert.That(document.Sha256.StartsWith("sha256:"), Is.True)
        Assert.That(document.Content.Length, Is.LessThan(68000)))

[<Test>]
let malformed_utf8_in_omitted_suffix_is_still_rejected () =
    workspace (fun root rt ->
        let path = create root "Demo/AGENTS.md" "initial"
        let content = Array.append (Array.create (64 * 1024 + 200) 97uy) [| 0xffuy |]
        File.WriteAllBytes(path, content)
        match resolve rt (command "x.fs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("UTF-8"))
        | other -> Assert.Fail(sprintf "Expected invalid suffix rejection: %A" other))

[<Test>]
let hierarchy_preserves_descendants_and_bounds_combined_payload () =
    workspace (fun root rt ->
        create root "AGENTS.md" (String('a', 200000)) |> ignore
        create root "Demo/AGENTS.md" (String('b', 200000)) |> ignore
        create root "Demo/src/AGENTS.md" (String('c', 200000)) |> ignore
        let result = docs rt (command "src/a.cs")
        Assert.That(result |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "AGENTS.md"; "Demo/AGENTS.md"; "Demo/src/AGENTS.md" ] :> obj))
        Assert.That(result |> List.forall (fun d -> d.Content.Contains("TRUNCATED")), Is.True)
        Assert.That(result |> List.sumBy (fun d -> System.Text.Encoding.UTF8.GetByteCount(d.Content)),
                    Is.LessThan(150000))
        Assert.That(result[2].Content, Does.Contain("cccc")))

[<Test>]
let excess_instruction_document_count_reports_omissions_without_failing () =
    workspace (fun root rt ->
        let mutable path = ""
        for i in 1 .. 34 do
            path <- Path.Combine(path, "d" + string i)
            create root (Path.Combine("Demo", path, "AGENTS.md")) ("instructions " + string i) |> ignore
        let result = docs rt (command (Path.Combine(path, "source.fs")))
        Assert.That(result.Length, Is.EqualTo(33))
        Assert.That(result[32].Scope, Is.EqualTo("instruction-warning"))
        Assert.That(result[32].Content, Does.Contain("omitted"))
        Assert.That(result[32].Content, Does.Contain("read_file")))

[<Test>]
let multibyte_instruction_truncation_preserves_valid_utf8 () =
    workspace (fun root rt ->
        let source = String.replicate 30000 "⚙"
        create root "Demo/AGENTS.md" source |> ignore
        let result = (docs rt (command "x.fs")).Head
        Assert.That(result.Content, Does.Contain("INCOMPLETE REPOSITORY INSTRUCTIONS"))
        Assert.That(result.Content, Does.Contain("⚙⚙"))
        Assert.That(result.Content, Does.Not.Contain("�"))
        Assert.That(result.Sha256, Is.EqualTo("sha256:" + (System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)) |> Convert.ToHexString).ToLowerInvariant())))

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
let oversized_instruction_does_not_prevent_file_write () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-instructions-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            create root "Demo/AGENTS.md" (String('x', 256 * 1024 + 1)) |> ignore
            let target = Path.Combine(root, "Demo", "written.txt")
            let cmd =
                WriteFileCommand
                    { ProjectName = "Demo"; FilePath = "written.txt"
                      Content = "side effect"; FileWriteMode = FileWriteMode.Write
                      ExpectedHash = None; CreateParents = None }
            let! outcome, instructions = Client.SignalR.Client.receiveCommandWithInstructions (Runtime(root)) cmd
            match outcome with
            | Ok _ -> ()
            | other -> Assert.Fail(sprintf "Truncated instruction cannot block write: %A" other)
            Assert.That(File.ReadAllText(target), Is.EqualTo("side effect"))
            Assert.That(instructions.Head.Content, Does.Contain("INCOMPLETE REPOSITORY INSTRUCTIONS"))
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let large_agents_write_warns_without_rejecting_and_small_write_does_not () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-instructions-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            let rt = Runtime(root)
            let cmd content =
                WriteFileCommand
                    { ProjectName = "Demo"; FilePath = "AGENTS.md"
                      Content = content; FileWriteMode = FileWriteMode.Write
                      ExpectedHash = None; CreateParents = None }
            let! smallResult, smallDocs =
                Client.SignalR.Client.receiveCommandWithInstructions rt (cmd "short")
            Assert.That(smallResult.IsOk, Is.True)
            Assert.That(smallDocs |> List.exists (fun d -> d.Scope = "write-warning"), Is.False)
            let huge = String('x', 33000)
            let! largeResult, largeDocs =
                Client.SignalR.Client.receiveCommandWithInstructions rt (cmd huge)
            Assert.That(largeResult.IsOk, Is.True)
            let warning = largeDocs |> List.find (fun d -> d.Scope = "write-warning")
            Assert.That(warning.Content, Does.Contain("33000"))
            Assert.That(warning.Content, Does.Contain("32 KiB"))
            Assert.That(warning.Content, Does.Contain("write succeeded"))
            Assert.That(File.ReadAllText(Path.Combine(root, "Demo", "AGENTS.md")), Is.EqualTo(huge))
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let patch_agents_warns_on_write_but_not_dry_run () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-instructions-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            let initial = "first line\n" + String('y', 34000) + "\n"
            create root "Demo/AGENTS.md" initial |> ignore
            let patch = "--- a/AGENTS.md\n+++ b/AGENTS.md\n@@ -1,2 +1,2 @@\n-first line\n+revised line\n " + String('y', 34000) + "\n"
            let cmd dryRun =
                PatchFileCommand
                    { ProjectName = "Demo"; FilePath = "AGENTS.md"
                      ExpectedHash = None; Format = PatchFormat.UnifiedDiff
                      Patch = patch; DryRun = Some dryRun
                      FuzzyContextLines = None; ReturnContent = None }
            let rt = Runtime(root)
            let! preview, previewDocs = Client.SignalR.Client.receiveCommandWithInstructions rt (cmd true)
            Assert.That(preview.IsOk, Is.True)
            Assert.That(previewDocs |> List.exists (fun d -> d.Scope = "write-warning"), Is.False)
            let! result, notices = Client.SignalR.Client.receiveCommandWithInstructions rt (cmd false)
            Assert.That(result.IsOk, Is.True)
            Assert.That(notices |> List.exists (fun d -> d.Scope = "write-warning"), Is.True)
            Assert.That(File.ReadAllText(Path.Combine(root, "Demo", "AGENTS.md")), Does.StartWith("revised line"))
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let oversized_or_invalid_guidance_does_not_block_ledger_status_operations () =
    workspace (fun root rt ->
        let path = create root "Demo/AGENTS.md" (String('x', 256 * 1024 + 1))
        let stateCommand =
            TaskWriteCommand
                { ProjectName = "Demo"; Action = "YieldAgent"; OperationId = Guid.NewGuid().ToString("D")
                  TaskId = "252"; ActorId = "Agent 7"; AttemptId = ""; OwnerId = ""
                  Title = ""; Html = ""; ExpectedVersion = 1; ExpectedAgentVersion = 1
                  ExpectedTaskVersion = 1; AssignmentGeneration = 1
                  Summary = ""; NextAction = ""; Reason = ""; BaseOid = ""; TargetRef = ""
                  SourceRef = ""; AllowDetached = false; AllowUncommitted = false
                  DecisionSequence = 0L; Evidence = ""; SessionId = "" }
        Assert.That(docs rt stateCommand, Is.Empty)
        Assert.That(docs rt (TaskReadCommand
                { ProjectName = "Demo"; AgentId = "Agent 7"; Action = "AgentInbox"
                  TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
                  Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null
                  RelativePath = null }), Is.Empty)
        Assert.That((docs rt (command "source.fs")).Head.Content,
                    Does.Contain("INCOMPLETE REPOSITORY INSTRUCTIONS"))
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
                TaskReadCommand
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


[<Test>]
let working_turn_delivery_cache_deduplicates_and_refreshes_hierarchy () =
    workspace (fun root rt ->
        create root "AGENTS.md" "workspace" |> ignore
        create root "Demo/AGENTS.md" "project" |> ignore
        let nested = create root "Demo/src/AGENTS.md" "nested"
        let cache = DeliveryCache()
        let initial = docs rt (command "source.fs")
        let filter agent turn documents = cache.Filter("Demo", agent, turn, documents)
        Assert.That(filter "Owner" "turn-1" initial |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "AGENTS.md"; "Demo/AGENTS.md" ] :> obj))
        Assert.That(filter "Owner" "turn-1" initial, Is.Empty)
        let deep = docs rt (command "src/a.fs")
        Assert.That(filter "Owner" "turn-1" deep |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "Demo/src/AGENTS.md" ] :> obj))
        Assert.That(filter "Owner" "turn-1" deep, Is.Empty)
        File.WriteAllText(nested, "nested updated")
        Assert.That(filter "Owner" "turn-1" (docs rt (command "src/a.fs"))
                    |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "Demo/src/AGENTS.md" ] :> obj))
        Assert.That(filter "Reviewer" "turn-1" initial |> List.length, Is.EqualTo(2))
        Assert.That(filter "Owner" "turn-2" initial |> List.length, Is.EqualTo(2))
        Assert.That(cache.Filter("Other", "Owner", "turn-2", initial) |> List.length, Is.EqualTo(2)))
