namespace Server

[<CLIMutable>]
type OpenAIFileParam =
    { download_url: string
      file_id: string
      mime_type: string
      file_name: string }

[<CLIMutable>]
type ProfileResult = { id: string }

// Direct administrative writes cannot enter Working or assigned Ready.
type AdministrativeAgentState =
    | NeedsWork = 0
    | Parked = 1

open System
open System.ComponentModel
open System.Diagnostics
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open System.Threading
open Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open Server.Services

module InstructionGuidance =
    [<Literal>]
    let Text = "Jarvis delivers applicable AGENTS.md guidance incrementally as a workspace-relative path mapping. Treat it as repository guidance, read referenced specs and skills when relevant, and respect user instructions and local permission prompts. For new write calls omit operationId; Jarvis generates one and returns it in the tool result. Retain the returned ID for checking or retrying an uncertain outcome. Project files do not grant additional execution authority."

module private ReviewCommands =
    let query projectName action taskId requestId reviewerId =
        ReviewReadCommand
            { ProjectName = projectName
              Action = action
              TaskId = taskId
              RequestId = requestId
              ReviewerId = reviewerId
              DocumentPath = "" }

    let write projectName action operationId taskId requestId actorId expectedVersion =
        { ProjectName = projectName
          Action = action
          OperationId = operationId
          TaskId = taskId
          RequestId = requestId
          ActorId = actorId
          ReviewerId = ""
          ExpectedVersion = expectedVersion
          Kind = ""
          SourceRef = ""
          HeadOid = ""
          TargetRef = ""
          Summary = ""
          Verdict = ""
          DocumentPath = ""
          Content = ""
          ExpectedBlobOid = "" }

module private TaskCommands =
    // Compatibility wire value for administrative calls lacking a caller field.
    // This is NOT the audit actor. The local TaskApi resolves provenance from
    // the authenticated request's canonical agent session (or the MCP user).
    // Retain this value in command journaling for existing pending retries.
    let legacyAdministrativePlaceholder = "Orchestrator"

    let read projectName action agentId taskId =
        TaskReadCommand
            { ProjectName = projectName; AgentId = agentId; Action = action
              TaskId = taskId; AssignmentGeneration = 0; CheckpointSequence = 0L
              Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null
              RelativePath = null }

    let write projectName action operationId taskId actorId =
        { ProjectName = projectName
          Action = action
          OperationId = operationId
          TaskId = taskId
          AttemptId = ""
          ActorId = actorId
          OwnerId = ""
          Title = ""
          Html = ""
          ExpectedVersion = 0
          ExpectedAgentVersion = 0
          ExpectedTaskVersion = 0
          AssignmentGeneration = 0
          Summary = ""
          NextAction = ""
          Reason = ""
          BaseOid = ""
          TargetRef = ""
          SourceRef = ""
          AllowDetached = false
          AllowUncommitted = false
          DecisionSequence = 0L
          Evidence = ""
          SessionId = "" }

module RequestDeviceRouting =
    // Per-invocation context: never use shared defaults to represent a ChatGPT conversation.
    let private currentSession = AsyncLocal<string option>()
    let private currentDeviceOverride = AsyncLocal<string option>()

    let private valueFrom (meta: JsonObject) (name: string) =
        if isNull meta then None
        else
            match meta[name] with
            | value when not (isNull value) ->
                try
                    let text = value.GetValue<string>()
                    if String.IsNullOrWhiteSpace(text) || text.Length > 256 then None else Some text
                with _ -> None
            | _ -> None

    let private currentAgentId = AsyncLocal<string option>()
    let private currentInvalidAgentId = AsyncLocal<bool>()
    let agentId () = currentAgentId.Value
    let invalidAgentId () = currentInvalidAgentId.Value

    let private agentArgument (request: CallToolRequestParams) =
        if isNull request.Arguments then None
        else
            let extract name =
                match request.Arguments.TryGetValue(name) with
                | true, value when value.ValueKind = JsonValueKind.String ->
                    let identifier = value.GetString()
                    if String.IsNullOrWhiteSpace identifier || identifier.Length > 256 then None
                    else Some identifier
                | _ -> None
            extract "agentId" |> Option.orElseWith (fun () -> extract "fromAgentId")

    let session () = currentSession.Value
    let deviceOverride () = currentDeviceOverride.Value
    let private invalidDeviceId = String.replicate 257 "x"


    let private deviceArgument (request: CallToolRequestParams) =
        match request.Arguments with
        | null -> None
        | arguments ->
            match arguments.TryGetValue("deviceId") with
            | true, value when value.ValueKind = JsonValueKind.String ->
                let text = value.GetString()
                if String.IsNullOrWhiteSpace(text) then None
                elif text.Length > 256 then Some invalidDeviceId
                else Some text
            | true, _ -> Some invalidDeviceId
            | false, _ -> None

    let private metadataDevice (meta: JsonObject) =
        if isNull meta || not (meta.ContainsKey "jarvis/deviceId") then None
        else valueFrom meta "jarvis/deviceId" |> Option.orElse (Some invalidDeviceId)

    let withMetadata (request: CallToolRequestParams) (operation: unit -> Task<'a>) =
        task {
            let oldSession, oldDevice, oldAgent, oldInvalid =
                currentSession.Value, currentDeviceOverride.Value, currentAgentId.Value, currentInvalidAgentId.Value
            currentSession.Value <- valueFrom request.Meta "openai/session"
            currentAgentId.Value <- agentArgument request
            let hasAgentField =
                not (isNull request.Arguments)
                && (request.Arguments.ContainsKey("agentId") || request.Arguments.ContainsKey("fromAgentId"))
            currentInvalidAgentId.Value <- hasAgentField && currentAgentId.Value.IsNone
            currentDeviceOverride.Value <- deviceArgument request |> Option.orElseWith (fun () -> metadataDevice request.Meta)
            try return! operation()
            finally
                currentSession.Value <- oldSession
                currentDeviceOverride.Value <- oldDevice
                currentAgentId.Value <- oldAgent
                currentInvalidAgentId.Value <- oldInvalid
        }

module McpToolHelpers =
    /// Convert an optional explicit checkout selector into the registered-worktree
    /// project name understood by the existing client path and Git validation.
    /// The ledger API deliberately does not use this helper.
    let projectWithWorktreeName (projectName: string) (worktreeName: string) =
        if String.IsNullOrEmpty worktreeName then projectName
        elif String.IsNullOrWhiteSpace worktreeName then
            invalidArg "worktreeName" "Expected a nonblank registered worktree directory name."
        elif String.IsNullOrWhiteSpace projectName
             || projectName.Contains("::", StringComparison.Ordinal) then
            invalidArg "worktreeName" "Pass a plain projectName when worktreeName is provided."
        elif worktreeName = "." || worktreeName = ".."
             || worktreeName.Contains("..", StringComparison.Ordinal)
             || worktreeName.IndexOfAny([| '/'; char 92 |]) >= 0
             || (worktreeName |> Seq.exists Char.IsControl) then
            invalidArg "worktreeName" "Expected a registered worktree directory name, not a path."
        else
            projectName + "::" + worktreeName

    let private formatError error =
        match error with
        | NotFound message -> $"NotFound: {message}"
        | PermissionDenied message -> $"PermissionDenied: {message}"
        | ConfirmationRequired request -> $"ConfirmationRequired: {request.Summary}"
        | ValidationFailed message -> $"ValidationFailed: {message}"
        | Conflict message -> $"Conflict: {message}"
        | ExecutionFailed message -> $"ExecutionFailed: {message}"
        | OutputTruncated message -> $"OutputTruncated: {message}"
        | LedgerFailure failure -> $"LedgerFailure({failure.Code}): {failure.Message}"

    let private textContent text =
        TextContentBlock(Text = text) :> ContentBlock

    let private textResult text isError =
        let result = CallToolResult()
        result.Content <- ResizeArray<ContentBlock>([ textContent text ])
        result.IsError <- Nullable isError
        result

    let private structuredResult structuredContent text isError =
        let result = textResult text isError
        result.StructuredContent <- Nullable structuredContent
        result

    let private errorResult error =
        textResult (formatError error) true

    let attachInstructions (documents: InstructionDocument list) (result: CallToolResult) =
        if isNull (box documents) || documents.IsEmpty then result
        else
            let contents = ResizeArray<ContentBlock>()
            let repository = JsonObject()
            for document in documents do
                if document.Scope <> "working-turn"
                   && document.Scope <> "write-warning"
                   && document.Scope <> "instruction-warning" then
                    let path =
                        if document.Path = "AGENTS.md" then "."
                        elif document.Path.EndsWith("/AGENTS.md", StringComparison.Ordinal) then
                            document.Path.Substring(0, document.Path.Length - "/AGENTS.md".Length)
                        else document.Path
                    repository[path] <- JsonValue.Create(document.Content)
            if repository.Count > 0 then
                let guidance = JsonObject()
                guidance["AGENTS.md"] <- repository
                contents.Add(textContent (guidance.ToJsonString()))
            for document in documents do
                if document.Scope = "working-turn" then
                    contents.Add(textContent ("Jarvis Working turn instruction:\n" + document.Content))
                elif document.Scope = "write-warning" || document.Scope = "instruction-warning" then
                    contents.Add(textContent ("Jarvis instruction notice:\n" + document.Content))
            for block in result.Content do
                contents.Add(block)
            result.Content <- contents
            result

    let private jsonResult value =
        JsonSerializer.Serialize(value)
        |> fun serialized -> textResult serialized false

    let private serverLocalCapability name description permissions mutatesState : CommandCapability =
        { Name = name
          Description = description
          Permissions = permissions
          MutatesState = mutatesState
          RequiresConfirmation = false
          SupportsDryRun = false
          MaxInputBytes = None
          MaxOutputBytes = Some AgentProtocol.maxResponseBytes
          InputSchemaJson = None
          OutputSchemaJson = None }

    let private ledgerTaskReadMethods = [ "GetIntegrationSubmissions"; "AgentInbox"; "GetAssignment"; "GetCheckpoint";  "InspectAttemptSource"; "TaskLifecycleHistory"; "GetTaskSpecPage"; "DiffTaskSpec";
                                         "GetRole"; "ListRoles"; "GetAgentInstructionConfig"; "ResolveAgentInstructions" ]
    let private ledgerTaskWriteMethods =
        [ "SubmitIntegration"; "CompleteIntegration"; "NotifyAgent"; "AcknowledgeNotification"; "CreateTask"; "ReviseTaskSpec"; "RegisterAgent"; "AssignTask"; "ReissueAssignment"; "CheckpointAttempt"
          "SetAttemptSource"; "YieldAgent"; "CompleteAgentWork"
          "SetAgentState"; "CorrectAgentWorkingState"; "ReissueQueuedAttempt"; "RecoverAgentAssignment"; "AcknowledgeReviewFeedback"
          "DeferAttempt"; "ResumeAttempt"; "AbandonAttempt"; "CancelTask"
          "CreateRole"; "UpdateRole"; "AttachRole"; "DetachRole"; "SetAgentInstructions"
          "TransferOwner"; "CloseTask"; "ReopenTask"; "ImportTaskSpec"; "ExportTaskSpec" ]

    let private ledgerReadMethods =
        [ "LedgerHealth"; "GetOperationStatus"; "PendingOperations"; "ReconcilePendingOperations"; "PendingOperationSummary"; "PendingOperationsPage"; "InspectPendingOperation" ]
    let private ledgerWriteMethods = [ "InitializeLedger"; "BindExistingLedger"; "RebindLedgerRemote"; "AcknowledgePendingOperation" ]

    let private ledgerReviewMethods =
        [ "GetTask"; "TaskOverview"; "ReviewDetails"; "ReviewerInbox"; "ListReviewDocuments"; "ReadReviewDocument" ]

    let private ledgerReviewWriteMethods =
        [ "SubmitReview"; "ClaimReview"; "DecideReview"; "ReleaseReview"; "WithdrawReview"
          "PutReviewDocument" ]

    let private ledgerAliasCapabilities =
        let fromWire wireName publicName =
            AgentProtocol.capabilities
            |> List.find (fun capability -> capability.Name = wireName)
            |> fun capability -> { capability with Name = publicName }

        [ for name in ledgerTaskReadMethods ->
              if name = "AgentInbox" then
                  { fromWire "TaskRead" name with
                      Description = "Self-inbox: an agent's call may activate its Ready assignment into Working. Use ObserveAgent to inspect another agent without activity."
                      MutatesState = true }
              else fromWire "TaskRead" name
          for name in ledgerTaskWriteMethods -> fromWire "TaskWrite" name
          for name in ledgerReadMethods -> fromWire "LedgerRead" name
          for name in ledgerWriteMethods -> fromWire "LedgerWrite" name
          for name in ledgerReviewMethods -> fromWire "ReviewRead" name
          for name in ledgerReviewWriteMethods -> fromWire "ReviewWrite" name ]

    let mcpListCommandsResult : ListCommandsResult =
        { ProtocolVersion = AgentProtocol.version
          Commands =
            (AgentProtocol.capabilities
             |> List.filter (fun capability ->
                 capability.Name <> "ReviewRead" && capability.Name <> "ReviewWrite"
                 && capability.Name <> "TaskRead" && capability.Name <> "TaskWrite"
                 && capability.Name <> "LedgerRead" && capability.Name <> "LedgerWrite"))
            @ ledgerAliasCapabilities
            @ [ serverLocalCapability
                    "GetProfile"
                    "Returns the profile represented by the authenticated OAuth credentials."
                    [ ReadOnly ]
                    false
                serverLocalCapability
                    "Feedback"
                    "Persists explicit structured agent feedback on the Jarvis server."
                    []
                    true
                serverLocalCapability "ListFeedback" "Lists persisted agent feedback." [ ReadOnly ] false
                serverLocalCapability "GetFeedback" "Reads one feedback item." [ ReadOnly ] false
                serverLocalCapability "UpdateFeedback" "Updates feedback resolution state." [] true
                serverLocalCapability
                    "GetFeedbackSummary"
                    "Summarizes persisted agent feedback."
                    [ ReadOnly ]
                    false
                serverLocalCapability "UpdateServer" "Schedules a verified systemd-managed server update with rollback." [ ProcessExecution; NetworkAccess; Destructive ] true
                serverLocalCapability "GetServerUpdateStatus" "Reads the server update status file." [ ReadOnly ] false
                serverLocalCapability "ListDevices" "Lists registered Jarvis devices and the selected default." [ ReadOnly ] false
                serverLocalCapability "SelectDevice" "Selects the default Jarvis command destination." [] true
                serverLocalCapability
                    "GetConnectionDiagnostics"
                    "Returns the current Jarvis client session and recent persisted connection lifecycle events."
                    [ ReadOnly ]
                    false
                serverLocalCapability
                    "GetAgentCorrectionEvidence"
                    "Read-only authenticated correction recovery evidence: protocol gate, unresolved permit and bounded execution witnesses; never authorizes a correction."
                    [ ReadOnly ]
                    false ] }

    let private errorCategory = function
        | NotFound _ -> "NotFound"
        | PermissionDenied _ -> "PermissionDenied"
        | ConfirmationRequired _ -> "ConfirmationRequired"
        | ValidationFailed _ -> "ValidationFailed"
        | Conflict _ -> "Conflict"
        | ExecutionFailed _ -> "ExecutionFailed"
        | OutputTruncated _ -> "OutputTruncated"
        | LedgerFailure _ -> "LedgerFailure"

    let toCallToolResult (response: AgentCommandResponse) =
        let result =
            match response.Result, response.Error with
            | Some serialized, None ->
                try
                    use _ = JsonDocument.Parse(serialized)
                    textResult serialized false
                with ex ->
                    errorResult (ExecutionFailed $"Invalid serialized Jarvis result: {ex.Message}")
            | None, Some error ->
                errorResult error
            | Some _, Some error ->
                errorResult (ExecutionFailed $"Invalid Jarvis response: both result and error were set. {formatError error}")
            | None, None ->
                errorResult (ExecutionFailed "Invalid Jarvis response: neither result nor error was set.")
        result

    /// Every forwarded project tool result carries its canonical mutation
    /// evidence. An empty array is explicit, not an omitted field. This stays
    /// visible even when the requested command fails after a committed write.
    /// The separate activation receipt is richer only when the ledger has
    /// actually confirmed the requesting agent's Ready -> Working transition.
    let attachStateEffects (response: AgentCommandResponse) (result: CallToolResult) =
        if response.PendingJob.IsSome then result
        else
            let changes =
                if isNull (box response.AgentStateChanges) then [] else response.AgentStateChanges
            let details = JsonObject()
            details["agentStateChanges"] <- JsonSerializer.SerializeToNode changes
            let metadata = if isNull result.Meta then JsonObject() else result.Meta
            metadata["jarvis/agent_state_changes"] <- JsonSerializer.SerializeToNode changes
            match response.InboxActivation with
            | None -> ()
            | Some activation ->
                details["agentInboxActivation"] <- JsonSerializer.SerializeToNode activation
                metadata["jarvis/inbox_activation"] <- JsonSerializer.SerializeToNode activation
            result.Meta <- metadata
            if isNull result.Content then result.Content <- ResizeArray<ContentBlock>()
            result.Content.Add(textContent (details.ToJsonString()))
            result

    /// Preliminary and final dispatches may independently mutate canonical
    /// state (notably a cross-project notification sender validation). Never
    /// lose the preliminary mutation from the caller-visible result.
    let mergeStateEffects (prior: AgentCommandResponse) (final: AgentCommandResponse) =
        // Unknown final effects are NOT an empty list. A promoted destination
        // must carry its source evidence into the client-owned job, so that
        // polling eventually returns both confirmed sets of mutations.
        let changes =
            if final.PendingJob.IsSome || isNull (box final.AgentStateChanges) then
                Unchecked.defaultof<AgentStateChange list>
            else
                let source =
                    if isNull (box prior.AgentStateChanges) then [] else prior.AgentStateChanges
                (source @ final.AgentStateChanges) |> List.distinct
        { final with
            AgentStateChanges = changes
            InboxActivation =
                if final.PendingJob.IsSome then None
                else
                    match final.InboxActivation with
                    | Some _ -> final.InboxActivation
                    | None ->
                        prior.InboxActivation
                        |> Option.filter (fun receipt -> receipt.ActivatedByThisCall) }

    /// A polled tool job carries the exact original response as typed data.
    /// Present its instructions and canonical side effects with the polling
    /// envelope, rather than substituting invented empty metadata.
    let completedToolEvidence command (response: AgentCommandResponse) =
        match command, response.Result with
        | GetJobResultCommand _, Some payload ->
            try
                let job = JsonSerializer.Deserialize<JobResult>(payload)
                if isNull (box job) then response
                else job.ToolResponse |> Option.defaultValue response
            with _ -> response
        | _ -> response

    let private imageResult (response: AgentCommandResponse) =
        match response.Result, response.Error with
        | Some serialized, None ->
            try
                let image = JsonSerializer.Deserialize<ReadImageResult>(serialized)
                let result = CallToolResult()
                result.Content <-
                    ResizeArray<ContentBlock>(
                        [ TextContentBlock(Text = image.FilePath) :> ContentBlock
                          ImageContentBlock.FromBytes(image.Data, image.MimeType) :> ContentBlock ]
                    )
                result.IsError <- Nullable false
                result
            with ex ->
                errorResult (ExecutionFailed $"Invalid serialized Jarvis image result: {ex.Message}")
        | None, Some error -> errorResult error
        | Some _, Some error ->
            errorResult (ExecutionFailed $"Invalid Jarvis response: both result and error were set. {formatError error}")
        | None, None ->
            errorResult (ExecutionFailed "Invalid Jarvis response: neither result nor error was set.")

    let private exportResult (context: HttpContext) (response: AgentCommandResponse) =
        match response.Result, response.Error with
        | Some serialized, None ->
            try
                let payload = JsonSerializer.Deserialize<ExportFilePayload>(serialized)
                if isNull payload.Data then
                    errorResult (ExecutionFailed "Invalid serialized Jarvis export result: data was null.")
                elif payload.Data.LongLength > int64 AgentProtocol.maxExportBytes then
                    errorResult (OutputTruncated $"Export payload exceeded the {AgentProtocol.maxExportBytes} byte limit.")
                else
                    let store = context.RequestServices.GetRequiredService<ExportStore>()
                    let ownerId = Auth.tryUserId context.User |> Option.defaultValue ""
                    let created = store.Add(payload.FilePath, payload.Data, ownerId = ownerId)
                    let downloadUrl =
                        $"{context.Request.Scheme}://{context.Request.Host}/exports/{created.Token}"
                    { FilePath = payload.FilePath
                      FileName = created.FileName
                      MimeType = created.MimeType
                      Bytes = created.Bytes
                      DownloadUrl = downloadUrl
                      ExpiresAt = created.ExpiresAt }
                    |> jsonResult
            with ex ->
                errorResult (ExecutionFailed $"Invalid serialized Jarvis export result: {ex.Message}")
        | None, Some error -> errorResult error
        | Some _, Some error ->
            errorResult (ExecutionFailed $"Invalid Jarvis response: both result and error were set. {formatError error}")
        | None, None ->
            errorResult (ExecutionFailed "Invalid Jarvis response: neither result nor error was set.")

    let private requiredScopes command =
        match command with
        | ListCommandsCommand
        | ListProjectsCommand
        | BrowserListTabsCommand
        | BrowserListWindowsCommand
        | ListScheduledBrowserExecutionsCommand
        | ListWorktreesCommand _
        | GetWorktreeCommand _
        | InspectWorktreeRemovalCommand _
        | TaskReadCommand _
        | ObserveAgentCommand _
        | LedgerReadCommand _
        | ReviewReadCommand _
        | GetProjectDetailsCommand _
        | ListDirectoryCommand _
        | SearchFilesCommand _
        | SearchTextCommand _
        | ReadFileCommand _
        | ReadFilePageCommand _
        | ReadFilesCommand _
        | GetAgentInstructionsCommand _
        | ReadImageCommand _
        | ExportFileCommand _
        | ListConfiguredTasksCommand _
        | ListSkillsCommand _
        | GetSkillCommand _
        | GetGitStatusCommand _
        | GetGitDiffCommand _
        | ListJobsCommand _
        | GetJobResultCommand _
        | MessageCommand _
        | GetClientActivityCommand _ -> [ Auth.WorkspaceRead ]
        | ImportFileCommand _
        | WriteFileCommand _
        | CreateSkillCommand _
        | PatchFileCommand _ -> [ Auth.WorkspaceWrite ]
        | UpdateClientCommand -> [ Auth.ProcessExecute ]
        | ScheduleBrowserExecutionCommand _
        | CancelScheduledBrowserExecutionCommand _
        | BrowserOpenTabCommand _
        | BrowserCloseTabCommand _
        | BrowserRunInNewTabCommand _
        | BrowserExecuteFileCommand _
        | BrowserExecuteJsCommand _
        | BrowserCaptureDomCommand _
        | RunCommandCommand _
        | RunProjectTaskCommand _
        | StartJobCommand _
        | CancelJobCommand _ -> [ Auth.ProcessExecute ]
        | GitCommitCommand _
        | CreateWorktreeCommand _
        | CreateProjectCommand _
        | RemoveWorktreeCommand _
        | TaskWriteCommand _
        | CrossProjectNotificationCommand _
        | LedgerWriteCommand _
        | ReviewWriteCommand _
        | RetryPendingIntentCommand _
        | SupersedePendingIntentCommand _
        | AbandonPendingIntentCommand _ -> [ Auth.GitWrite ]

        // File export both executes browser code and writes into the workspace.
        // Check *both* OAuth scopes before any client command is dispatched.
        | BrowserExportDomCommand _ -> [ Auth.WorkspaceWrite; Auth.ProcessExecute ]

    let private oauthErrorResult (context: HttpContext) errorCode description scope =
        let response = errorResult (PermissionDenied description)
        let options = context.RequestServices.GetRequiredService<IOptions<JarvisOptions>>().Value
        let quote = Char.ToString(char 34)
        let scopePart =
            match scope with
            | Some value -> ", scope=" + quote + value + quote
            | None -> ""

        let challenge =
            "Bearer resource_metadata=" + quote + Auth.resourceMetadataUri options + quote
            + ", error=" + quote + errorCode + quote
            + ", error_description=" + quote + description + quote
            + scopePart

        let challenges = JsonArray()
        challenges.Add(JsonValue.Create(challenge))

        let meta = JsonObject()
        meta["mcp/www_authenticate"] <- challenges
        response.Meta <- meta
        response

    let private authorizationErrorResult (context: HttpContext) scope =
        oauthErrorResult
            context
            "insufficient_scope"
            $"Missing required OAuth scope: {scope}"
            (Some scope)

    let private authenticationErrorResult (context: HttpContext) scope =
        oauthErrorResult context "invalid_token" "Authentication required." scope

    let private toolName command =
        AgentCommandInfo.protocolName command

    let private serverVersion =
        match typeof<JarvisOptions>.Assembly.GetName().Version with
        | null -> "unknown"
        | version -> version.ToString()

    let private beginOperation (context: HttpContext) userId command =
        try
            let store = context.RequestServices.GetRequiredService<FeedbackStore>()
            let users = context.RequestServices.GetRequiredService<UserService>()
            let clientVersion, protocolVersion =
                match users.GetSelectedSession(userId, RequestDeviceRouting.session(), RequestDeviceRouting.deviceOverride()) with
                | ValueSome session -> Some session.ClientVersion, Some session.ProtocolVersion
                | ValueNone -> None, None

            let operation =
                store.BeginOperation(
                    userId,
                    toolName command,
                    AgentCommandInfo.projectName command,
                    serverVersion,
                    clientVersion,
                    protocolVersion
                )

            Some(store, operation)
        with _ ->
            None

    let private finishOperation tracking response elapsedMs =
        match tracking with
        | None -> ()
        | Some(store: FeedbackStore, operation: OperationStart) ->
            try
                let success = response.Error.IsNone && response.Result.IsSome
                let category = response.Error |> Option.map errorCategory
                store.CompleteOperation(operation.Id, success, category, elapsedMs)
            with _ ->
                ()

    let private attachOperationId tracking (result: CallToolResult) =
        match tracking with
        | None -> result
        | Some(_, operation: OperationStart) ->
            let meta =
                if isNull result.Meta then JsonObject()
                else result.Meta

            meta["jarvis/operation_id"] <- JsonValue.Create(operation.Id)
            result.Meta <- meta
            result

    // Agents can omit an operation UUID on new writes. Explicit caller IDs
    // are retained unchanged to support retry of an uncertain outcome.
    let resolveOperationId (id: string) =
        if String.IsNullOrWhiteSpace id then Guid.NewGuid().ToString("D"), true
        else id, false

    // Reuse the operation identity for generated task IDs, so an uncertain
    // retry with the same operationId targets exactly the same task.
    let resolveTaskId (taskId: string) (operationId: string) =
        if not (String.IsNullOrWhiteSpace taskId) then taskId, false
        else
            match Guid.TryParse operationId with
            | true, parsed when parsed <> Guid.Empty -> "task-" + parsed.ToString("N"), true
            | _ -> taskId, false // Invalid operation IDs are rejected by the client.

    let private ensureOperationId command =
        match command with
        | TaskWriteCommand cmd ->
            let id, generated = resolveOperationId cmd.OperationId
            TaskWriteCommand { cmd with OperationId = id }, (if generated then Some id else None)
        | ReviewWriteCommand cmd ->
            let id, generated = resolveOperationId cmd.OperationId
            ReviewWriteCommand { cmd with OperationId = id }, (if generated then Some id else None)
        | LedgerWriteCommand cmd when cmd.Action <> "AcknowledgePendingOperation" ->
            let id, generated = resolveOperationId cmd.OperationId
            LedgerWriteCommand { cmd with OperationId = id }, (if generated then Some id else None)
        | _ -> command, None

    /// Include the generated ID in an ordinary text content block as well as
    /// metadata: agents must be able to copy it for status checks or retries.
    let attachGeneratedOperationId (generated: string option) (result: CallToolResult) =
        match generated with
        | None -> result
        | Some id ->
            let meta = if isNull result.Meta then JsonObject() else result.Meta
            meta["jarvis/generated_operation_id"] <- JsonValue.Create(id)
            result.Meta <- meta
            if isNull result.Content then
                result.Content <- ResizeArray<ContentBlock>()
            result.Content.Add(
                textContent ($"Jarvis operationId: {id} (server-generated; use this exact ID for status checks or retries if the outcome is uncertain)."))
            result

    let attachGeneratedTaskId (generated: string option) (result: CallToolResult) =
        match generated with
        | None -> result
        | Some taskId ->
            let meta = if isNull result.Meta then JsonObject() else result.Meta
            meta["jarvis/generated_task_id"] <- JsonValue.Create(taskId)
            result.Meta <- meta
            if isNull result.Content then
                result.Content <- ResizeArray<ContentBlock>()
            result.Content.Add(textContent $"Jarvis taskId: {taskId} (generated; use this ID for subsequent task operations).")
            result

    let send (client: ClientService) (http: IHttpContextAccessor) command =
        task {
            match Option.ofObj http.HttpContext with
            | None ->
                return errorResult (PermissionDenied "The MCP request is missing its HTTP context.")
            | Some _ when RequestDeviceRouting.invalidAgentId() ->
                return errorResult (ValidationFailed "agentId must be a nonempty string of at most 256 characters.")
            | Some context ->
                let command, generatedId = ensureOperationId command
                let command, generatedTaskId =
                    match command with
                    | TaskWriteCommand cmd when cmd.Action = "CreateTask" ->
                        let taskId, generated = resolveTaskId cmd.TaskId cmd.OperationId
                        TaskWriteCommand { cmd with TaskId = taskId },
                        (if generated then Some taskId else None)
                    | _ -> command, None
                let scopes = requiredScopes command
                let missingScope =
                    scopes |> List.tryFind (fun scope -> not (Auth.hasScope scope context.User))

                match Auth.tryUserId context.User with
                | Some userId when missingScope.IsNone &&
                                   (AgentCommandInfo.projectName command = None ||
                                    AgentCommandInfo.projectName command = Some "@global" ||
                                    Auth.hasScope Auth.WorkspaceRead context.User) ->
                    // The authenticated MCP actor, not a notification's
                    // destination/observed target, owns an execution witness.
                    // Registration is a target-only operation, not agent
                    // activity by the freshly registered label.
                    let sourceAgent =
                        match command with
                        | TaskWriteCommand write when write.Action = "RegisterAgent" -> None
                        | TaskWriteCommand write when
                            not (String.IsNullOrWhiteSpace write.ActorId)
                            && write.ActorId <> TaskCommands.legacyAdministrativePlaceholder ->
                            Some write.ActorId
                        | TaskReadCommand read when read.Action = "AgentInbox" ->
                            Some read.AgentId
                        | ObserveAgentCommand observer -> Some observer.ObserverAgentId
                        | _ -> RequestDeviceRouting.agentId()
                    let admission =
                        match sourceAgent with
                        | None -> Ok(beginOperation context userId command, None)
                        | Some agent when String.IsNullOrWhiteSpace agent ->
                            Error "Initiating agent label is empty; no client operation was dispatched."
                        | Some agent ->
                            let store = context.RequestServices.GetService<FeedbackStore>()
                            if isNull (box store) then
                                Error "Durable agent execution storage is unavailable; command not dispatched."
                            else
                                let users = context.RequestServices.GetService<UserService>()
                                let clientVersion, protocolVersion =
                                    if isNull (box users) then None, None
                                    else
                                        match users.GetSelectedSession(
                                            userId, RequestDeviceRouting.session(),
                                            RequestDeviceRouting.deviceOverride()) with
                                        | ValueSome session -> Some session.ClientVersion, Some session.ProtocolVersion
                                        | ValueNone -> None, None
                                let witnessProject =
                                    match command with
                                    | TaskWriteCommand notification when
                                          notification.Action = "NotifyAgent"
                                          && not (String.IsNullOrWhiteSpace notification.SourceRef)
                                          && notification.SourceRef <> notification.ProjectName ->
                                        Some notification.SourceRef
                                    | _ -> AgentCommandInfo.projectName command
                                match store.TryBeginAgentOperation(
                                          userId, agent, toolName command,
                                          witnessProject,
                                          serverVersion, clientVersion, protocolVersion) with
                                | Ok started -> Ok(Some(store, started), Some started.Id)
                                | Error failure -> Error failure
                    let tracking, activityRequestId, admissionFailure =
                        match admission with
                        | Ok (record, request) -> record, request, None
                        | Error failure -> None, None, Some failure
                    // The same durable SQLite writer-transaction domain gates
                    // initiating commands and administrative correction. The
                    // permit targets the Working owner, NOT the operator.
                    // Take it before any client-side Git correction can run.
                    let correctionPermit, correctionFailure =
                        match command, admissionFailure with
                        | TaskWriteCommand correction, None
                            when correction.Action = "CorrectAgentWorkingState" ->
                            let store = context.RequestServices.GetService<FeedbackStore>()
                            if isNull (box store) then
                                None, Some "Durable correction barrier is unavailable."
                            else
                                let serialized = JsonSerializer.Serialize correction
                                let fingerprint =
                                    SHA256.HashData(Encoding.UTF8.GetBytes serialized)
                                    |> Convert.ToHexString
                                let users = context.RequestServices.GetService<UserService>()
                                let selectedDevice =
                                    if isNull (box users) then None
                                    else
                                        match users.GetSelectedSession(
                                            userId, RequestDeviceRouting.session(),
                                            RequestDeviceRouting.deviceOverride()) with
                                        | ValueSome session when session.State = Registered ->
                                            Some(session.DeviceId, session.Generation)
                                        | _ -> None
                                let deviceId, generation =
                                    selectedDevice |> Option.defaultValue ("", 0L)
                                let binding : AgentCorrectionRequestBinding =
                                    { ProjectName = correction.ProjectName
                                      RequestingAgentId = correction.ActorId
                                      RequestFingerprint = fingerprint
                                      InitiatingRequestId = activityRequestId |> Option.defaultValue ""
                                      DeviceId = deviceId
                                      DeviceGeneration = generation }
                                // The initiator's durable admitted witness must
                                // agree with this exact request. A reused UUID
                                // from another project/actor/payload cannot
                                // borrow an unresolved target exclusion.
                                match store.TryAcquireAgentCorrectionForRequest(
                                        userId, correction.OwnerId, correction.OperationId,
                                        binding) with
                                | Ok permitId -> Some(correction.OwnerId, permitId, deviceId), None
                                | Error failure -> None, Some failure
                        | _ -> None, None
                    let dispatchFailure =
                        admissionFailure |> Option.orElse correctionFailure
                    let stopwatch = Stopwatch.StartNew()
                    // A tool can commit a Ready->Working transition before
                    // failing its requested operation. Publish typed mutation
                    // evidence, independently of command kind or success.
                    let publishChanges (response: AgentCommandResponse) =
                        task {
                            for project, agentIds in
                                AgentStateInvalidationRouting.affectedByProject response.AgentStateChanges do
                                try
                                    do! client.InvalidateAgentStatesForUser(userId, project, agentIds)
                                with ex ->
                                    // This best-effort signal cannot retroactively
                                    // fail a committed canonical ledger operation.
                                    let factory = context.RequestServices.GetService<ILoggerFactory>()
                                    if not (isNull factory) then
                                        let logger = factory.CreateLogger("Jarvis.AgentStateInvalidation")
                                        logger.LogWarning(ex, "Could not deliver agent-state invalidation hints.")
                        }
                    // Cross-project notifications validate the sender before
                    // dispatching to the destination. A promoted source read
                    // is NOT proof of successful validation: resolve the
                    // original result via its client-owned job first.
                    let resolveSenderValidation (project: string) (source: AgentCommandResponse) deadline =
                        task {
                            match source.PendingJob with
                            | None -> return source
                            | Some handle ->
                                match handle.DeviceId with
                                | None ->
                                    return
                                        { source with
                                            Result = None
                                            Error = Some(ExecutionFailed
                                                "Source validation job has no bound device; cannot safely poll a different candidate.")
                                            PendingJob = None
                                            AgentStateChanges = [] }
                                | Some originalDevice ->
                                    // Shared typed resolver checks the job ID, project,
                                    // actor, terminal status and deadline. The callback
                                    // always polls the exact verified source device.
                                    return!
                                        ProjectNotificationRouting.resolveHealth project
                                            (defaultArg sourceAgent "")
                                            deadline source
                                            (fun query ->
                                                task {
                                                    do! Task.Delay(500, context.RequestAborted)
                                                    return!
                                                        client.SendCommandToUser(
                                                            userId, GetJobResultCommand query,
                                                            ?conversationId = RequestDeviceRouting.session(),
                                                            deviceId = originalDevice,
                                                            ?agentId = sourceAgent)
                                                })
                        }
                    let! response, continuationId =
                        match dispatchFailure, command with
                        | Some failure, _ ->
                            Task.FromResult(
                                { Result = None
                                  Error = Some(ExecutionFailed(
                                      $"Durable MCP activity admission refused: {failure}"))
                                  Instructions = []
                                  AgentStateChanges = []
                                  InboxActivation = None
                                  PendingJob = None }, None)
                        | None, TaskWriteCommand notification
                            when notification.Action = "NotifyAgent"
                                 && not (String.IsNullOrWhiteSpace notification.SourceRef)
                                 && notification.SourceRef <> notification.ProjectName ->
                            task {
                                // The original sender read owns the parent
                                // witness. Health probes are routing checks,
                                // not evidence that the sender completed.
                                let! initialSender = client.SendCommandToProject(
                                    userId, notification.SourceRef,
                                    TaskReadCommand
                                        { ProjectName = notification.SourceRef
                                          Action = "AgentInbox"; AgentId = notification.ActorId
                                          TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
                                          Cursor = ""; PageSize = 0; BeforeOid = null
                                          AfterOid = null; RelativePath = null },
                                    ?conversationId = RequestDeviceRouting.session(),
                                    ?deviceId = RequestDeviceRouting.deviceOverride(),
                                    ?agentId = sourceAgent,
                                    ?activityRequestId = activityRequestId)
                                let! sender =
                                    resolveSenderValidation notification.SourceRef initialSender
                                        (DateTimeOffset.UtcNow.AddMinutes(5.))
                                do! publishChanges sender
                                match sender.Result, sender.Error, sender.PendingJob with
                                | Some _, None, None ->
                                    let continuation =
                                        match activityRequestId with
                                        | None -> Ok None
                                        | Some parentId ->
                                            let store = context.RequestServices.GetService<FeedbackStore>()
                                            if isNull (box store) then
                                                Error "Durable execution continuation store is unavailable."
                                            else
                                                store.TryBeginAgentContinuation(
                                                    userId, parentId, notification.ProjectName)
                                                |> Result.map Some
                                    match continuation with
                                    | Error failure ->
                                        return
                                            { sender with
                                                Result = None
                                                Error = Some(ExecutionFailed(
                                                    $"Cross-project continuation refused: {failure}"))
                                                PendingJob = None }, None
                                    | Ok childId ->
                                        // Mainline's one logical destination
                                        // job preserves the sender's activation
                                        // evidence across promoted completions.
                                        let forwarded =
                                            CrossProjectNotificationCommand
                                                { Notification = notification
                                                  SourceStateChanges = sender.AgentStateChanges
                                                  SourceInboxActivation = sender.InboxActivation }
                                        let! destination = client.SendCommandToProject(
                                            userId, notification.ProjectName, forwarded,
                                            ?conversationId = RequestDeviceRouting.session(),
                                            ?deviceId = RequestDeviceRouting.deviceOverride(),
                                            ?agentId = sourceAgent,
                                            ?activityRequestId = childId)
                                        return destination, childId
                                | _ -> return sender, None
                            }
                        | None, _ ->
                            task {
                                let selectedDevice =
                                    match correctionPermit with
                                    | Some (_, _, boundDeviceId) -> Some boundDeviceId
                                    | None -> RequestDeviceRouting.deviceOverride()
                                let! forwarded = client.SendCommandToUser(
                                    userId, command,
                                    ?conversationId = RequestDeviceRouting.session(),
                                    ?deviceId = selectedDevice,
                                    ?agentId = sourceAgent,
                                    ?activityRequestId = activityRequestId)
                                return forwarded, None
                            }
                    // A failed stage can clear only its own *Admitted*
                    // witness. A bound SignalR route is Dispatched; even a
                    // timeout or lost response must remain unresolved.
                    // Infrastructure health probes do not carry an agent ID.
                    match response.Error with
                    | Some _ ->
                        let store = context.RequestServices.GetService<FeedbackStore>()
                        if not (isNull (box store)) then
                            for requestId in [ activityRequestId; continuationId ] |> List.choose id do
                                store.TryMarkAgentNotDispatched(userId, requestId) |> ignore
                    | None -> ()
                    // A correction permit is a crash-stable exclusion, not a
                    // timeout lease. Release it only on proven non-dispatch or
                    // on a durable authenticated client-terminal witness AND
                    // a confirmed successful canonical Git command result.
                    // Failed/uncertain Git results require receipt reconciliation
                    // and deliberately keep the exclusive permit.
                    match correctionPermit, activityRequestId with
                    | Some (targetAgent, permitId, _), Some requestId ->
                        let store = context.RequestServices.GetService<FeedbackStore>()
                        if not (isNull (box store)) then
                            let resolved =
                                match store.GetAgentExecutionResolution(userId, requestId) with
                                | Ok NeverDispatched -> true
                                | Ok AuthenticatedClientFinished
                                    when response.Result.IsSome && response.Error.IsNone
                                         && response.PendingJob.IsNone -> true
                                | _ -> false
                            if resolved then
                                match store.TryReleaseAgentCorrection(
                                        userId, targetAgent, permitId, true) with
                                | Ok () -> ()
                                | Error failure ->
                                    let loggerFactory =
                                        context.RequestServices.GetService<ILoggerFactory>()
                                    if not (isNull loggerFactory) then
                                        loggerFactory.CreateLogger("Jarvis.AgentCorrectionGate")
                                            .LogWarning(
                                                "Correction {OperationId} succeeded or was not dispatched, " +
                                                "but exclusive permit {PermitId} could not be released: {Error}",
                                                requestId, permitId, failure)
                    | _ -> ()
                    stopwatch.Stop()
                    finishOperation tracking response stopwatch.ElapsedMilliseconds

                    do! publishChanges response
                    let evidence = completedToolEvidence command response
                    // Terminal evidence is returned to the polling caller. It
                    // is not a new mutation: repeated polls must not replay
                    // effects or repeatedly broadcast invalidation messages.
                    // Other devices use the existing bounded cache TTL if no
                    // direct update notification was delivered.

                    return
                        (match command with
                         | ReadImageCommand _ -> imageResult response
                         | ExportFileCommand _ -> exportResult context response
                         | _ -> toCallToolResult response)
                        |> attachStateEffects evidence
                        |> attachInstructions evidence.Instructions
                        |> attachOperationId tracking
                        |> attachGeneratedOperationId generatedId
                        |> attachGeneratedTaskId (if response.Result.IsSome && response.Error.IsNone && response.PendingJob.IsNone then generatedTaskId else None)
                | Some _ ->
                    match missingScope with
                    | Some scope -> return authorizationErrorResult context scope
                    | None -> return authorizationErrorResult context Auth.WorkspaceRead
                | None ->
                    return authenticationErrorResult context (Some(String.concat " " scopes))
        }

    let getProfile (http: IHttpContextAccessor) =
        task {
            match Option.ofObj http.HttpContext with
            | None ->
                return errorResult (PermissionDenied "The MCP request is missing its HTTP context.")
            | Some context ->
                match Auth.tryUserId context.User with
                | Some userId ->
                    let profile = { id = Auth.profileId userId }
                    let structured = JsonSerializer.SerializeToElement(profile)
                    let serialized = JsonSerializer.Serialize(profile)
                    return structuredResult structured serialized false
                | None ->
                    return authenticationErrorResult context None
        }

    let private optionalString (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private validateText fieldName maxLength value =
        if String.IsNullOrWhiteSpace value then
            Error $"{fieldName} is required."
        elif value.Length > maxLength then
            Error $"{fieldName} must be at most {maxLength} characters."
        else
            Ok(value.Trim())

    let private optionalBoundedText fieldName maxLength value =
        match optionalString value with
        | None -> Ok None
        | Some text when text.Length > maxLength ->
            Error $"{fieldName} must be at most {maxLength} characters."
        | Some text ->
            Ok(Some(text.Trim()))

    let private validationMessages results =
        results
        |> List.choose (function
            | Ok () -> None
            | Error message -> Some message)

    let private aggregateValidationError messages =
        let formatted =
            messages
            |> List.map (fun message -> $"- {message}")
            |> String.concat Environment.NewLine

        ValidationFailed $"Invalid arguments:{Environment.NewLine}{formatted}"

    let validationErrorResult message =
        errorResult (ValidationFailed message)

    let private authenticatedFeedbackContext (http: IHttpContextAccessor) =
        match Option.ofObj http.HttpContext with
        | None ->
            Error(errorResult (PermissionDenied "The MCP request is missing its HTTP context."))
        | Some context ->
            match Auth.tryUserId context.User with
            | Some userId when Auth.hasScope Auth.WorkspaceRead context.User ->
                Ok(context, userId)
            | Some _ ->
                Error(authorizationErrorResult context Auth.WorkspaceRead)
            | None ->
                Error(authenticationErrorResult context (Some Auth.WorkspaceRead))

    let submitFeedback
        (store: FeedbackStore)
        (http: IHttpContextAccessor)
        (projectName: string)
        (toolName: string)
        (category: string)
        (severity: string)
        (summary: string)
        (details: string)
        (workaround: string)
        (operationId: string)
        =
        task {
            match authenticatedFeedbackContext http with
            | Error result ->
                return result
            | Ok(_, userId) ->
                let projectResult = optionalBoundedText "projectName" 128 projectName
                let toolResult = optionalBoundedText "toolName" 128 toolName
                let categoryResult = FeedbackValidation.category category
                let severityResult = FeedbackValidation.severity severity
                let summaryResult = validateText "summary" 240 summary
                let detailsResult = optionalBoundedText "details" 4000 details
                let workaroundResult = optionalBoundedText "workaround" 2000 workaround
                let operationResult = optionalBoundedText "operationId" 64 operationId

                let validationErrors =
                    [ projectResult |> Result.map ignore
                      toolResult |> Result.map ignore
                      categoryResult |> Result.map ignore
                      severityResult |> Result.map ignore
                      summaryResult |> Result.map ignore
                      detailsResult |> Result.map ignore
                      workaroundResult |> Result.map ignore
                      operationResult |> Result.map ignore ]
                    |> validationMessages

                if not validationErrors.IsEmpty then
                    return errorResult (aggregateValidationError validationErrors)
                else
                    match
                        projectResult,
                        toolResult,
                        categoryResult,
                        severityResult,
                        summaryResult,
                        detailsResult,
                        workaroundResult,
                        operationResult
                    with
                    | Ok project, Ok tool, Ok category, Ok severity, Ok summary, Ok details, Ok workaround, Ok explicitOperation ->
                        match store.ResolveOperation(userId, explicitOperation, tool, project) with
                        | Error message ->
                            return errorResult (ValidationFailed message)
                        | Ok linkedOperation ->
                            let input =
                                { ProjectName = project
                                  ToolName = tool
                                  Category = category
                                  Severity = severity
                                  Summary = summary
                                  Details = details
                                  Workaround = workaround
                                  OperationId = explicitOperation }

                            return
                                store.AddFeedback(userId, input, linkedOperation)
                                |> jsonResult
                    | _ ->
                        return errorResult (ExecutionFailed "Feedback validation aggregation invariant failed.")
        }

    let listFeedback
        (store: FeedbackStore)
        (http: IHttpContextAccessor)
        (projectName: string)
        (toolName: string)
        (category: string)
        (severity: string)
        (limit: Nullable<int>)
        =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                let requestedLimit = if limit.HasValue then limit.Value else 50
                if requestedLimit < 1 || requestedLimit > 100 then
                    return errorResult (ValidationFailed "limit must be between 1 and 100.")
                else
                    let project = optionalString projectName
                    let tool = optionalString toolName
                    let categoryResult =
                        match optionalString category with
                        | None -> Ok None
                        | Some value -> FeedbackValidation.category value |> Result.map Some
                    let severityResult =
                        match optionalString severity with
                        | None -> Ok None
                        | Some value -> FeedbackValidation.severity value |> Result.map Some

                    let validationErrors =
                        [ categoryResult |> Result.map ignore
                          severityResult |> Result.map ignore ]
                        |> validationMessages

                    if not validationErrors.IsEmpty then
                        return errorResult (aggregateValidationError validationErrors)
                    else
                        match categoryResult, severityResult with
                        | Ok category, Ok severity ->
                            return store.ListFeedback(userId, project, tool, category, severity, requestedLimit) |> jsonResult
                        | _ ->
                            return errorResult (ExecutionFailed "Feedback filter validation aggregation invariant failed.")
        }

    let getFeedback (store: FeedbackStore) (http: IHttpContextAccessor) (feedbackId: string) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                match validateText "feedbackId" 64 feedbackId with
                | Error message -> return errorResult (ValidationFailed message)
                | Ok id ->
                    match store.GetFeedback(userId, id) with
                    | Some entry -> return jsonResult entry
                    | None -> return errorResult (NotFound "Feedback was not found for the authenticated user.")
        }

    let updateFeedback
        (store: FeedbackStore) (http: IHttpContextAccessor)
        (feedbackId: string) (status: string) (resolutionCommit: string) (resolutionNotes: string) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                let idResult = validateText "feedbackId" 64 feedbackId
                let statusResult = FeedbackValidation.status status
                let commitResult = optionalBoundedText "resolutionCommit" 128 resolutionCommit
                let notesResult = optionalBoundedText "resolutionNotes" 4000 resolutionNotes
                let errors =
                    [ idResult |> Result.map ignore
                      statusResult |> Result.map ignore
                      commitResult |> Result.map ignore
                      notesResult |> Result.map ignore ] |> validationMessages
                if not errors.IsEmpty then return errorResult (aggregateValidationError errors)
                else
                    match idResult, statusResult, commitResult, notesResult with
                    | Ok id, Ok state, Ok commit, Ok notes ->
                        match store.UpdateFeedback(userId, id, state, commit, notes) with
                        | Some updated -> return jsonResult updated
                        | None -> return errorResult (NotFound "Feedback was not found for the authenticated user.")
                    | _ -> return errorResult (ExecutionFailed "Feedback validation aggregation invariant failed.")
        }

    let feedbackSummary (store: FeedbackStore) (http: IHttpContextAccessor) (projectName: string) (toolName: string) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                return
                    store.GetSummary(userId, optionalString projectName, optionalString toolName)
                    |> jsonResult
        }

    let private serverUpdateGate = obj()
    let mutable private serverUpdatePending = false

    let updateServer (http: IHttpContextAccessor) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(context, _) when not (Auth.hasScope Auth.ProcessExecute context.User) ->
                return authorizationErrorResult context Auth.ProcessExecute
            | Ok _ ->
                let acquired = lock serverUpdateGate (fun () ->
                    if serverUpdatePending then false
                    else serverUpdatePending <- true; true)
                if not acquired then
                    return errorResult (Conflict "A server update request is already being scheduled.")
                else
                    use _releaseGate = { new IDisposable with member _.Dispose() = lock serverUpdateGate (fun () -> serverUpdatePending <- false) }
                    try
                        let info = ProcessStartInfo("/usr/bin/sudo")
                        info.ArgumentList.Add("-n")
                        info.ArgumentList.Add("/usr/bin/systemd-run")
                        info.ArgumentList.Add("--unit=jarvis-server-update")
                        info.ArgumentList.Add("--collect")
                        info.ArgumentList.Add("/usr/local/sbin/jarvis-update-server")
                        info.UseShellExecute <- false
                        info.RedirectStandardOutput <- true
                        info.RedirectStandardError <- true
                        use updaterProcess = new Process()
                        updaterProcess.StartInfo <- info
                        if not (updaterProcess.Start()) then
                            return errorResult (ExecutionFailed "Unable to launch server updater.")
                        else
                            let! stdout = updaterProcess.StandardOutput.ReadToEndAsync()
                            let! stderr = updaterProcess.StandardError.ReadToEndAsync()
                            do! updaterProcess.WaitForExitAsync()
                            if updaterProcess.ExitCode <> 0 then
                                return errorResult (ExecutionFailed $"Server update scheduling failed: {stderr.Trim()}")
                            else
                                return jsonResult {| scheduled = true; message = stdout.Trim(); statusTool = "GetServerUpdateStatus" |}
                    with ex ->
                        return errorResult (ExecutionFailed $"Server update scheduling failed: {ex.Message}")
        }

    let getServerUpdateStatus (http: IHttpContextAccessor) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok _ ->
                try
                    let path = "/var/lib/jarvis/server-update-status"
                    let status = if System.IO.File.Exists(path) then System.IO.File.ReadAllText(path).Trim() else "not-configured"
                    return jsonResult {| status = status |}
                with ex ->
                    return errorResult (ExecutionFailed ex.Message)
        }

    let private deviceStatuses (users: UserService) userId =
        users.ListDevices(userId)
        |> Array.map (fun session ->
            {| deviceId = session.DeviceId
               deviceName = session.DeviceName
               state = match session.State with | Registered -> "Registered" | Disconnected -> "Disconnected"
               connectionId = session.ConnectionId |> Option.defaultValue ""
               generation = session.Generation
               protocolVersion = session.ProtocolVersion
               clientVersion = session.ClientVersion
               supportedCommands = session.SupportedCommands
               registeredAt = session.RegisteredAt
               lastSeenAt = session.LastSeenAt
               disconnectedAt = session.DisconnectedAt |> Option.map _.ToString("O") |> Option.defaultValue ""
               lastFailure = session.LastFailure |> Option.defaultValue "" |})

    let listDevices (users: UserService) (http: IHttpContextAccessor) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                return
                    {| defaultDeviceId = users.GetDefaultDeviceId(userId) |> Option.defaultValue ""
                       conversationSelection = users.GetSelectedDeviceId(userId, RequestDeviceRouting.session()) |> Option.defaultValue ""
                       conversationIdAvailable = RequestDeviceRouting.session().IsSome
                       devices = deviceStatuses users userId |}
                    |> jsonResult
        }

    let selectDevice (users: UserService) (http: IHttpContextAccessor) (deviceId: string) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                match validateText "deviceId" 256 deviceId with
                | Error message -> return errorResult (ValidationFailed message)
                | Ok id ->
                    match RequestDeviceRouting.session() with
                    | None -> return errorResult (ValidationFailed "openai/session metadata is required for conversation-scoped device selection. Use a per-command deviceId override when session metadata is unavailable.")
                    | Some conversationId when users.SelectConversationDevice(userId, conversationId, id) ->
                        return
                            {| selectedDeviceId = id
                               devices = deviceStatuses users userId |}
                            |> jsonResult
                    | Some _ ->
                        return errorResult (NotFound "Device not registered for the authenticated user.")
        }

    let getConnectionDiagnostics
        (store: FeedbackStore)
        (users: UserService)
        (http: IHttpContextAccessor)
        (limit: Nullable<int>)
        =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                let requestedLimit = if limit.HasValue then limit.Value else 50
                if requestedLimit < 1 || requestedLimit > 100 then
                    return errorResult (ValidationFailed "limit must be between 1 and 100.")
                else
                    let currentSession =
                        match users.GetSelectedSession(userId, RequestDeviceRouting.session(), RequestDeviceRouting.deviceOverride()) with
                        | ValueNone -> null
                        | ValueSome session ->
                            box
                                {| state =
                                    match session.State with
                                    | Registered -> "Registered"
                                    | Disconnected -> "Disconnected"
                                   deviceId = session.DeviceId
                                   deviceName = session.DeviceName
                                   connectionId = session.ConnectionId |> Option.defaultValue ""
                                   generation = session.Generation
                                   protocolVersion = session.ProtocolVersion
                                   clientVersion = session.ClientVersion
                                   supportedCommands = session.SupportedCommands
                                   registeredAt = session.RegisteredAt
                                   lastSeenAt = session.LastSeenAt
                                   disconnectedAt = session.DisconnectedAt |> Option.map _.ToString("O") |> Option.defaultValue ""
                                   lastFailure = session.LastFailure |> Option.defaultValue ""
                                   registrationReason = session.RegistrationReason |}

                    return
                        {| currentSession = currentSession
                           defaultDeviceId = users.GetDefaultDeviceId(userId) |> Option.defaultValue ""
                           selectedDeviceId = users.GetSelectedDeviceId(userId, RequestDeviceRouting.session()) |> Option.defaultValue ""
                           conversationIdAvailable = RequestDeviceRouting.session() |> Option.isSome
                           devices = deviceStatuses users userId
                           events = store.ListConnectionEvents(userId, requestedLimit) |}
                        |> jsonResult
        }

    /// Operator recovery is intentionally read-only. These independent reads
    /// are NOT one atomic liveness certification and may race new admission.
    /// The actual mutation still requires its own serialized SQLite exclusion,
    /// authenticated process/job termination and canonical Git CAS receipt.
    let getAgentCorrectionEvidence
        (store: FeedbackStore)
        (http: IHttpContextAccessor)
        (targetAgentId: string)
        (limit: Nullable<int>)
        =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok(_, userId) ->
                let maxRows = if limit.HasValue then limit.Value else 25
                match validateText "targetAgentId" 256 targetAgentId with
                | Error message -> return errorResult (ValidationFailed message)
                | Ok target when maxRows < 1 || maxRows > 50 ->
                    return errorResult (ValidationFailed "limit must be between 1 and 50.")
                | Ok target ->
                    match store.GetAgentExecutionReadiness(),
                          store.InspectAgentCorrectionPermit(userId, target),
                          store.ListUnresolvedAgentExecutions(userId, target, maxRows),
                          store.ListDurableDeviceRegistrations(userId, maxRows) with
                    | Ok protocol, Ok permit, Ok unresolved, Ok registrations ->
                        let epochVerified =
                            match protocol with
                            | LegacyDrainVerified _ -> true
                            | LegacyDrainUnverified -> false
                        return
                            {| targetAgentId = target.ToUpperInvariant()
                               protocolEpochVerified = epochVerified
                               unresolvedCorrectionPermit = permit
                               unresolvedExecutions = unresolved.Executions
                               hasMore = unresolved.HasMore
                               durableDeviceRegistrations = registrations.Registrations
                               deviceRegistrationsHasMore = registrations.HasMore
                               rosterCanOmitPreUpgradeDevices = true
                               readOnlyNonAtomic = true
                               authorizesCorrection = false |}
                            |> jsonResult
                    | Error problem, _, _, _
                    | _, Error problem, _, _
                    | _, _, Error problem, _
                    | _, _, _, Error problem ->
                        return errorResult (ExecutionFailed
                            ("Cannot verify durable correction evidence: " + problem))
        }

    let listMcpCommands (http: IHttpContextAccessor) =
        task {
            match authenticatedFeedbackContext http with
            | Error result -> return result
            | Ok _ ->
                return mcpListCommandsResult |> jsonResult
        }

    let optionOfString (value: string) =
        if String.IsNullOrWhiteSpace(value) then None else Some value

    let optionOfNullable (value: Nullable<int>) =
        if value.HasValue then Some value.Value else None

    let optionOfNullableInt64 (value: Nullable<int64>) =
        if value.HasValue then Some value.Value else None

    let optionOfNullableBool (value: Nullable<bool>) =
        if value.HasValue then Some value.Value else None

    let parseFileWriteMode (value: string) =
        if String.Equals(value, "Append", StringComparison.OrdinalIgnoreCase) then Ok Append
        elif String.Equals(value, "Write", StringComparison.OrdinalIgnoreCase) then Ok Write
        else Error $"fileWriteMode must be one of: Append, Write. Received: '{value}'."

[<McpServerToolType>]
type JarvisMcpTools =
    [<McpServerTool(Title = "Get profile", UseStructuredContent = true, OutputSchemaType = typeof<ProfileResult>, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":[]}]"""); McpMeta("openai/profile", true); Description("Return the profile represented by the authenticated OAuth credentials.")>]
    static member GetProfile(http: IHttpContextAccessor) =
        McpToolHelpers.getProfile http

    [<McpServerTool(Title = "Submit feedback", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Submit structured feedback about Jarvis tool failures, limitations, ergonomics, missing capabilities, documentation, or positive behavior. Does not require local client approval.")>]
    static member Feedback(
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue("")>] toolName: string,
        [<Description("Feedback category. Allowed values: ToolFailure, ToolLimitation, Ergonomics, MissingCapability, Documentation, Positive, Other.")>] category: string,
        [<Description("Feedback severity. Allowed values: Info, Friction, Blocking.")>] severity: string,
        summary: string,
        [<Optional; DefaultParameterValue("")>] details: string,
        [<Optional; DefaultParameterValue("")>] workaround: string,
        [<Optional; DefaultParameterValue("")>] operationId: string,
        store: FeedbackStore,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.submitFeedback store http projectName toolName category severity summary details workaround operationId

    [<McpServerTool(Title = "List feedback", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List recent feedback submitted by the authenticated user. Filters are optional and limit defaults to 50.")>]
    static member ListFeedback(
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue("")>] toolName: string,
        [<Optional; DefaultParameterValue("")>] category: string,
        [<Optional; DefaultParameterValue("")>] severity: string,
        [<Optional; DefaultParameterValue(50)>] limit: int,
        store: FeedbackStore,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.listFeedback store http projectName toolName category severity (Nullable limit)

    [<McpServerTool(Title = "Get feedback", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get a feedback record by ID for the authenticated user.")>]
    static member GetFeedback(feedbackId: string, store: FeedbackStore, http: IHttpContextAccessor) =
        McpToolHelpers.getFeedback store http feedbackId

    [<McpServerTool(Title = "Update feedback", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Update feedback status: Open, InProgress, Resolved, WontFix. Closing stores resolution commit and notes; reopening clears resolution details.")>]
    static member UpdateFeedback(feedbackId: string, status: string, resolutionCommit: string, resolutionNotes: string, store: FeedbackStore, http: IHttpContextAccessor) =
        McpToolHelpers.updateFeedback store http feedbackId status resolutionCommit resolutionNotes

    [<McpServerTool(Title = "Get feedback summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Summarize feedback counts by tool, category, and severity for the authenticated user.")>]
    static member GetFeedbackSummary(
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue("")>] toolName: string,
        store: FeedbackStore,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.feedbackSummary store http projectName toolName

    [<McpServerTool(Title = "Update server", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Update the Jarvis server from a verified published release, with automatic rollback. The service restarts during the update.")>]
    static member UpdateServer(http: IHttpContextAccessor) =
        McpToolHelpers.updateServer http

    [<McpServerTool(Title = "Get server update status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read the latest server update result after reconnecting.")>]
    static member GetServerUpdateStatus(http: IHttpContextAccessor) =
        McpToolHelpers.getServerUpdateStatus http

    [<McpServerTool(Title = "List devices", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List the authenticated user's Jarvis devices, global fallback default, and this ChatGPT conversation's selected device.")>]
    static member ListDevices(users: UserService, http: IHttpContextAccessor) =
        McpToolHelpers.listDevices users http

    [<McpServerTool(Title = "Select device", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Select a Jarvis device for this ChatGPT conversation using openai/session metadata, without changing other agents' selections. Requires session metadata; alternatively pass deviceId to individual command tools.")>]
    static member SelectDevice(deviceId: string, users: UserService, http: IHttpContextAccessor) =
        McpToolHelpers.selectDevice users http deviceId

    [<McpServerTool(Title = "Get connection diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Return the current Jarvis client session and recent persisted SignalR connection lifecycle events. Does not require the local client to be connected.")>]
    static member GetConnectionDiagnostics(
        [<Optional; DefaultParameterValue(50)>] limit: int,
        store: FeedbackStore,
        users: UserService,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.getConnectionDiagnostics store users http (Nullable limit)

    [<McpServerTool(Title = "Get agent correction evidence", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read-only recovery evidence for an agent: immutable correction permit, pending execution witnesses, and protocol-epoch readiness. User-scoped, bounded, not an authorization or proof of quiescence.")>]
    static member GetAgentCorrectionEvidence(
        targetAgentId: string,
        [<Optional; DefaultParameterValue(25)>] limit: int,
        store: FeedbackStore,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.getAgentCorrectionEvidence
            store http targetAgentId (Nullable limit)

    [<McpServerTool(Title = "List commands", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List the complete public Jarvis MCP command catalog, including server-local tools.")>]
    static member ListCommands(http: IHttpContextAccessor) =
        McpToolHelpers.listMcpCommands http

    [<McpServerTool(Title = "List projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List projects exposed by the connected Jarvis client.")>]
    static member ListProjects([<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http ListProjectsCommand

    [<McpServerTool(Title = "Create project", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Create a new top-level workspace folder. Optionally initialize local Git, create .jarvis.json and AGENTS.md, configure origin, and initialize the remote-backed Jarvis ledger with explicit remote visibility acknowledgment. Never overwrites an existing folder.")>]
    static member CreateProject(name: string,
                                [<Optional; DefaultParameterValue(false)>] initializeGit: bool,
                                [<Optional; DefaultParameterValue(false)>] createJarvisConfig: bool,
                                [<Optional; DefaultParameterValue(false)>] createAgentInstructions: bool,
                                [<Optional; DefaultParameterValue(false)>] initializeLedger: bool,
                                [<Optional; DefaultParameterValue("")>] remoteUrl: string,
                                [<Optional; DefaultParameterValue(false)>] acknowledgeRemoteVisibility: bool,
                                [<Optional; DefaultParameterValue("")>] deviceId: string,
                                client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (CreateProjectCommand
                { Name = name; InitializeGit = initializeGit || initializeLedger
                  CreateJarvisConfig = createJarvisConfig
                  CreateAgentInstructions = createAgentInstructions
                  InitializeLedger = initializeLedger; RemoteUrl = remoteUrl
                  AcknowledgeRemoteVisibility = acknowledgeRemoteVisibility })

    [<McpServerTool(Title = "List worktrees", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Discover all registered Git worktrees for a project, regardless of directory layout.")>]
    static member ListWorktrees(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListWorktreesCommand { ProjectName = projectName })

    [<McpServerTool(Title = "Get worktree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Inspect an existing Git-registered project worktree.")>]
    static member GetWorktree(projectName: string, agentId: string, worktreePath: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetWorktreeCommand { ProjectName = projectName; WorktreePath = worktreePath })

    [<McpServerTool(Title = "Create worktree", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Create an isolated Git working tree and branch for development. The worktree is registered with Jarvis and requires local approval.")>]
    static member CreateWorktree(projectName: string, agentId: string, name: string, branch: string, baseRef: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (CreateWorktreeCommand { ProjectName = projectName; Name = name; Branch = branch; BaseRef = baseRef })

    [<McpServerTool(Title = "Inspect worktree removal", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Inspect cleanliness, lock state and integration before removing a worktree.")>]
    static member InspectWorktreeRemoval(projectName: string, agentId: string, worktreePath: string, [<Optional; DefaultParameterValue("")>] targetRef: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (InspectWorktreeRemovalCommand { ProjectName = projectName; WorktreePath = worktreePath; TargetRef = McpToolHelpers.optionOfString targetRef })

    [<McpServerTool(Title = "Remove worktree", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Clean up a completed Git worktree after verifying it is clean, integrated and unlocked. Requires local approval.")>]
    static member RemoveWorktree(projectName: string, agentId: string, worktreePath: string, [<Optional; DefaultParameterValue("")>] targetRef: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (RemoveWorktreeCommand { ProjectName = projectName; WorktreePath = worktreePath; TargetRef = McpToolHelpers.optionOfString targetRef })

    [<McpServerTool(Title = "ledger health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read the verified remote orphan Git ledger identity, trusted head and sequence. Requires an explicit existing binding; never initializes a ledger.")>]
    static member LedgerHealth(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (LedgerReadCommand { ProjectName = projectName; Action = "Health"; OperationId = ""; Cursor = ""; PageSize = 0 })

    [<McpServerTool(Title = "pending operation summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Aggregate pending intent counts by verified disposition, age, action and actor across the selected project. Never returns saved command JSON or session metadata, never retries or discards uncertain writes.")>]
    static member PendingOperationSummary(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerReadCommand { ProjectName = projectName; Action = "PendingOperationSummary"; OperationId = ""; Cursor = ""; PageSize = 0 })

    [<McpServerTool(Title = "pending operations page", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List at most 100 pending journal entries after an opaque UUID cursor, with compact verified disposition, actor, task and action metadata. No saved command JSON, mutation, replay, or deletion.")>]
    static member PendingOperationsPage(projectName: string, agentId: string, cursor: string, pageSize: int, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerReadCommand { ProjectName = projectName; Action = "PendingOperationsPage"; OperationId = ""; Cursor = cursor; PageSize = pageSize })

    [<McpServerTool(Title = "inspect one pending operation", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Targeted inspection of one exact saved pending intent and its verified ledger outcome, including original stored JSON. May include historical session metadata. Read only; never retries or discards.")>]
    static member InspectPendingOperation(projectName: string, agentId: string, operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerReadCommand { ProjectName = projectName; Action = "InspectPendingOperation"; OperationId = operationId; Cursor = ""; PageSize = 0 })

    [<McpServerTool(Title = "pending operations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Inspect the local crash-persistent write intent journal, including original command parameters for safe retry with the same UUID. No source or ledger mutations.")>]
    static member PendingOperations(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerReadCommand { ProjectName = projectName; Action = "PendingOperations"; OperationId = ""; Cursor = ""; PageSize = 0 })

    [<McpServerTool(Title = "reconcile pending operations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Compare each locally pending ledger command against one verified remote ledger snapshot. Report Applied, NotFoundAtVerifiedHead, BindingMismatch or IdentityMismatch; never erase local recovery evidence or perform a write.")>]
    static member ReconcilePendingOperations(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerReadCommand { ProjectName = projectName; Action = "ReconcilePendingOperations"; OperationId = ""; Cursor = ""; PageSize = 0 })

    [<McpServerTool(Title = "get operation status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a specific caller-stable ledger operation UUID from the verified remote ledger; NotFoundAtVerifiedHead is not proof that a timed-out push will never arrive.")>]
    static member GetOperationStatus(projectName: string, agentId: string, operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerReadCommand { ProjectName = projectName; Action = "GetOperationStatus"; OperationId = operationId; Cursor = ""; PageSize = 0 })

    [<McpServerTool(Title = "acknowledge pending operation", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("After inspecting a matching Applied ledger receipt, explicitly remove only the corresponding local pending journal record. Requires the exact verified remote requestHash. Does not push, mutate or initialize the ledger.")>]
    static member AcknowledgePendingOperation(projectName: string, agentId: string, operationId: string, expectedRequestHash: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerWriteCommand
                { ProjectName = projectName; Action = "AcknowledgePendingOperation"; OperationId = operationId
                  ProjectId = ""; LedgerId = ""; Epoch = 0
                  RemoteName = ""; ApprovedFetchUrl = ""; ApprovedPushUrl = ""
                  ExpectedTrustedHead = ""; ExpectedRequestHash = expectedRequestHash
                  AcknowledgeVisibility = false })


    [<McpServerTool(Title = "retry a saved pending intent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("EXPLICIT OPERATOR RECOVERY: after targeted inspection, retry the exact stored pending Task/Review command under its original UUID and current ledger CAS/state guards. Requires original SHA256 and verified ledger head. Retired ResumeAgent and agent lifecycle writes are not retryable. No new session/UUID, no automatic retry, no archive cleanup.")>]
    static member RetryPendingIntent(projectName: string, agentId: string, operationId: string, expectedCommandSha256: string, expectedVerifiedHead: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (RetryPendingIntentCommand
                { ProjectName = projectName; OperationId = operationId
                  OperatorId = agentId; ExpectedCommandSha256 = expectedCommandSha256
                  ExpectedVerifiedHead = expectedVerifiedHead })

    [<McpServerTool(Title = "supersede a saved pending intent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("EXPLICIT OPERATOR RECOVERY: archive, never silently discard, a locally uncertain intent only with matching saved SHA256, a pinned verified head, an operator reason and a distinct matching Applied receipt for the same task. Archive is durable, prevents UUID reuse, and remains observable if an original push arrives later. Never pushes or mutates the canonical ledger.")>]
    static member SupersedePendingIntent(projectName: string, agentId: string, operationId: string, expectedCommandSha256: string, expectedVerifiedHead: string, relatedOperationId: string, relatedRequestHash: string, reason: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (SupersedePendingIntentCommand
                { ProjectName = projectName; OperationId = operationId
                  OperatorId = agentId; ExpectedCommandSha256 = expectedCommandSha256
                  ExpectedVerifiedHead = expectedVerifiedHead
                  RelatedOperationId = relatedOperationId
                  RelatedRequestHash = relatedRequestHash; Reason = reason })

    [<McpServerTool(Title = "abandon obsolete pending intent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("EXPLICIT OPERATOR RECOVERY: archive an exact saved pending UUID only after verified current canonical task/review state at a pinned head positively proves the original action obsolete. Requires original SHA256 and operator reason. Archive-first crash durable, never mutates remote ledger or assumes a missing receipt means failed push.")>]
    static member AbandonPendingIntent(projectName: string, agentId: string, operationId: string, expectedCommandSha256: string, expectedVerifiedHead: string, reason: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (AbandonPendingIntentCommand
                { ProjectName = projectName; OperationId = operationId; OperatorId = agentId
                  ExpectedCommandSha256 = expectedCommandSha256
                  ExpectedVerifiedHead = expectedVerifiedHead; Reason = reason })

    [<McpServerTool(Title = "initialize ledger", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Initialize a NEW orphan Git ledger and bind it locally only after explicit public/unknown visibility acknowledgment and Git write approval. Refuses existing ledger refs.")>]
    static member InitializeLedger(projectName: string, projectId: string, ledgerId: string, epoch: int, remoteName: string, approvedFetchUrl: string, approvedPushUrl: string, acknowledgeVisibility: bool, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerWriteCommand
                { ProjectName = projectName; Action = "InitializeLedger"; OperationId = operationId
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = epoch
                  RemoteName = remoteName; ApprovedFetchUrl = approvedFetchUrl; ApprovedPushUrl = approvedPushUrl
                  ExpectedTrustedHead = ""; ExpectedRequestHash = ""; AcknowledgeVisibility = acknowledgeVisibility })

    [<McpServerTool(Title = "bind existing ledger", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Bind to an existing remote orphan ledger with independently supplied expected trusted commit, project identity and explicit public/unknown visibility acknowledgment; no implicit trust or creation.")>]
    static member BindExistingLedger(projectName: string, projectId: string, ledgerId: string, epoch: int, remoteName: string, approvedFetchUrl: string, approvedPushUrl: string, expectedTrustedHead: string, acknowledgeVisibility: bool, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerWriteCommand
                { ProjectName = projectName; Action = "BindExistingLedger"; OperationId = operationId
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = epoch
                  RemoteName = remoteName; ApprovedFetchUrl = approvedFetchUrl; ApprovedPushUrl = approvedPushUrl
                  ExpectedTrustedHead = expectedTrustedHead; ExpectedRequestHash = ""; AcknowledgeVisibility = acknowledgeVisibility })

    [<McpServerTool(Title = "rebind ledger remote", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Explicitly rebind a client to a verified remote of the SAME project/ledger/epoch, with independently supplied trusted ancestry and visibility acknowledgment. Keeps previous local binding backup.")>]
    static member RebindLedgerRemote(projectName: string, remoteName: string, approvedFetchUrl: string, approvedPushUrl: string, expectedTrustedHead: string, acknowledgeVisibility: bool, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (LedgerWriteCommand
                { ProjectName = projectName; Action = "RebindLedgerRemote"; OperationId = operationId
                  ProjectId = ""; LedgerId = ""; Epoch = 0; RemoteName = remoteName
                  ApprovedFetchUrl = approvedFetchUrl; ApprovedPushUrl = approvedPushUrl
                  ExpectedTrustedHead = expectedTrustedHead; ExpectedRequestHash = ""; AcknowledgeVisibility = acknowledgeVisibility })

    [<McpServerTool(Title = "close task", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Explicitly record that a task is closed. Does not inspect reviews, worker completion or Git integration.")>]
    static member CloseTask(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "CloseTask" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedTaskVersion = expectedTaskVersion })

    [<McpServerTool(Title = "agent inbox", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("SELF-INBOX ONLY: reading your own inbox may automatically activate a Ready assignment into Working and update last-seen. To inspect another agent with no activity/state changes, use ObserveAgent(projectName, agentId, targetAgentId). The response reports the canonical state after any activation.")>]
    static member AgentInbox(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskReadCommand { ProjectName = projectName; AgentId = agentId; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null })

    [<McpServerTool(Title = "observe agent without activation", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read-only observation of a registered target agent in the same project. agentId is the initiating tool-call actor label, cross-checked against the client transport; the authenticated OAuth user supplies authority. targetAgentId is the subject, never the actor. Returns verified canonical state plus explicitly labeled client-local last-seen evidence, without activating Ready assignments, updating last-seen or reading private notification bodies. Use agent_inbox for your own turn.")>]
    static member ObserveAgent(projectName: string, agentId: string, targetAgentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (ObserveAgentCommand
                { ProjectName = projectName; ObserverAgentId = agentId; TargetAgentId = targetAgentId })

    [<McpServerTool(Title = "create task", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Atomically create a task and canonical HTML spec on the bound Git ledger. Omit taskId to generate a unique, stable ID derived from operationId; provide taskId explicitly for named tasks and imports. Reusing operationId on retry preserves the generated ID.")>]
    static member CreateTask(projectName: string, agentId: string, title: string, html: string, [<Optional; DefaultParameterValue("")>] taskId: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "CreateTask" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with Title = title; Html = html })

    [<McpServerTool(Title = "revise task spec", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Revise a canonical HTML task spec with an exact task version. Supply expectedSpecBlobOid for assigned tasks or to assert the exact current spec; leave it empty for unassigned Open tasks. Assignment adoption remains explicit.")>]
    static member ReviseTaskSpec(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, title: string, html: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, [<Optional; DefaultParameterValue("")>] expectedSpecBlobOid: string, client: ClientService, http: IHttpContextAccessor) =
        let pinned = not (String.IsNullOrWhiteSpace expectedSpecBlobOid)
        let cmd = TaskCommands.write projectName (if pinned then "ReviseTaskSpecPinned" else "ReviseTaskSpec") operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedVersion = expectedTaskVersion; BaseOid = expectedSpecBlobOid; Title = title; Html = html })

    [<McpServerTool(Title = "register agent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Register an agent in the canonical ledger with no assignment, Stopped execution and explicit Parked disposition.")>]
    static member RegisterAgent(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "RegisterAgent" operationId "" agentId
        McpToolHelpers.send client http (TaskWriteCommand cmd)

    [<McpServerTool(Title = "get role", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a versioned shared role, including content and Git ledger blob provenance.")>]
    static member GetRole(projectName: string, agentId: string, roleId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskCommands.read projectName "GetRole" "" roleId)

    [<McpServerTool(Title = "list roles", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List shared instruction roles defined in this project's canonical ledger.")>]
    static member ListRoles(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskCommands.read projectName "ListRoles" "" "")

    [<McpServerTool(Title = "create role", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Create an independent versioned shared agent role with purpose and instructions. Does not assign tasks or grant permissions.")>]
    static member CreateRole(projectName: string, agentId: string, roleId: string, purpose: string, instructions: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "CreateRole" operationId roleId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with Title = purpose; Html = instructions })

    [<McpServerTool(Title = "update role", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Revise shared role purpose and instructions with an exact version. Edits apply on the next instruction read without changing historical assignments.")>]
    static member UpdateRole(projectName: string, agentId: string, roleId: string, expectedVersion: int, purpose: string, instructions: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "UpdateRole" operationId roleId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedVersion = expectedVersion; Title = purpose; Html = instructions })

    [<McpServerTool(Title = "get agent instruction config", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a registered agent's versioned role memberships and persistent individual instructions.")>]
    static member GetAgentInstructionConfig(projectName: string, agentId: string, targetAgentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskCommands.read projectName "GetAgentInstructionConfig" targetAgentId "")

    [<McpServerTool(Title = "resolve agent instructions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read all current role and per-agent instruction layers at one verified Git ledger head, with scope, version, hashes and deterministic precedence.")>]
    static member ResolveAgentInstructions(projectName: string, agentId: string, targetAgentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskCommands.read projectName "ResolveAgentInstructions" targetAgentId "")

    [<McpServerTool(Title = "attach role", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Attach an existing shared role to a registered agent using the current agent-instruction version (zero when absent). No permission or activity effect.")>]
    static member AttachRole(projectName: string, agentId: string, targetAgentId: string, roleId: string, expectedVersion: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "AttachRole" operationId roleId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with OwnerId = targetAgentId; ExpectedVersion = expectedVersion })

    [<McpServerTool(Title = "detach role", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Detach role membership with optimistic versioning; the shared role remains in the ledger for other agents.")>]
    static member DetachRole(projectName: string, agentId: string, targetAgentId: string, roleId: string, expectedVersion: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "DetachRole" operationId roleId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with OwnerId = targetAgentId; ExpectedVersion = expectedVersion })

    [<McpServerTool(Title = "set agent instructions", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Replace an agent's persistent individual instruction body using the exact current config version. Empty body clears the override while retaining memberships.")>]
    static member SetAgentInstructions(projectName: string, agentId: string, targetAgentId: string, expectedVersion: int, instructions: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "SetAgentInstructions" operationId "" TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with OwnerId = targetAgentId; ExpectedVersion = expectedVersion; Html = instructions })

    [<McpServerTool(Title = "integration submissions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read every recorded exact-commit integration submission and the latest submitted boundary for a task, including pending and integrated evidence. Does not merge or infer that a review verdict implies integration.")>]
    static member GetIntegrationSubmissions(projectName: string, agentId: string, taskId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskReadCommand { ProjectName = projectName; AgentId = ""; Action = "GetIntegrationSubmissions"; TaskId = taskId; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null })

    [<McpServerTool(Title = "submit integration boundary", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Submit an exact source branch commit as a durable integration boundary for Lead. Creates an immutable submission record without merging, pausing the agent or changing a review verdict. Each submission ID must be a caller-stable UUID; operationId can be omitted for new calls or supplied for exact retries; optional reviewId must refer to a frozen review of that exact commit.")>]
    static member SubmitIntegration(projectName: string, taskId: string, agentId: string, submissionId: string, sourceRef: string, submittedCommit: string, targetRef: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] reviewId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "SubmitIntegration" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with AttemptId = submissionId; SourceRef = sourceRef; BaseOid = submittedCommit; TargetRef = targetRef; Reason = reviewId })

    [<McpServerTool(Title = "complete integration boundary", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Record an exact integration commit only after verifying the remote target branch contains it and that it contains the submitted commit; does not merge, push source branches or mutate task/review lifecycle.")>]
    static member CompleteIntegration(projectName: string, taskId: string, agentId: string, submissionId: string, integratedCommit: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "CompleteIntegration" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with AttemptId = submissionId; BaseOid = integratedCommit })

    [<McpServerTool(Title = "notify agent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Send a durable notification to an agent in the same project or in optional toProjectName. Cross-project sends verify the sender in the source ledger and write only to the destination Git ledger, routing to a connected client that has its binding. The recipient inbox exposes the source project and agent. Omit operationId for a generated ID; reuse the returned ID for an uncertain retry.")>]
    static member NotifyAgent(projectName: string, fromAgentId: string, toAgentId: string, message: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] taskId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, [<Optional; DefaultParameterValue("")>] toProjectName: string, client: ClientService, http: IHttpContextAccessor) =
        let crossProject = not (String.IsNullOrWhiteSpace toProjectName) && toProjectName <> projectName
        let destination = if crossProject then toProjectName else projectName
        let cmd = TaskCommands.write destination "NotifyAgent" operationId taskId fromAgentId
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with OwnerId = toAgentId; Summary = message
                       SourceRef = if crossProject then projectName else "" })

    [<McpServerTool(Title = "acknowledge notification", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Mark an existing agent coordination notification as read by its recipient. Preserves the message in the Git ledger; unrelated tasks, assignments, reviews and execution states are unchanged.")>]
    static member AcknowledgeNotification(projectName: string, agentId: string, notificationId: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "AcknowledgeNotification" operationId "" agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with AttemptId = notificationId })

    [<McpServerTool(Title = "assign task", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Assign a registered unoccupied agent to an Open task. The agent immediately becomes Ready; the queued attempt is separate from execution. Reviews are independent.")>]
    static member AssignTask(projectName: string, taskId: string, attemptId: string, agentId: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "AssignTask" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with AttemptId = attemptId; OwnerId = agentId })

    [<McpServerTool(Title = "get assignment generation", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read an immutable assignment generation with the pinned spec OID, instructions, source baseline and optional acknowledgment receipt. Older generations remain readable after reissue or recovery.")>]
    static member GetAssignment(projectName: string, agentId: string, taskId: string, assignmentGeneration: int, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (TaskReadCommand
                { ProjectName = projectName; AgentId = ""; Action = "GetAssignment"
                  TaskId = taskId; AssignmentGeneration = assignmentGeneration; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null })

    [<McpServerTool(Title = "reissue assignment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Update instructions or canonical HTML for an unstarted Ready assignment. Optionally adopt a separately revised canonical spec by supplying expectedCurrentSpecBlobOid and expectedTaskVersion instead of newSpecHtml. Those inputs are mutually exclusive; Working assignments cannot be updated. Immutable issuance history is preserved.")>]
    static member ReissueAssignment(projectName: string, taskId: string, agentId: string, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, instructions: string, [<Optional; DefaultParameterValue("")>] newSpecHtml: string, [<Optional; DefaultParameterValue("")>] expectedCurrentSpecBlobOid: string, [<Optional; DefaultParameterValue(0)>] expectedTaskVersion: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let adoption = not (String.IsNullOrWhiteSpace expectedCurrentSpecBlobOid)
        let cmd = TaskCommands.write projectName (if adoption then "AdoptTaskSpec" else "ReissueAssignment") operationId taskId agentId
        McpToolHelpers.send client http
            (TaskWriteCommand
                { cmd with ExpectedVersion = expectedAttemptVersion
                           ExpectedAgentVersion = expectedAgentVersion
                           ExpectedTaskVersion = expectedTaskVersion
                           AssignmentGeneration = assignmentGeneration
                           BaseOid = expectedCurrentSpecBlobOid
                           Summary = instructions; Html = newSpecHtml })

    [<McpServerTool(Title = "checkpoint Working attempt", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Write an immutable progress checkpoint for the current Working assignment without releasing the slot. summary and nextAction must each contain non-whitespace text, max 8192 UTF-8 bytes. evidence is optional owner-reported text, max 8192 UTF-8 bytes; longer reports must be stored separately and referenced within this bound (never truncated). sourceCommit is optional: omit or pass empty for read-only/unbound audits; if given, it must be a valid available 40/64-character lowercase Git commit SHA. Optional exact version/generation assertions default to current ledger values. Invalid fields receive field-specific errors; no source is fabricated and evidence is not independently verified.")>]
    static member CheckpointAttempt(projectName: string, taskId: string, agentId: string, summary: string, nextAction: string, [<Optional; DefaultParameterValue("")>] sourceCommit: string, [<Optional; DefaultParameterValue("")>] evidence: string, [<Optional; DefaultParameterValue(0)>] expectedAttemptVersion: int, [<Optional; DefaultParameterValue(0)>] expectedAgentVersion: int, [<Optional; DefaultParameterValue(0)>] assignmentGeneration: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "CheckpointAttempt" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with ExpectedVersion = expectedAttemptVersion
                       ExpectedAgentVersion = expectedAgentVersion
                       AssignmentGeneration = assignmentGeneration
                       Summary = summary; NextAction = nextAction
                       BaseOid = sourceCommit; Evidence = evidence })

    [<McpServerTool(Title = "get checkpoint", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read an immutable attempt checkpoint by its ledger sequence from a verified canonical snapshot.")>]
    static member GetCheckpoint(projectName: string, agentId: string, taskId: string, checkpointSequence: int64, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskReadCommand
            { ProjectName = projectName; AgentId = ""; Action = "GetCheckpoint"
              TaskId = taskId; AssignmentGeneration = 0; CheckpointSequence = checkpointSequence; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null })

    [<McpServerTool(Title = "set attempt source", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Set the source branch for a current Working or Ready attempt. For initial binding supply both baseOid and targetRef; a later source-branch change omits both. Initial binding verifies Git ancestry and optional allowDetached/allowUncommitted; rebinding preserves pinned baseline and independent review snapshots.")>]
    static member SetAttemptSource(projectName: string, taskId: string, agentId: string, expectedAttemptVersion: int, assignmentGeneration: int, sourceRef: string,
                                   [<Optional; DefaultParameterValue("")>] baseOid: string,
                                   [<Optional; DefaultParameterValue("")>] targetRef: string,
                                   [<Optional; DefaultParameterValue(false)>] allowDetached: bool,
                                   [<Optional; DefaultParameterValue(false)>] allowUncommitted: bool,
                                   [<Optional; DefaultParameterValue("")>] operationId: string,
                                   [<Optional; DefaultParameterValue("")>] deviceId: string,
                                   client: ClientService, http: IHttpContextAccessor) =
        let hasBase = not (String.IsNullOrWhiteSpace baseOid)
        let hasTarget = not (String.IsNullOrWhiteSpace targetRef)
        if hasBase <> hasTarget then
            raise (ArgumentException("Initial source binding requires both baseOid and targetRef. Omit both to update an already bound branch."))
        if not hasBase && (allowDetached || allowUncommitted) then
            raise (ArgumentException("allowDetached and allowUncommitted apply only to initial source binding."))
        let cmd = TaskCommands.write projectName (if hasBase then "BindAttemptSource" else "RebindAttemptSource") operationId taskId agentId
        McpToolHelpers.send client http
            (TaskWriteCommand
                { cmd with ExpectedVersion = expectedAttemptVersion; AssignmentGeneration = assignmentGeneration
                           SourceRef = sourceRef; BaseOid = baseOid; TargetRef = targetRef
                           AllowDetached = allowDetached; AllowUncommitted = allowUncommitted })

    [<McpServerTool(Title = "inspect attempt source", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read the pinned source repository/ref and current registered Git worktree observation without changing task ownership, assignment or agent state. Missing source is SourceUnavailable, not agent Free.")>]
    static member InspectAttemptSource(projectName: string, agentId: string, taskId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http
            (TaskReadCommand
                { ProjectName = projectName; AgentId = ""; Action = "InspectAttemptSource"
                  TaskId = taskId; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null })

    [<McpServerTool(Title = "acknowledge review decision", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Acknowledge a specific review verdict by its decision sequence without interrupting the agent's implementation slot.")>]
    static member AcknowledgeReviewFeedback(projectName: string, taskId: string, agentId: string, decisionSequence: int64, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "AcknowledgeFeedback" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with DecisionSequence = decisionSequence })

    [<McpServerTool(Title = "yield agent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Yield the current Working assignment to Ready with a checkpoint and continuation instruction. Omit version/generation arguments to resolve the current ledger snapshot, or supply them as exact assertions.")>]
    static member YieldAgent(projectName: string, taskId: string, agentId: string, summary: string, nextAction: string, [<Optional; DefaultParameterValue(0)>] expectedAttemptVersion: int, [<Optional; DefaultParameterValue(0)>] expectedAgentVersion: int, [<Optional; DefaultParameterValue(0)>] assignmentGeneration: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "YieldAgent" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedVersion = expectedAttemptVersion; ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration; Summary = summary; NextAction = nextAction })

    [<McpServerTool(Title = "complete agent work", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Complete the specified current agent assignment, recording a checkpoint and releasing the slot. Version/generation arguments are optional strict assertions; omitting them resolves current ledger state.")>]
    static member CompleteAgentWork(projectName: string, taskId: string, agentId: string, summary: string, [<Optional; DefaultParameterValue(0)>] expectedAttemptVersion: int, [<Optional; DefaultParameterValue(0)>] expectedAgentVersion: int, [<Optional; DefaultParameterValue(0)>] assignmentGeneration: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "CompleteAgentWork" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedVersion = expectedAttemptVersion; ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration; Summary = summary })

    [<McpServerTool(Title = "set agent state", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Administratively park or unpark an unassigned agent. Only NeedsWork and Parked are valid; assigned Ready is controlled by assignment lifecycle and Working starts automatically on agent activity. Requires current agent version and a reason.")>]
    static member SetAgentState(projectName: string, agentId: string, targetAgentId: string, state: AdministrativeAgentState, expectedAgentVersion: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "SetAgentState" operationId "" targetAgentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with Summary = state.ToString(); ExpectedAgentVersion = expectedAgentVersion; Reason = reason })

    [<McpServerTool(Title = "correct working agent state", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Administratively correct one exact assigned Working agent to Ready without creating a worker checkpoint, reissuing an assignment or changing last-seen activity. Requires exact task/attempt identity, task/attempt/agent versions, assignment generation and a reason; records an immutable operator-attributed lifecycle event.")>]
    static member CorrectAgentWorkingState(projectName: string, agentId: string, targetAgentId: string, taskId: string, attemptId: string, expectedTaskVersion: int, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "CorrectAgentWorkingState" operationId taskId agentId
        McpToolHelpers.send client http
            (TaskWriteCommand
                { cmd with OwnerId = targetAgentId; AttemptId = attemptId
                           ExpectedTaskVersion = expectedTaskVersion; ExpectedVersion = expectedAttemptVersion
                           ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration
                           Reason = reason })

    [<McpServerTool(Title = "reissue queued task", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Explicitly assign a queued attempt to an unoccupied agent. Does not preempt a current Ready or Working assignment.")>]
    static member ReissueQueuedAttempt(projectName: string, taskId: string, agentId: string, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "ReissueQueuedAttempt" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedVersion = expectedAttemptVersion; ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration; Reason = reason })

    [<McpServerTool(Title = "recover agent assignment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Prepare continuation of an interrupted agent assignment by recording its checkpoint and issuing a new assignment generation. Requires an explicit operator action.")>]
    static member RecoverAgentAssignment(projectName: string, taskId: string, agentId: string, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "RecoverAgentAssignment" operationId taskId agentId
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedVersion = expectedAttemptVersion; ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration; Reason = reason })

    [<McpServerTool(Title = "DeferAttempt", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Defer a Queued or Started attempt, releasing its matching agent assignment. disposition is Deferred (default; formerly park_attempt) or Blocked. Both preserve task, owner/history, and independent reviews. Requires exact task/attempt/agent versions.")>]
    static member DeferAttempt(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, reason: string, [<Optional; DefaultParameterValue("Deferred")>] disposition: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        if disposition <> "Deferred" && disposition <> "Blocked" then
            raise (ArgumentException("disposition must be Deferred or Blocked.", "disposition"))
        let cmd = TaskCommands.write projectName (if disposition = "Blocked" then "BlockAttempt" else "ParkAttempt") operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with ExpectedTaskVersion = expectedTaskVersion; ExpectedVersion = expectedAttemptVersion
                       ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration
                       Reason = reason })

    [<McpServerTool(Title = "ResumeAttempt", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Explicitly requeue a deferred or blocked attempt with a new generation and assign its unoccupied owner Ready. Reject if that owner has another current assignment. Reviews are unchanged.")>]
    static member ResumeAttempt(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "ResumeAttempt" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with ExpectedTaskVersion = expectedTaskVersion; ExpectedVersion = expectedAttemptVersion
                       ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration
                       Reason = reason })

    [<McpServerTool(Title = "AbandonAttempt", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("End an implementation attempt and release its assignment if occupied, retaining historical reviews and leaving other agents' work unchanged. An active task returns to Open.")>]
    static member AbandonAttempt(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "AbandonAttempt" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with ExpectedTaskVersion = expectedTaskVersion; ExpectedVersion = expectedAttemptVersion
                       ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration
                       Reason = reason })

    [<McpServerTool(Title = "CancelTask", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Cancel a project task while retaining its history and independent reviews. Assigned tasks require exact attempt and agent version fields; unrelated assignments are unchanged.")>]
    static member CancelTask(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, reason: string, [<Optional; DefaultParameterValue(0)>] expectedAttemptVersion: int, [<Optional; DefaultParameterValue(0)>] expectedAgentVersion: int, [<Optional; DefaultParameterValue(0)>] assignmentGeneration: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let openTask = expectedAttemptVersion = 0 && expectedAgentVersion = 0 && assignmentGeneration = 0
        let cmd = TaskCommands.write projectName (if openTask then "CancelOpenTask" else "CancelTask") operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with ExpectedTaskVersion = expectedTaskVersion; ExpectedVersion = expectedAttemptVersion
                       ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration
                       Reason = reason })

    [<McpServerTool(Title = "transfer attempt owner", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Transfer an implementation attempt to another registered agent while preserving independent reviews.")>]
    static member TransferOwner(projectName: string, taskId: string, newAttemptId: string, agentId: string, expectedTaskVersion: int, expectedAttemptVersion: int, expectedAgentVersion: int, assignmentGeneration: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "TransferOwner" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with AttemptId = newAttemptId; OwnerId = agentId
                       ExpectedTaskVersion = expectedTaskVersion; ExpectedVersion = expectedAttemptVersion
                       ExpectedAgentVersion = expectedAgentVersion; AssignmentGeneration = assignmentGeneration
                       Reason = reason })

    [<McpServerTool(Title = "Reopen task", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Explicitly reopen Closed or Cancelled task, retaining all historical attempt and approval evidence with a new future assignment.")>]
    static member ReopenTask(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, reason: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "ReopenTask" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedTaskVersion = expectedTaskVersion; Reason = reason })

    [<McpServerTool(Title = "Task lifecycle history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read immutable administrative transitions and their previous/new task, attempt and owner metadata.")>]
    static member TaskLifecycleHistory(projectName: string, agentId: string, taskId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskReadCommand
            { ProjectName = projectName; AgentId = ""; Action = "LifecycleHistory"; TaskId = taskId
              AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null })

    [<McpServerTool(Title = "Read a bounded canonical task specification page", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a bounded canonical task specification page. Requires an existing bound orphan Git ledger.")>]
    static member GetTaskSpecPage(projectName: string, agentId: string, taskId: string, pageBytes: int, cursor: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskReadCommand
            { ProjectName = projectName; AgentId = ""; Action = "GetSpecPage"; TaskId = taskId
              AssignmentGeneration = 0; CheckpointSequence = 0L
              Cursor = cursor
              PageSize = pageBytes
              BeforeOid = null
              AfterOid = null
              RelativePath = null })

    [<McpServerTool(Title = "Compare two pinned canonical task specification blob OIDs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Compare two pinned canonical task specification blob OIDs. Requires an existing bound orphan Git ledger.")>]
    static member DiffTaskSpec(projectName: string, agentId: string, taskId: string, beforeBlobOid: string, afterBlobOid: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (TaskReadCommand
            { ProjectName = projectName; AgentId = ""; Action = "DiffTaskSpec"; TaskId = taskId
              AssignmentGeneration = 0; CheckpointSequence = 0L
              Cursor = ""
              PageSize = 0
              BeforeOid = beforeBlobOid
              AfterOid = afterBlobOid
              RelativePath = null })

    [<McpServerTool(Title = "Create a non-authoritative copy of canonical HTML inside a registered worktree, refusing overwrite, traversal and symlinks", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Create a non-authoritative copy of canonical HTML inside a registered worktree, refusing overwrite, traversal and symlinks. Requires an existing bound orphan Git ledger.")>]
    static member ExportTaskSpec(projectName: string, agentId: string, taskId: string, relativeHtmlPath: string, expectedSpecBlobOid: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "ExportTaskSpec" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand
            { cmd with SourceRef = relativeHtmlPath; BaseOid = expectedSpecBlobOid })

    [<McpServerTool(Title = "Import stable UTF-8 source HTML from registered worktree with immutable provenance and expected blob checks", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Import stable UTF-8 source HTML from registered worktree with immutable provenance and expected blob checks. Requires an existing bound orphan Git ledger.")>]
    static member ImportTaskSpec(projectName: string, agentId: string, taskId: string, expectedTaskVersion: int, expectedSpecBlobOid: string, expectedSourceBlobOid: string, title: string, sourceRelativePath: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let cmd = TaskCommands.write projectName "ImportTaskSpec" operationId taskId TaskCommands.legacyAdministrativePlaceholder
        McpToolHelpers.send client http (TaskWriteCommand { cmd with ExpectedVersion = expectedTaskVersion; BaseOid = expectedSpecBlobOid; TargetRef = expectedSourceBlobOid; Title = title; SourceRef = sourceRelativePath })

    [<McpServerTool(Title = "task state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a task's current lifecycle, assignment, and implementation attempt from the bound Git ledger.")>]
        static member GetTask(projectName: string, agentId: string, taskId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            McpToolHelpers.send client http (ReviewCommands.query projectName "GetTask" taskId "" "")

    [<McpServerTool(Title = "task overview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read all canonical tasks, attempts and independently managed reviews from one ledger snapshot.")>]
        static member TaskOverview(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            McpToolHelpers.send client http (ReviewCommands.query projectName "ListProjectTasks" "" "" "")

    [<McpServerTool(Title = "review details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read an independent review request, snapshot ref, optional diff context and recorded feedback.")>]
        static member ReviewDetails(projectName: string, agentId: string, taskId: string, requestId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            McpToolHelpers.send client http (ReviewCommands.query projectName "GetReview" taskId requestId "")

    [<McpServerTool(Title = "list review documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List a review's summary.md and individual findings/*.md files, including Git blob IDs, without embedding their contents in metadata.")>]
        static member ListReviewDocuments(projectName: string, agentId: string, taskId: string, requestId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            McpToolHelpers.send client http (ReviewCommands.query projectName "ListDocuments" taskId requestId "")

    [<McpServerTool(Title = "read review document", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a review's summary.md or findings/<slug>.md and obtain the Git blob ID for a subsequent edit.")>]
        static member ReadReviewDocument(projectName: string, agentId: string, taskId: string, requestId: string, documentPath: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            McpToolHelpers.send client http
                (ReviewReadCommand
                    { ProjectName = projectName; Action = "ReadDocument"; TaskId = taskId
                      RequestId = requestId; ReviewerId = ""; DocumentPath = documentPath })

    [<McpServerTool(Title = "write review document", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Create or update one review summary.md or findings/<slug>.md on the Git ledger (up to 512 KiB). Supply the current blob ID when replacing a document; review status is unchanged.")>]
        static member PutReviewDocument(projectName: string, taskId: string, requestId: string, agentId: string, documentPath: string, content: string, expectedBlobOid: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            let cmd = ReviewCommands.write projectName "PutDocument" operationId taskId requestId agentId 0
            McpToolHelpers.send client http
                (ReviewWriteCommand
                    { cmd with DocumentPath = documentPath; Content = content
                               ExpectedBlobOid = expectedBlobOid })

    [<McpServerTool(Title = "reviewer inbox", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List unclaimed reviews available to this reviewer and reviews currently claimed by this reviewer.")>]
        static member ReviewerInbox(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            McpToolHelpers.send client http (ReviewCommands.query projectName "ReviewerQueue" "" "" agentId)

    [<McpServerTool(Title = "submit review", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Publish a review snapshot for any available Git commit. No assignment or review phase required. Optionally replace an existing unclaimed request using expectedRequestVersion; claimed reviews cannot be replaced. Replacements retain targetRef and purpose. For independent reviews omit taskId (namespace _reviews).")>]
        static member SubmitReview(projectName: string, agentId: string, requestId: string, headOid: string, [<Optional; DefaultParameterValue("")>] operationId: string,
                                   [<Optional; DefaultParameterValue("")>] taskId: string,
                                   [<Optional; DefaultParameterValue("")>] sourceRef: string,
                                   [<Optional; DefaultParameterValue("")>] targetRef: string,
                                   [<Optional; DefaultParameterValue("")>] purpose: string,
                                   [<Optional; DefaultParameterValue("")>] summary: string,
                                   [<Optional; DefaultParameterValue("")>] deviceId: string,
                                   [<Optional; DefaultParameterValue(0)>] expectedRequestVersion: int,
                                   client: ClientService, http: IHttpContextAccessor) =
            if expectedRequestVersion < 0 then
                raise (ArgumentOutOfRangeException("expectedRequestVersion", "Expected request version must be zero or positive."))
            if expectedRequestVersion > 0 && (not (String.IsNullOrWhiteSpace targetRef) || not (String.IsNullOrWhiteSpace purpose)) then
                raise (ArgumentException("Replacement preserves the original targetRef and purpose; omit both on update."))
            let namespaceId = if String.IsNullOrWhiteSpace taskId then "_reviews" else taskId
            let cmd = ReviewCommands.write projectName (if expectedRequestVersion > 0 then "Update" else "Submit") operationId namespaceId requestId agentId expectedRequestVersion
            McpToolHelpers.send client http (ReviewWriteCommand
                { cmd with Kind = purpose; SourceRef = sourceRef; HeadOid = headOid
                           TargetRef = targetRef; Summary = summary })

    [<McpServerTool(Title = "claim review", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Claim an unclaimed review and freeze its submitted Git branch for independent inspection.")>]
        static member ClaimReview(projectName: string, taskId: string, requestId: string, agentId: string, expectedRequestVersion: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            let cmd = ReviewCommands.write projectName "Claim" operationId taskId requestId agentId expectedRequestVersion
            McpToolHelpers.send client http (ReviewWriteCommand cmd)

    [<McpServerTool(Title = "decide review", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Accept a claimed review or request changes. Write detailed findings in separate Markdown documents using put_review_document.")>]
        static member DecideReview(projectName: string, taskId: string, requestId: string, agentId: string, expectedRequestVersion: int, verdict: string, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            let cmd = ReviewCommands.write projectName "Decide" operationId taskId requestId agentId expectedRequestVersion
            McpToolHelpers.send client http (ReviewWriteCommand { cmd with Verdict = verdict })

    [<McpServerTool(Title = "release review claim", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Make a claimed review available to another reviewer, retaining its frozen source snapshot and leaving the task unchanged.")>]
        static member ReleaseReview(projectName: string, taskId: string, requestId: string, agentId: string, expectedRequestVersion: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            let cmd = ReviewCommands.write projectName "Release" operationId taskId requestId agentId expectedRequestVersion
            McpToolHelpers.send client http (ReviewWriteCommand cmd)

    [<McpServerTool(Title = "withdraw review", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Withdraw a review request while preserving its findings and leaving task assignments unchanged.")>]
        static member WithdrawReview(projectName: string, taskId: string, requestId: string, agentId: string, expectedRequestVersion: int, [<Optional; DefaultParameterValue("")>] operationId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
            let cmd = ReviewCommands.write projectName "Withdraw" operationId taskId requestId agentId expectedRequestVersion
            McpToolHelpers.send client http (ReviewWriteCommand cmd)

    [<McpServerTool(Title = "Get project details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get project details, README, notes, TODO, and related special files.")>]
    static member GetProjectDetails(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (GetProjectDetailsCommand { ProjectName = projectName })

    [<McpServerTool(Title = "List directory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List one bounded project directory. Very large listings return a clear limit error instead of an oversized array.")>]
    static member ListDirectory(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, folderPath: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ListDirectoryCommand { ProjectName = projectName; FolderPath = folderPath })

    [<McpServerTool(Title = "Search files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Search file and folder names lazily. Response fields: Matches, StopReason, FilesVisited and DirectoriesVisited. StopReason=Finished means complete; all other reasons mean partial results. Narrow query or folder to continue.")>]
    static member SearchFiles(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, query: string, folderPath: string, maxResults: Nullable<int>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (SearchFilesCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(Title = "Search text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Search text with bounded source scans. Response fields: Matches, StopReason, FilesVisited and DirectoriesVisited. Only Finished means complete; all other reasons mean partial results. Narrow globs/folder/query to continue.")>]
    static member SearchText(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, query: string, folderPath: string, includeGlobs: string array, excludeGlobs: string array, maxResults: Nullable<int>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (SearchTextCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            IncludeGlobs = includeGlobs |> Array.toList
            ExcludeGlobs = excludeGlobs |> Array.toList
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(Title = "List registered browser tabs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]""");
      Description("List only tabs manually registered by the user in the local Jarvis browser extension.")>]
    static member BrowserListTabs(agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http BrowserListTabsCommand

    [<McpServerTool(Title = "List registered browser windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]""");
      Description("List only browser windows explicitly registered through the extension.")>]
    static member BrowserListWindows(agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http BrowserListWindowsCommand

    [<McpServerTool(Title = "Schedule browser execution", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute","workspace:read"]}]""");
      Description("Persist one-shot or repeating browser launch in an explicitly registered window, executing a project JavaScript file with optional cleanup. No replay of uncertain occurrences.")>]
    static member ScheduleBrowserExecution(scheduleId: string, runAfterSeconds: int, url: string, projectName: string, agentId: string,
                                           [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, scriptPath: string,
                                           [<Optional; DefaultParameterValue("")>] argsJson: string,
                                           [<Optional; DefaultParameterValue("")>] alias: string,
                                           [<Optional; DefaultParameterValue(false)>] active: bool,
                                           [<Optional; DefaultParameterValue("")>] deviceId: string,
                                           [<Optional; DefaultParameterValue("")>] windowRegistration: string,
                                           [<Optional; DefaultParameterValue(false)>] closeTabAfterExecution: bool,
                                           repeatEverySeconds: Nullable<int>,
                                           client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ScheduleBrowserExecutionCommand {
            ScheduleId = scheduleId; RunAfterSeconds = runAfterSeconds; Url = url
            ProjectName = projectName; ScriptPath = scriptPath
            ArgsJson = McpToolHelpers.optionOfString argsJson
            Alias = McpToolHelpers.optionOfString alias
            Active = Some active; WindowRegistration = McpToolHelpers.optionOfString windowRegistration
            CloseTabAfterExecution = Some closeTabAfterExecution
            RepeatEverySeconds = McpToolHelpers.optionOfNullable repeatEverySeconds })

    [<McpServerTool(Title = "List scheduled browser executions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]""");
      Description("List local one-shot and recurring browser actions, next due times and last launch status, without revealing script arguments.")>]
    static member ListScheduledBrowserExecutions(agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http ListScheduledBrowserExecutionsCommand

    [<McpServerTool(Title = "Cancel pending browser execution", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]""");
      Description("Cancel a pending local browser schedule (including recurring); an in-progress occurrence cannot be undone.")>]
    static member CancelScheduledBrowserExecution(scheduleId: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (CancelScheduledBrowserExecutionCommand { ScheduleId = scheduleId })

    [<McpServerTool(Title = "Open browser tab", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]""");
      Description("Open and automatically register a new browser tab on a site previously authorized by the user. Requires the extension's explicit agent-created-tabs opt-in; never grants access to existing unregistered tabs. Defaults to background. Do not blindly retry after timeout.")>]
    static member BrowserOpenTab(url: string, agentId: string,
                                  [<Optional; DefaultParameterValue("")>] alias: string,
                                  [<Optional; DefaultParameterValue(false)>] active: bool,
                                  [<Optional; DefaultParameterValue("")>] deviceId: string,
                                  [<Optional; DefaultParameterValue("")>] windowRegistration: string,
                                  client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (BrowserOpenTabCommand {
            WindowRegistration = windowRegistration
            Url = url; Alias = McpToolHelpers.optionOfString alias; Active = Some active })

    [<McpServerTool(Title = "Close Jarvis-created browser tab", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]""");
      Description("Close only a verified Jarvis-created tab, never a manually registered tab or browser window.")>]
    static member BrowserCloseTab(tabRegistration: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (BrowserCloseTabCommand { TabRegistration = tabRegistration })

    [<McpServerTool(Title = "Run project script in a new browser tab", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute","workspace:read"]}]""");
      Description("Create a new tab in a registered browser window, run a project .js file, and optionally close it. Never retries uncertain page effects.")>]
    static member BrowserRunInNewTab(windowRegistration: string, url: string, projectName: string, agentId: string,
                                      [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, scriptPath: string,
                                      [<Optional; DefaultParameterValue("")>] argsJson: string,
                                      [<Optional; DefaultParameterValue(false)>] active: bool,
                                      [<Optional; DefaultParameterValue(true)>] closeTabAfterExecution: bool,
                                      timeoutMs: Nullable<int>,
                                      [<Optional; DefaultParameterValue("")>] deviceId: string,
                                      client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (BrowserRunInNewTabCommand {
            WindowRegistration = windowRegistration; Url = url; ProjectName = projectName
            ScriptPath = scriptPath; ArgsJson = McpToolHelpers.optionOfString argsJson
            Active = Some active; CloseTabAfterExecution = Some closeTabAfterExecution
            TimeoutMs = McpToolHelpers.optionOfNullable timeoutMs })

    [<McpServerTool(Title = "Execute browser project script", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute","workspace:read"]}]""");
      Description("Execute a project-relative .js file inside a user-registered browser tab. argsJson is a JSON object. Side effects may persist even if transport times out.")>]
    static member BrowserExecuteFile(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, scriptPath: string, tabRegistration: string,
                                      [<Optional; DefaultParameterValue("")>] argsJson: string,
                                      timeoutMs: Nullable<int>,
                                      [<Optional; DefaultParameterValue("")>] deviceId: string,
                                      client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (BrowserExecuteFileCommand {
            ProjectName = projectName; ScriptPath = scriptPath; TabRegistration = tabRegistration
            ArgsJson = McpToolHelpers.optionOfString argsJson
            TimeoutMs = McpToolHelpers.optionOfNullable timeoutMs })

    [<McpServerTool(Title = "Execute browser JavaScript", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]""");
      Description("Execute inline JavaScript in a manually registered browser tab. Only intended for investigation; use BrowserExecuteFile for maintained scripts.")>]
    static member BrowserExecuteJs(tabRegistration: string, code: string, agentId: string,
                                    [<Optional; DefaultParameterValue("")>] argsJson: string,
                                    timeoutMs: Nullable<int>,
                                    [<Optional; DefaultParameterValue("")>] deviceId: string,
                                    client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (BrowserExecuteJsCommand {
            TabRegistration = tabRegistration; Code = code
            ArgsJson = McpToolHelpers.optionOfString argsJson
            TimeoutMs = McpToolHelpers.optionOfNullable timeoutMs })


    [<McpServerTool(Title = "Capture rendered browser DOM", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]""");
      Description("Capture live post-JavaScript HTML from an authorized tab, optionally waiting for delay, selector state or DOM event. waitJson examples: {\"kind\":\"delay\",\"milliseconds\":1500}, {\"kind\":\"selector\",\"selector\":\"#results\",\"state\":\"exists\"}, {\"kind\":\"event\",\"target\":\"document\",\"name\":\"ready\"}. Inline output is bounded; use BrowserExportDom for large pages.")>]
    static member BrowserCaptureDom(tabRegistration: string, agentId: string,
                                    [<Optional; DefaultParameterValue("")>] waitJson: string,
                                    [<Optional; DefaultParameterValue("")>] captureSelector: string,
                                    timeoutMs: Nullable<int>,
                                    [<Optional; DefaultParameterValue("")>] deviceId: string,
                                    client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (BrowserCaptureDomCommand {
            TabRegistration = tabRegistration
            WaitJson = McpToolHelpers.optionOfString waitJson
            CaptureSelector = McpToolHelpers.optionOfString captureSelector
            TimeoutMs = McpToolHelpers.optionOfNullable timeoutMs })

    [<McpServerTool(Title = "Export rendered browser DOM", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write","process:execute"]}]""");
      Description("Capture up to 8 MiB of live rendered HTML into an explicit project-relative new file. Uses bounded, checksummed chunk transfer and atomic write; refuses overwrite. A tab must be authorized.")>]
    static member BrowserExportDom(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePath: string, tabRegistration: string,
                                   [<Optional; DefaultParameterValue("")>] waitJson: string,
                                   [<Optional; DefaultParameterValue("")>] captureSelector: string,
                                   timeoutMs: Nullable<int>,
                                   [<Optional; DefaultParameterValue("")>] deviceId: string,
                                   client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (BrowserExportDomCommand {
            ProjectName = projectName; FilePath = filePath; TabRegistration = tabRegistration
            WaitJson = McpToolHelpers.optionOfString waitJson
            CaptureSelector = McpToolHelpers.optionOfString captureSelector
            TimeoutMs = McpToolHelpers.optionOfNullable timeoutMs })

    [<McpServerTool(Title = "Read file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read one small source file or inclusive one-based line range. File/line limits are enforced before allocation; use ReadFilePage to inspect large UTF-8 files.")>]
    static member ReadFile(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePath: string, startLine: Nullable<int>, endLine: Nullable<int>, includeLineNumbers: Nullable<bool>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ReadFileCommand {
            ProjectName = projectName
            FilePath = filePath
            StartLine = McpToolHelpers.optionOfNullable startLine
            EndLine = McpToolHelpers.optionOfNullable endLine
            IncludeLineNumbers = McpToolHelpers.optionOfNullableBool includeLineNumbers })

    [<McpServerTool(Title = "Read file page", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a bounded UTF-8 page of a project source file, including a continuation cursor. Subsequent calls reject changed files instead of silently mixing versions. Use for files too large for ReadFile.")>]
    static member ReadFilePage(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePath: string, [<Optional; DefaultParameterValue(""); Description("Continuation token returned by the previous page; omit for the first page.")>] cursor: string, pageBytes: Nullable<int>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ReadFilePageCommand {
            ProjectName = projectName
            FilePath = filePath
            Cursor = McpToolHelpers.optionOfString cursor
            PageBytes = McpToolHelpers.optionOfNullable pageBytes })

    [<McpServerTool(Title = "Read files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read up to 32 project files with a bounded aggregate JSON response. Per-file errors indicate missing files or resource limits.")>]
    static member ReadFiles(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePaths: string array, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ReadFilesCommand { ProjectName = projectName; FilePaths = filePaths |> Array.toList })

    [<McpServerTool(Title = "Get agent instructions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read the complete applicable AGENTS.md hierarchy for a directory, including workspace and selected checkout ancestors. No file-size or document-count limit. Large responses return a continuation cursor; call again with the same project, directory and cursor to obtain every byte. A chunk field identifies partial segments of individual oversized files.")>]
    static member GetAgentInstructions(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, directory: string, [<Optional; DefaultParameterValue(""); Description("Continuation cursor from a previous response; omit for the first page.")>] cursor: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (GetAgentInstructionsCommand {
            ProjectName = projectName
            AgentId = agentId
            Directory = directory
            Cursor = McpToolHelpers.optionOfString cursor })

    [<McpServerTool(Title = "Read image", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a PNG, JPEG, or WebP image from a project and return it as native MCP image content for model vision.")>]
    static member ReadImage(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePath: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ReadImageCommand { ProjectName = projectName; FilePath = filePath })

    [<McpServerTool(Title = "Export file", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Export one bounded project file to a short-lived HTTPS URL that can be opened by the user. The URL is an unguessable bearer capability and expires automatically.")>]
    static member ExportFile(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePath: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ExportFileCommand { ProjectName = projectName; FilePath = filePath })

    [<McpServerTool(Title = "Import file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false);
      McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]""");
      McpMeta("openai/fileParams", JsonValue = """["file"]""");
      Description("Import a ChatGPT-provided file into a project path. The Jarvis client downloads the temporary file URL directly and enforces its local import-size limit.")>]
    static member ImportFile(
        projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string,
        filePath: string,
        file: OpenAIFileParam,
        overwrite: bool,
        createParents: bool,
        maxBytes: Nullable<int64>,
        [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService,
        http: IHttpContextAccessor
    ) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ImportFileCommand {
            ProjectName = projectName
            FilePath = filePath
            DownloadUrl = file.download_url
            FileId = file.file_id
            MimeType = McpToolHelpers.optionOfString file.mime_type
            FileName = McpToolHelpers.optionOfString file.file_name
            Overwrite = overwrite
            CreateParents = createParents
            MaxBytes = McpToolHelpers.optionOfNullableInt64 maxBytes })

    [<McpServerTool(Title = "Update client", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Update the local Jarvis client from a verified published release, then restart it in the same terminal.")>]
    static member UpdateClient([<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http UpdateClientCommand

    [<McpServerTool(Title = "Write file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]"""); Description("Create, update or append to a file in the selected project workspace using configured local permissions. Requires client approval.")>]
    static member WriteFile(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePath: string, content: string, [<Description("File write mode. Allowed values: Write, Append.")>] fileWriteMode: string, [<Description("Optional precondition: SHA-256 hash to require matching existing content, none to require a new file, or empty string for no precondition.")>] expectedHash: string, createParents: Nullable<bool>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        match McpToolHelpers.parseFileWriteMode fileWriteMode with
        | Error message ->
            Task.FromResult(McpToolHelpers.validationErrorResult message)
        | Ok mode ->
            McpToolHelpers.send client http (WriteFileCommand {
                ProjectName = projectName
                FilePath = filePath
                Content = content
                FileWriteMode = mode
                ExpectedHash = McpToolHelpers.optionOfString expectedHash
                CreateParents = McpToolHelpers.optionOfNullableBool createParents })

    [<McpServerTool(Title = "Patch file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]"""); Description("Edit a project file using an atomic unified diff, with optional expected-hash checking and dry-run validation. Requires client approval.")>]
    static member PatchFile(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, filePath: string, patch: string, expectedHash: string, dryRun: Nullable<bool>, fuzzyContextLines: Nullable<int>, returnContent: Nullable<bool>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (PatchFileCommand {
            ProjectName = projectName
            FilePath = filePath
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            Format = UnifiedDiff
            Patch = patch
            DryRun = McpToolHelpers.optionOfNullableBool dryRun
            FuzzyContextLines = McpToolHelpers.optionOfNullable fuzzyContextLines
            ReturnContent = McpToolHelpers.optionOfNullableBool returnContent })

    [<McpServerTool(Title = "Run command", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Execute a bounded development command within the local Jarvis OS-permission sandbox for the selected project workspace. Commands run with the explicitly configured local account permissions and require client approval. Supply a short reason for the command.")>]
    static member RunCommand(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, executable: string, args: string array, [<Optional; DefaultParameterValue("")>] reason: string, workingDirectory: string, timeoutSeconds: Nullable<int>, maxOutputBytes: Nullable<int>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (RunCommandCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            Reason = McpToolHelpers.optionOfString reason
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            TimeoutSeconds = McpToolHelpers.optionOfNullable timeoutSeconds
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "List configured tasks", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List locally configured project tasks from .jarvis.json.")>]
    static member ListConfiguredTasks(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ListConfiguredTasksCommand { ProjectName = projectName })

    [<McpServerTool(Title = "List skills", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List project skills and shared workspace-global skills (scope identified on each result). Use projectName=@global to list global skills only. Skills do not grant execution privileges.")>]
    static member ListSkills(projectName: string, agentId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListSkillsCommand { ProjectName = projectName })

    [<McpServerTool(Title = "Get skill", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a project skill; use projectName=@global for a shared workspace-global skill. Skills contain procedural guidance only.")>]
    static member GetSkill(projectName: string, agentId: string, skillName: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetSkillCommand { ProjectName = projectName; SkillName = skillName })

    [<McpServerTool(Title = "Create skill", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]"""); Description("Create or update project-local or workspace-global agent guidance using normal workspace permissions. Use projectName=@global for shared skills; overwrite=true updates an existing skill.")>]
    static member CreateSkill(
        projectName: string, agentId: string,
        skillName: string,
        content: string,
        [<Optional; DefaultParameterValue(false)>] overwrite: bool,
        [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.send client http (CreateSkillCommand { ProjectName = projectName; SkillName = skillName; Content = content; Overwrite = overwrite })

    [<McpServerTool(Title = "Run project task", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Run a predefined build, test or other development task from the project's .jarvis.json inside the local Jarvis OS-permission sandbox. Requires client approval.")>]
    static member RunProjectTask(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, taskName: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (RunProjectTaskCommand { ProjectName = projectName; TaskName = taskName })

    [<McpServerTool(Title = "Get git status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get git status for a project.")>]
    static member GetGitStatus(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (GetGitStatusCommand { ProjectName = projectName })

    [<McpServerTool(Title = "Get git diff", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get git diff for a project or project-relative path.")>]
    static member GetGitDiff(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, path: string, maxOutputBytes: Nullable<int>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (GetGitDiffCommand {
            ProjectName = projectName
            Path = McpToolHelpers.optionOfString path
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "Create git commit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Commit selected project files to the local Git branch. Does not push to a remote; requires local approval.")>]
    static member GitCommit(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, message: string, body: string, paths: string array, allowEmpty: bool, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (GitCommitCommand {
            ProjectName = projectName
            Message = message
            Body = McpToolHelpers.optionOfString body
            Paths = paths |> Array.toList
            AllowEmpty = allowEmpty })

    [<McpServerTool(Title = "Start job", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Start a long-running development process inside the local Jarvis OS-permission sandbox. Requires client approval.")>]
    static member StartJob(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, executable: string, args: string array, workingDirectory: string, maxOutputBytes: Nullable<int>, [<Optional; DefaultParameterValue("")>] reason: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (StartJobCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            Reason = McpToolHelpers.optionOfString reason
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "List jobs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List running or completed Jarvis jobs.")>]
    static member ListJobs(projectName: string, agentId: string, [<Optional; DefaultParameterValue(""); Description("Optional name of a Git-registered worktree; omitted targets the project main checkout.")>] worktreeName: string, includeCompleted: bool, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        let projectName = McpToolHelpers.projectWithWorktreeName projectName worktreeName
        McpToolHelpers.send client http (ListJobsCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            IncludeCompleted = includeCompleted })

    [<McpServerTool(Title = "Get job result", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get buffered output and status for a Jarvis job.")>]
    static member GetJobResult(projectName: string, agentId: string, jobId: string, afterSequence: Nullable<int64>, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetJobResultCommand {
            ProjectName = projectName
            JobId = jobId
            AfterSequence = McpToolHelpers.optionOfNullableInt64 afterSequence })

    [<McpServerTool(Title = "Cancel job", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Stop a running Jarvis-managed development job. Requires client approval.")>]
    static member CancelJob(projectName: string, agentId: string, jobId: string, [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (CancelJobCommand { ProjectName = projectName; JobId = jobId })

    [<McpServerTool(Title = "Message", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Send a short informational message to the connected Jarvis client activity log, for example progress or turn-completion messages such as 'Done with this turn'. This does not require local approval.")>]
    static member Message(
        message: string,
        agentId: string,
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.send client http (MessageCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            Message = message })

    [<McpServerTool(Title = "Get client activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read connected client activity. Observer diagnostics (GitStatus/ListJobs) are omitted by default; set includeObservers=true to include them. Optionally filter by project.")>]
    static member GetClientActivity(
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue(20)>] limit: int,
        [<Optional; DefaultParameterValue(false)>] includeObservers: bool,
        [<Optional; DefaultParameterValue("")>] deviceId: string, client: ClientService,
        http: IHttpContextAccessor
    ) =
        let boundedLimit =
            limit
            |> max 1
            |> min 100

        McpToolHelpers.send client http (GetClientActivityCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            Limit = Some boundedLimit
            IncludeObservers = Some includeObservers })
