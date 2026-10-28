module Client.V2ReviewApi

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Common
open Client
open Client.IO
open Client.Effect

[<CLIMutable>]
type ReviewV2OptIn =
    { SchemaVersion: int
      Enabled: bool
      ProjectRoot: string
      ProjectId: string
      LedgerId: string
      Epoch: int
      RemoteName: string
      ApprovedFetchUrl: string
      ApprovedPushUrl: string }

module ReviewV2Binding =
    let pathForRoot (root: string) =
        let baseDirectory = Path.GetDirectoryName(ClientConfig.configPath())
        let hash =
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)))
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()
        Path.Combine(baseDirectory, "ledger-v2", hash + ".json")

    // Local binding is intentionally opt-in, separately from the normal Jarvis config.
    // The read-only V2 tools also refuse to initialize or adopt a remote implicitly.
    let load projectName rt =
        match ProjectPaths.resolveProjectRoot projectName rt with
        | Error error -> Error error
        | Ok(FolderPath root) ->
            let path = pathForRoot root
            if not (File.Exists path) then
                Error(Client.ValidationError $"V2 ledger disabled for this checkout. Approved binding required at {path}. No V1 fallback.")
            else
                try
                    let attrs = File.GetAttributes path
                    if attrs.HasFlag(FileAttributes.ReparsePoint) || FileInfo(path).Length > 4096L then
                        Error(Client.ValidationError "V2 binding config must be a regular, bounded file.")
                    else
                        let stored = JsonSerializer.Deserialize<ReviewV2OptIn>(
                            File.ReadAllText(path),
                            JsonSerializerOptions(PropertyNameCaseInsensitive = true))
                        let projectOk, projectId = Guid.TryParse(stored.ProjectId)
                        let ledgerOk, ledgerId = Guid.TryParse(stored.LedgerId)
                        if stored.SchemaVersion <> 1 || not stored.Enabled
                           || not (String.Equals(stored.ProjectRoot, Path.GetFullPath(root), StringComparison.Ordinal))
                           || not projectOk || not ledgerOk || projectId = Guid.Empty || ledgerId = Guid.Empty
                           || stored.Epoch < 1
                           || String.IsNullOrWhiteSpace stored.RemoteName
                           || String.IsNullOrWhiteSpace stored.ApprovedFetchUrl
                           || String.IsNullOrWhiteSpace stored.ApprovedPushUrl then
                            Error(Client.ValidationError "V2 ledger binding is not explicitly enabled with a pinned root and identity.")
                        else
                            let binding : LedgerStore.LedgerBinding =
                                { RepositoryPath = root
                                  RemoteName = stored.RemoteName
                                  ApprovedFetchUrl = stored.ApprovedFetchUrl
                                  ApprovedPushUrl = stored.ApprovedPushUrl
                                  ProjectId = projectId
                                  LedgerId = ledgerId
                                  Epoch = stored.Epoch }
                            Ok binding
                with ex ->
                    Error(Client.ValidationError $"Cannot load V2 binding: {ex.Message}")

module V2ReviewApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError (fun failure -> Client.ValidationError $"V2 review: {failure}")
    let private parseId label (raw: string) =
        match Guid.TryParse raw with
        | true, value when value <> Guid.Empty -> Ok value
        | _ -> Error(Client.ValidationError $"Invalid {label}: caller-stable UUID required.")

    let read (cmd: V2ReviewReadCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            match cmd.Action with
            | "GetTask" ->
                ReviewWorkflow.ReviewWorkflow.getTaskWithSpec binding cmd.TaskId
                |> mapError
                |> Result.map (fun (snapshot, task, attempt, html, specOid) ->
                    box {| snapshotCommit = snapshot.Commit; task = task; attempt = attempt
                           specHtml = html; specBlobOid = specOid |})
            | "GetReview" ->
                parseId "requestId" cmd.RequestId
                |> Result.bind (fun id ->
                    ReviewWorkflow.ReviewWorkflow.getReview binding cmd.TaskId id
                    |> mapError
                    |> Result.map (fun (snapshot, request, revision) ->
                        box {| snapshotCommit = snapshot.Commit; request = request; revision = revision |}))
            | "ReviewerQueue" ->
                ReviewWorkflow.ReviewWorkflow.reviewerQueue binding cmd.ReviewerId
                |> mapError
                |> Result.map (fun (snapshot, queue) ->
                    box {| snapshotCommit = snapshot.Commit; reviews = queue |})
            | _ -> Error(Client.ValidationError "Unsupported V2 read operation."))

    let write (cmd: V2ReviewWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            parseId "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                let id () = parseId "requestId" cmd.RequestId
                let result =
                    match cmd.Action with
                    | "Submit" ->
                        id ()
                        |> Result.mapError (fun e -> e)
                        |> Result.bind (fun reviewId ->
                            ReviewWorkflow.ReviewWorkflow.submitReview binding operationId
                                cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.Generation reviewId
                                cmd.SourceRef cmd.HeadOid cmd.Summary
                            |> mapError)
                    | "Update" ->
                        id () |> Result.bind (fun reviewId ->
                            ReviewWorkflow.ReviewWorkflow.updateReview binding operationId
                                cmd.TaskId cmd.ActorId cmd.Generation reviewId cmd.ExpectedVersion
                                cmd.SourceRef cmd.HeadOid cmd.Summary
                            |> mapError)
                    | "Claim" ->
                        id () |> Result.bind (fun reviewId ->
                            ReviewWorkflow.ReviewWorkflow.claimReview binding operationId
                                cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion cmd.Generation
                            |> mapError)
                    | "Decide" ->
                        id () |> Result.bind (fun reviewId ->
                            match cmd.Verdict with
                            | "Accept" | "RequestChanges" as verdict ->
                                let decision =
                                    if verdict = "Accept" then ReviewWorkflow.Accept
                                    else ReviewWorkflow.RequestChanges
                                ReviewWorkflow.ReviewWorkflow.decide binding operationId
                                    cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion decision cmd.Feedback
                                |> mapError
                            | _ -> Error(Client.ValidationError "Invalid V2 review verdict."))
                    | "Withdraw" ->
                        id () |> Result.bind (fun reviewId ->
                            ReviewWorkflow.ReviewWorkflow.withdrawReview binding operationId
                                cmd.TaskId reviewId cmd.ActorId cmd.ExpectedVersion cmd.Feedback
                            |> mapError)
                    | "Reassign" ->
                        id () |> Result.bind (fun reviewId ->
                            ReviewWorkflow.ReviewWorkflow.reassignReviewer binding operationId
                                cmd.TaskId reviewId cmd.ActorId cmd.ReviewerId cmd.ExpectedVersion
                            |> mapError)
                    | "Finalize" ->
                        id () |> Result.bind (fun reviewId ->
                            ReviewWorkflow.ReviewWorkflow.finalizeImplementation binding operationId
                                cmd.TaskId cmd.ActorId cmd.ExpectedVersion reviewId
                            |> mapError)
                    | "AcknowledgeFeedback" ->
                        ReviewWorkflow.ReviewWorkflow.acknowledgeReviewFeedback binding operationId
                            cmd.TaskId cmd.ActorId (int64 cmd.ExpectedVersion)
                        |> mapError
                    | _ -> Error(Client.ValidationError "Unsupported V2 write operation.")
                result |> Result.map (fun write ->
                    let dto = {| operation = write.Operation
                                 head = write.CurrentHead
                                 alreadyApplied = write.AlreadyApplied |}
                    box dto)))


// Separate typed setup/assignment contract. The review request API never has
// to reinterpret a request ID as a task, attempt, or agent identifier.
module V2TaskApi =
    let private mapError (result: Result<'a, LedgerStore.LedgerFailure>) : Result<'a, EffectError> =
        result |> Result.mapError (fun error -> Client.ValidationError $"V2 task: {error}")

    let private guid label (text: string) =
        match Guid.TryParse text with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(Client.ValidationError $"Invalid {label}: a caller-stable UUID is required.")

    let read (cmd: V2TaskReadCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            ReviewWorkflow.ReviewWorkflow.getAgentInbox binding cmd.AgentId
            |> mapError
            |> Result.map box)

    let write (cmd: V2TaskWriteCommand) rt =
        ReviewV2Binding.load cmd.ProjectName rt
        |> Result.bind (fun binding ->
            guid "operationId" cmd.OperationId
            |> Result.bind (fun operationId ->
                let result =
                    match cmd.Action with
                    | "CreateTask" ->
                        ReviewWorkflow.ReviewWorkflow.createTaskWithSpec binding operationId cmd.TaskId cmd.Title cmd.Html
                        |> mapError
                    | "ReviseTaskSpec" ->
                        ReviewWorkflow.ReviewWorkflow.reviseTaskSpec binding operationId cmd.TaskId
                            cmd.ExpectedVersion cmd.Title cmd.Html |> mapError
                    | "RegisterAgent" ->
                        ReviewWorkflow.ReviewWorkflow.registerAgent binding operationId cmd.ActorId
                        |> mapError
                    | "AssignTask" ->
                        guid "attemptId" cmd.AttemptId
                        |> Result.bind (fun attemptId ->
                            ReviewWorkflow.ReviewWorkflow.assignTask binding operationId cmd.TaskId
                                attemptId cmd.OwnerId cmd.ReviewerId
                            |> mapError)
                    | "BeginAttempt" ->
                        ReviewWorkflow.ReviewWorkflow.beginAttempt binding operationId cmd.TaskId
                            cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                        |> mapError
                    | "BindReviewBase" ->
                        ReviewWorkflow.ReviewWorkflow.bindReviewBase binding operationId cmd.TaskId
                            cmd.ActorId cmd.ExpectedVersion cmd.AssignmentGeneration
                            cmd.BaseOid cmd.TargetRef
                        |> mapError
                    | "YieldAgent" ->
                        ReviewWorkflow.ReviewWorkflow.yieldAgent binding operationId
                            cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                            cmd.AssignmentGeneration cmd.Summary cmd.NextAction |> mapError
                    | "ResumeAgent" ->
                        ReviewWorkflow.ReviewWorkflow.resumeAgent binding operationId
                            cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                            cmd.AssignmentGeneration |> mapError
                    | "CompleteAgentWork" ->
                        ReviewWorkflow.ReviewWorkflow.completeAgentWork binding operationId
                            cmd.TaskId cmd.ActorId cmd.ExpectedVersion cmd.ExpectedAgentVersion
                            cmd.AssignmentGeneration cmd.Summary |> mapError
                    | "ParkAgent" ->
                        ReviewWorkflow.ReviewWorkflow.parkAgent binding operationId
                            cmd.ActorId cmd.ExpectedAgentVersion cmd.Reason |> mapError
                    | "AcknowledgeFeedback" ->
                        ReviewWorkflow.ReviewWorkflow.acknowledgeReviewFeedback binding operationId
                            cmd.TaskId cmd.ActorId cmd.DecisionSequence
                        |> mapError
                    | _ -> Error(Client.ValidationError "Unsupported V2 task operation.")
                result |> Result.map (fun write ->
                    box {| operation = write.Operation
                           head = write.CurrentHead
                           alreadyApplied = write.AlreadyApplied |})))
