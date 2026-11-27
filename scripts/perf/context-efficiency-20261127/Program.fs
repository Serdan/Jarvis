// Transport and disposable fixture derived from git-call-optimization-20261126/Program.fs at 291b549.
namespace ContextEfficiencyHarness

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Claims
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open ModelContextProtocol.Protocol
open Common
open Common.SignalR
open global.Client
open global.Client.LedgerStore
open global.Client.OrchestrationApi
open global.Client.ReviewWorkflow
open global.Client.SignalR
open Server
open Server.Services

type CapturedResponse =
    { CorrelationId: string
      Response: AgentCommandResponse }

type ToolHarness =
    { Client: ClientService
      Http: HttpContextAccessor
      Services: ServiceProvider
      Responses: ConcurrentQueue<CapturedResponse>
      Timings: ConcurrentDictionary<string, CommandDispatchTiming>
      Queue: CommandDispatchQueue
      UserId: string
      ConnectionId: string
      DeviceId: string
      Generation: int64 }

type TraceSummary =
    { GitProcessStarts: int
      RemoteFetches: int
      CommandCounts: Dictionary<string, int>
      TracePath: string }

module Program =
    let private fixedDate = "2026-11-26T11:00:00+00:00"
    let private fixedProjectId = Guid.Parse("10000000-0000-4000-8000-000000000001")
    let private fixedLedgerId = Guid.Parse("20000000-0000-4000-8000-000000000001")
    let private fixedOperation = Guid.Parse("30000000-0000-4000-8000-000000000001")

    let private git workingDirectory (arguments: string list) =
        use gitProcess = new Process()
        gitProcess.StartInfo.FileName <- "git"
        gitProcess.StartInfo.WorkingDirectory <- workingDirectory
        gitProcess.StartInfo.UseShellExecute <- false
        gitProcess.StartInfo.RedirectStandardOutput <- true
        gitProcess.StartInfo.RedirectStandardError <- true
        gitProcess.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] <- "1"
        gitProcess.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        gitProcess.StartInfo.Environment["GIT_AUTHOR_NAME"] <- "Jarvis Perf Fixture"
        gitProcess.StartInfo.Environment["GIT_AUTHOR_EMAIL"] <- "jarvis-perf@example.invalid"
        gitProcess.StartInfo.Environment["GIT_COMMITTER_NAME"] <- "Jarvis Perf Fixture"
        gitProcess.StartInfo.Environment["GIT_COMMITTER_EMAIL"] <- "jarvis-perf@example.invalid"
        gitProcess.StartInfo.Environment["GIT_AUTHOR_DATE"] <- fixedDate
        gitProcess.StartInfo.Environment["GIT_COMMITTER_DATE"] <- fixedDate
        for argument in arguments do
            gitProcess.StartInfo.ArgumentList.Add(argument)
        if not (gitProcess.Start()) then
            failwithf "Could not start git %s" (String.Join(" ", arguments))
        let stdout = gitProcess.StandardOutput.ReadToEndAsync()
        let stderr = gitProcess.StandardError.ReadToEndAsync()
        gitProcess.WaitForExit()
        let output = stdout.GetAwaiter().GetResult()
        let error = stderr.GetAwaiter().GetResult()
        if gitProcess.ExitCode <> 0 then
            failwithf "git %s failed (%d): %s%s" (String.Join(" ", arguments)) gitProcess.ExitCode output error
        output.Trim()

    let private hash (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(value))
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private normalizeText (fixtureRoot: string) (value: string) =
        let withoutRoot =
            if String.IsNullOrEmpty fixtureRoot then value
            else value.Replace(fixtureRoot, "$FIXTURE", StringComparison.Ordinal)
        Regex.Replace(
            withoutRoot,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})",
            "<TIME>")

    let private canonicalJson fixtureRoot (json: string) =
        try
            use document = JsonDocument.Parse(json)
            let builder = StringBuilder()
            let volatileProperties =
                HashSet<string>(
                    [| "createdAtUtc"; "updatedAtUtc"; "registeredAtUtc"; "lastSeenAtUtc"
                       "stateEnteredAtUtc"; "workingTurnStartedAtUtc"; "startedAtUtc"
                       "completedAtUtc"; "recordedAtUtc"; "timestamp"; "durationMs"
                       "elapsedMs"; "lastUsedAtUtc" |],
                    StringComparer.OrdinalIgnoreCase)
            let commitProperties =
                HashSet<string>(
                    [| "headOid"; "commitOid"; "snapshotOid"; "currentHeadOid"
                       "ledgerHeadOid"; "committedHeadOid" |],
                    StringComparer.OrdinalIgnoreCase)
            let rec write propertyName (element: JsonElement) =
                match element.ValueKind with
                | JsonValueKind.Object ->
                    builder.Append('{') |> ignore
                    let properties = element.EnumerateObject() |> Seq.sortBy (fun item -> item.Name) |> Seq.toArray
                    properties
                    |> Array.iteri (fun index item ->
                        if index > 0 then builder.Append(',') |> ignore
                        builder.Append(JsonSerializer.Serialize(item.Name)).Append(':') |> ignore
                        write (Some item.Name) item.Value)
                    builder.Append('}') |> ignore
                | JsonValueKind.Array ->
                    builder.Append('[') |> ignore
                    let items = element.EnumerateArray() |> Seq.toArray
                    items
                    |> Array.iteri (fun index item ->
                        if index > 0 then builder.Append(',') |> ignore
                        write None item)
                    builder.Append(']') |> ignore
                | JsonValueKind.String ->
                    let name = defaultArg propertyName ""
                    let value = element.GetString() |> Option.ofObj |> Option.defaultValue ""
                    let normalized =
                        if volatileProperties.Contains(name) then "<TIME>"
                        elif commitProperties.Contains(name) && Regex.IsMatch(value, "^[0-9a-f]{40}$") then "<COMMIT>"
                        else normalizeText fixtureRoot value
                    builder.Append(JsonSerializer.Serialize(normalized)) |> ignore
                | JsonValueKind.Number -> builder.Append(element.GetRawText()) |> ignore
                | JsonValueKind.True -> builder.Append("true") |> ignore
                | JsonValueKind.False -> builder.Append("false") |> ignore
                | JsonValueKind.Null
                | JsonValueKind.Undefined -> builder.Append("null") |> ignore
                | _ -> builder.Append(JsonSerializer.Serialize(element.GetRawText())) |> ignore
            write None document.RootElement
            builder.ToString()
        with _ ->
            normalizeText fixtureRoot json

    let private responseProjection fixtureRoot (response: AgentCommandResponse) =
        let projection =
            {| Result = response.Result |> Option.map (canonicalJson fixtureRoot)
               Error = response.Error |> Option.map (fun value -> normalizeText fixtureRoot (sprintf "%A" value))
               Instructions = response.Instructions |> List.map (fun value -> normalizeText fixtureRoot (sprintf "%A" value))
               StateChangeEvidence = normalizeText fixtureRoot (sprintf "%A" response.StateChangeEvidence)
               InboxActivation = response.InboxActivation |> Option.map (fun value -> normalizeText fixtureRoot (sprintf "%A" value))
               PendingJob = response.PendingJob |> Option.map (fun value -> normalizeText fixtureRoot (sprintf "%A" value)) |}
        JsonSerializer.Serialize(projection)

    let private resultClass (response: AgentCommandResponse) =
        match response.Error, response.Result with
        | Some error, _ -> sprintf "%A" error
        | None, Some _ -> "Success"
        | None, None -> "NoResult"

    let private parseTrace path =
        let commands =
            [ "fetch"; "push"; "worktree-list"; "status"; "rev-parse"; "ls-tree"
              "log"; "cat-file"; "show"; "for-each-ref"; "update-ref"; "merge-base" ]
        let counts = Dictionary<string, int>(StringComparer.Ordinal)
        for command in commands do counts[command] <- 0
        let mutable processStarts = 0
        let mutable fetches = 0
        if File.Exists path then
            for line in File.ReadLines path do
                try
                    use document = JsonDocument.Parse(line)
                    let root = document.RootElement
                    let mutable eventElement = Unchecked.defaultof<JsonElement>
                    let mutable argvElement = Unchecked.defaultof<JsonElement>
                    if root.TryGetProperty("event", &eventElement)
                       && eventElement.GetString() = "start"
                       && root.TryGetProperty("argv", &argvElement) then
                        let argv = argvElement.EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toArray
                        if argv.Length > 0 then
                            let executable = Path.GetFileName(argv[0])
                            if executable.StartsWith("git", StringComparison.OrdinalIgnoreCase) then
                                processStarts <- processStarts + 1
                                let contains value = argv |> Array.exists (fun item -> String.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                                if contains "fetch" then fetches <- fetches + 1
                                if contains "push" then counts["push"] <- counts["push"] + 1
                                if contains "status" then counts["status"] <- counts["status"] + 1
                                if contains "rev-parse" then counts["rev-parse"] <- counts["rev-parse"] + 1
                                if contains "ls-tree" then counts["ls-tree"] <- counts["ls-tree"] + 1
                                if contains "log" then counts["log"] <- counts["log"] + 1
                                if contains "cat-file" then counts["cat-file"] <- counts["cat-file"] + 1
                                if contains "show" then counts["show"] <- counts["show"] + 1
                                if contains "for-each-ref" then counts["for-each-ref"] <- counts["for-each-ref"] + 1
                                if contains "update-ref" then counts["update-ref"] <- counts["update-ref"] + 1
                                if contains "merge-base" then counts["merge-base"] <- counts["merge-base"] + 1
                                if contains "worktree" && contains "list" then
                                    counts["worktree-list"] <- counts["worktree-list"] + 1
                with _ -> ()
        { GitProcessStarts = processStarts
          RemoteFetches = fetches
          CommandCounts = counts
          TracePath = path }

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    type private Fixture =
        { Root: string
          Workspace: string
          Project: string
          BareRemote: string
          Runtime: Runtime
          Binding: LedgerBinding
          SeedSha: string
          LinkedWorktrees: (string * string) list }

    let private createFixture root =
        let workspace = Path.Combine(root, "workspace")
        let project = Path.Combine(workspace, "FixtureProject")
        let bare = Path.Combine(root, "remote.git")
        Directory.CreateDirectory(project) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "traces")) |> ignore
        git root [ "init"; "--bare"; bare ] |> ignore
        git project [ "init"; "-b"; "main" ] |> ignore
        git project [ "config"; "user.name"; "Jarvis Perf Fixture" ] |> ignore
        git project [ "config"; "user.email"; "jarvis-perf@example.invalid" ] |> ignore
        File.WriteAllText(Path.Combine(project, "README.md"), "Disposable Jarvis performance fixture.\n")
        git project [ "add"; "README.md" ] |> ignore
        git project [ "commit"; "-m"; "fixture source" ] |> ignore
        git project [ "remote"; "add"; "origin"; bare ] |> ignore
        git project [ "push"; "-u"; "origin"; "main" ] |> ignore
        git root [ "--git-dir"; bare; "symbolic-ref"; "HEAD"; "refs/heads/main" ] |> ignore
        let seedSha = git project [ "rev-parse"; "HEAD" ]

        let binding : LedgerBinding =
            { RepositoryPath = project
              RemoteName = "origin"
              ApprovedFetchUrl = bare
              ApprovedPushUrl = bare
              ProjectId = fixedProjectId
              LedgerId = fixedLedgerId
              Epoch = 1 }
        let initialSnapshot = LedgerStore.initialize binding true |> unwrap "initialize disposable ledger"
        let runtime = Runtime(workspace, ConsoleTui.ConsoleTui(), FullTrust)
        LedgerBindingConfig.bindExisting
            "FixtureProject" runtime fixedProjectId fixedLedgerId 1 "origin" bare bare initialSnapshot.Commit true
        |> unwrap "bind disposable project ledger"
        |> ignore
        ReviewWorkflow.registerAgent binding (Guid.Parse("30000000-0000-4000-8000-000000000002")) "Operator"
        |> unwrap "seed Operator agent"
        |> ignore
        ReviewWorkflow.createTask binding (Guid.Parse("30000000-0000-4000-8000-000000000003")) "perf-task-0001" "Performance fixture task"
        |> unwrap "seed task"
        |> ignore

        let linked =
            [ 1 .. 3 ]
            |> List.map (fun index ->
                let name = sprintf "fixture-linked-%02d" index
                let path = Path.Combine(root, "worktrees", name)
                Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
                git project [ "worktree"; "add"; "-b"; name; path; "HEAD" ] |> ignore
                name, path)
        { Root = root
          Workspace = workspace
          Project = project
          BareRemote = bare
          Runtime = runtime
          Binding = binding
          SeedSha = seedSha
          LinkedWorktrees = linked }

    let private remoteState bare =
        let targets =
            git bare [ "for-each-ref"; "--format=%(refname)%09%(objectname)"; "refs/heads" ]
            |> fun output -> output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.sort
        let refs = targets |> List.map (fun target -> target.Split('\t')[0])
        let builder = StringBuilder()
        for reference in refs do
            builder.Append(reference).Append('\n') |> ignore
            let entries = git bare [ "ls-tree"; "-r"; "--full-tree"; reference ]
            for line in entries.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
                let parts = line.Split([| '\t' |], 2)
                if parts.Length = 2 then
                    let metadata = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    if metadata.Length >= 3 then
                        let blob = git bare [ "cat-file"; "blob"; metadata[2] ]
                        builder.Append(parts[1]).Append('\u0000').Append(normalizeText "" blob).Append('\n') |> ignore
        hash (builder.ToString()), refs, targets

    let private createToolHarness runtime delayMs =
        let userId = "perf-user"
        let connectionId = "perf-connection"
        let deviceId = "perf-device"
        let users = UserService()
        users.TransportConnected(userId, connectionId)
        let registration : ClientRegistration =
            { DeviceId = deviceId
              DeviceName = "Perf fixture client"
              ProtocolVersion = AgentProtocol.version
              ClientVersion = "perf-harness"
              SupportedCommands = AgentProtocol.commandDefinitions |> List.map (fun definition -> definition.Name) }
        users.Register(userId, registration, connectionId) |> ignore
        let selected =
            match users.GetSelectedSession(userId, None, Some deviceId) with
            | ValueSome value -> value
            | ValueNone -> failwith "Disposable test device was not registered."
        let tracker = ClientResponseTracker()
        let responses = ConcurrentQueue<CapturedResponse>()
        let timings = ConcurrentDictionary<string, CommandDispatchTiming>()
        let queue = CommandDispatchQueue(1, 64, fun timing -> timings[timing.CorrelationId] <- timing)

        let complete correlationId response =
            responses.Enqueue({ CorrelationId = correlationId; Response = response })
            tracker.Complete(correlationId, userId, connectionId, selected.Generation, response) |> ignore

        let clientProxy =
            { new IClientService with
                member _.ReceiveMessage(_message: string) = Task.CompletedTask
                member _.ReceiveCommand(correlationId: string, commandJson: string, agentId: string) =
                    let execute () =
                        task {
                            if delayMs > 0 then do! Task.Delay(delayMs)
                            let! _, _, response = global.Client.SignalR.Client.processIncomingCommand runtime commandJson agentId
                            complete correlationId response
                        } :> Task
                    let onUnhandled _ =
                        complete correlationId (CommandDispatchRecovery.unexpectedResponse commandJson)
                        Task.CompletedTask
                    if not (queue.TrySubmit(correlationId, execute, onUnhandled)) then
                        complete correlationId (CommandDispatchRecovery.unexpectedResponse commandJson)
                    Task.CompletedTask
                member _.ReceiveSourceCommand(_correlationId: string, _commandJson: string, _agentId: string, _originalOperationId: string) = Task.CompletedTask
                member _.ReceiveManualGrantedCommand(_correlationId: string, _commandJson: string, _agentId: string) = Task.CompletedTask
                member _.ReceiveManualGrantApproval(_correlationId: string, _requestJson: string, _agentId: string) = Task.CompletedTask
                member _.InvalidateAgentStates(_projectName: string, _agentIds: string array) = Task.CompletedTask
                member _.InvalidateAllAgentStates() = Task.CompletedTask
                member _.RecordAgentActivity(_projectName: string, _agentId: string, _observedAtUtc: DateTimeOffset) = Task.CompletedTask }

        let hubClients =
            { new IHubClients<IClientService> with
                member _.All = clientProxy
                member _.AllExcept(_excludedConnectionIds) = clientProxy
                member _.Client(_connectionId) = clientProxy
                member _.Clients(_connectionIds) = clientProxy
                member _.Group(_groupName) = clientProxy
                member _.GroupExcept(_groupName, _excludedConnectionIds) = clientProxy
                member _.Groups(_groupNames) = clientProxy
                member _.User(_userId) = clientProxy
                member _.Users(_userIds) = clientProxy }
        let hub =
            { new IHubContext<HubService, IClientService> with
                member _.Clients = hubClients
                member _.Groups = Unchecked.defaultof<IGroupManager> }
        let http = HttpContextAccessor()
        let options: JarvisOptions =
            { Auth0Domain = "fixture.auth.invalid"
              Audience = "https://jarvis.fixture.invalid"
              OpenAIAppsChallenge = "fixture-challenge"
              EnableOperatorConversations = false }
        let serviceCollection = ServiceCollection()
        serviceCollection.AddSingleton<IOptions<JarvisOptions>>(Options.Create<JarvisOptions>(options)) |> ignore
        let services = serviceCollection.BuildServiceProvider()
        let client =
            new ClientService(hub, users, tracker, http, NullLogger<ClientService>.Instance, new OptionalFeedbackStore(""))
        { Client = client
          Http = http
          Services = services
          Responses = responses
          Timings = timings
          Queue = queue
          UserId = userId
          ConnectionId = connectionId
          DeviceId = deviceId
          Generation = selected.Generation }

    let private invokeTool (harness: ToolHarness) (toolName: string) (actor: string) (scopes: string list) action =
        task {
            let context = DefaultHttpContext()
            let identity =
                ClaimsIdentity(
                    [ Claim("sub", harness.UserId)
                      Claim("scope", String.Join(" ", scopes)) ],
                    "perf-harness")
            let principal = ClaimsPrincipal(identity)
            for scope in scopes do
                if not (Auth.hasScope scope principal) then
                    failwithf "Harness failed to attach required scope claim %s." scope
            context.User <- principal
            context.RequestServices <- harness.Services
            harness.Http.HttpContext <- context
            let arguments = Dictionary<string, JsonElement>()
            arguments["agentId"] <- JsonSerializer.SerializeToElement(actor)
            arguments["deviceId"] <- JsonSerializer.SerializeToElement(harness.DeviceId)
            let request = CallToolRequestParams(Name = toolName, Arguments = arguments)
            let! toolResult = RequestDeviceRouting.withMetadata request action
            let mutable captured = Unchecked.defaultof<CapturedResponse>
            let response = if harness.Responses.TryDequeue(&captured) then Some captured else None
            let timing =
                response
                |> Option.bind (fun value ->
                    match harness.Timings.TryGetValue(value.CorrelationId) with
                    | true, timing -> Some timing
                    | _ -> None)
            return toolResult, response, timing
        }


    let private bytes (value: string) = Encoding.UTF8.GetByteCount(value)
    let private guid n = Guid.Parse(sprintf "70000000-0000-4000-8000-%012d" n)

    let private seedFrontier (fixture: Fixture) representative =
        let b = fixture.Binding
        let must label value = unwrap label value |> ignore
        for i, agent in [ 1, "Interpreter"; 2, "WorkerReady"; 3, "WorkerBusy"; 4, "WorkerIdle"; 5, "Reviewer" ] do
            ReviewWorkflow.registerAgent b (guid i) agent |> must "register synthetic agent"
        if representative then
            for i in 1 .. 8 do
                let agent = sprintf "FrontierObserver%02d" i
                ReviewWorkflow.registerAgent b (guid (100+i)) agent |> must "register additional synthetic agent"
        let roleBody = String.replicate 130 "Preserve exact source, <scope>, and evidence. Verify lifecycle. Δ.\n"
        RoleInstructions.RoleInstructions.createRole b "Operator" (guid 10) "fixture-executor" "Synthetic executor" roleBody
        |> must "create role"
        for i, agent in [ 2, "WorkerReady"; 3, "WorkerBusy"; 5, "Reviewer" ] do
            RoleInstructions.RoleInstructions.attachRole b "Operator" (guid (10+i)) agent "fixture-executor" 0
            |> must "attach role"
            RoleInstructions.RoleInstructions.setAgentInstructions b "Operator" (guid (20+i)) agent 1
                (String.replicate 14 (sprintf "%s only: keep pinned specification and exact version.\n" agent))
            |> must "set agent instructions"
        File.WriteAllText(Path.Combine(fixture.Workspace, "AGENTS.md"),
            String.replicate 55 "Workspace synthetic instruction: preserve <source> and exact scope.\n")
        File.WriteAllText(Path.Combine(fixture.Project, "AGENTS.md"),
            String.replicate 35 "Project synthetic instruction: verify the current task; use canonical state.\n")
        let spec = "<article>" + String.replicate 100 "<p>Freeze scope, source provenance and exact outcome. Δ &amp; evidence.</p>" + "</article>"
        for i in 1 .. 12 do
            let id = sprintf "frontier-%02d" i
            ReviewWorkflow.createTaskWithSpec b (guid (30+i)) id ("Active fixture " + id) spec |> must "create task"
        for i, agent in [ 1, "WorkerReady"; 2, "WorkerBusy" ] do
            let id = sprintf "frontier-%02d" i
            ReviewWorkflow.assignTask b (guid (50+i)) id (guid (60+i)) agent |> must "assign task"
            ReviewWorkflow.resumeAgent b (guid (70+i)) id agent 0 0 0 |> must "start task"
        ReviewWorkflow.checkpointAttempt b (guid 81) "frontier-02" "WorkerBusy" 0 0 0
            "Completed fixture inspection" "Continue fixture test" "" "Synthetic evidence" |> must "checkpoint"
        ReviewWorkflow.yieldAgent b (guid 82) "frontier-01" "WorkerReady" 0 0 0
            "Initial checkpoint" "Resume fixture" |> must "yield to Ready"
        let reviewId = guid 91
        ReviewWorkflow.submitBranchReview b (guid 92) "frontier-03" "WorkerBusy" reviewId
            "refs/heads/main" fixture.SeedSha "Synthetic review demand" "Implementation" "refs/heads/main"
        |> must "publish disposable review"
        if representative then
            let acceptedReview = guid 93
            ReviewWorkflow.submitBranchReview b (guid 94) "frontier-04" "WorkerBusy" acceptedReview
                "refs/heads/main" fixture.SeedSha "Synthetic accepted integration demand" "Implementation" "refs/heads/main"
            |> must "publish disposable integration review"
            ReviewWorkflow.claimBranchReview b (guid 95) "frontier-04" acceptedReview "Reviewer" 1
            |> must "claim disposable integration review"
            ReviewWorkflow.decideBranchReview b (guid 96) "frontier-04" acceptedReview "Reviewer" 2 Accept
            |> must "accept disposable integration review"
            ReviewWorkflow.submitIntegration b (guid 97) "frontier-04" "WorkerBusy" (guid 98)
                "refs/heads/main" fixture.SeedSha (acceptedReview.ToString("N")) "refs/heads/main"
            |> must "submit disposable pending integration"
        reviewId.ToString("N")

    // Bulk history fixture, patterned on ReviewWorkflowTests.AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness.
    // Canonical record shapes; not a replay of all historical mutations. No real identities/remote.
    let private seedHistory (fixture: Fixture) count extraReviews =
        if count > 0 then
            let snap = LedgerStore.fetch fixture.Binding |> unwrap "fetch disposable ledger"
            let auxiliary = Path.Combine(fixture.Root, "ledger-bulk")
            git fixture.Project [ "worktree"; "add"; "--detach"; auxiliary; snap.Commit ] |> ignore
            let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
            let write (path: string) schema value =
                let full = Path.Combine(auxiliary, path)
                Directory.CreateDirectory(Path.GetDirectoryName(full)) |> ignore
                let original = JsonSerializer.Serialize(value, options)
                File.WriteAllText(full, sprintf "{\"schemaVersion\":%d,%s" schema (original.Substring(1)))
            for i in 0 .. count - 1 do
                let id = sprintf "history%05d" i
                let attempt = (guid (100000+i)).ToString("N")
                let review = (guid (200000+i)).ToString("N")
                let submission = (guid (300000+i)).ToString("N")
                let seqNo = int64 i + 10L
                write $"tasks/{id}/task.json" 2
                    ({ TaskId = id; Title = id + " " + String.replicate 9 "Historical task "; Version = 3
                       Lifecycle = "Closed"; SpecVersion = 1; AttemptId = attempt } : ReviewTask)
                write $"tasks/{id}/attempts/{attempt}.json" 3
                    ({ TaskId = id; AttemptId = attempt; Version = 3; OwnerAgentId = "WorkerIdle"
                       Phase = "Finished"; AssignmentGeneration = 1; Priority = i % 5
                       IssuedSequence = seqNo; SpecBlobOid = null; BaseCommit = null; TargetRef = null
                       SourceRepositoryId = null; SourceRemote = null; SourcePushRemote = null; SourceRef = null } : ReviewAttempt)
                write $"tasks/{id}/reviews/{review}/review.json" 3
                    ({ ReviewId = review; TaskId = id; AttemptId = attempt; OwnerAgentId = "WorkerIdle"
                       Kind = "Implementation"; TaskRef = null; ReviewRef = null; SourceVersion = 0
                       ReviewedHeadOid = null; BaseCommit = null; TargetRef = null; Status = "Accepted"
                       ReviewerId = "Reviewer"; Version = 2; CreatedSequence = seqNo
                       DecisionSequence = seqNo; Acknowledged = true } : BranchReview)
                if i < extraReviews then
                    let second = (guid (400000+i)).ToString("N")
                    write $"tasks/{id}/reviews/{second}/review.json" 3
                        ({ ReviewId = second; TaskId = id; AttemptId = attempt; OwnerAgentId = "WorkerIdle"
                           Kind = "Implementation"; TaskRef = null; ReviewRef = null; SourceVersion = 0
                           ReviewedHeadOid = null; BaseCommit = null; TargetRef = null; Status = "ChangesRequested"
                           ReviewerId = "Reviewer"; Version = 2; CreatedSequence = seqNo
                           DecisionSequence = seqNo; Acknowledged = true } : BranchReview)
                if i % 7 = 0 then
                    write $"tasks/{id}/integrations/{submission}.json" 2
                        ({ SubmissionId = submission; TaskId = id; ActorId = "WorkerIdle"
                           SourceRef = null; SubmittedCommit = null; ReviewId = review; ReviewRef = null
                           ReviewVersion = 0; ReviewedHeadOid = null; IntegrationRef = null
                           TargetHeadAtSubmission = null; TargetRef = null; Status = "Integrated"
                           CreatedSequence = seqNo; IntegratedCommit = null; CompletedAtUtc = fixedDate
                           PublicationPreviousHead = null; PublicationObservedHead = null
                           PublicationOperationId = null; PublicationMechanism = null; SharedBinding = None } : IntegrationSubmission)
                File.WriteAllText(Path.Combine(auxiliary, "tasks", id, "spec.html"),
                    "<article><p>" + String.replicate 1024 "X" + "</p></article>")
            git auxiliary [ "add"; "tasks" ] |> ignore
            git auxiliary [ "commit"; "-m"; "Synthetic bulk history" ] |> ignore
            let head = git auxiliary [ "rev-parse"; "HEAD" ]
            git fixture.Project [ "push"; "origin"; head + ":refs/heads/jarvis-orchestration" ] |> ignore

    let private properties (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Object then value.EnumerateObject() |> Seq.toList else []
    let private prop name value =
        properties value |> List.tryFind (fun p -> String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun p -> p.Value)

    // Count actual instruction/spec strings, not whole documents or arbitrary matching text.
    let private contextSegments (payload: string) =
        let found = ResizeArray<_>()
        let add path (el: JsonElement) =
            if el.ValueKind = JsonValueKind.String then
                let content = el.GetString()
                found.Add({| Path = path; Sha256 = hash content; ContentBytes = bytes content
                             EscapedJsonBytes = bytes (el.GetRawText()) - 2 |})
        let rec walk path (el: JsonElement) =
            match el.ValueKind with
            | JsonValueKind.Object ->
                for p in properties el do
                    let name = p.Name.ToLowerInvariant()
                    let child = path + "/" + name
                    if name = "spechtml" || (name = "content" && (path.Contains("/documents/") || path = "/specpage"))
                       || path.EndsWith("/agents.md") then add child p.Value
                    else walk child p.Value
            | JsonValueKind.Array ->
                el.EnumerateArray() |> Seq.iteri (fun i item -> walk (path + "/" + string i) item)
            | _ -> ()
        use doc = JsonDocument.Parse(payload)
        let rootPath = if prop "SpecBlobOid" doc.RootElement |> Option.isSome &&
                          prop "PageBytes" doc.RootElement |> Option.isSome then "/specpage" else ""
        walk rootPath doc.RootElement
        List.ofSeq found

    let private summarizedShape (payload: string) =
        use doc = JsonDocument.Parse(payload)
        properties doc.RootElement
        |> List.map (fun p ->
            let count =
                match p.Value.ValueKind with
                | JsonValueKind.Array -> Some (p.Value.GetArrayLength())
                | JsonValueKind.Number when p.Name.EndsWith("Omitted", StringComparison.OrdinalIgnoreCase) -> Some (p.Value.GetInt32())
                | _ -> None
            p.Name, count)
        |> List.choose (fun (n,c) -> c |> Option.map (fun v -> n,v))
        |> Map.ofList

    let private readMcpTextBytes (toolJson: string) =
        use doc = JsonDocument.Parse(toolJson)
        match prop "content" doc.RootElement with
        | Some content when content.ValueKind = JsonValueKind.Array ->
            content.EnumerateArray() |> Seq.sumBy (fun el ->
                match prop "text" el with
                | Some t when t.ValueKind = JsonValueKind.String -> bytes (t.GetString())
                | _ -> 0)
        | _ -> 0

    let private argValue flag defaultValue (argv: string array) =
        argv |> Array.tryFindIndex ((=) flag)
        |> Option.bind (fun i -> if i+1 < argv.Length then Some argv[i+1] else None)
        |> Option.defaultValue defaultValue

    let private run argv =
        task {
            let sourceRoot = Path.GetFullPath(argValue "--source-root" (Directory.GetCurrentDirectory()) argv)
            let output = Path.GetFullPath(argValue "--out" "/tmp/jarvis-context-efficiency.json" argv)
            let history = Int32.Parse(argValue "--history" "220" argv)
            let delayMs = Int32.Parse(argValue "--transport-delay-ms" "0" argv)
            let ceilingOnly = Array.contains "--ceiling-only" argv
            let representative = Array.contains "--representative" argv
            let extraReviews = if representative then 203 else 0
            if representative && (history <> 526 || ceilingOnly) then
                failwith "Representative fixture requires 526 history tasks and full workflow."
            if history < 0 || history > 5000 || delayMs < 0 || delayMs > 500 then failwith "Invalid controlled fixture parameters."
            let sourceSha = git sourceRoot [ "rev-parse"; "HEAD" ]
            if not (String.IsNullOrWhiteSpace(git sourceRoot [ "status"; "--porcelain=v1"; "--"; "Client"; "Server"; "Common" ])) then
                failwith "Measured source paths are dirty."
            let fixtureRoot = Path.Combine(Path.GetTempPath(), "jarvis-context-efficiency-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(fixtureRoot) |> ignore
            let fixture = createFixture fixtureRoot
            let reviewId = seedFrontier fixture representative
            seedHistory fixture history extraReviews
            let harness = createToolHarness fixture.Runtime delayMs
            let project = "FixtureProject"
            let scope = [ "workspace:read" ]
            let rows = ResizeArray<obj>()
            let cycles = ResizeArray<obj>()
            let head () = git fixture.BareRemote [ "rev-parse"; "refs/heads/jarvis-orchestration" ]
            let oneCall scenario actor tool (polledJobId: string option) action =
                task {
                    let sw = Stopwatch.StartNew()
                    let! result, captured, timing = invokeTool harness tool actor scope action
                    sw.Stop()
                    let response = captured |> Option.map (fun c -> c.Response) |> Option.defaultWith (fun () -> failwithf "No routed response: %s" scenario)
                    let toolJson = JsonSerializer.Serialize(result : CallToolResult)
                    let raw = response.Result |> Option.defaultValue ""
                    let terminal =
                        if tool = "GetJobResult" && raw <> "" then
                            let job = JsonSerializer.Deserialize<JobResult>(raw)
                            job.ToolResponse
                        else if response.PendingJob.IsNone then Some response else None
                    let terminalPayload = terminal |> Option.bind (fun r -> r.Result) |> Option.defaultValue ""
                    let segments = if terminalPayload = "" then [] else contextSegments terminalPayload
                    let counts = if terminalPayload = "" then Map.empty else summarizedShape terminalPayload
                    rows.Add(box {| Scenario = scenario; Tool = tool; Actor = actor
                                    DurationMs = sw.Elapsed.TotalMilliseconds
                                    QueueMs = timing |> Option.map (fun t -> t.QueueMilliseconds)
                                    ExecutionMs = timing |> Option.map (fun t -> t.ExecutionMilliseconds)
                                    RawResultBytes = bytes raw
                                    SerializedResponseBytes = JsonSerializer.SerializeToUtf8Bytes(response).Length
                                    DeliveredTextBytes = readMcpTextBytes toolJson
                                    DeliveredMcpJsonBytes = bytes toolJson
                                    TerminalPayloadBytes = bytes terminalPayload
                                    TerminalPayloadSha256 = if terminalPayload = "" then "" else hash terminalPayload
                                    TerminalClass = terminal |> Option.map resultClass
                                    PolledJobId = polledJobId
                                    PendingJobId = response.PendingJob |> Option.map (fun h -> h.JobId)
                                    ContextSegments = segments; TopLevelCountsAndOmissions = counts
                                    TerminalProjection = terminal |> Option.map (responseProjection fixtureRoot)
                                    ToolJson = toolJson |})
                    return response, terminal
                }
            let route scenario actor tool action =
                task {
                    let! initial, terminal = oneCall scenario actor tool None action
                    match initial.PendingJob with
                    | None -> return terminal.Value
                    | Some handle ->
                        let mutable finished = None
                        let mutable poll = 0
                        let deadline = Stopwatch.StartNew()
                        while finished.IsNone && deadline.Elapsed < TimeSpan.FromMinutes(3.) do
                            do! Task.Delay(50)
                            poll <- poll + 1
                            let! result, final =
                                oneCall scenario actor "GetJobResult" (Some handle.JobId)
                                    (fun () -> JarvisMcpTools.GetJobResult(project, actor, handle.JobId, Nullable 0L,
                                                                     harness.DeviceId, harness.Client, harness.Http))
                            if result.Error.IsSome then failwithf "Poll error %A" result.Error
                            finished <- final
                        return finished |> Option.defaultWith (fun () -> failwithf "Unresolved fixture handle %s after %d polls" handle.JobId poll)
                }
            let success (response: AgentCommandResponse) =
                if response.Error.IsSome || response.Result.IsNone then failwithf "Expected successful route, got %A" response.Error
                response.Result.Value
            let measureCycle name work =
                task {
                    let before = head ()
                    let first = rows.Count
                    let sw = Stopwatch.StartNew()
                    do! work ()
                    sw.Stop()
                    let after = head ()
                    cycles.Add(box {| Name = name; FirstCallIndex = first; CallCount = rows.Count-first
                                      DurationMs = sw.Elapsed.TotalMilliseconds; BeforeHead = before; AfterHead = after |})
                    return before, after
                }
            if not ceilingOnly then
                let! before,after = measureCycle "interpreter" (fun () -> task {
                    let targets =
                        [ "WorkerReady"; "WorkerBusy"; "WorkerIdle"; "Reviewer" ]
                        @ (if representative then [ for i in 1 .. 8 -> sprintf "FrontierObserver%02d" i ] else [])
                    for target in targets do
                        let! r =
                            route ("observe-" + target) "Interpreter" "ObserveAgent"
                                (fun () -> JarvisMcpTools.ObserveAgent(project,"Interpreter",target,harness.DeviceId,harness.Client,harness.Http))
                        success r |> ignore
                    for taskId in [ "frontier-01"; "frontier-02"; "frontier-03" ] do
                        let! r =
                            route ("task-" + taskId) "Interpreter" "GetTask"
                                (fun () -> JarvisMcpTools.GetTask(project,"Interpreter",taskId,harness.DeviceId,harness.Client,harness.Http))
                        success r |> ignore
                    let! r =
                        route "review-detail" "Interpreter" "ReviewDetails"
                            (fun () -> JarvisMcpTools.ReviewDetails(project,"Interpreter","frontier-03",reviewId,harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                    let! r =
                        route "integrations-history" "Interpreter" "GetIntegrationSubmissions"
                            (fun () -> JarvisMcpTools.GetIntegrationSubmissions(project,"Interpreter","history00000",harness.DeviceId,harness.Client,harness.Http))
                    if history > 0 then success r |> ignore
                    let! r =
                        route "integration-frontier" "Interpreter" "GetIntegrationSubmissions"
                            (fun () -> JarvisMcpTools.GetIntegrationSubmissions(project,"Interpreter",(if representative then "frontier-04" else "frontier-03"),harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                })
                if before <> after then failwith "Read-only Interpreter cycle changed canonical ledger head."
                let! before,after = measureCycle "worker-bootstrap" (fun () -> task {
                    let actor = "WorkerReady"
                    let! r =
                        route "worker-inbox" actor "AgentInbox"
                            (fun () -> JarvisMcpTools.AgentInbox(project,actor,"",harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                    if r.InboxActivation.IsNone || not r.InboxActivation.Value.ActivatedByThisCall then
                        failwith "Disposable worker did not activate via inbox."
                    let! r =
                        route "worker-instructions" actor "GetAgentInstructions"
                            (fun () -> JarvisMcpTools.GetAgentInstructions(project,actor,"",".","",harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                    let! r =
                        route "worker-task" actor "GetTask"
                            (fun () -> JarvisMcpTools.GetTask(project,actor,"frontier-01",harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                    let! r =
                        route "worker-spec-page" actor "GetTaskSpecPage"
                            (fun () -> JarvisMcpTools.GetTaskSpecPage(project,actor,"frontier-01",16384,"",harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                })
                if before = after then failwith "Worker activation did not persist."
                let! _,_ = measureCycle "reviewer-bootstrap" (fun () -> task {
                    let actor = "Reviewer"
                    let! r =
                        route "reviewer-inbox" actor "ReviewerInbox"
                            (fun () -> JarvisMcpTools.ReviewerInbox(project,actor,harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                    let! r =
                        route "reviewer-instructions" actor "GetAgentInstructions"
                            (fun () -> JarvisMcpTools.GetAgentInstructions(project,actor,"",".","",harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                    let! r =
                        route "reviewer-task" actor "GetTask"
                            (fun () -> JarvisMcpTools.GetTask(project,actor,"frontier-03",harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                    let! r =
                        route "reviewer-details" actor "ReviewDetails"
                            (fun () -> JarvisMcpTools.ReviewDetails(project,actor,"frontier-03",reviewId,harness.DeviceId,harness.Client,harness.Http))
                    success r |> ignore
                })
                ()
            let! before,after = measureCycle "overview" (fun () -> task {
                let! r =
                    route "task-overview" "Interpreter" "TaskOverview"
                        (fun () -> JarvisMcpTools.TaskOverview(project,"Interpreter",harness.DeviceId,harness.Client,harness.Http))
                match r.Error with
                | None -> success r |> ignore
                | Some(OutputTruncated _) -> ()
                | other -> failwithf "Unexpected TaskOverview error %A" other
            })
            if before <> after then failwith "Overview changed canonical ledger head."
            do! harness.Queue.Complete()
            harness.Services.Dispose()
            let report = {| SchemaVersion = 1; SourceSha = sourceSha; SourceRoot = sourceRoot
                            FixtureRoot = fixtureRoot; HistoryTasks = history; FrontierTasks = 12
                            RegisteredAgents = if representative then 14 else 6
                            HistoricalReviews = history + extraReviews
                            FrontierReviews = if representative then 2 else 1
                            FrontierPendingIntegrations = if representative then 1 else 0
                            HistoricalIntegrations = if history = 0 then 0 else (history-1)/7+1
                            Representative = representative
                            TransportDelayMs = delayMs; CeilingOnly = ceilingOnly
                            Cycles = List.ofSeq cycles; Calls = List.ofSeq rows |}
            Directory.CreateDirectory(Path.GetDirectoryName(output)) |> ignore
            File.WriteAllText(output,JsonSerializer.Serialize(report,JsonSerializerOptions(WriteIndented=true)))
            printfn "Source=%s fixture=%s output=%s calls=%d" sourceSha fixtureRoot output rows.Count
            return 0
        }

    [<EntryPoint>]
    let main argv =
        try run argv |> fun t -> t.GetAwaiter().GetResult()
        with ex -> eprintfn "%O" ex; 1
