module WorktreeTests

open System
open System.IO
open Client.Worktrees
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
