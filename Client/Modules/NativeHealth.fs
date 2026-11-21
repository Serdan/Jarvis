module Client.NativeHealth

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Client.LedgerStore
open Client.ReviewWorkflow
open Common
open Common.SignalR

// All times used to decide eligibility originate at the authenticated server.
// Local time is only a diagnostic; a missing observation is NEVER an idle proof.
type Settings =
    { Enabled: bool
      InactivitySeconds: int }

type Candidate =
    { ProjectName: string
      ProjectId: Guid
      LedgerId: Guid
      Epoch: int
      ApprovedFetchUrl: string
      ApprovedPushUrl: string
      GitHead: string
      AgentId: string
      AgentVersion: int
      TaskId: string
      TaskVersion: int
      AttemptId: string
      AttemptVersion: int
      Generation: int
      SpecBlobOid: string
      SourceRef: string }

type CheckSummary =
    { CheckedAtUtc: DateTimeOffset
      Enabled: bool
      Candidates: int
      Holds: int
      Recoveries: int
      Errors: string list }

type Dependencies =
    { Enumerate: unit -> Result<Candidate list, string>
      Current: Candidate -> Result<Candidate option, string>
      Activity: string -> string -> Task<Result<AgentActivitySnapshot, string>>
      Recover: Candidate -> Guid -> Result<unit, string> }

module NativeHealth =
    let defaultTimeoutSeconds = 600
    let pollInterval = TimeSpan.FromSeconds 60.
    let maxCheckDuration = TimeSpan.FromSeconds 90.

    let settingsFromStored (stored: ClientStoredConfig) =
        { Enabled = stored.NativeHealthEnabled |> Option.defaultValue false
          InactivitySeconds = stored.NativeHealthTimeoutSeconds |> Option.defaultValue defaultTimeoutSeconds }

    // A stable deterministic ID for one exact CAS state, independent of
    // clients and poll intervals. Later versions get fresh IDs; Git receipts
    // and the pending-intent journal reconcile uncertain results by this ID.
    let recoveryOperationId (candidate: Candidate) =
        let material =
            $"NativeHealthRecover:v1:{candidate.ProjectId:N}:{candidate.LedgerId:N}:{candidate.Epoch}:{candidate.TaskId}:{candidate.AttemptId}:{candidate.Generation}:{candidate.TaskVersion}:{candidate.AttemptVersion}:{candidate.AgentVersion}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes material)
        let bytes = hash[..15]
        bytes[6] <- (bytes[6] &&& 0x0Fuy) ||| 0x50uy
        bytes[8] <- (bytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(bytes)

    let private idleAt (timeout: TimeSpan) (observation: AgentActivitySnapshot) =
        if isNull (box observation) then Error "Server activity response unavailable."
        elif observation.ServerNowUtc = DateTimeOffset.MinValue then
            Error "Server activity clock is invalid."
        else
            match observation.LastSeenAtUtc with
            | None -> Error "No authoritative qualifying activity after server restart."
            | Some seen when seen = DateTimeOffset.MinValue || seen > observation.ServerNowUtc ->
                Error "Invalid or future server activity timestamp."
            | Some seen ->
                Ok (observation.ServerNowUtc - seen >= timeout)

    let private sameCandidate (before: Candidate) (after: Candidate) =
        { after with GitHead = before.GitHead } = before

    // One bounded pass, no AgentInbox, no implicit activation, no MCP execution.
    // Recovery uses a fresh canonical read followed by a final server activity read.
    let checkOnce (settings: Settings) (dependencies: Dependencies) : Task<CheckSummary> =
        task {
            let mutable holds = 0
            let mutable recoveries = 0
            let errors = ResizeArray<string>()
            let error (text: string) =
                holds <- holds + 1
                if errors.Count < 4 then errors.Add(text.Substring(0, min 240 text.Length))
            let skip () = holds <- holds + 1
            let mutable candidates = []
            let clock = Stopwatch.StartNew()
            let mutable budgetExceeded = false
            if settings.Enabled then
                if settings.InactivitySeconds < 1 || settings.InactivitySeconds > 604800 then
                    error "Native Health timeout is outside the positive configured range."
                else
                    try
                        match dependencies.Enumerate() with
                        | Ok values when values.Length <= 512 -> candidates <- values
                        | Ok _ -> error "Native Health candidate bound exceeded."
                        | Error reason -> error ("Canonical roster unavailable: " + reason)
                    with ex -> error ("Canonical roster exception: " + ex.GetType().Name)

                let timeout = TimeSpan.FromSeconds(float settings.InactivitySeconds)
                for candidate in candidates do
                    if clock.Elapsed >= maxCheckDuration then
                        if not budgetExceeded then
                            budgetExceeded <- true
                            error "Native Health bounded check exceeded 90 seconds."
                    if not budgetExceeded then
                        try
                            let! first = dependencies.Activity candidate.ProjectName candidate.AgentId
                            match first with
                            | Error reason -> error ("Activity unavailable for " + candidate.AgentId + ": " + reason)
                            | Ok observation ->
                                match idleAt timeout observation with
                                | Error reason -> error (candidate.AgentId + ": " + reason)
                                | Ok false -> skip ()
                                | Ok true ->
                                    match dependencies.Current candidate with
                                    | Error reason -> error (candidate.AgentId + ": Git recheck unavailable: " + reason)
                                    | Ok None -> skip ()
                                    | Ok (Some current) when not (sameCandidate candidate current) -> skip ()
                                    | Ok (Some current) ->
                                        let! latest = dependencies.Activity candidate.ProjectName candidate.AgentId
                                        match latest with
                                        | Error reason -> error (candidate.AgentId + ": fresh activity unavailable: " + reason)
                                        | Ok fresh ->
                                            // Any new activity, reset, or reversed server clock is a HOLD.
                                            if fresh.ServerNowUtc < observation.ServerNowUtc
                                               || fresh.LastSeenAtUtc <> observation.LastSeenAtUtc then
                                                skip ()
                                            else
                                                match idleAt timeout fresh with
                                                | Error reason -> error (candidate.AgentId + ": " + reason)
                                                | Ok false -> skip ()
                                                | Ok true ->
                                                    let operationId = recoveryOperationId current
                                                    match dependencies.Recover current operationId with
                                                    | Ok () -> recoveries <- recoveries + 1
                                                    | Error reason ->
                                                        error (candidate.AgentId + ": recovery not confirmed, operation "
                                                               + operationId.ToString("D") + ": " + reason)
                        with ex -> error (candidate.AgentId + ": " + ex.GetType().Name)
            return
                { CheckedAtUtc = DateTimeOffset.UtcNow
                  Enabled = settings.Enabled
                  Candidates = candidates.Length
                  Holds = holds
                  Recoveries = recoveries
                  Errors = errors |> Seq.toList }
        }

    let internal candidateAt project (binding: LedgerBinding) snapshot agentId =
        match ReviewWorkflow.getAgentWorkingTurnAt binding snapshot agentId with
        | Error issue -> Error (sprintf "%A" issue)
        | Ok agent when agent.Execution <> "Working"
                        || isNull (box agent.CurrentAssignment) -> Ok None
        | Ok agent ->
            let assignment = agent.CurrentAssignment
            match ReviewWorkflow.getTaskAt binding snapshot assignment.TaskId with
            | Error issue -> Error (sprintf "%A" issue)
            | Ok (task, Some attempt)
                when task.Lifecycle = "InProgress"
                     && task.AttemptId = assignment.AttemptId
                     && attempt.AttemptId = assignment.AttemptId
                     && attempt.OwnerAgentId = agentId
                     && attempt.Phase = "Started"
                     && attempt.AssignmentGeneration >= 1
                     && agent.Version >= 1
                     && attempt.Version >= 1 ->
                let candidate : Candidate =
                    { ProjectName = project
                      ProjectId = binding.ProjectId
                      LedgerId = binding.LedgerId
                      Epoch = binding.Epoch
                      ApprovedFetchUrl = binding.ApprovedFetchUrl
                      ApprovedPushUrl = binding.ApprovedPushUrl
                      GitHead = snapshot.Commit
                      AgentId = agentId
                      AgentVersion = agent.Version
                      TaskId = task.TaskId
                      TaskVersion = task.Version
                      AttemptId = attempt.AttemptId
                      AttemptVersion = attempt.Version
                      Generation = attempt.AssignmentGeneration
                      SpecBlobOid = attempt.SpecBlobOid
                      SourceRef = attempt.SourceRef }
                Ok (Some candidate)
            | _ -> Ok None

    let private agentIdFromRecord (binding: LedgerBinding) snapshot (path: string) =
        match LedgerStore.readAt binding snapshot path with
        | Error issue -> Error (sprintf "%A" issue)
        | Ok (None, _) -> Error ("Missing canonical registered agent: " + path)
        | Ok (Some content, _) ->
            try
                use json = JsonDocument.Parse(content)
                let root = json.RootElement
                if root.GetProperty("schemaVersion").GetInt32() <> 3 then
                    Error ("Unsupported registered agent schema: " + path)
                else
                    let id = root.GetProperty("agentId").GetString()
                    if String.IsNullOrWhiteSpace id then Error ("Invalid registered agent identity: " + path)
                    else
                        let key = Convert.ToBase64String(Encoding.UTF8.GetBytes id)
                                        .TrimEnd('=').Replace('+', '-').Replace('/', '_')
                        if path <> $"agents/{key}.json" then
                            Error ("Registered agent path and identity mismatch: " + path)
                        else Ok id
            with :? JsonException -> Error ("Malformed registered agent record: " + path)
               | :? InvalidOperationException -> Error ("Invalid registered agent record: " + path)
               | :? KeyNotFoundException -> Error ("Missing registered agent fields: " + path)

    // Only locally approved, Git-bound projects are scanned. Unbound ordinary
    // directories are never promoted into health authority by discovery.
    let enumerate (rt: Runtime) =
        match ProjectBrowser.listGitProjects rt with
        | Error reason -> Error (sprintf "%A" reason)
        | Ok projects ->
            let names = projects |> List.map (fun (ProjectName name) -> name) |> List.distinct |> List.sort
            if names.Length > 64 then Error "Project enumeration exceeds bounded scope."
            else
                let candidates = ResizeArray<Candidate>()
                let seen = Dictionary<string, LedgerBinding>(StringComparer.Ordinal)
                let mutable failure = None
                let clock = Stopwatch.StartNew()
                for name in names do
                    if clock.Elapsed >= maxCheckDuration then
                        failure <- Some "Native Health registered project scan exceeded 90 seconds."
                    if failure.IsNone then
                        match ProjectPaths.resolveProjectRoot name rt with
                        | Error reason -> failure <- Some (sprintf "%A" reason)
                        | Ok (FolderPath root) ->
                            if File.Exists(OrchestrationApi.LedgerBindingConfig.pathForRoot root) then
                                match OrchestrationApi.LedgerBindingConfig.loadWithSnapshot name rt with
                                | Error reason -> failure <- Some (sprintf "%A" reason)
                                | Ok (binding, snapshot) ->
                                    let identity = $"{binding.ProjectId:N}:{binding.LedgerId:N}:{binding.Epoch}"
                                    match seen.TryGetValue identity with
                                    | true, original ->
                                        if original.ApprovedFetchUrl <> binding.ApprovedFetchUrl
                                           || original.ApprovedPushUrl <> binding.ApprovedPushUrl then
                                            failure <- Some ("Conflicting approved aliases for " + identity)
                                    | false, _ ->
                                        seen.Add(identity, binding)
                                        match LedgerStore.listAt binding snapshot "agents" with
                                        | Error reason -> failure <- Some (sprintf "%A" reason)
                                        | Ok paths ->
                                            let agentFiles =
                                                paths |> List.filter (fun path ->
                                                    path.StartsWith("agents/", StringComparison.Ordinal)
                                                    && path.EndsWith(".json", StringComparison.Ordinal))
                                            if agentFiles.Length > 512 then
                                                failure <- Some ("Registered agent enumeration exceeds bound for " + name)
                                            else
                                                for path in agentFiles do
                                                    if failure.IsNone then
                                                        match agentIdFromRecord binding snapshot path with
                                                        | Error reason -> failure <- Some reason
                                                        | Ok id ->
                                                            match candidateAt name binding snapshot id with
                                                            | Error reason -> failure <- Some reason
                                                            | Ok None -> ()
                                                            | Ok (Some candidate) -> candidates.Add candidate
                match failure with
                | Some reason -> Error reason
                | None -> Ok (candidates |> Seq.toList)

    let current (rt: Runtime) (before: Candidate) =
        match OrchestrationApi.LedgerBindingConfig.loadWithSnapshot before.ProjectName rt with
        | Error issue -> Error (sprintf "%A" issue)
        | Ok (binding, snapshot) ->
            if binding.ProjectId <> before.ProjectId || binding.LedgerId <> before.LedgerId
               || binding.Epoch <> before.Epoch
               || binding.ApprovedFetchUrl <> before.ApprovedFetchUrl
               || binding.ApprovedPushUrl <> before.ApprovedPushUrl then
                Error "Canonical approved project binding changed."
            else candidateAt before.ProjectName binding snapshot before.AgentId

    // Uses the existing task-write pending-intent journal and Git CAS recovery
    // mutation. This code never creates a browser/session/workflow execution.
    let recover (rt: Runtime) (candidate: Candidate) (operationId: Guid) =
        let command : TaskWriteCommand =
            { ProjectName = candidate.ProjectName
              Action = "RecoverAgentAssignment"
              OperationId = operationId.ToString("D")
              TaskId = candidate.TaskId
              AttemptId = candidate.AttemptId
              ActorId = candidate.AgentId
              OwnerId = candidate.AgentId
              Title = ""
              Html = ""
              ExpectedVersion = candidate.AttemptVersion
              ExpectedAgentVersion = candidate.AgentVersion
              ExpectedTaskVersion = candidate.TaskVersion
              AssignmentGeneration = candidate.Generation
              Summary = ""
              NextAction = ""
              Reason = "Native Health recovery after confirmed server-side Jarvis tool inactivity."
              BaseOid = ""
              TargetRef = ""
              SourceRef = ""
              AllowDetached = false
              AllowUncommitted = false
              DecisionSequence = 0L
              Evidence = ""
              SessionId = "" }
        match OrchestrationApi.TaskApi.write command rt with
        | Ok _ -> Ok ()
        | Error error ->
            // A lost response is not proof of failure. Inspect the ORIGINAL ID;
            // do not allocate a replacement UUID or silently replay a new intent.
            match OrchestrationApi.LedgerBindingConfig.load candidate.ProjectName rt with
            | Error bindingError -> Error (sprintf "Outcome unconfirmed: %A; %A" error bindingError)
            | Ok binding ->
                match LedgerStore.getOperationStatus binding operationId with
                | Ok (_, Some receipt) when receipt.Type = "RecoverAgentAssignment" -> Ok ()
                | _ -> Error (sprintf "Outcome unconfirmed: %A. Reconcile the pending intent by original UUID." error)

    type Service(settings: unit -> Result<Settings, string>,
                 dependencies: Dependencies,
                 report: string -> unit,
                 ?interval: TimeSpan) =
        let delay = defaultArg interval pollInterval
        do if delay <= TimeSpan.Zero then invalidArg "interval" "Polling interval must be positive."
        let cancellation = new CancellationTokenSource()
        let mutable running : Task option = None
        let gate = obj()
        let mutable lastCheck : CheckSummary option = None
        let loop () : Task =
            task {
                while not cancellation.IsCancellationRequested do
                    try
                        match settings() with
                        | Error reason -> report ("Native Health HOLD: invalid configuration: " + reason)
                        | Ok config ->
                            let! summary = checkOnce config dependencies
                            lock gate (fun () -> lastCheck <- Some summary)
                            if config.Enabled then
                                let detail =
                                    if summary.Errors.IsEmpty then ""
                                    else " errors=" + (summary.Errors |> String.concat " | ")
                                report
                                    $"Native Health check: candidates={summary.Candidates} holds={summary.Holds} recovered={summary.Recoveries}{detail}"
                    with ex ->
                        report ("Native Health HOLD: check failure " + ex.GetType().Name)
                    try do! Task.Delay(delay, cancellation.Token)
                    with :? OperationCanceledException -> ()
            } :> Task

        member _.LastCheck = lock gate (fun () -> lastCheck)

        member _.Start() =
            lock gate (fun () ->
                if running.IsNone then
                    running <- Some(Task.Run(Func<Task>(loop))))

        member _.StopAsync() : Task =
            task {
                cancellation.Cancel()
                let pending = lock gate (fun () -> running)
                match pending with
                | None -> ()
                | Some worker -> do! worker
            } :> Task

        interface IDisposable with
            member _.Dispose() = cancellation.Cancel()
