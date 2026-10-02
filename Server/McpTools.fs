namespace Server

[<CLIMutable>]
type ProfileResult = { id: string }

open System
open System.ComponentModel
open System.Diagnostics
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open Server.Services

module McpToolHelpers =
    let private formatError error =
        match error with
        | NotFound message -> $"NotFound: {message}"
        | PermissionDenied message -> $"PermissionDenied: {message}"
        | ConfirmationRequired request -> $"ConfirmationRequired: {request.Summary}"
        | ValidationFailed message -> $"ValidationFailed: {message}"
        | Conflict message -> $"Conflict: {message}"
        | ExecutionFailed message -> $"ExecutionFailed: {message}"
        | OutputTruncated message -> $"OutputTruncated: {message}"

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

    let mcpListCommandsResult : ListCommandsResult =
        { ProtocolVersion = AgentProtocol.version
          Commands =
            AgentProtocol.capabilities
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
                serverLocalCapability
                    "GetFeedbackSummary"
                    "Summarizes persisted agent feedback."
                    [ ReadOnly ]
                    false
                serverLocalCapability
                    "GetConnectionDiagnostics"
                    "Returns the current Jarvis client session and recent persisted connection lifecycle events."
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

    let toCallToolResult (response: AgentCommandResponse) =
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

    let private requiredScope command =
        match command with
        | ListCommandsCommand
        | ListProjectsCommand
        | GetProjectDetailsCommand _
        | ListDirectoryCommand _
        | SearchFilesCommand _
        | SearchTextCommand _
        | ReadFileCommand _
        | ReadFilesCommand _
        | ReadImageCommand _
        | ListProjectTasksCommand _
        | ListSkillsCommand _
        | GetSkillCommand _
        | GetGitStatusCommand _
        | GetGitDiffCommand _
        | ListJobsCommand _
        | GetJobResultCommand _
        | GetClientActivityCommand _ -> Auth.WorkspaceRead
        | WriteFileCommand _
        | PatchFileCommand _ -> Auth.WorkspaceWrite
        | RunCommandCommand _
        | RunProjectTaskCommand _
        | StartJobCommand _
        | CancelJobCommand _ -> Auth.ProcessExecute
        | GitCommitCommand _ -> Auth.GitWrite

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
        let name = AgentCommandInfo.name command
        if name.EndsWith("Command", StringComparison.Ordinal) then
            name.Substring(0, name.Length - "Command".Length)
        else
            name

    let private serverVersion =
        match typeof<JarvisOptions>.Assembly.GetName().Version with
        | null -> "unknown"
        | version -> version.ToString()

    let private beginOperation (context: HttpContext) userId command =
        try
            let store = context.RequestServices.GetRequiredService<FeedbackStore>()
            let users = context.RequestServices.GetRequiredService<UserService>()
            let clientVersion, protocolVersion =
                match users.GetSession userId with
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

    let send (client: ClientService) (http: IHttpContextAccessor) command =
        task {
            match Option.ofObj http.HttpContext with
            | None ->
                return errorResult (PermissionDenied "The MCP request is missing its HTTP context.")
            | Some context ->
                let scope = requiredScope command

                match Auth.tryUserId context.User with
                | Some userId when Auth.hasScope scope context.User ->
                    let tracking = beginOperation context userId command
                    let stopwatch = Stopwatch.StartNew()
                    let! response = client.SendCommandToUser(userId, command)
                    stopwatch.Stop()
                    finishOperation tracking response stopwatch.ElapsedMilliseconds

                    return
                        (match command with
                         | ReadImageCommand _ -> imageResult response
                         | _ -> toCallToolResult response)
                        |> attachOperationId tracking
                | Some _ ->
                    return authorizationErrorResult context scope
                | None ->
                    return authenticationErrorResult context (Some scope)
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
                | Error message, _, _, _, _, _, _, _
                | _, Error message, _, _, _, _, _, _
                | _, _, Error message, _, _, _, _, _
                | _, _, _, Error message, _, _, _, _
                | _, _, _, _, Error message, _, _, _
                | _, _, _, _, _, Error message, _, _
                | _, _, _, _, _, _, Error message, _
                | _, _, _, _, _, _, _, Error message ->
                    return errorResult (ValidationFailed message)
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

                    match categoryResult, severityResult with
                    | Ok category, Ok severity ->
                        return store.ListFeedback(userId, project, tool, category, severity, requestedLimit) |> jsonResult
                    | Error message, _
                    | _, Error message ->
                        return errorResult (ValidationFailed message)
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
                        match users.GetSession userId with
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
                                   registeredAt = session.RegisteredAt
                                   lastSeenAt = session.LastSeenAt
                                   disconnectedAt = session.DisconnectedAt |> Option.map _.ToString("O") |> Option.defaultValue ""
                                   lastFailure = session.LastFailure |> Option.defaultValue ""
                                   registrationReason = session.RegistrationReason |}

                    return
                        {| currentSession = currentSession
                           events = store.ListConnectionEvents(userId, requestedLimit) |}
                        |> jsonResult
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
        if String.Equals(value, "Append", StringComparison.OrdinalIgnoreCase) then Append
        elif String.Equals(value, "Write", StringComparison.OrdinalIgnoreCase) then Write
        else invalidArg "fileWriteMode" $"Unknown file write mode: {value}"

[<McpServerToolType>]
type JarvisMcpTools =
    [<McpServerTool(Title = "Get profile", UseStructuredContent = true, OutputSchemaType = typeof<ProfileResult>, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":[]}]"""); McpMeta("openai/profile", true); Description("Return the profile represented by the authenticated OAuth credentials.")>]
    static member GetProfile(http: IHttpContextAccessor) =
        McpToolHelpers.getProfile http

    [<McpServerTool(Title = "Submit feedback", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Submit structured feedback about Jarvis tool failures, limitations, ergonomics, missing capabilities, documentation, or positive behavior. Does not require local client approval.")>]
    static member Feedback(
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue("")>] toolName: string,
        category: string,
        severity: string,
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

    [<McpServerTool(Title = "Get feedback summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Summarize feedback counts by tool, category, and severity for the authenticated user.")>]
    static member GetFeedbackSummary(
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue("")>] toolName: string,
        store: FeedbackStore,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.feedbackSummary store http projectName toolName

    [<McpServerTool(Title = "Get connection diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Return the current Jarvis client session and recent persisted SignalR connection lifecycle events. Does not require the local client to be connected.")>]
    static member GetConnectionDiagnostics(
        [<Optional; DefaultParameterValue(50)>] limit: int,
        store: FeedbackStore,
        users: UserService,
        http: IHttpContextAccessor
    ) =
        McpToolHelpers.getConnectionDiagnostics store users http (Nullable limit)

    [<McpServerTool(Title = "List commands", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List the complete public Jarvis MCP command catalog, including server-local tools.")>]
    static member ListCommands(http: IHttpContextAccessor) =
        McpToolHelpers.listMcpCommands http

    [<McpServerTool(Title = "List projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List projects exposed by the connected Jarvis client.")>]
    static member ListProjects(client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http ListProjectsCommand

    [<McpServerTool(Title = "Get project details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get project details, README, notes, TODO, and related special files.")>]
    static member GetProjectDetails(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetProjectDetailsCommand { ProjectName = projectName })

    [<McpServerTool(Title = "List directory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List files and folders in a project directory.")>]
    static member ListDirectory(projectName: string, folderPath: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListDirectoryCommand { ProjectName = projectName; FolderPath = folderPath })

    [<McpServerTool(Title = "Search files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Search project file and folder names.")>]
    static member SearchFiles(projectName: string, query: string, folderPath: string, maxResults: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (SearchFilesCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(Title = "Search text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Search text inside project files.")>]
    static member SearchText(projectName: string, query: string, folderPath: string, includeGlobs: string array, excludeGlobs: string array, maxResults: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (SearchTextCommand {
            ProjectName = projectName
            Query = query
            FolderPath = McpToolHelpers.optionOfString folderPath
            IncludeGlobs = includeGlobs |> Array.toList
            ExcludeGlobs = excludeGlobs |> Array.toList
            MaxResults = McpToolHelpers.optionOfNullable maxResults })

    [<McpServerTool(Title = "Read file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read one project file.")>]
    static member ReadFile(projectName: string, filePath: string, startLine: Nullable<int>, endLine: Nullable<int>, includeLineNumbers: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ReadFileCommand {
            ProjectName = projectName
            FilePath = filePath
            StartLine = McpToolHelpers.optionOfNullable startLine
            EndLine = McpToolHelpers.optionOfNullable endLine
            IncludeLineNumbers = McpToolHelpers.optionOfNullableBool includeLineNumbers })

    [<McpServerTool(Title = "Read files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read multiple project files.")>]
    static member ReadFiles(projectName: string, filePaths: string array, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ReadFilesCommand { ProjectName = projectName; FilePaths = filePaths |> Array.toList })

    [<McpServerTool(Title = "Read image", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a PNG, JPEG, or WebP image from a project and return it as native MCP image content for model vision.")>]
    static member ReadImage(projectName: string, filePath: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ReadImageCommand { ProjectName = projectName; FilePath = filePath })

    [<McpServerTool(Title = "Write file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]"""); Description("Write or append to a project file. Requires approval in the local Jarvis client.")>]
    static member WriteFile(projectName: string, filePath: string, content: string, fileWriteMode: string, expectedHash: string, createParents: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        let mode = McpToolHelpers.parseFileWriteMode fileWriteMode
        McpToolHelpers.send client http (WriteFileCommand {
            ProjectName = projectName
            FilePath = filePath
            Content = content
            FileWriteMode = mode
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            CreateParents = McpToolHelpers.optionOfNullableBool createParents })

    [<McpServerTool(Title = "Patch file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:write"]}]"""); Description("Apply an atomic unified diff patch to one project file. Requires approval in the local Jarvis client.")>]
    static member PatchFile(projectName: string, filePath: string, patch: string, expectedHash: string, dryRun: Nullable<bool>, fuzzyContextLines: Nullable<int>, returnContent: Nullable<bool>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (PatchFileCommand {
            ProjectName = projectName
            FilePath = filePath
            ExpectedHash = McpToolHelpers.optionOfString expectedHash
            Format = UnifiedDiff
            Patch = patch
            DryRun = McpToolHelpers.optionOfNullableBool dryRun
            FuzzyContextLines = McpToolHelpers.optionOfNullable fuzzyContextLines
            ReturnContent = McpToolHelpers.optionOfNullableBool returnContent })

    [<McpServerTool(Title = "Run command", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Run a bounded local command in a project. Supply a short human-readable reason for why the command is needed. Requires approval in the local Jarvis client.")>]
    static member RunCommand(projectName: string, executable: string, args: string array, [<Optional; DefaultParameterValue("")>] reason: string, workingDirectory: string, timeoutSeconds: Nullable<int>, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (RunCommandCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            Reason = McpToolHelpers.optionOfString reason
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            TimeoutSeconds = McpToolHelpers.optionOfNullable timeoutSeconds
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "List project tasks", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List locally configured project tasks from .jarvis.json.")>]
    static member ListProjectTasks(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListProjectTasksCommand { ProjectName = projectName })

    [<McpServerTool(Title = "List skills", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List project-local Jarvis skills from .jarvis/skills. Skills provide procedural instructions and do not grant execution privileges.")>]
    static member ListSkills(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListSkillsCommand { ProjectName = projectName })

    [<McpServerTool(Title = "Get skill", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read a named project-local Jarvis skill from .jarvis/skills/<name>/SKILL.md (or skill.md). The returned content is procedural guidance only; use normal Jarvis tools for any actions.")>]
    static member GetSkill(projectName: string, skillName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetSkillCommand { ProjectName = projectName; SkillName = skillName })

    [<McpServerTool(Title = "Run project task", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Run a named task configured in the project's .jarvis.json. Requires local process approval.")>]
    static member RunProjectTask(projectName: string, taskName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (RunProjectTaskCommand { ProjectName = projectName; TaskName = taskName })

    [<McpServerTool(Title = "Get git status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get git status for a project.")>]
    static member GetGitStatus(projectName: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetGitStatusCommand { ProjectName = projectName })

    [<McpServerTool(Title = "Get git diff", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get git diff for a project or project-relative path.")>]
    static member GetGitDiff(projectName: string, path: string, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetGitDiffCommand {
            ProjectName = projectName
            Path = McpToolHelpers.optionOfString path
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "Create git commit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["git:write"]}]"""); Description("Create a reversible local git commit from selected paths. Requires approval in the local Jarvis client and does not push to a remote.")>]
    static member GitCommit(projectName: string, message: string, body: string, paths: string array, allowEmpty: bool, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GitCommitCommand {
            ProjectName = projectName
            Message = message
            Body = McpToolHelpers.optionOfString body
            Paths = paths |> Array.toList
            AllowEmpty = allowEmpty })

    [<McpServerTool(Title = "Start job", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Start a long-running local job. Requires approval in the local Jarvis client.")>]
    static member StartJob(projectName: string, executable: string, args: string array, workingDirectory: string, maxOutputBytes: Nullable<int>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (StartJobCommand {
            ProjectName = projectName
            Executable = executable
            Args = args |> Array.toList
            WorkingDirectory = McpToolHelpers.optionOfString workingDirectory
            MaxOutputBytes = McpToolHelpers.optionOfNullable maxOutputBytes })

    [<McpServerTool(Title = "List jobs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("List running or completed Jarvis jobs.")>]
    static member ListJobs(projectName: string, includeCompleted: bool, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (ListJobsCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            IncludeCompleted = includeCompleted })

    [<McpServerTool(Title = "Get job result", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Get buffered output and status for a Jarvis job.")>]
    static member GetJobResult(jobId: string, afterSequence: Nullable<int64>, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (GetJobResultCommand {
            JobId = jobId
            AfterSequence = McpToolHelpers.optionOfNullableInt64 afterSequence })

    [<McpServerTool(Title = "Cancel job", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["process:execute"]}]"""); Description("Cancel a running Jarvis job. Requires approval in the local Jarvis client.")>]
    static member CancelJob(jobId: string, client: ClientService, http: IHttpContextAccessor) =
        McpToolHelpers.send client http (CancelJobCommand { JobId = jobId })

    [<McpServerTool(Title = "Get client activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false); McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["workspace:read"]}]"""); Description("Read recent in-memory activity from the connected Jarvis client. Use this to reconstruct what the client was doing when a previous ChatGPT session stalled. Optionally filter by project.")>]
    static member GetClientActivity(
        [<Optional; DefaultParameterValue("")>] projectName: string,
        [<Optional; DefaultParameterValue(20)>] limit: int,
        client: ClientService,
        http: IHttpContextAccessor
    ) =
        let boundedLimit =
            limit
            |> max 1
            |> min 100

        McpToolHelpers.send client http (GetClientActivityCommand {
            ProjectName = McpToolHelpers.optionOfString projectName
            Limit = Some boundedLimit })
