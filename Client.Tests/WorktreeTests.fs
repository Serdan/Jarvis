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
let ``NUL porcelain parser preserves paths containing newlines`` () =
    let main = Path.Combine(Path.GetTempPath(), "main\ncheckout")
    let linked = Path.Combine(Path.GetTempPath(), "linked\ncheckout")
    let sample =
        $"worktree {main}\000HEAD abc\000branch refs/heads/main\000\000worktree {linked}\000HEAD def\000branch refs/heads/feature/linked\000\000"
    match parsePorcelainZ main sample with
    | Ok [ mainEntry; linkedEntry ] ->
        mainEntry.Path |> shouldEqual (Path.GetFullPath main)
        mainEntry.IsMain |> shouldEqual true
        linkedEntry.Path |> shouldEqual (Path.GetFullPath linked)
        linkedEntry.Branch |> shouldEqual (Some "feature/linked")
    | other -> Assert.Fail($"Unexpected parsed worktrees: {other}")

[<Test>]
let ``discovery includes existing sibling worktrees outside naming convention`` () =
    withRepo (fun root project context ->
        let sibling = Path.Combine(root, "another\nlayout")
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


[<Test>]
let ``unrelated worktree process does not block removal`` () =
    if OperatingSystem.IsWindows() then Assert.Ignore("Uses the POSIX sleep executable.")
    else
        withRepo (fun _ _ context ->
            let make name =
                createWorktree
                    { ProjectName = "Project1"; Name = name; Branch = "feature/" + name; BaseRef = "HEAD" } context
                |> function Ok item -> item | Error e -> failwithf "%A" e
            let target = make "remove-me"
            let other = make "keep-me"
            let id =
                Client.JobManager.startJob
                    { ProjectName = "Project1::keep-me"; Executable = "sleep"; Args = [ "30" ]
                      Reason = None; WorkingDirectory = None; MaxOutputBytes = Some 1024 } context
                |> function Ok started -> started.JobId | Error e -> failwithf "%A" e
            try
                match inspectRemoval { ProjectName = "Project1"; WorktreePath = target.Path; TargetRef = None } context with
                | Ok inspection ->
                    inspection.RunningJobCount |> shouldEqual 0
                    inspection.CanRemove |> shouldEqual true
                | Error e -> Assert.Fail($"Inspection failed: {e}")
                match removeWorktree { ProjectName = "Project1"; WorktreePath = target.Path; TargetRef = None } context with
                | Ok _ -> Directory.Exists other.Path |> shouldEqual true
                | Error e -> Assert.Fail($"Removal failed: {e}")
            finally
                Client.JobManager.cancelJob { ProjectName = "Project1"; JobId = id } context |> shouldEqual (Ok()))

[<Test>]
let ``removal reservation fences concurrent admission and unresolved provenance`` () =
    withRepo (fun root _ context ->
        let selected = ProjectPaths.ProjectTarget.parse "Project1" |> function Ok x -> x | Error e -> failwithf "%A" e
        let target: WorktreeActivity.RemovalTarget =
            { Project = ProjectPaths.ProjectTarget.projectId selected
              Path = Path.Combine(root, "target")
              ResolveCheckout = fun _ -> Error(ValidationError "unresolved")
              ResolveSourceRef = fun _ -> Error(ValidationError "unresolved") }
        let admit scope =
            WorktreeActivity.admit (Guid.NewGuid().ToString("N")) scope
        do
            use unknown =
                admit (WorktreeActivity.Checkout selected)
                |> function Ok x -> x | Error e -> failwithf "%A" e
            WorktreeActivity.count target |> shouldEqual 1
            match WorktreeActivity.reserveRemoval target with
            | Error 1 -> ()
            | _ -> Assert.Fail("Unresolved resource must block reservation.")
        do
            use unknownSource =
                admit (WorktreeActivity.SourceRef(selected, "refs/heads/feature/source", false))
                |> function Ok x -> x | Error e -> failwithf "%A" e
            WorktreeActivity.count target |> shouldEqual 1
            match WorktreeActivity.reserveRemoval target with
            | Error 1 -> ()
            | _ -> Assert.Fail("Unresolved source-ref provenance must block reservation.")
        use reservation =
            WorktreeActivity.reserveRemoval target
            |> function Ok x -> x | Error n -> failwithf "Blocked by %d" n
        let rejected scope =
            match admit scope with
            | Error _ -> ()
            | Ok x -> x.Dispose(); Assert.Fail("Conflicting admission succeeded.")
        rejected (WorktreeActivity.Paths [ Path.Combine(target.Path, "child") ])
        rejected (WorktreeActivity.Paths [ root ])
        rejected (WorktreeActivity.Unknown (Some target.Project))
        rejected (WorktreeActivity.Checkout selected)
        let otherProject =
            ProjectPaths.ProjectTarget.parse "Project2"
            |> function Ok x -> x | Error e -> failwithf "%A" e
        use unrelatedProject =
            admit (WorktreeActivity.ProjectWorktrees (ProjectPaths.ProjectTarget.projectId otherProject))
            |> function Ok x -> x | Error e -> failwithf "%A" e
        use unrelated =
            admit (WorktreeActivity.Paths [ target.Path + "-sibling" ])
            |> function Ok x -> x | Error e -> failwithf "%A" e
        use coordination =
            admit WorktreeActivity.NoCheckout
            |> function Ok x -> x | Error e -> failwithf "%A" e
        ())

[<Test>]
let ``promoted inspection and removal exclude themselves but count active target operations`` () =
    withRepo (fun root _ context ->
        let runtime = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
        let make name =
            createWorktree
                { ProjectName = "Project1"; Name = name; Branch = "feature/" + name; BaseRef = "HEAD" } context
            |> function Ok item -> item | Error e -> failwithf "%A" e
        let target = make "promoted-target"
        let other = make "promoted-other"
        let response payload: AgentCommandResponse =
            { Result = Some payload; Error = None; Instructions = []
              StateChangeEvidence = StateChangeEvidence.Confirmed []; InboxActivation = None; PendingJob = None }
        let serialize command = System.Text.Json.JsonSerializer.Serialize<AgentCommand>(command)
        let start command run =
            let reply =
                Client.SignalR.Client.executeOrPromoteWith runtime (serialize command) "Worker 3" TimeSpan.Zero
                    (fun _ _ -> run())
                |> _.GetAwaiter().GetResult()
            match reply.PendingJob with
            | Some handle -> handle
            | None -> failwithf "Expected pending operation: %A" reply
        let finish (handle: ToolJobHandle) =
            let mutable terminal = None
            let observed =
                System.Threading.SpinWait.SpinUntil((fun () ->
                    match Client.JobManager.getJobResult
                        { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None } context with
                    | Ok result when result.ToolResponse.IsSome ->
                        terminal <- result.ToolResponse
                        true
                    | _ -> false), TimeSpan.FromSeconds(10.))
            if not observed then failwithf "No terminal result for %s" handle.JobId
            terminal.Value
        let hold () = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        let runHeld (gate: System.Threading.Tasks.TaskCompletionSource<unit>) action () =
            task {
                do! gate.Task
                return action()
            }
        let coordination = hold()
        let sibling = hold()
        let targetOperation = hold()
        let inspectGate = hold()
        let removeGate = hold()
        let mutable jobs = []
        let remember command run =
            let handle = start command run
            jobs <- handle :: jobs
            handle
        try
            let inbox =
                TaskReadCommand
                    { ProjectName = "Project1"; AgentId = "Worker 3"; Action = "AgentInbox"
                      TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
                      Cursor = ""; PageSize = 0; BeforeOid = ""; AfterOid = ""; RelativePath = "" }
            remember inbox (runHeld coordination (fun () -> response "{}")) |> ignore
            remember (GetGitStatusCommand { ProjectName = "Project1::promoted-other" })
                (runHeld sibling (fun () -> response "{}")) |> ignore
            let targetJob =
                remember (GetGitStatusCommand { ProjectName = "Project1::promoted-target" })
                    (runHeld targetOperation (fun () -> response "{}"))
            let query: InspectWorktreeRemovalCommand =
                { ProjectName = "Project1"; WorktreePath = target.Path; TargetRef = None }
            let inspection =
                remember (InspectWorktreeRemovalCommand query)
                    (runHeld inspectGate (fun () ->
                        match inspectRemoval query context with
                        | Ok check ->
                            check.CanRemove |> shouldEqual false
                            check.RunningJobCount |> shouldEqual 1
                            response "ok"
                        | Error e -> failwithf "%A" e))
            inspectGate.SetResult()
            (finish inspection).Error |> shouldEqual None
            targetOperation.SetResult()
            (finish targetJob).Error |> shouldEqual None
            let remove: RemoveWorktreeCommand =
                { ProjectName = "Project1"; WorktreePath = target.Path; TargetRef = None }
            let removal =
                remember (RemoveWorktreeCommand remove)
                    (runHeld removeGate (fun () ->
                        match removeWorktree remove context with
                        | Ok removed -> removed.Path |> shouldEqual target.Path; response "ok"
                        | Error e -> failwithf "%A" e))
            removeGate.SetResult()
            (finish removal).Error |> shouldEqual None
            Directory.Exists target.Path |> shouldEqual false
            Directory.Exists other.Path |> shouldEqual true
        finally
            for gate in [ coordination; sibling; targetOperation; inspectGate; removeGate ] do gate.TrySetResult() |> ignore
            for handle in jobs do finish handle |> ignore)

[<Test>]
let ``branch-selected checkout writes are target scoped and fenced by reservations`` () =
    withRepo (fun root _ context ->
        let runtime = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
        let make name branch =
            createWorktree
                { ProjectName = "Project1"; Name = name; Branch = branch; BaseRef = "HEAD" } context
            |> function Ok item -> item | Error e -> failwithf "%A" e
        let source = make "branch-source" "feature/branch-source"
        let sibling = make "branch-sibling" "feature/branch-sibling"
        let sourceRef = "refs/heads/feature/branch-source"
        let response payload: AgentCommandResponse =
            { Result = Some payload; Error = None; Instructions = []
              StateChangeEvidence = StateChangeEvidence.Confirmed []; InboxActivation = None; PendingJob = None }
        let serialize (command: AgentCommand) = System.Text.Json.JsonSerializer.Serialize<AgentCommand>(command)
        let startHeld (command: AgentCommand) =
            let resolveGate = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            let resolved = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            let gate = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            let reply =
                Client.SignalR.Client.executeOrPromoteWith runtime (serialize command) "Worker 3" TimeSpan.Zero
                    (fun _ _ ->
                        task {
                            do! resolveGate.Task
                            let resolution = WorktreeActivity.resolveCurrent source.Path
                            resolved.TrySetResult() |> ignore
                            match resolution with
                            | Ok () -> ()
                            | Error e -> failwithf "Could not pin resolved checkout: %A" e
                            do! gate.Task
                            return response "{}"
                        })
                |> _.GetAwaiter().GetResult()
            match reply.PendingJob with
            | Some handle -> handle, resolveGate, resolved, gate
            | None ->
                resolveGate.TrySetResult() |> ignore
                gate.TrySetResult() |> ignore
                failwithf "Expected pending operation: %A" reply
        let finish (handle: ToolJobHandle) =
            let mutable terminal = None
            let observed =
                System.Threading.SpinWait.SpinUntil((fun () ->
                    match Client.JobManager.getJobResult
                        { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None } context with
                    | Ok result when result.ToolResponse.IsSome ->
                        terminal <- result.ToolResponse
                        true
                    | _ -> false), TimeSpan.FromSeconds(10.))
            if not observed then failwithf "No terminal result for %s" handle.JobId
            terminal.Value
        let inspect path =
            let query: InspectWorktreeRemovalCommand =
                { ProjectName = "Project1"; WorktreePath = path; TargetRef = None }
            match inspectRemoval query context with
            | Ok check -> check
            | Error e -> failwithf "Inspection failed: %A" e
        let review action headOid : AgentCommand =
            ReviewWriteCommand
                { ProjectName = "Project1"; Action = action; OperationId = Guid.NewGuid().ToString("D")
                  TaskId = "worktree-removal-test"; RequestId = Guid.NewGuid().ToString("D")
                  ActorId = "Worker 3"; ReviewerId = ""; ExpectedVersion = 1; Kind = ""
                  SourceRef = sourceRef; HeadOid = headOid; TargetRef = ""; Summary = ""; Verdict = ""
                  DocumentPath = ""; Content = ""; ExpectedBlobOid = "" }
        let taskWrite action : AgentCommand =
            TaskWriteCommand
                { ProjectName = "Project1"; Action = action; OperationId = Guid.NewGuid().ToString("D")
                  TaskId = "worktree-removal-test"; AttemptId = "attempt"; ActorId = "Worker 3"
                  OwnerId = "Worker 3"; Title = ""; Html = ""; ExpectedVersion = 1
                  ExpectedAgentVersion = 1; ExpectedTaskVersion = 1; AssignmentGeneration = 1
                  Summary = ""; NextAction = ""; Reason = ""; BaseOid = ""
                  TargetRef = "refs/heads/main"; SourceRef = sourceRef; AllowDetached = false
                  AllowUncommitted = false; DecisionSequence = 0L; Evidence = ""; SessionId = "" }
        let runHeldCase command verifyRelocation =
            let handle, resolveGate, resolved, gate = startHeld command
            try
                let targetCheck = inspect source.Path
                targetCheck.CanRemove |> shouldEqual false
                targetCheck.RunningJobCount |> shouldEqual 1
                let siblingCheck = inspect sibling.Path
                siblingCheck.CanRemove |> shouldEqual true
                siblingCheck.RunningJobCount |> shouldEqual 0
                resolveGate.TrySetResult() |> ignore
                if not (resolved.Task.Wait(TimeSpan.FromSeconds(10.))) then failwith "Source checkout resolution did not complete."
                let pinnedCheck = inspect source.Path
                pinnedCheck.CanRemove |> shouldEqual false
                pinnedCheck.RunningJobCount |> shouldEqual 1
                if verifyRelocation then
                    GitCommandTests.expectGitSuccess context [ "-C"; source.Path; "switch"; "-c"; "feature/after-resolution" ]
                    let relocated = Path.Combine(root, "branch-source-relocated")
                    GitCommandTests.expectGitSuccess context [ "worktree"; "add"; relocated; sourceRef ]
                    let originalCheck = inspect source.Path
                    originalCheck.CanRemove |> shouldEqual false
                    originalCheck.RunningJobCount |> shouldEqual 1
                    let relocatedCheck = inspect relocated
                    relocatedCheck.CanRemove |> shouldEqual true
                    relocatedCheck.RunningJobCount |> shouldEqual 0
                    GitCommandTests.expectGitSuccess context [ "worktree"; "remove"; relocated ]
                    GitCommandTests.expectGitSuccess context [ "-C"; source.Path; "switch"; "feature/branch-source" ]
            finally
                resolveGate.TrySetResult() |> ignore
                gate.TrySetResult() |> ignore
            (finish handle).Error |> shouldEqual None
        runHeldCase (review "Submit" "") true
        runHeldCase (review "Update" "") false
        runHeldCase (taskWrite "BindAttemptSource") false

        let selected =
            ProjectPaths.ProjectTarget.parse "Project1"
            |> function Ok target -> target | Error e -> failwithf "%A" e
        let removalTarget: WorktreeActivity.RemovalTarget =
            { Project = ProjectPaths.ProjectTarget.projectId selected
              Path = source.Path
              ResolveCheckout = fun _ -> Ok source.Path
              ResolveSourceRef = fun reference ->
                  if reference = sourceRef then Ok(Some source.Path) else Ok None }
        use reservation =
            WorktreeActivity.reserveRemoval removalTarget
            |> function Ok lease -> lease | Error count -> failwithf "Unexpected blockers: %d" count
        let reject command =
            let reply =
                Client.SignalR.Client.executeOrPromoteWith runtime (serialize command) "Worker 3" TimeSpan.Zero
                    (fun _ _ -> System.Threading.Tasks.Task.FromResult(response "{}"))
                |> _.GetAwaiter().GetResult()
            match reply.PendingJob, reply.Error with
            | None, Some _ -> ()
            | _ -> Assert.Fail(sprintf "Conflicting source operation was admitted: %A" reply)
        let allow command =
            let reply =
                Client.SignalR.Client.executeOrPromoteWith runtime (serialize command) "Worker 3" (TimeSpan.FromSeconds 5.)
                    (fun _ _ -> System.Threading.Tasks.Task.FromResult(response "{}"))
                |> _.GetAwaiter().GetResult()
            reply.Error |> shouldEqual None
            match reply.PendingJob with
            | Some handle -> (finish handle).Error |> shouldEqual None
            | None -> ()
        reject (review "Submit" "")
        reject (review "Update" "")
        reject (taskWrite "BindAttemptSource")
        allow (review "Claim" "")
        allow (review "Submit" (String.replicate 40 "0"))
        allow (taskWrite "RebindAttemptSource"))
