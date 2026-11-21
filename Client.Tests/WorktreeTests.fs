module WorktreeTests

open System
open System.IO
open Client
open Client.Worktrees
open Client.IO
open Common
open NUnit.Framework
open FsUnitTyped

let private withRepo action =
    let root, project = GitCommandTests.createTempProject ()
    try
        let context = GitCommandTests.TestContext(root)
        GitCommandTests.initRepo context
        File.WriteAllText(Path.Combine(project, "README.md"), "initial")
        GitCommandTests.expectGitSuccess context [ "add"; "README.md" ]
        GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "Initial" ]
        action root project context
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Test>]
let ``porcelain parser retains worktree flags and paths`` () =
    let sample = "worktree /tmp/test-main\nHEAD abc\nbranch refs/heads/main\n\nworktree /tmp/test-linked\nHEAD def\ndetached\nlocked keep\nprunable missing\n\n"
    match parsePorcelain "/tmp/test-main" sample with
    | Ok [main; linked] ->
        main.IsMain |> shouldEqual true
        main.Branch |> shouldEqual (Some "main")
        linked.IsDetached |> shouldEqual true
        linked.IsLocked |> shouldEqual true
        linked.IsPrunable |> shouldEqual true
    | other -> Assert.Fail($"Unexpected parsed worktrees: {other}")

[<Test>]
let ``malformed porcelain record is reported`` () =
    match parsePorcelain "/tmp/main" "worktree /tmp/main\n\n" with
    | Error _ -> ()
    | _ -> Assert.Fail("Malformed records must fail.")

[<Test>]
let ``discovery includes existing sibling worktrees outside naming convention`` () =
    withRepo (fun root project context ->
        let sibling = Path.Combine(root, "another-layout")
        GitCommandTests.expectGitSuccess context [ "worktree"; "add"; "-b"; "feature/old"; sibling; "HEAD" ]
        match listWorktrees { ProjectName = "Project1" } context with
        | Ok entries ->
            entries.Length |> shouldEqual 2
            entries |> List.exists (fun x -> x.Path = sibling && x.Branch = Some "feature/old") |> shouldEqual true
            entries |> List.exists (fun x -> x.Path = project && x.IsMain) |> shouldEqual true
        | Error e -> Assert.Fail($"Discovery failed: {e}"))

[<Test>]
let ``bulk worktree discovery has unknown cleanliness while selected inspection stays accurate`` () =
    withRepo (fun root _ context ->
        let sibling = Path.Combine(root, "bulk-discovery-linked")
        GitCommandTests.expectGitSuccess context
            [ "worktree"; "add"; "-b"; "feature/bulk-discovery"; sibling; "HEAD" ]
        let command: ListWorktreesCommand = { ProjectName = "Project1" }
        match listWorktrees command context with
        | Error e -> Assert.Fail($"Discovery failed: {e}")
        | Ok all ->
            Assert.That(all.Length, Is.EqualTo(2))
            Assert.That(all |> List.forall (fun entry -> entry.IsClean.IsNone), Is.True)
        let selected = { ProjectName = "Project1"; WorktreePath = sibling }
        match getWorktree selected context with
        | Error e -> Assert.Fail($"Selected inspection failed: {e}")
        | Ok entry -> Assert.That(entry.IsClean, Is.EqualTo(Some true))
        File.WriteAllText(Path.Combine(sibling, "uncommitted.txt"), "dirty")
        match getWorktree selected context with
        | Error e -> Assert.Fail($"Selected dirty inspection failed: {e}")
        | Ok entry -> Assert.That(entry.IsClean, Is.EqualTo(Some false))
        let removal: InspectWorktreeRemovalCommand = { ProjectName = "Project1"; WorktreePath = sibling; TargetRef = Some "HEAD" }
        match inspectRemoval removal context with
        | Error e -> Assert.Fail($"Removal safety inspection failed: {e}")
        | Ok inspected ->
            Assert.That(inspected.IsClean, Is.False)
            Assert.That(inspected.CanRemove, Is.False))

[<Test>]
let ``creation uses hidden project container and supports slash branches`` () =
    withRepo (fun root _ context ->
        let cmd = { ProjectName = "Project1"; Name = "input-config"; Branch = "feature/input-config"; BaseRef = "HEAD" }
        match createWorktree cmd context with
        | Ok info ->
            info.Path |> shouldEqual (Path.Combine(root, ".Project1-worktrees", "input-config"))
            info.Branch |> shouldEqual (Some "feature/input-config")
            Directory.Exists info.Path |> shouldEqual true
        | Error e -> Assert.Fail($"Create failed: {e}")
        match createWorktree cmd context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Duplicate worktree must fail."))

[<Test>]
let ``creation rejects traversal name`` () =
    withRepo (fun _ _ context ->
        match createWorktree { ProjectName = "Project1"; Name = "../escape"; Branch = "feature/escape"; BaseRef = "HEAD" } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Traversal must be rejected."))

[<Test>]
let ``getWorktree rejects unregistered path`` () =
    withRepo (fun root _ context ->
        match getWorktree { ProjectName = "Project1"; WorktreePath = root } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Unregistered path must be rejected."))

[<Test>]
let ``removal inspection refuses unmerged worktree and cleanup preserves it`` () =
    withRepo (fun _ _ context ->
        let created =
            match createWorktree { ProjectName = "Project1"; Name = "unmerged"; Branch = "feature/unmerged"; BaseRef = "HEAD" } context with
            | Ok item -> item
            | Error e -> failwith $"Create failed: {e}"
        let query: InspectWorktreeRemovalCommand = { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None }
        match inspectRemoval query context with
        | Ok check ->
            // Initial HEAD is shared and hence already an ancestor. Dirty state should still prevent removal.
            File.WriteAllText(Path.Combine(created.Path, "draft.txt"), "uncommitted")
            match inspectRemoval query context with
            | Ok dirty -> dirty.CanRemove |> shouldEqual false
            | Error e -> Assert.Fail($"Inspection failed: {e}")
        | Error e -> Assert.Fail($"Inspection failed: {e}")
        match removeWorktree { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
        | Error _ -> Directory.Exists created.Path |> shouldEqual true
        | Ok _ -> Assert.Fail("Dirty worktree removed."))

[<Test>]
let ``cleanup of integrated clean worktree leaves branch and deletes empty container`` () =
    withRepo (fun root _ context ->
        let created =
            match createWorktree { ProjectName = "Project1"; Name = "finished"; Branch = "feature/finished"; BaseRef = "HEAD" } context with
            | Ok item -> item
            | Error e -> failwith $"Create failed: {e}"
        match inspectRemoval { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
        | Ok readiness -> readiness.CanRemove |> shouldEqual true
        | Error e -> Assert.Fail($"Inspection failed: {e}")
        match removeWorktree { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
        | Ok removed -> removed.Path |> shouldEqual created.Path
        | Error e -> Assert.Fail($"Remove failed: {e}")
        Directory.Exists created.Path |> shouldEqual false
        Directory.Exists(Path.Combine(root, ".Project1-worktrees")) |> shouldEqual false
        GitCommandTests.gitOutput context [ "branch"; "--list"; "feature/finished" ]
        |> _.Contains("feature/finished") |> shouldEqual true)

[<Test>]
let ``locked worktree cannot be removed`` () =
    withRepo (fun _ _ context ->
        let created =
            match createWorktree { ProjectName = "Project1"; Name = "locked"; Branch = "feature/locked"; BaseRef = "HEAD" } context with
            | Ok item -> item
            | Error e -> failwith $"Create failed: {e}"
        GitCommandTests.expectGitSuccess context [ "worktree"; "lock"; created.Path ]
        match inspectRemoval { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
        | Ok readiness -> readiness.CanRemove |> shouldEqual false
        | Error e -> Assert.Fail($"Inspection failed: {e}")
        match removeWorktree { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
        | Error _ -> Directory.Exists created.Path |> shouldEqual true
        | Ok _ -> Assert.Fail("Locked worktree removed."))
[<Test>]
let ``existing file and Git commands accept registered worktree selector`` () =
    withRepo (fun _ project context ->
        let created =
            match createWorktree { ProjectName = "Project1"; Name = "selector"; Branch = "feature/selector"; BaseRef = "HEAD" } context with
            | Ok item -> item
            | Error e -> failwith $"Create failed: {e}"
        File.WriteAllText(Path.Combine(created.Path, "other.txt"), "only linked")
        match Client.ClientShell.getGitStatus { ProjectName = "Project1::selector" } context with
        | Ok result ->
            result.ExitCode |> shouldEqual 0
            result.StdOut.Contains("other.txt") |> shouldEqual true
        | Error e -> Assert.Fail($"Worktree selector failed: {e}")
        match Client.ProjectBrowser.readFile
                  { ProjectName = "Project1::selector"; FilePath = "other.txt"; StartLine = None; EndLine = None; IncludeLineNumbers = None } context with
        | Ok (Content content) -> content |> shouldEqual "only linked"
        | other -> Assert.Fail($"Linked worktree read failed: {other}")
        match Client.ClientShell.getGitStatus { ProjectName = "Project1::ghost" } context with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Unregistered worktree should not resolve.")
        match Client.ProjectBrowser.writeFile
                  { ProjectName = "Project1::selector"; FilePath = "created-through-file-tool.txt"
                    Content = "linked worktree only"; FileWriteMode = Write
                    ExpectedHash = None; CreateParents = None } context with
        | Ok () -> ()
        | Error e -> Assert.Fail($"Registered worktree edit failed: {e}")
        File.ReadAllText(Path.Combine(created.Path, "created-through-file-tool.txt"))
        |> shouldEqual "linked worktree only"
        // Main worktree is still isolated from linked edits.
        File.Exists(Path.Combine(project, "other.txt")) |> shouldEqual false
        File.Exists(Path.Combine(project, "created-through-file-tool.txt")) |> shouldEqual false)

[<Test>]
let ``list projects hides linked sibling worktrees discovered through Git`` () =
    withRepo (fun root _ context ->
        let sibling = Path.Combine(root, "linked-sibling")
        GitCommandTests.expectGitSuccess context [ "worktree"; "add"; "-b"; "feature/sibling"; sibling; "HEAD" ]
        match Client.ProjectBrowser.listProjects context with
        | Ok projects ->
            let names = projects |> Seq.map (fun (ProjectName name) -> name) |> Seq.toList
            names |> shouldContain "Project1"
            names |> shouldNotContain "linked-sibling"
        | Error e -> Assert.Fail($"ListProjects failed: {e}"))

[<Test>]
let ``unmerged unique worktree commit prevents safe removal`` () =
    withRepo (fun _ _ context ->
        let created =
            match createWorktree { ProjectName = "Project1"; Name = "pending"; Branch = "feature/pending"; BaseRef = "HEAD" } context with
            | Ok item -> item
            | Error e -> failwith $"Create failed: {e}"
        let feature = "Project1::pending"
        match Client.ClientShell.runCommand
            { ProjectName = feature; Executable = "git"; Args = [ "commit"; "--allow-empty"; "-m"; "Unique" ]
              Reason = None; WorkingDirectory = None; TimeoutSeconds = Some 10; MaxOutputBytes = Some 4000 } context with
        | Ok result when result.ExitCode = 0 -> ()
        | other -> Assert.Fail($"Unique commit failed: {other}")
        match inspectRemoval { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
        | Ok inspect -> inspect.IsIntegrated |> shouldEqual false
        | Error e -> Assert.Fail($"Unmerged inspection failed: {e}")
        match removeWorktree { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
        | Error _ -> Directory.Exists created.Path |> shouldEqual true
        | Ok _ -> Assert.Fail("Unmerged branch was removed."))

[<Test>]
let ``active job in a selected worktree blocks safe removal`` () =
    if OperatingSystem.IsWindows() then
        Assert.Ignore("Uses the POSIX sleep executable.")
    else
        withRepo (fun _ _ context ->
            let created =
                match createWorktree
                    { ProjectName = "Project1"; Name = "running-job"
                      Branch = "feature/running-job"; BaseRef = "HEAD" } context with
                | Ok item -> item
                | Error e -> failwith $"Create failed: {e}"
            let id =
                match Client.JobManager.startJob
                    { ProjectName = "Project1::running-job"
                      Executable = "sleep"; Args = [ "30" ]; Reason = Some "Test active worktree job"
                      WorkingDirectory = None; MaxOutputBytes = Some 1024 } context with
                | Ok started -> started.JobId
                | Error e -> failwith $"StartJob failed: {e}"
            try
                match inspectRemoval { ProjectName = "Project1"; WorktreePath = created.Path; TargetRef = None } context with
                | Ok inspection ->
                    inspection.CanRemove |> shouldEqual false
                    inspection.RunningJobCount |> shouldEqual 1
                | Error e -> Assert.Fail($"Inspection failed: {e}")
            finally
                Client.JobManager.cancelJob { ProjectName = "Project1::running-job"; JobId = id } context |> shouldEqual (Ok()))

[<TestCase(0)>]
[<TestCase(220)>]
[<TestCase(2200)>]
let source_pinned_worktree_bulk_read_has_one_git_command_across_synthetic_populations (recordCount: int) =
    // Parsing the complete porcelain output is a pure operation, independent of
    // worktree count. It must not reintroduce per-worktree status subprocesses.
    let main = Path.Combine(Path.GetTempPath(), "perf-worktree-main")
    let records =
        [ for index in 1 .. recordCount ->
            sprintf "worktree %s\nHEAD %s\nbranch refs/heads/synthetic-%04d\n\n"
                (Path.Combine(Path.GetTempPath(), sprintf "perf-worktree-%04d" index))
                (String.replicate 40 "a") index ]
        |> String.concat ""
    match parsePorcelain main records with
    | Ok items -> Assert.That(items.Length, Is.EqualTo(recordCount))
    | Error error -> Assert.Fail("Bulk porcelain fixture must decode: " + error)
    withRepo (fun root _ context ->
        let observe (phase: string) (expectedCount: int) =
            let previous, trace = McpLatencyTrace.beginTrace true (Guid.NewGuid().ToString("D"))
            try
                let result = listWorktrees { ProjectName = "Project1" } context
                match result with
                | Ok entries -> Assert.That(entries.Length, Is.EqualTo(expectedCount))
                | Error error -> Assert.Fail(sprintf "Actual disposable Git bulk discovery failed: %A" error)
                let diagnostic = McpLatencyTrace.render trace "completed"
                Assert.That(diagnostic.IsSome, Is.True)
                use json = System.Text.Json.JsonDocument.Parse(diagnostic.Value)
                let stages = json.RootElement.GetProperty("stages").EnumerateArray() |> Seq.toList
                Assert.That(stages.Length, Is.EqualTo(1),
                            "Bulk ListWorktrees must issue one Git child and no fetch/status-per-worktree.")
                Assert.That(stages.Head.GetProperty("stage").GetString(), Is.EqualTo("git.worktree"))
                Assert.That(stages.Head.GetProperty("calls").GetInt32(), Is.EqualTo(1))
                TestContext.Progress.WriteLine(
                    System.Text.Json.JsonSerializer.Serialize(
                        {| records = recordCount; actualWorktrees = expectedCount
                           phase = phase; actualGitWorktreeCommands = 1
                           remoteFetches = 0; syntheticParserPopulation = true |}))
            finally McpLatencyTrace.restore previous
        observe "cold" 1
        observe "warm" 1
        // A real topology change (not merely synthetic text) must still use
        // one bulk command with unknown, not fabricated, cleanliness.
        let sibling = Path.Combine(root, "perf-bulk-advanced")
        GitCommandTests.expectGitSuccess context
            [ "worktree"; "add"; "-b"; "feature/perf-bulk-advanced"; sibling; "HEAD" ]
        observe "advance" 2)
