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
              ResolveCheckout = fun _ -> Error(ValidationError "unresolved") }
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
                    { ProjectName = "Project1"; LaunchId = null; AgentId = "Worker 3"; Action = "AgentInbox"
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


let private reviewCommand project action head : ReviewWriteCommand =
    { ProjectName = project; Action = action; OperationId = Guid.NewGuid().ToString("D")
      TaskId = "review-task"; RequestId = Guid.NewGuid().ToString("D")
      ActorId = "Worker 3"; ReviewerId = ""; ExpectedVersion = 1
      Kind = ""; SourceRef = "refs/heads/feature/observed"; HeadOid = head
      TargetRef = ""; Summary = ""; Verdict = ""; DocumentPath = ""; Content = ""; ExpectedBlobOid = "" }

let private bindCommand project action =
    TaskWriteCommand
        { ProjectName = project; Action = action; OperationId = Guid.NewGuid().ToString("D")
          TaskId = "review-task"; AttemptId = ""; ActorId = "Worker 3"; OwnerId = "Worker 3"
          Title = ""; Html = ""; ExpectedVersion = 1; ExpectedAgentVersion = 0
          ExpectedTaskVersion = 0; AssignmentGeneration = 1; Summary = ""; NextAction = ""
          Reason = ""; BaseOid = ""; TargetRef = ""; SourceRef = "refs/heads/feature/observed"
          AllowDetached = false; AllowUncommitted = false; DecisionSequence = 0L; Evidence = ""; SessionId = "" }

let private checkoutObservationCommands project =
    [ ReviewWriteCommand (reviewCommand project "Submit" "")
      ReviewWriteCommand (reviewCommand project "Update" " \t")
      bindCommand project "BindAttemptSource"
      ReviewWriteCommand { reviewCommand project "Submit" "" with SourceRef = "" } ]

let private coordinationCommands project =
    [ ReviewWriteCommand (reviewCommand project "Submit" (String.replicate 40 "a"))
      ReviewWriteCommand (reviewCommand project "Update" (String.replicate 40 "a"))
      ReviewWriteCommand (reviewCommand project "Claim" "")
      bindCommand project "RebindAttemptSource"
      bindCommand project "CheckpointAttempt"
      RetryPendingIntentCommand
          { ProjectName = project; OperationId = Guid.NewGuid().ToString("D")
            OperatorId = "Lead"; ExpectedCommandSha256 = ""; ExpectedVerifiedHead = "" } ]

let private makeObservationTarget root context =
    let created =
        createWorktree
            { ProjectName = "Project1"; Name = "observed"; Branch = "feature/observed"; BaseRef = "HEAD" } context
        |> function Ok item -> item | Error e -> failwithf "%A" e
    let selected = ProjectPaths.ProjectTarget.parse "Project1" |> function Ok x -> x | Error e -> failwithf "%A" e
    let target: WorktreeActivity.RemovalTarget =
        { Project = ProjectPaths.ProjectTarget.projectId selected; Path = created.Path
          ResolveCheckout = fun _ -> Ok(Path.Combine(root, "Project1")) }
    target

let private accepted = function Ok lease -> lease | Error error -> failwithf "Admission failed: %A" error
let private rejected = function
    | Error(ValidationError message) -> Assert.That(message, Does.Contain("worktree removal"))
    | Error error -> Assert.Fail(sprintf "Unexpected error: %A" error)
    | Ok (lease: IDisposable) -> lease.Dispose(); Assert.Fail("Conflicting admission succeeded.")

[<Test>]
let ``typed direct checkout observations fence a different registered branch`` () =
    withRepo (fun root _ context ->
        let target = makeObservationTarget root context
        let admit = Client.SignalR.Client.admitCommand
        let runtime = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
        for command in checkoutObservationCommands "Project1" do
            do
                use active = admit command |> accepted
                // Independent removal operation, not the observing command itself.
                use observer = WorktreeActivity.enter "independent-removal"
                WorktreeActivity.count target |> shouldEqual 1
                let query: InspectWorktreeRemovalCommand =
                    { ProjectName = "Project1"; WorktreePath = target.Path; TargetRef = None }
                let inspection = inspectRemoval query context |> function Ok x -> x | Error e -> failwithf "%A" e
                inspection.CanRemove |> shouldEqual false
                inspection.RunningJobCount |> shouldEqual 1
                match WorktreeActivity.reserveRemoval target with
                | Error 1 -> ()
                | other -> Assert.Fail(sprintf "Unexpected reservation: %A" other)
            WorktreeActivity.count target |> shouldEqual 0
            do
                use reservation = WorktreeActivity.reserveRemoval target |> function Ok x -> x | Error n -> failwithf "%d" n
                admit command |> rejected
                match Client.SignalR.Client.receiveCommand runtime command |> _.GetAwaiter().GetResult() with
                | Error(ValidationError message) -> Assert.That(message, Does.Contain("worktree removal"))
                | other -> Assert.Fail(sprintf "Direct dispatch crossed removal reservation: %A" other)
                for control in coordinationCommands "Project1" @ checkoutObservationCommands "OtherProject" do
                    use allowed = admit control |> accepted
                    ()
            use resumed = admit command |> accepted
            ())

[<Test>]
let ``typed promoted checkout observations fence before executing callback`` () =
    withRepo (fun root _ context ->
        let target = makeObservationTarget root context
        let runtime = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
        let response: AgentCommandResponse =
            { Result = Some "{}"; Error = None; Instructions = []
              StateChangeEvidence = StateChangeEvidence.Confirmed []; InboxActivation = None; PendingJob = None }
        let run command callback =
            let json = System.Text.Json.JsonSerializer.Serialize<AgentCommand>(command)
            Client.SignalR.Client.executeOrPromoteWith runtime json "Worker 3" TimeSpan.Zero callback
            |> _.GetAwaiter().GetResult()
        let finish (handle: ToolJobHandle) =
            let mutable terminal = None
            let observed =
                System.Threading.SpinWait.SpinUntil((fun () ->
                    match Client.JobManager.getJobResult
                        { ProjectName = "Project1"; JobId = handle.JobId; AfterSequence = None } context with
                    | Ok result when result.ToolResponse.IsSome -> terminal <- result.ToolResponse; true
                    | _ -> false), TimeSpan.FromSeconds(10.))
            if not observed then failwithf "No terminal result for %s" handle.JobId
            terminal.Value
        for command in checkoutObservationCommands "Project1" do
            let gate = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            let started =
                run command (fun _ _ -> task {
                    do! gate.Task
                    return response
                })
            let handle = started.PendingJob |> Option.defaultWith (fun () -> failwithf "Expected promoted job: %A" started)
            try
                WorktreeActivity.count target |> shouldEqual 1
                match WorktreeActivity.reserveRemoval target with
                | Error 1 -> ()
                | other -> Assert.Fail(sprintf "Unexpected reservation: %A" other)
            finally
                gate.TrySetResult() |> ignore
                (finish handle).Error |> shouldEqual None
            do
                use reservation = WorktreeActivity.reserveRemoval target |> function Ok x -> x | Error n -> failwithf "%d" n
                let mutable called = false
                let callback _ _ =
                    called <- true
                    System.Threading.Tasks.Task.FromResult response
                // The exact terminal operation can return its cached result
                // without re-executing the checkout observation.
                let cached = run command callback
                cached.Error |> shouldEqual None
                called |> shouldEqual false
                let newId = Guid.NewGuid().ToString("D")
                let fresh =
                    match command with
                    | ReviewWriteCommand saved -> ReviewWriteCommand { saved with OperationId = newId }
                    | TaskWriteCommand saved -> TaskWriteCommand { saved with OperationId = newId }
                    | _ -> failwith "Unexpected checkout observation command."
                let blocked = run fresh callback
                blocked.PendingJob |> shouldEqual None
                blocked.Error.IsSome |> shouldEqual true
                called |> shouldEqual false
                for control in coordinationCommands "Project1" do
                    let allowed = run control (fun _ _ -> System.Threading.Tasks.Task.FromResult response)
                    match allowed.PendingJob with
                    | Some handle -> (finish handle).Error |> shouldEqual None
                    | None -> allowed.Error |> shouldEqual None
            WorktreeActivity.count target |> shouldEqual 0)

[<Test>]
let ``saved review replay takes its own lease within ledger-only parent`` () =
    withRepo (fun root _ context ->
        let target = makeObservationTarget root context
        let parentId = Guid.NewGuid().ToString("N")
        use parent = WorktreeActivity.admit parentId WorktreeActivity.NoCheckout |> accepted
        use parentContext = WorktreeActivity.enter parentId
        for action in [ "Submit"; "Update" ] do
            let saved = reviewCommand "Project1" action ""
            do
                use replay = WorktreeActivity.admitReviewReplay saved |> accepted
                use observer = WorktreeActivity.enter "independent-removal"
                WorktreeActivity.count target |> shouldEqual 1
                match WorktreeActivity.reserveRemoval target with
                | Error 1 -> ()
                | other -> Assert.Fail(sprintf "Replay did not fence: %A" other)
            WorktreeActivity.count target |> shouldEqual 0
            do
                use reservation = WorktreeActivity.reserveRemoval target |> function Ok x -> x | Error n -> failwithf "%d" n
                WorktreeActivity.admitReviewReplay saved |> rejected
                use explicitHead =
                    WorktreeActivity.admitReviewReplay { saved with HeadOid = String.replicate 40 "a" } |> accepted
                use ledgerOnly = WorktreeActivity.admitReviewReplay { saved with Action = "Claim" } |> accepted
                ()
            use resumed = WorktreeActivity.admitReviewReplay saved |> accepted
            ())
