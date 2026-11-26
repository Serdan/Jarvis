module GitCallBaselineTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Client
open Client.LedgerStore
open Client.OrchestrationApi
open Client.ReviewWorkflow
open Client.Worktrees
open Client.Tests
open NUnit.Framework

type private GitTraceSummary =
    { ElapsedMilliseconds: float
      TraceFile: string
      ProcessStarts: int
      FetchStarts: int
      PushStarts: int
      UploadPackStarts: int
      ReceivePackStarts: int
      StatusStarts: int
      ProcessElapsedMilliseconds: float }

type private CanonicalTree =
    { RefName: string
      RefOid: string
      TreeOid: string
      NormalizedTreeSha256: string }

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private runGit workingDirectory (arguments: string list) =
    use process = new Process()
    process.StartInfo.FileName <- "git"
    process.StartInfo.WorkingDirectory <- workingDirectory
    process.StartInfo.UseShellExecute <- false
    process.StartInfo.RedirectStandardOutput <- true
    process.StartInfo.RedirectStandardError <- true
    for argument in arguments do
        process.StartInfo.ArgumentList.Add(argument)
    if not (process.Start()) then
        failwithf "Could not start git %A in %s" arguments workingDirectory
    let stdout = process.StandardOutput.ReadToEnd()
    let stderr = process.StandardError.ReadToEnd()
    process.WaitForExit()
    if process.ExitCode <> 0 then
        failwithf "git %A failed in %s (exit %d): %s" arguments workingDirectory process.ExitCode stderr
    stdout.Trim()

let private stableGuid (value: string) =
    let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))
    Guid(bytes[0..15])

let private unwrap = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected ledger failure: %A" error

let private failureClass (error: LedgerFailure) =
    let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(error, typeof<LedgerFailure>)
    case.Name

let private resultClass = function
    | Ok _ -> "Ok"
    | Error error -> failureClass error

let private isVolatileJsonKey (key: string) =
    let normalized = key.ToLowerInvariant()
    normalized = "snapshotcommit"
    || normalized = "createdatutc"
    || normalized = "updatedatutc"
    || normalized = "startedatutc"
    || normalized = "finishedatutc"
    || normalized = "completedatutc"
    || normalized = "recordedatutc"
    || normalized = "timestamp"
    || normalized = "timestamputc"

let private canonicalJson (value: obj) =
    let raw = JsonSerializer.Serialize(value)
    let root = JsonNode.Parse(raw)

    let rec render (node: JsonNode) =
        if isNull node then
            "null"
        else
            match node with
            | :? JsonObject as jsonObject ->
                jsonObject
                |> Seq.choose (fun property ->
                    if isVolatileJsonKey property.Key then None
                    else Some(property.Key, property.Value))
                |> Seq.sortBy fst
                |> Seq.map (fun (key, child) ->
                    JsonSerializer.Serialize(key) + ":" + render child)
                |> String.concat ","
                |> fun body -> "{" + body + "}"
            | :? JsonArray as jsonArray ->
                jsonArray
                |> Seq.map render
                |> String.concat ","
                |> fun body -> "[" + body + "]"
            | _ -> node.ToJsonString()

    render root

let private sha256 (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes(value))
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let private sourceSha () =
    runGit repoRoot [ "rev-parse"; "HEAD" ]

let private traceSummary path elapsedMilliseconds =
    let startsBySid = Dictionary<string, string list>()
    let mutable processStarts = 0
    let mutable fetchStarts = 0
    let mutable pushStarts = 0
    let mutable uploadPackStarts = 0
    let mutable receivePackStarts = 0
    let mutable statusStarts = 0
    let mutable processElapsedMilliseconds = 0.0

    let tryString (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
            Some(value.GetString())
        else
            None

    let tryNumber (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number then
            Some(value.GetDouble())
        else
            None

    let tryArguments (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty("argv", &value) && value.ValueKind = JsonValueKind.Array then
            value.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.String then Some(item.GetString())
                else None)
            |> Seq.toList
        else
            []

    if File.Exists(path) then
        for line in File.ReadLines(path) do
            try
                use document = JsonDocument.Parse(line)
                let root = document.RootElement
                match tryString root "event" with
                | Some "start" ->
                    processStarts <- processStarts + 1
                    let sid = tryString root "sid" |> Option.defaultValue ""
                    let arguments = tryArguments root
                    startsBySid[sid] <- arguments
                    if arguments |> List.contains "fetch" then fetchStarts <- fetchStarts + 1
                    if arguments |> List.contains "push" then pushStarts <- pushStarts + 1
                    if arguments |> List.exists (fun argument -> argument.Contains("upload-pack", StringComparison.OrdinalIgnoreCase)) then
                        uploadPackStarts <- uploadPackStarts + 1
                    if arguments |> List.exists (fun argument -> argument.Contains("receive-pack", StringComparison.OrdinalIgnoreCase)) then
                        receivePackStarts <- receivePackStarts + 1
                    if arguments |> List.contains "status" then statusStarts <- statusStarts + 1
                | Some "exit" ->
                    match tryNumber root "t_abs" with
                    | Some seconds -> processElapsedMilliseconds <- processElapsedMilliseconds + seconds * 1000.0
                    | None -> ()
                | _ -> ()
            with _ ->
                ()

    { ElapsedMilliseconds = elapsedMilliseconds
      TraceFile = "traces/" + Path.GetFileName(path)
      ProcessStarts = processStarts
      FetchStarts = fetchStarts
      PushStarts = pushStarts
      UploadPackStarts = uploadPackStarts
      ReceivePackStarts = receivePackStarts
      StatusStarts = statusStarts
      ProcessElapsedMilliseconds = processElapsedMilliseconds }

let private reportDirectory () =
    match Environment.GetEnvironmentVariable("JARVIS_GIT_PERF_REPORT_DIR") with
    | null
    | "" -> Path.Combine(TestContext.CurrentContext.WorkDirectory, "git-call-reports")
    | directory -> directory

let private withTrace2 (action: unit -> 'T) =
    let directory = Path.Combine(reportDirectory (), "traces")
    Directory.CreateDirectory(directory) |> ignore
    let tracePath = Path.Combine(directory, "jarvis-git-trace-" + Guid.NewGuid().ToString("N") + ".jsonl")
    let previous = Environment.GetEnvironmentVariable("GIT_TRACE2_EVENT")
    Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", tracePath)
    let stopwatch = Stopwatch.StartNew()
    let result =
        try
            action()
        finally
            stopwatch.Stop()
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", previous)
    let summary = traceSummary tracePath stopwatch.Elapsed.TotalMilliseconds
    result, summary

let private writeReport scenario (report: 'T) =
    let directory = reportDirectory ()
    Directory.CreateDirectory(directory) |> ignore
    let path = Path.Combine(directory, scenario + ".json")
    let options = JsonSerializerOptions(WriteIndented = true)
    File.WriteAllText(path, JsonSerializer.Serialize(report, options))
    TestContext.Progress.WriteLine("JARVIS_GIT_CALL_REPORT " + path)

let private sample operation =
    let serialize = function
        | Ok v -> canonicalJson (box v)
        | Error error -> sprintf "%A" error
    let value, _, measurement = PerformanceRegression.sample operation serialize
    value, measurement

let private sampleDto (sample: PerformanceRegression.Sample) =
    {| elapsedMilliseconds = sample.DurationMs
       allocatedBytes = sample.AllocatedBytes
       processStarts = sample.GitProcesses
       remoteFetches = sample.RemoteFetches
       individualBlobReads = sample.SingleBlobReads
       batchBlobReads = sample.BatchBlobReads
       outputBytes = sample.GitStdoutBytes
       resultBytes = sample.ResultBytes |}

let private traceDto (trace: GitTraceSummary) =
    {| elapsedMilliseconds = trace.ElapsedMilliseconds
       traceFile = trace.TraceFile
       processStarts = trace.ProcessStarts
       fetchStarts = trace.FetchStarts
       pushStarts = trace.PushStarts
       uploadPackStarts = trace.UploadPackStarts
       receivePackStarts = trace.ReceivePackStarts
       statusStarts = trace.StatusStarts
       processElapsedMilliseconds = trace.ProcessElapsedMilliseconds |}

let private canonicalLedgerTree root bare =
    let refName = "refs/heads/jarvis-orchestration"
    let refOid = runGit root [ "--git-dir"; bare; "rev-parse"; refName ]
    let treeOid = runGit root [ "--git-dir"; bare; "rev-parse"; refOid + "^{tree}" ]
    let paths =
        runGit root [ "--git-dir"; bare; "ls-tree"; "-r"; "--name-only"; refOid ]
        |> fun output ->
            output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let entries =
        paths
        |> List.map (fun path ->
            let content = runGit root [ "--git-dir"; bare; "show"; refOid + ":" + path ]
            let normalized =
                if path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) then
                    match JsonNode.Parse(content) with
                    | null -> "null"
                    | node ->
                        let rec render (value: JsonNode) =
                            if isNull value then "null"
                            else
                                match value with
                                | :? JsonObject as jsonObject ->
                                    jsonObject
                                    |> Seq.choose (fun property ->
                                        if isVolatileJsonKey property.Key then None
                                        else Some(property.Key, property.Value))
                                    |> Seq.sortBy fst
                                    |> Seq.map (fun (key, child) ->
                                        JsonSerializer.Serialize(key) + ":" + render child)
                                    |> String.concat ","
                                    |> fun body -> "{" + body + "}"
                                | :? JsonArray as jsonArray ->
                                    jsonArray |> Seq.map render |> String.concat "," |> fun body -> "[" + body + "]"
                                | _ -> value.ToJsonString()
                        render node
                else
                    content
            path + "\n" + normalized)
    { RefName = refName
      RefOid = refOid
      TreeOid = treeOid
      NormalizedTreeSha256 = sha256 (String.concat "\n---\n" entries) }

let private withFixture (action:
    string ->
    string ->
    string ->
    string ->
    LedgerBinding ->
    LedgerBinding ->
    string ->
    string ->
    (string -> Guid) ->
    string ->
    unit) =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-git-call-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let bare = Path.Combine(root, "remote.git")
    let owner = Path.Combine(root, "owner")
    let reader = Path.Combine(root, "reader")
    let fileUrl = Uri(bare).AbsoluteUri

    let cleanupBinding checkout =
        try
            let pendingPath = LedgerBindingConfig.pathForRoot checkout
            if Directory.Exists(pendingPath) then Directory.Delete(pendingPath, true)
        with _ -> ()

    let oldAuthorDate = Environment.GetEnvironmentVariable("GIT_AUTHOR_DATE")
    let oldCommitterDate = Environment.GetEnvironmentVariable("GIT_COMMITTER_DATE")
    Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", "2026-11-26T00:00:00Z")
    Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", "2026-11-26T00:00:00Z")

    try
        runGit root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        runGit root [ "clone"; bare; owner ] |> ignore
        runGit owner [ "config"; "user.name"; "Jarvis Perf Fixture" ] |> ignore
        runGit owner [ "config"; "user.email"; "jarvis-perf-fixture@example.invalid" ] |> ignore
        File.WriteAllText(Path.Combine(owner, "source.txt"), "fixture base\n")
        runGit owner [ "add"; "source.txt" ] |> ignore
        runGit owner [ "commit"; "-m"; "fixture base" ] |> ignore
        runGit owner [ "push"; "-u"; "origin"; "HEAD" ] |> ignore

        runGit root [ "clone"; bare; reader ] |> ignore
        for checkout in [ owner; reader ] do
            runGit checkout [ "config"; "user.name"; "Jarvis Perf Fixture" ] |> ignore
            runGit checkout [ "config"; "user.email"; "jarvis-perf-fixture@example.invalid" ] |> ignore
            runGit checkout [ "remote"; "set-url"; "origin"; fileUrl ] |> ignore

        let projectId = Guid.Parse("b73d0b6f-1b9e-47a8-8c21-73d5f09f3a11")
        let ledgerId = Guid.Parse("4f8e8c1d-5e2d-4e0a-9f36-6a8e0e9f7712")
        let binding checkout =
            { RepositoryPath = checkout
              RemoteName = "origin"
              ApprovedFetchUrl = fileUrl
              ApprovedPushUrl = fileUrl
              ProjectId = projectId
              LedgerId = ledgerId
              Epoch = 1 }

        let first = binding owner
        let second = binding reader
        LedgerStore.initialize first true |> unwrap |> ignore
        ReviewWorkflow.registerAgent first (stableGuid "register-owner") "Owner" |> unwrap |> ignore
        ReviewWorkflow.registerAgent first (stableGuid "register-reviewer") "Reviewer" |> unwrap |> ignore

        let baseOid = runGit owner [ "rev-parse"; "HEAD" ]
        let sourceRef = "refs/heads/" + runGit owner [ "branch"; "--show-current" ]

        let initTask taskId =
            ReviewWorkflow.createTask first (stableGuid ("create-" + taskId)) taskId ("Task " + taskId) |> unwrap |> ignore
            let attemptId = stableGuid ("attempt-" + taskId)
            ReviewWorkflow.assignTask first (stableGuid ("assign-" + taskId)) taskId attemptId "Owner" |> unwrap |> ignore
            ReviewWorkflow.beginAttempt first (stableGuid ("begin-" + taskId)) taskId "Owner" 1 1 |> unwrap |> ignore
            ReviewWorkflow.bindReviewBase first (stableGuid ("bind-" + taskId)) taskId "Owner" 2 1 baseOid sourceRef |> unwrap |> ignore
            attemptId

        action root bare owner reader first second baseOid sourceRef initTask fileUrl
    finally
        cleanupBinding owner
        cleanupBinding reader
        Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", oldAuthorDate)
        Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", oldCommitterDate)
        try
            if Directory.Exists(root) then Directory.Delete(root, true)
        with _ -> ()

[<TestFixture; NonParallelizable>]
type GitCallBaselineTests() =

    [<Test>]
    member _.AgentInboxAndTaskOverviewHaveStableSemanticDigests() =
        withFixture (fun root bare _owner reader _first second _baseOid _sourceRef initTask _fileUrl ->
            let taskId = "perf-inbox-task"
            let attemptId = initTask taskId

            let (coldInboxResult, coldInboxSample), coldInboxTrace =
                withTrace2 (fun () ->
                    sample (fun () -> ReviewWorkflow.getAgentInbox second "Owner"))

            let (warmInboxResult, warmInboxSample), warmInboxTrace =
                withTrace2 (fun () ->
                    sample (fun () -> ReviewWorkflow.getAgentInbox second "Owner"))

            let coldInbox = unwrap coldInboxResult
            let warmInbox = unwrap warmInboxResult
            let coldInboxJson = JsonSerializer.Serialize(coldInbox)
            let warmInboxJson = JsonSerializer.Serialize(warmInbox)
            let inboxDigest = sha256 coldInboxJson
            Assert.That(warmInboxJson, Is.EqualTo(coldInboxJson), "cold/warm inbox payload must be equivalent")
            Assert.That(coldInboxSample.GitProcesses, Is.GreaterThan(0), "inbox read should exercise Git")
            Assert.That(coldInboxSample.RemoteFetches, Is.GreaterThan(0), "inbox read should fetch its pinned snapshot")

            let (coldOverviewResult, coldOverviewSample), coldOverviewTrace =
                withTrace2 (fun () ->
                    sample (fun () -> ReviewWorkflow.listProjectTasks second))

            let (warmOverviewResult, warmOverviewSample), warmOverviewTrace =
                withTrace2 (fun () ->
                    sample (fun () ->
                        ReviewWorkflow.listProjectTasks second))

            let coldOverview = unwrap coldOverviewResult
            let warmOverview = unwrap warmOverviewResult
            let coldOverviewJson = JsonSerializer.Serialize(coldOverview)
            let warmOverviewJson = JsonSerializer.Serialize(warmOverview)
            let overviewDigest = sha256 coldOverviewJson
            Assert.That(warmOverviewJson, Is.EqualTo(coldOverviewJson), "cold/warm task overview payload must be equivalent")

            let invalidResult, invalidSample =
                sample (fun () -> ReviewWorkflow.getAgentInbox second "")
            Assert.That(resultClass invalidResult, Is.EqualTo("InvalidBinding"))
            Assert.That(invalidSample.GitProcesses, Is.EqualTo(0), "invalid agent id must fail before Git")

            let canonical = canonicalLedgerTree root bare
            Assert.That(coldInbox.SnapshotCommit, Is.EqualTo(canonical.RefOid))
            Assert.That(coldInbox.InstructionLayers.SnapshotCommit, Is.EqualTo(canonical.RefOid))
            Assert.That(coldOverview.SnapshotCommit, Is.EqualTo(canonical.RefOid))
            Assert.That(coldInbox.Agent.AgentId, Is.EqualTo("Owner"))
            Assert.That(coldInbox.Focused.IsSome, Is.True)
            Assert.That(coldInbox.Working, Is.EqualTo(coldInbox.Focused))
            let focused = coldInbox.Focused.Value
            Assert.That(focused.Task.TaskId, Is.EqualTo(taskId))
            Assert.That(Guid.Parse(focused.Attempt.AttemptId), Is.EqualTo(attemptId))
            Assert.That(coldOverview.Tasks.Length, Is.EqualTo(1))
            Assert.That(coldOverview.Tasks.Head.Task, Is.EqualTo(focused.Task))
            Assert.That(coldOverview.Tasks.Head.Attempt, Is.EqualTo(Some focused.Attempt))
            Assert.That(coldOverview.Reviews, Is.Empty)
            // Full raw digests remain per-fixture evidence. The cross-fixture
            // projection binds the verified local snapshot and source identity
            // without treating independent wall-clock/remote-root OIDs as equal.
            let attempt = focused.Attempt
            Assert.That(attempt.BaseCommit, Is.EqualTo(_baseOid))
            Assert.That(attempt.TargetRef, Is.EqualTo(_sourceRef))
            let stableAttempt =
                {| TaskId = attempt.TaskId; AttemptId = attempt.AttemptId; Version = attempt.Version; OwnerAgentId = attempt.OwnerAgentId
                   Phase = attempt.Phase; AssignmentGeneration = attempt.AssignmentGeneration; Priority = attempt.Priority
                   IssuedSequence = attempt.IssuedSequence; SpecBlobOid = attempt.SpecBlobOid; BaseCommit = attempt.BaseCommit
                   TargetRef = attempt.TargetRef; SourceRef = attempt.SourceRef |}
            let agent = coldInbox.Agent
            let stableInbox =
                {| AgentId = agent.AgentId; Version = agent.Version; CurrentAssignment = agent.CurrentAssignment; Execution = agent.Execution
                   UnassignedDisposition = agent.UnassignedDisposition; StateReason = agent.StateReason; StateSequence = agent.StateSequence
                   ActiveSessionId = agent.ActiveSessionId; AssociatedSessionIds = agent.AssociatedSessionIds
                   task = focused.Task; attempt = stableAttempt
                   OperatorActionRequired = coldInbox.OperatorActionRequired; NeedsOrchestratorAssignment = coldInbox.NeedsOrchestratorAssignment
                   SlotAvailable = coldInbox.SlotAvailable; Unassigned = coldInbox.Unassigned
                   RunnableQueue = coldInbox.RunnableQueue; Parked = coldInbox.Parked; Blocked = coldInbox.Blocked
                   BlockedChallengeWaits = coldInbox.BlockedChallengeWaits; ChallengeSubscribed = coldInbox.ChallengeSubscribed
                   ChallengeAssigned = coldInbox.ChallengeAssigned; ChallengeTriage = coldInbox.ChallengeTriage
                   ChallengeRecent = coldInbox.ChallengeRecent; ChallengeDependencies = coldInbox.ChallengeDependencies
                   ChallengeAssignedOmitted = coldInbox.ChallengeAssignedOmitted; ChallengeTriageOmitted = coldInbox.ChallengeTriageOmitted
                   ChallengeRecentOmitted = coldInbox.ChallengeRecentOmitted; ChallengeDependenciesOmitted = coldInbox.ChallengeDependenciesOmitted
                   PendingReviews = coldInbox.PendingReviews; AvailableReviews = coldInbox.AvailableReviews
                   UnacknowledgedFeedback = coldInbox.UnacknowledgedFeedback; PendingNotifications = coldInbox.PendingNotifications
                   IntegrationSubmissions = coldInbox.IntegrationSubmissions
                   RoleIds = coldInbox.InstructionLayers.RoleIds
                   AgentConfigVersion = coldInbox.InstructionLayers.AgentConfigVersion
                   Precedence = coldInbox.InstructionLayers.Precedence
                   Documents = coldInbox.InstructionLayers.Documents |}
            writeReport "agent-inbox" {|
                schemaVersion = 1
                scenario = "agent-inbox-and-task-overview"
                sourceSha = sourceSha ()
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                fixture = {| agentCount = 2; taskCount = 1; taskId = taskId; ownerAgentId = "Owner" |}
                inboxResultClass = resultClass coldInboxResult
                inboxPayloadSha256 = inboxDigest
                inboxSemanticSha256 = sha256 (canonicalJson (box stableInbox))
                snapshotMatchesCanonicalRef = true
                inboxPayloadBytes = Encoding.UTF8.GetByteCount(coldInboxJson)
                inboxCold = sampleDto coldInboxSample
                inboxColdTrace = traceDto coldInboxTrace
                inboxWarm = sampleDto warmInboxSample
                inboxWarmTrace = traceDto warmInboxTrace
                taskOverviewResultClass = resultClass coldOverviewResult
                taskOverviewPayloadSha256 = overviewDigest
                taskOverviewSemanticSha256 = sha256 (canonicalJson (box {| task = focused.Task; attempt = stableAttempt; reviews = coldOverview.Reviews |}))
                taskOverviewPayloadBytes = Encoding.UTF8.GetByteCount(coldOverviewJson)
                taskOverviewCold = sampleDto coldOverviewSample
                taskOverviewColdTrace = traceDto coldOverviewTrace
                taskOverviewWarm = sampleDto warmOverviewSample
                taskOverviewWarmTrace = traceDto warmOverviewTrace
                invalidAgentResultClass = resultClass invalidResult
                invalidAgentGitProcessStarts = invalidSample.GitProcesses
                canonicalRef = canonical.RefName
                canonicalRefOid = canonical.RefOid
                canonicalTreeOid = canonical.TreeOid
                canonicalNormalizedTreeSha256 = canonical.NormalizedTreeSha256
            |}
        )

    [<Test>]
    member _.UncertainPublicationReconcilesWithoutDuplicateEffect() =
        withFixture (fun root bare _owner _reader first second _baseOid _sourceRef _initTask _fileUrl ->
            let operationId = stableGuid "uncertain-publication-operation"
            let taskId = "perf-uncertain-publication"
            let title = "Uncertain publication recovery"

            let inject phase arguments : LedgerStore.GitTransportFault option =
                if phase = "confirmation-fetch" && List.tryHead arguments = Some "fetch" then
                    Some
                        { FaultExitCode = 1
                          FaultStderr = "controlled confirmation fetch timeout"
                          FaultTimedOut = true }
                else
                    None

            let (initialResult, initialMeasurement), initialTrace =
                withTrace2 (fun () ->
                    LedgerStore.measureMutationStages (fun () ->
                        LedgerStore.withGitTransportFaults inject (fun () ->
                            ReviewWorkflow.createTask first operationId taskId title)))

            Assert.That(resultClass initialResult, Is.EqualTo("OutcomeUnknown"),
                        "failed confirmation after publication must remain uncertain")
            let initialRef = canonicalLedgerTree root bare

            let (statusResult, statusSample), statusTrace =
                withTrace2 (fun () ->
                    sample (fun () ->
                        LedgerStore.getOperationStatus second operationId))

            let statusSnapshot, receipt = unwrap statusResult
            Assert.That(receipt.IsSome, "reconciliation must find the durable operation receipt")
            let taskSnapshot, taskResult, _ = ReviewWorkflow.getTask second taskId |> unwrap
            Assert.That(statusSnapshot.Commit, Is.EqualTo(initialRef.RefOid))
            Assert.That(taskSnapshot.Commit, Is.EqualTo(initialRef.RefOid))
            Assert.That(taskResult.TaskId, Is.EqualTo(taskId))
            Assert.That(taskResult.Title, Is.EqualTo(title))
            let operation = receipt.Value
            Assert.That(Guid.Parse(operation.OperationId), Is.EqualTo(operationId))
            Assert.That(operation.Type, Is.EqualTo("CreateTask"))
            Assert.That(sha256 operation.CanonicalRequestJson, Is.EqualTo(operation.RequestHash).IgnoreCase)
            Assert.That(sha256 operation.OriginalResultJson, Is.EqualTo(operation.OriginalResultHash).IgnoreCase)

            let (replayResult, replayMeasurement), replayTrace =
                withTrace2 (fun () ->
                    LedgerStore.measureMutationStages (fun () ->
                        ReviewWorkflow.createTask first operationId taskId title))

            Assert.That(resultClass replayResult, Is.EqualTo("Ok"))
            let replay = unwrap replayResult
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation, Is.EqualTo(operation))
            let afterReplay = canonicalLedgerTree root bare
            Assert.That(afterReplay.RefOid, Is.EqualTo(initialRef.RefOid),
                        "idempotent replay must not create another durable commit")

            let changedRequestResult = ReviewWorkflow.createTask first operationId taskId "Changed request"
            Assert.That(resultClass changedRequestResult, Is.EqualTo("OperationIdReused"),
                        "reusing an operation id with different content must be rejected")
            let afterConflict = canonicalLedgerTree root bare
            Assert.That(afterConflict.RefOid, Is.EqualTo(afterReplay.RefOid),
                        "idempotency conflict must not mutate the canonical ref")

            let receiptJson = canonicalJson (box receipt.Value)
            writeReport "uncertain-publication" {|
                schemaVersion = 1
                scenario = "uncertain-publication-recovery"
                sourceSha = sourceSha ()
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                operationId = operationId.ToString("D")
                taskId = taskId
                initialResultClass = resultClass initialResult
                initialMutation = initialMeasurement
                initialTrace = traceDto initialTrace
                recoveryResultClass = resultClass statusResult
                recoveryReceiptFound = receipt.IsSome
                recoveryReceiptSha256 = sha256 receiptJson
                recoveryReceiptSemanticSha256 = sha256 (canonicalJson (box {| OperationId = operation.OperationId; Type = operation.Type; Actor = operation.Actor; Sequence = operation.Sequence; Paths = operation.Paths; RequestHash = operation.RequestHash |}))
                receiptAndSnapshotVerified = true
                replayRefUnchanged = true
                conflictRefUnchanged = true
                recoveryRead = sampleDto statusSample
                recoveryTrace = traceDto statusTrace
                recoveredTaskSha256 = sha256 (canonicalJson (box taskResult))
                replayResultClass = resultClass replayResult
                replayMutation = replayMeasurement
                replayTrace = traceDto replayTrace
                changedRequestResultClass = resultClass changedRequestResult
                canonicalRef = afterConflict.RefName
                canonicalRefOid = afterConflict.RefOid
                canonicalTreeOid = afterConflict.TreeOid
                canonicalNormalizedTreeSha256 = afterConflict.NormalizedTreeSha256
            |}
        )

    [<Test>]
    member _.WorktreeDiscoveryRecordsActualGitFanoutAndParserScaling() =
        let parserResults =
            [ 0; 220; 2200 ]
            |> List.map (fun count ->
                let mainPath = Path.Combine(Path.GetTempPath(), "jarvis-parser-main")
                let records =
                    [ 0 .. count - 1 ]
                    |> List.map (fun index ->
                        let path = Path.Combine(Path.GetTempPath(), "jarvis-parser-" + string index)
                        let head = index.ToString("x").PadLeft(40, '0')
                        let branch = if index = 0 then "refs/heads/main" else "refs/heads/perf-" + string index
                        "worktree " + path + "\nHEAD " + head + "\nbranch " + branch)
                let input = String.concat "\n\n" records
                let stopwatch = Stopwatch.StartNew()
                let parsed =
                    match Worktrees.parsePorcelain mainPath input with
                    | Ok entries -> entries
                    | Error error -> Assert.Fail(error); []
                stopwatch.Stop()
                Assert.That(parsed.Length, Is.EqualTo(count))
                let semantic = parsed |> List.map (fun item -> Path.GetRelativePath(Path.GetTempPath(), item.Path), item.Head, item.Branch, item.IsMain, item.IsDetached)
                {| population = count
                   elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds
                   payloadSha256 = sha256 (canonicalJson (box semantic)) |})

        let parent, root = GitCommandTests.createTempProject()
        let context = GitCommandTests.TestContext parent
        let worktreePaths = [ 1 .. 3 ] |> List.map (fun index -> Path.Combine(parent, "linked-" + string index))

        try
            GitCommandTests.expectGitSuccess context [ "init"; "--initial-branch=main" ] |> ignore
            GitCommandTests.expectGitSuccess context [ "config"; "user.name"; "Jarvis Perf Fixture" ] |> ignore
            GitCommandTests.expectGitSuccess context [ "config"; "user.email"; "jarvis-perf-fixture@example.invalid" ] |> ignore
            File.WriteAllText(Path.Combine(root, "README.md"), "worktree fixture\n")
            GitCommandTests.expectGitSuccess context [ "add"; "README.md" ] |> ignore
            GitCommandTests.expectGitSuccess context [ "commit"; "-m"; "worktree fixture" ] |> ignore

            for index, path in List.indexed worktreePaths do
                GitCommandTests.expectGitSuccess context
                    [ "worktree"; "add"; "-b"; "perf/linked-" + string (index + 1); path; "HEAD" ]
                |> ignore

            File.WriteAllText(Path.Combine(worktreePaths[1], "dirty.txt"), "dirty\n")

            let discoveryResult, discoveryTrace =
                withTrace2 (fun () ->
                    Worktrees.listWorktrees { ProjectName = "Project1" } context)

            let entries =
                match discoveryResult with
                | Ok value -> value
                | Error error -> Assert.Fail(sprintf "%A" error); []

            Assert.That(entries.Length, Is.EqualTo(4))
            Assert.That(discoveryTrace.FetchStarts, Is.EqualTo(0))
            Assert.That(discoveryTrace.PushStarts, Is.EqualTo(0))
            let expectedHead = runGit root [ "rev-parse"; "HEAD" ]
            let expectedPaths = root :: worktreePaths |> Set.ofList
            Assert.That(entries |> List.map (fun item -> item.Path) |> Set.ofList, Is.EqualTo(box expectedPaths))
            for item in entries do
                Assert.That(item.Head, Is.EqualTo(expectedHead))
                Assert.That(item.MainPath, Is.EqualTo(root))
                Assert.That(item.IsDetached, Is.False)
                Assert.That(item.IsLocked, Is.False)
                Assert.That(item.IsPrunable, Is.False)
                Assert.That(item.IsMain, Is.EqualTo(item.Path = root))
                let expectedBranch =
                    if item.Path = root then "main"
                    else "perf/" + Path.GetFileName(item.Path)
                Assert.That(item.Branch, Is.EqualTo(Some expectedBranch))
            let identity =
                entries
                |> List.sortBy (fun item -> item.Path)
                |> List.map (fun item ->
                    {| path = Path.GetRelativePath(parent, item.Path)
                       head = "verified-fixture-head"
                       branch = item.Branch
                       isMain = item.IsMain
                       isDetached = item.IsDetached
                       isLocked = item.IsLocked
                       isPrunable = item.IsPrunable |})

            let fullPayload =
                entries
                |> List.sortBy (fun item -> item.Path)
                |> List.map (fun item ->
                    {| path = Path.GetRelativePath(parent, item.Path)
                       head = "verified-fixture-head"
                       branch = item.Branch
                       isMain = item.IsMain
                       isDetached = item.IsDetached
                       isLocked = item.IsLocked
                       isPrunable = item.IsPrunable
                       isClean =
                           match item.IsClean with
                           | Some true -> "clean"
                           | Some false -> "dirty"
                           | None -> "unknown" |})

            writeReport "worktree-discovery" {|
                schemaVersion = 1
                scenario = "worktree-discovery"
                sourceSha = sourceSha ()
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                parserPopulations = parserResults
                actualWorktreeCount = entries.Length
                fixtureHead = expectedHead
                actualHeadVerified = true
                actualIdentitySha256 = sha256 (canonicalJson (box identity))
                actualFullPayloadSha256 = sha256 (canonicalJson (box fullPayload))
                actualTrace = traceDto discoveryTrace
                cleanliness = fullPayload |> List.map (fun item -> {| path = item.path; state = item.isClean |})
            |}
        finally
            for path in worktreePaths do
                try GitCommandTests.expectGitSuccess context [ "worktree"; "remove"; "--force"; path ] |> ignore
                with _ -> ()
            try
                if Directory.Exists(parent) then Directory.Delete(parent, true)
            with _ -> ()

    [<Test>]
    member _.ControlledFileTransportDelayIsObservedByRealGitFetch() =
        if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) then
            Assert.Ignore("The controlled git-upload-pack delay wrapper is POSIX-only.")

        withFixture (fun root _bare _owner reader _first second _baseOid _sourceRef _initTask _fileUrl ->
            let marker = Path.Combine(root, "upload-pack-invocations.txt")
            let execPath = runGit root [ "--exec-path" ]
            let realUploadPack = Path.Combine(execPath, "git-upload-pack")
            let wrapper = Path.Combine(root, "delayed-upload-pack")
            let script =
                "#!/bin/sh\n"
                + "printf 'upload-pack\\n' >> '" + marker + "'\n"
                + "sleep 0.050\n"
                + "exec '" + realUploadPack + "' \"$@\"\n"
            File.WriteAllText(wrapper, script)
            File.SetUnixFileMode(wrapper,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
                ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
            runGit reader [ "config"; "remote.origin.uploadpack"; wrapper ] |> ignore

            let (fetchResult, fetchSample), fetchTrace =
                withTrace2 (fun () ->
                    sample (fun () -> LedgerStore.fetch second))

            unwrap fetchResult |> ignore
            let invocationCount =
                if File.Exists(marker) then File.ReadAllLines(marker).Length else 0
            Assert.That(invocationCount, Is.GreaterThan(0),
                        "file:// fetch must invoke the configured delayed upload-pack helper")
            Assert.That(fetchTrace.FetchStarts, Is.GreaterThan(0))
            Assert.That(fetchTrace.UploadPackStarts, Is.GreaterThan(0))

            writeReport "controlled-transport-delay" {|
                schemaVersion = 1
                scenario = "controlled-file-transport-delay"
                sourceSha = sourceSha ()
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                configuredDelayMilliseconds = 50
                uploadPackWrapperInvocations = invocationCount
                fetch = sampleDto fetchSample
                trace = traceDto fetchTrace
            |}
        )

    [<Test>]
    member _.ActualDelayedFetchCancellationAndPinnedReadRemainIndependent() =
        if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) then
            Assert.Ignore("The controlled git-upload-pack handshake is POSIX-only.")

        withFixture (fun root bare _owner reader _first second _baseOid _sourceRef _initTask _fileUrl ->
            let pinned = LedgerStore.fetch second |> unwrap
            let canonicalBefore = canonicalLedgerTree root bare
            Assert.That(pinned.Commit, Is.EqualTo(canonicalBefore.RefOid))
            let marker = Path.Combine(root, "remote-upload-pack-starts")
            let gate = Path.Combine(root, "release-upload-pack")
            let wrapper = Path.Combine(root, "controlled-upload-pack.sh")
            File.WriteAllText(wrapper,
                "#!/bin/sh\n" +
                "printf x >> '" + marker + "'\n" +
                "while [ ! -f '" + gate + "' ]; do sleep 0.02; done\n" +
                "sleep 1\n" +
                "exec git-upload-pack \"$@\"\n")
            File.SetUnixFileMode(wrapper,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
                ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
            runGit reader [ "config"; "remote.origin.uploadpack"; wrapper ] |> ignore

            let awaitStarts expected =
                let deadline = Stopwatch.StartNew()
                let count () =
                    if File.Exists marker then File.ReadAllText(marker).Length else 0
                while count () < expected && deadline.Elapsed < TimeSpan.FromSeconds(10.) do
                    Thread.Sleep 20
                Assert.That(count (), Is.EqualTo(expected),
                            "Actual remote upload-pack child did not enter the handshake.")
            let release () = File.WriteAllText(gate, "released")

            try
                let delayed =
                    System.Threading.Tasks.Task.Run(fun () ->
                        LedgerStore.measureGitReads (fun () -> LedgerStore.fetch second))
                awaitStarts 1

                let pinnedResult, pinnedMetrics =
                    LedgerStore.measureGitReads (fun () -> LedgerStore.readAt second pinned "agents/T3duZXI.json")
                let content, oid = unwrap pinnedResult
                Assert.That(content.IsSome, Is.True)
                Assert.That(oid.IsSome, Is.True)
                let expectedBlob = runGit root [ "--git-dir"; bare; "rev-parse"; pinned.Commit + ":agents/T3duZXI.json" ]
                Assert.That(oid.Value, Is.EqualTo(expectedBlob))
                Assert.That(pinnedMetrics.RemoteFetches, Is.Zero,
                            "A pinned read must not wait for or trigger another remote fetch.")
                Assert.That(pinnedMetrics.ProcessStarts, Is.LessThanOrEqualTo(4))

                release ()
                let delayedResult, delayedMetrics = delayed.GetAwaiter().GetResult()
                unwrap delayedResult |> ignore
                Assert.That(delayedMetrics.RemoteFetches, Is.EqualTo(1),
                            "The delayed fetch must not be retried.")
                Assert.That(File.ReadAllText(marker).Length, Is.EqualTo(1))

                File.Delete(gate)
                use cancellation = new CancellationTokenSource()
                let stage = "refs/jarvis/fetch/" + (stableGuid "cancelled-fetch-stage").ToString("N")
                let command: BoundedProcess.Command =
                    { Executable = "git"
                      Args = [ "fetch"; "--no-tags"; "--no-write-fetch-head"; "--refmap="
                               "origin"; "refs/heads/jarvis-orchestration:" + stage ]
                      WorkingDirectory = reader
                      Timeout = TimeSpan.FromSeconds(10.)
                      MaxOutputBytes = Some 4096
                      StandardInput = BoundedProcess.ClosedInput
                      ExtraEnvironment = []
                      ConfigureStart = None }
                let cancelledFetch =
                    System.Threading.Tasks.Task.Run(fun () ->
                        BoundedProcess.runWithCancellation cancellation.Token command)
                awaitStarts 2
                cancellation.Cancel()
                let cancelledResult = cancelledFetch.GetAwaiter().GetResult()
                let cancelledClass =
                    match cancelledResult with
                    | Error (ContextError message) when message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ->
                        "Cancelled"
                    | other ->
                        Assert.Fail(sprintf "Remote fetch cancellation was not honored: %A" other)
                        "UnexpectedResult"
                Assert.That(File.ReadAllText(marker).Length, Is.EqualTo(2),
                            "The cancelled transport must not be replayed.")

                use probe = new Process()
                probe.StartInfo.FileName <- "git"
                probe.StartInfo.WorkingDirectory <- reader
                probe.StartInfo.UseShellExecute <- false
                probe.StartInfo.ArgumentList.Add("show-ref")
                probe.StartInfo.ArgumentList.Add("--verify")
                probe.StartInfo.ArgumentList.Add(stage)
                probe.StartInfo.RedirectStandardOutput <- true
                probe.StartInfo.RedirectStandardError <- true
                Assert.That(probe.Start(), Is.True)
                probe.WaitForExit()
                let stageRefCreated = probe.ExitCode = 0
                Assert.That(stageRefCreated, Is.False,
                            "A cancelled remote stage must not become a trusted local ref.")

                release ()
                let resumedResult, resumedMetrics =
                    LedgerStore.measureGitReads (fun () -> LedgerStore.fetch second)
                let resumed = unwrap resumedResult
                Assert.That(resumed.Commit, Is.EqualTo(pinned.Commit))
                Assert.That((canonicalLedgerTree root bare).RefOid, Is.EqualTo(canonicalBefore.RefOid))
                Assert.That(resumedMetrics.RemoteFetches, Is.EqualTo(1))
                let wrapperInvocations = File.ReadAllText(marker).Length
                Assert.That(wrapperInvocations, Is.EqualTo(3),
                            "Recovery must perform one fresh fetch, not replay the cancelled one.")

                writeReport "controlled-transport-cancellation" {|
                    schemaVersion = 1
                    scenario = "controlled-file-transport-delay-and-cancellation"
                    sourceSha = sourceSha ()
                    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                    pinnedSnapshotCommit = pinned.Commit
                    pinnedSnapshotAndBlobVerified = true
                    canonicalRefUnchanged = true
                    pinnedReadResultClass = "Ok"
                    pinnedReadRemoteFetches = pinnedMetrics.RemoteFetches
                    delayedFetchRemoteFetches = delayedMetrics.RemoteFetches
                    cancelledFetchResultClass = cancelledClass
                    cancelledFetchRemoteUploadPackStarts = 1
                    cancelledFetchStageRefCreated = stageRefCreated
                    resumedFetchRemoteFetches = resumedMetrics.RemoteFetches
                    uploadPackWrapperInvocations = wrapperInvocations
                    configuredDelayMilliseconds = 1000
                |}
            finally
                release ()
        )
