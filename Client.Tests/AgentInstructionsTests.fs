module AgentInstructionsTests

open System
open System.IO
open System.Diagnostics
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
let invalid_encoding_fails_and_oversized_content_is_omitted () =
    workspace (fun root rt ->
        let path = create root "Demo/AGENTS.md" "valid"
        File.WriteAllBytes(path, [| 0xffuy; 0xfeuy |])
        match resolve rt (command "a.cs") with
        | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("UTF-8"))
        | other -> Assert.Fail(sprintf "Expected invalid UTF-8: %A" other)
        // Oversized instructions are never injected as incomplete prefixes.
        File.WriteAllText(path, String('a', 256 * 1024 + 1))
        let document = (docs rt (command "a.cs")).Head
        Assert.That(document.Scope, Is.EqualTo("instruction-warning"))
        Assert.That(document.Content, Does.Contain("INCOMPLETE REPOSITORY INSTRUCTIONS"))
        Assert.That(document.Content, Does.Contain("262145"))
        Assert.That(document.Content, Does.Contain("get_agent_instructions"))
        Assert.That(document.Content, Does.Contain("NOT delivered"))
        Assert.That(document.Content, Does.Not.Contain(String('a', 1024)))
        Assert.That(document.Sha256.StartsWith("sha256:"), Is.True)
        Assert.That(document.Content.Length, Is.LessThan(1024)))

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
        Assert.That(result |> List.forall (fun d -> d.Scope = "instruction-warning"), Is.True)
        Assert.That(result |> List.forall (fun d -> d.Content.Contains("NOT delivered")), Is.True)
        Assert.That(result |> List.sumBy (fun d -> System.Text.Encoding.UTF8.GetByteCount(d.Content)),
                    Is.LessThan(4096))
        Assert.That(result[2].Content, Does.Not.Contain(String('c', 1024))))

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
        Assert.That(result[32].Content, Does.Contain("get_agent_instructions")))

[<Test>]
let multibyte_oversized_instruction_is_omitted_without_prefix () =
    workspace (fun root rt ->
        let source = String.replicate 30000 "⚙"
        create root "Demo/AGENTS.md" source |> ignore
        let result = (docs rt (command "x.fs")).Head
        Assert.That(result.Scope, Is.EqualTo("instruction-warning"))
        Assert.That(result.Content, Does.Contain("INCOMPLETE REPOSITORY INSTRUCTIONS"))
        Assert.That(result.Content, Does.Not.Contain("⚙"))
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
            Assert.That(instructions.Head.Scope, Is.EqualTo("instruction-warning"))
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
        let mainTarget =
            ProjectPaths.ProjectTarget.parse "Demo"
            |> function Ok target -> target | Error error -> failwithf "%A" error
        let initial = docs rt (command "source.fs")
        let filter agent turn documents = cache.Filter(mainTarget, agent, turn, HierarchyLoaded, documents, fun () -> Ok [])
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
        let otherTarget =
            ProjectPaths.ProjectTarget.parse "Other"
            |> function Ok target -> target | Error error -> failwithf "%A" error
        Assert.That(cache.Filter(otherTarget, "Owner", "turn-2", HierarchyLoaded, initial, fun () -> Ok []) |> List.length, Is.EqualTo(2)))


let private gitAt directory arguments =
    use proc = new Process()
    proc.StartInfo.FileName <- "git"
    proc.StartInfo.WorkingDirectory <- directory
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    for argument in arguments do proc.StartInfo.ArgumentList.Add(argument)
    if not (proc.Start()) then failwith "Unable to start git fixture"
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwithf "Git worktree fixture failed: %s" stderr
    stdout

let private explicitInstructions (rt: Runtime) selector directory cursor =
    let target =
        ProjectPaths.ProjectTarget.parse selector
        |> function Ok found -> found | Error error -> failwithf "Invalid checkout selector: %A" error
    getExplicit target
        { ProjectName = selector; Directory = directory; Cursor = cursor } rt

[<Test>]
let registered_worktrees_resolve_distinct_instructions_from_actual_checkout_roots () =
    workspace (fun root rt ->
        let main = Path.Combine(root, "Demo")
        create root "AGENTS.md" "workspace guidance" |> ignore
        create root "Demo/AGENTS.md" "main guidance" |> ignore
        gitAt main ["init"; "-q"] |> ignore
        gitAt main ["config"; "user.name"; "Fixture"] |> ignore
        gitAt main ["config"; "user.email"; "fixture@example.test"] |> ignore
        create root "Demo/initial.txt" "baseline" |> ignore
        gitAt main ["add"; "."] |> ignore
        gitAt main ["commit"; "-qm"; "baseline"] |> ignore
        let worktreeRoot = Path.Combine(root, ".Demo-worktrees")
        let a = Path.Combine(worktreeRoot, "tree-a")
        let b = Path.Combine(worktreeRoot, "tree-b")
        gitAt main ["worktree"; "add"; "-qb"; "branch-a"; a; "HEAD"] |> ignore
        gitAt main ["worktree"; "add"; "-qb"; "branch-b"; b; "HEAD"] |> ignore
        create a "AGENTS.md" "checkout A" |> ignore
        create a "src/AGENTS.md" "nested A" |> ignore
        create b "AGENTS.md" "checkout B" |> ignore
        create b "src/AGENTS.md" "nested B" |> ignore
        create a "src/engine.fs" "module EngineA" |> ignore
        create b "src/engine.fs" "module EngineB" |> ignore
        let source selector =
            let cmd : Common.ReadFileCommand = { ProjectName = selector; FilePath = "src/engine.fs"
                                                 StartLine = None; EndLine = None; IncludeLineNumbers = None }
            match Client.ProjectBrowser.readFile cmd rt with
            | Ok(Content text) -> text
            | Error error -> failwithf "Cannot read selected worktree source: %A" error
        Assert.That(source "Demo::tree-a", Is.EqualTo("module EngineA"))
        Assert.That(source "Demo::tree-b", Is.EqualTo("module EngineB"))
        let check selector =
            let cmd =
                ReadFileCommand
                    { ProjectName = selector; FilePath = "src/engine.fs"
                      StartLine = None; EndLine = None; IncludeLineNumbers = None }
            docs rt cmd
        Assert.That(check "Demo::tree-a" |> List.map (fun d -> d.Content),
                    Is.EqualTo([ "workspace guidance"; "checkout A"; "nested A" ] :> obj))
        Assert.That(check "Demo::tree-b" |> List.map (fun d -> d.Content),
                    Is.EqualTo([ "workspace guidance"; "checkout B"; "nested B" ] :> obj))
        Assert.That(check "Demo::tree-a" |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "AGENTS.md"; "Demo/AGENTS.md"; "Demo/src/AGENTS.md" ] :> obj))
        for selector, expectedRoot, expectedNested in
            [ "Demo::tree-a", "checkout A", "nested A"
              "Demo::tree-b", "checkout B", "nested B" ] do
            let hierarchy =
                match explicitInstructions rt selector "src" None with
                | Ok page -> page["AGENTS.md"].AsObject()
                | Error error -> failwithf "Cannot get worktree instructions: %A" error
            Assert.That(hierarchy["."].GetValue<string>(), Is.EqualTo("workspace guidance"))
            Assert.That(hierarchy["Demo"].GetValue<string>(), Is.EqualTo(expectedRoot))
            Assert.That(hierarchy["Demo/src"].GetValue<string>(), Is.EqualTo(expectedNested))
        let rootA =
            match roots rt "Demo::tree-a" with
            | Ok docs -> docs
            | Error error -> failwithf "Cannot resolve worktree roots: %A" error
        Assert.That(rootA |> List.map (fun d -> d.Content),
                    Is.EqualTo([ "workspace guidance"; "checkout A" ] :> obj))
        let checkoutA =
            ProjectPaths.ProjectTarget.parse "Demo::tree-a"
            |> function Ok value -> value | Error error -> failwithf "%A" error
        let checkoutB =
            ProjectPaths.ProjectTarget.parse "Demo::tree-b"
            |> function Ok value -> value | Error error -> failwithf "%A" error
        Assert.That(ProjectPaths.ProjectTarget.projectName checkoutA, Is.EqualTo("Demo"))
        Assert.That(ProjectPaths.ProjectTarget.projectName checkoutB, Is.EqualTo("Demo"))
        Assert.That(checkoutA, Is.Not.EqualTo(checkoutB))
        let sourceCommand : Common.AgentCommand =
            ReadFileCommand
                { ProjectName = "Demo::tree-a"; FilePath = "src/engine.fs"
                  StartLine = None; EndLine = None; IncludeLineNumbers = None }
        let prepared =
            match prepareWithTarget rt (Some checkoutA) sourceCommand with
            | Ok (documents, Some context) -> documents, context
            | other -> failwithf "Expected prepared checkout context: %A" other
        let instructions, sourceContext = prepared
        Assert.That(instructions |> List.map (fun d -> d.Content),
                    Is.EqualTo([ "workspace guidance"; "checkout A"; "nested A" ] :> obj))
        let selectedSource : Common.ReadFileCommand =
            { ProjectName = "Demo::tree-a"; FilePath = "src/engine.fs"
              StartLine = None; EndLine = None; IncludeLineNumbers = None }
        match Client.ProjectBrowser.readFileIn sourceContext selectedSource rt with
        | Ok(Content value) -> Assert.That(value, Is.EqualTo("module EngineA"))
        | Error error -> Assert.Fail(sprintf "Prepared checkout source read failed: %A" error)
        let cache = DeliveryCache()
        Assert.That(cache.Filter(checkoutA, "Owner", "turn-1", HierarchyLoaded, check "Demo::tree-a", fun () -> Ok []).Length, Is.EqualTo(3))
        Assert.That(cache.Filter(checkoutA, "Owner", "turn-1", HierarchyLoaded, check "Demo::tree-a", fun () -> Ok []), Is.Empty)
        Assert.That(cache.Filter(checkoutB, "Owner", "turn-1", HierarchyLoaded, check "Demo::tree-b", fun () -> Ok []).Length, Is.EqualTo(3))
        match ProjectPaths.ProjectTarget.parse "Demo::tree-a::unexpected" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Nested selectors must be rejected")
        match ProjectPaths.ProjectTarget.parse "Demo::../escape" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Traversal selectors must be rejected")
        match ProjectPaths.resolveProjectContext "Demo::unknown" rt with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Unknown worktree should not resolve")
        match ProjectPaths.resolveProjectPath "Demo::tree-a" "../other/AGENTS.md" rt with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail(sprintf "Checkout-relative traversal should be rejected: %A" other)
        let target = create a "unsafe-instructions.md" "wrong source"
        File.Delete(Path.Combine(a, "src", "AGENTS.md"))
        let link = Path.Combine(a, "src", "AGENTS.md")
        try
            File.CreateSymbolicLink(link, target) |> ignore
            match resolve rt (ReadFileCommand
                { ProjectName = "Demo::tree-a"; FilePath = "src/engine.fs"
                  StartLine = None; EndLine = None; IncludeLineNumbers = None }) with
            | Error(Client.PermissionDenied _) -> ()
            | other -> Assert.Fail(sprintf "Worktree instruction symlink must be rejected: %A" other)
        with
        | :? UnauthorizedAccessException -> ()
        | :? PlatformNotSupportedException -> ()
    )

[<Test>]
let bootstrap_is_once_per_working_turn_even_for_empty_ledger_responses () =
    let target =
        ProjectPaths.ProjectTarget.parse "Demo"
        |> function Ok value -> value | Error error -> failwithf "%A" error
    let cache = DeliveryCache()
    let mutable rootReads = 0
    let roots () =
        rootReads <- rootReads + 1
        Ok [ { Scope = "workspace"; Path = "AGENTS.md"; Sha256 = "sha256:first"; Content = "root" } ]
    let deliver turn documents = cache.Filter(target, "Owner", turn, RootBootstrapRequired, documents, roots)
    Assert.That(deliver "first" [] |> List.map (fun d -> d.Content),
                Is.EqualTo([ "root" ] :> obj))
    Assert.That(deliver "first" [], Is.Empty)
    Assert.That(deliver "first" [], Is.Empty)
    Assert.That(rootReads, Is.EqualTo(1))
    Assert.That(deliver "second" [] |> List.length, Is.EqualTo(1))
    Assert.That(rootReads, Is.EqualTo(2))
    let other = cache.Filter(target, "Reviewer", "first", RootBootstrapRequired, [], roots)
    Assert.That(other.Length, Is.EqualTo(1))
    Assert.That(rootReads, Is.EqualTo(3))

[<Test>]
let empty_completed_source_lookup_suppresses_redundant_checkout_bootstrap () =
    let target =
        ProjectPaths.ProjectTarget.parse "Demo::tree-a"
        |> function Ok selected -> selected | Error error -> failwithf "%A" error
    let cache = DeliveryCache()
    let mutable rootLoads = 0
    let load () =
        rootLoads <- rootLoads + 1
        Ok [ { Scope = "workspace"; Path = "AGENTS.md"; Sha256 = "sha256:root"
               Content = "different checkout" } ]
    // Source lookup completed successfully but found no instruction files.
    Assert.That(cache.Filter(target, "Owner", "turn-1", HierarchyLoaded, [], load), Is.Empty)
    Assert.That(rootLoads, Is.Zero)
    // Later ledger-only calls must not rediscover a checkout.
    Assert.That(cache.Filter(target, "Owner", "turn-1", RootBootstrapRequired, [], load), Is.Empty)
    Assert.That(rootLoads, Is.Zero)
    // A new working turn resets the cache and ledger bootstrap works normally.
    let fresh = cache.Filter(target, "Owner", "turn-2", RootBootstrapRequired, [], load)
    Assert.That(fresh |> List.map (fun d -> d.Content), Is.EqualTo([ "different checkout" ] :> obj))
    Assert.That(rootLoads, Is.EqualTo(1))

[<Test>]
let failed_bootstrap_does_not_block_operations_and_retries_until_success () =
    let target =
        ProjectPaths.ProjectTarget.parse "Demo"
        |> function Ok target -> target | Error error -> failwithf "%A" error
    let cache = DeliveryCache()
    let mutable attempts = 0
    let load () =
        attempts <- attempts + 1
        if attempts = 1 then Error(Client.ValidationError "Invalid AGENTS.md")
        else Ok [ { Scope = "project"; Path = "Demo/AGENTS.md"
                    Sha256 = "sha256:valid"; Content = "valid guidance" } ]
    let call documents = cache.Filter(target, "Owner", "turn-1", RootBootstrapRequired, documents, load)
    let warning = call []
    Assert.That(warning |> List.exists (fun doc -> doc.Scope = "instruction-warning"), Is.True)
    Assert.That(attempts, Is.EqualTo(1))
    Assert.That(call [] |> List.map (fun doc -> doc.Content),
                Is.EqualTo([ "valid guidance" ] :> obj))
    Assert.That(attempts, Is.EqualTo(2))
    Assert.That(call [], Is.Empty)
    Assert.That(attempts, Is.EqualTo(2))

[<Test>]
let write_notice_does_not_suppress_initial_instructions () =
    let target =
        ProjectPaths.ProjectTarget.parse "Demo"
        |> function Ok target -> target | Error error -> failwithf "%A" error
    let cache = DeliveryCache()
    let note = { Scope = "write-warning"; Path = "Demo/AGENTS.md"; Sha256 = ""; Content = "Notice" }
    let roots () = Ok [ { Scope = "workspace"; Path = "AGENTS.md"; Sha256 = "sha256:root"; Content = "Root" } ]
    let delivered = cache.Filter(target, "Owner", "turn-1", RootBootstrapRequired, [note], roots)
    Assert.That(delivered |> List.map (fun d -> d.Scope),
                Is.EqualTo([ "workspace"; "write-warning" ] :> obj))


[<Test>]
let omission_warning_is_redelivered_after_complete_file_replaces_it_and_reverts () =
    workspace (fun root rt ->
        let path = create root "Demo/AGENTS.md" (String('A', 90000))
        let target =
            ProjectPaths.ProjectTarget.parse "Demo"
            |> function Ok value -> value | Error error -> failwithf "%A" error
        let cache = DeliveryCache()
        let read () = docs rt (command "some.cs")
        let filter () =
            cache.Filter(target, "Owner", "same-turn", HierarchyLoaded, read(), fun () -> Ok [])
        let first = filter ()
        Assert.That(first.Length, Is.EqualTo(1))
        Assert.That(first.Head.Scope, Is.EqualTo("instruction-warning"))
        Assert.That(filter(), Is.Empty)
        File.WriteAllText(path, "New complete instructions")
        let second = filter ()
        Assert.That(second.Length, Is.EqualTo(1))
        Assert.That(second.Head.Scope, Is.EqualTo("project"))
        Assert.That(second.Head.Content, Is.EqualTo("New complete instructions"))
        File.WriteAllText(path, String('A', 90000))
        let third = filter ()
        Assert.That(third.Length, Is.EqualTo(1))
        Assert.That(third.Head.Scope, Is.EqualTo("instruction-warning"))
        Assert.That(third.Head.Content, Is.EqualTo(first.Head.Content))
        Assert.That(filter(), Is.Empty)
        File.WriteAllText(path, "New complete instructions")
        let fourth = filter ()
        Assert.That(fourth.Length, Is.EqualTo(1))
        Assert.That(fourth.Head.Scope, Is.EqualTo("project"))
        Assert.That(fourth.Head.Content, Is.EqualTo(second.Head.Content)))

[<Test>]
let omitted_instruction_is_delivered_when_later_response_has_room () =
    workspace (fun root rt ->
        // The multi-directory request sees another sizeable ancestor first.
        // A subsequent source-only request must deliver complete source guidance.
        create root "AGENTS.md" (String('w', 40000)) |> ignore
        create root "Demo/AGENTS.md" (String('p', 40000)) |> ignore
        create root "Demo/other/AGENTS.md" (String('o', 40000)) |> ignore
        create root "Demo/src/AGENTS.md" (String('s', 40000)) |> ignore
        let target =
            ProjectPaths.ProjectTarget.parse "Demo"
            |> function Ok found -> found | Error error -> failwithf "%A" error
        let cache = DeliveryCache()
        let multi = ReadFilesCommand
                        { ProjectName = "Demo"; FilePaths = ["other/data.fs"; "src/data.fs"] }
        let initial = docs rt multi
        let omitted = initial |> List.find (fun document -> document.Path = "Demo/src/AGENTS.md")
        Assert.That(omitted.Scope, Is.EqualTo("instruction-warning"))
        Assert.That(omitted.Content, Does.Contain("NOT delivered"))
        Assert.That(omitted.Content, Does.Not.Contain(String('s', 1024)))
        let first = cache.Filter(target, "Owner", "turn-1", HierarchyLoaded, initial, fun () -> Ok [])
        Assert.That(first |> List.exists (fun doc -> doc.Path = omitted.Path), Is.True)
        let following = docs rt (command "src/data.fs")
        let source = following |> List.find (fun document -> document.Path = "Demo/src/AGENTS.md")
        Assert.That(source.Scope, Is.EqualTo("project"))
        Assert.That(source.Content, Is.EqualTo(String('s', 40000)))
        let delivered = cache.Filter(target, "Owner", "turn-1", HierarchyLoaded, following, fun () -> Ok [])
        Assert.That(delivered |> List.map (fun document -> document.Path),
                    Is.EqualTo([ "Demo/src/AGENTS.md" ] :> obj))
        Assert.That(cache.Filter(target, "Owner", "turn-1", HierarchyLoaded, following, fun () -> Ok []), Is.Empty))

[<Test>]
let explicit_hierarchy_reads_every_ancestor_without_auto_delivery_limits () =
    workspace (fun root rt ->
        let workspaceText = String('w', 260000)
        let projectText = String('p', 260000)
        let sourceText = String('s', 260000)
        create root "AGENTS.md" workspaceText |> ignore
        create root "Demo/AGENTS.md" projectText |> ignore
        create root "Demo/src/AGENTS.md" sourceText |> ignore
        create root "Demo/src/core/AGENTS.md" "" |> ignore
        create root "Demo/unrelated/AGENTS.md" "not applicable" |> ignore
        let mutable cursor = None
        let output = System.Collections.Generic.Dictionary<string, System.Text.StringBuilder>()
        let mutable pages = 0
        let mutable doneReading = false
        while not doneReading && pages < 20 do
            let page =
                match explicitInstructions rt "Demo" "src/core" cursor with
                | Ok value -> value
                | Error error -> failwithf "Unexpected explicit retrieval failure: %A" error
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(page.ToJsonString()), Is.LessThanOrEqualTo(640 * 1024))
            for property in page["AGENTS.md"].AsObject() do
                let existing =
                    match output.TryGetValue(property.Key) with
                    | true, builder -> builder
                    | false, _ ->
                        let builder = System.Text.StringBuilder()
                        output.Add(property.Key, builder)
                        builder
                match page["chunk"] with
                | null -> ()
                | chunk when chunk["path"].GetValue<string>() = property.Key ->
                    Assert.That(chunk["offsetBytes"].GetValue<int64>(),
                                Is.EqualTo(int64 (System.Text.Encoding.UTF8.GetByteCount(existing.ToString()))))
                | _ -> ()
                existing.Append(property.Value.GetValue<string>()) |> ignore
            pages <- pages + 1
            match page["nextCursor"] with
            | null -> doneReading <- true
            | value -> cursor <- Some(value.GetValue<string>())
        Assert.That(doneReading, Is.True, "Every instruction byte must be retrievable using cursors")
        Assert.That(pages, Is.GreaterThan(1))
        Assert.That(output.Keys |> Seq.toList |> List.sort,
                    Is.EqualTo([ "."; "Demo"; "Demo/src"; "Demo/src/core" ] :> obj))
        Assert.That(output["."].ToString(), Is.EqualTo(workspaceText))
        Assert.That(output["Demo"].ToString(), Is.EqualTo(projectText))
        Assert.That(output["Demo/src"].ToString(), Is.EqualTo(sourceText))
        Assert.That(output["Demo/src/core"].ToString(), Is.EqualTo("")))

[<Test>]
let explicit_hierarchy_continues_large_multibyte_escaped_document () =
    workspace (fun root rt ->
        let huge = String.replicate 180000 "🌍\n\""
        create root "Demo/src/AGENTS.md" huge |> ignore
        let collected = System.Text.StringBuilder()
        let mutable cursor = None
        let mutable pages = 0
        let mutable finished = false
        while not finished && pages < 30 do
            let page =
                match explicitInstructions rt "Demo" "src" cursor with
                | Ok page -> page
                | Error error -> failwithf "Unexpected continuation error: %A" error
            let contents = page["AGENTS.md"].AsObject()
            match contents["Demo/src"] with
            | null -> ()
            | node ->
                Assert.That(page["chunk"], Is.Not.Null)
                let offsetNode = page["chunk"].AsObject()["offsetBytes"]
                Assert.That(offsetNode.GetValue<int64>(),
                            Is.EqualTo(int64 (System.Text.Encoding.UTF8.GetByteCount(collected.ToString()))))
                collected.Append(node.GetValue<string>()) |> ignore
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(page.ToJsonString()), Is.LessThanOrEqualTo(640 * 1024))
            pages <- pages + 1
            match page["nextCursor"] with
            | null -> finished <- true
            | node -> cursor <- Some(node.GetValue<string>())
        Assert.That(finished, Is.True)
        Assert.That(pages, Is.GreaterThan(1))
        Assert.That(collected.ToString(), Is.EqualTo(huge)))

[<Test>]
let explicit_hierarchy_is_not_limited_to_32_instruction_files () =
    workspace (fun root rt ->
        let mutable directory = ""
        for i in 1 .. 40 do
            directory <-
                if directory = "" then "d" + string i
                else directory + "/d" + string i
            create root ("Demo/" + directory + "/AGENTS.md") ("instruction " + string i) |> ignore
        let page =
            match explicitInstructions rt "Demo" directory None with
            | Ok page -> page
            | Error error -> failwithf "Expected complete instruction hierarchy: %A" error
        Assert.That(page["AGENTS.md"].AsObject().Count, Is.EqualTo(40))
        Assert.That(page["nextCursor"], Is.Null))

[<Test>]
let explicit_cursor_rejects_changed_hierarchy_and_unsafe_directories () =
    workspace (fun root rt ->
        let sourcePath = create root "Demo/src/AGENTS.md" (String('x', 900000))
        let first =
            match explicitInstructions rt "Demo" "src" None with
            | Ok value -> value
            | Error error -> failwithf "%A" error
        Assert.That(first["nextCursor"], Is.Not.Null)
        let cursor = first["nextCursor"].GetValue<string>()
        File.WriteAllText(sourcePath, String('y', 900000))
        match explicitInstructions rt "Demo" "src" (Some cursor) with
        | Error(Client.ValidationError message) ->
            Assert.That(message, Does.Contain("changed"))
        | other -> Assert.Fail(sprintf "Expected stale cursor rejection: %A" other)
        match explicitInstructions rt "Demo" "../escape" None with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Path traversal must be rejected"))


[<Test>]
let explicit_tool_transport_returns_safe_page_and_continuation () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-explicit-transport-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            create root "Demo/AGENTS.md" (String('x', 1800000)) |> ignore
            let rt = Runtime(root)
            let mutable cursor = None
            let mutable pages = 0
            let mutable complete = false
            let rebuilt = System.Text.StringBuilder()
            while not complete && pages < 10 do
                let cmd : AgentCommand =
                    GetAgentInstructionsCommand
                        { ProjectName = "Demo"; Directory = ""; Cursor = cursor }
                let input = System.Text.Json.JsonSerializer.Serialize<AgentCommand>(cmd)
                let! _, result, envelope =
                    Client.SignalR.Client.processIncomingCommand rt input ""
                Assert.That(result.IsOk, Is.True)
                Assert.That(envelope.Error.IsNone, Is.True)
                Assert.That(envelope.Result.IsSome, Is.True)
                use json = System.Text.Json.JsonDocument.Parse(envelope.Result.Value)
                rebuilt.Append(json.RootElement.GetProperty("AGENTS.md").GetProperty("Demo").GetString())
                |> ignore
                let mutable next = Unchecked.defaultof<System.Text.Json.JsonElement>
                pages <- pages + 1
                if json.RootElement.TryGetProperty("nextCursor", &next) then
                    cursor <- Some(next.GetString())
                else complete <- true
            Assert.That(complete, Is.True)
            Assert.That(pages, Is.GreaterThan(1))
            Assert.That(rebuilt.ToString(), Is.EqualTo(String('x', 1800000)))
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let explicit_command_routes_through_client_without_auto_hierarchy_injection () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-explicit-instructions-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            create root "AGENTS.md" "workspace guidance" |> ignore
            create root "Demo/AGENTS.md" "project guidance" |> ignore
            let cmd = GetAgentInstructionsCommand
                          { ProjectName = "Demo"; Directory = ""; Cursor = None }
            let! response, auto = Client.SignalR.Client.receiveCommandWithInstructions (Runtime(root)) cmd
            Assert.That(auto, Is.Empty)
            match response with
            | Error error -> Assert.Fail(sprintf "Explicit command failed: %A" error)
            | Ok serialized ->
                use result = System.Text.Json.JsonDocument.Parse(serialized)
                let mapping = result.RootElement.GetProperty("AGENTS.md")
                Assert.That(mapping.GetProperty(".").GetString(), Is.EqualTo("workspace guidance"))
                Assert.That(mapping.GetProperty("Demo").GetString(), Is.EqualTo("project guidance"))
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let malformed_project_selector_returns_validation_error_at_command_boundary () =
    task {
        let root = Path.Combine(Path.GetTempPath(), "jarvis-invalid-project-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root, "Demo")) |> ignore
        try
            let rt = Runtime(root)
            let command : Common.AgentCommand =
                ReadFileCommand
                    { ProjectName = "Demo::bad::selector"; FilePath = "example.fs"
                      StartLine = None; EndLine = None; IncludeLineNumbers = None }
            let payload = System.Text.Json.JsonSerializer.Serialize(command)
            let! _, response, envelope =
                Client.SignalR.Client.processIncomingCommand rt payload "Owner"
            match response with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Malformed project selector must be validation error: %A" other)
            Assert.That(envelope.Instructions, Is.Empty)
            let optionalProject : Common.AgentCommand =
                GetJobResultCommand
                    { ProjectName = ""; JobId = "nonexistent"; AfterSequence = None }
            let optionalPayload = System.Text.Json.JsonSerializer.Serialize(optionalProject)
            let! _, lookup, _ = Client.SignalR.Client.processIncomingCommand rt optionalPayload "Owner"
            match lookup with
            | Error(Client.NotFoundError _) -> ()
            | other -> Assert.Fail(sprintf "Optional empty job project scope must remain supported: %A" other)
        finally if Directory.Exists root then Directory.Delete(root, true)
    }

[<Test>]
let project_skill_operations_load_actual_skill_ancestor_instructions () =
    workspace (fun root rt ->
        create root "AGENTS.md" "workspace guidance" |> ignore
        create root "Demo/AGENTS.md" "project guidance" |> ignore
        create root "Demo/.agents/AGENTS.md" "agent configuration guidance" |> ignore
        create root "Demo/.agents/skills/AGENTS.md" "shared skill guidance" |> ignore
        create root "Demo/.agents/skills/example/AGENTS.md" "specific skill guidance" |> ignore
        create root "Demo/.agents/skills/example/SKILL.md" "# Example skill" |> ignore
        let expected =
            [ "AGENTS.md"; "Demo/AGENTS.md"; "Demo/.agents/AGENTS.md"
              "Demo/.agents/skills/AGENTS.md"; "Demo/.agents/skills/example/AGENTS.md" ]
        let check cmd =
            Assert.That(docs rt cmd |> List.map (fun document -> document.Path), Is.EqualTo(expected :> obj))
        check (GetSkillCommand { ProjectName = "Demo"; SkillName = "example" })
        check (CreateSkillCommand
                  { ProjectName = "Demo"; SkillName = "example"
                    Content = "# Revised"; Overwrite = true }))

[<Test>]
let task_spec_import_export_load_instructions_from_source_html_parent () =
    workspace (fun root rt ->
        create root "AGENTS.md" "workspace guidance" |> ignore
        create root "Demo/AGENTS.md" "project guidance" |> ignore
        create root "Demo/specs/AGENTS.md" "spec guidance" |> ignore
        create root "Demo/specs/deep/AGENTS.md" "deep spec guidance" |> ignore
        let taskWrite action path : Common.AgentCommand =
            TaskWriteCommand
                { ProjectName = "Demo"; Action = action
                  OperationId = Guid.NewGuid().ToString("D"); TaskId = "task-1"
                  AttemptId = ""; ActorId = "Owner"; OwnerId = ""
                  Title = ""; Html = ""; ExpectedVersion = 0
                  ExpectedAgentVersion = 0; ExpectedTaskVersion = 0
                  AssignmentGeneration = 0; Summary = ""; NextAction = ""
                  Reason = ""; BaseOid = ""; TargetRef = ""; SourceRef = path
                  AllowDetached = false; AllowUncommitted = false
                  DecisionSequence = 0L; Evidence = ""; SessionId = "" }
        let expected =
            [ "AGENTS.md"; "Demo/AGENTS.md"; "Demo/specs/AGENTS.md"
              "Demo/specs/deep/AGENTS.md" ]
        for action in [ "ExportTaskSpec"; "ImportTaskSpec" ] do
            let command = taskWrite action "specs/deep/draft.html"
            Assert.That(docs rt command |> List.map (fun doc -> doc.Path), Is.EqualTo(expected :> obj))
            let target =
                Client.ProjectPaths.ProjectTarget.parse "Demo"
                |> function Ok selected -> selected | Error error -> failwithf "%A" error
            match prepareWithTarget rt (Some target) command with
            | Ok (documents, Some source) ->
                Assert.That(documents |> List.map (fun doc -> doc.Path), Is.EqualTo(expected :> obj))
                match Client.ProjectPaths.resolveProjectRoot "Demo" rt with
                | Ok expectedRoot ->
                    Assert.That(Client.ProjectPaths.ResolvedProject.sourceRoot source,
                                Is.EqualTo(expectedRoot))
                | Error error -> Assert.Fail(sprintf "Cannot resolve fixture: %A" error)
            | other -> Assert.Fail(sprintf "Task spec must have prepared source context: %A" other)
        match resolve rt (taskWrite "ImportTaskSpec" "../escape.html") with
        | Error(Client.PermissionDenied _) -> ()
        | other -> Assert.Fail(sprintf "Source-spec traversal must fail before ledger mutation: %A" other))

[<Test>]
let worktree_git_discovery_drains_both_pipes_and_bounds_a_hung_process () =
    if OperatingSystem.IsWindows() then
        Assert.Ignore("Shell fixture requires Unix executables.")
    workspace (fun root _ ->
        let script name body =
            let path = create root name ("#!/bin/sh\n" + body + "\n")
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            path
        // If stderr is not drained, the process blocks before printing stdout.
        let verbose =
            script "verbose-git.sh"
                "head -c 262144 /dev/zero >&2; printf 'worktree /tmp/fixture\n'"
        match Client.ProjectPaths.WorktreeGit.list verbose root (TimeSpan.FromSeconds 3.) with
        | Ok output -> Assert.That(output, Does.Contain("worktree /tmp/fixture"))
        | other -> Assert.Fail(sprintf "Both pipes must drain concurrently: %A" other)
        // A producer that never closes stdout must still be terminated.
        let hung = script "hung-git.sh" "printf 'worktree /tmp/fixture\n'; sleep 30"
        let clock = Stopwatch.StartNew()
        let outcome = Client.ProjectPaths.WorktreeGit.list hung root (TimeSpan.FromMilliseconds 350.)
        clock.Stop()
        match outcome with
        | Error(Client.ValidationError message) -> Assert.That(message, Does.Contain("timed out"))
        | other -> Assert.Fail(sprintf "Expected bounded timeout: %A" other)
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds 5.))
        if OperatingSystem.IsLinux() then
            // Parent exits promptly while its child retains the inherited
            // stdout/stderr handles. Kill(true) on the exited parent cannot
            // reach the orphan; only its original process group can.
            let pidFile = Path.Combine(root, "orphan.pid")
            let orphan =
                script "orphan-git.sh" ("sleep 30 & echo $! > '" + pidFile + "'; exit 0")
            let result = Client.ProjectPaths.WorktreeGit.list orphan root (TimeSpan.FromMilliseconds 350.)
            match result with
            | Error(Client.ValidationError msg) -> Assert.That(msg, Does.Contain("timed out"))
            | other -> Assert.Fail(sprintf "Inherited orphan pipe must time out: %A" other)
            Assert.That(File.Exists pidFile, Is.True)
            let childPid = Int32.Parse(File.ReadAllText(pidFile).Trim())
            let childAlive () =
                let stat = "/proc/" + string childPid + "/stat"
                if not (File.Exists stat) then false
                else
                    let record = File.ReadAllText(stat)
                    let state = record[record.LastIndexOf(") ", StringComparison.Ordinal) + 2]
                    state <> 'Z' && state <> 'X'
            // Allow the kernel to finish propagating SIGKILL and reaping.
            let clock = Stopwatch.StartNew()
            while childAlive() && clock.Elapsed < TimeSpan.FromSeconds 2. do
                System.Threading.Thread.Sleep(25)
            let stillAlive = childAlive()
            if stillAlive then
                try Process.GetProcessById(childPid).Kill(true) with _ -> ()
            Assert.That(stillAlive, Is.False, "The timed-out process group left an orphan running.")
    )

[<Test>]
let prepared_project_task_snapshot_keeps_instructions_and_execution_in_sync () =
    workspace (fun root rt ->
        Directory.CreateDirectory(Path.Combine(root, "Demo", "src")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "Demo", "other")) |> ignore
        create root "Demo/src/AGENTS.md" "original task instructions" |> ignore
        create root "Demo/other/AGENTS.md" "replacement task instructions" |> ignore
        let config = Path.Combine(root, "Demo", ".jarvis.json")
        let writeConfig dir arg =
            File.WriteAllText(config,
                sprintf """{"tasks":{"probe":{"executable":"dotnet","args":["%s"],"workingDirectory":"%s","timeoutSeconds":10}}}"""
                    arg dir)
        writeConfig "src" "--version"
        let cmd : Common.AgentCommand = RunProjectTaskCommand { ProjectName = "Demo"; TaskName = "probe" }
        let target =
            Client.ProjectPaths.ProjectTarget.parse "Demo"
            |> function Ok value -> value | Error error -> failwithf "%A" error
        let snapshot =
            match Client.AgentInstructions.prepareOperationWithTarget rt (Some target) cmd with
            | Ok value -> value
            | Error error -> failwithf "Project task preparation failed: %A" error
        Assert.That(snapshot.Instructions |> List.map (fun d -> d.Path),
                    Is.EqualTo([ "Demo/src/AGENTS.md" ] :> obj))
        let prepared = snapshot.ProjectTask |> Option.defaultWith (fun () -> failwith "Missing configured task")
        Assert.That(prepared.WorkingDirectory, Is.EqualTo(Some "src"))
        Assert.That(prepared.Args, Is.EqualTo([ "--version" ] :> obj))
        // A concurrent configuration update must not change the command
        // whose instructions were selected and whose permissions were checked.
        writeConfig "other" "--info"
        let source = snapshot.Source |> Option.defaultWith (fun () -> failwith "Missing checkout")
        let current = Client.ClientShell.resolveProjectTaskIn source
                          { ProjectName = "Demo"; TaskName = "probe" } rt
        match current with
        | Ok latest -> Assert.That(latest.WorkingDirectory, Is.EqualTo(Some "other"))
        | Error error -> Assert.Fail(sprintf "Updated configuration not read: %A" error)
        let authorization = Client.ClientShell.projectTaskAsRunCommand "Demo" prepared
        Assert.That(authorization.Args, Is.EqualTo([ "--version" ] :> obj))
        match Client.ClientShell.runProjectTaskIn source prepared rt with
        | Ok output ->
            Assert.That(output.ExitCode, Is.EqualTo(0))
            Assert.That(output.StdOut.Trim(), Is.Not.Empty)
        | Error error -> Assert.Fail(sprintf "Prepared task could not run: %A" error)
    )
