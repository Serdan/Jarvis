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
        // Main worktree is still isolated from linked edits.
        File.Exists(Path.Combine(project, "other.txt")) |> shouldEqual false)

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
