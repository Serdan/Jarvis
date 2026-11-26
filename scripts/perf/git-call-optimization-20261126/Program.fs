namespace GitCallOptimizationHarness

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

type Measurement =
    { Scenario: string
      Tool: string
      Actor: string
      DurationMilliseconds: float
      QueueMilliseconds: float option
      ExecutionMilliseconds: float option
      GitProcessStarts: int
      RemoteFetches: int
      CommandCounts: Dictionary<string, int>
      ResponseClass: string option
      ResponseDigest: string option
      ResponseProjection: string option
      ToolResultDigest: string option
      ToolIsError: bool option
      Exception: string option
      StateDigestBefore: string option
      StateDigestAfter: string option
      RefsBefore: string list
      RefsAfter: string list
      TracePath: string }

type Report =
    { SchemaVersion: int
      SourceRoot: string
      SourceBranch: string
      SourceSha: string
      ParentSha: string
      RemoteMainSha: string
      MeasuredSourceClean: bool
      FixtureRoot: string
      FixtureSeedSha: string
      FixturePublishedSha: string
      FixtureRemote: string
      Measurements: Measurement list }

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
        let refs =
            git bare [ "for-each-ref"; "--format=%(refname)"; "refs/heads" ]
            |> fun output -> output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.sort
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
        hash (builder.ToString()), refs

    let private createToolHarness runtime =
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

    let private toolIsError (toolResult: obj) =
        let property = toolResult.GetType().GetProperty("IsError")
        if isNull property then None
        else
            let value = property.GetValue(toolResult)
            if isNull value then None
            else
                match value with
                | :? Nullable<bool> as nullable when nullable.HasValue -> Some nullable.Value
                | :? bool as flag -> Some flag
                | _ -> None

    let private measureTool fixtureRoot harness scenario toolName actor scopes action =
        task {
            let tracePath = Path.Combine(fixtureRoot, "traces", scenario + ".jsonl")
            if File.Exists tracePath then File.Delete tracePath
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", tracePath)
            let stopwatch = Stopwatch.StartNew()
            let mutable toolResult = None
            let mutable captured = None
            let mutable timing = None
            let mutable exceptionText = None
            try
                try
                    let! result, response, dispatchTiming = invokeTool harness toolName actor scopes action
                    toolResult <- Some result
                    captured <- response
                    timing <- dispatchTiming
                with ex ->
                    exceptionText <- Some(ex.ToString())
            finally
                stopwatch.Stop()
                Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null)

            let trace = parseTrace tracePath
            let projection =
                captured
                |> Option.map (fun value -> responseProjection fixtureRoot value.Response)
            let toolJson =
                toolResult
                |> Option.map (fun value ->
                    try JsonSerializer.Serialize(value)
                    with _ -> sprintf "%A" value)
            let responseDigest = projection |> Option.map hash
            let toolDigest = toolJson |> Option.map (canonicalJson fixtureRoot >> hash)
            let resultClass = captured |> Option.map (fun value -> resultClass value.Response)
            return
                { Scenario = scenario
                  Tool = toolName
                  Actor = actor
                  DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds
                  QueueMilliseconds = timing |> Option.map (fun value -> value.QueueMilliseconds)
                  ExecutionMilliseconds = timing |> Option.map (fun value -> value.ExecutionMilliseconds)
                  GitProcessStarts = trace.GitProcessStarts
                  RemoteFetches = trace.RemoteFetches
                  CommandCounts = trace.CommandCounts
                  ResponseClass = resultClass
                  ResponseDigest = responseDigest
                  ResponseProjection = projection
                  ToolResultDigest = toolDigest
                  ToolIsError = toolResult |> Option.bind toolIsError
                  Exception = exceptionText
                  StateDigestBefore = None
                  StateDigestAfter = None
                  RefsBefore = []
                  RefsAfter = []
                  TracePath = trace.TracePath }
        }

    let private withState before after refsBefore refsAfter measurement =
        { measurement with
            StateDigestBefore = Some before
            StateDigestAfter = Some after
            RefsBefore = refsBefore
            RefsAfter = refsAfter }

    let private argValue flag defaultValue argv =
        argv
        |> Array.tryFindIndex ((=) flag)
        |> Option.bind (fun index -> if index + 1 < argv.Length then Some argv[index + 1] else None)
        |> Option.defaultValue defaultValue

    let private run argv =
        task {
            let sourceRoot = Path.GetFullPath(argValue "--source-root" (Directory.GetCurrentDirectory()) argv)
            let outputPath = Path.GetFullPath(argValue "--out" (Path.Combine(sourceRoot, "artifacts", "git-call-optimization.json")) argv)
            let sourceSha = git sourceRoot [ "rev-parse"; "HEAD" ]
            let parentSha =
                try git sourceRoot [ "rev-parse"; "HEAD^" ]
                with _ -> ""
            let sourceBranch = git sourceRoot [ "rev-parse"; "--abbrev-ref"; "HEAD" ]
            let remoteMainSha =
                try git sourceRoot [ "rev-parse"; "refs/remotes/origin/main" ]
                with _ -> ""
            let measuredSourceClean =
                String.IsNullOrWhiteSpace(git sourceRoot [ "status"; "--porcelain=v1"; "--"; "Client"; "Server" ])
            if not measuredSourceClean then
                failwith "Measured Client/Server source paths are dirty; refusing to report a baseline or candidate."

            let fixtureRoot = Path.Combine(Path.GetTempPath(), "jarvis-git-call-optimization-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(fixtureRoot) |> ignore
            let fixture = createFixture fixtureRoot
            let harness = createToolHarness fixture.Runtime
            let measurements = ResizeArray<Measurement>()

            let! listWorktrees =
                measureTool fixtureRoot harness "list-worktrees" "ListWorktrees" "Operator" [ "workspace:read" ]
                    (fun () -> JarvisMcpTools.ListWorktrees("FixtureProject", "Operator", harness.DeviceId, harness.Client, harness.Http))
            measurements.Add(listWorktrees)

            let firstWorktreeName, linkedPath = fixture.LinkedWorktrees.Head
            let! getWorktree =
                measureTool fixtureRoot harness "get-worktree" "GetWorktree" "Operator" [ "workspace:read" ]
                    (fun () -> JarvisMcpTools.GetWorktree("FixtureProject", "Operator", linkedPath, harness.DeviceId, harness.Client, harness.Http))
            measurements.Add(getWorktree)

            let readFile (worktreeName: string) : Task<CallToolResult> =
                JarvisMcpTools.ReadFile(
                    "FixtureProject", "Operator", worktreeName, "README.md",
                    Unchecked.defaultof<Nullable<int>>,
                    Unchecked.defaultof<Nullable<int>>,
                    Unchecked.defaultof<Nullable<bool>>,
                    harness.DeviceId, harness.Client, harness.Http)

            let! coldWorktreeRead =
                measureTool fixtureRoot harness "read-file-worktree-cold" "ReadFile" "Operator" [ "workspace:read" ]
                    (fun () -> readFile firstWorktreeName)
            measurements.Add(coldWorktreeRead)

            for sample in 1 .. 30 do
                let scenario = sprintf "read-file-worktree-warm-%02d" sample
                let! warmRead =
                    measureTool fixtureRoot harness scenario "ReadFile" "Operator" [ "workspace:read" ]
                        (fun () -> readFile firstWorktreeName)
                measurements.Add(warmRead)

            // A second clone publishes a new head. The selected worktree advances,
            // but registered-path caching must not make file contents stale.
            let publisher = Path.Combine(fixture.Root, "publisher")
            git fixture.Root [ "clone"; fixture.BareRemote; publisher ] |> ignore
            git publisher [ "config"; "user.name"; "Fixture Publisher" ] |> ignore
            git publisher [ "config"; "user.email"; "fixture-publisher@example.invalid" ] |> ignore
            File.WriteAllText(Path.Combine(publisher, "README.md"), "Updated by the second fixture clone.\n")
            git publisher [ "add"; "README.md" ] |> ignore
            git publisher [ "commit"; "-m"; "publish updated fixture head" ] |> ignore
            git publisher [ "push"; "origin"; "main" ] |> ignore
            git fixture.Project [ "fetch"; "origin" ] |> ignore
            git fixture.Project [ "merge"; "--ff-only"; "origin/main" ] |> ignore
            git linkedPath [ "merge"; "--ff-only"; "main" ] |> ignore
            let fixturePublishedSha = git linkedPath [ "rev-parse"; "HEAD" ]

            let! afterHeadUpdate =
                measureTool fixtureRoot harness "read-file-after-head-update" "ReadFile" "Operator" [ "workspace:read" ]
                    (fun () -> readFile firstWorktreeName)
            measurements.Add(afterHeadUpdate)

            // Add a new registered worktree after the cache is warm. The first
            // lookup must refresh registration; the next lookup should be warm.
            let newWorktreeName = "fixture-linked-after-cache"
            let newWorktreePath = Path.Combine(fixture.Root, "worktrees", newWorktreeName)
            git fixture.Project [ "worktree"; "add"; "-b"; newWorktreeName; newWorktreePath; "HEAD" ] |> ignore
            do! Task.Delay(200)

            let! afterRegistrationCold =
                measureTool fixtureRoot harness "read-file-after-worktree-add-cold" "ReadFile" "Operator" [ "workspace:read" ]
                    (fun () -> readFile newWorktreeName)
            measurements.Add(afterRegistrationCold)

            let! afterRegistrationWarm =
                measureTool fixtureRoot harness "read-file-after-worktree-add-warm" "ReadFile" "Operator" [ "workspace:read" ]
                    (fun () -> readFile newWorktreeName)
            measurements.Add(afterRegistrationWarm)

            let! getTask =
                measureTool fixtureRoot harness "get-task" "GetTask" "Operator" [ "workspace:read" ]
                    (fun () -> JarvisMcpTools.GetTask("FixtureProject", "Operator", "perf-task-0001", harness.DeviceId, harness.Client, harness.Http))
            measurements.Add(getTask)

            // Project-scoped TaskWrite requires read for project selection and git:write for mutation.
            let beforeWrite, refsBefore = remoteState fixture.BareRemote
            let operationId = "40000000-0000-4000-8000-000000000001"
            let! registerAgent =
                measureTool fixtureRoot harness "register-agent" "RegisterAgent" "BenchmarkWorker" [ "workspace:read"; "git:write" ]
                    (fun () -> JarvisMcpTools.RegisterAgent("FixtureProject", "BenchmarkWorker", operationId, harness.DeviceId, harness.Client, harness.Http))
            let afterWrite, refsAfter = remoteState fixture.BareRemote
            measurements.Add(withState beforeWrite afterWrite refsBefore refsAfter registerAgent)

            let! operationStatus =
                measureTool fixtureRoot harness "get-operation-status" "GetOperationStatus" "BenchmarkWorker" [ "workspace:read" ]
                    (fun () -> JarvisMcpTools.GetOperationStatus("FixtureProject", "BenchmarkWorker", operationId, harness.DeviceId, harness.Client, harness.Http))
            measurements.Add(operationStatus)

            let beforeRetry, refsBeforeRetry = remoteState fixture.BareRemote
            let! retrySameOperation =
                measureTool fixtureRoot harness "retry-same-operation" "RegisterAgent" "BenchmarkWorker" [ "workspace:read"; "git:write" ]
                    (fun () -> JarvisMcpTools.RegisterAgent("FixtureProject", "BenchmarkWorker", operationId, harness.DeviceId, harness.Client, harness.Http))
            let afterRetry, refsAfterRetry = remoteState fixture.BareRemote
            measurements.Add(withState beforeRetry afterRetry refsBeforeRetry refsAfterRetry retrySameOperation)

            let beforeConflict, refsBeforeConflict = remoteState fixture.BareRemote
            let! reusedOperationChangedInput =
                measureTool fixtureRoot harness "operation-id-reused-with-changed-input" "RegisterAgent" "DifferentWorker" [ "workspace:read"; "git:write" ]
                    (fun () -> JarvisMcpTools.RegisterAgent("FixtureProject", "DifferentWorker", operationId, harness.DeviceId, harness.Client, harness.Http))
            let afterConflict, refsAfterConflict = remoteState fixture.BareRemote
            measurements.Add(withState beforeConflict afterConflict refsBeforeConflict refsAfterConflict reusedOperationChangedInput)

            let beforeUnauthorized, refsBeforeUnauthorized = remoteState fixture.BareRemote
            let! unauthorizedWrite =
                measureTool fixtureRoot harness "unauthorized-register-agent" "RegisterAgent" "UnauthorizedWorker" [ "workspace:read" ]
                    (fun () -> JarvisMcpTools.RegisterAgent("FixtureProject", "UnauthorizedWorker", "50000000-0000-4000-8000-000000000001", harness.DeviceId, harness.Client, harness.Http))
            let afterUnauthorized, refsAfterUnauthorized = remoteState fixture.BareRemote
            measurements.Add(withState beforeUnauthorized afterUnauthorized refsBeforeUnauthorized refsAfterUnauthorized unauthorizedWrite)

            do! harness.Queue.Complete()
            harness.Services.Dispose()

            let report =
                { SchemaVersion = 2
                  SourceRoot = sourceRoot
                  SourceBranch = sourceBranch
                  SourceSha = sourceSha
                  ParentSha = parentSha
                  RemoteMainSha = remoteMainSha
                  MeasuredSourceClean = measuredSourceClean
                  FixtureRoot = fixtureRoot
                  FixtureSeedSha = fixture.SeedSha
                  FixturePublishedSha = fixturePublishedSha
                  FixtureRemote = fixture.BareRemote
                  Measurements = List.ofSeq measurements }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)) |> ignore
            let options = JsonSerializerOptions(WriteIndented = true)
            File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options))
            printfn "Source: %s (%s)" sourceSha sourceBranch
            printfn "Fixture: %s" fixtureRoot
            printfn "Report: %s" outputPath
            for measurement in measurements do
                printfn "%s: %.2f ms, %d Git starts, %d fetches, response=%s"
                    measurement.Scenario measurement.DurationMilliseconds measurement.GitProcessStarts
                    measurement.RemoteFetches (defaultArg measurement.ResponseClass "<no response>")
            return 0
        }

    [<EntryPoint>]
    let main argv =
        try
            run argv |> fun operation -> operation.GetAwaiter().GetResult()
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
