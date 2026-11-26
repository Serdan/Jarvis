module Client.AutomationJobTerminalWake

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Common
open Client.LedgerStore
open Client.ChallengeWorkflow
open Client.OrchestrationApi

module Durable = Client.BoundedProcess.DurableJobs

/// All ingress events are hints. The only positive terminal authority is the
/// original durable request plus its supervisor's matching terminal receipt.
type ReconcileReport =
    { Committed: int
      Failures: string list }

let private statusText = function
    | JobStatus.Completed code -> sprintf "Completed(%d)" code
    | JobStatus.Canceled -> "Canceled"
    | JobStatus.FailedToStart error ->
        let message = if isNull error then "" else error
        "FailedToStart: " + message.Substring(0, min 512 message.Length)
    | _ -> invalidOp "Nonterminal observation cannot satisfy a job condition."

let private terminalEvidence directory (request: Durable.Request) =
    match Durable.observe directory request with
    | Durable.Terminal receipt ->
        let digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes receipt)
        Some(JsonSerializer.Serialize
            {| schema = "engineering-job-terminal-v1"
               jobId = request.JobId
               project = request.Project
               actor = request.Actor.Value
               startedAt = request.StartedAt.ToString("O")
               completedAt = receipt.CompletedAt.Value.ToString("O")
               outcome = statusText receipt.Status
               receiptSha256 = Convert.ToHexString(digest).ToLowerInvariant() |})
    | Durable.Live _ | Durable.Unknown _ -> None

let private operationId (binding: LedgerBinding) (challenge: AgentChallenge) conditionId jobId =
    let identity =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "engineering-job-terminal-resolution-v1"
               project = binding.ProjectId; ledger = binding.LedgerId; epoch = binding.Epoch
               challenge = challenge.ChallengeId; condition = conditionId
               version = challenge.Version; job = jobId |}
    Guid((SHA256.HashData identity).AsSpan(0, 16))

let private resolutionCommand project actor (challenge: AgentChallenge) conditionId (operationId: Guid) evidence : ChallengeWriteCommand =
    { ProjectName = project; ActorId = actor; OperationId = operationId.ToString()
      Action = "Resolve"; ChallengeId = challenge.ChallengeId; ConditionId = conditionId
      ExpectedVersion = challenge.Version; Title = ""; TaskId = challenge.TaskId; AttemptId = ""
      DependencyChallengeIds = []; ExpectedTaskVersion = 0; ExpectedAttemptVersion = 0
      ExpectedAgentVersion = 0; AssignmentGeneration = 0; Conditions = []
      Evidence = evidence; Reason = ""; Outcome = "Satisfied"; NewOwnerAgentId = ""; Subscribe = false }

/// Discover relevant canonical challenge locators in one snapshot before any
/// mutation. Concurrent changes fail closed and are retried on a later hint.
let private discover binding actor =
    let rec page snapshot cursor acc =
        match ChallengeWorkflow.listPage binding actor 50 cursor snapshot with
        | Error failure -> Error failure
        | Ok result ->
            let matching =
                result.Challenges
                |> List.filter (fun summary ->
                    summary.IssuerAgentId = actor
                    && summary.PendingConditions > 0
                    && not (List.isEmpty summary.OwnedUnresolvedConditionIds))
                |> List.map _.ChallengeId
            let accumulated = List.rev matching @ acc
            if String.IsNullOrEmpty result.NextCursor then Ok(List.rev accumulated)
            else page result.SnapshotCommit result.NextCursor accumulated
    page "" "" []

/// Read-only durable registry access uses the same supervisor observer as
/// JobManager. There is no second registry, no job spawning, and no inferred
/// terminal state on eviction, wrong owner, foreign project or unknown result.
/// The supplied binding resolver is the trusted client-local project resolver.
let reconcile enabled root
              (loadBinding: string -> Result<LedgerBinding, Client.EffectError>)
              (onCommitted: ChallengeWriteCommand -> OperationResponse.Presented -> unit) =
    if not enabled then { Committed = 0; Failures = [] }
    else
        let failures = ResizeArray<string>()
        let mutable committed = 0
        let report error = failures.Add error
        let entries =
            try
                Durable.entries root
                |> Array.choose (fun (directory, request) ->
                    match request.Actor with
                    | Some actor when not (String.IsNullOrWhiteSpace actor) ->
                        try
                            match terminalEvidence directory request with
                            | Some _ -> Some((request.Project, actor), (directory, request))
                            | None -> None
                        with ex ->
                            report ("Job evidence unavailable: " + ex.Message)
                            None
                    | _ -> None)
                |> Array.groupBy fst
            with ex ->
                report ("Job registry unavailable: " + ex.Message)
                [||]
        for (project, actor), items in entries do
            try
                match loadBinding project with
                | Error error -> report (sprintf "Binding unavailable for %s: %A" project error)
                | Ok binding ->
                    let jobs =
                        items |> Array.map (fun (_, (directory, request)) ->
                            request.JobId, (directory, request)) |> Map.ofArray
                    match discover binding actor with
                    | Error failure -> report (sprintf "Challenge discovery held: %A" failure)
                    | Ok ids ->
                        for challengeId in ids do
                            // One fresh read per resolution. Resolving a sibling
                            // increments the challenge CAS version. Re-read after
                            // each successful operation rather than reusing it.
                            let mutable more = true
                            while more do
                                more <- false
                                match ChallengeWorkflow.getPage binding actor challengeId 1 0L "" with
                                | Error failure -> report (sprintf "Challenge read held: %A" failure)
                                | Ok detail ->
                                    let challenge = detail.Challenge
                                    if challenge.IssuerAgentId = actor
                                       && (challenge.Status = "Pending" || challenge.Status = "Blocked") then
                                        let candidate =
                                            challenge.Conditions
                                            |> List.tryPick (fun condition ->
                                                if condition.Status <> "Pending" || condition.OwnerAgentId <> actor
                                                   || condition.OwnerKind <> "Agent" then None
                                                else
                                                    JobTerminalCondition.tryJobId condition.Description
                                                    |> Option.bind (fun jobId ->
                                                        Map.tryFind jobId jobs
                                                        |> Option.bind (fun (directory, original) ->
                                                            // Re-open the original request, not event-supplied
                                                            // metadata. It must remain byte-semantically identical.
                                                            try
                                                                let fresh = Durable.readRequest directory
                                                                if fresh <> original || fresh.JobId <> jobId
                                                                   || fresh.Project <> project || fresh.Actor <> Some actor then None
                                                                else
                                                                    terminalEvidence directory fresh
                                                                    |> Option.map (fun evidence -> condition.ConditionId, jobId, evidence)
                                                            with _ -> None)))
                                        match candidate with
                                        | None -> ()
                                        | Some(conditionId, jobId, evidence) ->
                                            let op = operationId binding challenge conditionId jobId
                                            let cmd = resolutionCommand project actor challenge conditionId op evidence
                                            // Existing Challenge CAS owns dependency requeue and generation.
                                            // Exact stable UUID + identical evidence makes retry idempotent.
                                            match ChallengeWorkflow.resolveJobTerminal binding op actor challengeId
                                                    conditionId challenge.Version jobId evidence with
                                            | Error failure -> report (sprintf "Job terminal resolution held: %A" failure)
                                            | Ok write ->
                                                if not write.AlreadyApplied then
                                                    committed <- committed + 1
                                                    try onCommitted cmd (OperationResponse.present write |> unbox)
                                                    with ex -> report ("Committed wake hint failed: " + ex.Message)
                                                more <- true
            with ex -> report ("Job reconciliation held: " + ex.Message)
        { Committed = committed; Failures = List.ofSeq failures }

/// A single coalescing event consumer. Startup, atomic receipt publication,
/// post-commit registration and reconnect call the same reconciliation.
/// Retries are deadline-driven only after a failed reconciliation; successful
/// idle hosts never poll. This host is intentionally not enabled by Program
/// until the production scheduler and registration ingress are jointly proved.
type Host(enabled: bool, root: string,
          loadBinding: string -> Result<LedgerBinding, Client.EffectError>,
          onCommitted: ChallengeWriteCommand -> OperationResponse.Presented -> unit,
          report: string -> unit) =
    let gate = obj()
    let signal = new SemaphoreSlim(0, 1)
    let cancel = new CancellationTokenSource()
    let mutable stopped = false
    let notify () =
        lock gate (fun () ->
            if not enabled || stopped then false
            else
                try signal.Release() |> ignore with :? SemaphoreFullException -> ()
                true)
    let safeReport message = try report message with _ -> ()
    // Root directory creation plus per-job watchers installed before reading
    // the receipts closes the recursive-watcher new-directory race. Watches
    // are ephemeral, bounded by the existing registry, and never authority.
    let jobWatches = System.Collections.Generic.Dictionary<string, FileSystemWatcher>(StringComparer.Ordinal)
    let watcher =
        if not enabled then None
        else
            let watch = new FileSystemWatcher(root)
            watch.NotifyFilter <- NotifyFilters.DirectoryName
            watch.Created.Add(fun _ -> notify() |> ignore)
            watch.Deleted.Add(fun _ -> notify() |> ignore)
            watch.Renamed.Add(fun _ -> notify() |> ignore)
            watch.Error.Add(fun _ -> notify() |> ignore)
            watch.EnableRaisingEvents <- true
            Some watch
    let refreshWatches () =
        let current =
            Directory.EnumerateDirectories(root)
            |> Seq.choose (fun path ->
                try
                    let id = Path.GetFileName path
                    if Durable.validId id then Some(Durable.jobDirectory root id) else None
                with _ -> None)
            |> Set.ofSeq
        for path in jobWatches.Keys |> Seq.toArray do
            if not (Set.contains path current) then
                jobWatches[path].Dispose()
                jobWatches.Remove path |> ignore
        for path in current do
            if not (jobWatches.ContainsKey path) then
                let watch = new FileSystemWatcher(path)
                try
                    let relevant (event: FileSystemEventArgs) =
                        if event.Name = "receipt.json" || event.Name = "request.json" then
                            notify() |> ignore
                    watch.NotifyFilter <- NotifyFilters.FileName ||| NotifyFilters.LastWrite
                    watch.Created.Add relevant
                    watch.Changed.Add relevant
                    watch.Renamed.Add(fun e -> relevant e)
                    watch.Error.Add(fun _ -> notify() |> ignore)
                    watch.EnableRaisingEvents <- true
                    jobWatches.Add(path, watch)
                with _ ->
                    watch.Dispose()
                    reraise()
    let worker =
        if not enabled then Task.CompletedTask
        else
            notify() |> ignore
            Task.Run(Func<Task>(fun () ->
                task {
                    let mutable retryMs = 1000
                    let mutable retry = false
                    try
                        while not cancel.IsCancellationRequested do
                            if retry then
                                let! _ = signal.WaitAsync(retryMs, cancel.Token)
                                ()
                            else
                                do! signal.WaitAsync(cancel.Token)
                            if not cancel.IsCancellationRequested then
                                let outcome =
                                    try
                                        refreshWatches()
                                        reconcile true root loadBinding onCommitted
                                    with ex -> { Committed = 0; Failures = ["Receipt watch unavailable: " + ex.Message] }
                                retry <- not (List.isEmpty outcome.Failures)
                                for failure in outcome.Failures do safeReport failure
                                retryMs <- if retry then min 60000 (retryMs * 2) else 1000
                    with :? OperationCanceledException -> ()
                } :> Task))
    member _.Notify() = notify()
    interface IDisposable with
        member _.Dispose() =
            let close =
                lock gate (fun () ->
                    if stopped then false
                    else
                        stopped <- true
                        true)
            if close then
                watcher |> Option.iter (fun watch -> watch.Dispose())
                cancel.Cancel()
                try worker.GetAwaiter().GetResult() finally
                    for watch in jobWatches.Values do watch.Dispose()
                    jobWatches.Clear()
                    cancel.Dispose()
                    signal.Dispose()
